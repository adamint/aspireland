# Demand service

Run `python3.13 main.py`; Aspire supplies `PORT`. `GET /health` reports process
health. `POST /demand` accepts the contract `DemandRequest` and limits each
exclusive/inclusive `(from, to]` window to six simulated hours.

Demand is generated from versioned BLAKE2 keys for each domain, station, flight,
date, minute, and opportunity. Peak periods use multiple bounded station-minute
opportunities, producing many small regular cohorts instead of rare giant
groups. There is no shared random sequence, so request order and window
partitioning do not change IDs or results. Numeric station profiles drive
weekday, weekend, education, medical, leisure, and airport-shift travel. Return
journeys are linked with `returnAt` and are not emitted a second time. Daily
weather is sampled once and shared by that day's synthetic flight delays,
cancellations, loads, exit delays, and airport cohorts.

Profile 2 uses `America/New_York` city time for weekday/hour curves, airport
shifts, events and flight schedules. Wire timestamps and opportunity keys
remain UTC; browser timezone preferences never change demand. DST transitions
retain distinct UTC minutes. Residential AM departures and job/education PM
returns follow separate purpose/destination weights. Midday office traffic is
quieter; weekend leisure destinations become busier. Rates retain their relative
station/time differences rather than flattening at a shared three-cohort cap.

`normal` and `rough` change synthetic rates, not capacity or operating rules.
The model is deliberately illustrative rather than calibrated to a real city.
