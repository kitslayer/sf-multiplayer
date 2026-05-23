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

# --- Resolve port ---
PORT="${2:-}"
if [ -z "$PORT" ]; then
  for try in $(seq 0 $((MAX_LOBBIES - 1))); do
    cand=$((BASE_PORT + try))
    # Skip if in our registry
    if ls "$REGISTRY"/*.conf 2>/dev/null | xargs -r grep -l "^port=${cand}$" >/dev/null; then
      continue
    fi
    # Skip if anything else on the system holds it
    if ss -lunH "sport = :${cand}" 2>/dev/null | grep -q .; then
      continue
    fi
    PORT=$cand
    break
  done
fi
if [ -z "$PORT" ]; then
  echo "No free port in range ${BASE_PORT}-$((BASE_PORT + MAX_LOBBIES - 1))." >&2
  exit 1
fi

BRIDGEPORT=$((PORT + 10000))  # 11337+ — separate from anything in 1000-9999
LOG="/tmp/sf-oracle-unity-${BRIDGEPORT}.log"
BEPLOG="$HOME/sf-mirror-local/BepInEx/LogOutput.log"

echo "Starting lobby '$CODE' on UDP $PORT (bridge $BRIDGEPORT)..."
SFHEADLESS_PORT="$PORT" \
SFHEADLESS_BRIDGEPORT="$BRIDGEPORT" \
SFHEADLESS_DEBUG=1 \
  nohup bash "$REPO_DIR/launch-sf-headless.sh" >/dev/null 2>&1 &
PID=$!
disown

# Record before waiting so stop-lobby can find us even if startup hangs.
cat > "$REGISTRY/${CODE}.conf" <<EOF
code=${CODE}
port=${PORT}
bridge=${BRIDGEPORT}
pid=${PID}
log=${LOG}
beplog=${BEPLOG}
started=$(date -u +%Y-%m-%dT%H:%M:%SZ)
EOF

# Brief sanity-wait so we can fail fast if Proton refused to boot.
for i in $(seq 1 30); do
  if ss -lunH "sport = :${PORT}" 2>/dev/null | grep -q .; then
    echo "Lobby '$CODE' READY → connect: -address <server-ip> -port $PORT"
    echo "  pid:    $PID"
    echo "  log:    $LOG"
    echo "  bepinex: $BEPLOG"
    exit 0
  fi
  sleep 1
done

echo "Lobby '$CODE' did NOT bind port $PORT within 30s." >&2
echo "  pid=$PID — check $LOG for clues, then stop-lobby.sh $CODE if needed." >&2
exit 1
