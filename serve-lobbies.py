#!/usr/bin/env python3
"""HTTP lobby-browser endpoint for sf-multiplayer (Phase 6.13 v1.5).

Reads the lobby registry at $SF_LOBBIES_DIR (default /tmp/sf-lobbies/) and
serves it as JSON over HTTP. Any server browser (in-game mod, web UI,
external tool) can poll GET /lobbies to discover running lobbies.

Usage:
    ./serve-lobbies.py                       # bind 0.0.0.0:8080
    ./serve-lobbies.py --port 8080
    ./serve-lobbies.py --host 127.0.0.1

Endpoints:
    GET  /lobbies        ->  {"generatedAt": "...", "lobbies": [...]}
    GET  /healthz        ->  {"status":"ok","lobbiesAlive":N}
    POST /lobbies        ->  create on demand (header X-SF-Token, rate-limit,
                             SF_MAX_LOBBIES cap); 200 {"code","port"}
    POST /lobbies/stop   ->  {"code":"AAAA"} stop a lobby (token-gated)

A background reaper stops dead-pid lobbies and lobbies empty (per the router's
/router/stats) for SF_LOBBY_EMPTY_TTL. See notes/ROUTER.md.

Each lobby entry:
    {"code": "AAAA", "port": "1337", "bridge": "11337", "pid": "12345",
     "log": "/tmp/...", "started": "2026-05-23T12:00:00Z", "alive": true}

Stale entries (pid is dead) are filtered out. Cheap enough to poll at 1Hz.
No external deps; only stdlib.
"""
from __future__ import annotations

import argparse
import hmac
import json
import os
import random
import re
import signal
import subprocess
import sys
import threading
import time
import urllib.request
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

REGISTRY_DIR = os.environ.get("SF_LOBBIES_DIR", "/tmp/sf-lobbies")
REPO_DIR = os.path.dirname(os.path.abspath(__file__))

# --- Control plane (POST /lobbies create/stop) config -----------------------
# CONTROL_TOKEN gates lobby creation. Fail closed: if unset, creation is
# disabled (the in-game CREATE button gets 403). The installer bakes the same
# token into the client so the in-game button can authenticate. It is a shared
# secret, not real per-user auth — the real abuse protections are the per-IP
# rate limit, the max-lobbies cap, and the empty-lobby reaper.
CONTROL_TOKEN = os.environ.get("SF_CONTROL_TOKEN", "")
MAX_LOBBIES = int(os.environ.get("SF_MAX_LOBBIES", "10"))   # matches launch-lobby.sh
CREATE_MIN_INTERVAL = float(os.environ.get("SF_CREATE_MIN_INTERVAL", "10"))  # sec/IP between creates
LAUNCH_TIMEOUT = float(os.environ.get("SF_LAUNCH_TIMEOUT", "45"))  # launch-lobby.sh waits ≤30s for bind

# Lobby codes are A-Z0-9 (router maxCodeLen=16). Validate any client-supplied
# code before it reaches the shell or a registry file path.
LOBBY_CODE_RE = re.compile(r"^[A-Z0-9]{1,16}$")

# --- Reaper config -----------------------------------------------------------
ROUTER_STATS_URL = os.environ.get("SF_ROUTER_STATS", "http://127.0.0.1:8081/router/stats")
REAP_INTERVAL = float(os.environ.get("SF_REAP_INTERVAL", "30"))   # sec between reaper passes
LOBBY_MIN_AGE = float(os.environ.get("SF_LOBBY_MIN_AGE", "120"))  # don't reap a lobby younger than this
EMPTY_TTL = float(os.environ.get("SF_LOBBY_EMPTY_TTL", "300"))    # reap after this long with 0 clients

