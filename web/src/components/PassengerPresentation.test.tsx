// @vitest-environment jsdom
import { cleanup, render, screen, within } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { JourneyCards } from "./JourneyCards";
import { StationBoardView } from "./StationBoardView";
import type { JourneyResult, NetworkDefinition, StationBoard } from "../types";

afterEach(() => { cleanup(); vi.restoreAllMocks(); });

const network: NetworkDefinition = {
  stations: [
    { id: "university", name: "University", x: 0, y: 0, character: "campus", accessPerMinute: 100, platformCapacity: 100, residential: 0, jobs: 0, education: 1, leisure: 0 },
    { id: "central-exchange", name: "Central Exchange", x: 1, y: 0, character: "business", accessPerMinute: 100, platformCapacity: 100, residential: 0, jobs: 1, education: 0, leisure: 0 },
    { id: "southbank", name: "Southbank", x: 2, y: 0, character: "residential", accessPerMinute: 100, platformCapacity: 100, residential: 1, jobs: 0, education: 0, leisure: 0 },
    { id: "airport", name: "Airport", x: 3, y: 0, character: "airport", accessPerMinute: 100, platformCapacity: 100, residential: 0, jobs: 1, education: 0, leisure: 0 },
  ],
  lines: [
    { id: "Green", name: "Green", color: "#46b887", stations: ["university", "central-exchange", "southbank"], travelSeconds: 120, headwaySeconds: 600 },
    { id: "AX", name: "Airport Express", color: "#bd88ed", stations: ["airport", "central-exchange"], travelSeconds: 300, headwaySeconds: 600 },
  ],
  accessEdges: [],
};

const journey: JourneyResult = {
  scenarioId: "current",
  generation: 4,
  version: 1,
  asOf: "2026-11-17T08:12:00Z",
  request: { origin: "university", destination: "airport" },
  error: null,
  options: [{
    id: "route",
    label: "Fastest",
    totalSeconds: 1320,
    transfers: 1,
    accessible: true,
    arrivesAt: "2026-11-17T08:34:00Z",
    estimateKind: "live",
    warnings: [],
    legs: [
      { kind: "access", from: "university", to: "university", service: "walk", waitSeconds: 0, rideSeconds: 0, walkSeconds: 44, stops: [] },
      { kind: "ride", line: "Green", from: "university", to: "central-exchange", platform: "university:Green:+", destination: "southbank", service: "local", waitSeconds: 60, rideSeconds: 600, walkSeconds: 0, stops: ["university", "central-exchange"] },
      { kind: "transfer", from: "central-exchange", to: "central-exchange", service: "walk", waitSeconds: 0, rideSeconds: 0, walkSeconds: 120, stops: [] },
      { kind: "ride", line: "AX", from: "central-exchange", to: "airport", platform: "central-exchange:AX:-", destination: "airport", service: "express", waitSeconds: 60, rideSeconds: 420, walkSeconds: 0, stops: ["central-exchange", "airport"] },
    ],
  }],
};

