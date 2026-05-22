package main

import (
	"errors"
	"fmt"
	"net"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/StickFightDev/StickFightDedicatedSrv/physics"
)

//Lobby holds a Stick Fight lobby
type Lobby struct {
	//We don't want race conditions with such a latent game
	sync.Mutex

	Server *Server //The server that's hosting this lobby

	//Lobby settings
	MaxPlayers         int        //The maximum amount of players allowed at any one time
	Health             byte       //The starting health of all players (enum 100, 200, 300, 1, 25, 50, 75)
	Regen              byte       //If health regeneration should be enabled
	WeaponSpawnRateMin int        //The minimum amount of seconds to wait before spawning a new weapon, 0 to disable weapon spawning
	WeaponSpawnRateMax int        //The maximum amount of seconds to wait before spawning a new weapon, 0 to disable weapon spawning
	Weapons            []Weapon   //A list of enabled weapons for this lobby
	Public             bool       //If false, requires an invitation from the lobby owner to join
	DisableSpectate    bool       //If true, disallows spectators from watching the lobby
	TourneyRules       bool       //If enabled, tourney rules will be in effect and override stock game rules
	Invited            []CSteamID //A list of invited SteamIDs
	RandomMaps         bool       //If the map rotation should be randomized or in order
	GameMode           GameMode   //The game mode of this lobby
	NextGameMode       GameMode   //The next game mode to use for this lobby
	TeamType           string     //The format of teams represented with letters beginning at A

	//Session tracker
	Running                       bool      //If the lobby is currently running
	LobbyOwner                    CSteamID  //The current owner of the lobby
	LobbyCreationTime             time.Time //The time of the lobby's creation
	LobbyRoomCode                 string    //The room code for other players to join this lobby directly
	LastTimestamp                 uint32    //The timestamp of the last packet that was accepted by the lobby as present time
	CurrentLevel                  *Level    //The currently-loaded level
	InFight                       bool      //If the match is in progress
	FightStartTime                time.Time //The match's start time
	CompletedLevelsSinceLastStats int       //The amount of matches played so far since the last time the stats map was used
	LastAppliedScale              float32   //The scale to use when managing coordinates on the map
	LastSpawnedWeaponOnLeftSide   bool      //If the last weapon was spawned on the left side or not
	LastSpawnedWeaponTime         time.Time //The last time a weapon was spawned
	CheckingWinner                bool      //Stops multiple CheckWinner calls from happening concurrently

	Clients    []*Client //The Stick Fight clients currently playing in this lobby
	Spectators []*Client //The Stick Fight clients currently spectating this lobby
	Levels     []*Level  //The Stick Fight maps to rotate through each match

	//clientsMu guards mutations of the Clients slice against concurrent reads
	//from the 30Hz tick broadcast + per-packet handler goroutines. All
	//iteration over Clients in hot paths must go through snapshotClients().
	//This replaces the recover() band-aid that previously caught the out-of-
	//bounds panics when clientLeft shrank the slice mid-iteration.
	clientsMu sync.RWMutex

	//— Phase 5 server-authoritative state (M2+) ——————————————————————
	//World is the per-lobby physics simulator. nil until first scene is loaded;
	//re-hydrated on ChangeMap to the new scene's collision geometry. Only ticks
	//when the lobby has at least one v26-aware client connected.
	World *physics.World
	//PlayerEntityID maps player slot index (0..MaxPlayers-1) to the physics
	//entity id assigned by World.SpawnEntity. -1 means no entity for that slot.
	PlayerEntityID [4]physics.EntityID
	//tickStop is closed to signal the tick goroutine to exit.
	tickStop chan struct{}
	//hasV26Clients caches whether any client in this lobby advertised v26.
	//Cheap optimization so we don't broadcast snapshots that nobody reads.
	hasV26Clients bool
	//SpawnedProjectileSyncs deduplicates server-side projectile spawns. Clients
	//resend their projectile list every playerUpdate (~50Hz); we only want to
	//spawn one entity per (player, SyncIndex). Pruned on map change.
	SpawnedProjectileSyncs map[uint32]bool
	//lastSnapshotBytes is the marshalled body of the previous worldStateSnapshot
	//we sent (minus the leading uint32 tick). If a new snapshot's body matches,
	//we skip the network send — bandwidth savings for idle lobbies. Reset on
	//forceKeyframe.
	lastSnapshotBytes []byte
	//snapshotsSinceKeyframe counts how many deltas we've sent since the last
	//full keyframe. Periodically (every 30 snapshots ≈ 1s @30Hz) we force a
	//keyframe so a client that joined late or dropped packets can catch up.
	snapshotsSinceKeyframe int
	//forceKeyframe makes the next snapshot a full keyframe (used when a client
	//joins, the map changes, or a v26 client first appears).
	forceKeyframe bool
	//Replay is an optional logger that records server-emitted snapshots and
	//events for offline playback. Nil if replayDir is unset; closed by Close().
	Replay *ReplayLogger
	//SyncableEntities maps a client-assigned object sync index (uint16) to the
	//server's physics EntityID. Populated on objectSpawned; cleared on map
	//change. Used by future server-side object physics + replay reconstruction.
	SyncableEntities map[uint16]physics.EntityID
}

//captureObjectSpawned decodes an objectSpawned packet body and registers a
//corresponding EntityDynamic in the lobby's physics world. The relay to other
//clients still happens (so client-side physics continues driving the visible
//object); this is for server-side tracking — hit detection, replay,
//eventual server-authoritative broadcasts.
//
//Wire format (per MultiplayerManager.OnObjectSpawned decompile):
//  uint16 objectIndex
//  f32 posY, f32 posZ
//  f32 rotX, f32 rotY, f32 rotZ
//  byte spawnableObjectType flags
//  if flag bit 0 (ShallSyncPosition): uint16 syncIndex
//  if flag bit 1 (Weapon): uint16 weaponSpawnID
func (lobby *Lobby) captureObjectSpawned(packet *Packet) {
	defer func() {
		if r := recover(); r != nil {
			log.Debug("captureObjectSpawned: parse recovered: ", r)
		}
	}()
	if lobby.World == nil {
		return
	}
	if packet.ByteCapacity() < 27 { // 2 + 4*5 + 1 minimum
		return
	}
	savedOffset := packet.ByteOffset()
	packet.SeekByte(0, false)
	defer packet.SeekByte(savedOffset, false)

	_ = packet.ReadU16LENext(1)[0] // objectIndex (prefab kind — informational only server-side)
	posY := packet.ReadF32LENext(1)[0]
	posZ := packet.ReadF32LENext(1)[0]
	_ = packet.ReadF32LENext(1)[0] // rotX
	_ = packet.ReadF32LENext(1)[0] // rotY
	_ = packet.ReadF32LENext(1)[0] // rotZ
	flags := packet.ReadByteNext()
	const (
		flagShallSyncPosition = 0x1
		flagWeapon            = 0x2
	)
	if flags&flagShallSyncPosition == 0 {
		return // Decorative-only spawn; no sync index, no point tracking.
	}
	if packet.ByteCapacity()-packet.ByteOffset() < 2 {
		return
	}
	syncIndex := packet.ReadU16LENext(1)[0]
	if lobby.SyncableEntities == nil {
		lobby.SyncableEntities = make(map[uint16]physics.EntityID)
	}
	//If we already have an entity for this sync index, leave it (clients sometimes
	//re-broadcast spawn packets after a hello-handshake; don't duplicate state).
	if existing, ok := lobby.SyncableEntities[syncIndex]; ok && existing != 0 {
		return
	}
	id := lobby.World.SpawnEntity(physics.Entity{
		Kind: physics.EntityDynamic,
		Box: physics.AABB{
			Center: physics.Vec3{X: 0, Y: posY, Z: posZ},
			Half:   physics.Vec3{X: 0.4, Y: 0.4, Z: 0.4}, // ballpark for crates / barrels
		},
		Meta: uint32(flags),
	})
	lobby.SyncableEntities[syncIndex] = id
	log.Trace("Captured syncable object: syncIndex=", syncIndex, " entity=", id, " at Y=", posY, " Z=", posZ)
}

//snapshotClients returns a stable copy of the Clients slice taken under the
//read lock. Iteration over the returned slice is safe even if the lobby's
//Clients slice is mutated concurrently — callers operate on the snapshot.
func (lobby *Lobby) snapshotClients() []*Client {
	lobby.clientsMu.RLock()
	defer lobby.clientsMu.RUnlock()
	out := make([]*Client, len(lobby.Clients))
	copy(out, lobby.Clients)
	return out
}

//alreadySpawnedProjectile reports whether (playerIndex, syncIndex) has been
//seen recently. First call returns false and records the pair; subsequent calls
//for the same pair return true. The map is cleared on ChangeMap.
func (lobby *Lobby) alreadySpawnedProjectile(playerIndex int, syncIndex uint16) bool {
	if lobby.SpawnedProjectileSyncs == nil {
		lobby.SpawnedProjectileSyncs = make(map[uint32]bool)
	}
	key := uint32(playerIndex&0xff)<<16 | uint32(syncIndex)
	if lobby.SpawnedProjectileSyncs[key] {
		return true
	}
	lobby.SpawnedProjectileSyncs[key] = true
	return false
}

//pickLobbyLevel returns the lobby map. We deliberately prefer Landfall scene 0
//over registered workshop IDs: the decompiled client sets isInLobby = (mapNumber == 0)
//AND tries to download any workshop map it doesn't already have, gating the local
//player's spawn behind a download that may never complete (NewMapCycleLoaded sets
//needsToDownloadMaps=true → spawnPlayerAction() is skipped → no stickman).
//Stick with Landfall scene 0; the client always has its own scenes locally.
func pickLobbyLevel() *Level {
	return newLevelLandfall(0)
}

//NewLobby retuns a new lobby
func NewLobby(srv *Server, roomCode string) (*Lobby, error) {
	if len(srv.Lobbies) >= maxLobbies {
		return nil, errors.New("too many lobbies")
	}

	if roomCode == "" {
		roomCode = LobbyRoomCode(6)
	}

	lobby := &Lobby{
		Running:            true,                                             //Mark this lobby as running
		LobbyCreationTime:  time.Now(),                                       //Set the lobby's creation time to now
		LobbyRoomCode:      roomCode,                                         //Generate the lobby's room code with 6 characters
		Server:             srv,                                              //A pointer to this lobby's host server
		MaxPlayers:         4,                                                //Default to a max of 4 players, as expected by the stock game
		WeaponSpawnRateMin: 5,                                                //Default to one weapon at least for every 5 seconds
		WeaponSpawnRateMax: 8,                                                //Default to one weapon at max for every 8 seconds
		Weapons:            tourneyWeapons,                                   //Use the known-safe tourney pool; validWeapons includes enum values whose prefabs are sometimes missing from m_WeaponObjects (Object.Instantiate(null) → ArgumentException in OnWeaponSpawned). M5: confirm mapping by dumping the player prefab's "Weapons" children.
		CurrentLevel:       pickLobbyLevel(),                                 //Default to a random lobby map (or Landfall scene 0 if none preloaded)
		LastAppliedScale:   1.0,                                              //The last applied map scaling, used to scale objects and other positions on the map
		Clients:            make([]*Client, 0),                               //Initialize the clients slice
		Levels:             defaultLevels,                                    //Default to the default levels list
		GameMode:           Stock{},                                          //Default to the Stock game mode
		Public:             defaultPublic,                                    //Whether new lobbies allow auto-join from random clients (-publicLobbies flag)
		World:              physics.NewWorld(),                               //Phase 5: per-lobby physics simulator
		tickStop:           make(chan struct{}),
		Replay:             NewReplayLogger(replayDir, roomCode),             //Phase 5 M5: optional binary replay log
	}
	for i := range lobby.PlayerEntityID {
		lobby.PlayerEntityID[i] = 0 //0 == no entity assigned yet (EntityID starts at 1)
	}

	//Hydrate the world with the initial lobby map's geometry.
	if lobby.CurrentLevel != nil && lobby.CurrentLevel.Type() == 0 {
		if md, ok := loadedMaps[lobby.CurrentLevel.SceneIndex()]; ok && md != nil {
			hydrateWorldFromMap(lobby.World, md)
		}
	}

	go lobby.runTickLoop()

	return lobby, nil
}

//hydrateWorldFromMap loads a scene's static colliders + killboxes into the
//physics world. Called from NewLobby and from ChangeMap (when a new scene is
//selected). Does not touch dynamic entities — those are added separately.
func hydrateWorldFromMap(w *physics.World, md *MapDataForLevel) {
	statics := make([]physics.AABB, 0, len(md.StaticColliders))
	for _, c := range md.StaticColliders {
		statics = append(statics, physics.AABB{
			Center: physics.Vec3{X: c.Pos[0], Y: c.Pos[1], Z: c.Pos[2]},
			Half:   physics.Vec3{X: c.Size[0] * 0.5, Y: c.Size[1] * 0.5, Z: c.Size[2] * 0.5},
		})
	}
	w.LoadStatics(statics)

	killboxes := make([]physics.AABB, 0, len(md.Killboxes))
	for _, k := range md.Killboxes {
		killboxes = append(killboxes, physics.AABB{
			Center: physics.Vec3{X: k.Pos[0], Y: k.Pos[1], Z: k.Pos[2]},
			Half:   physics.Vec3{X: k.Size[0] * 0.5, Y: k.Size[1] * 0.5, Z: k.Size[2] * 0.5},
		})
	}
	w.LoadKillboxes(killboxes)
}

//runTickLoop runs the lobby's physics simulation at 60Hz and dispatches the
//resulting events. Exits when tickStop is closed.
//
//For M2: we only Step() when at least one v26 client is present (avoids burning
//CPU on relay-only lobbies). When v26 clients are present, every tick we:
//  1. Drain queued inputs (M3+)
//  2. Step the world
//  3. Handle emitted physics events (damage, despawn, killbox death — translate
//     to serverEvent packets and broadcast to v26 clients only)
//  4. Every other tick (≈30Hz), broadcast a worldStateSnapshot
func (lobby *Lobby) runTickLoop() {
	const tickHz = 60
	dt := time.Second / tickHz
	t := time.NewTicker(dt)
	defer t.Stop()
	snapshotEvery := 2 //60Hz tick / 2 = 30Hz snapshot

	//runSafely runs fn under a recover so a single panicking tick doesn't kill
	//the whole loop. Returns true if fn ran cleanly.
	runSafely := func(label string, fn func()) (ok bool) {
		defer func() {
			if r := recover(); r != nil {
				log.Error("Lobby ", lobby.LobbyRoomCode, " tick ", label, " panicked: ", r)
				ok = false
			}
		}()
		fn()
		return true
	}

	tickCount := 0
	lastWeaponSpawn := time.Now()
	weaponSpawnWait := 5 + randomizer.Intn(4)
	for {
		select {
		case <-lobby.tickStop:
			return
		case <-t.C:
			if !lobby.IsRunning() {
				return
			}
			//Periodic weapon spawn — runs regardless of MatchInProgress so dev
			//testing (no ready-up handshake) still sees weapons appear. Stock
			//game-mode logic still drives weapon spawning during a real match;
			//this is a safety net for non-match periods.
			if lobby.WeaponSpawnRateMin > 0 && len(lobby.snapshotClients()) > 0 {
				if int(time.Since(lastWeaponSpawn).Seconds()) >= weaponSpawnWait {
					func() {
						defer func() {
							if r := recover(); r != nil {
								log.Warn("Periodic SpawnWeaponRandom recovered from panic: ", r)
							}
						}()
						lobby.SpawnWeaponRandom()
					}()
					lastWeaponSpawn = time.Now()
					weaponSpawnWait = lobby.WeaponSpawnRateMin + randomizer.Intn(lobby.WeaponSpawnRateMax-lobby.WeaponSpawnRateMin+1)
				}
			}
			if !lobby.hasV26Clients {
				continue //Skip simulation when nobody benefits from it.
			}
			if lobby.World == nil {
				continue //World not yet hydrated; wait for a map load.
			}
			var events []physics.Event
			if !runSafely("Step", func() {
				events = lobby.World.Step()
			}) {
				continue
			}
			for _, ev := range events {
				evCopy := ev
				runSafely("handlePhysicsEvent", func() {
					lobby.handlePhysicsEvent(evCopy)
				})
			}
			tickCount++
			if tickCount%snapshotEvery == 0 {
				runSafely("BroadcastWorldSnapshot", func() {
					lobby.BroadcastWorldSnapshot()
				})
			}
		}
	}
}

