import { createHash } from "node:crypto";
import { lstat, readFile, realpath, writeFile } from "node:fs/promises";
import http from "node:http";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const webRoot = path.dirname(here);
const defaultDist = path.join(here, "dist");
const stationPattern = /^[a-z][a-z0-9-]{0,63}$/;
const mimeTypes = {
  ".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8", ".svg": "image/svg+xml",
  ".woff": "font/woff", ".woff2": "font/woff2",
};
const assetPattern = /^assets\/[A-Za-z0-9_-]+\.(?:js|css|woff2?)$/;
const sha256 = (data) => createHash("sha256").update(data).digest("hex");

class HttpError extends Error {
  constructor(status, message) {
    super(message);
    this.status = status;
  }
}

function check(condition) {
  if (!condition) throw new HttpError(502, "Passenger service returned invalid data.");
}

const text = (value) => { check(typeof value === "string" && value.length <= 2000); return value; };
const shortText = (value) => { check(typeof value === "string" && value.length <= 128); return value; };
const bool = (value) => { check(typeof value === "boolean"); return value; };
const number = (value) => { check(Number.isFinite(value) && value >= 0); return value; };
const integer = (value) => { check(Number.isSafeInteger(number(value))); return value; };
const date = (value) => {
  check(typeof value === "string" && value.length <= 40 &&
    /^\d{4}-\d\d-\d\dT\d\d:\d\d(?::\d\d(?:\.\d{1,7})?)?(?:Z|[+-]\d\d:\d\d)$/.test(value) &&
    Number.isFinite(Date.parse(value)));
  return value;
};
const stationId = (value) => { check(typeof value === "string" && stationPattern.test(value)); return value; };
const nullable = (validate) => (value) => value == null ? null : validate(value);
const list = (validate, max = 1000) => (value) => {
  check(Array.isArray(value) && value.length <= max);
  return value.map(validate);
};
const pick = (shape) => (value) => {
  check(value !== null && typeof value === "object" && !Array.isArray(value));
  return Object.fromEntries(Object.entries(shape).map(([key, validate]) => [key, validate(value[key])]));
};

