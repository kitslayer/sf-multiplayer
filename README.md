<div align="center">

# 🎮 WANNA TEST IT? &nbsp;→&nbsp; [**⬇️ DOWNLOAD HERE**](https://github.com/kitslayer/sf-multiplayer/raw/main/ALKA-KITSLAYER-StickFight-Installer.zip) &nbsp;←

### [**`⬇️  ALKA-KITSLAYER 1-Click Installer (.zip)`**](https://github.com/kitslayer/sf-multiplayer/raw/main/ALKA-KITSLAYER-StickFight-Installer.zip)

Self-contained — BepInEx + the ALKA plugins + patched `Assembly-CSharp` are **all inside**.
Unzip → run **`INSTALAR-ALKA-KITSLAYER.bat`** → launch Stick Fight. That's it.

`SFClientRecon 0.4.0` · `SFServerBrowser 0.3.0` · native uGUI lobby (**F2**) · smooth crates

</div>

---

# sf-multiplayer

Centralized dedicated-server revival for **Stick Fight: The Game** (Steam app 674940), built for the competitive scene to replace stock P2P with an authoritative server. Goal: fix rubber-banding, host-migration drops, and the long-standing physics divergence between clients.

> **Status: beta / live-test verification.** Real Stick Fight (Windows or Linux/Proton) connects directly to `SFHeadlessHost.dll` running inside a headless instance of the game on UDP 1337. The host-side gameplay loop — physics, killboxes, weapon spawn timers — runs through SF’s own code, with the headless host plugin driving it via Harmony patches.
> 
> The v26.5 protocol with client-side prediction + server reconciliation (canonical CSGO/Valorant model) is shipped: client predicts locally, sends inputs to the server at 60Hz, server runs the authoritative simulation, broadcasts 30Hz `WorldStateSnapshot`, client shift-corrects when its prediction diverged. Multi-lobby sharding (multi-process) is live — one host can run several isolated matches concurrently. Current work is bug verification from live testing + polish.

## What’s in this repo

|Path                                                                                                                                                         |What it is                                                                                                                                                                                                                                                                                            |
|-------------------------------------------------------------------------------------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
|[`sf-headless-host/`](sf-headless-host)                                                                                                                      |**Server-side plugin.** BepInEx + Harmony plugin (~3500 lines) that turns headless Stick Fight into a v25 + v26-speaking authoritative server. Drives SF’s own host-side gameplay via Harmony patches, broadcasts state snapshots, processes client inputs, validates damage with tick-history rewind.|
|[`sf-client-recon/`](sf-client-recon)                                                                                                                        |**Client-side companion plugin.** BepInEx plugin shipped to each player’s install. Receives `WorldStateSnapshot`, smoothly reconciles local state toward server values, detects divergence, sends `PktPlayerInput` at 60Hz, emits projectile-fire events.                                             |
|[`sf-server-browser/`](sf-server-browser) |**In-game lobby browser plugin.** IMGUI "SERVERS" menu — lists lobbies (`GET /lobbies`), JOIN / join-by-code, and CREATE (`POST`, token-gated). Reads `SF_LOBBY_ENDPOINT`.|
|[`sf-router/`](sf-router) |**Single-port UDP router (Go).** One public port fronts many lobbies; routes each client to its lobby's backend `SF.exe` via a SELECT control datagram + the `/tmp/sf-lobbies` registry. Unit-tested (`go test -race`).|
|[`launch-router.sh`](launch-router.sh) |Launches the router in registry (multi-lobby) mode.|
|[`launch-sf-headless.sh`](launch-sf-headless.sh)                                                                                                             |Single-oracle launcher (Proton + batchmode).                                                                                                                                                                                                                                                          |
|[`launch-lobby.sh`](launch-lobby.sh) / [`stop-lobby.sh`](stop-lobby.sh) / [`list-lobbies.sh`](list-lobbies.sh) / [`stop-all-lobbies.sh`](stop-all-lobbies.sh)|Multi-lobby management — each lobby is its own isolated SF process + wineprefix + UDP port.                                                                                                                                                                                                           |
|[`serve-lobbies.py`](serve-lobbies.py)                                                                                                                       |HTTP `/lobbies` JSON endpoint + HTML viewer (for in-game / web server browsers).                                                                                                                                                                                                                      |
|[`setup-all.sh`](setup-all.sh)                                                                                                                               |One-command build + deploy of both plugins.                                                                                                                                                                                                                                                           |
|[`launch-sf-player.sh`](launch-sf-player.sh)                                                                                                                 |Graphical second-player instance for local end-to-end testing.                                                                                                                                                                                                                                        |
|[`deploy/`](deploy)                                                                                                                                          |Windows `.bat` wrappers for lobby management.                                                                                                                                                                                                                                                         |
|[`maps/`](maps)                                                                                                                                              |123 Landfall scene JSON dumps — geometry, spawns, killboxes. Used by the server for authoritative spawn positions.                                                                                                                                                                                    |
|[`tools/`](tools)                                                                                                                                            |Python utilities. `dump-sf-maps.py` is an offline UnityPy-based map extractor.                                                                                                                                                                                                                        |
|[`sf-leveldumper/`](sf-leveldumper)                                                                                                                          |One-shot BepInEx plugin that walks Landfall scenes and dumps geometry to JSON. Not loaded at runtime.                                                                                                                                                                                                 |
|[`notes/`](notes)                                                                                                                                            |Living design + research docs. [`WHATS_NEW.md`](WHATS_NEW.md) is the running session log; [`NEXT_STEPS.md`](NEXT_STEPS.md) is current state + roadmap; [`notes/ARCHITECTURE.md`](notes/ARCHITECTURE.md) is the system overview.                                                                       |
|[`legacy/`](legacy)                                                                                                                                          |Earlier architectures (Go dedicated server, v26 client plugin draft, lobby browser, launcher fork) kept for reference. Not in the active data path.                                                                                                                                                   |