# per-IP last-create timestamps (rate limit) + per-code first-seen-empty (reaper)
_last_create: dict[str, float] = {}
_empty_since: dict[str, float] = {}
_create_lock = threading.Lock()


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
  // Escape registry values before injecting into the DOM (defense in depth —
  // codes are server-generated A-Z0-9, but never trust .conf data); attach the
  // copy handler via a listener instead of an inline onclick (no string injection).
  const esc = s => String(s).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
  const cards = d.lobbies.map(l =>{
    const code = esc(l.code || "?");
    const port = esc(l.port || "?");
    const pid = esc(l.pid || "?");
    const cmd = `-address ${esc(host)} -port ${port}`;
    const startedShort = esc((l.started||"").slice(11,19));
    return `<div class="lobby">
      <h2>${code}</h2>
      <div class="meta"><span>port ${port}</span><span>pid ${pid}</span>${startedShort?`<span>since ${startedShort}</span>`:""}</div>
      <div class="connect">
        <div class="connect-string">
          <code>${cmd}</code>
          <button class="copybtn" data-cmd="${cmd}">copy</button>
        </div>
      </div>
    </div>`;
  }).join("");
  grid.innerHTML = cards;
  grid.querySelectorAll(".copybtn").forEach(b => b.addEventListener("click", () => copyToClipboard(b.getAttribute("data-cmd"), b)));
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

    def do_POST(self) -> None:
        # POST /lobbies        → create a lobby on demand (token + rate-limit + cap)
        # POST /lobbies/stop    → stop a lobby by code (token-gated)
        path = self.path.rstrip("/")
        if path == "/lobbies":
            self._handle_create()
            return
        if path == "/lobbies/stop":
            self._handle_stop()
            return
        self._send_json(404, {"error": "not found"})

    def _client_ip(self) -> str:
        # Do NOT trust X-Forwarded-For by default: a client can spoof it to evade
        # the per-IP rate limit. Only honor it behind a trusted reverse proxy
        # (set SF_TRUST_XFF=1 in that deployment).
        if os.environ.get("SF_TRUST_XFF") == "1":
            xff = self.headers.get("X-Forwarded-For", "")
            if xff:
                return xff.split(",")[0].strip()
        return self.client_address[0]

    def _authed(self) -> bool:
        # Fail closed: no server token configured → creation disabled.
        if not CONTROL_TOKEN:
            return False
        # Constant-time compare (avoid a token-timing side channel).
        return hmac.compare_digest(self.headers.get("X-SF-Token", ""), CONTROL_TOKEN)

    def _handle_create(self) -> None:
        if not self._authed():
            self._send_json(403, {"error": "forbidden (bad or missing token; creation may be disabled)"})
            return
        ip = self._client_ip()
        now = time.time()
        with _create_lock:
            last = _last_create.get(ip, 0.0)
            if now - last < CREATE_MIN_INTERVAL:
                self._send_json(429, {"error": "slow down", "retryAfterSec": round(CREATE_MIN_INTERVAL - (now - last), 1)})
                return
            live = [l for l in load_lobbies() if l.get("alive")]
            if len(live) >= MAX_LOBBIES:
                self._send_json(429, {"error": "server at lobby capacity", "max": MAX_LOBBIES})
                return
            _last_create[ip] = now  # reserve the slot before the slow spawn

        code, port, err = create_lobby()
        if err:
            self._send_json(500, {"error": err})
            return
        print(f"[control] created lobby {code} on port {port} (by {ip})")
        self._send_json(200, {"code": code, "port": port})

    def _handle_stop(self) -> None:
        if not self._authed():
            self._send_json(403, {"error": "forbidden"})
            return
        try:
            # Cap the body read so a huge Content-Length can't stall/OOM a thread.
            length = min(int(self.headers.get("Content-Length", "0")), 4096)
            body = json.loads(self.rfile.read(length) or b"{}")
            code = str(body.get("code", "")).strip().upper()
        except (ValueError, json.JSONDecodeError):
            self._send_json(400, {"error": "bad json"})
            return
        if not LOBBY_CODE_RE.match(code):
            self._send_json(400, {"error": "missing or invalid code"})
            return
        ok = stop_lobby(code)
        self._send_json(200 if ok else 500, {"code": code, "stopped": ok})

    def _send_json(self, code: int, obj: dict) -> None:
        self._send(code, "application/json", json.dumps(obj).encode())

    def _send(self, code: int, ctype: str, body: bytes) -> None:
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, fmt: str, *args) -> None:
        pass  # silence default access-log spam


# ---------------------------------------------------------------------------
# Lobby lifecycle: create (launch-lobby.sh), stop (stop-lobby.sh), reaper.
# ---------------------------------------------------------------------------

_CODE_ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"


def _gen_code() -> str:
    """A 4-char code not currently in the registry."""
    existing = {l.get("code", "") for l in load_lobbies()}
    for _ in range(20):
        code = "".join(random.choice(_CODE_ALPHABET) for _ in range(4))
        if code not in existing:
            return code
    return "".join(random.choice(_CODE_ALPHABET) for _ in range(4))


