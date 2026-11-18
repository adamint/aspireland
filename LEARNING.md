# Learning Aspire with Aspireland

Goal: explain and demonstrate developing **with a coding assistant** using
Aspire, and building **application AI assistants** with Aspire. These are
different actors with different permissions. Work from local development toward
deployment, one observed operation at a time. Discuss cloud changes before
provisioning anything.

## 1. The declared graph and one observed request

Inspected on November 17, 2026. The graph below reflects the completed wiring
cleanup; the original request trace predates those changes.

The [AppHost](apphost.cs) uses Aspire 13.6.5.
Eight concrete deterministic executables own their routes and dependencies.
The existing AI resource runs its own deployable project.

| Resources | Responsibility |
| --- | --- |
| `operations-web`, `passenger-web` | Two React/Vite sites from the same frontend project |
| `operations-api`, `passenger-api` | Deterministic gateways; explicit forwarding to the AI host |
| `scenario`, `demand` | Authoritative simulation coordinator and separate Python synthetic-demand service |
| `control`, twelve `train-*` workers | Train-worker coordination and individual train processes |
| `dispatch`, `incidents` | Operational commands and incident changes |
| `planner`, `information` | Journey planning and passenger-facing projections/arrivals |
| `analytics` | Separate Python service reading scenario state |
| `ai-operations` | Dedicated AI host with distinct passenger/operator routes and tools |
| `postgres` / `metro` | Authoritative checkpoint, command/event history and publication outbox |
| `events` | RabbitMQ distribution of world snapshots |
| `cache` | Redis; projection writes are not the public request's read source |

### Request: show the passenger the current network

The frontend [polls](web/src/hooks.ts) `GET /api/world` every 1.5 seconds.
The [Vite proxy](web/vite.config.ts) forwards `/api` requests to its supplied
`API_URL`. The passenger [gateway](src/MetroOps.PassengerApi/PassengerRoutes.cs)
calls the information service's `GET /world`.
The [handler](src/MetroOps.Information/InformationRoutes.cs) returns the latest
in-memory projection and network definition. Before the first projection it
returns 503; after ten wall-clock seconds without an update it marks data stale.

The [projection consumer](src/MetroOps.Information/InformationProjection.cs)
subscribes to RabbitMQ and bootstraps from scenario state. When accepting a newer
world it writes Redis with a ten-minute expiry before updating memory.
The inspected public handler does not read Redis. PostgreSQL is authoritative;
Redis is not the source read by this endpoint.

### Observed evidence

Sent one GET through the running passenger-web proxy with an HTTP client and
a supplied W3C trace header. Response: HTTP 200, world version 49586,
`stale: false`, no warning.

```text
passenger-api: GET /api/world        span d31bc40ba30337f4
  passenger-api: outgoing GET        span b1c4160a7293ac3b
    information: GET /world          span 2f96bd3da8bfaa6f
```

The first observed span's parent is the supplied caller ID
`d45c6b5782e77954`; no caller span was exported.
All three spans share the trace ID. Each subsequent span's parent ID matches
the preceding span ID.

[Telemetry registration](src/MetroOps.ServiceDefaults/Telemetry.cs)
enables ASP.NET Core and HttpClient instrumentation and conditionally exports
OTLP when an endpoint is configured.

- The AppHost declares dependencies; a trace records instrumented operations.
- No browser or Node span was observed. This does not mean the proxy was skipped.
- No Redis span is not, alone, proof of no Redis activity. Here the handler's
  implementation establishes that it reads memory.
- Background publication and a later read are not automatically one trace.
- HTTP success does not prove simulation correctness. `stale: false` is the
  application's timestamp-based freshness assessment.

### Initial observation: configuration, not inference

The capabilities endpoint reported text, transcription and speech enabled.
This only reflects configuration checks. The code supports OpenAI and Azure
OpenAI/Entra, but this lesson did not verify the running endpoint, deployment,
credentials or successful model inference. No model call was made.

That observation was not inference proof. The SDK-backed verification below
is a separate, later operation.

## 2. Keep the AppHost at the root

`apphost.cs` and `apphost.run.json` now live together at the repository root.
The single `aspire.config.json` selects that file and retains SDK 13.6.5.
Resource paths are relative to the root; the resource names, user-secrets ID
`aspireland-metroops-local` and named PostgreSQL volume are unchanged.
Launch profiles do not open a browser automatically.
The NuGet source configuration and useful Aspire VS Code recommendation moved
to the root too; the obsolete AppHost directory is gone.

