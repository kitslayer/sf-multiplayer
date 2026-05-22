// Package oracle is the Go-side client for the headless Stick Fight oracle
// (sf-headless-host BepInEx plugin). One Oracle per Lobby owns:
//   - The OS process running headless SF + BepInEx + SFHeadlessHost
//   - A UDP socket that talks JSON to the oracle's bridge port
//   - A goroutine that polls/streams snapshots and feeds them to subscribers
//
// Phase 6.3 wires Lobby up to spawn/kill/query an Oracle. Phase 6.4 retires
// the Go AABB physics in favor of these snapshots as the source of truth.
package oracle

import (
	"encoding/json"
	"errors"
	"fmt"
	"net"
	"os"
	"os/exec"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

// Snapshot is one tick of oracle state. Field names mirror the wire JSON.
type Snapshot struct {
	Tick  int64    `json:"tick"`
	Scene string   `json:"scene"`
	Ents  []Entity `json:"ents"`
}

// Entity is one player rig the oracle saw in the active scene.
type Entity struct {
	Slot int     `json:"slot"`
	X    float32 `json:"x"`
	Y    float32 `json:"y"`
	Z    float32 `json:"z"`
}

// Oracle is a per-lobby headless SF instance + its bridge connection.
type Oracle struct {
	// Process management.
	gamePort   int    // Lidgren port (clients connect here for gameplay)
	bridgePort int    // JSON control port (Go server talks here)
	scene      int    // Initial scene buildIndex
	cmd        *exec.Cmd
	stdout     *os.File

	// Bridge connection.
	conn *net.UDPConn

	// Latest snapshot. Protected with atomics + mutex for the slice.
	snapMu     sync.RWMutex
	lastSnap   Snapshot
	lastTickAt time.Time

	// Lifecycle.
	closed atomic.Bool
	done   chan struct{}
}

// Config controls a new Oracle.
type Config struct {
	// LauncherScript is the path to the shell wrapper that knows how to start
	// Stick Fight under Proton/Goldberg. Must accept SFHEADLESS_* env vars
	// and forward to StickFight.exe with -batchmode -nographics. If empty,
	// the default $HOME/sf-multiplayer/launch-sf-headless.sh is used.
	LauncherScript string

	// GamePort is the Lidgren UDP port the oracle's SF instance binds for
	// in-game traffic. Must be unique per oracle on the host.
	GamePort int

	// BridgePort is the JSON UDP port the oracle's bridge socket binds.
	// Must be unique per oracle and different from GamePort.
	BridgePort int

	// Scene is the Landfall buildIndex to load on boot. Use 6 (Desert3) as a
	// safe default; the oracle can be told to switch via Bridge.LoadMap.
	Scene int

	// BootTimeout is how long to wait for the bridge ping to succeed after
	// process launch. Use 60s — Proton + Unity init is slow.
	BootTimeout time.Duration

	// SnapshotInterval is how often we expect a snapshot. The oracle emits at
	// 30 Hz; we tolerate up to SnapshotInterval × 3 of silence before flagging
	// the oracle stalled.
	SnapshotInterval time.Duration
}

// Default returns a Config with sane defaults for a single oracle on the dev box.
func Default() Config {
	return Config{
		GamePort:         1340,
		BridgePort:       1341,
		Scene:            6,
		BootTimeout:      60 * time.Second,
		SnapshotInterval: 33 * time.Millisecond,
	}
}

// Spawn launches a new oracle process and connects to its bridge.
// Returns once the bridge responds to a ping or the boot timeout expires.
func Spawn(cfg Config) (*Oracle, error) {
	if cfg.GamePort == cfg.BridgePort {
		return nil, errors.New("oracle: GamePort and BridgePort must differ")
	}
	if cfg.GamePort <= 0 || cfg.BridgePort <= 0 {
		return nil, errors.New("oracle: invalid port")
	}
	if cfg.BootTimeout <= 0 {
		cfg.BootTimeout = 60 * time.Second
	}
	if cfg.SnapshotInterval <= 0 {
		cfg.SnapshotInterval = 33 * time.Millisecond
	}
	launcher := cfg.LauncherScript
	if launcher == "" {
		home, _ := os.UserHomeDir()
		launcher = home + "/sf-multiplayer/launch-sf-headless.sh"
	}

	o := &Oracle{
		gamePort:   cfg.GamePort,
		bridgePort: cfg.BridgePort,
		scene:      cfg.Scene,
		done:       make(chan struct{}),
	}

	// Spawn the process.
	cmd := exec.Command(launcher)
	cmd.Env = append(os.Environ(),
		fmt.Sprintf("SFHEADLESS_PORT=%d", cfg.GamePort),
		fmt.Sprintf("SFHEADLESS_BRIDGEPORT=%d", cfg.BridgePort),
		fmt.Sprintf("SFHEADLESS_SCENE=%d", cfg.Scene),
	)
	logPath := fmt.Sprintf("/tmp/sf-oracle-%d.log", cfg.BridgePort)
	logf, err := os.Create(logPath)
	if err != nil {
		return nil, fmt.Errorf("oracle: open log %s: %w", logPath, err)
	}
	cmd.Stdout = logf
	cmd.Stderr = logf
	cmd.SysProcAttr = sysProcAttrDetached()
	if err := cmd.Start(); err != nil {
		logf.Close()
		return nil, fmt.Errorf("oracle: start %s: %w", launcher, err)
	}
	o.cmd = cmd
	o.stdout = logf

	// Open bridge socket (we connect-style so reads/writes are scoped to oracle).
	addr := &net.UDPAddr{IP: net.ParseIP("127.0.0.1"), Port: cfg.BridgePort}
	c, err := net.DialUDP("udp4", nil, addr)
	if err != nil {
		o.Kill()
		return nil, fmt.Errorf("oracle: dial bridge: %w", err)
	}
	o.conn = c

	// Wait for ping success.
	deadline := time.Now().Add(cfg.BootTimeout)
	for time.Now().Before(deadline) {
		if reply, err := o.send(`{"cmd":"ping"}`, 500*time.Millisecond); err == nil {
			if strings.Contains(reply, `"reply":"pong"`) {
				go o.runStreamReader()
				return o, nil
			}
		}
		time.Sleep(2 * time.Second)
	}
	o.Kill()
	return nil, fmt.Errorf("oracle: boot timeout (%s) waiting for bridge ping on %d", cfg.BootTimeout, cfg.BridgePort)
}

// send writes one JSON command and reads one reply with the given deadline.
// For one-shot RPC. The snapshot-stream reader uses a separate longer-lived
// read loop in runStreamReader.
func (o *Oracle) send(cmd string, timeout time.Duration) (string, error) {
	if o.closed.Load() {
		return "", errors.New("oracle: closed")
	}
	if _, err := o.conn.Write([]byte(cmd)); err != nil {
		return "", err
	}
	buf := make([]byte, 16*1024)
	_ = o.conn.SetReadDeadline(time.Now().Add(timeout))
	n, err := o.conn.Read(buf)
	if err != nil {
		return "", err
	}
	return string(buf[:n]), nil
}

// runStreamReader loops on UDP reads and updates lastSnap. The oracle emits
// snapshots at 30 Hz; we treat each {"reply":"snapshot",...} packet as one
// tick. Other replies are routed to the one-shot send() via the conn buffer
// — UDP unordered delivery means small races are possible but acceptable.
func (o *Oracle) runStreamReader() {
	defer close(o.done)
	buf := make([]byte, 16*1024)
	for !o.closed.Load() {
		_ = o.conn.SetReadDeadline(time.Now().Add(5 * time.Second))
		n, err := o.conn.Read(buf)
		if err != nil {
			if o.closed.Load() {
				return
			}
			continue
		}
		body := buf[:n]
		// Cheap pre-check before json.Unmarshal cost.
		if !bytesContain(body, []byte(`"reply":"snapshot"`)) {
			continue
		}
		var snap Snapshot
		if err := json.Unmarshal(body, &snap); err != nil {
			continue
		}
		o.snapMu.Lock()
		o.lastSnap = snap
		o.lastTickAt = time.Now()
		o.snapMu.Unlock()
	}
}

// Snapshot returns the most recently-received state snapshot from the oracle.
// Returns (Snapshot{}, false) if no snapshot has arrived yet.
func (o *Oracle) Snapshot() (Snapshot, bool) {
	o.snapMu.RLock()
	defer o.snapMu.RUnlock()
	if o.lastTickAt.IsZero() {
		return Snapshot{}, false
	}
	return o.lastSnap, true
}

// LoadMap tells the oracle to switch to a different scene index.
func (o *Oracle) LoadMap(sceneIndex int) error {
	cmd := fmt.Sprintf(`{"cmd":"loadMap","scene":%d}`, sceneIndex)
	_, err := o.send(cmd, 2*time.Second)
	return err
}

// Ping sanity-checks the oracle is responsive.
func (o *Oracle) Ping() error {
	reply, err := o.send(`{"cmd":"ping"}`, 1*time.Second)
	if err != nil {
		return err
	}
	if !strings.Contains(reply, `"reply":"pong"`) {
		return fmt.Errorf("oracle: unexpected ping reply: %s", reply)
	}
	return nil
}

// GamePort is the port real SF clients connect to for actual gameplay.
func (o *Oracle) GamePort() int { return o.gamePort }

// BridgePort is the port the bridge socket listens on (for Go-side use).
func (o *Oracle) BridgePort() int { return o.bridgePort }

// Kill terminates the oracle process tree and closes the bridge socket.
// Safe to call multiple times.
//
// We can't just signal o.cmd.Process — Proton spawns a tree (Python wrapper
// → wineserver → StickFight.exe). The cmd handle only sees the topmost
// Python process; signalling that doesn't reliably take down the actual
// SF process. Instead we kill the process *group* we created in
// sysProcAttrDetached (Setpgid=true), which covers every descendant.
func (o *Oracle) Kill() {
	if !o.closed.CompareAndSwap(false, true) {
		return
	}
	if o.conn != nil {
		o.conn.Close()
	}
	if o.cmd != nil && o.cmd.Process != nil {
		pid := o.cmd.Process.Pid
		killGroup(pid)
		// Proton re-parents the actual StickFight.exe + wineserver out of our
		// process group, so the group kill misses them. Additionally pkill any
		// process whose argv mentions this oracle's unity log path — that path
		// is unique per oracle (sf-oracle-unity-<bridgeport>.log), so we only
		// kill OUR instances even when multiple oracles share a host.
		unityLog := fmt.Sprintf("sf-oracle-unity-%d.log", o.bridgePort)
		_ = exec.Command("pkill", "-9", "-f", unityLog).Run()

		done := make(chan error, 1)
		go func() { done <- o.cmd.Wait() }()
		select {
		case <-done:
		case <-time.After(2 * time.Second):
			killGroupForce(pid)
			_ = exec.Command("pkill", "-9", "-f", unityLog).Run()
			select {
			case <-done:
			case <-time.After(2 * time.Second):
				// OS reaps eventually; we've done what we can.
			}
		}
	}
	if o.stdout != nil {
		o.stdout.Close()
	}
}

// bytesContain reports whether haystack contains needle. Faster than
// strings.Contains on []byte since it avoids the conversion.
func bytesContain(haystack, needle []byte) bool {
	if len(needle) == 0 {
		return true
	}
	if len(needle) > len(haystack) {
		return false
	}
outer:
	for i := 0; i <= len(haystack)-len(needle); i++ {
		for j := 0; j < len(needle); j++ {
			if haystack[i+j] != needle[j] {
				continue outer
			}
		}
		return true
	}
	return false
}
