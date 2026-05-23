# Live status — for monitoring while away

Updated continuously by Claude as work progresses. Latest commit at top. Check this from phone to see what's happening.

## Active goal (from /goal)

1. ✅ **Investigate the crash** — don't patch unless 100% certain
2. ⏳ **Deploy to .115** (Proxmox VM, user `miles`, password fallback)
3. ⏳ **Build an installer** for end-user client setup
4. ⏳ **Build a GUI server browser**
5. ⛔ Don't launch anything on this local laptop — test on .115 only

## What's happening right now

**Now (2026-05-23 ~20:45):**
- Crash investigation complete; findings in [`notes/CRASH_INVESTIGATION.md`](notes/CRASH_INVESTIGATION.md)
- Per-lobby log listener bug fixed (commit pending push)
- Stopped all local oracles per goal directive
- About to start .115 deployment

## Key crash findings (full doc: [`CRASH_INVESTIGATION.md`](notes/CRASH_INVESTIGATION.md))

- **7 crashes today**, all `Access Violation 0xc0000005` in `StickFight.exe`
- **5 of 7 at the SAME instruction address `0x0057ed26`** with identical bytes — deterministic, not a race
- Faulting instruction is `lock xadd dword ptr [eax], ecx` (atomic refcount inc)
- Writing to `0x7f800004` — sentinel pointer near user/kernel boundary
- Without StickFight.exe symbols, can't attribute to a function. Top hypotheses: NSO event iteration race, init-chain NRE state-corruption cascade, ObjectUpdate-for-freed-NSO
- **NOT patching speculatively** — needs disassembly / runtime trace to confirm a fix

## Bug fix shipped this session that was MY BUG

`PerLobbyLogListener` from commit `bce8bcc` used `lock (_lock)` which compiles to `Monitor.Enter(obj, ref bool)` — a 2-arg overload that SF's Mono 2.0 runtime DOES NOT HAVE. Every log event threw `MissingMethodException`, BepInEx logged it, recursed back into the listener → infinite loop → 400MB log floods per oracle in 10 minutes. **Fixed** with `[ThreadStatic] _reentryGuard` + try/catch. Commit pending.

## TODO list (will tick off as I go)

- [x] Stop local oracles
- [x] Confirm crash mechanism (instruction-level)
- [x] Fix PerLobbyLogListener Monitor.Enter bug
- [x] Write CRASH_INVESTIGATION.md
- [x] Commit + push status doc + listener fix + crash investigation (commit `a38e3ed`)
- [x] Test SSH to .115 (key auth works, hostname `ubuntu-i7`)
- [x] Inventory existing state on .115 (Ubuntu 6.14, 15GB RAM, no SF/Proton, OLD Go sfdsrv running on 1337)
- [x] Stop + remove sfdsrv on .115 (systemd unit + /opt/sfdsrv gone)
- [x] Sync SF install to .115 (`~/sf-oracle/install`, 398M, plugins md5 verified)
- [x] Sync project to .115 (`~/sf-multiplayer`, 19M)
- [⏳] Sync Proton to .115 (~/sf-oracle/proton, in progress 886/1400 MB)
- [x] Write `deploy/start-oracle-server.sh` (server-bundled launcher)
- [x] Write `deploy/sf-oracle.service` (systemd unit)
- [x] Write `deploy/install-sf-client.sh` (client installer)
- [x] Write `deploy/server-browser.html` (standalone GUI browser)
- [ ] Install systemd unit on .115 + start
- [ ] Validate oracle boots cleanly on .115 (Proton path issues likely)
- [ ] Document `notes/DEPLOY.md` for future-self
- [ ] Commit + push all deploy files

## Known open issues (carried from earlier)

| ID | Issue | Status |
|---|---|---|
| OPEN-1 | Can't die to void | Likely fixed (P1-8 reverted) — untested |
| OPEN-2 | Lava no damage | Likely fixed (P1-8 reverted) — untested |
| OPEN-3 | Can't hit guns out of hands | Mechanism unclear — untested |
| OPEN-4 | Chains randomly break | Fixed by reverts — untested |
| OPEN-5 | Ice randomly breaks | Fixed by reverts + P0-15 — untested |
| OPEN-6 | Boxes disappear | Fixed by reverts — untested |
| OPEN-7 | Gun spawn breaks chain | Fixed by Phase 6.21 (commit `28269b1`) — untested |
| CRASH | Oracle native AV at 0x0057ed26 | Investigation written; NO patch (would be speculative) |

## Recent commits this session

| Commit | What |
|---|---|
| `bce8bcc` | phase 6.22 per-lobby logs + reserved-port skip (had Monitor.Enter bug) |
| `28269b1` | phase 6.21 suppress destruction from WeaponPickUp collisions |
| `4323818` | comprehensive session handoff docs (`SESSION_2026-05-23.md`) |
| `39f4c56` | deploy fix: SFHeadlessHost to Steam install + dead code cleanup |
| `4affabc` | revert P1-8, P0-11 heuristic, dynamic-NSO patch (live-test regressions) |

Plus the listener fix pending commit + push.

## Plugin binary identification

The build of the plugin paired with this commit. Used to confirm a deployed plugin matches expectations.

- `SFHeadlessHost.dll`: md5 will be in next commit
- `SFClientRecon.dll`: md5 will be in next commit
