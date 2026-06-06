package router

import (
	"net"
	"testing"
	"time"
)

// startEcho launches a UDP echo server that replies "<prefix>:<payload>" to the
// sender. Returns its address and a stop func.
func startEcho(t *testing.T, prefix string) (*net.UDPAddr, func()) {
	t.Helper()
	conn, err := net.ListenUDP("udp", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Fatalf("echo listen: %v", err)
	}
	done := make(chan struct{})
	go func() {
		buf := make([]byte, 2048)
		for {
			n, from, err := conn.ReadFromUDP(buf)
			if err != nil {
				select {
				case <-done:
					return
				default:
					return
				}
			}
			reply := append([]byte(prefix+":"), buf[:n]...)
			_, _ = conn.WriteToUDP(reply, from)
		}
	}()
	return conn.LocalAddr().(*net.UDPAddr), func() { close(done); _ = conn.Close() }
}

// TestRelayRoundTrip proves a client datagram reaches the backend through the
// router and the reply comes BACK from the router's public address (not the
// backend's) — the core NAT/return-path behavior Stick Fight clients rely on.
func TestRelayRoundTrip(t *testing.T) {
	echoAddr, stopEcho := startEcho(t, "A")
	defer stopEcho()

	r, err := New("127.0.0.1:0", echoAddr.String())
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	go func() { _ = r.Run() }()
	defer r.Close()
	time.Sleep(50 * time.Millisecond) // let Run start

	routerAddr := r.LocalAddr().(*net.UDPAddr)

	cli, err := net.DialUDP("udp", nil, routerAddr)
	if err != nil {
		t.Fatalf("client dial: %v", err)
	}
	defer cli.Close()

	if _, err := cli.Write([]byte("hello")); err != nil {
		t.Fatalf("client write: %v", err)
	}

	_ = cli.SetReadDeadline(time.Now().Add(2 * time.Second))
	buf := make([]byte, 2048)
	n, from, err := cli.ReadFromUDP(buf)
	if err != nil {
		t.Fatalf("client read (relay round-trip failed): %v", err)
	}
	if got, want := string(buf[:n]), "A:hello"; got != want {
		t.Errorf("payload = %q, want %q", got, want)
	}
	// The reply MUST appear to come from the router, not the backend — else the
	// client (which only knows the router) would discard it.
	if from.Port != routerAddr.Port {
		t.Errorf("reply source port = %d, want router port %d (backend was %d)",
			from.Port, routerAddr.Port, echoAddr.Port)
	}
}

// TestPerClientFlows verifies two distinct client endpoints get two independent
// flows (one per client), which is how the router will keep two players routed
// to their respective lobbies.
func TestPerClientFlows(t *testing.T) {
	echoAddr, stopEcho := startEcho(t, "B")
	defer stopEcho()

	r, err := New("127.0.0.1:0", echoAddr.String())
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	go func() { _ = r.Run() }()
	defer r.Close()
	time.Sleep(50 * time.Millisecond)

	routerAddr := r.LocalAddr().(*net.UDPAddr)

	for i := 0; i < 2; i++ {
		cli, err := net.DialUDP("udp", nil, routerAddr)
		if err != nil {
			t.Fatalf("client %d dial: %v", i, err)
		}
		defer cli.Close()
		if _, err := cli.Write([]byte("x")); err != nil {
			t.Fatalf("client %d write: %v", i, err)
		}
		_ = cli.SetReadDeadline(time.Now().Add(2 * time.Second))
		if _, err := cli.Read(make([]byte, 64)); err != nil {
			t.Fatalf("client %d read: %v", i, err)
		}
	}

	time.Sleep(50 * time.Millisecond)
	if got := r.Stats().Flows; got != 2 {
		t.Errorf("flows = %d, want 2 (one per client endpoint)", got)
	}
}

// TestPerIPFlowCap verifies the DoS backstop: with a per-IP flow cap of 2, a
// third distinct endpoint from the SAME source IP is dropped rather than
// allocating another flow (so one source can't exhaust the global flow table).
func TestPerIPFlowCap(t *testing.T) {
	echoAddr, stopEcho := startEcho(t, "C")
	defer stopEcho()

	r, err := New("127.0.0.1:0", echoAddr.String())
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	r.SetMaxFlowsPerIP(2)
	go func() { _ = r.Run() }()
	defer r.Close()
	time.Sleep(50 * time.Millisecond)
	routerAddr := r.LocalAddr().(*net.UDPAddr)

	// Three endpoints, all from 127.0.0.1 (distinct source ports). Only the first
	// two should establish a flow; the third trips the per-IP cap and is dropped.
	for i := 0; i < 3; i++ {
		cli, err := net.DialUDP("udp", nil, routerAddr)
		if err != nil {
			t.Fatalf("client %d dial: %v", i, err)
		}
		defer cli.Close()
		if _, err := cli.Write([]byte("x")); err != nil {
			t.Fatalf("client %d write: %v", i, err)
		}
		_ = cli.SetReadDeadline(time.Now().Add(200 * time.Millisecond))
		_, _ = cli.Read(make([]byte, 64)) // drain reply; 3rd will time out (dropped)
	}

	time.Sleep(50 * time.Millisecond)
	if got := r.Stats().Flows; got != 2 {
		t.Errorf("flows = %d, want 2 (per-IP cap of 2)", got)
	}
}
