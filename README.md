# sf-multiplayer

Centralized dedicated-server revival for **Stick Fight: The Game** (Steam app 674940), built for the competitive scene to replace stock P2P with an authoritative server. Goal: fix rubber-banding, host-migration drops, and the long-standing physics divergence between clients.

> **Status: alpha, active development.** Real Stick Fight (Windows or Linux/Proton) connects directly to `SFHeadlessHost.dll` running inside a headless instance of the game on UDP 1337. The host-side gameplay loop — physics, killboxes, weapon spawn timers, pickup/throw/drop, destruction, scene transitions — runs through SF's own code, with the headless host plugin driving it via Harmony patches. We extended SF's stock wire protocol (v25) with a v26 channel for server-authoritative state snapshots and client inputs.
>
> **What works today:** end-to-end connection + handshake + spawn + match flow, weapon pickup/throw/drop, death + round advance + 123-map rotation, ice/crate/chain destruction broadcast to all clients including the breaker (fixes "ice doesn't break on my screen"), server-side NSO position snapshots so boxes and moving platforms occupy the same place on every client, server-side projectile registration on fire, server-side damage validation (range + magnitude), multi-lobby on one host (multi-process), in-game chat commands (`/code`, `/start`, `/players`, etc.), client-side snapshot smoothing + divergence-snap, anticheat rate observer.
>
> **End-state goal:** full client-side prediction + server reconciliation. Client predicts movement locally for input responsiveness, sends inputs to the server, server runs the authoritative simulation, broadcasts snapshots, client snap-corrects when its prediction diverged. We have the wire protocol shipped (v26.3) and the foundations of all the pieces — what's still rough is the actual replay-rollback loop and hit-registration on server-side projectiles. See [`NEXT_STEPS.md`](NEXT_STEPS.md) for the roadmap and [`WHATS_NEW.md`](WHATS_NEW.md) for the running session log.

## What's in this repo

| Path | What it is |
|---|---|
| [`sf-headless-host/`](sf-headless-host/) | **Live entry point (server-side).** BepInEx + Harmony plugin that turns headless Stick Fight into a v25-speaking server. Implements the raw-UDP protocol, drives SF's own host-side gameplay via Harmony patches, forwards intercepted broadcasts to clients, and from Phase 6.10 onward broadcasts authoritative state snapshots on a v26 channel. |
| [`sf-client-recon/`](sf-client-recon/) | **Client-side companion plugin (Phase 6.11+).** BepInEx plugin that listens on UDP 1339 for v26 `WorldStateSnapshot` packets and snap-corrects the local player's position to the server's authoritative view. Foundation for client-prediction + server-reconciliation. Deployed to `<SF install>/BepInEx/plugins/SFClientRecon.dll` on each player's machine. |
| [`launch-sf-headless.sh`](launch-sf-headless.sh) | Launches Stick Fight under Proton in `-batchmode -nographics` with the headless-host plugin loaded. Honors `SFHEADLESS_PORT` / `SFHEADLESS_BRIDGEPORT` env vars for one-instance use. |
| [`launch-lobby.sh`](launch-lobby.sh) | **Multi-lobby (Phase 6.13 v1).** Spawn one new oracle on the next free UDP port. `launch-lobby.sh [CODE] [PORT]`. Each lobby is fully isolated (separate wineprefix, log, process). |
| [`stop-lobby.sh`](stop-lobby.sh) / [`stop-all-lobbies.sh`](stop-all-lobbies.sh) / [`list-lobbies.sh`](list-lobbies.sh) | Lobby lifecycle management. Registry at `/tmp/sf-lobbies/`. |
| [`setup-all.sh`](setup-all.sh) | One-command build + deploy of both plugins (server + client). |
| [`serve-lobbies.py`](serve-lobbies.py) | HTTP lobby-browser endpoint. `GET /lobbies` returns running lobbies as JSON for server browsers. |
| [`launch-sf-player.sh`](launch-sf-player.sh) | Launches Stick Fight in graphical mode (player-side) against `127.0.0.1:1337`. For local end-to-end testing. |
| [`launch-sf-bepinex.sh`](launch-sf-bepinex.sh) | Wrapper that launches SF under Proton with `WINEDLLOVERRIDES="winhttp=n,b"` so BepInEx actually loads (Steam's vanilla "Play" doesn't set this env var). |
| [`maps/`](maps/) | Dumped map data — 123 Landfall scenes as JSON, used by the server for authoritative weapon/player spawn positions. |
| [`refs/`](refs/) | Decompiled `Assembly-CSharp` source (~358 .cs files) — not redistributed, generated from your own copy. See [`refs/README.md`](refs/README.md). |
| [`notes/`](notes/) | Living design + research docs. [`notes/SUMMARY.md`](notes/SUMMARY.md) is the latest top-level entry point; [`notes/phase6/`](notes/phase6/) tracks the current architecture work. |
| [`tools/`](tools/) | Python scripts. `dump-sf-maps.py` is an offline UnityPy-based map extractor that runs without launching SF. |
| [`sf-leveldumper/`](sf-leveldumper/) | One-shot BepInEx plugin that walks Landfall scenes and dumps geometry + spawn points + killboxes to JSON. Not loaded at runtime. |
| [`legacy/`](legacy/) | Earlier architectures kept for reference — Go dedicated server, v26 client plugin, local-control fix, lobby browser, launcher fork. None are in the active data path. See [`legacy/README.md`](legacy/README.md). |

