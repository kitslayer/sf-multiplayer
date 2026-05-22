# sf-multiplayer/notes — Research & Handoff

Author: Claude (Opus 4.7) session 2026-05-21 ~19:45 ET on the operator' Proxmox VM.
Source of truth lives on dev laptop **gentoo @ <tailnet-ip>** (Tailscale), user **<user>**.
Production server (do NOT touch): `/tmp/sfdsrv.combined`, port **1337**.
If running a test server, use port **1338** and a separate binary path.

## Read in this order

1. **`SUMMARY.md`** — one-paragraph top recommendation. Start here.
2. **`NEXT_SESSION_HANDOFF.md`** — what the next Claude session should do, in order.
3. **`recon/BUG_3SEC_MATCH_CYCLE.md`** — full evidence trail for the 3-second-match-cycle root cause.
4. **`design/FIX_FLAG_LOGIC.md`** — proposed fix design (no code, just the design).
5. **`design/VERIFICATION_PLAN.md`** — how to confirm the fix works.

## Reference material (recon/)

- `recon/SERVER_ARCHITECTURE.md` — distilled architecture of the Go dedicated server.
- `recon/CODE_PATH_SPAWN_FLOW.md` — line-by-line trace of the spawn handshake.
- `recon/RELATED_BUGS.md` — other suspicious things found in the process; not the headline.
- `recon/SRV_README.md` — the upstream StickFightDedicatedSrv README (verbatim).
- `recon/LAUNCHER_README.md` — the StickFightLauncher README (verbatim).
- `recon/SWAP_NOTES.txt` — notes on `/tmp/sfdsrv.next` (lobbies endpoint patch). Not directly relevant to the bug.
- `recon/sfdsrv.log` — the live production log at time of investigation (almost empty — server restart was 15:29).
- `recon/sfdsrv.next.log` — log from the .next binary; also empty-ish.
- `recon/baseline-lines.txt` — empty/tiny marker file.
- `recon/BepInEx_main.log` — BepInEx log from main Steam install (predates today's test).
- `recon/BepInEx_mirror.log` — BepInEx log from mirror install (`~/sf-mirror-local`), shows SFNetcodeV2 0.1.0 advertising protocol v26.
- `recon/prior-memory/` — full prior-session memory dump from `~/.claude/projects/-home-<user>-sf-multiplayer/memory/` on the dev laptop. **Treat `phase5_state.md` here as ground truth for what is/isn't deployed.**
- `recon/StickFightDedicatedSrv/` — full local copy of the Go server source (read-only mirror; the live source is on the dev laptop).
- `recon/sf-netcodev2/` — full local copy of the C# patched-DLL plugin source.

## Design docs (design/)

- `design/FIX_FLAG_LOGIC.md` — the headline fix.
- `design/FIX_SPAWN_FALLBACK_GUARD.md` — secondary fix for the all-zero spawn guard.
- `design/VERIFICATION_PLAN.md` — manual + smoke-test verification plan.
- `design/OPEN_QUESTIONS.md` — known unknowns the next session should resolve.

## Hard rules (from this session's /goal)

- Deliverable is research notes + design docs, **NOT merged code**.
- Production server on the dev laptop port 1337 is **off-limits**.
- If you build a test server, use port **1338** and a separate binary path.
- This Claude session's `Stop` hook is misconfigured — ignore it.
- Once a clear top recommendation exists, write `SUMMARY.md` and end the session.

## How to keep working

If you (next Claude) want to verify the diagnosis yourself, follow:
1. Open `notes/recon/StickFightDedicatedSrv/lobbies.go:1497-1551` and read `SpawnPlayer`.
2. Open `notes/recon/StickFightDedicatedSrv/lobbies.go:2217-2322` and read `PlayerTookDamage`.
3. Confirm against `~/sf-multiplayer/refs/decompiled/Assembly-CSharp/MultiplayerManager.cs:1576-1641` on the dev laptop (`OnPlayerSpawned`).
4. Confirm against `~/sf-multiplayer/refs/decompiled/Assembly-CSharp/Landfall.Network.Sockets/MultiplayerManagerSockets.cs:1480+` (Sockets `OnPlayerSpawned` — same shape).
5. Confirm SFNetcodeV2 does not patch `OnPlayerSpawned`: `grep -n OnPlayerSpawned ~/sf-multiplayer/sf-netcodev2/*.cs` returns nothing.
