import { useEffect, useMemo, useRef, useState, type KeyboardEvent, type PointerEvent } from "react";
import { isWaitingCohort } from "../cohorts";
import { lineFor } from "../format";
import { publicOnboardCrowding, publicTrainLabel, publicTrainStatus } from "../passengerData";
import type {
  Incident,
  MapLayer,
  MapMode,
  MovementGrant,
  NetworkDefinition,
  StationDefinition,
  TrainState,
  WorldState,
} from "../types";

interface NetworkMapProps {
  network: NetworkDefinition;
  world: WorldState;
  selectedStationId?: string | null;
  selectedTrainId?: string | null;
  onSelectStation: (stationId: string) => void;
  onSelectTrain?: (trainId: string) => void;
  calm?: boolean;
  stale?: boolean;
}

interface Point {
  x: number;
  y: number;
}

export const MAP_WIDTH = 1550;
export const MAP_HEIGHT = 1200;
const MAP_ASPECT = MAP_HEIGHT / MAP_WIDTH;
const MIN_VIEW_WIDTH = 620;
const AX_CONTROL_ONE = { x: 320, y: 1120 };
const AX_CONTROL_TWO = { x: 650, y: 820 };
const DEFAULT_SNAPSHOT_INTERVAL_MS = 1500;

const SCHEMATIC: Record<string, Point> = {
  pinecrest: { x: 100, y: 430 },
  westhaven: { x: 300, y: 430 },
  "west-junction": { x: 500, y: 500 },
  "civic-square": { x: 670, y: 500 },
  "central-exchange": { x: 850, y: 500 },
  "union-terminal": { x: 1030, y: 500 },
  "east-junction": { x: 1200, y: 500 },
  riverbank: { x: 1340, y: 350 },
  eastgate: { x: 1460, y: 220 },
  airport: { x: 100, y: 1100 },
  aerocity: { x: 300, y: 830 },
  stadium: { x: 1340, y: 700 },
  fairgrounds: { x: 1460, y: 850 },
  northgate: { x: 1120, y: 90 },
  university: { x: 1040, y: 275 },
  "museum-quarter": { x: 920, y: 710 },
  "medical-center": { x: 790, y: 865 },
  southbank: { x: 720, y: 1080 },
};

const linePatterns: Record<string, string | undefined> = {
  Blue: undefined,
  Amber: "14 8",
  Green: "3 7",
  AX: "22 8 4 8",
};

function useReducedMotion(): boolean {
  const [reduced, setReduced] = useState(() => window.matchMedia?.("(prefers-reduced-motion: reduce)").matches ?? false);
  useEffect(() => {
    const query = window.matchMedia?.("(prefers-reduced-motion: reduce)");
    if (!query) return;
    const update = () => setReduced(query.matches);
    query.addEventListener("change", update);
    return () => query.removeEventListener("change", update);
  }, []);
  return reduced;
}

export interface ClockAnchor {
  key: string;
  scenarioId: string;
  generation: number;
  simulatedMs: number;
  targetSimulatedMs: number;
  monotonicMs: number;
  authoritativeMs: number;
  authoritativeMonotonicMs: number;
  speed: number;
  paused: boolean;
}

type ClockWorld = Pick<WorldState, "scenarioId" | "generation" | "version" | "simTime" | "speed" | "paused">;

export function reconcileClockAnchor(previous: ClockAnchor | null, world: ClockWorld, monotonicNow: number): ClockAnchor {
  const incoming = new Date(world.simTime).getTime();
  const key = `${world.scenarioId}:${world.generation}:${world.version}:${world.simTime}:${world.speed}:${world.paused}`;
  const scenarioChanged = previous && (previous.scenarioId !== world.scenarioId || previous.generation !== world.generation);
  const authoritativeMovedBackward = previous && incoming < previous.authoritativeMs;
  if (!previous || scenarioChanged || authoritativeMovedBackward || world.paused) {
    return {
      key,
      scenarioId: world.scenarioId,
      generation: world.generation,
      simulatedMs: incoming,
      targetSimulatedMs: incoming,
      monotonicMs: monotonicNow,
      authoritativeMs: incoming,
      authoritativeMonotonicMs: monotonicNow,
      speed: 0,
      paused: world.paused,
    };
  }
  if (incoming === previous.authoritativeMs) {
    if (previous.paused) {
      return {
        ...previous,
        key,
        simulatedMs: incoming,
        targetSimulatedMs: incoming,
        monotonicMs: monotonicNow,
        authoritativeMonotonicMs: monotonicNow,
        speed: 0,
        paused: false,
      };
    }
    return { ...previous, key, speed: Math.min(previous.speed, world.speed), paused: false };
  }
  const rendered = readAnimatedSimTime(previous, monotonicNow, false);
  const elapsedMonotonic = previous.paused
    ? DEFAULT_SNAPSHOT_INTERVAL_MS
    : Math.max(1, monotonicNow - previous.authoritativeMonotonicMs);
  const observedSpeed = Math.min(world.speed, Math.max(0, incoming - rendered) / elapsedMonotonic);
  return {
    key,
    scenarioId: world.scenarioId,
    generation: world.generation,
    simulatedMs: rendered,
    targetSimulatedMs: incoming,
    monotonicMs: monotonicNow,
    authoritativeMs: incoming,
    authoritativeMonotonicMs: monotonicNow,
    speed: observedSpeed,
    paused: world.paused,
  };
}

