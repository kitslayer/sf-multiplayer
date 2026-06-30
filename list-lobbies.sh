#!/usr/bin/env bash
# List running lobbies. Each entry comes from $SF_LOBBIES_DIR/CODE.conf;
# the pid is checked for liveness and stale entries are flagged.
#
# Usage: ./list-lobbies.sh
set -u

REGISTRY="${SF_LOBBIES_DIR:-/tmp/sf-lobbies}"

if [ ! -d "$REGISTRY" ] || [ -z "$(ls -A "$REGISTRY" 2>/dev/null)" ]; then
  echo "No lobbies running."
  exit 0
fi

printf "%-8s %-6s %-8s %-8s %-21s %s\n" "CODE" "PORT" "PID" "STATE" "STARTED" "LOG"
printf "%-8s %-6s %-8s %-8s %-21s %s\n" "----" "----" "---" "-----" "-------" "---"
for conf in "$REGISTRY"/*.conf; do
  [ -f "$conf" ] || continue
  code=$(grep '^code='    "$conf" | cut -d= -f2)
  port=$(grep '^port='    "$conf" | cut -d= -f2)
  pid=$(grep '^pid='      "$conf" | cut -d= -f2)
  log=$(grep '^log='      "$conf" | cut -d= -f2)
  started=$(grep '^started=' "$conf" | cut -d= -f2-)
  if [ "$pid" = "static" ]; then
    state="STATIC"
  elif kill -0 "$pid" 2>/dev/null; then
    state="UP"
  else
    state="STALE"
  fi
  printf "%-8s %-6s %-8s %-8s %-21s %s\n" "$code" "$port" "$pid" "$state" "$started" "$log"
done
