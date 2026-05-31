# Single-port router + in-game lobbies — live test runbook

Branch `sharding`. Server side (Go router + Python control plane) is unit-tested;
the client side compiles but needs this live 2-player test. Nothing here is
deployed to the live oracle yet — this is the runbook to do that + verify.

## Build artifacts (already built to dist/)
- `sf-router/` → `go build ./cmd/sf-router` (Go 1.21+)
- `dist/SFClientRecon.dll`  (has SELECT + RequestJoinLobby)
- `dist/SFServerBrowser.dll` (has JOIN / JOIN-CODE / CREATE)

## A. Server setup on .115 (oracle box)
The router fronts port 1337; backends move to OTHER ports. **This changes the
oracle's port layout**, so back up / be ready to revert (see §E).

1. Build + copy the router binary to .115 (or `git pull` + `go build` there):
   `scp $(go build -o /tmp/sf-router ./sf-router/cmd/sf-router && echo /tmp/sf-router) miles@192.168.1.115:/tmp/sf-router`
2. Stop the current single oracle on 1337 (it must free 1337 for the router):
   `sudo systemctl stop sf-oracle`   (or note its port and run the router on a different public port for a first test, e.g. 1346)
3. Start two backend lobbies on non-1337 ports (launch-lobby.sh picks free ports, skipping reserved):
   `SF_LOBBIES_DIR=/tmp/sf-lobbies ./launch-lobby.sh MAIN`
   `SF_LOBBIES_DIR=/tmp/sf-lobbies ./launch-lobby.sh DUO`
   Confirm: `./list-lobbies.sh` shows MAIN + DUO UP on their ports (NOT 1337).
4. Start the lobby HTTP + control plane (token enables CREATE):
   `SF_CONTROL_TOKEN=shardtest SF_MAX_LOBBIES=6 python3 serve-lobbies.py --port 8080 &`
   Confirm: `curl -s localhost:8080/lobbies` lists MAIN + DUO.
5. Start the router on 1337, routing by the registry, with stats for the reaper:
   `./launch-router.sh`   (listens 0.0.0.0:1337, registry /tmp/sf-lobbies, stats 127.0.0.1:8081)
   Confirm: `ss -ulnp | grep :1337` shows sf-router; `curl -s localhost:8081/router/stats` → `{"flows":0,...}`.
6. Firewall: ensure UDP/1337 + TCP/8080 are reachable from the LAN; backend
   ports can stay loopback once everything works (§ single-port proof).

