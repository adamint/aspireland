import { useEffect, useId, useState } from "react";
import { api } from "../api";
import { ComparisonCard } from "../components/ComparisonCard";
import { stationName, titleCase } from "../format";
import type { ComparisonResult, NetworkDefinition, ServiceAction, WorldState } from "../types";

const actionLabels: Record<string, string> = {
  "clear-incident": "Clear incident",
  "release-train": "Release held train",
  "hold-train": "Hold train",
  "deploy-reserve": "Deploy reserve",
  "set-headway": "Set line headway",
  "short-turn": "Short-turn train",
  "withdraw-train": "Withdraw train",
  incident: "Open incident",
};
const incidentKinds = [
  "door-fault", "extended-dwell", "train-withdrawal", "elevator-outage", "circulation",
  "platform-closure", "segment-closure", "station-closure", "demand-surge",
];
const trainIncidents = new Set(["door-fault", "extended-dwell", "train-withdrawal"]);

interface Choice { id: string; label: string }

export function targetDescription(target: string, world: WorldState, network: NetworkDefinition): string {
  const train = world.trains.find((candidate) => candidate.id === target);
  if (train) return `${train.id} (${train.line || "Reserve"}) at ${stationName(network, train.stationId)}`;
  if (target.includes(">")) return target.split(">").map((id) => stationName(network, id)).join(" ↔ ");
  const station = network.stations.find((candidate) => candidate.id === target.split(":")[0]);
  if (station) return `${station.name}${target.includes(":") ? ` (${titleCase(target.split(":").slice(1).join(" "))})` : ""}`;
  return network.lines.find((line) => line.id === target)?.name ?? target;
}

function targetChoices(kind: string, incidentKind: string, world: WorldState, network: NetworkDefinition): Choice[] {
  const trains = (predicate: (train: WorldState["trains"][number]) => boolean) => world.trains
    .filter(predicate).map((train) => ({ id: train.id, label: targetDescription(train.id, world, network) }));
  switch (kind) {
    case "clear-incident": return world.incidents.filter((incident) => incident.state !== "cleared")
      .map((incident) => ({ id: incident.id, label: `${titleCase(incident.kind)}: ${targetDescription(incident.target, world, network)}` }));
    case "set-headway": return network.lines.map((line) => ({ id: line.id, label: line.name }));
    case "deploy-reserve": return trains((train) => train.state === "reserve" && Date.parse(train.readyAt) <= Date.parse(world.simTime));
    case "release-train": return trains((train) => train.state === "held");
    case "hold-train": return trains((train) => !["reserve", "withdrawn", "travelling", "uncertain"].includes(train.state));
    case "withdraw-train": return trains((train) => !["withdrawn", "travelling", "uncertain"].includes(train.state));
    case "short-turn": return trains((train) => !["reserve", "withdrawn", "uncertain"].includes(train.state) &&
      network.lines.some((line) => line.id === train.line && line.stations.some((id) => ["west-junction", "east-junction"].includes(id))));
    case "incident":
      if (trainIncidents.has(incidentKind)) return trains(() => true);
      if (incidentKind === "segment-closure") {
        const segments = new Map<string, Choice>();
        for (const line of network.lines) {
          line.stations.slice(1).forEach((to, index) => {
            const from = line.stations[index];
            const key = [from, to].sort().join(">");
            if (!segments.has(key)) segments.set(key, { id: `${from}>${to}`, label: `${stationName(network, from)} ↔ ${stationName(network, to)}` });
          });
        }
        return [...segments.values()];
      }
      return network.stations.map((station) => ({ id: station.id, label: station.name }));
    default: return [];
  }
}

function targetLabel(kind: string, incidentKind: string): string {
  if (kind === "clear-incident") return "Incident to clear";
  if (kind === "set-headway") return "Service line";
  if (kind === "deploy-reserve") return "Reserve train";
  if (kind === "incident") return trainIncidents.has(incidentKind) ? "Affected train" : incidentKind === "segment-closure" ? "Affected segment" : "Affected station";
  return kind === "release-train" ? "Held train" : "Service train";
}

function TermDisclosure({ term, children }: { term: string; children: string }) {
  return <details className="term-disclosure"><summary>{term}</summary><p>{children}</p></details>;
}

