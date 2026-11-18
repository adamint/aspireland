from __future__ import annotations

import unittest
import http.client
import json
import threading
from datetime import datetime, timedelta, timezone
from http.server import ThreadingHTTPServer
from zoneinfo import ZoneInfo

from python.demand.generator import (
    KeyedRandom,
    STATION_BY_ID,
    _rate,
    flights_for_day,
    generate,
    validate_request,
)
from python.demand.main import DemandHandler

UTC = timezone.utc


class DemandTests(unittest.TestCase):
    def test_request_partition_and_call_order_do_not_change_results(self) -> None:
        start = datetime(2026, 11, 17, 6, 0, tzinfo=UTC)
        midpoint = start + timedelta(hours=3)
        end = start + timedelta(hours=6)

        second_first = generate(42017, 3, "normal", midpoint, end)
        first_second = generate(42017, 3, "normal", start, midpoint)
        whole = generate(42017, 3, "normal", start, end)

        combined_cohorts = sorted(
            first_second["cohorts"] + second_first["cohorts"],
            key=lambda item: (item["at"], item["id"]),
        )
        combined_flights = sorted(
            first_second["flights"] + second_first["flights"],
            key=lambda item: (item["actualAt"], item["id"]),
        )
        self.assertEqual(whole["cohorts"], combined_cohorts)
        self.assertEqual(whole["flights"], combined_flights)
        self.assertEqual(
            first_second,
            generate(42017, 3, "normal", start, midpoint),
        )
        self.assertEqual(
            second_first,
            generate(42017, 3, "normal", midpoint, end),
        )

    def test_stream_version_changes_versioned_opportunities(self) -> None:
        start = datetime(2026, 11, 17, 6, 0, tzinfo=UTC)
        end = start + timedelta(hours=6)
        first = generate(99, 1, "normal", start, end)
        second = generate(99, 2, "normal", start, end)
        first_ids = {item["id"] for item in first["cohorts"]}
        second_ids = {item["id"] for item in second["cohorts"]}
        self.assertTrue(first_ids)
        self.assertTrue(second_ids)
        self.assertFalse(first_ids & second_ids)

    def test_normal_weekday_peak_has_visible_network_throughput(self) -> None:
        start = datetime(2026, 11, 17, 12, 30, tzinfo=UTC)
        normal = generate(20261117, 1, "normal", start, start + timedelta(minutes=10))
        passenger_count = sum(int(item["count"]) for item in normal["cohorts"])
        self.assertGreaterEqual(passenger_count, 600)
        self.assertLessEqual(passenger_count, 1_000)
        self.assertGreater(len(normal["cohorts"]), 100)

        rough = generate(20261117, 1, "rough", start, start + timedelta(minutes=10))
        rough_count = sum(int(item["count"]) for item in rough["cohorts"])
        self.assertGreater(rough_count, passenger_count)

    def test_station_character_changes_between_weekday_and_weekend(self) -> None:
        civic = STATION_BY_ID["civic-square"]
        museum = STATION_BY_ID["museum-quarter"]
        monday_midday = datetime(2026, 11, 16, 17, 0, tzinfo=UTC)
        saturday_midday = datetime(2026, 11, 21, 17, 0, tzinfo=UTC)

        self.assertLess(
            _rate(civic, saturday_midday, "normal"),
            _rate(civic, monday_midday, "normal") * 0.5,
        )
        self.assertGreater(
            _rate(museum, saturday_midday, "normal"),
            _rate(museum, monday_midday, "normal"),
        )

    def test_city_rush_hours_are_directional_and_midday_is_quieter(self) -> None:
        homes = {"pinecrest", "eastgate", "northgate", "southbank", "westhaven"}
        jobs = {"civic-square", "central-exchange", "union-terminal", "university", "medical-center"}
        totals = {}
        flows = {}
        for label, day, hour in (("am", 17, 12), ("pm", 17, 22), ("midday", 17, 17), ("weekend", 21, 17)):
            cohorts = []
            for seed in (11, 42, 123):
                start = datetime(2026, 11, day, hour, tzinfo=UTC)
                cohorts.extend(generate(seed, 1, "normal", start, start + timedelta(hours=2))["cohorts"])
            totals[label] = sum(c["count"] for c in cohorts)
            flows[label] = (
                sum(c["count"] for c in cohorts if c["origin"] in homes and c["destination"] in jobs),
                sum(c["count"] for c in cohorts if c["origin"] in jobs and c["destination"] in homes),
                sum(c["count"] for c in cohorts if c["origin"] == "civic-square"),
                sum(c["count"] for c in cohorts if c["destination"] in {"museum-quarter", "fairgrounds"}),
            )
        self.assertGreater(flows["am"][0], flows["pm"][0] * 1.7, flows)
        self.assertGreater(flows["pm"][1], flows["am"][1] * 1.7, flows)
        self.assertLess(totals["midday"], min(totals["am"], totals["pm"]) * 0.7, totals)
        self.assertLess(flows["weekend"][2], flows["midday"][2] * 0.5, flows)
        self.assertGreater(flows["weekend"][3], flows["midday"][3], flows)

    def test_flight_schedule_uses_city_time_across_summer_and_winter(self) -> None:
        city = ZoneInfo("America/New_York")
        for month in (7, 11):
            flights = flights_for_day(KeyedRandom(42, 1, "normal"), datetime(2026, month, 17).date(), "normal")
            scheduled = flights[0][1]
            self.assertEqual((6, 20), (scheduled.astimezone(city).hour, scheduled.astimezone(city).minute))
            self.assertEqual(0, scheduled.utcoffset().total_seconds())

    def test_timezone_offsets_and_fall_back_partition_preserve_opportunities(self) -> None:
        start = datetime(2026, 11, 1, 4, tzinfo=UTC)
        midpoint, end = start + timedelta(hours=2), start + timedelta(hours=4)
        whole = generate(42, 1, "normal", start, end)
        city = ZoneInfo("America/New_York")
        self.assertEqual(whole, generate(42, 1, "normal", start.astimezone(city), end.astimezone(city)))
        first = generate(42, 1, "normal", start, midpoint)
        second = generate(42, 1, "normal", midpoint, end)
        self.assertEqual(
            whole["cohorts"],
            sorted(first["cohorts"] + second["cohorts"], key=lambda c: (c["at"], c["id"])),
        )
        self.assertEqual(
            whole["flights"],
            sorted(first["flights"] + second["flights"], key=lambda f: (f["actualAt"], f["id"])),
        )

    def test_saturday_stadium_and_weekend_fair_surges_are_bounded(self) -> None:
        stadium_start = datetime(2026, 11, 21, 20, 0, tzinfo=UTC)
        stadium = generate(20261117, 1, "normal", stadium_start, stadium_start + timedelta(hours=3))
        stadium_departures = [
            item for item in stadium["cohorts"] if item["origin"] == "stadium"
        ]
        self.assertTrue(stadium_departures)
        self.assertTrue(all(1 <= int(item["count"]) <= 24 for item in stadium_departures))

        fair_start = datetime(2026, 11, 22, 9, 0, tzinfo=UTC)
        fair = generate(20261117, 1, "normal", fair_start, fair_start + timedelta(hours=6))
        fair_arrivals = [
            item for item in fair["cohorts"] if item["destination"] == "fairgrounds"
        ]
        self.assertTrue(fair_arrivals)
        self.assertTrue(all(int(item["count"]) <= 18 for item in fair_arrivals))

    def test_flights_share_daily_weather_and_drive_airport_cohorts(self) -> None:
        random = KeyedRandom(20261117, 1, "rough")
        flights = flights_for_day(random, datetime(2026, 11, 17, tzinfo=UTC).date(), "rough")
        weather = {flight[0]["weather"] for flight in flights}
        self.assertEqual(1, len(weather))
        self.assertTrue(
            all(flight[2] >= flight[1] for flight in flights),
            "actual flight times must not precede the schedule",
        )

        arrival = next(flight for flight in flights if flight[0]["direction"] == "arrival" and not flight[4])
        window_start = arrival[2]
        batch = generate(
            20261117,
            1,
            "rough",
            window_start,
            window_start + timedelta(hours=1),
        )
        airport_origins = [
            item for item in batch["cohorts"] if item["origin"] == "airport"
        ]
        self.assertTrue(airport_origins)
        self.assertTrue(any(int(item["count"]) > 10 for item in airport_origins))

    def test_linked_returns_are_not_emitted_as_duplicate_cohorts(self) -> None:
        start = datetime(2026, 11, 17, 6, 0, tzinfo=UTC)
        response = generate(710, 1, "normal", start, start + timedelta(hours=6))
        linked = [item for item in response["cohorts"] if "returnAt" in item]
        self.assertTrue(linked)
        ids = [item["id"] for item in response["cohorts"]]
        self.assertEqual(len(ids), len(set(ids)))
        self.assertFalse(
            any(
                item["at"] == outbound["returnAt"]
                and item["origin"] == outbound["destination"]
                and item["destination"] == outbound["origin"]
                for outbound in linked
                for item in response["cohorts"]
            )
        )

    def test_validation_rejects_oversized_and_naive_windows(self) -> None:
        with self.assertRaisesRegex(ValueError, "6 hours"):
            validate_request(
                {
                    "seed": 1,
                    "streamVersion": 1,
                    "preset": "normal",
                    "from": "2026-11-17T00:00:00Z",
                    "to": "2026-11-17T06:00:01Z",
                }
            )
        with self.assertRaisesRegex(ValueError, "UTC offset"):
            validate_request(
                {
                    "seed": 1,
                    "streamVersion": 1,
                    "preset": "normal",
                    "from": "2026-11-17T00:00:00",
                    "to": "2026-11-17T01:00:00",
                }
            )

    def test_http_endpoint_accepts_bounded_chunked_json(self) -> None:
        quiet_handler = type(
            "QuietDemandHandler",
            (DemandHandler,),
            {"log_message": lambda self, format, *args: None},
        )
        server = ThreadingHTTPServer(("127.0.0.1", 0), quiet_handler)
        thread = threading.Thread(target=server.serve_forever)
        thread.start()
        try:
            payload = json.dumps(
                {
                    "seed": 1,
                    "streamVersion": 1,
                    "preset": "normal",
                    "from": "2026-11-17T06:00:00Z",
                    "to": "2026-11-17T07:00:00Z",
                }
            ).encode()
            connection = http.client.HTTPConnection(
                "127.0.0.1", server.server_address[1], timeout=3
            )
            connection.request(
                "POST",
                "/demand",
                body=iter([payload]),
                headers={"Content-Type": "application/json"},
                encode_chunked=True,
            )
            response = connection.getresponse()
            body = json.loads(response.read())
            connection.close()
            self.assertEqual(200, response.status)
            self.assertIn("cohorts", body)
            self.assertIn("flights", body)
        finally:
            server.shutdown()
            server.server_close()
            thread.join(timeout=3)


if __name__ == "__main__":
    unittest.main()
