# Analytics service

Run `python3.13 main.py`; Aspire supplies `PORT` and the public
`SCENARIO_URL`. The service only reads `GET {SCENARIO_URL}/analytics-state`
every two seconds. This groups current cohort inventory by state, station,
accessibility and readiness, retaining the oldest waiting time without
transferring retired journeys, commands or grants. No checkpoint data is
deleted. It has no command endpoint or write credential.

`GET /analytics` reports observed counters, current passenger inventory,
station queues, line loads, conservation, history, and incident effects.
Completed and abandoned counters come from `WorldState.metrics` exactly once;
only `outside`, `platform`, `onboard`, and `transfer` cohorts remain in current
inventory. Forecasts are explicitly unavailable here because forecasts belong
to the scenario comparison runner.

`GET /health` reports dependency readiness. If the scenario cannot be reached,
both `/health` and `/analytics` return HTTP 503. A prior result is
retained only as a clearly marked stale response; the service never substitutes
success-shaped zero metrics.
