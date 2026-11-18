// @vitest-environment jsdom
import { StrictMode } from "react";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { api } from "../api";
import { AssistantPanel } from "./AssistantPanel";
import type { AssistantResponse, ComparisonResult, NetworkDefinition, StationBoard } from "../types";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
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

const metric = { mean: 1, min: 0, max: 2 };
const comparison: ComparisonResult = {
  scenarioId: "current",
  generation: 4,
  version: 1,
  checkpointTime: "2026-11-17T11:34:24Z",
  horizonMinutes: 15,
  samples: 3,
  candidate: { kind: "hold", target: "green-1", amount: 30 },
  baseline: {
    waitingMinutes: metric, completed: metric, deniedBoardings: metric, abandoned: metric,
    remainingQueue: metric, oldestQueueSeconds: metric, platformOverloads: metric, accessibilityFailures: metric,
  },
  intervention: {
    waitingMinutes: metric, completed: metric, deniedBoardings: metric, abandoned: metric,
    remainingQueue: metric, oldestQueueSeconds: metric, platformOverloads: metric, accessibilityFailures: metric,
  },
  method: "paired",
  warnings: [],
};

const journeyContext = { origin: "university", destination: "southbank", accessible: true };
const richResponse: AssistantResponse = {
  text: "**University** answer:\n\n- Take *Green* toward Southbank",
  arrivals: board,
  tools: ["Resolve_station", "get_arrivals"],
  journeyContext,
  journey: {
    scenarioId: "current", generation: 4, version: 1, asOf: "2026-11-17T11:34:24Z",
    request: journeyContext, options: [],
  },
  comparison,
  proposal: {
    id: "proposal-1", action: { kind: "hold", target: "green-1", amount: 30 },
    scenarioId: "current", generation: 4, version: 1, explanation: "Hold the train.", status: "pending",
  },
};

it("renders only safe answer text for passengers and preserves follow-up context", async () => {
  const assistant = vi.spyOn(api, "assistant")
    .mockResolvedValueOnce(richResponse)
    .mockResolvedValueOnce({ text: "Follow-up answer" });
  const view = render(<AssistantPanel kind="passenger" network={network} currentScenarioId="current" currentGeneration={4}
    capabilities={{ text: true, transcription: false, speech: true, provider: "OpenAI" }} capabilityError={null} />);

  fireEvent.change(screen.getByRole("textbox"), { target: { value: "University arrivals" } });
  fireEvent.click(screen.getByRole("button", { name: "Send" }));
  await screen.findByText("University");

  expect(assistant.mock.calls[0][0]).toEqual(expect.objectContaining({
    viewerTimeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
    journeyContext: null,
  }));
  expect(view.container.textContent).not.toMatch(/Resolve_station|university:Green|\*\*/);
  expect(view.container.querySelector(".tool-provenance")).toBeNull();
  expect(view.container.querySelector(".assistant-arrivals")).toBeNull();
  expect(view.container.querySelector(".journey-results")).toBeNull();
  expect(view.container.querySelector(".comparison-card")).toBeNull();
  expect(view.container.querySelector(".proposal-card")).toBeNull();
  expect(screen.queryByRole("button", { name: /Read reply|Voice conversation/ })).toBeNull();

  fireEvent.change(screen.getByRole("textbox"), { target: { value: "What about accessibility?" } });
  fireEvent.click(screen.getByRole("button", { name: "Send" }));
  await screen.findByText("Follow-up answer");
  expect(assistant.mock.calls[1][0]).toEqual(expect.objectContaining({
    journeyContext,
    viewerTimeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
  }));
});

it("shows the shared-chat disclosure only for available public Signal chat", () => {
  const view = render(<AssistantPanel kind="passenger"
    capabilities={{ text: true, transcription: false, speech: false, provider: "AI provider", sharedChat: true }}
    capabilityError={null} />);
  expect(screen.getByText(/Questions go to an AI provider/).textContent).toMatch(/don't include personal information/i);

  view.rerender(<AssistantPanel kind="passenger"
    capabilities={{ text: true, transcription: false, speech: false, provider: "AI provider" }}
    capabilityError={null} />);
  expect(screen.queryByText(/Questions go to an AI provider/)).toBeNull();

  view.rerender(<AssistantPanel kind="passenger"
    capabilities={{ text: false, transcription: false, speech: false, provider: "AI provider", sharedChat: true }}
    capabilityError={null} />);
  expect(screen.queryByText(/Questions go to an AI provider/)).toBeNull();

  view.rerender(<AssistantPanel kind="operations"
    capabilities={{ text: true, transcription: false, speech: false, provider: "AI provider", sharedChat: true }}
    capabilityError={null} />);
  expect(screen.queryByText(/Questions go to an AI provider/)).toBeNull();
});

it("keeps rich result and provenance flows in Dispatch", async () => {
  vi.spyOn(api, "assistant").mockResolvedValue(richResponse);
  const approve = vi.spyOn(api, "approveProposal").mockResolvedValue({
    accepted: true, status: "approved", message: "Approved", version: 2,
  });
  const view = render(<AssistantPanel kind="operations" network={network} currentScenarioId="current" currentGeneration={4}
    capabilities={{ text: true, transcription: true, speech: true, provider: "OpenAI" }} capabilityError={null} />);

  fireEvent.change(screen.getByRole("textbox"), { target: { value: "Investigate" } });
  fireEvent.click(screen.getByRole("button", { name: "Send" }));
  await screen.findByRole("table", { name: "Station arrivals" });

  expect(view.container.querySelector(".tool-provenance")).not.toBeNull();
  expect(view.container.querySelector(".comparison-card")).not.toBeNull();
  expect(view.container.querySelector(".proposal-card")).not.toBeNull();
  fireEvent.click(screen.getByRole("button", { name: "Approve and revalidate" }));
  await waitFor(() => expect(approve).toHaveBeenCalledWith(richResponse.proposal));
});

const voiceCapabilities = { text: true, transcription: true, speech: true, provider: "OpenAI" };

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => { resolve = done; });
  return { promise, resolve };
}

