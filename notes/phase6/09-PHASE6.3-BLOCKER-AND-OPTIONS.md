# Phase 6.3 blocker — client connection protocol mismatch

> ⚠️ **Historical (2026-05).** Phase design doc. Current state: [NEXT_STEPS.md](../../NEXT_STEPS.md).

**Status: blocker identified; design options sketched; needs operator decision before code.**

## What we tried

After 6.2 (Go oracle bridge working end-to-end — spawn, ping, snapshot, loadMap, kill all verified), the natural next step was: have a real Stick Fight client connect to the oracle's game port (1340) so the oracle's actual Movement.cs / Controllers tick with a player rig present, and `Snapshot().Ents` populates.

Test: pointed our existing smoke-test client (the Go mock that exercises the centralized server's wire protocol) at the oracle's game port:

```bash
/tmp/sfsmoke -secs 5 -proto 25 -steam 76561199000000009 -addr 127.0.0.1:1340
```

Result: smoke test sent its v25 handshake bytes; oracle's Lidgren NetServer silently ignored them. No client-join in the oracle's BepInEx log. Lidgren ate the packets without producing a NetConnection.

## Root cause

There are TWO incompatible wire protocols in play.

### The centralized-server protocol (what our Go server + patched DLL speak)

When the StickFightDev maintainer patched Assembly-CSharp to point at a centralized server, they replaced the Lidgren transport with raw UDP. Each datagram is a single SF message: `[u32 timestamp][u8 msgType][body...][u64 steamID][u8 channel]`. No connection state, no peer discovery, no reliability — the protocol is essentially "fire-and-forget over the wire."

This is what:
- `StickFightDedicatedSrv/packets.go` marshals/unmarshals
- The patched DLL `Assembly-CSharp.srv.v25.dll` emits and consumes
- Our `cmd/smoke-test/main.go` produces

### The stock Stick Fight Lidgren protocol (what the oracle speaks)

The oracle is running stock unpatched SF inside `-batchmode`. `MatchMakingHandlerSockets.HostServer()` creates a `Lidgren.Network.NetServer` listening on UDP 1340 with these enabled message types:

- `DiscoveryRequest` / `DiscoveryResponse` (LAN browsing)
- `ConnectionApproval` (handshake)

Lidgren wraps every payload in its own framing — connection negotiation, session IDs, sequence numbers, reliability channels. A peer must:

1. Send a Lidgren `Connect` with a NetPeerConfiguration matching `APP_NAME` ("StickFight 1.0")
2. Wait for `ConnectionApproval` exchange
3. Then send payloads as `NetOutgoingMessage`s

Our raw-byte v25 traffic doesn't survive Lidgren's framing check, so it's silently dropped before reaching any game-logic handler.

## Why the test "ran fine" looking from the outside

The smoke-test prints "[smoke] sent N packets" — it's just measuring outgoing UDP writes. The OS happily delivers the bytes; the oracle's Lidgren peer ingests them, decides they aren't valid Lidgren frames, drops them, and we never see "client joined" because no NetConnection ever forms.

## Architecture options for 6.3

### Option A — Patch the oracle's NetworkSocketServer to speak raw UDP

Harmony-patch `Landfall.Network.Sockets.NetworkSocketServer` and `P2PPackageHandler` inside the oracle so they ingest the same raw-byte protocol as our Go server, bypassing Lidgren.

**Pros:**
- Existing patched-DLL clients connect directly to the oracle, no protocol shim.
- Go server stays out of game-traffic path → minimum latency.

**Cons:**
- Major Harmony patching surface — `NetServer.ReadMessage`, `P2PPackageHandler.Update`, `MatchMakingHandlerSockets.ReadMessage`. Tens of methods.
- Re-implements the patched-DLL's existing IL edits inside the SERVER instead of the CLIENT — the existing patch already taught the v25 client to bypass Lidgren; we'd be doing the same surgery on the server.
- Brittle against any SF update that changes Lidgren internals.

### Option B — Lidgren-speaking bridge in Go

Make the Go server speak Lidgren itself, as a CLIENT relative to the oracle. The Go server stays the entry point for our clients (raw-UDP v25/v26 on port 1337). On each player-input packet from a client, Go forwards it through a Lidgren `NetClient` connection to the oracle (port 1340), then routes the oracle's snapshot updates back to clients.

**Pros:**
- Centralized-server architecture preserved — clients keep connecting to Go on the same port with the same protocol. Zero client-side changes.
- The Lidgren CLIENT API is simpler than the SERVER API (we don't have to implement peer discovery / connection approval logic — just call Connect()).
- A Go Lidgren implementation exists (or is portable from C#).

**Cons:**
- Adds a hop (client → Go → oracle → Go → client) per packet. Probably +1-2ms LAN, +10-30ms WAN. Acceptable for SF.
- We have to write/port a Go Lidgren client. Lidgren's protocol isn't standardized; only the C# implementation is canonical. Reasonable subset is ~1000 lines.
- Per-lobby state on Go side now includes Lidgren connection state.

### Option C — Restore the unpatched DLL, drop our centralized-server architecture

Clients use stock unpatched SF; they connect to the oracle's IP:1340 directly via Lidgren as if the oracle were a friend's machine hosting a P2P match. Go server stops being the gameplay route entirely; it only handles matchmaking ("here's the oracle IP for your match") and lobby browsing.

**Pros:**
- Conceptually the cleanest. The oracle IS Stick Fight. Clients speak stock SF protocol to it.
- Most of our centralized-server work becomes unnecessary scaffolding — Go's role compresses to matchmaker.
- No Lidgren reimplementation needed.

**Cons:**
- Throws away most of `StickFightDedicatedSrv/` (lobby, packet relay, wire-format fixes, v26 protocol). We keep only matchmaking + HTTP endpoints + maybe room codes.
- Throws away the patched DLL + SFNetcodeV2 plugin — clients use stock SF, no prediction, no v26.
- Clients need a way to find out the oracle's address. Stock SF expects Steam P2P or LAN discovery — neither works for arbitrary internet servers without a launcher mod.
- Heaven.heist542 / ALKA expected "centralized server with full physics simulations + client input validation + anticheat." Centralized-server architecture goes away; anticheat moves into the oracle (server-trusts-the-client-runs-the-game inversion).

### Option D — Hybrid: oracle as physics oracle ONLY, not network endpoint

Oracle never accepts real client connections. Go server stays the network endpoint and gameplay relay. Go sends `applyInput` commands to the oracle's bridge whenever a client sends `playerInput`. Oracle's Movement.cs / Controller code receives those inputs as if from local input, processes them, and emits snapshots back through the bridge. Go reads snapshots and broadcasts them to clients as `worldStateSnapshot`.

**Pros:**
- Architecture stays close to today's. Clients still connect to Go on 1337. Wire protocols unchanged.
- Oracle becomes a pure "physics consultant" — Go asks "given input I, what's the new world state?" and the oracle replies.
- No Lidgren work needed.

**Cons:**
- We have to write a Harmony patch in SFHeadlessHost that intercepts the input-flow chain. There's a `MatchMakingHandlerSockets.HostServer()` path that creates a NetworkSocketServer normally — we don't host one; instead we inject inputs into `Controller` directly.
- The oracle's "input injection" needs to look enough like a real client's input that all the game-state side-effects fire (animations, weapon firing, ragdoll triggers).
- Have to also synthesize "fake clients" in the oracle's perspective so the game doesn't get confused about having zero players. May need to spawn dummy Players in the scene and bind them to the bridge inputs.

## Recommendation

**Option D is the best fit for this project's existing architecture.** We've spent a session+ getting the centralized-server wire protocol working and fixing its bugs. Throwing that away (Option C) wastes the work. Lidgren reimplementation (Option B) is multi-week. Patching server-side to speak raw UDP (Option A) re-does the patched-DLL work inside Unity, which is fragile.

Option D keeps everything we have, adds a new SFHeadlessHost responsibility: be the "physics simulator" service that the Go server calls. The game-coordination (matchmaking, lobbies, networking) stays in Go; the physics-truth (player positions, ragdoll, killboxes, weapon trajectories) moves to the oracle.

## Recommended 6.3 plan under Option D

1. **Don't have the oracle host a NetworkSocketServer at all.** Skip the `HostServer()` call in SFHeadlessHost. Game port 1340 closes — only the bridge port 1341 stays open.
2. **Spawn dummy Player objects in the oracle's scene.** On first `applyInput` for a slot, instantiate a Controller + Player rig at that slot's spawn position. Maintain slot → GameObject mapping.
3. **Add a new bridge command:** `{"cmd":"applyInput","slot":N,"stickX":...,"stickY":...,"buttons":N,"aimX":...,"aimY":...}`. Oracle finds the slot's Player and applies the input as if it came from local controllers.
4. **Snapshot already exposes positions** (Phase 6.2 done). Now they'll be non-empty.
5. **Go server side:** in `Lobby` add an `Oracle` field. On each incoming `playerInput` packet from a v26 client, forward it through `oracle.ApplyInput()`. On each oracle snapshot, broadcast it as `worldStateSnapshot` to all v26 clients in the lobby. Retire the `physics/` package's player tick.

That's 2-3 days of focused work. Lower risk than B/C/D-prime.

## What we keep / retire under Option D

**Keep:**
- All of Path A's Go server (lobby coordination, packet relay, wire-format fixes)
- v26 protocol (snapshots come from oracle now instead of `physics/` package, but wire shape unchanged)
- SFNetcodeV2 client plugin (still handles snapshot reception + prediction)
- Replay logger
- Anticheat input rate limits

**Retire (over time):**
- `physics/` package's player-tick + projectile-tick. Killboxes + static colliders are still loaded for legacy-v25 hit checks but won't be the source of truth.
- `Lobby.SyncableEntities` server-side tracking — oracle owns it.
- The auto-respawn-on-spawn code + the `(0, *, 0)` sentinel guard — oracle picks spawn positions natively.

## What needs operator decision before coding starts

1. **Confirm Option D over the alternatives.** A/B/C are all real options with different trade-offs; D matches our codebase best but isn't obviously "right" for everyone.
2. **Concurrent lobby cap.** Each oracle is 1+ GB RAM. On the i7-7700 Proxmox VM we can realistically support ~4 lobbies. For the comp scene that's fine (≤8 simultaneous matches), for public-server scale we'd need a fleet.
3. **Should the Go server still run server-side `physics/`** as a fallback for un-oracled lobbies (e.g. workshop-map lobbies the oracle doesn't have data for)? Or remove entirely?

---

## Phase 6.3 progress notes (incremental)

### Verified (Option D, partial):
- Added `{"cmd":"spawnPlayer","slot":N}` to the bridge. Plugin parses + replies with ack/err.
- Tried `TrySpawnPlayer` via `ControllerHandler.playerPrefab` reflection.
- Result: `err: "ControllerHandler instance not in scene"`. ControllerHandler doesn't exist in a Landfall gameplay scene (it's in the lobby scene).

### What this means for 6.3 path:
The Landfall scenes are the "match" scenes — they have map geometry, killboxes, weapon spawns, etc., but no ControllerHandler / GameManager / MultiplayerManager DontDestroyOnLoad infrastructure. Those get instantiated by the lobby scene's bootstrap code.

Three concrete sub-options for Phase 6.3 spawn-player:

A. **Load lobby scene first** — `SceneManager.LoadScene(0)` to boot the game's normal init path (which spawns ControllerHandler, GameManager, etc. as DontDestroyOnLoad), THEN switch to the Landfall scene. The infrastructure persists across the switch. Closest to how real SF works.

B. **Synthesize the bootstrap GameObjects ourselves** — On scene-loaded in the oracle plugin, instantiate empty GameObjects with ControllerHandler / GameManager / MultiplayerManager components attached. Risky because those classes probably reference scene-specific assets at Awake().

C. **Skip ControllerHandler entirely; spawn the player prefab manually** — We don't actually need a Controller for input — we'd be feeding input via Harmony override anyway. Just `Instantiate(playerPrefab)` and have the inputs feed into the rig's `Actions` field directly.

**Recommendation: try A first.** Cheapest test — just change `InitialScene` from 6 to 0 in our default and see if a scene change to a Landfall map after lobby spawn works naturally. If yes, ControllerHandler will be there. If no (lobby map requires real interaction to "leave"), fall back to C.

### Other observations:
- Snapshot stream continues working even when `HostServer()` fails (Lidgren bind error from ghost wineserver holding the port from a previous run). Bridge is independent of game port — good design separation.
- Cleanup of ghost wineserver processes is essential between oracle launches. Add `pkill -9 -f wineserver` to the oracle Kill() path.
- The python-side test confirmed: `{"cmd":"ping"}` → `{"reply":"pong","tick":0,"scene":"Desert3"}` ← fully working end-to-end.
