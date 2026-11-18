// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { PassengerApp } from "./PassengerApp";
import type { NetworkDefinition, TrainState, WorldSnapshot, WorldState } from "../types";

const { useWorld, useCapabilities, useStationBoard, journey } = vi.hoisted(() => ({
  useWorld: vi.fn(),
  useCapabilities: vi.fn(),
  useStationBoard: vi.fn(),
  journey: vi.fn(),
}));

vi.mock("../hooks", () => ({
  useWorld,
  useCapabilities,
  useStationBoard,
}));

vi.mock("../api", () => ({
  api: { journey },
}));

const network: NetworkDefinition = {
  stations: [
    { id: "westhaven", name: "Westhaven", x: 100, y: 100, character: "residential", accessPerMinute: 100, platformCapacity: 100, residential: 1, jobs: 0, education: 0, leisure: 0 },
    { id: "old-town", name: "Old Town", x: 200, y: 100, character: "historic", accessPerMinute: 100, platformCapacity: 100, residential: 1, jobs: 1, education: 0, leisure: 1 },
    { id: "central-exchange", name: "Central Exchange", x: 300, y: 100, character: "business", accessPerMinute: 100, platformCapacity: 100, residential: 0, jobs: 1, education: 0, leisure: 0 },
    { id: "university", name: "University", x: 400, y: 100, character: "campus", accessPerMinute: 100, platformCapacity: 100, residential: 0, jobs: 0, education: 1, leisure: 0 },
  ],
  lines: [
    { id: "Blue", name: "Blue Line", color: "#4b8ff5", stations: ["westhaven", "old-town", "central-exchange"], travelSeconds: 120, headwaySeconds: 480 },
    { id: "Amber", name: "Amber Line", color: "#f0a53a", stations: ["westhaven", "old-town", "university"], travelSeconds: 120, headwaySeconds: 480 },
    { id: "Green", name: "Green Line", color: "#46b887", stations: ["old-town", "university"], travelSeconds: 120, headwaySeconds: 480 },
  ],
  accessEdges: [
    { stationId: "central-exchange", from: "concourse", to: "platform", seconds: 65, peoplePerMinute: 24, accessible: true, equipmentId: "central-exchange:elevator" },
  ],
};

const baseWorld: WorldState = {
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
  cohorts: [],
  incidents: [],
  history: [],
  blockOccupancy: {},
  platformOccupancy: {},
  junctionReservations: {},
  grants: {},
  commands: {},
  headways: {},
  metrics: { generated: 0, completed: 0, abandoned: 0, deniedBoardings: 0, waitingPassengerSeconds: 0, accessibilityFailures: 0, platformOverloads: 0 },
  flights: [],
  demandCursor: 0,
};

const train: TrainState = {
  id: "blue-1",
  line: "Blue",
  state: "travelling",
  stationId: "westhaven",
  nextStationId: "central-exchange",
  direction: 1,
  routeIndex: 0,
  capacity: 100,
  load: 60,
  version: 1,
  readyAt: baseWorld.simTime,
  depot: "west",
  compatibleLines: ["Blue"],
};

function showWorld(world: WorldState, stale = false, error: string | null = null) {
  const snapshot: WorldSnapshot = { world, network, stale };
  useWorld.mockReturnValue({ data: snapshot, error, loading: false, lastSuccess: new Date(), refresh: vi.fn() });
}

beforeEach(() => {
  window.history.replaceState({}, "", "/");
  showWorld(baseWorld);
  useCapabilities.mockReturnValue({ data: null, error: null, loading: false, lastSuccess: null });
  useStationBoard.mockReturnValue({ data: null, error: null, loading: false, lastSuccess: null });
  journey.mockResolvedValue({
    scenarioId: "scenario",
    generation: 1,
    version: 1,
    asOf: baseWorld.simTime,
    request: { origin: "westhaven", destination: "university" },
    options: [],
  });
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
  vi.unstubAllEnvs();
});

it("announces loading in the main content area, then shows the trip planner", () => {
  useWorld.mockReturnValue({ data: null, error: null, loading: true, lastSuccess: null, refresh: vi.fn() });
  const view = render(<PassengerApp />);
  const status = screen.getByRole("status");
  expect(status.textContent).toBe("Loading service information…");
  expect(screen.getByRole("main").contains(status)).toBe(true);
  expect(screen.queryByRole("button", { name: "Find a route" })).toBeNull();

  showWorld(baseWorld);
  view.rerender(<PassengerApp />);
  expect(screen.queryByRole("status")).toBeNull();
  expect(screen.getByRole("button", { name: "Find a route" })).toBeTruthy();
});

