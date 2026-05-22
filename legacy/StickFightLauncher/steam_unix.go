//go:build !windows

package main

import "os"

func (s *Steam) GetRootFolder() string {
	home, err := os.UserHomeDir()
	if err != nil {
		return ""
	}

	candidates := []string{
		home + "/.steam/steam",
		home + "/.local/share/Steam",
		home + "/.steam/root",
	}
	for _, p := range candidates {
		if fi, err := os.Stat(p); err == nil && fi.IsDir() {
			return p
		}
	}
	return ""
}
