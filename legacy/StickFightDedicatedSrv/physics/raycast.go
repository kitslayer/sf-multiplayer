package physics

import "math"

// Ray is a parameterized line starting at Origin and extending along Dir.
// Dir is not required to be normalized; downstream code that interprets t as
// distance should normalize first.
type Ray struct {
	Origin Vec3
	Dir    Vec3
}

// HitResult describes a raycast intersection.
type HitResult struct {
	Hit      bool
	T        float32 // parameter along the ray (0 = origin, 1 = origin+dir for un-normalized)
	Point    Vec3    // ray.Origin + ray.Dir * T
	Normal   Vec3    // surface normal at hit point (axis-aligned for AABB)
	HitIndex int     // index into the slab of statics that was hit (caller-defined)
}

// RayAABB tests a ray against an AABB and returns the nearest entry intersection.
// Implementation is the classic slab method: clip the ray against the 3 axis slabs
// of the box, take the latest entry and earliest exit; if entry <= exit and exit >= 0
// the ray hits.
//
// Returns a HitResult with Hit=false if the ray misses (or only intersects behind
// the origin). The HitIndex is left unset and must be filled by the caller.
func RayAABB(r Ray, box AABB) HitResult {
	mn, mx := box.Min(), box.Max()
	tEntry := float32(math.Inf(-1))
	tExit := float32(math.Inf(1))
	var hitAxis int
	var hitSign float32

	// Per-axis slab test.
	for axis := 0; axis < 3; axis++ {
		var o, d, lo, hi float32
		switch axis {
		case 0:
			o, d, lo, hi = r.Origin.X, r.Dir.X, mn.X, mx.X
		case 1:
			o, d, lo, hi = r.Origin.Y, r.Dir.Y, mn.Y, mx.Y
		case 2:
			o, d, lo, hi = r.Origin.Z, r.Dir.Z, mn.Z, mx.Z
		}
		if d == 0 {
			// Ray parallel to this axis' slabs: must already be inside the slab.
			if o < lo || o > hi {
				return HitResult{}
			}
			continue
		}
		t1 := (lo - o) / d
		t2 := (hi - o) / d
		sign := float32(-1)
		if t1 > t2 {
			t1, t2 = t2, t1
			sign = 1
		}
		if t1 > tEntry {
			tEntry = t1
			hitAxis = axis
			hitSign = sign
		}
		if t2 < tExit {
			tExit = t2
		}
		if tEntry > tExit || tExit < 0 {
			return HitResult{}
		}
	}

	// Behind the origin → not a forward hit.
	if tEntry < 0 {
		return HitResult{}
	}

	normal := Vec3{}
	switch hitAxis {
	case 0:
		normal.X = hitSign
	case 1:
		normal.Y = hitSign
	case 2:
		normal.Z = hitSign
	}

	return HitResult{
		Hit:    true,
		T:      tEntry,
		Point:  r.Origin.Add(r.Dir.Scale(tEntry)),
		Normal: normal,
	}
}

// SweptAABB tests a moving AABB ("from box" stepping by Dir over t∈[0,1]) against
// a static AABB. Implemented as a ray cast from the moving box's center against a
// Minkowski-expanded version of the static box (sum of half-extents). Returns the
// earliest contact t∈[0,1]; t=0 means starting overlap. Hit=false means no contact
// in this step (including contacts at t>1, which mean the ray would hit if extended
// but doesn't with the given displacement).
func SweptAABB(from AABB, dir Vec3, static AABB) HitResult {
	expanded := static.Expand(from.Half)
	h := RayAABB(Ray{Origin: from.Center, Dir: dir}, expanded)
	if h.Hit && h.T > 1 {
		return HitResult{}
	}
	return h
}
