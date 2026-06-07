# Status for Miles (read first when you're back)

> ⚠️ **Superseded (2026-06-06).** Frozen at the Phase 6.3 era (branch `phase-6-headless` is long gone) — current state: [`NEXT_STEPS.md`](../../NEXT_STEPS.md). Live server: `69.53.117.43`.

**Branch:** `phase-6-headless` on https://github.com/kitslayer/sf-multiplayer  
**Main is at v0.1-alpha** — last "stable" point before the headless pivot.

## TL;DR

Headless SF runs as a real physics oracle now. Concretely:

```
python: {"cmd":"spawnPlayer","slot":0}
oracle: {"reply":"ack","ok":true}
python: {"cmd":"snapshot"}
oracle: {"reply":"snapshot","tick":284,"scene":"MainScene",
         "ents":[{"slot":0,"x":0.000,"y":8.000,"z":0.000}]}
```

That's a **real Unity-managed player rig**, in a **real SF scene**, reporting its **real Unity transform position** through our bridge. The hard architectural unknown — "can we run SF headlessly as a physics oracle?" — is **proven yes**.

## What works end-to-end

1. **`-batchmode -nographics` SF launches** (Goldberg shims Steam, BepInEx 5.4 loads, Harmony patches apply).
2. **`SFHeadlessHost.dll` plugin** auto-detects batchmode and bootstraps:
   - Patches the hardcoded port 1337 → `$SFHEADLESS_PORT` (1340 default).
   - Loads a scene via `SceneManager.LoadScene(N)`.
   - Opens a JSON UDP bridge on `$SFHEADLESS_BRIDGEPORT` (1341 default).
   - Streams snapshots at 30Hz to whoever pinged it.
3. **Go `oracle` package** (in `StickFightDedicatedSrv/oracle/`):
   - `oracle.Spawn(cfg)` launches the headless process via `launch-sf-headless.sh`, waits for the bridge to respond, returns a handle. ~8 seconds.
   - `oracle.Snapshot()` returns the most recent state.
   - `oracle.LoadMap(N)` switches scenes (tested: Desert3 ↔ Castle5 works).
   - `oracle.Ping()` health check.
   - `oracle.Kill()` tears down the process tree (sometimes leaves a wineserver — see "Known issues").
4. **`cmd/oracle-test`** smoke binary exercises all of the above. Just `go run ./cmd/oracle-test` from `StickFightDedicatedSrv/`.
5. **Bridge `spawnPlayer` command** instantiates a Player rig via `ControllerHandler.playerPrefab`. The rig shows up in the snapshot.

## What's NOT done yet (the last 20% for Phase 6.3)

- **Map geometry under the player.** Right now we spawn the rig in MainScene (where ControllerHandler lives) but that's the menu/title scene — no platforms. To get falling-onto-platforms behavior we either (a) load Landfall scene additively after spawn, or (b) work out how SF normally migrates the player rig across map changes and replicate it. ~30 min experiment.
- **Input injection.** Player rig sits there idle. No way for our Go server to make it move yet. Need a Harmony override on `Controller.Update` that reads from a bridge-managed input buffer instead of InControl. ~2 hours of patching + testing.
- **Go-side Lobby integration.** The `Lobby` struct doesn't yet spawn an oracle on creation. The `oracle` package is standalone — wire it into lobbies + retire the `physics/` package's player tick. ~1 day of refactoring.

## Architecture decision you should weigh in on

While trying to have a real client connect to the oracle's game port, I hit a fundamental architecture choice: **the patched DLL (which our existing clients run) speaks raw UDP. The oracle's hosted server speaks Lidgren.** They can't talk directly.

I wrote up four options in `notes/phase6/09-PHASE6.3-BLOCKER-AND-OPTIONS.md`. My recommendation is **Option D** (oracle is a pure physics-oracle service that Go drives via the bridge; clients still connect to Go on 1337 as today). That's what the current code is moving toward.

