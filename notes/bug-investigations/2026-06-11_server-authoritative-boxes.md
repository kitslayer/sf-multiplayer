# 2026-06-11 — Server-authoritative boxes with client-side prediction

**Status:** implemented (host 0.4.0, client 0.6.0, box-fix 0.3.0); local 2-client verification in this doc's last section.

## The problem

Boxes behaved differently on every client. Root cause was architectural, not a bug:

- Each client ran **pure local physics** on pushable crates — `LerpLocalDummy` patched off, `mIsListening=false`, and `SmoothTowardTargets` skipped pushables entirely. **No server position was ever applied to a pushable crate on any client.**
- Each client relayed its own crate positions up at 5Hz (msg 26), and the host applied **both** clients' relays to its own scene via `ApplyClientObjectUpdate` — last-writer-wins. With two clients, the server's "truth" ping-ponged between two divergent sims.
- Net effect: A's pushes never appeared on B, crates settled in different places per client, and server-side hit detection used an oscillating mix.

The relay also meant **any client could teleport any crate** (msg 26 had zero validation) — an issue-#2-family hole.

## The architecture now (v26.7)

**The oracle's Unity sim is the single authority for pushable crates.**

- Host drops client ObjectUpdates (`SFHEADLESS_ACCEPT_CLIENT_CRATES=1` restores legacy).
- Clients influence crates only through (a) their player rig — `playerUpdate` teleports the server-side ghost rig, which physically collides with oracle crates — and (b) weapon fire: the server's virtual projectiles now shove the crate they hit (`ApplyBulletCrateKick`, +2.5 m/s mass-independent, governed by SFBoxFix's velocity cap). Explosive wall-hits already applied `AddExplosionForce` server-side.
- Clients keep crates **dynamic locally** (instant push feel) and a new reconciler (`ReconcilePushableCrates`, FixedUpdate) continuously steers them toward the latency-compensated server pose.

### Why the May reconciliation attempts failed, and the countermeasures

| May failure | Countermeasure |
|---|---|
| Compared local state against the raw last snapshot (RTT-stale) → every moving crate looked wrong, corrections yanked it backward mid-push | Error is measured against the server pose **extrapolated by the server's own velocity** (estimated from consecutive snapshots, capped at 0.18s) |
| Position writes on dynamic bodies → penetration injection → solver explosions | Corrections **steer velocity only**: `rb.velocity = Lerp(rb.velocity, serverVel + err·k, blend)`; position is written only in the rare hard-snap |
| Constant micro-corrections → jitter, creep, "se deslizan" | **Deadband** (0.20u): agreeing sims get zero injected motion; at-rest server pose zeroes residual local creep |
| Corrections fought the player's own push | **Touch grace**: blend ×0.25 while any player rig is within 1.5u |
| Warps swept crates through ice/chains → spurious destructions | Hard-snap (>1.8u) zeroes velocities and marks the root in `_recentLerpAt` so the P0-15 guard suppresses destruction events |

### Ground-truth discovery: the rotation axis was wrong on the wire AND on the server

From the decompile (`NetworkSyncableObject.cs:498-512`, `LerpLocalDummy` :270-274) and `sf-docs/04`:

- Stock SF syncs NSO rotation as **the up-vector's (y,z)** (`ShortVector2(transform.up)`) and reconstructs `Quaternion.LookRotation(Cross(Vector3.right, up), up)`. The one real, network-visible rotation axis is **world X** — the up vector tilting within the Y-Z play plane.
- Our v26 NSO snapshot carried `eulerAngles.z`, which is ≈0 for a crate tipping about X. **Tipped crates were unrepresentable on our wire.**
- SFBoxFix's constraint mask froze X+Y and freed Z ("the wire syncs rotZ") — built against our own wrong field, not vanilla. The server tipped crates about the *into-the-screen* axis; clients tip about X. Under reconciliation that's permanent orientation divergence, so it had to be fixed at the source.

