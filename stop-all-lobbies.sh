#!/usr/bin/env bash
# Stop every lobby in the registry. Wrapper around stop-lobby.sh per entry.
# Also kills any orphaned StickFight.exe processes the registry didn't track
# (e.g. ones started manually via launch-sf-headless.sh).
#
# Usage: ./stop-all-lobbies.sh
set -u

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REGISTRY="${SF_LOBBIES_DIR:-/tmp/sf-lobbies}"

if [ -d "$REGISTRY" ]; then
  for conf in "$REGISTRY"/*.conf; do
    [ -f "$conf" ] || continue
    code=$(basename "$conf" .conf)
    bash "$REPO_DIR/stop-lobby.sh" "$code" || true
  done
fi

# Sweep: any leftover headless SF processes (manual launches, crash debris).
PIDS=$(pgrep -f "StickFight.exe.*-batchmode" || true)
if [ -n "$PIDS" ]; then
  echo "Found untracked headless SF processes: $PIDS — killing."
  echo "$PIDS" | xargs -r kill 2>/dev/null || true
  sleep 2
  echo "$PIDS" | xargs -r kill -9 2>/dev/null || true
fi

echo "All lobbies stopped."
