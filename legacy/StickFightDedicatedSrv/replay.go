package main

import (
	"encoding/binary"
	"os"
	"path/filepath"
	"sync"
	"time"
)

// ReplayLogger appends snapshot + serverEvent records to a binary file per
// lobby session. The file is opened lazily on first write and closed when the
// lobby closes. Format is intentionally simple so an offline player can replay
// the match into a viewer:
//
//   File header (16 bytes):
//     magic   "SFRPL\0\0\0" (8 bytes)
//     version uint32        (currently 1)
//     unused  uint32
//
//   Per record (variable):
//     uint32 sinceStartMs   ms since the first record (so replays don't depend
//                            on absolute wall-clock time)
//     byte   kind           0 = worldStateSnapshot, 1 = serverEvent
//     uint32 length         bytes that follow
//     []byte payload        the marshaled packet body (matches wire format)
//
// The replay isn't a one-to-one reconstruction of every packet — only the
// authoritative server-emitted ones — but that's sufficient to scrub a match
// frame-by-frame in a viewer that consumes the same wire format.
type ReplayLogger struct {
	mu      sync.Mutex
	f       *os.File
	started time.Time
}

const (
	replayKindSnapshot byte = 0
	replayKindEvent    byte = 1
)

var replayMagic = [8]byte{'S', 'F', 'R', 'P', 'L', 0, 0, 0}

// NewReplayLogger creates a fresh logger for a lobby. dir is created if it
// doesn't already exist. Returns nil if replayDir is empty.
func NewReplayLogger(dir, roomCode string) *ReplayLogger {
	if dir == "" {
		return nil
	}
	if err := os.MkdirAll(dir, 0755); err != nil {
		log.Warn("Replay: could not mkdir ", dir, ": ", err)
		return nil
	}
	name := filepath.Join(dir, roomCode+"-"+time.Now().UTC().Format("20060102T150405")+".sfreplay")
	f, err := os.Create(name)
	if err != nil {
		log.Warn("Replay: could not create ", name, ": ", err)
		return nil
	}
	// Write file header so a reader can validate before parsing records.
	f.Write(replayMagic[:])
	var hdr [8]byte
	binary.LittleEndian.PutUint32(hdr[0:4], 1) // version
	f.Write(hdr[:])
	log.Info("Replay: logging to ", name)
	return &ReplayLogger{f: f, started: time.Now()}
}

// Append writes a single record. Safe for concurrent callers — the tick goroutine
// and event goroutines can both call this.
func (r *ReplayLogger) Append(kind byte, payload []byte) {
	if r == nil || r.f == nil {
		return
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	var head [9]byte
	binary.LittleEndian.PutUint32(head[0:4], uint32(time.Since(r.started).Milliseconds()))
	head[4] = kind
	binary.LittleEndian.PutUint32(head[5:9], uint32(len(payload)))
	r.f.Write(head[:])
	if len(payload) > 0 {
		r.f.Write(payload)
	}
}

// Close finishes writing and releases the file handle.
func (r *ReplayLogger) Close() {
	if r == nil {
		return
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.f != nil {
		r.f.Close()
		r.f = nil
	}
}
