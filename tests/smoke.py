"""Live acceptance check. Resets the local scenario; never point this at a shared deployment."""
import argparse
import json
import time
import urllib.error
import urllib.request
import uuid


def request(base, path, body=None, raw=None, content_type=None, timeout=120):
    headers = {}
    data = raw
    if body is not None:
        data = json.dumps(body).encode()
        headers["Content-Type"] = "application/json"
    if content_type:
        headers["Content-Type"] = content_type
    req = urllib.request.Request(base + path, data=data, headers=headers)
    with urllib.request.urlopen(req, timeout=timeout) as response:
        payload = response.read()
        if "json" in response.headers.get("Content-Type", ""):
            return json.loads(payload)
        return payload


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--operations", required=True, help="operations-web URL")
    parser.add_argument("--passenger", required=True, help="passenger-web URL")
    parser.add_argument("--voice", action="store_true", help="Make real paid OpenAI text and speech requests")
    parser.add_argument("--resume-current", action="store_true",
                        help="Skip the initial reset when retrying a preserved local scenario; still resets on success")
    args = parser.parse_args()
    ops, public = args.operations.rstrip("/"), args.passenger.rstrip("/")
    evidence = {}
    if not args.resume_current:
        request(ops, "/api/scenarios", {"seed": 20261117, "preset": "normal", "startTime": "2026-11-17T07:30:00Z"})
    request(ops, "/api/clock", {"speed": 60, "paused": False})
    initial = request(ops, "/api/world")["world"]
    evidence["scenario"] = {"id": initial["scenarioId"], "generation": initial["generation"], "resumed": args.resume_current}
    require(len(initial["trains"]) == 12, "All twelve train identities must exist.")
    completed = 0
    active_trains = [t for t in initial["trains"] if t["state"] != "reserve"]
    terminals = {line["id"]: (line["stations"][0], line["stations"][-1])
                 for line in request(ops, "/api/world")["network"]["lines"]}
    terminal_departures = {}
    deadline = time.monotonic() + 1200
    while time.monotonic() < deadline:
        time.sleep(2)
        world = request(ops, "/api/world")["world"]
        metrics = world["metrics"]
        active = sum(c["count"] for c in world["cohorts"] if c["state"] not in ("completed", "abandoned"))
        require(metrics["generated"] == active + metrics["completed"] + metrics["abandoned"], "Passenger conservation failed.")
        require(all(0 <= t["load"] <= t["capacity"] for t in world["trains"]), "Train capacity invariant failed.")
        onboard = sum(c["count"] for c in world["cohorts"] if c["state"] == "onboard")
        require(sum(t["load"] for t in world["trains"]) == onboard, "Worker and scenario passenger inventories diverged.")
        active_grants = [g for g in world["grants"].values() if not g["completed"]]
        require(len({g["block"] for g in active_grants}) == len(active_grants), "Conflicting active block grants exist.")
        terminal_departures = {
            train["id"]: sorted({g["from"] for g in world["grants"].values()
                                 if g["trainId"] == train["id"] and g["completed"]
                                 and g["from"] in terminals[train["line"]]})
            for train in active_trains
        }
        completed = metrics["completed"]
        if completed >= 10 and all(len(stops) == 2 for stops in terminal_departures.values()):
            break
    require(completed >= 10, "No meaningful end-to-end passenger completion occurred within 1200 seconds.")
    require(all(len(stops) == 2 for stops in terminal_departures.values()),
            f"Some trains did not complete repeated terminal turnbacks: {terminal_departures}")
    evidence["flow"] = {"simulatedTime": world["simTime"], "generated": metrics["generated"], "completed": completed, "active": active,
                        "abandoned": metrics["abandoned"], "onboard": onboard, "conservationDelta": 0,
                        "completedTerminalDepartures": terminal_departures}
    request(ops, "/api/clock", {"paused": True})

    def action(kind, target, value=None, amount=None, scenario=None):
        state = scenario or request(ops, "/api/world")["world"]
        return request(ops, "/api/actions", {
            "action": {"kind": kind, "target": target, "value": value, "amount": amount},
            "scenarioId": state["scenarioId"], "generation": state["generation"],
            "commandId": str(uuid.uuid4()), "reason": "Live acceptance check",
        })

    route_request = {"origin": "airport", "destination": "medical-center", "accessible": True}
    before = request(public, "/api/journeys", route_request)
    require(before["options"], "The ordinary, non-AI journey form must return a real accessible route.")
    require(all(o["totalSeconds"] >= sum(l["rideSeconds"] + l["waitSeconds"] + l["walkSeconds"] for l in o["legs"]) - 1
                for o in before["options"]), "Advertised trip time omits a component.")
    disrupted_hub = next(l["from"] for l in before["options"][0]["legs"]
                        if l["walkSeconds"] > 0 and l["from"] in ("central-exchange", "union-terminal"))
    incident = action("incident", disrupted_hub, "elevator-outage", 1200)
    require(incident["accepted"], "Manual elevator incident was not accepted.")
    after = request(public, "/api/journeys", route_request)
    require(after["options"], "An accessible alternative through Union Terminal must remain usable.")
    for itinerary in after["options"]:
        require(not any(l["from"] == disrupted_hub and l["walkSeconds"] > 0 for l in itinerary["legs"]),
                "The route still uses the inaccessible interchange transfer.")
    evidence["accessibleReroute"] = {"disruptedHub": disrupted_hub, "before": before["options"][0], "after": after["options"][0]}
    current = request(ops, "/api/world")["world"]
    projection_deadline = time.monotonic() + 15
    while True:
        board = request(public, f"/api/stations/{disrupted_hub}/arrivals")
        if board["scenarioId"] == current["scenarioId"] and any("elevator" in r.lower() for r in board["restrictions"]):
            break
        if time.monotonic() >= projection_deadline:
            break
        time.sleep(0.2)
    require(board["scenarioId"] == current["scenarioId"], "The board serves a previous scenario.")
    require(any("elevator" in r.lower() for r in board["restrictions"]), "The incident did not reach public station information.")
    action("clear-incident", incident["entityId"])

    try:
        request(public, "/api/actions", {"action": {"kind": "deploy-reserve"}})
    except urllib.error.HTTPError as error:
        require(error.code in (401, 403, 404, 405), "Unexpected passenger write response.")
    else:
        raise AssertionError("The passenger service exposed an operational write endpoint.")
    evidence["passengerWrites"] = "not exposed"

    current = request(ops, "/api/world")["world"]
    comparison = request(ops, "/api/compare", {
        "action": {"kind": "set-headway", "target": "Blue", "amount": 900},
        "scenarioId": current["scenarioId"], "generation": current["generation"],
    })
    require(comparison["samples"] == 8 and comparison["horizonMinutes"] == 30, "Forecast does not use the requested bounded comparison.")
    require(comparison["baseline"]["waitingMinutes"]["mean"] > 0, "A paused checkpoint did not advance through the forecast horizon.")
    for side in ("baseline", "intervention"):
        for value in comparison[side].values():
            require(value["min"] <= value["mean"] <= value["max"], "A forecast range is invalid.")
    evidence["forecast"] = comparison

    if args.voice:
        capabilities = request(public, "/api/capabilities")
        require(all(capabilities[k] for k in ("text", "speech", "transcription")), "Real model capabilities are not configured.")
        audio = request(public, "/api/speech", {"text": "What is the fastest accessible route from University to the airport?"})
        require(len(audio) > 1000, "Speech synthesis did not return real audio.")
        boundary = "MetroOps" + uuid.uuid4().hex
        multipart = (
            f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"journey.mp3\"\r\n"
            "Content-Type: audio/mpeg\r\n\r\n"
        ).encode() + audio + f"\r\n--{boundary}--\r\n".encode()
        transcript = request(public, "/api/transcribe", raw=multipart, content_type=f"multipart/form-data; boundary={boundary}")
        answer = request(public, "/api/assistant", {"message": transcript["text"]})
        require(answer.get("journey", {}).get("options"), "The real voice question did not produce a tool-returned itinerary.")
        require("plan_journey" in answer.get("tools", []), "The assistant did not call the authoritative journey tool.")
        evidence["voice"] = {"audioBytes": len(audio), "transcript": transcript["text"], "tools": answer["tools"],
                             "journeyScenario": answer["journey"]["scenarioId"]}
    else:
        evidence["voice"] = "not requested"

    old = current
    request(ops, "/api/scenarios", {"seed": 20261117, "preset": "normal"})
    require(not action("hold-train", "blue-1", amount=60, scenario=old)["accepted"], "A command from before reset was applied.")
    request(ops, "/api/clock", {"speed": 10, "paused": False})
    print(json.dumps(evidence, indent=2))


if __name__ == "__main__":
    main()
