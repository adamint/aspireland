from __future__ import annotations

import hashlib
import math
from dataclasses import dataclass
from datetime import date, datetime, time, timedelta, timezone
from typing import Iterable
from zoneinfo import ZoneInfo

UTC = timezone.utc
CITY_TIMEZONE = ZoneInfo("America/New_York")
MAX_WINDOW = timedelta(hours=6)


@dataclass(frozen=True)
class StationProfile:
    id: str
    residential: float
    jobs: float
    education: float
    leisure: float
    medical: float = 0.0
    airport_employee: float = 0.0


STATIONS = (
    StationProfile("pinecrest", 1.0, 0.1, 0.0, 0.1),
    StationProfile("westhaven", 0.8, 0.4, 0.0, 0.2),
    StationProfile("west-junction", 0.6, 0.4, 0.0, 0.2),
    StationProfile("civic-square", 0.1, 1.0, 0.1, 0.1),
    StationProfile("central-exchange", 0.2, 1.6, 0.2, 0.5),
    StationProfile("union-terminal", 0.3, 1.1, 0.1, 0.5),
    StationProfile("east-junction", 0.7, 0.4, 0.0, 0.2),
    StationProfile("riverbank", 0.7, 0.7, 0.1, 0.8),
    StationProfile("eastgate", 1.1, 0.2, 0.0, 0.2),
    StationProfile("airport", 0.1, 0.8, 0.0, 0.3, airport_employee=1.1),
    StationProfile("aerocity", 0.3, 0.8, 0.0, 0.3, airport_employee=1.0),
    StationProfile("stadium", 0.1, 0.2, 0.0, 0.5),
    StationProfile("fairgrounds", 0.2, 0.1, 0.0, 1.2),
    StationProfile("northgate", 1.0, 0.2, 0.0, 0.1),
    StationProfile("university", 0.6, 0.5, 1.3, 0.5),
    StationProfile("museum-quarter", 0.3, 0.3, 0.4, 1.1),
    StationProfile("medical-center", 0.3, 1.0, 0.3, 0.1, medical=1.4),
    StationProfile("southbank", 1.1, 0.2, 0.0, 0.3),
)
STATION_BY_ID = {station.id: station for station in STATIONS}
RESIDENTIAL_IDS = tuple(station.id for station in STATIONS if station.residential >= 0.6)


def parse_iso(value: object, field: str) -> datetime:
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


def validate_request(payload: object) -> tuple[int, int, str, datetime, datetime]:
    if not isinstance(payload, dict):
        raise ValueError("request body must be a JSON object")
    expected = {"seed", "streamVersion", "preset", "from", "to"}
    missing = expected - payload.keys()
    extra = payload.keys() - expected
    if missing:
        raise ValueError(f"missing fields: {', '.join(sorted(missing))}")
    if extra:
        raise ValueError(f"unknown fields: {', '.join(sorted(extra))}")
    seed = payload["seed"]
    stream_version = payload["streamVersion"]
    preset = payload["preset"]
    if isinstance(seed, bool) or not isinstance(seed, int):
        raise ValueError("seed must be an integer")
    if seed < -(2**63) or seed > 2**63 - 1:
        raise ValueError("seed must fit a signed 64-bit integer")
    if isinstance(stream_version, bool) or not isinstance(stream_version, int):
        raise ValueError("streamVersion must be an integer")
    if not 1 <= stream_version <= 1_000_000:
        raise ValueError("streamVersion must be between 1 and 1000000")
    if preset not in {"normal", "rough"}:
        raise ValueError("preset must be 'normal' or 'rough'")
    start = parse_iso(payload["from"], "from")
    end = parse_iso(payload["to"], "to")
    if end <= start:
        raise ValueError("to must be later than from")
    if end - start > MAX_WINDOW:
        raise ValueError("request window must not exceed 6 hours")
    return seed, stream_version, preset, start, end


