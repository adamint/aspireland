from __future__ import annotations

import json
import threading
from collections import Counter, defaultdict
from copy import deepcopy
from datetime import datetime, timedelta, timezone
from http.client import HTTPException
from json import JSONDecodeError
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.parse import urlsplit, urlunsplit
from urllib.request import Request, urlopen

UTC = timezone.utc
INVENTORY_STATES = {
    "outside": "outside",
    "platform": "platform",
    "onboard": "onboard",
    "transfer": "transfer",
}


def parse_timestamp(value: object, field: str) -> datetime:
    if not isinstance(value, str):
        raise ValueError(f"{field} must be an ISO-8601 string")
    text = value[:-1] + "+00:00" if value.endswith("Z") else value
    try:
        parsed = datetime.fromisoformat(text)
    except ValueError as exc:
        raise ValueError(f"{field} must be a valid ISO-8601 timestamp") from exc
    if parsed.tzinfo is None:
        raise ValueError(f"{field} must include a UTC offset")
    return parsed.astimezone(UTC)


def iso(value: datetime) -> str:
    return value.astimezone(UTC).isoformat().replace("+00:00", "Z")


def scenario_state_url(base_url: str) -> str:
    parsed = urlsplit(base_url)
    if parsed.scheme not in {"http", "https"} or not parsed.netloc:
        raise ValueError("SCENARIO_URL must be an absolute http or https URL")
    if parsed.username is not None or parsed.password is not None:
        raise ValueError("SCENARIO_URL must not contain credentials")
    if parsed.query or parsed.fragment:
        raise ValueError("SCENARIO_URL must not contain a query or fragment")
    path = parsed.path.rstrip("/")
    if path.endswith("/state"):
        path = path.removesuffix("/state")
    state_path = path if path.endswith("/analytics-state") else f"{path}/analytics-state"
    return urlunsplit((parsed.scheme, parsed.netloc, state_path, "", ""))


