# Spawn flow — line-by-line walk

Annotated trace of what happens between "ChangeMap finishes" and "player is on the new map with their HP." This is the critical path for the 3-second-match-cycle bug.

## Step 1 — Server broadcasts MapChange

`lobbies.go:1855+ ChangeMap`. After picking the next level and updating `lobby.CurrentLevel`, the server broadcasts `packetTypeMapChange` with the embedded level data. (Hydration of the physics world happens here too, line 1921.)

`FightStartTime` is set to zero (`time.Time{}` at line 1877), so `MatchInProgress()` returns false until `StartMatch` is called.

`UnReadyAllPlayers` clears every player's `Ready` flag.

## Step 2 — Client receives MapChange

Stock SF: `P2PPackageHandler.cs:253-254` dispatches to `mNetworkHandler.OnMapChanged(data)`. The patched DLL doesn't intercept this path (verified — no `MapChange`/`OnMapChanged` harmony patches in `sf-netcodev2/SFNetcodeV2.cs`).

`OnMapChanged` (in `MultiplayerManager.cs` / Sockets variant) does the local Unity scene load, and at the end (around line 1280-1370 in the Sockets variant) registers a deferred `spawnPlayerAction` that calls `RequestSpawnPlayer(isInLobby)` once the scene is ready.

## Step 3 — Client sends ClientRequestingToSpawn

`MultiplayerManagerSockets.cs:1574-1612 RequestSpawnPlayer`:

```csharp
public void RequestSpawnPlayer(bool isInLobby)
{
    if (IsServer) { SpawnPlayer(); return; }
    if (mLocalPlayerIndex < 0) throw ...;
    Vector3 vector = ((!isInLobby) ? new Vector3(0f, 12f, 0f) : new Vector3(0f, 0f, 0f));
    Vector3 eulerAngles = Quaternion.identity.eulerAngles;
    for (byte b = 0; b < mConnectedClients.Length; b++)
    {
        if (mConnectedClients[b] != null && mConnectedClients[b].ControlledLocally)
        {
            byte[] array = new byte[25];   // 1 byte playerIndex + 6×f32
            // ... write the 25-byte payload ...
            SendP2PPacketToServer(array, P2PPackageHandler.MsgType.ClientRequestingToSpawn);
        }
    }
}
```

Sentinel position is `(0, 12, 0)` for non-lobby maps, `(0, 0, 0)` for the lobby map. **The patched DLL does not change this.**

## Step 4 — Go server receives ClientRequestingToSpawn

`server.go:457 Handle` → looks up the lobby for the source address → `lobby.Handle(packet)` → switch case at `lobbies.go:763-784`:

```go
case packetTypeClientRequestingToSpawn:
    playerIndex := int(packet.ReadByteNext())
    player := lobby.GetPlayerByIndex(playerIndex)
    if player == nil { log.Error(...); return }
    if player.Client.Addr.String() != packet.Src.String() { log.Error(...); return }
    spx := packet.ReadF32LENext(1)[0]
    spy := packet.ReadF32LENext(1)[0]
    spz := packet.ReadF32LENext(1)[0]
    srx := packet.ReadF32LENext(1)[0]
    sry := packet.ReadF32LENext(1)[0]
    srz := packet.ReadF32LENext(1)[0]
    lobby.SpawnPlayer(playerIndex, spx, spy, spz, srx, sry, srz)
```

For a non-lobby-map spawn, this calls `SpawnPlayer(playerIndex, 0, 12, 0, 0, 0, 0)`.

## Step 5 — Server `SpawnPlayer`

`lobbies.go:1497-1551`:

```go
func (lobby *Lobby) SpawnPlayer(index int, posX, posY, posZ, rotX, rotY, rotZ float32) {
    if !lobby.IsRunning() { return }
    clientIndex, playerIndex := lobby.GetIndexesByPlayerIndex(index)
    if clientIndex < 0 || playerIndex < 0 { log.Error("Unknown player ", index); return }
    if lobby.Clients[clientIndex].Players[playerIndex].Spawned {
        log.Warn("Ignoring spawn request for already spawned player ", index)
        return
    }

    flag := 0
    if !lobby.CurrentLevel.IsLobby() && lobby.GetPlayerCount(true) > 1 {
        flag = 1               // ← BUG SITE
    }

    //If the client asserted (0,0,0) AND we have dumped map data, override.
    if posX == 0 && posY == 0 && posZ == 0 && lobby.CurrentLevel != nil && lobby.CurrentLevel.Type() == 0 {
        if md, ok := loadedMaps[lobby.CurrentLevel.SceneIndex()]; ok && md != nil && len(md.PlayerSpawns) > 0 {
            s := md.PlayerSpawns[index%len(md.PlayerSpawns)]
            posX, posY, posZ = s.Pos[0], s.Pos[1], s.Pos[2]
        }
    }
    // The guard misses (0, 12, 0) — see FIX_SPAWN_FALLBACK_GUARD.md.

    packetClientSpawned := NewPacket(packetTypeClientSpawned, 0, 0)
    packetClientSpawned.Grow(30)
    packetClientSpawned.WriteByteNext(byte(index))
    packetClientSpawned.WriteF32LENext([]float32{posX, posY, posZ, rotX, rotY, rotZ})
    packetClientSpawned.WriteByteNext(byte(flag))
    packetClientSpawned.WriteI32LENext([]int32{0}) // colorCount=0
    lobby.Clients[clientIndex].Players[playerIndex].Spawned = true
    lobby.BroadcastPacket(packetClientSpawned, nil)
    log.Info("Spawned player ", index, " at position ...")

    // Auto-ready goroutine: after 3s, mark Ready and StartMatch if all ready.
    go func(playerIndex int) {
        time.Sleep(3 * time.Second)
        ...
    }(index)

    // M2/M3: spawn server-side physics entity at the same (posX, posY, posZ).
    if lobby.World != nil && index >= 0 && index < len(lobby.PlayerEntityID) {
        if prev := lobby.PlayerEntityID[index]; prev != 0 {
            lobby.World.Kill(prev)
        }
        entID := lobby.World.SpawnEntity(physics.Entity{
            Kind: physics.EntityPlayer,
            Box: physics.AABB{
                Center: physics.Vec3{X: posX, Y: posY, Z: posZ},
                Half:   physics.Vec3{X: 0.5, Y: 1.0, Z: 0.5},
            },
        })
        lobby.PlayerEntityID[index] = entID
    }
}
```

