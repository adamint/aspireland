import { useLayoutEffect, useRef, useState } from "react";
import { api } from "../api";

type CaptureStatus = "idle" | "requesting" | "recording" | "transcribing";

interface Capture {
  stream?: MediaStream;
  recorder?: MediaRecorder;
  accept?: (text: string) => void;
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

function errorMessage(error: unknown, fallback: string) {
  return (error instanceof Error || error instanceof DOMException) && error.message ? error.message : fallback;
}

export function useAssistantVoice(transcriptionAvailable: boolean) {
  const canRecord = transcriptionAvailable && !!navigator.mediaDevices?.getUserMedia && typeof MediaRecorder !== "undefined";
  const [captureStatus, setCaptureStatus] = useState<CaptureStatus>("idle");
  const [audioError, setAudioError] = useState<string | null>(null);
  const activeCapture = useRef<Capture | null>(null);

  useLayoutEffect(() => {
    if (!canRecord) setCaptureStatus("idle");
    return () => {
      const capture = activeCapture.current;
      activeCapture.current = null;
      if (capture) releaseCapture(capture);
    };
  }, [canRecord]);

  const startRecording = async () => {
    if (!canRecord || activeCapture.current) return;
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

  return { canRecord, captureStatus, audioError, startRecording, stopRecording };
}
