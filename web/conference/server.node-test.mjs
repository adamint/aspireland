import assert from "node:assert/strict";
import { createHash, randomUUID } from "node:crypto";
import { mkdir, rm, writeFile } from "node:fs/promises";
import http from "node:http";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { createPassengerServer, readConfig } from "./server.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));
const time = "2026-11-17T08:00:00Z";
const secret = "PRIVATE-CHECKPOINT-DO-NOT-SHARE";
const journeyRequest = { origin: "university", destination: "airport", accessible: false, departureTime: null };
const leg = {
  kind: "ride", line: "AX", from: "university", to: "airport", platform: "university:AX:+",
  destination: "airport", service: "express", waitSeconds: 30, rideSeconds: 300, walkSeconds: 0,
  stops: ["university", "airport"], internal: secret,
};

function snapshot() {
  const station = (id) => ({
    id, name: id, x: 100, y: 200, character: "airport", accessPerMinute: 100,
    platformCapacity: 200, residential: secret, jobs: secret, private: secret,
  });
  return {
    world: {
      scenarioId: "scenario", generation: 10, version: 123, simTime: time, updatedAt: time,
      paused: false, speed: 5, seed: secret, policies: secret, history: [secret],
      grants: { secret }, commands: { secret }, randomStreams: { secret }, blockOccupancy: { secret },
      trains: [{
        id: "ax-1", line: "AX", state: "travelling", stationId: "university", nextStationId: "airport",
        capacity: 180, load: 75, activeGrantId: secret, workerInstanceId: secret, holdReason: secret,
        depot: secret, compatibleLines: [secret], lastHeartbeatAt: secret,
      }],
      cohorts: [
        { id: secret, stationId: "university", state: "platform", count: 4, readyAt: time, destination: secret },
        { id: secret, stationId: "university", state: "platform", count: 6, readyAt: time },
        { id: secret, stationId: "airport", state: "outside", count: 3, readyAt: time },
        { id: secret, stationId: "airport", state: "outside", count: 999, readyAt: "2026-11-18T08:00:00Z" },
        { id: secret, stationId: "airport", state: "onboard", count: 999, readyAt: time },
      ],
      incidents: [
        { id: "alert", kind: "elevator-outage", target: "university", state: "active", description: "Lift unavailable.", startedAt: time, endsAt: secret, source: secret },
        { id: secret, kind: "door-fault", target: "ax-1", state: "pending", description: secret, startedAt: time },
        { id: secret, kind: "door-fault", target: "ax-1", state: "active", description: secret, startedAt: "2026-11-18T08:00:00Z" },
        { id: secret, kind: "door-fault", target: "ax-1", state: "cleared", description: secret, startedAt: time },
      ],
    },
    network: {
      stations: [station("university"), station("airport")],
      lines: [{ id: "AX", name: "Airport Express", color: "#abcdef", stations: ["university", "airport"], travelSeconds: secret, headwaySeconds: secret }],
      accessEdges: [{ stationId: "university", from: "street", to: "platform", seconds: 30, peoplePerMinute: 50, accessible: true, equipmentId: null, private: secret }],
      policies: secret,
    },
    stale: false, warning: secret, checkpoint: secret,
  };
}

function board() {
  return {
    stationId: "university", scenarioId: "scenario", generation: 10, asOf: time, updatedAt: time, stale: false,
    arrivals: [{ trainId: "ax-1", line: "AX", service: "express", platform: "university:AX:+", destination: "airport", seconds: 30, predictionKind: "live", status: "On time", load: 75, capacity: 180, grant: secret }],
    restrictions: ["Lift unavailable."], checkpoint: secret,
  };
}

function journey() {
  return {
    scenarioId: "scenario", generation: 10, version: 123, asOf: time, request: journeyRequest,
    options: [{
      id: "trip", label: "Fastest", legs: [leg], totalSeconds: 330, transfers: 0, accessible: true,
      arrivesAt: "2026-11-17T08:05:30Z", estimateKind: "live", warnings: [], internal: secret,
    }],
    error: null, checkpoint: secret,
  };
}

async function listen(server) {
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });
  return `http://127.0.0.1:${server.address().port}`;
}