const requestShape = pick({
  origin: stationId, destination: stationId, accessible: bool, departureTime: nullable(date),
});
const arrivalShape = pick({
  trainId: shortText, line: shortText, service: shortText, platform: shortText,
  destination: stationId, seconds: nullable(number), predictionKind: shortText, status: shortText,
  load: integer, capacity: integer,
});
const legShape = pick({
  kind: shortText, line: nullable(shortText), from: stationId, to: stationId,
  platform: nullable(shortText), destination: nullable(stationId), service: shortText,
  waitSeconds: number, rideSeconds: number, walkSeconds: number, stops: list(stationId),
});
const itineraryShape = pick({
  id: shortText, label: shortText, legs: list(legShape, 100), totalSeconds: number,
  transfers: integer, accessible: bool, arrivesAt: date, estimateKind: shortText, warnings: list(text, 100),
});
const boardShape = pick({
  stationId, scenarioId: shortText, generation: integer, asOf: date, updatedAt: date, stale: bool,
  arrivals: list(arrivalShape), restrictions: list(text, 100),
});
const journeyShape = pick({
  scenarioId: shortText, generation: integer, version: integer, asOf: date,
  request: requestShape, options: list(itineraryShape, 10), error: nullable(text),
});
const networkShape = pick({
  stations: list(pick({
    id: stationId, name: shortText, x: number, y: number, character: shortText,
    accessPerMinute: number, platformCapacity: number,
  })),
  lines: list(pick({
    id: shortText, name: shortText, color: (value) => {
      check(typeof value === "string" && /^#[\da-f]{6}$/i.test(value)); return value;
    }, stations: list(stationId),
  }), 100),
  accessEdges: list(pick({
    stationId, from: shortText, to: shortText, seconds: number,
    peoplePerMinute: number, accessible: bool, equipmentId: nullable(shortText),
  }), 10_000),
});
const trainShape = pick({
  id: shortText, line: shortText, state: shortText, stationId,
  nextStationId: nullable(stationId), capacity: integer, load: integer,
});
const incidentShape = pick({
  id: shortText, kind: shortText, target: shortText, state: shortText, description: text,
});

function projectWorld(data) {
  check(data && data.world);
  const network = networkShape(data.network);
  const ids = new Set(network.stations.map((station) => station.id));
  check(ids.size > 0 && ids.size === network.stations.length && network.lines.length > 0);
  check(network.lines.every((line) => line.stations.length > 0 && line.stations.every((id) => ids.has(id))));
  check(network.accessEdges.every((edge) => ids.has(edge.stationId)));
  const world = pick({
    scenarioId: shortText, generation: integer, version: integer, simTime: date,
    updatedAt: date, paused: bool, speed: number, trains: list(trainShape),
  })(data.world);
  check(world.trains.every((train) => ids.has(train.stationId) && (!train.nextStationId || ids.has(train.nextStationId))));
  const now = Date.parse(world.simTime);
  check(Array.isArray(data.world.incidents) && data.world.incidents.length <= 10_000);
  world.incidents = data.world.incidents.flatMap((incident) => {
    check(incident && typeof incident.state === "string");
    if (incident.state !== "active") return [];
    if (Date.parse(date(incident.startedAt)) > now) return [];
    return [incidentShape(incident)];
  });
  check(Array.isArray(data.world.cohorts) && data.world.cohorts.length <= 100_000);
  const queues = new Map();
  for (const cohort of data.world.cohorts) {
    check(cohort && ["outside", "platform", "transfer", "onboard", "completed", "abandoned"].includes(cohort.state));
    if (cohort.state !== "outside" && cohort.state !== "platform") continue;
    if (Date.parse(date(cohort.readyAt)) > now) continue;
    check(ids.has(cohort.stationId));
    const key = `${cohort.stationId}:${cohort.state}`;
    const count = integer((queues.get(key)?.count ?? 0) + integer(cohort.count));
    queues.set(key, { stationId: cohort.stationId, state: cohort.state, count, readyAt: world.simTime });
  }
  // The shared map expects these containers. Queue rows are anonymous totals, not upstream cohorts.
  world.cohorts = [...queues.values()];
  world.grants = {};
  const stale = bool(data.stale);
  return { world, network, stale, warning: stale ? "Live service information is delayed." : null };
}

function parseUpstream(value) {
  const match = typeof value === "string" && /^http:\/\/(127\.0\.0\.1|\[::1\]):([1-9]\d{0,4})\/?$/.exec(value);
  if (!match || Number(match[2]) > 65535) {
    throw new Error("Passenger upstream must be an explicit loopback HTTP origin, e.g. http://127.0.0.1:60902 (no credentials, path, query or fragment).");
  }
  return `http://${match[1]}:${match[2]}`;
}

export function readConfig(args = [], env = process.env) {
  const values = {};
  for (let i = 0; i < args.length; i += 2) {
    if (!["--port", "--upstream"].includes(args[i]) || args[i + 1] === undefined || values[args[i]] !== undefined) {
      throw new Error("Unknown or incomplete argument. Use --port NUMBER and --upstream http://127.0.0.1:PORT.");
    }
    values[args[i]] = args[i + 1];
  }
  const port = values["--port"] ?? env.CONFERENCE_PORT ?? "6173";
  if (!/^[1-9]\d{0,4}$/.test(port) || Number(port) > 65535) throw new Error("Conference port must be an integer from 1 to 65535.");
  return { port: Number(port), upstream: parseUpstream(values["--upstream"] ?? env.PASSENGER_API_URL) };
}

async function loadAssets(distDir) {
  try {
    const root = await realpath(distDir);
    const manifest = JSON.parse(await readFile(path.join(root, "passenger-build.json"), "utf8"));
    if (manifest.kind !== "passenger-attendee-v1" || !manifest.files || Object.keys(manifest.files).length > 128) throw new Error();
    const assets = new Map();
    let total = 0;
    for (const [name, hash] of Object.entries(manifest.files)) {
      if (name !== "index.html" && name !== "favicon.svg" && !assetPattern.test(name)) throw new Error();
      const file = path.join(root, name);
      if (!(await realpath(file)).startsWith(`${root}${path.sep}`) || !(await lstat(file)).isFile()) throw new Error();
      const stat = await lstat(file);
      if (stat.size > 4 * 1024 * 1024 || (total += stat.size) > 8 * 1024 * 1024) throw new Error();
      const data = await readFile(file);
      if (sha256(data) !== hash) throw new Error();
      assets.set(`/${name}`, { data, type: mimeTypes[path.extname(name)] });
    }
    if (!assets.has("/index.html") || ![...assets.keys()].some((name) => name.endsWith(".js"))) throw new Error();
    return assets;
  } catch {
    throw new Error("Missing or invalid passenger build. Run npm run build:conference in web first.");
  }
}

async function buildPassenger() {
  const result = spawnSync("npm", ["run", "build", "--", "--outDir", "conference/dist", "--manifest"], {
    cwd: webRoot, stdio: "inherit", env: { ...process.env, VITE_SITE: "passenger", VITE_ATTENDEE: "true" },
  });
  if (result.error || result.status !== 0) throw new Error("Passenger build failed.");
  const manifest = JSON.parse(await readFile(path.join(defaultDist, ".vite/manifest.json"), "utf8"));
  const entry = Object.keys(manifest).filter((key) => manifest[key].isEntry);
  if (entry.length !== 1 || entry[0] !== "index.html") throw new Error("Unexpected passenger build entry.");
  const names = new Set(["index.html", "favicon.svg"]);
  const visited = new Set();
  const visit = (key) => {
    if (visited.has(key)) return;
    visited.add(key);
    const chunk = manifest[key];
    if (!chunk) throw new Error("Missing passenger build chunk.");
    for (const name of [chunk.file, ...(chunk.css ?? []), ...(chunk.assets ?? [])]) names.add(name);
    for (const imported of [...(chunk.imports ?? []), ...(chunk.dynamicImports ?? [])]) visit(imported);
  };
  visit(entry[0]);
  const files = Object.fromEntries(await Promise.all([...names].map(async (name) =>
    [name, sha256(await readFile(path.join(defaultDist, name)))])));
  await writeFile(path.join(defaultDist, "passenger-build.json"), JSON.stringify({ kind: "passenger-attendee-v1", files }));
  await loadAssets(defaultDist);
  console.log("Passenger attendee build ready in web/conference/dist (15-second polling; normal dist unchanged).");
}

function readBody(req, maxBytes, timeoutMs) {
  if (!/^application\/json(?:\s*;\s*charset=utf-8)?$/i.test(req.headers["content-type"] ?? "")) {
    throw new HttpError(415, "Use application/json.");
  }
  if (Number(req.headers["content-length"]) > maxBytes) throw new HttpError(413, "Request body is too large.");
  return new Promise((resolve, reject) => {
    const chunks = [];
    let size = 0;
    const finish = (error) => {
      clearTimeout(timer);
      req.off("data", onData).off("end", onEnd).off("error", onError).off("aborted", onAbort);
      if (error) { req.pause(); reject(error); }
      else {
        try { resolve(JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(Buffer.concat(chunks)))); }
        catch { reject(new HttpError(400, "Invalid JSON request.")); }
      }
    };
    const onData = (chunk) => {
      size += chunk.length;
      if (size > maxBytes) finish(new HttpError(413, "Request body is too large."));
      else chunks.push(chunk);
    };
    const onEnd = () => finish();
    const onError = () => finish(new HttpError(400, "Incomplete request body."));
    const onAbort = onError;
    const timer = setTimeout(() => finish(new HttpError(408, "Request body timed out.")), timeoutMs);
    req.on("data", onData).once("end", onEnd).once("error", onError).once("aborted", onAbort);
  });
}

