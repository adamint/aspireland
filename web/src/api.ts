import type {
  ActionProposal,
  AiCapabilities,
  AssistantRequest,
  AssistantResponse,
  CommandResult,
  ComparisonResult,
  JourneyRequest,
  JourneyResult,
  ServiceAction,
  StationBoard,
  WorldSnapshot,
} from "./types";

export class ApiError extends Error {
  readonly status: number;

  constructor(message: string, status: number) {
    super(message);
    this.name = "ApiError";
    this.status = status;
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, {
    ...init,
    headers: {
      ...(init?.body instanceof FormData ? {} : { "Content-Type": "application/json" }),
      ...init?.headers,
    },
  });

  if (!response.ok) {
    let message = `${response.status} ${response.statusText}`;
    try {
      const body = (await response.json()) as { error?: string };
      if (body.error) message = body.error;
    } catch {
      // The response may not be JSON for a reverse-proxy failure.
    }
    throw new ApiError(message, response.status);
  }

  if (response.status === 204) return undefined as T;
  return (await response.json()) as T;
}

function post<T>(path: string, body: unknown): Promise<T> {
  return request<T>(path, { method: "POST", body: JSON.stringify(body) });
}

export const api = {
  world: (signal?: AbortSignal) => request<WorldSnapshot>("/api/world", { signal }),
  board: (stationId: string, signal?: AbortSignal) =>
    request<StationBoard>(`/api/stations/${encodeURIComponent(stationId)}/arrivals`, { signal }),
  journey: (body: JourneyRequest) => post<JourneyResult>("/api/journeys", body),
  capabilities: () => request<AiCapabilities>("/api/capabilities"),
  assistant: (body: AssistantRequest) => post<AssistantResponse>("/api/assistant", body),
  clock: (body: { paused?: boolean; speed?: number }) => post<WorldSnapshot>("/api/clock", body),
  scenario: (body: { seed?: number; preset: string; startTime?: string }) =>
    post<WorldSnapshot>("/api/scenarios", body),
  authority: (mode: string) => post<WorldSnapshot>("/api/authority", { mode }),
  action: (
    action: ServiceAction,
    context: { scenarioId: string; generation: number; expectedVersion?: number; reason: string },
  ) =>
    post<CommandResult>("/api/actions", {
      action,
      scenarioId: context.scenarioId,
      generation: context.generation,
      expectedVersion: context.expectedVersion,
      commandId: crypto.randomUUID(),
      reason: context.reason,
    }),
  compare: (action: ServiceAction, scenarioId: string, generation: number) =>
    post<ComparisonResult>("/api/compare", { action, scenarioId, generation }),
  approveProposal: (proposal: ActionProposal) =>
    post<CommandResult>(`/api/proposals/${encodeURIComponent(proposal.id)}/approve`, {
      scenarioId: proposal.scenarioId,
      generation: proposal.generation,
    }),
  transcribe: async (file: Blob) => {
    if (file.size > 10 * 1024 * 1024) throw new ApiError("Recording exceeds the 10 MB limit.", 413);
    const form = new FormData();
    form.append("file", file, "aspireland-recording.webm");
    return request<{ text: string }>("/api/transcribe", { method: "POST", body: form });
  },
};
