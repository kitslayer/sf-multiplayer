# Phase 6.6 — Pickup forwarding + physics-objects investigation

**Status:** code shipped 2026-05-22 (`6cc293a`); awaiting live test. Read [`10-PHASE6.5-host-side-gameplay.md`](10-PHASE6.5-host-side-gameplay.md) first.

## What 6.6 ships

### Pickup (`ClientRequestingWeaponPickUp`, msgType 25)
Re-broadcast as `WeaponWasPickedUp` (24) with the same body. We bypass SF's `OnPlayerRequestingWeaponPickUp` validation because `mSpawnedWeapons` on the oracle is empty — host-side `SpawnWeapon` only broadcasts; it doesn't populate the host's own dict, and we don't add it ourselves. Validation would always reject. With one trusted client and no anti-cheat threat model, pure relay is fine.

### Pure-relay gameplay packets
The following incoming msgTypes are relayed to all OTHER v25 clients (sender excluded), matching SF's host-side handlers which are also pure relays:

| msgType | Name | Why pure-relay |
|---|---|---|
| 11 | PlayerTookDamage | SF's `OnPlayerTookDamage` just broadcasts |
| 13 | PlayerForceAdded | SF's `RequestForceAdded` just broadcasts |
| 14 | PlayerForceAddedAndBlock | Same |
| 15 | PlayerLavaForceAdded | Same |
| 16 | PlayerFallOut | SF's `OnPlayerFallOut` just broadcasts |
| 17 | PlayerWonWithRicochet | Same |
| 26 | ObjectUpdate | Position sync for syncable objects |
| 28 | ObjectSimpleDestruction | Destruction event |
| 30 | ObjectDestructionCollision | Destructible collision event |

### Weapon drop (`ClientRequestWeaponDrop`, msgType 22)
Reimplemented in C#: append two ushort IDs (weaponSpawnID + syncableObjectSpawnID from our own counter, starting at 32768 to avoid colliding with spawn-side IDs) and broadcast as `WeaponDropped`. Matches SF's `OnPlayerRequestingWeaponDrop` exactly.

### What's NOT forwarded yet
- `WeaponThrown` (20) / `RequestingWeaponThrow` (21) — throw mechanics. Probably need similar treatment to drop (append IDs and rebroadcast). Defer until tested.
- `OptionsChanged` (37) — lobby options sync. Mostly cosmetic between matches.
- `ObjectSpawned` (27) — host-originated; clients send `ObjectHello` (36) to request initial state, we don't handle that yet.
- `GroundWeaponsInit` (31) / `MapInfo` (32) / `MapInfoSync` (33) — map-init sync. Probably fine to skip since the client auto-syncs at scene load.

## Why boxes/barrels don't move

The user reported "physics objects don't move when bumped." Diagnosis chain:

### How SF normally drives box physics on a host
1. Host's player rig (Controller + Rigidbody chain) is in the match scene.
2. Player walks into a box → Unity physics applies collision impulse → box's `Rigidbody.velocity` changes → box's `transform.position` changes.
3. Box has a `NetworkSyncableObject` component. Its `LateUpdate` runs every frame:
   ```csharp
   if (IsNetworkMatch && matchTime >= 1f && mHasControl && mIsListening)
       TickSyncPos();  // fires every 0.2s
   ```
4. `TickSyncPos` → `SendNewObjectStatePackage` → reads current position+rotation → builds a 10-byte packet → calls `mNetworkManager.OnObjectMoved(data, channel=10)`.
5. `OnObjectMoved` → `SendMessageToAllClients(data, MsgType.ObjectUpdate, ignoreServer=true, ...)`.
6. Clients receive the ObjectUpdate, apply the new position to their local box.

### What's missing on our Path-A oracle
On stock SF this all works because the host has a local player rig that collides with boxes. **On our oracle, there is no player rig in the Desert3 scene.** The user's rig lives on their client, not on the oracle. So nothing pushes the oracle's box. The box sits at its initial position forever. `SendNewObjectStatePackage` will keep firing every 0.2s, but it'll always send the same (unchanged) position. Clients see static boxes.

### The fix shape (Phase 6.7 territory)
The oracle needs a player rig in the active scene that mirrors the connected client's position. The chain:

- Client's `PlayerUpdate` (msgType 10) packets stream at 60Hz containing the client's player position + animation state.
- The oracle's `HandlePlayerUpdate` currently extracts the position into the v25-state map but doesn't apply it to a Unity rig.
- The oracle needs to: spawn a rig (use the existing Phase 6.3 `TrySpawnPlayer` infrastructure), then teleport its hip rigidbody to the position each PlayerUpdate.
- That moving rigidbody collides with boxes → physics → `NetworkSyncableObject` broadcasts ObjectUpdate → our prefix forwards → user sees boxes move.

There's also the "we don't want the oracle to have a body" constraint the user mentioned earlier. That's about *visibility* — the rig shouldn't appear on the user's screen. Options:
- Don't spawn a `ClientSpawned` packet for the oracle rig (so the client never knows it exists).
- Or spawn it but with a "ghost" character index that the client filters.

Either way: the oracle needs a *physical* rig in its own scene, even if it's invisible to clients. The collision-against-box step is what we lose without it.

## How to verify after a live test

The plugin now logs an NSO inventory ~4s after match-start. Look for:

```
[P6.5 NSO] Inventory: N NetworkSyncableObjects found in active scene.
   Static mHasControl=true, K/N are listening (mIsListening=true).
```

Expected on Desert3: N around 5-15 (boxes, barrels, the box-stack near spawn).

If you see `mHasControl=false` — IsServer postfix didn't fire when NSO.Start ran (timing race). Fix: order the postfix install earlier or force-Update via reflection.

If you see `K=0/N` listening — `InitSyncedObjects` didn't run. Trace back: `PrepareMapForTravel(comingIn=true)` should call it. Check the Unity log for any exception in that coroutine.

Then watch for `[P6.5] HostBroadcast` lines with `msgType=26(ObjectUpdate)` — those are the boxes broadcasting their position. They'll fire even when the boxes are stationary (every 0.2s per NSO). If you see them, the protocol path works; the missing piece is collision input to make the position actually change.

## Pickup test plan

Once you're in-game on Desert3 with a gun visible:
1. Walk over the gun.
2. Check the oracle log for `[SF] Pickup: player=X weapon=Y → broadcasting WeaponWasPickedUp`.
3. Expected outcome: gun attaches to your hand and disappears from the map.

If the log line fires but you don't see the pickup on screen, the issue is the client's handling of `WeaponWasPickedUp` — possibly the client expects the weapon to be in its own `mSpawnedWeapons` dict by that point, and we need to verify the earlier `WeaponSpawned` packet was applied correctly client-side.

If the log line doesn't fire, the client isn't sending `ClientRequestingWeaponPickUp` to the oracle — likely because the client's local weapon prefab isn't triggering its trigger collider. Investigate client-side weapon prefab + Trigger setup.
