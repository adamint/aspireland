import { useEffect, useState, type FormEvent } from "react";
import { api } from "../api";
import { AssistantPanel } from "../components/AssistantPanel";
import { JourneyCards } from "../components/JourneyCards";
import { NetworkMap } from "../components/NetworkMap";
import { StationBoardView } from "../components/StationBoardView";
import { TransitBrand } from "../components/TransitBrand";
import { formatPassengerDateTime, formatDateTimeLocal, formatDuration, lineFor, parseSimulatedLocalInput, stationName, titleCase, viewerTimeZone } from "../format";
import { useCapabilities, useStationBoard, useWorld } from "../hooks";
import { activeStationIncidents, lineIncidents, publicAlert, publicOnboardCrowding, publicTrainStatus } from "../passengerData";
import interLicense from "../inter-OFL.txt?url";
import type { JourneyResult, NetworkDefinition, TrainState, WorldSnapshot, WorldState } from "../types";

function usePath() {
  const [path, setPath] = useState(window.location.pathname);
  useEffect(() => {
    const handle = () => setPath(window.location.pathname);
    window.addEventListener("popstate", handle);
    return () => window.removeEventListener("popstate", handle);
  }, []);
  const navigate = (next: string) => {
    const url = new URL(next, window.location.origin);
    window.history.pushState({}, "", `${url.pathname}${url.hash}`);
    setPath(url.pathname);
    window.requestAnimationFrame(() => {
      if (url.hash) document.querySelector(url.hash)?.scrollIntoView({ behavior: "smooth" });
      else window.scrollTo({ top: 0, behavior: "smooth" });
    });
  };
  return { path, navigate };
}

function PassengerHeader({
  onNavigate,
  simulatedTime,
  stale,
  showClock = true,
}: {
  onNavigate: (path: string) => void;
  simulatedTime?: string;
  stale?: boolean;
  showClock?: boolean;
}) {
  return (
    <>
      {showClock && <div className="public-sim-banner">
        <span>{stale ? "Updates delayed" : "Travel date and time"}</span>
        <strong>{simulatedTime ? formatPassengerDateTime(simulatedTime) : "Updating…"}</strong>
      </div>}
      <header className="passenger-header">
        <button className="passenger-brand" onClick={() => onNavigate("/")} aria-label="Aspireland Transit home">
          <TransitBrand />
        </button>
        <nav aria-label="Passenger navigation">
          <button onClick={() => onNavigate("/#status")}>Service status</button>
          <button onClick={() => onNavigate("/#plan")}>Plan a trip</button>
          <button onClick={() => onNavigate("/#map")}>System map</button>
          <button onClick={() => onNavigate("/#assistant")}>Ask Aspireland</button>
        </nav>
        <a className="header-access-link" href="#accessibility">Accessibility</a>
      </header>
    </>
  );
}

function ServiceAlerts({ snapshot, stale = false }: { snapshot?: WorldSnapshot; stale?: boolean }) {
  const incidents = snapshot?.world.incidents.filter((incident) => incident.state === "active") ?? [];
  const currentAlerts = Boolean(snapshot) && !stale && incidents.length > 0;
  const warning = stale || currentAlerts;
  return (
    <section className={`public-service-alerts${warning ? " public-service-alerts--warning" : ""}`}
      id="service-alerts" aria-label="Service alerts" tabIndex={-1}>
      <div className="public-service-alerts-inner">
        <div className="public-service-alerts-label">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden="true" focusable="false">
            {warning ? <path d="m12 3 10 18H2L12 3Zm0 6v5m0 3v1" />
              : <><circle cx="12" cy="12" r="9" /><path d="M12 11v6m0-10v1" /></>}
          </svg>
          <h2>Service alerts</h2>
        </div>
        {currentAlerts && snapshot ? (
          <details>
            <summary>
              <strong>{incidents.length} service {incidents.length === 1 ? "alert" : "alerts"}</strong>
              <span className="public-alerts-action">View details</span>
              <svg className="public-alerts-toggle" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true" focusable="false"><path d="m4 6 4 4 4-4" /></svg>
            </summary>
            <ul>
              {incidents.map((incident) => {
                const alert = publicAlert(incident, snapshot.network, snapshot.world.trains);
                return <li key={incident.id}><strong>{alert.location} — {alert.title}</strong><span>{alert.description}</span></li>;
              })}
            </ul>
          </details>
        ) : (
          <p>
            {!snapshot ? "Checking service alerts…" : stale ? <>
              <strong>Live service alerts unavailable</strong>
              <span>Service updates are delayed. Last updated {formatPassengerDateTime(snapshot.world.updatedAt)}. Check again before travelling.</span>
            </> : "No service alerts reported"}
          </p>
        )}
      </div>
    </section>
  );
}

