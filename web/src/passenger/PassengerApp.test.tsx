// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { PassengerApp } from "./PassengerApp";
import { formatPassengerDateTime, formatSimTimestamp } from "../format";
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
    { id: "Blue", name: "Blue Line", color: "#4b8ff5", stations: ["westhaven", "old-town", "central-exchange"], travelSeconds: 120, segmentTravelSeconds: [90, 180], headwaySeconds: 480 },
    { id: "Amber", name: "Amber Line", color: "#f0a53a", stations: ["westhaven", "old-town", "university"], travelSeconds: 120, segmentTravelSeconds: [540, 480], headwaySeconds: 480 },
    { id: "Green", name: "Green Line", color: "#46b887", stations: ["old-town", "university"], travelSeconds: 120, segmentTravelSeconds: [135], headwaySeconds: 480 },
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

it("starts home without a station or arrivals, then loads them after explicit selection", () => {
  useStationBoard.mockReturnValue({
    data: {
      stationId: "central-exchange",
      scenarioId: "scenario",
      generation: 1,
      asOf: baseWorld.simTime,
      updatedAt: baseWorld.updatedAt,
      stale: false,
      arrivals: [],
      restrictions: [],
    },
    error: null,
    loading: false,
    lastSuccess: new Date(),
  });

  render(<PassengerApp />);

  expect(useStationBoard).toHaveBeenLastCalledWith(null);
  const details = screen.getByRole("complementary", { name: "Map details" });
  expect(within(details).getByRole("heading", { name: "Select a station" })).toBeTruthy();
  expect(within(details).queryByText("Arrival times as of")).toBeNull();

  fireEvent.click(screen.getByRole("button", { name: "Central Exchange station" }));

  expect(useStationBoard).toHaveBeenLastCalledWith("central-exchange");
  expect(within(details).getByRole("heading", { name: "Central Exchange" })).toBeTruthy();
  expect(within(details).getByText(/Arrival times as of/)).toBeTruthy();
});

it("preserves canonical station deep links and loads their arrivals immediately", () => {
  window.history.replaceState({}, "", "/stations/central-exchange");
  render(<PassengerApp />);
  expect(useStationBoard).toHaveBeenLastCalledWith("central-exchange");
  expect(screen.getByRole("heading", { name: "Central Exchange" })).toBeTruthy();
});

it.each(["/", "/stations/central-exchange"])("shows local simulation time on %s with a distinct stale marker", (path) => {
  window.history.replaceState({}, "", path);
  showWorld(baseWorld, true);

  const view = render(<PassengerApp />);
  const banner = view.container.querySelector(".public-sim-banner")!;
  expect(within(banner as HTMLElement).getByText("Simulation time")).toBeTruthy();
  expect(within(banner as HTMLElement).getByText(formatSimTimestamp(baseWorld.simTime))).toBeTruthy();
  expect(banner.querySelector("i.stale")).toBeTruthy();
});

it.each([false, true])("keeps trip help independent of transcription availability (%s)", (transcription) => {
  useCapabilities.mockReturnValue({
    data: { text: true, transcription, speech: true },
    error: null,
    loading: false,
    lastSuccess: new Date(),
  });

  render(<PassengerApp />);

  expect(screen.getByRole("heading", { name: "Signal" })).toBeTruthy();
  expect(screen.getByText("Type a question about routes or arrivals.")).toBeTruthy();
  expect(screen.queryByText(/Push to talk/i)).toBeNull();
});

it("tucks the NDC Porto greeting into a footer postcard", () => {
  render(<PassengerApp />);

  const footer = screen.getByRole("contentinfo");
  const summary = within(footer).getByText("Postcard from Porto");
  const postcard = summary.closest("details")!;
  expect(postcard.hasAttribute("open")).toBe(false);
  fireEvent.click(summary);
  expect(postcard.hasAttribute("open")).toBe(true);
  expect(within(postcard).getByText("Olá, NDC Porto!").getAttribute("lang")).toBe("pt-PT");
});

const serviceIncident = {
  id: "trunk-closure",
  kind: "segment-closure",
  target: "westhaven>old-town",
  state: "active",
  source: "manual",
  description: "Shared track closed",
  startedAt: baseWorld.simTime,
  severity: 1,
};

