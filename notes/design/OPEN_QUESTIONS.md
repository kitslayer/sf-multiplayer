# Open questions for the next session

Things this session noticed but did not (could not, given the no-code rule) resolve.

## Confirmed-but-unresolved

### 1. Dumped killbox arrays are empty for every Landfall map

Every `~/sf-multiplayer/maps/landfall-*.json` has `"killboxes": []`. Spot-checked scenes 1, 7, 51, 100, 124 — all empty. `tools/dump-sf-maps.py` only raw-parses the MapInfo header for spawn points + static colliders; killboxes are MonoBehaviour-typed objects the UnityPy reader can't see without TypeTrees.

**Impact today:** the server's `World.Step()` killbox loop never fires (`EventPlayerKilledByKillbox` is dead code in production). The match-cycle bug is *not* caused by this (see `BUG_3SEC_MATCH_CYCLE.md`), but server-side anticheat for "player fell off and didn't admit it" is currently impossible.

**Recommendation:** Once the BepInEx runtime dumper (`sf-leveldumper`) can advance past the Steam splash on a real interactive session, re-dump and refresh `~/sf-multiplayer/maps/landfall-*.json` with populated killboxes. Alternative: write a Cecil/Mono.Cecil-based offline dumper that can read MonoBehaviour TypeTrees. The prior session attempted this and stalled. Track in Phase 5 M5.

### 2. Server-side player physics gravity will pull v26 players off-map after the flag fix

Once `flag=1` is removed from match-start, v26 players will exist as real `EntityPlayer` entities in the server-side physics world. The world ticks at 60Hz, gravity is `(0, -9.81, 0)`. Static colliders ARE present in the dumped data (so swept-collision should catch the player on a platform).

**Risk:** Axis-convention mismatch. The static colliders' positions in the dumped JSON are in Unity's native XYZ. The server treats `Y` as world-up and `Z` as lateral (per `physics/player.go:60-67` comments). Spawns at `Z≈-30` to `-50` (from the dumps). If the static collider AABBs don't actually cover the player's spawn position, the player free-falls until... nothing (no killboxes in the dumps), so they fall forever and stack downward velocity.

**Recommendation:** After the flag fix, run a smoke test and check `lobby.World.Get(entID).Box.Center` of a v26 player over time. If Y is monotonically decreasing without bound, the static colliders aren't catching. Spot-check a single scene's static colliders + spawn — verify with the dumped JSON that there IS a collider beneath the spawn point in both Y and Z extent.

### 3. The "auto-ready in 3 seconds" timer is now a footgun

After the flag fix, the auto-ready timer (`lobbies.go:1558-1586`) becomes the trigger that starts the *next* match after a real match ends. Today it fires on every spawn — including the corpse spawn — which is part of why the loop is so tight. After the fix it'll only fire on legitimate spawns, but the timer was added for Goldberg-faked tests that don't send `clientReadyUp`. Real Steam clients DO send `clientReadyUp`. So in a real-Steam-vs-real-Steam match, the auto-ready timer fires alongside the real ready-up. Probably fine — `StartMatch` guards against double-start with `if lobby.MatchInProgress() { return }` (line 1717) — but worth keeping in mind.

**Recommendation:** Once real-Steam testing is feasible (Phase 5 M5 polish), consider gating auto-ready behind a CLI flag like `-autoReady` that's off by default for prod and on for dev.

## Not investigated this session

### 4. Replay format snapshot bodies look 0-byte for the BJSLLD replay

When I hex-dumped `/tmp/sf-replays/BJSLLD-20260521T190804.sfreplay` the first record after the SFRPL header looked like `sinceStartMs=33, kind=0 (snapshot), length=0`. Could mean either:
- The match had no v26 entities to serialize (empty body), which `BroadcastWorldSnapshot` legitimately writes as length-0 today after the `if len(body) > 0` guard fix.
- Or my offset arithmetic is wrong.

Not blocking for the flag fix. Worth a quick check if you build out a replay viewer.

### 5. What is `/tmp/sfdsrv-baseline-lines.txt` (5 bytes)?

Pulled it; contents are tiny. Looks like a marker/checksum file from some baseline run. Not investigated.

### 6. The flag-decision logic on upstream StickFightDev

The `flag=1` condition probably came from the abandoned upstream JoshuaDoes/StickFightDev project. Did upstream guard it differently? Comparing against the original repo would either confirm the bug was carried over or reveal that upstream guarded it correctly and the operator' fork diverged. Worth a quick `git log -p -- lobbies.go` on the dev laptop's clone.

### 7. v25 client behavior

All analysis here is for v26 (patched-DLL) clients. v25 clients also go through the same `SpawnPlayer` path and would also receive `clientSpawned` with `flag=1`. So v25 vs v25 matches should also be 3-second loops. The prior session's tests were v26-vs-v26 (Goldberg 2nd instance loaded SFNetcodeV2). If you see different behavior in v25-only tests, that'd be a surprise and worth investigating.

### 8. Does `clientRequestingToSpawn` actually get sent at match start?

I traced the C# decompile (`MultiplayerManagerSockets.RequestSpawnPlayer:1574+`) and it does send `ClientRequestingToSpawn` for non-host clients. But the host's path (`if (IsServer) SpawnPlayer(); return;`) sends `ClientSpawned` directly — bypassing the server's flag-decision. In a dedicated-server setup nobody is `IsServer` (it's the standalone Go server), so all clients hit RequestSpawnPlayer and the flag bug applies to all of them. Worth confirming `IsServer` is indeed always false in the patched DLL setup. A quick `grep -n IsServer ~/sf-multiplayer/sf-netcodev2/*.cs` and `grep -n "set.*IsServer" ~/sf-multiplayer/refs/decompiled/Assembly-CSharp/MultiplayerManagerSockets.cs` would settle it.

### 9. The /lobbies HTTP endpoint and lobby browser

`/tmp/sfdsrv.next.SWAP_NOTES.txt` mentions a `/lobbies` endpoint and `/tmp/sf-lobby-browser` (Linux + .exe builds). This is a separate workstream from the 3-second bug and was queued for deployment. Not investigated.

### 10. Connection rate limit

`server.go:40-70` limits accept-class packets to 8 per IP per 10s. With two Goldberg instances on the same IP (both `127.0.0.1`), repeated reconnect-flap from the kill loop could plausibly hit the rate limit and produce silent drops. Unlikely to be the cause of the headline bug, but worth keeping in mind for repro tests — if you see `Anticheat: refusing clientRequestingAccepting from 127.0.0.1 (rate limited)` in the log, raise the limit temporarily.