function ServiceStatus({ world, network, stale }: { world: WorldState; network: NetworkDefinition; stale: boolean }) {
  return (
    <section className="public-section status-section" id="status">
      <div className="section-heading">
        <div><span className="eyebrow">{stale ? "Last report" : "Right now"}</span><h2>Service status</h2></div>
        <span className="updated-label">Last updated {formatPassengerDateTime(world.updatedAt)}</span>
      </div>
      <div className="line-status-grid">
        {network.lines.map((line) => {
          const incidents = lineIncidents(line, world.trains, world.incidents);
          const affected = new Set(
            incidents
              .flatMap((incident) => line.stations.filter((station) => incident.target.includes(station)))
              .map((station) => stationName(network, station)),
          );
          const operating = world.trains.filter((train) => train.line === line.id && !["withdrawn", "reserve"].includes(train.state)).length;
          return (
            <article key={line.id} id={`line-${line.id.toLowerCase()}`} tabIndex={-1}
              className={`line-status-card ${incidents.length ? "line-status-card--alert" : ""}`}>
              <header>
                <span className={`public-line-icon public-line-icon--${line.id.toLowerCase()}`}>{line.id}</span>
                <div><strong>{line.name}</strong><small>{stale ? `Last report: ${operating} in service` : `${operating} trains in service`}</small></div>
                <span className={stale ? "status-unknown" : incidents.length ? "status-alert" : "status-good"}>
                  {stale ? "Status unavailable" : incidents.length ? "Service alert" : "No service alerts"}
                </span>
              </header>
              {incidents.length ? (
                <div className="line-alerts">
                  {stale ? <><p>These are the last reported alerts.</p>{incidents.slice(0, 3).map((incident) => {
                    const alert = publicAlert(incident, network, world.trains);
                    return <p key={incident.id}><strong>{alert.location} — {alert.title}:</strong> {alert.description}</p>;
                  })}</> : <p><a href="#service-alerts">View service alerts</a></p>}
                  {affected.size > 0 && <small>Affected: {[...affected].join(", ")}</small>}
                </div>
              ) : (
                <p>{stale ? "Live service status is unavailable. Check again before travelling." : "No service alerts reported on this line."}</p>
              )}
              <details className="public-line-stops">
                <summary>Stops</summary>
                <ol>{line.stations.map((id) => <li key={id}><a href={`/stations/${id}`}>{stationName(network, id)}</a></li>)}</ol>
              </details>
            </article>
          );
        })}
      </div>
    </section>
  );
}