## Architecture

```
SF clients (Windows or Linux/Proton, patched Assembly-CSharp.dll + SFClientRecon.dll)
   │  UDP v25 (msgTypes 0-38)  ── stock SF gameplay packets
   │       to :1337
   │  UDP v26 (msgType 40 PlayerInput, 41 ClientFireWeapon)
   │       to :1337
   ▼
Headless Stick Fight (Proton + Goldberg, BepInEx + SFHeadlessHost.dll)
   • Raw-UDP server on :1337 — speaks v25 + v26 on same socket
   • Drives SF's own MultiplayerManager / GameManager via Harmony patches
     — IsServer=true, IsNetworkMatch pinned true, SpawnRandomWeapon replaced
   • Per-client auth NetworkPlayer "ghost rig" mirrors client position so
     server-side physics (box pushing, NSO interaction) works
   • Tick history ring buffer (~2s @ 30Hz) for lag-comp damage validation
   • Per-client packet rate observer (anticheat)
   ▼  UDP v26 msgType 39 WorldStateSnapshot — 30Hz
   ▼  to each client's recorded v26 endpoint (default :1339)
SF clients
   • SFClientRecon parses snapshots: snaps + smoothes local player to
     server position, applies NSO positions, detects + hard-snaps on
     prediction divergence > 2.5u
```

Real Stick Fight runs in `-batchmode -nographics` on the server. It hosts the match using its own gameplay code; `SFHeadlessHost.dll` makes that code think it's a multiplayer host. Clients run their own SF normally + a small companion plugin (`SFClientRecon.dll`) that handles the v26 channel. See [`notes/PROTOCOL.md`](notes/PROTOCOL.md) for the wire-format spec, [`notes/phase6/`](notes/phase6/) for design docs.

## Quickstart

### Launch the server (headless Stick Fight)

```bash
SFHEADLESS_BRIDGEPORT=1341 SFHEADLESS_PORT=1337 SFHEADLESS_DEBUG=1 \
  bash launch-sf-headless.sh
```

This launches Stick Fight under Proton in `-batchmode -nographics`, loads `SFHeadlessHost.dll` via BepInEx, and binds the v25 raw-UDP server on `0.0.0.0:1337`. Logs go to `$SF_MIRROR/BepInEx/LogOutput.log` and `/tmp/sf-oracle-unity-1341.log`.

### Connect a real client

For a Steam install of Stick Fight, set these launch options:

```
WINEDLLOVERRIDES="winhttp=n,b" %command% -address 127.0.0.1 -port 1337
```

The `-address` / `-port` flags are read by a patched `Assembly-CSharp.dll`. For a Goldberg-shimmed local mirror, use [`launch-sf-player.sh`](launch-sf-player.sh).

### Build SFHeadlessHost

It's a .NET 4.6 BepInEx 5 assembly. **You need to provide the reference DLLs locally** — they're not in this repo for copyright reasons. In `sf-headless-host/refs/`:

- `Assembly-CSharp.dll` — from your Stick Fight install (`StickFight_Data/Managed/`)
- `UnityEngine.dll` — same directory
- `BepInEx.dll`, `0Harmony.dll` — from a BepInEx 5.4.x install

Then:

```bash
cd sf-headless-host
dotnet build -c Release
# Output → bin/Release/SFHeadlessHost.dll
```

Deploy to `<SF install>/BepInEx/plugins/SFHeadlessHost.dll`.

## Wire protocol

Two interleaved layers on the same 14-byte envelope: `[u32 timestamp LE][u8 msgType][N body][u64 steamID LE][u8 channel]`.

**v25 (stock SF):** `P2PPackageHandler.MsgType` 0..38 — handshake, player update, weapon pickup/throw/drop, object spawn/update/destruction, map change, etc. Implementation in [`sf-headless-host/SFHeadlessHost.cs`](sf-headless-host/SFHeadlessHost.cs) `Handle*` methods. The handshake (`ClientRequestingAccepting` → `ClientAccepted` → `ClientRequestingIndex` → `ClientInit` → `ClientRequestingToSpawn` → `ClientSpawned`) closely follows stock SF.