class KeyedRandom:
    def __init__(self, seed: int, stream_version: int, preset: str):
        self._root = f"metroops-demand-profile-2-v{stream_version}|{seed}|{preset}".encode()

    def _bytes(self, *parts: object) -> bytes:
        key = "|".join(str(part) for part in parts).encode()
        return hashlib.blake2b(self._root + b"|" + key, digest_size=16).digest()

    def uniform(self, *parts: object) -> float:
        return int.from_bytes(self._bytes(*parts)[:8], "big") / 2**64

    def normal(self, *parts: object) -> float:
        first = max(self.uniform(*parts, "u1"), 1e-12)
        second = self.uniform(*parts, "u2")
        return math.sqrt(-2 * math.log(first)) * math.cos(2 * math.pi * second)

    def identifier(self, prefix: str, *parts: object) -> str:
        return f"{prefix}-{self._bytes(*parts).hex()[:20]}"

    def weighted(self, items: Iterable[tuple[str, float]], *parts: object) -> str:
        choices = [(name, max(0.0, weight)) for name, weight in items]
        total = sum(weight for _, weight in choices)
        if total <= 0:
            raise ValueError("weighted choice requires a positive weight")
        target = self.uniform(*parts) * total
        cumulative = 0.0
        for name, weight in choices:
            cumulative += weight
            if target < cumulative:
                return name
        return choices[-1][0]


def _bell(hour: float, center: float, width: float) -> float:
    return math.exp(-0.5 * ((hour - center) / width) ** 2)


def _weekday_factor(day: int) -> float:
    return (0.96, 1.03, 1.06, 1.02, 0.91, 0.78, 0.70)[day]


def _purpose_weights(station: StationProfile, at: datetime) -> list[tuple[str, float]]:
    at = at.astimezone(CITY_TIMEZONE)
    hour = at.hour + at.minute / 60
    weekend = at.weekday() >= 5
    morning = _bell(hour, 7.7, 1.2)
    evening = _bell(hour, 17.4, 1.5)
    midday = _bell(hour, 12.5, 2.8)
    night = _bell(hour, 21.0, 2.0)
    shift_start = sum(_bell(hour, value, 0.65) for value in (5.7, 13.7, 21.7))
    shift_end = sum(_bell(hour, value, 0.65) for value in (7.2, 15.2, 23.2))
    if weekend:
        return [
            ("residential-leisure", station.residential * (0.15 + 0.50 * midday + 0.35 * night)),
            ("leisure-return", station.leisure * (0.08 + 0.50 * evening + 0.35 * night)),
            ("medical", station.medical * (0.20 + 0.35 * shift_start + 0.35 * shift_end)),
            ("general", 0.06 + station.jobs * 0.04),
        ]
    return [
        ("commute", station.residential * (0.08 + 0.95 * morning)),
        ("job-return", station.jobs * (0.03 + 0.70 * evening)),
        ("education", station.residential * 0.24 * _bell(hour, 8.2, 1.0)),
        ("education-return", station.education * 0.60 * _bell(hour, 16.0, 1.4)),
        ("residential-leisure", station.residential * 0.22 * evening),
        ("leisure-return", station.leisure * (0.08 + 0.32 * night)),
        ("medical", station.medical * (0.16 + 0.35 * shift_start + 0.35 * shift_end)),
        ("general", 0.05 + midday * 0.05),
    ]


def _rate(station: StationProfile, at: datetime, preset: str) -> float:
    at = at.astimezone(CITY_TIMEZONE)
    weights = _purpose_weights(station, at)
    rate = 0.006 + sum(weight for _, weight in weights) * 0.055
    if at.weekday() >= 5:
        if station.id == "civic-square":
            rate *= 0.28
        if station.id in {"museum-quarter", "fairgrounds", "riverbank"}:
            rate *= 1.45
    else:
        rate *= _weekday_factor(at.weekday())
        if station.id == "civic-square" and 7 <= at.hour <= 18:
            rate *= 1.25
    return rate * 20 * (1.22 if preset == "rough" else 1.0)


def _destination_weights(origin: str, purpose: str, at: datetime) -> list[tuple[str, float]]:
    at = at.astimezone(CITY_TIMEZONE)
    weekend = at.weekday() >= 5
    result: list[tuple[str, float]] = []
    for station in STATIONS:
        if station.id == origin:
            continue
        if purpose == "commute":
            weight = station.jobs + station.education * 0.45 + station.medical * 0.7
        elif purpose == "job-return":
            weight = station.residential
        elif purpose == "education":
            weight = station.education * 1.7 + station.jobs * 0.15
        elif purpose == "education-return":
            weight = station.residential + station.leisure * 0.15
        elif purpose == "residential-leisure":
            weight = station.leisure * (1.5 if weekend else 1.0) + station.jobs * 0.08
        elif purpose == "leisure-return":
            weight = station.residential
        elif purpose == "medical":
            weight = station.medical * 2.0 + station.jobs * 0.15
            if origin == "medical-center":
                weight = station.residential
        else:
            weight = (
                station.residential * 0.35
                + station.jobs * 0.45
                + station.education * 0.15
                + station.leisure * (0.35 if weekend else 0.18)
                + station.medical * 0.2
            )
        result.append((station.id, max(weight, 0.01)))
    return result


