# sf-multiplayer/notes

Living research + design docs for the centralized-server revival of Stick Fight. Treat this as the architectural archive — code lives in `../sf-headless-host/` and `../sf-client-recon/`, but the *why* lives here.

## Read in this order

Newcomer to the project? Read these in order:

1. [`../README.md`](../README.md) — repo overview + quickstart
2. [`../NEXT_STEPS.md`](../NEXT_STEPS.md) — current state + roadmap toward client-prediction / server-reconciliation
3. [`ARCHITECTURE.md`](ARCHITECTURE.md) — full system overview (server side, client side, wire layer, authority model)
4. [`PROTOCOL.md`](PROTOCOL.md) — wire-format reference (every msgType, channel routing, v26 extensions)
5. [`OBJECT_SYNC.md`](OBJECT_SYNC.md) — definitive guide to SF's three world-object sync mechanisms (NSO, MapInfoSyncableBase, DestructiblePiece). Read this BEFORE debugging any "boxes/platforms/ice misbehave" issue.
6. [`BUGS_BACKLOG.md`](BUGS_BACKLOG.md) — incident log of every non-trivial bug + root cause + fix or open status
7. [`AUDIT_2026-05-23.md`](AUDIT_2026-05-23.md) — end-of-session deep audit covering open P0 bugs (destruction race, MapInfoSync precision, late-join gap, moving-platform drift, lerp-collision shatter)

## Operating manuals

- [`VPS.md`](VPS.md) — deployment guide (Proton + BepInEx + Goldberg + systemd + firewall)

## Phase 6 design history

The current architecture (centralized server using SF's own host-side code, driven by a BepInEx + Harmony plugin) is Phase 6. Earlier phases (Go server, lobby-relay only) are parked in `../legacy/`. Phase 6 design docs:

- [`phase6/00-PHASE6-OVERVIEW.md`](phase6/00-PHASE6-OVERVIEW.md) — entry-point summary
- [`phase6/STATUS-FOR-MILES.md`](phase6/STATUS-FOR-MILES.md) — readable status snapshot
- [`phase6/09-PHASE6.3-BLOCKER-AND-OPTIONS.md`](phase6/09-PHASE6.3-BLOCKER-AND-OPTIONS.md) — pre-Path-A decision context
- [`phase6/10-PHASE6.5-host-side-gameplay.md`](phase6/10-PHASE6.5-host-side-gameplay.md) — current host-side patch set + rationale
- [`phase6/11-PHASE6.6-pickup-and-physics.md`](phase6/11-PHASE6.6-pickup-and-physics.md) — pickup forwarding + diagnosis of why boxes initially didn't move
- [`phase6/12-PHASE6.13-sharding.md`](phase6/12-PHASE6.13-sharding.md) — multi-lobby v1 (shipped) + v2 (design)
- [`phase6/13-rewind-buffer.md`](phase6/13-rewind-buffer.md) — lag-comp design
- [`phase6/14-chat-commands.md`](phase6/14-chat-commands.md) — chat-command admin interface

Each phase doc reflects the state at the time of writing — newer findings supersede older ones. When in doubt, trust `ARCHITECTURE.md` + `BUGS_BACKLOG.md`.

## Legacy research (recon/, design/)

The [`recon/`](recon/) directory has the original reverse-engineering notes from the Phase 5 era (Go server, sfdsrv, sf-netcodev2). [`SUMMARY.md`](SUMMARY.md) is the headline from that work — the 3-second-match-cycle bug root cause. [`design/`](design/) has the fix designs that were drafted. These are kept for context but the current architecture supersedes them; the underlying SF behaviors documented are still valid references.

## File conventions

- Top-level `.md` docs in this directory are LIVING — updated as understanding evolves
- `phase6/NN-NAME.md` files are SNAPSHOTS at the time of writing (the prefix index implies write order)
- `recon/` is FROZEN reference material from earlier sessions
- `AUDIT_<date>.md` files are point-in-time deep dives; new ones get appended, old ones stay as historical record
