import { useLayoutEffect, useRef, useState } from "react";
import { api } from "../api";

type CaptureStatus = "idle" | "requesting" | "recording" | "transcribing";

interface Capture {
  stream?: MediaStream;
  recorder?: MediaRecorder;
  accept?: (text: string) => void;
}

interface Playback {
  replyId: string;
  source: "manual" | "conversation";
  url?: string;
  audio?: HTMLAudioElement;
}

function releaseCapture(capture: Capture) {
  const { recorder, stream } = capture;
  capture.recorder = undefined;
  capture.stream = undefined;
  if (recorder) {
    recorder.ondataavailable = recorder.onstop = recorder.onerror = null;
    if (recorder.state !== "inactive") recorder.stop();
  }
  stream?.getTracks().forEach((track) => track.stop());
}

function releasePlayback(playback: Playback) {
  if (playback.audio) {
    playback.audio.onended = playback.audio.onerror = null;
    playback.audio.pause();
  }
  if (playback.url) URL.revokeObjectURL(playback.url);
}

function errorMessage(error: unknown, fallback: string) {
  return (error instanceof Error || error instanceof DOMException) && error.message ? error.message : fallback;
}

export function useAssistantVoice(transcriptionAvailable: boolean, speechAvailable: boolean) {
  const canRecord = transcriptionAvailable && !!navigator.mediaDevices?.getUserMedia && typeof MediaRecorder !== "undefined";
  const canPlay = speechAvailable && typeof Audio !== "undefined"
    && typeof URL.createObjectURL === "function" && typeof URL.revokeObjectURL === "function";
  const [captureStatus, setCaptureStatus] = useState<CaptureStatus>("idle");
  const [playback, setPlayback] = useState<{ replyId: string; playing: boolean } | null>(null);
  const [audioError, setAudioError] = useState<string | null>(null);
  const activeCapture = useRef<Capture | null>(null);
  const activePlayback = useRef<Playback | null>(null);

  useLayoutEffect(() => {
    if (!canRecord) setCaptureStatus("idle");
    return () => {
      const capture = activeCapture.current;
      activeCapture.current = null;
      if (capture) releaseCapture(capture);
    };
  }, [canRecord]);

  useLayoutEffect(() => {
    if (!canPlay) setPlayback(null);
    return () => {
      const playing = activePlayback.current;
      activePlayback.current = null;
      if (playing) releasePlayback(playing);
    };
  }, [canPlay]);

  const stopPlayback = () => {
    const playing = activePlayback.current;
    if (!playing) return;
    activePlayback.current = null;
    releasePlayback(playing);
    setPlayback(null);
  };

  const stopConversationPlayback = () => {
    if (activePlayback.current?.source === "conversation") stopPlayback();
  };

  const playReply = async (replyId: string, text: string, source: Playback["source"] = "manual") => {
    if (!canPlay || activeCapture.current) return;
    const sameReply = activePlayback.current?.replyId === replyId;
    stopPlayback();
    if (sameReply) return;
    const playing: Playback = { replyId, source };
    activePlayback.current = playing;
    setAudioError(null);
    setPlayback({ replyId, playing: false });

    const playbackError = (message: string) => {
      if (activePlayback.current !== playing) return;
      stopPlayback();
      setAudioError(message);
    };

    try {
      const blob = await api.speech(text);
      if (activePlayback.current !== playing) return;
      playing.url = URL.createObjectURL(blob);
      const audio = playing.audio = new Audio(playing.url);
      audio.onended = () => {
        if (activePlayback.current === playing) stopPlayback();
      };
      audio.onerror = () => playbackError(audio.error?.message || "Speech playback is unavailable.");
      await audio.play();
      if (activePlayback.current === playing) setPlayback({ replyId, playing: true });
    } catch (error) {
      playbackError(error instanceof DOMException && error.name === "NotAllowedError"
        ? "Playback blocked by the browser. Use the speaker button to try again."
        : errorMessage(error, "Speech playback is unavailable."));
    }
  };

  const startRecording = async () => {
    if (!canRecord || activeCapture.current) return;
    stopPlayback();
    setAudioError(null);
    const capture: Capture = {};
    activeCapture.current = capture;
    setCaptureStatus("requesting");

    const finish = (error?: string) => {
      if (activeCapture.current !== capture) return false;
      activeCapture.current = null;
      releaseCapture(capture);
      setCaptureStatus("idle");
      if (error) setAudioError(error);
      return true;
    };

    try {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
      if (activeCapture.current !== capture) {
        stream.getTracks().forEach((track) => track.stop());
        return;
      }
      capture.stream = stream;
      const recorder = capture.recorder = new MediaRecorder(stream);
      const chunks: Blob[] = [];
      recorder.ondataavailable = ({ data }) => {
        if (data.size) chunks.push(data);
      };
      recorder.onerror = () => finish("The microphone stopped working. Try again or type your question.");
      recorder.onstop = async () => {
        releaseCapture(capture);
        if (activeCapture.current !== capture) return;
        if (!capture.accept || !chunks.length) {
          finish("No audio was recorded. Try again or type your question.");
          return;
        }

        const blob = new Blob(chunks, { type: recorder.mimeType || chunks[0].type || "audio/webm" });
        chunks.length = 0;
        let text: string;
        try {
          text = (await api.transcribe(blob)).text.trim();
        } catch (error) {
          finish(errorMessage(error, "Transcription failed. Try again or type your question."));
          return;
        }
        if (finish(text ? undefined : "No words were recognized. Try again or type your question.")) {
          if (text) capture.accept(text);
        }
      };
      recorder.start();
      setCaptureStatus("recording");
    } catch (error) {
      finish(`Microphone unavailable: ${errorMessage(error, "could not start recording")} You can still type your question.`);
    }
  };

  const stopRecording = (accept: (text: string) => void) => {
    const capture = activeCapture.current;
    if (capture?.recorder?.state !== "recording") return;
    capture.accept = accept;
    setCaptureStatus("transcribing");
    capture.recorder.stop();
  };

  return { canRecord, canPlay, captureStatus, playback, audioError, startRecording, stopRecording, playReply, stopConversationPlayback };
}
