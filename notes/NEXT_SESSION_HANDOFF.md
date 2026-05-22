# NEXT SESSION HANDOFF — pick up here

Hi future Claude / future Miles. Here's where things stand.

## What you should do, in order

1. **Read `SUMMARY.md`.** One paragraph; the headline recommendation.
2. **Read `recon/BUG_3SEC_MATCH_CYCLE.md`.** Full evidence trail for the diagnosis. Confidence is very high. Don't re-derive — if you want to spot-check, the verification recipe is below.
3. **Read `design/FIX_FLAG_LOGIC.md` and `design/FIX_SPAWN_FALLBACK_GUARD.md`.** These are the changes to make.
4. **Implement on the dev laptop.** Source lives at `~/sf-multiplayer/StickFightDedicatedSrv/` on `gentoo @ 100.66.167.44` (user `miles`, SSH key auth works from this VM).
5. **Build and test on port 1338.** **Do not touch the production server on port 1337.** Build path: `/tmp/sfdsrv.test`. See `design/VERIFICATION_PLAN.md` for the recipe.
6. **Once verified, ask Miles before swapping into prod.** The cutover replaces `/tmp/sfdsrv.combined` on port 1337 and should be his call.

Estimated total time: ~1.5-2 hours, dominated by the live 2-Goldberg-instance test loop.

## Quick-orient cheat sheet

- Prod server: `/tmp/sfdsrv.combined`, port 1337, on dev laptop.
- Source: `~/sf-multiplayer/StickFightDedicatedSrv/` on dev laptop (git repo).
- Server architecture overview: `recon/SERVER_ARCHITECTURE.md` in this dir.
- Decompiled stock SF: `~/sf-multiplayer/refs/decompiled/Assembly-CSharp/` on dev laptop (358 .cs files).
- Patched DLL source: `~/sf-multiplayer/sf-netcodev2/` on dev laptop; local copy at `recon/sf-netcodev2/` here.
- Prior session memory: `recon/prior-memory/` here (mirrored from the dev laptop). `phase5_state.md` is the most useful single file.
- Approved Phase 5 plan: `/home/miles/.claude/plans/iterative-sparking-pascal.md` on the dev laptop.

## Verification quick recipe

To prove the bug exists before fixing (5 minutes):

```bash
ssh miles@100.66.167.44
cd ~/sf-multiplayer/StickFightDedicatedSrv

# Build a test binary at a separate path
go build -o /tmp/sfdsrv.test .

# Sanity-check that port 1338 is free
ss -tlnp | grep -E "1337|1338"

# Run test server on 1338
/tmp/sfdsrv.test -address 0.0.0.0:1338 -mapsDir ~/sf-multiplayer/maps -verbosity 1 > /tmp/sfdsrv.test.log 2>&1 &
TEST_PID=$!
sleep 1

# Confirm it's running
curl -s http://127.0.0.1:1338/status

# Use the existing launch-local2.sh (or modified copy pointing at :1338) to bring up two Goldberg instances.
# Then tail the log:
tail -f /tmp/sfdsrv.test.log

# Look for the smoking gun:
#   "Spawned player N at position {X:0 Y:12 Z:0} ... using flag 1"
# followed immediately by
#   "Player N took a killing blow from player N of type Other"
# followed by
#   "Player N is the winner!"
# then ~3 seconds later "Started match!" again.

# To stop the test server:
kill $TEST_PID
```

If the log matches, the diagnosis is confirmed. Implement the fixes from `design/FIX_FLAG_LOGIC.md` and `design/FIX_SPAWN_FALLBACK_GUARD.md`, rebuild, re-test.

## What I did NOT do

- I did **not** write or modify any code in `~/sf-multiplayer/`. Everything I produced is `.md` files under `/home/miles/sf-multiplayer/notes/` on **this** VM (the Proxmox VM, not the dev laptop).
- I did **not** restart, stop, or touch the production server on port 1337.
- I did **not** run a test server.
- I did **not** message anyone, push to git, or update the prior-session memory directory on the dev laptop.

## What I noticed but didn't act on

See `design/OPEN_QUESTIONS.md` and `recon/RELATED_BUGS.md`. Highlights:

- All dumped killbox arrays are empty (`tools/dump-sf-maps.py` doesn't extract them). Server-side killbox anticheat is unreachable.
- The spawn-position fallback guard (`posX == 0 && posY == 0 && posZ == 0`) misses the real sentinel `(0, 12, 0)` — see `FIX_SPAWN_FALLBACK_GUARD.md`. Bundle this with the flag fix.
- A couple of latent race conditions in `lobbies.go` (direct `lobby.Clients[...]` indexing) — track in M5 hardening.
- The `swears = []string{" "}` filter trips on every space — currently neutered, but worth noting before anyone flips it on.

## Files in this directory (treemap)

```
notes/
├── SUMMARY.md                       <- start here
├── README.md                        <- index + house rules
├── NEXT_SESSION_HANDOFF.md          <- this file
├── design/
│   ├── FIX_FLAG_LOGIC.md            <- the headline fix design
│   ├── FIX_SPAWN_FALLBACK_GUARD.md  <- secondary related fix
│   ├── VERIFICATION_PLAN.md         <- how to confirm the fix works
│   └── OPEN_QUESTIONS.md            <- known unknowns
└── recon/
    ├── BUG_3SEC_MATCH_CYCLE.md      <- full evidence trail
    ├── SERVER_ARCHITECTURE.md       <- distilled Go server architecture
    ├── CODE_PATH_SPAWN_FLOW.md      <- line-by-line spawn-handshake trace
    ├── RELATED_BUGS.md              <- other bugs noticed
    ├── SRV_README.md                <- upstream README copies
    ├── LAUNCHER_README.md
    ├── SWAP_NOTES.txt
    ├── sfdsrv.log
    ├── sfdsrv.next.log
    ├── baseline-lines.txt
    ├── BepInEx_main.log
    ├── BepInEx_mirror.log
    ├── prior-memory/                <- full mirror of prior session's memory dir
    ├── StickFightDedicatedSrv/      <- read-only mirror of Go server source
    └── sf-netcodev2/                <- read-only mirror of patched-DLL source
```

Good luck.
