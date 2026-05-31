package router

import (
	"bytes"
	"encoding/binary"
)

// SELECT control protocol (client → router, router-only — never forwarded to a
// backend). A client announces which lobby its datagrams belong to.
//
// Wire (little-endian):
//
//	[8] magic   = selectMagic
//	[1] op      = opSelect | opLeave
//	[1] codeLen (0..maxCodeLen)
//	[N] code    (ASCII A-Z0-9, uppercased by client)
//	[4] nonce   (echoed in the ACK so the client can correlate)
//
// The ACK (router → client) is:
//
//	[8] magic
//	[1] op      = opAck
//	[1] status  (ackOK | ackNoSuchCode)
//	[4] nonce
//
// Disambiguation from game traffic: a Stick Fight datagram is
// [u32 ts][u8 msgType≤46][...]. selectMagic[4] = 'R' (0x52 = 82) is not a valid
// msgType, and we additionally compare the full 8-byte magic, so a control
// datagram can never be mistaken for game traffic (or vice-versa).
var selectMagic = []byte{'S', 'F', 'R', 'T', 'R', 0x00, 0x00, 0x01}

const (
	opSelect byte = 0x01
	opLeave  byte = 0x02
	opAck    byte = 0x81

	ackOK         byte = 0x00
	ackNoSuchCode byte = 0x01

	maxCodeLen = 16

	// minimum SELECT/LEAVE length: magic(8) + op(1) + codeLen(1) + nonce(4),
	// with a zero-length code.
	minControlLen = 8 + 1 + 1 + 4
)

// isControl reports whether data is a router control datagram (SELECT/LEAVE).
func isControl(data []byte) bool {
	return len(data) >= minControlLen && bytes.Equal(data[:8], selectMagic)
}

// parseControl extracts the op, lobby code, and nonce. ok is false on a
// malformed datagram. Caller should have checked isControl first.
func parseControl(data []byte) (op byte, code string, nonce uint32, ok bool) {
	if len(data) < minControlLen {
		return 0, "", 0, false
	}
	op = data[8]
	codeLen := int(data[9])
	if codeLen > maxCodeLen {
		return 0, "", 0, false
	}
	// layout: [0:8]magic [8]op [9]codeLen [10:10+codeLen]code [..+4]nonce
	if len(data) < 10+codeLen+4 {
		return 0, "", 0, false
	}
	code = string(data[10 : 10+codeLen])
	nonce = binary.LittleEndian.Uint32(data[10+codeLen : 10+codeLen+4])
	return op, code, nonce, true
}

// parseAck parses a SELECT-ACK (router → client). ACK layout differs from
// SELECT: [8]magic [1]op=opAck [1]status [4]nonce (no codeLen/code). This is
// what the client uses to confirm its lobby binding.
func parseAck(data []byte) (status byte, nonce uint32, ok bool) {
	if len(data) < 14 || !bytes.Equal(data[:8], selectMagic) || data[8] != opAck {
		return 0, 0, false
	}
	return data[9], binary.LittleEndian.Uint32(data[10:14]), true
}

// buildAck builds a SELECT-ACK datagram echoing nonce with the given status.
func buildAck(nonce uint32, status byte) []byte {
	out := make([]byte, 0, 14)
	out = append(out, selectMagic...)
	out = append(out, opAck, status)
	var n [4]byte
	binary.LittleEndian.PutUint32(n[:], nonce)
	out = append(out, n[:]...)
	return out
}