def _return_delay_hours(purpose: str, random: KeyedRandom, key: tuple[object, ...]) -> float | None:
    probability_and_range = {
        "commute": (0.91, 7.0, 10.0),
        "job-return": (0.18, 8.0, 12.0),
        "education": (0.88, 5.0, 9.0),
        "education-return": (0.15, 8.0, 12.0),
        "residential-leisure": (0.80, 2.0, 5.5),
        "leisure-return": (0.12, 8.0, 12.0),
        "medical": (0.72, 1.3, 4.5),
        "general": (0.52, 1.0, 6.0),
    }.get(purpose)
    if probability_and_range is None:
        return None
    probability, low, high = probability_and_range
    if random.uniform(*key, "return-present") >= probability:
        return None
    return low + (high - low) * random.uniform(*key, "return-duration")


def _cohort(
    random: KeyedRandom,
    origin: str,
    destination: str,
    at: datetime,
    key: tuple[object, ...],
    purpose: str,
    count_range: tuple[int, int] = (1, 10),
    return_at: datetime | None | object = ...,
) -> dict[str, object]:
    minimum, maximum = count_range
    size_shape = random.uniform(*key, "group-size") ** 1.7
    count = minimum + int(size_shape * (maximum - minimum + 1))
    count = min(count, maximum)
    accessible = random.uniform(*key, "accessible") < (0.075 if purpose == "medical" else 0.052)
    base_patience = 1_050 if purpose.startswith("event") else 1_500
    patience = round(
        max(600, min(3_600, base_patience + random.normal(*key, "patience") * 420)),
        1,
    )
    if return_at is ...:
        delay = _return_delay_hours(purpose, random, key)
        resolved_return = at + timedelta(hours=delay) if delay is not None else None
    else:
        resolved_return = return_at
    value: dict[str, object] = {
        "id": random.identifier("cohort", *key),
        "origin": origin,
        "destination": destination,
        "count": count,
        "accessible": accessible,
        "at": iso(at),
        "patienceSeconds": patience,
    }
    if isinstance(resolved_return, datetime):
        value["returnAt"] = iso(resolved_return)
    return value


def _regular_cohorts(
    random: KeyedRandom, at: datetime, preset: str
) -> Iterable[dict[str, object]]:
    minute_key = at.strftime("%Y%m%dT%H%M")
    for station in STATIONS:
        rate = _rate(station, at, preset)
        whole_opportunities = int(rate)
        opportunity_count = whole_opportunities + (1 if rate > whole_opportunities else 0)
        for slot in range(opportunity_count):
            key = ("regular", station.id, minute_key, slot)
            if slot >= whole_opportunities and random.uniform(*key, "occurs") >= (
                rate - whole_opportunities
            ):
                continue
            purpose = random.weighted(_purpose_weights(station, at), *key, "purpose")
            destination = random.weighted(
                _destination_weights(station.id, purpose, at),
                *key,
                "destination",
            )
            yield _cohort(
                random,
                station.id,
                destination,
                at,
                key,
                purpose,
            )

    local = at.astimezone(CITY_TIMEZONE)
    hour = local.hour + local.minute / 60
    shift_pressure = sum(_bell(hour, value, 0.55) for value in (4.8, 12.8, 20.8))
    employee_scale = sum(station.airport_employee for station in STATIONS) / 2.1
    employee_rate = min(
        0.34, shift_pressure * employee_scale * (0.22 if preset == "rough" else 0.18)
    )
    if random.uniform("airport-employees", minute_key, "occurs") < employee_rate:
        origin = random.weighted(
            [(station_id, STATION_BY_ID[station_id].residential) for station_id in RESIDENTIAL_IDS],
            "airport-employees",
            minute_key,
            "origin",
        )
        destination = random.weighted(
            [
                ("airport", STATION_BY_ID["airport"].airport_employee),
                ("aerocity", STATION_BY_ID["aerocity"].airport_employee),
            ],
            "airport-employees",
            minute_key,
            "destination",
        )
        yield _cohort(
            random,
            origin,
            destination,
            at,
            ("airport-employees", minute_key),
            "airport-employee",
            (2, 9),
            at + timedelta(hours=7.5 + random.uniform("airport-employees", minute_key, "shift") * 2),
        )


