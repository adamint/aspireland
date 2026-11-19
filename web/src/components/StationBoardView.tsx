import { formatEta, formatLocalTime, formatSimTimestamp, formatWallTimestamp, publicPlatformLabel, stationName, titleCase } from "../format";
import type { NetworkDefinition, StationBoard } from "../types";
import { PassengerCapacity } from "./PassengerCapacity";

interface StationBoardViewProps {
  board: StationBoard | null;
  network: NetworkDefinition;
  scenarioId: string;
  generation: number;
  loading: boolean;
  error: string | null;
  stale?: boolean;
  compact?: boolean;
  passenger?: boolean;
}

export function StationBoardView({
  board,
  network,
  scenarioId,
  generation,
  loading,
  error,
  stale = false,
  compact = false,
  passenger = false,
}: StationBoardViewProps) {
  if (loading && !board) return <div className="loading-state"><span className="spinner" /> Loading arrivals…</div>;
  if (error && !board) return <div className="notice notice--error">{error}</div>;
  if (!board) return <div className="empty-state">Choose a station for arrival times.</div>;
  if (board.scenarioId !== scenarioId || board.generation !== generation) {
    return (
      <div className="loading-state" aria-live="polite">
        <span className="spinner" />
        Updating station arrivals…
        <small className="board-identity-note">Previous arrivals are hidden until current updates arrive.</small>
      </div>
    );
  }

  const unavailable = stale || board.stale || Boolean(error);
  return (
    <div className={`arrival-board ${compact ? "arrival-board--compact" : ""}`}>
      <div className="arrival-board-status">
        <span className={`freshness ${unavailable ? "freshness--stale" : ""}`}>
          {unavailable ? "Live arrivals unavailable" : `Arrival times as of ${passenger
            ? formatLocalTime(board.asOf, { date: "always" })
            : formatSimTimestamp(board.asOf)}`}
        </span>
        <span className="wall-update">Last updated {passenger
          ? formatLocalTime(board.updatedAt)
          : formatWallTimestamp(board.updatedAt)}</span>
      </div>
      {board.restrictions.length > 0 && (
        <div className="restriction-list">
          {board.restrictions.map((restriction) => <span key={restriction}>{restriction}</span>)}
        </div>
      )}
      <div className="arrival-table" role="table" aria-label="Station arrivals">
        <div className="arrival-row arrival-row--head" role="row">
          <span role="columnheader">Service</span>
          <span role="columnheader">Destination / platform</span>
          <span role="columnheader">Arrival</span>
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
                <strong>{terminatesHere ? "Last stop" : destination}</strong>
                <small>{terminatesHere ? `Arriving at ${destination}. This train ends here.` : publicPlatform}</small>
              </span>
              <span role="cell">
                <strong aria-label={unavailable ? "Arrival time unavailable" : undefined}>
                  {unavailable ? "—" : arrival.seconds == null ? "Time unavailable" : formatEta(arrival.seconds)}
                </strong>
                {!unavailable && <small>{arrival.predictionKind === "live" ? "Live update" : "Estimated"} · {titleCase(arrival.status)}</small>}
              </span>
              <span role="cell">
                {passenger ? <PassengerCapacity load={arrival.load} capacity={arrival.capacity} stale={unavailable} /> : <>
                  <span className={`load-meter ${ratio > 0.85 ? "critical" : ratio > 0.6 ? "busy" : ""}`}>
                    <i style={{ width: `${Math.min(100, ratio * 100)}%` }} />
                  </span>
                  <small>{arrival.load}/{arrival.capacity}</small>
                </>}
              </span>
            </div>
          );
        })}
      </div>
      {board.arrivals.length === 0 && !unavailable && <div className="empty-state">No arrival times are available. Check again shortly.</div>}
    </div>
  );
}
