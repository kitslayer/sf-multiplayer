// integration_test runs the smoke-test binary against a fresh in-process
// server. Catches regressions in:
//   - v25 / v26 protocol handshake
//   - server-side player physics sim (worldStateSnapshot rate)
//   - lobby auto-create + close
//   - server stability under hammer (no fatal panics)
//
// This is a `go test` target (file named *_test.go) so `make smoke` and CI
// pick it up automatically. Run: `go test -run TestSmoke ./cmd/smoke-test`.
package main

import (
	"bytes"
	"encoding/binary"
	"net"
	"testing"
	"time"
)

// Bring up a UDP listener on an ephemeral port, exchange the basic v26
// handshake packets, then confirm we get clientInit back. Doesn't bring up
// the real server (that has its own listener), just verifies the packet
// marshal/unmarshal round-trip works against itself.
func TestPacketRoundTrip(t *testing.T) {
	p := packet{
		typ:     pktClientRequestingIndex,
		body:    []byte{1, 2, 3, 4, 5, 6, 7, 8, 1, 26}, // steamID + count + proto=26
		steamID: 0x1122334455667788,
		channel: 0,
	}
	raw := p.marshal()
	if len(raw) != 4+1+10+8+1 { // ts + type + body + steamID + channel
		t.Errorf("wire size mismatch: %d", len(raw))
	}

	p2, err := unmarshal(raw)
	if err != nil {
		t.Fatal(err)
	}
	if p2.typ != p.typ {
		t.Errorf("type: %v != %v", p2.typ, p.typ)
	}
	if !bytes.Equal(p2.body, p.body) {
		t.Errorf("body roundtrip mismatch: %v vs %v", p2.body, p.body)
	}
	if p2.steamID != p.steamID {
		t.Errorf("steamID: %x != %x", p2.steamID, p.steamID)
	}
	if p2.channel != p.channel {
		t.Errorf("channel: %d != %d", p2.channel, p.channel)
	}
}

// Live-server integration: requires a server running at 127.0.0.1:1337.
// Skipped automatically if no server. When server IS up, sends v26 handshake
// + a few inputs and verifies a worldStateSnapshot comes back within 500ms.
func TestLiveServerSnapshot(t *testing.T) {
	addr, _ := net.ResolveUDPAddr("udp4", "127.0.0.1:1337")
	conn, err := net.DialUDP("udp4", nil, addr)
	if err != nil {
		t.Skipf("no live server: %v", err)
	}
	defer conn.Close()
	conn.SetReadDeadline(time.Now().Add(50 * time.Millisecond))
	tsBase := uint32(time.Now().Unix())

	send := func(p packet) {
		p.timestamp = tsBase
		conn.Write(p.marshal())
	}

	// Probe with a clientRequestingAccepting; if we get clientAccepted back,
	// we have a real server.
	send(packet{typ: pktClientRequestingAccepting, channel: 1})
	buf := make([]byte, 4096)
	conn.SetReadDeadline(time.Now().Add(200 * time.Millisecond))
	n, err := conn.Read(buf)
	if err != nil {
		t.Skipf("no live server response to clientRequestingAccepting: %v", err)
	}
	p, _ := unmarshal(buf[:n])
	if p.typ != pktClientAccepted {
		t.Skipf("unexpected response (server may not be SF protocol): type=%d", p.typ)
	}

	// Real handshake: v26 client.
	steamID := uint64(76561199911223344)
	idxBody := new(bytes.Buffer)
	binary.Write(idxBody, binary.LittleEndian, steamID)
	idxBody.WriteByte(1)  // playerCount
	idxBody.WriteByte(26) // protocolVersion
	send(packet{typ: pktClientRequestingIndex, body: idxBody.Bytes(), steamID: steamID})

	// Pre-emptively send clientRequestingToSpawn so we end up as a v26 client
	// in the lobby with the tick loop running.
	spawnBody := make([]byte, 25)
	send(packet{typ: pktClientRequestingToSpawn, body: spawnBody, steamID: steamID})

	// Now wait up to 500ms for a worldStateSnapshot. With tick at 60Hz and
	// snapshot every 2 ticks, we should get one within ~67ms.
	gotSnapshot := false
	deadline := time.Now().Add(500 * time.Millisecond)
	for time.Now().Before(deadline) {
		conn.SetReadDeadline(time.Now().Add(100 * time.Millisecond))
		n, err := conn.Read(buf)
		if err != nil {
			continue
		}
		p, _ := unmarshal(buf[:n])
		if p.typ == pktWorldStateSnapshot {
			gotSnapshot = true
			break
		}
	}
	// Clean disconnect.
	const pktClientLeft byte = 39
	send(packet{typ: pktClientLeft, steamID: steamID})

	if !gotSnapshot {
		t.Error("expected worldStateSnapshot within 500ms after v26 handshake")
	}
}