//handlePhysicsEvent translates a physics-layer event into protocol packets.
//M4 wires hit-detection events to damage broadcasts so both v25 (legacy
//playerTookDamage) and v26 (serverEvent) clients see consistent damage.
func (lobby *Lobby) handlePhysicsEvent(ev physics.Event) {
	switch ev.Kind {
	case physics.EventProjectileHitEntity:
		victim := lobby.playerSlotForEntity(ev.Other)
		attacker := lobby.playerSlotForEntity(ev.Entity) // projectile.OwnerID
		log.Info("Lobby ", lobby.LobbyRoomCode, " projectile hit player slot ", victim, " (from ", attacker, ")")
		if victim < 0 {
			return
		}
		//Default projectile damage placeholder. M5 will pull this from the
		//weapon table per projectile kind.
		const defaultDamage = 25.0
		lobby.broadcastServerEventDamage(victim, attacker, defaultDamage, "projectile")
		//Also fire the legacy v25 playerTookDamage so older clients react.
		lobby.DamagePlayer(victim, max0(attacker), defaultDamage, damageTypeOther, Vector2{})
	case physics.EventProjectileHitStatic:
		//No game-state effect; useful for v26 clients wanting impact VFX.
		lobby.broadcastServerEventImpact(ev.Point)
	case physics.EventPlayerKilledByKillbox:
		victim := lobby.playerSlotForEntity(ev.Entity)
		log.Info("Lobby ", lobby.LobbyRoomCode, " player slot ", victim, " hit killbox")
		if victim < 0 {
			return
		}
		//666.666 is the magic kill-by-killbox value the patched DLL handles in
		//its PlayerTookDamage receive path. Server-originated kill.
		lobby.DamagePlayer(victim, victim, 666.666, damageTypeOther, Vector2{})
		lobby.broadcastServerEventDamage(victim, -1, 666.666, "killbox")
	case physics.EventEntityDespawn:
		log.Trace("Lobby ", lobby.LobbyRoomCode, " entity ", ev.Entity, " despawned (", ev.Reason, ")")
	}
}

//playerSlotForEntity maps a physics EntityID back to the player slot index
//(0..MaxPlayers-1). Returns -1 if no slot has that entity ID.
func (lobby *Lobby) playerSlotForEntity(id physics.EntityID) int {
	if id == 0 {
		return -1
	}
	for i, e := range lobby.PlayerEntityID {
		if e == id {
			return i
		}
	}
	return -1
}

//broadcastServerEventDamage emits a serverEvent {DamageEvent, ...} to all v26
//clients in the lobby. v25 clients still get the legacy playerTookDamage.
//Wire format (M4 v26):
//  byte  eventType = 3 (DamageEvent)
//  byte  victimSlot
//  i8    attackerSlot (-1 = self/world)
//  f32   damage
//  byte  reasonLen
//  byte[reasonLen] reason (UTF-8)
func (lobby *Lobby) broadcastServerEventDamage(victimSlot, attackerSlot int, damage float32, reason string) {
	if !lobby.hasV26Clients {
		return
	}
	packet := NewPacket(packetTypeServerEvent, 0, 0)
	reasonBytes := []byte(reason)
	if len(reasonBytes) > 255 {
		reasonBytes = reasonBytes[:255]
	}
	packet.Grow(int64(8 + len(reasonBytes)))
	packet.WriteByteNext(3) // DamageEvent
	packet.WriteByteNext(byte(victimSlot))
	packet.WriteByteNext(byte(int8(attackerSlot))) // -1 sentinel preserved via two's complement
	packet.WriteF32LENext([]float32{damage})
	packet.WriteByteNext(byte(len(reasonBytes)))
	packet.WriteBytesNext(reasonBytes)
	for _, c := range lobby.snapshotClients() {
		if c != nil && !c.Closed && c.ProtocolVersion == ProtocolVersionAuthoritative {
			lobby.Server.SendPacket(packet, c.Addr)
		}
	}
}

//broadcastServerEventImpact emits a "projectile hit static" effects event for
//v26 clients (the v25 path doesn't need this — the original RayCastForward
//collision handler already plays particles locally).
func (lobby *Lobby) broadcastServerEventImpact(at physics.Vec3) {
	if !lobby.hasV26Clients {
		return
	}
	packet := NewPacket(packetTypeServerEvent, 0, 0)
	packet.Grow(13)
	packet.WriteByteNext(2) // ProjectileHitStatic
	packet.WriteF32LENext([]float32{at.X, at.Y, at.Z})
	for _, c := range lobby.snapshotClients() {
		if c != nil && !c.Closed && c.ProtocolVersion == ProtocolVersionAuthoritative {
			lobby.Server.SendPacket(packet, c.Addr)
		}
	}
}

//max0 returns x clamped to [0, ∞). Used to translate -1 attacker-slot to 0
//(player index 0) for the legacy v25 damage packet which doesn't have a
//"world / unknown attacker" sentinel.
func max0(x int) int {
	if x < 0 {
		return 0
	}
	return x
}

//BroadcastWorldSnapshot constructs a worldStateSnapshot packet from the current
//physics world state and broadcasts it to v26-aware clients only.
//Wire format (M3+):
//   uint32 serverTick
//   byte   snapType (0=keyframe, 1=delta)
//   uint16 entityCount
//   per entity:
//     uint32 EntityID
//     byte   EntityKind
//     byte   playerSlot (0..3 if Kind==EntityPlayer; 0xFF otherwise)
//     int16  posX*100 (cm)
//     int16  posY*100 (cm)
//     int16  posZ*100 (cm)
//     int16  velX*100
//     int16  velY*100
//     int16  velZ*100
//     byte   flags (bit0=alive, bit1=grounded)
//Per-entity = 4+1+1+12+1 = 19 bytes; full snapshot is ~19×N + 7 bytes header.
func (lobby *Lobby) BroadcastWorldSnapshot() {
	if !lobby.hasV26Clients {
		return
	}
	w := lobby.World
	if w == nil {
		return
	}
	//Count alive entities for the upcoming buffer size.
	alive := 0
	for _, e := range w.Entities {
		if e.Alive {
			alive++
		}
	}
	const headerSize = 7
	const perEntity = 19
	//Build the body (entity records) once into a byte slice so we can compare
	//to the previously sent body for suppression. Tick changes every snapshot
	//but doesn't represent real state change; we exclude it from the dedup
	//compare.
	body := make([]byte, 0, alive*perEntity)
	for _, e := range w.Entities {
		if !e.Alive {
			continue
		}
		slot := byte(0xFF)
		if e.Kind == physics.EntityPlayer {
			if s := lobby.playerSlotForEntity(e.ID); s >= 0 {
				slot = byte(s)
			}
		}
		body = appendU32LE(body, uint32(e.ID))
		body = append(body, byte(e.Kind))
		body = append(body, slot)
		body = appendI16LE(body, int16(e.Box.Center.X*100))
		body = appendI16LE(body, int16(e.Box.Center.Y*100))
		body = appendI16LE(body, int16(e.Box.Center.Z*100))
		body = appendI16LE(body, int16(e.Velocity.X*100))
		body = appendI16LE(body, int16(e.Velocity.Y*100))
		body = appendI16LE(body, int16(e.Velocity.Z*100))
		flags := byte(0)
		if e.Alive {
			flags |= 1
		}
		if e.Grounded {
			flags |= 2
		}
		body = append(body, flags)
	}
	const keyframeInterval = 30 //~1s @30Hz: forces a keyframe every second
	isKeyframe := lobby.forceKeyframe || lobby.lastSnapshotBytes == nil || lobby.snapshotsSinceKeyframe >= keyframeInterval
	if !isKeyframe && bytesEqual(body, lobby.lastSnapshotBytes) {
		//Identical body: skip the network send entirely. Idle lobbies emit
		//roughly nothing. Tick counter still advances; clients see no traffic
		//but their world state matches reality.
		lobby.snapshotsSinceKeyframe++
		return
	}
	packet := NewPacket(packetTypeWorldStateSnapshot, 0, 0)
	packet.Grow(int64(headerSize + len(body)))
	packet.WriteU32LENext([]uint32{uint32(w.Tick)})
	snapType := byte(0) //0=keyframe
	if !isKeyframe {
		snapType = 1 //delta (same wire shape, but flag tells client this isn't a forced full sync)
	}
	packet.WriteByteNext(snapType)
	packet.WriteU16LENext([]uint16{uint16(alive)})
	if len(body) > 0 {
		//crunch's WriteBytes is a do-while; empty slices crash it.
		packet.WriteBytesNext(body)
	}

	lobby.lastSnapshotBytes = body
	if isKeyframe {
		lobby.snapshotsSinceKeyframe = 0
		lobby.forceKeyframe = false
	} else {
		lobby.snapshotsSinceKeyframe++
	}
	//Log to replay (if enabled). We record the marshaled body so an offline
	//player can rebuild state per-tick.
	if lobby.Replay != nil {
		lobby.Replay.Append(replayKindSnapshot, body)
	}
	//Broadcast to v26 clients only.
	for _, c := range lobby.snapshotClients() {
		if c == nil || c.Closed || c.ProtocolVersion != ProtocolVersionAuthoritative {
			continue
		}
		lobby.Server.SendPacket(packet, c.Addr)
	}
}

//bytesEqual returns true if a and b are bytewise identical (including length).
func bytesEqual(a, b []byte) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if a[i] != b[i] {
			return false
		}
	}
	return true
}

func appendU32LE(b []byte, v uint32) []byte {
	return append(b, byte(v), byte(v>>8), byte(v>>16), byte(v>>24))
}

func appendI16LE(b []byte, v int16) []byte {
	u := uint16(v)
	return append(b, byte(u), byte(u>>8))
}

//RecomputeV26Status updates the cached hasV26Clients flag. Call when client
//list or per-client ProtocolVersion changes.
func (lobby *Lobby) RecomputeV26Status() {
	any := false
	for _, c := range lobby.snapshotClients() {
		if c != nil && !c.Closed && c.ProtocolVersion == ProtocolVersionAuthoritative {
			any = true
			break
		}
	}
	lobby.hasV26Clients = any
}

//IsRunning returns true if the lobby is currently running
func (lobby *Lobby) IsRunning() bool {
	lobby.Lock()
	defer lobby.Unlock()
	return lobby.Running
}

//Close closes the lobby
func (lobby *Lobby) Close() {
	if !lobby.IsRunning() {
		return
	}

	log.Info("Closing lobby!")

	for _, client := range lobby.Spectators {
		client.Close()
	}
	for _, client := range lobby.snapshotClients() {
		if client != nil {
			client.Close()
		}
	}
	lobby.MaxPlayers = 0
	lobby.CurrentLevel = nil
	lobby.FightStartTime = time.Time{}
	lobby.CompletedLevelsSinceLastStats = 0
	lobby.clientsMu.Lock()
	lobby.Clients = nil
	lobby.clientsMu.Unlock()
	lobby.Spectators = nil
	lobby.Levels = nil
	lobby.Running = false
	//Flush the replay log before the lobby goes away.
	if lobby.Replay != nil {
		lobby.Replay.Close()
		lobby.Replay = nil
	}
	//Stop the tick goroutine so it doesn't leak when the lobby is closed.
	if lobby.tickStop != nil {
		select {
		case <-lobby.tickStop:
			//already closed
		default:
			close(lobby.tickStop)
		}
	}
}

//BroadcastPacket broadcasts a packet to every client in the lobby, except ignoreAddr if specified
func (lobby *Lobby) BroadcastPacket(packet *Packet, ignoreAddr *net.UDPAddr) {
	if !lobby.IsRunning() {
		return
	}

	clients := lobby.snapshotClients()
	for clientIndex := 0; clientIndex < len(clients); clientIndex++ {
		if clients[clientIndex] != nil {
			if ignoreAddr != nil && ignoreAddr.String() == clients[clientIndex].Addr.String() {
				continue //Ignore this address
			}
			lobby.Server.SendPacket(packet, clients[clientIndex].Addr)
		}
	}

	for clientIndex := 0; clientIndex < len(lobby.Spectators); clientIndex++ {
		if lobby.Spectators[clientIndex] != nil {
			lobby.Server.SendPacket(packet, lobby.Spectators[clientIndex].Addr)
		}
	}

	if packet.ShouldLog() {
		log.Trace("Broadcasted packet: ", packet)
	}
}

//IsTeamed checks if two player indexes are on the same team
func (lobby *Lobby) IsTeamed(pi1, pi2 int) bool {
	switch lobby.TeamType {
		case "ab":
			for i := 0; i < len(lobby.GetPlayers()); i += 2 {
				if i == pi1 && i+1 == pi2 { return true }
				if i == pi2 && i+1 == pi1 { return true }
			}
		case "ac":
			for i := 0; i < len(lobby.GetPlayers()); i += 2 {
				if i == pi1 && i+2 == pi2 { return true }
				if i == pi2 && i+2 == pi1 { return true }
			}
	}

	return false
}

