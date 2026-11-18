import { formatSimTimestamp, titleCase } from "../format";
import type { ComparisonResult, ForecastMetrics, MetricRange } from "../types";

function delta(baseline: MetricRange, intervention: MetricRange, lowerIsBetter: boolean): { value: number; good: boolean } {
  const value = intervention.mean - baseline.mean;
  return { value, good: lowerIsBetter ? value <= 0 : value >= 0 };
}

const metrics: Array<{ key: keyof ForecastMetrics; label: string; lower: boolean }> = [
  { key: "waitingMinutes", label: "Waiting minutes", lower: true },
  { key: "completed", label: "Completed journeys", lower: false },
  { key: "deniedBoardings", label: "Denied boardings", lower: true },
  { key: "abandoned", label: "Abandoned", lower: true },
  { key: "remainingQueue", label: "Remaining queue", lower: true },
  { key: "accessibilityFailures", label: "Access failures", lower: true },
];

export function ComparisonCard({ comparison, currentScenarioId, currentGeneration }: {
  comparison: ComparisonResult;
  currentScenarioId?: string;
  currentGeneration?: number;
}) {
  const invalidated = (currentScenarioId !== undefined && comparison.scenarioId !== currentScenarioId) ||
    (currentGeneration !== undefined && comparison.generation !== currentGeneration);
  return (
    <article className="comparison-card">
      <header>
        <div>
          <span className="eyebrow">Paired forecast</span>
          <h3>{titleCase(comparison.candidate.kind)}</h3>
        </div>
        <span>{comparison.samples} samples · {comparison.horizonMinutes} min</span>
      </header>
      {invalidated && <div className="notice notice--warning">Scenario changed. Run a new comparison before using this forecast.</div>}
      <div className="comparison-grid">
        {metrics.map(({ key, label, lower }) => {
          const base = comparison.baseline[key];
          const candidate = comparison.intervention[key];
          const change = delta(base, candidate, lower);
          return (
            <div key={key} className="comparison-metric">
              <span>{label}</span>
              <strong>{candidate.mean.toFixed(1)}</strong>
              <small>range {candidate.min.toFixed(1)}–{candidate.max.toFixed(1)}</small>
              <em className={change.good ? "good" : "bad"}>
                {change.value > 0 ? "+" : ""}{change.value.toFixed(1)} vs baseline
              </em>
            </div>
          );
        })}
      </div>
      <footer>
        <span>Checkpoint {formatSimTimestamp(comparison.checkpointTime)}</span>
        <span>{comparison.method}</span>
      </footer>
      {comparison.warnings.length > 0 && <div className="notice notice--warning">{comparison.warnings.join(" · ")}</div>}
    </article>
  );
}
