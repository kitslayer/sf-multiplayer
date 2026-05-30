using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SFServerBrowser
{
    // SFServerBrowser v0.1.0 — client-side overlay.
    //
    // Adds a "SERVERS" button to Stick Fight's main menu and an in-game
    // Escape menu overlay listing connected players.
    //
    // The button overlay is drawn via Unity's OnGUI (IMGUI) so we don't
    // need to clone vanilla menu prefabs (which are fragile across SF
    // versions). It looks consistent with the SF aesthetic — bold white
    // panels on dark background — and stays out of the way until needed.
    //
    // Server list fetched from the HTTP endpoint that the oracle's
    // serve-lobbies.py exposes (configurable via SF_LOBBY_ENDPOINT env var).
    //
    // CONSTRAINT (from user): does NOT modify the existing Quick Match /
    // Host Match → oracle redirect that SfOracleLobbyConnect already handles.
    // This is purely additive UI.

    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.stickfightdev.server-browser";
        public const string PluginName = "SFServerBrowser";
        public const string PluginVersion = "0.1.0";

        internal static ManualLogSource Log;
        internal static Plugin Instance;

        // Configurable via env var; falls back to default oracle.
        private string _lobbyEndpoint;

        // UI state
        private bool _showServersPanel;
        private bool _showInGamePanel;
        private List<ServerEntry> _servers = new List<ServerEntry>();
        private string _statusText = "";
        private float _lastFetchAt = -999f;
        private const float FetchCooldown = 3f;
        private Vector2 _serversScroll;
        private Vector2 _inGameScroll;
        private bool _stylesInited;
        private GUIStyle _btnStyle, _btnAccent, _titleStyle, _rowStyle, _statusStyle;

        // For detecting if we're in a match (vs lobby/main menu) for Esc menu
        private string _currentSceneName = "";

        private struct ServerEntry
        {
            public string Code;
            public string Host;
            public int Port;
            public int Players;
            public int Capacity;
            public string Status;
        }

        private void Awake()
        {
            Log = Logger;
            Instance = this;

            // Skip on headless oracle
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (arg == "-batchmode" || arg == "-nographics")
                {
                    Log.LogInfo($"{PluginName}: batchmode — UI disabled.");
                    return;
                }
            }

            _lobbyEndpoint = Environment.GetEnvironmentVariable("SF_LOBBY_ENDPOINT");
            if (string.IsNullOrEmpty(_lobbyEndpoint))
                _lobbyEndpoint = "http://69.53.117.43:8080/lobbies";

            Log.LogInfo($"{PluginName} v{PluginVersion} starting. Endpoint: {_lobbyEndpoint}");

            SceneManager.sceneLoaded += (s, m) => _currentSceneName = s.name;
        }

        private void Update()
        {
            // ESC handling — only in-match (not lobby/main menu)
            if (Input.GetKeyDown(KeyCode.F3))
            {
                // F3 toggles the in-game player list (independent from vanilla ESC pause)
                _showInGamePanel = !_showInGamePanel;
            }
        }

        private void OnGUI()
        {
            if (!_stylesInited) InitStyles();

            DrawMainMenuServersButton();
            if (_showServersPanel) DrawServersPanel();
            if (_showInGamePanel) DrawInGamePanel();
        }

        // ========== UI Styles (SF aesthetic) ==========
        private void InitStyles()
        {
            _stylesInited = true;
            _btnStyle = new GUIStyle(GUI.skin.button);
            _btnStyle.fontSize = 22;
            _btnStyle.fontStyle = FontStyle.Bold;
            _btnStyle.normal.textColor = Color.black;
            _btnStyle.hover.textColor = new Color(0.1f, 0.4f, 0.8f);
            _btnStyle.alignment = TextAnchor.MiddleCenter;
            _btnStyle.normal.background = MakeTex(2, 2, new Color(0.97f, 0.97f, 0.95f));
            _btnStyle.hover.background = MakeTex(2, 2, new Color(0.85f, 0.9f, 1f));
            _btnStyle.padding = new RectOffset(10, 10, 10, 10);

            _btnAccent = new GUIStyle(_btnStyle);
            _btnAccent.normal.background = MakeTex(2, 2, new Color(0.95f, 0.85f, 0.2f));
            _btnAccent.hover.background = MakeTex(2, 2, new Color(1f, 0.95f, 0.4f));

            _titleStyle = new GUIStyle(GUI.skin.label);
            _titleStyle.fontSize = 28;
            _titleStyle.fontStyle = FontStyle.Bold;
            _titleStyle.alignment = TextAnchor.MiddleCenter;
            _titleStyle.normal.textColor = new Color(1f, 0.9f, 0.4f);

            _rowStyle = new GUIStyle(GUI.skin.box);
            _rowStyle.normal.background = MakeTex(2, 2, new Color(0.1f, 0.12f, 0.18f, 0.85f));
            _rowStyle.normal.textColor = Color.white;
            _rowStyle.fontSize = 14;
            _rowStyle.alignment = TextAnchor.MiddleLeft;
            _rowStyle.padding = new RectOffset(12, 12, 8, 8);

            _statusStyle = new GUIStyle(GUI.skin.label);
            _statusStyle.fontSize = 13;
            _statusStyle.normal.textColor = new Color(0.7f, 0.8f, 0.9f);
        }

        private Texture2D MakeTex(int w, int h, Color c)
        {
            var px = new Color[w * h];
            for (int i = 0; i < px.Length; i++) px[i] = c;
            var t = new Texture2D(w, h);
            t.SetPixels(px); t.Apply();
            return t;
        }

        // ========== SERVERS button on main menu ==========
        private void DrawMainMenuServersButton()
        {
            if (_currentSceneName != "MainScene" && _currentSceneName != "") return;
            if (_showServersPanel || _showInGamePanel) return;

            // Floating button in top-right
            int w = 200, h = 60;
            var r = new Rect(Screen.width - w - 20, 20, w, h);
            if (GUI.Button(r, "SERVERS", _btnAccent))
            {
                _showServersPanel = true;
                RefreshServers();
            }
        }

        // ========== SERVERS browser panel ==========
        private void DrawServersPanel()
        {
            float pw = Mathf.Min(700, Screen.width - 40);
            float ph = Mathf.Min(560, Screen.height - 40);
            var panel = new Rect((Screen.width - pw) / 2, (Screen.height - ph) / 2, pw, ph);

            GUI.Box(panel, "", _rowStyle);
            GUILayout.BeginArea(panel);
            GUILayout.Space(10);
            GUILayout.Label("SERVERS", _titleStyle, GUILayout.Height(40));
            GUILayout.Label(_statusText, _statusStyle, GUILayout.Height(20));

            // Buttons row
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("REFRESH", _btnStyle, GUILayout.Width(140), GUILayout.Height(44)))
                RefreshServers();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("CLOSE", _btnStyle, GUILayout.Width(140), GUILayout.Height(44)))
                _showServersPanel = false;
            GUILayout.EndHorizontal();

            GUILayout.Space(10);

            // Server list
            _serversScroll = GUILayout.BeginScrollView(_serversScroll, GUILayout.Height(ph - 180));
            if (_servers.Count == 0)
            {
                GUILayout.Label("No servers found. Click REFRESH to try again.", _statusStyle, GUILayout.Height(30));
            }
            else
            {
                foreach (var s in _servers)
                {
                    GUILayout.BeginHorizontal(_rowStyle, GUILayout.Height(58));
                    GUILayout.BeginVertical();
                    GUILayout.Label($"<b>{s.Code}</b>  —  {s.Host}:{s.Port}", _statusStyle);
                    GUILayout.Label($"Players: {s.Players}/{s.Capacity}   Status: {s.Status}", _statusStyle);
                    GUILayout.EndVertical();
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("CONNECT", _btnStyle, GUILayout.Width(140), GUILayout.Height(44)))
                    {
                        ConnectToServer(s);
                    }
                    GUILayout.EndHorizontal();
                    GUILayout.Space(4);
                }
            }
            GUILayout.EndScrollView();

            GUILayout.EndArea();
        }

        // ========== IN-GAME player list panel ==========
        private void DrawInGamePanel()
        {
            if (_currentSceneName == "MainScene") { _showInGamePanel = false; return; }

            float pw = Mathf.Min(500, Screen.width - 40);
            float ph = Mathf.Min(420, Screen.height - 40);
            var panel = new Rect((Screen.width - pw) / 2, (Screen.height - ph) / 2, pw, ph);

            GUI.Box(panel, "", _rowStyle);
            GUILayout.BeginArea(panel);
            GUILayout.Space(10);
            GUILayout.Label("PLAYERS IN MATCH", _titleStyle, GUILayout.Height(40));
            GUILayout.Label("Press F3 to close.", _statusStyle, GUILayout.Height(20));

            GUILayout.Space(10);
            _inGameScroll = GUILayout.BeginScrollView(_inGameScroll, GUILayout.Height(ph - 130));

            var playerList = GetConnectedPlayers();
            if (playerList.Count == 0)
            {
                GUILayout.Label("No players detected yet.", _statusStyle, GUILayout.Height(30));
            }
            else
            {
                foreach (var p in playerList)
                {
                    GUILayout.BeginHorizontal(_rowStyle, GUILayout.Height(48));
                    GUILayout.Label($"Slot {p.Slot}  —  {p.Name}", _statusStyle);
                    GUILayout.FlexibleSpace();
                    GUILayout.Label($"SteamID: {p.SteamId}", _statusStyle, GUILayout.Width(200));
                    GUILayout.EndHorizontal();
                    GUILayout.Space(4);
                }
            }
            GUILayout.EndScrollView();

            GUILayout.Space(10);
            if (GUILayout.Button("CLOSE", _btnStyle, GUILayout.Height(44)))
                _showInGamePanel = false;
            GUILayout.EndArea();
        }

        private struct PlayerRow { public int Slot; public string Name; public ulong SteamId; }
        private List<PlayerRow> GetConnectedPlayers()
        {
            var list = new List<PlayerRow>();
            try
            {
                var mmType = AccessTools.TypeByName("MultiplayerManager");
                if ((object)mmType == null) return list;
                var inst = UnityEngine.Object.FindObjectOfType(mmType);
                if ((object)inst == null) return list;
                var clientsField = AccessTools.Field(mmType, "ConnectedClients");
                if ((object)clientsField == null) clientsField = AccessTools.Field(mmType, "mConnectedClients");
                if ((object)clientsField == null) return list;
                var arr = clientsField.GetValue(inst) as Array;
                if (arr == null) return list;
                for (int i = 0; i < arr.Length; i++)
                {
                    var entry = arr.GetValue(i);
                    if (entry == null) continue;
                    var idField = AccessTools.Field(entry.GetType(), "ClientID");
                    ulong sid = 0;
                    if ((object)idField != null)
                    {
                        var idObj = idField.GetValue(entry);
                        if (idObj != null)
                        {
                            var steamField = AccessTools.Field(idObj.GetType(), "m_SteamID");
                            if ((object)steamField != null)
                            {
                                var v = steamField.GetValue(idObj);
                                if (v is ulong su) sid = su;
                            }
                        }
                    }
                    if (sid == 0) continue;
                    string name = $"Player {i}";
                    // Try to get steam name via SteamFriends if available (vanilla)
                    try
                    {
                        var sfType = AccessTools.TypeByName("Steamworks.SteamFriends");
                        var cidType = AccessTools.TypeByName("Steamworks.CSteamID");
                        if ((object)sfType != null && (object)cidType != null)
                        {
                            var getName = AccessTools.Method(sfType, "GetFriendPersonaName");
                            if ((object)getName != null)
                            {
                                var cid = Activator.CreateInstance(cidType, sid);
                                var n = getName.Invoke(null, new object[] { cid }) as string;
                                if (!string.IsNullOrEmpty(n)) name = n;
                            }
                        }
                    }
                    catch { }
                    list.Add(new PlayerRow { Slot = i, Name = name, SteamId = sid });
                }
            }
            catch (Exception e) { Log.LogWarning($"[player-list] {e.Message}"); }
            return list;
        }

        // ========== HTTP fetch ==========
        private void RefreshServers()
        {
            if (Time.realtimeSinceStartup - _lastFetchAt < FetchCooldown)
            {
                _statusText = $"Wait {FetchCooldown - (Time.realtimeSinceStartup - _lastFetchAt):0.0}s before refreshing again.";
                return;
            }
            _lastFetchAt = Time.realtimeSinceStartup;
            _statusText = "Fetching from " + _lobbyEndpoint + "...";
            StartCoroutine(FetchServersCoroutine());
        }

        private IEnumerator FetchServersCoroutine()
        {
            string body = null;
            string err = null;
            // Use System.Net.WebRequest synchronously on a thread to avoid Unity coroutine complexity
            // Simple: WebClient on thread, yield until done
            System.Threading.Thread t = new System.Threading.Thread(() =>
            {
                try
                {
                    System.Net.ServicePointManager.ServerCertificateValidationCallback = (a, b, c, d) => true;
                    using (var wc = new WebClient())
                    {
                        wc.Headers.Add("User-Agent", "SFServerBrowser/" + PluginVersion);
                        body = wc.DownloadString(_lobbyEndpoint);
                    }
                }
                catch (Exception e) { err = e.Message; }
            });
            t.IsBackground = true;
            t.Start();
            while (t.IsAlive) yield return null;

            if (err != null)
            {
                _statusText = "Error: " + err;
                _servers.Clear();
                yield break;
            }
            ParseServers(body);
        }

        private void ParseServers(string json)
        {
            _servers.Clear();
            if (string.IsNullOrEmpty(json)) { _statusText = "Empty response."; return; }
            // Minimal JSON parser for the known schema
            // Expected: {"generatedAt":"...", "lobbies":[{"code":"MAIN","port":1337,"alive":true,...}]}
            try
            {
                int lobbiesStart = json.IndexOf("\"lobbies\"");
                if (lobbiesStart < 0) { _statusText = "No 'lobbies' key."; return; }
                int arrStart = json.IndexOf('[', lobbiesStart);
                int arrEnd = json.IndexOf(']', arrStart);
                if (arrStart < 0 || arrEnd < 0) { _statusText = "Malformed array."; return; }
                string arr = json.Substring(arrStart + 1, arrEnd - arrStart - 1);

                int depth = 0, start = -1;
                for (int i = 0; i < arr.Length; i++)
                {
                    char c = arr[i];
                    if (c == '{') { if (depth == 0) start = i; depth++; }
                    else if (c == '}') { depth--; if (depth == 0 && start >= 0) {
                        string obj = arr.Substring(start, i - start + 1);
                        var e = new ServerEntry();
                        e.Code = ExtractString(obj, "code") ?? "?";
                        e.Host = ExtractString(obj, "host") ?? "69.53.117.43";
                        e.Port = ExtractInt(obj, "port", 1337);
                        e.Players = ExtractInt(obj, "players", 0);
                        e.Capacity = ExtractInt(obj, "capacity", 4);
                        e.Status = ExtractBool(obj, "alive", true) ? "ALIVE" : "DOWN";
                        _servers.Add(e);
                        start = -1;
                    } }
                }
                _statusText = $"Found {_servers.Count} server(s).";
            }
            catch (Exception e) { _statusText = "Parse error: " + e.Message; }
        }

        private static string ExtractString(string json, string key)
        {
            int i = json.IndexOf("\"" + key + "\"");
            if (i < 0) return null;
            int colon = json.IndexOf(':', i);
            int q1 = json.IndexOf('"', colon + 1);
            if (q1 < 0) return null;
            int q2 = json.IndexOf('"', q1 + 1);
            if (q2 < 0) return null;
            return json.Substring(q1 + 1, q2 - q1 - 1);
        }
        private static int ExtractInt(string json, string key, int def)
        {
            int i = json.IndexOf("\"" + key + "\"");
            if (i < 0) return def;
            int colon = json.IndexOf(':', i);
            if (colon < 0) return def;
            var sb = new StringBuilder();
            for (int k = colon + 1; k < json.Length; k++)
            {
                char c = json[k];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') continue;
                if (c == ',' || c == '}') break;
                sb.Append(c);
            }
            if (int.TryParse(sb.ToString(), out var v)) return v;
            return def;
        }
        private static bool ExtractBool(string json, string key, bool def)
        {
            int i = json.IndexOf("\"" + key + "\"");
            if (i < 0) return def;
            int colon = json.IndexOf(':', i);
            if (colon < 0) return def;
            string rest = json.Substring(colon + 1).TrimStart();
            if (rest.StartsWith("true")) return true;
            if (rest.StartsWith("false")) return false;
            return def;
        }

        // ========== Connect to selected server ==========
        // Don't reinvent the wheel — copy the launch options to clipboard
        // so user can paste into Steam launch options or just restart SF
        // with new args. This also doesn't break the Quick/Host Match
        // existing flow (which is constraint #18).
        private void ConnectToServer(ServerEntry s)
        {
            string launchArgs = $"-address {s.Host} -port {s.Port}";
            try { GUIUtility.systemCopyBuffer = launchArgs; } catch { }
            _statusText = $"Copied launch args to clipboard: {launchArgs}\nRestart SF with these args to connect.";
            Log.LogInfo($"[connect] Copied: {launchArgs}");
        }
    }
}
