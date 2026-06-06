#!/usr/bin/env bash
# check-mono2-landmines.sh — guard against C# patterns that crash under the
# game's ancient Mono 2.0 runtime.
#
# The BepInEx plugins are compiled by a MODERN C# compiler but RUN inside Stick
# Fight's Mono 2.0, where several modern lowerings emit calls to methods that
# don't exist — producing a silent feature-kill or a hard crash-loop. Known
# landmines this project has already been bitten by:
#
#   lock { ... }              -> lowers to 2-arg Monitor.Enter (MissingMethodException) [P0-19]
#   Array.Empty<T>()          -> method absent in Mono 2.0                              [P0-20]
#   IEnumerable<T> + yield     -> iterator emits Environment.CurrentManagedThreadId
#                                (absent) -> silently kills the feature                 [Bug A]
#   Type/MethodInfo == null    -> no op_Equality in Mono 2.0; cast through (object)     [P0-18]
#
# Safe replacements: Monitor.Enter(o)+try/finally ; new T[0] ; eager List<T> ;
# (object)methodInfo == null.
#
# Exit 1 if any HARD-FAIL pattern is found in *code* (matches inside // comments
# are ignored). Wire this into the build script or a pre-commit hook. REVIEW
# hits are printed but do not fail the run.
set -u

cd "$(dirname "$0")/.." || exit 2

# Source roots that run under the game's Mono 2.0 (exclude build output + refs).
ROOTS=(sf-headless-host sf-client-recon sf-server-browser sf-box-fix sf-leveldumper)
GREP_OPTS=(--include='*.cs' --exclude-dir=bin --exclude-dir=obj --exclude-dir=refs)

# Each pattern is prefixed with ^(?:(?!//).)*  so the keyword only matches when
# NOT preceded by a // comment on that line. A trailing filter drops block-comment
# continuation lines (content starting with *). Requires grep -P (PCRE).
NC='^(?:(?!//).)*'   # "no comment before"
fail=0

scan() { # usage: scan FAIL|WARN "label" "pcre-after-NC-prefix"
  local sev="$1" label="$2" pat="$3" hits
  hits=$(grep -rnP "${GREP_OPTS[@]}" "${NC}${pat}" "${ROOTS[@]}" 2>/dev/null \
         | grep -vE '^[^:]+:[0-9]+:[[:space:]]*\*' || true)
  [ -z "$hits" ] && return 0
  if [ "$sev" = FAIL ]; then
    printf '\033[31m✗ MONO2 LANDMINE\033[0m — %s\n' "$label"; fail=1
  else
    printf '\033[33m⚠ REVIEW\033[0m — %s\n' "$label"
  fi
  printf '%s\n\n' "$hits" | sed 's/^/    /'
}

# ---- HARD FAILS (unambiguous Mono-2.0 landmines) ----
scan FAIL "C# 'lock (...)' — use Monitor.Enter(o) + try/finally" '(?<![\w.])lock\s*\('
scan FAIL "Array.Empty<T>() — use new T[0]"                       '\bArray\.Empty\s*<'
scan FAIL "Environment.CurrentManagedThreadId — absent in Mono 2.0" '\bCurrentManagedThreadId\b'

# ---- REVIEW (often a landmine; confirm by hand) ----
scan WARN "method returns IEnumerable<...> (yield-iterator emits CurrentManagedThreadId — return IEnumerator or an eager List)" \
  '\b(?:public|private|internal|protected|static)[^=;(]*\bIEnumerable\s*<'
scan WARN "reflection result compared to null without (object) cast (Mono 2.0 lacks op_Equality)" \
  '\b(?:MethodInfo|FieldInfo|PropertyInfo|MemberInfo|ConstructorInfo|Type)\s+\w+\s*(?:==|!=)\s*null'

if [ "$fail" -ne 0 ]; then
  printf '\033[31mFAIL\033[0m: Mono-2.0 landmine(s) above. Fix before building the oracle.\n'
  exit 1
fi
printf '\033[32m✓\033[0m no Mono-2.0 hard-fail landmines in plugin source.\n'
exit 0