Fixes:
- **v26.7 wire appendix**: `[u16 count][u16 id, f32 upY, f32 upZ]×count` appended after the mapState section (the established append-only compat pattern — deployed 0.5.x clients ignore trailing bytes). Clients reconstruct rotation exactly like stock.
- **Unified constraint mask** (SFBoxFix `ApplyCrateConstraints` ↔ client `ApplyCrateConstraintMask`): free X (tip axis), freeze Y (yaw — unsyncable, locked identically on both sims) and Z (vanilla prefabs ship Z frozen).
- SFBoxFix `OverhangAssist` probes moved from X (the depth axis — testing an edge nobody can fall off) to Z, torque now about X.

### Tuning unification

Client `ConfigureCratePhysics` and server SFBoxFix had drifted (client mass 36 / dual materials 0.26+0.42 / solver 8-5 / CoM 0.58 / caps 2.5-13 vs server 45 / single 0.40 / 10-6 / 0.55 / 6-14). Under prediction any gap is a systematic error the reconciler must keep correcting. The client now mirrors SFBoxFix v0.3.0 exactly (mass 45, CoM 0.55, drag 0.12/0.35, solver 10/6, single CrateVanilla material, maxAngular 10, caps 6/14, Continuous collision on both). **Change values in both places or not at all.**

Client-only injected torques are gated out of reconcile mode (air tumble) — the server's real tumble arrives via the up-vector now. The bigger client behavior suite (stack pop, fall tumble, overhang, void rescue in `ApplyStackAndContactBehavior`) was already dead code (never invoked).

## Modes

| Mode | How | Behavior |
|---|---|---|
| **Default** | — | Predicted local physics + reconciliation; relay off; server authoritative |
| Legacy | `SF_CRATES_LOCAL_PHYSICS=1` (client) + `SFHEADLESS_ACCEPT_CLIENT_CRATES=1` (host) | The old pure-local + 5Hz relay world, for A/B comparison |
| Stock-follow | `SF_CRATES_SERVER_AUTHORITATIVE=1` (client) | Crates kinematic, stock lerp from host ObjectUpdate broadcasts (debug) |

## Diagnostics

- Client `[BOX-SYNC] crates=N meanErr=… maxErr=… hardSnaps=…` every 5s — the objective convergence metric. Healthy: meanErr < 0.1 idle, < 0.5 during fights; hardSnaps not climbing.
- Host `[BOXES] Dropped client ObjectUpdate #N` — legacy relays being dropped (expected from 0.5.x clients).
- Host `[P6.17] Bullet crate-kick #N` — server-side bullet shoves.

## Known limitations / follow-ups

