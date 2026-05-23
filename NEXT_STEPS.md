# Next steps

Where the project is and what's coming next. Last refreshed at commit `1e702b5`.

## Current state

Live server end-to-end. Steam Stick Fight (Windows or Linux/Proton) connects to a headless SF instance running [`sf-headless-host/SFHeadlessHost.dll`](sf-headless-host/) — a BepInEx + Harmony plugin that turns SF into its own v25-speaking dedicated server.

**Working:**
- Handshake, spawn, auto-load to a Landfall map
- Weapon pickup / throw / drop forwarded through SF's host-side handlers
- Death + kill propagation + round advance, full 123-map rotation
- Box pushing — via the Phase 6.7 *mirror rig* (a kinematic player rig per client that the oracle teleports to the client's reported position so it can collide with `NetworkSyncableObject`s)

**Known broken / partial:**
- Chains break with no input (runaway-fall crates from off-map hitting them; partial mitigation via NSO Y-cutoff freezer)
- Ice doesn't break from gunshots
- Boxes sometimes missing from spawn stacks (settle-phase coroutine just shipped in `1e702b5` but **untested live** — needs a reconnect)
- Per-map weapon allow-lists ignored — every map gets random-from-global
- Map-preset weapons (the ones placed in level geometry by Landfall) not synced

## End-state goal

Client-side prediction + server-side authoritative simulation + client reconciliation. Canonical CS/Valorant/Overwatch model:

1. Client runs local Movement / shoot / interact prediction so input feels instant
2. Client sends *inputs* (not positions) to server with sequence numbers
3. Server runs authoritative simulation
4. Server broadcasts snapshots at ~30Hz
5. Client compares local prediction to server snapshot; if divergent, snap-correct by replaying inputs from the snapshot's sequence forward

The current "mirror rig that teleports to client position" approach is acknowledged as a local maximum — boxes work, but the server isn't actually authoritative on anything player-driven, so cheats can fake position and ice/destructibles desync. Real prediction+reconciliation is the destination.

## Roadmap

### Phase 6.9 — real authoritative NetworkPlayer per client ✅
- Ripped the Phase 6.7 mirror rig (`SpawnMirrorRigsForAllClients`, `UpdateMirrorRigPosition`, `MakeRigKinematicMirror`) and the settle-phase coroutine
- Renamed → `SpawnAuthoritativePlayersForAllClients` + `ConfigureAuthoritativeRig`
- Per-instance `Controller.mHasControl = true` on each spawned rig (server-side authority)
- `HandlePlayerUpdate` is now pure relay; no longer drives anything server-side

### Phase 6.10 — server snapshots ✅
- New v26 msgType `PktWorldStateSnapshot` (39)
- Server broadcasts at 30Hz to all spawned clients on v26 port (1339)
- Wire format: `u32 serverTick, u8 playerCount, [u8 slot, f32 x, f32 y, f32 z] × N`
- Stock clients ignore msgType 39 (their `MsgType` enum stops at 38) — safe to ship before client plugin lands

### Phase 6.11 — client reconciliation ✅ (snap, no smoothing yet)
- New BepInEx plugin: [`sf-client-recon/`](sf-client-recon/) shipped to each player's `<SF install>/BepInEx/plugins/`
- Binds UDP 1339, parses incoming `PktWorldStateSnapshot`, snap-corrects local `NetworkPlayer` position to server's authoritative view
- Phase 6.11.2 (next): replace instant snap with smooth interpolation over ~100ms
- Phase 6.11.3 (later): also correct OTHER players' positions (currently they're still driven by forwarded `PlayerUpdate`)

### Phase 6.12 — input prediction + reconciliation replay (next up)
- Define v26 `PktPlayerInput` (msgType 40): `{ u32 sequenceNum, f32 stickX, f32 stickY, f32 aimX, f32 aimY, u32 buttons }`
- Client `SFClientRecon` plugin sends every fixed-update tick (or on input change) to the oracle on its v26 port
- Oracle parses inbound `PktPlayerInput`, populates the existing `SlotInputs` buffer — at which point the spawned authoritative rig's Movement.cs starts producing real authoritative positions instead of staying at spawn
- Oracle tags each outgoing snapshot with `lastInputSeq` (last sequence number consumed for that slot)
- Client maintains a sequence-tagged input ring buffer; on snapshot arrival, if local predicted position at sequence N differs from server position at sequence N beyond a tolerance, the client replays buffered inputs from N to current — the canonical CSGO rollback model

