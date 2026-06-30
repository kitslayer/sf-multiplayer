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
import socket
import struct
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
# code before it reaches the shell or a registry file path. Anchor with \Z, not
# $: Python's $ also matches just before a trailing newline, so "AAAA\n" would
# slip through (callers strip today, but the validator must be correct on its
# own — defense in depth against a future non-stripping caller).
LOBBY_CODE_RE = re.compile(r"^[A-Z0-9]{1,16}\Z")

# Always-on lobbies that must appear in the browser even though they weren't
# spawned via launch-lobby.sh — most importantly the persistent MAIN lobby that
# Quick/Host Match connects to. Without this the browser shows "no lobbies" even
# though MAIN is up (it has no .conf in the registry). Format: "CODE:PORT,CODE".
# Example: SF_STATIC_LOBBIES="MAIN:1337"
STATIC_LOBBIES = os.environ.get("SF_STATIC_LOBBIES", "")

# --- Reaper config -----------------------------------------------------------
ROUTER_STATS_URL = os.environ.get("SF_ROUTER_STATS", "http://127.0.0.1:8081/router/stats")
REAP_INTERVAL = float(os.environ.get("SF_REAP_INTERVAL", "30"))   # sec between reaper passes
LOBBY_MIN_AGE = float(os.environ.get("SF_LOBBY_MIN_AGE", "120"))  # don't reap a lobby younger than this
EMPTY_TTL = float(os.environ.get("SF_LOBBY_EMPTY_TTL", "300"))    # reap after this long with 0 clients

# --- GET rate limit (per source IP token bucket) -----------------------------
# A generous backstop against one source hammering the endpoints. The 1s lobby
# cache already bounds per-request cost; this bounds request volume. Defaults sit
# well above any legitimate poller (the browser refreshes every 10s). Set
# SF_GET_RATE_REFILL=0 to disable. Behind a reverse proxy, prefer the proxy's
# rate limiting.
GET_RATE_BURST = float(os.environ.get("SF_GET_RATE_BURST", "120"))    # max tokens (burst)
GET_RATE_REFILL = float(os.environ.get("SF_GET_RATE_REFILL", "20"))   # tokens/sec (0 disables)

# per-IP last-create timestamps (rate limit) + per-code first-seen-empty (reaper)
_last_create: dict[str, float] = {}
_empty_since: dict[str, float] = {}
_create_lock = threading.Lock()

# GET rate-limit buckets (ip -> (tokens, last_ts)) + short-TTL lobby-list cache.
_get_buckets: dict[str, tuple[float, float]] = {}
_get_buckets_lock = threading.Lock()
_lobbies_cache: dict = {"at": 0.0, "data": None}
_lobbies_cache_lock = threading.Lock()


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


def _load_lobbies_cached(ttl: float = 1.0) -> list[dict]:
    """load_lobbies() behind a short TTL so a fleet of GET pollers doesn't
    re-stat the registry on every request. Returns fresh per-entry copies so
    callers (merge/enrich) can mutate without corrupting the cached snapshot."""
    now = time.time()
    with _lobbies_cache_lock:
        if _lobbies_cache["data"] is None or now - _lobbies_cache["at"] >= ttl:
            _lobbies_cache["data"] = load_lobbies()
            _lobbies_cache["at"] = now
        snapshot = _lobbies_cache["data"]
    return [dict(e) for e in snapshot]


