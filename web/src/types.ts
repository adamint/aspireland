export type SiteKind = "operations" | "passenger";
export type AuthorityMode = "advise" | "approval" | "bounded";
export type MapLayer = "service" | "crowding" | "incidents";
export type MapMode = "city" | "schematic";
export type CohortState = "outside" | "platform" | "transfer" | "onboard" | "completed" | "abandoned";

export interface StationDefinition {
  id: string;
  name: string;
  x: number;
  y: number;
  character: string;
  accessPerMinute: number;
  platformCapacity: number;
  residential: number;
  jobs: number;
  education: number;
  leisure: number;
}

export interface LineDefinition {
  id: string;
  name: string;
  color: string;
  stations: string[];
  travelSeconds: number;
  headwaySeconds: number;
}

export interface AccessEdge {
  stationId: string;
  from: string;
  to: string;
  seconds: number;
  peoplePerMinute: number;
  accessible: boolean;
  equipmentId?: string | null;
}

export interface NetworkDefinition {
  stations: StationDefinition[];
  lines: LineDefinition[];
  accessEdges: AccessEdge[];
}

export interface TrainState {
  id: string;
  line: string;
  state: string;
  stationId: string;
  nextStationId?: string | null;
  direction: number;
  routeIndex: number;
  capacity: number;
  load: number;
  version: number;
  holdReason?: string | null;
  readyAt: string;
  departedAt?: string | null;
  arrivesAt?: string | null;
  lastHeartbeatAt?: string | null;
  workerInstanceId?: string | null;
  activeGrantId?: string | null;
  shortTurnAt?: string | null;
  depot: string;
  compatibleLines: string[];
}

export interface Cohort {
  id: string;
  origin: string;
  destination: string;
  stationId: string;
  count: number;
  accessible: boolean;
  state: CohortState;
  trainId?: string | null;
  createdAt: string;
  readyAt: string;
  patienceSeconds: number;
  legs: JourneyLeg[];
  legIndex: number;
  returnAt?: string | null;
  returnScheduled: boolean;
}

export interface Incident {
  id: string;
  kind: string;
  target: string;
  state: string;
  source: string;
  description: string;
  startedAt: string;
  endsAt?: string | null;
  severity: number;
}

export interface HistoryEvent {
  id: string;
  scenarioId: string;
  generation: number;
  version: number;
  simTime: string;
  kind: string;
  entityId: string;
  source: string;
  description: string;
  correlationId: string;
  streamVersion: number;
}

export interface PassengerMetrics {
  generated: number;
  completed: number;
  abandoned: number;
  deniedBoardings: number;
  waitingPassengerSeconds: number;
  accessibilityFailures: number;
  platformOverloads: number;
}

export interface MovementGrant {
  id: string;
  requestId: string;
  trainId: string;
  from: string;
  to: string;
  block: string;
  platform: string;
  junction?: string | null;
  departsAt: string;
  arrivesAt: string;
  completed: boolean;
}

export interface WorldState {
  scenarioId: string;
  generation: number;
  seed: number;
  streamVersion: number;
  version: number;
  simTime: string;
  updatedAt: string;
  paused: boolean;
  speed: number;
  preset: string;
  authorityMode: AuthorityMode;
  trains: TrainState[];
  cohorts: Cohort[];
  incidents: Incident[];
  history: HistoryEvent[];
  blockOccupancy: Record<string, string>;
  platformOccupancy: Record<string, string>;
  junctionReservations: Record<string, string>;
  grants: Record<string, MovementGrant>;
  commands: Record<string, CommandResult>;
  headways: Record<string, number>;
  metrics: PassengerMetrics;
  flights: Flight[];
  demandCursor: number;
  lastAgentActionAt?: string | null;
}

export interface Flight {
  id: string;
  direction: string;
  scheduledAt: string;
  actualAt: string;
  passengers: number;
  cancelled: boolean;
  weather: string;
}

export interface WorldSnapshot {
  world: WorldState;
  network: NetworkDefinition;
  stale: boolean;
  warning?: string | null;
}

export interface Arrival {
  trainId: string;
  line: string;
  service: string;
  platform: string;
  destination: string;
  seconds?: number | null;
  predictionKind: string;
  status: string;
  load: number;
  capacity: number;
}

export interface StationBoard {
  stationId: string;
  scenarioId: string;
  generation: number;
  asOf: string;
  updatedAt: string;
  stale: boolean;
  arrivals: Arrival[];
  restrictions: string[];
}

export interface JourneyRequest {
  origin: string;
  destination: string;
  accessible?: boolean;
  departureTime?: string | null;
}

export interface JourneyLeg {
  kind: string;
  line?: string | null;
  from: string;
  to: string;
  platform?: string | null;
  destination?: string | null;
  service: string;
  waitSeconds: number;
  rideSeconds: number;
  walkSeconds: number;
  stops: string[];
}

export interface Itinerary {
  id: string;
  label: string;
  legs: JourneyLeg[];
  totalSeconds: number;
  transfers: number;
  accessible: boolean;
  arrivesAt: string;
  estimateKind: string;
  warnings: string[];
}

export interface JourneyResult {
  scenarioId: string;
  generation: number;
  version: number;
  asOf: string;
  request: JourneyRequest;
  options: Itinerary[];
  error?: string | null;
}

export interface ServiceAction {
  kind: string;
  target?: string | null;
  value?: string | null;
  amount?: number | null;
}

export interface CommandResult {
  accepted: boolean;
  status: string;
  message: string;
  version: number;
  entityId?: string | null;
}

export interface MetricRange {
  mean: number;
  min: number;
  max: number;
}

export interface ForecastMetrics {
  waitingMinutes: MetricRange;
  completed: MetricRange;
  deniedBoardings: MetricRange;
  abandoned: MetricRange;
  remainingQueue: MetricRange;
  oldestQueueSeconds: MetricRange;
  platformOverloads: MetricRange;
  accessibilityFailures: MetricRange;
}

export interface ComparisonResult {
  scenarioId: string;
  generation: number;
  version: number;
  checkpointTime: string;
  horizonMinutes: number;
  samples: number;
  candidate: ServiceAction;
  baseline: ForecastMetrics;
  intervention: ForecastMetrics;
  method: string;
  warnings: string[];
}

export interface ActionProposal {
  id: string;
  action: ServiceAction;
  scenarioId: string;
  generation: number;
  version: number;
  explanation: string;
  status: string;
}

export interface ChatMessage {
  role: string;
  content: string;
}

export interface AssistantRequest {
  message: string;
  history?: ChatMessage[] | null;
  journeyContext?: JourneyRequest | null;
  viewerTimeZone?: string | null;
}

export interface AssistantResponse {
  text: string;
  journey?: JourneyResult | null;
  comparison?: ComparisonResult | null;
  proposal?: ActionProposal | null;
  tools?: string[] | null;
  journeyContext?: JourneyRequest | null;
  arrivals?: StationBoard | null;
}

export interface AiCapabilities {
  text: boolean;
  transcription: boolean;
  speech: boolean;
  provider: string;
  reason?: string | null;
}