export function readAnimatedSimTime(anchor: ClockAnchor, monotonicNow: number, reducedMotion: boolean): number {
  if (anchor.paused || reducedMotion) return anchor.targetSimulatedMs;
  const elapsed = Math.max(0, monotonicNow - anchor.monotonicMs);
  return Math.min(anchor.targetSimulatedMs, anchor.simulatedMs + elapsed * anchor.speed);
}

function useAnimatedSimTime(world: WorldState, reducedMotion: boolean): number {
  const [, renderFrame] = useState(0);
  const key = `${world.scenarioId}:${world.generation}:${world.version}:${world.simTime}:${world.speed}:${world.paused}`;
  const monotonicNow = performance.now();
  const anchor = useRef<ClockAnchor>(reconcileClockAnchor(null, world, monotonicNow));

  if (anchor.current.key !== key) {
    anchor.current = reconcileClockAnchor(anchor.current, world, monotonicNow);
  }

  useEffect(() => {
    if (world.paused || reducedMotion) return;
    let frame = 0;
    const animate = () => {
      renderFrame((value) => value + 1);
      frame = window.requestAnimationFrame(animate);
    };
    frame = window.requestAnimationFrame(animate);
    return () => window.cancelAnimationFrame(frame);
  }, [reducedMotion, world.paused]);

  return readAnimatedSimTime(anchor.current, performance.now(), reducedMotion);
}

function keyActivate(event: KeyboardEvent<SVGGElement>, action: () => void) {
  if (event.key === "Enter" || event.key === " ") {
    event.preventDefault();
    action();
  }
}

export function trainPosition(
  train: TrainState,
  grant: MovementGrant | undefined,
  points: Map<string, Point>,
  simulatedNow: number,
  reducedMotion: boolean,
): Point | null {
  const fromId = grant?.from ?? train.stationId;
  const from = points.get(fromId);
  if (!from) return null;
  if (!grant || grant.completed || reducedMotion) return from;
  const to = points.get(grant.to);
  if (!to) return from;
  const start = new Date(grant.departsAt).getTime();
  const end = new Date(grant.arrivesAt).getTime();
  if (end <= start) return from;
  const fraction = Math.max(0, Math.min(1, (simulatedNow - start) / (end - start)));
  if (
    train.line === "AX" &&
    ((grant.from === "airport" && grant.to === "central-exchange") ||
      (grant.from === "central-exchange" && grant.to === "airport"))
  ) {
    const forward = grant.from === "airport";
    const p0 = forward ? from : to;
    const p3 = forward ? to : from;
    const p1 = AX_CONTROL_ONE;
    const p2 = AX_CONTROL_TWO;
    const t = forward ? fraction : 1 - fraction;
    const inverse = 1 - t;
    return {
      x:
        inverse ** 3 * p0.x +
        3 * inverse ** 2 * t * p1.x +
        3 * inverse * t ** 2 * p2.x +
        t ** 3 * p3.x,
      y:
        inverse ** 3 * p0.y +
        3 * inverse ** 2 * t * p1.y +
        3 * inverse * t ** 2 * p2.y +
        t ** 3 * p3.y,
    };
  }
  return {
    x: from.x + (to.x - from.x) * fraction,
    y: from.y + (to.y - from.y) * fraction,
  };
}

