import { useEffect, useRef, useState } from "react";
import { api } from "../api";
import { stationName, titleCase, viewerTimeZone } from "../format";
import type {
  ActionProposal,
  AiCapabilities,
  AssistantResponse,
  ChatMessage,
  JourneyRequest,
  NetworkDefinition,
} from "../types";
import { ComparisonCard } from "./ComparisonCard";
import { JourneyCards } from "./JourneyCards";
import { SafeMarkdown } from "./SafeMarkdown";
import { StationBoardView } from "./StationBoardView";
import { TransitMark } from "./TransitBrand";

const toolLabels: Record<string, string> = {
  resolve_station: "Station lookup",
  get_arrivals: "Live arrival board",
  plan_journey: "Journey planner",
  get_world: "Network snapshot",
  get_service_status: "Service status",
  get_station: "Station details",
  compare_action: "Recovery forecast",
  propose_action: "Action proposal",
};

interface ConversationEntry {
  id: string;
  role: "user" | "assistant";
  text: string;
  payload?: AssistantResponse;
}

interface AssistantPanelProps {
  kind: "operations" | "passenger";
  network?: NetworkDefinition;
  currentGeneration?: number;
  currentScenarioId?: string;
  capabilities: AiCapabilities | null;
  capabilityError: string | null;
  onProposalApproved?: () => void;
  onClose?: () => void;
}

