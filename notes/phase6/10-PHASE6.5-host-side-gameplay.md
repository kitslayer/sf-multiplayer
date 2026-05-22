# Phase 6.5 — Host-side gameplay on the oracle

**Status:** ✅ Weapon spawning works end-to-end as of 2026-05-22 (commit `4bf87cc`, hardened in `d4807fc`).

**One-line summary:** the BepInEx plugin `SFHeadlessHost.dll` makes the headless Stick Fight process *think* it's a multiplayer host, so SF's own gameplay code (weapon spawn timer, killboxes, hit detection) runs server-authoritatively. The plugin intercepts SF's host broadcasts and forwards them through our own v25 UDP socket to real clients.

This is "Path A" — the oracle IS the server. There is no separate Go process in the data path for active matches.

## Architecture in 30 seconds

```
Real Steam SF (client)
   │  v25 raw-UDP wrapper [u32 ts][u8 msgType][N body][u64 steamID][u8 ch]
   ▼
Headless SF (oracle), port 1337
   │
   ├── SFHeadlessHost.cs ────────────────────────────────┐
   │       v25 protocol handler (DrainSfServer + handlers)
   │       Forward host broadcasts → clients (this file)
   │                                                     │
   ▼                                                     │
   SF's own Assembly-CSharp:                             │
       • MultiplayerManager (IsServer postfix → true) ───┘
       • MatchmakingHandler (IsNetworkMatch pinned true)
       • GameManager (StartMatch + inFight=true forced)
       • WeaponSelectionHandler (GetRandomWeaponIndex stubbed)
       • host-side gameplay (weapon spawn timer, killboxes, ...)
              │
              ▼
       MultiplayerManager.SendMessageToAllClients ──── PREFIX intercepts
              │                                            │
              ▼                                            ▼
       mConnectedClients loop (empty, no-op)         Plugin's v25 socket → clients
```

The key insight: SF's own `SendMessageToAllClients` iterates `mConnectedClients`, which is empty on the oracle because we never register clients into SF's tracking. So SF's own broadcast loop does nothing. But our Harmony prefix on the same method captures the `(byte[] data, MsgType type, ...)` arguments before SF's loop runs and we send them through our own UDP socket. The MsgType enum values match the v25 protocol byte IDs 1:1 for the first 38 entries, so no translation needed.

## The seven Harmony patches

All installed in `Plugin.Awake`. Each runs through the `TryPatch` helper so a single failure doesn't silently skip later patches — the summary line `[P6.5] All 7/7 patches installed.` should appear after boot.

| # | Target | Type | Why |
|---|---|---|---|
| 1 | `MultiplayerManager.IsServer` getter | postfix → true | Activates SF's host-side code branches (suppresses "Client trying to call server functions" LogError, gates weapon spawn). |
| 2 | `MultiplayerManager.SendMessageToAllClients` | prefix log + forward | The intercept point that turns SF's intent-to-broadcast into actual delivery via our v25 socket. |
| 3 | `MatchmakingHandler.IsNetworkMatch` getter | postfix → true | Partial — Mono inlines this getter into `SpawnRandomWeapon`, so the postfix can't catch inlined `ldsfld` reads. See #4. |
| 4 | `MatchmakingHandler.SetNetworkMatch(v)` | prefix `v=true` | Pins the backing field. `Controller`'s lifecycle resets the field to `IsInsideLobby` every tick (false on the oracle); we intercept every call and force the arg. **Param name `v` must match SF's — checked at install time.** |
| 5 | `WeaponSelectionHandler.GetRandomWeaponIndex` | prefix → cycled 0-7 | UI never initialized in batchmode, so SF would return -1 and skip spawn. We cycle through stock weapon indices. |
| 6 | `GameManager.SpawnRandomWeapon` | prefix replace impl | **The critical workaround for Mono inlining.** Even with IsNetworkMatch pinned, SF's compiled `SpawnRandomWeapon` reads `mIsNetworkMatch` via `ldsfld` directly (inlined getter) and may take the local-Instantiate else branch with null weaponObject. We bypass entirely and call `MultiplayerManager.SpawnWeapon` directly via reflection. |
| 7 | `P2PPackageHandler.SendP2PPacketToUser(CSteamID,…)` | prefix log | Observation-only. Catches direct host→client sends (ClientInit on new join, KickPlayer, etc). |

## Boot sequence on the oracle