function JourneyPlanner({
  network,
  world,
}: {
  network: NetworkDefinition;
  world: WorldState;
}) {
  const [origin, setOrigin] = useState("university");
  const [destination, setDestination] = useState("airport");
  const [accessible, setAccessible] = useState(false);
  const [when, setWhen] = useState<"now" | "later">("now");
  const [departureTime, setDepartureTime] = useState("");
  const [result, setResult] = useState<JourneyResult | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const timeZone = viewerTimeZone();

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setLoading(true);
    setError(null);
    try {
      setResult(
        await api.journey({
          origin,
          destination,
          accessible,
          departureTime: when === "later" ? parseSimulatedLocalInput(departureTime, timeZone) : null,
        }),
      );
    } catch (journeyError) {
      setError(journeyError instanceof Error ? journeyError.message : "Journey planning is unavailable.");
    } finally {
      setLoading(false);
    }
  };

  const swap = () => {
    setOrigin(destination);
    setDestination(origin);
  };

  return (
    <section className="public-section journey-planner-section" id="plan">
      <div className="journey-planner-intro">
        <h2>Where are you going?</h2>
        <p>Find a route, check travel times and avoid stairs if you need to.</p>
        <p>Times shown in your local time.</p>
        <div className="planner-time-callout">
          <span>Leave now at</span>
          <strong>{formatPassengerDateTime(world.simTime)}</strong>
        </div>
      </div>
      <form className="journey-form" onSubmit={(event) => void submit(event)}>
        <div className="journey-location-fields">
          <label>
            <span>From</span>
            <select value={origin} onChange={(event) => setOrigin(event.target.value)}>
              {network.stations.map((station) => <option key={station.id} value={station.id}>{station.name}</option>)}
            </select>
          </label>
          <button type="button" className="swap-button" onClick={swap} aria-label="Swap origin and destination">⇅</button>
          <label>
            <span>To</span>
            <select value={destination} onChange={(event) => setDestination(event.target.value)}>
              {network.stations.map((station) => <option key={station.id} value={station.id}>{station.name}</option>)}
            </select>
          </label>
        </div>
        <div className="journey-options">
          <div className="segmented segmented--light">
            <button type="button" className={when === "now" ? "active" : ""} onClick={() => setWhen("now")}>Leave now</button>
            <button type="button" className={when === "later" ? "active" : ""} onClick={() => {
              if (!departureTime) setDepartureTime(formatDateTimeLocal(world.simTime, timeZone));
              setWhen("later");
            }}>Leave later</button>
          </div>
          {when === "later" && (
            <label>
              <span>Departure date and time</span>
              <input type="datetime-local" value={departureTime} onChange={(event) => setDepartureTime(event.target.value)} required aria-invalid={Boolean(error)} />
            </label>
          )}
          <label className="accessible-check">
            <input type="checkbox" checked={accessible} onChange={(event) => setAccessible(event.target.checked)} />
            <span>Step-free trip</span>
          </label>
          <button className="public-primary-button" disabled={loading || origin === destination}>
            {loading ? "Finding a route…" : "Find a route"}
          </button>
        </div>
        {origin === destination && <div className="notice notice--warning">Choose two different stations.</div>}
        {error && <div className="notice notice--error" role="alert">{error}</div>}
      </form>
      {result && <JourneyCards passenger result={result} network={network} currentGeneration={world.generation} currentScenarioId={world.scenarioId} />}
    </section>
  );
}

function AccessibilityPanel({ world, network, stale }: { world: WorldState; network: NetworkDefinition; stale: boolean }) {
  const restrictions = world.incidents.filter((incident) =>
    incident.state === "active" &&
    ["elevator-outage", "circulation", "platform-closure", "station-closure"].includes(incident.kind),
  );
  return (
    <section className="accessibility-panel" id="accessibility">
      <div>
        <span className="accessibility-symbol" aria-hidden="true">♿</span>
        <span className="eyebrow">Step-free travel</span>
        <h2>Elevator and station access</h2>
        <p>Choose “Step-free trip” to avoid stairs.</p>
        <small>Last updated {formatPassengerDateTime(world.updatedAt)}</small>
      </div>
      <div className="accessibility-updates">
        {stale && <div className="notice notice--warning"><strong>Access status unavailable</strong><p>Updates are delayed. Check again before travelling.</p></div>}
        {!stale && restrictions.length === 0 && <div className="access-good"><strong>No access alerts reported</strong></div>}
        {restrictions.map((incident) => {
          const alert = publicAlert(incident, network, world.trains);
          return <article key={incident.id}>
            <strong>{alert.location}</strong>
            <span>{stale ? "Last report: " : ""}{alert.title}</span>
            <p>{alert.description}</p>
          </article>;
        })}
      </div>
    </section>
  );
}