def _require_dict(value: object, field: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise ValueError(f"{field} must be an object")
    return value


def _require_list(value: object, field: str) -> list[Any]:
    if not isinstance(value, list):
        raise ValueError(f"{field} must be an array")
    return value


def _integer(value: object, field: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int):
        raise ValueError(f"{field} must be an integer")
    return value


def _number(value: object, field: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ValueError(f"{field} must be numeric")
    return float(value)


def _count_by_station(
    cohorts: list[Any], sim_time: datetime
) -> tuple[list[dict[str, object]], dict[str, int], int, int, int, int]:
    queues: dict[str, dict[str, object]] = defaultdict(
        lambda: {"outside": 0, "platform": 0, "oldestQueueSeconds": 0.0}
    )
    inventory = {state: 0 for state in INVENTORY_STATES.values()}
    accessible_inventory = 0
    unknown_active = 0
    waiting_now = 0
    scheduled_not_ready = 0
    for index, raw in enumerate(cohorts):
        cohort = _require_dict(raw, f"cohorts[{index}]")
        count = _integer(cohort.get("count"), f"cohorts[{index}].count")
        if count < 0:
            raise ValueError(f"cohorts[{index}].count must not be negative")
        state_value = cohort.get("state")
        if not isinstance(state_value, str):
            raise ValueError(f"cohorts[{index}].state must be a string")
        state = state_value.lower()
        if state in INVENTORY_STATES:
            inventory[INVENTORY_STATES[state]] += count
            if cohort.get("accessible") is True:
                accessible_inventory += count
            if state in {"outside", "platform"}:
                timestamp = cohort.get("readyAt") or cohort.get("createdAt")
                ready = (
                    parse_timestamp(timestamp, f"cohorts[{index}].readyAt")
                    if timestamp is not None
                    else None
                )
                if ready is not None and ready > sim_time:
                    scheduled_not_ready += count
                    continue
                station = cohort.get("stationId") or cohort.get("origin")
                if not isinstance(station, str) or not station:
                    raise ValueError(
                        f"cohorts[{index}] waiting inventory requires stationId or origin"
                    )
                queues[station][state] = int(queues[station][state]) + count
                waiting_now += count
                if ready is not None:
                    age = max(0.0, (sim_time - ready).total_seconds())
                    queues[station]["oldestQueueSeconds"] = max(
                        float(queues[station]["oldestQueueSeconds"]), age
                    )
        elif state not in {"completed", "abandoned"}:
            unknown_active += count
    queue_list = []
    for station, counts in sorted(queues.items()):
        queue_list.append(
            {
                "stationId": station,
                "outside": counts["outside"],
                "platform": counts["platform"],
                "waiting": int(counts["outside"]) + int(counts["platform"]),
                "oldestQueueSeconds": round(float(counts["oldestQueueSeconds"]), 1),
            }
        )
    return (
        queue_list,
        inventory,
        accessible_inventory,
        unknown_active,
        waiting_now,
        scheduled_not_ready,
    )


def _line_loads(trains: list[Any]) -> list[dict[str, object]]:
    grouped: dict[str, dict[str, int]] = defaultdict(
        lambda: {"trains": 0, "load": 0, "capacity": 0, "held": 0}
    )
    for index, raw in enumerate(trains):
        train = _require_dict(raw, f"trains[{index}]")
        line = train.get("line")
        if not isinstance(line, str):
            raise ValueError(f"trains[{index}].line must be a string")
        line = line or "unassigned"
        load = _integer(train.get("load"), f"trains[{index}].load")
        capacity = _integer(train.get("capacity"), f"trains[{index}].capacity")
        if load < 0 or capacity < 0:
            raise ValueError(f"trains[{index}] load and capacity must not be negative")
        grouped[line]["trains"] += 1
        grouped[line]["load"] += load
        grouped[line]["capacity"] += capacity
        if train.get("holdReason"):
            grouped[line]["held"] += 1
    result = []
    for line, values in sorted(grouped.items()):
        capacity = values["capacity"]
        result.append(
            {
                "line": line,
                **values,
                "utilization": round(values["load"] / capacity, 4) if capacity else None,
            }
        )
    return result


def _history_summary(history: list[Any], incidents: list[Any]) -> dict[str, object]:
    counts: Counter[str] = Counter()
    recent: list[dict[str, object]] = []
    effect_terms = ("incident", "fault", "closure", "restriction", "delay", "repair")
    incident_effects: list[dict[str, object]] = []
    for index, raw in enumerate(history):
        event = _require_dict(raw, f"history[{index}]")
        kind = event.get("kind")
        if not isinstance(kind, str):
            raise ValueError(f"history[{index}].kind must be a string")
        counts[kind] += 1
        compact = {
            key: event.get(key)
            for key in ("id", "kind", "entityId", "simTime", "source", "description")
        }
        recent.append(compact)
        searchable = f"{kind} {event.get('description', '')}".lower()
        if any(term in searchable for term in effect_terms):
            incident_effects.append(compact)
    active = []
    for index, raw in enumerate(incidents):
        incident = _require_dict(raw, f"incidents[{index}]")
        if incident.get("state") == "active":
            active.append(
                {
                    key: incident.get(key)
                    for key in (
                        "id",
                        "kind",
                        "target",
                        "description",
                        "startedAt",
                        "endsAt",
                        "severity",
                    )
                }
            )
    return {
        "countsByKind": dict(sorted(counts.items())),
        "recent": recent[-20:],
        "incidentEffects": incident_effects[-20:],
        "activeIncidents": active,
    }


def compute_analytics(world_value: object) -> dict[str, object]:
    world = _require_dict(world_value, "world")
    metrics = _require_dict(world.get("metrics"), "metrics")
    cohorts = _require_list(world.get("cohorts"), "cohorts")
    trains = _require_list(world.get("trains"), "trains")
    history = _require_list(world.get("history"), "history")
    incidents = _require_list(world.get("incidents"), "incidents")
    sim_time = parse_timestamp(world.get("simTime"), "simTime")
    updated_at = parse_timestamp(world.get("updatedAt"), "updatedAt")
    generated = _integer(metrics.get("generated"), "metrics.generated")
    completed = _integer(metrics.get("completed"), "metrics.completed")
    abandoned = _integer(metrics.get("abandoned"), "metrics.abandoned")
    denied = _integer(metrics.get("deniedBoardings"), "metrics.deniedBoardings")
    waiting_seconds = _number(
        metrics.get("waitingPassengerSeconds"), "metrics.waitingPassengerSeconds"
    )
    accessibility_failures = _integer(
        metrics.get("accessibilityFailures"), "metrics.accessibilityFailures"
    )
    platform_overloads = _integer(
        metrics.get("platformOverloads"), "metrics.platformOverloads"
    )
    nonnegative = {
        "generated": generated,
        "completed": completed,
        "abandoned": abandoned,
        "deniedBoardings": denied,
        "waitingPassengerSeconds": waiting_seconds,
        "accessibilityFailures": accessibility_failures,
        "platformOverloads": platform_overloads,
    }
    if any(value < 0 for value in nonnegative.values()):
        raise ValueError("passenger metrics must not be negative")

    (
        queues,
        inventory,
        accessible_inventory,
        unknown_active,
        waiting_current,
        scheduled_not_ready,
    ) = _count_by_station(cohorts, sim_time)
    inventory_total = sum(inventory.values())
    accounted = completed + abandoned + inventory_total
    conservation_delta = generated - accounted
    scenario_id = world.get("scenarioId")
    generation = world.get("generation")
    version = world.get("version")
    if not isinstance(scenario_id, str):
        raise ValueError("scenarioId must be a string")
    generation = _integer(generation, "generation")
    version = _integer(version, "version")
    return {
        "status": "ready",
        "stale": False,
        "scenarioId": scenario_id,
        "generation": generation,
        "version": version,
        "simulatedTime": iso(sim_time),
        "sourceUpdatedAt": iso(updated_at),
        "time": {
            "simulated": iso(sim_time),
            "sourceUpdatedAt": iso(updated_at),
        },
        "observed": {
            "generated": generated,
            "completed": completed,
            "abandoned": abandoned,
            "deniedBoardings": denied,
            "waitingPassengerSeconds": round(waiting_seconds, 1),
            "waitingPassengerMinutes": round(waiting_seconds / 60, 2),
            "accessibilityFailures": accessibility_failures,
            "platformOverloads": platform_overloads,
        },
        "current": {
            **inventory,
            "inventory": inventory_total,
            "waiting": waiting_current,
            "scheduledNotReady": scheduled_not_ready,
            "accessibleInventory": accessible_inventory,
            "unknownActiveStatePassengers": unknown_active,
        },
        "conservation": {
            "generated": generated,
            "accounted": accounted,
            "delta": conservation_delta,
            "balanced": conservation_delta == 0,
            "formula": "generated = completed + abandoned + outside + platform + onboard + transfer",
        },
        "queues": queues,
        "lineLoads": _line_loads(trains),
        "history": _history_summary(history, incidents),
        "forecast": {
            "kind": "unavailable",
            "available": False,
            "message": "This endpoint reports observed live state; forecasts come from the scenario comparison runner.",
        },
    }


class AnalyticsPoller:
    def __init__(
        self,
        scenario_url: str,
        interval_seconds: float = 2.0,
        timeout_seconds: float = 1.5,
    ):
        self.state_url = scenario_state_url(scenario_url)
        self.interval_seconds = interval_seconds
        self.timeout_seconds = timeout_seconds
        self._lock = threading.Lock()
        self._analytics: dict[str, object] | None = None
        self._last_success: datetime | None = None
        self._error: str | None = None
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None

    def refresh(self) -> bool:
        request = Request(
            self.state_url,
            method="GET",
            headers={"Accept": "application/json", "User-Agent": "MetroOpsAnalytics/1.0"},
        )
        try:
            with urlopen(request, timeout=self.timeout_seconds) as response:
                if response.status != 200:
                    raise ValueError(f"scenario returned HTTP {response.status}")
                content_type = response.headers.get_content_type()
                if content_type != "application/json":
                    raise ValueError(
                        f"scenario returned unsupported Content-Type {content_type}"
                    )
                raw = response.read(8 * 1024 * 1024 + 1)
                if len(raw) > 8 * 1024 * 1024:
                    raise ValueError("scenario state exceeded 8 MiB")
            world = json.loads(raw)
            analytics = compute_analytics(world)
        except HTTPError as exc:
            self._record_error(f"scenario returned HTTP {exc.code}")
            return False
        except URLError as exc:
            self._record_error(f"scenario unavailable: {exc.reason}")
            return False
        except TimeoutError:
            self._record_error("scenario request timed out")
            return False
        except HTTPException as exc:
            self._record_error(f"scenario response failed: {exc}")
            return False
        except JSONDecodeError as exc:
            self._record_error(f"scenario returned invalid JSON at byte {exc.pos}")
            return False
        except (UnicodeDecodeError, ValueError) as exc:
            self._record_error(f"scenario state invalid: {exc}")
            return False
        now = datetime.now(UTC)
        with self._lock:
            self._analytics = analytics
            self._last_success = now
            self._error = None
        return True

    def _record_error(self, message: str) -> None:
        with self._lock:
            self._error = message

    def start(self) -> None:
        if self._thread is not None:
            return
        self.refresh()
        self._thread = threading.Thread(target=self._run, name="analytics-poller", daemon=True)
        self._thread.start()

    def _run(self) -> None:
        while not self._stop.wait(self.interval_seconds):
            self.refresh()

    def stop(self) -> None:
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=self.interval_seconds + self.timeout_seconds + 0.5)

    def analytics_response(self) -> tuple[int, dict[str, object]]:
        with self._lock:
            analytics = deepcopy(self._analytics)
            error = self._error
            last_success = self._last_success
        if analytics is None:
            return 503, {
                "status": "unavailable",
                "stale": True,
                "error": error or "scenario state has not been loaded",
                "forecast": {"kind": "unavailable", "available": False},
            }
        if error is not None:
            analytics["status"] = "stale"
            analytics["stale"] = True
            analytics["warning"] = error
            analytics["lastSuccessfulPollAt"] = iso(last_success) if last_success else None
            return 503, analytics
        analytics["lastSuccessfulPollAt"] = iso(last_success) if last_success else None
        return 200, analytics

    def inventory_response(self) -> tuple[int, dict[str, object]]:
        with self._lock:
            snapshot = self._analytics
            last_success = self._last_success
            poll_failed = self._error is not None
        now = datetime.now(UTC)
        source_updated = snapshot["sourceUpdatedAt"] if snapshot else None
        stale = (snapshot is None or poll_failed or last_success is None or
                 now - last_success > timedelta(seconds=10) or
                 now - parse_timestamp(source_updated, "sourceUpdatedAt") > timedelta(seconds=10))
        conservation = snapshot["conservation"] if snapshot else {}
        return (503 if stale else 200), {
            "status": "unavailable" if snapshot is None else "stale" if stale else "fresh",
            "invariant": "unknown" if stale else "balanced" if conservation["balanced"] else "mismatch",
            "scenarioId": snapshot["scenarioId"] if snapshot else None,
            "generation": snapshot["generation"] if snapshot else None,
            "version": snapshot["version"] if snapshot else None,
            "simulatedTime": snapshot["simulatedTime"] if snapshot else None,
            "sourceUpdatedAt": source_updated,
            "lastSuccessfulPollAt": iso(last_success) if last_success else None,
            "generated": conservation.get("generated"),
            "accounted": conservation.get("accounted"),
            "delta": conservation.get("delta"),
        }

    def health_response(self) -> dict[str, object]:
        with self._lock:
            ready = self._analytics is not None and self._error is None
            error = self._error
            last_success = self._last_success
        return {
            "status": "healthy" if ready else "unhealthy",
            "ready": ready,
            "upstream": "ready" if ready else "unavailable",
            "lastSuccessfulPollAt": iso(last_success) if last_success else None,
            "error": error,
        }
