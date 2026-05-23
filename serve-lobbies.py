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


HTML_VIEW = """<!doctype html>
<html><head><meta charset="utf-8"><title>sf-multiplayer lobbies</title>
<style>body{font-family:ui-monospace,monospace;background:#1a1a1a;color:#ddd;padding:1em;}
table{border-collapse:collapse;width:100%;}th,td{border:1px solid #444;padding:.4em .7em;text-align:left;}
th{background:#222;}.up{color:#7fff7f;}.stale{color:#ff7f7f;}h1{font-weight:normal;font-size:1.2em;}</style>
<meta http-equiv="refresh" content="5"></head><body>
<h1>sf-multiplayer lobbies <small id="ts"></small></h1>
<table><thead><tr><th>code</th><th>port</th><th>bridge</th><th>pid</th><th>started</th></tr></thead>
<tbody id="rows"></tbody></table>
<script>
fetch("/lobbies").then(r=>r.json()).then(d=>{
  document.getElementById("ts").textContent = "(updated " + d.generatedAt + ")";
  const rows = d.lobbies.map(l =>
    `<tr><td>${l.code||"?"}</td><td>${l.port||"?"}</td><td>${l.bridge||"?"}</td>` +
    `<td>${l.pid||"?"}</td><td>${l.started||"?"}</td></tr>`).join("");
  document.getElementById("rows").innerHTML = rows ||
    `<tr><td colspan=5 style="color:#888">no lobbies running</td></tr>`;
});
</script></body></html>
"""


class LobbyHandler(BaseHTTPRequestHandler):
    def do_GET(self) -> None:
        if self.path in ("", "/", "/index.html"):
            self._send(200, "text/html; charset=utf-8", HTML_VIEW.encode())
            return
        if self.path in ("/lobbies", "/lobbies/"):
            body = json.dumps(
                {
                    "generatedAt": datetime.now(timezone.utc).isoformat(),
                    "registry": REGISTRY_DIR,
                    "lobbies": [lobby for lobby in load_lobbies() if lobby.get("alive")],
                },
                indent=2,
            ).encode()
            self._send(200, "application/json", body)
            return
        self._send(404, "text/plain", b"Not found. Try GET /  or  GET /lobbies\n")

    def _send(self, code: int, ctype: str, body: bytes) -> None:
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Access-Control-Allow-Origin", "*")
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
