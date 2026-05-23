// SFLauncher — Windows lobby browser for sf-multiplayer.
//
// Standalone single-file .exe. Run it, paste the server's HTTP endpoint
// (e.g. http://192.168.1.115:8080), pick a lobby, click Connect. The
// launch options get copied to your clipboard and Steam opens.
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
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

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
        _urlBox.Text = LoadSavedUrl() ?? "http://192.168.1.115:8080/lobbies";

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

        // Kick off an initial fetch on shown
        Shown += async (_, _) => await RefreshAsync();
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