//Handle handles a packet in the lobby
func (lobby *Lobby) Handle(packet *Packet) {
	//Top-level recovery: any handler panic shouldn't crash the server. We log
	//and continue so other lobbies / clients are unaffected.
	defer func() {
		if r := recover(); r != nil {
			log.Error("Lobby.Handle panicked on packet from ", packet.Src, ": ", r)
		}
	}()
	if !lobby.IsRunning() {
		return
	}

	if packet.ShouldCheckTime() {
		//Check the timestamp!
		if packet.Timestamp < lobby.LastTimestamp {
			log.Warn("Packet from ", packet.Src, " too old: ", packet)
			return
		}
		/*
			if packet.Timestamp > uint32(time.Now().Unix()) {
				log.Warn("Packet from ", packet.Src, " too new: ", packet)
				return
			}
		*/
		lobby.LastTimestamp = packet.Timestamp
	}

	switch packet.Type {
	case packetTypePing:
		if packet.SteamID.ID != 0 {
			_, sourceClient := lobby.GetClientByAddr(packet.Src)
			targetClient := lobby.GetClientBySteamID(packet.SteamID)
			if sourceClient != nil && targetClient != nil {
				packet.SteamID = sourceClient.SteamID
				lobby.Server.SendPacket(packet, targetClient.Addr)
			}
		} else {
			packet.Type = packetTypePingResponse
			lobby.Server.SendPacket(packet, packet.Src)
		}

	case packetTypePingResponse:
		if packet.SteamID.ID != 0 {
			_, sourceClient := lobby.GetClientByAddr(packet.Src)
			targetClient := lobby.GetClientBySteamID(packet.SteamID)
			if sourceClient != nil && targetClient != nil {
				packet.SteamID = sourceClient.SteamID
				lobby.Server.SendPacket(packet, targetClient.Addr)
			}
		}

	case packetTypeClientRequestingToSpawn:
		playerIndex := int(packet.ReadByteNext())
		player := lobby.GetPlayerByIndex(playerIndex)
		if player == nil {
			log.Error("Unable to spawn invalid player ", playerIndex)
			return
		}
		if player.Client.Addr.String() != packet.Src.String() {
			log.Error("Client ", packet.Src, " is trying to spawn player ", playerIndex, " from client ", player.Client.Addr)
			return
		}

		//Patched DLL sends 25 bytes: byte playerIndex + float32×6 (posX,posY,posZ,rotX,rotY,rotZ).
		//Previously we only read 4 floats and scrambled them as (posX,posY,rotX,rotY), so the
		//player respawned at world position (X,Y,0) instead of (X,Y,Z) — off-map on most scenes.
		spx := packet.ReadF32LENext(1)[0]
		spy := packet.ReadF32LENext(1)[0]
		spz := packet.ReadF32LENext(1)[0]
		srx := packet.ReadF32LENext(1)[0]
		sry := packet.ReadF32LENext(1)[0]
		srz := packet.ReadF32LENext(1)[0]
		lobby.SpawnPlayer(playerIndex, spx, spy, spz, srx, sry, srz)

	case packetTypeLobbyType:
		_, client := lobby.GetClientByAddr(packet.Src)
		playerIndex := client.Players[0].Index

		if lobby.IsOwner(client.SteamID) {
			flag := int(packet.ReadByteNext())
			switch flag {
			case 1: //Friends only
				lobby.Public = false
				lobby.PlayerSaid(playerIndex, "Set lobby to private!")
			case 2: //Public
				lobby.Public = true
				lobby.PlayerSaid(playerIndex, "Set lobby to public!")
			default:
				lobby.PlayerSaid(playerIndex, "Unhandled lobby type %d!", flag)
			}
		} else {
			lobby.PlayerSaid(playerIndex, "No permissions!")
		}

	case packetTypeClientReadyUp:
		lobby.ReadyUp(packet)

	case packetTypeStartMatch:
		lobby.StartMatch()

	case packetTypeKickPlayer, packetTypeClientLeft:
		_, client := lobby.GetClientByAddr(packet.Src)
		if client != nil {
			lobby.KickClientBySteamID(client.SteamID.ID)
		}

	case packetTypePlayerTalked:
		lobby.PlayerTalked(packet)

	case packetTypePlayerUpdate:
		lobby.PlayerUpdate(packet)

	case packetTypePlayerInput:
		lobby.PlayerInput(packet)

	case packetTypePlayerTookDamage:
		lobby.PlayerTookDamage(packet)

	case packetTypePlayerFallOut:
		lobby.PlayerFallOut(packet)

	case packetTypePlayerForceAdded:
		lobby.BroadcastPacket(packet, packet.Src)

	case packetTypePlayerForceAddedAndBlock:
		lobby.BroadcastPacket(packet, packet.Src)

	case packetTypePlayerLavaForceAdded:
		lobby.BroadcastPacket(packet, packet.Src)

	case packetTypeClientRequestingWeaponDrop:
		nextWeaponSpawnID := lobby.GetNextWeaponSpawnID(false)
		nextObjectSpawnID := lobby.GetNextObjectSpawnID(false)

		packet.Type = packetTypeWeaponDropped
		packet.Grow(4)
		packet.WriteU16LENext([]uint16{nextWeaponSpawnID, nextObjectSpawnID})

		log.Info("Weapon ", int(packet.ReadByte(0x0)), " was dropped!")
		lobby.BroadcastPacket(packet, nil)

	case packetTypeClientRequestingWeaponPickUp:
		playerIndex := int(packet.ReadByteNext())
		weaponSpawnID := packet.ReadU16LENext(1)[0]

		if weapon, ok := lobby.CurrentLevel.SpawnedWeapons[weaponSpawnID]; ok && weapon != nil {
			packet.Type = packetTypeWeaponWasPickedUp

			log.Info("Player ", playerIndex, " picked up weapon ", weaponSpawnID, "!")
			lobby.BroadcastPacket(packet, nil)
		} else {
			log.Error("Player ", playerIndex, " tried to pick up invalid weapon ", weaponSpawnID, "!")
		}

	//Object-physics relay. Stock SF P2P had every client subscribe to object
	//updates from the "object host" (usually whoever last interacted, or the
	//room host on Photon). Our centralized server can't know who's authoritative
	//without a real ownership protocol, so for now we just relay these packets
	//to every OTHER client in the lobby. The sender's local physics still runs
	//(SF clients always run object physics locally), so as long as at least one
	//client touches an object, the others see its motion.
	//Phase 5 M5 next step: server-side object simulation with stable ownership.
	case packetTypeObjectSpawned:
		//Capture server-side state for the spawned object (M5 work-in-progress).
		//Wire format (matches MultiplayerManager.OnObjectSpawned decompile):
		//  uint16 objectIndex
		//  f32 posY, f32 posZ
		//  f32 rotX, f32 rotY, f32 rotZ
		//  byte spawnableObjectType flags (bit 0 = ShallSyncPosition, bit 1 = Weapon)
		//  if ShallSyncPosition: uint16 syncIndex
		//  if Weapon: uint16 weaponSpawnID
		lobby.captureObjectSpawned(packet)
		lobby.BroadcastPacket(packet, packet.Src)

	case packetTypeObjectUpdate,
		packetTypeObjectSimpleDestruction,
		packetTypeObjectInvokeDestructionEvent,
		packetTypeObjectDestructionCollision,
		packetTypeGroundWeaponsInit,
		packetTypeObjectHello:
		lobby.BroadcastPacket(packet, packet.Src)

	case packetTypeClientRequestingWeaponThrow:
		nextWeaponSpawnID := lobby.GetNextWeaponSpawnID(false)
		nextObjectSpawnID := lobby.GetNextObjectSpawnID(false)

		packet.Type = packetTypeWeaponThrown
		packet.Grow(4)
		packet.WriteU16LE(packet.ByteCapacity()-4, []uint16{nextWeaponSpawnID, nextObjectSpawnID})

		log.Info("Weapon ", int(packet.ReadByte(0x0)), " was thrown!")
		lobby.BroadcastPacket(packet, nil)

	default:
		log.Error(fmt.Sprintf("Unhandled packet from %s: %s", packet.Src, packet))
	}
}

//GetMaxHealth returns the maximum and starting health of a player
func (lobby *Lobby) GetMaxHealth() float32 {
	switch lobby.Health {
	case 0:
		return 100
	case 1:
		return 200
	case 2:
		return 300
	case 3:
		return 1
	case 4:
		return 25
	case 5:
		return 50
	case 6:
		return 75
	}

	return 0
}

//GetNextWeaponSpawnID returns the next available weaponSpawnID
func (lobby *Lobby) GetNextWeaponSpawnID(beginFromEnd bool) uint16 {
	if !lobby.IsRunning() {
		return 0
	}

	if lobby.CurrentLevel.SpawnedWeapons == nil {
		lobby.CurrentLevel.SpawnedWeapons = make(map[uint16]*SyncableWeapon)
	}

	weaponSpawnID := uint16(65534)
	if beginFromEnd {
		weaponSpawnID = uint16(len(lobby.CurrentLevel.SpawnedWeapons))
	}

	for {
		//log.Trace("Trying weapon spawn ID ", weaponSpawnID)
		if _, ok := lobby.CurrentLevel.SpawnedWeapons[weaponSpawnID]; !ok {
			break
		}

		if beginFromEnd {
			weaponSpawnID--
		} else {
			weaponSpawnID++
		}
	}

	lobby.CurrentLevel.SpawnedWeapons[weaponSpawnID] = &SyncableWeapon{}
	return weaponSpawnID
}

//GetNextObjectSpawnID returns the next available objectSpawnID
func (lobby *Lobby) GetNextObjectSpawnID(beginFromEnd bool) uint16 {
	if !lobby.IsRunning() {
		return 0
	}

	if lobby.CurrentLevel.SpawnedObjects == nil {
		lobby.CurrentLevel.SpawnedObjects = make(map[uint16]*SyncableObject)
	}

	objectSpawnID := uint16(65534)
	if beginFromEnd {
		objectSpawnID = uint16(len(lobby.CurrentLevel.SpawnedObjects))
	}

	for {
		//log.Trace("Trying object spawn ID ", objectSpawnID)
		if _, ok := lobby.CurrentLevel.SpawnedObjects[objectSpawnID]; !ok {
			break
		}

		if beginFromEnd {
			objectSpawnID--
		} else {
			objectSpawnID++
		}
	}

	lobby.CurrentLevel.SpawnedObjects[objectSpawnID] = &SyncableObject{}
	return objectSpawnID
}

//GetPlayerCount returns how many players are in this lobby
func (lobby *Lobby) GetPlayerCount(excludeSelf bool) int {
	playerCount := 0
	for _, client := range lobby.snapshotClients() {
		if client != nil {
			playerCount += client.GetPlayerCount()
		}
	}
	if excludeSelf && playerCount > 0 {
		playerCount--
	}
	return playerCount
}

//GetPlayersTooMany returns true if the current player count plus the playersToAdd count exceeds the lobby's maximum player setting
func (lobby *Lobby) GetPlayersTooMany(playersToAdd int, excludeSelf bool) bool {
	if !lobby.IsRunning() {
		return true
	}

	return lobby.GetPlayerCount(excludeSelf)+playersToAdd > lobby.MaxPlayers
}

//GetPlayers returns the current player list in order of playerIndex
func (lobby *Lobby) GetPlayers() []*Player {
	if lobby == nil {
		return make([]*Player, 0)
	}
	clients := lobby.snapshotClients()
	if len(clients) == 0 {
		return make([]*Player, 0)
	}

	players := make(map[int]*Player)
	for clientIndex := 0; clientIndex < len(clients); clientIndex++ {
		if clients[clientIndex] == nil {
			continue
		}
		for playerIndex := 0; playerIndex < clients[clientIndex].GetPlayerCount(); playerIndex++ {
			players[clients[clientIndex].Players[playerIndex].Index] = clients[clientIndex].Players[playerIndex]
		}
	}

	playerList := make([]*Player, lobby.MaxPlayers)
	for playerIndex := 0; playerIndex < lobby.MaxPlayers; playerIndex++ {
		if player, ok := players[playerIndex]; ok {
			playerList[playerIndex] = player
		} else {
			playerList[playerIndex] = nil
		}
	}

	return playerList
}

//GetActivePlayers returns the current player list
func (lobby *Lobby) GetActivePlayers() []*Player {
	playerList := make([]*Player, 0)
	for _, player := range lobby.GetPlayers() {
		if player != nil {
			playerList = append(playerList, player)
		}
	}
	return playerList
}

//GetPlayerByIndex returns the player with a matching index
func (lobby *Lobby) GetPlayerByIndex(index int) *Player {
	if lobby.Clients == nil || len(lobby.Clients) == 0 {
		return nil
	}

	players := lobby.GetPlayers()
	if index >= len(players) {
		return nil
	}

	return players[index]
}

//GetNextPlayerIndex returns the next available playerIndex
func (lobby *Lobby) GetNextPlayerIndex() int {
	if !lobby.IsRunning() {
		return -1
	}

	if lobby.GetPlayersTooMany(1, true) {
		return -1
	}
	if lobby.Clients == nil || len(lobby.Clients) == 0 {
		return -1
	}

	usedIndexes := make(map[int]bool)
	for clientIndex := 0; clientIndex < len(lobby.Clients); clientIndex++ {
		for playerIndex := 0; playerIndex < lobby.Clients[clientIndex].GetPlayerCount(); playerIndex++ {
			if lobby.Clients[clientIndex].Players[playerIndex].Index > -1 {
				usedIndexes[lobby.Clients[clientIndex].Players[playerIndex].Index] = true
			}
		}
	}

	nextPlayerIndex := 0
	for {
		if lobby.GetPlayersTooMany(1, true) {
			return -1
		}
		if isUsed, ok := usedIndexes[nextPlayerIndex]; ok && isUsed {
			nextPlayerIndex++
			continue
		}
		break
	}

	log.Trace("Next player index: ", nextPlayerIndex)
	return nextPlayerIndex
}

//GetClientByAddr returns the client with a matching address
func (lobby *Lobby) GetClientByAddr(addr *net.UDPAddr) (int, *Client) {
	for clientIndex, client := range lobby.snapshotClients() {
		if client != nil && client.Addr.String() == addr.String() {
			return clientIndex, client
		}
	}
	return -1, nil
}

//GetClientBySteamID returns the client with a matching SteamID
func (lobby *Lobby) GetClientBySteamID(steamID CSteamID) *Client {
	for _, client := range lobby.snapshotClients() {
		if client != nil && client.SteamID.CompareCSteamID(steamID) {
			return client
		}
	}
	return nil
}

//GetClientBySteamUsername returns the client with a matching Steam username
func (lobby *Lobby) GetClientBySteamUsername(steamUsername string) *Client {
	for _, client := range lobby.snapshotClients() {
		if client == nil {
			continue
		}
		if client.SteamID.GetUsername() == steamUsername {
			return client
		}
		if client.SteamID.GetNormalizedUsername() == steamUsername {
			return client
		}
	}
	return nil
}

//GetIndexesByPlayerIndex returns the index of the client list and the index of the client's player list that matches the specified player index
func (lobby *Lobby) GetIndexesByPlayerIndex(index int) (int, int) {
	if lobby.Clients == nil || len(lobby.Clients) == 0 {
		return -1, -1
	}

	for clientIndex := 0; clientIndex < len(lobby.Clients); clientIndex++ {
		for playerIndex := 0; playerIndex < lobby.Clients[clientIndex].GetPlayerCount(); playerIndex++ {
			if lobby.Clients[clientIndex].Players[playerIndex].Index == index {
				return clientIndex, playerIndex
			}
		}
	}

	return -1, -1
}

//KickClientBySteamID kicks all clients from the lobby that have a matching SteamID
func (lobby *Lobby) KickClientBySteamID(steamID uint64) {
	if !lobby.IsRunning() {
		return
	}

	if lobby.Clients == nil || len(lobby.Clients) == 0 {
		return
	}

	for clientIndex := 0; clientIndex < len(lobby.Clients); clientIndex++ {
		if lobby.Clients[clientIndex].SteamID.CompareSteamID(steamID) {
			lobby.ClientRemoveByClientIndex(clientIndex)
		}
	}
}

