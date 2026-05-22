// oracle-test spawns one headless SF oracle, pings it, asks for a snapshot,
// loads a different map, then tears it down. Quick smoke test for the
// Go ↔ oracle bridge end-to-end.
//
// Usage:
//   go run ./cmd/oracle-test            # default ports + scene
//   go run ./cmd/oracle-test -secs 30   # run a 30-second snapshot stream then quit
package main

import (
	"flag"
	"fmt"
	"log"
	"os"
	"time"

	"github.com/StickFightDev/StickFightDedicatedSrv/oracle"
)

func main() {
	gamePort := flag.Int("gamePort", 1340, "Lidgren game port")
	bridgePort := flag.Int("bridgePort", 1341, "Oracle bridge port")
	scene := flag.Int("scene", 6, "Initial Landfall scene index")
	streamFor := flag.Int("secs", 0, "If >0, stream snapshots for N secs and print every snap")
	loadAfter := flag.Int("loadAfter", 0, "If >0, ask oracle to switch to this scene after first snapshot")
	flag.Parse()

	cfg := oracle.Default()
	cfg.GamePort = *gamePort
	cfg.BridgePort = *bridgePort
	cfg.Scene = *scene
	cfg.BootTimeout = 90 * time.Second // Proton + Unity boot can take a while on first cold launch

	fmt.Printf("Spawning oracle: gamePort=%d bridgePort=%d scene=%d\n", *gamePort, *bridgePort, *scene)
	t0 := time.Now()
	o, err := oracle.Spawn(cfg)
	if err != nil {
		log.Fatalf("Spawn failed: %v", err)
	}
	defer o.Kill()
	fmt.Printf("Oracle ready in %s.\n", time.Since(t0))

	// First snapshot — may take up to ~1s after spawn to populate.
	for i := 0; i < 30; i++ {
		if snap, ok := o.Snapshot(); ok {
			fmt.Printf("First snapshot: tick=%d scene=%s ents=%d\n", snap.Tick, snap.Scene, len(snap.Ents))
			break
		}
		time.Sleep(100 * time.Millisecond)
	}

	if *loadAfter > 0 {
		fmt.Printf("Asking oracle to load scene %d\n", *loadAfter)
		if err := o.LoadMap(*loadAfter); err != nil {
			fmt.Printf("LoadMap failed: %v\n", err)
		}
		time.Sleep(3 * time.Second)
		if snap, ok := o.Snapshot(); ok {
			fmt.Printf("After loadMap: tick=%d scene=%s ents=%d\n", snap.Tick, snap.Scene, len(snap.Ents))
		}
	}

	if *streamFor > 0 {
		deadline := time.Now().Add(time.Duration(*streamFor) * time.Second)
		last := int64(-1)
		for time.Now().Before(deadline) {
			snap, ok := o.Snapshot()
			if ok && snap.Tick != last {
				fmt.Printf("[t=%v] tick=%d scene=%s ents=%d\n",
					time.Since(t0).Truncate(time.Millisecond), snap.Tick, snap.Scene, len(snap.Ents))
				last = snap.Tick
			}
			time.Sleep(33 * time.Millisecond)
		}
	}

	fmt.Println("Tearing down oracle.")
	o.Kill()
	os.Exit(0)
}
