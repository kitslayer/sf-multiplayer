#!/usr/bin/env bash
# Spin up an SF oracle for one lobby. Each lobby is a separate Proton+SF
# process with its own wineprefix, its own UDP port, and its own log file —
# fully isolated, no in-process sharding needed (yet).
#
# Usage:
#   ./launch-lobby.sh                  # auto-generate 4-letter lobby code
#   ./launch-lobby.sh LOBBYCODE        # use specific code
#   ./launch-lobby.sh LOBBYCODE 1338   # use specific code + port
#
# Lobby registry lives in $SF_LOBBIES_DIR (default /tmp/sf-lobbies/). Each
# entry is a single file ${CODE}.conf with key=value lines so stop-lobby.sh
# and list-lobbies.sh can introspect.
set -eu

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REGISTRY="${SF_LOBBIES_DIR:-/tmp/sf-lobbies}"
BASE_PORT="${SF_BASE_PORT:-1337}"
MAX_LOBBIES="${SF_MAX_LOBBIES:-10}"

mkdir -p "$REGISTRY"

# --- Resolve lobby code ---
CODE="${1:-}"
if [ -z "$CODE" ]; then
  CODE=$(tr -dc 'A-Z0-9' < /dev/urandom | head -c4)
  echo "Generated lobby code: $CODE"
fi

if [ -f "$REGISTRY/${CODE}.conf" ]; then
  # Stale? Check pid is alive.
  OLD_PID=$(grep '^pid=' "$REGISTRY/${CODE}.conf" | cut -d= -f2)
  if [ -n "$OLD_PID" ] && kill -0 "$OLD_PID" 2>/dev/null; then
    echo "Lobby $CODE is already running (pid $OLD_PID). Use stop-lobby.sh first." >&2
    exit 1
  fi
  echo "Removing stale registry entry for $CODE (pid $OLD_PID is gone)."
  rm "$REGISTRY/${CODE}.conf"
fi

# Ports we must NOT hand to a lobby backend: the V26 CLIENT (SFClientRecon)
# snapshot listeners, AND the sf-router's public port (1338, see
# deploy/sf-router.service) which sits inside this BASE_PORT pool — a lobby must
# never grab the router's own port (F2). Extend via SF_RESERVED_PORTS.
RESERVED_PORTS="${SF_RESERVED_PORTS:-1338 1339 1340}"

# Atomic registry writer (F6): write a temp then rename so concurrent readers
# (the Go router's 2s refresh, serve-lobbies' loader + reaper) never observe a
# half-written .conf (e.g. code= present but port= not yet). STARTED fixed once.
STARTED="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
write_conf() {  # $1 = pid
  cat > "$REGISTRY/${CODE}.conf.tmp" <<EOF
code=${CODE}
port=${PORT}
bridge=${BRIDGEPORT}
pid=${1}
log=${LOG}
beplog=${BEPLOG}
pluginlog=${PLUGINLOG}
started=${STARTED}
EOF
  mv -f "$REGISTRY/${CODE}.conf.tmp" "$REGISTRY/${CODE}.conf"
}

