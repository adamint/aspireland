from __future__ import annotations

import json
import os
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

try:
    from .service import AnalyticsPoller
except ImportError:
    from service import AnalyticsPoller


class AnalyticsHandler(BaseHTTPRequestHandler):
    server_version = "MetroOpsAnalytics/1.0"
    poller: AnalyticsPoller

    def _json(self, status: int, body: object) -> None:
        encoded = json.dumps(body, separators=(",", ":"), allow_nan=False).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(encoded)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(encoded)

    def do_GET(self) -> None:
        if self.path == "/health":
            health = self.poller.health_response()
            self._json(200 if health["ready"] else 503, health)
            return
        if self.path == "/analytics":
            status, body = self.poller.analytics_response()
            self._json(status, body)
            return
        if self.path == "/inventory":
            status, body = self.poller.inventory_response()
            self._json(status, body)
            return
        self._json(404, {"error": "not found"})

    def log_message(self, format: str, *args: object) -> None:
        print(f"analytics {self.address_string()} {format % args}")


def _port() -> int:
    raw = os.environ.get("PORT", "8080")
    try:
        port = int(raw)
    except ValueError as exc:
        raise SystemExit("PORT must be an integer") from exc
    if not 1 <= port <= 65535:
        raise SystemExit("PORT must be between 1 and 65535")
    return port


def main() -> None:
    scenario_url = os.environ.get("SCENARIO_URL")
    if not scenario_url:
        raise SystemExit("SCENARIO_URL is required")
    poller = AnalyticsPoller(scenario_url)
    AnalyticsHandler.poller = poller
    poller.start()
    server = ThreadingHTTPServer(("0.0.0.0", _port()), AnalyticsHandler)
    print(f"analytics listening on {server.server_address[1]}")
    try:
        server.serve_forever()
    finally:
        poller.stop()


if __name__ == "__main__":
    main()