function renderVoice() {
  const stopTrack = vi.fn();
  const getUserMedia = vi.fn(async () => ({ getTracks: () => [{ stop: stopTrack }] }));
  vi.stubGlobal("navigator", Object.create(navigator, { mediaDevices: { value: { getUserMedia } } }));
  vi.stubGlobal("MediaRecorder", class {
    state = "inactive";
    mimeType = "audio/webm";
    ondataavailable?: (event: { data: Blob }) => void;
    onstop?: () => void;

    start() { this.state = "recording"; }
    stop() {
      this.state = "inactive";
      queueMicrotask(() => {
        this.ondataavailable?.({ data: new Blob(["synthetic audio"], { type: this.mimeType }) });
        this.onstop?.();
      });
    }
  });
  const transcribe = vi.spyOn(api, "transcribe").mockResolvedValue({ text: "Spoken question" });
  const assistant = vi.spyOn(api, "assistant").mockResolvedValue({ text: "Signal's answer" });
  const view = render(<AssistantPanel kind="passenger" capabilities={voiceCapabilities} capabilityError={null} />);
  const input = screen.getByRole<HTMLTextAreaElement>("textbox");
  return { view, input, getUserMedia, stopTrack, transcribe, assistant };
}

async function startRecording() {
  fireEvent.click(screen.getByRole("button", { name: "Start recording" }));
  return screen.findByRole("button", { name: "Stop recording" });
}

function sendTyped(value: string) {
  fireEvent.change(screen.getByRole("textbox"), { target: { value } });
  fireEvent.click(screen.getByRole("button", { name: "Send" }));
}

it("starts and stops on separate taps and puts the transcript in the editable draft", async () => {
  const { input, getUserMedia, stopTrack, transcribe, assistant } = renderVoice();
  expect(getUserMedia).not.toHaveBeenCalled();
  expect(screen.getByRole("heading", { name: "Signal" })).toBeTruthy();
  expect(screen.queryByRole("checkbox")).toBeNull();
  const stop = await startRecording();
  expect(screen.getByRole("status").textContent).toMatch(/Recording/);
  expect(transcribe).not.toHaveBeenCalled();
  fireEvent.change(input, { target: { value: "Existing draft" } });
  fireEvent.click(stop);
  expect(screen.getByRole("status").textContent).toMatch(/Transcribing/);
  await waitFor(() => expect(input.value).toBe("Existing draft\nSpoken question"));
  expect(stopTrack).toHaveBeenCalledTimes(1);
  expect(assistant).not.toHaveBeenCalled();
  sendTyped("Edited question");
  expect(assistant).toHaveBeenCalledWith(expect.objectContaining({ message: "Edited question" }));
  await screen.findByText("Signal's answer");
});

it("never exposes reply playback or automatic voice conversation for legacy speech capability", async () => {
  const { assistant } = renderVoice();
  sendTyped("Typed question");
  await screen.findByText("Signal's answer");
  expect(assistant).toHaveBeenCalledTimes(1);
  expect(screen.queryByRole("button", { name: /Read reply|Voice conversation/ })).toBeNull();
  expect(screen.queryByText(/AI-generated voice|Preparing reply audio|Playing reply/)).toBeNull();
});

it("stops using the microphone if permission resolves after unmount", async () => {
  const { view, getUserMedia, stopTrack, transcribe } = renderVoice();
  const permission = deferred<Awaited<ReturnType<typeof getUserMedia>>>();
  getUserMedia.mockReturnValueOnce(permission.promise);
  fireEvent.click(screen.getByRole("button", { name: "Start recording" }));
  expect(screen.getByRole("status").textContent).toMatch(/Waiting for microphone permission/);
  view.unmount();
  await act(async () => permission.resolve({ getTracks: () => [{ stop: stopTrack }] }));
  expect(stopTrack).toHaveBeenCalledTimes(1);
  expect(transcribe).not.toHaveBeenCalled();
});

it("leaves typed text usable after microphone denial", async () => {
  const { getUserMedia, input } = renderVoice();
  getUserMedia.mockRejectedValueOnce(new DOMException("Permission denied", "NotAllowedError"));
  fireEvent.change(input, { target: { value: "Keep me" } });
  fireEvent.click(screen.getByRole("button", { name: "Start recording" }));
  expect((await screen.findByRole("alert")).textContent).toMatch(/Microphone unavailable/);
  expect(input.value).toBe("Keep me");
});

it("hides recording when transcription is unavailable and hides attendee voice in Dispatch", async () => {
  const { view } = renderVoice();
  view.rerender(<AssistantPanel kind="passenger" capabilities={{ ...voiceCapabilities, speech: false }} capabilityError={null} />);
  expect(screen.getByRole("button", { name: "Start recording" })).toBeTruthy();

  view.rerender(<AssistantPanel kind="passenger" capabilities={{ ...voiceCapabilities, transcription: false }} capabilityError={null} />);
  expect(screen.queryByRole("button", { name: "Start recording" })).toBeNull();

  view.rerender(<AssistantPanel kind="operations" capabilities={voiceCapabilities} capabilityError={null} />);
  expect(screen.getByRole("heading", { name: "Dispatch assistant" })).toBeTruthy();
  expect(screen.queryByRole("button", { name: "Start recording" })).toBeNull();
});
