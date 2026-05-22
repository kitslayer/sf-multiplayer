# Design — Fix `SpawnPlayer` flag logic (3-second-match-cycle fix)

**Status:** ✅ Applied in initial commit `12801bc` (lobbies.go:1523 + lobbies.go:1769). Doc retained for design rationale + diagnostic history.

> **2026-05-22 note:** the Go server this fix targeted is being deprecated as part of the Path A pivot — see [`../phase6/10-PHASE6.5-host-side-gameplay.md`](../phase6/10-PHASE6.5-host-side-gameplay.md). The fix still lives in `lobbies.go` for archival reasons; the live SF server is now the headless Unity instance with `SFHeadlessHost.dll`, not sfdsrv.

## Problem (one line)

`SpawnPlayer` in `StickFightDedicatedSrv/lobbies.go:1514-1517` sets `flag=1` ("force die") for every player spawn during any multiplayer match. Stock SF client interprets `flag=1` as instant-death-at-(0,-100,0). Result: every match-start triggers an instant kill → CheckWinner → ChangeMap → 3-second auto-ready → repeat.

Full evidence trail: `notes/recon/BUG_3SEC_MATCH_CYCLE.md`.

## Goal

`flag=1` should only be sent when a player is genuinely joining mid-match (the original SF "late joiner spawns dead, waits for next round" mechanic). For the first spawn of each round, `flag=0`.

## Proposed condition

Replace:

```go
flag := 0
if !lobby.CurrentLevel.IsLobby() && lobby.GetPlayerCount(true) > 1 {
    flag = 1
}
```

with a condition that distinguishes "fresh round spawn" from "late join during an in-progress match." Two candidate forms — recommend candidate A.

### Candidate A (recommended) — track "has spawned this round"

Add a per-player flag `SpawnedThisRound bool` to the `Player` struct (in `players.go`). Reset it in `StartMatch` for everybody and in `ChangeMap` for everybody. Set it to true at the end of `SpawnPlayer`. Then:

```go
flag := 0
if !lobby.CurrentLevel.IsLobby() && lobby.MatchInProgress() &&
    lobby.Clients[clientIndex].Players[playerIndex].SpawnedThisRound {
    // True late joiner: player already alive this round, asking to spawn again.
    // Or alternative: a NEW player joined mid-match. Either way, force-die so
    // they wait for the next round (the original SF behavior).
    flag = 1
}
```

The discriminator `SpawnedThisRound` is necessary because `MatchInProgress()` is already true by the time clients send their `clientRequestingToSpawn` (the server sets `FightStartTime` in `StartMatch` *before* broadcasting the start-match packet — `lobbies.go:1784-1785`).

### Candidate B (simpler, more conservative) — always flag=0

Just unconditionally use `flag = 0`. Side effect: a player who joins the lobby mid-match would spawn alive (no longer waits for next round). For a standalone dedicated server with public lobbies this is arguably better UX, but it diverges from stock SF semantics. Pick B only if there's no appetite for tracking per-round spawn state.

### Candidate C — defer until ChangeMap completes

Wait until ChangeMap finishes broadcasting the new map before processing any `clientRequestingToSpawn` packets, and clear a "round-start-window" flag a few seconds after ChangeMap. This is more complicated than needed and is **not recommended**.

## What changes

- `players.go` — add `SpawnedThisRound bool` to the `Player` struct.
- `lobbies.go` — three writes:
  - In `SpawnPlayer` (line 1497+): replace the flag block as shown in Candidate A. Set `SpawnedThisRound = true` after a successful spawn (e.g. right after line 1548 `Spawned = true`).
  - In `StartMatch` (line 1707+): when iterating clients/players to reset Health, also `Players[j].SpawnedThisRound = false`. The existing loop is at line 1745-1751.
  - In `ChangeMap` (line 1856+): when un-readying, also clear `SpawnedThisRound`. Or equivalently rely on the `StartMatch` reset since `SpawnedThisRound` is only consulted during MatchInProgress.

## What does NOT change

- No client-side change. The patched DLL's `OnPlayerSpawned` interpretation of `flag=1` stays correct (and serves its intended late-joiner purpose).
- No protocol change. The wire format of `clientSpawned` is unchanged.
- No physics-world change. The empty-killbox issue (see `RELATED_BUGS.md`) is independent.

## Risk

- **Spurious early CheckWinner during round transition.** If two players' spawn requests arrive nearly simultaneously and one is briefly seen as "spawned but the other isn't," CheckWinner could fire with len(survivors)==1 spuriously. The existing `CheckWinner` already guards via `CheckingWinner bool` (line 1825-1828) so concurrent calls coalesce, but the gate is for two simultaneous calls — a single legit call with one survivor still triggers ChangeMap. Mitigation: in `CheckWinner`, also require that at least one *kill or fallout event* has occurred since match start, not just "one player has nonzero health." That's a separate hardening item — track in `OPEN_QUESTIONS.md` rather than bundling.
- **Late-joiner behavior shift.** If you go with Candidate B, late-joiners spawn alive — possibly griefable. Candidate A preserves the original behavior.
- **Auto-ready interaction.** The 3-second auto-ready timer was added because Goldberg-faked clients don't send `clientReadyUp`. Fix doesn't touch this. Once the kill loop is gone, the auto-ready will just trigger the normal start of the *next* match after a real match ends, which is fine.

## Estimated effort

15-30 minutes to implement + 30-60 minutes to verify with a 2-Goldberg-instance test. Single PR scope.