def _event_cohorts(
    random: KeyedRandom, at: datetime, preset: str
) -> Iterable[dict[str, object]]:
    minute_key = at.strftime("%Y%m%dT%H%M")
    local = at.astimezone(CITY_TIMEZONE)
    hour = local.hour + local.minute / 60
    multiplier = 1.15 if preset == "rough" else 1.0
    if local.weekday() == 5:
        stadium_rate = min(0.75, _bell(hour, 22.05, 0.62) * 0.68 * multiplier)
        for slot in range(2):
            if random.uniform("stadium-event", minute_key, slot) >= stadium_rate / 2:
                continue
            destination = random.weighted(
                [
                    (station.id, station.residential + station.jobs * 0.12)
                    for station in STATIONS
                    if station.id != "stadium"
                ],
                "stadium-event",
                minute_key,
                slot,
                "destination",
            )
            yield _cohort(
                random,
                "stadium",
                destination,
                at,
                ("stadium-event", minute_key, slot),
                "event-stadium",
                (5, 24),
                None,
            )
    if local.weekday() >= 5:
        arrival_rate = min(0.42, _bell(hour, 11.0, 1.35) * 0.34 * multiplier)
        if random.uniform("fair-event-arrival", minute_key) < arrival_rate:
            origin = random.weighted(
                [
                    (station.id, station.residential)
                    for station in STATIONS
                    if station.id != "fairgrounds"
                ],
                "fair-event-arrival",
                minute_key,
                "origin",
            )
            return_at = local.replace(hour=17, minute=15, second=0, microsecond=0)
            return_at += timedelta(
                minutes=int(random.uniform("fair-event-arrival", minute_key, "return") * 105)
            )
            yield _cohort(
                random,
                origin,
                "fairgrounds",
                at,
                ("fair-event-arrival", minute_key),
                "event-fair",
                (3, 18),
                return_at,
            )


FLIGHT_TIMES = (
    ("arrival", "06:20"),
    ("departure", "06:45"),
    ("arrival", "07:35"),
    ("departure", "08:10"),
    ("arrival", "09:25"),
    ("departure", "10:20"),
    ("arrival", "11:40"),
    ("departure", "12:30"),
    ("arrival", "14:10"),
    ("departure", "15:00"),
    ("arrival", "16:25"),
    ("departure", "17:15"),
    ("arrival", "18:35"),
    ("departure", "19:20"),
    ("arrival", "20:45"),
    ("departure", "21:35"),
    ("arrival", "22:30"),
)


def _weather(random: KeyedRandom, day: date, preset: str) -> tuple[str, int, float, float]:
    value = random.uniform("weather", day.isoformat())
    if preset == "rough":
        if value < 0.28:
            return "clear", 2, 0.015, 1.00
        if value < 0.72:
            return "rain", 18, 0.07, 0.92
        return "storm", 52, 0.20, 0.78
    if value < 0.67:
        return "clear", 1, 0.008, 1.00
    if value < 0.91:
        return "rain", 13, 0.04, 0.94
    return "storm", 42, 0.14, 0.82


def flights_for_day(
    random: KeyedRandom, day: date, preset: str
) -> list[tuple[dict[str, object], datetime, datetime, int, bool, str]]:
    weather, shared_delay, cancellation_rate, load_factor = _weather(random, day, preset)
    day_load = 0.88 + random.uniform("flight-day-load", day.isoformat()) * 0.24
    flights = []
    for index, (direction, clock) in enumerate(FLIGHT_TIMES):
        hour, minute = (int(value) for value in clock.split(":"))
        scheduled = datetime.combine(day, time(hour, minute), CITY_TIMEZONE).astimezone(UTC)
        jitter = round(random.normal("flight", day.isoformat(), index, "delay") * 7)
        delay = max(0, shared_delay + jitter)
        if weather == "storm":
            delay += round(random.uniform("flight", day.isoformat(), index, "storm-delay") * 28)
        cancelled = (
            random.uniform("flight", day.isoformat(), index, "cancelled") < cancellation_rate
        )
        actual = scheduled + timedelta(minutes=delay)
        peak = 1.14 if 7 <= hour <= 9 or 16 <= hour <= 20 else 0.92
        base_load = 105 + int(random.uniform("flight", day.isoformat(), index, "load") * 85)
        passengers = max(45, min(210, round(base_load * peak * day_load * load_factor)))
        flight_id = random.identifier("flight", day.isoformat(), direction, index)
        record = {
            "id": flight_id,
            "direction": direction,
            "scheduledAt": iso(scheduled),
            "actualAt": iso(actual),
            "passengers": passengers,
            "cancelled": cancelled,
            "weather": weather,
        }
        flights.append((record, scheduled, actual, passengers, cancelled, weather))
    return flights


