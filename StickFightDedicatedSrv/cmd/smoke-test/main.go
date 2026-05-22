// smoke-test exercises a running StickFightDedicatedSrv with a mock v26 client.
// It performs the protocol handshake the same way a real SF client would, then
// streams synthetic playerInput packets and prints any worldStateSnapshot
// packets the server broadcasts back.
//
// Purpose:
//   1. End-to-end protocol round-trip without needing a real Stick Fight client
//   2. Verify the server's v26 code paths (tick loop, snapshot broadcast,
//      playerInput dispatch) work without crashes
//   3. Provides a reproducible regression target for future protocol changes
//
// Usage:
//     go run ./cmd/smoke-test [-addr 127.0.0.1:1337] [-secs 30]
package main

import (
	"bytes"
	"encoding/binary"
	"flag"
	"fmt"
	"net"
	"os"
	"time"
)

// --- Wire-format helpers (subset of what the server's packets.go does).

const (
	pktClientRequestingAccepting byte = 3
	pktClientAccepted            byte = 4
	pktClientInit                byte = 5
	pktClientRequestingIndex     byte = 6
	pktClientRequestingToSpawn   byte = 7
	pktClientSpawned             byte = 8
	pktPlayerUpdate              byte = 10
	pktPlayerInput               byte = 42
	pktWorldStateSnapshot        byte = 43
	pktServerEvent               byte = 44
)

// packet wraps a SF UDP packet. Frame: 4 bytes LE timestamp | 1 byte type |
// payload | 8 bytes LE steamID | 1 byte channel.
type packet struct {
	timestamp uint32
	typ       byte
	body      []byte
	steamID   uint64
	channel   byte
}

func (p packet) marshal() []byte {
	buf := new(bytes.Buffer)
	binary.Write(buf, binary.LittleEndian, p.timestamp)
	buf.WriteByte(p.typ)
	buf.Write(p.body)
	binary.Write(buf, binary.LittleEndian, p.steamID)
	buf.WriteByte(p.channel)
	return buf.Bytes()
}

func unmarshal(raw []byte) (packet, error) {
	if len(raw) < 14 { // 4 + 1 + 0 + 8 + 1
		return packet{}, fmt.Errorf("packet too small: %d bytes", len(raw))
	}
	p := packet{}
	r := bytes.NewReader(raw)
	binary.Read(r, binary.LittleEndian, &p.timestamp)
	p.typ, _ = r.ReadByte()
	bodyLen := len(raw) - 4 - 1 - 8 - 1
	if bodyLen > 0 {
		p.body = make([]byte, bodyLen)
		r.Read(p.body)
	}
	binary.Read(r, binary.LittleEndian, &p.steamID)
	p.channel, _ = r.ReadByte()
	return p, nil
}

