package physics

import "testing"

// Projectile vs player: projectile spawned moving toward player AABB; after
// enough ticks they overlap and EventProjectileHitEntity fires.
func TestProjectileHitPlayer(t *testing.T) {
	w := NewWorld()
	w.Gravity = Vec3{} // disable gravity to make the test deterministic

	target := w.SpawnEntity(Entity{
		Kind:     EntityPlayer,
		Box:      AABB{Center: Vec3{0, 0, 5}, Half: Vec3{0.5, 1, 0.5}},
		Grounded: true,
	})
	shooter := w.SpawnEntity(Entity{
		Kind:     EntityPlayer,
		Box:      AABB{Center: Vec3{0, 0, 0}, Half: Vec3{0.5, 1, 0.5}},
		Grounded: true,
	})
	projectile := w.SpawnEntity(Entity{
		Kind:     EntityProjectile,
		Box:      AABB{Center: Vec3{0, 0, 0.5}, Half: Vec3{0.1, 0.1, 0.1}},
		Velocity: Vec3{0, 0, 20}, // 20 m/s toward the target
		OwnerID:  shooter,
		TTLTicks: 0, // not invincible
	})

	var hitEvent bool
	for i := 0; i < 60 && !hitEvent; i++ {
		ev := w.Step()
		for _, e := range ev {
			if e.Kind == EventProjectileHitEntity && e.Entity == projectile && e.Other == target {
				hitEvent = true
			}
		}
	}
	if !hitEvent {
		t.Fatal("expected EventProjectileHitEntity")
	}
	if w.Get(projectile) != nil {
		t.Errorf("projectile should be dead after hit")
	}
}

// Projectile doesn't self-hit the shooter when TTLTicks > 0 (the "no instant
// self-hit on muzzle" invariant).
func TestProjectileNoSelfHitEarly(t *testing.T) {
	w := NewWorld()
	w.Gravity = Vec3{}

	shooter := w.SpawnEntity(Entity{
		Kind:     EntityPlayer,
		Box:      AABB{Center: Vec3{0, 0, 0}, Half: Vec3{1, 1, 1}},
		Grounded: true,
	})
	// Projectile starts inside the shooter (same Center). With OwnerID==shooter
	// and TTLTicks>0 the collision system should NOT fire a hit.
	projectile := w.SpawnEntity(Entity{
		Kind:     EntityProjectile,
		Box:      AABB{Center: Vec3{0, 0, 0}, Half: Vec3{0.1, 0.1, 0.1}},
		Velocity: Vec3{0, 0, 0.01},
		OwnerID:  shooter,
		TTLTicks: 120,
	})

	for i := 0; i < 5; i++ {
		ev := w.Step()
		for _, e := range ev {
			if e.Kind == EventProjectileHitEntity {
				t.Fatalf("unexpected self-hit at tick %d (entity=%d other=%d)", i, e.Entity, e.Other)
			}
		}
	}
	if w.Get(projectile) == nil {
		t.Error("projectile should still be alive")
	}
}
