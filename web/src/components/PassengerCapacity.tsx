import { publicOnboardCrowding } from "../passengerData";

export function PassengerCapacity({ load, capacity, stale }: { load: number; capacity: number; stale: boolean }) {
  const { ratio, label, detail } = publicOnboardCrowding(load, capacity, stale);
  if (ratio === null) return <small>{label}</small>;
  return (
    <span className={`load-meter ${ratio > 0.85 ? "critical" : ratio > 0.6 ? "busy" : ""}`}
      role="meter" aria-label="Onboard capacity" aria-valuemin={0} aria-valuemax={capacity}
      aria-valuenow={Math.min(load, capacity)}
      aria-valuetext={detail}>
      <i aria-hidden="true" style={{ width: `${Math.min(100, ratio * 100)}%` }} />
    </span>
  );
}