In a multiplayer match on a non-lobby map: `flag` is set to 1.

## Step 6 — Client receives ClientSpawned

`P2PPackageHandler.cs:250-252` dispatches to `mNetworkHandler.OnPlayerSpawned(data)`.

`MultiplayerManager.cs:1576-1641` / `MultiplayerManagerSockets.cs:1480+`:

```csharp
public void OnPlayerSpawned(byte[] data) {
    byte b; Vector3 vector, euler; bool flag;
    using (MemoryStream input = new MemoryStream(data)) {
        using BinaryReader binaryReader = new BinaryReader(input);
        b = binaryReader.ReadByte();
        vector.x = binaryReader.ReadSingle();
        vector.y = binaryReader.ReadSingle();
        vector.z = binaryReader.ReadSingle();
        euler.x = binaryReader.ReadSingle();
        euler.y = binaryReader.ReadSingle();
        euler.z = binaryReader.ReadSingle();
        flag = binaryReader.ReadBoolean();
        if (flag) vector = new Vector3(0f, -100f, 0f);     // ← teleport
    }
    GameObject gameObject = UnityEngine.Object.Instantiate(m_PlayerPrefab, vector, Quaternion.Euler(euler));
    // ... color setup, Controller setup, NetworkPlayer setup ...
    if (!flag) {
        mGameManager.RevivePlayer(component2);
    } else {
        gameObject.GetComponent<HealthHandler>().ForcedDie();   // ← instant death
    }
}
```

So the patched DLL: instantiates the player at `(0, -100, 0)`, then calls `ForcedDie()`.

## Step 7 — `HealthHandler.ForcedDie` propagates the kill

Stock SF `HealthHandler.ForcedDie` (decompile in `~/sf-multiplayer/refs/decompiled/Assembly-CSharp/HealthHandler.cs` — not pulled here, but inferable from the call site) sets HP to 0 and broadcasts the player's death via the regular damage path. That sends a `PlayerTookDamage` packet to the server with `damage=666.666`.

## Step 8 — Server `PlayerTookDamage`

`lobbies.go:2217-2322`. The 666.666 branch at line 2288-2308:

```go
if damage == 666.666 {
    log.Info("Player ", playerIndex, " took a killing blow from player ", attackerIndex, " of type ", damageType)
    lobby.Clients[clientIndex].Players[clientPlayerIndex].Health = 0
    lobby.Clients[clientIndex].Players[clientPlayerIndex].Stats.Deaths++
    lobby.Clients[clientIndex].Players[clientPlayerIndex].LastAttackerIndex = attackerIndex
    lobby.Clients[clientIndex].Players[clientPlayerIndex].LastDamageType = damageType
    if attackerIndex != playerIndex {
        lobby.Clients[attackerClientIndex].Players[attackerClientPlayerIndex].Stats.Kills++
    }
    lobby.BroadcastPacket(packet, packet.Src)
    lobby.CheckWinner()
    return
}
```

Sets HP=0, calls `CheckWinner`.

## Step 9 — CheckWinner → ChangeMap

`lobbies.go:1813-1853`. Iterates `lobby.GetPlayers()`, counts those with `Health > 0`. With both spawning players insta-dying, the second one to die leaves exactly one survivor → `ChangeMap(-1, survivors[0].Index)`. (If both die in the same tick, `len(survivors)==0` → `ChangeMap(-1, 255)`.)

`ChangeMap` (Step 1 again) — loop closes.

## Step 10 — Auto-ready timer triggers next match

3 seconds after the kill, the auto-ready goroutine started in Step 5 fires:

```go
go func(playerIndex int) {
    time.Sleep(3 * time.Second)
    if !lobby.IsRunning() { return }
    ci, pi := lobby.GetIndexesByPlayerIndex(playerIndex)
    if ci < 0 || pi < 0 { return }
    clients := lobby.snapshotClients()
    if ci >= len(clients) || clients[ci] == nil { return }
    clients[ci].Players[pi].Ready = true
    if !lobby.MatchInProgress() {
        allReady := true
        for _, p := range lobby.GetPlayers() {
            if p != nil && !p.Ready { allReady = false; break }
        }
        if allReady && len(lobby.GetActivePlayers()) > 0 {
            log.Info("Auto-starting match (all players auto-readied)")
            go lobby.StartMatch()
        }
    }
}(index)
```

Marks the slot Ready (the ChangeMap un-ready already happened). If both players' auto-ready timers fire, `StartMatch` is invoked. `StartMatch` sets FightStartTime, broadcasts startMatch, kicks off the GameMode's StartMatch goroutine. Each client then sends `clientRequestingToSpawn` again → back to Step 4. Loop repeats forever at ~3 seconds per cycle.

## Where to break the loop

At Step 5 — change the `flag = 1` condition so the first spawn of each round uses `flag = 0`. See `notes/design/FIX_FLAG_LOGIC.md`.