it("puts active service alerts above the home header without a duplicate lower summary", () => {
  showWorld({ ...baseWorld, incidents: [serviceIncident, { ...serviceIncident, id: "cleared", state: "cleared" }] });
  render(<PassengerApp />);

  const alerts = screen.getByRole("region", { name: "Service alerts" });
  const header = screen.getByRole("navigation", { name: "Passenger navigation" }).closest("header")!;
  expect(alerts.compareDocumentPosition(header) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  expect(within(alerts).getByText("1 service alert")).toBeTruthy();
  expect(within(alerts).getByText("Between Westhaven and Old Town — Track section closed")).toBeTruthy();
  expect(within(alerts).getByText("Service between these stations is affected. Check your route before travelling.")).toBeTruthy();
  expect(screen.getAllByText("Between Westhaven and Old Town — Track section closed")).toHaveLength(1);
  expect(screen.queryByRole("region", { name: "Service summary" })).toBeNull();
  expect(screen.queryByText("Travel date and time")).toBeNull();
  expect(screen.getByText("Leave now at")).toBeTruthy();
  fireEvent.click(screen.getByRole("button", { name: "Leave later" }));
  expect(screen.getByLabelText("Departure date and time")).toBeTruthy();
});

it.each([[true, null], [false, "Refresh failed"]] as const)("does not present old top alerts as current (%s, %s)", (stale, error) => {
  showWorld({ ...baseWorld, incidents: [serviceIncident] }, stale, error);
  render(<PassengerApp />);

  const alerts = screen.getByRole("region", { name: "Service alerts" });
  expect(within(alerts).getByText("Live service alerts unavailable")).toBeTruthy();
  expect(within(alerts).getByText(/Service updates are delayed\. Last updated/)).toBeTruthy();
  expect(within(alerts).queryByText("1 service alert")).toBeNull();
  expect(within(alerts).queryByText(/Track section closed/)).toBeNull();
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

it("opens and focuses the relevant shared alert from the correct line card", () => {
  const greenIncident = {
    ...serviceIncident,
    id: "green-access",
    kind: "station-closure",
    target: "university",
    description: "University closed",
    endsAt: "2026-11-17T09:00:00Z",
  };
  showWorld({ ...baseWorld, incidents: [serviceIncident, greenIncident] });
  const scrollIntoView = vi.fn();
  HTMLElement.prototype.scrollIntoView = scrollIntoView;
  render(<PassengerApp />);

  const greenCard = screen.getByText("Green Line").closest("article") as HTMLElement;
  const button = within(greenCard).getByRole("button", { name: "View Green Line service alert" });
  expect(button.tagName).toBe("BUTTON");
  fireEvent.click(button);

  const target = screen.getByText("University — Station closed").closest("li")!;
  expect(target.closest("details")?.hasAttribute("open")).toBe(true);
  expect(document.activeElement).toBe(target);
  expect(scrollIntoView).toHaveBeenCalledExactlyOnceWith({ behavior: "smooth", block: "center" });
  expect(within(target).getByText(`Restoration expected ${formatPassengerDateTime(greenIncident.endsAt)}.`)).toBeTruthy();
  expect(within(screen.getByText("Blue Line").closest("article") as HTMLElement)
    .getByRole("button", { name: "View Blue Line service alert" })).toBeTruthy();
  expect(within(screen.getByText("Amber Line").closest("article") as HTMLElement)
    .getByRole("button", { name: "View Amber Line service alert" })).toBeTruthy();
});

it("shows running time from the previous stop without presenting it as a wait", () => {
  render(<PassengerApp />);
  const blue = screen.getByText("Blue Line").closest("article") as HTMLElement;
  fireEvent.click(within(blue).getByText("Stops"));
  const stops = within(blue).getAllByRole("listitem");

  expect(stops[0].textContent).toBe("Westhaven");
  expect(stops[1].textContent).toContain("2 min from previous stop");
  expect(stops[2].textContent).toContain("3 min from previous stop");
  expect(within(blue).queryByText(/wait/i)).toBeNull();
});

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
  expect(within(details).getByText("Onboard crowding")).toBeTruthy();
  expect(within(details).queryByText("Space available")).toBeNull();
  expect(within(details).getByRole("meter", { name: "Onboard capacity" }).getAttribute("aria-valuetext")).toBe("60 of 100 passenger capacity");
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
  fireEvent.click(screen.getByRole("button", { name: "Central Exchange station" }));
  const marker = screen.getByRole("button", { name: /^blue-1,/ });
  marker.focus();
  fireEvent.keyDown(marker, { key: "Enter" });

  const details = screen.getByRole("complementary", { name: "Map details" });
  showWorld({ ...baseWorld, version: 2, trains: [{ ...train, nextStationId: null, capacity: 0 }] });
  view.rerender(<PassengerApp />);
  expect(document.activeElement).toBe(marker);
  expect(marker.getAttribute("aria-label")).toContain("Capacity not reported");

  showWorld({ ...baseWorld, version: 3, trains: [{ ...train, stationId: "unknown", nextStationId: null, capacity: 0 }] });
  view.rerender(<PassengerApp />);
  expect(within(details).getAllByText("Not reported")).toHaveLength(2);
  expect(within(details).getByText("Capacity not reported")).toBeTruthy();
  expect(within(details).queryByRole("meter")).toBeNull();
  expect(details.textContent).not.toContain("unknown%");

  showWorld({ ...baseWorld, version: 4, trains: [{ ...train, state: "held", stationId: "central-exchange", nextStationId: null, load: 20 }] });
  view.rerender(<PassengerApp />);
  expect(within(details).getByText("Waiting at the station")).toBeTruthy();
  expect(within(details).getByText("Current station")).toBeTruthy();
  expect(within(details).getByRole("meter", { name: "Onboard capacity" }).getAttribute("aria-valuetext")).toBe("20 of 100 passenger capacity");
  fireEvent.click(within(details).getByRole("button", { name: /Back to Central Exchange arrivals/ }));
  expect(within(details).getByText("Arrivals")).toBeTruthy();
});

it.each([[true, null], [false, "Refresh failed"]] as const)("does not score old train crowding (%s, %s)", (stale, error) => {
  showWorld({ ...baseWorld, trains: [train] }, stale, error);
  render(<PassengerApp />);
  fireEvent.click(screen.getByRole("button", { name: /^blue-1,/ }));
  const details = screen.getByRole("complementary", { name: "Map details" });
  expect(within(details).getByText("Live crowding unavailable")).toBeTruthy();
  expect(within(details).queryByText("Space available")).toBeNull();
  expect(within(details).queryByRole("meter")).toBeNull();
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
  expect(screen.getByText(/free on-demand ride to a station/i)).toBeTruthy();
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
  expect(view.container.textContent).not.toMatch(/synthetic/i);
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

it("does not offer reserve trains on the rider map or train directory", () => {
  showWorld({ ...baseWorld, trains: [{ ...train, id: "reserve-ba", state: "reserve", line: "", nextStationId: null }] });
  render(<PassengerApp />);
  expect(screen.queryByRole("button", { name: /^reserve-ba,/ })).toBeNull();
  expect(screen.getByRole("list", { name: "Trains", hidden: true }).textContent).not.toContain("reserve-ba");
});