Run `aspire start --apphost apphost.cs --isolated --non-interactive` from the
root. When several apps are running, stop this one with
`aspire stop --apphost apphost.cs --non-interactive`, never `--all`.

The relocated app resumed the previous scenario,
generation 10, from the same volume; its version advanced past 50718.
Both websites responded and all 25 application processes became healthy.
Four workers initially hit concurrent MSBuild cache-file locks and were started
individually after the competing builds finished. This was not a domain reset
or a successful-looking substitute for running those workers.

## 3. Optional Foundry, official model clients

The original local experiments below used an existing Foundry account.
The release AppHost provisions nothing in Azure by default. With explicit
opt-in it provisions a separate account using typed deployment APIs and gives
only the AI host model references. See [Enable Foundry](docs/foundry.md).

`AddAzureOpenAIClient` registers the official SDK client there.
AzureCliCredential selects the configured tenant and
the SDK refreshes tokens. The manual HTTP serializers, multipart construction,
authentication headers and token cache were deleted. API input validation,
the six-round/twelve-call tool limit, cancellation, provider errors and
application-side tool permissions remain in application code.

Before any client is registered, GenAI message-content capture is set to false;
`EnableSensitiveTelemetryData=false` explicitly overrides environment defaults.
SDK metadata spans are enabled. No prompt, response, tool argument/result or
audio payload is added to telemetry.

### Observed SDK inference

Observed in a test against an Entra-only Foundry account:

- The account remained Entra-only (`disableLocalAuth=true`).
- A real Airport request called `resolve_station` and `get_arrivals`, returning
  the typed Airport board with five incoming services.
- SDK speech produced 84,746 MP3 bytes for a synthetic test question.
  SDK transcription recognized the intended origin/destination; that transcript
  drove two station resolutions and the real journey planner.
- The structured itinerary was present; speech of its displayed answer produced
  308,906 MP3 bytes. No conversation or audio was saved for this check.

The trace contained the API calls, service tools, SDK `chat gpt41` spans and
model HTTP requests without errors. SDK span
attributes contain model/token/finish metadata, not message content. This
verifies inference rather than treating capability flags as proof.

## 4. References and coordinated project builds

C# HTTP clients now use Aspire service discovery: `https+http://scenario`,
`https+http://planner`, and the other declared resource names. Both the domain
client and the AI tool client use injected, discovery-enabled HttpClients.
The worker's control client does too. The custom C# `*_URL` lookup and the
privately constructed AI tool HttpClient are gone.

The graph is explicit: each service declaration shows its role, references,
health endpoint and startup dependencies. There is no `Service` or `Writer`
wrapper hiding those edges. `WaitFor` remains because startup ordering is not
connection configuration.

The remaining environment settings have specific jobs: `TRAIN_ID` and
`VITE_SITE` select worker/site identity; the four application keys enforce distinct
permissions; `AZURE_TENANT_ID` selects the authorized local credential tenant.
The concrete service projects replaced the old `ROLE` switch.
Python analytics still needs `SCENARIO_URL`; the Vite proxies need `API_URL`.
Neither uses the C# discovery handler. The browser never receives an operator
key; its same-origin development proxy supplies that header.

Checking the pinned package mattered. `AddCSharpApp` in 13.6.5 still launched
per-instance `dotnet run`, so merely renaming the API did not fix the shared
MSBuild-cache race. The matching released `Aspire.Hosting.Dotnet`
`13.6.5-preview.1.26559.7` supplies `AddDotnetProject` and its coordinated build.
SDK pinning remains 13.6.5.

Observed one `__dotnet-project-build` traversal restoring/building the service
host and worker projects once, with zero warnings/errors. Workers then launched
their built executable directly, with no `dotnet run` or per-instance build.
All twelve workers ran and registered without individual worker restarts.
A real Airport assistant request used station resolution and the arrival tool
through discovery. Focused endpoint-resolution tests exercise both C# clients.

That startup also reproduced a separate PostgreSQL open timeout in scenario
initialization; restarting scenario allowed startup to finish. This is not
proof that the database failure is fixed. It is the next infrastructure-health
and recovery gate. Analytics correctly reported its existing 8 MiB state-size
limit on the long-running checkpoint rather than returning invented metrics.

## 5. Clients own connections; readiness checks dependencies