function TrainFacts({ train, network, stale }: { train: TrainState; network: NetworkDefinition; stale: boolean }) {
  const currentStation = network.stations.find((station) => station.id === train.stationId);
  const nextStation = network.stations.find((station) => station.id === train.nextStationId);
  const inService = !["reserve", "withdrawn"].includes(train.state);
  const locationLabel = stale || train.state === "uncertain" ? "Last reported station" : train.state === "travelling" ? "Departed" : "Current station";
  const crowding = publicOnboardCrowding(train.load, train.capacity, stale);
  return (
    <dl className="public-train-facts">
      <div><dt>{stale ? "Last reported status" : "Status"}</dt><dd>{publicTrainStatus(train.state)}</dd></div>
      <div><dt>{locationLabel}</dt><dd>{currentStation?.name ?? "Not reported"}</dd></div>
      {inService && <div><dt>{stale ? "Last reported next station" : "Next station"}</dt><dd>{nextStation?.name ?? "Not reported"}</dd></div>}
      {inService && <div>
        <dt>Onboard crowding</dt>
        <dd className="public-crowding"><strong>{crowding.label}</strong><span>{crowding.detail}</span></dd>
      </div>}
    </dl>
  );
}

function HomePage({
  world,
  network,
  capabilities,
  capabilityError,
  onNavigate,
  stale,
}: {
  world: WorldState;
  network: NetworkDefinition;
  capabilities: ReturnType<typeof useCapabilities>["data"];
  capabilityError: string | null;
  onNavigate: (path: string) => void;
  stale: boolean;
}) {
  const [selectedStationId, setSelectedStationId] = useState<string | null>("central-exchange");
  const [selectedTrainId, setSelectedTrainId] = useState<string | null>(null);
  const board = useStationBoard(selectedStationId);
  const selectedStation = network.stations.find((station) => station.id === selectedStationId);
  const selectedTrain = world.trains.find((train) => train.id === selectedTrainId && !["reserve", "withdrawn"].includes(train.state));
  const selectedLine = selectedTrain ? lineFor(network, selectedTrain.line) : undefined;

  return (
    <main>
      <section className="passenger-hero">
        <h1>Plan your trip</h1>
        <p>Find your next train and a route that works for you.</p>
      </section>

      <JourneyPlanner network={network} world={world} />
      <ServiceStatus world={world} network={network} stale={stale} />

      <section className="public-section public-map-section" id="map">
        <div className="section-heading">
          <div><span className="eyebrow">Explore</span><h2>System map</h2></div>
          <p>Choose a station for arrivals or a train for its latest reported location and crowding.</p>
        </div>
        <div className="public-map-layout">
          <NetworkMap
            network={network}
            world={world}
            selectedStationId={selectedTrain ? null : selectedStationId}
            selectedTrainId={selectedTrain?.id}
            onSelectStation={(id) => {
              setSelectedStationId(id);
              setSelectedTrainId(null);
            }}
            onSelectTrain={setSelectedTrainId}
            calm
            stale={stale}
          />
          <aside className="public-board-preview" aria-label="Map details">
            <div className="board-preview-heading">
              <span className="eyebrow">{selectedTrain ? "Train details" : "Arrivals"}</span>
              <h3 aria-live="polite">{selectedTrain?.id ?? selectedStation?.name ?? "Select a station"}</h3>
              {selectedTrain && !["reserve", "withdrawn"].includes(selectedTrain.state) && selectedLine && (
                <a className="public-train-line-link" href={`#line-${selectedTrain.line.toLowerCase()}`}>
                  {selectedLine.name} status and stops
                </a>
              )}
              {selectedStationId && (
                <button onClick={() => selectedTrain ? setSelectedTrainId(null) : onNavigate(`/stations/${selectedStationId}`)}>
                  {selectedTrain ? `Back to ${selectedStation?.name ?? "station"} arrivals` : "Open full station page →"}
                </button>
              )}
            </div>
            {selectedTrain ? <TrainFacts train={selectedTrain} network={network} stale={stale} /> : <StationBoardView
              passenger
              board={board.data}
              network={network}
              scenarioId={world.scenarioId}
              generation={world.generation}
              loading={board.loading}
              error={board.error}
              stale={stale}
              compact
            />}
          </aside>
        </div>
      </section>

      <AccessibilityPanel world={world} network={network} stale={stale} />

      <section className="public-section public-assistant-section" id="assistant">
        <div className="assistant-intro">
          <span className="eyebrow">Trip help</span>
          <h2>Ask about your trip</h2>
          <p>{capabilities?.text
            ? "Type a question about routes or arrivals."
            : "Use the trip planner to find a route, or choose a station for arrival times."}</p>
        </div>
        <AssistantPanel
          kind="passenger"
          network={network}
          currentGeneration={world.generation}
          currentScenarioId={world.scenarioId}
          capabilities={capabilities}
          capabilityError={capabilityError}
        />
      </section>
    </main>
  );
}