export async function createPassengerServer({
  upstream, distDir = defaultDist, cacheMs = 2000, maxUpstreamRequests = 4,
  maxActiveRequests = 32, upstreamTimeoutMs = 8000, bodyTimeoutMs = 3000,
  maxResponseBytes = 32 * 1024 * 1024,
}) {
  upstream = parseUpstream(upstream);
  const assets = await loadAssets(distDir);
  const agent = new http.Agent({ keepAlive: true, maxSockets: maxUpstreamRequests, maxFreeSockets: maxUpstreamRequests });
  let upstreamRequests = 0;
  let activeRequests = 0;
  const cache = new Map();

  async function upstreamRead(target, body, project) {
    if (upstreamRequests >= maxUpstreamRequests) throw new HttpError(503, "Passenger service is busy. Try again shortly.");
    upstreamRequests++;
    try {
      return await new Promise((resolve, reject) => {
        let response;
        let done = false;
        let timer;
        const finish = (error, value) => {
          if (done) return;
          done = true;
          clearTimeout(timer);
          if (error) {
            request.destroy();
            response?.destroy();
            reject(error instanceof HttpError ? error : new HttpError(502, "Passenger service is unavailable."));
          } else resolve(value);
        };
        const request = http.request(`${upstream}${target}`, {
          agent, method: body === undefined ? "GET" : "POST",
          headers: {
            Accept: "application/json",
            ...(body === undefined ? {} : { "Content-Type": "application/json", "Content-Length": Buffer.byteLength(body) }),
          },
        }, (res) => {
          response = res;
          if (res.statusCode !== 200 || !/^application\/json(?:\s*;|$)/i.test(res.headers["content-type"] ?? "") ||
              Number(res.headers["content-length"]) > maxResponseBytes) {
            finish(new HttpError(502, "Passenger service returned an invalid response."));
            return;
          }
          const chunks = [];
          let size = 0;
          res.on("data", (chunk) => {
            size += chunk.length;
            if (size > maxResponseBytes) finish(new HttpError(502, "Passenger service response exceeded its limit."));
            else chunks.push(chunk);
          });
          res.once("error", (error) => finish(error));
          res.once("aborted", () => finish(new HttpError(502, "Passenger service response was incomplete.")));
          res.once("end", () => {
            if (done) return;
            try {
              const data = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(Buffer.concat(chunks)));
              finish(null, project(data));
            } catch { finish(new HttpError(502, "Passenger service returned invalid data.")); }
          });
        });
        request.once("error", (error) => finish(error));
        timer = setTimeout(() => finish(new HttpError(504, "Passenger service timed out.")), upstreamTimeoutMs);
        request.end(body);
      });
    } finally { upstreamRequests--; }
  }

  function cachedRead(target, project) {
    const previous = cache.get(target);
    if (previous?.pending) return previous.pending;
    if (previous && previous.expiresAt > Date.now()) return Promise.resolve(previous.value);
    cache.delete(target);
    if (cache.size >= 64) {
      const oldest = [...cache].find(([, entry]) => !entry.pending);
      if (oldest) cache.delete(oldest[0]);
      else throw new HttpError(503, "Passenger service is busy. Try again shortly.");
    }
    const entry = {};
    entry.pending = upstreamRead(target, undefined, project).then((value) => {
      entry.value = value;
      entry.pending = null;
      entry.expiresAt = Date.now() + cacheMs;
      return value;
    }, (error) => { cache.delete(target); throw error; });
    cache.set(target, entry);
    return entry.pending;
  }

  const worldRead = () => cachedRead("/api/world", projectWorld);
  const server = http.createServer({
    maxHeaderSize: 8192, headersTimeout: 5000, requestTimeout: 10_000,
    connectionsCheckingInterval: 1000,
  }, async (req, res) => {
    res.setHeader("X-Content-Type-Options", "nosniff");
    res.setHeader("X-Frame-Options", "DENY");
    res.setHeader("Referrer-Policy", "no-referrer");
    res.setHeader("Cross-Origin-Resource-Policy", "same-origin");
    res.setHeader("Permissions-Policy", "camera=(), microphone=(), geolocation=()");
    res.setHeader("Cache-Control", "no-store");
    res.setHeader("Content-Security-Policy", "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; font-src 'self'; img-src 'self' data:; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'; object-src 'none'");
    const json = (status, data) => {
      if (res.destroyed) return;
      if (!req.complete) {
        res.setHeader("Connection", "close");
        res.once("finish", () => req.destroy());
      }
      res.writeHead(status, { "Content-Type": "application/json; charset=utf-8" });
      res.end(JSON.stringify(data));
    };
    if (activeRequests >= maxActiveRequests) { json(503, { error: "Passenger service is busy. Try again shortly." }); return; }
    activeRequests++;
    let released = false;
    const release = () => { if (!released) { released = true; activeRequests--; } };
    res.once("close", release).once("finish", release);
    try {
      if (req.method === "POST" && req.headers["sec-fetch-site"] === "cross-site") throw new HttpError(403, "Cross-site requests are not allowed.");
      const target = req.url ?? "";
      if (!target.startsWith("/") || target.startsWith("//") || /[%\\?#\s]/.test(target)) throw new HttpError(404, "Not found.");
      const boardMatch = /^\/api\/stations\/([a-z][a-z0-9-]{0,63})\/arrivals$/.exec(target);
      const stationMatch = /^\/stations\/([a-z][a-z0-9-]{0,63})\/?$/.exec(target);
      const apiGet = target === "/api/world" || target === "/api/capabilities" || Boolean(boardMatch);
      const ai = ["/api/assistant", "/api/transcribe", "/api/speech"].includes(target);
      const asset = assets.get(target === "/" || stationMatch ? "/index.html" : target);
      if (!apiGet && !ai && target !== "/api/journeys" && !asset) throw new HttpError(404, "Not found.");
      const methods = apiGet ? ["GET"] : ai || target === "/api/journeys" ? ["POST"] : ["GET", "HEAD"];
      if (!methods.includes(req.method)) {
        res.setHeader("Allow", methods.join(", "));
        throw new HttpError(405, "Method not allowed.");
      }
      if (ai) throw new HttpError(503, "AI and voice are disabled for anonymous conference attendees. Use the ordinary journey planner.");
      if (req.method !== "POST" && (req.headers["transfer-encoding"] || Number(req.headers["content-length"]) > 0)) {
        throw new HttpError(400, "This request must not have a body.");
      }
      if (target === "/api/capabilities") {
        json(200, {
          text: false, transcription: false, speech: false, provider: "disabled",
          reason: "AI and voice are disabled for anonymous conference attendees. Use the ordinary journey planner.",
        });
      } else if (target === "/api/world") {
        json(200, await worldRead());
      } else if (target === "/api/journeys") {
        const body = await readBody(req, 4096, bodyTimeoutMs);
        let request;
        try {
          check(body && Object.keys(body).every((key) => ["origin", "destination", "accessible", "departureTime"].includes(key)));
          request = requestShape({ ...body, accessible: body.accessible === undefined ? false : body.accessible, departureTime: body.departureTime ?? null });
        } catch { throw new HttpError(400, "Invalid journey request."); }
        json(200, await upstreamRead(target, JSON.stringify(request), (data) => {
          const result = journeyShape(data);
          check(JSON.stringify(result.request) === JSON.stringify(request));
          return result;
        }));
      } else if (boardMatch || stationMatch) {
        const id = (boardMatch ?? stationMatch)[1];
        const world = await worldRead();
        if (!world.network.stations.some((station) => station.id === id)) throw new HttpError(404, "Station not found.");
        if (boardMatch) {
          json(200, await cachedRead(target, (data) => {
            const result = boardShape(data);
            check(result.stationId === id);
            return result;
          }));
        } else {
          res.writeHead(200, { "Content-Type": asset.type, "Content-Length": asset.data.length });
          res.end(req.method === "HEAD" ? undefined : asset.data);
        }
      } else {
        res.writeHead(200, {
          "Content-Type": asset.type, "Content-Length": asset.data.length,
          "Cache-Control": target.startsWith("/assets/") ? "public, max-age=31536000, immutable" : "no-store",
        });
        res.end(req.method === "HEAD" ? undefined : asset.data);
      }
    } catch (error) {
      json(error instanceof HttpError ? error.status : 502, {
        error: error instanceof HttpError ? error.message : "Passenger service is unavailable.",
      });
    }
  });
  server.maxConnections = 64;
  server.maxHeadersCount = 32;
  server.maxRequestsPerSocket = 100;
  server.keepAliveTimeout = 5000;
  server.setTimeout(10_000, (socket) => socket.destroy());
  server.once("close", () => agent.destroy());
  return server;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    if (process.argv.length === 3 && process.argv[2] === "--build") await buildPassenger();
    else {
      const { port, upstream } = readConfig(process.argv.slice(2));
      const server = await createPassengerServer({ upstream });
      server.once("error", (error) => { console.error(`Passenger origin could not listen (${error.code ?? "error"}).`); process.exitCode = 1; });
      server.listen(port, "127.0.0.1", () => console.log(`Passenger attendee origin: http://127.0.0.1:${port}`));
      for (const signal of ["SIGINT", "SIGTERM"]) process.once(signal, () => {
        server.close();
        server.closeAllConnections();
      });
    }
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
