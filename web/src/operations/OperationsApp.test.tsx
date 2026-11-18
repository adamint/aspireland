// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { OperationsApp } from "./OperationsApp";
import type { NetworkDefinition, WorldSnapshot, WorldState } from "../types";

const { useWorld, useCapabilities, useStationBoard, action, compare, refresh } = vi.hoisted(() => ({
  useWorld: vi.fn(),
  useCapabilities: vi.fn(),
  useStationBoard: vi.fn(),
  action: vi.fn(),
  compare: vi.fn(),
  refresh: vi.fn(),
}));
vi.mock("../api", () => ({ api: { action, compare } }));

vi.mock("../hooks", () => ({
  useWorld,
  useCapabilities,
  useStationBoard,
}));

const network: NetworkDefinition = {
  stations: [
    { id: "central-exchange", name: "Central Exchange", x: 100, y: 100, character: "business", accessPerMinute: 100, platformCapacity: 100, residential: 0, jobs: 1, education: 0, leisure: 0 },
  ],
  lines: [],
  accessEdges: [
    { stationId: "central-exchange", from: "concourse", to: "platform", seconds: 65, peoplePerMinute: 24, accessible: true, equipmentId: "central-exchange:elevator" },
  ],
};

const world: WorldState = {
  scenarioId: "scenario",
  generation: 1,
  seed: 1,
  streamVersion: 1,
  version: 1,
  simTime: "2026-11-17T08:00:00Z",
  updatedAt: "2026-11-17T13:00:00Z",
  paused: true,
  speed: 1,
  preset: "normal",
  authorityMode: "advise",
  trains: [],
  cohorts: [
    { id: "platform", origin: "central-exchange", destination: "elsewhere", stationId: "central-exchange", count: 7, accessible: false, state: "platform", createdAt: "2026-11-17T08:00:00Z", readyAt: "2026-11-17T08:00:00Z", patienceSeconds: 300, legs: [], legIndex: 0, returnScheduled: false },
    { id: "outside", origin: "central-exchange", destination: "elsewhere", stationId: "central-exchange", count: 3, accessible: false, state: "outside", createdAt: "2026-11-17T08:00:00Z", readyAt: "2026-11-17T08:00:00Z", patienceSeconds: 300, legs: [], legIndex: 0, returnScheduled: false },
    { id: "onboard", origin: "central-exchange", destination: "elsewhere", stationId: "central-exchange", count: 2, accessible: false, state: "onboard", trainId: "blue-1", createdAt: "2026-11-17T08:00:00Z", readyAt: "2026-11-17T08:00:00Z", patienceSeconds: 300, legs: [], legIndex: 0, returnScheduled: false },
    { id: "transfer", origin: "central-exchange", destination: "elsewhere", stationId: "central-exchange", count: 4, accessible: false, state: "transfer", createdAt: "2026-11-17T08:00:00Z", readyAt: "2026-11-17T08:00:00Z", patienceSeconds: 300, legs: [], legIndex: 0, returnScheduled: false },
    { id: "retired", origin: "central-exchange", destination: "elsewhere", stationId: "central-exchange", count: 5, accessible: false, state: "completed", createdAt: "2026-11-17T08:00:00Z", readyAt: "2026-11-17T08:00:00Z", patienceSeconds: 300, legs: [], legIndex: 0, returnScheduled: false },
    { id: "scheduled-return", origin: "elsewhere", destination: "central-exchange", stationId: "central-exchange", count: 80, accessible: false, state: "outside", createdAt: "2026-11-17T08:00:00Z", readyAt: "2026-11-17T12:00:00Z", patienceSeconds: 300, legs: [], legIndex: 0, returnAt: "2026-11-17T12:00:00Z", returnScheduled: true },
    { id: "walking-transfer", origin: "elsewhere", destination: "central-exchange", stationId: "central-exchange", count: 30, accessible: false, state: "transfer", createdAt: "2026-11-17T08:00:00Z", readyAt: "2026-11-17T08:02:00Z", patienceSeconds: 300, legs: [], legIndex: 0, returnScheduled: false },
  ],
  incidents: [{
    id: "restored-elevator",
    kind: "elevator-outage",
    target: "central-exchange:elevator",
    state: "cleared",
    source: "manual",
    description: "Elevator restored",
    startedAt: "2026-11-17T07:30:00Z",
    severity: 1,
  }],
  history: [],
  blockOccupancy: {},
  platformOccupancy: {},
  junctionReservations: {},
  grants: {},
  commands: {},
  headways: {},
  metrics: { generated: 21, completed: 5, abandoned: 0, deniedBoardings: 0, waitingPassengerSeconds: 0, accessibilityFailures: 0, platformOverloads: 0 },
  flights: [],
  demandCursor: 0,
};

beforeEach(() => {
  const snapshot: WorldSnapshot = { world, network, stale: false };
  useWorld.mockReturnValue({ data: snapshot, error: null, loading: false, lastSuccess: new Date(), refresh });
  useCapabilities.mockReturnValue({ data: null, error: null, loading: false, lastSuccess: null });
  useStationBoard.mockReturnValue({ data: null, error: null, loading: false, lastSuccess: null });
  action.mockResolvedValue({ accepted: true, status: "accepted", message: "Incident cleared.", version: 2 });
  refresh.mockResolvedValue(undefined);
});

