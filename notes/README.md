# sf-multiplayer/notes

Living research + design docs for the centralized-server revival of Stick Fight. Treat this as the architectural archive — code lives in `../sf-headless-host/` and `../sf-client-recon/`, but the *why* lives here.

> **Source of truth.** The two canonical, continuously-updated docs are [`../README.md`](../README.md) (repo overview) and [`../NEXT_STEPS.md`](../NEXT_STEPS.md) (current state + roadmap). For the system + wire details, [`ARCHITECTURE.md`](ARCHITECTURE.md) and [`PROTOCOL.md`](PROTOCOL.md) here are the reference. Everything else in this directory (session snapshots, `SUMMARY.md`, `phase6/STATUS-FOR-MILES.md`, dated `AUDIT_*`/`SESSION_*` files) is point-in-time history — accurate when written, superseded since. When in doubt, trust the four docs above + [`BUGS_BACKLOG.md`](BUGS_BACKLOG.md).

## Read in this order

Newcomer to the project? Read these in order:

1. [`../README.md`](../README.md) — repo overview + quickstart **(source of truth)**
2. [`../NEXT_STEPS.md`](../NEXT_STEPS.md) — current state + roadmap toward client-prediction / server-reconciliation **(source of truth)**
3. [`ARCHITECTURE.md`](ARCHITECTURE.md) — full system overview (server side, client side, wire layer, authority model)
4. [`PROTOCOL.md`](PROTOCOL.md) — wire-format reference (every msgType, channel routing, v26 extensions)
5. [`OBJECT_SYNC.md`](OBJECT_SYNC.md) — definitive guide to SF's three world-object sync mechanisms (NSO, MapInfoSyncableBase, DestructiblePiece). Read this BEFORE debugging any "boxes/platforms/ice misbehave" issue.
6. [`BUGS_BACKLOG.md`](BUGS_BACKLOG.md) — incident log of every non-trivial bug + root cause + fix or open status
7. [`bug-investigations/`](bug-investigations/) — deep-dive root-cause analyses with evidence + fix sketches (one file per investigation)
8. [`SF_VANILLA_INSPECTION.md`](SF_VANILLA_INSPECTION.md) — Unity Explorer setup + how to read vanilla SF runtime state for ground-truth comparison

The dated deep dive [`AUDIT_2026-05-23.md`](AUDIT_2026-05-23.md) (destruction race, MapInfoSync precision, late-join gap, moving-platform drift, lerp-collision shatter) is a historical snapshot — most of its open P0s have since shipped; cross-check status against [`BUGS_BACKLOG.md`](BUGS_BACKLOG.md).

## Operating manuals

- [`VPS.md`](VPS.md) — deployment guide (Proton + BepInEx + Goldberg + systemd + firewall)
- [`DEPLOY.md`](DEPLOY.md) — fresh-server bring-up + systemd unit + rsync/update runbook
- [`MULTI_LOBBY_LIVE.md`](MULTI_LOBBY_LIVE.md) — single-port multi-lobby: deployed state, capacity, ops, revert
- [`ROUTER.md`](ROUTER.md) — sf-router architecture + operations
- [`ROUTER_LIVE_TEST.md`](ROUTER_LIVE_TEST.md) — router deploy + 2-client verification runbook
- [`LIVE_TEST_CHECKLIST.md`](LIVE_TEST_CHECKLIST.md) — quick 2-player smoke test against the live server
- [`SERVER_VS_ASSEMBLY_MAP_LOAD.md`](SERVER_VS_ASSEMBLY_MAP_LOAD.md) — who loads the map (oracle vs client Assembly), and the map-load sequence

The live player-facing server is `69.53.117.43` (game UDP 1337, lobby browser TCP 8080). `192.168.1.115` is the VM's internal LAN address — it only appears in on-VM runbook steps, never as the address a player connects to.

## Phase 6 design history

The current architecture (centralized server using SF's own host-side code, driven by a BepInEx + Harmony plugin) is Phase 6. Earlier phases (Go server, lobby-relay only) are parked in `../legacy/`. These Phase 6 docs are SNAPSHOTS at their time of writing — for current status read `../NEXT_STEPS.md`, not the dated status files below:

- [`phase6/00-PHASE6-OVERVIEW.md`](phase6/00-PHASE6-OVERVIEW.md) — entry-point summary
- [`phase6/STATUS-FOR-MILES.md`](phase6/STATUS-FOR-MILES.md) — historical status snapshot (superseded by `../NEXT_STEPS.md`)
- [`phase6/09-PHASE6.3-BLOCKER-AND-OPTIONS.md`](phase6/09-PHASE6.3-BLOCKER-AND-OPTIONS.md) — pre-Path-A decision context
- [`phase6/10-PHASE6.5-host-side-gameplay.md`](phase6/10-PHASE6.5-host-side-gameplay.md) — current host-side patch set + rationale
- [`phase6/11-PHASE6.6-pickup-and-physics.md`](phase6/11-PHASE6.6-pickup-and-physics.md) — pickup forwarding + diagnosis of why boxes initially didn't move
- [`phase6/12-PHASE6.13-sharding.md`](phase6/12-PHASE6.13-sharding.md) — multi-lobby v1 (shipped) + v2 (design)
- [`phase6/13-rewind-buffer.md`](phase6/13-rewind-buffer.md) — lag-comp design
- [`phase6/14-chat-commands.md`](phase6/14-chat-commands.md) — chat-command admin interface

Each phase doc reflects the state at the time of writing — newer findings supersede older ones. When in doubt, trust `ARCHITECTURE.md` + `BUGS_BACKLOG.md`.

## Legacy research (recon/, design/)

The [`recon/`](recon/) directory has the original reverse-engineering notes from the Phase 5 era (Go server, sfdsrv, sf-netcodev2). [`SUMMARY.md`](SUMMARY.md) is the headline from that work — the 3-second-match-cycle bug root cause. [`design/`](design/) has the fix designs that were drafted. These are FROZEN historical references — the current Phase 6 headless-host architecture supersedes them (the Go server is parked in `../legacy/`); the underlying SF behaviors documented are still valid references. Do not treat `SUMMARY.md` as current status.

## File conventions

- Top-level `.md` docs in this directory are LIVING — updated as understanding evolves
- `phase6/NN-NAME.md` files are SNAPSHOTS at the time of writing (the prefix index implies write order)
- `recon/` is FROZEN reference material from earlier sessions
- `AUDIT_<date>.md` files are point-in-time deep dives; new ones get appended, old ones stay as historical record
