package router

import (
	"os"
	"path/filepath"
	"testing"
	"time"
)

func writeConf(t *testing.T, dir, code string, port, pid int) {
	t.Helper()
	body := "code=" + code + "\nport=" + itoa(port) + "\npid=" + itoa(pid) + "\nstarted=2026-01-01T00:00:00Z\n"
	if err := os.WriteFile(filepath.Join(dir, code+".conf"), []byte(body), 0o644); err != nil {
		t.Fatalf("write conf: %v", err)
	}
}

func itoa(n int) string {
	if n == 0 {
		return "0"
	}
	neg := n < 0
	if neg {
		n = -n
	}
	var b [20]byte
	i := len(b)
	for n > 0 {
		i--
		b[i] = byte('0' + n%10)
		n /= 10
	}
	if neg {
		i--
		b[i] = '-'
	}
	return string(b[i:])
}

func TestRegistryLookup(t *testing.T) {
	dir := t.TempDir()
	self := os.Getpid() // a guaranteed-alive pid
	writeConf(t, dir, "MAIN", 1338, self)
	writeConf(t, dir, "DUO", 1340, self)
	writeConf(t, dir, "DEAD", 1342, 0x7FFFFFF0) // implausible pid → treated dead

	reg := NewRegistry(dir, 10*time.Millisecond)

	if addr, ok := reg.Lookup("MAIN"); !ok || addr.Port != 1338 {
		t.Errorf("Lookup(MAIN) = %v,%v want :1338", addr, ok)
	}
	if addr, ok := reg.Lookup("duo"); !ok || addr.Port != 1340 { // case-insensitive
		t.Errorf("Lookup(duo) = %v,%v want :1340", addr, ok)
	}
	if _, ok := reg.Lookup("DEAD"); ok {
		t.Error("Lookup(DEAD) returned ok for a dead pid")
	}
	if _, ok := reg.Lookup("NOPE"); ok {
		t.Error("Lookup(NOPE) returned ok for an absent lobby")
	}
}

func TestRegistryReloadPicksUpNewLobby(t *testing.T) {
	dir := t.TempDir()
	reg := NewRegistry(dir, 5*time.Millisecond)
	if _, ok := reg.Lookup("LATE"); ok {
		t.Fatal("LATE present before it was written")
	}
	writeConf(t, dir, "LATE", 1350, os.Getpid())
	time.Sleep(10 * time.Millisecond) // let TTL expire
	if addr, ok := reg.Lookup("LATE"); !ok || addr.Port != 1350 {
		t.Errorf("after reload Lookup(LATE) = %v,%v want :1350", addr, ok)
	}
}

func TestRegistryMissingDir(t *testing.T) {
	reg := NewRegistry(filepath.Join(t.TempDir(), "does-not-exist"), time.Millisecond)
	if _, ok := reg.Lookup("X"); ok {
		t.Error("Lookup on missing dir returned ok")
	}
}

// TestRegistryKeepsCacheOnTransientError is the regression for the audit's
// "wipes the cache on any FS error" finding: a non-ENOENT scan failure (here, a
// suddenly-unreadable dir) must KEEP the previously-loaded lobbies rather than
// drop every live client.
func TestRegistryKeepsCacheOnTransientError(t *testing.T) {
	if os.Geteuid() == 0 {
		t.Skip("root bypasses directory permissions")
	}
	dir := t.TempDir()
	writeConf(t, dir, "KEEP", 1360, os.Getpid())
	reg := NewRegistry(dir, time.Millisecond)
	if _, ok := reg.Lookup("KEEP"); !ok {
		t.Fatal("KEEP not loaded initially")
	}
	// Make the dir unreadable → ReadDir returns a permission (non-ENOENT) error.
	if err := os.Chmod(dir, 0o000); err != nil {
		t.Fatalf("chmod: %v", err)
	}
	defer func() { _ = os.Chmod(dir, 0o755) }() // restore so TempDir cleanup works
	time.Sleep(5 * time.Millisecond)            // expire the 1ms TTL
	if _, ok := reg.Lookup("KEEP"); !ok {
		t.Error("KEEP dropped after a transient FS error; the cache should be retained")
	}
}
