package main

import (
	"os"
	"path/filepath"
	"testing"
)

// LoadMapsFromDir reads JSON dumps, populates loadedMaps.
func TestLoadMapsFromDir(t *testing.T) {
	// reset registry
	loadedMaps = make(map[int32]*MapDataForLevel)

	dir := t.TempDir()
	sample := []byte(`{
		"sceneIndex": 6,
		"name": "test",
		"staticColliders": [{"pos":[0,0,0],"rot":[0,0,0,1],"size":[10,1,5],"kind":"Box"}],
		"playerSpawns": [{"pos":[1,2,3]},{"pos":[-1,2,3]}],
		"weaponSpawns": [{"pos":[0,5,0]}],
		"killboxes": [],
		"syncableObjects": []
	}`)
	if err := os.WriteFile(filepath.Join(dir, "landfall-6.json"), sample, 0644); err != nil {
		t.Fatal(err)
	}

	n, err := LoadMapsFromDir(dir)
	if err != nil {
		t.Fatal(err)
	}
	if n != 1 {
		t.Errorf("loaded %d, want 1", n)
	}
	m, ok := loadedMaps[6]
	if !ok || m == nil {
		t.Fatal("scene 6 not in registry")
	}
	if len(m.PlayerSpawns) != 2 {
		t.Errorf("player spawns: %d", len(m.PlayerSpawns))
	}
	if len(m.WeaponSpawns) != 1 {
		t.Errorf("weapon spawns: %d", len(m.WeaponSpawns))
	}
}

// WeaponSpawnCandidates prefers explicit weapon-pickup markers; falls back to
// player spawn positions (with +1m Y) when none exist.
func TestWeaponSpawnCandidatesExplicit(t *testing.T) {
	loadedMaps = map[int32]*MapDataForLevel{
		7: {
			SceneIndex:   7,
			WeaponSpawns: []SpawnJSON{{Pos: [3]float32{0, 5, 0}}, {Pos: [3]float32{0, 5, 10}}},
			PlayerSpawns: []SpawnJSON{{Pos: [3]float32{0, 2, 0}}},
		},
	}
	cands := WeaponSpawnCandidates(7)
	if len(cands) != 2 {
		t.Fatalf("expected 2 explicit weapon candidates, got %d", len(cands))
	}
	if cands[0].Y != 5 {
		t.Errorf("expected Y=5, got %v", cands[0].Y)
	}
}

func TestWeaponSpawnCandidatesFallback(t *testing.T) {
	loadedMaps = map[int32]*MapDataForLevel{
		8: {
			SceneIndex:   8,
			WeaponSpawns: nil,
			PlayerSpawns: []SpawnJSON{
				{Pos: [3]float32{1, 2, 3}},
				{Pos: [3]float32{-1, 2, 3}},
			},
		},
	}
	cands := WeaponSpawnCandidates(8)
	if len(cands) != 2 {
		t.Fatalf("expected 2 fallback weapon candidates, got %d", len(cands))
	}
	// Fallback lifts Y by 1.0.
	if cands[0].Y != 3.0 {
		t.Errorf("expected fallback Y=3.0 (2+1), got %v", cands[0].Y)
	}
}

func TestWeaponSpawnCandidatesUnknownScene(t *testing.T) {
	loadedMaps = map[int32]*MapDataForLevel{}
	cands := WeaponSpawnCandidates(99)
	if cands != nil {
		t.Errorf("expected nil for unknown scene, got %v", cands)
	}
}
