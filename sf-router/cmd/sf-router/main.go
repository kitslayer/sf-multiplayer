// Command sf-router is the single-port UDP front-door for sf-multiplayer.
//
// Routing mode (normal operation) — route by lobby code via the launch-lobby.sh
// registry, with an HTTP stats endpoint for the lifecycle reaper:
//
//	sf-router -listen 0.0.0.0:1337 -registry /tmp/sf-lobbies -stats 127.0.0.1:8081
//
// Stage-0 transparent mode (debugging) — relay everything to one fixed backend:
//
//	sf-router -listen 0.0.0.0:1337 -backend 127.0.0.1:1338
//
// In both modes a client only ever talks to :1337; the router relays replies
// back from that port. In routing mode a client must first send a SELECT
// control datagram naming its lobby code.
package main

import (
	"encoding/json"
	"flag"
	"log"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	router "github.com/kitslayer/sf-multiplayer/sf-router"
)

func main() {
	listen := flag.String("listen", "0.0.0.0:1337", "public UDP address clients connect to")
	registry := flag.String("registry", "", "lobby registry dir (e.g. /tmp/sf-lobbies); enables routing mode")
	backend := flag.String("backend", "", "stage-0 fixed backend (e.g. 127.0.0.1:1338); used only when -registry is empty")
	stats := flag.String("stats", "", "optional HTTP addr for GET /router/stats (e.g. 127.0.0.1:8081)")
	regTTL := flag.Duration("registry-ttl", 2*time.Second, "lobby registry cache TTL")
	maxPerIP := flag.Int("max-flows-per-ip", 64, "max concurrent flows from one source IP (0 = unlimited)")
	flag.Parse()

	log.SetFlags(log.LstdFlags | log.Lmsgprefix)
	log.SetPrefix("")

	var (
		r       *router.Router
		err     error
		regStop chan struct{}
	)
	switch {
	case *registry != "":
		reg := router.NewRegistry(*registry, *regTTL)
		regStop = make(chan struct{})
		reg.StartRefresh(regStop) // keep the cache warm off the relay hot path
		r, err = router.NewRouting(*listen, reg.Lookup)
		if err == nil {
			log.Printf("[router] routing mode: registry=%s ttl=%s", *registry, *regTTL)
		}
	case *backend != "":
		r, err = router.New(*listen, *backend)
	default:
		log.Fatalf("[router] need -registry DIR (routing mode) or -backend HOST:PORT (stage-0)")
	}
	if err != nil {
		log.Fatalf("[router] startup failed: %v", err)
	}
	r.SetMaxFlowsPerIP(*maxPerIP)

	if *stats != "" {
		go serveStats(*stats, r)
	}

	// Clean shutdown on SIGINT/SIGTERM so flows + sockets close tidily.
	sig := make(chan os.Signal, 1)
	signal.Notify(sig, syscall.SIGINT, syscall.SIGTERM)
	go func() {
		<-sig
		log.Printf("[router] shutting down")
		if regStop != nil {
			close(regStop)
		}
		r.Close()
	}()

	if err := r.Run(); err != nil {
		log.Fatalf("[router] run error: %v", err)
	}
}

// serveStats exposes GET /router/stats for the serve-lobbies.py reaper to find
// empty lobbies (and for monitoring).
func serveStats(addr string, r *router.Router) {
	mux := http.NewServeMux()
	mux.HandleFunc("/router/stats", func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_ = json.NewEncoder(w).Encode(r.Stats())
	})
	log.Printf("[router] stats HTTP on %s/router/stats", addr)
	if err := http.ListenAndServe(addr, mux); err != nil {
		log.Printf("[router] stats server error: %v", err)
	}
}
