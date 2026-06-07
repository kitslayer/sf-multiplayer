# Live status — for monitoring while away

> ⚠️ **Superseded (2026-06-06).** Historical phone-monitoring scratchpad — current state lives in [`NEXT_STEPS.md`](NEXT_STEPS.md) + [`README.md`](README.md). Live server: `69.53.117.43`.

Updated continuously by Claude as work progresses. Latest commit at top. Check this from phone to see what's happening.

## Active goal (from /goal)

1. ✅ **Investigate the crash** — don't patch unless 100% certain
2. ⏳ **Deploy to .115** (Proxmox VM, user `miles`, password fallback)
3. ⏳ **Build an installer** for end-user client setup
4. ⏳ **Build a GUI server browser**
5. ⛔ Don't launch anything on this local laptop — test on .115 only

## What's happening right now

**Now (2026-05-23 ~21:25):**
- ✅ Crash investigation complete; findings in [`notes/CRASH_INVESTIGATION.md`](notes/CRASH_INVESTIGATION.md)
- ✅ Per-lobby log listener bug fixed
- ✅ Stopped all local oracles per goal directive
- ✅ **Phase 6 oracle DEPLOYED + RUNNING on .115** (69.53.117.43)
  - Port 1337 UDP/TCP bound
  - UDP ping verified from laptop
  - Per-lobby plugin log streaming heartbeats
  - All 10 Phase 6.5 patches loaded
  - Systemd auto-restart on crash
- Next: lobby browser HTTP on .115, real client testing whenever you're ready

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
- [x] Sync Proton (1.4G) + Steam Runtime libs (1.1G) to .115
- [x] Write `deploy/start-oracle-server.sh` (server-bundled launcher; xvfb-run wrapper added)
- [x] Write `deploy/sf-oracle.service` (systemd unit)
- [x] Write `deploy/install-sf-client.sh` (client installer)
- [x] Write `deploy/server-browser.html` (standalone GUI browser)
- [x] Install systemd unit on .115 + start
- [x] Install required apt deps on .115 (i386 libs + steam-installer + xvfb)
- [x] Validate oracle boots cleanly on .115 (port bound, plugin log streams, UDP ping responds)
- [x] Document `notes/DEPLOY.md` with apt deps + xvfb requirement
- [x] Commit + push xvfb fix + STATUS update (commit `5b91159`)
- [x] Test the lobby browser HTTP endpoint on .115 (running on :8080, `/healthz` + `/lobbies` both responding)
- [ ] (optional) Document `install-sf-client.sh` usage in DEPLOY.md
- [ ] (optional) Make `server-browser.html` discoverable via README

## What comp players actually do (two clicks)

Send them the `dist/` folder from this repo. Inside:
- `install-sf-client.bat` — double-click ONCE to set up BepInEx + plugins
- `SFLauncher.exe` — real Windows GUI lobby browser, double-click to launch
- `README.md` — quickstart

In SFLauncher: paste `http://69.53.117.43:8080/lobbies`, hit Refresh, pick a lobby, click Connect. The launch options get copied to clipboard and Steam opens. They click Play. Done.

(They only need to redo the install-sf-client.bat step if plugins update — usually never.)

## What you (the operator) can do now

Open `deploy/server-browser.html` in any browser (save it locally first). Enter:
```
http://69.53.117.43:8080/lobbies
```
and click "refresh". You'll see the MAIN lobby on .115. Click "copy linux" or "copy windows" to grab the launch options.

Steam launch options for your Stick Fight (paste in Steam → Properties → Launch Options):
```
WINEDLLOVERRIDES="winhttp=n,b" %command% -address 69.53.117.43 -port 1337
```
(Or for Windows clients, drop the WINEDLLOVERRIDES part.)

Then click Play. You'll connect to the .115 oracle.

## Resource use measured on .115 (idle, no clients)

```
xvfb-run         2 MB RSS
Xvfb            12 MB RSS
wineserver      16 MB RSS, ~9% CPU
StickFight.exe 424 MB RSS, ~59% CPU
```

Total ~450 MB RAM + ~70% of one core when idle. With players, expect ~500MB + ~80%.

## .115 hardening still TODO

- [ ] systemctl enable + auto-start on reboot (currently only `start`ed; needs `enable` so it survives `sudo reboot`). The unit file has WantedBy=multi-user.target which `enable` would wire up.
- [ ] Open UDP port 1337 in the host firewall (LAN already reachable; for WAN access, may need NAT forwarding on the router)
- [ ] Open TCP port 8080 same way (if exposing the lobby browser publicly — probably DON'T want this)
- [ ] Log rotation on /tmp/sf-oracle-unity-11337.log (Unity log floods over time — see CRASH_INVESTIGATION about the 6.7M-line incident)

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
