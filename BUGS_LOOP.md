# SF polish loop — queue, protocol & evidence log

**Branch:** `loop/polish-2026-06-22`. This file is the loop's single source of truth and is designed to survive context resets. **Read it in full at the start of every iteration.**

## Mission
Aggressive full-fix pass on three problem areas kit flagged as broken: **(1)** lobby browser + lobby-switching, **(2)** server-authoritative boxes, **(3)** anti-cheat. Attempt real end-to-end fixes — *including* parts that ultimately need kit's live 2-player test — but always be honest about which level of verification each fix actually reached.

## HARD RULES — never violate
- **KEEP the quick-draw / pickup-instant-shot behavior** (pickup with no cooldown + no recoil). It is *wanted* in the comp scene. Do NOT "fix" it, ever.
- **The anti-cheat low-damage-kill kick is intentionally log-only** (gated behind `SF_AC_KICK=1`). Anticheat is observation-only by default (`SF_ANTICHEAT_ENFORCE=1` flips it) — that is **by design** (backlog P2-6). Do NOT "re-enable" either as if it were a bug.
- **Stay on this branch.** Do NOT push, do NOT deploy to `.115`, do NOT touch `main`, do NOT touch any live infra. Merges to main happen only after kit live-verifies.
- **`main` is PUBLIC — keep secrets + personal info out of everything you commit.** No VM/SSH creds or passwords (`.115`), no private keys, no real names / emails / Discord handles, no real-player SteamIDs — not in commit messages, not in `BUGS_LOOP.md`, not in code comments, and not in captured evidence (scrub IPs/usernames/SteamIDs out of logs + screenshots before saving to `loop-evidence/`). Don't over-sanitize otherwise: normal code, bug details, and the already-public server address are fine. The loop never pushes (kit controls merge→main) — this just keeps the branch clean so the eventual merge needs zero scrubbing.
- **Compile-verify EVERY code change before committing.** C# host/client/box/browser: `~/.dotnet/dotnet build <proj>` (8.0.422). Router: `cd sf-router && go build ./... && go test ./...` (go 1.26.2). Never commit a non-building tree.
- **Process discipline (I have killed kit's game twice).** The oracle AND player2 both run from `sf-mirror-local`; distinguish by the `-batchmode` flag, NEVER by install path. NEVER blanket `pkill` SF. Before launching or killing anything, list the matching processes and confirm none is kit's interactive game.
- **When something looks "wrong," check `notes/` + `notes/BUGS_BACKLOG.md` + memory BEFORE changing it.** It may be intentional, or a known accepted trade-off (the P2 table). Several "obvious bugs" in this codebase were deliberate (see backlog "Closed without code change").

## Per-iteration protocol — do ALL of it, every time. Do NOT get into a groove.
1. **Re-read this file's queue.** Pick the single highest-value UNBLOCKED item. State *why that one* and not the others.
2. **Investigate from scratch.** Do not inherit last iteration's framing. Read the actual code (cite `file:line`), logs, `notes/PROTOCOL.md`, and the backlog row. Write a root-cause hypothesis backed by specific evidence.
3. **SKEPTIC PASS (written, mandatory, BEFORE you change anything):**
   - (a) What evidence would prove this hypothesis **wrong**?
   - (b) Could this "bug" be **intentional** / a known trade-off? (check first)
   - (c) What could this change **break** — side effects, other call sites, other maps, other players?
   - (d) Is there a **simpler or safer** fix?
   - Assign a **confidence: N%**.
4. **Make the MINIMAL change.** One bug = one commit. No drive-by edits.
5. **VERIFY at the highest feasible level** (hierarchy below). **Capture evidence**: screenshots → `loop-evidence/<ID>/`, test output, or the exact log lines you grepped. If you can only reach CANDIDATE, say so *loudly*.
6. **SKEPTIC PASS, post-change:** did the evidence actually *confirm* the fix, or does it just *look right*? State residual risk and the **exact** live test kit must run.
7. **Commit** (format below). **Append a full LOG entry** at the bottom of this file. **Update the queue** item's status.
8. **Stop-and-flag if** confidence < ~50% on an unverifiable change, or the change is risky and can't get past CANDIDATE. Better to hand kit a tight, well-documented batch than pile up untested edits.

## Verification hierarchy — always reach for the highest feasible level
- **A — Tests (strongest, fully autonomous).** `sf-router` go tests; `./stress-test-anticheat.py` fired at a local headless oracle. Reproduce the bug as a *failing* test first when you can, then fix to green.
- **B — Web-UI screenshots (autonomous, safe).** `serve-lobbies.py` lobby browser via the Playwright MCP: navigate `localhost`, exercise switch/copy/refresh, screenshot before+after.
- **C — Headless log checks (autonomous, safe — NO display).** Run the headless oracle locally, fire synthetic packets, grep `BepInEx/LogOutput.log` + the Unity log for expected (or forbidden) lines.
- **D — Single-client in-game screenshot (display-coupled, process-risky).** Launch ONE SF client against a local test oracle, screenshot the in-game browser overlay / map. Only if safe; obey process discipline; label as partial.
- **E — Two-player live (NOT autonomous — cannot be done in the loop).** Do NOT fake it. Write it into the **Punch list for kit** with a precise repro + pass/fail criterion.

## Commit message format
```
<scope>: <one-line summary>  [VERIFIED via A/B/C/D | CANDIDATE — needs E]

Hypothesis: ...
Evidence:   file:line ...
Fix:        ...
UPS:        what this should fix
DOWNS:      what it risks / might break / assumptions it rests on
Confidence: N%
Test:       exact test that confirms (or that kit must run)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

## Queue — seeded from `notes/BUGS_BACKLOG.md`; re-rank as you learn
Status: `TODO` / `WIP` / `CANDIDATE` (fix made, needs kit live test) / `VERIFIED` / `BLOCKED` / `WONTFIX`.

| ID | Area | Status | Summary & pointer |
|----|------|--------|-------------------|
| BOX-1 | boxes | TODO | **P0-23** — server NSOs constantly fall into the void (`Y=-30..-50`, hundreds filtered/min). Backlog calls this *the real root cause of "boxes feel wrong"* — no client fix works while the server drops its own boxes. Diagnostic was staged. Likely `Physics.SyncTransforms()` after additive load, or floor colliders not registered headless. Verify: **C** (headless oracle + filtered-count / collider-summary log). `SFHeadlessHost.cs:~597`. |
| BOX-2 | boxes | STALE | **P0-24 — NOT a live bug (static cross-file trace).** Backlog claimed the round-advance re-arm "never happens." In fact `AdvanceRound`(SFHeadlessHost.cs:3742)→`ResetOracleStateForRoundAdvance`(3783, resets the auth/nso flags + clears rigs)→`ScheduleOracleReloadCurrentMap`(SfMapTerrainHost.cs:1083) which re-arms the FULL cascade incl. `_oracleStartMatchFired=false`(1091). Cascade: StartMatch→CountDown→NSO inventory→`SpawnAuthoritativePlayersForAllClients`. Fixed by `472f447` (2026-05-24). **No code change** — adding a trigger would double-spawn rigs. Runtime confirm → kit punch list. |
| LOBBY-1 | lobby | BLOCKED | Backend switching is **sound** (`go test` green; SELECT/rebind/stale-reresolve all tested). The one switching issue found = the **DOCUMENTED, accepted per-IP limit**: two same-IP players in DIFFERENT lobbies mis-route the non-SELECTing game socket (notes/ROUTER.md:93-96, ROUTER_LIVE_TEST.md:60). Reproduced + pinned as a skipped regression in `colocated_gamesocket_test.go`. Real fix is client-side (out of router scope). **BLOCKED on kit:** is this your symptom (two LOCAL instances, different lobbies)? If not, need the exact repro → Punch list. |
| LOBBY-2 | lobby | TODO | In-game browser UI / switching UX — `sf-server-browser/` (`ServerBrowserScreens.cs`, `LobbyOverlay.cs`). Verify: **D** (single-client overlay screenshot) or **E**. |
| AC-1 | anticheat | TODO | Anti-cheat behavior. Investigate thresholds vs `stress-test-anticheat.py`. RESPECT the log-only / observation-only rules above — confirm calibration, don't enable enforcement. Verify: **A** (run stress test, grep `[anticheat]` warnings). `SFHeadlessHost.cs`. |
| OPEN-1 | boxes/dmg | TODO | Can't die to void. Trace void damage through `ValidateDamagePacket` (void = self-attacker, should pass). Verify: **C** then **E**. |
| OPEN-2 | boxes/dmg | TODO | Lava no damage — same family as OPEN-1. Verify: **C** then **E**. |
| OPEN-3 | boxes/dmg | TODO | Can't hit guns out of players' hands. Trace `PktPlayerForceAddedAndBlock` / damage-type filtering in `SfDispatch`. Verify: **C** then **E**. |
| OPEN-4 | boxes | TODO | Chains randomly break. Likely fixed by P0-11 revert (`4affabc`) — verify it stays fixed. Verify: **C** then **E**. |
| OPEN-5 | boxes | TODO | Ice randomly breaks. Likely fixed by dynamic-NSO revert. Verify: **C** then **E**. |
| OPEN-6 | boxes | TODO | Boxes disappear randomly. Same family as OPEN-5. Verify: **C** then **E**. |

## Punch list for kit — live 2-player tests only kit can run
- **LOBBY-1 — confirm the switching symptom.** Are you testing two LOCAL SF instances (same IP) in DIFFERENT lobbies? If so, "switching doesn't work right" is the *documented* per-IP game-socket limit (notes/ROUTER.md) — workaround: put both in the SAME lobby, or test from distinct IPs; the real fix is client-side (game socket SELECTs, or SELECT registers its port). If the symptom is something else (same lobby, or the in-game browser UI itself), give the exact repro so I can target LOBBY-2.
- **P0-24 — confirm rigs respawn rounds 2+.** 2 clients, start a match, score a kill so the round advances. After the new map loads, watch the oracle heartbeat: `rigs=` should return to the connected-client count (not stay `rigs=0`) and boxes should be pushable again. If `rigs=0 matchStarted=True` persists past round 1, P0-24 is real after all — reopen. (Static trace says the re-arm is intact; this only confirms it at runtime.)

## LOG — append-only, newest at the bottom
### Iteration 1 — BOX-2 / P0-24 — VERDICT: STALE (no code change)  [VERIFIED via static cross-file trace; runtime → kit]
- **Picked because:** backlog flagged it high-value + "tractable ~5-line fix" + headless-verifiable.
- **Hypothesis (backlog):** `AdvanceRound` clears the auth rig but never re-arms `_authSpawnAt`/`_nsoInventoryDone`, so `SpawnAuthoritativePlayersForAllClients` never re-fires → `rigs=0` rounds 2+.
- **Skeptic pass (a) — disproof found:** the re-arm DOES exist. `AdvanceRound`(3742)→`ResetOracleStateForRoundAdvance`(3783) resets `_authSpawnDone`,`_authSpawnAt`,`_nsoInventoryDone`,`_nsoInventoryAt` + clears rigs; then `ScheduleOracleReloadCurrentMap`(SfMapTerrainHost.cs:1083) sets `_oracleStartMatchAt=now+0.5` AND `_oracleStartMatchFired=false`(1091) — the precise guard at SFHeadlessHost.cs:2091 I suspected might stick. Cascade re-fires end-to-end (2091→2106→2116→2127→`SpawnAuthoritativePlayersForAllClients`).
- **Skeptic pass (c) — DOWNS of "fixing" it:** adding any extra spawn trigger would double-spawn authoritative rigs (2 `NetworkPlayer`s/slot) — a real regression. Correct action = NO CHANGE.
- **Evidence:** SFHeadlessHost.cs:2091-2132, 3742-3805; SfMapTerrainHost.cs:1083-1100. Fixed by `472f447` ("Fix Oracle multiplayer round flow", 2026-05-24) — same day the backlog row was written, hence the staleness.
- **Confidence P0-24 is stale:** ~88%. The cited mechanism is definitively false; residual ~12% = runtime timing, or `SpawnAuthoritativePlayersForAllClients` early-returning for some *other* reason — covered by the kit punch-list test.
- **Follow-up:** `notes/BUGS_BACKLOG.md` P0-24 row should move to "fixed" once kit confirms at runtime.

### Iteration 2 (start) — LOBBY-1 — baseline established  [VERIFIED via A: go test]
- **Picked because:** verifiable at level A (router has go tests) → cleanest place to start the lobby work.
- **Baseline:** `go -C sf-router test ./...` → `ok` (router/select/registry all green, 1.18s).
- **Implication:** kit's "switching doesn't work right" is NOT in the *tested* router logic — it's either an untested router transition or the UI layer (`serve-lobbies.py` / `sf-server-browser` → LOBBY-2).
- **Skeptic note:** "tests green" ≠ "no bug" — green only proves *covered* paths. Do NOT close LOBBY-1; treat green as "narrow the search," not "case solved."
- **Next (cron fire):** read `select.go`/`registry.go` for untested switching paths; Playwright-screenshot `serve-lobbies.py`; reproduce the exact symptom as a failing test before fixing.

### Iteration 3 — LOBBY-1 (cont.) — confirmed a DOCUMENTED limit, no fix  [VERIFIED via A: reproducing test]
- **Investigated:** SELECT/rebind path (router.go:263-337) + select.go + the existing co-located test.
- **Found (skeptic a):** the non-SELECTing game socket rides a single per-IP `ipBind` slot (router.go:295, overwritten per SELECT) → two same-IP players in different lobbies hijack the first's game socket. `TestCoLocatedBoundFlowSurvivesOtherSelect` misses it (there both clients SELECT, so both epBind'd).
- **Skeptic b — checked notes/ (HARD RULE) BEFORE changing:** it's the ALREADY-DOCUMENTED accepted limit (notes/ROUTER.md:93-96, ROUTER_LIVE_TEST.md:60, MULTI_LOBBY_LIVE.md:134). Client (`SendSelectLobbyPacket`, SFClientRecon.cs:1871) SELECTs only from the recon socket, exactly as documented. NOT a new bug.
- **Verified:** wrote `colocated_gamesocket_test.go`; router log shows the game flow rebuilt A→B on the co-located SELECT. Then `t.Skip`'d it (asserts post-fix behavior; un-skip when a client-side fix lands) so the suite stays green.
- **Skeptic c — DOWNS of "fixing" in the router:** not cleanly possible — the router can't disambiguate two non-SELECTing game sockets on one IP from (IP,port) alone. A speculative router change would risk the working single-player + co-located-same-lobby paths. Real fix is client-side/protocol.
- **Confidence:** ~95% this is the per-IP limit; whether it's *kit's* symptom is unknown → BLOCKED on his repro (punch list).
- **Net:** +1 pending regression test, no behavior change; backend switching otherwise sound.

