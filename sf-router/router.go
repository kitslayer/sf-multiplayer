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
//   - return-path addressing with a trivial UDP echo, no client changes.
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
	"sort"
	"sync"
	"time"
)

// FlowIdleTimeout is how long a client→backend flow may sit with no traffic
// before the router tears it down. Must exceed the backend's own stale-client
// sweep (30s in SFHeadlessHost) so the router never drops a flow the backend
// still considers live.
const FlowIdleTimeout = 45 * time.Second

// BindIdleTimeout is how long a SELECT binding survives with no traffic before
// the reaper drops it. Longer than FlowIdleTimeout so a brief lull (between a
// flow being reaped and the client's next SELECT) doesn't lose the binding.
const BindIdleTimeout = 5 * time.Minute

// maxFlows / maxBindings bound memory against spoofed-source-address sprays.
// A handful of real clients use a few of each; these are generous ceilings.
const (
	maxFlows    = 4096
	maxBindings = 8192
)

// defaultMaxFlowsPerIP bounds one source IP's share of the flow table — generous
// enough for many players behind a single NAT, while stopping one source from
// exhausting maxFlows. Overridable via -max-flows-per-ip (0 = unlimited).
const defaultMaxFlowsPerIP = 64

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

// bound records which lobby a client endpoint/IP is pinned to via SELECT. We
// store the CODE (not a resolved address) and re-resolve through the registry
// on use, so a lobby that restarts on a different port — or a port reused by a
// different code — never leaves a client pinned to a stale backend.
type bound struct {
	code     string
	lastSeen time.Time
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

	// defaultCode, when non-empty (routing mode only), routes datagrams from a
	// client that has NO SELECT binding to this lobby code instead of dropping
	// them. This lets a client that never SELECTs still reach a lobby: an old
	// direct-connect client repointed at the router, and — importantly — the
	// patched-DLL game socket in the brief window before its co-located recon
	// socket's SELECT lands (the recon SELECT then pins the IP to the chosen
	// lobby, overriding this default). Empty = drop unselected traffic (the
	// original strict behavior).
	defaultCode string

	// lister, when set (routing mode), returns the current live lobby codes so a
	// LIST control datagram can answer the in-game browser over the same UDP port
	// — no HTTP/website needed. Set once before Run; read on the (cold) LIST path.
	lister func() []string

	// maxFlowsPerIP caps concurrent flows from one source IP (0 = unlimited).
	// Set once before Run; read under mu on the new-flow path.
	maxFlowsPerIP int

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
		pub:           pub,
		backend:       ba,
		flows:         make(map[string]*flow),
		epBind:        make(map[string]*bound),
		ipBind:        make(map[string]*bound),
		stop:          make(chan struct{}),
		maxFlowsPerIP: defaultMaxFlowsPerIP,
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
		maxFlowsPerIP: defaultMaxFlowsPerIP,
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
		backend, code, ok := r.effectiveBackend(cliAddr)
		if !ok {
			r.mu.Unlock()
			// Routing mode + no SELECT (or its lobby is gone) → drop; the
			// client resends after SELECT.
			return
		}
		if len(r.flows) >= maxFlows {
			r.mu.Unlock()
			log.Printf("[router] flow cap %d reached; dropping new flow from %s", maxFlows, key)
			return
		}
		if r.maxFlowsPerIP > 0 && r.countFlowsForIPLocked(cliAddr.IP) >= r.maxFlowsPerIP {
			r.mu.Unlock()
			log.Printf("[router] per-IP flow cap %d reached for %s; dropping new flow", r.maxFlowsPerIP, cliAddr.IP)
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

// effectiveBackend resolves the current backend (and lobby code) for a client
// endpoint. Caller holds r.mu. Stage 0: the single fixed backend (no code).
// Routing mode: find the binding (the endpoint's own SELECT first, then its
// IP-level fallback for the patched-DLL game socket that shares the IP but
// never SELECTs), then RE-RESOLVE the code through the registry every time so a
// restarted/moved lobby is never served from a stale cached address. Refreshes
// the binding's lastSeen so the reaper keeps live bindings.
func (r *Router) effectiveBackend(cliAddr *net.UDPAddr) (*net.UDPAddr, string, bool) {
	if !r.requireSelect {
		return r.backend, "", true
	}
	b := r.epBind[cliAddr.String()]
	if b == nil {
		b = r.ipBind[cliAddr.IP.String()]
	}
	if b == nil {
		// No SELECT binding. Fall back to the default lobby if one is configured
		// (re-resolved every time, so a restarted default lobby is never served
		// from a stale address); otherwise drop and wait for a SELECT.
		if r.defaultCode != "" {
			if backend, ok := r.resolve(r.defaultCode); ok {
				return backend, r.defaultCode, true
			}
		}
		return nil, "", false
	}
	backend, ok := r.resolve(b.code)
	if !ok {
		return nil, "", false // lobby gone — drop until the client re-SELECTs a live one
	}
	b.lastSeen = time.Now()
	return backend, b.code, true
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
	case opList:
		// Answer the in-game browser's lobby-list request over UDP (no HTTP).
		r.mu.Lock()
		lister := r.lister
		r.mu.Unlock()
		if lister == nil {
			return // stage-0 / no registry — nothing to list
		}
		codes := lister()
		sort.Strings(codes)
		st := r.Stats() // locks r.mu internally; we hold no lock here
		lobbies := make([]LobbyInfo, 0, len(codes))
		for _, c := range codes {
			players := (st.ByCode[c] + 1) / 2 // a player ≈ 2 flows (recon + game socket)
			if players > 255 {
				players = 255
			}
			lobbies = append(lobbies, LobbyInfo{Code: c, Players: byte(players), Capacity: 4})
		}
		if _, err := r.pub.WriteToUDP(buildListResp(nonce, lobbies), cliAddr); err != nil {
			log.Printf("[router] LIST reply to %s failed: %v", cliAddr, err)
		}
		return
	case opSelect:
		backend, found := r.resolve(code)
		if !found {
			r.sendAck(cliAddr, nonce, ackNoSuchCode)
			log.Printf("[router] SELECT %q from %s → no such lobby", code, cliAddr)
			return
		}
		r.mu.Lock()
		now := time.Now()
		// Cap bindings against spoofed-source sprays. Refresh-in-place of an
		// existing endpoint is always allowed; only brand-new endpoints are
		// capped.
		if _, exists := r.epBind[cliAddr.String()]; !exists && len(r.epBind) >= maxBindings {
			r.mu.Unlock()
			log.Printf("[router] binding cap %d reached; ignoring SELECT from %s", maxBindings, cliAddr)
			return
		}
		r.epBind[cliAddr.String()] = &bound{code: code, lastSeen: now}
		r.ipBind[cliAddr.IP.String()] = &bound{code: code, lastSeen: now}
		// Tear down only flows that are now STALE (their effective backend
		// changed) — covers a single client switching lobbies, while leaving a
		// co-located different-lobby player's per-endpoint-bound flows intact.
		r.teardownStaleLocked(cliAddr.IP)
		r.mu.Unlock()
		r.sendAck(cliAddr, nonce, ackOK)
		log.Printf("[router] SELECT %q from %s → %s", code, cliAddr, backend)
		return
	default:
		// Unknown op — ignore.
		return
	}
}

// unbindLocked removes an endpoint's bindings (and its IP fallback) and tears
// down stale flows for its IP. Caller holds r.mu.
func (r *Router) unbindLocked(cliAddr *net.UDPAddr) {
	delete(r.epBind, cliAddr.String())
	delete(r.ipBind, cliAddr.IP.String())
	// The endpoint's own flow no longer has a binding → effectiveBackend fails
	// → teardownStaleLocked closes it. A co-located player's epBind'd flows
	// still resolve and are kept.
	r.teardownStaleLocked(cliAddr.IP)
}

// teardownStaleLocked closes + removes flows on the given IP whose effective
// backend no longer matches (binding changed, or its lobby is gone), so they
// rebuild against the current binding. Flows still resolving to their current
// backend are kept — so a SELECT by one player doesn't disturb a co-located
// player whose endpoint is correctly bound. Caller holds r.mu.
func (r *Router) teardownStaleLocked(ip net.IP) {
	for key, fl := range r.flows {
		if !fl.clientAddr.IP.Equal(ip) {
			continue
		}
		backend, _, ok := r.effectiveBackend(fl.clientAddr)
		if !ok || backend.String() != fl.backend.String() {
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

// SetMaxFlowsPerIP sets the per-source-IP flow cap (0 = unlimited). Call before Run.
func (r *Router) SetMaxFlowsPerIP(n int) {
	r.mu.Lock()
	r.maxFlowsPerIP = n
	r.mu.Unlock()
}

// SetDefaultCode sets the lobby code that clients with no SELECT binding are
// routed to (routing mode only; "" = drop unselected traffic). Call before Run.
func (r *Router) SetDefaultCode(code string) {
	r.mu.Lock()
	r.defaultCode = code
	r.mu.Unlock()
}

// SetLister sets the function that returns live lobby codes for the LIST control
// op (typically Registry.Codes). Call before Run.
func (r *Router) SetLister(fn func() []string) {
	r.mu.Lock()
	r.lister = fn
	r.mu.Unlock()
}

// countFlowsForIPLocked counts active flows whose client shares ip. Caller holds
// r.mu. O(flows), but only runs on new-flow creation (rare) and only after the
// O(1) global cap check has passed.
func (r *Router) countFlowsForIPLocked(ip net.IP) int {
	n := 0
	for _, fl := range r.flows {
		if fl.clientAddr.IP.Equal(ip) {
			n++
		}
	}
	return n
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

// reaper periodically (1) closes idle flows, (2) tears down flows whose backend
// has moved/disappeared (lobby restart/port-reuse) so they re-resolve, and (3)
// drops idle SELECT bindings so they don't accumulate.
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
			// (1) + (2): idle and stale flows.
			for key, fl := range r.flows {
				if now.Sub(fl.lastSeen) > FlowIdleTimeout {
					_ = fl.upSock.Close()
					delete(r.flows, key)
					log.Printf("[router] reaped idle flow %s (cli=%d srv=%d)", key, fl.rxFromCli, fl.rxFromSrv)
					continue
				}
				if r.requireSelect {
					backend, _, ok := r.effectiveBackend(fl.clientAddr)
					if !ok || backend.String() != fl.backend.String() {
						_ = fl.upSock.Close()
						delete(r.flows, key)
						log.Printf("[router] reaped stale flow %s (backend moved/gone)", key)
					}
				}
			}
			// (3): idle bindings.
			for key, b := range r.epBind {
				if now.Sub(b.lastSeen) > BindIdleTimeout {
					delete(r.epBind, key)
				}
			}
			for key, b := range r.ipBind {
				if now.Sub(b.lastSeen) > BindIdleTimeout {
					delete(r.ipBind, key)
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
