# StickFightDedicatedSrv — current architecture (as of 2026-05-21)

> ⚠️ **Historical record (2026-05-21).** Describes the now-abandoned Go `sfdsrv`/`StickFightDedicatedSrv` server (now in [`legacy/`](../../legacy)); not the current BepInEx/Harmony headless host. Kept for reference; current state in [NEXT_STEPS.md](../../NEXT_STEPS.md).

Distilled from a read of all `.go` files under `~/sf-multiplayer/StickFightDedicatedSrv/` on the dev laptop. Treat as a quick orientation map for the next session; the source code is the ground truth.

## Process model

- One process per server, listening on UDP+TCP at a single address (default `127.0.0.1:1337` for prod, use `1338` for test).
- TCP listener serves HTTP-shaped requests (`/status`, `/lobbies`, `/maps`, `/invite`).
- UDP listener serves the real game-protocol packets.
- `Server.ReadPackets` runs N goroutines (one per CPU) reading UDP; each packet is `go srv.Handle(...)`'d.

## Files and what's in them

- `main.go` — CLI flag parsing + steamcmd init + `Server.Run()`.
- `server.go` — `Server` struct, packet dispatch, HTTP-shape handling, connect rate limit.
- `lobbies.go` (103 KB, **the meat**) — `Lobby` struct + all game-flow code. Contains `ClientInit`, `Handle`, `SpawnPlayer`, `StartMatch`, `MatchInProgress`, `CheckWinner`, `ChangeMap`, `PlayerUpdate`, `PlayerTookDamage`, `PlayerFallOut`, `PlayerInput`, `BroadcastWorldSnapshot`, `runTickLoop`, `handlePhysicsEvent`, killbox handling, etc.
- `clients.go` — `Client` struct + `ClientAdd`/`ClientRemove` + `clientsMu` snapshot helper.
- `players.go` — `Player` struct (per-player game state: Health, Stats, Ready, Spawned, etc.).
- `packets.go` — Packet type enum, `NewPacketFromBytes`, wire-format helpers.
- `combat.go` — Weapon enum constants and type defs.
- `gamemodes.go`, `gm_stock.go`, `gm_duel.go`, `gm_gungame.go`, `gm_tournament.go` — game-mode interface + 4 concrete modes.
- `levels.go` — `Level` struct (sceneIndex, mapSize, etc.) + level-cycling logic.
- `map_loader.go` — Dumped-map JSON loader + `WeaponSpawnCandidates`.
- `objects.go` — Generic object/weapon syncable types.
- `replay.go` — Per-lobby `SFRPL` binary log writer.
- `steam.go` — `CSteamID` + steamcmd integration.
- `types.go` — Vector2/3, FightState, basic types.
- `codes.go` — Room-code generator.
- `physics/` — Per-lobby physics world (M0b-M4 server-authoritative netcode):
  - `world.go` — `World`, `Entity`, `Step()` with gravity + swept-AABB + killbox check.
  - `player.go` — `ApplyPlayerInput` (movement only — no double-jump, wall-slide, etc.).
  - `aabb.go`, `raycast.go` — Geometry primitives.
  - `mapdata.go` — JSON schema for dumped maps + `HydrateWorld`.
  - `events.go` — `Event` struct + event kinds.
  - `*_test.go` — Unit tests (15+ passing per prior session notes).
- `cmd/smoke-test/` — Go mock client exercising v25 + v26 protocols end-to-end.

## Lobby lifecycle

```
NewLobby (server.go:555)
   ↓
ClientInit (a few clients connect, get player slot indices)
   ↓
ChangeMap (hydrate first map; possibly the lobby map first)
   ↓
[StartMatch can fire when all players Ready (or auto-ready 3s timer)]
   ↓
FightStartTime = time.Now()
broadcast StartMatch
GameMode.StartMatch(lobby) starts in its own goroutine
   ↓
runTickLoop runs at 60Hz while lobby is alive:
   - Step physics world (only if hasV26Clients && World != nil)
   - Handle emitted physics events → broadcast packets
   - Every other tick: BroadcastWorldSnapshot
   - Independently: periodic weapon spawns
   ↓
Player kills happen via PlayerTookDamage (or physics killbox).
CheckWinner → ChangeMap when 1 survivor.
   ↓
ChangeMap unsets FightStartTime, un-readies everyone, picks next level,
broadcasts mapChange + level data, hydrates the new map into the World.
   ↓
[Loop to ReadyUp → StartMatch]
```

## Protocol versions

- **v25** — original SF protocol, P2P-flavor on top of UDP, position+weapon "playerUpdate" packets at 50Hz, client-asserted damage via `playerTookDamage(666.666)` for kills. Server is a relay + game-state-keeper. No physics on server (or rather: physics exists but does nothing useful — see below).
- **v26** — what the patched DLL (SFNetcodeV2) advertises. Adds:
  - `playerInput` packet (replaces `playerUpdate`'s motion role).
  - `worldStateSnapshot` packet (server → clients, 30Hz, M2+).
  - `serverEvent` packet (server-authoritative damage/impacts, M4+).
  - Client-side reflection-based remote player lerp (`PlayerSync` class in the plugin).
  - Server-side physics world ticks only when `hasV26Clients` is true.

`Lobby.hasV26Clients` is a cached bool (recomputed on `ClientAdd`/`ClientRemove`/`RecomputeV26Status`). Mixed-version lobbies work — v25 clients get legacy packets, v26 clients additionally get snapshot stream.

## Match-flow critical paths (for the 3-second-cycle bug)

- `SpawnPlayer` at `lobbies.go:1497-1551` — **flag=1 bug lives here**.
- `PlayerTookDamage` at `lobbies.go:2217-2322` — handles incoming damage, including 666.666 kill blow.
- `CheckWinner` at `lobbies.go:1813-1853` — survivor count → ChangeMap.
- `ChangeMap` at `lobbies.go:1855+` — un-ready, clear FightStartTime, pick next level, hydrate.
- `StartMatch` at `lobbies.go:1707-1789` — set FightStartTime, broadcast, start game mode goroutine.
- `MatchInProgress` at `lobbies.go:1791-1796` — `!FightStartTime.IsZero()`.
- Auto-ready goroutine inside `SpawnPlayer` at `lobbies.go:1558-1586` — 3s after spawn, mark ready and StartMatch if all ready.
- `runTickLoop` at `lobbies.go:283-358` — 60Hz physics tick.
- `handlePhysicsEvent` at `lobbies.go:363-394` — physics → damage broadcast.

## Where the "do not touch in prod" surface is

- Live binary: `/tmp/sfdsrv.combined` (PID owner of port 1337).
- Live source: `~/sf-multiplayer/StickFightDedicatedSrv/` on dev laptop @ `<tailnet-ip>`.
- Live replay output: `/tmp/sf-replays/` (binaries named `ABCDEF-YYYYMMDDTHHMMSS.sfreplay`).
- Live BepInEx logs on the dev laptop's Steam install: `~/.local/share/Steam/steamapps/common/StickFightTheGame/BepInEx/LogOutput.log` (currently has StickFightGym, not SFNetcodeV2 — see `BepInEx_main.log` here for verbatim).
- Mirror Steam install (with SFNetcodeV2 deployed): `~/sf-mirror-local/BepInEx/` — see `BepInEx_mirror.log` (advertises v26).

For testing, build a separate binary, run on port 1338, do **not** kill or restart the 1337 process unless the operator explicitly asks.
