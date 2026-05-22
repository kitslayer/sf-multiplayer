package physics

// EntityID is the stable handle the server uses to refer to dynamic entities
// across snapshots. IDs are never recycled within a lobby (the server tracks
// "this projectile was destroyed at tick N" so late-arriving packets for it can
// be discarded cleanly).
type EntityID uint32

// EntityKind enumerates the entity types the simulator knows about.
// Different kinds use slightly different integration paths.
type EntityKind uint8

const (
	EntityNone       EntityKind = iota
	EntityPlayer                // capsule-ish AABB driven by playerInput
	EntityProjectile            // ballistic, may have gravity, dies on hit or ttl
	EntityWeapon                // thrown weapon — ballistic until grounded or picked up
	EntityDynamic               // generic syncable object (barrel/crate; M5)
)

// Entity is the per-tick state of one dynamic body. We keep this small and
// value-shaped on purpose: a Lobby's World holds them in a flat slice and
// touches them every tick; cache locality matters more than richer types.
type Entity struct {
	ID       EntityID
	Kind     EntityKind
	Box      AABB    // current AABB in world space (Center is "position")
	Velocity Vec3    // m/s
	Alive    bool    // false = removed; slot may be reused for a new ID later
	Grounded bool    // for player + weapon entities, set true when resting on a collider
	TTLTicks uint32  // 0 = no expiry (player); for projectiles, ticks remaining
	OwnerID  EntityID // for projectiles: who fired it (so we don't self-hit immediately)
	Meta     uint32  // free-use field per Kind (e.g. weapon type byte for projectiles)
}

// World is a per-Lobby physics world. It owns the static level geometry plus
// a roster of dynamic entities and ticks them forward at a fixed rate.
//
// Concurrency note: World is intended to be accessed from a single goroutine
// per Lobby (the lobby's match goroutine). Server.Handle() goroutines deposit
// playerInput packets into a per-lobby queue which the Lobby goroutine drains
// before each tick. The lobby goroutine is the sole writer to World state.
type World struct {
	Statics   []AABB   // immutable for a given scene (loaded from map JSON)
	Killboxes []AABB   // overlap → player dies
	Entities  []Entity // append-only; reuse slots where !Alive

	Gravity   Vec3    // typical SF gravity ~ (0, -9.81*scaleFactor, 0); tuned in M0.5
	Tick      uint64  // monotonic; increments by 1 each Step()
	StepDT    float32 // physics step in seconds; 1/60 typical
	nextID    EntityID
}

// NewWorld returns a fresh World with sensible defaults. Gravity and StepDT
// can be adjusted afterward; M0.5 tunes them by replay comparison.
func NewWorld() *World {
	return &World{
		Gravity: Vec3{0, -9.81, 0},
		StepDT:  1.0 / 60.0,
	}
}

// LoadStatics replaces the world's static colliders. Killboxes are populated
// separately via LoadKillboxes — callers should LoadStatics + LoadKillboxes
// together when a new scene is set on the Lobby.
func (w *World) LoadStatics(statics []AABB) { w.Statics = statics }

// LoadKillboxes replaces the world's killbox volumes.
func (w *World) LoadKillboxes(boxes []AABB) { w.Killboxes = boxes }

// SpawnEntity adds e to the world and returns its assigned ID. The caller's
// ID field is overwritten. Slots from dead entities are reused.
func (w *World) SpawnEntity(e Entity) EntityID {
	w.nextID++
	e.ID = w.nextID
	e.Alive = true
	// Reuse a dead slot if one exists; cheap O(n) scan is fine — entity counts
	// per lobby are tiny (≤ a few dozen) and this only runs on spawn events.
	for i := range w.Entities {
		if !w.Entities[i].Alive {
			w.Entities[i] = e
			return e.ID
		}
	}
	w.Entities = append(w.Entities, e)
	return e.ID
}

// Get returns a pointer to the live entity with the given ID, or nil if it
// doesn't exist or is dead. The returned pointer is invalidated by any
// SpawnEntity call that grows w.Entities.
func (w *World) Get(id EntityID) *Entity {
	for i := range w.Entities {
		if w.Entities[i].Alive && w.Entities[i].ID == id {
			return &w.Entities[i]
		}
	}
	return nil
}

// Kill marks an entity as dead. Idempotent.
func (w *World) Kill(id EntityID) {
	if e := w.Get(id); e != nil {
		e.Alive = false
	}
}

