#!/usr/bin/env bash
# Stop every lobby in the registry. Wrapper around stop-lobby.sh per entry.
# Also kills any orphaned StickFight.exe processes the registry didn't track
# (e.g. ones started manually via launch-sf-headless.sh).
#
# Usage: ./stop-all-lobbies.sh
set -u

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REGISTRY="${SF_LOBBIES_DIR:-/tmp/sf-lobbies}"

SWEEP_ORPHANS=0
[ "${1:-}" = "--orphans" ] && SWEEP_ORPHANS=1

if [ -d "$REGISTRY" ]; then
  for conf in "$REGISTRY"/*.conf; do
    [ -f "$conf" ] || continue
    # Never stop a static (systemd-managed) lobby like MAIN: stop-lobby.sh would
    # kill the systemd oracle AND rm -rf its live wineprefix. systemd owns it.
    if grep -qs '^static=true$' "$conf"; then
      echo "Skipping static lobby $(basename "$conf" .conf) (systemd-managed)."
      continue
    fi
    code=$(basename "$conf" .conf)
    bash "$REPO_DIR/stop-lobby.sh" "$code" || true
  done
fi

# Orphan sweep (opt-in: --orphans). Kills leftover headless SF processes the
# registry didn't track (manual launch-sf-headless.sh runs, crash debris). OFF by
# default because the broad pgrep ALSO matches the systemd MAIN oracle's
# StickFight.exe (a child of the xvfb wrapper, not the MAIN.conf pid) and would
# bounce the front-door server. Pass --orphans only when you mean it.
if [ "$SWEEP_ORPHANS" = "1" ]; then
  PIDS=$(pgrep -f "StickFight.exe.*-batchmode" || true)
  if [ -n "$PIDS" ]; then
    echo "Found headless SF processes: $PIDS — killing (--orphans)."
    echo "$PIDS" | xargs -r kill 2>/dev/null || true
    sleep 2
    echo "$PIDS" | xargs -r kill -9 2>/dev/null || true
  fi
else
  echo "(orphan SF-process sweep skipped; pass --orphans to also kill untracked headless instances)"
fi

echo "All lobbies stopped."
