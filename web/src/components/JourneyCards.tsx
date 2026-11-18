import { formatDuration, formatPassengerDateTime, formatSimTimestamp, publicPlatformLabel, stationName, titleCase } from "../format";
import type { JourneyLeg, JourneyResult, NetworkDefinition } from "../types";

interface JourneyCardsProps {
  result: JourneyResult;
  network?: NetworkDefinition;
  currentGeneration?: number;
  currentScenarioId?: string;
  passenger?: boolean;
}

function destinationFor(leg: JourneyLeg, network?: NetworkDefinition): string {
  if (leg.destination) return leg.destination;
  const line = network?.lines.find((candidate) => candidate.id === leg.line);
  return (leg.platform?.endsWith(":-") ? line?.stations[0] : line?.stations.at(-1)) ?? leg.to;
}

function legTitle(leg: JourneyLeg, network?: NetworkDefinition): string {
  const name = (id: string) => (network ? stationName(network, id) : titleCase(id));
  if (leg.kind === "ride") {
    const destination = name(destinationFor(leg, network));
    const lineName = network?.lines.find((line) => line.id === leg.line)?.name ?? leg.line ?? "Connecting service";
    const service = /line/i.test(lineName) ? lineName : `${lineName} line`;
    return leg.service === "express" || leg.line === "AX"
      ? `Board Airport Express toward ${destination}`
      : `Board ${service} toward ${destination}`;
  }
  if (leg.kind === "access") return `Enter ${name(leg.to || leg.from)}`;
  if (leg.kind === "egress") return `Exit at ${name(leg.from || leg.to)}`;
  if (leg.kind === "transfer") {
    return leg.from === leg.to
      ? `Transfer at ${name(leg.from)}`
      : `Transfer from ${name(leg.from)} to ${name(leg.to)}`;
  }
  if (leg.kind === "walk") {
    return leg.from === leg.to
      ? `Walk within ${name(leg.from)}`
      : `Walk from ${name(leg.from)} to ${name(leg.to)}`;
  }
  return `${titleCase(leg.kind)} from ${name(leg.from)} to ${name(leg.to)}`;
}

function legPlatform(leg: JourneyLeg, network?: NetworkDefinition): string | null {
  if (!network || !leg.platform || !leg.line) return null;
  return publicPlatformLabel(network, leg.from, leg.line, destinationFor(leg, network), leg.platform);
}

export function JourneyCards({ result, network, currentGeneration, currentScenarioId, passenger = false }: JourneyCardsProps) {
  const timestamp = passenger ? formatPassengerDateTime : formatSimTimestamp;
  const invalidated = (currentGeneration !== undefined && result.generation !== currentGeneration) ||
    (currentScenarioId !== undefined && result.scenarioId !== currentScenarioId);
  return (
    <div className="journey-results" aria-live="polite">
      <div className="journey-results-heading">
        <div>
          <span className="eyebrow">Journey options</span>
          <h3>
            {network ? stationName(network, result.request.origin) : result.request.origin}
            <span aria-hidden="true"> → </span>
            {network ? stationName(network, result.request.destination) : result.request.destination}
          </h3>
        </div>
        <span className={`freshness ${invalidated ? "freshness--stale" : ""}`}>
          {invalidated ? "Service information changed · plan again" : `Updated ${timestamp(result.asOf)}`}
        </span>
      </div>
      {result.error && <div className="notice notice--error">{result.error}</div>}
      {result.options.length === 0 && !result.error && (
        <div className="empty-state">No routes found. Try another station or departure time.</div>
      )}
      <div className="journey-card-grid">
        {result.options.map((option) => (
          <article className={`journey-card ${invalidated ? "journey-card--stale" : ""}`} key={option.id}>
            <header>
              <div>
                <span className="journey-label">{option.label}</span>
                <strong>{formatDuration(option.totalSeconds)}</strong>
              </div>
              <div className="journey-meta">
                <span>{option.transfers} {option.transfers === 1 ? "transfer" : "transfers"}</span>
                <span>{option.accessible ? "Step-free" : "May include stairs"}</span>
                <span>{titleCase(option.estimateKind)} estimate</span>
              </div>
            </header>
            <ol className="journey-legs">
              {option.legs.map((leg, index) => (
                <li key={`${option.id}-${index}`} className={`journey-leg journey-leg--${leg.kind}`}>
                  <span className={`leg-icon ${leg.line ? `line-${leg.line.toLowerCase()}` : ""}`}>
                    {leg.kind === "ride" ? leg.line : leg.kind === "access" ? "Enter" : leg.kind === "egress" ? "Exit" : titleCase(leg.kind)}
                  </span>
                  <div>
                    <strong>{legTitle(leg, network)}</strong>
                    {legPlatform(leg, network) && <p>{legPlatform(leg, network)}</p>}
                    {leg.waitSeconds > 0 && <p>{formatDuration(leg.waitSeconds)} wait before boarding</p>}
                    {leg.kind === "ride" && <p>Travel to {network ? stationName(network, leg.to) : titleCase(leg.to)}{leg.rideSeconds > 0 ? ` (${formatDuration(leg.rideSeconds)} ride)` : ""}</p>}
                    {leg.walkSeconds > 0 && <p>{formatDuration(leg.walkSeconds)} walk</p>}
                    {leg.kind === "ride" && leg.stops.length > 1 && <small>{leg.stops.length - 1} {leg.stops.length === 2 ? "stop" : "stops"}</small>}
                  </div>
                </li>
              ))}
            </ol>
            <footer>
              <span>Arrive {timestamp(option.arrivesAt)}</span>
              {option.warnings.length > 0 && (
                <ul>
                  {option.warnings.map((warning) => <li key={warning}>{warning}</li>)}
                </ul>
              )}
            </footer>
          </article>
        ))}
      </div>
    </div>
  );
}
