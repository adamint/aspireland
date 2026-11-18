import { useEffect, useState, type FormEvent } from "react";
import { api } from "../api";
import { AssistantPanel } from "../components/AssistantPanel";
import { JourneyCards } from "../components/JourneyCards";
import { NetworkMap } from "../components/NetworkMap";
import { StationBoardView } from "../components/StationBoardView";
import { TransitBrand } from "../components/TransitBrand";
import { formatClock, formatDateTimeLocal, formatWallTimestamp, lineFor, parseSimulatedLocalInput, relativeFreshness, stationName, titleCase, viewerTimeZone } from "../format";
import { useCapabilities, useStationBoard, useWorld } from "../hooks";
import { activeStationIncidents, lineIncidents } from "../passengerData";
import type { JourneyResult, NetworkDefinition, TrainState, WorldState } from "../types";

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
}: {
  onNavigate: (path: string) => void;
  simulatedTime: string;
  stale: boolean;
}) {
  return (
    <>
      <div className="public-sim-banner">
        <span>{stale ? "Simulation · updates delayed" : "Simulated service"}</span>
        <strong>{formatClock(simulatedTime)}</strong>
        <span>This is simulated service, not a real transit system.</span>
      </div>
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

function ServiceStatus({ world, network }: { world: WorldState; network: NetworkDefinition }) {
  return (
    <section className="public-section status-section" id="status">
      <div className="section-heading">
        <div><span className="eyebrow">Right now</span><h2>Service status</h2></div>
        <span className="updated-label">Updated {relativeFreshness(world.updatedAt)}</span>
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
            <article key={line.id} className={`line-status-card ${incidents.length ? "line-status-card--alert" : ""}`}>
              <header>
                <span className={`public-line-icon public-line-icon--${line.id.toLowerCase()}`}>{line.id}</span>
                <div><strong>{line.name}</strong><small>{operating} trains in service</small></div>
                <span className={incidents.length ? "status-alert" : "status-good"}>{incidents.length ? "Service alert" : "Good service"}</span>
              </header>
              {incidents.length ? (
                <div className="line-alerts">
                  {incidents.slice(0, 3).map((incident) => (
                    <p key={incident.id}><strong>{titleCase(incident.kind)}:</strong> {incident.description}</p>
                  ))}
                  {affected.size > 0 && <small>Affected: {[...affected].join(", ")}</small>}
                </div>
              ) : (
                <p>No current disruptions are reported on this line.</p>
              )}
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
        <p>Compare live routes, including step-free options.</p>
        <div className="planner-time-callout">
          <span>Leave now at</span>
          <strong>{formatClock(world.simTime)}</strong>
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
              <span>Departure date and time ({timeZone})</span>
              <input type="datetime-local" value={departureTime} onChange={(event) => setDepartureTime(event.target.value)} required aria-invalid={Boolean(error)} />
            </label>
          )}
          <label className="accessible-check">
            <input type="checkbox" checked={accessible} onChange={(event) => setAccessible(event.target.checked)} />
            <span>Step-free route only</span>
          </label>
          <button className="public-primary-button" disabled={loading || origin === destination}>
            {loading ? "Planning…" : "Plan journey"}
          </button>
        </div>
        {origin === destination && <div className="notice notice--warning">Choose two different stations.</div>}
        {error && <div className="notice notice--error" role="alert">{error}</div>}
      </form>
      {result && <JourneyCards result={result} network={network} currentGeneration={world.generation} currentScenarioId={world.scenarioId} />}
    </section>
  );
}

function AccessibilityPanel({ world, network }: { world: WorldState; network: NetworkDefinition }) {
  const restrictions = world.incidents.filter((incident) =>
    incident.state === "active" &&
    ["elevator-outage", "circulation", "platform-closure", "station-closure"].includes(incident.kind),
  );
  return (
    <section className="accessibility-panel" id="accessibility">
      <div>
        <span className="accessibility-symbol" aria-hidden="true">♿</span>
        <span className="eyebrow">Accessibility status</span>
        <h2>Step-free travel updates</h2>
        <p>The ordinary journey form can avoid known inaccessible paths. You do not need the AI assistant to plan an accessible trip.</p>
      </div>
      <div className="accessibility-updates">
        {restrictions.length === 0 ? (
          <div className="access-good"><strong>All modeled step-free paths are available.</strong><span>Last updated {formatWallTimestamp(world.updatedAt)} local</span></div>
        ) : restrictions.map((incident) => (
          <article key={incident.id}>
            <strong>{stationName(network, incident.target.split(":")[0])}</strong>
            <span>{titleCase(incident.kind)}</span>
            <p>{incident.description}</p>
          </article>
        ))}
      </div>
    </section>
  );
}