//IsInvited returns true if the specified SteamID was invited to the server
func (lobby *Lobby) IsInvited(steamID uint64) bool {
	if !lobby.IsRunning() {
		return false
	}

	if lobby.Public {
		return true
	}

	if len(lobby.GetPlayers()) == 0 {
		return true
	}

	for _, invited := range lobby.Invited {
		if invited.CompareSteamID(steamID) {
			return true
		}
	}
	return false
}

//IsOwner returns true if the specified SteamID is the owner of the lobby
func (lobby *Lobby) IsOwner(steamID CSteamID) bool {
	if !lobby.IsRunning() {
		return false
	}

	return lobby.LobbyOwner.CompareCSteamID(steamID)
}

//ClientInit initializes a client and returns an error if it fails
func (lobby *Lobby) ClientInit(packet *Packet) error {
	if !lobby.IsRunning() {
		return errors.New("lobby not running")
	}

	packet.SeekByte(0, false) //Seek to the start of the packet data

	steamID := packet.ReadU64LENext(1)[0] //Read in the SteamID
	lobby.KickClientBySteamID(steamID)    //Remove this player from the lobby if they currently exist in it

	//Make sure this player is allowed in the lobby
	if !lobby.IsInvited(steamID) {
		return fmt.Errorf("not invited to this lobby")
	}

	clientPlayerCount := int(packet.ReadByteNext())        //Read in the requested player count
	//if lobby.GetPlayersTooMany(clientPlayerCount, false) { //Check to see if there's enough open spots in the lobby
	//	return fmt.Errorf("unable to add %d players to lobby with %d/%d players", clientPlayerCount)
	//}

	protocolVersion := int(packet.ReadByteNext()) //Read in the client's protocol version
	//v25 = legacy patched DLL (relay-mode behavior). v26 = SFNetcodeV2-patched
	//(server-authoritative). Anything else: reject.
	if protocolVersion != ProtocolVersionLegacy && protocolVersion != ProtocolVersionAuthoritative {
		return fmt.Errorf("protocol version %d is unsupported", protocolVersion)
	}
	log.Info("ClientInit: ", packet.Src, " advertised protocol v", protocolVersion)

	newClient := NewClient(lobby, packet.Src, steamID, clientPlayerCount, packet) //Create a new client to host the new players
	newClient.ProtocolVersion = protocolVersion
	if lobby.GetPlayersTooMany(clientPlayerCount, false) { //Check to see if there's enough open spots in the lobby
		if lobby.DisableSpectate {
			return fmt.Errorf("unable to add %d players to lobby with %d/%d players", clientPlayerCount, len(lobby.GetPlayers()), lobby.MaxPlayers)
		}
		lobby.SpectatorAdd(newClient) //Add the new client to the lobby's spectator list
	} else {
		lobby.ClientAdd(newClient) //Add the new client to the lobby's player list
	}

	//Initialize the client. Layout (empirically what the patched DLL parses):
	//   accept(1) | playerIndex(1) | maxPlayers(1) | mapType(1) | mapSize:i32(4) | mapData(...)
	//   then 4× {slotSteamID:u64; if non-empty non-local: 52 bytes of stats} | weaponCount:u16 | settings(4)
	//Note: the decompiled stock MultiplayerManager.InitDataFromServerRecieved shows no maxPlayers byte,
	//but empirically removing it breaks the client (clientRequestingToSpawn never arrives → black screen).
	//Either the patched DLL has its own InitData impl not visible in the decompile, or the byte routes
	//through a different code path. Keeping the maxPlayers byte until we have a clean repro to debug.
	packetClientInit := NewPacket(packetTypeClientInit, 0, 0)
	packetClientInit.Grow(8 + int64(lobby.CurrentLevel.Size()))
	packetClientInit.WriteByteNext(0x1)
	packetClientInit.WriteByteNext(byte(newClient.Players[0].Index))
	packetClientInit.WriteByteNext(byte(lobby.MaxPlayers))
	packetClientInit.WriteByteNext(lobby.CurrentLevel.Type())
	packetClientInit.WriteI32LENext([]int32{lobby.CurrentLevel.Size()})
	packetClientInit.WriteBytesNext(lobby.CurrentLevel.Data())

	lobbyPlayers := lobby.GetPlayers()
	for i := 0; i < len(lobbyPlayers); i++ {
		packetClientInit.Grow(8)
		if lobbyPlayers[i] != nil {
			packetClientInit.WriteU64LENext([]uint64{lobbyPlayers[i].Client.SteamID.ID})
			if lobbyPlayers[i].Client.SteamID.ID != 0 && lobbyPlayers[i].Client.Addr.String() != packet.Src.String() {
				packetClientInit.Grow(52)
				pStats := lobbyPlayers[i].Stats
				packetClientInit.WriteI32LENext([]int32{
					pStats.Wins, pStats.Kills, pStats.Deaths, pStats.Suicides, pStats.Falls,
					pStats.CrownSteals,
					pStats.BulletsHit, pStats.BulletsMissed, pStats.BulletsShot,
					pStats.Blocks, pStats.PunchesLanded,
					pStats.WeaponsPickedUp, pStats.WeaponsThrown,
				})
				//Per-slot colorCount = 0 (no per-player color customization).
				//Without this the patched DLL reads our weaponCount/settings bytes as
				//color data, tries to alloc a multi-GB byte array, and OOMs the coroutine
				//(see Unity output_log.txt: "Reading color count" / "Reading color data (<huge>)").
				packetClientInit.Grow(4)
				packetClientInit.WriteI32LENext([]int32{0})
			}
		} else {
			packetClientInit.WriteU64LENext([]uint64{0})
		}
	}

	//TODO: Weapons
	packetClientInit.Grow(2)
	packetClientInit.WriteU16LENext([]uint16{0}) //How many weapons to spawn

	//Lobby settings
	packetClientInit.Grow(4)
	packetClientInit.WriteBytesNext([]byte{
		0, //Still not entirely sure, gets assigned to OptionsHolder.maps on the client and no issues when set to 0
		lobby.Health,
		lobby.Regen,
		2, //Set weapon spawn rate to 2 so clients don't request to spawn weapons
	})


	//Send the clientInit packet!
	lobby.Server.SendPacket(packetClientInit, packet.Src)
	log.Info("Initialized client ", packet.Src, " for ", clientPlayerCount, " players")

	//Send the workshop map cycle to the client
	lobby.WorkshopMapsLoaded(packet.Src)

	//Push the "world is ready" bundle. The patched DLL's OnMapDataRecieved expects
	//a Vector2 (8 bytes) + variable payload — our previous SendMapInfoSync was wrong-format
	//(EndOfStreamException in MultiplayerManager.OnMapDataRecieved per Unity output_log.txt)
	//so it's removed. GroundWeaponsInit + optionsChanged are still cheap and well-formed.
	lobby.GroundWeaponsInit()              //No-ops cleanly when PlacedWeapons is empty
	lobby.SendOptionsChanged(packet.Src)   //type 37: mirror clientInit's lobby settings tail

	return nil
}

//SendMapInfoSync tells a client that the map state is synced. The body format
//is currently a best-guess single-zero-byte ack — if the DLL's OnMapInfoRecieved
//expects more, we'll see it ignore this and we tune the payload then.
func (lobby *Lobby) SendMapInfoSync(addr *net.UDPAddr) {
	if !lobby.IsRunning() {
		return
	}
	packet := NewPacket(packetTypeMapInfoSync, 0, 0)
	packet.Grow(1)
	packet.WriteByteNext(0x0)
	lobby.Server.SendPacket(packet, addr)
}

//SendOptionsChanged sends the current lobby settings as a server-originated
//optionsChanged packet. Layout mirrors the 4-byte settings tail of clientInit:
//[maps, health, regen, weaponSpawnRate]
func (lobby *Lobby) SendOptionsChanged(addr *net.UDPAddr) {
	if !lobby.IsRunning() {
		return
	}
	packet := NewPacket(packetTypeOptionsChanged, 0, 0)
	packet.Grow(4)
	packet.WriteBytesNext([]byte{
		0,
		lobby.Health,
		lobby.Regen,
		2, //Match the clientInit tail — weapon spawn rate "2" suppresses client weapon-spawn requests
	})
	lobby.Server.SendPacket(packet, addr)
}

//ClientAdd adds the specified client to the lobby as one or more players
func (lobby *Lobby) ClientAdd(client *Client) {
	if lobby.GetPlayersTooMany(client.GetPlayerCount(), false) {
		return
	}

	if len(lobby.Clients) == 0 {
		lobby.LobbyOwner = client.SteamID
		lobby.Server.SendPacket(NewPacket(packetTypeRequestingOptions, 0, 0), client.Addr)
	}

	//Add the client to the list of available clients
	lobby.clientsMu.Lock()
	lobby.Clients = append(lobby.Clients, client)
	lobby.clientsMu.Unlock()

	//Initialize each of the players in the client
	for clientPlayer := 0; clientPlayer < client.GetPlayerCount(); clientPlayer++ {
		playerIndex := lobby.GetNextPlayerIndex()
		client.Players[clientPlayer].Index = playerIndex             //Set the next player index for this player
		lobby.ClientJoined(client.Addr, playerIndex, client.SteamID) //Tell the lobby that this client has joined
	}

	//Phase 5: update v26-client cache so the tick loop knows whether to simulate.
	lobby.RecomputeV26Status()
	//Force a keyframe on the next snapshot since a client just joined.
	lobby.forceKeyframe = true
}

//SpectatorAdd adds the specified client to the lobby as a spectator
func (lobby *Lobby) SpectatorAdd(client *Client) {
	if !lobby.GetPlayersTooMany(client.GetPlayerCount(), false) {
		lobby.ClientAdd(client) //There's enough open spots, try to let the client play instead
		return
	}

	lobby.Spectators = append(lobby.Spectators, client)
}

//ClientRemoveByClientIndex removes the specified client from the lobby
func (lobby *Lobby) ClientRemoveByClientIndex(clientIndex int) {
	if !lobby.IsRunning() {
		return
	}

	//Make sure this client actually exists
	if clientIndex < 0 || clientIndex >= len(lobby.Clients) {
		return
	}

	//Get the SteamID of the client
	steamID := lobby.Clients[clientIndex].SteamID

	//Close the client
	lobby.Clients[clientIndex].Close()

	if len(lobby.Clients) > 0 {
		lobby.ClientLeft(steamID) //Tell the other players that this client left

		//Remove the client from the lobby
		lobby.clientsMu.Lock()
		if len(lobby.Clients) > clientIndex {
			lobby.Clients[clientIndex] = nil                                 //Nullify the client
			copy(lobby.Clients[clientIndex:], lobby.Clients[clientIndex+1:]) //Shift every client after this client left by one
			lobby.Clients = lobby.Clients[:len(lobby.Clients)-1]             //Remove the last element
		}
		lobby.clientsMu.Unlock()
	} else {
		lobby.Close() //Close the lobby, since there's no more players
	}
	//Phase 5: recompute v26 cache after the client list changes.
	lobby.RecomputeV26Status()
}

//ClientJoined broadcasts to the lobby that the specified player is now part of this lobby
func (lobby *Lobby) ClientJoined(addr *net.UDPAddr, playerIndex int, steamID CSteamID) {
	if lobby == nil {
		return
	}

	packetClientJoined := NewPacket(packetTypeClientJoined, 0, 0)
	packetClientJoined.Grow(9)
	packetClientJoined.WriteByteNext(byte(playerIndex))
	packetClientJoined.WriteU64LENext([]uint64{steamID.ID})
	lobby.BroadcastPacket(packetClientJoined, addr)
	log.Info("Client ", steamID, " joined the lobby!")
}

//ClientLeft broadcasts to the lobby that the specified SteamID is no longer part of this lobby
func (lobby *Lobby) ClientLeft(steamID CSteamID) {
	if lobby == nil {
		return
	}

	packetClientLeft := NewPacket(packetTypeClientLeft, 0, 0)
	packetClientLeft.SteamID = steamID
	lobby.BroadcastPacket(packetClientLeft, nil)
	log.Info("Client ", steamID, " left the lobby!")

	if lobby.LobbyOwner.CompareCSteamID(steamID) {
		lobbyPlayers := lobby.GetActivePlayers()
		if len(lobbyPlayers) > 0 {
			lobby.LobbyOwner = lobbyPlayers[0].Client.SteamID
			log.Info("New lobby owner: ", lobby.LobbyOwner)
		} else {
			lobby.Close()
		}
	}
}

//WorkshopMapsLoaded sends the workshop map cycle to the specified client, or broadcasts if nil
func (lobby *Lobby) WorkshopMapsLoaded(addr *net.UDPAddr) {
	workshopMaps := make([]uint64, 0)
	for i := 0; i < len(lobby.Levels); i++ {
		if lobby.Levels[i].Type() == 2 {
			workshopMaps = append(workshopMaps, lobby.Levels[i].steamWorkshopID)
		}
	}
	for i := 0; i < len(lobbyLevels); i++ {
		if lobbyLevels[i].Type() == 2 {
			workshopMaps = append(workshopMaps, lobbyLevels[i].steamWorkshopID)
		}
	}
	if len(workshopMaps) > 0 {
		packetWorkshopMapsLoaded := NewPacket(packetTypeWorkshopMapsLoaded, 1, 0)
		packetWorkshopMapsLoaded.Grow(2 + int64(len(workshopMaps)*8)) //Grow by 2 bytes for workshop map count, then 8 bytes per map
		packetWorkshopMapsLoaded.WriteU16LENext([]uint16{uint16(len(workshopMaps))})
		packetWorkshopMapsLoaded.WriteU64LENext(workshopMaps)

		if addr != nil {
			lobby.Server.SendPacket(packetWorkshopMapsLoaded, addr)
		} else {
			lobby.BroadcastPacket(packetWorkshopMapsLoaded, nil)
		}
	}
}

