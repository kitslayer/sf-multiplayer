// Package physics is the server-authoritative simulation layer for Stick Fight v2.
// It is intentionally NOT a full physics engine: SF uses 3D Unity physics with
// ConfigurableJoint ragdolls that would be a multi-month port. Instead we run a
// minimal kinematic integrator (AABBs + ballistic projectiles + raycasts against
// static level geometry) that owns gameplay state (positions, hits, killboxes,
// weapon spawn timing) while the client keeps doing local ragdoll wobble.
//
// See ~/.claude/plans/iterative-sparking-pascal.md for the architecture rationale.
package physics

import "math"

// Vec3 is a 3D vector. We keep three components even though SF gameplay is
// effectively YZ-plane motion, because static level geometry has a real X extent
// (platforms have depth) and projectiles can be sampled in 3D for visual fidelity
// during snapshots.
type Vec3 struct {
	X, Y, Z float32
}

// Add returns a + b.
func (a Vec3) Add(b Vec3) Vec3 { return Vec3{a.X + b.X, a.Y + b.Y, a.Z + b.Z} }

// Sub returns a - b.
func (a Vec3) Sub(b Vec3) Vec3 { return Vec3{a.X - b.X, a.Y - b.Y, a.Z - b.Z} }

// Scale returns a * s.
func (a Vec3) Scale(s float32) Vec3 { return Vec3{a.X * s, a.Y * s, a.Z * s} }

// Length returns the Euclidean magnitude of a.
func (a Vec3) Length() float32 {
	return float32(math.Sqrt(float64(a.X*a.X + a.Y*a.Y + a.Z*a.Z)))
}

// LengthSq returns the squared magnitude (avoids the sqrt; useful for compares).
func (a Vec3) LengthSq() float32 { return a.X*a.X + a.Y*a.Y + a.Z*a.Z }

// Normalized returns a unit-length copy of a. Returns zero vector if a is zero.
func (a Vec3) Normalized() Vec3 {
	l := a.Length()
	if l == 0 {
		return Vec3{}
	}
	return Vec3{a.X / l, a.Y / l, a.Z / l}
}

// AABB is an axis-aligned bounding box defined by its center and half-extents.
// Half-extents (rather than min/max) keep arithmetic symmetric and make the
// expanded-AABB Minkowski-sum raycast trick cheaper.
type AABB struct {
	Center Vec3
	Half   Vec3 // half-width on each axis; full extent is 2*Half
}

// Min returns the minimum corner (center - half) of the AABB.
func (b AABB) Min() Vec3 { return b.Center.Sub(b.Half) }

// Max returns the maximum corner (center + half) of the AABB.
func (b AABB) Max() Vec3 { return b.Center.Add(b.Half) }

// Contains reports whether p is inside b (inclusive on faces).
func (b AABB) Contains(p Vec3) bool {
	mn, mx := b.Min(), b.Max()
	return p.X >= mn.X && p.X <= mx.X &&
		p.Y >= mn.Y && p.Y <= mx.Y &&
		p.Z >= mn.Z && p.Z <= mx.Z
}

// Overlaps reports whether two AABBs share any volume.
func (b AABB) Overlaps(o AABB) bool {
	bmn, bmx := b.Min(), b.Max()
	omn, omx := o.Min(), o.Max()
	return bmx.X >= omn.X && bmn.X <= omx.X &&
		bmx.Y >= omn.Y && bmn.Y <= omx.Y &&
		bmx.Z >= omn.Z && bmn.Z <= omx.Z
}

// Expand returns a copy of b with each half-extent increased by extra. Used for
// Minkowski-sum collision queries (e.g. swept AABB vs AABB = ray vs expanded AABB).
func (b AABB) Expand(extra Vec3) AABB {
	return AABB{Center: b.Center, Half: Vec3{b.Half.X + extra.X, b.Half.Y + extra.Y, b.Half.Z + extra.Z}}
}