### Phase 6.13 — World shards (multi-lobby) ✅ v1 + ✅ v1.5
- v1 (shipped 2026-05-23): multi-process — each lobby is its own Proton+SF oracle on its own UDP port, fully isolated. Driven by [`launch-lobby.sh`](launch-lobby.sh), [`stop-lobby.sh`](stop-lobby.sh), [`list-lobbies.sh`](list-lobbies.sh), [`stop-all-lobbies.sh`](stop-all-lobbies.sh).
- v1.5 (shipped 2026-05-23): lobby browser endpoint — [`serve-lobbies.py`](serve-lobbies.py) exposes the registry as JSON at `GET /lobbies`. Any server browser (in-game mod, web UI) can poll. Filters out stale entries.
- v2 (design only): in-process — N additive scenes at Z-offset in one SF process, per-shard state isolation, Harmony dispatch on SF singletons. See [`notes/phase6/12-PHASE6.13-sharding.md`](notes/phase6/12-PHASE6.13-sharding.md) for the design + roadmap.

### Phase 6.14 — Server-authoritative NSO positions ✅ (v0.1)
- Server includes NSO positions in `WorldStateSnapshot` (msgType 39)
- Client snaps local NSO transforms to server values
- Next: smoothing/interpolation between snapshots (6.14.1)

### Phase 6.14.5 — Server rewind buffer (lag-comp) — design only
- See [`notes/phase6/13-rewind-buffer.md`](notes/phase6/13-rewind-buffer.md)
- CSGO-style 100ms tick history; server rewinds positions to validate damage events
- Gated behind anticheat-lite (slot↔SteamID validation, weapon plausibility) shipping first

### Phase 6.15 — Chat-command admin interface — design only
- See [`notes/phase6/14-chat-commands.md`](notes/phase6/14-chat-commands.md)
- Patched DLL already emits `/start`, `/code`, `/newlobby`, etc. as `PktPlayerTalked` on owner-channel `(slot*2)+3`
- Server side: parse → dispatch → send chat response
- Half-day effort for v1 commands (`/start`, `/code`, `/newlobby`); `/join` waits on v2 in-process sharding

### Phase 6.16+ — broader authority
- Server-authoritative damage / hit registration (server validates damage events vs position+weapon)
- Server-authoritative destructibles full-physics (server simulates shards too, not just NSO parents)
- Server-authoritative weapon spawns with map-preset weapons + per-map allow-lists (Phase 6.8 task #34)
- Anticheat — promote observer to actively rate-limit + slot ↔ SteamID validation
- Workshop maps loaded at runtime (not just pre-dumped Landfall maps)
- Client-side smooth interpolation on snapshot apply (Phase 6.11.2)
- Input prediction replay rollback (Phase 6.12.2)

## Where to start reading

- [`sf-headless-host/SFHeadlessHost.cs`](sf-headless-host/SFHeadlessHost.cs) — the live plugin (~3300 lines). Key entry points: `Awake()` (Harmony patches), `HandleClientRequestingToSpawn`, `HandlePlayerUpdate`, `SpawnMirrorRigsForAllClients`, `TrySpawnPlayer`
- [`notes/phase6/10-PHASE6.5-host-side-gameplay.md`](notes/phase6/10-PHASE6.5-host-side-gameplay.md) — current host-side patch set + rationale
- [`notes/phase6/11-PHASE6.6-pickup-and-physics.md`](notes/phase6/11-PHASE6.6-pickup-and-physics.md) — pickup forwarding + diagnosis of why boxes initially didn't move
- [`refs/decompiled/Assembly-CSharp/MultiplayerManager.cs`](refs/) — host-side dispatcher
- [`refs/decompiled/Assembly-CSharp/NetworkSyncableObject.cs`](refs/) — the sync pattern (good reference for snapshot design)

## How to test locally

Server side:

```bash
SFHEADLESS_BRIDGEPORT=1341 SFHEADLESS_PORT=1337 SFHEADLESS_DEBUG=1 \
  bash launch-sf-headless.sh
```

Client side — Steam Stick Fight launch options:

```
WINEDLLOVERRIDES="winhttp=n,b" %command% -address 127.0.0.1 -port 1337
```

Logs:
- `$SF_MIRROR/BepInEx/LogOutput.log` — plugin output
- `/tmp/sf-oracle-unity-1341.log` — Unity log (use this for SF's own diagnostics)

The `-address` / `-port` flags are read by a patched `Assembly-CSharp.dll` shipped with the client (not in this repo for copyright reasons). The patched DLL has no embedded IP; everything comes from the CLI flags.
