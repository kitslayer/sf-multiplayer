# Phase 6 — Headless Unity as the Server

> ⚠️ **Partly superseded (2026-06-06).** Original Phase-6 design doc (the Path-A pivot below did happen and shipped). Current state: [`NEXT_STEPS.md`](../../NEXT_STEPS.md).

> **2026-05-22 path update:** the project pivoted from "oracle as physics consultant + Go server in the data path" (the original Phase 6 design, sometimes called Path D below) to **"oracle IS the server, no Go bridge"** (Path A). The headless Unity instance now speaks the v25 protocol directly to real Steam clients via raw UDP. The Go dedicated server is no longer in the active data path. See [`10-PHASE6.5-host-side-gameplay.md`](10-PHASE6.5-host-side-gameplay.md) for what's live and the seven Harmony patches that make it work.

**Goal:** run a headless Unity instance of Stick Fight itself as the multiplayer server. Real Movement.cs, real ConfigurableJoint ragdolls, real killboxes, real weapon prefabs — Unity is the source of truth for physics AND the network endpoint.

**Why:** comp scene needs frame-perfect physics. Reimplementing SF's physics in Go would take 6+ months and drift on every Landfall update. Embedding the real Unity layer sidesteps the entire class of "wrong gravity / wrong AABB / wrong killbox" bugs we've been chasing.

**Trade-off accepted:** ~1+ GB RAM per concurrent lobby, single-VM hosting tops out at maybe 4–8 lobbies. Public-scale would need a fleet. For the comp scene this is fine.

## Phases

| # | Status | What | Decision gate |
|---|---|---|---|
| 6.0 | ✅ **PASSED** ([see 01](01-batchmode-nographics-vanilla.log), [02](02-batchmode-goldberg.log)) | Confirm SF can launch in `-batchmode -nographics` and progress past Steam init | Headless launch + Steam-API success required |
| 6.1 | ✅ Done | `SFHeadlessHost` BepInEx plugin — detects batchmode, bypasses splash, loads MainScene | Plugin loads, Bep banner appears |
| 6.2 | ✅ Done | Go ↔ oracle bridge JSON command channel on UDP 1341. Originally the Path-D IPC; retained for diagnostic ping/snapshot/teleport, now loopback-only after security review | spawnPlayer + loadMap + ping roundtrip from `cmd/oracle-test` |
| 6.3 | ✅ Done | Input injection into SF's `Controller.Update` via Harmony prefix, with reflection-driven CharacterActions write — see [09-PHASE6.3-BLOCKER-AND-OPTIONS.md](09-PHASE6.3-BLOCKER-AND-OPTIONS.md) for the saga | Bridge-driven rig moves in oracle |
| 6.4 | ✅ Done — **pivot from Path D to Path A** | Embed sfdsrv's v25 raw-UDP protocol directly inside `SFHeadlessHost.dll`. Real Steam SF connects to oracle:1337, handshake completes, client spawns into Desert3 | Live test: Steam SF joins + auto-loads map + sees real geometry |
| 6.5 | ✅ Done | Drive SF's own host-side gameplay loop on the oracle via 7 Harmony patches (IsServer, IsNetworkMatch pin, GameManager.StartMatch, SpawnRandomWeapon replacement, etc.) | First WeaponSpawned packet forwarded through plugin's v25 socket → client renders weapon |
| 6.6 | ⏳ Next | Forward incoming gameplay packets (msgType ≥ 11, e.g. ClientRequestingWeaponPickUp, PlayerTookDamage) into SF's `P2PPackageHandler.CheckMessageType` so pickup / damage / physics interactions work | User can pick up + use weapons; killboxes register |

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
