// @vitest-environment jsdom
import { act } from "react";
import { cleanup, fireEvent, render, within } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { NetworkMap, readAnimatedSimTime, reconcileClockAnchor, trainPosition } from "./NetworkMap";
import type { MovementGrant, NetworkDefinition, TrainState, WorldState } from "../types";

afterEach(() => cleanup());

const network: NetworkDefinition = {
  stations: [
    { id: "one", name: "One", x: 100, y: 100, character: "residential", accessPerMinute: 100, platformCapacity: 100, residential: 1, jobs: 0, education: 0, leisure: 0 },
    { id: "two", name: "Two", x: 300, y: 100, character: "business", accessPerMinute: 100, platformCapacity: 100, residential: 0, jobs: 1, education: 0, leisure: 0 },
  ],
  lines: [{ id: "Blue", name: "Blue", color: "#4b8ff5", stations: ["one", "two"], travelSeconds: 120, headwaySeconds: 480 }],
  accessEdges: [],
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
  state: "dwelling",
  stationId: "one",
  nextStationId: "two",
  direction: 1,
  routeIndex: 0,
  capacity: 100,
  load: 20,
  version: 1,
  readyAt: world.simTime,
  depot: "west",
  compatibleLines: ["Blue"],
};

it("keeps passenger train targets below stations and captures moving-marker taps without panning", () => {
  const onSelectTrain = vi.fn();
  const onSelectStation = vi.fn();
  const view = render(
    <NetworkMap network={network} world={{ ...world, trains: [train] }} onSelectTrain={onSelectTrain} onSelectStation={onSelectStation} calm />,
  );
  const map = view.getByRole("group", { name: /Interactive Aspireland metro map/ });
  const marker = within(map).getByRole("button", { name: /^blue-1,/ });
  const station = within(map).getByRole("button", { name: "One station" });
  expect(marker.compareDocumentPosition(station) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  const target = marker.querySelector(".map-train-hit-target");
  expect(target).not.toBeNull();
  const capture = vi.fn();
  marker.setPointerCapture = capture;
  const pointer = Object.assign(new MouseEvent("pointerdown", { bubbles: true, cancelable: true, button: 0 }), { pointerId: 7, pointerType: "touch" });
  fireEvent(target!, pointer);
  expect(capture).toHaveBeenCalledWith(7);
  expect(pointer.defaultPrevented).toBe(false);
  fireEvent.click(target!);
  expect(onSelectTrain).toHaveBeenCalledExactlyOnceWith("blue-1");
  expect(document.activeElement).toBe(marker);

  fireEvent.click(station);
  expect(onSelectStation).toHaveBeenCalledExactlyOnceWith("one");
  expect(onSelectTrain).toHaveBeenCalledTimes(1);
});

it("offers a stable train directory with keyboard activation and current selection", () => {
  const onSelectTrain = vi.fn();
  const trains = [train, { ...train, id: "blue-2" }];
  const props = { network, world: { ...world, trains }, selectedTrainId: "blue-2", onSelectTrain, onSelectStation: vi.fn(), calm: true };
  const view = render(<NetworkMap {...props} />);
  const summary = view.getByText("Train directory and keyboard-accessible map alternative");
  const directory = summary.closest("details")!;
  fireEvent.click(summary);
  expect(within(directory).getByRole("list")).toBeTruthy();
  const buttons = within(directory).getAllByRole("button");
  expect(buttons.map((button) => button.textContent?.match(/blue-\d/)?.[0])).toEqual(["blue-1", "blue-2"]);
  expect(buttons[1].getAttribute("aria-pressed")).toBe("true");
  fireEvent.click(buttons[1]);
  expect(onSelectTrain).toHaveBeenLastCalledWith("blue-2");
  buttons[1].focus();

  view.rerender(<NetworkMap {...props} world={{ ...world, version: 2, trains: [...trains].reverse() }} />);
  expect(within(directory).getAllByRole("button")).toEqual(buttons);
  expect(document.activeElement).toBe(buttons[1]);
  const marker = view.container.querySelector(".map-train")!;
  fireEvent.keyDown(marker, { key: " " });
  expect(onSelectTrain).toHaveBeenLastCalledWith("blue-1");
  expect(within(view.getByRole("group", { name: /Interactive Aspireland metro map/ })).getByRole("button", { name: /^blue-2,/ }).getAttribute("aria-pressed")).toBe("true");
});

it("does not trap ordinary wheel scrolling and zooms through explicit controls", () => {
  const view = render(
    <NetworkMap
      network={network}
      world={world}
      onSelectStation={() => undefined}
      onSelectTrain={() => undefined}
    />,
  );
  const map = view.container.querySelector("svg.network-map");
  expect(map).not.toBeNull();
  const originalViewBox = map!.getAttribute("viewBox");
  const wheel = new WheelEvent("wheel", { cancelable: true, deltaY: -100 });
  act(() => map!.dispatchEvent(wheel));
  expect(wheel.defaultPrevented).toBe(false);
  expect(map!.getAttribute("viewBox")).toBe(originalViewBox);
  fireEvent.click(view.getByRole("button", { name: "Zoom in" }));
  expect(map!.getAttribute("viewBox")).not.toBe(originalViewBox);
});

it("counts only platform and outside cohorts as waiting", () => {
  const view = render(
    <NetworkMap
      network={network}
      world={{
        ...world,
        cohorts: [
          { id: "platform", origin: "one", destination: "two", stationId: "one", count: 7, accessible: false, state: "platform", createdAt: world.simTime, readyAt: world.simTime, patienceSeconds: 300, legs: [], legIndex: 0, returnScheduled: false },
          { id: "outside", origin: "one", destination: "two", stationId: "one", count: 3, accessible: false, state: "outside", createdAt: world.simTime, readyAt: world.simTime, patienceSeconds: 300, legs: [], legIndex: 0, returnScheduled: false },
          { id: "onboard", origin: "one", destination: "two", stationId: "one", count: 2, accessible: false, state: "onboard", trainId: "blue-1", createdAt: world.simTime, readyAt: world.simTime, patienceSeconds: 300, legs: [], legIndex: 0, returnScheduled: false },
          { id: "transfer", origin: "one", destination: "two", stationId: "one", count: 4, accessible: false, state: "transfer", createdAt: world.simTime, readyAt: world.simTime, patienceSeconds: 300, legs: [], legIndex: 0, returnScheduled: false },
          { id: "completed", origin: "one", destination: "two", stationId: "one", count: 5, accessible: false, state: "completed", createdAt: world.simTime, readyAt: world.simTime, patienceSeconds: 300, legs: [], legIndex: 0, returnScheduled: false },
          { id: "scheduled-return", origin: "two", destination: "one", stationId: "one", count: 80, accessible: false, state: "outside", createdAt: world.simTime, readyAt: "2026-11-17T12:00:00Z", patienceSeconds: 300, legs: [], legIndex: 0, returnAt: "2026-11-17T12:00:00Z", returnScheduled: true },
        ],
      }}
      onSelectStation={() => undefined}
      onSelectTrain={() => undefined}
    />,
  );

  expect(view.getByLabelText("One station, 10 passengers waiting")).toBeTruthy();
});

it("tracks sustained backend progress instead of accumulating requested-speed lead", () => {
  const base = new Date(world.simTime).getTime();
  let anchor = reconcileClockAnchor(null, { ...world, paused: false, speed: 60 }, 0);

  for (let index = 1; index <= 120; index += 1) {
    const monotonicMs = index * 1500;
    const authoritativeMs = base + Math.floor(index / 4) * 20_000;
    anchor = reconcileClockAnchor(
      anchor,
      {
        ...world,
        paused: false,
        speed: 60,
        version: index + 1,
        simTime: new Date(authoritativeMs).toISOString(),
      },
      monotonicMs,
    );
    const atSnapshot = readAnimatedSimTime(anchor, monotonicMs, false);
    const betweenSnapshots = readAnimatedSimTime(anchor, monotonicMs + 750, false);
    expect(betweenSnapshots).toBeLessThanOrEqual(authoritativeMs);
    if (index >= 4) expect(betweenSnapshots).toBeGreaterThan(atSnapshot);
  }

  const finalAuthoritativeMs = base + Math.floor(120 / 4) * 20_000;
  const points = new Map([["one", { x: 100, y: 100 }], ["two", { x: 300, y: 100 }]]);
  const trains: TrainState[] = Array.from({ length: 10 }, (_, index) => {
    const line = ["Blue", "Amber", "Green"][index % 3];
    return {
    id: `${line.toLowerCase()}-${index + 1}`,
    line,
    state: "travelling",
    stationId: "one",
    nextStationId: "two",
    direction: 1,
    routeIndex: 0,
    capacity: 100,
    load: 20,
    version: 1,
    readyAt: world.simTime,
    activeGrantId: `grant-${index}`,
    depot: "west",
    compatibleLines: [line],
    };
  });
  const grants: MovementGrant[] = trains.map((train, index) => {
    const shortGrant = index % 2 === 0;
    return {
      id: `grant-${index}`,
      requestId: `request-${index}`,
      trainId: train.id,
      from: "one",
      to: "two",
      block: `${train.line}:one>two`,
      platform: `two:${train.line}:+`,
      departsAt: new Date(finalAuthoritativeMs - (shortGrant ? 30_000 : 300_000)).toISOString(),
      arrivesAt: new Date(finalAuthoritativeMs + (shortGrant ? 90_000 : 780_000)).toISOString(),
      completed: false,
    };
  });
  const firstFrame = readAnimatedSimTime(anchor, 120 * 1500, false);
  const secondFrame = readAnimatedSimTime(anchor, 120 * 1500 + 500, false);

  for (let index = 0; index < trains.length; index += 1) {
    const first = trainPosition(trains[index], grants[index], points, firstFrame, false);
    const second = trainPosition(trains[index], grants[index], points, secondFrame, false);
    expect(second!.x).toBeGreaterThan(first!.x);
    expect(second!.x).toBeLessThan(300);
  }
});

it("advances monotonically at 1x and 60x, freezes on pause, and resets by generation", () => {
  const initial = reconcileClockAnchor(null, { ...world, paused: false, speed: 1 }, 1000);
  expect(readAnimatedSimTime(initial, 2000, false)).toBe(new Date(world.simTime).getTime());

  const running = reconcileClockAnchor(initial, { ...world, paused: false, speed: 1, version: 2, simTime: "2026-11-17T08:00:01Z" }, 2000);
  expect(readAnimatedSimTime(running, 2500, false)).toBe(new Date("2026-11-17T08:00:00.500Z").getTime());
  expect(readAnimatedSimTime(running, 3000, false)).toBe(new Date("2026-11-17T08:00:01Z").getTime());

  const fast = reconcileClockAnchor(running, { ...world, paused: false, speed: 60, version: 3, simTime: "2026-11-17T08:00:11Z" }, 3000);
  expect(readAnimatedSimTime(fast, 3500, false)).toBe(new Date("2026-11-17T08:00:06Z").getTime());
  expect(readAnimatedSimTime(fast, 4000, false)).toBe(new Date("2026-11-17T08:00:11Z").getTime());

  const paused = reconcileClockAnchor(fast, { ...world, paused: true, speed: 60, version: 4, simTime: "2026-11-17T08:01:30Z" }, 4000);
  expect(readAnimatedSimTime(paused, 5000, false)).toBe(new Date("2026-11-17T08:01:30Z").getTime());

  const reset = reconcileClockAnchor(paused, { ...world, scenarioId: "new", generation: 2, paused: false, simTime: "2026-11-17T07:30:00Z" }, 5000);
  expect(readAnimatedSimTime(reset, 5000, false)).toBe(new Date("2026-11-17T07:30:00Z").getTime());
  expect(readAnimatedSimTime(fast, 4000, true)).toBe(fast.targetSimulatedMs);
});

it("interpolates only inside an authoritative active grant and clamps at its endpoints", () => {
  const train: TrainState = {
    id: "blue-1",
    line: "Blue",
    state: "travelling",
    stationId: "one",
    nextStationId: "two",
    direction: 1,
    routeIndex: 0,
    capacity: 100,
    load: 20,
    version: 1,
    readyAt: "2026-11-17T08:00:00Z",
    activeGrantId: "grant",
    depot: "west",
    compatibleLines: ["Blue"],
  };
  const grant: MovementGrant = {
    id: "grant",
    requestId: "request",
    trainId: train.id,
    from: "one",
    to: "two",
    block: "Blue:one>two",
    platform: "two:Blue:+",
    departsAt: "2026-11-17T08:00:00Z",
    arrivesAt: "2026-11-17T08:01:00Z",
    completed: false,
  };
  const points = new Map([["one", { x: 100, y: 100 }], ["two", { x: 300, y: 100 }]]);

  expect(trainPosition(train, grant, points, new Date("2026-11-17T07:59:00Z").getTime(), false)?.x).toBe(100);
  expect(trainPosition(train, grant, points, new Date("2026-11-17T08:00:30Z").getTime(), false)?.x).toBe(200);
  expect(trainPosition(train, grant, points, new Date("2026-11-17T08:02:00Z").getTime(), false)?.x).toBe(300);
  expect(trainPosition(train, undefined, points, new Date("2026-11-17T08:00:30Z").getTime(), false)?.x).toBe(100);
  expect(trainPosition(train, grant, points, new Date("2026-11-17T08:00:30Z").getTime(), true)?.x).toBe(100);
});
