# Design — Loosen the all-zero spawn-position guard

**Status:** ✅ Applied in initial commit `12801bc` (players.go:15 + lobbies.go:1533). Doc retained for design rationale.

> **2026-05-22 note:** the Go server this guard lived in is being deprecated as part of the Path A pivot — see [`../phase6/10-PHASE6.5-host-side-gameplay.md`](../phase6/10-PHASE6.5-host-side-gameplay.md).

## Problem

`StickFightDedicatedSrv/lobbies.go:1522`:

```go
if posX == 0 && posY == 0 && posZ == 0 && lobby.CurrentLevel != nil && lobby.CurrentLevel.Type() == 0 {
    if md, ok := loadedMaps[lobby.CurrentLevel.SceneIndex()]; ok && md != nil && len(md.PlayerSpawns) > 0 {
        s := md.PlayerSpawns[index%len(md.PlayerSpawns)]
        posX = s.Pos[0]
        posY = s.Pos[1]
        posZ = s.Pos[2]
    }
}
```

The intent is: if the client says "I don't have a preferred spawn position" (all-zero), look up the dumped map's spawn points and pick one. But the client never sends all-zero in non-lobby maps.

`Landfall.Network.Sockets/MultiplayerManagerSockets.cs:1586`:

```csharp
Vector3 vector = ((!isInLobby) ? new Vector3(0f, 12f, 0f) : new Vector3(0f, 0f, 0f));
```

For non-lobby maps the client requests spawn at `(0, 12, 0)` — drop-from-height-12. The guard's `posY == 0` check fails, so the dumped spawn data is never used. The patched DLL doesn't change this either. So the server's "pick a real platform spawn" feature is dead code in match flow.

## What this causes

Players spawn at world position `(0, 12, 0)` in every match. They fall under client-side gravity onto whatever platform happens to be near `(0, 0, 0)` in the scene. In most Landfall maps the playable platform is centered around `Z ≈ -30` to `-50` (per the dumped map JSON), not `Z ≈ 0`. So `(0, 12, 0)` is *off-platform* in those scenes — the player falls into the void or onto a wrong piece of geometry.

This is masked today by the bigger flag=1 instant-death bug. After the headline fix lands, players will actually have HP and start falling — and they'll fall off-map.

## Proposed fix

Loosen the guard so it triggers on the patched-DLL's actual sentinel value too. Two options:

### Option A — treat any "obviously-not-game-coords" position as a sentinel

```go
if (posX == 0 && posZ == 0) && lobby.CurrentLevel != nil && lobby.CurrentLevel.Type() == 0 {
    // The client sends (0,12,0) for non-lobby maps and (0,0,0) for lobby maps.
    // Neither value is a usable in-scene position for any Landfall map. Override.
    if md, ok := loadedMaps[lobby.CurrentLevel.SceneIndex()]; ok && md != nil && len(md.PlayerSpawns) > 0 {
        s := md.PlayerSpawns[index%len(md.PlayerSpawns)]
        posX = s.Pos[0]
        posY = s.Pos[1]
        posZ = s.Pos[2]
    }
}
```

Note we drop the `posY == 0` clause and keep `posX == 0 && posZ == 0` since those are common to both sentinel values. This is safe because no real Landfall spawn is at exactly `(0, *, 0)` — see the dumped spawns dump in `notes/recon/RELATED_BUGS.md`.

### Option B — always override in non-lobby maps when dumped data exists

```go
if !lobby.CurrentLevel.IsLobby() && lobby.CurrentLevel.Type() == 0 {
    if md, ok := loadedMaps[lobby.CurrentLevel.SceneIndex()]; ok && md != nil && len(md.PlayerSpawns) > 0 {
        s := md.PlayerSpawns[index%len(md.PlayerSpawns)]
        posX = s.Pos[0]
        posY = s.Pos[1]
        posZ = s.Pos[2]
    }
}
```

Simpler. Server-authoritative spawn positions in all non-lobby maps. Slightly more aggressive — overrides any honest client-asserted position too — but the patched DLL doesn't assert anything meaningful here anyway. Recommend B unless there's a reason to honor non-zero client-asserted positions (e.g. a future client that does picks its own spawn — but stock SF never does).

## Why not fix the patched DLL instead

Could be done, but the server already has the right data (dumped spawn arrays) and the right code path (just behind a too-strict guard). Fixing the patched DLL would require an additional Harmony patch and a redeploy. Keep client surface minimal.

## Workshop maps

`loadedMaps` doesn't have JSONs for workshop maps (those aren't pre-dumped). For workshop maps `loadedMaps[scene]` is `ok=false` so the new condition is a no-op — the client's `(0, 12, 0)` is sent through, matching today's behavior on workshop maps. That's fine for now; workshop-map spawn override is M5 polish.

## What does NOT change

- Wire format unchanged.
- Patched DLL unchanged.
- The fix has zero effect when no dumped JSON exists for the current scene (workshop maps).
