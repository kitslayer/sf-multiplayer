# sf-multiplayer

Centralized dedicated-server revival for **Stick Fight: The Game** (Steam app 674940), built for the competitive scene to replace stock P2P with an authoritative server. Fixes rubber-banding, host-migration drops, and the long-standing physics divergence between clients.

> **Status: alpha.** Server-side gameplay works end-to-end (v25 relay + v26 authoritative paths). Comp-ready features (server-side physics oracle, full anti-cheat, multi-region) are in progress. Not yet stamped "v2 release" — see [`notes/`](notes/) for current state.

## What's in this repo

| Path | What it is |
|---|---|
| [`StickFightDedicatedSrv/`](StickFightDedicatedSrv/) | Go dedicated server. Originally forked from [StickFightDev/StickFightDedicatedSrv](https://github.com/StickFightDev/StickFightDedicatedSrv) (dormant since 2022); substantially extended with Phase 5 server-authoritative netcode (M2-M4 done, M5 in progress). |
| [`sf-netcodev2/`](sf-netcodev2/) | BepInEx + Harmony plugin. Bumps client protocol to v26, hooks `Controller.Update` to emit `playerInput`, parses `worldStateSnapshot` + `serverEvent` packets, runs client prediction. |
| [`sf-leveldumper/`](sf-leveldumper/) | BepInEx plugin that walks Landfall scenes and dumps geometry + spawn points + killboxes to JSON. Runtime-mode listener supports workshop maps too. |
| [`sf-localcontrol-fix/`](sf-localcontrol-fix/) | Small earlier-session plugin for local-control behavior fixes. |
| [`tools/`](tools/) | Python scripts. `dump-sf-maps.py` is an offline UnityPy-based map extractor that runs without launching SF. |
| [`maps/`](maps/) | Dumped map data — 123 Landfall scenes as JSON, used by the server for authoritative weapon/player spawn positions. |
| [`notes/`](notes/) | Living design + research docs. `notes/SUMMARY.md` is the latest top-level entry point. `notes/recon/` has bug investigations, `notes/design/` has fix proposals. |
| [`launch-sf-bepinex.sh`](launch-sf-bepinex.sh) | Wrapper that launches SF under Proton with `WINEDLLOVERRIDES="winhttp=n,b"` so BepInEx actually loads (Steam's vanilla "Play" doesn't set this env var). |

## Architecture

```
SF clients (Windows or Linux/Proton)
   │  UDP — Lidgren-style packets (v25 legacy + v26 authoritative)
   ▼
Go dedicated server (StickFightDedicatedSrv/)
   • Lobby + matchmaking
   • Wire-protocol relay
   • Server-side physics simulation (AABB, killbox, projectile)
   • Snapshot broadcast @ 30 Hz to v26 clients
   • Damage validation, anticheat, replay logging
```

**Current state (Path A: relay + lightweight server physics):** server authoritative for player position, projectile trajectory, damage events, weapon spawn timing/positions. Ragdoll/joint wobble stays client-side (cosmetic).

**Planned Phase 6 (Path D: headless-Unity oracle):** for the comp scene's need for perfect physics, replace the Go AABB sim with a per-lobby headless Unity instance running real SF Movement.cs / ConfigurableJoint / killbox logic. See [`notes/design/`](notes/design/) for the in-progress plan.

## Quickstart

### Build the server

```bash
cd StickFightDedicatedSrv
go build -o /tmp/sfdsrv .
/tmp/sfdsrv -address 0.0.0.0:1337 -mapsDir ../maps -publicLobbies
```

The server listens on UDP `:1337` plus an HTTP-on-the-same-port shim for `/status`, `/lobbies`, `/maps`, `/invite`.

### Build the client plugins

Each plugin is a .NET 4.6 BepInEx 5 assembly. **You need to provide the reference DLLs locally** — they're not in this repo for copyright reasons. For each plugin's `refs/` directory you need:

- `Assembly-CSharp.dll` — from your Stick Fight install (`StickFight_Data/Managed/`)
- `UnityEngine.dll` — same directory
- `BepInEx.dll`, `0Harmony.dll` — from a BepInEx 5.4.x install

Then:

```bash
cd sf-netcodev2  # or sf-leveldumper, sf-localcontrol-fix
dotnet build -c Release
# Output → bin/Release/<PluginName>.dll
```

Deploy the `.dll` to `<SF install>/BepInEx/plugins/`.

### Launch SF with the plugins loaded

If you're on Linux (Proton):

```bash
./launch-sf-bepinex.sh
```

If you're on Windows via Steam, add `WINEDLLOVERRIDES="winhttp=n,b" %command%` to the game's Launch Options (Properties → General).

### Connect to the server

Use the matching launcher fork (TBD upstream link) or set the address with `-address <ip>:<port>` on the patched DLL.

## Wire-protocol notes

**v25** is the original P2P-relay protocol used by upstream and stock-with-patched-DLL clients. The server forwards packets unchanged; gameplay logic stays client-side.

**v26** is the new authoritative protocol introduced here:

| ID | Type | Direction | Notes |
|---|---|---|---|
| 42 | `playerInput` | client → server | 60 Hz; stick + aim + buttons + sequence |
| 43 | `worldStateSnapshot` | server → client | 30 Hz, ~19 bytes per entity (id + kind + slot + posXYZ\*100 + velXYZ\*100 + flags) |
| 44 | `serverEvent` | server → client | reliable; damage/impact/weapon-spawn events |

A v26 client advertises protocol `26` in its `clientRequestingIndex`; the server downgrades to v25 (relay only) if it sees `25`.

See [`StickFightDedicatedSrv/packets.go`](StickFightDedicatedSrv/packets.go) for the full enum and [`notes/recon/`](notes/recon/) for byte-layout traces.

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
