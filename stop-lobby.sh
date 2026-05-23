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

PID=$(grep '^pid=' "$CONF" | cut -d= -f2)
PORT=$(grep '^port=' "$CONF" | cut -d= -f2)

if [ -n "$PID" ] && kill -0 "$PID" 2>/dev/null; then
  # Find every descendant of $PID (Proton spawns srt-bwrap → pv-adverb →
  # python → steam.exe → StickFight.exe) and kill the tree.
  echo "Stopping lobby '$CODE' (pid $PID, port $PORT)..."
  pkill -P "$PID" 2>/dev/null || true
  # Match the StickFight.exe specifically by its bridge-port logfile,
  # which is unique per lobby (legacy variable BRIDGEPORT is captured at
  # launch in the registry as 'bridge'). NEVER pkill by "-port $PORT"
  # because player SF instances are launched with the same -port and we
  # don't want to murder them.
  BRIDGE=$(grep '^bridge=' "$CONF" | cut -d= -f2)
  if [ -n "$BRIDGE" ]; then
    pkill -f "StickFight.exe.*-logFile.*sf-oracle-unity-${BRIDGE}\.log" 2>/dev/null || true
  fi
  kill "$PID" 2>/dev/null || true
  # Wait briefly for Proton to tear down.
  for i in 1 2 3 4 5; do
    kill -0 "$PID" 2>/dev/null || break
    sleep 1
  done
  kill -9 "$PID" 2>/dev/null || true
else
  echo "Lobby '$CODE' was not running (pid $PID dead); cleaning up registry."
fi

rm "$CONF"
echo "Lobby '$CODE' stopped."