it.each(["westhaven>old-town", "old-town>westhaven"])(
  "associates shared adjacent segment closure %s with both affected lines",
  (target) => {
  showWorld({
    ...baseWorld,
    incidents: [{
      id: "segment",
      kind: "segment-closure",
      target,
      state: "active",
      source: "manual",
      description: "Shared trunk closed",
      startedAt: baseWorld.simTime,
      severity: 1,
    }],
  });

  render(<PassengerApp />);

  expect(within(screen.getByText("Blue Line").closest("article") as HTMLElement).getByText("Service alert")).toBeTruthy();
  expect(within(screen.getByText("Amber Line").closest("article") as HTMLElement).getByText("Service alert")).toBeTruthy();
  expect(within(screen.getByText("Green Line").closest("article") as HTMLElement).getByText("No service alerts")).toBeTruthy();
  },
);

it("ignores cleared station incidents and restored equipment", () => {
  window.history.replaceState({}, "", "/stations/central-exchange");
  showWorld({
    ...baseWorld,
    incidents: [{
      id: "elevator",
      kind: "elevator-outage",
      target: "central-exchange:elevator",
      state: "cleared",
      source: "manual",
      description: "Elevator restored",
      startedAt: baseWorld.simTime,
      severity: 1,
    }],
  });

  render(<PassengerApp />);

  expect(screen.queryByText(/active alert/)).toBeNull();
  expect(screen.getByText("Available")).toBeTruthy();
  expect(screen.queryByText("Out")).toBeNull();
});

it("converts leave-later values from the viewer's local zone to UTC", async () => {
  vi.stubEnv("TZ", "America/New_York");
  const resolvedOptions = Intl.DateTimeFormat.prototype.resolvedOptions;
  const zone = vi.spyOn(Intl.DateTimeFormat.prototype, "resolvedOptions").mockImplementation(function (this: Intl.DateTimeFormat) {
    return { ...resolvedOptions.call(this), timeZone: "America/New_York" };
  });
  render(<PassengerApp />);

  fireEvent.click(screen.getByRole("button", { name: "Leave later" }));
  expect(screen.getByLabelText("Departure date and time")).toHaveProperty("value", "2026-11-17T03:00");
  fireEvent.change(screen.getByLabelText("Departure date and time"), { target: { value: "2026-11-17T08:00" } });
  fireEvent.click(screen.getByRole("button", { name: "Find a route" }));

  await waitFor(() => expect(journey).toHaveBeenCalled());
  expect(journey.mock.calls[0][0].departureTime).toBe("2026-11-17T13:00:00.000Z");
  zone.mockRestore();
});

it("shows invalid local departure errors instead of submitting a normalized date", async () => {
  render(<PassengerApp />);
  fireEvent.click(screen.getByRole("button", { name: "Leave later" }));
  const input = screen.getByLabelText(/Departure date and time/);
  fireEvent.change(input, { target: { value: "" } });
  fireEvent.submit(input.closest("form")!);
  expect(await screen.findByRole("alert")).toHaveProperty("textContent", expect.stringMatching(/valid.*departure/i));
  expect(journey).not.toHaveBeenCalled();
});

it("opens read-only train details and restores arrivals when the same station is selected", () => {
  showWorld({ ...baseWorld, trains: [train] });
  render(<PassengerApp />);

  fireEvent.click(screen.getByRole("button", { name: /^blue-1,/ }));

  const details = screen.getByRole("complementary", { name: "Map details" });
  expect(within(details).getByRole("heading", { name: "blue-1" })).toBeTruthy();
  expect(within(details).getByRole("link", { name: "Blue Line status and stops" })).toBeTruthy();
  expect(within(details).getByText("Between stations")).toBeTruthy();
  expect(within(details).getByText("Departed")).toBeTruthy();
  expect(within(details).getByText("Westhaven")).toBeTruthy();
  expect(within(details).getByText("Next station")).toBeTruthy();
  expect(within(details).getByText("Central Exchange")).toBeTruthy();
  expect(within(details).getByText("60 / 100 passengers · 60% full")).toBeTruthy();
  expect(within(details).queryByText(/\bETA\b|arriv.*min|\bhold\b|release|withdraw|deploy/i)).toBeNull();
  expect(screen.getByRole("button", { name: /^blue-1,/ }).getAttribute("aria-pressed")).toBe("true");

  fireEvent.click(screen.getByRole("button", { name: "Central Exchange station" }));

  expect(within(details).getByText("Arrivals")).toBeTruthy();
  expect(within(details).getByRole("heading", { name: "Central Exchange" })).toBeTruthy();
  expect(within(details).queryByRole("heading", { name: "blue-1" })).toBeNull();
  expect(useStationBoard).toHaveBeenLastCalledWith("central-exchange");
  expect(screen.getByRole("button", { name: /^blue-1,/ }).getAttribute("aria-pressed")).toBe("false");
});

