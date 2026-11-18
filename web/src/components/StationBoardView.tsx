import { formatEta, formatSimTimestamp, formatWallTimestamp, publicPlatformLabel, stationName, titleCase } from "../format";
import type { NetworkDefinition, StationBoard } from "../types";

interface StationBoardViewProps {
  board: StationBoard | null;
  network: NetworkDefinition;
  scenarioId: string;
  generation: number;
  loading: boolean;
  error: string | null;
  compact?: boolean;
}

export function StationBoardView({
  board,
  network,
  scenarioId,
  generation,
  loading,
  error,
  compact = false,
}: StationBoardViewProps) {
  if (loading && !board) return <div className="loading-state"><span className="spinner" /> Loading live arrivals…</div>;
  if (error && !board) return <div className="notice notice--error">{error}</div>;
  if (!board) return <div className="empty-state">Select a station to view its live board.</div>;
  if (board.scenarioId !== scenarioId || board.generation !== generation) {
    return (
      <div className="loading-state" aria-live="polite">
        <span className="spinner" />
        Refreshing arrivals for the new scenario…
        <small className="board-identity-note">The previous scenario’s board is hidden.</small>
      </div>
    );
  }

  return (
    <div className={`arrival-board ${compact ? "arrival-board--compact" : ""}`}>
      <div className="arrival-board-status">
        <span className={`freshness ${board.stale ? "freshness--stale" : ""}`}>
          {board.stale ? "Stale data · as of" : "As of"} {formatSimTimestamp(board.asOf)}
        </span>
        <span className="wall-update">Projection updated {formatWallTimestamp(board.updatedAt)} local</span>
        {error && <span className="inline-error">Refresh failed: {error}</span>}
      </div>
      {board.restrictions.length > 0 && (
        <div className="restriction-list">
          {board.restrictions.map((restriction) => <span key={restriction}>{restriction}</span>)}
        </div>
      )}
      <div className="arrival-table" role="table" aria-label="Live station arrivals">
        <div className="arrival-row arrival-row--head" role="row">
          <span role="columnheader">Service</span>
          <span role="columnheader">Destination / platform</span>
          <span role="columnheader">Prediction</span>
          <span role="columnheader">Crowding</span>
        </div>
        {board.arrivals.map((arrival) => {
          const ratio = arrival.load / Math.max(1, arrival.capacity);
          const destination = stationName(network, arrival.destination);
          const terminatesHere = arrival.destination === board.stationId;
          const publicPlatform = publicPlatformLabel(network, board.stationId, arrival.line, arrival.destination, arrival.platform);
          return (
            <div className="arrival-row" role="row" key={`${arrival.trainId}-${arrival.platform}-${arrival.destination}`}>
              <span role="cell">
                <strong className={`line-pill line-pill--${arrival.line.toLowerCase()}`}>{arrival.line}</strong>
                <small>{arrival.line === "AX" || arrival.service === "express" ? "Airport Express" : titleCase(arrival.service)}</small>
              </span>
              <span role="cell">
                <strong>{terminatesHere ? "Terminates here" : destination}</strong>
                <small>{terminatesHere ? `Arriving at ${destination}. No onward service.` : publicPlatform}</small>
              </span>
              <span role="cell">
                <strong>{formatEta(arrival.seconds)}</strong>
                <small>{titleCase(arrival.predictionKind)} · {titleCase(arrival.status)}</small>
              </span>
              <span role="cell">
                <span className={`load-meter ${ratio > 0.85 ? "critical" : ratio > 0.6 ? "busy" : ""}`}>
                  <i style={{ width: `${Math.min(100, ratio * 100)}%` }} />
                </span>
                <small>{arrival.load}/{arrival.capacity}</small>
              </span>
            </div>
          );
        })}
      </div>
      {board.arrivals.length === 0 && <div className="empty-state">No predicted arrivals are currently available.</div>}
    </div>
  );
}
