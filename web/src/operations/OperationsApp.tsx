import { useMemo, useState, type ReactNode } from "react";
import { api } from "../api";
import { isWaitingCohort, summarizeCohorts } from "../cohorts";
import { AssistantPanel } from "../components/AssistantPanel";
import { NetworkMap } from "../components/NetworkMap";
import { StationBoardView } from "../components/StationBoardView";
import { TransitBrand } from "../components/TransitBrand";
import { formatClock, formatDuration, formatSimTimestamp, formatWallTimestamp, stationName, titleCase } from "../format";
import { useCapabilities, useStationBoard, useWorld } from "../hooks";
import { activeStationIncidents } from "../passengerData";
import { DispatchDesk } from "./DispatchDesk";
import type {
  Incident,
  NetworkDefinition,
  ServiceAction,
  StationDefinition,
  TrainState,
  WorldState,
} from "../types";

type DetailTab = "arrivals" | "station" | "operations";

function Metric({ label, value, detail, tone, className = "" }: { label: string; value: string; detail?: string; tone?: string; className?: string }) {
  return (
    <div className={`ops-metric ${tone ? `ops-metric--${tone}` : ""} ${className}`}>
      <span>{label}</span>
      <strong>{value}</strong>
      {detail && <small>{detail}</small>}
    </div>
  );
}

function Controls({ world, refresh }: { world?: WorldState; refresh: () => Promise<void> }) {
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [seed, setSeed] = useState(world ? String(world.seed) : "");

  const run = async (label: string, operation: () => Promise<unknown>) => {
    setBusy(label);
    setError(null);
    try {
      await operation();
      await refresh();
    } catch (controlError) {
      setError(controlError instanceof Error ? controlError.message : "The control request failed.");
    } finally {
      setBusy(null);
    }
  };

  return (
    <div className="ops-control-cluster">
      <button
        className={`pause-button ${world?.paused ? "paused" : ""}`}
        onClick={world ? () => void run("clock", () => api.clock({ paused: !world.paused })) : undefined}
        disabled={!world || busy !== null}
      >
        {world?.paused ? "Resume" : "Pause"}
      </button>
      <label className="compact-field">
        <span>Speed</span>
        <select
          value={world?.speed ?? ""}
          onChange={world ? (event) => void run("speed", () => api.clock({ speed: Number(event.target.value) })) : undefined}
          disabled={!world || busy !== null}
        >
          {!world && <option value="">—</option>}
          {[1, 5, 10, 30, 60].map((speed) => <option key={speed} value={speed}>{speed}×</option>)}
        </select>
      </label>
      <label className="compact-field">
        <span>Authority</span>
        <select
          value={world?.authorityMode ?? ""}
          onChange={world ? (event) => void run("authority", () => api.authority(event.target.value)) : undefined}
          disabled={!world || busy !== null}
        >
          {!world && <option value="">—</option>}
          <option value="advise">Advise</option>
          <option value="approval">Approval</option>
          <option value="bounded">Bounded</option>
        </select>
      </label>
      <details className="scenario-menu" inert={!world} aria-disabled={!world || undefined}>
        <summary>New scenario</summary>
        <div>
          <label>
            <span>Seed</span>
            <input value={seed} inputMode="numeric" onChange={(event) => setSeed(event.target.value)} />
          </label>
          <button disabled={!world} onClick={world ? () => void run("normal", () => api.scenario({ seed: Number(seed), preset: "normal" })) : undefined}>Normal day</button>
          <button disabled={!world} onClick={world ? () => void run("rough", () => api.scenario({ seed: Number(seed), preset: "rough" })) : undefined}>Rough day</button>
        </div>
      </details>
      {error && <span className="control-error">{error}</span>}
    </div>
  );
}

