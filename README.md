<div align="center">

# 🎮 WANNA TEST IT? &nbsp;→&nbsp; [**⬇️ DOWNLOAD HERE**](https://github.com/kitslayer/sf-multiplayer/raw/main/sf-multiplayer-StickFight-Installer.zip) &nbsp;←

### [**`⬇️  sf-multiplayer 1-Click Installer (.zip)`**](https://github.com/kitslayer/sf-multiplayer/raw/main/sf-multiplayer-StickFight-Installer.zip)

Self-contained — BepInEx + the client plugins + patched `Assembly-CSharp` are **all inside**.
Unzip → run **`INSTALAR-sf-multiplayer.bat`** → launch Stick Fight. That's it.

`SFClientRecon 0.5.3` · `SFServerBrowser 0.5.3` · native uGUI lobby (**F2**) · smooth crates

</div>

---

# sf-multiplayer — Stick Fight: The Game dedicated server

**A community project** to kill **Stick Fight's peer-to-peer host model** and replace it with a real **dedicated, server-authoritative** backend — so matches stop rubber-banding, surviving a host's connection drop, and showing each player a different physics simulation.

In stock Stick Fight one of the players *is* the host: their PC runs the match, everyone else relays through them, and if they lag or leave, the lobby dies and the simulation diverges. **sf-multiplayer removes that.** A headless copy of the game runs the match on a dedicated server (the "oracle"); every player is just a client. The result is lower, *consistent* lag, no host-migration drops, and both screens converging on the **same** authoritative world.

> **What it gives you**
> - 🛰️ **Dedicated server, no P2P host** — the match lives on the oracle, not on a player's PC.
> - 🎯 **Server-authoritative simulation** — physics, killboxes, weapon spawns and damage all resolved on the server (client predicts locally, server reconciles — the CS:GO / Valorant model).
> - 🛡️ **Anti-cheat** — damage is validated server-side with tick-history rewind; movement/fire is bounded; clients can't fabricate hits or teleport.
> - 📉 **Less lag, more in-sync** — 60 Hz client input, 30 Hz authoritative snapshots, uncapped FPS, client-local crate physics for smoothness.
> - 🧩 **Multi-lobby** — one server runs many isolated matches at once; a single-port UDP router fronts them all.

## ✨ Features (the client mod)

Everything below ships in the **1-click installer** above — drop it on any Steam copy of Stick Fight and you're online.

| Feature | What it does |
|---|---|
| **Dedicated-server client** (`SFClientRecon`) | Connects to the oracle instead of a P2P host; runs client prediction + server reconciliation; uncaps FPS. |
| **In-game lobby & server browser** | Press **F2** for the native in-game lobby (BROWSE / JOIN by code / CREATE / SETTINGS), or use the "PLAY ONLINE" menu button. Auto-finds the server from your `-address`. |
| **English + Spanish UI** | English by default; switch to Spanish in the lobby's **SETTINGS** tab (your choice is saved). |
| **Server-authoritative crates** | Crates push, tip, tumble and fall off edges like vanilla — but driven so both clients agree, without the old rubber-banding. |
| **Weapon fixes** | Thrown weapons fly clean and never damage the thrower; the throw button never sticks; map weapons register correctly. |
| **Explosive barrels & map gimmicks** | Powder barrels detonate again; frozen map scripts can be self-driven (`SF_MAP_LOCAL_TYPES`). |
| **Anti-cheat host** | Server-side damage validation, tick rewind, and bounds checks (`SFHeadlessHost`). |
| **Clean uninstall** | The uninstaller restores your game *exactly* as it was and leaves any other mods you had untouched. |

## 🔧 Recent fixes

Active development (see the [commit history](https://github.com/kitslayer/sf-multiplayer/commits/main) for who did what):

- **Crates** tip / tumble / fall off edges like vanilla again (the rotation was frozen on the wrong axis); player-push capped so they're not over-floaty.
- **Weapons** — thrown weapons fly clean and never hit the thrower; the throw button no longer sticks; fast-throw can't duplicate a weapon (rate-limited — also closes that server exploit); map weapons stopped vanishing (a repeated init was deleting them).
- **Explosive barrels** detonate again.
- **In-game menu** is clickable again (IMGUI control-id shift was eating clicks).
- **Lobby UI** auto-finds the server, English/Spanish toggle, native uGUI lobby (**F2**), team scoreboard (RED vs BLUE + extra players, **F4**).
- **Performance** — killed per-frame reflection + log spam; uncapped FPS; input sent at a fixed 60 Hz regardless of your frame rate.

## What’s in this repo

|Path                                                                                                                                                         |What it is                                                                                                                                                                                                                                                                                            |
|-------------------------------------------------------------------------------------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
|[`sf-headless-host/`](sf-headless-host)                                                                                                                      |**Server-side plugin.** BepInEx + Harmony plugin (~6,400 lines, plus a ~1,280-line map-terrain helper) that turns headless Stick Fight into a v25 + v26-speaking authoritative server. Drives SF’s own host-side gameplay via Harmony patches, broadcasts state snapshots, processes client inputs, validates damage with tick-history rewind.|
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

SF’s stock `P2PPackageHandler.MsgType` enum has 38 entries (`Ping=0` … `KickPlayer=38`). This repo extends with **v26.6** (current snapshot wire version):

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

### Team

| Name | Role | Contact |
|------|------|---------|
| **kit** | **Lead & maintainer.** Dedicated server & VPS oracle, headless host, v26 protocol, single-port relay/router, multi-lobby, monitoring, anti-cheat, hosting & deploy. | GitHub: [@kitslayer](https://github.com/kitslayer) |
| **ALKA** | **Major early contributor — client side (now departed).** Client mod, in-game lobby/GUI, crate & box physics, FPS uncap, the Windows installer, and live testing & QA. Has since moved on to a separate edition for the Spanish-speaking community. | GitHub: [@AlkaPrime12](https://github.com/AlkaPrime12) |

This project is led and maintained by kit — the dedicated server, backend, protocol, router, and hosting. ALKA contributed a great deal on the client side early on (the client mod, in-game lobby/GUI, and crate/box physics) before stepping away from this project, and that work is still part of what ships here. The split of work is visible in each author's commits — see the [commit history](https://github.com/kitslayer/sf-multiplayer/commits/main).

Repo: [github.com/kitslayer/sf-multiplayer](https://github.com/kitslayer/sf-multiplayer)

### Other

- **JoshuaDoes** ([@JoshuaDoes](https://github.com/JoshuaDoes)) — original [StickFightDev/StickFightDedicatedSrv](https://github.com/StickFightDev/StickFightDedicatedSrv) (dormant since July 2022). This repo started as a fork; the v25 relay + lobby + matchmaking core in [`legacy/StickFightDedicatedSrv/`](legacy/StickFightDedicatedSrv) is from that project.
- **Landfall Games** — for making Stick Fight in the first place. This is an unofficial community project not affiliated with or endorsed by Landfall.
- **The SF competitive Discord** ([DSF](https://discord.gg/nrzMBA6XVc)) — for the ask + the testing.

## License

Code original to this repo is MIT-licensed (see [`LICENSE`](LICENSE)). The forked upstream code under [`legacy/StickFightDedicatedSrv/`](legacy/StickFightDedicatedSrv) carried no clear license — using under good-faith interpretation that it was released for community use; will swap to a clean BSD/MIT if upstream confirms or relicense if requested.

Reference DLLs and decompiled Assembly-CSharp source are **not** included; they remain copyright Landfall and you must obtain them from your own purchased copy of the game.