func main() {
	addr := flag.String("addr", "127.0.0.1:1337", "Server address")
	secs := flag.Int("secs", 15, "How long to run the synthetic input stream")
	protocolVer := flag.Int("proto", 26, "Protocol version to advertise (25=relay, 26=authoritative)")
	steamID := flag.Uint64("steam", 76561199888888881, "Fake SteamID to identify as")
	flag.Parse()

	udpAddr, err := net.ResolveUDPAddr("udp4", *addr)
	if err != nil {
		fmt.Fprintln(os.Stderr, "resolve:", err)
		os.Exit(1)
	}
	conn, err := net.DialUDP("udp4", nil, udpAddr)
	if err != nil {
		fmt.Fprintln(os.Stderr, "dial:", err)
		os.Exit(1)
	}
	defer conn.Close()

	tsBase := uint32(time.Now().Unix())
	send := func(p packet) {
		p.timestamp = tsBase
		raw := p.marshal()
		if _, err := conn.Write(raw); err != nil {
			fmt.Fprintln(os.Stderr, "send:", err)
		}
	}

	// 1. clientRequestingAccepting (empty body, channel 1)
	send(packet{typ: pktClientRequestingAccepting, channel: 1})
	fmt.Println("[smoke] sent clientRequestingAccepting")
	time.Sleep(150 * time.Millisecond)

	// 2. clientRequestingIndex: 8 bytes SteamID + 1 byte playerCount + 1 byte protocolVersion
	idxBody := new(bytes.Buffer)
	binary.Write(idxBody, binary.LittleEndian, *steamID)
	idxBody.WriteByte(1) // playerCount = 1
	idxBody.WriteByte(byte(*protocolVer))
	send(packet{typ: pktClientRequestingIndex, body: idxBody.Bytes(), steamID: *steamID})
	fmt.Printf("[smoke] sent clientRequestingIndex (steamID=%d proto=%d)\n", *steamID, *protocolVer)

	// 3. After clientInit arrives, the real client sends clientRequestingToSpawn.
	// We do that pre-emptively after a short delay — the server tolerates either order.
	go func() {
		time.Sleep(500 * time.Millisecond)
		spawnBody := make([]byte, 25)
		spawnBody[0] = 0 // playerIndex
		// floats remain zero — server doesn't validate them, just broadcasts.
		send(packet{typ: pktClientRequestingToSpawn, body: spawnBody, steamID: *steamID})
		fmt.Println("[smoke] sent clientRequestingToSpawn")
	}()

	// 4. Start streaming playerInput packets at 60Hz (v26) AND fake playerUpdate
	//    at 50Hz (v25) so we exercise both code paths regardless of which the
	//    server expects. The server should accept whichever matches the
	//    advertised protocolVersion.
	inputTicker := time.NewTicker(time.Second / 60)
	defer inputTicker.Stop()
	updateTicker := time.NewTicker(time.Second / 50)
	defer updateTicker.Stop()

	// Listener goroutine — print any incoming packets so we see snapshots etc.
	go func() {
		buf := make([]byte, 8192)
		stats := map[byte]int{}
		t := time.NewTicker(time.Second)
		defer t.Stop()
		for {
			conn.SetReadDeadline(time.Now().Add(2 * time.Second))
			n, err := conn.Read(buf)
			if err != nil {
				select {
				case <-t.C:
					if len(stats) > 0 {
						fmt.Printf("[smoke] per-sec recv: %v\n", stats)
						stats = map[byte]int{}
					}
				default:
				}
				continue
			}
			p, err := unmarshal(buf[:n])
			if err != nil {
				fmt.Println("[smoke] bad packet:", err)
				continue
			}
			stats[p.typ]++
			if p.typ == pktWorldStateSnapshot {
				// Decode header: u32 tick, byte snapType, u16 entityCount
				if len(p.body) >= 7 {
					tick := binary.LittleEndian.Uint32(p.body[0:4])
					snapType := p.body[4]
					ec := binary.LittleEndian.Uint16(p.body[5:7])
					if ec > 0 {
						fmt.Printf("[smoke] worldStateSnapshot tick=%d type=%d entities=%d\n", tick, snapType, ec)
					}
				}
			} else if p.typ == pktClientInit {
				fmt.Printf("[smoke] clientInit received (%d-byte body, first byte=%d)\n", len(p.body), firstOr(p.body))
			}
		}
	}()

	endAt := time.Now().Add(time.Duration(*secs) * time.Second)
	var seq uint32 = 0
	var inputs, updates int
	for time.Now().Before(endAt) {
		select {
		case <-inputTicker.C:
			// playerInput: byte idx + f32x4 stick/aim + u16 buttons + u32 seq
			if *protocolVer >= 26 {
				inBody := new(bytes.Buffer)
				inBody.WriteByte(0) // playerIndex
				// Stick moves slightly so the server's lobby tick has reason to update positions.
				stickX := float32(0.5)
				binary.Write(inBody, binary.LittleEndian, stickX)
				binary.Write(inBody, binary.LittleEndian, float32(0))
				binary.Write(inBody, binary.LittleEndian, float32(0))
				binary.Write(inBody, binary.LittleEndian, float32(0))
				binary.Write(inBody, binary.LittleEndian, uint16(0))
				binary.Write(inBody, binary.LittleEndian, seq)
				seq++
				send(packet{typ: pktPlayerInput, body: inBody.Bytes(), steamID: *steamID, channel: 2})
				inputs++
			}
		case <-updateTicker.C:
			// playerUpdate (mimics what the patched DLL sends): i16 posY, i16 posZ, byte rotX, byte rotY, byte yVal, byte movement, byte fightState, u16 projCount=0, byte weapon=0
			up := new(bytes.Buffer)
			binary.Write(up, binary.LittleEndian, int16(0))
			binary.Write(up, binary.LittleEndian, int16(int(time.Now().UnixNano()/1e9)%1000)) // wandering Z
			up.WriteByte(0)
			up.WriteByte(0)
			up.WriteByte(0)
			up.WriteByte(0)
			up.WriteByte(0)
			binary.Write(up, binary.LittleEndian, uint16(0))
			up.WriteByte(0)
			send(packet{typ: pktPlayerUpdate, body: up.Bytes(), steamID: *steamID, channel: 2})
			updates++
		}
	}
	fmt.Printf("[smoke] sent %d playerInput, %d playerUpdate over %d secs\n", inputs, updates, *secs)

	// Clean disconnect: send clientLeft.
	const pktClientLeft byte = 39
	send(packet{typ: pktClientLeft, steamID: *steamID})
	fmt.Println("[smoke] sent clientLeft, exiting")
}

func firstOr(b []byte) byte {
	if len(b) == 0 {
		return 0
	}
	return b[0]
}