- Grenade/explosive **projectile detonations** apply server-side blast via `ApplyExplosiveBlastAt` only on wall hits of explosive-classified virtual projectiles; other explosion paths (e.g. SF's own explosion logic on the oracle) need live verification. If crates don't blast server-side in some path, the local blast shows and crates ease back to server truth — the follow-up is relaying the explosion event into a server-side `AddExplosionForce`.
- Ghost-rig pushes are teleport-driven on the server vs force-driven prediction locally; magnitudes can differ. Touch-grace + soft zone absorb it; `ReconGain`/`ReconDeadband` are the live-tunables.
- Yaw (rotation about Y) is frozen on both sims — vanilla allows it but cannot sync it; locking it is strictly better for convergence and looks identical from the side-on camera.

## Local 2-client verification (2026-06-11) — INCOMPLETE, resume before .115 deploy

Four live rounds with kit (laptop oracle + 2 Goldberg mirrors). Each round exposed a real defect (all fixed in this commit):

| Round | Telemetry | Defect found |
|---|---|---|
| 1 | idle: meanErr 0.06u / 0 snaps ✅; pushing: maxErr 1.6u, 5 hard snaps | ghost-rig kinematic sweep = infinite-mass push → `GhostPushCrateCap` |
| 2 | meanErr ~0.4 flat across the field, never recovering | friction eats velocity corrections on resting crates → settled-divergence position resolution |
| 3 | crates=162 on a 90-crate map; meanErr=40.0; hardSnaps +hundreds/5s | NSO id collisions across coexisting map scenes → scene filters + transition suppression + identity gate |
| 4 | oracle `[BOX-DIAG] void=107/163, y=[-278.9,…]`, cycling every round | stock off-screen cull de-colliders the ORACLE's crates below y=-11 → headless cull kill |

**Verified:** idle convergence, wire compat (0.5.x client vs 0.4.0 host), all patches install (`Patched IgnorePlayerWhenOffScreen.Update (headless cull kill)`).

### Evening session (same day) — pipeline VERIFIED WORKING live

After five more root-caused fixes (commits `689791f`…`f93f8c1`), sustained healthy play across many rounds and transitions: **meanErr 0.06–0.10, maxErr <0.2, hard-snap counters frozen between rounds, void=0 on live maps**; combat envelope meanErr ~0.5 / maxErr ~2 with quick recovery (explosion parity is the known residual). First live bullet crate-kick fired. The fixes, each evidence-driven (wire sniffer / live debug console / console tees):

1. **FindLocalSlot** (3 iterations): every client claimed slot 0 → slot 0's v26 endpoint flip-flopped 40×/s and slot 1's snapshots went to dead port :1339 (seen on the wire). Final: `NetworkPlayer.mHasLocalControl` → `Controller.playerID`; `mLocalPlayerIndex` only if nonzero (patched DLL never sets it in oracle mode); Controller path gated to offline; order-dependent fallback deleted.
2. **Oracle map-scene tracking** via `sceneLoaded` hook: stale-settle-skip rounds left all NSO caches filtered to the previous map (authority broadcast old-scene crates → uniform 22–37u client errors; debug console caught "0 pushable crates" mid-match).
3. **Client NSO cache scene filter** (mirror of the oracle's) + **grounded rescue gate**: a >8u correction is accepted only when our own crate fell below y=-20 — time-based acceptance had mass-teleported fields toward the previous map's layout.
4. **NRE storm source-fix**: null P2PPackageHandler channel slots filled with empty queues (~150 NREs/s on every client, chronic FPS tax). Harmony-patching `IsPacketAvailable` on clients is FORBIDDEN (suspected packet-pump interference; see commit history).
5. **Server bullet damage → SHADOW** ("fake hits"): the swept-sphere hit test against RTT-lagged ghost rigs emitted PlayerTookDamage on client-side misses — particles without HP change. Bullets now follow the throw-auth shadow pattern; `SFHEADLESS_BULLET_DAMAGE=1` re-enables.

**Tooling added** (made the above findable): per-instance Unity console tees (`/tmp/sf-console-<port>.log`, timestamped), file-driven live debug console (`/tmp/sf-cmd-{1340,1342,oracle}.txt` → `boxes | box <id> | rigs`), `Mono2Polyfills.cs` in the host (kills the per-boot TypeLoadException).

**Operational rules learned (also in private memory):** verify builds with "Build succeeded" + `strings -el` (UTF-16) probes of log literals — a failed build once shipped a stale DLL silently; after kills, verify port ownership with `ss -ulpn` (zombie wineservers share UDP ports and eat handshakes); clients need a human menu-click to connect (auto-connect's log lies) and do NOT re-handshake after an oracle restart.

**Still open before .115:** explosion→crate parity (local blasts vs server); crate-kick classification uses `transform.root` (map root — should classify the rb's own subtree); pre-connect Controller slot-0 window (cosmetic); SFBoxFix dead code strip; server-browser local test (needs `SF_LOBBY_ENDPOINT=http://…:8080` on clients + `serve-lobbies.py` + `SF_LOBBY_CODE=MAIN` on the oracle — researched, not yet run); late-join keyframe test.