function OperationsFrame({ world, waiting = 0, refresh, warning, children }: {
  world?: WorldState;
  waiting?: number;
  refresh: () => Promise<void>;
  warning?: ReactNode;
  children: ReactNode;
}) {
  return (
    <div className="ops-app">
      <header className="ops-topbar">
        <div className="ops-brand"><TransitBrand /></div>
        <div className="sim-clock">
          <span className={!world ? "status-dot status-dot--unknown" : world.paused ? "status-dot status-dot--paused" : "status-dot"} />
          <div><small>SIMULATED TIME</small><strong>{world ? formatClock(world.simTime) : "Awaiting data"}</strong></div>
          <span>{world ? world.paused ? "PAUSED" : `${world.speed}× LIVE` : "—"}</span>
        </div>
        <div className="scenario-identity">
          <small>SCENARIO</small>
          <strong>{world ? `${titleCase(world.preset)} · G${world.generation}` : "Awaiting data"}</strong>
          <span>{world?.scenarioId.slice(0, 8) ?? "—"}</span>
        </div>
        <Controls key={world ? "loaded" : "pending"} world={world} refresh={refresh} />
      </header>
      {warning}
      <section className="ops-metrics-strip" aria-label="Network metrics" aria-busy={!world || undefined}>
        <Metric label="Trains active" value={world ? world.trains.filter((train) => train.state !== "reserve" && train.state !== "withdrawn").length.toString() : "—"} detail={world ? `${world.trains.length} registered` : "\u00a0"} />
        <Metric className="ops-metric--waiting-time" label="Waiting" value={world ? waiting.toLocaleString() : "—"} detail={world ? `${formatDuration(world.metrics.waitingPassengerSeconds)} accumulated` : "\u00a0"} tone={world && waiting > 600 ? "warning" : ""} />
        <Metric label="Completed" value={world?.metrics.completed.toLocaleString() ?? "—"} detail={world ? `${world.metrics.deniedBoardings} denied boardings` : "\u00a0"} />
        <Metric label="Incidents" value={world ? world.incidents.filter((incident) => incident.state === "active").length.toString() : "—"} detail={world ? `${world.metrics.accessibilityFailures} access failures` : "\u00a0"} tone={world?.incidents.some((incident) => incident.state === "active") ? "critical" : ""} />
        <Metric label="Shared trunk" value={world ? `${Object.keys(world.blockOccupancy).filter((key) => key.startsWith("BA:")).length} blocks` : "—"} detail={world ? `${Object.keys(world.junctionReservations).length} junction routes` : "\u00a0"} />
      </section>
      {children}
    </div>
  );
}

function DispatchHeading() {
  return <div className="dispatch-heading"><div><h1>Dispatch desk</h1><p>Exceptions, recovery and service controls</p></div><nav aria-label="Dispatch sections"><a href="#dispatch-assistant">Dispatch assistant</a><a href="#network">Network and station inspection ↓</a></nav></div>;
}

