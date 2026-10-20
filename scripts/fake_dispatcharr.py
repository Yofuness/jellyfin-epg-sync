#!/usr/bin/env python3
"""Minimal fake of the Dispatcharr API endpoints the plugin uses, for the integration test.

Serves <data>/channels.json, <data>/groups.json and <data>/streams.json:
  GET  /api/channels/channels/       -> channels.json
  GET  /api/channels/groups/         -> groups.json
  POST /api/channels/streams/by-ids/ -> streams.json filtered by {"ids": [...]}
  GET  /logos/<file>.png             -> <data>/logos/<file>.png (no key; like Dispatcharr's logo cache URLs)
The API routes require an X-API-Key header, like the real API.
Usage: fake_dispatcharr.py <data dir> [port]
"""
import json
import sys
from http.server import BaseHTTPRequestHandler, HTTPServer

DATA = sys.argv[1]
PORT = int(sys.argv[2]) if len(sys.argv) > 2 else 8000


def load(name):
    with open(f"{DATA}/{name}", encoding="utf-8") as f:
        return json.load(f)


class Handler(BaseHTTPRequestHandler):
    def _send(self, status, body):
        payload = json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def _authorized(self):
        if self.headers.get("X-API-Key"):
            return True
        self._send(401, {"detail": "Authentication credentials were not provided."})
        return False

    def do_GET(self):
        if self.path.startswith("/logos/") and self.path.endswith(".png") and "/.." not in self.path:
            try:
                with open(f"{DATA}{self.path}", "rb") as f:
                    payload = f.read()
            except FileNotFoundError:
                self._send(404, {"detail": "Not found."})
                return
            self.send_response(200)
            self.send_header("Content-Type", "image/png")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)
            return
        if not self._authorized():
            return
        routes = {"/api/channels/channels/": "channels.json", "/api/channels/groups/": "groups.json"}
        if self.path in routes:
            self._send(200, load(routes[self.path]))
        else:
            self._send(404, {"detail": "Not found."})

    def do_POST(self):
        if not self._authorized():
            return
        if self.path != "/api/channels/streams/by-ids/":
            self._send(404, {"detail": "Not found."})
            return
        body = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0))) or b"{}")
        ids = set(body.get("ids", []))
        self._send(200, [s for s in load("streams.json") if s["id"] in ids])


HTTPServer(("0.0.0.0", PORT), Handler).serve_forever()
