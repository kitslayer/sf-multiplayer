# 2026-05-24 (evening session) — match-flow Mono 2.0 bugs + real box blockers

> Status: **shipped + open**. The Mono 2.0 / `/start` / lobby-kill fixes are deployed in v0.3.6 client + v0.3.7 server. The two open server-side issues blocking real box-push and round-stability are documented below.

## Context

Continuation session after ALKA's v0.3.4 merge surfaced a cascade of issues during live 2-player testing. Earlier in the day we wrote five root-cause docs in `notes/bug-investigations/` for the v0.3.4 issues; this evening we deployed the fixes, hit several more Mono 2.0 / Roslyn landmines around HarmonyX dynamic methods, and turned up two concrete server-side blockers that explain the persistent "boxes feel wrong" + "round 2 broken" symptoms.

## Shipped this session

### Fix 1 — Mono 2.0 `Monitor.Enter(obj, ref bool)` in `SFClientRecon`

**File:** `sf-client-recon/SFClientRecon.cs:378`, `:399`

C# 4+ lowers `lock(x){…}` to `Monitor.Enter(x, ref lockTaken)` — the 2-arg form added in .NET 4.0. Unity 5.6.3's bundled Mono 2.0.50727 only has the 1-arg `Monitor.Enter(object)`. Both `lock(_snapLock)` blocks (snapshot RX handler + drain in `Update`) threw `MissingMethodException` on every snapshot, killing the RX thread. Because the same thread also TX'es `PktPlayerInput` (`"RX thread started (bidirectional — same socket also TXes PlayerInput)"`), inputs stopped flowing → server saw `input=0.0/s` → client visibly stuck in lobby.

**Fix:** explicit `Monitor.Enter(obj) / try / finally / Monitor.Exit(obj)`.

### Fix 2 — Mono 2.0 `Array.Empty<T>()` via HarmonyLib.Traverse

**Files:** `sf-client-recon/SfOracleLobbyConnect.cs:114`, `:198-200`, `:285` (now rewritten)

Roslyn emits `Array.Empty<object>()` for `params object[]` calls with no args. `Traverse.Create(x).Method("name").GetValue<T>()` triggers this for the empty args list — three sites used this pattern. The smoking-gun Unity stack:

```
MissingMethodException: Method not found: 'System.Array.Empty'.
at (wrapper dynamic-method) GameManager.DMD<StartMatch> (...) <0x00015>
at GameManager.NetworkAllPlayersDiedButOne (...)
at (wrapper dynamic-method) MultiplayerManager.DMD<OnMapChanged> (...)
at (wrapper dynamic-method) P2PPackageHandler.DMD<CheckMessageType> (...)
```

When the server broadcast `PktMapChange`, the chain `P2PPackageHandler.CheckMessageType → OnMapChanged → NetworkAllPlayersDiedButOne → StartMatch` ran on the client, but the StartMatch DMD wrapper crashed at IL offset 0x15. The original prefix's `Traverse.Method("IsInLobby")` was the actual emitter. Scene change never completed — client stuck in lobby UI even though the server had advanced.

