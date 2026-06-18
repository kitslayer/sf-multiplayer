#!/usr/bin/env bash
# Register (or unregister) the systemd oracle in the lobby registry so the
# sf-router routes its lobby code to it. The oracle is a long-lived systemd
# service (auto-restart), unlike launch-lobby.sh lobbies, so it isn't written
# into /tmp/sf-lobbies by launch-lobby.sh — this helper does it instead, wired
# into the unit's ExecStartPost (register) / ExecStopPost (unregister) so MAIN
# survives an oracle restart (fresh MainPID) and disappears cleanly on stop.
#
# Usage: register-main-lobby.sh [register|unregister]
# Env (from the sf-oracle unit): SF_LOBBY_CODE, SFHEADLESS_PORT,
#   SFHEADLESS_BRIDGEPORT, SF_LOBBIES_DIR; MAINPID is set by systemd.
set -eu

ACTION="${1:-register}"
DIR="${SF_LOBBIES_DIR:-/tmp/sf-lobbies}"
CODE="${SF_LOBBY_CODE:-MAIN}"
CONF="$DIR/${CODE}.conf"

mkdir -p "$DIR"

if [ "$ACTION" = "unregister" ]; then
  rm -f "$CONF"
  exit 0
fi

# systemd exports MAINPID to ExecStartPost; fall back to our own pid otherwise.
PID="${MAINPID:-$$}"
# Atomic write (F6): temp + rename so the Go router (2s refresh) and serve-lobbies
# loader never observe a half-written MAIN.conf during (re)registration.
cat > "$CONF.tmp" <<EOF
code=${CODE}
port=${SFHEADLESS_PORT:-1337}
bridge=${SFHEADLESS_BRIDGEPORT:-11337}
pid=${PID}
started=$(date -u +%Y-%m-%dT%H:%M:%SZ)
EOF
mv -f "$CONF.tmp" "$CONF"