def create_lobby() -> tuple[str, int, str | None]:
    """Spawn a backend lobby via launch-lobby.sh. Returns (code, port, err)."""
    code = _gen_code()
    script = os.path.join(REPO_DIR, "launch-lobby.sh")
    try:
        proc = subprocess.run(
            ["bash", script, code],
            cwd=REPO_DIR, capture_output=True, text=True, timeout=LAUNCH_TIMEOUT,
        )
    except subprocess.TimeoutExpired:
        return code, 0, "lobby launch timed out"
    except OSError as e:
        return code, 0, f"launch failed: {e}"
    if proc.returncode != 0:
        tail = (proc.stderr or proc.stdout or "").strip().splitlines()[-1:] or ["unknown error"]
        return code, 0, f"launch-lobby.sh exit {proc.returncode}: {tail[0]}"
    # launch-lobby.sh wrote the .conf before returning; read the assigned port.
    conf = os.path.join(REGISTRY_DIR, f"{code}.conf")
    port = 0
    try:
        with open(conf) as f:
            for line in f:
                if line.startswith("port="):
                    port = int(line.strip().split("=", 1)[1])
                    break
    except (OSError, ValueError):
        pass
    if port <= 0:
        return code, 0, "lobby started but port unknown"
    return code, port, None


def stop_lobby(code: str) -> bool:
    script = os.path.join(REPO_DIR, "stop-lobby.sh")
    try:
        proc = subprocess.run(
            ["bash", script, code],
            cwd=REPO_DIR, capture_output=True, text=True, timeout=30,
        )
        return proc.returncode == 0
    except (subprocess.TimeoutExpired, OSError):
        return False


def _router_bycode() -> dict | None:
    """Per-code flow counts from the router's /router/stats, or None if down."""
    try:
        with urllib.request.urlopen(ROUTER_STATS_URL, timeout=2) as resp:
            data = json.loads(resp.read())
        bc = data.get("byCode")
        return bc if isinstance(bc, dict) else {}
    except Exception:
        return None


def _lobby_age(entry: dict) -> float:
    started = entry.get("started", "")
    try:
        t = datetime.fromisoformat(started.replace("Z", "+00:00"))
        return (datetime.now(timezone.utc) - t).total_seconds()
    except (ValueError, AttributeError):
        return 1e9  # unknown age → treat as old (don't shield from reaping)


def reaper_loop() -> None:
    """Periodically stop dead-pid lobbies and long-empty lobbies.

    Empty-detection uses the router's per-code client counts; if the router is
    unreachable we fail safe and only clean up dead-pid lobbies (we can't tell
    emptiness without it).
    """
    while True:
        time.sleep(REAP_INTERVAL)
        try:
            lobbies = load_lobbies()
            bycode = _router_bycode()
            now = time.time()
            seen_codes = set()
            for l in lobbies:
                code = l.get("code", "")
                if not code:
                    continue
                seen_codes.add(code)
                if not l.get("alive"):
                    print(f"[reaper] dead pid → stopping stale lobby {code}")
                    stop_lobby(code)
                    _empty_since.pop(code, None)
                    continue
                if bycode is None:
                    continue  # router down → don't emptiness-reap
                clients = int(bycode.get(code, 0))
                if clients > 0:
                    _empty_since.pop(code, None)
                    continue
                if _lobby_age(l) < LOBBY_MIN_AGE:
                    continue  # grace for freshly-created lobbies nobody joined yet
                first = _empty_since.setdefault(code, now)
                if now - first >= EMPTY_TTL:
                    print(f"[reaper] empty {EMPTY_TTL:.0f}s → stopping lobby {code}")
                    stop_lobby(code)
                    _empty_since.pop(code, None)
            # forget codes that no longer exist
            for code in list(_empty_since):
                if code not in seen_codes:
                    _empty_since.pop(code, None)
        except Exception as e:  # never let the reaper thread die
            print(f"[reaper] pass error: {e}")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--host", default="0.0.0.0")
    ap.add_argument("--port", type=int, default=8080)
    ap.add_argument("--no-reaper", action="store_true", help="disable the empty/dead-lobby reaper")
    args = ap.parse_args()

    # ThreadingHTTPServer so a slow create (launch-lobby.sh waits for UDP bind)
    # doesn't block GET /lobbies polling from the browser.
    httpd = ThreadingHTTPServer((args.host, args.port), LobbyHandler)
    print(f"Lobby browser → http://{args.host}:{args.port}/lobbies (registry={REGISTRY_DIR})")
    print(f"  control: POST /lobbies (create) — {'ENABLED' if CONTROL_TOKEN else 'DISABLED (no SF_CONTROL_TOKEN)'}; "
          f"max={MAX_LOBBIES}")
    if not args.no_reaper:
        threading.Thread(target=reaper_loop, name="reaper", daemon=True).start()
        print(f"  reaper: every {REAP_INTERVAL:.0f}s; empty>{EMPTY_TTL:.0f}s via {ROUTER_STATS_URL}")
    signal.signal(signal.SIGINT, lambda *_: sys.exit(0))
    httpd.serve_forever()
    return 0


if __name__ == "__main__":
    sys.exit(main())
