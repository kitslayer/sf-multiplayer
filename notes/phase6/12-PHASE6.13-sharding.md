# Phase 6.13 — World shards (multi-lobby on one server)

> ⚠️ **Historical (2026-05-23).** Phase design doc. Current state: [NEXT_STEPS.md](../../NEXT_STEPS.md).

**Status:** v1 (multi-process) shipped 2026-05-23. v2 (in-process) is design-only.

## Two approaches

### v1: multi-process — SHIPPED

Each lobby is a separate Proton+SF oracle process with its own UDP port, wineprefix, and log file. Fully isolated: no shared state, no MM multiplexing, no per-shard routing layer. Driven by [`../../launch-lobby.sh`](../../launch-lobby.sh), [`../../stop-lobby.sh`](../../stop-lobby.sh), etc.

Pros:
- Architecturally trivial — every lobby is the same single-match Path A we've been building since 6.0.
- Crash isolation: one bad lobby doesn't take down the others.
- Per-lobby resource accounting (`top` shows you exactly who's using what).

Cons:
- ~500MB RAM + 1 CPU core per lobby. 10 lobbies ≈ 5GB + 10 cores.
- One Proton/wine boot per lobby (≈8s startup latency).
- Multiple Goldberg instances, each binding their own Steam-emu state.

For the comp scene's foreseeable scale (a handful of concurrent lobbies on a hobby VPS), v1 is the right answer. The cost only becomes painful at 20+ concurrent lobbies.

### v2: in-process — DESIGN ONLY

ALKA's `WORLD_SHARDS.md` describes the target: one SF process, N additive scenes at `shardId × 5000` Z-offset, per-shard state isolation. His own implementation only does the **scene** part — `applyInput` and snapshot routing are marked "próximo: scoped por shard" (i.e. not done).

The hard problem isn't scenes — it's SF's singletons.

## The singleton problem

Stick Fight assumes one match per process. The host-side logic lives on globals:

- `MultiplayerManager.Instance` — match state, player list, weapon spawn timers, broadcast dispatch
- `GameManager.Instance` — round flow, kill detection, map cycle
- `MatchmakingHandler.Instance` — `IsServer`, `IsNetworkMatch`
- `ControllerHandler.Instance` — player rig spawning
- `WeaponSelectionHandler.Instance` — weapon-spawn random selection

For each to work per-shard, we need either:

**Option A — multiple instances + Harmony getter dispatch.** Spawn N instances of each singleton at scene-load time (one per shard's scene root). Harmony-patch every `*.Instance` getter to look up "current shard context" and return the right instance.

The current-shard context has to be set *before* SF code runs on behalf of a shard. Concretely:
- Inbound packet handlers (e.g. `HandlePlayerInput`) set `CurrentShard = lookup(client.LobbyCode)` at entry, clear at exit.
- Per-shard rigs' `Controller.Update` calls also set the context for that frame.
- Background updates (FixedUpdate on Movement/Animation) implicitly run per-rig, so each rig's Update should set the context for its own subtree.

The hard part: SF's code doesn't expect Instance lookups to ever change mid-call. If a method calls `MultiplayerManager.Instance.A()` and then `GameManager.Instance.B()`, both must resolve to the same-shard instance. As long as `CurrentShard` is stable during a single packet handler / single FixedUpdate frame for a rig, this works. Probably 50–100 Harmony patches.

**Option B — global singleton, per-shard state on the side.** Keep the global singletons; intercept their methods to multiplex by lobby. Every call into MultiplayerManager would check `CurrentShard.PlayerList` instead of the global one. Requires patching *every* method that touches global state, which is much more surface than Option A.

Option A is the practical answer.

## Per-shard state we need regardless

Even before tackling the singleton question, four things need to be shard-scoped on Path A:

1. **`SlotToRig`** — currently `Dictionary<int, GameObject>`. Becomes `Dictionary<(lobbyCode, slot), GameObject>`.
2. **`SlotInputs`** — currently `Dictionary<int, InputFrame>`. Same lift.
3. **`_sfClients`** — already keyed by `from.ToString()`. Add `LobbyCode` field to `SfClient` (set during a new `ClientRequestingLobby` handshake step that comes before `ClientRequestingIndex`).
4. **Snapshot + broadcast routing** — `BroadcastWorldStateSnapshot` already iterates `_sfClients`; gate by `cli.LobbyCode == shard.LobbyCode`. Same for `ForwardBroadcastToV25Clients`.

These four are doable in a day or two, and they're a strict prerequisite for v2. Worth doing even before the singleton work, because they let multi-process v1 also benefit (e.g. shared lobby browser UI).

## Roadmap

- **v1 (shipped)** — multi-process, launch-lobby.sh family.
- **v1.5** — lobby browser endpoint (HTTP `/lobbies` JSON listing) so a real launcher can pick from running lobbies. Add to the headless plugin or a tiny separate Go process; either way ~50 lines.
- **v2.0** — per-shard state isolation (SlotToRig, SlotInputs, _sfClients, snapshot routing keyed by lobby code). Foundation for v2.1.
- **v2.1** — scene-level sharding (ALKA's `WorldShardManager` equivalent: additive load + Z-offset).
- **v2.2** — Harmony dispatch on SF singletons (`Instance` getters route to per-shard instance based on `CurrentShard`).
- **v2.3** — performance: shared PhysX broadphase across shards, single FixedUpdate loop driving N MultiplayerManagers. Probably end-state for resource-efficient hosting.

## Why we're confident v1 is enough for the comp scene

- 1v1 / 2v2 SF matches; typical concurrent active lobbies in the comp Discord measured in low single digits during peak.
- VPS pricing at the resource level: 8GB / 4 cores ≈ $20/month gets us 8 concurrent v1 lobbies, which is plenty for comp testing and casual ramps.
- v1's crash isolation is genuinely valuable during the alpha. If MultiplayerManager dies in one lobby due to an unexpected NRE, the others keep playing.

We can build v2 *if and when* resource scaling actually matters. Until then, v1 multi-process is the right shape.
