// Package router implements a single-port UDP front-door for the
// sf-multiplayer dedicated server.
//
// Stick Fight clients (both the patched-DLL game socket speaking the raw v25
// protocol AND the SFClientRecon socket speaking v26) send plain UDP datagrams
// to one public port. The router forwards every datagram from a given client
// endpoint to the backend headless SF.exe that hosts that client's lobby, and
// relays the backend's replies back out the public socket so the client sees
// them coming from the router (its only known peer). Because the wire is
// stateless raw UDP on both protocols, the router needs no connection state to
// preserve — it is a per-client-endpoint NAT/relay.
//
// Stage 0 (this file): transparent relay to a single fixed backend, so the
// relay + return-path addressing can be proven with a trivial UDP echo before
// any client changes. Stage 1 adds SELECT-based per-lobby backend choice; the
// seam is Router.pickBackend, currently a constant.
package router

import (
	"fmt"
	"log"
	"net"
	"sync"
	"time"
)

// FlowIdleTimeout is how long a client→backend flow may sit with no traffic
// before the router tears it down. Must exceed the backend's own stale-client
// sweep (30s in SFHeadlessHost) so the router never drops a flow the backend
// still considers live.
const FlowIdleTimeout = 45 * time.Second

// reapInterval is how often the janitor scans for idle flows.
const reapInterval = 5 * time.Second

// upstreamBufBytes bounds a single datagram read from a backend. SF datagrams
// are tiny (tens to low-hundreds of bytes); 2KB is comfortably generous.
const upstreamBufBytes = 2048

// flow is one client endpoint's relay to a backend. The upstream goroutine
// reads backend→client and writes out the shared public socket; the main loop
// writes client→backend on this flow's dialed socket.
type flow struct {
	clientAddr *net.UDPAddr // the real client (public side)
	backend    *net.UDPAddr // the backend SF.exe we forward to
	upSock     *net.UDPConn // dialed socket toward the backend
	lastSeen   time.Time    // refreshed on any traffic either direction
	rxFromCli  uint64       // datagrams client→backend
	rxFromSrv  uint64       // datagrams backend→client
}

// Router relays UDP between clients on a single public port and per-lobby
// backends. Safe for concurrent use: the public socket is read by Run's loop
// and written by every flow's upstream goroutine (UDPConn allows concurrent
// WriteToUDP).
type Router struct {
	pub     *net.UDPConn // public listener (clients send here)
	backend *net.UDPAddr // Stage 0: the single fixed backend

	mu    sync.Mutex
	flows map[string]*flow // keyed by clientAddr.String()

	stop chan struct{}
	wg   sync.WaitGroup
}

// New binds the public listener and prepares a router whose every client is
// relayed to backendAddr (Stage 0 fixed-backend mode).
func New(listenAddr, backendAddr string) (*Router, error) {
	la, err := net.ResolveUDPAddr("udp", listenAddr)
	if err != nil {
		return nil, fmt.Errorf("resolve listen %q: %w", listenAddr, err)
	}
	ba, err := net.ResolveUDPAddr("udp", backendAddr)
	if err != nil {
		return nil, fmt.Errorf("resolve backend %q: %w", backendAddr, err)
	}
	pub, err := net.ListenUDP("udp", la)
	if err != nil {
		return nil, fmt.Errorf("listen %q: %w", listenAddr, err)
	}
	return &Router{
		pub:     pub,
		backend: ba,
		flows:   make(map[string]*flow),
		stop:    make(chan struct{}),
	}, nil
}

// pickBackend chooses the backend for a client's first datagram. Stage 0
// returns the single fixed backend. Stage 1 replaces this with SELECT-based
// lookup against the lobby registry (and returns ok=false to drop traffic from
// a client that has not selected a lobby yet).
func (r *Router) pickBackend(clientAddr *net.UDPAddr, firstPacket []byte) (*net.UDPAddr, bool) {
	return r.backend, true
}

// Run services the public socket until Close. Blocks.
func (r *Router) Run() error {
	log.Printf("[router] listening on %s → backend %s (stage-0 fixed)", r.pub.LocalAddr(), r.backend)
	r.wg.Add(1)
	go r.reaper()

	buf := make([]byte, upstreamBufBytes)
	for {
		n, cliAddr, err := r.pub.ReadFromUDP(buf)
		if err != nil {
			select {
			case <-r.stop:
				return nil
			default:
				// A transient read error shouldn't kill the relay.
				log.Printf("[router] public read error: %v", err)
				continue
			}
		}
		r.handleClientDatagram(cliAddr, buf[:n])
	}
}