function TrainCard({
  train,
  network,
  world,
  onChanged,
}: {
  train: TrainState;
  network: NetworkDefinition;
  world: WorldState;
  onChanged: () => Promise<void>;
}) {
  const [message, setMessage] = useState<{ text: string; error: boolean } | null>(null);
  const [busy, setBusy] = useState(false);

  const perform = async (action: ServiceAction) => {
    setBusy(true);
    try {
      const result = await api.action(action, {
        scenarioId: world.scenarioId,
        generation: world.generation,
        expectedVersion: world.version,
        reason: `Manual train control from ${train.id} detail card`,
      });
      setMessage({ text: result.message, error: !result.accepted });
      await onChanged();
    } catch (error) {
      setMessage({ text: error instanceof Error ? error.message : "Train command failed.", error: true });
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="train-detail">
      <header>
        <span className={`line-pill line-pill--${train.line.toLowerCase()}`}>{train.line}</span>
        <div>
          <span className="eyebrow">Train</span>
          <h2>{train.id}</h2>
        </div>
        <span className={`state-badge state-badge--${train.state}`}>{titleCase(train.state)}</span>
      </header>
      <dl className="detail-list">
        <div><dt>Location</dt><dd>{stationName(network, train.stationId)}</dd></div>
        <div><dt>Next stop</dt><dd>{train.nextStationId ? stationName(network, train.nextStationId) : "Awaiting assignment"}</dd></div>
        <div><dt>Load</dt><dd>{train.load}/{train.capacity} · {Math.round((train.load / Math.max(1, train.capacity)) * 100)}%</dd></div>
        <div><dt>Direction</dt><dd>{train.direction > 0 ? "Outbound" : "Inbound"}</dd></div>
        <div><dt>Hold reason</dt><dd className={train.holdReason ? "warning-text" : ""}>{train.holdReason ?? "No active hold"}</dd></div>
        <div><dt>Worker heartbeat</dt><dd>{train.lastHeartbeatAt ? `${formatWallTimestamp(train.lastHeartbeatAt)} local` : "Not reported"}</dd></div>
      </dl>
      <div className="train-load-visual">
        <i style={{ width: `${Math.min(100, (train.load / Math.max(1, train.capacity)) * 100)}%` }} />
      </div>
      <div className="button-row">
        <button disabled={busy || ["reserve", "withdrawn", "travelling", "uncertain"].includes(train.state)} onClick={() => void perform({ kind: "hold-train", target: train.id, amount: 120 })}>Hold 2 min</button>
        <button disabled={busy || train.state !== "held"} onClick={() => void perform({ kind: "release-train", target: train.id })}>Release</button>
        <button className="danger-button" disabled={busy || ["travelling", "uncertain", "withdrawn"].includes(train.state)} onClick={() => void perform({ kind: "withdraw-train", target: train.id })}>Withdraw</button>
      </div>
      {message && <div className={`notice ${message.error ? "notice--error" : ""}`} role={message.error ? "alert" : "status"}>{message.text}</div>}
    </section>
  );
}

function StationMetrics({ station, world }: { station: StationDefinition; world: WorldState }) {
  const summary = summarizeCohorts(world.cohorts.filter((cohort) => cohort.stationId === station.id), world.simTime);
  return (
    <div className="station-metric-grid">
      <Metric label="Waiting" value={summary.waiting.toLocaleString()} detail={`${Math.round((summary.waiting / station.platformCapacity) * 100)}% platform capacity`} tone={summary.waiting > station.platformCapacity * 0.8 ? "critical" : ""} />
      <Metric label="Outside" value={summary.outside.toLocaleString()} detail="Access queue" />
      <Metric label="Platform" value={summary.platform.toLocaleString()} detail={`Capacity ${station.platformCapacity}`} />
      <Metric label="Transfers" value={summary.transfers.toLocaleString()} detail="Between platform groups" />
    </div>
  );
}

function AccessStatus({ station, network, incidents }: { station: StationDefinition; network: NetworkDefinition; incidents: Incident[] }) {
  const edges = network.accessEdges.filter((edge) => edge.stationId === station.id);
  return (
    <div className="access-list">
      {edges.map((edge, index) => {
        const outage = incidents.find((incident) =>
          incident.state === "active" &&
          (incident.target === edge.equipmentId || incident.target === station.id) &&
          (incident.kind.includes("elevator") || incident.kind.includes("circulation") || incident.kind.includes("station")),
        );
        return (
          <div className={`access-edge ${outage ? "access-edge--out" : ""}`} key={`${edge.from}-${edge.to}-${index}`}>
            <span className="access-icon">{edge.accessible ? "♿" : "↕"}</span>
            <div>
              <strong>{titleCase(edge.from)} → {titleCase(edge.to)}</strong>
              <small>{edge.seconds}s · {edge.peoplePerMinute}/min · {edge.equipmentId ? titleCase(edge.equipmentId.split(":").at(-1) ?? "") : "Open path"}</small>
            </div>
            <span>{outage ? "Unavailable" : "Open"}</span>
          </div>
        );
      })}
    </div>
  );
}

function StationDetail({
  station,
  network,
  world,
  tab,
  setTab,
}: {
  station: StationDefinition;
  network: NetworkDefinition;
  world: WorldState;
  tab: DetailTab;
  setTab: (tab: DetailTab) => void;
}) {
  const board = useStationBoard(station.id);
  const stationIncidents = activeStationIncidents(world.incidents, station.id);
  const stationHistory = world.history
    .filter((event) => event.entityId === station.id || event.description.toLowerCase().includes(station.name.toLowerCase()))
    .slice(-12)
    .reverse();

  return (
    <section className="station-detail">
      <header className="detail-header">
        <div className={`station-character station-character--${station.character}`} aria-hidden="true" />
        <div>
          <span className="eyebrow">{titleCase(station.character)} station</span>
          <h2>{station.name}</h2>
        </div>
      </header>
      <div className="detail-tabs" role="tablist">
        {(["arrivals", "station", "operations"] as DetailTab[]).map((value) => (
          <button
            role="tab"
            aria-selected={tab === value}
            className={tab === value ? "active" : ""}
            onClick={() => setTab(value)}
            key={value}
          >
            {titleCase(value)}
          </button>
        ))}
      </div>
      {tab === "arrivals" && (
        <StationBoardView
          board={board.data}
          network={network}
          scenarioId={world.scenarioId}
          generation={world.generation}
          loading={board.loading}
          error={board.error}
          compact
        />
      )}
      {tab === "station" && (
        <div className="detail-section-stack">
          <StationMetrics station={station} world={world} />
          <div>
            <h3>Access and equipment</h3>
            <AccessStatus station={station} network={network} incidents={stationIncidents} />
          </div>
        </div>
      )}
      {tab === "operations" && (
        <div className="detail-section-stack">
          <div>
            <h3>Active incidents</h3>
            {stationIncidents.length === 0 ? <div className="empty-state">No incidents at this station.</div> : (
              <div className="incident-list">
                {stationIncidents.map((incident) => (
                  <article key={incident.id}>
                    <span className="incident-severity">{incident.severity.toFixed(1)}</span>
                    <div><strong>{titleCase(incident.kind)}</strong><p>{incident.description}</p></div>
                    <span>{titleCase(incident.state)}</span>
                  </article>
                ))}
              </div>
            )}
          </div>
          <div>
            <h3>Recent history</h3>
            <div className="history-list">
              {stationHistory.map((event) => (
                <article key={event.id}>
                  <time>{formatSimTimestamp(event.simTime)}</time>
                  <div><strong>{titleCase(event.kind)}</strong><p>{event.description}</p></div>
                  <span>{event.source}</span>
                </article>
              ))}
              {stationHistory.length === 0 && <div className="empty-state">No station history is available yet.</div>}
            </div>
          </div>
        </div>
      )}
    </section>
  );
}

export function OperationsApp() {
  const worldState = useWorld();
  const capabilities = useCapabilities();
  const [selectedStationId, setSelectedStationId] = useState<string | null>("central-exchange");
  const [selectedTrainId, setSelectedTrainId] = useState<string | null>(null);
  const [tab, setTab] = useState<DetailTab>("station");

  const snapshot = worldState.data;
  const selectedStation = snapshot?.network.stations.find((station) => station.id === selectedStationId) ?? null;
  const selectedTrain = snapshot?.world.trains.find((train) => train.id === selectedTrainId) ?? null;
  const waiting = useMemo(
    () => snapshot?.world.cohorts
      .filter((cohort) => isWaitingCohort(cohort, snapshot.world.simTime))
      .reduce((sum, cohort) => sum + cohort.count, 0) ?? 0,
    [snapshot],
  );

  if (!snapshot) {
    return (
      <OperationsFrame refresh={worldState.refresh}>
        <main>
          <section className="dispatch-section page-loading">
            <DispatchHeading />
            {worldState.loading ? <p role="status">Loading the dispatch desk…</p> : (
              <>
                <div className="notice notice--error" role="alert">{worldState.error ?? "The operations API did not return a world snapshot."}</div>
                <button className="primary-button" onClick={() => void worldState.refresh()}>Retry</button>
              </>
            )}
          </section>
        </main>
      </OperationsFrame>
    );
  }

  const { world, network } = snapshot;
  return (
    <OperationsFrame world={world} waiting={waiting} refresh={worldState.refresh} warning={(snapshot.stale || worldState.error) && (
        <div className="global-warning">
          <strong>{snapshot.stale ? "Projection data is stale." : "Live refresh failed."}</strong>
          <span>{snapshot.warning ?? worldState.error} Last good wall-clock update {formatWallTimestamp(world.updatedAt)} local.</span>
        </div>
      )}>
      <main>
      <section className="dispatch-section" aria-label="Dispatch desk">
        <DispatchHeading />
        <div className="dispatch-layout">
          <DispatchDesk
            key={`${world.scenarioId}:${world.generation}`}
            world={world}
            network={network}
            stale={snapshot.stale}
            error={worldState.error}
            refresh={worldState.refresh}
            onInspectStation={(id) => { setSelectedStationId(id); setSelectedTrainId(null); }}
            onInspectTrain={(id) => { setSelectedTrainId(id); setSelectedStationId(null); }}
          />
          <aside className="dispatch-assistant" id="dispatch-assistant">
            <AssistantPanel
              kind="operations"
              network={network}
              currentGeneration={world.generation}
              currentScenarioId={world.scenarioId}
              capabilities={capabilities.data}
              capabilityError={capabilities.error}
              onProposalApproved={worldState.refresh}
            />
          </aside>
        </div>
      </section>
      <section className="ops-network-section" id="network" aria-label="Network and station inspection">
        <h2>Network and station inspection</h2>
        <div className="ops-workspace">
        <NetworkMap
          network={network}
          world={world}
          selectedStationId={selectedStationId}
          selectedTrainId={selectedTrainId}
          onSelectStation={(stationId) => {
            setSelectedStationId(stationId);
            setSelectedTrainId(null);
          }}
          onSelectTrain={(trainId) => {
            setSelectedTrainId(trainId);
            setSelectedStationId(null);
          }}
        />
        <aside className="ops-detail-panel">
          {selectedTrain ? (
            <TrainCard train={selectedTrain} network={network} world={world} onChanged={worldState.refresh} />
          ) : selectedStation ? (
            <StationDetail station={selectedStation} network={network} world={world} tab={tab} setTab={setTab} />
          ) : (
            <div className="empty-state">Choose a station or train to inspect live state.</div>
          )}
        </aside>
        </div>
      </section>
      <section className="ops-audit-section" aria-label="Recent audit history">
        <header><h2>Recent audit history</h2><span>Latest {Math.min(12, world.history.length)} recorded events</span></header>
        <div className="history-list">
          {world.history.slice(-12).reverse().map((event) => <article key={event.id}>
            <time>{formatSimTimestamp(event.simTime)}</time><div><strong>{titleCase(event.kind)}</strong><p>{event.description}</p></div><span>{event.source}</span>
          </article>)}
          {!world.history.length && <div className="empty-state">No events have been recorded in this scenario yet. Applied actions and service events appear here.</div>}
        </div>
      </section>
      </main>
    </OperationsFrame>
  );
}
