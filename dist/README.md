# sf-multiplayer client bundle

**For comp players to join a Stick Fight server.**

Everything you need is in this folder. Tested on Windows 10/11.

## Two-step setup

### 1. First time only — install the mods

Double-click **`install-sf-client.bat`**. It will:
- find your Stick Fight install automatically (Steam paths)
- download BepInEx
- copy the two plugin DLLs into the install
- tell you what Steam launch options to set

When it finishes, set those launch options in Steam:
- Right-click **Stick Fight: The Game** → Properties → Launch Options
- Paste:  `-address SERVER_IP -port 1337`  (replace SERVER_IP with what the host gives you, e.g. `192.168.1.115`)
- Close the Properties window.

### 2. Every time — launch the lobby browser

Double-click **`SFLauncher.exe`**. The window shows running lobbies on the server. Pick one, click **Connect** — it copies the launch options to your clipboard and opens Steam. Then click Play in Steam.

If the connect string in your Steam launch options is already right (most cases), you don't even need SFLauncher — just hit Play.

## Files in this folder

| File | Purpose |
|---|---|
| `install-sf-client.bat` | One-time setup. Detects SF install, installs BepInEx, copies plugins. |
| `SFLauncher.exe` | Lobby browser GUI (Windows, single-file). |
| `SFHeadlessHost.dll` | Server-side plugin (also gets deployed on the client; it self-disables in interactive mode). |
| `SFClientRecon.dll` | Client-side plugin — handles the v26 reconciliation protocol. |

## Troubleshooting

**"Couldn't find Stick Fight"** — drag the `StickFight.exe` file from your install into the .bat window, then press Enter.

**Lobby browser says "Couldn't reach server"** — paste the server's HTTP endpoint URL into the box at the top, e.g. `http://192.168.1.115:8080/lobbies`. Click Refresh.

**Steam launches the game but you don't connect** — your launch options are wrong. They should look exactly like `-address 192.168.1.115 -port 1337` (no quotes, no leading spaces).

**No lobbies showing** — ask the host whether their oracle is actually running. They can confirm with `bash list-lobbies.sh` on the server.

## Where this came from

- Source: https://github.com/kitslayer/sf-multiplayer
- Bug? Open an issue on GitHub.
- The plugins here match the latest `main` branch as of bundle time. Updates: re-download this folder.
