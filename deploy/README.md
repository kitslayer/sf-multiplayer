# `deploy/` — Windows one-click scripts

Windows wrappers for the lobby management scripts. The Linux equivalents at the repo root (`launch-lobby.sh`, `stop-all-lobbies.sh`, `list-lobbies.sh`) are the source of truth; these `.bat` files mirror their behavior so Windows users can double-click instead of installing WSL.

| Script | What it does |
|---|---|
| `launch-lobby.bat [CODE] [PORT]` | Spawns one headless `StickFight.exe -batchmode -nographics` on the next free UDP port. Auto-generates a 4-char code if omitted. |
| `stop-all-lobbies.bat` | `taskkill /F /IM StickFight.exe` + wipes the lobby registry. |
| `list-lobbies.bat` | Lists `%TEMP%\sf-lobbies\*.conf`. |

## Env vars

| Var | Default | Notes |
|---|---|---|
| `SF_ORACLE_INSTALL` | `C:\Program Files (x86)\Steam\steamapps\common\StickFightTheGame` | Your Stick Fight install (must have `BepInEx/` + `SFHeadlessHost.dll` in plugins) |
| `SF_BASE_PORT` | `1337` | First UDP port to try |
| `SF_LOBBIES_DIR` | `%TEMP%\sf-lobbies` | Registry directory |

## Quickstart on Windows

1. Drop `SFHeadlessHost.dll` into `<SF install>\BepInEx\plugins\`
2. Drop `SFClientRecon.dll` into the SAME plugins dir (it skips on batchmode, so it's a no-op in the oracle instance)
3. Double-click `launch-lobby.bat` — a headless SF spawns, port 1337 binds
4. From a player machine (or a second `StickFight.exe` on this box with different Goldberg config), connect via Steam launch options:
   ```
   WINEDLLOVERRIDES="winhttp=n,b" %command% -address 127.0.0.1 -port 1337
   ```

## Differences from ALKA's `deploy/*.bat`

ALKA's scripts assume his hybrid Go server + Unity headless model. Ours skip the Go server entirely (Path A — headless SF *is* the server) and only manage the SF instance per lobby. Same one-click experience; simpler stack.

If you need lobby browser / matchmaking on top, run `serve-lobbies.py` (works on Windows via Python) — it exposes `GET /lobbies` from the registry as JSON.
