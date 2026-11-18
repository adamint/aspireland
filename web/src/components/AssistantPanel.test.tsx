// @vitest-environment jsdom
import { StrictMode } from "react";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { api } from "../api";
import { AssistantPanel } from "./AssistantPanel";
import type { NetworkDefinition, StationBoard } from "../types";

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
  const play = vi.fn<() => Promise<void>>().mockResolvedValue(undefined);
  const pause = vi.fn();
  vi.stubGlobal("Audio", class {
    play = play;
    pause = pause;
  });
  const createObjectURL = vi.fn(() => "blob:reply");
  const revokeObjectURL = vi.fn();
  vi.stubGlobal("URL", class extends URL {
    static createObjectURL = createObjectURL;
    static revokeObjectURL = revokeObjectURL;
  });
  const transcribe = vi.spyOn(api, "transcribe").mockResolvedValue({ text: "Spoken question" });
  const assistant = vi.spyOn(api, "assistant").mockResolvedValue({ text: "Signal's answer" });
  const speech = vi.spyOn(api, "speech").mockResolvedValue(new Blob(["synthetic reply"]));
  const view = render(<AssistantPanel kind="passenger" capabilities={voiceCapabilities} capabilityError={null} />);
  const input = screen.getByRole<HTMLTextAreaElement>("textbox");
  return { view, input, getUserMedia, stopTrack, transcribe, assistant, speech, play, pause, createObjectURL, revokeObjectURL };
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

it("plays replies automatically only for opted-in recorded turns without reopening the microphone", async () => {
  const { input, getUserMedia, assistant, speech, play } = renderVoice();
  const mode = screen.getByRole("button", { name: "Voice conversation" });
  expect(mode.getAttribute("aria-pressed")).toBe("false");
  fireEvent.click(mode);
  expect(mode.getAttribute("aria-pressed")).toBe("true");
  sendTyped("Typed question");
  await screen.findByText("Signal's answer");
  expect(speech).not.toHaveBeenCalled();
  expect(getUserMedia).not.toHaveBeenCalled();

  fireEvent.change(input, { target: { value: "Unsent draft" } });
  fireEvent.click(await startRecording());
  await waitFor(() => expect(play).toHaveBeenCalledTimes(1));
  expect(assistant.mock.calls.map(([request]) => request.message)).toEqual(["Typed question", "Spoken question"]);
  expect(speech).toHaveBeenCalledExactlyOnceWith("Signal's answer");
  expect(screen.getByRole("status").textContent).toMatch(/Playing/);
  expect(screen.getByRole("button", { name: "Read reply 2 aloud" }).getAttribute("aria-pressed")).toBe("true");
  expect(getUserMedia).toHaveBeenCalledTimes(1);
  expect(input.value).toBe("Unsent draft");
});

it("stops a reply on a second tap and replaces audio when another reply is selected", async () => {
  const { assistant, speech, play, pause, revokeObjectURL } = renderVoice();
  assistant.mockResolvedValueOnce({ text: "First answer" }).mockResolvedValueOnce({ text: "Second answer" });
  sendTyped("First question");
  const first = await screen.findByRole("button", { name: "Read reply 1 aloud" });
  sendTyped("Second question");
  const second = await screen.findByRole("button", { name: "Read reply 2 aloud" });
  expect(speech).not.toHaveBeenCalled();
  fireEvent.click(first);
  await waitFor(() => expect(play).toHaveBeenCalledTimes(1));
  fireEvent.click(second);
  await waitFor(() => expect(play).toHaveBeenCalledTimes(2));
  expect(first.getAttribute("aria-pressed")).toBe("false");
  expect(pause).toHaveBeenCalledTimes(1);
  fireEvent.click(second);
  expect(second.getAttribute("aria-pressed")).toBe("false");
  expect(pause).toHaveBeenCalledTimes(2);
  expect(revokeObjectURL).toHaveBeenCalledTimes(2);
  expect(speech.mock.calls).toEqual([["First answer"], ["Second answer"]]);
});

it("ignores speech that arrives after the rider selects another reply", async () => {
  const { assistant, speech, createObjectURL, play } = renderVoice();
  assistant.mockResolvedValueOnce({ text: "First answer" }).mockResolvedValueOnce({ text: "Second answer" });
  const slowSpeech = deferred<Blob>();
  const currentBlob = new Blob(["second"]);
  speech.mockReturnValueOnce(slowSpeech.promise).mockResolvedValueOnce(currentBlob);
  sendTyped("First question");
  const first = await screen.findByRole("button", { name: "Read reply 1 aloud" });
  sendTyped("Second question");
  const second = await screen.findByRole("button", { name: "Read reply 2 aloud" });
  fireEvent.click(first);
  fireEvent.click(second);
  await waitFor(() => expect(play).toHaveBeenCalledTimes(1));
  await act(async () => slowSpeech.resolve(new Blob(["first"])));
  expect(createObjectURL).toHaveBeenCalledExactlyOnceWith(currentBlob);
  expect(first.getAttribute("aria-pressed")).toBe("false");
  expect(second.getAttribute("aria-pressed")).toBe("true");
});

