# Multi-lobby — LIVE on .115 (2026-05-31)

Single-port router + in-game lobbies is **deployed, running, and verified** on the
`.115` server. This is the result + operations doc for the `sharding` branch work.

## What's running (all systemd — survives reboot / my session ending)

| Service | Port | Role |
|---|---|---|
| `sf-oracle` | UDP 1337 | The **MAIN** lobby (headless SF). Always-on default. `Restart=on-failure`. |
| `sf-router` | UDP **1338** (public) | Single front door. Routes each client to its lobby's backend by the SELECT'd code, per `/tmp/sf-lobbies`. Stats on `127.0.0.1:8081`. |
| `sf-lobbies` | TCP 8080 (public) | Control plane: `GET /lobbies` (list), `POST /lobbies` (create, token), `POST /lobbies/stop`, reaper. |

Extra lobbies (e.g. **DUO** on 1342) are spawned by `launch-lobby.sh` /
`POST /lobbies` and live on **loopback** ports — only the router (1338) and
control plane (8080) are exposed at the firewall (which already allows UDP
1337-1340 + TCP 8080).

Clients connect to **one** UDP port (`.115:1338`); the recon socket sends a
SELECT naming the lobby code; the router pins that client (by endpoint + by IP
for the game socket) to the backend and relays both directions. Backends never
know the router exists.

### Topology
```
laptop / .148 ──(UDP 1338)──► sf-router ──┬─ MAIN  → 127.0.0.1:1337  (sf-oracle, systemd)
   game sock + recon sock      (by code)  ├─ DUO   → 127.0.0.1:1342  (launch-lobby.sh)
   SERVERS menu ──(HTTP 8080)──► sf-lobbies└─ <created lobbies on loopback…>
```

## Verified live (2026-05-31)

Synthetic UDP clients (`tools/sf-synthetic-client.py`) from **two real IPs**:

- **Routing**: laptop (.47) `SELECT MAIN` → ACK **OK**; `.148` `SELECT DUO` → ACK **OK**.
- **Isolation across 2 IPs** (the thing two same-IP mirrors *can't* prove): with
  `.148` bursting to DUO, the DUO backend's rx jumped **0 → 119/s** while the
  MAIN backend stayed flat at its baseline — `byCode {"DUO":2,"MAIN":2}`, **zero
  crossover**. Each backend received only its own lobby's traffic.
- **Bad code rejected**: `SELECT NOPE` → ACK **NO_SUCH_CODE** (this is the
  "lobby not found" path the in-game browser shows).
- **Create**: token'd `POST /lobbies` → `{"code":"…","port":…}`, booted +
  auto-registered + routable in ~25s. No-token `POST` → **403**.
- **Relay transparency** (Stage 0, earlier): a real graphical client played
  through the router to the oracle with normal movement/boxes.

> NOTE: a true *two-real-game-clients-in-two-lobbies* test still needs a human to
> click through each client's SERVERS menu — that's the one step below for you.
> Everything the router/control-plane does under it is proven.

## Capacity — how many lobbies at once?  (asked: "how many lobbys can be had")

Host = **i7-7700 (4 physical / 8 logical cores), ~15.5 GB RAM.** Measured by
spinning up to 8 concurrent headless lobbies:

- **RAM**: ~**485 MB per lobby** (StickFight.exe ~420 MB + wine helpers). At 8
  lobbies, **10.8 GB still free.** RAM supports ~**25-30** — *not* the limit.
- **CPU is the limit.** Each **idle** headless lobby burns ~**0.55-0.6 of a
  logical core** (Unity's loop runs uncapped even with nobody in it). 8 idle
  lobbies summed to ~4.5 cores.

**Practical answer:**
- **~6 concurrent ACTIVE matches** comfortably (matches with players + physics
  cost more CPU than idle; this leaves headroom on the 4C/8T box). The control
  plane cap `SF_MAX_LOBBIES` is set to **6** for this reason.
- **~8-10 idle/waiting lobbies** before CPU saturates.
- Biggest lever to ~**double** it: cap the headless framerate
  (`Application.targetFrameRate`) in `SFHeadlessHost` so idle lobbies stop
  spinning a half-core for nothing. (Follow-up, not done.)

## Using .148 as a second test machine

`.148` (ubuntu4070ti) is a **different IP**, so it's how you test true
different-lobby isolation that the two same-IP laptop mirrors can't. It is set up
two ways:

1. **Real graphical client** (for actually playing a 2-machine match):
   - Installed: `~/sf-mirror-local` (Goldberg SF + net35 plugins) + Proton-
     Experimental + `~/play-sf-148.sh` + token. Plugins verified to **load
     cleanly under .148's Mono** (no TypeLoad/MissingMethod).
   - **Launch (on .148's own desktop):** `~/play-sf-148.sh`  → boots SF pointed at
     the router, default lobby **DUO**. Pick MAIN/DUO/any code in-game via SERVERS.
   - (Graphical boot needs .148's real display/GPU, so it must be launched from
     the machine's desktop, not a headless SSH.)
2. **Synthetic UDP client** (for data-plane/isolation tests without a GUI):
   `python3 ~/sf-tools/sf-synthetic-client.py --router 192.168.1.115:1338 --code DUO --count 300 --game-socket`

### The full 2-machine real-game test (for you, when back)
1. **Laptop**: `SF_HOST=192.168.1.115 SF_PORT=1338 SF_LOBBY_ENDPOINT=http://192.168.1.115:8080/lobbies bash launch-sf-player.sh` → SERVERS → **JOIN MAIN**.
2. **.148** (its desktop): `~/play-sf-148.sh` → SERVERS → **JOIN DUO**.
3. Expect: you do **not** see each other (different lobbies). On `.115`,
   `curl localhost:8081/router/stats` shows `byCode` MAIN and DUO each with that
   one client's flows. Then have both JOIN **MAIN** → you see each other + boxes sync.

## Operations

```bash
# status
systemctl status sf-router sf-lobbies sf-oracle
curl -s localhost:8081/router/stats        # flows by lobby code
curl -s localhost:8080/lobbies             # lobby list
ls /tmp/sf-lobbies/                         # registry

# lobbies
cd ~/sf-multiplayer
SF_LOBBIES_DIR=/tmp/sf-lobbies ./launch-lobby.sh CODE [port]   # add one
SF_LOBBIES_DIR=/tmp/sf-lobbies ./stop-lobby.sh CODE            # stop one
# create via API (token in ~/sf-oracle/sf-control.env):
curl -X POST -H "X-SF-Token: $TOK" localhost:8080/lobbies
```

- **MAIN** auto-recovers on oracle restart/reboot (drop-in
  `deploy/sf-oracle.service.d/lobby-register.conf` re-writes `MAIN.conf` via
  `register-main-lobby.sh`).
- **DUO and created lobbies are ephemeral** (launch-lobby.sh, no auto-restart);
  recreate with the CREATE button in-game or `launch-lobby.sh`. `/tmp/sf-lobbies`
  is cleared on reboot, so only MAIN returns automatically after a reboot.
- The empty-lobby reaper TTL is set to **24h** (`SF_LOBBY_EMPTY_TTL=86400` in
  `sf-lobbies.service`) so idle lobbies survive a player-less window; dead-pid
  cleanup still runs every pass. Lower it for a busy public server.

## Revert to the plain single oracle
```bash
sudo systemctl disable --now sf-router sf-lobbies
SF_LOBBIES_DIR=/tmp/sf-lobbies ~/sf-multiplayer/stop-lobby.sh DUO   # + any others
# sf-oracle keeps serving MAIN on 1337 directly; clients use -port 1337.
```

## Known limitations
- **Two players behind ONE public IP in DIFFERENT lobbies**: the patched-DLL game
  socket can't SELECT, so the router binds it per-IP — a second different-code
  client on the same IP may mis-route its game socket. Distinct IPs (different
  houses / the .148 box) and same-lobby are fine. Fix = a game-socket SELECT in
  the patched `Assembly-CSharp` (deferred).
- `1-click-install` should bake the router endpoint + `SF_LOBBY_ENDPOINT` +
  token for public deploy (not done — ops step).
