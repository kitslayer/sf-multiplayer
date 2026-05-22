package physics

// PlayerInput is one sample of a player's input state. Coordinates are in the
// stock SF convention: Movement.X/Y come from the left analog stick (or WASD),
// Aim.X/Y come from the right stick (or mouse direction relative to player),
// each clamped to [-1, 1]. Button bits are defined in PlayerButton.
//
// This struct is the *Go* shape used by the simulator; the wire format (M2+)
// will pack the same fields into a smaller binary representation.
type PlayerInput struct {
	MovementX float32      // -1..1, +X = "right" (toward +Y in SF world)
	MovementY float32      // -1..1, +Y = "up" (jump axis)
	AimX      float32      // -1..1, aim direction
	AimY      float32      // -1..1
	Buttons   PlayerButton // bitmask
	Sequence  uint32       // client tick counter for reconciliation
}

// PlayerButton is a bitmask of digital input states.
type PlayerButton uint16

const (
	BtnPunchOrFire PlayerButton = 1 << iota
	BtnBlock
	BtnJump
	BtnThrow
	BtnJumpJustPressed // edge-trigger version of BtnJump
)

// Has reports whether all of mask's bits are set in b.
func (b PlayerButton) Has(mask PlayerButton) bool { return b&mask == mask }

// PlayerSimParams are tunable knobs for player physics. Values here are placeholders
// derived from rough observation of stock SF; M0.5 / M3 tune them against golden
// replay data.
type PlayerSimParams struct {
	MoveAccel    float32 // m/s^2 of horizontal force from full-stick movement
	MaxRunSpeed  float32 // m/s soft cap on horizontal velocity
	JumpVelocity float32 // m/s instantaneous +Y velocity on jump
	GroundFriction float32 // 0..1; per-tick velocity attenuation when grounded
}

// DefaultPlayerSimParams returns reasonable starting values. Tunable later.
func DefaultPlayerSimParams() PlayerSimParams {
	return PlayerSimParams{
		MoveAccel:      40,
		MaxRunSpeed:    8,
		JumpVelocity:   10,
		GroundFriction: 0.85,
	}
}

// ApplyPlayerInput applies one input frame to one player entity. Modifies the
// entity's velocity directly; the next Step() will integrate it to a position.
//
// SF axis convention (confirmed via Movement.cs: Vector3.up for jumps, Vector3.forward
// for lateral forces):
//   - X = camera depth (looking into the screen) — gameplay-irrelevant for players
//   - Y = world up; jumps and gravity act here
//   - Z = lateral (left/right movement axis); MovementX stick maps here
//
// Jump fires on the BtnJumpJustPressed edge (only when grounded).
//
// Returns true if the input was applied; false if the entity isn't a player or
// is dead.
func ApplyPlayerInput(w *World, playerID EntityID, in PlayerInput, params PlayerSimParams) bool {
	e := w.Get(playerID)
	if e == nil || e.Kind != EntityPlayer {
		return false
	}
	dt := w.StepDT

	// Horizontal acceleration from stick — drives lateral (Z) velocity.
	desiredAccel := in.MovementX * params.MoveAccel
	e.Velocity.Z += desiredAccel * dt

	// Soft cap lateral speed.
	if e.Velocity.Z > params.MaxRunSpeed {
		e.Velocity.Z = params.MaxRunSpeed
	} else if e.Velocity.Z < -params.MaxRunSpeed {
		e.Velocity.Z = -params.MaxRunSpeed
	}

	// Jump (only when grounded; only on edge). +Y instantaneous velocity.
	if e.Grounded && in.Buttons.Has(BtnJumpJustPressed) {
		e.Velocity.Y = params.JumpVelocity
		e.Grounded = false
	}

	// Ground friction when stick is neutral.
	if e.Grounded && in.MovementX == 0 {
		e.Velocity.Z *= params.GroundFriction
		if e.Velocity.Z < 0.01 && e.Velocity.Z > -0.01 {
			e.Velocity.Z = 0
		}
	}

	return true
}