//SpawnPlayer spawns the specified player at the specified coordinates
func (lobby *Lobby) SpawnPlayer(index int, posX, posY, posZ, rotX, rotY, rotZ float32) {
	if !lobby.IsRunning() {
		return
	}

	clientIndex, playerIndex := lobby.GetIndexesByPlayerIndex(index)
	if clientIndex < 0 || playerIndex < 0 {
		log.Error("Unknown player ", index)
		return
	}

	if lobby.Clients[clientIndex].Players[playerIndex].Spawned {
		log.Warn("Ignoring spawn request for already spawned player ", index)
		return
	}

	//flag = 0 means "revive player at the asserted position" (normal round
	//start). flag = 1 means "teleport to (0,-100,0) and ForcedDie()" —
	//intended for true late-joiners who arrive mid-match and have to wait for
	//next round. The previous condition (`GetPlayerCount(true) > 1`) fired on
	//EVERY round-start spawn in multiplayer matches, telling the patched DLL
	//to instakill both players → 3-second-match-cycle bug. See
	//notes/recon/BUG_3SEC_MATCH_CYCLE.md + notes/design/FIX_FLAG_LOGIC.md.
	flag := 0
	pl := lobby.Clients[clientIndex].Players[playerIndex]
	if !lobby.CurrentLevel.IsLobby() && lobby.MatchInProgress() && pl.SpawnedThisRound {
		//Real late-joiner: this slot has already spawned this round and is
		//asking to spawn again — force them to wait for next round.
		flag = 1
	}

	//If the client asserted (0, *, 0) the patched DLL is using its sentinel
	//spawn position — (0,0,0) for the lobby, (0,12,0) for non-lobby maps —
	//meaning "let the server pick." Override with the dumped map's spawn point
	//for this slot so the player lands on a platform.
	if posX == 0 && posZ == 0 && lobby.CurrentLevel != nil && lobby.CurrentLevel.Type() == 0 {
		if md, ok := loadedMaps[lobby.CurrentLevel.SceneIndex()]; ok && md != nil && len(md.PlayerSpawns) > 0 {
			s := md.PlayerSpawns[index%len(md.PlayerSpawns)]
			posX = s.Pos[0]
			posY = s.Pos[1]
			posZ = s.Pos[2]
		}
	}

	//Layout the patched DLL actually parses (from Unity output_log.txt "Reading..." traces):
	//  index(1) | posX:f32 | posY:f32 | posZ:f32 | rotX:f32 | rotY:f32 | rotZ:f32 | spawnFlag:bool(1) | colorCount:i32
	//The original code stopped at spawnFlag (26 bytes); the patched client also reads a
	//color-count int32 right after, then EndOfStreamException out if it's missing.
	//Sending colorCount=0 closes the loop without sending per-color customization data.
	packetClientSpawned := NewPacket(packetTypeClientSpawned, 0, 0)
	packetClientSpawned.Grow(30)
	packetClientSpawned.WriteByteNext(byte(index))
	packetClientSpawned.WriteF32LENext([]float32{
		posX, posY, posZ,
		rotX, rotY, rotZ,
	})
	packetClientSpawned.WriteByteNext(byte(flag))
	packetClientSpawned.WriteI32LENext([]int32{0}) //colorCount = 0 (no per-player color customization)

	lobby.Clients[clientIndex].Players[playerIndex].Spawned = true
	lobby.Clients[clientIndex].Players[playerIndex].SpawnedThisRound = true

	lobby.BroadcastPacket(packetClientSpawned, nil)
	log.Info("Spawned player ", index, " at position {X:", posX, " Y:", posY, " Z:", posZ, "} with rotation {X:", rotX, " Y:", rotY, " Z:", rotZ, "} using flag ", flag)

	//Auto-ready newly-spawned players so the match starts without each client
	//having to send a ClientReadyUp packet. Dev/testing convenience: in real
	//play stock SF clients send the readyup themselves, but the goldberg-faked
	//2nd instance never does. After a brief delay (so all expected players
	//have time to spawn) we kick off the match.
	go func(playerIndex int) {
		time.Sleep(3 * time.Second)
		if !lobby.IsRunning() {
			return
		}
		ci, pi := lobby.GetIndexesByPlayerIndex(playerIndex)
		if ci < 0 || pi < 0 {
			return
		}
		clients := lobby.snapshotClients()
		if ci >= len(clients) || clients[ci] == nil {
			return
		}
		clients[ci].Players[pi].Ready = true
		//If everyone is now ready and we're not yet in a match, start it.
		if !lobby.MatchInProgress() {
			allReady := true
			for _, p := range lobby.GetPlayers() {
				if p != nil && !p.Ready {
					allReady = false
					break
				}
			}
			if allReady && len(lobby.GetActivePlayers()) > 0 {
				log.Info("Auto-starting match (all players auto-readied)")
				go lobby.StartMatch()
			}
		}
	}(index)

	//Phase 5 M2/M3: also spawn the player as a server-side physics entity so the
	//simulation has someone to collide projectiles with and (M3) drive movement
	//for. Player AABB half-extents are tuned to roughly match the stickman's
	//standing-up profile (0.5m wide × 1m tall × 0.5m deep).
	if lobby.World != nil && index >= 0 && index < len(lobby.PlayerEntityID) {
		//Despawn any prior entity for this slot (in case of respawn).
		if prev := lobby.PlayerEntityID[index]; prev != 0 {
			lobby.World.Kill(prev)
		}
		//SF map spawn points represent the player's FEET position (where
		//Unity's local-origin "ground" pivot is for the player prefab). The
		//physics AABB center is at the CENTER of the body; if we put center
		//= feet pos, the player's bottom half is embedded in the platform
		//and the sweep collision returns "already overlapping" → either
		//teleport-through or stuck-grounded. Lift center by Half.Y so the
		//feet land at the actual spawn point.
		const playerHalfY = 1.0
		entID := lobby.World.SpawnEntity(physics.Entity{
			Kind: physics.EntityPlayer,
			Box: physics.AABB{
				Center: physics.Vec3{X: posX, Y: posY + playerHalfY, Z: posZ},
				Half:   physics.Vec3{X: 0.5, Y: playerHalfY, Z: 0.5},
			},
		})
		lobby.PlayerEntityID[index] = entID
	}
}

//PlayerInput applies a v26 client's input frame to the server-side player
//entity. Packet body layout (matches what SFNetcodeV2.dll will emit):
//  byte  playerIndex
//  f32   stickX, stickY
//  f32   aimX, aimY
//  u16   buttons (PlayerButton bitmask)
//  u32   sequence (for reconciliation; logged for M3 debug, otherwise unused
//                  for the M2 stub which only acts on movement+jump)
//
//Anticheat: clamps stick magnitudes to [-1, 1] and applies a rough rate limit.
//Inputs that arrive faster than 80/sec from one client are dropped (legitimate
//clients send at 60Hz; 80 gives a generous margin for jitter / network bursts).
func (lobby *Lobby) PlayerInput(packet *Packet) {
	defer func() {
		if r := recover(); r != nil {
			log.Error("PlayerInput panicked: ", r, " for packet from ", packet.Src)
		}
	}()
	if !lobby.IsRunning() {
		return
	}
	if lobby.World == nil {
		return
	}
	_, client := lobby.GetClientByAddr(packet.Src)
	if client == nil {
		return
	}
	//Only v26 clients should be sending playerInput; if v25 sent it, ignore.
	if client.ProtocolVersion != ProtocolVersionAuthoritative {
		return
	}
	//Rate limit (anticheat): refuse > 80 inputs/sec from one client.
	now := time.Now()
	if !client.InputRateBudget(now) {
		log.Trace("playerInput rate-limited for client ", packet.Src)
		return
	}
	if packet.ByteCapacity() < 23 {
		log.Trace("playerInput packet too short (", packet.ByteCapacity(), " bytes)")
		return
	}
	packet.SeekByte(0, false)
	playerIndex := int(packet.ReadByteNext())
	if playerIndex < 0 || playerIndex >= len(lobby.PlayerEntityID) {
		return
	}
	entID := lobby.PlayerEntityID[playerIndex]
	if entID == 0 {
		return //Player hasn't spawned in the world yet.
	}
	in := physics.PlayerInput{
		MovementX: clamp(packet.ReadF32LENext(1)[0], -1, 1),
		MovementY: clamp(packet.ReadF32LENext(1)[0], -1, 1),
		AimX:      clamp(packet.ReadF32LENext(1)[0], -1, 1),
		AimY:      clamp(packet.ReadF32LENext(1)[0], -1, 1),
		Buttons:   physics.PlayerButton(packet.ReadU16LENext(1)[0]),
		Sequence:  packet.ReadU32LENext(1)[0],
	}
	physics.ApplyPlayerInput(lobby.World, entID, in, physics.DefaultPlayerSimParams())
}

//clamp returns v clamped to [lo, hi].
func clamp(v, lo, hi float32) float32 {
	if v < lo {
		return lo
	}
	if v > hi {
		return hi
	}
	return v
}

//ReadyUp marks a player as ready
func (lobby *Lobby) ReadyUp(packet *Packet) {
	if !lobby.IsRunning() {
		return
	}

	playerCount := int(packet.ReadByteNext())
	for i := 0; i < playerCount; i++ {
		playerIndex := int(packet.ReadByteNext())
		clientIndex, clientPlayerIndex := lobby.GetIndexesByPlayerIndex(playerIndex)
		if clientIndex <= -1 || clientPlayerIndex <= -1 {
			continue
		}
		if !lobby.Clients[clientIndex].Paused { //If the client is marked as paused, don't accept the automatic ready-up
			lobby.Clients[clientIndex].Players[clientPlayerIndex].Ready = true
		}
	}

	if lobby.MatchInProgress() {
		lobby.Server.SendPacket(NewPacket(packetTypeStartMatch, 0, 0), packet.Src)
	} else {
		go lobby.StartMatch()
	}
}

//StartMatch starts the match if all players are ready
func (lobby *Lobby) StartMatch() {
	if !lobby.IsRunning() {
		return
	}

	/*if lobby.CurrentLevel.IsLobby() {
		log.Warn("Can't start match on lobby map!")
		return
	}*/

	if lobby.MatchInProgress() {
		log.Warn("Can't start match when already in fight!")
		return
	}

	notReady := false
	players := lobby.GetPlayers()
	for _, player := range players {
		if player != nil && !player.Ready {
			lobby.PlayerSaid(player.Index, "Either my internet or PC is slow, sorry!")
			notReady = true
		}
	}

	if notReady {
		log.Warn("Can't start match until all players are ready!")
		return
	}

	//time.Sleep(time.Second * 3)

	//TODO: Send list of pre-spawned weapons
	//TODO: Start goroutines for each object to track

	//Initialize the map
	lobby.InitMap()

	//Reset player data
	for i := 0; i < len(lobby.Clients); i++ {
		if lobby.Clients[i].GetPlayerCount() > 0 {
			for j := 0; j < len(lobby.Clients[i].Players); j++ {
				lobby.Clients[i].Players[j].Health = lobby.GetMaxHealth()
				//Clear per-round spawn tracking so the next clientRequestingToSpawn
				//is treated as a fresh round-start (flag=0), not a late-joiner.
				lobby.Clients[i].Players[j].SpawnedThisRound = false
			}
		}
	}

	switch lobby.NextGameMode.(type) {
	case Stock:
		switch lobby.GameMode.(type) {
		case Stock:
		default:
			lobby.GameMode = Stock{}
			log.Trace("-- Set game mode to Stock")
		}
	case Tournament:
		switch lobby.GameMode.(type) {
		case Tournament:
		default:
			lobby.GameMode = Tournament{}
			log.Trace("-- Set game mode to Tournament")
		}
	case Duel:
		switch lobby.GameMode.(type) {
		case Duel:
		default:
			lobby.GameMode = Duel{}
			log.Trace("-- Set game mode to Duel")
		}
	case GunGame:
		switch lobby.GameMode.(type) {
		case GunGame:
		default:
			lobby.GameMode = GunGame{}
			log.Trace("-- Set game mode to Gun Game")
		}
	}

	lobby.FightStartTime = time.Now()
	lobby.BroadcastPacket(NewPacket(packetTypeStartMatch, 0, 0), nil)
	log.Info("Started match!")

	go lobby.GameMode.StartMatch(lobby)
}

//MatchInProgress returns true if the match is in progress. Previously this
//took lobby.Lock() (the embedded sync.Mutex), which deadlocked any caller
//that was already holding the lock. The check just compares a time.Time to
//the zero value; if writers (StartMatch, ChangeMap, Close) hold lobby.Lock
//while modifying FightStartTime, a concurrent torn read here is fine — we
//only return true/false, and the bool is monotonic per match.
func (lobby *Lobby) MatchInProgress() bool {
	return !lobby.FightStartTime.IsZero()
}

//UnReadyAllPlayers unreadies every player
func (lobby *Lobby) UnReadyAllPlayers() {
	if !lobby.IsRunning() {
		return
	}

	for i := 0; i < len(lobby.Clients); i++ {
		if lobby.Clients[i].GetPlayerCount() > 0 {
			for j := 0; j < len(lobby.Clients[i].Players); j++ {
				lobby.Clients[i].Players[j].Ready = false
			}
		}
	}
}

//CheckWinner checks to see if the specified playerIndex is the winner, and starts a new match if they are
func (lobby *Lobby) CheckWinner() {
	if !lobby.IsRunning() {
		return
	}

	if !lobby.CurrentLevel.IsLobby() {
		if !lobby.MatchInProgress() {
			return
		}
	}

	if lobby.CheckingWinner {
		return
	}
	lobby.CheckingWinner = true

	survivors := make([]*Player, 0)

	for _, pl := range lobby.GetPlayers() {
		if pl != nil {
			if pl.Health > 0 {
				survivors = append(survivors, pl)
			}
		}
	}

	log.Trace("-----\n\nPlayers: ", lobby.GetPlayers(), "\n\nSurvivors: ", survivors, "\n\n")

	if len(survivors) == 1 {
		log.Info("Player ", survivors[0].Index, " is the winner!")
		lobby.ChangeMap(-1, survivors[0].Index)
	}

	if len(survivors) == 0 {
		log.Info("No one survived!")
		lobby.ChangeMap(-1, 255)
	}

	lobby.CheckingWinner = false
}

//ChangeMap changes the map and declares the winner
func (lobby *Lobby) ChangeMap(mapIndex, winnerIndex int) {
	if !lobby.IsRunning() {
		return
	}

	/*
		If a real winnerIndex is specified, and the match hasn't even started yet (which is prior to every client sending clientReadyUp),
		that means that a glitch occurred somewhere with the map change and caused it to be initiated twice, which can cause lag due to
		double map loads when the server broadcasts both, because a real map change should only occur once while the match is still in
		progress (as this function will end the match), and also because if you use the /map command to change the map before the last
		match could begin, the winnerIndex will be set to 255, which means no one won!

		The solution: If the match was ended already, and the winnerIndex is a real player who won somehow before the next match started,
		don't allow the map change.
	*/
	if !lobby.CurrentLevel.IsLobby() {
		if !lobby.MatchInProgress() && winnerIndex != 255 {
			return
		}
	}

	lobby.FightStartTime = time.Time{}
	lobby.UnReadyAllPlayers()

	//Wait for the game mode to finish processing the match
	for !lobby.GameMode.IsDone() {
		if lobby.GameMode.IsDone() {
			break
		}
	}

	lobby.CompletedLevelsSinceLastStats++

	//Support gamemode-required level playlists
	levelPlaylist := lobby.Levels
	if len(lobby.GameMode.GetLevels()) > 0 {
		levelPlaylist = lobby.GameMode.GetLevels()
	}

	if mapIndex < 0 || mapIndex >= len(levelPlaylist) {
		if !lobby.TourneyRules && lobby.CompletedLevelsSinceLastStats >= 30 {
			lobby.CompletedLevelsSinceLastStats = 0
			lobby.CurrentLevel = newLevelLandfall(102)
		} else {
			lobby.CurrentLevel = levelPlaylist[randomizer.Intn(len(levelPlaylist)-1)]
		}
	} else {
		lobby.CurrentLevel = levelPlaylist[mapIndex]
	}

	packetMapChange := NewPacket(packetTypeMapChange, 0, 0)
	packetMapChange.Grow(2)
	packetMapChange.WriteByteNext(byte(winnerIndex))
	packetMapChange.WriteByteNext(lobby.CurrentLevel.Type())
	packetMapChange.Grow(int64(lobby.CurrentLevel.Size()))
	packetMapChange.WriteBytesNext(lobby.CurrentLevel.Data())

	lobby.BroadcastPacket(packetMapChange, nil)
	log.Info("Changed map: ", lobby.CurrentLevel)

	//Phase 5 M2: re-hydrate the physics world from the new scene's dumped data,
	//and clear any per-match dynamic entities (projectiles, etc.) that were left
	//over from the previous map.
	if lobby.World != nil && lobby.CurrentLevel != nil && lobby.CurrentLevel.Type() == 0 {
		if md, ok := loadedMaps[lobby.CurrentLevel.SceneIndex()]; ok && md != nil {
			hydrateWorldFromMap(lobby.World, md)
		}
		//Reap transient entities — projectiles + thrown weapons shouldn't survive
		//a map change. Players (kind 1) we keep so M3 can re-position them.
		for i := range lobby.World.Entities {
			e := &lobby.World.Entities[i]
			if e.Kind != physics.EntityPlayer {
				e.Alive = false
			}
		}
	}
	//Clear the per-projectile dedup map so new SyncIndex values for the next
	//match aren't shadowed by stale entries.
	lobby.SpawnedProjectileSyncs = nil
	//Drop the syncable-object index → entity mapping; the next map's objects
	//get fresh sync indices from the client.
	lobby.SyncableEntities = nil
	//Force a snapshot keyframe so clients see the new map's entity baseline,
	//and discard the old body so suppression doesn't accidentally skip the
	//first divergent snapshot.
	lobby.lastSnapshotBytes = nil
	lobby.snapshotsSinceKeyframe = 0
	lobby.forceKeyframe = true
	//REVERTED: auto-respawn-on-map-change broadcasted clientSpawned for every
	//player after each map change. The patched DLL spawns the player on its
	//own via its local OnMapChanged → handles spawn-point selection internally;
	//our server-side broadcast was causing the client to render a SECOND
	//copy of each player. The original short-match-cycle problem this was
	//trying to fix has to be diagnosed differently (probably a different
	//missing packet or wrong fight-state byte).
}

