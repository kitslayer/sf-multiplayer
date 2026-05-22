# Bug — Matches end after ~3 seconds, in a loop

> **Status: ✅ RESOLVED in initial commit `12801bc`.** Fix is in `lobbies.go:1521-1540` (flag=0 on round-start spawns) and `lobbies.go:1769` (`StartMatch` resets `SpawnedThisRound = false`). See [`../design/FIX_FLAG_LOGIC.md`](../design/FIX_FLAG_LOGIC.md) for the design and rationale. Subsequent line refs in this doc target the recon snapshot under `notes/recon/StickFightDedicatedSrv/`, not live source.

Symptom (from `prior-memory/phase5_state.md` "End-of-session state"):
> The short-match-cycle problem (matches ending in 3 seconds) is still real.

Three hypotheses were left open by the prior session. This document closes the diagnosis.

## TL;DR root cause

**`SpawnPlayer` in the Go server unconditionally sets `flag=1` ("force die") for every player-spawn during any multiplayer match.** The stock SF client interprets that as "teleport to (0, -100, 0) and call `HealthHandler.ForcedDie()`". So *every match starts with both players insta-dying*; the kill propagates to the server; `CheckWinner` declares a winner; `ChangeMap` rotates; auto-ready's 3-second timer fires; the loop repeats. There is no "the match runs and then ends mysteriously"; the match was killed before any player ever touched the ground.

Confidence: very high. The interpretation of `flag=1` is verbatim in the decompiled stock client and the patched DLL does **not** override it.

## Evidence

### 1. The server sets flag=1 in multiplayer matches

`StickFightDedicatedSrv/lobbies.go:1497-1551` (function `SpawnPlayer`):

```go
//SpawnPlayer spawns the specified player at the specified coordinates
func (lobby *Lobby) SpawnPlayer(index int, posX, posY, posZ, rotX, rotY, rotZ float32) {
    ...
    flag := 0 //0 (default) = revive player for new map, 1 = forced die for spawned player
    if !lobby.CurrentLevel.IsLobby() && lobby.GetPlayerCount(true) > 1 {
        flag = 1
    }
    ...
    packetClientSpawned.WriteByteNext(byte(flag))
    ...
}
```

`!IsLobby` is true on every real match map. `GetPlayerCount(true) > 1` is true in any multiplayer match. So `flag=1` is set every single time a player respawns in any normal multiplayer game.

`SpawnPlayer` is called from `clientRequestingToSpawn` (line 763-784), which the client sends at match start (via `RequestSpawnPlayer` in the decompile, see below).

### 2. The client interprets flag=1 as "die immediately"

`refs/decompiled/Assembly-CSharp/MultiplayerManager.cs:1576-1641`:

```csharp
public void OnPlayerSpawned(byte[] data)
{
    byte b;
    Vector3 vector = default(Vector3);
    Vector3 euler = default(Vector3);
    bool flag;
    using (MemoryStream input = new MemoryStream(data))
    {
        using BinaryReader binaryReader = new BinaryReader(input);
        b = binaryReader.ReadByte();
        vector.x = binaryReader.ReadSingle();
        vector.y = binaryReader.ReadSingle();
        vector.z = binaryReader.ReadSingle();
        euler.x = binaryReader.ReadSingle();
        euler.y = binaryReader.ReadSingle();
        euler.z = binaryReader.ReadSingle();
        flag = binaryReader.ReadBoolean();
        if (flag)
        {
            vector = new Vector3(0f, -100f, 0f);    // <-- override position
        }
    }
    GameObject gameObject = UnityEngine.Object.Instantiate(m_PlayerPrefab, vector, Quaternion.Euler(euler));
    ...
    if (!flag)
    {
        mGameManager.RevivePlayer(component2);
    }
    else
    {
        gameObject.GetComponent<HealthHandler>().ForcedDie();   // <-- instant death
    }
}
```

The Sockets variant in `Landfall.Network.Sockets/MultiplayerManagerSockets.cs:1480+` has the same shape (also verified).

### 3. The patched DLL does NOT override OnPlayerSpawned

```
$ grep -rn "OnPlayerSpawned\|ForcedDie\|RevivePlayer" sf-netcodev2/
# (nothing relevant — only "flags" matches refer to the v26 snapshot wire format,
# not the OnPlayerSpawned `flag` boolean)
```

The Harmony patches in `SFNetcodeV2.cs` only touch:
- `RequestPlayerIndex` (protocol-version bump 25→26)
- `RayCastForward.FixedUpdate` (disabled while v26 active)
- `Controller.Update` (postfix: emit `playerInput`)
- `P2PPackageHandler.CheckMessageType` (prefix: intercept snapshot/serverEvent)

`OnPlayerSpawned` runs unchanged from the stock DLL.

### 4. The kill flows back to the server and triggers ChangeMap

`HealthHandler.ForcedDie` (stock SF) zeros the player's HP and triggers the normal kill flow, which sends `PlayerTookDamage` with damage=666.666 (kill blow). The Go server's `PlayerTookDamage` handler at `lobbies.go:2288-2308`:

```go
if damage == 666.666 {
    log.Info("Player ", playerIndex, " took a killing blow from player ", attackerIndex, " of type ", damageType)
    lobby.Clients[clientIndex].Players[clientPlayerIndex].Health = 0
    lobby.Clients[clientIndex].Players[clientPlayerIndex].Stats.Deaths++
    ...
    lobby.CheckWinner()
    return
}
```

`CheckWinner` (line 1814-1853) → 1 survivor → `ChangeMap`. `ChangeMap` un-readies everyone and clears `FightStartTime`. The auto-ready goroutine (line 1558-1586) fires after 3 seconds, marks the slot Ready, and calls `StartMatch()` when both are ready. The cycle repeats.

### 5. Why this matches the "3-second" cadence

The dominant 3 seconds is the auto-ready timer at `lobbies.go:1559` (`time.Sleep(3 * time.Second)`). Each cycle is roughly: ChangeMap → patched DLL processes mapChange and re-sends clientRequestingToSpawn → SpawnPlayer (flag=1) → ForcedDie → PlayerTookDamage(666.666) → CheckWinner → ChangeMap. The 3 seconds between ChangeMap and the next StartMatch is dominated by the auto-ready timer; the kill itself is near-instant. So "matches ending in 3 seconds" is consistent with "match starts → both players are insta-killed → auto-ready fires 3s later to start the next one."

### 6. Why the (reverted) auto-respawn fix made matches last 23s instead of 3s

`phase5_state.md` notes that the prior auto-respawn-on-map-change goroutine made matches sustain "23s vs 3s." That goroutine broadcast `clientSpawned` with **flag=0** (revive) ~400ms after each map change. So the sequence was:

1. Match start → server sends clientSpawned(flag=1) → patched DLL teleports player to (0,-100,0) and ForcedDies.
2. ~400ms later, the auto-respawn goroutine broadcasts clientSpawned(flag=0) for the same player → patched DLL re-spawns at the proper position and calls RevivePlayer.

The player ended up alive *after* the second packet. The 23s was the natural match length until somebody actually died for real. But this caused **duplicate render** because the patched DLL had also already instantiated a corpse player object during step 1 (and possibly another via local OnMapChanged logic). The right fix is to skip flag=1 in the first place, not to paper over it with a second broadcast.

## Hypotheses ruled out

The prior session listed three possibilities; I ruled out (2) and downgraded (1) and (3):

1. *"`MatchInProgress`/winner detection is firing too eagerly somewhere."* — partially right, but the root cause is upstream of CheckWinner. CheckWinner is correctly declaring a winner; the bug is that there's a valid kill at all.
2. *"Server-side killbox detection might be reporting kills against a stale (0,0,0) player position."* — ruled out. The dumped maps have **empty killbox arrays** (`killboxes: []` in every `landfall-*.json` I checked). The UnityPy raw-parse dumper extracted spawn points and static colliders but never killboxes; the server's `World.Step()` killbox loop iterates over zero AABBs and emits zero `EventPlayerKilledByKillbox` events. See `RELATED_BUGS.md` — this is a separate latent issue, but it isn't the 3-second cause.
3. *"`playerFallOut` / `playerTookDamage` race where one player's kill triggers winner-declaration before the second has spawned."* — possible but not necessary. `flag=1` kills *all* spawning players, not just one, so the race window isn't required for the observed behavior.

## How to confirm in a live test (the next session)

1. On the dev laptop, run a test server on port 1338 (separate binary path; do NOT touch port 1337):
   ```
   cd ~/sf-multiplayer/StickFightDedicatedSrv && go build -o /tmp/sfdsrv.test .
   /tmp/sfdsrv.test -address 0.0.0.0:1338 -mapsDir ~/sf-multiplayer/maps -verbosity 1 2> /tmp/sfdsrv.test.log
   ```
2. Add `log.Info("flag-decision: posIn=", posX, posY, posZ, " flag=", flag)` right after the `if !lobby.CurrentLevel.IsLobby() ...` block (this is for diagnostic verification before fixing — don't merge this).
3. Re-run the two-Goldberg-instance test against port 1338.
4. Expected log: every spawn shows `flag=1`. Matches end immediately.
5. Apply the proposed fix in `notes/design/FIX_FLAG_LOGIC.md`.
6. Re-run. Expected: `flag=0` for the first spawn of each round; matches sustain until a real kill.

## Surface area of the fix

- One file: `StickFightDedicatedSrv/lobbies.go`.
- One function: `SpawnPlayer`.
- One condition change at line 1514-1517.
- Optional secondary fix to the spawn-fallback guard at line 1522. See `notes/design/FIX_SPAWN_FALLBACK_GUARD.md`.

No client-side change is needed for the core fix. (A client-side change would be needed if you instead wanted to neuter `flag=1` semantics in the patched DLL — discouraged because then late-joiners would spawn alive mid-match.)
