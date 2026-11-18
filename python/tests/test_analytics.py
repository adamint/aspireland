from __future__ import annotations

import json
import unittest
from datetime import datetime, timezone
from email.message import Message
from urllib.error import URLError
from unittest.mock import patch

from python.analytics.service import AnalyticsPoller, compute_analytics, scenario_state_url


def world_state() -> dict[str, object]:
    cohorts = [
        {
            "id": "outside",
            "origin": "pinecrest",
            "stationId": "pinecrest",
            "count": 3,
            "accessible": False,
            "state": "outside",
            "createdAt": "2026-11-17T07:25:00Z",
            "readyAt": "2026-11-17T07:26:00Z",
        },
        {
            "id": "platform",
            "origin": "westhaven",
            "stationId": "westhaven",
            "count": 2,
            "accessible": True,
            "state": "platform",
            "createdAt": "2026-11-17T07:20:00Z",
            "readyAt": "2026-11-17T07:22:00Z",
        },
        {
            "id": "onboard",
            "origin": "eastgate",
            "stationId": "central-exchange",
            "count": 4,
            "accessible": False,
            "state": "onboard",
            "createdAt": "2026-11-17T07:10:00Z",
            "readyAt": "2026-11-17T07:10:00Z",
        },
        {
            "id": "transfer",
            "origin": "airport",
            "stationId": "union-terminal",
            "count": 2,
            "accessible": True,
            "state": "transfer",
            "createdAt": "2026-11-17T07:12:00Z",
            "readyAt": "2026-11-17T07:45:00Z",
        },
        {
            "id": "terminal-completed",
            "origin": "airport",
            "stationId": "airport",
            "count": 99,
            "accessible": False,
            "state": "completed",
            "createdAt": "2026-11-17T06:00:00Z",
            "readyAt": "2026-11-17T06:00:00Z",
        },
        {
            "id": "terminal-abandoned",
            "origin": "airport",
            "stationId": "airport",
            "count": 88,
            "accessible": False,
            "state": "abandoned",
            "createdAt": "2026-11-17T06:00:00Z",
            "readyAt": "2026-11-17T06:00:00Z",
        },
    ]
    return {
        "scenarioId": "scenario-1",
        "generation": 2,
        "version": 42,
        "simTime": "2026-11-17T07:30:00Z",
        "updatedAt": "2026-11-17T07:30:01Z",
        "cohorts": cohorts,
        "trains": [
            {
                "id": "blue-1",
                "line": "Blue",
                "load": 75,
                "capacity": 150,
                "holdReason": None,
            },
            {
                "id": "blue-2",
                "line": "Blue",
                "load": 120,
                "capacity": 150,
                "holdReason": "blocked",
            },
        ],
        "history": [
            {
                "id": "h1",
                "kind": "IncidentOpened",
                "entityId": "inc-1",
                "simTime": "2026-11-17T07:20:00Z",
                "source": "scripted",
                "description": "Elevator fault restricted access",
            }
        ],
        "incidents": [
            {
                "id": "inc-1",
                "kind": "elevator-outage",
                "target": "central-exchange",
                "state": "active",
                "description": "Elevator unavailable",
                "startedAt": "2026-11-17T07:20:00Z",
                "endsAt": None,
                "severity": 1.0,
            }
        ],
        "metrics": {
            "generated": 18,
            "completed": 5,
            "abandoned": 2,
            "deniedBoardings": 7,
            "waitingPassengerSeconds": 3600.0,
            "accessibilityFailures": 1,
            "platformOverloads": 2,
        },
    }


class FakeResponse:
    def __init__(self, value: object):
        self.status = 200
        self._body = json.dumps(value).encode()
        self.headers = Message()
        self.headers["Content-Type"] = "application/json"

    def __enter__(self) -> "FakeResponse":
        return self

    def __exit__(self, *args: object) -> None:
        return None

    def read(self, limit: int) -> bytes:
        return self._body[:limit]


