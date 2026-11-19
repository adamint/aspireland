# Run a single-host demo

Use Aspire to run the private graph and Caddy to serve the separate attendee
origin over HTTPS. This is a supervised demo, not an unattended production
service. It doesn't require Azure, Kubernetes, a registry, or a Dev Tunnel.

```text
attendee → HTTPS/Caddy → 127.0.0.1:6173 (attendee bundle + route allowlist)
                       → passenger-api on loopback → private Aspire graph
```

## Set up the host

Use a dedicated host or VM with the [local prerequisites](../README.md#run-it-locally)
and [Caddy 2](https://caddyserver.com/docs/install). Use a dedicated OS account
for Aspire that can run Docker. Keep Docker and the host account private.
Check out a specific Git revision and run all Aspire commands from that same
clone. Leave Foundry unconfigured for the default demo; there's no model
dependency or cost unless you opt in to Signal below.

Set the ingress rules **before** starting Aspire:

- Allow public **TCP 443 to Caddy only**. Configure a DNS A record for the
  host name you use below. If you need TCP 80 for Caddy's ACME HTTP challenge,
  allow it to Caddy only. Keep management access private.
- Deny every other public inbound port, including dynamically allocated
  ports. Enforce this outside the Docker host as well (for example with an
  upstream firewall); Docker-published ports can bypass some host firewall
  rules. Don't add a public IPv6 address/AAAA record unless IPv6 ingress is
  covered by the same rules.
- Never forward to the Aspire Dashboard, either Vite dev server, either API
  directly, a worker, or Postgres/RabbitMQ/Redis. Use the private host session
  or a private SSH port forward for operator access.

## Start the demo

Pick a unique Docker volume name for this instance. Set a stable Postgres
password before first start and keep it outside the checkout. The following
initializes a new demo account's configuration; don't regenerate the file on
restart or when pointing at an existing volume.

```sh
cd -P .
dotnet tool restore
dotnet aspire --version
export MetroOps__PostgresVolume=metroops-demo-postgres
(umask 077; mkdir -p "$HOME/.config/metroops"
 set -C; openssl rand -hex 32 > "$HOME/.config/metroops/postgres-password")

npm ci --prefix web
npm run build:conference --prefix web
env MetroOps__ProvisionFoundry=false \
  "Parameters__postgres-password=$(cat "$HOME/.config/metroops/postgres-password")" \
  dotnet aspire start --launch-profile demo --non-interactive
dotnet aspire wait passenger-api --timeout 180 --non-interactive
dotnet aspire wait operations-web --timeout 180 --non-interactive
dotnet aspire describe --non-interactive
```

Use the `demo` profile for only one instance on the host. It keeps the dashboard
at `https://localhost:17380`, Rider at `http://127.0.0.1:17381`, and Dispatch at
`http://127.0.0.1:17382`. Use `dotnet aspire start --isolated --non-interactive`
without the `demo` profile in other worktrees.

Start the attendee origin in a private terminal. The `demo` profile keeps the
Passenger API upstream on `127.0.0.1:17383`; the attendee origin still listens
on `127.0.0.1:6173`. Keep this process running for the talk.

```sh
export PASSENGER_API_PORT=17383
npm run start:conference --prefix web -- \
  --port 6173 --upstream "http://127.0.0.1:$PASSENGER_API_PORT"
```

Open `http://127.0.0.1:6173` on the host first. Once that's working, run Caddy
on the same host. Set `DEMO_HOST` to the actual DNS name of this instance.
Caddy needs permission to bind port 443 and a writable, persistent data
directory for TLS certificates. If Caddy is managed by your OS, use the
config and environment below in that service, not a second Caddy process.

```sh
export METROOPS_DEMO_HOST="$DEMO_HOST"
caddy validate --config deploy/Caddyfile
caddy run --config deploy/Caddyfile
```

The attendee origin serves only the production passenger bundle, public
status and map data, station boards, and ordinary journey lookups. Journey
requests use POST, but don't change the simulation. The attendee origin
handles the route and field allowlists; Caddy forwards to no other service.
Signal is off by default and attendee voice is always off. No redirect from
HTTP is configured; share the HTTPS URL. If you add a tunnel, it must
point to this same attendee origin only, never to the underlying services.

If you're using Dev Tunnels, set `ATTENDEE_URL` to the attendee HTTPS URL
printed by `devtunnel host`. Run this from the repository root to write a
QR code for your slide:

```sh
uv run --script scripts/attendee-qr.py "$ATTENDEE_URL" --output attendee-qr.png
```

### Enable Signal text chat (optional)

Use the sealed build from this revision; don't share the private Passenger
site to turn on chat. If [Foundry](foundry.md) is already configured for chat,
check that the private `passenger-api` returns `text: true` at
`/api/capabilities`. Start just the attendee origin with the opt-in:

```sh
CONFERENCE_SIGNAL_CHAT=true npm run start:conference --prefix web -- \
  --port 6173 --upstream "http://127.0.0.1:$PASSENGER_API_PORT"
```

The origin checks the private text capability at startup. If it can't confirm
it, the planner and boards still work and chat stays off until you restart
the origin with a ready upstream. With chat enabled, expect `text: true` and
both `transcription` and `speech` to remain `false`. Neither recordings nor
Dispatch become public.

Signal sends questions to the configured model provider. Don't enter personal
information. The attendee origin doesn't cache chat responses, log prompts, or
track attendees. It allows one chat request at a time, two starts per rolling
minute shared by all attendees, and 120 starts per server run. A turn can make
several model requests; these limits don't replace the provider's quota.
Check one actual answer before sharing the URL. To turn attendee chat back
off, restart just the attendee origin without `CONFERENCE_SIGNAL_CHAT=true`.
No change to the tunnel, Aspire graph, or Foundry is needed to disable it.

## Check from outside

Open `https://$DEMO_HOST` in a signed-out browser off the host. Plan an
accessible Airport → Medical Center journey and open `/stations/airport`.
Check that the map loads and the board shows an updating simulated time.

```sh
ORIGIN="https://$DEMO_HOST"
curl --fail "$ORIGIN/api/stations/airport/arrivals"
curl --fail "$ORIGIN/api/capabilities"
curl --silent --output /dev/null --write-out '%{http_code}\n' \
  -X POST "$ORIGIN/api/actions"
```

Expect an Airport board, `text: false` unless you enabled Signal, both voice
flags `false`, and `404` for the operator action. Confirm an external client can't connect to the
raw attendee port or any Aspire resource port before sharing the URL. This
check doesn't reset or change the world.

## Keep the world

Postgres holds the saved world, command receipts, event history and one
pending passenger snapshot.
The default volume is `aspireland-metroops-postgres`; the runbook sets a
different name. Keep the **same volume name and password** across starts.
Use **New scenario** to start another simulation without deleting the volume.
RabbitMQ and Redis are transient; the app can reconstruct passenger data from
the saved world. Backups and credential files aren't repository artifacts.

The app keeps the newest **10,000 SQL events across all generations** and the
newest **250 events in the checkpoint**. Completed and abandoned passenger
totals stay in the checkpoint, but individual completed and abandoned cohorts
do not. Movement grants, internal receipts, resolved incidents and operator
command receipts aren't capped by this policy.

Before upgrading, keep the current Git revision and a logical backup. Find the
one running container for the configured volume:

```sh
docker ps --filter "volume=$MetroOps__PostgresVolume" --format '{{.ID}} {{.Names}}'
```

Set `PG_CONTAINER` to that container ID. Back up `metro` and check that the
archive contains a readable table of contents:

```sh
umask 077
mkdir -p backups
BACKUP="backups/metro-$(date -u +%Y%m%dT%H%M%SZ).dump"
docker exec "$PG_CONTAINER" sh -c \
  'PGPASSWORD="$POSTGRES_PASSWORD" exec pg_dump -w -U postgres -d metro -Fc' > "$BACKUP"
docker exec -i "$PG_CONTAINER" pg_restore --list < "$BACKUP" >/dev/null
```

Copy the archive to private off-host storage. A readable archive isn't a
restore drill: rehearse restoring into a fresh database and volume before you
rely on it. Stop writers and never restore over a running world. Restore the
pre-upgrade archive to a clean `metro` database using `pg_restore --exit-on-error`
with a compatible PostgreSQL version, then start the matching application
revision against that volume. Don't delete the original volume to make room.

## Stop the demo

Stop Caddy (or close public ingress), then stop the attendee process and run
`dotnet aspire stop --non-interactive` from the demo clone. For foreground processes,
use Ctrl+C. Don't delete the database volume.

## Upgrade an existing world

### Upgrade to Aspire 17

Schedule a maintenance window; don't restart a live talk. Restore the pinned
CLI with `dotnet tool restore` in the release clone and check that
`dotnet aspire --version` reports 17.0.0.

Stop the existing writers and back up the retained database as above. Stop
only that AppHost, using its explicit `--apphost` path. Never run two graphs
against the same database. Keep both the existing volume name and its actual
PostgreSQL password: a new isolated checkout generates different user secrets.
Supply the retained password privately through `Parameters__postgres-password`
and the volume through `MetroOps__PostgresVolume`; don't put either in Git or logs.
Preserve the application credentials through `Parameters__service-key`,
`Parameters__worker-key`, `Parameters__operator-key` and `Parameters__agent-key`.
Their factories read these configured values before generating fresh defaults.
Keep `MetroOps__ProvisionFoundry=false` unless a separate cloud change is approved.
Aspire 17 currently selects PostgreSQL 18.6 rather than the earlier 18.3 image;
both use major version 18, but keep the backup for rollback.

Start the release AppHost with the pinned CLI and wait for its resources.
Check the retained scenario identity/generation, passenger conservation and
fresh station boards before reopening traffic. Don't use **Set simulation
time**: that command deliberately resets the world. The Aspire-only upgrade
doesn't need the history-format migration described below.

Rediscover the `passenger-api` HTTP port. If it changed, restart only the attendee
origin with the new upstream, the same sealed build and the same listener port
(for example, 6173). Leave the tunnel/public URL and permissions unchanged.
Expect a short maintenance gap; an unchanged public URL is not zero downtime.

### Upgrade the history format

When moving an existing database to the bounded history and single-snapshot
outbox, don't restart just one writer against a mix of old and new processes.

1. Close public ingress. Stop `information` and all four PostgreSQL writers:
   `scenario`, `control`, `dispatch` and `incidents`. Keep all five stopped
   while you back up Postgres. Archive older events separately if you need them.
2. Deploy the new revision. Rebuild and start **`scenario` first**, leaving
   the other four stopped. Wait for `dotnet aspire wait scenario --non-interactive`
   to report healthy. Scenario migrates the schema, prunes history and
   rebuilds the pending passenger snapshot from the checkpoint without
   resetting the world. If restarting the whole AppHost, use
   `.WithExplicitStart()` on `control`, `dispatch`, `incidents` and
   `information` in the maintenance checkout so they don't start with the
   graph. Start them explicitly in the next step.
3. Rebuild and start `control`, `dispatch` and `incidents`, then `information`.
   Check that the passenger projection version catches up to Dispatch and that
   generated passengers equal completed plus abandoned plus active inventory.
   Look up the current `passenger-api` HTTP port before restarting the attendee
   origin and reopening ingress.

Normal PostgreSQL `VACUUM` makes deleted space reusable; it generally doesn't
shrink files on disk. Check table, TOAST and WAL size after the system settles.
`VACUUM FULL` rewrites a table and needs free disk space, an exclusive lock
and a maintenance window. Never delete WAL files manually.

### Roll back the history migration

The safest rollback is to restore the pre-migration backup into a new volume
and run the matching old revision. Only that backup can recover pruned rows,
and restoring it also rolls back work committed since the backup.

For a code-only rollback of this migration, stop the same five resources
and take a backup of the current database. Remove the new slot column
(`ALTER TABLE metro_outbox DROP COLUMN slot`) before restarting the old
revision. It can read the preserved checkpoint but has the original publisher
failure mode. A code-only rollback doesn't recover pruned history.