The relevant service projects use `Aspire.Npgsql`, `Aspire.RabbitMQ.Client` and
`Aspire.StackExchange.Redis` 13.6.5. Their supported DI registrations supply
the data source, broker connection and Redis multiplexer, plus dependency health
checks and operation telemetry. Direct package references and manual client
construction were removed.

`WorldStore` and the outbox borrow connections from the managed Npgsql data
source. Their row locks, SQL, transactions, command deduplication and atomic
checkpoint/event/outbox writes are unchanged. The publisher and projection own
their RabbitMQ channels, not the shared connection. The projection no longer
disposes the shared Redis multiplexer. Redis still commits before memory accepts
a newer projection.

RabbitMQ/Redis autoactivation is disabled so an unavailable dependency cannot
kill the host before its existing, logged recovery loop runs. Failed singleton
creation is retried through DI, not cached in a faulted `Lazy`. Scenario
initialization also stays alive and explicitly unhealthy while PostgreSQL is
unavailable; it does not skip initialization or advance the simulation.

`/health` now runs the registered dependency checks as well as runtime readiness.
Information also checks its ten-second projection freshness limit. A healthy
process is no longer presented as proof that its database is reachable.

Observed after the migration:

- All twelve workers and both websites started from one coordinated build,
  without subset restarts.
- Stopping this AppHost's PostgreSQL made incidents return health HTTP 503 with
  `PostgreSql: unhealthy`; scenario also reported its clock/store failure.
  Starting that database restored both checks without restarting a service.
- Stopping Redis made information unhealthy and public boards visibly stale.
  Restarting it restored fresh projections without replacing the process.
- Stopping RabbitMQ did the same; after recovery, the durable outbox delivered
  a newer world and the boards became fresh again.

The earlier Npgsql open timeout was a real connection failure, not a bad action
target. Its exact host-network cause was not established; neither a larger
timeout nor an automatic successful command response was substituted. The app
now uses managed connections, reports the failure truthfully, and has verified
recovery instead of advertising healthy dependency-free endpoints.
The paused-clock recovery was also exercised: PostgreSQL stopped, scenario
reported unhealthy, PostgreSQL restarted, and `/health` recovered with no warning
while the world was still paused. A successful store read clears the previous
error before the paused/sub-second early returns.

## 6. One fleet definition

[`Fleet.Trains`](src/MetroOps.Contracts/Fleet.cs) defines the twelve identities,
assignments, initial positions/directions/capacities, depots and reserves once.
Kernel initialization and the AppHost's worker loop both consume it. Initial
dwell and reserve readiness still use the same seeded entity streams.

The root file-based AppHost references Contracts with `#:project`.
`TreatProjectReferencesAsResources=false` makes this a normal library reference,
not an executable resource. Executable resources remain the visible
`AddDotnetProject` declarations.

Domain tests preserve the original fleet identities and check initialization
against the shared definition, including seeded readiness and occupied
platforms. The rebuilt root AppHost started all twelve worker resources healthy
from that definition.

## 7. AI has its own process boundary

The existing `ai-operations` resource now launches
[`MetroOps.AiHost`](src/MetroOps.AiHost/Program.cs), not the role-switched service
host. Model configuration, SDK/auth registration, bounded orchestration, tool
registries, proposals and audio validation moved with it. The deterministic
projects have no OpenAI SDK, Azure credential or AI-host project dependency.

Both frontends keep their public routes and contracts. Their gateways forward
to fixed AI routes through Aspire discovery, preserving HTTP failures,
multipart audio, speech bytes/headers and cancellation. The AI host still owns
the inference/tool timeouts and iteration bounds; the forwarding client does
not truncate a valid multi-round request with the domain client's timeout.

Passenger-api receives no model references or operator/agent key. It can forward
only capabilities, assistant, transcription and speech to `/passenger/*`.
That server-selected registry has no comparison, proposal or action tools.
Every `/operator/*` route, including proposal lookup, independently requires the
operator key. A prompt, JSON role field or user-supplied header on a passenger
request cannot select the operator registry. The AI host has only the bounded
agent dispatch credential; human approval still goes through operations-api
and dispatch, not a model tool.

Focused HTTP-host tests cover those route guards, hostile passenger role/tool
payloads, proposal isolation, error propagation, multipart/audio forwarding and
authenticated operator forwarding. All 38 runtime tests pass after extraction;
the original SDK/tool tests now exercise the moved implementation.

Observed through the running gateways:

- Airport text used `resolve_station` and `get_arrivals` and returned the typed
  board.