export function ActionWorkbench({ world, network, suggestedAction, refresh }: {
  world: WorldState;
  network: NetworkDefinition;
  suggestedAction?: ServiceAction;
  refresh: () => Promise<void>;
}) {
  const fieldId = useId();
  const [kind, setKind] = useState(suggestedAction?.kind ?? "hold-train");
  const [target, setTarget] = useState(suggestedAction?.target ?? "");
  const [value, setValue] = useState(suggestedAction?.value ?? "extended-dwell");
  const [amount, setAmount] = useState(String(suggestedAction?.amount ?? 120));
  const [reason, setReason] = useState("Manual recovery action");
  const [result, setResult] = useState<{ text: string; error: boolean } | null>(null);
  const [comparison, setComparison] = useState<ComparisonResult | null>(null);
  const [busy, setBusy] = useState<string | null>(null);

  useEffect(() => {
    if (!suggestedAction) return;
    setKind(suggestedAction.kind);
    setTarget(suggestedAction.target ?? "");
    setValue(suggestedAction.value ?? "extended-dwell");
    setAmount(String(suggestedAction.amount ?? 120));
    setResult(null);
  }, [suggestedAction]);

  const targets = targetChoices(kind, value, world, network);
  const currentTarget = targets.some((choice) => choice.id === target) ? target : target ? "" : targets[0]?.id ?? "";
  const selectedTrain = world.trains.find((train) => train.id === currentTarget);
  const values = kind === "deploy-reserve"
    ? network.lines.filter((line) => selectedTrain?.compatibleLines.includes(line.id) && line.stations.includes(selectedTrain.depot))
      .map((line) => ({ id: line.id, label: line.name }))
    : kind === "short-turn"
      ? network.stations.filter((station) => ["west-junction", "east-junction"].includes(station.id) &&
        network.lines.find((line) => line.id === selectedTrain?.line)?.stations.includes(station.id))
        .map((station) => ({ id: station.id, label: station.name }))
      : [];
  const currentValue = kind === "incident" ? value : values.some((choice) => choice.id === value) ? value : values[0]?.id ?? "";
  const usesAmount = ["hold-train", "set-headway", "incident"].includes(kind);
  const maxAmount = kind === "incident" ? 86400 : 3600;
  const amountLabel = kind === "hold-train" ? "Hold duration (seconds)" : kind === "set-headway" ? "Target headway (seconds)" : "Incident duration (seconds)";
  const validation = !currentTarget ? targets.length ? "The previous target is no longer eligible. Choose a current target."
    : `No eligible ${kind === "clear-incident" ? "incidents" : kind === "deploy-reserve" ? "ready reserves" : "targets"} for this action. Choose another action or refresh the snapshot.`
    : ["deploy-reserve", "short-turn"].includes(kind) && !currentValue ? "No compatible service or turnback is available for this train."
      : usesAmount && (!Number.isFinite(Number(amount)) || Number(amount) < 1 || Number(amount) > maxAmount) ? `Enter a duration between 1 and ${maxAmount} seconds.`
        : !reason.trim() ? "Add a reason for the audit history." : null;

  useEffect(() => setComparison(null), [kind, currentTarget, currentValue, amount, world.scenarioId, world.generation]);

  const buildAction = (): ServiceAction => ({
    kind, target: currentTarget,
    ...(kind === "incident" || values.length > 0 ? { value: currentValue } : {}),
    ...(usesAmount ? { amount: Number(amount) } : {}),
  });
  const run = async (operation: "execute" | "compare") => {
    if (validation) return;
    setBusy(operation);
    setResult(null);
    try {
      if (operation === "compare") {
        setComparison(await api.compare(buildAction(), world.scenarioId, world.generation));
      } else {
        const command = await api.action(buildAction(), {
          scenarioId: world.scenarioId, generation: world.generation,
          expectedVersion: world.version, reason: reason.trim(),
        });
        setResult({ text: command.message, error: !command.accepted });
        await refresh();
      }
    } catch (error) {
      setResult({ text: error instanceof Error ? error.message : `${titleCase(operation)} failed.`, error: true });
    } finally {
      setBusy(null);
    }
  };

  return (
    <section className="action-workbench" aria-label="Authorized service controls">
      <div className="form-grid">
        <label className="full-field"><span>Action</span>
          <select value={kind} onChange={(event) => {
            const next = event.target.value;
            setKind(next);
            setTarget("");
            setValue(next === "incident" ? "extended-dwell" : "");
            setAmount(String(next === "incident" ? 600 : next === "set-headway" ? world.headways[network.lines[0]?.id] ?? 480 : 120));
            setResult(null);
          }}>
            {Object.entries(actionLabels).map(([id, label]) => <option key={id} value={id}>{label}</option>)}
          </select>
        </label>
        {kind === "incident" && <div className="full-field">
          <div className="field-with-help-heading">
            <label htmlFor={`${fieldId}-incident`}><span>Incident type</span></label>
            {value === "extended-dwell" && <TermDisclosure term="Dwell time">The time a train is stopped at a station with its doors open for boarding and getting off.</TermDisclosure>}
          </div>
          <select id={`${fieldId}-incident`} value={value} onChange={(event) => setValue(event.target.value)}>
            {incidentKinds.map((id) => <option key={id} value={id}>{titleCase(id)}</option>)}
          </select>
        </div>}
        <label className="full-field"><span>{targetLabel(kind, value)}</span>
          <select value={currentTarget} disabled={!targets.length} onChange={(event) => setTarget(event.target.value)}>
            {!currentTarget && <option value="">{targets.length ? "Choose a current target" : "No eligible targets"}</option>}
            {targets.map((choice) => <option key={choice.id} value={choice.id}>{choice.label}</option>)}
          </select>
        </label>
        {["deploy-reserve", "short-turn"].includes(kind) && <div className="full-field">
          <div className="field-with-help-heading">
            <label htmlFor={`${fieldId}-value`}><span>{kind === "deploy-reserve" ? "Deploy on line" : "Turn back at"}</span></label>
            {kind === "short-turn" && <TermDisclosure term="Short-turn">Turn a train back before its usual terminus (the end of the line).</TermDisclosure>}
          </div>
          <select id={`${fieldId}-value`} value={currentValue} disabled={!values.length} onChange={(event) => setValue(event.target.value)}>
            {!values.length && <option value="">No compatible options</option>}
            {values.map((choice) => <option key={choice.id} value={choice.id}>{choice.label}</option>)}
          </select>
        </div>}
        {usesAmount && <div className="full-field">
          <div className="field-with-help-heading">
            <label htmlFor={`${fieldId}-amount`}><span>{amountLabel}</span></label>
            {kind === "set-headway" && <TermDisclosure term="Headway floor">The minimum spacing between train departures, not a timetable or a promise of exact arrival times.</TermDisclosure>}
          </div>
          <input id={`${fieldId}-amount`} type="number" min={1} max={maxAmount} step="any" value={amount} onChange={(event) => setAmount(event.target.value)} />
        </div>}
        <label className="full-field"><span>Reason for audit</span><input value={reason} onChange={(event) => setReason(event.target.value)} /></label>
      </div>
      {kind === "set-headway" && <p className="action-help">Current target: {world.headways[currentTarget] ?? network.lines.find((line) => line.id === currentTarget)?.headwaySeconds ?? "not reported"} seconds. Manual controls allow up to 3600 seconds; the server checks fleet feasibility.</p>}
      {validation && <div className="notice notice--warning">{validation}</div>}
      <div className="button-row">
        <button onClick={() => void run("compare")} disabled={busy !== null || Boolean(validation)}>{busy === "compare" ? "Comparing…" : "Compare 30 min"}</button>
        <button className="primary-button" onClick={() => void run("execute")} disabled={busy !== null || Boolean(validation)}>{busy === "execute" ? "Applying…" : "Apply action"}</button>
      </div>
      <small className="action-help">Server revalidates scenario, generation {world.generation} and version {world.version} before applying.</small>
      {result && <div className={`notice ${result.error ? "notice--error" : ""}`} role={result.error ? "alert" : "status"}>{result.text}</div>}
      {comparison && <ComparisonCard comparison={comparison} currentScenarioId={world.scenarioId} currentGeneration={world.generation} />}
    </section>
  );
}