## Architecture

```
Player machines                                Server machine
(Windows native or Linux/Proton)               (Linux + Proton + Goldberg + BepInEx)

StickFight.exe (graphical)                     StickFight.exe -batchmode -nographics
  + patched Assembly-CSharp.dll    v25 UDP       + SFHeadlessHost.dll
  + SFClientRecon.dll              ────────►       • v25 + v26 server (UDP 1337 / 1339)
                                   v26 input       • Harmony-patches MultiplayerManager
                                   60Hz            • 30Hz WorldStateSnapshot broadcast
                                   ◄────────       • Server-authoritative NSOs, projectiles
                                   v26 snap        • Damage validation w/ tick-history rewind
                                   30Hz
```

Real Stick Fight runs in `-batchmode -nographics`. It hosts the match using its own gameplay code; `SFHeadlessHost.dll` makes that code think it’s a multiplayer host. Clients connect on raw UDP. See [`notes/ARCHITECTURE.md`](notes/ARCHITECTURE.md) for the full overview, including authority model and channel routing.

## Quickstart

### Launch a single server (headless Stick Fight)

```
SFHEADLESS_BRIDGEPORT=1341 SFHEADLESS_PORT=1337 SFHEADLESS_DEBUG=1 \
  bash launch-sf-headless.sh
```

This launches Stick Fight under Proton in `-batchmode -nographics`, loads `SFHeadlessHost.dll` via BepInEx, and binds the v25 raw-UDP server on `0.0.0.0:1337`. Logs go to `$SF_MIRROR/BepInEx/LogOutput.log` and `/tmp/sf-oracle-unity-1341.log`.

### Launch a multi-lobby host

```
bash launch-lobby.sh CODE123      # spawn one isolated lobby
bash list-lobbies.sh              # tabulate running lobbies
python serve-lobbies.py           # HTTP /lobbies JSON for browsers
```

Each lobby is a fully isolated SF process on its own UDP port with its own wineprefix. ~500 MB RAM + 1 vCPU per lobby; a hobby VPS handles 6–8 concurrent.

### Connect a real client

For a Steam install of Stick Fight, set these launch options:

```
WINEDLLOVERRIDES="winhttp=n,b" %command% -address 127.0.0.1 -port 1337
```

The `-address` / `-port` flags are read by a patched `Assembly-CSharp.dll`. For a Goldberg-shimmed local mirror, use [`launch-sf-player.sh`](launch-sf-player.sh).

### Build the plugins

Both `SFHeadlessHost` and `SFClientRecon` are .NET 4.6 BepInEx 5 assemblies. **You need to provide the reference DLLs locally** — they’re not in this repo for copyright reasons. Each plugin’s `refs/` directory needs:

- `Assembly-CSharp.dll` — from your Stick Fight install (`StickFight_Data/Managed/`)
- `UnityEngine.dll` — same directory
- `BepInEx.dll`, `0Harmony.dll` — from a BepInEx 5.4.x install

Then:

```
bash setup-all.sh
```

This builds both plugins and deploys them to your local oracle + Steam installs.

## Wire protocol

Every packet wraps the SF MsgType body in a 14-byte envelope:
`[u32 timestamp LE][u8 msgType][N body][u64 steamID LE][u8 channel]`

SF’s stock `P2PPackageHandler.MsgType` enum has 38 entries (`Ping=0` … `KickPlayer=38`). This repo extends with **v26.5**:

|ID|Direction              |Purpose                                                                                                                                 |
|--|-----------------------|----------------------------------------------------------------------------------------------------------------------------------------|
|39|server → clients @ 30Hz|`WorldStateSnapshot` — player positions w/ `lastInputSeq`, NSO positions, projectile positions, `MapInfoSyncableBase` platform positions|
|40|client → server @ 60Hz |`PktPlayerInput` — stick + aim + buttons + sequence number                                                                              |
|41|client → server, event |`PktClientFireWeapon` — emitted on local `Weapon.ActuallyShoot`                                                                         |

Implementation lives in [`sf-headless-host/SFHeadlessHost.cs`](sf-headless-host/SFHeadlessHost.cs) (server) and [`sf-client-recon/SFClientRecon.cs`](sf-client-recon/SFClientRecon.cs) (client). Full byte layouts in [`notes/PROTOCOL.md`](notes/PROTOCOL.md). Channel-routing reference in [`notes/ARCHITECTURE.md`](notes/ARCHITECTURE.md).

## Development status

- [`WHATS_NEW.md`](WHATS_NEW.md) — running session log (start here)
- [`NEXT_STEPS.md`](NEXT_STEPS.md) — current state + remaining work
- [`notes/ARCHITECTURE.md`](notes/ARCHITECTURE.md) — full system overview
- [`notes/PROTOCOL.md`](notes/PROTOCOL.md) — wire-format spec
- [`notes/OBJECT_SYNC.md`](notes/OBJECT_SYNC.md) — debugging guide for SF’s three world-object sync mechanisms (NSO, MapInfoSyncableBase, DestructiblePiece)
- [`notes/BUGS_BACKLOG.md`](notes/BUGS_BACKLOG.md) — incident log with root causes + fixes
- [`notes/AUDIT_2026-05-23.md`](notes/AUDIT_2026-05-23.md) — latest end-of-session deep audit

Issues and PRs welcome.

## Credits

### Team (sf-multiplayer / oracle)

| Name | Role | Contact |
|------|------|---------|
| **kitslayer** | Maintainer, headless host, VPS oracle, v26 protocol | GitHub: [@kitslayer](https://github.com/kitslayer) · Discord: `kitslayer` |
| **AlkaDev** | Client plugin, Windows deploy scripts, live testing, box/lobby fixes | GitHub: [@AlkaPrime12](https://github.com/AlkaPrime12) · Discord: `Tyralka0660` |

Repo: [github.com/kitslayer/sf-multiplayer](https://github.com/kitslayer/sf-multiplayer)

### Other

- **JoshuaDoes** ([@JoshuaDoes](https://github.com/JoshuaDoes)) — original [StickFightDev/StickFightDedicatedSrv](https://github.com/StickFightDev/StickFightDedicatedSrv) (dormant since July 2022). This repo started as a fork; the v25 relay + lobby + matchmaking core in [`legacy/StickFightDedicatedSrv/`](legacy/StickFightDedicatedSrv) is from that project.
- **Landfall Games** — for making Stick Fight in the first place. This is an unofficial community project not affiliated with or endorsed by Landfall.
- **The SF competitive Discord** — for the ask + the testing.

## License

Code original to this repo is MIT-licensed (see [`LICENSE`](LICENSE)). The forked upstream code under [`legacy/StickFightDedicatedSrv/`](legacy/StickFightDedicatedSrv) carried no clear license — using under good-faith interpretation that it was released for community use; will swap to a clean BSD/MIT if upstream confirms or relicense if requested.

Reference DLLs and decompiled Assembly-CSharp source are **not** included; they remain copyright Landfall and you must obtain them from your own purchased copy of the game.
