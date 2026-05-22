package physics

// EventKind enumerates the kinds of events Step() can emit. The Lobby goroutine
// translates these into protocol packets (serverEvent / worldStateSnapshot).
type EventKind uint8

const (
	EventNone EventKind = iota

	// EventProjectileHitStatic — a projectile hit level geometry. Lobby may
	// want to emit an effects event (impact particles) to clients.
	EventProjectileHitStatic

	// EventProjectileHitEntity — a projectile hit another entity. Damage
	// resolution happens in lobby code based on the OwnerID / weapon type;
	// this just signals that the hit happened.
	EventProjectileHitEntity

	// EventPlayerKilledByKillbox — a player overlapped a killbox volume.
	// Lobby translates this into a death + scoring update.
	EventPlayerKilledByKillbox

	// EventEntityDespawn — an entity died (TTL expired or otherwise). Lobby
	// should clear any per-entity bookkeeping (e.g. weapon spawn IDs).
	EventEntityDespawn
)

// Event is the simulator's per-tick output. We keep these small and
// allocate-friendly; the lobby goroutine drains them and resets the slice.
type Event struct {
	Kind   EventKind
	Entity EntityID // primary affected entity (the projectile, the player, etc.)
	Other  EntityID // secondary (e.g. owner for entity-hit events)
	Point  Vec3     // contact point if relevant (impact location)
	Reason string   // human-readable detail for logging; not on the wire
}
