// SFLauncher — Windows one-click client + lobby browser for sf-multiplayer.
//
// Standalone single-file .exe. First run: auto-installs BepInEx + plugins
// into the user's Stick Fight Steam install (no .bat needed). Subsequent
// runs: opens the lobby browser directly. Always idempotent — re-running
// re-checks the install and updates plugins if needed.
//
// Plugin DLLs are bundled as embedded resources inside this .exe.
//
// Build:
//   cd deploy/SFLauncher
//   dotnet publish -c Release
// Output:
//   bin/Release/net8.0-windows/win-x64/publish/SFLauncher.exe
//   (~70MB single self-contained file — no .NET install needed on user's machine)

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SFLauncher;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

internal sealed class MainForm : Form
{
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly string s_settingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "sf-multiplayer-launcher", "settings.txt");

    private readonly TextBox _urlBox = new();
    private readonly Button _refreshBtn = new() { Text = "Refresh" };
    private readonly CheckBox _autoChk = new() { Text = "Auto-refresh (10s)", AutoSize = true };
    private readonly ListView _list = new();
    private readonly Label _statusLbl = new() { AutoSize = true, ForeColor = Color.Gray };
    private readonly Button _connectBtn = new() { Text = "Connect to selected lobby", Enabled = false };
    private readonly System.Windows.Forms.Timer _autoTimer = new() { Interval = 10_000 };

    public MainForm()
    {
        Text = "sf-multiplayer · launcher";
        Size = new Size(720, 480);
        MinimumSize = new Size(560, 320);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10f);
        BackColor = Color.FromArgb(20, 20, 24);
        ForeColor = Color.FromArgb(230, 230, 235);

        // --- URL row ---
        var urlLabel = new Label
        {
            Text = "Lobby endpoint:",
            AutoSize = true,
            Location = new Point(12, 18),
            ForeColor = ForeColor
        };

        _urlBox.Location = new Point(120, 14);
        _urlBox.Width = 380;
        _urlBox.BackColor = Color.FromArgb(35, 35, 40);
        _urlBox.ForeColor = ForeColor;
        _urlBox.BorderStyle = BorderStyle.FixedSingle;
        _urlBox.Text = LoadSavedUrl() ?? "http://69.53.117.43:8080/lobbies";

        _refreshBtn.Location = new Point(508, 13);
        _refreshBtn.Size = new Size(82, 25);
        _refreshBtn.BackColor = Color.FromArgb(50, 50, 56);
        _refreshBtn.ForeColor = ForeColor;
        _refreshBtn.FlatStyle = FlatStyle.Flat;
        _refreshBtn.Click += async (_, _) => await RefreshAsync();

        _autoChk.Location = new Point(600, 16);
        _autoChk.ForeColor = ForeColor;
        _autoChk.CheckedChanged += (_, _) =>
        {
            if (_autoChk.Checked) _autoTimer.Start();
            else _autoTimer.Stop();
        };

        _autoTimer.Tick += async (_, _) => await RefreshAsync();

        // --- ListView ---
        _list.Location = new Point(12, 50);
        _list.Size = new Size(680, 320);
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.HideSelection = false;
        _list.GridLines = false;
        _list.BackColor = Color.FromArgb(28, 28, 32);
        _list.ForeColor = ForeColor;
        _list.Font = new Font("Cascadia Mono, Consolas, monospace", 10f, FontStyle.Regular);
        _list.Columns.Add("Code", 90);
        _list.Columns.Add("Port", 60);
        _list.Columns.Add("Started", 90);
        _list.Columns.Add("PID", 80);
        _list.Columns.Add("Connect string (Steam launch options)", 360);
        _list.SelectedIndexChanged += (_, _) => _connectBtn.Enabled = _list.SelectedItems.Count > 0;
        _list.DoubleClick += (_, _) => ConnectSelected();

        // --- Status + Connect ---
        _statusLbl.Location = new Point(12, 384);

        _connectBtn.Location = new Point(420, 380);
        _connectBtn.Size = new Size(272, 32);
        _connectBtn.BackColor = Color.FromArgb(80, 140, 80);
        _connectBtn.ForeColor = Color.White;
        _connectBtn.FlatStyle = FlatStyle.Flat;
        _connectBtn.Click += (_, _) => ConnectSelected();

        Controls.AddRange(new Control[]
        {
            urlLabel, _urlBox, _refreshBtn, _autoChk,
            _list, _statusLbl, _connectBtn
        });

        // Layout responsiveness
        Resize += (_, _) =>
        {
            _list.Width = ClientSize.Width - 24;
            _list.Height = ClientSize.Height - 130;
            _statusLbl.Top = _list.Bottom + 4;
            _connectBtn.Top = _list.Bottom + 8;
            _connectBtn.Left = ClientSize.Width - _connectBtn.Width - 12;
            _urlBox.Width = ClientSize.Width - _urlBox.Left - 220;
            _refreshBtn.Left = _urlBox.Right + 8;
            _autoChk.Left = _refreshBtn.Right + 10;
        };

        // First-run install check + initial lobby fetch
        Shown += async (_, _) =>
        {
            await EnsureClientInstalledAsync();
            await RefreshAsync();
        };
    }

    // ============================================================
    //  AUTO-INSTALLER — runs on first launch; idempotent on re-runs
    // ============================================================
    private const string BepInExUrl =
        "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x86_5.4.23.5.zip";
    private const string ExpectedHeadlessResource = "SFLauncher.Resources.SFHeadlessHost.dll";
    private const string ExpectedReconResource    = "SFLauncher.Resources.SFClientRecon.dll";

    private async Task EnsureClientInstalledAsync()
    {
        var sf = FindStickFightInstall();
        if (sf == null)
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Couldn't auto-detect Stick Fight. Pick your install folder (the one with StickFight.exe in it).",
                UseDescriptionForTitle = true
            };
            if (dlg.ShowDialog(this) != DialogResult.OK)
            {
                SetStatus("Skipped install. Lobby browser will work but you won't be able to connect.", Color.OrangeRed);
                return;
            }
            sf = dlg.SelectedPath;
            if (!File.Exists(Path.Combine(sf, "StickFight.exe")))
            {
                MessageBox.Show(this, "That folder doesn't contain StickFight.exe.", "Wrong folder",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        var pluginsDir = Path.Combine(sf, "BepInEx", "plugins");
        var ourHost = Path.Combine(pluginsDir, "SFHeadlessHost.dll");
        var ourRecon = Path.Combine(pluginsDir, "SFClientRecon.dll");
        var bundledHostMd5 = GetEmbeddedMd5(ExpectedHeadlessResource);
        var bundledReconMd5 = GetEmbeddedMd5(ExpectedReconResource);

        bool needBep = !File.Exists(Path.Combine(sf, "winhttp.dll"))
                       || !Directory.Exists(Path.Combine(sf, "BepInEx"));
        bool needPlugins = !File.Exists(ourHost) || !File.Exists(ourRecon)
                          || FileMd5(ourHost)  != bundledHostMd5
                          || FileMd5(ourRecon) != bundledReconMd5;

        if (!needBep && !needPlugins)
        {
            SetStatus($"Install OK · plugins up to date · {Path.GetFileName(sf)}", Color.FromArgb(127, 255, 127));
            return;
        }

        // Show a small progress dialog
        using var progress = new InstallProgressForm(this);
        progress.Show(this);
        Application.DoEvents();

        try
        {
            if (needBep)
            {
                progress.SetStatus("Downloading BepInEx 5.4.23.5...");
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
                using var resp = await http.GetAsync(BepInExUrl);
                resp.EnsureSuccessStatusCode();
                var bytes = await resp.Content.ReadAsByteArrayAsync();

                progress.SetStatus("Installing BepInEx into Stick Fight...");
                using var ms = new MemoryStream(bytes);
                using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue; // skip directories
                    var target = Path.Combine(sf, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    // (E5) zip-slip guard: reject entries resolving outside the install
                    // dir. Defense-in-depth — the archive is a trusted HTTPS GitHub asset,
                    // but a malicious/MITM'd zip with ../ entries must not write out of tree.
                    var fullRoot = Path.GetFullPath(sf) + Path.DirectorySeparatorChar;
                    if (!Path.GetFullPath(target).StartsWith(fullRoot, StringComparison.Ordinal)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var src = entry.Open();
                    using var dst = File.Create(target);
                    await src.CopyToAsync(dst);
                }
            }

            Directory.CreateDirectory(pluginsDir);
            progress.SetStatus("Installing plugin: SFHeadlessHost.dll");
            ExtractEmbeddedTo(ExpectedHeadlessResource, ourHost);
            progress.SetStatus("Installing plugin: SFClientRecon.dll");
            ExtractEmbeddedTo(ExpectedReconResource, ourRecon);

            progress.SetStatus("Done.");
            await Task.Delay(500);
            progress.Close();
            SetStatus($"Install complete · {Path.GetFileName(sf)}", Color.FromArgb(127, 255, 127));
        }
        catch (Exception ex)
        {
            progress.Close();
            MessageBox.Show(this,
                $"Install failed: {ex.Message}\n\nYou can still browse lobbies — but the game won't connect until plugins are installed.",
                "Install error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetStatus("Install failed — see error dialog", Color.OrangeRed);
        }
    }

    // ---- find SF install ----
    private static string? FindStickFightInstall()
    {
        string[] candidates =
        {
            // Standard Steam paths
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Steam", "steamapps", "common", "StickFightTheGame"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Steam", "steamapps", "common", "StickFightTheGame"),
            @"C:\Program Files (x86)\Steam\steamapps\common\StickFightTheGame",
            @"C:\Program Files\Steam\steamapps\common\StickFightTheGame",
        };
        foreach (var c in candidates)
            if (File.Exists(Path.Combine(c, "StickFight.exe")))
                return c;

        // Try Steam's libraryfolders.vdf to find non-default library paths
        try
        {
            var steamPath = GetSteamInstallPath();
            if (steamPath != null)
            {
                var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                {
                    foreach (var line in File.ReadAllLines(vdf))
                    {
                        // crude vdf parse — look for "path" entries
                        var m = System.Text.RegularExpressions.Regex.Match(line, @"""path""\s+""([^""]+)""");
                        if (m.Success)
                        {
                            var lib = m.Groups[1].Value.Replace(@"\\", @"\");
                            var sf = Path.Combine(lib, "steamapps", "common", "StickFightTheGame");
                            if (File.Exists(Path.Combine(sf, "StickFight.exe"))) return sf;
                        }
                    }
                }
            }
        }
        catch { /* registry / vdf read can fail; user folder picker handles it */ }
        return null;
    }

    private static string? GetSteamInstallPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam");
            return key?.GetValue("SteamPath") as string;
        }
        catch { return null; }
    }

    // ---- embedded resource extraction ----
    private static void ExtractEmbeddedTo(string resourceName, string targetPath)
    {
        using var src = typeof(Program).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Bundled resource not found: {resourceName}. " +
                "The .exe was built without embedded plugins.");
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        using var dst = File.Create(targetPath);
        src.CopyTo(dst);
    }

    private static string? GetEmbeddedMd5(string resourceName)
    {
        try
        {
            using var src = typeof(Program).Assembly.GetManifestResourceStream(resourceName);
            if (src == null) return null;
            using var md5 = System.Security.Cryptography.MD5.Create();
            var hash = md5.ComputeHash(src);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
        catch { return null; }
    }

    private static string? FileMd5(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var md5 = System.Security.Cryptography.MD5.Create();
            var hash = md5.ComputeHash(fs);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
        catch { return null; }
    }

    // ---- settings persistence ----
    private static string? LoadSavedUrl()
    {
        try { return File.Exists(s_settingsFile) ? File.ReadAllText(s_settingsFile).Trim() : null; }
        catch { return null; }
    }
    private void SaveUrl()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(s_settingsFile)!);
            File.WriteAllText(s_settingsFile, _urlBox.Text.Trim());
        }
        catch { /* best-effort */ }
    }

    // ---- lobby fetch ----
    private async Task RefreshAsync()
    {
        var url = _urlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            SetStatus("Enter a URL.", Color.OrangeRed);
            return;
        }

        SetStatus("Fetching…", Color.Gray);
        SaveUrl();

        try
        {
            using var resp = await s_http.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                SetStatus($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}", Color.OrangeRed);
                return;
            }
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var generated = root.TryGetProperty("generatedAt", out var g) ? g.GetString() ?? "" : "";
            var lobbies = root.TryGetProperty("lobbies", out var l) ? l : default;

            // Server hostname from URL for the connect string
            var host = "127.0.0.1";
            if (Uri.TryCreate(url, UriKind.Absolute, out var u))
                host = u.Host;

            _list.BeginUpdate();
            _list.Items.Clear();
            int count = 0;
            if (lobbies.ValueKind == JsonValueKind.Array)
            {
                foreach (var lobby in lobbies.EnumerateArray())
                {
                    var code = lobby.TryGetProperty("code", out var c) ? c.GetString() ?? "?" : "?";
                    var port = lobby.TryGetProperty("port", out var p) ? p.GetString() ?? "?" : "?";
                    var pid = lobby.TryGetProperty("pid", out var pi) ? pi.GetString() ?? "?" : "?";
                    var started = lobby.TryGetProperty("started", out var s) ? (s.GetString() ?? "") : "";
                    var startedShort = started.Length >= 19 ? started.Substring(11, 8) : "";
                    var connect = $"-address {host} -port {port}";
                    var item = new ListViewItem(code);
                    item.SubItems.Add(port);
                    item.SubItems.Add(startedShort);
                    item.SubItems.Add(pid);
                    item.SubItems.Add(connect);
                    item.Tag = (host, port);
                    _list.Items.Add(item);
                    count++;
                }
            }
            _list.EndUpdate();
            var ts = generated.Length >= 19 ? generated.Substring(11, 8) : "";
            SetStatus($"Updated {ts} UTC · {count} lobbies", Color.FromArgb(127, 255, 127));
        }
        catch (TaskCanceledException)
        {
            SetStatus("Timeout — server not responding", Color.OrangeRed);
        }
        catch (HttpRequestException ex)
        {
            SetStatus($"Couldn't reach server: {ex.Message}", Color.OrangeRed);
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}", Color.OrangeRed);
        }
    }

    private void SetStatus(string text, Color color)
    {
        _statusLbl.Text = text;
        _statusLbl.ForeColor = color;
    }

    // ---- connect to selected lobby ----
    private void ConnectSelected()
    {
        if (_list.SelectedItems.Count == 0) return;
        var item = _list.SelectedItems[0];
        var (host, port) = ((string, string))item.Tag!;

        var winLaunchOpts = $"-address {host} -port {port}";
        var linuxLaunchOpts = $"WINEDLLOVERRIDES=\"winhttp=n,b\" %command% -address {host} -port {port}";

        // Build the message + clipboard payload
        var msg = $@"Lobby:  {item.SubItems[0].Text} ({host}:{port})

The launch options below have been copied to your clipboard.
Paste into Steam → Stick Fight → Properties → Launch Options.

  Windows: {winLaunchOpts}
  Linux:   {linuxLaunchOpts}

Then click Play in Steam.

[OK] = open Steam now
[Cancel] = just copy, don't open Steam";

        // Copy windows version to clipboard by default (most users)
        try { Clipboard.SetText(winLaunchOpts); }
        catch { /* clipboard can fail in some sandboxes */ }

        var result = MessageBox.Show(this, msg, "Connect to lobby",
            MessageBoxButtons.OKCancel, MessageBoxIcon.Information,
            MessageBoxDefaultButton.Button1);

        if (result == DialogResult.OK)
        {
            try
            {
                // Steam protocol URL to launch Stick Fight
                // App ID 674940 is Stick Fight: The Game
                Process.Start(new ProcessStartInfo
                {
                    FileName = "steam://run/674940",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    $"Couldn't open Steam: {ex.Message}\n\nOpen it manually and click Play on Stick Fight.",
                    "Open Steam manually",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}

internal sealed class InstallProgressForm : Form
{
    private readonly Label _label = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };

    public InstallProgressForm(Form owner)
    {
        Text = "Setting up Stick Fight…";
        Size = new Size(420, 130);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ControlBox = false;
        Owner = owner;
        BackColor = Color.FromArgb(20, 20, 24);
        ForeColor = Color.FromArgb(230, 230, 235);
        Font = new Font("Segoe UI", 10f);

        _label.Location = new Point(15, 30);
        _label.Size = new Size(380, 50);
        _label.ForeColor = ForeColor;
        _label.Text = "Preparing…";

        Controls.Add(_label);
    }

    public void SetStatus(string text)
    {
        _label.Text = text;
        Application.DoEvents();
    }
}
