# Bug investigations

Permanent home for **investigative theories** about bugs we're tracking down — root-cause analyses, evidence, and proposed fixes. Distinct from:

- [`BUGS_BACKLOG.md`](../BUGS_BACKLOG.md) — short-form list of known issues
- [`CRASH_INVESTIGATION.md`](../CRASH_INVESTIGATION.md) — long-form analysis of the native PhysX-joint crash specifically
- Per-session handoff docs (e.g. [`SESSION_2026-05-23.md`](../SESSION_2026-05-23.md)) — what happened in a working session

This folder is for **deep, multi-system investigations**. One file per investigation. Frozen after fix lands — they become historical record.

## Files

| Filename | Status | Subject |
|---|---|---|
| [`2026-05-24_v0.3.4-session-bugs.md`](2026-05-24_v0.3.4-session-bugs.md) | Mostly fixed; F open | Five bugs surfaced from the first 2-player live session on v0.3.4 (Mono iterator landmine, NSO snapshot collapse, mono.dll JIT crash, Awake postfix dead, Update NRE, box physics divergence). Bug F's documented client-side fix turned out to be downstream of the OPEN-A server-floor blocker — see the session-2 followup. |
| [`2026-05-24_session2-match-flow-and-box-blockers.md`](2026-05-24_session2-match-flow-and-box-blockers.md) | Match-flow shipped; box blockers OPEN | Evening session — Mono 2.0 `Monitor.Enter`/`Array.Empty<T>()` landmines, ALKA's silent 12s `RoundMinPlaySec` gate, missing lobby-kill auto-start. Plus two real open blockers turned up in the runtime logs: server NSOs falling into the void (P0-23) and auth rigs not re-spawned after round-advance (P0-24). |
| [`2026-05-24_missing-vanilla-mechanisms.md`](2026-05-24_missing-vanilla-mechanisms.md) | Reference | Vanilla SF mechanisms (`RigidBodyIndexHolder + SendAddedForce`, `IgnorePlayerWhenOffScreen`, level-editor TAG) that the oracle never implements. Read this **before** trying to write client-side box-physics patches — it disambiguates which "force-sync" mechanism does what (knockback vs box-push). |
| [`2026-05-24_vanilla-ground-truth.md`](2026-05-24_vanilla-ground-truth.md) | Reference | Unity Explorer crate component dump (Crate2(6) vs Crate(3)) from a vanilla-SF inspection session. |
| [`2026-05-24_OPEN-3_chains_break_root_cause.md`](2026-05-24_OPEN-3_chains_break_root_cause.md) | Investigating | `ApplyExplosiveBlastAt`'s `networkForce=true` hides a desync; chain breaks via this path don't broadcast. |
| [`2026-05-24_door-and-local-physics-sync.md`](2026-05-24_door-and-local-physics-sync.md) | Design | Door / swing-chain impulse-sync design (follow-up to the chains investigation). |

## When to add a new file here

- A bug that requires reading >100 lines of code or multiple files
- A bug with multiple plausible hypotheses worth documenting
- A bug spanning client + server + protocol layers
- A crash whose root cause needs reverse engineering
- An "X feels wrong" symptom that hides a clear logic bug

If it's a one-line fix you can describe in a commit message, **don't put it here** — just fix it. This is for cases where the analysis is the artifact.

## File naming

```
YYYY-MM-DD_<short-slug>.md
```

Example: `2026-05-24_v0.3.4-session-bugs.md`.

## Template

```markdown
# <Title> — investigation (YYYY-MM-DD)

> Status: **investigating** | **partial fix** | **fixed in <commit>** | **superseded by <file>**

## Symptom

What the user / log / monitoring saw. Reproducible? When? On which build?

## Evidence

Log excerpts, screenshots, repro commands, file:line references. Quote verbatim
where possible — paraphrasing loses information.

## Hypotheses considered

1. **<short name>** — what it was, why it seemed plausible, confidence level
2. **<short name>** — ...
3. **<short name>** — ...

## Root cause (or current best guess)

The story: what's actually happening, in plain English. Include file:line refs.

## Why we missed it earlier

Optional but useful — what assumption was wrong, what test would have caught it.

## Fix sketch

What needs to change. Code outline, not actual code. Risks of the fix.

## Open questions

What we still don't know. What would resolve each open question.
```

## How to update an existing investigation

If new evidence arrives, append a dated section to the file. Don't rewrite history — investigations are most valuable when you can see the reasoning evolve.

```markdown
## Update YYYY-MM-DD

New evidence: <...>
This changes our best guess from <X> to <Y> because <reason>.
```

When the bug is fixed:
1. Update Status header to `**fixed in <commit-hash>**`
2. Add a "Resolution" section quoting the fix
3. Don't delete the file — it's the historical record