it("shows rider journey updates and arrivals in the browser's local day and time", () => {
  const resolvedOptions = Intl.DateTimeFormat.prototype.resolvedOptions;
  vi.spyOn(Intl.DateTimeFormat.prototype, "resolvedOptions").mockImplementation(function (this: Intl.DateTimeFormat) {
    return { ...resolvedOptions.call(this), timeZone: "America/Los_Angeles" };
  });
  render(<JourneyCards passenger network={network} result={{
    ...journey,
    asOf: "2026-11-18T00:59:00Z",
    options: [{ ...journey.options[0], arrivesAt: "2026-11-18T01:29:00Z" }],
  }} />);
  expect(screen.getByText("Updated Tuesday, November 17 at 4:59 PM")).toBeTruthy();
  expect(screen.getByText("Arrive Tuesday, November 17 at 5:29 PM")).toBeTruthy();
  expect(screen.queryByText(/GMT|Europe\/|America\//)).toBeNull();
});

it("uses passenger-facing station and platform labels in itinerary cards", () => {
  const view = render(<JourneyCards result={journey} network={network} currentGeneration={4} />);
  const titles = [...view.container.querySelectorAll(".journey-leg strong")].map((element) => element.textContent);
  expect(titles).toEqual([
    "Enter University",
    "Board Green line toward Southbank",
    "Transfer at Central Exchange",
    "Board Airport Express toward Airport",
  ]);
  expect(screen.getByText(/Green southbound toward Southbank/)).toBeTruthy();
  expect(screen.getByText(/AX toward Airport/)).toBeTruthy();
  expect(screen.getAllByText("1 stop")).toHaveLength(2);
  expect(view.container.textContent).not.toMatch(/university:Green|Internal platform|1 stops/);
  expect(screen.getByText(/Travel to Central Exchange/)).toBeTruthy();
  expect(screen.getByText(/2 min walk/)).toBeTruthy();
});

it.each([
  [60, 100, false, null],
  [0, 100, false, null],
  [110, 100, false, null],
  [0, 0, false, "Capacity not reported"],
  [86, 100, true, "Live crowding unavailable"],
] as const)("only shows a rider capacity meter for a current %s/%s reading (stale: %s)", (load, capacity, stale, fallback) => {
  const board: StationBoard = { stationId: "university", scenarioId: "current", generation: 4, asOf: journey.asOf,
    updatedAt: journey.asOf, stale, restrictions: [],
    arrivals: [{ trainId: "green-1", line: "Green", service: "local", platform: "university:Green:+", destination: "southbank",
      seconds: 60, predictionKind: "live", status: "on-time", load, capacity }] };
  render(<StationBoardView passenger board={board} network={network} scenarioId="current" generation={4} loading={false} error={null} />);
  const crowding = screen.getAllByRole("cell")[3];
  expect(screen.getByRole("columnheader", { name: "Crowding" })).toBeTruthy();
  const meter = within(crowding).queryByRole("meter", { name: "Onboard capacity" });
  if (fallback) {
    expect(meter).toBeNull();
    expect(crowding.textContent).toBe(fallback);
  } else {
    expect(crowding.textContent).toBe("");
    expect(meter?.getAttribute("aria-valuenow")).toBe(String(Math.min(load, capacity)));
    expect(meter?.getAttribute("aria-valuemax")).toBe(String(capacity));
    expect(meter?.getAttribute("aria-valuetext")).toBe(`${load} of ${capacity} passenger capacity`);
  }
});

it("separates multi-stop travel, waits and exits without internal IDs", () => {
  const longer: JourneyResult = {
    ...journey,
    options: [{
      ...journey.options[0],
      legs: [
        { ...journey.options[0].legs[1], to: "southbank", stops: ["university", "central-exchange", "southbank"] },
        { kind: "egress", from: "southbank", to: "southbank", service: "walk", waitSeconds: 0, rideSeconds: 0, walkSeconds: 45, stops: [] },
      ],
    }],
  };
  render(<JourneyCards result={longer} network={network} currentGeneration={4} />);
  expect(screen.getByText("2 stops")).toBeTruthy();
  expect(screen.getByText(/1 min wait/)).toBeTruthy();
  expect(screen.getByText("Exit at Southbank")).toBeTruthy();
});

it("keeps live boards public and reports stale data explicitly", () => {
  const board: StationBoard = {
    stationId: "university", scenarioId: "current", generation: 4,
    asOf: journey.asOf, updatedAt: journey.asOf, stale: true, restrictions: ["Elevator unavailable"],
    arrivals: [{ trainId: "green-1", line: "Green", service: "local", platform: "university:Green:+", destination: "southbank", seconds: null, predictionKind: "stale", status: "stale", load: 10, capacity: 100 }],
  };
  const view = render(<StationBoardView board={board} network={network} scenarioId="current" generation={4} loading={false} error={null} />);
  expect(screen.getAllByText("Live arrivals unavailable")).toHaveLength(1);
  expect(screen.getByText(/Last updated/)).toBeTruthy();
  expect(screen.queryByText(/ETA unknown|Stale|On Time|1 min/)).toBeNull();
  expect(screen.getByText("Green southbound toward Southbank")).toBeTruthy();
  expect(view.container.textContent).not.toMatch(/university:Green|platform ID|green-1/);
});

it("hides a board from a previous scenario while awaiting the matching projection", () => {
  const board: StationBoard = {
    stationId: "university",
    scenarioId: "old",
    generation: 3,
    asOf: "2026-11-17T09:07:00Z",
    updatedAt: "2026-11-17T14:07:00Z",
    stale: false,
    restrictions: [],
    arrivals: [{
      trainId: "green-1",
      line: "Green",
      service: "local",
      platform: "university:Green:+",
      destination: "southbank",
      seconds: 60,
      predictionKind: "live",
      status: "on-time",
      load: 10,
      capacity: 100,
    }],
  };

  render(
    <StationBoardView
      board={board}
      network={network}
      scenarioId="current"
      generation={4}
      loading={false}
      error={null}
    />,
  );
  expect(screen.getByText("Updating station arrivals…")).toBeTruthy();
  expect(screen.queryByText("Southbank")).toBeNull();
});

it("distinguishes an incoming terminating train from a boardable onward service without changing its ETA", () => {
  const board: StationBoard = {
    stationId: "airport", scenarioId: "current", generation: 4,
    asOf: journey.asOf, updatedAt: journey.asOf, stale: false, restrictions: [],
    arrivals: [{ trainId: "ax-1", line: "AX", service: "express", platform: "airport:AX:-", destination: "airport", seconds: 180, predictionKind: "live", status: "on-time", load: 10, capacity: 100 }],
  };
  render(<StationBoardView board={board} network={network} scenarioId="current" generation={4} loading={false} error={null} />);
  expect(screen.getByText("Last stop")).toBeTruthy();
  expect(screen.getByText("Arriving at Airport. This train ends here.")).toBeTruthy();
  expect(screen.queryByText("AX toward Airport")).toBeNull();
  expect(screen.getByText("3 min")).toBeTruthy();
});

it.each(["projection", "refresh"] as const)("hides arrival estimates when %s updates fail even if the cached board was fresh", (failure) => {
  const board: StationBoard = {
    stationId: "university", scenarioId: "current", generation: 4, asOf: journey.asOf,
    updatedAt: journey.asOf, stale: false, restrictions: [],
    arrivals: [{ trainId: "green-1", line: "Green", service: "local", platform: "university:Green:+", destination: "southbank", seconds: 60, predictionKind: "live", status: "on-time", load: 10, capacity: 100 }],
  };
  render(<StationBoardView board={board} network={network} scenarioId="current" generation={4} loading={false}
    stale={failure === "projection"} error={failure === "refresh" ? "Refresh failed" : null} />);
  expect(screen.getAllByText("Live arrivals unavailable")).toHaveLength(1);
  expect(screen.getByText(/Last updated/)).toBeTruthy();
  expect(screen.queryByText(/1 min|On Time|ETA unknown|Stale/)).toBeNull();
});