it("keeps keyboard-selected train details current without inventing unknown locations", () => {
  showWorld({ ...baseWorld, trains: [train] });
  const view = render(<PassengerApp />);
  const marker = screen.getByRole("button", { name: /^blue-1,/ });
  marker.focus();
  fireEvent.keyDown(marker, { key: "Enter" });

  const details = screen.getByRole("complementary", { name: "Map details" });
  showWorld({ ...baseWorld, version: 2, trains: [{ ...train, nextStationId: null, capacity: 0 }] });
  view.rerender(<PassengerApp />);
  expect(document.activeElement).toBe(marker);
  expect(marker.getAttribute("aria-label")).toContain("capacity unknown");

  showWorld({ ...baseWorld, version: 3, trains: [{ ...train, stationId: "unknown", nextStationId: null, capacity: 0 }] });
  view.rerender(<PassengerApp />);
  expect(within(details).getAllByText("Not reported")).toHaveLength(2);
  expect(within(details).getByText("60 passengers · capacity unknown")).toBeTruthy();
  expect(details.textContent).not.toContain("unknown%");

  showWorld({ ...baseWorld, version: 4, trains: [{ ...train, state: "held", stationId: "central-exchange", nextStationId: null, load: 20 }] });
  view.rerender(<PassengerApp />);
  expect(within(details).getByText("Waiting at the station")).toBeTruthy();
  expect(within(details).getByText("Current station")).toBeTruthy();
  expect(within(details).getByText("20 / 100 passengers · 20% full")).toBeTruthy();
  fireEvent.click(within(details).getByRole("button", { name: /Back to Central Exchange arrivals/ }));
  expect(within(details).getByText("Arrivals")).toBeTruthy();
});

it("does not offer dead train buttons on a station mini-map", () => {
  window.history.replaceState({}, "", "/stations/central-exchange");
  showWorld({ ...baseWorld, trains: [train] });
  const view = render(<PassengerApp />);

  expect(screen.queryByRole("button", { name: /^blue-1,/ })).toBeNull();
  const marker = view.container.querySelector(".map-train")!;
  expect(marker.getAttribute("tabindex")).toBeNull();
  expect(marker.getAttribute("aria-hidden")).toBe("true");
  expect(screen.getByRole("button", { name: "Central Exchange station" })).toBeTruthy();
});

it("uses rider language and links a stopped train to its line status and stops", () => {
  showWorld({ ...baseWorld, trains: [{ ...train, state: "dwelling" }] });
  const view = render(<PassengerApp />);

  expect(screen.getByRole("checkbox", { name: "Step-free trip" })).toBeTruthy();
  expect(screen.getByText("Choose “Step-free trip” to avoid stairs.")).toBeTruthy();
  expect(screen.getByText("No access alerts reported")).toBeTruthy();
  fireEvent.click(screen.getByRole("button", { name: /^blue-1,/ }));

  const details = screen.getByRole("complementary", { name: "Map details" });
  expect(within(details).getByText("At the station")).toBeTruthy();
  expect(screen.getByRole("button", { name: /^blue-1,/ }).getAttribute("aria-label")).toContain("At the station");
  expect(within(screen.getByRole("list", { name: "Trains", hidden: true })).getByText(/At the station/)).toBeTruthy();
  const lineLink = within(details).getByRole("link", { name: "Blue Line status and stops" });
  expect(lineLink.getAttribute("href")).toBe("#line-blue");
  const lineStatus = document.getElementById("line-blue")!;
  expect(lineStatus.getAttribute("tabindex")).toBe("-1");
  expect(within(lineStatus).getByText("Stops")).toBeTruthy();
  expect(within(lineStatus).getByRole("link", { name: "Old Town" }).getAttribute("href")).toBe("/stations/old-town");
  expect(screen.queryByText(/modeled|ordinary journey|planner-grounded|Dwelling/i)).toBeNull();
  expect(view.container.textContent).not.toMatch(/simulat|synthetic/i);
});

it.each([
  [true, null],
  [false, "Updates unavailable"],
] as const)("does not announce clear service or access when updates are delayed (%s, %s)", (stale, error) => {
  showWorld(baseWorld, stale, error);
  render(<PassengerApp />);

  expect(screen.queryByText("No access alerts reported")).toBeNull();
  expect(screen.queryByText(/Good service|No service alerts|No current disruptions|paths are available/i)).toBeNull();
  expect(screen.getByText("Access status unavailable")).toBeTruthy();
  expect(screen.getAllByText(/Last updated/).length).toBeGreaterThan(0);
});

it("does not label old station access as open or available", () => {
  window.history.replaceState({}, "", "/stations/central-exchange");
  showWorld(baseWorld, true);
  render(<PassengerApp />);
  const access = screen.getByRole("heading", { name: "Elevator and station access" }).closest("aside")!;
  expect(within(access).getByText("Status unavailable")).toBeTruthy();
  expect(within(access).queryByText(/^(Open|Available)$/)).toBeNull();
});

it("clearly identifies reserve trains as not in service", () => {
  showWorld({ ...baseWorld, trains: [{ ...train, id: "reserve-ba", state: "reserve", line: "", nextStationId: null }] });
  render(<PassengerApp />);
  fireEvent.click(screen.getByRole("button", { name: /^reserve-ba,/ }));
  const details = screen.getByRole("complementary", { name: "Map details" });
  expect(within(details).getByText("Reserve train — not in service")).toBeTruthy();
  expect(within(details).queryByRole("link", { name: /status and stops/ })).toBeNull();
});