//TempMap assigns a temporary Landfall map to the fight
func (lobby *Lobby) TempMap(sceneIndex int32, winnerIndex int) {
	if !lobby.IsRunning() {
		return
	}

	lobby.FightStartTime = time.Time{}
	lobby.UnReadyAllPlayers()

	lobby.CurrentLevel = newLevelLandfall(sceneIndex)

	packetMapChange := NewPacket(packetTypeMapChange, 0, 0)
	packetMapChange.Grow(2)
	packetMapChange.WriteByteNext(byte(winnerIndex))
	packetMapChange.WriteByteNext(lobby.CurrentLevel.Type())
	packetMapChange.Grow(int64(lobby.CurrentLevel.Size()))
	packetMapChange.WriteBytesNext(lobby.CurrentLevel.Data())

	lobby.BroadcastPacket(packetMapChange, nil)
	log.Info("Changed map temporarily: ", lobby.CurrentLevel)
}

//InitMap initializes the map before it can begin
func (lobby *Lobby) InitMap() {
	if !lobby.IsRunning() {
		return
	}

	//Change the map scaling
	if lobby.CurrentLevel.MapSize > 0 {
		lobby.ChangeMapSize(lobby.CurrentLevel.MapSize)
	} else {
		lobby.ChangeMapSize(1)
	}

	//Initialize the ground weapons
	lobby.GroundWeaponsInit()

	//Initialize the map objects
	//lobby.MapObjectsInit()
}

//ChangeMapSize is called when the map size changes, so that anything which needs to scale can be scaled
func (lobby *Lobby) ChangeMapSize(newSize float32) {
	if !lobby.IsRunning() {
		return
	}

	lobby.LastAppliedScale = newSize / 10.0
}

//GroundWeaponsInit reads the current level's list of placed weapons and tells the connected clients that they're pre-spawned
func (lobby *Lobby) GroundWeaponsInit() {
	if !lobby.IsRunning() {
		return
	}

	placedWeapons := lobby.CurrentLevel.PlacedWeapons
	if len(placedWeapons) > 0 {
		packetGroundWeaponsInit := NewPacket(packetTypeGroundWeaponsInit, 0, 0)
		packetGroundWeaponsInit.Grow(2 + int64(len(placedWeapons)*12)) //Grow by 2 bytes for count, then 12 bytes per weapon
		packetGroundWeaponsInit.WriteU16LENext([]uint16{uint16(len(placedWeapons))})
		for i := 0; i < len(placedWeapons); i++ {
			weapon := placedWeapons[i]
			packetGroundWeaponsInit.WriteF32LENext([]float32{weapon.PositionX, weapon.PositionY})
			packetGroundWeaponsInit.WriteU16LENext([]uint16{lobby.GetNextWeaponSpawnID(false), lobby.GetNextObjectSpawnID(true)})
		}

		lobby.BroadcastPacket(packetGroundWeaponsInit, nil)
		log.Debug("Initialized ground weapons: ", placedWeapons)
	}
}

//PlayerUpdate syncs a player's network position and weapon
func (lobby *Lobby) PlayerUpdate(packet *Packet) { //420 IQ level strats here, buckle up
	//Defensive: bad packets / concurrent race against client churn would
	//otherwise crash the server (Phase 5 brought new code paths that touch
	//the World concurrently with the lobby state). We log and continue.
	defer func() {
		if r := recover(); r != nil {
			log.Error("PlayerUpdate panicked: ", r, " for packet from ", packet.Src)
		}
	}()
	if !lobby.IsRunning() {
		return
	}

	/*
		if !lobby.CurrentLevel.IsLobby() {
			if !lobby.MatchInProgress() {
				return
			}
		}
	*/

	clientIndex, client := lobby.GetClientByAddr(packet.Src) //Get the client index that this playerUpdate packet is from
	if client == nil {
		return
	}

	//The update channel is calculated as (playerIndex * 2) + 2, so reverse it to get the playerIndex
	playerIndex := (packet.Channel - 2) / 2
	if playerIndex <= -1 || playerIndex >= lobby.MaxPlayers { //Return if it's not a valid playerIndex
		return
	}

	//Get the client's player index by finding the client that holds a player with the matching playerIndex
	_, clientPlayerIndex := lobby.GetIndexesByPlayerIndex(playerIndex)

	//Make sure we aren't a damn fool
	if clientIndex <= -1 || clientPlayerIndex <= -1 {
		return
	}

	//Send the playerUpdate packet out before processing it, rip if it's invalid
	lobby.BroadcastPacket(packet, packet.Src)

	netPosition := NetworkPosition{
		Position:     Vector3{Y: float32(packet.ReadI16LENext(1)[0]) / 100.0, Z: float32(packet.ReadI16LENext(1)[0]) / 100.0}, //Read in the position of the player
		Rotation:     Vector2{float32(packet.ReadByteNext()) / 100.0, float32(packet.ReadByteNext()) / 100.0},                 //Read in the rotation axis of the player
		YValue:       float32(packet.ReadByteNext()) / 100.0,                                                                  //Read in the player's YValue (known to be 100 for holding the up key, 156 for holding the down key, unknown for controllers)
		MovementType: MovementType(packet.ReadByteNext()),                                                                     //Read in the movement type of the player
	}

	netWeapon := NetworkWeapon{
		FightState: FightState(packet.ReadByteNext()), //Read in the fight state of the player
	}

	projectileCount := packet.ReadU16LENext(1)[0]           //Read in the amount of projectiles to read
	projectiles := make([]Projectile, int(projectileCount)) //We max out at 256 projectiles, for now...
	if len(projectiles) > 0 {                               //If we have projectiles available to read
		for i := 0; i < len(projectiles); i++ { //Loop over the projectiles that we can store
			//Read in the data about the projectile
			projectiles[i].ShootPosition = Vector2{float32(packet.ReadI16LENext(1)[0]), float32(packet.ReadI16LENext(1)[0])}
			projectiles[i].Shoot = Vector2{float32(packet.ReadByteNext()), float32(packet.ReadByteNext())}
			projectiles[i].SyncIndex = packet.ReadU16LENext(1)[0]
		}
	}
	netWeapon.Projectiles = projectiles
	if projectileCount > 256 { //If we maxed out at 256 projectiles and have more to be read
		packet.SeekByte((int64(projectileCount)*8)-int64(256), true) //Seek ahead so that the weapon type can be correctly read
		//TODO: Store pages of projectiles or find an alternative indexing that supports uint16 or int16 indexes
	}
	netWeapon.Weapon = Weapon(packet.ReadByteNext()) //Read in the player's current weapon

	//Phase 5 M4: spawn a server-side projectile entity for each newly-asserted
	//projectile. The Shoot field is the projectile's direction; we synthesize a
	//muzzle velocity from it. This gives us hit detection against player AABBs
	//even when the only clients in the lobby are v25 (relay-mode).
	//
	//Note: clients fire many projectiles per second (full-auto weapons). We
	//throttle by SyncIndex — only spawn a server-side entity for SyncIndex
	//values we haven't seen for this player in the last few seconds. The
	//SpawnedProjectileSyncs map is per-lobby and pruned on map change.
	if lobby.World != nil && len(projectiles) > 0 {
		ownerEnt := lobby.PlayerEntityID[playerIndex%len(lobby.PlayerEntityID)]
		muzzle := physics.Vec3{
			X: 0,
			Y: netPosition.Position.Y,
			Z: netPosition.Position.Z,
		}
		now := time.Now()
		shooter := lobby.Clients[clientIndex]
		for _, p := range projectiles {
			if lobby.alreadySpawnedProjectile(playerIndex, p.SyncIndex) {
				continue
			}
			//Anticheat: cap unique projectiles per shooter per second.
			if shooter != nil && !shooter.ProjectileRateBudget(now) {
				log.Warn("Anticheat: dropped projectile from player ", playerIndex, " (rate-limited)")
				continue
			}
			//Direction stored in Shoot (sbyte, scaled /100 by client convention).
			//Use it as a unit-ish vector and scale to a default projectile speed.
			const defaultProjectileSpeed = 30.0
			dir := physics.Vec3{X: 0, Y: p.Shoot.Y / 100.0, Z: p.Shoot.X / 100.0}.Normalized()
			lobby.World.SpawnEntity(physics.Entity{
				Kind:     physics.EntityProjectile,
				Box:      physics.AABB{Center: muzzle, Half: physics.Vec3{X: 0.1, Y: 0.1, Z: 0.1}},
				Velocity: dir.Scale(defaultProjectileSpeed),
				OwnerID:  ownerEnt,
				TTLTicks: 120, // ~2 seconds at 60Hz
				Meta:     uint32(netWeapon.Weapon),
			})
		}
	}

	//Phase 5 M5 anticheat — position plausibility check. Threshold needs
	//calibration against real SF coordinate scales; the first deployed value
	//(5m) clamped honest movement during local testing. Set to a deliberately-
	//loose 80m for now (sqrt(6400) ≈ obvious-teleport territory) and only
	//log — no clamping — until we have golden-replay-derived numbers.
	const maxStepSqPerUpdate = 80.0 * 80.0
	if pl := lobby.Clients[clientIndex].Players[clientPlayerIndex]; pl != nil {
		prev := pl.Position.Position
		dy := netPosition.Position.Y - prev.Y
		dz := netPosition.Position.Z - prev.Z
		distSq := dy*dy + dz*dz
		if prev != (Vector3{}) && distSq > maxStepSqPerUpdate {
			log.Warn("Anticheat: large position jump for player ", playerIndex, " (", distSq, "m² in one update) — likely a teleport / lag spike (logged, not clamped)")
		}
	}

	//Here's the strat in action
	lobby.Clients[clientIndex].Players[clientPlayerIndex].Position = netPosition
	lobby.Clients[clientIndex].Players[clientPlayerIndex].Weapon = netWeapon

	//Phase 5: mirror the client-asserted position into the server-side physics
	//entity for this player so projectile hit-tests and killbox checks work
	//against a current position. For v25 clients this is the only way the server
	//knows where the player is; for v26 clients this is overridden by the
	//physics sim driven by playerInput packets.
	if lobby.World != nil && playerIndex >= 0 && playerIndex < len(lobby.PlayerEntityID) {
		if entID := lobby.PlayerEntityID[playerIndex]; entID != 0 {
			if e := lobby.World.Get(entID); e != nil {
				//Map SF wire coords (posY = lateral, posZ = depth-ish) to our
				//world axes: world Y stays up (use the netPosition's Y as Y is
				//SF's "lateral" but we treat world Z as lateral and world Y as
				//up; SF compresses XYZ to YZ via plane). For now: mirror Y to
				//world Y for matching height, Z to world Z for lateral.
				e.Box.Center.Y = netPosition.Position.Y
				e.Box.Center.Z = netPosition.Position.Z
			}
		}
	}

	if logPlayerUpdate { //It's really spammy, trust me
		log.Debug(
			"Player ", playerIndex, ": ",
			"Position(", netPosition.Position, ") Rotation(", netPosition.Rotation, ") YValue:", netPosition.YValue, " Movement: ", netPosition.MovementType,
			" Fight:", netWeapon.FightState, " Weapon:", netWeapon.Weapon, " Projectiles:", projectiles,
		)
	}
}

func (lobby *Lobby) DamagePlayer(damagee, attacker int, damage float32, damageType DamageType, particleDirection Vector2) {
	log.Warn("Player ", damagee, " took ", damage, " damage from player ", attacker, " of type ", damageType)
	packet := NewPacket(packetTypePlayerTookDamage, (damagee * 2) + 2, 0)
	packet.Grow(14)
	packet.WriteByteNext(byte(attacker))
	packet.WriteF32LENext([]float32{damage, particleDirection.X, particleDirection.Y})
	packet.WriteByteNext(byte(damageType))

	damageeClientIndex, _ := lobby.GetIndexesByPlayerIndex(damagee)
	if damageeClientIndex < 0 {
		log.Warn("DamagePlayer: no client found for player index ", damagee)
		return
	}
	clients := lobby.snapshotClients()
	if damageeClientIndex >= len(clients) || clients[damageeClientIndex] == nil {
		log.Warn("DamagePlayer: client index ", damageeClientIndex, " out of range or nil")
		return
	}
	victim := clients[damageeClientIndex]
	//v26 clients get the authoritative serverEvent damage path instead — sending
	//both legacy + serverEvent would double-count. Skip the legacy packet for
	//v26 victims; M4 hit-validation only emits the legacy packet to v25 clients.
	if victim.ProtocolVersion == ProtocolVersionAuthoritative {
		return
	}
	lobby.Server.SendPacket(packet, victim.Addr)
}

