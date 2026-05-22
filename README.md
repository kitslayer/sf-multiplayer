# sf-multiplayer

Centralized dedicated-server revival for **Stick Fight: The Game** (Steam app 674940), built for the competitive scene to replace stock P2P with an authoritative server. Fixes rubber-banding, host-migration drops, and the long-standing physics divergence between clients.

> **Status: alpha, mid-pivot (2026-05-22).** The architecture pivoted from "Go server + headless Unity oracle" to "headless Unity IS the server, no Go bridge" (Path A). Real Steam SF connects directly to `SFHeadlessHost.dll` running inside headless Unity on UDP 1337. Host-side gameplay (weapon spawns, killboxes) runs through SF's own code driven by 7 Harmony patches — see [`notes/phase6/10-PHASE6.5-host-side-gameplay.md`](notes/phase6/10-PHASE6.5-host-side-gameplay.md) for the live state.
>
> The Go dedicated server (`StickFightDedicatedSrv/`) is no longer in the active data path. The repo still carries it (with v25 relay + v26 authoritative scaffolding) as archival reference; it will be quarantined or deleted in an upcoming cleanup pass.

## What's in this repo

| Path | What it is |
|---|---|
| [`sf-headless-host/`](sf-headless-host/) | **Live entry point.** BepInEx + Harmony plugin that turns headless Stick Fight into a v25-speaking server. Implements the raw-UDP protocol, drives SF's own host-side gameplay via 7 Harmony patches, forwards intercepted broadcasts to clients. |
| [`launch-sf-headless.sh`](launch-sf-headless.sh) | Launches Stick Fight under Proton in `-batchmode -nographics` with the headless-host plugin loaded. Per-instance wineprefix at `/tmp/sf-oracle-prefix-<bridgeport>`. |
| [`launch-sf-player.sh`](launch-sf-player.sh) | Launches Stick Fight in graphical mode (player-side) against `127.0.0.1:1337`. For local end-to-end testing. |
| [`StickFightDedicatedSrv/`](StickFightDedicatedSrv/) | **Deprecated (no longer in data path).** Go dedicated server, originally forked from [StickFightDev/StickFightDedicatedSrv](https://github.com/StickFightDev/StickFightDedicatedSrv). Phase 5 work (M1–M4) lives here; physics + v26 paths are now dead code. Will be deleted or quarantined in an upcoming cleanup pass. |
| [`sf-netcodev2/`](sf-netcodev2/) | **Parked.** BepInEx + Harmony plugin for the v26 client protocol (Go-server-coordinated path). Disabled in deployed plugins directory; obsolete under Path A. |
| [`sf-localcontrol-fix/`](sf-localcontrol-fix/) | **Parked.** Local-control behavior fix from an earlier session. Disabled. |
| [`sf-leveldumper/`](sf-leveldumper/) | One-shot BepInEx plugin that walks Landfall scenes and dumps geometry + spawn points + killboxes to JSON. Not loaded at runtime. |
| [`tools/`](tools/) | Python scripts. `dump-sf-maps.py` is an offline UnityPy-based map extractor that runs without launching SF. |
| [`maps/`](maps/) | Dumped map data — 123 Landfall scenes as JSON, used by the server for authoritative weapon/player spawn positions. |
| [`notes/`](notes/) | Living design + research docs. `notes/SUMMARY.md` is the latest top-level entry point. `notes/recon/` has bug investigations, `notes/design/` has fix proposals. |
| [`launch-sf-bepinex.sh`](launch-sf-bepinex.sh) | Wrapper that launches SF under Proton with `WINEDLLOVERRIDES="winhttp=n,b"` so BepInEx actually loads (Steam's vanilla "Play" doesn't set this env var). |

## Architecture

```
SF clients (Windows or Linux/Proton)
   │  UDP v25 — [u32 ts][u8 msgType][N body][u64 steamID][u8 channel]
   ▼
Headless Stick Fight (Proton + Goldberg, BepInEx + SFHeadlessHost.dll)
   • Raw-UDP v25 server on :1337 (no Lidgren handshake)
   • Drives SF's own MultiplayerManager / GameManager via 7 Harmony patches
     — IsServer=true, IsNetworkMatch pinned true, SpawnRandomWeapon replaced
   • Forwards intercepted SendMessageToAllClients broadcasts to v25 clients
   • Diagnostic bridge command channel on 127.0.0.1:1341 (loopback)
```

**Live state (Path A, since 2026-05-22):** real Stick Fight runs in `-batchmode -nographics`. It hosts the match using its own gameplay code — physics, killboxes, weapon spawn timers — and `SFHeadlessHost.dll` makes that code think it's a multiplayer host. Clients connect on raw UDP. See [`notes/phase6/10-PHASE6.5-host-side-gameplay.md`](notes/phase6/10-PHASE6.5-host-side-gameplay.md).

**Earlier path (Path D, deprecated):** Go server `StickFightDedicatedSrv/` ran lobby matchmaking + AABB physics + a headless Unity instance as a "physics oracle" over a JSON IPC bridge. Still in tree as archival reference; not in the active data path.

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

The `-address` / `-port` flags are read by the patched `Assembly-CSharp.dll` (see [`refs/`](refs/) for the decompile). For a Goldberg-shimmed local mirror, use [`launch-sf-player.sh`](launch-sf-player.sh).

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

Deploy to `<SF install>/BepInEx/plugins/SFHeadlessHost.dll`. (The other plugins in this repo — `sf-netcodev2/`, `sf-localcontrol-fix/`, `sf-leveldumper/` — are not part of the active Path A and should stay parked.)

## Wire-protocol notes

**v25** is the protocol the live Path A oracle speaks. It's the raw-UDP one used by the patched DLL (`StickFightDev/StickFightDLL` dev-v25). Every packet wraps the SF MsgType body in a 14-byte envelope: `[u32 timestamp LE][u8 msgType][N body][u64 steamID LE][u8 channel]`. SF's `P2PPackageHandler.MsgType` enum (38 entries from `Ping=0` ... `KickPlayer=38`) defines the dispatch.

Implementation lives in [`sf-headless-host/SFHeadlessHost.cs`](sf-headless-host/SFHeadlessHost.cs). The handshake (`ClientRequestingAccepting` → `ClientAccepted` → `ClientRequestingIndex` → `ClientInit` → `ClientRequestingToSpawn` → `ClientSpawned`) is implemented in the `Handle*` methods; the v25 wrapper codec is in `SendSfPacket`. Byte layouts for the 50-byte `ClientInit` body are documented in [`notes/recon/`](notes/recon/).

**v26** is the older authoritative protocol designed when the Go server was load-bearing. It's still in `StickFightDedicatedSrv/packets.go` (IDs 42-44 for `playerInput` / `worldStateSnapshot` / `serverEvent`) but is no longer used under Path A. Will be removed alongside the Go server.

## Credits

- **JoshuaDoes** ([@JoshuaDoes](https://github.com/JoshuaDoes)) — original [StickFightDev/StickFightDedicatedSrv](https://github.com/StickFightDev/StickFightDedicatedSrv) (dormant since July 2022). This repo started as a fork and reuses the lobby + matchmaking + packet-relay core. Discord-confirmed they also have v26 launcher work that isn't in their public repo.
- **Landfall Games** — for making Stick Fight in the first place. This is an unofficial community project not affiliated with or endorsed by Landfall.
- **The SF competitive Discord** — for the ask + the testing.

## License

Code original to this repo is MIT-licensed (see `LICENSE`). The forked upstream code (StickFightDedicatedSrv source originally from JoshuaDoes) carried no clear license — using under good-faith interpretation that it was released for community use; will swap to a clean BSD/MIT if upstream confirms or relicense if requested.

Reference DLLs and decompiled Assembly-CSharp source are **not** included; they remain copyright Landfall and you must obtain them from your own purchased copy of the game.

## Development status

Roadmap, current bugs, and design docs live in [`notes/`](notes/). Most useful entries:

- `notes/README.md` — index
- `notes/SUMMARY.md` — latest one-paragraph status
- `notes/NEXT_SESSION_HANDOFF.md` — pick-up-here doc for contributors

Issues and PRs welcome.