class AnalyticsTests(unittest.TestCase):
    def test_conservation_uses_only_current_inventory_and_metric_terminals(self) -> None:
        result = compute_analytics(world_state())
        self.assertEqual(11, result["current"]["inventory"])
        self.assertEqual(5, result["current"]["waiting"])
        self.assertEqual(18, result["conservation"]["accounted"])
        self.assertEqual(0, result["conservation"]["delta"])
        self.assertTrue(result["conservation"]["balanced"])
        self.assertEqual(5, result["observed"]["completed"])
        self.assertEqual(2, result["observed"]["abandoned"])

    def test_actual_wire_states_preserve_mixed_inventory_conservation(self) -> None:
        result = compute_analytics(world_state())
        current = result["current"]
        self.assertEqual(
            {"outside": 3, "platform": 2, "onboard": 4, "transfer": 2},
            {state: current[state] for state in ("outside", "platform", "onboard", "transfer")},
        )
        self.assertFalse(
            any(queue["stationId"] == "union-terminal" for queue in result["queues"])
        )
        self.assertEqual(0, current["scheduledNotReady"])
        self.assertEqual(0, current["unknownActiveStatePassengers"])
        active = sum(current[state] for state in ("outside", "platform", "onboard", "transfer"))
        self.assertEqual(
            result["observed"]["generated"],
            result["observed"]["completed"] + result["observed"]["abandoned"] + active,
        )

    def test_future_linked_return_is_inventory_but_not_current_waiting(self) -> None:
        world = world_state()
        world["metrics"]["generated"] = 24
        world["cohorts"].append(
            {
                "id": "scheduled-return",
                "origin": "fairgrounds",
                "stationId": "fairgrounds",
                "count": 6,
                "accessible": False,
                "state": "outside",
                "createdAt": "2026-11-17T07:00:00Z",
                "readyAt": "2026-11-17T15:00:00Z",
            }
        )

        before = compute_analytics(world)
        self.assertEqual(17, before["current"]["inventory"])
        self.assertEqual(9, before["current"]["outside"])
        self.assertEqual(5, before["current"]["waiting"])
        self.assertEqual(6, before["current"]["scheduledNotReady"])
        self.assertFalse(
            any(queue["stationId"] == "fairgrounds" for queue in before["queues"])
        )
        self.assertTrue(before["conservation"]["balanced"])

        world["simTime"] = "2026-11-17T15:01:00Z"
        after = compute_analytics(world)
        fairgrounds = next(
            queue for queue in after["queues"] if queue["stationId"] == "fairgrounds"
        )
        self.assertEqual(6, fairgrounds["outside"])
        self.assertEqual(11, after["current"]["waiting"])
        self.assertEqual(0, after["current"]["scheduledNotReady"])
        self.assertTrue(after["conservation"]["balanced"])

    def test_queue_line_load_and_incident_effects_are_reported(self) -> None:
        result = compute_analytics(world_state())
        pinecrest = next(
            queue for queue in result["queues"] if queue["stationId"] == "pinecrest"
        )
        self.assertEqual(3, pinecrest["waiting"])
        self.assertEqual(240.0, pinecrest["oldestQueueSeconds"])
        blue = result["lineLoads"][0]
        self.assertEqual(195, blue["load"])
        self.assertEqual(300, blue["capacity"])
        self.assertEqual(0.65, blue["utilization"])
        self.assertEqual(1, len(result["history"]["activeIncidents"]))
        self.assertEqual(1, len(result["history"]["incidentEffects"]))
        self.assertFalse(result["forecast"]["available"])

    def test_upstream_failure_returns_stale_503_not_zero_success(self) -> None:
        poller = AnalyticsPoller("http://scenario:8080")
        with patch("python.analytics.service.urlopen", return_value=FakeResponse(world_state())):
            self.assertTrue(poller.refresh())
        status, live = poller.analytics_response()
        self.assertEqual(200, status)
        self.assertEqual(18, live["observed"]["generated"])

        with patch(
            "python.analytics.service.urlopen",
            side_effect=URLError("connection refused"),
        ):
            self.assertFalse(poller.refresh())
        status, stale = poller.analytics_response()
        self.assertEqual(503, status)
        self.assertTrue(stale["stale"])
        self.assertEqual(18, stale["observed"]["generated"])
        self.assertIn("connection refused", stale["warning"])

    def test_inventory_reports_a_bounded_current_conservation_check(self) -> None:
        world = world_state()
        world["updatedAt"] = datetime.now(timezone.utc).isoformat()
        world["metrics"]["generated"] += 1
        poller = AnalyticsPoller("http://scenario")
        with patch("python.analytics.service.urlopen", return_value=FakeResponse(world)):
            self.assertTrue(poller.refresh())
        status, inventory = poller.inventory_response()
        self.assertEqual(200, status)
        self.assertEqual("fresh", inventory["status"])
        self.assertEqual("mismatch", inventory["invariant"])
        self.assertEqual(
            (19, 18, 1),
            (inventory["generated"], inventory["accounted"], inventory["delta"]),
        )
        self.assertNotIn("queues", inventory)

    def test_inventory_does_not_confuse_fresh_poll_with_fresh_source(self) -> None:
        world = world_state()
        world["updatedAt"] = "2026-01-01T00:00:00Z"
        poller = AnalyticsPoller("http://scenario")
        with patch("python.analytics.service.urlopen", return_value=FakeResponse(world)):
            self.assertTrue(poller.refresh())
        status, inventory = poller.inventory_response()
        self.assertEqual(503, status)
        self.assertEqual("stale", inventory["status"])
        self.assertEqual("unknown", inventory["invariant"])
        self.assertEqual(18, inventory["generated"])

    def test_inventory_does_not_report_zero_before_first_snapshot(self) -> None:
        status, inventory = AnalyticsPoller("http://scenario").inventory_response()
        self.assertEqual(503, status)
        self.assertEqual("unavailable", inventory["status"])
        self.assertEqual("unknown", inventory["invariant"])
        self.assertIsNone(inventory["generated"])

    def test_startup_unavailable_is_explicit_and_readiness_stays_unhealthy(self) -> None:
        poller = AnalyticsPoller("http://scenario")
        with patch(
            "python.analytics.service.urlopen",
            side_effect=URLError("not ready"),
        ):
            self.assertFalse(poller.refresh())
        status, response = poller.analytics_response()
        self.assertEqual(503, status)
        self.assertEqual("unavailable", response["status"])
        health = poller.health_response()
        self.assertEqual("unhealthy", health["status"])
        self.assertFalse(health["ready"])

    def test_scenario_url_is_read_only_and_rejects_credentials(self) -> None:
        self.assertEqual(
            "http://scenario:8080/analytics-state",
            scenario_state_url("http://scenario:8080/"),
        )
        self.assertEqual(
            "https://example.test/base/analytics-state",
            scenario_state_url("https://example.test/base"),
        )
        with self.assertRaisesRegex(ValueError, "credentials"):
            scenario_state_url("http://user:secret@example.test")


if __name__ == "__main__":
    unittest.main()