**Patched-DLL extensions:** msgTypes 56 (`LerpPlayer`) + 57 (`ColorChanged`). Emitted by kit's patched `Assembly-CSharp.dll` for remote-lerp triggers + player color sync. Blind-relayed to peers.

**v26 (this repo):** msgTypes 39+ for the prediction+reconciliation architecture.

| ID | Name | Direction | Purpose |
|----|------|-----------|---------|
| 39 | `WorldStateSnapshot` | server → all clients, 30Hz | Authoritative player + NSO + projectile positions, with `lastInputSeq` per player for reconciliation |
| 40 | `PlayerInput`         | client → server, 60Hz     | stick/aim/buttons + sequence number; server validates + clamps, feeds Movement.cs on the auth rig |
| 41 | `ClientFireWeapon`    | client → server, event    | Emitted on `Weapon.ActuallyShoot`; server registers a virtual projectile, simulates trajectory |

Full byte layouts + version history in [`notes/PROTOCOL.md`](notes/PROTOCOL.md).

## In-game admin

Chat commands (type into chat with `/` prefix, response comes back as a thought bubble from the server):

| Command | Effect |
|---------|--------|
| `/help`     | List available commands |
| `/code`     | Show the current lobby's code |
| `/players`  | Show client/spawn/rig counts |
| `/lobbies`  | List other running lobbies on this host |
| `/start`    | Force-start the current lobby's match |
| `/restart`, `/next` | Schedule a map advance |
| `/ping`     | Server replies `pong` |
| `/version`  | Show plugin version |

Server emits a welcome message ("Welcome to lobby {code}. Type /help for commands.") on first spawn.

## Ops + monitoring

| Tool | What it does |
|---|---|
| [`healthcheck.py`](healthcheck.py) | UDP Ping to an oracle; exits 0/1 for liveness probes. |
| [`serve-lobbies.py`](serve-lobbies.py) | HTTP `GET /lobbies` JSON + tiny HTML viewer at `/`. Reads `/tmp/sf-lobbies/`. |
| [`stress-test-anticheat.py`](stress-test-anticheat.py) | Fires fake `PlayerInput` packets at a configurable pps to verify anticheat thresholds fire. Dev tool — don't aim at prod. |
| BepInEx log heartbeat | Every 30s the oracle logs `clients=N spawned=M rx=X/s snap=Y/s input=Z/s rigs=K matchStarted=...` for instant ops visibility. |
| `SF_ANTICHEAT_ENFORCE=1` env var | Promotes anticheat observer to actually drop packets when thresholds exceeded. Off by default. |

VPS deployment guide: [`notes/VPS.md`](notes/VPS.md) — Proton + BepInEx + Goldberg + systemd template + firewall rules.

## Development status

Roadmap, current bugs, and design docs live in [`notes/`](notes/). Most useful entries:

- [`NEXT_STEPS.md`](NEXT_STEPS.md) — current state + roadmap toward client-prediction / server-reconciliation
- [`WHATS_NEW.md`](WHATS_NEW.md) — running session log (what shipped today)
- [`notes/PROTOCOL.md`](notes/PROTOCOL.md) — wire-format spec for every msgType in use
- [`notes/VPS.md`](notes/VPS.md) — Path A deploy guide
- [`notes/phase6/`](notes/phase6/) — phase-by-phase design notes (sharding v2, rewind buffer, chat-command parser, etc.)

Issues and PRs welcome.

## Credits

- **JoshuaDoes** ([@JoshuaDoes](https://github.com/JoshuaDoes)) — original [StickFightDev/StickFightDedicatedSrv](https://github.com/StickFightDev/StickFightDedicatedSrv) (dormant since July 2022). This repo started as a fork; the v25 relay + lobby + matchmaking core in [`legacy/StickFightDedicatedSrv/`](legacy/StickFightDedicatedSrv/) is from that project.
- **Landfall Games** — for making Stick Fight in the first place. This is an unofficial community project not affiliated with or endorsed by Landfall.
- **The SF competitive Discord** — for the ask + the testing.

## License

Code original to this repo is MIT-licensed (see [`LICENSE`](LICENSE)). The forked upstream code under [`legacy/StickFightDedicatedSrv/`](legacy/StickFightDedicatedSrv/) carried no clear license — using under good-faith interpretation that it was released for community use; will swap to a clean BSD/MIT if upstream confirms or relicense if requested.

Reference DLLs and decompiled Assembly-CSharp source are **not** included; they remain copyright Landfall and you must obtain them from your own purchased copy of the game.
