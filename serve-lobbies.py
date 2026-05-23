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
<html lang="en"><head><meta charset="utf-8"><title>sf-multiplayer lobbies</title>
<style>
*{box-sizing:border-box;}
body{font-family:ui-monospace,SFMono-Regular,Menlo,monospace;background:#0e0e10;color:#e1e1e6;padding:1.5em;margin:0;line-height:1.45;}
h1{font-weight:600;font-size:1.4em;margin:0 0 .8em 0;color:#fff;}
header{display:flex;justify-content:space-between;align-items:center;flex-wrap:wrap;gap:.5em;}
header small{color:#888;font-size:.85em;}
#grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(280px,1fr));gap:1em;margin-top:1em;}
.lobby{background:#1a1a1f;border:1px solid #2a2a30;border-radius:8px;padding:1em;display:flex;flex-direction:column;gap:.5em;}
.lobby h2{margin:0;font-size:1.8em;font-weight:700;letter-spacing:.1em;color:#7fff7f;font-family:inherit;}
.lobby .meta{color:#888;font-size:.85em;}
.lobby .meta span{margin-right:1em;}
.connect{margin-top:.3em;}
.connect-string{display:flex;gap:.4em;align-items:center;}
.connect-string code{background:#0a0a0c;border:1px solid #2a2a30;padding:.4em .6em;border-radius:4px;flex:1;overflow-x:auto;white-space:nowrap;color:#a8c5ff;font-size:.8em;}
.connect-string button{background:#2a2a30;color:#e1e1e6;border:1px solid #3a3a40;padding:.4em .8em;border-radius:4px;cursor:pointer;font-family:inherit;font-size:.8em;}
.connect-string button:hover{background:#3a3a40;}
.connect-string button.copied{background:#7fff7f;color:#000;}
.empty{color:#666;text-align:center;padding:2em;border:1px dashed #333;border-radius:8px;}
.legend{margin-top:1.5em;color:#666;font-size:.85em;border-top:1px solid #2a2a30;padding-top:1em;}
.legend code{background:#1a1a1f;padding:.1em .3em;border-radius:3px;color:#a8c5ff;}
</style>
<meta http-equiv="refresh" content="10"></head><body>
<header>
  <h1>sf-multiplayer · lobbies</h1>
  <small id="ts"></small>
</header>
<div id="grid"></div>
<div class="legend">
  Connect from Steam Stick Fight with launch options:<br>
  <code>WINEDLLOVERRIDES="winhttp=n,b" %command% -address SERVER_IP -port PORT</code>
  &nbsp;&nbsp; · &nbsp;&nbsp; Once in: type <code>/help</code> in chat for admin commands.
</div>
<script>
function copyToClipboard(text, btn){
  navigator.clipboard.writeText(text).then(()=>{
    btn.textContent="copied";
    btn.classList.add("copied");
    setTimeout(()=>{btn.textContent="copy";btn.classList.remove("copied");},1500);
  });
}
fetch("/lobbies").then(r=>r.json()).then(d=>{
  document.getElementById("ts").textContent = "updated " + (d.generatedAt||"").slice(11,19) + " UTC · " + (d.lobbies?.length||0) + " running";
  const grid = document.getElementById("grid");
  if (!d.lobbies || !d.lobbies.length){
    grid.innerHTML = '<div class="empty">no lobbies running. start one with <code>./launch-lobby.sh CODE</code></div>';
    return;
  }
  // Detect server hostname for the connect string. If we're served from
  // localhost, leave as 127.0.0.1 (the user will edit); otherwise use the
  // page hostname.
  const host = location.hostname || "127.0.0.1";
  const cards = d.lobbies.map(l =>{
    const code = l.code || "?";
    const port = l.port || "?";
    const cmd = `-address ${host} -port ${port}`;
    const startedShort = (l.started||"").slice(11,19);
    return `<div class="lobby">
      <h2>${code}</h2>
      <div class="meta"><span>port ${port}</span><span>pid ${l.pid||"?"}</span>${startedShort?`<span>since ${startedShort}</span>`:""}</div>
      <div class="connect">
        <div class="connect-string">
          <code>${cmd}</code>
          <button onclick="copyToClipboard('${cmd}', this)">copy</button>
        </div>
      </div>
    </div>`;
  }).join("");
  grid.innerHTML = cards;
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
        if self.path in ("/healthz", "/healthz/"):
            # Simple liveness probe for monitoring (Prometheus, Uptime Robot,
            # etc.). 200 if process is up + registry is readable.
            alive_count = sum(1 for l in load_lobbies() if l.get("alive"))
            body = json.dumps({"status": "ok", "lobbiesAlive": alive_count}).encode()
            self._send(200, "application/json", body)
            return
        self._send(404, "text/plain", b"Not found. Try GET /  or  GET /lobbies  or  GET /healthz\n")

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
