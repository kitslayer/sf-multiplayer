# Design — Verification plan for the flag-logic fix

How the next session should confirm the fix actually fixes the 3-second-match-cycle bug.

## Pre-flight (without modifying code)

Goal: prove the diagnosis is correct **before** changing anything.

1. SSH to the dev laptop: `ssh user@<tailnet-ip>`.
2. Confirm the production server is still on port 1337 and the binary is `/tmp/sfdsrv.combined`:
   ```
   ss -tlnp | grep 1337
   ps -p $(pgrep -f sfdsrv.combined) -o cmd
   ```
3. Confirm no test process is on port 1338: `ss -tlnp | grep 1338` (expect empty).
4. Add a one-shot diagnostic log line above the flag check in `SpawnPlayer`. *This is for verification only — don't merge.* Build a new binary at a separate path:
   ```
   cd ~/sf-multiplayer/StickFightDedicatedSrv
   go build -o /tmp/sfdsrv.test .
   ```
5. Run on port 1338 in a separate tmux pane:
   ```
   /tmp/sfdsrv.test -address 0.0.0.0:1338 -mapsDir ~/sf-multiplayer/maps -verbosity 1 > /tmp/sfdsrv.test.log 2>&1 &
   ```
6. Launch two Goldberg-faked SF instances pointed at `127.0.0.1:1338` (use existing test setup — `launch-local2.sh` already exists and was used in the prior session).
7. Watch `/tmp/sfdsrv.test.log`. Expect:
   - `Spawned player N at position {X:0 Y:12 Z:0} with rotation ... using flag 1` for every spawn after the first map.
   - `Player N took a killing blow from player N of type Other` immediately after each spawn (note attacker==victim).
   - `Player N is the winner!` shortly after.
   - `Started match!` again ~3 seconds later (auto-ready).
   - This pattern repeating in ~3-second cycles.

If the log matches, the diagnosis is confirmed.

## Apply the fix

Per `notes/design/FIX_FLAG_LOGIC.md` (Candidate A). Implementation order:
1. Add `SpawnedThisRound bool` to the Player struct in `players.go`.
2. Update `SpawnPlayer` in `lobbies.go:1497-1551`:
   - Replace the flag block with the Candidate A form.
   - Set `Players[clientPlayerIndex].SpawnedThisRound = true` after `Spawned = true`.
3. Update `StartMatch` in `lobbies.go:1707+`:
   - In the loop that resets Health (line 1745-1751), also reset `SpawnedThisRound = false`.
4. (Optional, for paranoia) In `ChangeMap` (line 1856+), also clear `SpawnedThisRound` for everyone right after `UnReadyAllPlayers()`.
5. Build: `go build -o /tmp/sfdsrv.test .`

Apply `FIX_SPAWN_FALLBACK_GUARD.md` (Option B) in the same patch — small, related, prevents off-platform spawns once the kill loop is gone.

## Post-fix verification

1. Run `/tmp/sfdsrv.test` on port 1338 as above.
2. Smoke-test first: `cd ~/sf-multiplayer/StickFightDedicatedSrv/cmd/smoke-test && go run . -secs 18 -proto 26 -steam 76561198000000001 -address 127.0.0.1:1338`. Expect the existing pass criteria (~97% snapshot delivery) to still hold.
3. Two-Goldberg-instance test against port 1338. Expect:
   - First spawn of each round: `flag 0` in the log.
   - No "killing blow" events at match-start.
   - Matches actually run (>10 seconds usual; varies by skill).
   - When a player legitimately dies (killbox in the actual SF client world, weapon damage, etc.) the kill flow still works — `CheckWinner` → `ChangeMap`.
4. Late-joiner test (regression): with one match running, connect a third Goldberg instance. Expect: third player joins, spawns with `flag=1` (dead), waits for next round, then spawns alive on the next `ChangeMap`.
5. If both 3 and 4 hold, the fix is good. Promote `/tmp/sfdsrv.test` to `/tmp/sfdsrv.combined`:
   ```
   # Tell the operator before doing this — production swap should be his call.
   kill $(pgrep -f sfdsrv.combined)   # OR however the operator stops it
   cp /tmp/sfdsrv.test /tmp/sfdsrv.combined
   /tmp/sfdsrv.combined -address 0.0.0.0:1337 -mapsDir ~/sf-multiplayer/maps -publicLobbies -replayDir /tmp/sf-replays -verbosity 1 &
   curl http://127.0.0.1:1337/status
   ```

## Rollback plan

If the fix breaks something subtle (e.g. spurious early CheckWinner), revert by re-deploying the prior binary. Keep a copy of the current `/tmp/sfdsrv.combined` before swapping:
```
cp /tmp/sfdsrv.combined /tmp/sfdsrv.combined.prev_$(date +%Y%m%d-%H%M%S)
```

The prior binary handles the existing 3-second loop gracefully (server doesn't crash; it just rotates matches), so a revert is low risk.

## Replay-based regression (optional, post-M5 once the replay viewer exists)

Once a replay viewer that consumes the `SFRPL`-format binary log exists (see `notes/recon/StickFightDedicatedSrv/replay.go`), record one match before and one match after the fix. Compare:
- Number of `worldStateSnapshot` records per match (expect post-fix to be ≫ pre-fix because matches are longer).
- Number of `serverEvent` damage records of type "killbox" with attacker==-1 at the very start (expect zero post-fix).

This is a nice-to-have, not blocking.
