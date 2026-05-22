# Other bugs / oddities noticed while diagnosing the headline issue

Not the 3-second-cycle root cause, but worth recording.

## 1. Killbox arrays empty in every dumped map JSON

`tools/dump-sf-maps.py` (per `prior-memory/phase5_state.md`, can't read MonoBehaviour TypeTrees) only extracted spawn points and static colliders. Killbox arrays are empty in every `~/sf-multiplayer/maps/landfall-*.json`.

Spot-checked:
- `landfall-1.json`: 4 spawns at `X=0, Y≈-1.5, Z=[-29, -51]`; 26 statics; **0 killboxes**.
- `landfall-7.json`: 4 spawns; 36 statics; **0 killboxes**.
- `landfall-51.json`: 4 spawns; 0 listed statics; **0 killboxes**.
- `landfall-100.json`: 4 spawns; 30 statics; **0 killboxes**.

Consequence: server's `EventPlayerKilledByKillbox` path (`physics/world.go:177-189` and `lobbies.go:381-393`) is unreachable in prod. Server-side anticheat against "player teleported through floor" or "player fell out and didn't admit it" is currently impossible. The damage gating still works via the client-asserted `playerTookDamage(666.666)` path.

Fix needs a better dumper — either a runtime BepInEx dumper that survives Proton/Goldberg headless launch, or an offline TypeTree-aware C# dumper (Cecil / `pythonnet`).

## 2. The spawn-position fallback guard at `lobbies.go:1522` is dead in prod

The guard requires `posX == 0 && posY == 0 && posZ == 0` to override with a dumped-map spawn. But the patched DLL sends `(0, 12, 0)` for non-lobby maps (`MultiplayerManagerSockets.cs:1586`). So `posY == 0` fails and the override is never used in real matches. Players spawn at `(0, 12, 0)` which is off-map on most scenes (playable platforms are around `Z=-30` to `-50`).

This is masked today by the headline flag=1 bug (which kills players before they fall). After the headline fix, this will likely manifest as "players spawn in the wrong place and fall off-map." Design doc: `notes/design/FIX_SPAWN_FALLBACK_GUARD.md`.

## 3. `attackerIndex == playerIndex` for killbox kills sent via legacy v25 path

`handlePhysicsEvent` at `lobbies.go:389`:

```go
lobby.DamagePlayer(victim, victim, 666.666, damageTypeOther, Vector2{})
```

Attacker is set to the victim themselves. Then `PlayerTookDamage` at line 2298:

```go
if attackerIndex != playerIndex {
    lobby.Clients[attackerClientIndex].Players[attackerClientPlayerIndex].Stats.Kills++
}
```

The kill-credit increment is skipped because attacker == victim, so killbox deaths credit as suicide. This is semantically the right behavior (player killed themselves by falling off), but it's worth noting that the v26 path (`broadcastServerEventDamage` at line 390) uses `attackerSlot=-1` to signal "no attacker, killbox", which the v26 client could distinguish from suicide. The legacy v25 path can't.

Not urgent. Track if anyone complains about killbox kills showing as "suicide" in stats UIs.

## 4. `clientsMu` race-condition retrofit is incomplete

The prior session added per-lobby `clientsMu sync.RWMutex` + `snapshotClients()` helper for hot iteration paths. Spot-checked `lobbies.go` and confirmed:
- `BroadcastWorldSnapshot`, `BroadcastPacket`, server-event broadcasts, `GetClient*`, `GetPlayers`, `RecomputeV26Status` — all use snapshot.
- BUT direct `lobby.Clients[...]` indexing still appears in some paths: lines 1746, 1748, 1804, 1809, 2243, 2253, 2269, 2275, 2280, 2292-2299, 2355-2356. These read/write Health and Stats fields without the snapshot.

This is mostly fine because writes go through `lobby.Lock()` higher up — but the `PlayerTookDamage` handler at line 2253 reads `lobby.Clients[attackerClientIndex].Players[...].Health` without locking, and a concurrent `ClientRemove` (which reslices `Clients`) could panic. The existing `defer recover()` swallows the panic; the data is lost.

Track in M5 hardening.

## 5. `server.go:712-722 HasSwear` has dead validation

```go
func (srv *Server) HasSwear(message string) (tripped bool) {
    trippedWords, err := srv.Filter.Check(message)
    if err != nil {
        tripped = true
    }
    if len(trippedWords) > 0 {
        tripped = true
    }
    ...
}
```

`swears = []string{" "}` — the filter is configured with a single-space sentinel, which trips on every message containing a space. Probably intentionally neutered for now, but it's a footgun if someone ever flips the filter on without realizing.

## 6. `MatchInProgress` lock is reentrant-unsafe

`lobbies.go:1791-1796`:

```go
func (lobby *Lobby) MatchInProgress() bool {
    lobby.Lock()
    defer lobby.Unlock()
    return !lobby.FightStartTime.IsZero()
}
```

`lobby.Lock()` is a `sync.Mutex`. If anything calls `MatchInProgress()` while already holding the lock, it deadlocks. Spot-checked callers — most don't hold the lock, but `ChangeMap` and `StartMatch` both interact with `FightStartTime` directly. The pattern "lock to read FightStartTime" assumes other writers also hold the lock; if any writer touches `FightStartTime` without the lock you can get torn reads. Worth a once-over.

## 7. The HTTP server impersonates HTTP

`server.go:373-444 ReadHTTP` doesn't parse incoming requests as HTTP — it converts them into "fake" HTTP responses by stuffing the raw packet body into a `http.Response`. The routing for `/lobbies`, `/status`, `/maps`, `/invite` is done in `NewPacketFromBytes` (called from `ReadHTTP`), which is intricate and easy to break. Worth a refactor pass at some point.

Not related to the 3-second bug. Tracked here so future investigators don't get confused trying to understand the HTTP layer.