async function close(server) {
  server.closeAllConnections();
  await new Promise((resolve) => server.close(resolve));
}

async function fixture(t, options = {}) {
  const distDir = path.join(here, `.test-${randomUUID()}`);
  await mkdir(path.join(distDir, "assets"), { recursive: true });
  const files = {
    "index.html": '<!doctype html><div id="root"></div><script type="module" src="/assets/app.js"></script>',
    "assets/app.js": 'document.documentElement.dataset.site="passenger";',
    "assets/style.css": "body{color:purple}",
    "assets/font.woff2": "font",
    "favicon.svg": "<svg xmlns=\"http://www.w3.org/2000/svg\"/>",
  };
  await Promise.all(Object.entries({ ...files, "operations.html": secret, ".env": secret, "assets/unlisted.js": secret, "assets/app.js.map": secret })
    .map(([name, value]) => writeFile(path.join(distDir, name), value)));
  await writeFile(path.join(distDir, "passenger-build.json"), JSON.stringify({
    kind: "passenger-attendee-v1",
    files: Object.fromEntries(Object.entries(files).map(([name, data]) => [name, createHash("sha256").update(data).digest("hex")])),
  }));
  const requests = [];
  let handler = options.handler ?? ((req, res) => {
    res.setHeader("Content-Type", "application/json");
    res.setHeader("Set-Cookie", "operator=secret");
    res.setHeader("X-Internal-Key", secret);
    const data = req.url === "/api/world" ? snapshot()
      : req.url === "/api/journeys" ? journey() : board();
    res.end(JSON.stringify(data));
  });
  const upstreamServer = http.createServer(async (req, res) => {
    let body = "";
    for await (const chunk of req) body += chunk;
    requests.push({ path: req.url, method: req.method, headers: req.headers, body });
    handler(req, res);
  });
  const upstream = await listen(upstreamServer);
  let server;
  t.after(async () => {
    if (server) await close(server);
    await close(upstreamServer);
    await rm(distDir, { recursive: true, force: true });
  });
  server = await createPassengerServer({ upstream, distDir, ...options.limits });
  const origin = await listen(server);
  return { origin, requests, distDir, server, setHandler: (next) => { handler = next; } };
}

function rawRequest(origin, target, { method = "GET", headers = {}, body } = {}) {
  return new Promise((resolve, reject) => {
    const req = http.request(origin, { path: target, method, headers }, (res) => {
      let text = "";
      res.on("data", (chunk) => { text += chunk; });
      res.on("end", () => resolve({ status: res.statusCode, headers: res.headers, text }));
    });
    req.on("error", reject);
    req.end(body);
  });
}

test("configuration requires an explicit numeric loopback HTTP upstream and valid port", () => {
  assert.deepEqual(readConfig([], { PASSENGER_API_URL: "http://127.0.0.1:60902" }), {
    port: 6173, upstream: "http://127.0.0.1:60902",
  });
  assert.equal(readConfig(["--port", "6174", "--upstream", "http://[::1]:60902"], {}).port, 6174);
  assert.equal(readConfig([], { PASSENGER_API_URL: "http://127.0.0.1:80/" }).upstream, "http://127.0.0.1:80");
  for (const upstream of [
    "https://127.0.0.1:60902", "http://example.com:60902", "http://127.1:60902",
    "http://2130706433:60902", "http://localhost:60902", "http://127.0.0.1:0",
    "http://user:pass@127.0.0.1:60902", "http://127.0.0.1:60902/api",
    "http://127.0.0.1:60902?x=1", "http://127.0.0.1:60902#x",
  ]) assert.throws(() => readConfig([], { PASSENGER_API_URL: upstream }), /upstream/i, upstream);
  for (const port of ["0", "65536", "1.2", "-1", "6173oops", ""]) {
    assert.throws(() => readConfig([], { PASSENGER_API_URL: "http://127.0.0.1:60902", CONFERENCE_PORT: port }), /port/i);
  }
  assert.throws(() => readConfig([], {}), /upstream/i);
  assert.throws(() => readConfig(["--host", "0.0.0.0"], {}), /argument/i);
});

