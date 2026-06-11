#!/usr/bin/env bash
# sf-oracle-watchdog.sh — restart the oracle when its UDP port goes DEAF.
#
# systemd's Restart= and the RuntimeMaxSec drop-in both rely on the process
# EXITING. On 2026-06-10 the oracle crashed and then WEDGED: the process stayed
# alive (so systemd saw "active") with the game loop dead and the UDP socket
# bound-but-unanswering. Nothing recovered it for hours. This watchdog probes
# the actual game protocol (healthcheck.py sends a v25 Ping, expects a reply)
# and restarts the unit when the port stops answering despite the unit being
# "active".
#
# Wired via sf-oracle-watchdog.timer (every 2 min). Idempotent, no persistent
# state. Logs to the journal (run by systemd).
set -u

UNIT="${SF_ORACLE_UNIT:-sf-oracle.service}"
HOST="${SF_ORACLE_HEALTH_HOST:-127.0.0.1}"
PORT="${SFHEADLESS_PORT:-1337}"
HEALTHCHECK="${SF_HEALTHCHECK:-/home/miles/sf-multiplayer/healthcheck.py}"
GRACE_SEC="${SF_ORACLE_WARMUP_SEC:-120}"   # don't probe during normal spin-up
PROBE_TRIES="${SF_ORACLE_PROBE_TRIES:-3}"
PROBE_GAP_SEC="${SF_ORACLE_PROBE_GAP:-5}"
PROBE_TIMEOUT="${SF_ORACLE_PROBE_TIMEOUT:-3}"

log() { echo "[sf-oracle-watchdog] $*"; }

# Only police a unit systemd believes is up. If it's activating/failed, leave
# systemd's own Restart= to handle it — don't fight the normal restart cycle.
state="$(systemctl is-active "$UNIT" 2>/dev/null)"
if [ "$state" != "active" ]; then
    log "unit is '$state' (not active) — deferring to systemd Restart=, no action."
    exit 0
fi

# Give a freshly-(re)started oracle time to load Wine + Unity + the scene
# before we expect it to answer Pings, or we'd restart-loop it during spin-up.
# ActiveEnterTimestampMonotonic is microseconds-since-boot; /proc/uptime is
# seconds-since-boot (world-readable). Both are monotonic, so the difference
# is the unit's active age, immune to wall-clock/NTP jumps.
ts_us="$(systemctl show "$UNIT" -p ActiveEnterTimestampMonotonic --value 2>/dev/null)"
up_sec="$(awk '{print int($1)}' /proc/uptime 2>/dev/null)"
if [ -n "$ts_us" ] && [ "$ts_us" != "0" ] && [ -n "$up_sec" ]; then
    age_sec=$(( up_sec - ts_us / 1000000 ))
    if [ "$age_sec" -ge 0 ] && [ "$age_sec" -lt "$GRACE_SEC" ]; then
        log "unit active only ${age_sec}s (< ${GRACE_SEC}s warm-up) — skipping probe."
        exit 0
    fi
fi

# Probe a few times; any single success means healthy.
i=0
while [ "$i" -lt "$PROBE_TRIES" ]; do
    if python3 "$HEALTHCHECK" --host "$HOST" --port "$PORT" --timeout "$PROBE_TIMEOUT" >/dev/null 2>&1; then
        exit 0
    fi
    i=$((i+1))
    [ "$i" -lt "$PROBE_TRIES" ] && sleep "$PROBE_GAP_SEC"
done

log "oracle ACTIVE but UDP $HOST:$PORT deaf after ${PROBE_TRIES} probes — restarting $UNIT."
systemctl restart "$UNIT"
exit 0