# --- Resolve + reserve a port atomically (F1) ---
# The old code wrote the .conf only AFTER the ~20s Proton spawn, so two creates
# racing inside that window both saw the port free (ss: not bound yet; registry:
# no .conf yet) and picked the same one — the second backend then failed to bind.
# Fix: hold a registry-wide lock across [port pick + reservation write] so a
# racing create sees the port already claimed. The lock fd is closed BEFORE we
# spawn the long-lived game, so the child never inherits (and pins) the lock.
exec 9>"$REGISTRY/.launch.lock"
flock 9
PORT="${2:-}"
if [ -z "$PORT" ]; then
  for try in $(seq 0 $((MAX_LOBBIES - 1))); do
    cand=$((BASE_PORT + try))
    # Skip if reserved for local v26 client listeners / the router port
    skip=0
    for rp in $RESERVED_PORTS; do
      if [ "$cand" = "$rp" ]; then skip=1; break; fi
    done
    [ "$skip" = "1" ] && continue
    # Skip if a LIVE registry entry holds it. A stale conf left by a crashed
    # lobby (dead pid) must NOT block reuse — otherwise crashes permanently
    # shrink the port pool whenever the reaper is down. (The ss check below still
    # guards a port that's genuinely bound; pid=static = systemd MAIN, always held.)
    held=0
    for cf in "$REGISTRY"/*.conf; do
      grep -qs "^port=${cand}$" "$cf" 2>/dev/null || continue
      cpid=$(sed -n 's/^pid=//p' "$cf" | head -1)
      if [ "$cpid" = "static" ] || { [ -n "$cpid" ] && kill -0 "$cpid" 2>/dev/null; }; then held=1; break; fi
    done
    [ "$held" = "1" ] && continue
    # Skip if anything else on the system holds it
    if ss -lunH "sport = :${cand}" 2>/dev/null | grep -q .; then
      continue
    fi
    PORT=$cand
    break
  done
fi
if [ -z "$PORT" ]; then
  flock -u 9; exec 9>&-
  echo "No free port in range ${BASE_PORT}-$((BASE_PORT + MAX_LOBBIES - 1))." >&2
  exit 1
fi

BRIDGEPORT=$((PORT + 10000))  # 11337+ — separate from anything in 1000-9999
LOG="/tmp/sf-oracle-unity-${BRIDGEPORT}.log"
BEPLOG="$HOME/sf-mirror-local/BepInEx/LogOutput.log"
# Phase 6.22 — per-lobby plugin log (truncate stale before launch).
# The plugin tees its BepInEx output here so multiple oracles sharing the
# same install don't trample each other in the shared LogOutput.log.
PLUGINLOG="/tmp/sf-oracle-plugin-${BRIDGEPORT}.log"
rm -f "$PLUGINLOG"

# Reserve the port in the registry NOW (pid = this script's pid, a live
# placeholder) so a concurrent create skips it; then release the lock + close
# the fd before spawning so the game process doesn't inherit the lock.
write_conf "$$"
flock -u 9
exec 9>&-

# Which headless launcher to use. On a SERVER (bundled Proton under
# ~/sf-oracle) use deploy/start-oracle-server.sh — it wraps Proton in xvfb-run
# and gives each bridge its own prefix (the recipe the live oracle uses). On a
# dev laptop (Steam Proton + sf-mirror-local) use launch-sf-headless.sh.
# Override explicitly with SFHEADLESS_LAUNCHER.
if [ -n "${SFHEADLESS_LAUNCHER:-}" ]; then
  LAUNCHER="$SFHEADLESS_LAUNCHER"
elif [ -x "${SF_ORACLE_ROOT:-$HOME/sf-oracle}/proton/proton" ]; then
  LAUNCHER="$REPO_DIR/deploy/start-oracle-server.sh"
else
  LAUNCHER="$REPO_DIR/launch-sf-headless.sh"
fi

echo "Starting lobby '$CODE' on UDP $PORT (bridge $BRIDGEPORT) via $(basename "$LAUNCHER")..."
SFHEADLESS_PORT="$PORT" \
SFHEADLESS_BRIDGEPORT="$BRIDGEPORT" \
SFHEADLESS_DEBUG=1 \
SF_LOBBY_CODE="$CODE" \
SFHEADLESS_LOGFILE="$PLUGINLOG" \
  nohup bash "$LAUNCHER" >/dev/null 2>&1 &
PID=$!
disown

# Update the reservation with the real backend pid (atomic).
write_conf "$PID"

# Brief sanity-wait so we can fail fast if Proton refused to boot.
for i in $(seq 1 30); do
  if ss -lunH "sport = :${PORT}" 2>/dev/null | grep -q .; then
    echo "Lobby '$CODE' READY → connect: -address <server-ip> -port $PORT"
    echo "  pid:        $PID"
    echo "  unity log:  $LOG"
    echo "  plugin log: $PLUGINLOG  (per-lobby; tee from BepInEx)"
    echo "  bepinex log: $BEPLOG    (shared across all lobbies on this install)"
    exit 0
  fi
  sleep 1
done

echo "Lobby '$CODE' did NOT bind port $PORT within 30s." >&2
echo "  pid=$PID — check $LOG for clues, then stop-lobby.sh $CODE if needed." >&2
exit 1
