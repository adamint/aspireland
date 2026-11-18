import type { LineDefinition, NetworkDefinition, StationDefinition } from "./types";

export function viewerTimeZone(): string {
  return Intl.DateTimeFormat().resolvedOptions().timeZone;
}

export function formatSimTimestamp(value: string, timeZone = viewerTimeZone()): string {
  return new Intl.DateTimeFormat("en-US", {
    weekday: "short",
    hour: "numeric",
    minute: "2-digit",
    timeZone,
    timeZoneName: "short",
  }).format(new Date(value));
}

export const formatClock = formatSimTimestamp;

export function formatWallTimestamp(value: string): string {
  return new Intl.DateTimeFormat("en-US", {
    hour: "numeric",
    minute: "2-digit",
    second: "2-digit",
    timeZone: viewerTimeZone(),
    timeZoneName: "short",
  }).format(new Date(value));
}

function localParts(value: Date, timeZone: string): Record<string, string> {
  const parts = new Intl.DateTimeFormat("en-CA", {
    timeZone,
    year: "numeric", month: "2-digit", day: "2-digit",
    hour: "2-digit", minute: "2-digit", second: "2-digit", hourCycle: "h23",
  }).formatToParts(value);
  return Object.fromEntries(parts.map(({ type, value: part }) => [type, part]));
}

export function formatDateTimeLocal(value: string, timeZone = viewerTimeZone()): string {
  const parts = localParts(new Date(value), timeZone);
  return `${parts.year}-${parts.month}-${parts.day}T${parts.hour}:${parts.minute}`;
}

export function parseSimulatedLocalInput(value: string, timeZone = viewerTimeZone()): string {
  const invalid = "Choose a valid local departure date and time.";
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2})?$/.test(value)) throw new Error(invalid);
  const wall = new Date(`${value.length === 16 ? `${value}:00` : value}Z`);
  if (!Number.isFinite(wall.getTime()) || wall.toISOString().slice(0, value.length) !== value) {
    throw new Error(invalid);
  }
  const matches = new Set<number>();
  // Sample both sides of daylight-saving changes, then verify the exact local time.
  for (const delta of [-36, 0, 36]) {
    const sample = new Date(wall.getTime() + delta * 3_600_000);
    const parts = localParts(sample, timeZone);
    const represented = Date.parse(`${parts.year}-${parts.month}-${parts.day}T${parts.hour}:${parts.minute}:${parts.second}Z`);
    const candidate = wall.getTime() - (represented - sample.getTime());
    const actual = localParts(new Date(candidate), timeZone);
    if (`${actual.year}-${actual.month}-${actual.day}T${actual.hour}:${actual.minute}:${actual.second}` === wall.toISOString().slice(0, 19)) {
      matches.add(candidate);
    }
  }
  if (matches.size === 0) throw new Error(`This departure time does not exist in ${timeZone}. Choose another time.`);
  if (matches.size > 1) throw new Error(`This departure time occurs twice in ${timeZone}. Choose an unambiguous time.`);
  return new Date([...matches][0]).toISOString();
}

export function formatDuration(seconds: number): string {
  if (!Number.isFinite(seconds)) return "Unknown";
  if (seconds < 60) return `${Math.max(0, Math.round(seconds))} sec`;
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes} min`;
  const hours = Math.floor(minutes / 60);
  const remainder = minutes % 60;
  return `${hours} hr ${remainder} min`;
}

export function formatEta(seconds?: number | null): string {
  if (seconds === null || seconds === undefined || !Number.isFinite(seconds)) return "ETA unknown";
  if (seconds <= 30) return "Arriving";
  return `${Math.max(1, Math.round(seconds / 60))} min`;
}

export function stationName(network: NetworkDefinition, id: string): string {
  return network.stations.find((station) => station.id === id)?.name ?? id;
}

export function lineFor(network: NetworkDefinition, id: string): LineDefinition | undefined {
  return network.lines.find((line) => line.id === id);
}

export function stationFor(network: NetworkDefinition, id: string): StationDefinition | undefined {
  return network.stations.find((station) => station.id === id);
}

const sharedTrunkStations = new Set([
  "west-junction",
  "civic-square",
  "central-exchange",
  "union-terminal",
  "east-junction",
]);

export function publicPlatformLabel(
  network: NetworkDefinition,
  stationId: string,
  line: string,
  destination: string,
  platform: string,
): string {
  const direction = platform.endsWith(":+") ? "+" : platform.endsWith(":-") ? "-" : null;
  const destinationName = stationName(network, destination);
  if (line === "AX") return `AX toward ${destinationName}`;
  if (sharedTrunkStations.has(stationId) && (line === "Blue" || line === "Amber")) {
    return `Shared trunk ${direction === "-" ? "westbound" : "eastbound"}`;
  }
  if (line === "Green") return `Green ${direction === "-" ? "northbound" : "southbound"} toward ${destinationName}`;
  if (line === "Amber") return `Amber local toward ${destinationName}`;
  if (line === "Blue") return `Blue toward ${destinationName}`;
  return `${line} toward ${destinationName}`;
}

export function titleCase(value: string): string {
  return value
    .replaceAll("-", " ")
    .replace(/\b\w/g, (letter) => letter.toUpperCase());
}

export function relativeFreshness(value: string): string {
  const seconds = Math.max(0, Math.round((Date.now() - new Date(value).getTime()) / 1000));
  if (seconds < 5) return "just now";
  if (seconds < 60) return `${seconds}s ago`;
  return `${Math.round(seconds / 60)}m ago`;
}
