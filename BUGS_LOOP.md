# SF polish loop — queue, protocol & evidence log

**Branch:** `loop/polish-2026-06-22`. This file is the loop's single source of truth and is designed to survive context resets. **Read it in full at the start of every iteration.**

## Mission
Aggressive full-fix pass on three problem areas kit flagged as broken: **(1)** lobby browser + lobby-switching, **(2)** server-authoritative boxes, **(3)** anti-cheat. Attempt real end-to-end fixes — *including* parts that ultimately need kit's live 2-player test — but always be honest about which level of verification each fix actually reached.

## HARD RULES — never violate
- **KEEP the quick-draw / pickup-instant-shot behavior** (pickup with no cooldown + no recoil). It is *wanted* in the comp scene. Do NOT "fix" it, ever.
- **The anti-cheat low-damage-kill kick is intentionally log-only** (gated behind `SF_AC_KICK=1`). Anticheat is observation-only by default (`SF_ANTICHEAT_ENFORCE=1` flips it) — that is **by design** (backlog P2-6). Do NOT "re-enable" either as if it were a bug.
- **Stay on this branch.** Do NOT push, do NOT deploy to `.115`, do NOT touch `main`, do NOT touch any live infra. Merges to main happen only after kit live-verifies.
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
| BOX-2 | boxes | TODO | **P0-24** — auth player rig destroyed on `AdvanceRound`, never re-spawned for rounds 2+ (`rigs=0` after round 1). Backlog: ~5-line fix — re-arm `_authSpawnAt` / `_nsoInventoryDone` chain after the "cleared N rig(s)" line. Verify: **C** (headless oracle, advance a round, grep heartbeat `rigs=`). High value + tractable — **good first item.** |
| LOBBY-1 | lobby | TODO | Lobby-switching backend. Suspect `sf-router/` (Go, has 4 test files) selection/registry logic + `serve-lobbies.py`. Reproduce switching bug as a failing go test if possible. Verify: **A** + **B**. |
| LOBBY-2 | lobby | TODO | In-game browser UI / switching UX — `sf-server-browser/` (`ServerBrowserScreens.cs`, `LobbyOverlay.cs`). Verify: **D** (single-client overlay screenshot) or **E**. |
| AC-1 | anticheat | TODO | Anti-cheat behavior. Investigate thresholds vs `stress-test-anticheat.py`. RESPECT the log-only / observation-only rules above — confirm calibration, don't enable enforcement. Verify: **A** (run stress test, grep `[anticheat]` warnings). `SFHeadlessHost.cs`. |
| OPEN-1 | boxes/dmg | TODO | Can't die to void. Trace void damage through `ValidateDamagePacket` (void = self-attacker, should pass). Verify: **C** then **E**. |
| OPEN-2 | boxes/dmg | TODO | Lava no damage — same family as OPEN-1. Verify: **C** then **E**. |
| OPEN-3 | boxes/dmg | TODO | Can't hit guns out of players' hands. Trace `PktPlayerForceAddedAndBlock` / damage-type filtering in `SfDispatch`. Verify: **C** then **E**. |
| OPEN-4 | boxes | TODO | Chains randomly break. Likely fixed by P0-11 revert (`4affabc`) — verify it stays fixed. Verify: **C** then **E**. |
| OPEN-5 | boxes | TODO | Ice randomly breaks. Likely fixed by dynamic-NSO revert. Verify: **C** then **E**. |
| OPEN-6 | boxes | TODO | Boxes disappear randomly. Same family as OPEN-5. Verify: **C** then **E**. |

## Punch list for kit — live 2-player tests only kit can run
_(The loop appends precise repro + pass/fail criteria here as it produces CANDIDATE fixes. Empty until the first CANDIDATE lands.)_

## LOG — append-only, newest at the bottom
_(One entry per iteration: ID, hypothesis, skeptic pass, fix, verification level + evidence path, confidence, commit hash.)_