export function AssistantPanel({
  kind,
  network,
  currentGeneration,
  currentScenarioId,
  capabilities,
  capabilityError,
  onProposalApproved,
  onClose,
}: AssistantPanelProps) {
  const [entries, setEntries] = useState<ConversationEntry[]>([]);
  const [input, setInput] = useState("");
  const [journeyContext, setJourneyContext] = useState<JourneyRequest | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [recording, setRecording] = useState(false);
  const [transcribing, setTranscribing] = useState(false);
  const [speak, setSpeak] = useState(false);
  const [audioError, setAudioError] = useState<string | null>(null);
  const [approvalState, setApprovalState] = useState<Record<string, string>>({});
  const recorder = useRef<MediaRecorder | null>(null);
  const chunks = useRef<Blob[]>([]);
  const audio = useRef<HTMLAudioElement | null>(null);
  const conversation = useRef<HTMLDivElement | null>(null);
  const hasConversationUpdate = useRef(false);

  useEffect(() => setJourneyContext(null), [currentGeneration, currentScenarioId]);
  useEffect(() => {
    if (!hasConversationUpdate.current) {
      hasConversationUpdate.current = true;
      return;
    }
    const container = conversation.current;
    if (!container) return;
    container.scrollTo({ top: container.scrollHeight, behavior: "smooth" });
  }, [entries, submitting]);
  useEffect(() => () => {
    recorder.current?.stream.getTracks().forEach((track) => track.stop());
    audio.current?.pause();
    if (audio.current?.src) URL.revokeObjectURL(audio.current.src);
  }, []);

  const playSpeech = async (text: string) => {
    if (!speak || !capabilities?.speech) return;
    try {
      setAudioError(null);
      audio.current?.pause();
      if (audio.current?.src) URL.revokeObjectURL(audio.current.src);
      const blob = await api.speech(text);
      const url = URL.createObjectURL(blob);
      audio.current = new Audio(url);
      await audio.current.play();
    } catch (speechError) {
      setAudioError(speechError instanceof Error ? speechError.message : "Speech playback is unavailable.");
    }
  };

  const send = async () => {
    const message = input.trim();
    if (!message || submitting) return;
    const userEntry: ConversationEntry = { id: crypto.randomUUID(), role: "user", text: message };
    const recent: ChatMessage[] = [...entries, userEntry].slice(-8).map((entry) => ({
      role: entry.role,
      content: entry.text,
    }));
    setEntries((current) => [...current, userEntry]);
    setInput("");
    setSubmitting(true);
    setError(null);
    try {
      const response = await api.assistant({
        message,
        history: recent,
        journeyContext,
        viewerTimeZone: viewerTimeZone(),
      });
      setEntries((current) => [
        ...current,
        { id: crypto.randomUUID(), role: "assistant", text: response.text, payload: response },
      ]);
      if (response.journeyContext) setJourneyContext(response.journeyContext);
      await playSpeech(response.text);
    } catch (assistantError) {
      setError(assistantError instanceof Error ? assistantError.message : "The assistant is unavailable.");
    } finally {
      setSubmitting(false);
    }
  };

  const startRecording = async () => {
    setAudioError(null);
    if (!navigator.mediaDevices?.getUserMedia || typeof MediaRecorder === "undefined") {
      setAudioError("This browser does not support microphone recording. You can still type your question.");
      return;
    }
    try {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
      const mediaRecorder = new MediaRecorder(stream);
      chunks.current = [];
      mediaRecorder.ondataavailable = (event) => {
        if (event.data.size) chunks.current.push(event.data);
      };
      mediaRecorder.onstop = async () => {
        stream.getTracks().forEach((track) => track.stop());
        const blob = new Blob(chunks.current, { type: mediaRecorder.mimeType || "audio/webm" });
        setRecording(false);
        setTranscribing(true);
        try {
          const result = await api.transcribe(blob);
          setInput(result.text);
        } catch (transcriptionError) {
          setAudioError(transcriptionError instanceof Error ? transcriptionError.message : "Transcription failed.");
        } finally {
          setTranscribing(false);
        }
      };
      recorder.current = mediaRecorder;
      mediaRecorder.start();
      setRecording(true);
    } catch (microphoneError) {
      setAudioError(
        microphoneError instanceof Error
          ? `Microphone unavailable: ${microphoneError.message}. You can still type your question.`
          : "Microphone permission was not granted. You can still type your question.",
      );
    }
  };

  const stopRecording = () => {
    if (recorder.current?.state === "recording") recorder.current.stop();
  };

  const stopAudio = () => {
    audio.current?.pause();
    if (audio.current) audio.current.currentTime = 0;
  };

  const approve = async (proposal: ActionProposal) => {
    setApprovalState((current) => ({ ...current, [proposal.id]: "Approving…" }));
    try {
      const result = await api.approveProposal(proposal);
      setApprovalState((current) => ({ ...current, [proposal.id]: result.message }));
      onProposalApproved?.();
    } catch (approvalError) {
      setApprovalState((current) => ({
        ...current,
        [proposal.id]: approvalError instanceof Error ? approvalError.message : "Approval failed.",
      }));
    }
  };

  const enabled = capabilities?.text === true;
  return (
    <section className={`assistant-panel assistant-panel--${kind}`} aria-label={`${kind} assistant`}>
      <header className="assistant-header">
        {kind === "passenger" && <TransitMark />}
        <div>
          {kind === "passenger" && <span className="eyebrow">Journey assistant</span>}
          <h2>{kind === "operations" ? "Dispatch assistant" : "Ask Aspireland"}</h2>
        </div>
        <span className={`provider-status ${enabled ? "online" : "offline"}`} title={capabilities?.provider}>
          {enabled ? kind === "operations" ? "Connected" : capabilities?.provider ?? "OpenAI" : "AI unavailable"}
        </span>
        {kind === "operations" && <TransitMark />}
        {onClose && <button className="assistant-close" onClick={onClose} aria-label="Close AI dispatcher">×</button>}
      </header>

      {!enabled && (
        <div className="notice notice--warning">
          {capabilities?.reason ?? capabilityError ?? "The model provider is not configured. Live forms and boards still work."}
        </div>
      )}

      <div className="conversation" ref={conversation}>
        {entries.length === 0 && (
          <div className="assistant-welcome">
            <strong>{kind === "operations" ? "Investigate the live network." : "Plan with the live network."}</strong>
            <p>
              {kind === "operations"
                ? "Ask why a queue is growing, compare an intervention, or request a bounded proposal."
                : "Ask for a route, service status, or a step-free alternative. Results come from planner tools."}
            </p>
          </div>
        )}
        {entries.map((entry) => (
          <article key={entry.id} className={`message message--${entry.role}`}>
            <div className="message-label">{entry.role === "user" ? "You" : "Aspireland"}</div>
            {entry.role === "assistant" ? <SafeMarkdown text={entry.text} /> : <p>{entry.text}</p>}
            {entry.payload?.tools && entry.payload.tools.length > 0 && (
              <details className="tool-provenance">
                <summary>Sources used</summary>
                <ul>{[...new Set(entry.payload.tools.map((tool) => toolLabels[tool.toLowerCase()] ?? "Live network tool"))].map((label) => <li key={label}>{label}</li>)}</ul>
              </details>
            )}
            {entry.payload?.journey && (
              <JourneyCards
                result={entry.payload.journey}
                network={network}
                currentGeneration={currentGeneration}
                currentScenarioId={currentScenarioId}
              />
            )}
            {entry.payload?.arrivals && network && currentScenarioId !== undefined && currentGeneration !== undefined && (
              <section className="assistant-arrivals" aria-label="Assistant station arrivals">
                <h3>{stationName(network, entry.payload.arrivals.stationId)} arrivals</h3>
                <StationBoardView
                  board={entry.payload.arrivals}
                  network={network}
                  scenarioId={currentScenarioId}
                  generation={currentGeneration}
                  loading={false}
                  error={null}
                  compact
                />
              </section>
            )}
            {entry.payload?.comparison && <ComparisonCard comparison={entry.payload.comparison} currentScenarioId={currentScenarioId} currentGeneration={currentGeneration} />}
            {entry.payload?.proposal && (
              <div className="proposal-card">
                <span className="eyebrow">Action proposal</span>
                <h3>{titleCase(entry.payload.proposal.action.kind)}</h3>
                <p>{entry.payload.proposal.explanation}</p>
                <dl>
                  <div><dt>Target</dt><dd>{entry.payload.proposal.action.target ?? "Network"}</dd></div>
                  <div><dt>Value</dt><dd>{entry.payload.proposal.action.value ?? entry.payload.proposal.action.amount ?? "—"}</dd></div>
                  <div><dt>Status</dt><dd>{approvalState[entry.payload.proposal.id] ?? titleCase(entry.payload.proposal.status)}</dd></div>
                </dl>
                {kind === "operations" && (
                  <button className="primary-button" onClick={() => void approve(entry.payload!.proposal!)}>Approve and revalidate</button>
                )}
              </div>
            )}
          </article>
        ))}
        {submitting && <div className="message message--assistant message--thinking"><span /><span /><span /></div>}
      </div>

      {error && <div className="notice notice--error">{error}</div>}
      {audioError && <div className="notice notice--warning">{audioError}</div>}

      <div className="assistant-composer">
        <label>
          <span className="sr-only">Message the assistant</span>
          <textarea
            value={input}
            onChange={(event) => setInput(event.target.value)}
            onKeyDown={(event) => {
              if (event.key === "Enter" && !event.shiftKey) {
                event.preventDefault();
                void send();
              }
            }}
            placeholder={kind === "operations" ? "Why is Central Exchange backing up?" : "How do I get from University to the airport?"}
            rows={3}
            disabled={!enabled}
          />
        </label>
        <div className="composer-actions">
          {kind === "passenger" && (
            <>
              {!recording ? (
                <button
                  className="voice-button"
                  onClick={() => void startRecording()}
                  disabled={!capabilities?.transcription || transcribing}
                >
                  {transcribing ? "Transcribing…" : "Push to talk"}
                </button>
              ) : (
                <button className="voice-button recording" onClick={stopRecording}>Stop recording</button>
              )}
              <label className="speak-toggle">
                <input
                  type="checkbox"
                  checked={speak}
                  onChange={(event) => setSpeak(event.target.checked)}
                  disabled={!capabilities?.speech}
                />
                Speak response <small>AI voice</small>
              </label>
              <button className="text-button" onClick={stopAudio}>Stop audio</button>
            </>
          )}
          <button className="primary-button" onClick={() => void send()} disabled={!enabled || submitting || !input.trim()}>
            {submitting ? "Working…" : "Send"}
          </button>
        </div>
        {kind === "passenger" && input.trim() && <small className="transcript-note">Review or edit the transcript before sending.</small>}
      </div>
    </section>
  );
}