test("serves only sealed passenger assets and intentional known station SPA routes", async (t) => {
  const f = await fixture(t);
  for (const target of ["/", "/index.html", "/stations/university", "/stations/airport/", "/assets/app.js", "/assets/style.css", "/assets/font.woff2", "/favicon.svg"]) {
    const response = await rawRequest(f.origin, target);
    assert.equal(response.status, 200, target);
    assert.ok(!response.text.includes(secret));
    assert.equal(response.headers["x-content-type-options"], "nosniff");
    assert.equal(response.headers["access-control-allow-origin"], undefined);
  }
  const head = await rawRequest(f.origin, "/assets/app.js", { method: "HEAD" });
  assert.equal(head.status, 200);
  assert.equal(head.text, "");
  const html = await rawRequest(f.origin, "/");
  assert.match(html.headers["content-security-policy"], /frame-ancestors 'none'/);
  assert.match(html.headers["permissions-policy"], /microphone=\(\)/);
});

test("denies development, operator, unknown, unlisted, encoded and traversal URLs", async (t) => {
  const f = await fixture(t);
  for (const target of [
    "/operations", "/operations.html", "/dispatch", "/dashboard", "/@vite/client", "/@fs/etc/passwd",
    "/src/main.tsx", "/node_modules/react/index.js", "/.env", "/package.json", "/passenger-build.json",
    "/assets/unlisted.js", "/assets/app.js.map", "/api/analytics", "/api/world?raw=true",
    "/api/world/", "/unknown", "/stations/unknown", "/stations/university/more", "//api/world",
    "/../.env", "/assets/../.env", "/%2e%2e/.env", "/assets%2fapp.js", "/assets\\app.js",
    "/%252e%252e/.env", "http://example.com/api/world",
  ]) {
    const response = await rawRequest(f.origin, target);
    assert.equal(response.status, 404, target);
    assert.ok(!response.text.includes(secret), target);
  }
  assert.ok(f.requests.every((req) => req.path === "/api/world"));
});

test("refuses a missing or modified passenger build instead of serving another dist", async (t) => {
  const f = await fixture(t);
  await writeFile(path.join(f.distDir, "assets/app.js"), secret);
  await assert.rejects(createPassengerServer({ upstream: "http://127.0.0.1:60902", distDir: f.distDir }), /build/i);
  await rm(path.join(f.distDir, "passenger-build.json"));
  await assert.rejects(createPassengerServer({ upstream: "http://127.0.0.1:60902", distDir: f.distDir }), /build/i);
});

test("projects the world, anonymous crowd totals and selectable train facts without checkpoint internals", async (t) => {
  const f = await fixture(t);
  const response = await fetch(`${f.origin}/api/world`, {
    headers: { Cookie: "operator=secret", Authorization: "Bearer secret", "X-Operator-Key": secret, "X-Forwarded-Host": "private" },
  });
  assert.equal(response.status, 200);
  const text = await response.text();
  assert.ok(!text.includes(secret));
  const data = JSON.parse(text);
  assert.equal(data.world.generation, 10);
  assert.deepEqual(data.world.trains[0], {
    id: "ax-1", line: "AX", state: "travelling", stationId: "university", nextStationId: "airport", capacity: 180, load: 75,
  });
  assert.deepEqual(data.world.cohorts, [
    { stationId: "university", state: "platform", count: 10, readyAt: time },
    { stationId: "airport", state: "outside", count: 3, readyAt: time },
  ]);
  assert.deepEqual(data.world.grants, {});
  assert.equal(data.world.incidents.length, 1);
  assert.equal(data.world.incidents[0].description, "Lift unavailable.");
  assert.equal(data.stale, false);
  for (const key of ["seed", "history", "commands", "policies", "blockOccupancy", "randomStreams"]) {
    assert.equal(key in data.world, false, key);
  }
  assert.equal(response.headers.get("set-cookie"), null);
  assert.equal(response.headers.get("x-internal-key"), null);
  for (const key of ["cookie", "authorization", "x-operator-key", "x-forwarded-host"]) {
    assert.equal(f.requests[0].headers[key], undefined, key);
  }
});

