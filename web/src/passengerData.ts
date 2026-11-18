import type { Incident, LineDefinition, TrainState } from "./types";

function lineContainsSegment(line: LineDefinition, target: string): boolean {
  const endpoints = target.split(">");
  if (endpoints.length !== 2 || endpoints.some((endpoint) => !endpoint)) return false;
  const [from, to] = endpoints;
  return line.stations.some((station, index) => {
    const next = line.stations[index + 1];
    return (station === from && next === to) || (station === to && next === from);
  });
}

export function lineIncidents(line: LineDefinition, trains: TrainState[], incidents: Incident[]): Incident[] {
  const trainIds = new Set(trains.filter((train) => train.line === line.id).map((train) => train.id));
  return incidents.filter(
    (incident) =>
      incident.state === "active" &&
      (line.stations.some((station) => incident.target === station || incident.target.startsWith(`${station}:`)) ||
        trainIds.has(incident.target) ||
        incident.target.includes(`${line.id}:`) ||
        incident.target === line.id ||
        (incident.kind === "segment-closure" && lineContainsSegment(line, incident.target))),
  );
}

export function activeStationIncidents(incidents: Incident[], stationId: string): Incident[] {
  return incidents.filter(
    (incident) =>
      incident.state === "active" &&
      (incident.target === stationId || incident.target.startsWith(`${stationId}:`)),
  );
}
