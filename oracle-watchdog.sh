#!/usr/bin/env bash
# ============================================================================
#  oracle-watchdog.sh — keep the ALKA oracle stack alive.
#
#  The oracle is three pieces, all launched with nohup and NO supervisor, so a
#  crash (e.g. a headless SF segfault mid-match) leaves the whole thing down and
#  nobody can connect ("no me deja entrar al server"). This watchdog checks each
#  piece every few seconds and restarts whatever died:
#
#    1. sf-router        — single public UDP port (1337)
#    2. serve-lobbies.py — lobby HTTP/JSON (8080)
#    3. MAIN lobby       — the always-on headless SF match (launch-lobby.sh)
#
#  Run it ONCE to bring everything up AND keep it up:
#      nohup bash oracle-watchdog.sh >/tmp/oracle-watchdog.log 2>&1 &
#  or install it as a systemd service (template printed with --systemd).
#
#  Env:
#    SF_ROUTER_PORT   public UDP port           (default 1337)
#    SF_LOBBY_PORT    serve-lobbies HTTP port   (default 8080)
#    SF_MAIN_CODE     always-on lobby code      (default MAIN)
#    SF_MAIN_PORT     always-on lobby UDP port  (default 1338)
#    WATCH_INTERVAL   seconds between checks     (default 10)
# ============================================================================
set -u
REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROUTER_PORT="${SF_ROUTER_PORT:-1337}"
LOBBY_PORT="${SF_LOBBY_PORT:-8080}"
MAIN_CODE="${SF_MAIN_CODE:-MAIN}"
MAIN_PORT="${SF_MAIN_PORT:-1338}"
INTERVAL="${WATCH_INTERVAL:-10}"
REGISTRY="${SF_LOBBIES_DIR:-/tmp/sf-lobbies}"
LOGDIR="${SF_WATCHDOG_LOGDIR:-/tmp}"
PY="$(command -v python3 || command -v python)"

log() { echo "[$(date '+%F %T')] $*"; }

# --- systemd template -------------------------------------------------------
if [ "${1:-}" = "--systemd" ]; then
  cat <<UNIT
# Save as /etc/systemd/system/alka-oracle.service, then:
#   sudo systemctl daemon-reload && sudo systemctl enable --now alka-oracle
[Unit]
Description=ALKA Stick Fight oracle watchdog
After=network-online.target
Wants=network-online.target
[Service]
Type=simple
User=${USER}
WorkingDirectory=${REPO_DIR}
Environment=SF_STATIC_LOBBIES=${MAIN_CODE}:${MAIN_PORT}
ExecStart=/usr/bin/env bash ${REPO_DIR}/oracle-watchdog.sh
Restart=always
RestartSec=5
[Install]
WantedBy=multi-user.target
UNIT
  exit 0
fi

# --- standalone-run guard (code-review) -------------------------------------
# This is the LEGACY ALKA-era standalone watchdog. Production now runs the
# systemd stack (sf-router.service + sf-lobbies.service + sf-oracle.service +
# sf-oracle-watchdog.{sh,timer}). Running this on the live box is HAZARDOUS: it
# assumes a different topology (router ${ROUTER_PORT} / MAIN on UDP ${MAIN_PORT})
# than the systemd oracle (router 1338 / MAIN 1337), so it would clobber the
# registry and double-launch. Require an explicit opt-in for dev boxes.
if [ "${SF_WATCHDOG_ALLOW_STANDALONE:-0}" != "1" ]; then
  log "Refusing to run: legacy standalone watchdog conflicts with the systemd stack."
  log "Production path: systemctl status sf-oracle-watchdog.timer. Dev override: SF_WATCHDOG_ALLOW_STANDALONE=1"
  exit 1
fi

# --- health checks ----------------------------------------------------------
router_up()  { pgrep -f 'sf-router' >/dev/null 2>&1; }
lobbies_up() { pgrep -f 'serve-lobbies.py' >/dev/null 2>&1; }
main_up() {
  local conf="$REGISTRY/${MAIN_CODE}.conf"
  [ -f "$conf" ] || return 1
  local pid; pid="$(sed -n 's/^pid=//p' "$conf" 2>/dev/null | head -1)"
  [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null
}

start_router() {
  log "router DOWN → starting (port ${ROUTER_PORT})"
  SF_ROUTER_LISTEN="0.0.0.0:${ROUTER_PORT}" SF_LOBBIES_DIR="$REGISTRY" \
    nohup bash "$REPO_DIR/launch-router.sh" >>"$LOGDIR/sf-router.log" 2>&1 &
  disown
}
start_lobbies() {
  log "serve-lobbies DOWN → starting (port ${LOBBY_PORT})"
  # SF_STATIC_LOBBIES makes the always-on lobby always show in the browser.
  SF_STATIC_LOBBIES="${SF_STATIC_LOBBIES:-${MAIN_CODE}:${MAIN_PORT}}" \
    nohup "$PY" "$REPO_DIR/serve-lobbies.py" --host 0.0.0.0 --port "$LOBBY_PORT" \
    >>"$LOGDIR/serve-lobbies.log" 2>&1 &
  disown
}
start_main() {
  log "MAIN lobby DOWN → starting (${MAIN_CODE} on UDP ${MAIN_PORT})"
  rm -f "$REGISTRY/${MAIN_CODE}.conf" 2>/dev/null
  nohup bash "$REPO_DIR/launch-lobby.sh" "$MAIN_CODE" "$MAIN_PORT" \
    >>"$LOGDIR/lobby-${MAIN_CODE}.log" 2>&1 &
  disown
}

log "oracle-watchdog starting — router:${ROUTER_PORT} lobbies:${LOBBY_PORT} main:${MAIN_CODE}@${MAIN_PORT} every ${INTERVAL}s"
mkdir -p "$REGISTRY" "$LOGDIR"
while true; do
  router_up  || start_router
  lobbies_up || start_lobbies
  # give the router+lobbies a moment before judging the lobby (it takes ~20s to bind)
  main_up    || start_main
  sleep "$INTERVAL"
done
