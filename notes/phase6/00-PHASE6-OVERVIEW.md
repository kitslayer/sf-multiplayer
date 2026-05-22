# Phase 6 — Headless Unity as the Physics Oracle

**Goal:** replace the Go server's lightweight AABB physics simulation with a per-lobby headless Unity instance of Stick Fight itself. Real Movement.cs, real ConfigurableJoint ragdolls, real killboxes, real weapon prefabs — Unity is the source of truth for physics; Go server stays as lobby coordinator + wire-protocol relay + player matchmaking.

**Why:** comp scene needs frame-perfect physics. Path A's reimplementation in Go would take 6+ months to match SF's behavior and even then would drift on every Landfall update. Embedding the real Unity layer sidesteps the entire class of "wrong gravity / wrong AABB / wrong killbox" bugs we've been chasing.

**Trade-off accepted:** ~1+ GB RAM per concurrent lobby, single-VM hosting tops out at maybe 4–8 lobbies. Public-scale would need a fleet. For the comp scene this is fine.

## Phases

| # | Status | What | Decision gate |
|---|---|---|---|
| 6.0 | ✅ **PASSED** ([see 01](01-batchmode-nographics-vanilla.log), [02](02-batchmode-goldberg.log)) | Confirm SF can launch in `-batchmode -nographics` and progress past Steam init | Headless launch + Steam-API success required |
| 6.1 | 🟡 Next | Write `SFHeadlessHost` BepInEx plugin — detects batchmode, bypasses splash, loads a Landfall scene, starts a Server-mode NetworkSocketServer | Plugin loads a scene + MultiplayerManager spins up Server.Start() |
| 6.2 | ⏳ | Define + implement IPC bridge between Go server and SFHeadlessHost (UDP or Unix socket). Inputs in / state out. | Go-driven 2-player match with Unity as physics oracle |
| 6.3 | ⏳ | Per-lobby lifecycle: spawn/teardown Unity per lobby, restart on crash, resource caps, health checks | One Go server manages 4 concurrent oracle instances |
| 6.4 | ⏳ | Cutover: replace `physics/` package calls with bridge calls. Path A stays as fallback for un-oraclable lobbies (or removed) | Live test session matches v0.1-alpha behavior with better physics |

## Phase 6.0 — what we proved

**Experiment 1** (vanilla install, no Goldberg, no plugins):
- Unity 5.6.3p4 launched with `Forcing GfxDevice: Null` — headless mode active
- All managed assemblies loaded (Assembly-CSharp, Lidgren, etc.)
- `SteamAPI_Init() failed` — expected without Steam running
- `MultiplayerManager.Awake()` fired, threw NRE on `WeaponPickUp.Awake()` (missing prefabs because no scene loaded yet)
- Enlighten worker threads spawned — scene-load infrastructure initialized
- Killed by 25-sec timeout. Game was still running and progressing.

**Experiment 2** (sf-mirror-local with Goldberg + BepInEx + SFLevelDumper plugin):
- BepInEx 5.4.23.5 Preloader loaded
- `Received stats and achievements from Steam` — **Goldberg's SteamAPI fake works in headless mode**
- 50+ achievement-load attempts in main-menu boot sequence (harmless, just because the menu UI tries to read them)
- Game stayed running for full 30 seconds with no scene loaded — sitting in main-menu-equivalent idle state

**Verdict:** `Application.isBatchMode == true` works. Steam side works via Goldberg. The only blocker is "no UI = no scene navigation," which a BepInEx plugin can force programmatically.

## Phase 6.1 — SFHeadlessHost design (next)

Plugin name: `sf-headless-host` (BepInEx, .NET 4.6, Harmony).

**Responsibilities:**
1. On `Awake`, check `Application.isBatchMode`. If false, do nothing — plugin is a no-op in interactive runs.
2. Force-skip the splash/intro: `SceneManager.LoadScene(1, LoadSceneMode.Single)` (or whichever is the lobby scene index, derived from `notes/recon/SERVER_ARCHITECTURE.md`).
3. Wait for the scene to load (`SceneManager.sceneLoaded` event), then:
   - Find or instantiate a `MultiplayerManager` / `MatchmakingHandler`
   - Force `MatchMakingHandlerSockets.IsServer = true` (currently always false — see `notes/recon/RELATED_BUGS.md` open question 8)
   - Call `Server.Start(port)` where `port` comes from an env var (so the Go server can pick distinct ports per lobby)
4. Open the IPC socket the Go server will talk to (defer concrete protocol to Phase 6.2).
5. Heartbeat the Go server every N seconds so it knows the oracle is alive.

**Open questions for 6.1:**
- What scene index is "the lobby" in the build? Probably `0` or `1`. Verify by grepping `SceneManager.LoadScene` calls in decompiled `Bootstrap*.cs` or `MainMenu*.cs`.
- Does `MatchmakingHandler.RunningOnSockets` need to be true for `MatchMakingHandlerSockets` to take precedence in `IsServer`? Verify.
- Can `Server.Start(port)` run without a `NetworkPlayer` rig present? Or do we also need to set up a dummy player slot?

**Risk:** SF's menu code may have init steps the gameplay scene depends on (registering prefabs, loading settings, etc.). Skipping the menu may leave the gameplay scene with missing references. Mitigation: run for a few seconds, log every error, fix forward.

## Things from current Path A we keep regardless

- Go server lobby/matchmaking
- Wire-protocol relay
- All wire-format bug fixes from v0.1-alpha (flag=0 spawn, (0,*,0) sentinel guard, weapon-ID encoding, round-to-nearest, etc.)
- SFNetcodeV2 client plugin (v26 protocol, prediction)
- Replay logging (binary format extensible to capture oracle state)
- Anticheat input rate limiting + plausibility checks
- HTTP `/status`, `/lobbies`, `/maps`, `/invite` endpoints

## Things from Path A that get retired (eventually, at 6.4)

- `physics/` package server-side simulation (AABB world, swept collision, killbox)
- `Lobby.SyncableEntities` / `captureObjectSpawned` (Unity tracks objects natively)
- Server-side projectile entity spawning + ballistic integration
- The killbox-empty-in-dumped-JSON workaround (Unity has the real killboxes loaded)
