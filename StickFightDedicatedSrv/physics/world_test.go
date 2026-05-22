package physics

import "testing"

// Projectile-fall test: drop a projectile from height 10, with no statics, and
// confirm it accelerates downward roughly per gravity. Sanity check that the
// integrator wires together correctly.
func TestProjectileFall(t *testing.T) {
	w := NewWorld()
	id := w.SpawnEntity(Entity{
		Kind: EntityProjectile,
		Box:  AABB{Center: Vec3{0, 10, 0}, Half: Vec3{0.1, 0.1, 0.1}},
	})

	// Tick for 1 second (60 ticks at 1/60).
	for i := 0; i < 60; i++ {
		w.Step()
	}
	e := w.Get(id)
	if e == nil {
		t.Fatal("entity disappeared")
	}
	// y = 10 + 0.5*g*t^2 with g = -9.81, t = 1 → y ≈ 10 - 4.905 = 5.095.
	// Forward-Euler integration accumulates a bit of bias; loose ±0.5 m tolerance.
	dy := 10 - e.Box.Center.Y
	if dy < 4.0 || dy > 6.0 {
		t.Errorf("expected ~4.9m fall in 1s, got dy=%v (y=%v)", dy, e.Box.Center.Y)
	}
}

// Projectile-vs-static: shoot a projectile at a wall, ensure it stops at the
// wall, gets despawned, and emits a EventProjectileHitStatic event.
func TestProjectileHitWall(t *testing.T) {
	w := NewWorld()
	w.Gravity = Vec3{} // disable gravity for a horizontal shot test
	w.LoadStatics([]AABB{
		{Center: Vec3{5, 0, 0}, Half: Vec3{1, 5, 5}}, // wall at x=4..6
	})
	id := w.SpawnEntity(Entity{
		Kind:     EntityProjectile,
		Box:      AABB{Center: Vec3{0, 0, 0}, Half: Vec3{0.1, 0.1, 0.1}},
		Velocity: Vec3{10, 0, 0}, // 10 m/s along +X
		TTLTicks: 240,            // 4 seconds at 60Hz
	})

	var hitEvent bool
	for i := 0; i < 60 && !hitEvent; i++ {
		ev := w.Step()
		for _, e := range ev {
			if e.Kind == EventProjectileHitStatic && e.Entity == id {
				hitEvent = true
			}
		}
	}
	if !hitEvent {
		t.Fatal("expected EventProjectileHitStatic")
	}
	if e := w.Get(id); e != nil {
		t.Errorf("projectile should be despawned after wall hit; still alive at %v", e.Box.Center)
	}
}

// Killbox: an entity that overlaps a killbox dies and emits the event.
func TestPlayerKillbox(t *testing.T) {
	w := NewWorld()
	w.Gravity = Vec3{}
	w.LoadKillboxes([]AABB{
		{Center: Vec3{0, -10, 0}, Half: Vec3{20, 1, 20}},
	})
	id := w.SpawnEntity(Entity{
		Kind:     EntityPlayer,
		Box:      AABB{Center: Vec3{0, -10, 0}, Half: Vec3{0.5, 1, 0.5}},
		Grounded: true, // pretend stationary; we just want overlap detection
	})

	var killEvent bool
	for i := 0; i < 3; i++ {
		ev := w.Step()
		for _, e := range ev {
			if e.Kind == EventPlayerKilledByKillbox && e.Entity == id {
				killEvent = true
			}
		}
	}
	if !killEvent {
		t.Fatal("expected EventPlayerKilledByKillbox")
	}
}

// TTL despawn: a projectile with finite TTL despawns even if it doesn't hit anything.
func TestProjectileTTL(t *testing.T) {
	w := NewWorld()
	w.Gravity = Vec3{}
	id := w.SpawnEntity(Entity{
		Kind:     EntityProjectile,
		Box:      AABB{Center: Vec3{0, 0, 0}, Half: Vec3{0.1, 0.1, 0.1}},
		Velocity: Vec3{1, 0, 0},
		TTLTicks: 30,
	})
	var despawn bool
	for i := 0; i < 60 && !despawn; i++ {
		ev := w.Step()
		for _, e := range ev {
			if e.Kind == EventEntityDespawn && e.Entity == id {
				despawn = true
			}
		}
	}
	if !despawn {
		t.Fatal("projectile should despawn from TTL")
	}
}

// Sliding-along-wall: an entity moving into a wall at 45° should retain its
// tangential velocity (slide along the wall) instead of stopping dead.
func TestSlideAlongWall(t *testing.T) {
	w := NewWorld()
	w.Gravity = Vec3{}
	w.LoadStatics([]AABB{
		{Center: Vec3{2, 0, 0}, Half: Vec3{0.5, 5, 5}}, // wall at x=1.5..2.5
	})
	id := w.SpawnEntity(Entity{
		Kind:     EntityPlayer,
		Box:      AABB{Center: Vec3{0, 0, 0}, Half: Vec3{0.5, 1, 0.5}},
		Velocity: Vec3{2, 0, 1}, // moving +X (into the wall) and +Z (tangential)
	})

	startZ := w.Get(id).Box.Center.Z
	for i := 0; i < 60; i++ {
		w.Step()
	}
	e := w.Get(id)
	if e == nil {
		t.Fatal("player disappeared")
	}
	endZ := e.Box.Center.Z
	if endZ-startZ < 0.5 {
		t.Errorf("expected Z motion (slide), got dz=%v (start=%v end=%v)", endZ-startZ, startZ, endZ)
	}
	// X velocity should be ~zero by now (after first contact). Position X must
	// not have penetrated through to the far side of the wall.
	if e.Box.Center.X > 2.0 {
		t.Errorf("player tunneled through wall; X=%v", e.Box.Center.X)
	}
}
