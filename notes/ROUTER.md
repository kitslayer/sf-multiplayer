# sf-router — single-port multi-lobby front-door

**Branch:** `sharding`. **Status:** server side (Stage 0 + 1) implemented +
unit-tested; client side (Stages 2/3/5) and the lifecycle control plane
(Stage 4) in progress. Full plan: `~/.claude/plans/iterative-sparking-pascal.md`.

## Why

Each lobby is its own headless `SF.exe` process on its own UDP port (v1
multi-process — avoids SF's one-match-per-process singleton limits; "true"
in-process sharding is deferred/maybe-never). Without a front-door, clients
and firewalls must deal with one port per lobby and there's no in-game way to
pick a lobby. The router gives **one public UDP port** for all lobbies and lets
the client choose a lobby by code, in-game. It is also the seam where a future
in-process shard could replace a process-backend without touching the client or
firewall.

## How it works

```
client ──UDP──► sf-router :1337 ──┬─► SF.exe lobby AAAA  127.0.0.1:1338
 (game sock +     (routes by      ├─► SF.exe lobby BBBB  127.0.0.1:1340
  recon sock)      lobby code)    └─► …backends loopback-only
```

- Both the patched-DLL **game socket** (raw v25, msgTypes 0–38) and the
  **SFClientRecon socket** (v26: 39/40/41/42) send plain UDP to the router.
  Confirmed raw-UDP on both sides (`SFHeadlessHost.StartHost`), so the router
  is a stateless per-client-endpoint relay — no connection state to preserve.
- The router relays each client datagram to its backend and relays replies back
  out the public socket, so the client only ever talks to `:1337`.
- **SELECT**: the recon socket sends a SELECT control datagram naming its lobby
  code (router-only framing — see PROTOCOL.md). The router resolves the code to
  a backend via the lobby registry and pins the client.
- **Binding**: per source-endpoint first, then a per-IP fallback so the game
  socket (same client IP, never SELECTs) rides the recon socket's selection.
- **Switch**: a SELECT with a new code tears down that IP's flows so they
  rebuild to the new backend (used by leave-to-menu lobby switching).
- **Reaping**: flows idle > 45s are closed (above the backend's 30s stale
  sweep). LEAVE frees a binding immediately.

## Modes / running it

```
# Routing mode (normal): route by registry, expose stats for the reaper
./launch-router.sh                      # 0.0.0.0:1337, /tmp/sf-lobbies, stats 127.0.0.1:8081

# Equivalent explicit:
sf-router -listen 0.0.0.0:1337 -registry /tmp/sf-lobbies -stats 127.0.0.1:8081

# Stage-0 transparent debug: relay everything to one backend, no SELECT
sf-router -listen 0.0.0.0:1337 -backend 127.0.0.1:1338
```

`GET http://127.0.0.1:8081/router/stats` → `{"flows":N,"byCode":{"AAAA":1,...}}`
(the serve-lobbies.py reaper uses byCode to find empty lobbies).

## Code

- `sf-router/router.go` — the relay + flow table + bindings + reaper.
- `sf-router/select.go` — SELECT/LEAVE/ACK wire framing.
- `sf-router/registry.go` — reads `/tmp/sf-lobbies/*.conf` (code→127.0.0.1:port,
  drops dead pids), 2s cache.
- `sf-router/cmd/sf-router/main.go` — flags, stats HTTP, signals.
- Tests: `*_test.go` — relay round-trip + return-path addressing, per-client
  flows, SELECT routing to the right backend, drop-unselected, switch rebind,
  unknown-code ACK, registry parse/reload/liveness, stats-by-code. `go test
  ./... -race` green.

## Firewall posture

Open only **UDP/1337** (router) + **TCP/8080** (serve-lobbies HTTP). All backend
SF.exe ports stay loopback — hidden from scanners.

## Known limitation (documented risk)

Backend selection falls back to per-IP for the game socket, so **two players
behind one public IP/NAT** could mis-bind if they pick different lobbies. Fine
for the comp scene (distinct IPs); the robust fix (make the game socket SELECT
too, via a patched-DLL change) is deferred. Per-endpoint SELECT bindings take
priority over the IP fallback, so each recon socket is always correctly bound.