test("preserves stale data status without forwarding upstream diagnostic warnings", async (t) => {
  const f = await fixture(t, { handler: (_req, res) => {
    res.setHeader("Content-Type", "application/json");
    res.end(JSON.stringify({ ...snapshot(), stale: true }));
  } });
  const data = await (await fetch(`${f.origin}/api/world`)).json();
  assert.equal(data.stale, true);
  assert.match(data.warning, /delayed/i);
  assert.ok(!JSON.stringify(data).includes(secret));
});

test("sanitizes arrival boards and ordinary journey payloads and forwards only fixed JSON", async (t) => {
  const f = await fixture(t);
  const arrivalResponse = await fetch(`${f.origin}/api/stations/university/arrivals`);
  assert.equal(arrivalResponse.status, 200);
  const arrivalText = await arrivalResponse.text();
  assert.ok(!arrivalText.includes(secret));
  assert.equal(JSON.parse(arrivalText).arrivals[0].trainId, "ax-1");
  const journeyResponse = await fetch(`${f.origin}/api/journeys`, {
    method: "POST", headers: { "Content-Type": "application/json", Cookie: "secret" }, body: JSON.stringify(journeyRequest),
  });
  assert.equal(journeyResponse.status, 200);
  const journeyText = await journeyResponse.text();
  assert.ok(!journeyText.includes(secret));
  assert.equal(JSON.parse(journeyText).options[0].legs[0].rideSeconds, 300);
  const forwarded = f.requests.find((req) => req.path === "/api/journeys");
  assert.deepEqual(JSON.parse(forwarded.body), journeyRequest);
  assert.equal(forwarded.headers["content-type"], "application/json");
  assert.equal(forwarded.headers.cookie, undefined);
});

test("validates the echoed journey departure by instant", async (t) => {
  const f = await fixture(t);
  const cases = [
    ["2026-11-17T08:00:00.000Z", "2026-11-17T08:00:00+00:00", 200],
    ["2026-11-17T08:00:00.1234000Z", "2026-11-17T03:00:00.1234-05:00", 200],
    ["2026-11-17T08:00:00.1234001Z", "2026-11-17T08:00:00.1234002+00:00", 502],
    ["2026-11-17T08:00:00.000Z", "2026-11-17T08:00:01+00:00", 502],
    ["2026-11-17T08:00:00.000Z", null, 502],
    [null, "2026-11-17T08:00:00+00:00", 502],
  ];
  for (const [departureTime, echoedTime, status] of cases) {
    await t.test(`${departureTime} -> ${echoedTime} yields ${status}`, async () => {
      f.setHandler((_, res) => {
        res.setHeader("Content-Type", "application/json");
        res.end(JSON.stringify({ ...journey(), request: { ...journeyRequest, departureTime: echoedTime } }));
      });
      const response = await rawRequest(f.origin, "/api/journeys", {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ ...journeyRequest, departureTime }),
      });
      assert.equal(response.status, status);
      if (status === 200) assert.equal(JSON.parse(response.text).request.departureTime, echoedTime);
    });
  }
});

test("AI and voice are explicitly disabled and all operator mutations are denied locally", async (t) => {
  const f = await fixture(t);
  const capabilities = await (await fetch(`${f.origin}/api/capabilities`)).json();
  assert.deepEqual([capabilities.text, capabilities.transcription, capabilities.speech], [false, false, false]);
  assert.match(capabilities.reason, /not available.*trip planner/i);
  for (const route of ["assistant", "speech", "transcribe"]) {
    const response = await rawRequest(f.origin, `/api/${route}`, { method: "POST", body: "{}" });
    assert.equal(response.status, 503);
  }
  for (const route of ["actions", "clock", "scenarios", "authority", "compare", "proposals/example/approve"]) {
    assert.equal((await rawRequest(f.origin, `/api/${route}`, { method: "POST", body: "{}" })).status, 404, route);
  }
  for (const [route, method] of [["/api/world", "POST"], ["/api/world", "HEAD"], ["/api/journeys", "GET"], ["/api/capabilities", "OPTIONS"], ["/", "POST"]]) {
    assert.equal((await rawRequest(f.origin, route, { method })).status, 405, `${method} ${route}`);
  }
  assert.equal(f.requests.length, 0);
});

