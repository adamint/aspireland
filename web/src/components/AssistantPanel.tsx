import { useEffect, useRef, useState } from "react";
import { api } from "../api";
import { stationName, titleCase, viewerTimeZone } from "../format";
import type {
  ActionProposal,
  AiCapabilities,
  AssistantJourneyContext,
  AssistantResponse,
  ChatMessage,
  NetworkDefinition,
} from "../types";
import { ComparisonCard } from "./ComparisonCard";
import { JourneyCards } from "./JourneyCards";
import { SafeMarkdown } from "./SafeMarkdown";
import { StationBoardView } from "./StationBoardView";
import { TransitMark } from "./TransitBrand";
import { useAssistantVoice } from "./useAssistantVoice";
import "./assistant-voice.css";

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
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [approvalState, setApprovalState] = useState<Record<string, string>>({});
  const journeyContext = useRef<AssistantJourneyContext | null>(null);
  const conversation = useRef<HTMLDivElement | null>(null);
  const hasConversationUpdate = useRef(false);
  const passenger = kind === "passenger";
  const enabled = capabilities?.text === true;
  const voice = useAssistantVoice(passenger && enabled && capabilities.transcription);
  const captureBusy = voice.captureStatus !== "idle";
  const recording = voice.captureStatus === "recording";

  useEffect(() => { journeyContext.current = null; }, [currentGeneration, currentScenarioId]);
  useEffect(() => {
    if (!hasConversationUpdate.current) {
      hasConversationUpdate.current = true;
      return;
    }
    const container = conversation.current;
    if (!container) return;
    container.scrollTo({ top: container.scrollHeight, behavior: "smooth" });
  }, [entries, submitting]);
  const appendDraft = (text: string) => {
    setInput((draft) => draft.trim() ? `${draft}\n${text}` : text);
  };

  const send = async (message: string) => {
    if (!enabled || !message || submitting) return;
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
        journeyContext: journeyContext.current,
        viewerTimeZone: viewerTimeZone(),
      });
      setEntries((current) => [...current, { id: crypto.randomUUID(), role: "assistant", text: response.text, payload: response }]);
      if (response.journeyContext) journeyContext.current = response.journeyContext;
    } catch (assistantError) {
      setError(assistantError instanceof Error ? assistantError.message : "The assistant is unavailable.");
    } finally {
      setSubmitting(false);
    }
  };

  const tapMicrophone = () => {
    if (recording) {
      voice.stopRecording(appendDraft);
    } else {
      void voice.startRecording();
    }
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

  const voiceStatus = recording ? "Recording…"
    : voice.captureStatus === "requesting" ? "Waiting for microphone permission…"
    : voice.captureStatus === "transcribing" ? "Transcribing…" : "";
  return (
    <section className={`assistant-panel assistant-panel--${kind}`} aria-label={`${kind} assistant`}>
      <header className="assistant-header">
        {passenger && <SignalMark />}
        <div>
          <h2>{passenger ? "Signal" : "Dispatch assistant"}</h2>
          {passenger && <span className="signal-subtitle">Your trip guide</span>}
        </div>
        <span className={`provider-status ${enabled ? "online" : "offline"}`} title={kind === "operations" ? capabilities?.provider : undefined}>
          {kind === "operations" ? enabled ? "Connected" : "AI unavailable" : enabled ? "Chat available" : "Chat unavailable"}
        </span>
        {kind === "operations" && <TransitMark />}
        {onClose && <button className="assistant-close" onClick={onClose} aria-label="Close AI dispatcher">×</button>}
      </header>

      {!enabled && (
        <div className="notice notice--warning">
          {kind === "passenger"
            ? "Trip chat is unavailable. Use the trip planner above, or check station arrivals."
            : capabilities?.reason ?? capabilityError ?? "The model provider is not configured. Live forms and boards still work."}
        </div>
      )}
      {passenger && enabled && capabilities?.sharedChat && (
        <div className="notice">
          Questions go to an AI provider. Don&apos;t include personal information.
        </div>
      )}

      <div className="conversation" ref={conversation}>
        {entries.length === 0 && (
          <div className="assistant-welcome">
            <strong>{kind === "operations" ? "Investigate the live network." : "Get help with your trip."}</strong>
            <p>
              {kind === "operations"
                ? "Ask why a queue is growing, compare an intervention, or request a bounded proposal."
                : "Ask for a route, arrival times, or a trip without stairs."}
            </p>
          </div>
        )}
        {entries.map((entry) => (
          <article key={entry.id} className={`message message--${entry.role}`}>
            <div>
              <div className="message-label">{entry.role === "user" ? "You" : passenger ? "Signal" : "Aspireland"}</div>
            </div>
            {entry.role === "assistant" ? <SafeMarkdown text={entry.text} /> : <p>{entry.text}</p>}
            {!passenger && entry.payload?.tools && entry.payload.tools.length > 0 && (
              <details className="tool-provenance">
                <summary>Sources used</summary>
                <ul>{[...new Set(entry.payload.tools.map((tool) => toolLabels[tool.toLowerCase()] ?? "Live network tool"))].map((label) => <li key={label}>{label}</li>)}</ul>
              </details>
            )}
            {!passenger && entry.payload?.journey && (
              <JourneyCards
                passenger={false}
                result={entry.payload.journey}
                network={network}
                currentGeneration={currentGeneration}
                currentScenarioId={currentScenarioId}
              />
            )}
            {!passenger && entry.payload?.arrivals && network && currentScenarioId !== undefined && currentGeneration !== undefined && (
              <section className="assistant-arrivals" aria-label="Assistant station arrivals">
                <h3>{stationName(network, entry.payload.arrivals.stationId)} arrivals</h3>
                <StationBoardView
                  passenger={false}
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
            {!passenger && entry.payload?.comparison && <ComparisonCard comparison={entry.payload.comparison} currentScenarioId={currentScenarioId} currentGeneration={currentGeneration} />}
            {!passenger && entry.payload?.proposal && (
              <div className="proposal-card">
                <span className="eyebrow">Action proposal</span>
                <h3>{titleCase(entry.payload.proposal.action.kind)}</h3>
                <p>{entry.payload.proposal.explanation}</p>
                <dl>
                  <div><dt>Target</dt><dd>{entry.payload.proposal.action.target ?? "Network"}</dd></div>
                  <div><dt>Value</dt><dd>{entry.payload.proposal.action.value ?? entry.payload.proposal.action.amount ?? "—"}</dd></div>
                  <div><dt>Status</dt><dd>{approvalState[entry.payload.proposal.id] ?? titleCase(entry.payload.proposal.status)}</dd></div>
                </dl>
                <button className="primary-button" onClick={() => void approve(entry.payload!.proposal!)}>Approve and revalidate</button>
              </div>
            )}
          </article>
        ))}
        {submitting && <div className="message message--assistant message--thinking"><span /><span /><span /></div>}
      </div>

      {error && <div className="notice notice--error">{error}</div>}
      {passenger && voice.audioError && <div className="notice notice--warning" role="alert">{voice.audioError}</div>}

      <div className="assistant-composer">
        <label>
          <span className="sr-only">Message the assistant</span>
          <textarea
            value={input}
            onChange={(event) => setInput(event.target.value)}
            onKeyDown={(event) => {
              if (event.key === "Enter" && !event.shiftKey && (!passenger || !event.nativeEvent.isComposing)) {
                event.preventDefault();
                if (!captureBusy) void send(input.trim());
              }
            }}
            placeholder={kind === "operations" ? "Why is Central Exchange backing up?" : "How do I get from University to the airport?"}
            rows={3}
            disabled={!enabled}
          />
        </label>
        <div className={`composer-actions${passenger ? " signal-composer-actions" : ""}`}>
          {voice.canRecord && (
            <button type="button" className={`signal-voice-control${recording ? " signal-recording" : ""}`}
              onClick={tapMicrophone} disabled={!recording && (captureBusy || submitting)}>
              <VoiceIcon kind={recording ? "stop" : "microphone"} />
              {recording ? "Stop recording" : "Start recording"}
            </button>
          )}
          <button className="primary-button" onClick={() => void send(input.trim())} disabled={!enabled || submitting || captureBusy || !input.trim()}>
            {submitting ? "Working…" : "Send"}
          </button>
        </div>
        {voice.canRecord && (
          <div className="signal-voice-caption">
            <span role="status">{voiceStatus}</span>
          </div>
        )}
        {passenger && input.trim() && <small className="transcript-note">Check the words before sending.</small>}
      </div>
    </section>
  );
}

function SignalMark() {
  return (
    <svg className="signal-mark" viewBox="0 0 40 40" aria-hidden="true" focusable="false">
      <rect width="40" height="40" rx="9" fill="currentColor" />
      <path d="M12 32l3-4m13 4-3-4M15 8h10a5 5 0 0 1 5 5v12a3 3 0 0 1-3 3H13a3 3 0 0 1-3-3V13a5 5 0 0 1 5-5Z"
        fill="none" stroke="white" strokeWidth="1.8" strokeLinecap="round" />
      <rect x="13.5" y="12" width="13" height="7" rx="2.5" fill="white" />
      <circle cx="15" cy="23.5" r="1.25" fill="white" />
      <circle cx="25" cy="23.5" r="1.25" fill="white" />
    </svg>
  );
}

function VoiceIcon({ kind }: { kind: "microphone" | "stop" }) {
  return (
    <svg className="signal-voice-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8"
      strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" focusable="false">
      {kind === "stop" ? <rect x="6" y="6" width="12" height="12" rx="2" fill="currentColor" />
        : kind === "microphone" ? (
          <>
            <rect x="9" y="3" width="6" height="12" rx="3" />
            <path d="M6 11v1a6 6 0 0 0 12 0v-1M12 18v3m-3 0h6" />
          </>
        ) : null}
    </svg>
  );
}
