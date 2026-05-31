// Command sf-router is the single-port UDP front-door for sf-multiplayer.
//
// Stage 0: transparent relay to one fixed backend.
//
//	sf-router -listen 0.0.0.0:1337 -backend 127.0.0.1:1338
//
// Every client datagram on :1337 is forwarded to the backend, and replies are
// relayed back so the client only ever talks to :1337. Stage 1 will add
// SELECT-based per-lobby routing (registry-driven).
package main

import (
	"flag"
	"log"
	"os"
	"os/signal"
	"syscall"

	"github.com/StickFightDev/StickFightDedicatedSrv/router"
)

func main() {
	listen := flag.String("listen", "0.0.0.0:1337", "public UDP address clients connect to")
	backend := flag.String("backend", "127.0.0.1:1338", "backend SF.exe UDP address (stage-0 fixed)")
	flag.Parse()

	log.SetFlags(log.LstdFlags | log.Lmsgprefix)
	log.SetPrefix("")

	r, err := router.New(*listen, *backend)
	if err != nil {
		log.Fatalf("[router] startup failed: %v", err)
	}

	// Clean shutdown on SIGINT/SIGTERM so flows + sockets close tidily.
	sig := make(chan os.Signal, 1)
	signal.Notify(sig, syscall.SIGINT, syscall.SIGTERM)
	go func() {
		<-sig
		log.Printf("[router] shutting down")
		r.Close()
	}()

	if err := r.Run(); err != nil {
		log.Fatalf("[router] run error: %v", err)
	}
}