test("allows cross-site public GET navigation but denies cross-site POST requests", async (t) => {
  const f = await fixture(t);
  const navigation = await rawRequest(f.origin, "/", {
    headers: { "Sec-Fetch-Site": "cross-site", "Sec-Fetch-Mode": "navigate", "Sec-Fetch-Dest": "document" },
  });
  assert.equal(navigation.status, 200);
  assert.equal(navigation.headers["access-control-allow-origin"], undefined);
  assert.equal((await rawRequest(f.origin, "/api/world", { headers: { "Sec-Fetch-Site": "cross-site" } })).status, 200);
  const calls = f.requests.length;
  const post = await rawRequest(f.origin, "/api/journeys", {
    method: "POST",
    headers: { "Content-Type": "application/json", "Sec-Fetch-Site": "cross-site" },
    body: JSON.stringify(journeyRequest),
  });
  assert.equal(post.status, 403);
  assert.equal(f.requests.length, calls);
});

test("rejects malformed, oversized and control-bearing journey requests before forwarding", async (t) => {
  const f = await fixture(t);
  for (const [body, contentType, status] of [
    ["{", "application/json", 400],
    [JSON.stringify({ ...journeyRequest, action: "reset" }), "application/json", 400],
    [JSON.stringify({ ...journeyRequest, destination: "../api/clock" }), "application/json", 400],
    [JSON.stringify({ ...journeyRequest, accessible: "true" }), "application/json", 400],
    [JSON.stringify({ ...journeyRequest, accessible: null }), "application/json", 400],
    [JSON.stringify({ ...journeyRequest, departureTime: "tomorrow" }), "application/json", 400],
    [JSON.stringify(journeyRequest), "text/plain", 415],
    [" ".repeat(4097), "application/json", 413],
  ]) {
    assert.equal((await rawRequest(f.origin, "/api/journeys", { method: "POST", headers: { "Content-Type": contentType }, body })).status, status);
  }
  assert.equal((await rawRequest(f.origin, "/api/journeys", {
    method: "POST", headers: { "Content-Type": "application/json", "Transfer-Encoding": "chunked" }, body: " ".repeat(4097),
  })).status, 413);
  assert.equal(f.requests.length, 0);
});

test("fails closed on malformed world, board or itinerary data without leaking upstream errors", async (t) => {
  const f = await fixture(t, { limits: { cacheMs: 1 } });
  for (const value of [
    {}, { ...snapshot(), stale: "false" },
    { ...snapshot(), world: { ...snapshot().world, trains: null } },
    { ...snapshot(), world: { ...snapshot().world, simTime: "bad" } },
    { ...snapshot(), world: { ...snapshot().world, cohorts: [{ state: "platform", stationId: "university", count: "many", readyAt: time }] } },
  ]) {
    f.setHandler((_req, res) => { res.setHeader("Content-Type", "application/json"); res.end(JSON.stringify(value)); });
    const response = await fetch(`${f.origin}/api/world`);
    assert.equal(response.status, 502);
    assert.ok(!(await response.text()).includes(secret));
  }
  f.setHandler((req, res) => {
    res.setHeader("Content-Type", "application/json");
    res.end(JSON.stringify(req.url === "/api/world" ? snapshot() : { ...board(), arrivals: [{ ...board().arrivals[0], load: "lots" }] }));
  });
  assert.equal((await fetch(`${f.origin}/api/stations/university/arrivals`)).status, 502);
  f.setHandler((req, res) => {
    res.setHeader("Content-Type", "application/json");
    res.end(JSON.stringify(req.url === "/api/world" ? snapshot() : { ...journey(), options: [{ ...journey().options[0], legs: null }] }));
  });
  assert.equal((await fetch(`${f.origin}/api/journeys`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(journeyRequest) })).status, 502);
});