function TrainFacts({ train, network }: { train: TrainState; network: NetworkDefinition }) {
  const currentStation = network.stations.find((station) => station.id === train.stationId);
  const nextStation = network.stations.find((station) => station.id === train.nextStationId);
  const locationLabel = train.state === "travelling" ? "Departed" : train.state === "uncertain" ? "Last reported station" : "Current station";
  return (
    <dl className="public-train-facts">
      <div><dt>Status</dt><dd>{titleCase(train.state)}</dd></div>
      <div><dt>{locationLabel}</dt><dd>{currentStation?.name ?? "Not reported"}</dd></div>
      <div><dt>Next station</dt><dd>{nextStation?.name ?? "Not reported"}</dd></div>
      <div>
        <dt>Crowding</dt>
        <dd>{train.capacity > 0
          ? `${train.load} / ${train.capacity} passengers · ${Math.round(train.load / train.capacity * 100)}% full`
          : `${train.load} passengers · capacity unknown`}</dd>
      </div>
    </dl>
  );
}

function HomePage({
  world,
  network,
  capabilities,
  capabilityError,
  onNavigate,
}: {
  world: WorldState;
  network: NetworkDefinition;
  capabilities: ReturnType<typeof useCapabilities>["data"];
  capabilityError: string | null;
  onNavigate: (path: string) => void;
}) {
  const [selectedStationId, setSelectedStationId] = useState<string | null>("central-exchange");
  const [selectedTrainId, setSelectedTrainId] = useState<string | null>(null);
  const board = useStationBoard(selectedStationId);
  const selectedStation = network.stations.find((station) => station.id === selectedStationId);
  const selectedTrain = world.trains.find((train) => train.id === selectedTrainId);
  const activeIncidents = world.incidents.filter((incident) => incident.state === "active");

  return (
    <main>
      <section className="passenger-hero">
        <h1>Plan your journey</h1>
        <p>Live arrivals and step-free routes across Aspireland.</p>
      </section>

      <JourneyPlanner network={network} world={world} />
      <section className="public-service-summary" aria-label="Live service summary">
        <div><strong>Service now</strong><a href="#status">{activeIncidents.length ? `${activeIncidents.length} active service ${activeIncidents.length === 1 ? "alert" : "alerts"}` : "No active service alerts"} →</a></div>
        <div className="summary-lines">
          {network.lines.map((line) => <a href="#status" key={line.id}><span className={`line-pill line-pill--${line.id.toLowerCase()}`}>{line.id}</span><span>{lineIncidents(line, world.trains, world.incidents).length ? "Service alert" : "Good service"}</span></a>)}
        </div>
      </section>
      <ServiceStatus world={world} network={network} />

      <section className="public-section public-map-section" id="map">
        <div className="section-heading">
          <div><span className="eyebrow">Explore</span><h2>Live system map</h2></div>
          <p>Choose a station for live arrivals or a train for its current status. Airport Express uses its own dedicated corridor.</p>
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
          />
          <aside className="public-board-preview" aria-label="Map details">
            <div className="board-preview-heading">
              <span className="eyebrow">{selectedTrain ? "Train details" : "Live arrivals"}</span>
              <h3 aria-live="polite">{selectedTrain?.id ?? selectedStation?.name ?? "Select a station"}</h3>
              {selectedTrain && <span className={`line-pill line-pill--${selectedTrain.line.toLowerCase()}`}>{lineFor(network, selectedTrain.line)?.name ?? selectedTrain.line}</span>}
              {selectedStationId && (
                <button onClick={() => selectedTrain ? setSelectedTrainId(null) : onNavigate(`/stations/${selectedStationId}`)}>
                  {selectedTrain ? `Back to ${selectedStation?.name ?? "station"} arrivals` : "Open full station page →"}
                </button>
              )}
            </div>
            {selectedTrain ? <TrainFacts train={selectedTrain} network={network} /> : <StationBoardView
              board={board.data}
              network={network}
              scenarioId={world.scenarioId}
              generation={world.generation}
              loading={board.loading}
              error={board.error}
              compact
            />}
          </aside>
        </div>
      </section>

      <AccessibilityPanel world={world} network={network} />

      <section className="public-section public-assistant-section" id="assistant">
        <div className="assistant-intro">
          <span className="eyebrow">OpenAI-powered, planner-grounded</span>
          <h2>Ask in your own words.</h2>
          <p>Type or use push-to-talk. You can edit the transcript before sending, and every answer remains available as text.</p>
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
}: {
  stationId: string;
  network: NetworkDefinition;
  world: WorldState;
  onNavigate: (path: string) => void;
}) {
  const station = network.stations.find((item) => item.id === stationId);
  const board = useStationBoard(station ? stationId : null);
  const stationIncidents = activeStationIncidents(world.incidents, stationId);
  const edges = network.accessEdges.filter((edge) => edge.stationId === stationId);
  const servingLines = network.lines.filter((line) => line.stations.includes(stationId));
  const trains = world.trains.filter((train) => train.stationId === stationId || train.nextStationId === stationId);

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
        <div className="station-page-updated"><span>Simulated time</span><strong>{formatClock(world.simTime)}</strong><small>Updated {relativeFreshness(world.updatedAt)}</small></div>
      </section>
      {stationIncidents.length > 0 && (
        <section className="station-alert-banner">
          <strong>{stationIncidents.length} active {stationIncidents.length === 1 ? "alert" : "alerts"}</strong>
          {stationIncidents.map((incident) => <span key={incident.id}>{titleCase(incident.kind)} · {incident.description}</span>)}
        </section>
      )}
      <section className="station-page-grid">
        <div className="station-board-card">
          <div className="section-heading"><div><span className="eyebrow">Departures</span><h2>Live arrivals</h2></div></div>
          <StationBoardView
            board={board.data}
            network={network}
            scenarioId={world.scenarioId}
            generation={world.generation}
            loading={board.loading}
            error={board.error}
          />
        </div>
        <aside className="station-info-card">
          <h2>Station access</h2>
          <div className="station-info-metrics">
            <div><span>Entrance capacity</span><strong>{station.accessPerMinute}/min</strong></div>
            <div><span>Platform capacity</span><strong>{station.platformCapacity}</strong></div>
            <div><span>Trains nearby</span><strong>{trains.length}</strong></div>
          </div>
          <h3>Access paths</h3>
          {edges.map((edge, index) => {
            const out = stationIncidents.some((incident) => incident.target === edge.equipmentId || incident.kind === "station-closure");
            return (
              <div className={`public-access-edge ${out ? "out" : ""}`} key={`${edge.from}-${edge.to}-${index}`}>
                <span>{edge.accessible ? "♿" : "↕"}</span>
                <div><strong>{titleCase(edge.from)} to {titleCase(edge.to)}</strong><small>{edge.seconds}s · {edge.peoplePerMinute} people/min</small></div>
                <em>{out ? "Out" : "Open"}</em>
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
        />
      </section>
    </main>
  );
}

export function PassengerApp() {
  const worldState = useWorld();
  const capabilities = useCapabilities();
  const { path, navigate } = usePath();

  if (worldState.loading && !worldState.data) {
    return <main className="app-loading passenger-theme"><TransitBrand /><p>Loading live service…</p></main>;
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
  const stationMatch = path.match(/^\/stations\/([^/]+)\/?$/);
  return (
    <div className="passenger-app">
      <PassengerHeader onNavigate={navigate} simulatedTime={world.simTime} stale={worldState.data.stale || Boolean(worldState.error)} />
      {(worldState.data.stale || worldState.error) && (
        <div className="public-data-warning">
          Live updates are delayed. Last wall-clock update was {formatWallTimestamp(world.updatedAt)} local.
        </div>
      )}
      {stationMatch ? (
        <StationPage stationId={decodeURIComponent(stationMatch[1])} network={network} world={world} onNavigate={navigate} />
      ) : (
        <HomePage
          world={world}
          network={network}
          capabilities={capabilities.data}
          capabilityError={capabilities.error}
          onNavigate={navigate}
        />
      )}
      <footer className="passenger-footer">
        <TransitBrand />
        <p>A synthetic transit network for the Aspireland MetroOps simulator. No real-world travel information is provided.</p>
      </footer>
    </div>
  );
}