//PlayerTookDamage syncs a player willingly admitting that they took damage
func (lobby *Lobby) PlayerTookDamage(packet *Packet) {
	if !lobby.IsRunning() {
		return
	}

	if !lobby.CurrentLevel.IsLobby() {
		if !lobby.MatchInProgress() {
			return
		}
	}

	_, client := lobby.GetClientByAddr(packet.Src) //Get the client index that this playerUpdate packet is from
	if client == nil {
		return
	}

	//The update channel is calculated as (playerIndex * 2) + 2, so reverse it to get the playerIndex
	playerIndex := (packet.Channel - 2) / 2
	if playerIndex <= -1 || playerIndex >= lobby.MaxPlayers { //Return if it's not a valid playerIndex
		return
	}

	//Get the client's player index by finding the client that holds a player with the matching playerIndex
	clientIndex, clientPlayerIndex := lobby.GetIndexesByPlayerIndex(playerIndex)

	//Make sure we aren't a damn fool
	if clientIndex <= -1 || clientPlayerIndex <= -1 {
		return
	}

	attackerIndex := int(packet.ReadByteNext())
	attackerClientIndex, attackerClientPlayerIndex := lobby.GetIndexesByPlayerIndex(attackerIndex)
	if attackerClientIndex <= -1 || attackerClientPlayerIndex <= -1 {
		return
	}

	//Snapshot the Clients slice once so a concurrent ClientRemove can't reslice
	//under us mid-function. Pointer derefs below operate on the snapshot.
	clients := lobby.snapshotClients()
	if clientIndex >= len(clients) || attackerClientIndex >= len(clients) {
		return
	}
	victimClient := clients[clientIndex]
	attackerClient := clients[attackerClientIndex]
	if victimClient == nil || attackerClient == nil {
		return
	}
	if clientPlayerIndex >= len(victimClient.Players) || attackerClientPlayerIndex >= len(attackerClient.Players) {
		return
	}
	victim := victimClient.Players[clientPlayerIndex]
	attacker := attackerClient.Players[attackerClientPlayerIndex]
	if victim == nil || attacker == nil {
		return
	}

	if attacker.Health <= 0 {
		return
	}

	damage := packet.ReadF32LENext(1)[0]
	particleDirection := Vector2{}
	if playParticles := packet.ReadByteNext(); playParticles == 1 {
		particleDirection.X = packet.ReadF32LENext(1)[0]
		particleDirection.Y = packet.ReadF32LENext(1)[0]
	}
	damageType := damageTypeOther
	if packet.ByteOffset() < packet.ByteCapacity() {
		damageType = DamageType(packet.ReadByteNext())
	}

	//Make sure this player isn't already dead
	if victim.Health <= 0 {
		log.Warn("Player ", playerIndex, " took damage despite being dead!")
		//return
	}

	//Make sure the player is ready if this isn't the lobby map
	if !victim.Ready && !lobby.CurrentLevel.IsLobby() {
		log.Warn("Player ", playerIndex, " took damage despite not being ready!")
		return
	}

	if lobby.IsTeamed(playerIndex, attackerIndex) {
		log.Warn("Player ", playerIndex, " avoided ", damage, " damage from ", attackerIndex, " thanks to friendly fire! (Team state: ", lobby.TeamType, ")")
		for i := 0; i < 5; i++ {
			lobby.DamagePlayer(playerIndex, attackerIndex, damage * -1, damageType, particleDirection) //"Heal" the player by dealing negative damage
		}
		return
	}

	if damage == 666.666 {
		log.Info("Player ", playerIndex, " took a killing blow from player ", attackerIndex, " of type ", damageType)

		//Kill the targeted player
		victim.Health = 0
		victim.Stats.Deaths++
		victim.LastAttackerIndex = attackerIndex
		victim.LastDamageType = damageType

		//Give the attacker a kill
		if attackerIndex != playerIndex {
			attacker.Stats.Kills++
		}

		//Broadcast the damage
		lobby.BroadcastPacket(packet, packet.Src)

		//Check for the winner
		lobby.CheckWinner()
		return
	}

	log.Info("Player ", playerIndex, " took ", damage, " damage from player ", attackerIndex, " of type ", damageType)

	//Remove the specified health from the player
	victim.Health -= damage

	//Broadcast the damage
	lobby.BroadcastPacket(packet, packet.Src)
	//lobby.BroadcastPacket(packet, nil)

	if victim.Health <= 0 {
		lobby.CheckWinner()
	}
}

//PlayerFallOut syncs a player falling out of bounds
func (lobby *Lobby) PlayerFallOut(packet *Packet) {
	if !lobby.IsRunning() {
		return
	}

	if !lobby.CurrentLevel.IsLobby() {
		if !lobby.MatchInProgress() {
			return
		}
	}

	//The update channel is calculated as (playerIndex * 2) + 2, so reverse it to get the playerIndex
	playerIndex := (packet.Channel - 2) / 2
	if playerIndex <= -1 || playerIndex >= lobby.MaxPlayers { //Return if it's not a valid playerIndex
		return
	}

	//Get the client's player index by finding the client that holds a player with the matching playerIndex
	clientIndex, clientPlayerIndex := lobby.GetIndexesByPlayerIndex(playerIndex)

	//Make sure we aren't a damn fool
	if clientIndex <= -1 || clientPlayerIndex <= -1 {
		return
	}

	if len(lobby.GetPlayers()) == 1 {
		lobby.ChangeMap(-1, 255)
		return
	}

	//Snapshot the Clients slice (same rationale as in PlayerTookDamage).
	clients := lobby.snapshotClients()
	if clientIndex >= len(clients) || clients[clientIndex] == nil {
		return
	}
	if clientPlayerIndex >= len(clients[clientIndex].Players) {
		return
	}
	pl := clients[clientIndex].Players[clientPlayerIndex]
	if pl == nil {
		return
	}
	pl.Health = 0
	pl.Stats.Deaths++

	//Broadcast the fallout
	lobby.BroadcastPacket(packet, packet.Src)
	//lobby.BroadcastPacket(packet, nil)

	lobby.CheckWinner()
}

//PlayerTalked syncs a player's chat message and processes chat commands
func (lobby *Lobby) PlayerTalked(packet *Packet) {
	if !lobby.IsRunning() {
		return
	}

	//The event channel is calculated as (playerIndex * 2) + 2 + 1, so reverse it to get the playerIndex
	playerIndex := (packet.Channel - 1 - 2) / 2
	if playerIndex <= -1 || playerIndex >= lobby.MaxPlayers { //Return if it's not a valid playerIndex
		return
	}

	//Get the client index and the client's player index by finding the client that holds a player with the matching playerIndex
	clientIndex, clientPlayerIndex := lobby.GetIndexesByPlayerIndex(playerIndex)

	//Make sure we aren't a damn fool
	if clientIndex <= -1 || clientPlayerIndex <= -1 {
		return
	}

	//Read in the message
	msg := string(packet.Bytes())
	if lobby.Server.HasSwear(msg) {
		lobby.PlayerThought(playerIndex, "No swearing!")
		return
	}

	//Broadcast the message
	lobby.BroadcastPacket(packet, packet.Src)

	//Log it
	log.Trace("[CHAT:", lobby.Clients[clientIndex].SteamID.ID, "] ", lobby.Clients[clientIndex].SteamID.GetUsername(), ": ", msg)

	if string(msg[0]) == "/" {
		cmd := strings.Split(string(msg[1:]), " ")
		switch cmd[0] {
		case "options":
			lobby.Server.SendPacket(NewPacket(packetTypeRequestingOptions, 0, 0), packet.Src)
		case "pos", "position":
			position := lobby.Clients[clientIndex].Players[clientPlayerIndex].Position.Position
			lobby.PlayerSaid(playerIndex, fmt.Sprintf("%s", position))
		case "weapon":
			if len(cmd) < 2 {
				lobby.PlayerSaid(playerIndex, "Current weapon:\n%s", string(lobby.Clients[clientIndex].Players[clientPlayerIndex].Weapon.Weapon))
				return
			}

			if lobby.IsOwner(lobby.Clients[clientIndex].SteamID) {
				selectedWeapon := weaponEmpty
				for i := 0; i < len(validWeapons); i++ {
					if strings.Join(cmd[1:], " ") == validWeapons[i].String() {
						selectedWeapon = validWeapons[i]
						break
					}
				}
				if selectedWeapon == weaponEmpty {
					i, err := strconv.Atoi(cmd[1])
					if err == nil {
						selectedWeapon = Weapon(i)
					}
				}

				if selectedWeapon != weaponEmpty {
					lobby.UpdateWeapon(playerIndex, selectedWeapon)
					lobby.PlayerSaid(playerIndex, "Received "+selectedWeapon.String())
					return
				}
				lobby.PlayerSaid(playerIndex, "Invalid weaponName!")
			} else {
				lobby.PlayerSaid(playerIndex, "No permissions!")
			}

		case "ping":
			delay := uint32(time.Now().Unix()) - packet.Timestamp
			lobby.PlayerSaid(playerIndex, "%d seconds\n2+ is bad", int(delay))

		case "public":
			if lobby.IsOwner(lobby.Clients[clientIndex].SteamID) {
				lobby.Public = true
				lobby.PlayerSaid(playerIndex, "Set lobby to public!")
			} else {
				lobby.PlayerSaid(playerIndex, "No permissions!")
			}
		case "private":
			if lobby.IsOwner(lobby.Clients[clientIndex].SteamID) {
				lobby.Public = false
				lobby.PlayerSaid(playerIndex, "Set lobby to private!")
			} else {
				lobby.PlayerSaid(playerIndex, "No permissions!")
			}
		case "code", "roomcode", "room", "id", "lobby":
			lobby.PlayerSaid(playerIndex, "Room code: %s", lobby.LobbyRoomCode)

		case "invite":
			if len(cmd) < 2 {
				lobby.PlayerSaid(playerIndex, "/invite username/steamID")
				break
			}

			var inviteClient *Client

			inviteID, err := strconv.ParseUint(cmd[1], 10, 64)
			if err != nil {
				inviteClient = lobby.Server.GetClientBySteamUsername(cmd[1])
				if inviteClient == nil {
					lobby.PlayerSaid(playerIndex, "Unknown player!")
					break
				}
			} else {
				inviteClient = lobby.Server.GetClientBySteamID(NewCSteamID(inviteID))
				if inviteClient == nil {
					lobby.PlayerSaid(playerIndex, "Unknown Steam ID!")
					break
				}
			}

			lobby.Invited = append(lobby.Invited, inviteClient.SteamID)
			lobby.PlayerSaid(playerIndex, "Invited %s!", inviteClient.SteamID.GetNormalizedUsername())
		case "join":
			if len(cmd) < 2 {
				lobby.PlayerSaid(playerIndex, "/join roomCode")
				break
			}

			dstLobby := lobby.Server.GetLobbyByCode(cmd[1])
			if dstLobby == nil {
				lobby.PlayerSaid(playerIndex, "Invalid lobby code!")
				break
			}

			if dstLobby.LobbyRoomCode == lobby.LobbyRoomCode {
				lobby.PlayerSaid(playerIndex, "Already in lobby!")
				break
			}

			err := dstLobby.ClientInit(lobby.Clients[clientIndex].ClientInit)
			if err != nil {
				lobby.PlayerSaid(playerIndex, "Error joining lobby!")
				log.Error("Error joining lobby: %v", err)
				break
			}

			lobby.KickClientBySteamID(lobby.Clients[clientIndex].SteamID.ID)
		case "newlobby":
			roomCode := LobbyRoomCode(6)
			if len(cmd) > 1 {
				roomCode = cmd[1]

				if lobby.Server.GetLobbyByCode(roomCode) != nil {
					lobby.PlayerSaid(playerIndex, "Lobby code exists!")
					break
				}
			}

			dstLobby, err := NewLobby(lobby.Server, roomCode)
			if err != nil {
				log.Error(err)
				lobby.PlayerSaid(playerIndex, "Error creating lobby!")
				break
			}

			err = dstLobby.ClientInit(lobby.Clients[clientIndex].ClientInit)
			if err != nil {
				lobby.PlayerSaid(playerIndex, "Error joining lobby!")
				break
			}

			lobby.Server.LobbyAdd(dstLobby)
			lobby.KickClientBySteamID(lobby.Clients[clientIndex].SteamID.ID)

		case "name", "norm", "normalized", "normal", "username", "steamname", "nickname":
			lobby.PlayerSaid(playerIndex, lobby.Clients[clientIndex].SteamID.GetNormalizedUsername())
		case "index":
			if len(cmd) < 2 {
				lobby.PlayerSaid(playerIndex, "/index playerIndex")
				break
			}

			indexed, err := strconv.Atoi(cmd[1])
			if err != nil {
				lobby.PlayerSaid(playerIndex, "Invalid playerIndex!")
				break
			}

			indexedPlayer := lobby.GetPlayerByIndex(indexed)
			if indexedPlayer == nil {
				lobby.PlayerSaid(playerIndex, "Unknown playerIndex!")
				break
			}

			lobby.PlayerSaid(playerIndex, indexedPlayer.Client.SteamID.GetNormalizedUsername())

		case "pause", "unready", "afk", "brb":
			lobby.Clients[clientIndex].Paused = true
			lobby.PlayerSaid(playerIndex, "Paused for next match!")

		case "team":
			if len(cmd) < 2 {
				lobby.PlayerSaid(playerIndex, "/team ab ac abc abd bcd fff")
				break
			}

			if !lobby.IsOwner(lobby.Clients[clientIndex].SteamID) {
				lobby.PlayerSaid(playerIndex, "Team: " + lobby.TeamType)
				break
			}

			switch cmd[1] {
				case "ab", "ac", "abc", "abd", "bcd", "fff":
					lobby.TeamType = cmd[1]
					lobby.PlayerSaid(playerIndex, "Set team: " + lobby.TeamType)
				default:
					lobby.PlayerSaid(playerIndex, "Invalid team type!")
			}

		case "resume", "ready":
			lobby.Clients[clientIndex].Paused = false
			for i := 0; i < len(lobby.Clients[clientIndex].Players); i++ {
				lobby.Clients[clientIndex].Players[i].Ready = true
			}
			lobby.PlayerSaid(playerIndex, "Ready!")

			if !lobby.MatchInProgress() {
				lobby.StartMatch()
			}

		case "gamemode", "gm", "game", "mode", "mod":
			if len(cmd) < 2 {
				switch lobby.GameMode.(type) {
				case Stock:
					lobby.PlayerSaid(playerIndex, "GameMode: Stock")
				case Tournament:
					lobby.PlayerSaid(playerIndex, "GameMode: Tournament")
				case Duel:
					lobby.PlayerSaid(playerIndex, "GameMode: Duel")
				case GunGame:
					lobby.PlayerSaid(playerIndex, "GameMode: GunGame")
				default:
					lobby.PlayerSaid(playerIndex, "Unknown gamemode!")
				}
				break
			}

			if lobby.IsOwner(lobby.Clients[clientIndex].SteamID) {
				switch cmd[1] {
				case "stock", "default", "original", "og", "regular", "vanilla", "sf", "stick", "fight", "stickfight", "landfall", "official":
					lobby.NextGameMode = Stock{}
					lobby.PlayerSaid(playerIndex, "Set gamemode of next match to Stock!")
				case "tourney", "tournament", "challenge", "hard", "hardcore", "hardmode":
					lobby.NextGameMode = Tournament{}
					lobby.PlayerSaid(playerIndex, "Set gamemode of next match to Tournament!")
				case "duel", "competitive", "compete", "competition":
					lobby.NextGameMode = Duel{}
					lobby.PlayerSaid(playerIndex, "Set gamemode of next match to Duel!")
				case "gun", "roulette", "gungame":
					lobby.NextGameMode = GunGame{
						PlayerData: make([]GunGamePlayerData, lobby.GetPlayerCount(false)),
					}
					lobby.PlayerSaid(playerIndex, "Set gamemode of next match to Gun Game!")
				default:
					lobby.PlayerSaid(playerIndex, "Unknown gamemode!")
				}
			} else {
				lobby.PlayerSaid(playerIndex, "No permissions!")
			}

		case "hp":
			if len(cmd) < 2 {
				lobby.PlayerSaid(playerIndex, "HP: %.2f", lobby.Clients[clientIndex].Players[clientPlayerIndex].Health)
				break
			}

			if !lobby.IsOwner(lobby.Clients[clientIndex].SteamID) {
				lobby.PlayerSaid(playerIndex, "No permissions!")
				break
			}

			healthBytes := []byte(cmd[1])
			if healthBytes[0] < 0 || healthBytes[0] > 6 {
				lobby.PlayerSaid(playerIndex, "Invalid HP setting!")
				break
			}

			lobby.Health = healthBytes[0]
			lobby.PlayerSaid(playerIndex, "Set max HP: %.2f", lobby.GetMaxHealth())

		case "maxplayers":
			if !lobby.IsOwner(lobby.Clients[clientIndex].SteamID) {
				lobby.PlayerSaid(playerIndex, "No permissions!")
				break
			}

			if len(cmd) < 2 {
				lobby.PlayerSaid(playerIndex, "/maxplayers playerCount")
				break
			}

			maxPlayers, err := strconv.Atoi(cmd[1])
			if err != nil {
				lobby.PlayerSaid(playerIndex, "Invalid playerCount!")
				break
			}

			/*if maxPlayers < lobby.MaxPlayers {
				lobby.PlayerSaid(playerIndex, "Cannot lower max players yet!")
				break
			}*/

			lobby.MaxPlayers = maxPlayers
			lobby.PlayerSaid(playerIndex, "Set max players to %d!", maxPlayers)

		case "travel":
			if len(cmd) < 3 {
				lobby.PlayerSaid(playerIndex, "/travel posX posY")
				break
			}

			posX, err := strconv.Atoi(cmd[1])
			if err != nil {
				lobby.PlayerSaid(playerIndex, "Invalid posX!")
				break
			}
			posY, err := strconv.Atoi(cmd[2])
			if err != nil {
				lobby.PlayerSaid(playerIndex, "Invalid posY!")
				break
			}

			timesTried := 0
			maxTries := 50
			for {
				if !lobby.IsRunning() {
					break
				}
				if len(lobby.Clients) <= clientIndex {
					break
				}
				if len(lobby.Clients[clientIndex].Players) <= clientPlayerIndex {
					break
				}

				position4 := lobby.Clients[clientIndex].Players[clientPlayerIndex].Position.Position
				pos4x := int(position4.X)
				pos4y := int(position4.Y)
				coordRange := 3
				minX := posX - coordRange
				maxX := posX + coordRange
				minY := posY - coordRange
				maxY := posY + coordRange

				if pos4x > minX && pos4x < maxX && pos4y > minY && pos4y < maxY {
					break
				}

				packetPlayerUpdate := NewPacket(packetTypePlayerUpdate, lobby.Clients[clientIndex].Players[clientPlayerIndex].GetChannelUpdate(), lobby.Clients[clientIndex].SteamID.ID)
				packetPlayerUpdate.Grow(12)
				packetPlayerUpdate.WriteI16LENext([]int16{int16(posX), int16(posY)})
				packetPlayerUpdate.WriteBytesNext(make([]byte, 8))

				lobby.BroadcastPacket(packetPlayerUpdate, nil)

				timesTried++
				if timesTried > maxTries {
					break
				}

				time.Sleep(time.Millisecond * 25)
			}

			if timesTried > maxTries {
				lobby.PlayerSaid(playerIndex, "Failed to travel that far!")
			} else {
				lobby.PlayerSaid(playerIndex, "Traveled towards\nX:%d Y:%d", posX, posY)
			}

		case "map":
			if len(cmd) < 2 {
				lobby.PlayerSaid(playerIndex, "Current map: %s", lobby.CurrentLevel)
				break
			}

			if !lobby.IsOwner(lobby.Clients[clientIndex].SteamID) {
				lobby.PlayerSaid(playerIndex, "No permissions!")
				break
			}

			switch cmd[1] {
			case "add":
				if len(cmd) < 4 {
					lobby.PlayerSaid(playerIndex, "/map add {landfall/steam} mapID")
					break
				}
				switch cmd[2] {
				case "landfall", "Landfall", "lf", "LF":
					mapIndex, err := strconv.Atoi(cmd[3])
					if err != nil || mapIndex < 0 {
						lobby.PlayerSaid(playerIndex, "Invalid map index!")
						break
					}
					lfMap := newLevelLandfall(int32(mapIndex))
					lobby.Levels = append(lobby.Levels, lfMap)
					lobby.PlayerSaid(playerIndex, "Added map: %s", lfMap)
				case "steam", "Steam", "workshop", "Workshop", "sw", "SW":
					workshopID, err := strconv.ParseUint(cmd[3], 10, 64)
					if err != nil {
						lobby.PlayerSaid(playerIndex, "Invalid workshop ID!")
						break
					}
					steamMap := newLevelCustomOnline(workshopID)
					lobby.Levels = append(lobby.Levels, steamMap)
					lobby.PlayerSaid(playerIndex, "Added map: %s", steamMap)

					//Broadcast the workshop map cycle
					lobby.WorkshopMapsLoaded(nil)
				default:
					lobby.PlayerSaid(playerIndex, "Unknown map type: %s", cmd[2])
					break
				}
			case "scene":
				if len(cmd) < 3 {
					lobby.PlayerSaid(playerIndex, "Must specify sceneIndex!")
					break
				}
				sceneIndex, err := strconv.Atoi(cmd[2])
				if err != nil || sceneIndex < 0 {
					lobby.PlayerSaid(playerIndex, "Invalid scene index!")
					break
				}
				lobby.TempMap(int32(sceneIndex), 255)
				lobby.PlayerSaid(playerIndex, "New map: Landfall %d!", sceneIndex)
			default:
				mapIndex, err := strconv.Atoi(cmd[1])
				if err != nil || mapIndex >= len(lobby.Levels) || mapIndex < -1 {
					lobby.PlayerSaid(playerIndex, "Invalid map index!\n0 to %d\n-1 for random", len(lobby.Levels)-1)
					break
				}
				lobby.ChangeMap(mapIndex, 255)
				lobby.PlayerSaid(playerIndex, "New map: %s!", lobby.CurrentLevel)
			}

		default:
			lobby.PlayerSaid(playerIndex, "Unknown command!")
		}
	}
}