Other options worth considering:
- **Option A** (patch the oracle's NetServer to speak raw UDP) — heavy Harmony work but eliminates the bridge.
- **Option C** (clients use stock unpatched DLL, connect directly to oracle) — throws away most of our centralized-server work but is conceptually cleanest.

If you want a different option, say so before I dive into wiring Option D into Lobby.

## How to play with what's working now

On the dev laptop:

```bash
# Build everything
cd ~/sf-multiplayer
ssh root@<vm> "cd /tmp/sf-headless-host && dotnet build -c Release"
scp root@<vm>:/tmp/sf-headless-host/bin/Release/SFHeadlessHost.dll \
    ~/sf-mirror-local/BepInEx/plugins/SFHeadlessHost.dll
cd StickFightDedicatedSrv && go build -o /tmp/oracletest ./cmd/oracle-test

# Run end-to-end
pkill -9 -f "StickFight\|wineserver"  # nuclear cleanup first
sleep 3
/tmp/oracletest -secs 30 -loadAfter 26    # spawn, snapshot, swap to Castle5
```

Or talk to the oracle yourself via netcat / python after launching it manually:
```bash
echo '{"cmd":"snapshot"}' | nc -u -w1 127.0.0.1 1341
```

## Known issues to be aware of

1. **Ghost wineserver processes.** When `oracle.Kill()` runs, Proton's wineserver sometimes survives and holds ports 1340/1341. Symptom: next launch's HostServer() + Bridge bind both fail with `SocketException: Error looking up error string`. Workaround: `pkill -9 -f "wine"` between runs. Permanent fix needs `oracle.Kill()` to walk the wineprefix's process tree and kill all wine* PIDs.

2. **Bridge logs interleave when multiple SF processes are alive.** Both write to the same `BepInEx/LogOutput.log`. If you see "heartbeat: scene=Desert3" mixed with "heartbeat: scene=MainScene", you've got two oracles running — nuclear cleanup needed.

3. **Mono 2.0 compat in BepInEx plugins.** Unity 5.6 `-batchmode` runs a stripped Mono runtime. The compiler-emitted `Type::op_Equality` etc. don't exist there. Every `Type/MethodInfo/PropertyInfo != null` in plugin code must be cast through `(object)` to force reference comparison. All the patterns are caught in `SFHeadlessHost.cs`. Watch for this in any new plugin code.

4. **Personal-info scrub on `main`.** Both `main` and `phase-6-headless` have the personal-info scrub. The `v0.1-alpha` tag was created BEFORE the scrub — old paths still visible at that tag specifically. Most viewers will see `main` or `phase-6-headless` so it's fine, but worth knowing.

## Commits this session (in order)

- `12801bc` Initial commit (main branch)
- `5af3278` phase 6.0 batchmode feasibility + docs
- `2312104` scrub personal info (and a separate cherry-pick onto main)
- `5d45353` phase 6.1: SFHeadlessHost plugin loads + hosts
- `8308ebd` phase 6.2: oracle UDP bridge — ping, snapshot stream, loadMap
- `b2ba4b1` phase 6.2 Go side — oracle.Spawn, Snapshot, LoadMap, Kill
- `ea2665c` phase 6.3 partial: bridge accepts spawnPlayer
- `fc3c2a7` phase 6.3: oracle ents:[] → ents:[{slot:0,x,y,z}] — first player rig

## What I'd do next session

1. **Sub-option C of Phase 6.3:** make TrySpawnPlayer instantiate the player prefab even in scenes without ControllerHandler (load Landfall + scene-load callback that spawns + parents to a synthetic ControllerHandler if needed). Then we have a player rig falling onto map geometry.

2. **Input override:** Harmony-patch `Controller.Update` (or `Movement.Update`) to read `stickX/stickY/buttons` from a static dict that the bridge populates on `{"cmd":"applyInput",...}` packets. Verify the rig moves.

3. **Wire `Lobby` to oracle.** On lobby creation, spawn an oracle; on player join, send `{"cmd":"spawnPlayer","slot":N}`; on `playerInput` from a client, send `{"cmd":"applyInput",...}`; broadcast snapshots back as `worldStateSnapshot`. Retire `physics/`'s player tick.

4. **Persistent `oracle.Kill()` cleanup** — track the wineprefix path and `pkill -9 -f $PREFIX` in Kill.

That's ~1-2 focused sessions to a working Option-D end-to-end where lobby gameplay is real-SF-physics from the oracle. Then it's polish: map-change handling, multi-oracle-per-host, etc.
