#!/usr/bin/env bash
# Stop one lobby by code. Reads $REGISTRY/CODE.conf, kills the pid, and
# removes the entry. Idempotent: a stale entry (pid already gone) is just
# cleaned up.
#
# Usage: ./stop-lobby.sh LOBBYCODE
set -eu

REGISTRY="${SF_LOBBIES_DIR:-/tmp/sf-lobbies}"
CODE="${1:?Usage: stop-lobby.sh LOBBYCODE}"
CONF="$REGISTRY/${CODE}.conf"

if [ ! -f "$CONF" ]; then
  echo "No registry entry for lobby '$CODE'." >&2
  exit 1
fi

# Never stop a static (systemd-managed) lobby like MAIN: doing so would kill the
# always-on oracle AND rm -rf its live wineprefix below. The reaper and
# stop-all-lobbies.sh refuse static lobbies; enforce it here too so EVERY caller
# (including the HTTP /lobbies/stop control endpoint) is covered.
if grep -qs '^static=true$' "$CONF"; then
  echo "Refusing to stop static (systemd-managed) lobby '$CODE'." >&2
  exit 1
fi

PID=$(grep '^pid=' "$CONF" | cut -d= -f2)
PORT=$(grep '^port=' "$CONF" | cut -d= -f2)

BRIDGE=$(grep '^bridge=' "$CONF" | cut -d= -f2)
# Guard (issue #5): pid and bridge must be numeric. A blank/garbage bridge would
# make the pkill patterns below ("...sf-oracle-unity-${BRIDGE}.log") misfire.
# Blank a non-numeric value so the `-n` guards below skip that kill rather than
# match unintended processes.
case "$PID"    in ''|*[!0-9]*) PID="" ;;    esac
case "$BRIDGE" in ''|*[!0-9]*) BRIDGE="" ;; esac
echo "Stopping lobby '$CODE' (pid ${PID:-?}, port ${PORT:-?}, bridge ${BRIDGE:-?})..."

# If the recorded launcher pid is still alive, reap its descendants too (Proton
# spawns srt-bwrap → pv-adverb → python → steam.exe → StickFight.exe).
if [ -n "$PID" ] && kill -0 "$PID" 2>/dev/null; then
  pkill -P "$PID" 2>/dev/null || true
  kill "$PID" 2>/dev/null || true
fi

# ALWAYS kill the StickFight.exe by its UNIQUE bridge-port logfile — do NOT gate
# this on the launcher pid being alive. Proton's wrapper often exits once it has
# spawned the game, leaving the recorded pid dead while the game runs on; the old
# code took the "not running" branch and orphaned a live (noisy) instance. The
# bridge logfile is unique per lobby, so this NEVER hits a player/graphical SF
# (different -logFile) — never pkill by "-port $PORT" (players share it).
if [ -n "$BRIDGE" ]; then
  pkill -f "StickFight.exe.*-logFile.*sf-oracle-unity-${BRIDGE}\.log" 2>/dev/null || true
  for i in 1 2 3 4 5; do
    pgrep -f "sf-oracle-unity-${BRIDGE}\.log" >/dev/null 2>&1 || break
    sleep 1
  done
  pkill -9 -f "StickFight.exe.*-logFile.*sf-oracle-unity-${BRIDGE}\.log" 2>/dev/null || true
fi

# Reclaim the per-lobby wineprefix. On a dev laptop these live in /tmp, which is
# tmpfs (RAM-backed) — leaving them after a stop LEAKS RAM (~300MB each). On a
# server they're on disk under $SF_ORACLE_ROOT. Removing means a same-bridge
# relaunch is cold again (cheap). Covers both launcher layouts.
if [ -n "$BRIDGE" ]; then
  rm -rf "/tmp/sf-oracle-prefix-${BRIDGE}" "${SF_ORACLE_ROOT:-$HOME/sf-oracle}/prefix-${BRIDGE}" 2>/dev/null || true
fi

rm -f "$CONF"
echo "Lobby '$CODE' stopped."
