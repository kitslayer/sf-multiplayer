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
// Two modes:
//   - Stage 0 (New): transparent relay to one fixed backend — proves the relay
//     + return-path addressing with a trivial UDP echo, no client changes.
//   - Stage 1 (NewRouting): SELECT-gated per-lobby routing. A client sends a
//     SELECT control datagram naming its lobby code; the router resolves it to
//     a backend (via the lobby registry) and pins the client. Game traffic
//     from a client that hasn't SELECTed is dropped. Bindings are per-endpoint
//     with a per-IP fallback so the patched-DLL game socket (same IP, never
//     SELECTs) rides the recon socket's selection.
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
	code       string       // lobby code (routing mode; "" in stage-0)
	upSock     *net.UDPConn // dialed socket toward the backend
	lastSeen   time.Time    // refreshed on any traffic either direction
	rxFromCli  uint64       // datagrams client→backend
	rxFromSrv  uint64       // datagrams backend→client
}

// bound records which lobby (and resolved backend) a client endpoint/IP is
// pinned to via SELECT.
type bound struct {
	code    string
	backend *net.UDPAddr
}

// Router relays UDP between clients on a single public port and per-lobby
// backends. Safe for concurrent use: the public socket is read by Run's loop
// and written by every flow's upstream goroutine (UDPConn allows concurrent
// WriteToUDP).
type Router struct {
	pub     *net.UDPConn // public listener (clients send here)
	backend *net.UDPAddr // Stage 0 fixed-backend mode (nil in routing mode)

	// Routing mode (Stage 1): resolve a lobby code → backend, and require a
	// SELECT before forwarding a client's game traffic.
	resolve       func(code string) (*net.UDPAddr, bool)
	requireSelect bool

	mu     sync.Mutex
	flows  map[string]*flow  // by clientAddr.String() (per source endpoint)
	epBind map[string]*bound // by client endpoint string (the endpoint that SELECTed)
	ipBind map[string]*bound // by client IP (fallback for the game socket, which never SELECTs)

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
		epBind:  make(map[string]*bound),
		ipBind:  make(map[string]*bound),
		stop:    make(chan struct{}),
	}, nil
}

// NewRouting binds the public listener in routing mode: clients must SELECT a
// lobby code, which resolve maps to a backend. Game traffic from a client that
// hasn't selected is dropped.
func NewRouting(listenAddr string, resolve func(code string) (*net.UDPAddr, bool)) (*Router, error) {
	la, err := net.ResolveUDPAddr("udp", listenAddr)
	if err != nil {
		return nil, fmt.Errorf("resolve listen %q: %w", listenAddr, err)
	}
	pub, err := net.ListenUDP("udp", la)
	if err != nil {
		return nil, fmt.Errorf("listen %q: %w", listenAddr, err)
	}
	return &Router{
		pub:           pub,
		resolve:       resolve,
		requireSelect: true,
		flows:         make(map[string]*flow),
		epBind:        make(map[string]*bound),
		ipBind:        make(map[string]*bound),
		stop:          make(chan struct{}),
	}, nil
}

