package physics

import "testing"

// Player moves laterally (along Z) when the stick is pushed.
func TestPlayerLateralMovement(t *testing.T) {
	w := NewWorld()
	w.Gravity = Vec3{} // disable gravity to isolate lateral motion
	// A floor so the player is "grounded" — without it the entity has no surface
	// to push off of, but for the lateral-accel test we only check velocity not
	// position-relative-to-floor, so omit floor and pretend grounded explicitly.
	id := w.SpawnEntity(Entity{
		Kind:     EntityPlayer,
		Box:      AABB{Center: Vec3{0, 0, 0}, Half: Vec3{0.5, 1, 0.5}},
		Grounded: true,
	})
	params := DefaultPlayerSimParams()

	// Apply full +X stick for 30 ticks (= 0.5s).
	for i := 0; i < 30; i++ {
		// Re-assert grounded each tick because the physics step would clear it
		// in absence of a floor.
		w.Get(id).Grounded = true
		ApplyPlayerInput(w, id, PlayerInput{MovementX: 1}, params)
		w.Step()
	}
	e := w.Get(id)
	if e == nil {
		t.Fatal("player gone")
	}
	// Z velocity should have hit the soft cap.
	if e.Velocity.Z < params.MaxRunSpeed*0.9 {
		t.Errorf("expected near-cap lateral velocity, got %v", e.Velocity.Z)
	}
	// Z position should have advanced.
	if e.Box.Center.Z < 1.0 {
		t.Errorf("expected lateral progress, Z=%v", e.Box.Center.Z)
	}
}

// Jump applies +Y instantaneous velocity on edge-press.
func TestPlayerJump(t *testing.T) {
	w := NewWorld()
	id := w.SpawnEntity(Entity{
		Kind:     EntityPlayer,
		Box:      AABB{Center: Vec3{0, 0, 0}, Half: Vec3{0.5, 1, 0.5}},
		Grounded: true,
	})
	params := DefaultPlayerSimParams()

	startY := w.Get(id).Box.Center.Y
	ApplyPlayerInput(w, id, PlayerInput{Buttons: BtnJumpJustPressed}, params)
	if w.Get(id).Velocity.Y != params.JumpVelocity {
		t.Errorf("expected jump velocity %v, got %v", params.JumpVelocity, w.Get(id).Velocity.Y)
	}
	if w.Get(id).Grounded {
		t.Error("should not be grounded after jump")
	}

	// Step several ticks; player should rise.
	for i := 0; i < 10; i++ {
		w.Step()
	}
	if w.Get(id).Box.Center.Y <= startY {
		t.Errorf("player should have risen above %v, got %v", startY, w.Get(id).Box.Center.Y)
	}
}

// Jump only fires on EDGE — holding Jump after the first frame does nothing.
func TestPlayerJumpEdgeOnly(t *testing.T) {
	w := NewWorld()
	id := w.SpawnEntity(Entity{
		Kind:     EntityPlayer,
		Box:      AABB{Center: Vec3{0, 0, 0}, Half: Vec3{0.5, 1, 0.5}},
		Grounded: true,
	})
	params := DefaultPlayerSimParams()

	// First input: BtnJumpJustPressed → fires.
	ApplyPlayerInput(w, id, PlayerInput{Buttons: BtnJumpJustPressed}, params)
	w.Step()
	w.Get(id).Grounded = true // pretend re-landed
	beforeY := w.Get(id).Velocity.Y

	// Second input: BtnJump (held) but no edge — should NOT re-jump.
	ApplyPlayerInput(w, id, PlayerInput{Buttons: BtnJump}, params)
	if w.Get(id).Velocity.Y != beforeY {
		t.Errorf("expected no re-jump on held button; vY changed from %v to %v", beforeY, w.Get(id).Velocity.Y)
	}
}

// Friction: when grounded and stick is neutral, lateral velocity decays.
func TestPlayerGroundFriction(t *testing.T) {
	w := NewWorld()
	w.Gravity = Vec3{}
	id := w.SpawnEntity(Entity{
		Kind:     EntityPlayer,
		Box:      AABB{Center: Vec3{0, 0, 0}, Half: Vec3{0.5, 1, 0.5}},
		Velocity: Vec3{0, 0, 5},
		Grounded: true,
	})
	params := DefaultPlayerSimParams()

	for i := 0; i < 10; i++ {
		w.Get(id).Grounded = true
		ApplyPlayerInput(w, id, PlayerInput{}, params)
		w.Step()
	}
	if w.Get(id).Velocity.Z >= 5 {
		t.Errorf("expected friction to decay velocity, got %v", w.Get(id).Velocity.Z)
	}
}
