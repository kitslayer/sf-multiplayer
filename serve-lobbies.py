#!/usr/bin/env python3
"""HTTP lobby-browser endpoint for sf-multiplayer (Phase 6.13 v1.5).

Reads the lobby registry at $SF_LOBBIES_DIR (default /tmp/sf-lobbies/) and
serves it as JSON over HTTP. Any server browser (in-game mod, web UI,
external tool) can poll GET /lobbies to discover running lobbies.

Usage:
    ./serve-lobbies.py                       # bind 0.0.0.0:8080
    ./serve-lobbies.py --port 8080
    ./serve-lobbies.py --host 127.0.0.1

Endpoint:
    GET /lobbies   ->  {"generatedAt": "...", "lobbies": [...]}

Each lobby entry:
    {"code": "AAAA", "port": "1337", "bridge": "11337", "pid": "12345",
     "log": "/tmp/...", "started": "2026-05-23T12:00:00Z", "alive": true}

Stale entries (pid is dead) are filtered out. Cheap enough to poll at 1Hz.
No external deps; only stdlib.
"""
from __future__ import annotations

import argparse
import json
import os
import signal
import sys
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, HTTPServer

REGISTRY_DIR = os.environ.get("SF_LOBBIES_DIR", "/tmp/sf-lobbies")


def load_lobbies() -> list[dict]:
    entries: list[dict] = []
    if not os.path.isdir(REGISTRY_DIR):
        return entries
    for name in os.listdir(REGISTRY_DIR):
        if not name.endswith(".conf"):
            continue
        path = os.path.join(REGISTRY_DIR, name)
        entry: dict = {}
        try:
            with open(path) as f:
                for line in f:
                    if "=" not in line:
                        continue
                    k, v = line.strip().split("=", 1)
                    entry[k] = v
        except OSError:
            continue
        pid_s = entry.get("pid", "")
        alive = False
        if pid_s.isdigit():
            try:
                os.kill(int(pid_s), 0)
                alive = True
            except OSError:
                alive = False
        entry["alive"] = alive
        entries.append(entry)
    return entries


class LobbyHandler(BaseHTTPRequestHandler):
    def do_GET(self) -> None:
        if self.path not in ("/lobbies", "/lobbies/"):
            self.send_response(404)
            self.end_headers()
            self.wfile.write(b"Not found. Try GET /lobbies\n")
            return
        body = json.dumps(
            {
                "generatedAt": datetime.now(timezone.utc).isoformat(),
                "registry": REGISTRY_DIR,
                "lobbies": [lobby for lobby in load_lobbies() if lobby.get("alive")],
            },
            indent=2,
        ).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Access-Control-Allow-Origin", "*")  # in-game / web UI can call
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, fmt: str, *args) -> None:
        pass  # silence default access-log spam


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--host", default="0.0.0.0")
    ap.add_argument("--port", type=int, default=8080)
    args = ap.parse_args()

    httpd = HTTPServer((args.host, args.port), LobbyHandler)
    print(f"Lobby browser → http://{args.host}:{args.port}/lobbies (registry={REGISTRY_DIR})")
    signal.signal(signal.SIGINT, lambda *_: sys.exit(0))
    httpd.serve_forever()
    return 0


if __name__ == "__main__":
    sys.exit(main())
