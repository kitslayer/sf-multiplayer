package physics

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
)

// MapData mirrors the JSON schema produced by SFLevelDumper (M0a). One file per
// Landfall scene (excluding 102=stats). Fields are kept as plain slices/structs
// so the JSON is human-inspectable and version-stable.
type MapData struct {
	SceneIndex      int               `json:"sceneIndex"`
	Name            string            `json:"name"`
	StaticColliders []ColliderData    `json:"staticColliders"`
	PlayerSpawns    []SpawnData       `json:"playerSpawns"`
	WeaponSpawns    []SpawnData       `json:"weaponSpawns"`
	Killboxes       []KillboxData     `json:"killboxes"`
	SyncableObjects []SyncableObjData `json:"syncableObjects"`
}

// ColliderData is one static piece of level geometry. Rotation is included for
// completeness (some platforms in workshop maps are rotated) — for axis-aligned
// boxes the rotation can be ignored; for rotated boxes the M0.5 prototype path
// will approximate as the world-space AABB the rotated box covers.
type ColliderData struct {
	Pos  [3]float32 `json:"pos"`
	Rot  [4]float32 `json:"rot"`  // quaternion x,y,z,w; identity = 0,0,0,1
	Size [3]float32 `json:"size"` // full extent (not half)
	Kind string     `json:"kind"` // "Box" | "Mesh" | "Sphere" | "Capsule"
}

// SpawnData is a single spawn point on a map.
type SpawnData struct {
	Pos [3]float32 `json:"pos"`
}

// KillboxData is a kill-volume (lava, void, etc.).
type KillboxData struct {
	Pos  [3]float32 `json:"pos"`
	Size [3]float32 `json:"size"`
}

// SyncableObjData is a pre-placed dynamic object (barrel/crate/etc.) the server
// will own in M5+. For M0-M4 we ignore these.
type SyncableObjData struct {
	Pos  [3]float32 `json:"pos"`
	Type string     `json:"type"`
}

// LoadMapData reads a map JSON file from disk and returns the parsed structure.
// Errors include the filename for diagnostics.
func LoadMapData(path string) (*MapData, error) {
	raw, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("read %s: %w", path, err)
	}
	var m MapData
	if err := json.Unmarshal(raw, &m); err != nil {
		return nil, fmt.Errorf("parse %s: %w", path, err)
	}
	return &m, nil
}

// LoadMapDir loads every landfall-*.json file in dir, returning a map keyed by
// SceneIndex. Errors on the first malformed file. Missing dir is not an error
// (returns empty map) so the server can boot even before M0a has been run.
func LoadMapDir(dir string) (map[int]*MapData, error) {
	out := make(map[int]*MapData)
	entries, err := os.ReadDir(dir)
	if err != nil {
		if os.IsNotExist(err) {
			return out, nil
		}
		return nil, err
	}
	for _, e := range entries {
		if e.IsDir() {
			continue
		}
		name := e.Name()
		if filepath.Ext(name) != ".json" {
			continue
		}
		m, err := LoadMapData(filepath.Join(dir, name))
		if err != nil {
			return nil, err
		}
		out[m.SceneIndex] = m
	}
	return out, nil
}

// AsAABBs converts the static colliders of m into a slice of world-space AABBs.
// For Box colliders this is exact. For other shapes (Mesh/Sphere/Capsule) we
// approximate as the axis-aligned bounding box of the shape in world space —
// the dumper is responsible for providing the AABB envelope when it emits Mesh
// entries; for Box entries we ignore rotation as a deliberate v2 simplification
// (workshop maps with rotated platforms get slightly chunky collision).
func (m *MapData) AsAABBs() []AABB {
	out := make([]AABB, 0, len(m.StaticColliders))
	for _, c := range m.StaticColliders {
		out = append(out, AABB{
			Center: Vec3{c.Pos[0], c.Pos[1], c.Pos[2]},
			Half:   Vec3{c.Size[0] * 0.5, c.Size[1] * 0.5, c.Size[2] * 0.5},
		})
	}
	return out
}

// KillboxesAsAABBs converts killbox volumes to AABBs.
func (m *MapData) KillboxesAsAABBs() []AABB {
	out := make([]AABB, 0, len(m.Killboxes))
	for _, k := range m.Killboxes {
		out = append(out, AABB{
			Center: Vec3{k.Pos[0], k.Pos[1], k.Pos[2]},
			Half:   Vec3{k.Size[0] * 0.5, k.Size[1] * 0.5, k.Size[2] * 0.5},
		})
	}
	return out
}

// HydrateWorld loads m's geometry into w (replacing any prior Statics/Killboxes).
// Doesn't touch w.Entities.
func (m *MapData) HydrateWorld(w *World) {
	w.LoadStatics(m.AsAABBs())
	w.LoadKillboxes(m.KillboxesAsAABBs())
}
