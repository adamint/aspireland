# Aspireland MetroOps

A fictional metro you can operate, disrupt, and ask about. Twelve train workers
share a live world with passenger queues, crowded platforms, station equipment,
and demand from a synthetic city and airport. The passenger site plans journeys;
the dispatch desk can change what's running.

This is a simulator, not railway control software or a calibrated transit model.
Train movement, outages, and journey times follow the simulation clock.

## Run it locally

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) **10.0.404**
  (see [`global.json`](global.json)) and the Aspire CLI **17.0.0**, pinned in
  [`dotnet-tools.json`](dotnet-tools.json).
- Docker Engine or Docker Desktop, running. Aspire starts Postgres, RabbitMQ,
  and Redis for you.
- [Node.js](https://nodejs.org/) **24 LTS** with npm, and
  [`uv`](https://docs.astral.sh/uv/getting-started/installation/) with Python
  **3.13** (`uv python install 3.13`).

From the clone, use its physical path. By default, Aspire names the Postgres
volume from the AppHost path, so worktrees don't share it. To reuse the old
default volume, set `MetroOps__PostgresVolume=aspireland-metroops-postgres`.
Don't share an explicit volume name across running instances; `--isolated`
doesn't change it.

```sh
cd -P .
dotnet tool restore
dotnet aspire --version
npm ci --prefix web
dotnet aspire start --isolated --non-interactive
dotnet aspire wait operations-web --timeout 180 --non-interactive
dotnet aspire wait passenger-web --timeout 180 --non-interactive
dotnet aspire describe --non-interactive
```

Use `dotnet aspire` from this clone so a different machine-wide CLI cannot
select a preview release. The AppHost and core integrations are pinned to GA
17.0.0. The optional `Aspire.Hosting.Foundry` and
`Aspire.Azure.AI.Extensions.OpenAI` integrations only ship as
`17.0.0-preview.1.26567.5` in that release train; those two packages remain
preview. Foundry stays off unless explicitly enabled.

Open **Rider site** on `passenger-web` or **Dispatch console** on
`operations-web` in the Aspire Dashboard's Resources view. Local ports change
between starts.

If you're already hosting the attendee site through a Dev Tunnel, set
`MetroOps__AttendeeUrl` (configuration key `MetroOps:AttendeeUrl`) to its HTTPS
URL before starting the AppHost. Use a URL without credentials, a query, or
a fragment. The `attendee-site` resource then links to **Attendee site (Dev Tunnel)**.
This only adds a dashboard link; it doesn't create a tunnel or change access.

In the redesigned dashboard, choose **Group resources** > **Tags** to group
resources by role, or use **Filters** to show only `interactive` (the three
site links). The other tags are `simulation`, `fleet`, `services`, `ai`,
`infrastructure`, and `config`.

Use the dashboard to follow a request across the graph or run
**Check passenger inventory** on `analytics` and **Inspect Airport arrivals**
on `information`.

On `operations-api`, **Set simulation time** offers Morning rush, Weekday
midday, Evening rush, Weekend leisure, and Quiet overnight. Confirming starts
a fresh scenario with the same seed for each preset and resets trains, riders,
incidents, and dispatch settings. The preset names map to fixed New York
simulation instants; watch the clock and passenger demand in Dispatch after
switching. **Trigger random service alert** opens one ordinary audited alert
without resetting the scenario.

`dotnet aspire stop --non-interactive` stops the instance, not its database volume.
See [the runbook](docs/deploy.md#keep-the-world) before relying on a saved world.

## Try the loop

1. Plan an accessible **Airport → Medical Center** journey on the passenger
   site. Note where it transfers.
2. In Dispatch, open an **Elevator Outage** at that interchange for `1200`
   simulated seconds. Inspect the station board, then plan the journey again.
   The station should show the restriction and the planner should avoid the
   affected accessible transfer.
3. Clear that incident from **Needs attention**. Watch the station board and
   plan the journey again.

AI is off by default. You can run the full simulation, operate the network, and
plan journeys without Azure. To provision a separate Foundry account for
the local assistants, see [Enable Foundry](docs/foundry.md).

For a longer check, [`tests/smoke.py`](tests/smoke.py) exercises train turnbacks,
passenger conservation, accessible rerouting, forecast comparisons, and
rejection of stale commands. **It resets the scenario**. Run it only against a
local instance whose state you can replace:

```sh
uv run --python 3.13 tests/smoke.py \
  --operations "$OPERATIONS_WEB_URL" --passenger "$PASSENGER_WEB_URL"
```

Set both variables from `dotnet aspire describe`. Allow up to 20 minutes for the
turnback check; achieved clock speed depends on the host. `--voice` is opt-in
and sends real, billable requests to Foundry.

## What's running

[`apphost.cs`](apphost.cs) declares the graph. The two local Vite sites forward to
separate ASP.NET gateways. Scenario, Control, Dispatch and Incidents share a
PostgreSQL transaction boundary for the world and audit history. Scenario
keeps one pending passenger snapshot and publishes it to RabbitMQ.
Information consumes it and writes a disposable Redis projection. Planner
uses the scenario state for journeys. Two Python services generate demand
and summarize passenger inventory. The train
workers talk to Control, and the optional model connections go only to the
separate AI host. See [the resource table and walkthroughs](LEARNING.md).

Scenario doesn't export successful standalone PostgreSQL spans, including the
frequent checkpoint and outbox calls. PostgreSQL errors and spans that belong to
a request still export, and database logs and metrics are unchanged.

## Run the tests

Run `dotnet test MetroOps.slnx` for the .NET tests. Persistence tests opt in
with `METROOPS_TEST_POSTGRES`, a connection string to a dedicated, disposable
`metroops_test_*` database. The paired publisher/subscriber test also needs
`METROOPS_TEST_BROKER_PORT` and `METROOPS_TEST_REDIS_PORT` for dedicated
loopback RabbitMQ and Redis instances. It uses the app's normal queue and
cache key, so don't point it at a running demo's broker or cache.

## Put it on a demo host

The [single-host runbook](docs/deploy.md) is the first deployment path, with no
cloud subscription required. Put **only** the separate, loopback-only attendee
origin behind HTTPS. It serves the passenger bundle, public status, station
boards and ordinary journeys. No operations or AI/voice flows are forwarded.
Keep Vite, both APIs, Dispatch, the Dashboard, and infrastructure ports off
public ingress. If you're carrying over a saved world, follow the
[database upgrade steps](docs/deploy.md#upgrade-an-existing-world) before
starting the new revision.

## License and assets

No license has been declared for this repository. See
[third-party notices](docs/third-party-notices.md) for the bundled font and
frontend dependencies, and [the brand notes](BRAND.md) for the Aspireland mark
and palette.