- Speech returned 81,866 MP3 bytes. Transcription drove station resolution and
  the real journey planner; spoken playback returned 303,146 bytes.
- Operator `propose_action` produced a headway proposal; the separate approval
  route accepted it. The proposed headway equaled the current headway, so this
  verified the path without changing service settings.
- Direct operator access without its key returned 401; passenger operator-path
  forwarding returned 404. Passenger-api's environment contained no model or
  operational credentials.

The request trace has 88 spans without errors, including the
gateway-to-AI HTTP boundary, deterministic tool requests and native SDK
`chat gpt41` spans. Attributes contain model/token/finish metadata, not message
content. Synthetic audio and conversation stayed in memory.

One coordinated traversal built all three executable .NET projects before
launch. The total remains 25 application processes and three infrastructure
containers; no extra chatbot resource or agent framework was introduced.

## 8. Read models should fit the reader

The long-lived checkpoint reached 10,958,557 bytes, of which 9,838,825 bytes
were cohort journeys. Python analytics correctly rejected that full-state
response at its 8 MiB guard. Raising the guard or deleting retired passengers
would hide the design problem.

Analytics now reads `/analytics-state`: the same checkpoint identity, counters,
trains and bounded history, with current inventory grouped by state, station,
accessibility and whether it is ready yet. Each group retains its oldest
readiness time. Completed/abandoned totals still come only from the metrics;
future returns remain inventory but not queues, and unknown states remain
visible as accounting errors. The authoritative checkpoint is unchanged.

On that preserved world, the summary was 112,234 bytes and computed analytics
returned HTTP 200: 20,608 generated, 20,608 accounted, delta zero. Dependency
failure still returns stale/unavailable 503, now from `/health` too. A regression
with more than 8 MiB of retired journeys checks that the summary stays below
64 KiB without modifying the source cohorts.

## 9. Frequent disruptions are not constant severe failures

Incident profile 2 uses separate versioned, seeded per-entity opportunities
each simulated second. Most train incidents are 30–119-second extended dwells;
the less common door faults last 2–8 minutes. Station incidents mix short
circulation restrictions with longer elevator repairs. Existing matching
incidents suppress duplicates; repairs clear only their own effects.

Across eight seeds and two hours per seed, normal produced 126 issues in
16 simulated hours: one every 457 seconds (7.6 minutes), including 88 short
issues. Rough produced 409 issues in the same 16-hour denominator: one every
141 seconds (2.3 minutes), including 302 short issues. The regression checks
the measured cadence, repair/duplicate behavior, continuing second-hour train
movements, unique active block grants and capacity limits. These are aggregate
rates, not a promise of an incident every fixed interval. Wall-clock intervals
depend on achieved simulation throughput.

## 10. Routing promises must match movement permissions

Ride feasibility and arrival estimates now consider restrictions at every
traversed stop, not only endpoints. Clearing a restriction restores the route.
Short-turn destinations govern boards, arrival estimates and boarding;
passengers already aboard beyond the turn transfer for replanning rather than
being silently completed or carried back. A reverse-platform reservation
blocks the turn before any direction, cohort or occupancy change.

Headways are departure regulation floors, not timetable guarantees. Scheduled
wait estimates also account for the available fleet's round-trip capacity,
dwell and turnback time. The existing Blue 480-second floor remains a valid
operating control without being advertised as a physically guaranteed
eight-minute service.

## 11. One identity across both sites

The shared [brand rules](BRAND.md) use the official Aspire palette and locally
served Poppins. The original A/track-junction asset supplies the favicon,
headers and assistant marks. Functional route colors remain separate from
brand purple; the passenger site has no decorative live dots.

Dispatch uses one responsive gutter across its identity, metrics, desk and map.
Fleet cards share row tracks so wrapped reserve identifiers do not misalign
state and station fields. The assistant heading sits on the same baseline as
the adjacent queue/recovery headings, with its mark at the trailing edge.
These changes arrived through Vite HMR without restarting the live services.

## 12. Separate executables, explicit shared mechanisms

The catch-all ServiceHost project and `ROLE` environment switch are gone.
Scenario, Control, Dispatch, Incidents, Planner, Information, OperationsApi and
PassengerApi each have a small executable with concrete route registration.
Their references show which capabilities they actually need.