// Run services the public socket until Close. Blocks.
func (r *Router) Run() error {
	mode := "stage-0 fixed → " + r.backend.String()
	if r.requireSelect {
		mode = "routing (SELECT-gated)"
	}
	log.Printf("[router] listening on %s [%s]", r.pub.LocalAddr(), mode)
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
// on first contact. In routing mode, a SELECT/LEAVE control datagram is handled
// here and never forwarded.
func (r *Router) handleClientDatagram(cliAddr *net.UDPAddr, data []byte) {
	if r.requireSelect && isControl(data) {
		r.handleControl(cliAddr, data)
		return
	}

	key := cliAddr.String()

	r.mu.Lock()
	fl := r.flows[key]
	if fl == nil {
		backend, code, ok := r.backendFor(cliAddr)
		if !ok {
			r.mu.Unlock()
			// Routing mode + no SELECT yet → drop (client resends after SELECT).
			return
		}
		nf, err := r.newFlow(cliAddr, backend, code)
		if err != nil {
			r.mu.Unlock()
			log.Printf("[router] dial backend %s for %s failed: %v", backend, key, err)
			return
		}
		fl = nf
		r.flows[key] = fl
		log.Printf("[router] new flow %s → %s (%s)", key, backend, code)
	}
	fl.lastSeen = time.Now()
	fl.rxFromCli++
	r.mu.Unlock()

	if _, err := fl.upSock.Write(data); err != nil {
		log.Printf("[router] forward to backend %s failed: %v", fl.backend, err)
	}
}

// backendFor selects the backend (and lobby code) for a client endpoint. Caller
// holds r.mu. Stage 0: the single fixed backend (no code). Routing mode: the
// endpoint's own SELECT binding first, then its IP-level fallback (covers the
// patched-DLL game socket that shares the client's IP but never SELECTs).
func (r *Router) backendFor(cliAddr *net.UDPAddr) (*net.UDPAddr, string, bool) {
	if !r.requireSelect {
		return r.backend, "", true
	}
	if b := r.epBind[cliAddr.String()]; b != nil {
		return b.backend, b.code, true
	}
	if b := r.ipBind[cliAddr.IP.String()]; b != nil {
		return b.backend, b.code, true
	}
	return nil, "", false
}

// handleControl processes a SELECT/LEAVE datagram and replies with an ACK.
func (r *Router) handleControl(cliAddr *net.UDPAddr, data []byte) {
	op, code, nonce, ok := parseControl(data)
	if !ok {
		return
	}
	switch op {
	case opLeave:
		r.mu.Lock()
		r.unbindLocked(cliAddr)
		r.mu.Unlock()
		r.sendAck(cliAddr, nonce, ackOK)
		log.Printf("[router] LEAVE from %s", cliAddr)
		return
	case opSelect:
		backend, found := r.resolve(code)
		if !found {
			r.sendAck(cliAddr, nonce, ackNoSuchCode)
			log.Printf("[router] SELECT %q from %s → no such lobby", code, cliAddr)
			return
		}
		r.mu.Lock()
		ip := cliAddr.IP.String()
		prev := r.ipBind[ip]
		changed := prev == nil || prev.code != code
		r.epBind[cliAddr.String()] = &bound{code: code, backend: backend}
		r.ipBind[ip] = &bound{code: code, backend: backend}
		if changed {
			// A switch (or first select): tear down this IP's existing flows so
			// they rebuild to the new backend on the next datagram. (Same-IP
			// two-player NAT is the documented edge case in the plan.)
			r.teardownByIPLocked(cliAddr.IP)
		}
		r.mu.Unlock()
		r.sendAck(cliAddr, nonce, ackOK)
		log.Printf("[router] SELECT %q from %s → %s (changed=%v)", code, cliAddr, backend, changed)
		return
	default:
		// Unknown op — ignore.
		return
	}
}

// unbindLocked removes an endpoint's bindings (and its IP fallback) and tears
// down its flows. Caller holds r.mu.
func (r *Router) unbindLocked(cliAddr *net.UDPAddr) {
	delete(r.epBind, cliAddr.String())
	delete(r.ipBind, cliAddr.IP.String())
	r.teardownByIPLocked(cliAddr.IP)
}

// teardownByIPLocked closes + removes all flows whose client shares the given
// IP, so subsequent datagrams rebuild against the current binding. Caller holds
// r.mu.
func (r *Router) teardownByIPLocked(ip net.IP) {
	for key, fl := range r.flows {
		if fl.clientAddr.IP.Equal(ip) {
			_ = fl.upSock.Close()
			delete(r.flows, key)
		}
	}
}

// sendAck writes a SELECT-ACK back to the client via the public socket.
func (r *Router) sendAck(cliAddr *net.UDPAddr, nonce uint32, status byte) {
	if _, err := r.pub.WriteToUDP(buildAck(nonce, status), cliAddr); err != nil {
		log.Printf("[router] ack to %s failed: %v", cliAddr, err)
	}
}

// newFlow dials a per-client socket toward the backend and starts its upstream
// pump. Caller holds r.mu.
func (r *Router) newFlow(cliAddr, backend *net.UDPAddr, code string) (*flow, error) {
	up, err := net.DialUDP("udp", nil, backend)
	if err != nil {
		return nil, err
	}
	fl := &flow{
		clientAddr: cliAddr,
		backend:    backend,
		code:       code,
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
// the lifecycle reaper to find empty lobbies). ByCode counts flows per lobby
// code; a registry lobby absent from ByCode (or with 0) has no live clients.
type Stats struct {
	Flows  int            `json:"flows"`
	ByCode map[string]int `json:"byCode"`
}

// Stats returns current flow counts, total and per lobby code.
func (r *Router) Stats() Stats {
	r.mu.Lock()
	defer r.mu.Unlock()
	byCode := make(map[string]int)
	for _, fl := range r.flows {
		if fl.code != "" {
			byCode[fl.code]++
		}
	}
	return Stats{Flows: len(r.flows), ByCode: byCode}
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