it("stops using the microphone if permission resolves after unmount", async () => {
  const { view, getUserMedia, stopTrack, transcribe } = renderVoice();
  const permission = deferred<Awaited<ReturnType<typeof getUserMedia>>>();
  getUserMedia.mockReturnValueOnce(permission.promise);
  fireEvent.click(screen.getByRole("button", { name: "Start recording" }));
  view.unmount();
  await act(async () => permission.resolve({ getTracks: () => [{ stop: stopTrack }] }));
  expect(stopTrack).toHaveBeenCalledTimes(1);
  expect(transcribe).not.toHaveBeenCalled();
});

it("leaves typed text usable after microphone denial or blocked audio playback", async () => {
  const { getUserMedia, input, play, revokeObjectURL } = renderVoice();
  getUserMedia.mockRejectedValueOnce(new DOMException("Permission denied", "NotAllowedError"));
  fireEvent.change(input, { target: { value: "Keep me" } });
  fireEvent.click(screen.getByRole("button", { name: "Start recording" }));
  expect((await screen.findByRole("alert")).textContent).toMatch(/Microphone unavailable/);
  expect(input.value).toBe("Keep me");

  fireEvent.click(screen.getByRole("button", { name: "Send" }));
  const speaker = await screen.findByRole("button", { name: "Read reply 1 aloud" });
  fireEvent.change(input, { target: { value: "Next question" } });
  play.mockRejectedValueOnce(new DOMException("Playback blocked", "NotAllowedError"));
  fireEvent.click(speaker);
  expect((await screen.findByRole("alert")).textContent).toMatch(/Playback blocked/);
  expect(speaker.getAttribute("aria-pressed")).toBe("false");
  expect(revokeObjectURL).toHaveBeenCalledTimes(1);
  expect(input.value).toBe("Next question");
});

it("hides controls for unavailable voice capabilities and in dispatch", async () => {
  const { view } = renderVoice();
  view.rerender(<AssistantPanel kind="passenger" capabilities={{ ...voiceCapabilities, speech: false }} capabilityError={null} />);
  sendTyped("Typed question");
  await screen.findByText("Signal's answer");
  expect(screen.getByRole("button", { name: "Start recording" })).toBeTruthy();
  expect(screen.queryByRole("button", { name: "Voice conversation" })).toBeNull();
  expect(screen.queryByRole("button", { name: /Read reply/ })).toBeNull();

  view.rerender(<AssistantPanel kind="passenger" capabilities={{ ...voiceCapabilities, transcription: false }} capabilityError={null} />);
  expect(screen.getByRole("button", { name: "Read reply 1 aloud" })).toBeTruthy();
  expect(screen.queryByRole("button", { name: "Start recording" })).toBeNull();
  expect(screen.queryByRole("button", { name: "Voice conversation" })).toBeNull();

  view.rerender(<AssistantPanel kind="operations" capabilities={voiceCapabilities} capabilityError={null} />);
  expect(screen.getByRole("heading", { name: "Dispatch assistant" })).toBeTruthy();
  expect(screen.queryByRole("button", { name: /Start recording|Voice conversation|Read reply/ })).toBeNull();
});

it("keeps a recording as a draft if voice conversation is turned off before transcription finishes", async () => {
  const { assistant, input, transcribe } = renderVoice();
  const pending = deferred<{ text: string }>();
  transcribe.mockReturnValueOnce(pending.promise);
  const mode = screen.getByRole("button", { name: "Voice conversation" });
  fireEvent.click(mode);
  fireEvent.click(await startRecording());
  await waitFor(() => expect(transcribe).toHaveBeenCalledTimes(1));
  fireEvent.click(mode);
  await act(async () => pending.resolve({ text: "Spoken question" }));
  expect(input.value).toBe("Spoken question");
  expect(assistant).not.toHaveBeenCalled();
});

it("does not start late automatic speech after voice conversation is turned off", async () => {
  const { speech, play, createObjectURL } = renderVoice();
  const pendingSpeech = deferred<Blob>();
  speech.mockReturnValueOnce(pendingSpeech.promise);
  const mode = screen.getByRole("button", { name: "Voice conversation" });
  fireEvent.click(mode);
  fireEvent.click(await startRecording());
  await waitFor(() => expect(speech).toHaveBeenCalledTimes(1));
  fireEvent.click(mode);
  await act(async () => pendingSpeech.resolve(new Blob(["late answer"])));
  expect(createObjectURL).not.toHaveBeenCalled();
  expect(play).not.toHaveBeenCalled();
  expect(screen.getByRole("button", { name: "Read reply 1 aloud" }).getAttribute("aria-pressed")).toBe("false");
});