function CitySprite({ station }: { station: StationDefinition }) {
  const { x, y, character, id } = station;

  if (character === "airport") {
    return (
      <g className="city-sprite" transform={`translate(${x - 72} ${y - 82})`} aria-hidden="true">
        <path d="M8 50h132v34H8z" fill="#d8e6e9" />
        <path d="M18 34h98l24 16H8z" fill="#edf4f2" />
        <path d="M26 16h15v20H26zm82-13h12v34h-12z" fill="#617984" />
        <path d="M113 3l18 10h-18z" fill="#f6fbff" />
        <path d="M19 58h109v12H19z" fill="#6eb7ca" />
        <path d="M10 84h136v9H10z" fill="#3d5661" />
        <path d="M-14 20l36 10-15 7-19-4-9 9-7-3 8-12z" fill="#f5faf9" />
      </g>
    );
  }

  if (character === "stadium") {
    return (
      <g className="city-sprite" transform={`translate(${x - 46} ${y - 58})`} aria-hidden="true">
        <ellipse cx="46" cy="39" rx="43" ry="28" fill="#6d553c" />
        <ellipse cx="46" cy="35" rx="31" ry="18" fill="#67a259" />
        <path d="M3 34v18l12 12h62l12-12V34H77v16H15V34z" fill="#f5c46b" />
        <path d="M22 30h48v6H22z" fill="#f7e6bd" />
      </g>
    );
  }

  if (character === "campus") {
    return (
      <g className="city-sprite" transform={`translate(${x - 44} ${y - 61})`} aria-hidden="true">
        <path d="M6 57h76v14H6z" fill="#5b654d" />
        <path d="M14 30h58v28H14z" fill="#b9d8a3" />
        <path d="M8 30L43 8l35 22z" fill="#789360" />
        <path d="M23 35h8v23h-8zm18 0h8v23h-8zm18 0h8v23h-8z" fill="#f0ebc9" />
        <circle cx="7" cy="22" r="10" fill="#4a8f69" />
        <rect x="4" y="26" width="6" height="20" fill="#6c533c" />
      </g>
    );
  }

  if (character === "business") {
    return (
      <g className="city-sprite" transform={`translate(${x - 53} ${y - 96})`} aria-hidden="true">
        <path d="M8 28h29v68H8zM42 4h35v92H42zM82 43h22v53H82z" fill="#8faab9" />
        <path d="M13 36h19M13 48h19M13 60h19M47 15h25M47 28h25M47 41h25M47 54h25M47 67h25M87 51h12M87 63h12" stroke="#d8eff0" strokeWidth="5" />
        <path d="M1 96h109v9H1z" fill="#45545d" />
      </g>
    );
  }

  if (character === "government") {
    return (
      <g className="city-sprite" transform={`translate(${x - 50} ${y - 72})`} aria-hidden="true">
        <path d="M12 38h76v35H12z" fill="#cfccb2" />
        <path d="M5 38L50 13l45 25z" fill="#e7e2c8" />
        <path d="M45 1h10v13H45z" fill="#879da0" />
        <path d="M22 43h9v30h-9zm16 0h9v30h-9zm16 0h9v30h-9zm16 0h9v30h-9z" fill="#f5f1d9" />
        <path d="M5 73h90v10H5z" fill="#66736e" />
      </g>
    );
  }

  if (character === "terminal") {
    return (
      <g className="city-sprite" transform={`translate(${x - 61} ${y - 73})`} aria-hidden="true">
        <path d="M7 33h113v48H7z" fill="#b6a98d" />
        <path d="M1 33L30 14h67l29 19z" fill="#d4c6a7" />
        <path d="M17 45h93v12H17z" fill="#8fc3ca" />
        <path d="M26 81V57m25 24V57m25 24V57m25 24V57" stroke="#5d6763" strokeWidth="7" />
        <circle cx="63" cy="27" r="10" fill="#f4eee0" stroke="#5d6763" strokeWidth="4" />
        <path d="M0 81h126v9H0z" fill="#49575a" />
      </g>
    );
  }

  if (character === "medical") {
    return (
      <g className="city-sprite" transform={`translate(${x - 47} ${y - 76})`} aria-hidden="true">
        <path d="M7 20h80v61H7z" fill="#dce4df" />
        <path d="M18 31h18v13H18zm40 0h18v13H58zM18 55h18v13H18zm40 0h18v13H58z" fill="#9ccbd0" />
        <path d="M40 27h14v42H40zM27 41h40v14H27z" fill="#d55d63" />
        <path d="M0 81h94v9H0z" fill="#66716c" />
      </g>
    );
  }

  if (character === "museum") {
    return (
      <g className="city-sprite" transform={`translate(${x - 49} ${y - 69})`} aria-hidden="true">
        <path d="M9 34h80v39H9z" fill="#c9c0a8" />
        <path d="M3 34L49 9l46 25z" fill="#e0d8c0" />
        <path d="M19 40h9v33h-9zm17 0h9v33h-9zm17 0h9v33h-9zm17 0h9v33h-9z" fill="#eee8d5" />
        <path d="M3 73h92v9H3z" fill="#6b7167" />
      </g>
    );
  }

  if (character === "hotel") {
    return (
      <g className="city-sprite" transform={`translate(${x - 42} ${y - 85})`} aria-hidden="true">
        <path d="M7 8h70v82H7z" fill="#b69b8c" />
        <path d="M16 20h13v10H16zm22 0h13v10H38zm22 0h10v10H60zM16 39h13v10H16zm22 0h13v10H38zm22 0h10v10H60zM16 58h13v10H16zm44 0h10v10H60z" fill="#dce9df" />
        <path d="M33 58h18v32H33z" fill="#4d6870" />
        <path d="M1 90h82v9H1z" fill="#59645f" />
        <text x="42" y="80" fill="#f5d58a" textAnchor="middle" fontSize="8" fontWeight="700">AERO</text>
      </g>
    );
  }

  if (character === "junction") {
    return (
      <g className="city-sprite" transform={`translate(${x - 46} ${y - 65})`} aria-hidden="true">
        <path d="M8 35h58v34H8z" fill="#a69c82" />
        <path d="M16 18h42l8 17H8z" fill="#d6c7a4" />
        <path d="M25 44h24v25H25z" fill="#6d8990" />
        <path d="M67 10h9v59h-9zM76 14l14 7-14 7z" fill="#637276" />
        <path d="M1 69h91v9H1z" fill="#4a575a" />
      </g>
    );
  }

  if (character === "riverfront") {
    return (
      <g className="city-sprite" transform={`translate(${x - 55} ${y - 61})`} aria-hidden="true">
        <path d="M8 25h38v40H8zM52 36h49v29H52z" fill="#c49a78" />
        <path d="M3 25L27 9l24 16zM47 36l29-18 30 18z" fill="#845f50" />
        <path d="M16 38h21v12H16zm47 8h27v10H63z" fill="#a8d3d3" />
        <path d="M0 65h111v8H0zM18 73v11m28-11v11m28-11v11m28-11v11" stroke="#5a5650" strokeWidth="6" />
      </g>
    );
  }

  if (character === "leisure") {
    return (
      <g className="city-sprite" transform={`translate(${x - 52} ${y - 73})`} aria-hidden="true">
        <circle cx="33" cy="34" r="27" fill="none" stroke="#dfb15b" strokeWidth="7" />
        <path d="M33 7v54M6 34h54M14 15l38 38m0-38L14 53M22 73h22L33 34z" stroke="#7b6651" strokeWidth="4" fill="none" />
        <path d="M63 36h39v37H63z" fill="#d87967" />
        <path d="M57 36l26-22 25 22z" fill="#f0d179" />
      </g>
    );
  }

  const residentialPalette: Record<string, [string, string]> = {
    pinecrest: ["#b8c89c", "#6f8062"],
    westhaven: ["#d0b28d", "#835f51"],
    eastgate: ["#bdc5d3", "#66778c"],
    northgate: ["#b3c7ba", "#587667"],
    southbank: ["#d2b1a0", "#8a6658"],
  };
  const [wall, roof] = residentialPalette[id] ?? ["#d3b895", "#765b4a"];
  return (
    <g className="city-sprite" transform={`translate(${x - 53} ${y - 54})`} aria-hidden="true">
      <path d="M3 31L24 13l21 18v31H3zM52 26L76 6l25 20v36H52z" fill={wall} />
      <path d="M0 32L24 8l24 24-6 6-18-17L6 38zm47-5L76 0l30 27-7 7-23-20-22 20z" fill={roof} />
      <path d="M15 42h13v20H15zm53-3h16v23H68z" fill="#536d72" />
      <path d="M-4 62h114v8H-4z" fill="#69745f" />
      <circle cx="45" cy="48" r="10" fill="#558b62" />
      <rect x="42" y="52" width="6" height="14" fill="#6b5341" />
    </g>
  );
}

