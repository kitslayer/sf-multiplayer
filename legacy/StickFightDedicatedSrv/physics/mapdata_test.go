package physics

import (
	"os"
	"path/filepath"
	"testing"
)

func TestLoadMapData(t *testing.T) {
	// Write a sample JSON file and reload it.
	dir := t.TempDir()
	path := filepath.Join(dir, "landfall-7.json")
	sample := []byte(`{
		"sceneIndex": 7,
		"name": "sample",
		"staticColliders": [
			{"pos":[0,0,0], "rot":[0,0,0,1], "size":[10,1,5], "kind":"Box"},
			{"pos":[0,5,0], "rot":[0,0,0,1], "size":[10,1,5], "kind":"Box"}
		],
		"playerSpawns": [{"pos":[1,2,0]},{"pos":[-1,2,0]}],
		"weaponSpawns": [{"pos":[0,3,0]}],
		"killboxes": [{"pos":[0,-10,0], "size":[40,1,40]}],
		"syncableObjects": []
	}`)
	if err := os.WriteFile(path, sample, 0644); err != nil {
		t.Fatal(err)
	}

	m, err := LoadMapData(path)
	if err != nil {
		t.Fatalf("LoadMapData: %v", err)
	}
	if m.SceneIndex != 7 {
		t.Errorf("scene index: %v", m.SceneIndex)
	}
	if len(m.StaticColliders) != 2 {
		t.Errorf("colliders: %v", len(m.StaticColliders))
	}
	if len(m.PlayerSpawns) != 2 {
		t.Errorf("player spawns: %v", len(m.PlayerSpawns))
	}
	if len(m.WeaponSpawns) != 1 {
		t.Errorf("weapon spawns: %v", len(m.WeaponSpawns))
	}

	// LoadMapDir picks up the file.
	all, err := LoadMapDir(dir)
	if err != nil {
		t.Fatalf("LoadMapDir: %v", err)
	}
	if _, ok := all[7]; !ok {
		t.Errorf("scene 7 not in loaded dir")
	}

	// Hydrate into a world and check we got 2 statics + 1 killbox.
	w := NewWorld()
	m.HydrateWorld(w)
	if len(w.Statics) != 2 {
		t.Errorf("statics: %v", len(w.Statics))
	}
	if len(w.Killboxes) != 1 {
		t.Errorf("killboxes: %v", len(w.Killboxes))
	}
}

func TestLoadMapDirMissingOk(t *testing.T) {
	// Loading a non-existent directory shouldn't error — it's a server-startup
	// convenience so we can boot before M0a has been run.
	all, err := LoadMapDir("/definitely/not/a/real/path/here")
	if err != nil {
		t.Errorf("unexpected err: %v", err)
	}
	if len(all) != 0 {
		t.Errorf("expected empty map; got %v entries", len(all))
	}
}