//PlayerSaid pretends a player said something out loud
func (lobby *Lobby) PlayerSaid(playerIndex int, msg string, data ...interface{}) {
	if !lobby.IsRunning() {
		return
	}

	clientIndex, clientPlayerIndex := lobby.GetIndexesByPlayerIndex(playerIndex)
	if clientIndex <= -1 || clientPlayerIndex <= -1 {
		return
	}

	resp := NewPacket(packetTypePlayerTalked, lobby.Clients[clientIndex].Players[clientPlayerIndex].GetChannelEvent(), lobby.Clients[clientIndex].SteamID.ID)
	respBytes := []byte(fmt.Sprintf(msg, data...))
	resp.Grow(int64(len(respBytes)))
	resp.WriteBytesNext(respBytes)
	lobby.BroadcastPacket(resp, nil)

	log.Trace("#[CHAT:", lobby.Clients[clientIndex].SteamID.ID, "] ", lobby.Clients[clientIndex].SteamID.GetUsername(), ": ", string(respBytes))
}

//PlayerThought pretends a player said something to themselves, where no one else can hear them
func (lobby *Lobby) PlayerThought(playerIndex int, msg string, data ...interface{}) {
	if !lobby.IsRunning() {
		return
	}

	clientIndex, clientPlayerIndex := lobby.GetIndexesByPlayerIndex(playerIndex)
	if clientIndex <= -1 || clientPlayerIndex <= -1 {
		return
	}

	resp := NewPacket(packetTypePlayerTalked, lobby.Clients[clientIndex].Players[clientPlayerIndex].GetChannelEvent(), lobby.Clients[clientIndex].SteamID.ID)
	respBytes := []byte(fmt.Sprintf(msg, data...))
	resp.Grow(int64(len(respBytes)))
	resp.WriteBytesNext(respBytes)
	lobby.Server.SendPacket(resp, lobby.Clients[clientIndex].Addr)

	log.Trace("#[CHAT:", lobby.Clients[clientIndex].SteamID.ID, "] ", lobby.Clients[clientIndex].SteamID.GetUsername(), ": ", string(respBytes))
}

//SpawnWeapon spawns the specified weapon on the map
func (lobby *Lobby) SpawnWeapon(weaponID Weapon, weaponSpawnPos Vector3) {
	if !lobby.IsRunning() {
		return
	}

	if !lobby.MatchInProgress() {
		return
	}

	nextWeaponSpawnID := lobby.GetNextWeaponSpawnID(false)
	nextObjectSpawnID := lobby.GetNextObjectSpawnID(false)

	packetWeaponSpawned := NewPacket(packetTypeWeaponSpawned, 0, 0)
	packetWeaponSpawned.Grow(8)
	//Reverted to upstream's `byte(weaponID) - 0x1`. The decompile's
	//OnWeaponSpawned reads `ReadByte` and looks up m_WeaponObjects[index] —
	//upstream (JoshuaDoes' StickFightDev repo, dev branch, in production for
	//years) has -1 here. Our previous attempt to "fix" by removing it broke
	//weapon-instantiation alignment on the prefab array.
	packetWeaponSpawned.WriteByteNext(byte(weaponID) - 0x1)
	//Client decodes Y/Z as ReadSByte() (signed -128..127). Cast via int8 to
	//preserve sign through Go's float→byte conversion (which is undefined for
	//negative floats and otherwise drops the sign). Round to nearest so we
	//don't lose ~0.5m of vertical/depth accuracy per call (was: truncate).
	clampToSByte := func(v float32) byte {
		if v >= 0 {
			v += 0.5
		} else {
			v -= 0.5
		}
		if v > 127 {
			v = 127
		} else if v < -128 {
			v = -128
		}
		return byte(int8(v))
	}
	packetWeaponSpawned.WriteBytesNext([]byte{clampToSByte(weaponSpawnPos.Y), clampToSByte(weaponSpawnPos.Z)})
	packetWeaponSpawned.WriteU16LENext([]uint16{nextWeaponSpawnID, nextObjectSpawnID})
	if lobby.CurrentLevel.IsLobby() && lobby.CurrentLevel.IsStats() && lobby.CurrentLevel.sceneIndex >= 104 && lobby.CurrentLevel.sceneIndex <= 124 {
		packetWeaponSpawned.WriteByteNext(1)
	}

	lobby.BroadcastPacket(packetWeaponSpawned, nil)
	log.Info("Spawned weapon ", weaponID, " at position ", weaponSpawnPos)
}

//SpawnWeapons spawns a list of weapons at the specified positions on the map
func (lobby *Lobby) SpawnWeapons(weapons []Weapon, weaponSpawnPositions []Vector3) {
	if !lobby.IsRunning() {
		return
	}

	if !lobby.MatchInProgress() {
		return
	}

	if len(weapons) != len(weaponSpawnPositions) {
		return
	}

	for i := 0; i < len(weapons); i++ {
		lobby.SpawnWeapon(weapons[i], weaponSpawnPositions[i])
	}
}

//SpawnWeaponRandom spawns a random weapon on the map.
//
//Phase 5 / M1: when dumped map data exists for the current scene, weapons spawn
//at positions derived from the actual map geometry (WeaponPickUp markers if
//present, otherwise player-spawn positions lifted +1m). Falls back to the
//legacy heuristic for scenes without dumped data (workshop maps, etc.).
func (lobby *Lobby) SpawnWeaponRandom() {
	if !lobby.IsRunning() {
		return
	}

	if !lobby.MatchInProgress() {
		return
	}

	if len(lobby.GetActivePlayers()) == 0 {
		return
	}

	weapons := make([]Weapon, randomizer.Intn(lobby.GetPlayerCount(false))+1)
	weaponSpawnPositions := make([]Vector3, len(weapons))
	for i := 0; i < len(weapons); i++ {
		weapons[i] = lobby.Weapons[randomizer.Intn(len(lobby.Weapons))]
		weaponSpawnPositions[i] = lobby.pickWeaponSpawnPosition()
	}

	log.Trace("Weapons to spawn: ", len(weapons))
	lobby.SpawnWeapons(weapons, weaponSpawnPositions)
}

//pickWeaponSpawnPosition returns a sensible position to spawn a weapon at on
//the lobby's current map. Uses dumped map data when available; otherwise the
//legacy "midair at platform height" heuristic.
func (lobby *Lobby) pickWeaponSpawnPosition() Vector3 {
	if lobby.CurrentLevel != nil && lobby.CurrentLevel.Type() == 0 {
		candidates := WeaponSpawnCandidates(lobby.CurrentLevel.SceneIndex())
		if len(candidates) > 0 {
			//Pick one uniformly; jitter X slightly so weapons don't always stack.
			base := candidates[randomizer.Intn(len(candidates))]
			base.X += (randomizer.Float32() - 0.5) * 0.2
			return base
		}
	}
	//Legacy fallback (relay-mode behavior): hardcoded height heuristic that
	//was used before M1. Alternating-sides + random-X for visual variety.
	height := 11.0 * lobby.LastAppliedScale
	x := float32(randomizer.Intn(8))
	if lobby.TourneyRules {
		x = float32(randomizer.Intn(2))
	}
	if lobby.LastSpawnedWeaponOnLeftSide {
		x *= -1.0
	}
	lobby.LastSpawnedWeaponOnLeftSide = !lobby.LastSpawnedWeaponOnLeftSide
	//Was Vector3{0, height, x} — the random "side variety" X was being placed in the
	//Z field, scrambling the axis. With X-as-X, Z=0, weapons stack along the player's
	//side; client wire encoding only sends Y/Z anyway so visible-side is determined by Y.
	return Vector3{X: x, Y: height, Z: 0}
}

//UpdateWeapon updates a player's weapon
func (lobby *Lobby) UpdateWeapon(playerIndex int, weapon Weapon) {
	clientIndex, clientPlayerIndex := lobby.GetIndexesByPlayerIndex(playerIndex)
	if clientIndex <= -1 || clientPlayerIndex <= -1 {
		return
	}

	player := lobby.Clients[clientIndex].Players[clientPlayerIndex]

	packetPlayerUpdate := NewPacket(packetTypePlayerUpdate, player.GetChannelUpdate(), player.Client.SteamID.ID)
	packetPlayerUpdate.Grow(12)

	packetPlayerUpdate.WriteI16LENext([]int16{int16(player.Position.Position.Y * 100.0), int16(player.Position.Position.Z * 100.0)})
	packetPlayerUpdate.WriteBytesNext([]byte{byte(player.Position.Rotation.X * 100.0), byte(player.Position.Rotation.Y * 100.0),
		byte(player.Position.YValue * 100.0), byte(player.Position.MovementType), byte(player.Weapon.FightState)})
	packetPlayerUpdate.WriteU16LENext([]uint16{0})
	packetPlayerUpdate.WriteByteNext(byte(weapon))

	lobby.BroadcastPacket(packetPlayerUpdate, nil)
}