// Step advances the simulation by exactly one StepDT.
//   1. Apply gravity to entities that don't ignore it (players + projectiles +
//      thrown weapons all do).
//   2. Decrement TTL for entities with one; kill at expiry.
//   3. Move entities; swept-collide against statics; resolve.
//   4. Killbox overlap check for player entities.
//
// Returns events that happened this tick (collisions, deaths) so the Lobby
// goroutine can translate them into protocol packets.
func (w *World) Step() []Event {
	w.Tick++
	var events []Event

	for i := range w.Entities {
		e := &w.Entities[i]
		if !e.Alive {
			continue
		}

		// Gravity (skip if entity is grounded; otherwise it accumulates downward
		// velocity into the floor and produces jitter).
		if e.Kind != EntityNone && !e.Grounded {
			e.Velocity = e.Velocity.Add(w.Gravity.Scale(w.StepDT))
		}

		// TTL decay for transient entities (projectiles).
		if e.TTLTicks > 0 {
			e.TTLTicks--
			if e.TTLTicks == 0 {
				e.Alive = false
				events = append(events, Event{
					Kind: EventEntityDespawn,
					Entity: e.ID,
					Reason: "ttl",
				})
				continue
			}
		}

		// Integrate position with swept collision against static AABBs.
		// We do a single sweep per axis (linear-only — no bouncing yet) for
		// stability; that's enough for projectiles and gives players a stable
		// "slide along walls" behavior.
		step := e.Velocity.Scale(w.StepDT)
		if step.LengthSq() > 0 {
			newCenter, hit := w.sweepEntity(e.Box, step)
			e.Box.Center = newCenter
			if hit.Hit {
				// Cancel velocity into the hit normal.
				e.Velocity = projectOnPlane(e.Velocity, hit.Normal)
				// Mark grounded if the hit normal points up (Y-axis).
				if hit.Normal.Y > 0.5 {
					e.Grounded = true
				}
				if e.Kind == EntityProjectile {
					// Projectiles die on first impact for now (no bouncing).
					e.Alive = false
					events = append(events, Event{
						Kind:   EventProjectileHitStatic,
						Entity: e.ID,
						Point:  hit.Point,
					})
				}
			} else {
				// In the air — Grounded must clear so gravity resumes next tick.
				e.Grounded = false
			}
		}

		// Killbox check (player-only for now).
		if e.Kind == EntityPlayer {
			for _, kb := range w.Killboxes {
				if kb.Overlaps(e.Box) {
					e.Alive = false
					events = append(events, Event{
						Kind:   EventPlayerKilledByKillbox,
						Entity: e.ID,
					})
					break
				}
			}
		}
	}
	// Per-tick projectile-vs-player collision check. Done in a second pass so
	// all entities have already integrated for this tick — the projectile's
	// updated position is what's tested against player AABBs.
	for i := range w.Entities {
		p := &w.Entities[i]
		if !p.Alive || p.Kind != EntityProjectile {
			continue
		}
		for j := range w.Entities {
			tgt := &w.Entities[j]
			if !tgt.Alive || tgt.Kind != EntityPlayer {
				continue
			}
			// Don't self-hit the shooter on the first few ticks. OwnerID matches
			// (set at projectile spawn); after TTL has counted down a bit, we
			// allow self-hits to support rebound/ricochet weapons later.
			if tgt.ID == p.OwnerID && p.TTLTicks > 0 {
				continue
			}
			if p.Box.Overlaps(tgt.Box) {
				p.Alive = false
				events = append(events, Event{
					Kind:   EventProjectileHitEntity,
					Entity: p.ID,
					Other:  tgt.ID,
					Point:  p.Box.Center,
				})
				break // projectile dies on first hit
			}
		}
	}
	return events
}

// sweepEntity attempts to move a body by delta, swept-collided against all statics.
// Returns the new center position and the earliest hit (if any).
func (w *World) sweepEntity(box AABB, delta Vec3) (Vec3, HitResult) {
	bestT := float32(1)
	var bestHit HitResult
	for i, s := range w.Statics {
		hit := SweptAABB(box, delta, s)
		if !hit.Hit {
			continue
		}
		if hit.T < bestT && hit.T <= 1 {
			bestT = hit.T
			bestHit = hit
			bestHit.HitIndex = i
		}
	}
	if bestHit.Hit {
		// Move up to (but not into) the contact point. Tiny epsilon prevents
		// surface tunneling on the next frame.
		const epsilon = 1e-4
		t := bestHit.T - epsilon
		if t < 0 {
			t = 0
		}
		return box.Center.Add(delta.Scale(t)), bestHit
	}
	return box.Center.Add(delta), HitResult{}
}

// projectOnPlane removes the component of v along the plane normal (which must
// be unit-length). Used to "slide" velocity along a contact surface so a player
// running into a wall stops moving into it but can keep moving along it.
func projectOnPlane(v Vec3, n Vec3) Vec3 {
	d := v.X*n.X + v.Y*n.Y + v.Z*n.Z
	return Vec3{v.X - n.X*d, v.Y - n.Y*d, v.Z - n.Z*d}
}