// handleClientDatagram forwards one client→backend datagram, creating the flow
// on first contact.
func (r *Router) handleClientDatagram(cliAddr *net.UDPAddr, data []byte) {
	key := cliAddr.String()

	r.mu.Lock()
	fl := r.flows[key]
	if fl == nil {
		backend, ok := r.pickBackend(cliAddr, data)
		if !ok {
			r.mu.Unlock()
			return // not selected a lobby yet → drop (Stage 1)
		}
		nf, err := r.newFlow(cliAddr, backend)
		if err != nil {
			r.mu.Unlock()
			log.Printf("[router] dial backend %s for %s failed: %v", backend, key, err)
			return
		}
		fl = nf
		r.flows[key] = fl
		log.Printf("[router] new flow %s → %s", key, backend)
	}
	fl.lastSeen = time.Now()
	fl.rxFromCli++
	r.mu.Unlock()

	if _, err := fl.upSock.Write(data); err != nil {
		log.Printf("[router] forward to backend %s failed: %v", fl.backend, err)
	}
}

// newFlow dials a per-client socket toward the backend and starts its upstream
// pump. Caller holds r.mu.
func (r *Router) newFlow(cliAddr, backend *net.UDPAddr) (*flow, error) {
	up, err := net.DialUDP("udp", nil, backend)
	if err != nil {
		return nil, err
	}
	fl := &flow{
		clientAddr: cliAddr,
		backend:    backend,
		upSock:     up,
		lastSeen:   time.Now(),
	}
	r.wg.Add(1)
	go r.pumpUpstream(fl)
	return fl, nil
}

// pumpUpstream reads backend→client datagrams off this flow's dialed socket and
// writes them back out the shared public socket addressed to the real client.
func (r *Router) pumpUpstream(fl *flow) {
	defer r.wg.Done()
	buf := make([]byte, upstreamBufBytes)
	for {
		n, err := fl.upSock.Read(buf)
		if err != nil {
			return // socket closed by reaper/Close, or backend gone
		}
		r.mu.Lock()
		fl.lastSeen = time.Now()
		fl.rxFromSrv++
		r.mu.Unlock()
		if _, werr := r.pub.WriteToUDP(buf[:n], fl.clientAddr); werr != nil {
			log.Printf("[router] reply to client %s failed: %v", fl.clientAddr, werr)
		}
	}
}

// reaper periodically closes flows idle beyond FlowIdleTimeout.
func (r *Router) reaper() {
	defer r.wg.Done()
	t := time.NewTicker(reapInterval)
	defer t.Stop()
	for {
		select {
		case <-r.stop:
			return
		case <-t.C:
			now := time.Now()
			r.mu.Lock()
			for key, fl := range r.flows {
				if now.Sub(fl.lastSeen) > FlowIdleTimeout {
					_ = fl.upSock.Close()
					delete(r.flows, key)
					log.Printf("[router] reaped idle flow %s (cli=%d srv=%d)", key, fl.rxFromCli, fl.rxFromSrv)
				}
			}
			r.mu.Unlock()
		}
	}
}

// Stats is a point-in-time view for the HTTP /router/stats endpoint (used by
// the lifecycle reaper to find empty lobbies).
type Stats struct {
	Flows int `json:"flows"`
}

// Stats returns current flow counts.
func (r *Router) Stats() Stats {
	r.mu.Lock()
	defer r.mu.Unlock()
	return Stats{Flows: len(r.flows)}
}

// LocalAddr is the public listener's bound address (useful when listening on
// :0 in tests).
func (r *Router) LocalAddr() net.Addr { return r.pub.LocalAddr() }

// Close stops the router and tears down all flows.
func (r *Router) Close() {
	close(r.stop)
	_ = r.pub.Close()
	r.mu.Lock()
	for key, fl := range r.flows {
		_ = fl.upSock.Close()
		delete(r.flows, key)
	}
	r.mu.Unlock()
	r.wg.Wait()
}
