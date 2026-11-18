// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { api } from "../api";
import type { NetworkDefinition, TrainState, WorldState } from "../types";
import { ActionWorkbench } from "./ActionWorkbench";

const stations = ["west-junction", "central-exchange", "east-junction", "southbank"];
const network: NetworkDefinition = {
  stations: stations.map((id) => ({ id, name: id.replaceAll("-", " "), x: 0, y: 0, character: "residential", accessPerMinute: 100, platformCapacity: 100, residential: 0, jobs: 0, education: 0, leisure: 0 })),
  lines: [
    { id: "Blue", name: "Blue Line", color: "#4b8ff5", stations: stations.slice(0, 3), travelSeconds: 120, headwaySeconds: 480 },
    { id: "Green", name: "Green Line", color: "#46b887", stations: ["central-exchange", "southbank"], travelSeconds: 120, headwaySeconds: 600 },
  ], accessEdges: [],
};
const baseTrain: TrainState = {
  id: "blue-1", line: "Blue", state: "dwelling", stationId: "west-junction", direction: 1,
  routeIndex: 0, capacity: 100, load: 10, version: 1, readyAt: "2026-11-17T08:00:00Z",
  depot: "west-junction", compatibleLines: ["Blue"],
};
const world: WorldState = {
  scenarioId: "scenario", generation: 1, version: 7, streamVersion: 7, seed: 1,
  simTime: "2026-11-17T08:00:00Z", updatedAt: "2026-11-17T13:00:00Z", paused: true, speed: 1, preset: "normal", authorityMode: "advise",
  trains: [baseTrain, { ...baseTrain, id: "reserve-green", line: "", state: "reserve", stationId: "southbank", depot: "southbank", compatibleLines: ["Green"] }],
  incidents: [], cohorts: [], history: [], blockOccupancy: {}, platformOccupancy: {}, junctionReservations: {}, grants: {}, commands: {}, headways: {}, flights: [], demandCursor: 0,
  metrics: { generated: 0, completed: 0, abandoned: 0, deniedBoardings: 0, waitingPassengerSeconds: 0, accessibilityFailures: 0, platformOverloads: 0 },
};

beforeEach(() => vi.spyOn(api, "action").mockResolvedValue({ accepted: true, status: "accepted", message: "Applied", version: 8 }));
afterEach(() => { cleanup(); vi.restoreAllMocks(); });

it.each([
  { kind: "incident", label: "Incident type", term: "Dwell time", explanation: /time.*stopped.*doors.*boarding/i },
  { kind: "set-headway", label: "Target headway (seconds)", term: "Headway floor", explanation: /minimum.*spacing.*not a timetable/i },
  { kind: "short-turn", label: "Turn back at", term: "Short-turn", explanation: /turn.*back before.*usual terminus/i },
])("explains $term with a native disclosure without changing the $label field name", ({ kind, label, term, explanation }) => {
  render(<ActionWorkbench world={world} network={network} refresh={async () => {}} />);
  fireEvent.change(screen.getByLabelText("Action"), { target: { value: kind } });

  const field = screen.getByLabelText(label);
  const summary = screen.getByText(term, { selector: "summary" });
  const disclosure = summary.closest("details")!;
  expect(summary.closest("label")).toBeNull();
  expect(disclosure.open).toBe(false);
  summary.focus();
  expect(document.activeElement).toBe(summary);
  fireEvent.click(summary);
  expect(disclosure.open).toBe(true);
  expect(screen.getByText(explanation)).toBeTruthy();
  expect(screen.getByLabelText(label)).toBe(field);
  expect(api.action).not.toHaveBeenCalled();
  fireEvent.click(summary);
  expect(disclosure.open).toBe(false);
  if (kind === "incident") {
    fireEvent.change(field, { target: { value: "door-fault" } });
    expect(screen.queryByText(term, { selector: "summary" })).toBeNull();
  }
});

it.each([
  { kind: "deploy-reserve", targetLabel: "Reserve train", target: "reserve-green", valueLabel: "Deploy on line", value: "Green" },
  { kind: "short-turn", targetLabel: "Service train", target: "blue-1", valueLabel: "Turn back at", value: "east-junction" },
])("provides valid native targets and values for $kind", async ({ kind, targetLabel, target, valueLabel, value }) => {
  render(<ActionWorkbench world={world} network={network} refresh={async () => {}} />);
  fireEvent.change(screen.getByLabelText("Action"), { target: { value: kind } });
  fireEvent.change(screen.getByLabelText(targetLabel), { target: { value: target } });
  fireEvent.change(screen.getByLabelText(valueLabel), { target: { value } });
  expect(screen.queryByLabelText(/Amount|Value/)).toBeNull();
  fireEvent.click(screen.getByRole("button", { name: "Apply action" }));
  await waitFor(() => expect(api.action).toHaveBeenCalledWith(
    { kind, target, value }, expect.objectContaining({ expectedVersion: 7 }),
  ));
});

it.each([
  { incident: "door-fault", targetLabel: "Affected train", target: "blue-1" },
  { incident: "elevator-outage", targetLabel: "Affected station", target: "central-exchange" },
  { incident: "segment-closure", targetLabel: "Affected segment", target: "west-junction>central-exchange" },
])("uses a correctly typed target for $incident", async ({ incident, targetLabel, target }) => {
  render(<ActionWorkbench world={world} network={network} refresh={async () => {}} />);
  fireEvent.change(screen.getByLabelText("Action"), { target: { value: "incident" } });
  fireEvent.change(screen.getByLabelText("Incident type"), { target: { value: incident } });
  const control = screen.getByLabelText(targetLabel);
  expect(control.tagName).toBe("SELECT");
  fireEvent.change(control, { target: { value: target } });
  fireEvent.click(screen.getByRole("button", { name: "Apply action" }));
  await waitFor(() => expect(api.action).toHaveBeenCalledWith(
    { kind: "incident", target, value: incident, amount: 600 }, expect.anything(),
  ));
});

it("does not silently retarget recovery after the selected incident disappears", () => {
  const incident = { id: "first", kind: "door-fault", target: "blue-1", state: "active", source: "manual", description: "Door fault", startedAt: world.simTime, severity: 1 };
  const suggested = { kind: "clear-incident", target: "first" };
  const view = render(<ActionWorkbench world={{ ...world, incidents: [incident] }} network={network} suggestedAction={suggested} refresh={async () => {}} />);
  view.rerender(<ActionWorkbench world={{ ...world, incidents: [{ ...incident, id: "second" }] }} network={network} suggestedAction={suggested} refresh={async () => {}} />);
  expect(screen.getByLabelText("Incident to clear")).toHaveProperty("value", "");
  expect(screen.getByRole("button", { name: "Apply action" })).toHaveProperty("disabled", true);
  expect(screen.getByText(/previous target is no longer eligible/)).toBeTruthy();
});

it("reports an accepted=false result as an error, not success", async () => {
  vi.mocked(api.action).mockResolvedValue({ accepted: false, status: "rejected", message: "Train cannot be held while travelling.", version: 7 });
  render(<ActionWorkbench world={world} network={network} refresh={async () => {}} />);
  fireEvent.click(screen.getByRole("button", { name: "Apply action" }));
  expect(await screen.findByRole("alert")).toHaveProperty("textContent", "Train cannot be held while travelling.");
});
