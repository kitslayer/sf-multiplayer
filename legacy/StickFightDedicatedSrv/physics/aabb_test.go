package physics

import (
	"math"
	"testing"
)

func feq(a, b float32) bool { return math.Abs(float64(a-b)) < 1e-5 }

func vecEq(a, b Vec3) bool { return feq(a.X, b.X) && feq(a.Y, b.Y) && feq(a.Z, b.Z) }

func TestAABBContains(t *testing.T) {
	b := AABB{Center: Vec3{0, 0, 0}, Half: Vec3{1, 1, 1}}
	cases := []struct {
		p    Vec3
		want bool
	}{
		{Vec3{0, 0, 0}, true},
		{Vec3{1, 1, 1}, true},      // on face — inclusive
		{Vec3{-1, -1, -1}, true},   // on opposite face
		{Vec3{1.01, 0, 0}, false},  // just outside +X
		{Vec3{0, 2, 0}, false},     // outside +Y
		{Vec3{0, 0, -1.5}, false},  // outside -Z
	}
	for _, c := range cases {
		if got := b.Contains(c.p); got != c.want {
			t.Errorf("Contains(%v): got %v want %v", c.p, got, c.want)
		}
	}
}

func TestAABBOverlaps(t *testing.T) {
	a := AABB{Center: Vec3{0, 0, 0}, Half: Vec3{1, 1, 1}}
	cases := []struct {
		b    AABB
		want bool
	}{
		{AABB{Vec3{1.5, 0, 0}, Vec3{1, 1, 1}}, true},     // overlap on +X
		{AABB{Vec3{3, 0, 0}, Vec3{1, 1, 1}}, false},      // disjoint
		{AABB{Vec3{2, 0, 0}, Vec3{1, 1, 1}}, true},       // touching (faces meet)
		{AABB{Vec3{0, 0, 0}, Vec3{0.1, 0.1, 0.1}}, true}, // contained
	}
	for _, c := range cases {
		if got := a.Overlaps(c.b); got != c.want {
			t.Errorf("Overlaps(%v): got %v want %v", c.b, got, c.want)
		}
	}
}

func TestRayAABBHit(t *testing.T) {
	box := AABB{Center: Vec3{0, 0, 0}, Half: Vec3{1, 1, 1}}

	// Ray from -X axis, looking +X. Should hit the -X face at t=1.
	r := Ray{Origin: Vec3{-2, 0, 0}, Dir: Vec3{1, 0, 0}}
	h := RayAABB(r, box)
	if !h.Hit {
		t.Fatal("expected hit")
	}
	if !feq(h.T, 1) {
		t.Errorf("T: got %v want 1", h.T)
	}
	if !vecEq(h.Normal, Vec3{-1, 0, 0}) {
		t.Errorf("Normal: got %v want -X", h.Normal)
	}
	if !vecEq(h.Point, Vec3{-1, 0, 0}) {
		t.Errorf("Point: got %v want (-1,0,0)", h.Point)
	}
}

func TestRayAABBMissBehind(t *testing.T) {
	box := AABB{Center: Vec3{0, 0, 0}, Half: Vec3{1, 1, 1}}
	// Ray starts inside-and-past the box, pointing further out: should not hit.
	r := Ray{Origin: Vec3{3, 0, 0}, Dir: Vec3{1, 0, 0}}
	h := RayAABB(r, box)
	if h.Hit {
		t.Errorf("expected miss-behind, got hit at T=%v", h.T)
	}
}

func TestRayAABBParallelOutside(t *testing.T) {
	box := AABB{Center: Vec3{0, 0, 0}, Half: Vec3{1, 1, 1}}
	// Ray parallel to X axis but Y is outside the slab.
	r := Ray{Origin: Vec3{-2, 5, 0}, Dir: Vec3{1, 0, 0}}
	h := RayAABB(r, box)
	if h.Hit {
		t.Error("parallel-and-outside should miss")
	}
}

func TestSweptAABBHit(t *testing.T) {
	moving := AABB{Center: Vec3{-3, 0, 0}, Half: Vec3{0.5, 0.5, 0.5}}
	static := AABB{Center: Vec3{0, 0, 0}, Half: Vec3{1, 1, 1}}
	// Move +X by 4 (over the box). Should hit when moving box's right face
	// meets the static box's left face. Half(moving)+Half(static) = 1.5, so
	// moving center must reach -1.5 → starting at -3, that's distance 1.5 / 4 = 0.375.
	h := SweptAABB(moving, Vec3{4, 0, 0}, static)
	if !h.Hit {
		t.Fatal("expected swept hit")
	}
	if !feq(h.T, 0.375) {
		t.Errorf("T: got %v want 0.375", h.T)
	}
}

func TestSweptAABBMissTooShort(t *testing.T) {
	moving := AABB{Center: Vec3{-3, 0, 0}, Half: Vec3{0.5, 0.5, 0.5}}
	static := AABB{Center: Vec3{0, 0, 0}, Half: Vec3{1, 1, 1}}
	// Move +X by only 1; won't reach the static box.
	h := SweptAABB(moving, Vec3{1, 0, 0}, static)
	if h.Hit {
		t.Errorf("expected miss; got T=%v", h.T)
	}
}

func TestVec3Math(t *testing.T) {
	a := Vec3{1, 2, 3}
	b := Vec3{4, 5, 6}
	if !vecEq(a.Add(b), Vec3{5, 7, 9}) {
		t.Error("Add")
	}
	if !vecEq(b.Sub(a), Vec3{3, 3, 3}) {
		t.Error("Sub")
	}
	if !vecEq(a.Scale(2), Vec3{2, 4, 6}) {
		t.Error("Scale")
	}
	if !feq(Vec3{3, 4, 0}.Length(), 5) {
		t.Error("Length")
	}
	if !vecEq(Vec3{3, 4, 0}.Normalized(), Vec3{0.6, 0.8, 0}) {
		t.Error("Normalized")
	}
	if !vecEq(Vec3{}.Normalized(), Vec3{}) {
		t.Error("zero Normalized should be zero")
	}
}
