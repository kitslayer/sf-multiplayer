# sf-multiplayer

Centralized dedicated-server revival for **Stick Fight: The Game** (Steam app 674940), built for the competitive scene to replace stock P2P with an authoritative server. Goal: fix rubber-banding, host-migration drops, and the long-standing physics divergence between clients.

> **Status: alpha.** Real Stick Fight (Windows or Linux/Proton) connects directly to `SFHeadlessHost.dll` running inside a headless instance of the game on UDP 1337. The host-side gameplay loop — physics, killboxes, weapon spawn timers — runs through SF's own code, with the headless host plugin driving it via Harmony patches.
>
> The end-state goal is full client-side prediction + server reconciliation: client predicts locally, sends inputs to the server, server runs the authoritative simulation, broadcasts snapshots, client snap-corrects when its prediction diverged.

## What's in this repo

| Path | What it is |
|---|---|
| [`sf-headless-host/`](sf-headless-host/) | **Live entry point.** BepInEx + Harmony plugin that turns headless Stick Fight into a v25-speaking server. Implements the raw-UDP protocol, drives SF's own host-side gameplay via Harmony patches, forwards intercepted broadcasts to clients. |
| [`launch-sf-headless.sh`](launch-sf-headless.sh) | Launches Stick Fight under Proton in `-batchmode -nographics` with the headless-host plugin loaded. |
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
SF clients (Windows or Linux/Proton)
   │  UDP v25 — [u32 ts][u8 msgType][N body][u64 steamID][u8 channel]
   ▼
Headless Stick Fight (Proton + Goldberg, BepInEx + SFHeadlessHost.dll)
   • Raw-UDP v25 server on :1337
   • Drives SF's own MultiplayerManager / GameManager via Harmony patches
     — IsServer=true, IsNetworkMatch pinned true, SpawnRandomWeapon replaced
   • Forwards intercepted SendMessageToAllClients broadcasts to v25 clients
```

Real Stick Fight runs in `-batchmode -nographics`. It hosts the match using its own gameplay code; `SFHeadlessHost.dll` makes that code think it's a multiplayer host. Clients connect on raw UDP. See [`notes/phase6/10-PHASE6.5-host-side-gameplay.md`](notes/phase6/10-PHASE6.5-host-side-gameplay.md) for the current state.

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

## Wire protocol (v25)

Every packet wraps the SF MsgType body in a 14-byte envelope: `[u32 timestamp LE][u8 msgType][N body][u64 steamID LE][u8 channel]`. SF's `P2PPackageHandler.MsgType` enum (38 entries from `Ping=0` ... `KickPlayer=38`) defines the dispatch.

Implementation lives in [`sf-headless-host/SFHeadlessHost.cs`](sf-headless-host/SFHeadlessHost.cs). The handshake (`ClientRequestingAccepting` → `ClientAccepted` → `ClientRequestingIndex` → `ClientInit` → `ClientRequestingToSpawn` → `ClientSpawned`) is implemented in the `Handle*` methods; the v25 wrapper codec is in `SendSfPacket`. Byte layouts for the 50-byte `ClientInit` body are documented in [`notes/recon/`](notes/recon/).

A future v26 protocol — adding `playerInput` (client→server inputs with sequence numbers) and `worldStateSnapshot` (server→client authoritative state) — is the foundation for the prediction+reconciliation end-state.

## Development status

Roadmap, current bugs, and design docs live in [`notes/`](notes/). Most useful entries:

- [`notes/README.md`](notes/README.md) — index
- [`notes/SUMMARY.md`](notes/SUMMARY.md) — latest one-paragraph status
- [`notes/NEXT_SESSION_HANDOFF.md`](notes/NEXT_SESSION_HANDOFF.md) — pick-up-here doc for contributors
- [`notes/phase6/`](notes/phase6/) — current architecture work (host-side gameplay, physics, pickup forwarding)

Issues and PRs welcome.

## Credits

- **JoshuaDoes** ([@JoshuaDoes](https://github.com/JoshuaDoes)) — original [StickFightDev/StickFightDedicatedSrv](https://github.com/StickFightDev/StickFightDedicatedSrv) (dormant since July 2022). This repo started as a fork; the v25 relay + lobby + matchmaking core in [`legacy/StickFightDedicatedSrv/`](legacy/StickFightDedicatedSrv/) is from that project.
- **Landfall Games** — for making Stick Fight in the first place. This is an unofficial community project not affiliated with or endorsed by Landfall.
- **The SF competitive Discord** — for the ask + the testing.

## License

Code original to this repo is MIT-licensed (see [`LICENSE`](LICENSE)). The forked upstream code under [`legacy/StickFightDedicatedSrv/`](legacy/StickFightDedicatedSrv/) carried no clear license — using under good-faith interpretation that it was released for community use; will swap to a clean BSD/MIT if upstream confirms or relicense if requested.

Reference DLLs and decompiled Assembly-CSharp source are **not** included; they remain copyright Landfall and you must obtain them from your own purchased copy of the game.
