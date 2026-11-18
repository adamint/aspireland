from __future__ import annotations

import json
import os
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

try:
    from .generator import generate_from_payload
except ImportError:
    from generator import generate_from_payload

MAX_BODY = 64 * 1024


class DemandHandler(BaseHTTPRequestHandler):
    server_version = "MetroOpsDemand/1.0"

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
            self._json(200, {"status": "healthy"})
            return
        self._json(404, {"error": "not found"})

    def do_POST(self) -> None:
        if self.path != "/demand":
            self._json(404, {"error": "not found"})
            return
        content_type = self.headers.get("Content-Type", "").split(";", 1)[0].strip().lower()
        if content_type != "application/json":
            self._json(400, {"error": "Content-Type must be application/json"})
            return
        try:
            raw = self._read_body()
        except ValueError:
            self._json(400, {"error": "invalid or oversized request body framing"})
            return
        try:
            payload = json.loads(raw)
            result = generate_from_payload(payload)
        except json.JSONDecodeError as exc:
            self._json(400, {"error": f"invalid JSON: {exc.msg}"})
            return
        except UnicodeDecodeError:
            self._json(400, {"error": "request body must be UTF-8 JSON"})
            return
        except ValueError as exc:
            self._json(400, {"error": str(exc)})
            return
        self._json(200, result)

    def _read_body(self) -> bytes:
        transfer_encoding = self.headers.get("Transfer-Encoding", "").strip().lower()
        length_text = self.headers.get("Content-Length")
        if transfer_encoding:
            if transfer_encoding != "chunked" or length_text is not None:
                raise ValueError("unsupported transfer encoding")
            body = bytearray()
            while True:
                size_line = self.rfile.readline(129)
                if not size_line.endswith(b"\r\n") or len(size_line) > 128:
                    raise ValueError("invalid chunk size")
                try:
                    size = int(size_line[:-2].split(b";", 1)[0], 16)
                except ValueError as exc:
                    raise ValueError("invalid chunk size") from exc
                if size < 0 or len(body) + size > MAX_BODY:
                    raise ValueError("request body too large")
                if size == 0:
                    while True:
                        trailer = self.rfile.readline(4097)
                        if trailer == b"\r\n":
                            return bytes(body)
                        if not trailer.endswith(b"\r\n") or len(trailer) > 4096:
                            raise ValueError("invalid chunk trailer")
                chunk = self.rfile.read(size)
                if len(chunk) != size or self.rfile.read(2) != b"\r\n":
                    raise ValueError("incomplete chunk")
                body.extend(chunk)
        if length_text is None:
            raise ValueError("missing Content-Length")
        try:
            length = int(length_text)
        except ValueError as exc:
            raise ValueError("invalid Content-Length") from exc
        if length < 0 or length > MAX_BODY:
            raise ValueError("request body too large")
        body = self.rfile.read(length)
        if len(body) != length:
            raise ValueError("incomplete request body")
        return body

    def log_message(self, format: str, *args: object) -> None:
        print(f"demand {self.address_string()} {format % args}")


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
    server = ThreadingHTTPServer(("0.0.0.0", _port()), DemandHandler)
    print(f"demand listening on {server.server_address[1]}")
    server.serve_forever()


if __name__ == "__main__":
    main()