[`ServiceDefaults`](src/MetroOps.ServiceDefaults/Defaults.cs) shares telemetry,
discovery, HTTP errors and readiness. Only the four authoritative writers
reference [`Simulation.Persistence`](src/MetroOps.Simulation.Persistence/WorldStore.cs).
The checkpoint, command result/history and outbox still commit atomically.
There was no checkpoint reset, migration or database proliferation.
Scenario owns publication; Information owns subscription/cache projection.
PassengerApi contains no operator routes, while shared public handlers and
fixed-path forwarding live in `PublicApi`.

Python apps, Vite sites, .NET project paths and generated secret parameters are
declared directly in the AppHost. HTTP endpoints, health checks, native
references, waits and key assignments remain visible at each resource.
The existing fleet loop registers all twelve workers. The optional Foundry
references go only to the AI host.

The November 18 startup retained the previous scenario,
generation 10. All twelve workers, both websites and the actual Foundry
deployments became healthy. Independent checks confirmed eight separate
binaries, HTTP authority separation and analytics conservation
22,975 = 22,975. Contract/dependency checks cover executable ownership and the
absence of database, broker, cache, model and persistence dependencies from
Planner and both APIs. A healthy dependency graph does not establish an
achieved 60x simulation rate; sustained measurements are recorded below.

## 13. Measure local pressure, not just generated demand

Demand profile 2 uses America/New_York city schedules, with UTC instants on the
wire and viewer-local presentation. It retains station purposes, flights,
events and linked returns; removing a shared rate cap avoids flattening the
station/time differences.

Three seeds (11, 42, 123), each with two-hour windows, drove the actual shared
kernel and unchanged fleet/capacities:

| City window | Mean train utilization | Minute samples with a full train | Denied boardings | Largest station queue |
| --- | --- | --- | --- | --- |
| Weekday 07:00–09:00 | 37.6–39.6% | 95–98 / 120 | 1,940–2,172 | 181–196 |
| Weekday 17:00–19:00 | 42.3–43.5% | 62–81 / 120 | 674–794 | 199–217 |
| Weekday 12:00–14:00 | 19.5–21.1% | 0 / 120 | 0 | 93–120 |
| Weekend 12:00–14:00 | 31.0–33.4% | 55–73 / 120 | 482–569 | 159–172 |

Morning pressure concentrated at residential approaches; evening pressure
shifted to Central Exchange, Union and university/civic stops. Weekend leisure
was stronger while office demand was quieter.

A separate six-hour seed-42 run kept one world from 06:30 to 12:30 city time.
Waiting peaked at 1,321 at 08:00, fell to 296 at 11:00, and ended at 362.
Denied boardings stopped increasing after 10:30; completions rose from 7,128
to 9,546 during the final two hours while movement continued. Conservation was
checked every minute. This is illustrative pressure and recovery, not calibrated
real transit: appreciable abandonment remains, and simulated minutes are not
wall-clock minutes.

## 14. Keep retired passengers out of hot transitions

The mature checkpoint contained 14,776 cohort records; 11,499 were already
completed or abandoned. Repeatedly reading and serializing that history under
the writer lock dominated the actual kernel work.

Control and clock transitions now load active inventory while retaining retired
records unchanged in the same checkpoint. The save merges them back under the
same lock; `UPDATE ... RETURNING` supplies the exact saved state to the outbox
in the same transaction. Full reads and scenario replacement keep their existing
contracts. Compact train reads decompress once, unchanged transitions do not
publish, and same-worker heartbeat writes are coalesced within the existing
90-second stale threshold. PostgreSQL uses lz4 for subsequent checkpoint/outbox
writes; existing records are not deleted or rewritten during initialization.

A 45-second isolated run of the retained snapshot with all twelve polling loops
advanced 675 simulated seconds: **14.90x**, with conservation intact. That probe
does not include fresh Python demand or a broker consumer, so it is not a claim
of live 60x throughput. Retention tests compare bounded and full-kernel
transitions, including the mature snapshot when `METROOPS_TEST_CHECKPOINT` is
provided, and verify exact checkpoint/outbox equality and rollback. Database
tests require `METROOPS_TEST_POSTGRES` naming a disposable `metroops_test_*`
database; otherwise they explicitly skip.

After the preserved-volume restart, the full running graph advanced 320
simulated seconds in 20.09 wall seconds: **15.93x**, with all ten sampled
intervals advancing. All twelve workers and Foundry remained healthy; the
live inventory command reported 29,286 generated = 29,286 accounted. This
clears the 8x regression gate, not the configured 60x target. The separate
passenger conference origin was running during that measurement.
