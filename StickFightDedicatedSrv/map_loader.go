package main

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
)

// LoadMapsFromDir reads every landfall-N.json under dir into the global
// loadedMaps registry. Idempotent. Returns the number of maps loaded.
//
// A missing/empty directory is not an error — the server still boots with an
// empty registry; per-lobby weapon-spawning falls back to the legacy heuristic
// for scenes not in the registry.
func LoadMapsFromDir(dir string) (int, error) {
	entries, err := os.ReadDir(dir)
	if err != nil {
		if os.IsNotExist(err) {
			return 0, nil
		}
		return 0, err
	}
	count := 0
	for _, e := range entries {
		if e.IsDir() || filepath.Ext(e.Name()) != ".json" {
			continue
		}
		raw, err := os.ReadFile(filepath.Join(dir, e.Name()))
		if err != nil {
			return count, fmt.Errorf("read %s: %w", e.Name(), err)
		}
		var m MapDataForLevel
		if err := json.Unmarshal(raw, &m); err != nil {
			return count, fmt.Errorf("parse %s: %w", e.Name(), err)
		}
		loadedMaps[m.SceneIndex] = &m
		count++
	}
	return count, nil
}

// WeaponSpawnCandidates returns a list of (x, y, z) positions a weapon can
// reasonably spawn at for the given scene. Priority:
//   1. Explicit WeaponPickUp markers from the dumped MapData
//   2. Player spawn positions, lifted +1m on Y (so the weapon hovers above the platform)
//   3. Empty if neither — caller falls back to its legacy heuristic
//
// Returned positions are world-space; the caller wraps them in a Vector3.
func WeaponSpawnCandidates(sceneIndex int32) []Vector3 {
	m, ok := loadedMaps[sceneIndex]
	if !ok || m == nil {
		return nil
	}
	out := make([]Vector3, 0, len(m.WeaponSpawns)+len(m.PlayerSpawns))
	for _, s := range m.WeaponSpawns {
		out = append(out, Vector3{X: s.Pos[0], Y: s.Pos[1], Z: s.Pos[2]})
	}
	if len(out) == 0 {
		// Fallback: use player spawn positions, lifted slightly so a weapon
		// hovers above the platform rather than embedding into it.
		for _, s := range m.PlayerSpawns {
			out = append(out, Vector3{X: s.Pos[0], Y: s.Pos[1] + 1.0, Z: s.Pos[2]})
		}
	}
	return out
}
