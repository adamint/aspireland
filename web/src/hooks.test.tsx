// @vitest-environment jsdom
import { act, cleanup, renderHook } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { useStationBoard, useWorld } from "./hooks";
import type { WorldSnapshot, WorldState } from "./types";

const { worldRequest, boardRequest } = vi.hoisted(() => ({
  worldRequest: vi.fn(),
  boardRequest: vi.fn(),
}));

vi.mock("./api", () => ({
  api: { world: worldRequest, board: boardRequest },
}));

const world: WorldState = {
  scenarioId: "old",
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

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
  vi.unstubAllEnvs();
  vi.useRealTimers();
});

it("ignores an older world request that completes after a newer one", async () => {
  const slow = deferred<WorldSnapshot>();
  const fast = deferred<WorldSnapshot>();
  worldRequest.mockReturnValueOnce(slow.promise).mockReturnValueOnce(fast.promise);

  const { result } = renderHook(() => useWorld());
  act(() => {
    void result.current.refresh();
  });

  await act(async () => {
    fast.resolve({ world: { ...world, scenarioId: "new", generation: 2, version: 2 }, network: { stations: [], lines: [], accessEdges: [] }, stale: false });
    await fast.promise;
  });
  expect(result.current.data?.world.scenarioId).toBe("new");

  await act(async () => {
    slow.resolve({ world, network: { stations: [], lines: [], accessEdges: [] }, stale: false });
    await slow.promise;
  });
  expect(result.current.data?.world.scenarioId).toBe("new");
});

it.each([
  ["normal", undefined, 1500, 3000],
  ["attendee", "true", 15000, 15000],
] as const)("keeps %s world and board polling at their intended intervals", async (_mode, flag, worldInterval, boardInterval) => {
  vi.useFakeTimers();
  vi.stubEnv("VITE_ATTENDEE", flag);
  worldRequest.mockResolvedValue({ world, network: { stations: [], lines: [], accessEdges: [] }, stale: false });
  boardRequest.mockResolvedValue(null);
  renderHook(() => useWorld());
  renderHook(() => useStationBoard("university"));
  await act(async () => {});

  await act(async () => { await vi.advanceTimersByTimeAsync(worldInterval - 1); });
  expect(worldRequest).toHaveBeenCalledTimes(1);
  expect(boardRequest).toHaveBeenCalledTimes(1);
  await act(async () => { await vi.advanceTimersByTimeAsync(1); });
  expect(worldRequest).toHaveBeenCalledTimes(2);
  if (boardInterval === worldInterval) {
    expect(boardRequest).toHaveBeenCalledTimes(2);
  } else {
    expect(boardRequest).toHaveBeenCalledTimes(1);
    await act(async () => { await vi.advanceTimersByTimeAsync(boardInterval - worldInterval); });
    expect(boardRequest).toHaveBeenCalledTimes(2);
  }
});
