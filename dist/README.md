# sf-multiplayer client bundle

**For comp players: one click, you're in a lobby.**

## Windows

1. Download **`SFLauncher.exe`** from this folder.
2. Double-click it. First run: it auto-installs BepInEx + plugins into your Steam Stick Fight install. Then the lobby browser window opens.
3. Pick a lobby, click **Connect**. Launch options get copied to your clipboard. Steam opens.
4. Right-click Stick Fight in Steam → Properties → Launch Options → paste → close.
5. Click **Play**. You're in.

Subsequent launches just open the lobby browser directly (install step is skipped if up to date).

## Linux / macOS

1. Download **`sflauncher.sh`** from this folder.
2. Make it executable: `chmod +x sflauncher.sh`
3. Run it: `./sflauncher.sh`
4. It downloads BepInEx + plugins, sets up your Steam Stick Fight install, opens a lobby-browser page in your default browser.
5. Set Steam launch options (printed at end of script) and click Play.

(No native GUI on Linux yet — the browser page is the GUI. Functionally identical, just opens in Firefox/Chrome instead of a window.)

## What's in this folder

| File | Purpose |
|---|---|
| `SFLauncher.exe` | Windows one-click app. Auto-installs + lobby browser. |
| `sflauncher.sh` | Linux/macOS one-click installer + browser launcher. |
| `install-sf-client.bat` | (Optional Windows) install-only batch. SFLauncher.exe already does this; provided for users who want to install without the GUI. |
| `install-sf-client.sh` | (Optional Linux) install-only script. Same as above. |
| `SFHeadlessHost.dll` | Server-side plugin (auto-deployed by the installers). |
| `SFClientRecon.dll` | Client-side plugin (auto-deployed by the installers). |

## Troubleshooting

**Windows: SmartScreen blocks SFLauncher.exe** — the .exe isn't code-signed. Click "More info" → "Run anyway". One-time; it remembers.

**"Couldn't auto-find Stick Fight"** — point the launcher at your install via folder picker (Windows) or paste the path (Linux).

**Lobby browser says "Couldn't reach server"** — type the right `http://<server-ip>:8080/lobbies` URL into the address bar at the top. It saves to LocalAppData so it persists.

**Game launches but doesn't connect** — Steam launch options are missing. They should look exactly like `-address 69.53.117.43 -port 1337` (no quotes, no leading spaces). Set them via right-click Stick Fight → Properties → Launch Options.

**No lobbies showing** — ask the host whether their oracle is running. They can verify with `bash list-lobbies.sh` on the server.

## Where this came from

- Source: https://github.com/kitslayer/sf-multiplayer
- Bug? Open an issue on GitHub.
- Bundle matches the latest `main` branch as of build time. Re-download to update.