function StationPage({
  stationId,
  network,
  world,
  onNavigate,
  stale,
}: {
  stationId: string;
  network: NetworkDefinition;
  world: WorldState;
  onNavigate: (path: string) => void;
  stale: boolean;
}) {
  const station = network.stations.find((item) => item.id === stationId);
  const board = useStationBoard(station ? stationId : null);
  const stationIncidents = activeStationIncidents(world.incidents, stationId);
  const edges = network.accessEdges.filter((edge) => edge.stationId === stationId);
  const servingLines = network.lines.filter((line) => line.stations.includes(stationId));
  const trains = world.trains.filter((train) => !["reserve", "withdrawn"].includes(train.state) && (train.stationId === stationId || train.nextStationId === stationId));

  if (!station) {
    return (
      <main className="station-page">
        <div className="notice notice--error">That station does not exist in the current network.</div>
        <button className="public-primary-button" onClick={() => onNavigate("/")}>Return home</button>
      </main>
    );
  }

  return (
    <main className="station-page">
      <button className="back-link" onClick={() => onNavigate("/")}>← System home</button>
      <section className="station-page-hero">
        <div className={`station-page-art station-page-art--${station.character}`} aria-hidden="true"><i /><i /><i /></div>
        <div>
          <span className="eyebrow">{titleCase(station.character)} district</span>
          <h1>{station.name}</h1>
          <div className="serving-lines">
            {servingLines.map((line) => <span key={line.id} className={`line-pill line-pill--${line.id.toLowerCase()}`}>{line.name}</span>)}
          </div>
        </div>
        <div className="station-page-updated"><span>Travel date and time</span><strong><time dateTime={world.simTime}>{formatPassengerDateTime(world.simTime)}</time></strong><small>Last updated <time dateTime={world.updatedAt}>{formatPassengerDateTime(world.updatedAt)}</time></small></div>
      </section>
      {stationIncidents.length > 0 && (
        <section className="station-alert-banner">
          <strong>{stale ? "Last reported alerts" : `${stationIncidents.length} ${stationIncidents.length === 1 ? "alert" : "alerts"}`}</strong>
          {stationIncidents.map((incident) => {
            const alert = publicAlert(incident, network, world.trains);
            return <span key={incident.id}>{alert.location} — {alert.title}. {alert.description}</span>;
          })}
        </section>
      )}
      <section className="station-page-grid">
        <div className="station-board-card">
          <div className="section-heading"><div><span className="eyebrow">Next trains</span><h2>Arrivals</h2></div></div>
          <StationBoardView
            passenger
            board={board.data}
            network={network}
            scenarioId={world.scenarioId}
            generation={world.generation}
            loading={board.loading}
            error={board.error}
            stale={stale}
          />
        </div>
        <aside className="station-info-card">
          <h2 id="accessibility">Elevator and station access</h2>
          <div className="station-info-metrics">
            <div><span>Lines</span><strong>{servingLines.length}</strong></div>
            <div><span>Station paths</span><strong>{edges.length}</strong></div>
            <div><span>Trains nearby</span><strong>{trains.length}</strong></div>
          </div>
          <h3>Getting through the station</h3>
          {edges.map((edge, index) => {
            const out = stationIncidents.some((incident) => incident.target === edge.equipmentId || incident.kind === "station-closure");
            return (
              <div className={`public-access-edge ${out ? "out" : ""}`} key={`${edge.from}-${edge.to}-${index}`}>
                <span>{edge.accessible ? "♿" : "↕"}</span>
                <div><strong>{titleCase(edge.from)} to {titleCase(edge.to)}</strong><small>{formatDuration(edge.seconds)} estimated · {edge.accessible ? "Step-free" : "Not step-free"}</small></div>
                <em>{stale ? "Status unavailable" : out ? "Unavailable" : "Available"}</em>
              </div>
            );
          })}
        </aside>
      </section>
      <section className="station-mini-map">
        <NetworkMap
          network={network}
          world={world}
          selectedStationId={stationId}
          onSelectStation={(id) => onNavigate(`/stations/${id}`)}
          calm
          stale={stale}
        />
      </section>
    </main>
  );
}