## B. Client setup on this laptop (two Goldberg mirrors)
1. Deploy the new client DLLs to BOTH mirrors + the Steam install plugins dir:
   ```
   for d in ~/.local/share/Steam/steamapps/common/StickFightTheGame/BepInEx/plugins \
            ~/sf-mirror-local/BepInEx/plugins ~/sf-mirror-local-p2/BepInEx/plugins; do
     cp dist/SFClientRecon.dll "$d/"; cp dist/SFServerBrowser.dll "$d/"
   done
   ```
   (SFServerBrowser.dll must be present in each plugins dir — it's a new plugin.)
2. Point clients at the ROUTER and the lobby endpoint. Launch each instance with:
   `-address 192.168.1.115 -port 1337` (router) and env
   `SF_LOBBY_ENDPOINT=http://192.168.1.115:8080/lobbies` and (for CREATE)
   `SF_CONTROL_TOKEN=shardtest`. The launch-sf-player.sh already passes
   `-address 192.168.1.115 -port 1337`; export the two env vars before it:
   ```
   SF_LOBBY_ENDPOINT=http://192.168.1.115:8080/lobbies SF_CONTROL_TOKEN=shardtest \
     bash launch-sf-player.sh
   SF_LOBBY_ENDPOINT=http://192.168.1.115:8080/lobbies SF_CONTROL_TOKEN=shardtest \
     bash /tmp/launch-sf-p2.sh
   ```
   (p2 uses SFCLIENTRECON_PORT=1341 so the two v26 sockets don't collide.)

## C. The test (pass criteria)

IMPORTANT — your two local mirrors share ONE IP (your LAN address). The router
binds the game socket by IP (the documented limit), so **two same-IP clients in
DIFFERENT lobbies will likely mis-route the second one's game traffic.** So the
*local* 2-mirror test validates the SAME-lobby relay + all the UI; true
different-lobby ISOLATION must be tested from **two different machines/IPs**.

Local 2-mirror test (primary):
1. **List**: each client → SERVERS → REFRESH → sees MAIN + DUO.
2. **Relay through the router (same lobby)**: BOTH clients → JOIN **MAIN**. They
   should see each other and boxes should sync — i.e. everything that works
   directly on the oracle still works *through the router*. On .115:
   - router log: `SELECT "MAIN" from <A>` + `<B>`; `new flow` lines; `curl localhost:8081/router/stats` → `byCode` MAIN ≈ 4 flows (2 clients × game+recon).
3. **Join-by-code + switch**: A → leave to menu → SERVERS → type `DUO` → JOIN CODE
   → A enters DUO (now alone there). Confirms code entry + clean switch + re-join.
4. **CREATE**: B → leave to menu → SERVERS → CREATE LOBBY → new code appears, B
   auto-joins it; `curl localhost:8080/lobbies` lists it.
5. **Switch repeatedly**: A: MAIN → menu → DUO → menu → created — ≥3×, each lands
   correctly, no stuck "connecting"/frozen player.
6. **No Mono errors**: `grep -iE "TypeLoad|MissingMethod|Exception" <each client>/BepInEx/LogOutput.log` clean; client logs show `[SELECT] lobby=… → …:1337` and snapshots flowing.

Two-machine test (validates isolation — do when a second box is available):
7. **Isolation**: machine 1 → JOIN MAIN, machine 2 → JOIN DUO. They must NOT see
   each other; `byCode` shows MAIN and DUO each with that one client's flows.
8. **Single-port proof**: backends bound to 127.0.0.1 (or `ufw deny` their ports)
   — clients still play purely through UDP/1337.

## D. Likely failure points (and where to look)
- Client stuck after JOIN, no snapshots: router log says `SELECT … no such lobby`
  (code mismatch) OR the game socket never got an ipBind (SELECT lost — should
  resend at 5Hz; check `[SELECT]` lines repeating). Verify the backend for that
  code is UP in `list-lobbies.sh`.
- Switch leaves player frozen: the re-run of `BeginOracleLobbyConnect` from the
  menu is the riskiest path (never exercised repeatedly before). Watch for the
  client re-entering the lobby scene cleanly; if it loops on "Notice me senpai",
  that's the handshake not re-establishing — capture the client log.
- CREATE returns 403: `SF_CONTROL_TOKEN` mismatch between client env and
  serve-lobbies.py. 429: rate limit / cap.
- Two same-IP players (both your mirrors are 127.0.0.1/your LAN IP!) in
  DIFFERENT lobbies: KNOWN LIMITATION — the second player's *game* socket may
  ride the shared IP binding to the wrong lobby. For this test, the two mirrors
  share one IP, so step 2 (A→MAIN, B→DUO) MAY exhibit the game-socket misroute.
  If it does, that's the documented NAT limit, not a regression — note it and
  test isolation from two different machines if possible.

## E. Revert (back to the known-good single oracle)
- Stop router + serve-lobbies + extra lobbies: `./stop-all-lobbies.sh`; kill the
  router and python; `sudo systemctl start sf-oracle` (restores the single
  oracle on 1337 with ALKA's working-box build, md5 d0955185).
- Client DLLs: restore from `~/sf-backups/2026-05-31_091238_pre-sharding/server-build/`
  (host) or rebuild ALKA's main client; the new client DLLs are backward-
  compatible with a direct oracle (SELECT is ignored), so a revert of just the
  server (stop router, run oracle on 1337) is enough to return to normal.

## Status checklist
- [x] Go router (relay, SELECT routing, registry, reaper) — 16 unit tests (-race), reviewed + hardened (3 findings fixed)
- [x] serve-lobbies.py control plane (create/stop/reaper) — smoke-tested (auth/GET); token'd create needs live
- [x] Client SELECT + RequestJoinLobby — compiles; C# reviewed (critical Mono2Polyfills fix applied to server-browser; SELECT-ACK feedback added)
- [x] Browser JOIN/JOIN-CODE/CREATE — compiles; Mono2Polyfills.cs added (was missing → would have TypeLoad-failed the whole plugin)
- [ ] Live 2-client test (this doc) — NOT YET RUN (needs the game; user-driven)
- [ ] Installer: bake router endpoint + SF_LOBBY_ENDPOINT + SF_CONTROL_TOKEN (ops step, do when deploying publicly)
- [ ] (deferred) patched-DLL game-socket SELECT → removes the same-IP NAT limit