def _flight_cohorts(
    random: KeyedRandom,
    flight: tuple[dict[str, object], datetime, datetime, int, bool, str],
    start: datetime,
    end: datetime,
) -> Iterable[dict[str, object]]:
    record, _, actual, passengers, cancelled, weather = flight
    if cancelled:
        return
    flight_id = str(record["id"])
    rail_share = 0.22 + random.uniform("flight-demand", flight_id, "rail-share") * 0.30
    rail_passengers = max(1, round(passengers * rail_share))
    cohort_count = 2 + int(random.uniform("flight-demand", flight_id, "cohorts") * 3)
    allocations = [rail_passengers // cohort_count] * cohort_count
    for index in range(rail_passengers % cohort_count):
        allocations[index] += 1
    if record["direction"] == "arrival":
        shared_exit = {"clear": 15, "rain": 21, "storm": 30}[weather]
        for index, count in enumerate(allocations):
            delay = shared_exit + round(
                random.uniform("flight-demand", flight_id, index, "exit") * 15
            )
            at = actual + timedelta(minutes=delay + index * 2)
            if not start < at <= end:
                continue
            destination = random.weighted(
                [
                    (
                        station.id,
                        station.jobs * 0.35
                        + station.residential * 0.45
                        + station.leisure * 0.25
                        + (0.65 if station.id in {"central-exchange", "union-terminal", "aerocity"} else 0),
                    )
                    for station in STATIONS
                    if station.id != "airport"
                ],
                "flight-demand",
                flight_id,
                index,
                "destination",
            )
            cohort = _cohort(
                random,
                "airport",
                destination,
                at,
                ("flight-arrival", flight_id, index),
                "flight-arrival",
                (count, count),
                None,
            )
            yield cohort
    else:
        shared_lead = 82 + int(random.uniform("flight-demand", flight_id, "lead") * 28)
        for index, count in enumerate(allocations):
            spread = round(random.normal("flight-demand", flight_id, index, "spread") * 13)
            at = actual - timedelta(minutes=max(48, shared_lead + spread + index * 3))
            if not start < at <= end:
                continue
            origin = random.weighted(
                [
                    (
                        station.id,
                        station.residential * 0.50
                        + station.jobs * 0.28
                        + station.leisure * 0.24
                        + (0.40 if station.id in {"central-exchange", "union-terminal", "aerocity"} else 0),
                    )
                    for station in STATIONS
                    if station.id != "airport"
                ],
                "flight-demand",
                flight_id,
                index,
                "origin",
            )
            yield _cohort(
                random,
                origin,
                "airport",
                at,
                ("flight-departure", flight_id, index),
                "flight-departure",
                (count, count),
                None,
            )


def generate(
    seed: int, stream_version: int, preset: str, start: datetime, end: datetime
) -> dict[str, list[dict[str, object]]]:
    if end <= start or end - start > MAX_WINDOW:
        raise ValueError("generation window must be positive and no longer than 6 hours")
    start, end = start.astimezone(UTC), end.astimezone(UTC)
    random = KeyedRandom(seed, stream_version, preset)
    cohorts: list[dict[str, object]] = []
    first_minute = start.replace(second=0, microsecond=0)
    if first_minute <= start:
        first_minute += timedelta(minutes=1)
    at = first_minute
    while at <= end:
        cohorts.extend(_regular_cohorts(random, at, preset))
        cohorts.extend(_event_cohorts(random, at, preset))
        at += timedelta(minutes=1)

    flights: list[dict[str, object]] = []
    day = (start.astimezone(CITY_TIMEZONE) - timedelta(days=1)).date()
    final_day = (end.astimezone(CITY_TIMEZONE) + timedelta(days=1)).date()
    while day <= final_day:
        for flight in flights_for_day(random, day, preset):
            record, _, actual, _, _, _ = flight
            if start < actual <= end:
                flights.append(record)
            cohorts.extend(_flight_cohorts(random, flight, start, end))
        day += timedelta(days=1)

    cohorts.sort(key=lambda item: (str(item["at"]), str(item["id"])))
    flights.sort(key=lambda item: (str(item["actualAt"]), str(item["id"])))
    if len(cohorts) > 20_000 or len(flights) > 1_000:
        raise ValueError("generated result exceeded service safety bounds")
    return {"cohorts": cohorts, "flights": flights}


def generate_from_payload(payload: object) -> dict[str, list[dict[str, object]]]:
    return generate(*validate_request(payload))