it("exposes active incident context and clears the selected incident from the dispatch desk", async () => {
  const active = { ...world.incidents[0], id: "broken-lift", state: "active", description: "Elevator unavailable" };
  useWorld.mockReturnValue({ data: { world: { ...world, incidents: [active] }, network, stale: false }, refresh, error: null, loading: false });
  render(<OperationsApp />);
  const queue = screen.getByRole("region", { name: "Incident and exception queue" });
  fireEvent.click(within(queue).getByRole("button", { name: /Elevator Outage.*Central Exchange/s }));
  const controls = screen.getByRole("region", { name: "Authorized service controls" });
  expect(within(controls).getByLabelText("Action")).toHaveProperty("value", "clear-incident");
  expect(within(controls).getByLabelText("Incident to clear")).toHaveProperty("value", "broken-lift");
  fireEvent.click(within(controls).getByRole("button", { name: "Apply action" }));
  await waitFor(() => expect(action).toHaveBeenCalledWith(
    { kind: "clear-incident", target: "broken-lift" },
    { scenarioId: "scenario", generation: 1, expectedVersion: 1, reason: "Manual recovery action" },
  ));
  expect(await screen.findByRole("status")).toHaveProperty("textContent", "Incident cleared.");
});

it("uses a real line target and preserves the full authorized manual headway range", async () => {
  const lines = [
    { id: "Blue", name: "Blue Line", color: "#4b8ff5", stations: ["central-exchange"], travelSeconds: 120, headwaySeconds: 480 },
    { id: "Green", name: "Green Line", color: "#46b887", stations: ["central-exchange"], travelSeconds: 120, headwaySeconds: 600 },
  ];
  useWorld.mockReturnValue({ data: { world, network: { ...network, lines }, stale: false }, refresh, error: null, loading: false });
  render(<OperationsApp />);
  fireEvent.change(screen.getByLabelText("Action"), { target: { value: "set-headway" } });
  fireEvent.change(screen.getByLabelText("Service line"), { target: { value: "Green" } });
  fireEvent.change(screen.getByLabelText("Target headway (seconds)"), { target: { value: "3600" } });
  fireEvent.click(screen.getByRole("button", { name: "Compare 30 min" }));
  await waitFor(() => expect(compare).toHaveBeenCalledWith({ kind: "set-headway", target: "Green", amount: 3600 }, "scenario", 1));
  fireEvent.click(screen.getByRole("button", { name: "Apply action" }));
  await waitFor(() => expect(action).toHaveBeenCalledWith(
    { kind: "set-headway", target: "Green", amount: 3600 },
    expect.objectContaining({ expectedVersion: 1, scenarioId: "scenario", generation: 1 }),
  ));
});

it("keeps empty queues actionable and reports authoritative rejection without hiding the controls", async () => {
  render(<OperationsApp />);
  expect(screen.getByText(/No active incidents, held trains or uncertain workers/)).toBeTruthy();
  expect(screen.getByText(/No trains are registered/)).toBeTruthy();
  fireEvent.change(screen.getByLabelText("Action"), { target: { value: "clear-incident" } });
  expect(screen.getByRole("button", { name: "Apply action" })).toHaveProperty("disabled", true);
  expect(screen.getByText(/No eligible incidents/)).toBeTruthy();

  cleanup();
  action.mockRejectedValue(new Error("Scenario version changed. Refresh and retry."));
  useWorld.mockReturnValue({ data: { world: { ...world, incidents: [{ ...world.incidents[0], state: "active" }] }, network, stale: true }, refresh, error: "Projection disconnected", loading: false });
  render(<OperationsApp />);
  expect(screen.getByRole("button", { name: "Retry network refresh" })).toBeTruthy();
  fireEvent.click(screen.getByRole("button", { name: "Apply action" }));
  expect(await screen.findByRole("alert")).toHaveProperty("textContent", "Scenario version changed. Refresh and retry.");
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

it("keeps transfers separate from the waiting queue", () => {
  const view = render(<OperationsApp />);
  const networkWaiting = view.container.querySelector(".ops-metrics-strip .ops-metric:nth-child(2)");
  expect(networkWaiting).not.toBeNull();
  expect(within(networkWaiting as HTMLElement).getByText("10")).toBeTruthy();

  fireEvent.click(screen.getByRole("tab", { name: "Station" }));
  const stationMetrics = view.container.querySelectorAll(".station-metric-grid .ops-metric");
  expect(within(stationMetrics[0] as HTMLElement).getByText("10")).toBeTruthy();
  expect(within(stationMetrics[3] as HTMLElement).getByText("34")).toBeTruthy();
  expect(screen.getByText("Open")).toBeTruthy();

  fireEvent.click(screen.getByRole("tab", { name: "Operations" }));
  expect(screen.getByText("No incidents at this station.")).toBeTruthy();
  expect(screen.queryByText("Elevator restored")).toBeNull();
});
