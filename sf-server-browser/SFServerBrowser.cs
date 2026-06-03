using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SFServerBrowser
{
    // ============================================================================
    //  SFServerBrowser v0.2.0 — ALKA server-browser & lobby overlay (client-side).
    //
    //  A professional IMGUI overlay for Stick Fight: a "PLAY ONLINE" entry button
    //  on the main menu opens a tabbed panel — BROWSE running lobbies, CREATE a
    //  new lobby, or JOIN by code — plus an in-match player list (F3) and a
    //  connection/status feedback layer (toasts + a connecting screen).
    //
    //  Why IMGUI: the game is Unity 5.6.3 (Mono 2.0). Runtime UI Toolkit does not
    //  exist on this engine and the vanilla menus are baked prefabs with no
    //  source, so OnGUI is the only runtime UI we can ship. The look is carried by
    //  UiTheme/UiWidgets (this is the design-system layer).
    //
    //  Purely additive: it does NOT modify the Quick/Host → oracle redirect that
    //  SfOracleLobbyConnect owns; it only calls the public RequestJoinLobby /
    //  RequestHostLobby bridges by reflection.
    //
    //  Data: lobby list + create come from serve-lobbies.py over HTTP
    //  (SF_LOBBY_ENDPOINT). No external deps; net35 / Mono-2.0 safe (no LINQ,
    //  tuples or Action<T> fields; coroutines OK via Mono2Polyfills).
    // ============================================================================
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public partial class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.stickfightdev.server-browser";
        public const string PluginName = "SFServerBrowser";
        public const string PluginVersion = "0.4.0";

        internal static ManualLogSource Log;
        internal static Plugin Instance;

        // Configurable via env var; null = fail closed with a clear message.
        private string _lobbyEndpoint;

        // ---- Overlay state machine -------------------------------------------
        internal enum Tab { Browse = 0, Create = 1, Join = 2 }
        private bool _overlayOpen;
        private float _overlayAnim;          // 0 closed → 1 open (eased)
        private Tab _tab = Tab.Browse;
        private bool _showHud;               // in-match player list (F3)

        // ---- Browse ----------------------------------------------------------
        private readonly List<ServerEntry> _servers = new List<ServerEntry>();
        private string _statusText = "";
        private bool _isFetching;
        private float _lastFetchAt = -999f;
        private const float FetchCooldown = 3f;
        private Vector2 _serversScroll;

        // ---- Create ----------------------------------------------------------
        private int _createMaxPlayers = 4;   // 2..8
        private int _createMode;             // 0 normal, 1 mods, 2 chaos (cosmetic hint)
        private bool _createPublic = true;
        private bool _isCreating;
        private float _createCooldownAt = -999f;

        // ---- Join ------------------------------------------------------------
        private string _joinCodeInput = "";
        private bool _focusJoinField;

        // ---- Connection feedback ---------------------------------------------
        private string _joiningCode;
        private float _joiningSince = -1f;
        private const float JoiningOverlayTtl = 8f;

        // ---- In-match HUD ----------------------------------------------------
        private Vector2 _hudScroll;
        private string _currentSceneName = "";

        // Cross-assembly bridges to SFClientRecon.Plugin (resolved once).
        private static System.Reflection.MethodInfo _joinMethod;
        private static System.Reflection.MethodInfo _hostMethod;
        private static bool _bridgeResolved;

        internal struct ServerEntry
        {
            public string Code;
            public string Host;
            public int Port;
            public int Players;
            public int Capacity;
            public bool Alive;
            public string Started;
        }
        private struct PlayerRow { public int Slot; public string Name; public ulong SteamId; }

        private void Awake()
        {
            Log = Logger;
            Instance = this;

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
            {
                // FALLBACK: derive the lobby endpoint from the SAME oracle the
                // client connects to (-address / SF_ORACLE_ADDRESS), defaulting to
                // the lobby HTTP port 8080. This is why the menu said "needs
                // endpoint": the launcher passes -address but not SF_LOBBY_ENDPOINT.
                // Now BROWSE/JOIN/CREATE just work without an extra env var.
                string host = ResolveOracleHost();
                if (!string.IsNullOrEmpty(host))
                {
                    string port = Environment.GetEnvironmentVariable("SF_LOBBY_PORT");
                    if (string.IsNullOrEmpty(port)) port = "8080";
                    _lobbyEndpoint = "http://" + host + ":" + port + "/lobbies";
                    Log.LogInfo($"{PluginName}: SF_LOBBY_ENDPOINT not set — derived {_lobbyEndpoint} from -address.");
                }
                else
                {
                    _lobbyEndpoint = null;
                    Log.LogWarning($"{PluginName}: no SF_LOBBY_ENDPOINT and no -address — browser disabled. Set SF_LOBBY_ENDPOINT or launch with -address HOST.");
                }
            }

            Log.LogInfo($"{PluginName} v{PluginVersion} starting. Endpoint: {_lobbyEndpoint}");
            SceneManager.sceneLoaded += OnSceneLoaded;

            // The native uGUI lobby overlay (native-uGUI style) lives on its own
            // GameObject so it survives scene loads. Toggle with F2 / Tab.
            try
            {
                var go = new GameObject("ALKALobbyOverlay");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _lobbyUi = go.AddComponent<LobbyOverlay>();
                _lobbyUi.Owner = this;
            }
            catch (Exception e) { Log.LogWarning($"{PluginName}: lobby overlay init failed: {e.Message}"); }
        }

        internal LobbyOverlay _lobbyUi;

        private void OnSceneLoaded(Scene s, LoadSceneMode m)
        {
            _currentSceneName = s.name;
            // leaving to a match scene closes the browser; entering menu closes HUD
            if (s.name != "MainScene") _overlayOpen = false;
            else _showHud = false;
            // a scene change usually means our join landed
            if (_joiningSince > 0f && s.name != "MainScene")
            {
                PushToast("Joined " + (_joiningCode ?? "lobby") + " — loading map…", 1);
                _joiningSince = -1f;
            }
        }

        private void Update()
        {
            // Animate overlay open/close on unscaled time (menus may pause time).
            float target = _overlayOpen ? 1f : 0f;
            _overlayAnim = Mathf.MoveTowards(_overlayAnim, target, Time.unscaledDeltaTime / 0.18f);

            if (Input.GetKeyDown(KeyCode.F3) && _currentSceneName != "MainScene")
                _showHud = !_showHud;

            // Esc closes the overlay if open (don't swallow the vanilla pause).
            if (_overlayOpen && Input.GetKeyDown(KeyCode.Escape))
                _overlayOpen = false;

            // expire a stuck connecting overlay
            if (_joiningSince > 0f && Time.unscaledTime - _joiningSince > JoiningOverlayTtl)
            {
                _joiningSince = -1f;
                PushToast("Still connecting… check the server is up.", 0);
            }
        }

        private void OnGUI()
        {
            var prevMatrix = BeginScaledUI();
            try
            {
                DrawEntryButton();

                if (_overlayAnim > 0.001f) DrawOverlay();
                if (_showHud) DrawHudPanel();
                if (_joiningSince > 0f) DrawConnectingOverlay();

                DrawToasts();
                DrawTooltipLayer();
            }
            finally
            {
                EndScaledUI(prevMatrix);
            }
        }

        // ====================================================================
        //  DATA LAYER — lobby list (GET) + create (POST) + cross-asm join/host.
        // ====================================================================

        private void RefreshServers()
        {
            if (_isFetching) return;
            if (Time.realtimeSinceStartup - _lastFetchAt < FetchCooldown)
            {
                _statusText = $"Wait {FetchCooldown - (Time.realtimeSinceStartup - _lastFetchAt):0.0}s…";
                return;
            }
            _lastFetchAt = Time.realtimeSinceStartup;
            _isFetching = true;
            _statusText = "Fetching lobbies…";
            StartCoroutine(FetchServersCoroutine());
        }

        // WebClient with a finite timeout (stock WebClient has none → a dead host
        // would block the worker thread ~100s).
        private sealed class TimedWebClient : WebClient
        {
            protected override WebRequest GetWebRequest(Uri address)
            {
                var req = base.GetWebRequest(address);
                if (req != null) req.Timeout = 6000;
                return req;
            }
        }

        private IEnumerator FetchServersCoroutine()
        {
            if (string.IsNullOrEmpty(_lobbyEndpoint))
            {
                _statusText = "Set SF_LOBBY_ENDPOINT to use the browser.";
                _servers.Clear();
                _isFetching = false;
                yield break;
            }
            string body = null, err = null;
            int t0 = Environment.TickCount;
            var t = new System.Threading.Thread(new System.Threading.ThreadStart(delegate
            {
                try
                {
                    using (var wc = new TimedWebClient())
                    {
                        wc.Headers.Add("User-Agent", "SFServerBrowser/" + PluginVersion);
                        body = wc.DownloadString(_lobbyEndpoint);
                    }
                }
                catch (Exception e) { err = e.Message; }
            }));
            t.IsBackground = true;
            t.Start();
            while (t.IsAlive) yield return null;

            _isFetching = false;
            if (err != null) { _statusText = "Error: " + err; _servers.Clear(); _pingMs = -1; yield break; }
            // Round-trip to the lobby endpoint ≈ a rough server ping for the UI.
            _pingMs = Mathf.Max(0, Environment.TickCount - t0);
            ParseServers(body);
        }

        private void ParseServers(string json)
        {
            _servers.Clear();
            if (string.IsNullOrEmpty(json)) { _statusText = "Empty response."; return; }
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
                    else if (c == '}')
                    {
                        depth--;
                        if (depth == 0 && start >= 0)
                        {
                            string obj = arr.Substring(start, i - start + 1);
                            var e = new ServerEntry();
                            e.Code = ExtractString(obj, "code") ?? "?";
                            e.Host = ExtractString(obj, "host") ?? "";
                            e.Port = ExtractInt(obj, "port", 1337);
                            e.Players = ExtractInt(obj, "players", 0);
                            e.Capacity = ExtractInt(obj, "capacity", 4);
                            e.Alive = ExtractBool(obj, "alive", true);
                            e.Started = ExtractString(obj, "started") ?? "";
                            _servers.Add(e);
                            start = -1;
                        }
                    }
                }
                _statusText = _servers.Count == 0 ? "No lobbies running." : $"{_servers.Count} lobby(ies) online.";
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
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '"') continue;
                if (c == ',' || c == '}') break;
                sb.Append(c);
            }
            int v;
            if (int.TryParse(sb.ToString(), out v)) return v;
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

        // ---- Join (no restart) ----------------------------------------------
        private void JoinLobby(string code)
        {
            code = SanitizeCode(code);
            if (code.Length == 0) { PushToast("Enter a lobby code first.", 2); return; }
            if (TryInvokeJoin(code))
            {
                _joiningCode = code;
                _joiningSince = Time.unscaledTime;
                _overlayOpen = false;
                PushToast("Connecting to " + code + "…", 0);
                Log.LogInfo("[browser] JOIN " + code + " via SFClientRecon.");
                return;
            }
            // Fallback: SFClientRecon absent — copy launch args for a manual restart.
            string host = EndpointHost(); int port = 1337;
            for (int i = 0; i < _servers.Count; i++)
            {
                if (_servers[i].Code == code)
                {
                    if (!string.IsNullOrEmpty(_servers[i].Host)) host = _servers[i].Host;
                    if (_servers[i].Port > 0) port = _servers[i].Port;
                    break;
                }
            }
            string args = "-address " + host + " -port " + port;
            try { GUIUtility.systemCopyBuffer = args; } catch { }
            PushToast("client-recon missing. Copied launch args — restart SF.", 2, 5f);
            Log.LogInfo("[browser] fallback clipboard: " + args);
        }

        private string EndpointHost()
        {
            if (string.IsNullOrEmpty(_lobbyEndpoint)) return "SERVER_IP";
            try { return new Uri(_lobbyEndpoint).Host; } catch { return "SERVER_IP"; }
        }

        // Resolve the oracle host from the same sources SfOracleLobbyConnect uses:
        // the -address launch arg, then SF_ORACLE_ADDRESS, then the endpoint file
        // BepInEx writes. Lets the browser auto-configure off the game's launch.
        private static string ResolveOracleHost()
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++)
                    if (args[i] == "-address" && !string.IsNullOrEmpty(args[i + 1]))
                        return args[i + 1].Trim();
            }
            catch { }
            var env = Environment.GetEnvironmentVariable("SF_ORACLE_ADDRESS");
            if (!string.IsNullOrEmpty(env)) return env.Trim();
            try
            {
                string gameRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, ".."));
                string[] candidates =
                {
                    System.IO.Path.Combine(System.IO.Path.Combine(System.IO.Path.Combine(gameRoot, "BepInEx"), "config"), "sf-oracle-endpoint.txt"),
                    System.IO.Path.Combine(System.IO.Path.Combine(System.IO.Path.Combine(gameRoot, "BepInEx"), "plugins"), "sf-oracle-endpoint.txt"),
                };
                foreach (var path in candidates)
                {
                    if (!System.IO.File.Exists(path)) continue;
                    var lines = System.IO.File.ReadAllLines(path);
                    if (lines.Length == 0) continue;
                    string line = (lines[0] ?? "").Trim();
                    if (line.Length == 0) continue;
                    int colon = line.IndexOf(':');
                    return colon > 0 ? line.Substring(0, colon).Trim() : line;
                }
            }
            catch { }
            return null;
        }

        private static void ResolveBridges()
        {
            if (_bridgeResolved) return;
            _bridgeResolved = true;
            var t = AccessTools.TypeByName("SFClientRecon.Plugin");
            if (t == null) return;
            _joinMethod = AccessTools.Method(t, "RequestJoinLobby", new[] { typeof(string) });
            _hostMethod = AccessTools.Method(t, "RequestHostLobby", new[] { typeof(string) });
        }

        private static bool TryInvokeJoin(string code)
        {
            ResolveBridges();
            if (_joinMethod == null) return false;
            try { _joinMethod.Invoke(null, new object[] { code }); return true; }
            catch (Exception e) { Log.LogWarning("[browser] RequestJoinLobby failed: " + e.Message); return false; }
        }

        // Host a *brand new* lobby in-place: create on the backend, then SELECT +
        // connect to it. RequestHostLobby (client-recon) performs the SELECT +
        // oracle-connect once the code is known.
        private static bool TryInvokeHost(string code)
        {
            ResolveBridges();
            if (_hostMethod == null) return TryInvokeJoin(code); // graceful: join path works too
            try { _hostMethod.Invoke(null, new object[] { code }); return true; }
            catch (Exception e) { Log.LogWarning("[browser] RequestHostLobby failed: " + e.Message); return false; }
        }

        // ---- Create a lobby on demand (POST to serve-lobbies) ----------------
        private void CreateLobby()
        {
            if (_isCreating) return;
            if (Time.realtimeSinceStartup < _createCooldownAt)
            {
                PushToast("Slow down — wait a moment before creating again.", 0);
                return;
            }
            _createCooldownAt = Time.realtimeSinceStartup + 5f;
            _isCreating = true;
            StartCoroutine(CreateLobbyCoroutine());
        }

        private IEnumerator CreateLobbyCoroutine()
        {
            if (string.IsNullOrEmpty(_lobbyEndpoint))
            {
                PushToast("Set SF_LOBBY_ENDPOINT to create lobbies.", 2);
                _isCreating = false;
                yield break;
            }
            string body = null, err = null;
            string url = _lobbyEndpoint;
            string token = Environment.GetEnvironmentVariable("SF_CONTROL_TOKEN") ?? "";
            // Pass the chosen options as a small JSON body (serve-lobbies may use
            // or ignore them; the create still works either way).
            string payload = "{\"maxPlayers\":" + _createMaxPlayers
                           + ",\"public\":" + (_createPublic ? "true" : "false")
                           + ",\"mode\":" + _createMode + "}";
            var t = new System.Threading.Thread(new System.Threading.ThreadStart(delegate
            {
                try
                {
                    using (var wc = new TimedWebClient())
                    {
                        wc.Headers.Add("User-Agent", "SFServerBrowser/" + PluginVersion);
                        wc.Headers.Add("Content-Type", "application/json");
                        if (token.Length > 0) wc.Headers.Add("X-SF-Token", token);
                        body = wc.UploadString(url, "POST", payload);
                    }
                }
                catch (Exception e) { err = e.Message; }
            }));
            t.IsBackground = true;
            t.Start();
            while (t.IsAlive) yield return null;

            _isCreating = false;
            if (err != null) { PushToast("Create failed: " + err, 2, 4.5f); yield break; }
            string code = ExtractString(body, "code");
            if (string.IsNullOrEmpty(code)) { PushToast("Create: no code in response.", 2); yield break; }
            Log.LogInfo("[browser] created lobby " + code + " — hosting.");
            PushToast("Lobby " + code + " created!", 1);
            // host-path so we enter as the owner of the fresh lobby
            code = SanitizeCode(code);
            _joiningCode = code;
            _joiningSince = Time.unscaledTime;
            _overlayOpen = false;
            TryInvokeHost(code);
        }

        // ---- Connected players (in-match HUD) --------------------------------
        private List<PlayerRow> GetConnectedPlayers()
        {
            var list = new List<PlayerRow>();
            try
            {
                var mmType = AccessTools.TypeByName("MultiplayerManager");
                if ((object)mmType == null) return list;
                var inst = UnityEngine.Object.FindObjectOfType(mmType);
                if ((object)inst == null) return list;
                var clientsField = AccessTools.Field(mmType, "ConnectedClients")
                                ?? AccessTools.Field(mmType, "mConnectedClients");
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
                    string name = "Player " + i;
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

        // ====================================================================
        //  INTERNAL API consumed by the native uGUI LobbyOverlay. Thin wrappers
        //  over the data layer so both UIs (IMGUI + uGUI) share one source.
        // ====================================================================
        internal List<ServerEntry> UiServers { get { return _servers; } }
        internal bool UiFetching { get { return _isFetching; } }
        internal bool UiCreating { get { return _isCreating; } }
        internal string UiStatusText { get { return _statusText; } }
        internal bool UiHasEndpoint { get { return !string.IsNullOrEmpty(_lobbyEndpoint); } }
        internal string UiEndpointHost { get { return EndpointHost(); } }
        internal string UiJoiningCode { get { return _joiningSince > 0f ? _joiningCode : null; } }
        internal int UiPingMs { get { return _pingMs; } }
        private int _pingMs = -1;

        internal void UiRefresh() { RefreshServers(); }
        internal void UiJoinCode(string code) { JoinLobby(code); }
        internal void UiCreate(int maxPlayers, bool isPublic)
        {
            _createMaxPlayers = Mathf.Clamp(maxPlayers, 2, 8);
            _createPublic = isPublic;
            _createMode = 0;
            CreateLobby();
        }

        // Code of the lobby we're currently connected to (from SFClientRecon).
        private static System.Reflection.FieldInfo _selCodeField;
        private static bool _selCodeTried;
        internal string UiCurrentLobbyCode()
        {
            try
            {
                if (!_selCodeTried)
                {
                    _selCodeTried = true;
                    var t = AccessTools.TypeByName("SFClientRecon.Plugin");
                    if (t != null) _selCodeField = AccessTools.Field(t, "SelectedLobbyCode");
                }
                if (_selCodeField != null)
                {
                    var v = _selCodeField.GetValue(null) as string;
                    if (!string.IsNullOrEmpty(v)) return v;
                }
            }
            catch { }
            return null;
        }

        internal bool UiInMatch { get { return _currentSceneName != "MainScene" && _currentSceneName != ""; } }

        // Player roster as display lines for the overlay's player list.
        internal List<string> UiPlayerLines()
        {
            var rows = GetConnectedPlayers();
            var outp = new List<string>(rows.Count);
            for (int i = 0; i < rows.Count; i++) outp.Add(rows[i].Name);
            return outp;
        }

        // Uppercase A-Z0-9 only, max 8 (explicit loop — no LINQ, Mono 2.0 safe).
        private static string SanitizeCode(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(8);
            for (int i = 0; i < s.Length && sb.Length < 8; i++)
            {
                char u = char.ToUpper(s[i]);
                if ((u >= 'A' && u <= 'Z') || (u >= '0' && u <= '9')) sb.Append(u);
            }
            return sb.ToString();
        }
    }
}