function CityBackdrop() {
  return (
    <g className="city-backdrop" aria-hidden="true">
      <rect width={MAP_WIDTH} height={MAP_HEIGHT} fill="url(#cityGround)" />

      <path d="M0 0h360l55 145-86 120-174 18L0 245z" fill="#416f4d" />
      <path d="M0 690l180-38 170 76 118 196-70 276H0z" fill="#477a51" />
      <path d="M530 975l185-62 186 63 101 224H470z" fill="#477d56" />
      <path d="M1325 0h225v235l-108 42-104-95z" fill="#477851" />
      <path d="M1350 905l200-72v367h-270l10-152z" fill="#46774d" />

      <path d="M1235-40C1190 190 1248 322 1175 472s-43 307-105 455-45 235-85 323" fill="none" stroke="#367986" strokeWidth="132" />
      <path d="M1235-40C1190 190 1248 322 1175 472s-43 307-105 455-45 235-85 323" fill="none" stroke="#6cb0bd" strokeWidth="84" />
      <path d="M1235-40C1190 190 1248 322 1175 472s-43 307-105 455-45 235-85 323" fill="none" stroke="#9bc8cc" strokeWidth="11" strokeDasharray="38 30" opacity=".55" />

      <g className="city-roads" fill="none" strokeLinecap="square">
        <path d="M40 305L500 500h710l225-245M170 610l330-110M500 500L300 830 100 1100M850 500L720 1080M1030 500l300 200 130 145M1040 275l-10 225M920 710l-70-210" stroke="#314f4e" strokeWidth="25" />
        <path d="M40 305L500 500h710l225-245M170 610l330-110M500 500L300 830 100 1100M850 500L720 1080M1030 500l300 200 130 145M1040 275l-10 225M920 710l-70-210" stroke="#8a967e" strokeWidth="5" strokeDasharray="13 17" />
      </g>

      <g className="airport-campus">
        <path d="M15 1165L250 900l45 37L60 1200z" fill="#353d3d" />
        <path d="M42 1160L257 922" stroke="#e4e3d3" strokeWidth="7" strokeDasharray="34 24" />
        <path d="M8 1085L235 865l25 26L33 1110z" fill="#5d6964" />
        <path d="M185 1060h205v94H185z" fill="#667477" />
        <path d="M202 1077h171v60H202z" fill="#879798" />
        <path d="M205 1082h42v50h-42zm59 0h42v50h-42zm59 0h42v50h-42z" fill="#758587" />
        <path d="M227 1005h9v67m52-94h9v94m48-124h9v124" stroke="#d9b953" strokeWidth="5" />
        <text x="32" y="1025" className="map-district-label">ASPIRELAND AIRFIELD</text>
      </g>

      <g className="industrial-district">
        <path d="M205 710h276v213H205z" fill="#4b5d5c" opacity=".62" />
        <path d="M220 748h72v65h-72zm87-14h88v79h-88zm-69 84h103v72H238zm119 13h101v59H357z" fill="#7c8982" />
        <path d="M220 748l36-18 36 18m15-14l44-22 44 22m-157 84l51-23 52 23m16 13l50-22 51 22" fill="#9fa99c" />
        <path d="M276 688h14v60h-14zm118-40h17v87h-17z" fill="#5d6a68" />
        <path d="M284 690c10-24 31-29 43-8M402 653c8-27 36-35 50-11" fill="none" stroke="#b7c2b7" strokeWidth="10" opacity=".4" />
        <text x="220" y="910" className="map-district-label">AEROCITY WORKS</text>
      </g>

      <g className="core-buildings">
        <path d="M575 338h68v128h-68zm82-42h55v170h-55zm69 61h63v109h-63zm81-98h65v207h-65zm80 46h72v161h-72zm85-77h63v238h-63zm77 97h60v141h-60z" fill="#647d83" />
        <path d="M584 353h49M584 374h49M584 395h49M666 314h37M666 339h37M666 364h37M816 276h47M816 302h47M816 328h47M981 246h45M981 273h45M981 300h45M981 327h45" stroke="#a9cfd0" strokeWidth="7" />
        <path d="M552 466h590v18H552z" fill="#435558" />
        <text x="725" y="205" className="map-district-label">ASPIRELAND CORE</text>
      </g>

      <g className="university-district">
        <path d="M915 128h247v174H915z" fill="#4f8058" />
        <path d="M942 170h72v65h-72zm108 10h83v55h-83z" fill="#b2c59e" />
        <path d="M936 170l42-28 42 28m24 10l47-30 48 30" fill="#7d9369" />
        <path d="M1016 144v116M918 244h243" stroke="#b5d0a7" strokeWidth="10" />
        <circle cx="1038" cy="244" r="21" fill="#6ea06e" />
        <text x="930" y="286" className="map-district-label">UNIVERSITY GREEN</text>
      </g>

      <g className="west-neighborhood">
        <path d="M42 260h395v230H42z" fill="#547558" opacity=".38" />
        <path d="M72 322h78v55H72zm103 22h71v51h-71zm101 16h82v55h-82zm-190 61h96v46H86zm122 8h74v48h-74zm102 8h91v42h-91z" fill="#a79f82" />
        <path d="M66 322l45-27 45 27m13 22l42-25 41 25m18 16l47-29 47 29" fill="#735e50" />
      </g>

      <g className="east-neighborhood">
        <path d="M1300 100h250v320l-118 22-121-97z" fill="#526f57" opacity=".45" />
        <path d="M1355 185h70v52h-70zm86-42h76v57h-76zm-45 87h89v54h-89zm-73 82h85v51h-85zm102 8h91v57h-91z" fill="#9da9ad" />
        <path d="M1349 185l41-24 41 24m4-42l44-25 44 25m-133 87l50-29 51 29" fill="#5f7180" />
      </g>

      <g className="southbank-district">
        <path d="M580 935h346v265H580z" fill="#4f7855" opacity=".48" />
        <path d="M620 1000h72v54h-72zm92-38h84v64h-84zm105 18h73v56h-73zm-183 80h94v59h-94zm118 24h111v52H752z" fill="#b49b8d" />
        <path d="M614 1000l42-26 42 26m8-38l48-29 48 29m9 18l43-25 42 25" fill="#765d52" />
        <path d="M560 942c82-47 239-58 380-8" fill="none" stroke="#7fab70" strokeWidth="24" />
        <text x="610" y="1170" className="map-district-label">SOUTHBANK GARDENS</text>
      </g>

      <g className="river-bridges">
        <path d="M1182 485l191-154" stroke="#d5c8a7" strokeWidth="28" />
        <path d="M1182 485l191-154" stroke="#5c6662" strokeWidth="8" strokeDasharray="18 12" />
        <path d="M1260 650l106 87" stroke="#d5c8a7" strokeWidth="24" />
        <path d="M1260 650l106 87" stroke="#5c6662" strokeWidth="7" strokeDasharray="16 11" />
      </g>

      <text x="55" y="205" className="map-district-label">PINECREST RIDGE</text>
      <text x="1260" y="560" className="map-district-label">RIVERBANK</text>
      <text x="1280" y="935" className="map-district-label">FAIRGROUNDS</text>
    </g>
  );
}