def _allow_get(ip: str) -> bool:
    """Per-IP token bucket for GET requests. Generous by default; refill<=0 disables."""
    if GET_RATE_REFILL <= 0:
        return True
    now = time.time()
    with _get_buckets_lock:
        tokens, last = _get_buckets.get(ip, (GET_RATE_BURST, now))
        tokens = min(GET_RATE_BURST, tokens + (now - last) * GET_RATE_REFILL)
        if tokens < 1.0:
            _get_buckets[ip] = (tokens, now)
            return False
        _get_buckets[ip] = (tokens - 1.0, now)
        return True


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
.lobby .meta .players{color:#7fff7f;font-weight:600;}
.lobby .meta .players.full{color:#ff9f7f;}
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
    // Live occupancy (server-computed in _enrich_lobbies): players = the router's
    // per-code flow count, capacity = the lobby .conf. Numbers only — not
    // injectable, so no escaping needed. Flag "full" when at capacity so players
    // don't bounce off a full lobby.
    const players = Math.max(0, Number(l.players) || 0);
    const capNum = Number(l.capacity);
    const cap = (capNum > 0) ? capNum : null;
    const full = (cap !== null && players >= cap);
    const occ = `<span class="${full ? "players full" : "players"}">${players}/${cap !== null ? cap : "?"} players${full ? " · full" : ""}</span>`;
    return `<div class="lobby">
      <h2>${code}</h2>
      <div class="meta">${occ}<span>port ${port}</span><span>pid ${pid}</span>${startedShort?`<span>since ${startedShort}</span>`:""}</div>
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
        if not _allow_get(self._client_ip()):
            self._send(429, "text/plain", b"rate limited; slow down\n")
            return
        if self.path in ("", "/", "/index.html"):
            self._send(200, "text/html; charset=utf-8", HTML_VIEW.encode())
            return
        if self.path in ("/lobbies", "/lobbies/"):
            body = json.dumps(
                {
                    "generatedAt": datetime.now(timezone.utc).isoformat(),
                    "registry": REGISTRY_DIR,
                    "lobbies": _enrich_lobbies(_merge_static(_load_lobbies_cached())),
                },
                indent=2,
            ).encode()
            self._send(200, "application/json", body)
            return
        if self.path in ("/healthz", "/healthz/"):
            # Liveness probe for monitoring (Prometheus, Uptime Robot, etc.).
            # `lobbiesAlive` = registry pid exists; `lobbiesResponsive` = the
            # UDP game port actually answered a Ping. status="degraded" when a
            # lobby is alive-but-deaf (the 2026-06-10 wedge) so a monitor sees
            # it even though the process is technically up.
            lobbies = _merge_static(_load_lobbies_cached())
            alive_count = sum(1 for l in lobbies if l.get("alive"))
            responsive = 0
            for l in lobbies:
                if not l.get("alive"):
                    continue
                try:
                    if _udp_responsive(int(l.get("port", "0"))):
                        responsive += 1
                except (ValueError, TypeError):
                    pass
            status = "ok" if (alive_count == 0 or responsive > 0) else "degraded"
            code = 200 if status == "ok" else 503
            body = json.dumps({"status": status, "lobbiesAlive": alive_count,
                               "lobbiesResponsive": responsive}).encode()
            self._send(code, "application/json", body)
            return
        if self.path == "/favicon.ico":
            # Browsers auto-request this; answer 204 (No Content) so it doesn't
            # surface as a console 404 on the lobby-browser page.
            self._send(204, "image/x-icon", b"")
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

    def _read_json(self, cap: int = 4096) -> dict:
        """Read an optional JSON request body, bounded to `cap` bytes."""
        try:
            length = min(int(self.headers.get("Content-Length", "0") or "0"), cap)
            if length <= 0:
                return {}
            raw = self.rfile.read(length) or b"{}"
            obj = json.loads(raw)
            return obj if isinstance(obj, dict) else {}
        except (ValueError, json.JSONDecodeError, OSError):
            return {}

    def _handle_create(self) -> None:
        if not self._authed():
            self._send_json(403, {"error": "forbidden (bad or missing token; creation may be disabled)"})
            return
        # Optional create options from the in-game CREATE / Host Match button.
        # All are clamped + defaulted, so a missing/garbage body is harmless.
        opts = self._read_json(2048)
        max_players = 4
        mode = 0
        public = True
        try:
            max_players = max(2, min(8, int(opts.get("maxPlayers", 4))))
        except (ValueError, TypeError):
            pass
        try:
            mode = max(0, min(2, int(opts.get("mode", 0))))
        except (ValueError, TypeError):
            pass
        public = bool(opts.get("public", True))

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
            prev_last = _last_create.get(ip)  # captured for rollback if spawn fails
            _last_create[ip] = now  # reserve the slot before the slow spawn

        code, port, err = create_lobby(max_players=max_players, public=public, mode=mode)
        if err:
            # The spawn failed → release the reserved rate-limit slot so the user
            # isn't locked out for CREATE_MIN_INTERVAL over a failed create.
            with _create_lock:
                if _last_create.get(ip) == now:  # don't clobber a concurrent create
                    if prev_last is None:
                        _last_create.pop(ip, None)
                    else:
                        _last_create[ip] = prev_last
            self._send_json(500, {"error": err})
            return
        print(f"[control] created lobby {code} on port {port} "
              f"(by {ip}; max={max_players} public={public} mode={mode})")
        self._send_json(200, {"code": code, "port": port, "capacity": max_players, "public": public, "mode": mode})

    def _handle_stop(self) -> None:
        if not self._authed():
            self._send_json(403, {"error": "forbidden"})
            return
        # _read_json caps the body, rejects a negative Content-Length, and swallows
        # a mid-read OSError (a hand-rolled read here previously did none of those).
        body = self._read_json()
        code = str(body.get("code", "")).strip().upper()
        if not LOBBY_CODE_RE.match(code):
            self._send_json(400, {"error": "missing or invalid code"})
            return
        # Never stop a static (systemd-managed) lobby like MAIN: stop-lobby.sh would
        # kill the always-on oracle AND rm -rf its live wineprefix. The reaper and
        # stop-all-lobbies.sh already refuse static lobbies; the HTTP control path
        # must too, so a create-token holder can't tear down the front-door oracle.
        if code in {s["code"] for s in _static_lobbies()}:
            self._send_json(409, {"error": "refusing to stop a static (systemd-managed) lobby", "code": code})
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


def create_lobby(max_players: int = 4, public: bool = True, mode: int = 0) -> tuple[str, int, str | None]:
    """Spawn a backend lobby via launch-lobby.sh. Returns (code, port, err).

    Create options are passed to launch-lobby.sh as environment variables
    (SF_LOBBY_MAX_PLAYERS / SF_LOBBY_PUBLIC / SF_LOBBY_MODE). The script may use
    or ignore them; either way we also record them in the lobby's .conf so the
    browser list can show capacity/visibility.
    """
    code = _gen_code()
    script = os.path.join(REPO_DIR, "launch-lobby.sh")
    env = dict(os.environ)
    env["SF_LOBBY_MAX_PLAYERS"] = str(max_players)
    env["SF_LOBBY_PUBLIC"] = "1" if public else "0"
    env["SF_LOBBY_MODE"] = str(mode)
    try:
        proc = subprocess.run(
            ["bash", script, code],
            cwd=REPO_DIR, capture_output=True, text=True, timeout=LAUNCH_TIMEOUT, env=env,
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
    # Record create options in the .conf so GET /lobbies reflects them. Append-
    # only and best-effort: failure here doesn't fail the create.
    try:
        with open(conf, "a") as f:
            f.write(f"capacity={max_players}\n")
            f.write(f"public={'true' if public else 'false'}\n")
            f.write(f"mode={mode}\n")
    except OSError:
        pass
    return code, port, None


# --- UDP liveness probe (for /healthz reporting only) -------------------------
# `alive` (pid exists) is NOT enough: on 2026-06-10 the oracle process stayed
# alive but its game loop was dead and the UDP port unanswering ("wedged"), and
# /healthz still reported it up. This probe sends a v25 Ping (msgType 0) and
# waits briefly for any reply — the same check healthcheck.py / the systemd
# watchdog use. Results are cached per port so browser/monitor polling can't
# fan out into a probe storm. Kept SEPARATE from `alive` on purpose: the reaper
# tears down any non-`alive` lobby, and a momentary non-response (e.g. mid map
# load) must not trigger a teardown — only a restart by the watchdog.
_probe_cache: dict = {}
_probe_lock = threading.Lock()

def _udp_responsive(port: int, host: str = "127.0.0.1", ttl: float = 5.0,
                    timeout: float = 0.4) -> bool:
    now = time.time()
    with _probe_lock:
        c = _probe_cache.get(port)
        if c and now - c[0] < ttl:
            return c[1]
    ok = False
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        sock.settimeout(timeout)
        ts = int(now) & 0xFFFFFFFF
        ping = struct.pack("<I", ts) + bytes([0]) + struct.pack("<Q", 0) + bytes([0])
        sock.sendto(ping, (host, port))
        data, _ = sock.recvfrom(2048)
        ok = len(data) >= 14
    except OSError:
        ok = False
    finally:
        sock.close()
    with _probe_lock:
        _probe_cache[port] = (now, ok)
    return ok


def _static_lobbies() -> list[dict]:
    """Parse SF_STATIC_LOBBIES into always-on lobby entries."""
    out: list[dict] = []
    for tok in STATIC_LOBBIES.split(","):
        tok = tok.strip()
        if not tok:
            continue
        code, _, port = tok.partition(":")
        code = code.strip().upper()
        if not LOBBY_CODE_RE.match(code):
            continue
        try:
            port_i = int(port) if port.strip() else 1337
        except ValueError:
            port_i = 1337
        out.append({"code": code, "port": str(port_i), "pid": "static",
                    "started": "", "alive": True, "static": "true"})
    return out


def _merge_static(lobbies: list[dict]) -> list[dict]:
    """Append configured static lobbies that aren't already in the registry."""
    seen = {l.get("code", "") for l in lobbies}
    for s in _static_lobbies():
        if s["code"] not in seen:
            lobbies.append(s)
            seen.add(s["code"])
    return lobbies


# Short-TTL cache for the router's per-code client counts so a fleet of browser
# clients polling GET /lobbies at ~1Hz doesn't fan out one router hit each.
_bycode_cache: dict = {"at": 0.0, "data": None}
_bycode_cache_lock = threading.Lock()


def _router_bycode_cached(ttl: float = 1.0) -> dict | None:
    now = time.time()
    # (F3) Guard the cache dict — every GET /lobbies handler runs on its own
    # thread (ThreadingHTTPServer), so concurrent pollers could interleave the
    # at/data writes. Fetch OUTSIDE the lock (don't hold it across router I/O);
    # a rare double-fetch under contention is harmless given the 1s TTL.
    with _bycode_cache_lock:
        if now - _bycode_cache["at"] < ttl:
            return _bycode_cache["data"]
    data = _router_bycode()
    with _bycode_cache_lock:
        _bycode_cache["at"] = now
        _bycode_cache["data"] = data
    return data


def _enrich_lobbies(lobbies: list[dict]) -> list[dict]:
    """Keep only alive lobbies and decorate each with a live player count (from
    the router) and a normalized integer capacity, for the server browser."""
    bycode = _router_bycode_cached()
    out: list[dict] = []
    for l in lobbies:
        if not l.get("alive"):
            continue
        try:
            cap = int(l.get("capacity", "4"))
        except (ValueError, TypeError):
            cap = 4
        l["capacity"] = cap
        players = 0
        if isinstance(bycode, dict):
            try:
                players = int(bycode.get(l.get("code", ""), 0))
            except (ValueError, TypeError):
                players = 0
        l["players"] = players
        out.append(l)
    return out


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
            static_codes = {s["code"] for s in _static_lobbies()}
            for l in lobbies:
                code = l.get("code", "")
                if not code:
                    continue
                seen_codes.add(code)
                # Never reap a static (systemd-managed) lobby — e.g. the always-on
                # MAIN oracle. stop-lobby.sh would kill the systemd process AND
                # rm -rf its live wineprefix; and MAIN's registry pid is the xvfb
                # wrapper (stays alive), so its dead-pid branch is unreliable too.
                # systemd owns these — the control plane must keep its hands off.
                if code in static_codes or str(l.get("static", "")).lower() in ("1", "true", "yes"):
                    _empty_since.pop(code, None)
                    continue
                if not l.get("alive"):
                    # Grace window (issue #5): a freshly-launched lobby may not have
                    # a reapable pid yet — Proton/Wine startup writes the registry
                    # entry before the wrapper pid is observable as alive. Reaping
                    # here would kill the lobby before anyone can join. Skip until it
                    # clears LOBBY_MIN_AGE, same as the empty-lobby branch below.
                    if _lobby_age(l) < LOBBY_MIN_AGE:
                        continue
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
            # prune idle per-IP rate-limit state so the maps stay bounded
            with _get_buckets_lock:
                for bip in [bip for bip, (_, last) in _get_buckets.items() if now - last > 300]:
                    _get_buckets.pop(bip, None)
            with _create_lock:
                for cip in [cip for cip, t in list(_last_create.items()) if now - t > 300]:
                    _last_create.pop(cip, None)
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
