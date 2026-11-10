#!/usr/bin/env python3
"""Isolated host fixture for the Phase 2 login workflow.

Listens on 0.0.0.0:8765. GET /login returns 200 with a small body and appends a
JSON line with the request timestamp. Set FIXTURE_BODY_BYTES to return a large
body (used to make a bandwidth-shaped path exceed the app's read timeout).
Bind address and port are configurable for test isolation.
"""

import json
import os
import sys
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

BODY_BYTES = int(os.environ.get("FIXTURE_BODY_BYTES", "2"))
HOST = os.environ.get("FIXTURE_HOST", "0.0.0.0")
PORT = int(os.environ.get("FIXTURE_PORT", "8765"))
LOG = os.environ.get("FIXTURE_LOG", "")


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def do_GET(self):  # noqa: N802
        started = time.time()
        if self.path.startswith("/health"):
            body = b"OK"
            status = 200
        elif self.path.startswith("/login"):
            body = b"X" * BODY_BYTES
            status = 200
        else:
            body = b"not found"
            status = 404

        self.send_response(status)
        self.send_header("Content-Type", "text/plain")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Connection", "close")
        self.end_headers()
        try:
            self.wfile.write(body)
            self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError):
            pass

        record = {
            "at": time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime(started)),
            "path": self.path,
            "status": status,
            "bytes": len(body),
            "durationMs": round((time.time() - started) * 1000, 1),
            "client": self.client_address[0],
        }
        line = json.dumps(record, separators=(",", ":"))
        print(line, flush=True)
        if LOG:
            with open(LOG, "a", encoding="utf-8") as handle:
                handle.write(line + "\n")

    def log_message(self, *_args):
        pass


if __name__ == "__main__":
    server = ThreadingHTTPServer((HOST, PORT), Handler)
    print(f"fixture listening on {HOST}:{PORT} body={BODY_BYTES}", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
