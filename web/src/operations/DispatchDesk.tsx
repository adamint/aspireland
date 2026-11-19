import { useMemo, useState } from "react";
import { formatLocalTime, formatWallTimestamp, stationName, titleCase } from "../format";
import type { NetworkDefinition, ServiceAction, WorldState } from "../types";
import { ActionWorkbench, targetDescription } from "./ActionWorkbench";

export function DispatchDesk({ world, network, stale, error, refresh, onInspectStation, onInspectTrain }: {
  world: WorldState;
  network: NetworkDefinition;
  stale: boolean;
  error: string | null;
  refresh: () => Promise<void>;
  onInspectStation: (id: string) => void;
  onInspectTrain: (id: string) => void;
}) {
  const incidents = world.incidents.filter((incident) => ["active", "pending"].includes(incident.state));
  const exceptions = world.trains.filter((train) => ["held", "uncertain"].includes(train.state));
  const [selectedKey, setSelectedKey] = useState(() => incidents[0] ? `incident:${incidents[0].id}` : exceptions[0] ? `train:${exceptions[0].id}` : "");
  const selectedIncident = world.incidents.find((incident) => `incident:${incident.id}` === selectedKey);
  const selectedTrain = world.trains.find((train) => `train:${train.id}` === selectedKey) ??
    world.trains.find((train) => train.id === selectedIncident?.target);
  const station = network.stations.find((candidate) => candidate.id === selectedTrain?.stationId ||
    candidate.id === selectedIncident?.target.split(/[:>]/)[0]);
  const lines = network.lines.filter((line) => selectedTrain ? line.id === selectedTrain.line :
    selectedIncident ? line.id === selectedIncident.target || line.stations.some((id) => selectedIncident.target.split(/[:>]/).includes(id)) : false);
  const suggestedAction = useMemo<ServiceAction | undefined>(() => {
    const incident = world.incidents.find((item) => `incident:${item.id}` === selectedKey);
    if (incident) return { kind: "clear-incident", target: incident.id };
    const train = world.trains.find((item) => `train:${item.id}` === selectedKey);
    if (!train) return undefined;
    return { kind: train.state === "reserve" ? "deploy-reserve" : train.state === "held" ? "release-train" : "hold-train", target: train.id, amount: 120 };
  }, [selectedKey, world.scenarioId, world.generation]);
  const reserves = world.trains.filter((train) => train.state === "reserve");
  const selectTrain = (id: string) => {
    setSelectedKey(`train:${id}`);
    onInspectTrain(id);
  };
  return (
    <div className="dispatch-primary">
      <div className="dispatch-action-grid">
        <section className="exception-panel" aria-label="Incident and exception queue">
          <header><h2>Needs attention</h2><span>{incidents.length + exceptions.length + (stale || error ? 1 : 0)}</span></header>
          {(stale || error) && <article className="projection-exception">
            <strong>{stale ? "Stale network snapshot" : "Network refresh failed"}</strong>
            <p>{error ?? "The projection is delayed. Actions are revalidated by the server."}</p>
            <small>Last update {formatWallTimestamp(world.updatedAt)}</small>
            <button onClick={() => void refresh()}>Retry network refresh</button>
          </article>}
          <div className="exception-list">
            {incidents.map((incident) => (
              <button key={incident.id} aria-pressed={selectedKey === `incident:${incident.id}`} onClick={() => setSelectedKey(`incident:${incident.id}`)}>
                <span className="exception-kind">{titleCase(incident.kind)} <small>{titleCase(incident.state)}</small></span>
                <strong>{targetDescription(incident.target, world, network)}</strong>
                <span>{incident.description}</span>
              </button>
            ))}
            {exceptions.map((train) => (
              <button key={train.id} aria-pressed={selectedKey === `train:${train.id}`} onClick={() => selectTrain(train.id)}>
                <span className="exception-kind">{train.state === "uncertain" ? "Worker state uncertain" : "Held train"}</span>
                <strong>{train.id} ({train.line}) at {stationName(network, train.stationId)}</strong>
                <span>{train.holdReason ?? (train.state === "uncertain" ? "Occupancy needs confirmation. Inspect worker state before recovery." : "Operator hold is active.")}</span>
              </button>
            ))}
          </div>
          {!incidents.length && !exceptions.length && !stale && !error && <div className="empty-state">No active incidents, held trains or uncertain workers. Inspect the fleet or use service controls below.</div>}
        </section>

        <section className="recovery-panel" aria-label="Dispatch recovery">
          <header><h2>{selectedIncident ? `${titleCase(selectedIncident.kind)} recovery` : selectedTrain ? `${selectedTrain.id} controls` : "Service controls"}</h2></header>
          {(selectedIncident || selectedTrain) ? <div className="recovery-context">
            <p>{selectedIncident?.description ?? selectedTrain?.holdReason ?? "Select an authorized action for this train."}</p>
            <dl>
              <div><dt>State</dt><dd>{titleCase(selectedIncident?.state ?? selectedTrain?.state ?? "")}</dd></div>
              <div><dt>Service</dt><dd>{lines.map((line) => line.name).join(", ") || (selectedTrain?.state === "reserve" ? "Unassigned reserve" : "No line directly identified")}</dd></div>
              {station && <div><dt>Station</dt><dd>{station.name}</dd></div>}
              {selectedTrain && <div><dt>Train</dt><dd>{selectedTrain.id}: {titleCase(selectedTrain.state)} ({selectedTrain.load}/{selectedTrain.capacity} aboard)</dd></div>}
              {selectedIncident && <div><dt>Since</dt><dd>{formatLocalTime(selectedIncident.startedAt, {
                date: "if-different",
                relativeTo: world.simTime,
              })}</dd></div>}
              {selectedTrain?.state === "reserve" && <div><dt>Compatible lines</dt><dd>{selectedTrain.compatibleLines.join(", ")}</dd></div>}
            </dl>
            <div className="context-links">
              {station && <a href="#network" onClick={() => onInspectStation(station.id)}>Inspect station</a>}
              {selectedTrain && <a href="#network" onClick={() => onInspectTrain(selectedTrain.id)}>Inspect train</a>}
            </div>
            {selectedTrain?.state === "uncertain" && <div className="notice notice--warning">Worker state is uncertain. Inspect the heartbeat and occupancy; a release action cannot override signaling authority.</div>}
          </div> : <p className="recovery-context">Choose an exception or a fleet entry for context. Line headways and all other manual controls are available here.</p>}
          <ActionWorkbench world={world} network={network} suggestedAction={suggestedAction} refresh={refresh} />
        </section>
      </div>
      <section className="fleet-panel" aria-label="Fleet and reserves">
        <header><h2>Fleet and reserves</h2><span>{world.trains.length - reserves.length} assigned / {reserves.length} reserves</span></header>
        <div className="fleet-list">
          {[...world.trains].sort((a, b) => Number(b.state === "reserve") - Number(a.state === "reserve")).map((train) => (
            <button key={train.id} onClick={() => selectTrain(train.id)} aria-pressed={selectedKey === `train:${train.id}`}>
              <span className={`line-pill line-pill--${train.line.toLowerCase()}`}>{train.line || "Reserve"}</span>
              <strong>{train.id}</strong><span>{titleCase(train.state)}</span><small>{stationName(network, train.stationId)}</small>
            </button>
          ))}
          {!world.trains.length && <div className="empty-state">No trains are registered in this snapshot. Refresh the network before attempting train actions.</div>}
        </div>
      </section>
    </div>
  );
}
