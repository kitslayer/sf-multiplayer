package router

import (
	"net"
	"testing"
	"time"
)

// newRoutingTest spins up a routing-mode router with an in-memory code→backend
// resolver, plus a client socket. Returns the client, the router's public addr,
// and cleanup.
func newRoutingTest(t *testing.T, resolve func(string) (*net.UDPAddr, bool)) (*net.UDPConn, *net.UDPAddr, *Router) {
	t.Helper()
	r, err := NewRouting("127.0.0.1:0", resolve)
	if err != nil {
		t.Fatalf("NewRouting: %v", err)
	}
	go func() { _ = r.Run() }()
	time.Sleep(50 * time.Millisecond)
	routerAddr := r.LocalAddr().(*net.UDPAddr)
	cli, err := net.DialUDP("udp", nil, routerAddr)
	if err != nil {
		t.Fatalf("client dial: %v", err)
	}
	return cli, routerAddr, r
}

// selectAndWaitAck sends a SELECT and reads the ACK (with a deadline), returning
// the ACK status byte.
func selectAndWaitAck(t *testing.T, cli *net.UDPConn, code string, nonce uint32) byte {
	t.Helper()
	if _, err := cli.Write(buildSelect(opSelect, code, nonce)); err != nil {
		t.Fatalf("write SELECT: %v", err)
	}
	_ = cli.SetReadDeadline(time.Now().Add(2 * time.Second))
	buf := make([]byte, 64)
	n, err := cli.Read(buf)
	if err != nil {
		t.Fatalf("read ACK: %v", err)
	}
	status, gotNonce, ok := parseAck(buf[:n])
	if !ok || gotNonce != nonce {
		t.Fatalf("bad ACK: status=%#x nonce=%#x ok=%v", status, gotNonce, ok)
	}
	return status
}

func TestSelectRoutesToCorrectBackend(t *testing.T) {
	echoA, stopA := startEcho(t, "A")
	defer stopA()
	echoB, stopB := startEcho(t, "B")
	defer stopB()
	resolve := func(code string) (*net.UDPAddr, bool) {
		switch code {
		case "AAAA":
			return echoA, true
		case "BBBB":
			return echoB, true
		}
		return nil, false
	}

	// Client 1 → AAAA
	cli1, _, r1 := newRoutingTest(t, resolve)
	defer r1.Close()
	defer cli1.Close()
	if st := selectAndWaitAck(t, cli1, "AAAA", 1); st != ackOK {
		t.Fatalf("SELECT AAAA status = %#x, want ok", st)
	}
	if got := sendRecv(t, cli1, "ping"); got != "A:ping" {
		t.Errorf("client1 got %q, want A:ping", got)
	}

	// Client 2 → BBBB on the SAME router
	cli2, err := net.DialUDP("udp", nil, r1.LocalAddr().(*net.UDPAddr))
	if err != nil {
		t.Fatalf("cli2 dial: %v", err)
	}
	defer cli2.Close()
	if st := selectAndWaitAck(t, cli2, "BBBB", 2); st != ackOK {
		t.Fatalf("SELECT BBBB status = %#x, want ok", st)
	}
	if got := sendRecv(t, cli2, "ping"); got != "B:ping" {
		t.Errorf("client2 got %q, want B:ping", got)
	}
}

func TestUnselectedTrafficDropped(t *testing.T) {
	echoA, stopA := startEcho(t, "A")
	defer stopA()
	resolve := func(code string) (*net.UDPAddr, bool) {
		if code == "AAAA" {
			return echoA, true
		}
		return nil, false
	}
	cli, _, r := newRoutingTest(t, resolve)
	defer r.Close()
	defer cli.Close()

	// No SELECT → game traffic must be dropped (no echo back).
	if _, err := cli.Write([]byte("ping")); err != nil {
		t.Fatalf("write: %v", err)
	}
	_ = cli.SetReadDeadline(time.Now().Add(300 * time.Millisecond))
	if n, err := cli.Read(make([]byte, 64)); err == nil {
		t.Errorf("got %d bytes back for unselected traffic; want drop (timeout)", n)
	}
	if got := r.Stats().Flows; got != 0 {
		t.Errorf("flows = %d after unselected traffic, want 0", got)
	}
}

func TestUnknownCodeAcksError(t *testing.T) {
	cli, _, r := newRoutingTest(t, func(string) (*net.UDPAddr, bool) { return nil, false })
	defer r.Close()
	defer cli.Close()
	if st := selectAndWaitAck(t, cli, "ZZZZ", 9); st != ackNoSuchCode {
		t.Errorf("SELECT unknown status = %#x, want ackNoSuchCode", st)
	}
}

func TestSwitchRebindsToNewBackend(t *testing.T) {
	echoA, stopA := startEcho(t, "A")
	defer stopA()
	echoB, stopB := startEcho(t, "B")
	defer stopB()
	resolve := func(code string) (*net.UDPAddr, bool) {
		switch code {
		case "AAAA":
			return echoA, true
		case "BBBB":
			return echoB, true
		}
		return nil, false
	}
	cli, _, r := newRoutingTest(t, resolve)
	defer r.Close()
	defer cli.Close()

	selectAndWaitAck(t, cli, "AAAA", 1)
	if got := sendRecv(t, cli, "x"); got != "A:x" {
		t.Fatalf("pre-switch got %q, want A:x", got)
	}
	// Switch to BBBB — old flow must be torn down and rebuilt to B.
	selectAndWaitAck(t, cli, "BBBB", 2)
	if got := sendRecv(t, cli, "y"); got != "B:y" {
		t.Errorf("post-switch got %q, want B:y", got)
	}
}

func TestStatsByCode(t *testing.T) {
	echoA, stopA := startEcho(t, "A")
	defer stopA()
	resolve := func(code string) (*net.UDPAddr, bool) {
		if code == "AAAA" {
			return echoA, true
		}
		return nil, false
	}
	cli, _, r := newRoutingTest(t, resolve)
	defer r.Close()
	defer cli.Close()
	selectAndWaitAck(t, cli, "AAAA", 1)
	sendRecv(t, cli, "x")
	time.Sleep(30 * time.Millisecond)
	st := r.Stats()
	if st.ByCode["AAAA"] != 1 {
		t.Errorf("ByCode[AAAA] = %d, want 1 (stats=%+v)", st.ByCode["AAAA"], st)
	}
}

// sendRecv writes payload and returns the string reply (2s deadline).
func sendRecv(t *testing.T, cli *net.UDPConn, payload string) string {
	t.Helper()
	if _, err := cli.Write([]byte(payload)); err != nil {
		t.Fatalf("write: %v", err)
	}
	_ = cli.SetReadDeadline(time.Now().Add(2 * time.Second))
	buf := make([]byte, 2048)
	n, err := cli.Read(buf)
	if err != nil {
		t.Fatalf("read: %v", err)
	}
	return string(buf[:n])
}
