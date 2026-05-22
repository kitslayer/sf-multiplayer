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

### Phase 6.9 — real authoritative NetworkPlayer per client (next up)
- **Rip** the mirror rig (`SpawnMirrorRigsForAllClients`, `UpdateMirrorRigPosition`, `MakeRigKinematicMirror`) and the just-shipped settle-phase coroutine
- **Spawn** a real NetworkPlayer per connected client on the oracle via `Instantiate(playerPrefab)` (`TrySpawnPlayer` is a starting point — it already binds CharacterActions via `TakeLocalControl`, but needs adjusting to behave as the server-authoritative copy)
- **Patch** `Controller.HasControl` / `NetworkPlayer.IsLocallyControlled` → `true` for all oracle-side rigs via Harmony postfix
- **Drive** each rig's Movement.cs from a per-slot input buffer — the `SlotInputs` infrastructure + `InjectInputPrefix` on `Controller.Update` already exist in the plugin

### Phase 6.10 — server snapshots
- Server broadcasts authoritative position/velocity for every entity (players + NSOs) at ~30Hz
- Reuse existing `PlayerUpdate` (msgType 10) carrier for player positions; broadcast to **all** clients including sender (not just others, as currently)
- Existing NSO `ObjectUpdate` (msgType 26) broadcast already covers boxes/barrels

### Phase 6.11 — client reconciliation
- Client-side Harmony patch (extend the patched `Assembly-CSharp.dll` or add a small new BepInEx plugin shipped alongside): on incoming `PlayerUpdate` for the client's *own* slot, snap-correct local position smoothly over ~100ms
- Local player keeps running Movement (= prediction); server's position is final

### Phase 6.12 — input prediction + reconciliation replay
- Define v26 `playerInput` msgType: `{ u32 sequenceNum, float stickX, float stickY, float aimX, float aimY, u32 buttons }`
- Client sends every fixed-update tick
- Server processes inputs in order; tags each outgoing snapshot with the last-acked sequence
- Client maintains an input ring buffer; on snapshot arrival, if local position at sequence N differs from server position at sequence N beyond tolerance, replay buffered inputs from N to current

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
