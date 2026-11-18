import { useCallback, useEffect, useRef, useState } from "react";
import { api } from "./api";
import type { StationBoard, WorldSnapshot } from "./types";

interface PollState<T> {
  data: T | null;
  error: string | null;
  loading: boolean;
  lastSuccess: Date | null;
}

export function useWorld(): PollState<WorldSnapshot> & { refresh: () => Promise<void> } {
  const [state, setState] = useState<PollState<WorldSnapshot>>({
    data: null,
    error: null,
    loading: true,
    lastSuccess: null,
  });
  const mounted = useRef(true);
  const nextRequestId = useRef(0);
  const latestSettledRequestId = useRef(0);

  const refresh = useCallback(async () => {
    const requestId = ++nextRequestId.current;
    try {
      const data = await api.world();
      if (mounted.current && requestId > latestSettledRequestId.current) {
        latestSettledRequestId.current = requestId;
        setState({ data, error: null, loading: false, lastSuccess: new Date() });
      }
    } catch (error) {
      if (mounted.current && requestId > latestSettledRequestId.current) {
        latestSettledRequestId.current = requestId;
        setState((current) => ({
          ...current,
          error: error instanceof Error ? error.message : "Unable to load the network.",
          loading: false,
        }));
      }
    }
  }, []);

  useEffect(() => {
    mounted.current = true;
    void refresh();
    const timer = window.setInterval(() => void refresh(), import.meta.env.VITE_ATTENDEE === "true" ? 15000 : 1500);
    return () => {
      mounted.current = false;
      window.clearInterval(timer);
    };
  }, [refresh]);

  return { ...state, refresh };
}

export function useStationBoard(stationId: string | null): PollState<StationBoard> {
  const [state, setState] = useState<PollState<StationBoard>>({
    data: null,
    error: null,
    loading: false,
    lastSuccess: null,
  });

  useEffect(() => {
    if (!stationId) {
      setState({ data: null, error: null, loading: false, lastSuccess: null });
      return;
    }

    let active = true;
    let controller: AbortController | null = null;
    const load = async () => {
      controller?.abort();
      controller = new AbortController();
      setState((current) => ({ ...current, loading: current.data === null }));
      try {
        const data = await api.board(stationId, controller.signal);
        if (active) setState({ data, error: null, loading: false, lastSuccess: new Date() });
      } catch (error) {
        if (active && !(error instanceof DOMException && error.name === "AbortError")) {
          setState((current) => ({
            ...current,
            error: error instanceof Error ? error.message : "Unable to load arrivals.",
            loading: false,
          }));
        }
      }
    };

    void load();
    const timer = window.setInterval(() => void load(), import.meta.env.VITE_ATTENDEE === "true" ? 15000 : 3000);
    return () => {
      active = false;
      controller?.abort();
      window.clearInterval(timer);
    };
  }, [stationId]);

  return state;
}

export function useCapabilities(): PollState<Awaited<ReturnType<typeof api.capabilities>>> {
  const [state, setState] = useState<PollState<Awaited<ReturnType<typeof api.capabilities>>>>({
    data: null,
    error: null,
    loading: true,
    lastSuccess: null,
  });

  useEffect(() => {
    let active = true;
    api
      .capabilities()
      .then((data) => active && setState({ data, error: null, loading: false, lastSuccess: new Date() }))
      .catch((error: unknown) => {
        if (active) {
          setState({
            data: null,
            error: error instanceof Error ? error.message : "AI capabilities are unavailable.",
            loading: false,
            lastSuccess: null,
          });
        }
      });
    return () => {
      active = false;
    };
  }, []);

  return state;
}
