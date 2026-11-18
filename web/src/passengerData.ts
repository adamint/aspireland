import type { Incident, LineDefinition, NetworkDefinition, TrainState } from "./types";

export function publicTrainStatus(state: string): string {
  const labels: Record<string, string> = {
    dwelling: "At the station",
    travelling: "Between stations",
    turning: "Turning back",
    held: "Waiting at the station",
    uncertain: "Location unconfirmed",
    reserve: "Reserve train — not in service",
    withdrawn: "Not in service",
  };
  return labels[state] ?? "Status unavailable";
}

export function publicAlert(incident: Incident, network: NetworkDefinition, trains: TrainState[]) {
  const station = network.stations.find((item) => incident.target === item.id || incident.target.startsWith(`${item.id}:`));
  const endpoints = incident.target.split(">").map((id) => network.stations.find((item) => item.id === id));
  const [from, to] = endpoints;
  const train = trains.find((item) => item.id === incident.target);
  const line = network.lines.find((item) => item.id === (train?.line ?? incident.target));
  const location = station?.name ?? (endpoints.length === 2 && from && to
    ? `Between ${from.name} and ${to.name}` : line?.name ?? "Network");
  const messages: Record<string, [string, string]> = {
    "elevator-outage": ["Elevator unavailable", "A step-free path is affected. Check a step-free trip before travelling."],
    circulation: ["Station access restricted", "Some paths through the station are restricted."],
    "platform-closure": ["Platform closed", "A platform is closed. Check arrival updates before travelling."],
    "station-closure": ["Station closed", "Choose another station for your trip."],
    "segment-closure": ["Track section closed", "Service between these stations is affected. Check your route before travelling."],
    "door-fault": ["Train door problem", "This train may be delayed while its doors are checked."],
    "extended-dwell": ["Train stopped longer than usual", "Allow extra time for your trip."],
    "demand-surge": ["Busy station", "Allow extra time to enter and board."],
  };
  const [title, description] = messages[incident.kind] ?? ["Service alert", incident.description];
  return { location, title, description };
}

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
