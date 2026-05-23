#!/usr/bin/env bash
# Launches one headless SF oracle on a Proton-equipped server. Designed for
# server-side deployment (no Steam install needed — Proton is bundled).
#
# Expected directory layout under $SF_ORACLE_ROOT (default: ~/sf-oracle):
#   install/        — SF binary + Assembly-CSharp.dll + BepInEx + plugins
#   proton/         — bundled Proton install (the 'proton' python script + dist/)
#   runtime/        — Steam-Runtime-style symlinks (created lazily, may be empty)
#   prefix-<port>/  — per-oracle wineprefix (created on first boot)
#
# Env (caller-overridable):
#   SF_ORACLE_ROOT          ~/sf-oracle
#   SFHEADLESS_PORT         1337
#   SFHEADLESS_BRIDGEPORT   $PORT + 10000
#   SF_LOBBY_CODE           DEPLOY
#   SFHEADLESS_LOGFILE      /tmp/sf-oracle-plugin-$BRIDGE.log  (per-lobby tee)
#
# Designed to be invoked from a systemd unit (Type=simple, Restart=on-failure)
# OR called by launch-lobby.sh on a server (for multi-lobby).
set -eu

ROOT="${SF_ORACLE_ROOT:-$HOME/sf-oracle}"
SF_DIR="$ROOT/install"
PROTON="$ROOT/proton/proton"

if [ ! -x "$PROTON" ]; then
  echo "Proton not found at $PROTON" >&2
  echo "  bundle Proton via: rsync ~/.local/share/Steam/steamapps/common/Proton*/ <server>:~/sf-oracle/proton/" >&2
  exit 2
fi
if [ ! -f "$SF_DIR/StickFight.exe" ]; then
  echo "SF install not found at $SF_DIR/StickFight.exe" >&2
  exit 2
fi

PORT="${SFHEADLESS_PORT:-1337}"
BRIDGE="${SFHEADLESS_BRIDGEPORT:-$((PORT + 10000))}"
PREFIX="$ROOT/prefix-$BRIDGE"
mkdir -p "$PREFIX"

# Per-lobby plugin log file (no shared LogOutput.log trampling)
PLUGINLOG="${SFHEADLESS_LOGFILE:-/tmp/sf-oracle-plugin-$BRIDGE.log}"
mkdir -p "$(dirname "$PLUGINLOG")"
# Truncate old plugin log on each start so operators can grep cleanly
: > "$PLUGINLOG"

# Proton needs STEAM_COMPAT_CLIENT_INSTALL_PATH to find its runtime. On a
# server without Steam, point it at the proton dir itself + a stub runtime.
export STEAM_COMPAT_CLIENT_INSTALL_PATH="$ROOT/runtime"
export STEAM_COMPAT_DATA_PATH="$PREFIX"
export WINEDLLOVERRIDES="winhttp=n,b"
export WINEDEBUG=-all
export PROTON_USE_XALIA=0
# Proton sometimes wants this for "skip the launcher dialog"
export PROTON_NO_ESYNC=0
export PROTON_NO_FSYNC=0

export SFHEADLESS_PORT="$PORT"
export SFHEADLESS_BRIDGEPORT="$BRIDGE"
export SFHEADLESS_DEBUG="${SFHEADLESS_DEBUG:-1}"
export SF_LOBBY_CODE="${SF_LOBBY_CODE:-DEPLOY}"
export SFHEADLESS_LOGFILE="$PLUGINLOG"
export SF_ROUND_END_DELAY="${SF_ROUND_END_DELAY:-0.5}"
export SF_NEXT_MATCH_DELAY="${SF_NEXT_MATCH_DELAY:-2.0}"

UNITY_LOG="/tmp/sf-oracle-unity-$BRIDGE.log"

cd "$SF_DIR"
echo "[start-oracle-server] launching SF in batchmode on port=$PORT bridge=$BRIDGE"
echo "  PREFIX:      $PREFIX"
echo "  PLUGIN LOG:  $PLUGINLOG"
echo "  UNITY LOG:   $UNITY_LOG"
echo "  LOBBY CODE:  $SF_LOBBY_CODE"

# Wine needs SOME display driver for its core (CreateWindow internals, even
# in -batchmode -nographics). On a headless server we wrap in xvfb-run so
# Wine gets a virtual X11 display. Without this, the SF binary loads but
# never progresses past nodrv_CreateWindow ("The explorer process failed to
# start.") and hangs at 0% CPU forever.
if command -v xvfb-run >/dev/null 2>&1; then
  exec xvfb-run -a --server-args="-screen 0 320x240x24" \
    "$PROTON" run "$SF_DIR/StickFight.exe" \
    -batchmode -nographics \
    -logFile "$UNITY_LOG"
else
  # Fallback to bare Proton — only works if there's an existing X display
  # (e.g., a desktop session with this script in a terminal).
  echo "  WARNING: xvfb-run not found — Wine may hang without a display driver. apt install xvfb."
  exec "$PROTON" run "$SF_DIR/StickFight.exe" \
    -batchmode -nographics \
    -logFile "$UNITY_LOG"
fi