**Fix:** replaced all three Traverse chains with direct `AccessTools.Method(type, name, paramTypes)` + `MethodInfo.Invoke(obj, args)`. Plus rewrote `GameManager_StartMatch_OraclePrefix` to **fully bypass** stock StartMatch (which itself probably uses Array.Empty internally — Landfall's Roslyn build) — we invoke `StartMapSequence` directly via reflection and return false.

### Fix 3 — ALKA's 12-second `RoundMinPlaySec` gate

**File:** `sf-headless-host/SFHeadlessHost.cs:97`

ALKA's v0.3.4 commit `1479440` added `internal static float RoundMinPlaySec = 12f` as "stops double MapChange / skip". The gate was checked in `TryScheduleRoundAdvance` and meant that for 12s after every map start, killing-blow damage packets were silently dropped with `"Round advance ignored: map grace Xs left"`. Stock SF rounds can end in 3–5s — every fast first-kill was being swallowed.

`_pendingRoundAdvanceAt` already dedupes within `RoundEndDelaySec` (0.5s), so the 12s gate was redundant safety. Set to `0f`.

### Fix 4 — Lobby kill auto-starts match (vanilla SF mechanic)

**File:** `sf-headless-host/SFHeadlessHost.cs` damage handler (~line 2985)

Stock SF starts the first match when one player kills another in the lobby — no separate "Ready" UI. After commit `b6a5b00`'s "manual /start only" change, the killing-blow handler in `TryScheduleRoundAdvance` bailed early on `!_matchStarted`, so lobby kills did nothing. Added a branch: if `!_matchStarted` and `dmg ≈ 666.666f`, call `FireMatchStart("lobby-kill …")` instead.

Net: hitting Quick Match in the SF UI → spawn into lobby → kill the other player → match starts automatically. No `/start` needed.

## Open — real blockers for box / round stability

### Open A — Server-side NSOs constantly fall into the void

**Evidence (oracle log this session):**
```
[P6.5] Skipping ObjectUpdate forward — Y=-35.8 out of playable range (#0)
[P6.5] Skipping ObjectUpdate forward — Y=-36.1 out of playable range (#1)
...
[P6.5] Skipping ObjectUpdate forward — Y=-50.1 out of playable range (#300)
[P6.5] Skipping ObjectUpdate forward — Y=-38.0 out of playable range (#500)
```

500+ outbound-filtered ObjectUpdates with `Y<-30` in a few minutes of play. The filter at `SFHeadlessHost.cs:597-606` drops these (added in P0-8 to avoid teleporting clients' crates to oblivion) — but the fact that the server is *generating* them at all means **server-side box physics has nothing to land on**. Either:

1. The server's headless Unity doesn't have the play-scene's floor colliders activated (additive load completes but Physics.SyncTransforms / collider registration may be incomplete in batchmode/nographics)
2. NSOs spawn at positions below the server's actual scene geometry
3. Per-map scene geometry isn't following the additive-load path correctly

Net effect: the server's authoritative box position is essentially undefined (boxes constantly falling). Clients lerp toward filtered/dropped positions or stale keyframes. **This is the actual cause of "boxes feel wrong" / "all fall straight down" with any client patch that trusts server positions.**

**Diagnosis path:**
- New `TickBoxDiagnostic` instrumentation in working tree (uncommitted) — 5s periodic dump of NSO count, Y range, void count, auth-rig position, nearest NSO. Deploy on next session.
- Scene-load summary instrumentation (also in working tree) — colliders per layer + Y range — to confirm whether server scenes ever have floor colliders.

Until this is resolved, no client-side box-physics tweak can produce vanilla-feeling push behavior.

### Open B — Auth player rig cleared on `AdvanceRound`, never re-spawned

**Evidence:**
```
[P6.9] Spawned authoritative rig for client slot=0 steamID=…   (round 1)
[SF] Round advance: cleared 1 authoritative rig(s) for next map.   (transition)
heartbeat: scene=MainScene tick=… clients=1 spawned=0 rigs=0 matchStarted=True   (round 2+)
```

No `[P6.9] Spawned authoritative rig` log after the round-advance clear. `SpawnAuthoritativePlayersForAllClients()` is gated to fire only after `_nsoInventoryDone` for the initial spawn; the round-advance path destroys rigs (`SFHeadlessHost.cs:3165`) but never re-arms the inventory→spawn timer chain, so subsequent rounds run with `rigs=0`.

Also visible: `INSTR2c: isDead=True ... willEarlyReturn=True` — the server's local Movement on the auth rig returns early after death, and there's no respawn.

**Net:** server has no player-rig for rounds 2+. No player-pushes-box happens server-side, no `mController`-driven Movement, no authoritative damage source.

**Fix sketch:** call `_authSpawnAt = Time.realtimeSinceStartup + 0.5f; _authSpawnFired = false;` (or equivalent) in `AdvanceRound` after the rig clear. Roughly 5 lines.

### Note on memory task #47 ("SOLVED: Box physics divergence")

The accompanying doc (`2026-05-24_v0.3.4-session-bugs.md` Bug F) was written but **the code change never landed**. `SmoothTowardTargets` still forces `isKinematic=true` on pushable crates, killing the `RelayPushableCrateUpdates` path (gated on `!rb.isKinematic`). Three attempts to apply the fix tonight regressed visibly (boxes falling through floor) — the root cause turned out to be Open-A above, not the client-side smoother interaction. Memory marker should be flipped from SOLVED back to OPEN, blocked on Open-A.

## Versions deployed end-of-session

| Component | Version | md5 |
|---|---|---|
| `SFHeadlessHost.dll` | 0.3.7 | `38bfc171…` (server) |
| `SFClientRecon.dll` | 0.3.6 | `932ed6f8…` (no box-fix; clean baseline after revert) |

Server runs on .115 (Proxmox VM 111). Client DLLs deployed to `~/sf-mirror-local`, `~/sf-mirror-local-p2`, `~/.local/share/Steam/steamapps/common/StickFightTheGame`.

## Suggested next-session order

1. Deploy the diagnostic-instrumented build (uncommitted in working tree) and pull a real `[BOX-DIAG]` slice
2. Open-B fix (small, contained) — re-arm auth-rig spawn after `AdvanceRound`
3. Open-A investigation — check whether scene's static colliders are actually present server-side; force `Physics.SyncTransforms()` after additive load if needed
4. Once Open-A is real (server boxes settle on platforms), revisit the SmoothTowardTargets pushable-crate exclusion — should work cleanly when server positions are sane

## Files touched

```
sf-client-recon/SFClientRecon.cs                # Monitor.Enter fix + (reverted box-smoother edits)
sf-client-recon/SfOracleLobbyConnect.cs         # Traverse → AccessTools (3 sites) + StartMatch bypass
sf-headless-host/SFHeadlessHost.cs              # RoundMinPlaySec=0 + lobby-kill auto-start + BOX-DIAG instrumentation (uncommitted)
```