export function PassengerApp() {
  const worldState = useWorld();
  const capabilities = useCapabilities();
  const { path, navigate } = usePath();
  const stationMatch = path.match(/^\/stations\/([^/]+)\/?$/);

  if (worldState.loading && !worldState.data) {
    return (
      <div className="passenger-app">
        {!stationMatch && <ServiceAlerts />}
        <PassengerHeader onNavigate={navigate} showClock={Boolean(stationMatch)} />
        <main>
          <section className="passenger-hero page-loading">
            <p role="status">Loading service information…</p>
          </section>
        </main>
      </div>
    );
  }

  if (!worldState.data) {
    return (
      <main className="app-loading passenger-theme">
        <div className="notice notice--error">{worldState.error ?? "Live transit information is unavailable."}</div>
        <button className="public-primary-button" onClick={() => void worldState.refresh()}>Try again</button>
      </main>
    );
  }

  const { world, network } = worldState.data;
  const stale = worldState.data.stale || Boolean(worldState.error);
  return (
    <div className="passenger-app">
      {!stationMatch && <ServiceAlerts snapshot={worldState.data} stale={stale} />}
      <PassengerHeader onNavigate={navigate} simulatedTime={world.simTime} stale={stale} showClock={Boolean(stationMatch)} />
      {stale && stationMatch && (
        <div className="public-data-warning">
          Service updates are delayed. Last updated {formatPassengerDateTime(world.updatedAt)}. Check again before travelling.
        </div>
      )}
      {stationMatch ? (
        <StationPage stationId={decodeURIComponent(stationMatch[1])} network={network} world={world} onNavigate={navigate} stale={stale} />
      ) : (
        <HomePage
          world={world}
          network={network}
          capabilities={capabilities.data}
          capabilityError={capabilities.error}
          onNavigate={navigate}
          stale={stale}
        />
      )}
      <footer className="passenger-footer">
        <TransitBrand />
        <p>Routes, station access and arrival times for Aspireland. <a className="font-license-link" href={interLicense} rel="license">Inter font license</a></p>
      </footer>
    </div>
  );
}
