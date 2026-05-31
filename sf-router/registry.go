package router

import (
	"bufio"
	"net"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"time"
)

// Registry resolves a lobby code to its backend UDP address by reading the
// file registry that launch-lobby.sh writes (/tmp/sf-lobbies/<CODE>.conf, with
// key=value lines incl. code=, port=, pid=). Backends are loopback, so the
// address is 127.0.0.1:<port>. Entries whose pid is dead are dropped — same
// liveness rule serve-lobbies.py uses. Results are cached and refreshed on a
// short TTL so the hot path doesn't stat the directory per datagram.
type Registry struct {
	dir string
	ttl time.Duration

	mu       sync.Mutex
	byCode   map[string]*net.UDPAddr
	loadedAt time.Time
}

// NewRegistry returns a registry reading dir (e.g. /tmp/sf-lobbies) with the
// given cache TTL (~2s is plenty; lobbies come and go on human timescales).
func NewRegistry(dir string, ttl time.Duration) *Registry {
	return &Registry{dir: dir, ttl: ttl, byCode: map[string]*net.UDPAddr{}}
}

// Lookup returns the backend address for a lobby code (case-insensitive),
// reloading the registry first if the cache is stale.
func (r *Registry) Lookup(code string) (*net.UDPAddr, bool) {
	code = strings.ToUpper(strings.TrimSpace(code))
	r.mu.Lock()
	defer r.mu.Unlock()
	if time.Since(r.loadedAt) > r.ttl {
		r.reloadLocked()
	}
	addr, ok := r.byCode[code]
	return addr, ok
}

// Codes returns the currently-known live lobby codes (for /router/stats / logs).
func (r *Registry) Codes() []string {
	r.mu.Lock()
	defer r.mu.Unlock()
	if time.Since(r.loadedAt) > r.ttl {
		r.reloadLocked()
	}
	out := make([]string, 0, len(r.byCode))
	for c := range r.byCode {
		out = append(out, c)
	}
	return out
}

// reloadLocked rescans the registry dir. Caller holds r.mu.
func (r *Registry) reloadLocked() {
	r.loadedAt = time.Now()
	next := make(map[string]*net.UDPAddr)
	entries, err := os.ReadDir(r.dir)
	if err != nil {
		// Dir missing/unreadable → no lobbies. Keep next empty.
		r.byCode = next
		return
	}
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".conf") {
			continue
		}
		code, port, pid, ok := parseConf(filepath.Join(r.dir, e.Name()))
		if !ok || !pidAlive(pid) {
			continue
		}
		next[strings.ToUpper(code)] = &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1), Port: port}
	}
	r.byCode = next
}

// parseConf reads a launch-lobby.sh .conf and returns code, port, pid.
func parseConf(path string) (code string, port, pid int, ok bool) {
	f, err := os.Open(path)
	if err != nil {
		return "", 0, 0, false
	}
	defer f.Close()
	sc := bufio.NewScanner(f)
	for sc.Scan() {
		line := sc.Text()
		i := strings.IndexByte(line, '=')
		if i < 0 {
			continue
		}
		k, v := line[:i], strings.TrimSpace(line[i+1:])
		switch k {
		case "code":
			code = v
		case "port":
			port, _ = strconv.Atoi(v)
		case "pid":
			pid, _ = strconv.Atoi(v)
		}
	}
	if code == "" || port <= 0 || port > 65535 || pid <= 0 {
		return "", 0, 0, false
	}
	return code, port, pid, true
}

// pidAlive reports whether pid is a live process. signal 0 probes existence:
// nil → alive; EPERM → exists but not ours (still alive); ESRCH → gone.
func pidAlive(pid int) bool {
	err := syscall.Kill(pid, 0)
	if err == nil {
		return true
	}
	return err == syscall.EPERM
}