```
T+0          plugin Awake, all 7 patches installed
T+~3s        BepInEx chainloader done, plugin starts BootState.WaitForInit
T+~4s        SceneManager.LoadScene(0, Single) — MainScene loads
T+~6s        BootState.Running — v25 UDP socket listening on 1337
T+(client join + 4s)
             [SF] Auto-match-start firing: broadcast MapChange + StartMatch (to client over v25)
             [P6.5] MatchmakingHandler.SetNetworkMatch(true)
             [P6.5] Invoking GameManager.StartMatch(MapType=0, sceneIdx=6, MovePlayers=false)
                — SF runs StartMapSequence coroutine
                — Desert3 loads ADDITIVELY on top of MainScene
T+(above + 3s)
             [P6.5] Forced GameManager.inFight = true (bypassing countdown UI)
             [P6.5] randomWeaponCounter = 2.0
T+(above + ~2s, then every 5-8s)
             [P6.5] GetRandomWeaponIndexPrefix call#N → returning K
             [P6.5 SRW] call#N → SpawnWeapon(id=K, pos=(0, 11, Z))
             [P6.5] HostBroadcast#M msgType=19(WeaponSpawned) bodyLen=8
             [P6.5] Forwarded msgType=19(WeaponSpawned) bodyLen=8 to N v25 client(s)
```

If the host-side gameplay loop stalls, the state probe (`[P6.5 probe]` lines, emitted every 2s while `_matchStarted` is true) shows `inFight`, `randomWeaponCounter`, `matchTime`, `stillInMenu`, `IsNetworkMatch` — read live from SF's runtime via reflection. Invaluable for diagnosis.

## What still doesn't work

| Symptom | Why | Fix on the way? |
|---|---|---|
| Can't pick up guns | Client sends `ClientRequestingWeaponPickUp` (msgType 25) via v25; oracle's `SfDispatch` doesn't have a handler for it | Forward incoming msgType ≥ 11 packets into SF's `P2PPackageHandler.CheckMessageType` via reflection — SF will route to `OnPlayerRequestingWeaponPickUp` etc. |
| Can't take damage | Same — `PlayerTookDamage` (msgType 11) not forwarded into SF's dispatcher | Same fix as above. |
| Physics objects don't move | SF host-side ObjectUpdate broadcasts would fire if rigidbodies in the loaded scene actually had clients to push to; today they're either unsynced or our forward needs to capture them | Likely "just works" once the above incoming-forward lands and rigs become network-syncable. |

## Operational notes

- **Oracle launch:** `SFHEADLESS_BRIDGEPORT=1341 SFHEADLESS_PORT=1337 SFHEADLESS_DEBUG=1 bash launch-sf-headless.sh`
- **Client launch options (Steam):** `WINEDLLOVERRIDES="winhttp=n,b" %command% -address 127.0.0.1 -port 1337`
- **Surgical oracle restart (don't kill your Steam SF):** `for pid in $(pgrep -f "StickFight.exe -batchmode"); do kill -9 $pid; done` then re-launch.
- **BepInEx log:** `$SF_MIRROR/BepInEx/LogOutput.log` (overwritten each oracle start).
- **Unity log:** `/tmp/sf-oracle-unity-1341.log` (overwritten each oracle start).
- **Bridge debug socket:** loopback-only on 127.0.0.1:1341 (hardened post-review — was 0.0.0.0).
- **Stale-client GC:** entries in `_sfClients` with LastSeen older than 30s are swept every 5s.

## Things to be careful with

1. **Mono inlining is unforgiving.** Harmony postfix on trivial property getters (single-statement returns) doesn't catch inlined call sites. If a future SF gameplay code path reads a property we've patched via postfix, treat it as untrusted — verify with a probe that the patched value is actually being observed.
2. **`(byte)__args[N]` on a boxed enum is brittle.** Mono tolerates it; stricter CLRs throw `InvalidCastException`. Use `Convert.ToInt32` via the `UnboxByte` helper.
3. **`!= null` on `FieldInfo`/`MethodInfo`/`Type` triggers Mono's missing `op_Inequality`.** Always cast to `object` first: `(object)f != null`. (See the section in [09-PHASE6.3-BLOCKER-AND-OPTIONS.md](09-PHASE6.3-BLOCKER-AND-OPTIONS.md) for the original lived experience.)
4. **`SceneManager.LoadScene(N, Single)` destroys MainScene.** `GameManager` lives in MainScene without `DontDestroyOnLoad`, so a Single load nukes it. Use `LoadSceneMode.Additive` for gameplay scenes. (`GameManager.StartMatch` already does this internally via `LoadMapCourotine`.)
5. **`mConnectedClients` is empty on the oracle, intentionally.** Our v25 protocol tracks clients independently in `_sfClients`. SF's `SendMessageToAllClients` loop is always a no-op; the broadcast intercept + v25 forward is what actually delivers packets.

## Code references

- Plugin source: [`sf-headless-host/SFHeadlessHost.cs`](../../sf-headless-host/SFHeadlessHost.cs)
- Patch installer: search for `[P6.5]` in that file
- Decompiled SF code we patch against: [`refs/decompiled/Assembly-CSharp/`](../../refs/decompiled/Assembly-CSharp/) (MultiplayerManager.cs, GameManager.cs, MatchmakingHandler.cs, WeaponSelectionHandler.cs, P2PPackageHandler.cs)
- Memory state for next session: `~/.claude/projects/-home-miles-sf-multiplayer/memory/phase6_4_state.md`