export function NetworkMap({
  network,
  world,
  selectedStationId,
  selectedTrainId,
  onSelectStation,
  onSelectTrain,
  calm = false,
  stale = false,
}: NetworkMapProps) {
  const [mode, setMode] = useState<MapMode>("city");
  const [layer, setLayer] = useState<MapLayer>("service");
  const [view, setView] = useState({ x: 0, y: 0, width: MAP_WIDTH, height: MAP_HEIGHT });
  const drag = useRef<{ x: number; y: number; viewX: number; viewY: number } | null>(null);
  const reducedMotion = useReducedMotion();
  const simulatedNow = useAnimatedSimTime(world, reducedMotion);
  const visibleTrains = useMemo(
    () => calm ? world.trains.filter((train) => !["reserve", "withdrawn"].includes(train.state)) : world.trains,
    [calm, world.trains],
  );

  const points = useMemo(
    () =>
      new Map(
        network.stations.map((station) => [
          station.id,
          mode === "schematic" ? (SCHEMATIC[station.id] ?? { x: station.x, y: station.y }) : { x: station.x, y: station.y },
        ]),
      ),
    [mode, network.stations],
  );

  const queueByStation = useMemo(() => {
    const totals = new Map<string, number>();
    for (const cohort of world.cohorts) {
      if (!isWaitingCohort(cohort, world.simTime)) continue;
      totals.set(cohort.stationId, (totals.get(cohort.stationId) ?? 0) + cohort.count);
    }
    return totals;
  }, [world.cohorts]);

  const incidentByTarget = useMemo(() => {
    const lookup = new Map<string, Incident[]>();
    for (const incident of world.incidents.filter((item) => item.state === "active")) {
      const current = lookup.get(incident.target) ?? [];
      current.push(incident);
      lookup.set(incident.target, current);
    }
    return lookup;
  }, [world.incidents]);

  const grantsById = useMemo(
    () => new Map(Object.values(world.grants).map((grant) => [grant.id, grant])),
    [world.grants],
  );

  const trainVisuals = useMemo(() => {
    const groups = new Map<string, Array<{ train: TrainState; point: Point }>>();
    for (const train of visibleTrains) {
      const grant = train.activeGrantId ? grantsById.get(train.activeGrantId) : undefined;
      const authoritativeGrant = grant?.trainId === train.id && !grant.completed ? grant : undefined;
      const point = trainPosition(train, authoritativeGrant, points, simulatedNow, reducedMotion);
      if (!point) continue;
      const key = `${Math.round(point.x / 24)}:${Math.round(point.y / 24)}`;
      const group = groups.get(key) ?? [];
      group.push({ train, point });
      groups.set(key, group);
    }

    return [...groups.values()].flatMap((group) => {
      const sorted = group.sort((left, right) => left.train.id.localeCompare(right.train.id));
      return sorted.map((visual, index) => {
        if (sorted.length === 1) return { ...visual, offset: { x: 0, y: calm ? -34 : 0 } };
        const row = Math.floor(index / 3);
        const rowStart = row * 3;
        const rowSize = Math.min(3, sorted.length - rowStart);
        const column = index - rowStart;
        return {
          ...visual,
          offset: {
            x: (column - (rowSize - 1) / 2) * 44,
            y: (calm ? -34 : -22) - row * 31,
          },
        };
      });
    }).sort((left, right) => left.train.id.localeCompare(right.train.id));
  }, [calm, grantsById, points, reducedMotion, simulatedNow, visibleTrains]);

  const directoryTrains = useMemo(
    () => [...visibleTrains].sort((left, right) => left.id.localeCompare(right.id)),
    [visibleTrains],
  );

  const axPath = useMemo(() => {
    const airport = points.get("airport") ?? { x: 100, y: 1100 };
    const central = points.get("central-exchange") ?? { x: 850, y: 500 };
    const union = points.get("union-terminal") ?? { x: 1030, y: 500 };
    return `M ${airport.x} ${airport.y} C ${AX_CONTROL_ONE.x} ${AX_CONTROL_ONE.y}, ${AX_CONTROL_TWO.x} ${AX_CONTROL_TWO.y}, ${central.x} ${central.y} L ${union.x} ${union.y}`;
  }, [points]);

  const zoomBy = (factor: number) => {
    setView((current) => {
      const nextWidth = Math.max(MIN_VIEW_WIDTH, Math.min(MAP_WIDTH, current.width * factor));
      const nextHeight = nextWidth * MAP_ASPECT;
      return {
        x: Math.max(0, Math.min(MAP_WIDTH - nextWidth, current.x + (current.width - nextWidth) / 2)),
        y: Math.max(0, Math.min(MAP_HEIGHT - nextHeight, current.y + (current.height - nextHeight) / 2)),
        width: nextWidth,
        height: nextHeight,
      };
    });
  };

  const onPointerDown = (event: PointerEvent<SVGSVGElement>) => {
    if (event.pointerType !== "mouse" || event.button !== 0) return;
    if ((event.target as Element).closest("[data-map-action]")) return;
    event.currentTarget.setPointerCapture(event.pointerId);
    drag.current = { x: event.clientX, y: event.clientY, viewX: view.x, viewY: view.y };
  };

  const onPointerMove = (event: PointerEvent<SVGSVGElement>) => {
    if (!drag.current) return;
    const scale = view.width / event.currentTarget.clientWidth;
    const nextX = drag.current.viewX - (event.clientX - drag.current.x) * scale;
    const nextY = drag.current.viewY - (event.clientY - drag.current.y) * scale;
    setView((current) => ({
      ...current,
      x: Math.max(0, Math.min(MAP_WIDTH - current.width, nextX)),
      y: Math.max(0, Math.min(MAP_HEIGHT - current.height, nextY)),
    }));
  };

  const resetView = () => setView({ x: 0, y: 0, width: MAP_WIDTH, height: MAP_HEIGHT });

  const trainMarkers = (
    <g className="map-trains">
      {trainVisuals.map(({ train, point, offset }) => {
        const line = lineFor(network, train.line);
        const selected = train.id === selectedTrainId;
        const occupancy = publicOnboardCrowding(train.load, train.capacity, stale);
        const crowding = calm ? `Onboard crowding: ${occupancy.label}. ${occupancy.detail}` :
          train.capacity > 0 ? `${Math.round(train.load / train.capacity * 100)}% full` : "capacity unknown";
        const hasIncident = (incidentByTarget.get(train.id)?.length ?? 0) > 0;
        return (
          <g
            key={train.id}
            transform={`translate(${point.x + offset.x} ${point.y + offset.y})`}
            className={`map-train ${selected ? "selected" : ""} ${hasIncident ? "has-incident" : ""}`}
            role={onSelectTrain ? "button" : undefined}
            tabIndex={onSelectTrain ? 0 : undefined}
            aria-hidden={onSelectTrain ? undefined : true}
            aria-pressed={onSelectTrain && calm ? selected : undefined}
            aria-label={onSelectTrain ? `${train.id}, ${calm ? publicTrainLabel(train, network) : line?.name ?? train.line}, ${crowding}, ${calm ? publicTrainStatus(train.state) : train.state}${!calm && train.holdReason ? `, ${train.holdReason}` : ""}` : undefined}
            data-map-action={onSelectTrain ? true : undefined}
            pointerEvents={onSelectTrain ? undefined : "none"}
            onPointerDown={onSelectTrain && calm ? (event) => {
              if (event.button === 0) event.currentTarget.setPointerCapture?.(event.pointerId);
            } : undefined}
            onClick={onSelectTrain ? (event) => {
              if (calm) event.currentTarget.focus({ preventScroll: true });
              onSelectTrain(train.id);
            } : undefined}
            onKeyDown={onSelectTrain ? (event) => keyActivate(event, () => onSelectTrain(train.id)) : undefined}
          >
            <rect x="-18" y="-11" width="36" height="22" rx="5" fill={line?.color ?? "#fff"} />
            {!calm && <>
              <rect x="-11" y="-6" width="14" height="8" rx="1" className="train-window" />
              <path d="M8-7h6v14H8z" className="train-window" />
              <path d="M-12 12h24" />
              <text y="-16" textAnchor="middle">{train.id}</text>
            </>}
            {onSelectTrain && calm && (
              <circle className="map-train-hit-target" r="1" fill="transparent" stroke="transparent" strokeWidth="44" vectorEffect="non-scaling-stroke" pointerEvents="all" />
            )}
          </g>
        );
      })}
    </g>
  );

  return (
    <section className={`network-map-shell ${calm ? "network-map-shell--calm" : ""}`} aria-label="Aspireland network map">
      <div className="map-toolbar">
        <div className="segmented" aria-label="Map style">
          <button className={mode === "city" ? "active" : ""} onClick={() => setMode("city")}>City</button>
          <button className={mode === "schematic" ? "active" : ""} onClick={() => setMode("schematic")}>Schematic</button>
        </div>
        <div className="segmented" aria-label="Map layer">
          {(["service", "crowding", "incidents"] as MapLayer[]).map((value) => (
            <button key={value} className={layer === value ? "active" : ""} onClick={() => setLayer(value)}>
              {calm && value === "crowding" ? "People waiting" : value[0].toUpperCase() + value.slice(1)}
            </button>
          ))}
        </div>
        <div className="map-zoom-controls" aria-label="Map zoom controls">
          <button onClick={() => zoomBy(.8)} aria-label="Zoom in">+</button>
          <button onClick={() => zoomBy(1.25)} aria-label="Zoom out">−</button>
          <button className="map-reset" onClick={resetView}>Fit network</button>
        </div>
      </div>

      {calm && layer === "crowding" && <p className="map-crowding-key">{stale ? "Last reported numbers" : "Numbers"} show people waiting to enter or board, not platform totals.</p>}
      <div className="map-frame">
        <svg
          className="network-map"
          viewBox={`${view.x} ${view.y} ${view.width} ${view.height}`}
          role={calm ? "group" : "img"}
          aria-label="Interactive Aspireland metro map. Drag with a mouse to pan and use the zoom controls above."
          onPointerDown={onPointerDown}
          onPointerMove={onPointerMove}
          onPointerUp={() => (drag.current = null)}
          onPointerCancel={() => (drag.current = null)}
        >
          <defs>
            <pattern id="cityGround" width="24" height="24" patternUnits="userSpaceOnUse">
              <rect width="24" height="24" fill="#2c514a" />
              <path d="M0 23h24M23 0v24" stroke="#315b51" strokeWidth="2" />
            </pattern>
            <filter id="mapShadow" x="-40%" y="-40%" width="180%" height="180%">
              <feDropShadow dx="0" dy="4" stdDeviation="3" floodColor="#071016" floodOpacity=".65" />
            </filter>
          </defs>

          {mode === "city" ? <CityBackdrop /> : <rect className="schematic-background" width={MAP_WIDTH} height={MAP_HEIGHT} />}

          <g className="route-underlay" fill="none" strokeLinecap="round" strokeLinejoin="round">
            {network.lines.filter((line) => line.id !== "AX").map((line) => {
              const route = line.stations.map((stationId) => points.get(stationId)).filter(Boolean) as Point[];
              return (
                <polyline
                  key={`${line.id}-underlay`}
                  points={route.map((point) => `${point.x},${point.y}`).join(" ")}
                  stroke={calm ? "#fff" : "#071219"}
                  strokeWidth="18"
                  opacity=".9"
                />
              );
            })}
            <path
              d={axPath}
              stroke={calm ? "#fff" : "#071219"}
              strokeWidth="20"
            />
          </g>

          <g className="routes" fill="none" strokeLinecap="square" strokeLinejoin="round">
            {network.lines.filter((line) => line.id !== "AX").map((line) => {
              const route = line.stations.map((stationId) => points.get(stationId)).filter(Boolean) as Point[];
              return (
                <polyline
                  key={line.id}
                  points={route.map((point) => `${point.x},${point.y}`).join(" ")}
                  stroke={line.color}
                  strokeWidth="9"
                  strokeDasharray={linePatterns[line.id]}
                />
              );
            })}
            <path
              d={axPath}
              stroke={lineFor(network, "AX")?.color ?? "#bd88ed"}
              strokeWidth="10"
              strokeDasharray={linePatterns.AX}
            />
            <text x="390" y="965" className="route-label route-label--ax">AX · DEDICATED EXPRESS CORRIDOR</text>
          </g>

          {mode === "city" && network.stations.map((station) => <CitySprite key={station.id} station={{ ...station, ...(points.get(station.id) ?? {}) }} />)}

          {calm && trainMarkers}

          {network.stations.map((station) => {
            const point = points.get(station.id) ?? station;
            const queue = queueByStation.get(station.id) ?? 0;
            const incidents = incidentByTarget.get(station.id) ?? [];
            const isSelected = selectedStationId === station.id;
            const radius = layer === "crowding" ? Math.min(45, 17 + Math.sqrt(queue) * 2.2) : 18;
            const crowdRatio = queue / Math.max(1, station.platformCapacity);
            return (
              <g
                key={station.id}
                className={`map-station ${isSelected ? "selected" : ""}`}
                transform={`translate(${point.x} ${point.y})`}
                role="button"
                tabIndex={0}
                aria-pressed={calm ? isSelected : undefined}
                aria-label={`${station.name} station${queue ? `, ${queue} passengers waiting` : ""}${incidents.length ? `, ${incidents.length} active incidents` : ""}`}
                data-map-action
                onClick={() => onSelectStation(station.id)}
                onKeyDown={(event) => keyActivate(event, () => onSelectStation(station.id))}
              >
                {layer === "crowding" && <circle r={radius} className={`crowd-halo ${crowdRatio > 0.8 ? "critical" : crowdRatio > 0.45 ? "busy" : ""}`} />}
                <rect x="-13" y="-13" width="26" height="26" rx="4" className="station-node" />
                <rect x="-6" y="-6" width="12" height="12" rx="2" className="station-node-core" />
                {layer === "incidents" && incidents.length > 0 && (
                  <g className="incident-pin" transform="translate(15 -21)">
                    <path d="M0-12L12 10H-12z" />
                    <text y="6" textAnchor="middle">!</text>
                  </g>
                )}
                <text className="station-label" y="31" textAnchor="middle">{station.name}</text>
                {layer === "crowding" && queue > 0 && <text className="crowd-count" y="-25" textAnchor="middle">{queue}</text>}
              </g>
            );
          })}

          {!calm && trainMarkers}
        </svg>

        <div className="map-legend" aria-label="Line legend">
          {network.lines.map((line) => (
            <span key={line.id}>
              <i style={{ backgroundColor: line.color }} className={`legend-line legend-line--${line.id.toLowerCase()}`} />
              <strong>{line.id}</strong> {line.name === line.id ? "" : line.name.replace(line.id, "").trim()}
            </span>
          ))}
        </div>
      </div>

      <details className={calm ? "station-directory map-directory" : "station-directory"}>
        <summary>{calm ? "Browse map as a list" : "Station directory and keyboard-accessible map alternative"}</summary>
        {calm && <h3>Stations</h3>}
        <div className="station-directory-grid">
          {network.stations.map((station) => (
            <button
              key={station.id}
              className={station.id === selectedStationId ? "selected" : ""}
              aria-pressed={calm ? station.id === selectedStationId : undefined}
              onClick={() => onSelectStation(station.id)}
            >
              <span>{station.name}</span>
              <small>{queueByStation.get(station.id) ?? 0} waiting · {incidentByTarget.get(station.id)?.length ?? 0} alerts</small>
            </button>
          ))}
        </div>
        {calm && onSelectTrain && (
          <div className="train-directory">
            <h3>Trains</h3>
            <ul className="station-directory-grid" aria-label="Trains">
              {directoryTrains.map((train) => (
                <li key={train.id}>
                  <button
                    className={train.id === selectedTrainId ? "selected" : ""}
                    aria-pressed={train.id === selectedTrainId}
                    onClick={() => onSelectTrain(train.id)}
                  >
                    <span>{publicTrainLabel(train, network)}</span>{" "}
                    <small>{publicTrainStatus(train.state)} · Onboard crowding: {publicOnboardCrowding(train.load, train.capacity, stale).label}. {publicOnboardCrowding(train.load, train.capacity, stale).detail}</small>
                  </button>
                </li>
              ))}
              {directoryTrains.length === 0 && <li className="empty-state">No trains in service reported.</li>}
            </ul>
          </div>
        )}
      </details>
    </section>
  );
}