test("shares in-flight and short-lived cached GET reads, then refreshes rather than serving stale cache", async (t) => {
  const f = await fixture(t, { limits: { cacheMs: 40 }, handler: (_req, res) => {
    setTimeout(() => { res.setHeader("Content-Type", "application/json"); res.end(JSON.stringify(snapshot())); }, 20);
  } });
  const responses = await Promise.all(Array.from({ length: 8 }, () => fetch(`${f.origin}/api/world`)));
  assert.ok(responses.every((response) => response.status === 200));
  assert.equal(f.requests.length, 1);
  await fetch(`${f.origin}/api/world`);
  assert.equal(f.requests.length, 1);
  await new Promise((resolve) => setTimeout(resolve, 50));
  f.setHandler((_req, res) => { res.statusCode = 500; res.end(secret); });
  assert.equal((await fetch(`${f.origin}/api/world`)).status, 502);
  assert.equal(f.requests.length, 2);
});

test("shares station board reads as well as world reads across attendees", async (t) => {
  const f = await fixture(t, { handler: (req, res) => {
    setTimeout(() => {
      res.setHeader("Content-Type", "application/json");
      res.end(JSON.stringify(req.url === "/api/world" ? snapshot() : board()));
    }, 10);
  } });
  const responses = await Promise.all(Array.from({ length: 8 }, () => fetch(`${f.origin}/api/stations/university/arrivals`)));
  assert.ok(responses.every((response) => response.status === 200));
  assert.deepEqual(f.requests.map((req) => req.path), ["/api/world", "/api/stations/university/arrivals"]);
});

test("rejects redirects, wrong content types, invalid JSON and oversized upstream responses", async (t) => {
  const f = await fixture(t, { limits: { maxResponseBytes: 8192 } });
  const handlers = [
    (_req, res) => { res.writeHead(302, { Location: "/private" }); res.end(); },
    (_req, res) => { res.setHeader("Content-Type", "text/html"); res.end(secret); },
    (_req, res) => { res.setHeader("Content-Type", "application/json"); res.end("{"); },
    (_req, res) => { res.setHeader("Content-Type", "application/json"); res.end(`"${"x".repeat(8192)}"`); },
  ];
  for (const handler of handlers) {
    f.setHandler(handler);
    const response = await fetch(`${f.origin}/api/world`);
    assert.equal(response.status, 502);
    assert.ok(!(await response.text()).includes(secret));
  }
  assert.ok(f.requests.every((req) => req.path === "/api/world"));
});

test("bounds upstream concurrency and timeouts and releases capacity after failures", async (t) => {
  const f = await fixture(t, { limits: { maxUpstreamRequests: 1, upstreamTimeoutMs: 80 }, handler: () => {} });
  const pending = fetch(`${f.origin}/api/world`);
  while (f.requests.length === 0) await new Promise((resolve) => setTimeout(resolve, 2));
  const overloaded = await fetch(`${f.origin}/api/journeys`, {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(journeyRequest),
  });
  assert.equal(overloaded.status, 503);
  assert.equal((await pending).status, 504);
  f.setHandler((_req, res) => { res.setHeader("Content-Type", "application/json"); res.end(JSON.stringify(snapshot())); });
  assert.equal((await fetch(`${f.origin}/api/world`)).status, 200);
});

test("applies the upstream deadline to streaming bodies, not just response headers", async (t) => {
  const f = await fixture(t, { limits: { upstreamTimeoutMs: 40 }, handler: (_req, res) => {
    res.writeHead(200, { "Content-Type": "application/json" });
    res.write("{");
  } });
  assert.equal((await fetch(`${f.origin}/api/world`)).status, 504);
});

test("bounds slow request bodies and total concurrent attendee requests", async (t) => {
  const f = await fixture(t, { limits: { bodyTimeoutMs: 50, maxActiveRequests: 1 } });
  const slow = new Promise((resolve, reject) => {
    const req = http.request(`${f.origin}/api/journeys`, {
      method: "POST", headers: { "Content-Type": "application/json", "Content-Length": "50" },
    }, (res) => {
      res.resume();
      res.on("end", () => resolve(res.statusCode));
    });
    req.on("error", reject);
    req.write("{");
  });
  await new Promise((resolve) => setTimeout(resolve, 10));
  assert.equal((await fetch(`${f.origin}/api/capabilities`)).status, 503);
  assert.equal(await slow, 408);
  assert.equal(f.requests.length, 0);
  assert.equal((await fetch(`${f.origin}/api/capabilities`)).status, 200);
});
