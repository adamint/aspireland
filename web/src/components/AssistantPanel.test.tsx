// @vitest-environment jsdom
import { StrictMode } from "react";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { api } from "../api";
import { AssistantPanel } from "./AssistantPanel";
import type { NetworkDefinition, StationBoard } from "../types";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

beforeEach(() => {
  Object.defineProperty(HTMLElement.prototype, "scrollIntoView", {
    configurable: true,
    value: vi.fn(() => Promise.resolve()),
  });
  Object.defineProperty(HTMLElement.prototype, "scrollTo", {
    configurable: true,
    value: vi.fn(),
  });
  vi.spyOn(window, "scrollTo").mockImplementation(() => undefined);
});

it("cold-mounts in StrictMode without scrolling the page to an offscreen assistant", () => {
  const consoleError = vi.spyOn(console, "error").mockImplementation(() => undefined);
  const view = render(
    <StrictMode>
      <AssistantPanel
        kind="passenger"
        capabilities={{ text: false, transcription: false, speech: false, provider: "OpenAI", reason: "Not configured" }}
        capabilityError={null}
      />
    </StrictMode>,
  );

  expect(window.scrollY).toBe(0);
  expect(window.scrollTo).not.toHaveBeenCalled();
  expect(HTMLElement.prototype.scrollIntoView).not.toHaveBeenCalled();
  expect(() => view.unmount()).not.toThrow();
  expect(consoleError).not.toHaveBeenCalled();
});

it("keeps assistant updates inside the conversation scroller", async () => {
  vi.spyOn(api, "assistant").mockResolvedValue({ text: "Grounded answer" });
  render(
    <AssistantPanel
      kind="passenger"
      capabilities={{ text: true, transcription: false, speech: false, provider: "OpenAI" }}
      capabilityError={null}
    />,
  );

  fireEvent.change(screen.getByPlaceholderText(/How do I get/), { target: { value: "Plan a trip" } });
  fireEvent.click(screen.getByRole("button", { name: "Send" }));
  await screen.findByText("Grounded answer");

  expect(window.scrollTo).not.toHaveBeenCalled();
  expect(HTMLElement.prototype.scrollIntoView).not.toHaveBeenCalled();
  expect(HTMLElement.prototype.scrollTo).toHaveBeenCalled();
});

it("sends the viewer timezone and renders typed arrival tools with safe text and collapsed provenance", async () => {
  const network: NetworkDefinition = {
    stations: ["university", "southbank"].map((id) => ({
      id, name: id === "university" ? "University" : "Southbank",
      x: 0, y: 0, character: "campus", accessPerMinute: 100, platformCapacity: 100,
      residential: 0, jobs: 0, education: 0, leisure: 0,
    })),
    lines: [], accessEdges: [],
  };
  const board: StationBoard = {
    stationId: "university", scenarioId: "current", generation: 4,
    asOf: "2026-11-17T11:34:24Z", updatedAt: "2026-11-17T11:34:24Z", stale: true, restrictions: [],
    arrivals: [{ trainId: "green-1", line: "Green", service: "local", platform: "university:Green:+", destination: "southbank", seconds: 60, predictionKind: "live", status: "on-time", load: 10, capacity: 100 }],
  };
  const assistant = vi.spyOn(api, "assistant").mockResolvedValue({
    text: "**University** arrivals:\n\n- Take *Green* toward Southbank",
    arrivals: board, tools: ["Resolve_station", "get_arrivals"],
  });
  const view = render(<AssistantPanel kind="passenger" network={network} currentScenarioId="current" currentGeneration={4}
    capabilities={{ text: true, transcription: false, speech: false, provider: "OpenAI" }} capabilityError={null} />);
  fireEvent.change(screen.getByRole("textbox"), { target: { value: "University arrivals" } });
  fireEvent.click(screen.getByRole("button", { name: "Send" }));
  await screen.findByRole("table", { name: "Station arrivals" });
  expect(assistant.mock.calls[0][0].viewerTimeZone).toBe(Intl.DateTimeFormat().resolvedOptions().timeZone);
  expect(screen.getByText("Live arrivals unavailable")).toBeTruthy();
  expect(view.container.querySelector(".tool-provenance")?.hasAttribute("open")).toBe(false);
  expect(view.container.textContent).not.toMatch(/Resolve_station|university:Green|\*\*/);
  fireEvent.click(screen.getByText("Sources used"));
  expect(screen.getByText("Station lookup")).toBeTruthy();
  expect(screen.getByText("Live arrival board")).toBeTruthy();

  view.rerender(<AssistantPanel kind="passenger" network={network} currentScenarioId="replacement" currentGeneration={4}
    capabilities={{ text: true, transcription: false, speech: false, provider: "OpenAI" }} capabilityError={null} />);
  expect(screen.queryByRole("table", { name: "Station arrivals" })).toBeNull();
  expect(screen.getByText("Updating station arrivals…")).toBeTruthy();
});
