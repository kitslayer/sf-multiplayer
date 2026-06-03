using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SFServerBrowser
{
    // ============================================================================
    //  LobbyOverlay — the native uGUI "hyper-complete" lobby (Centauri-style).
    //
    //  Toggle with F2 (works in the menu AND in a match). Three tabs:
    //    • LOBBY  — current code + COPY, host/ping/status, connected players,
    //               CREATE / START / LEAVE.
    //    • BROWSE — auto-refreshing list of public lobbies with JOIN buttons.
    //    • JOIN   — type a code and connect.
    //
    //  Built entirely from runtime uGUI (UguiKit), the same approach the
    //  reference mod uses, so it renders crisply on this game build. All data
    //  goes through Plugin's internal API (shared with the IMGUI browser).
    // ============================================================================
    internal class LobbyOverlay : MonoBehaviour
    {
        internal Plugin Owner;

        private bool _open;
        private bool _built;
        private int _tab;                       // 0 lobby, 1 browse, 2 join
        private float _nextPoll;                // players/status refresh cadence
        private float _nextBrowse;              // server-list refresh cadence
        private float _animT;                   // 0..1 open animation

        // Roots
        private Canvas _canvas;
        private GameObject _root;
        private CanvasGroup _group;
        private RectTransform _card;

        // Tabs
        private readonly GameObject[] _panels = new GameObject[3];
        private readonly Button[] _tabBtns = new Button[3];

        // Lobby tab dynamic refs
        private Text _codeText, _statusText, _hostText, _pingText, _playersEmpty;
        private RectTransform _playersBox;
        private Button _startBtn, _leaveBtn;

        // Browse tab
        private RectTransform _serverBox;
        private Text _browseStatus;

        // Join tab
        private InputField _joinInput;

        // ---- lifecycle --------------------------------------------------------
        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F2)) Toggle();
            if (_open && Input.GetKeyDown(KeyCode.Escape)) SetOpen(false);

            // open/close animation (scale + fade) on unscaled time
            float target = _open ? 1f : 0f;
            _animT = Mathf.MoveTowards(_animT, target, Time.unscaledDeltaTime / 0.16f);
            if (_root != null)
            {
                bool active = _animT > 0.001f;
                if (_root.activeSelf != active) _root.SetActive(active);
                if (active)
                {
                    float e = 1f - Mathf.Pow(1f - _animT, 3f);   // ease-out cubic
                    if (_group != null) _group.alpha = e;
                    if (_card != null) _card.localScale = Vector3.one * Mathf.Lerp(0.96f, 1f, e);
                }
            }

            if (!_open || Owner == null) return;

            // poll players + status ~2 Hz
            if (Time.unscaledTime >= _nextPoll)
            {
                _nextPoll = Time.unscaledTime + 0.5f;
                RefreshLobbyTab();
            }
            // auto-refresh the public list while BROWSE is visible
            if (_tab == 1 && Time.unscaledTime >= _nextBrowse)
            {
                _nextBrowse = Time.unscaledTime + 3f;
                Owner.UiRefresh();
                RefreshServerList();
            }
        }

        // ---- open/close -------------------------------------------------------
        internal void Toggle() { SetOpen(!_open); }
        internal void SetOpen(bool open)
        {
            _open = open;
            if (open)
            {
                EnsureBuilt();
                EnsureEventSystem();
                Owner?.UiRefresh();
                SelectTab(_tab);
                RefreshLobbyTab();
                RefreshServerList();
            }
        }

        // ---- build ------------------------------------------------------------
        private void EnsureBuilt()
        {
            if (_built) return;
            try { BuildUi(); _built = true; }
            catch (Exception e) { Plugin.Log.LogWarning("[lobby-ui] build failed: " + e.Message); }
        }

        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;
            if (UnityEngine.Object.FindObjectOfType<EventSystem>() != null) return;
            var go = new GameObject("ALKAEventSystem");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<EventSystem>();
            go.AddComponent<StandaloneInputModule>();
        }

        private void BuildUi()
        {
            _canvas = Ugui.CreateCanvas("ALKALobbyCanvas", 5000);
            _root = _canvas.gameObject;
            _group = _root.AddComponent<CanvasGroup>();

            // Scrim — click to close.
            var scrim = Ugui.Panel(_root.transform, "Scrim", Vector2.zero, Vector2.one, Ugui.Scrim, false);
            scrim.raycastTarget = true;
            var sb = scrim.gameObject.AddComponent<Button>();
            sb.transition = Selectable.Transition.None;
            sb.onClick.AddListener(delegate { SetOpen(false); });

            // Centered card.
            _card = Ugui.CenteredCard(_root.transform, "Card", 980f, 640f, Ugui.PanelBg);

            BuildHeader();
            BuildTabs();

            // content region as a child panel
            var content = Ugui.Panel(_card, "Content", new Vector2(0.025f, 0.055f), new Vector2(0.975f, 0.80f), Ugui.PanelBgLight, false).gameObject;
            _panels[0] = BuildLobbyTab(content.transform);
            _panels[1] = BuildBrowseTab(content.transform);
            _panels[2] = BuildJoinTab(content.transform);

            // footer hint
            Ugui.Label(_card, "F2 toggle  ·  Esc close  ·  ALKA Online", new Vector2(0.025f, 0.005f), new Vector2(0.975f, 0.05f),
                12, Ugui.TextDim, TextAnchor.MiddleCenter, FontStyle.Normal);
        }

        private void BuildHeader()
        {
            Ugui.Panel(_card, "Header", new Vector2(0f, 0.86f), new Vector2(1f, 1f), Ugui.Header, false);
            Ugui.Label(_card, "<color=#3FD9F2>ALKA</color>  ONLINE", new Vector2(0.03f, 0.86f), new Vector2(0.6f, 1f),
                30, Ugui.TextWhite, TextAnchor.MiddleLeft);
            Ugui.Label(_card, "Centralized multiplayer lobby", new Vector2(0.03f, 0.865f), new Vector2(0.6f, 0.9f),
                13, Ugui.TextDim, TextAnchor.LowerLeft, FontStyle.Normal);
            Ugui.Btn(_card, "X", new Vector2(0.93f, 0.88f), new Vector2(0.985f, 0.98f),
                delegate { SetOpen(false); }, 20, Ugui.Bad, new Color(1f, 0.45f, 0.45f, 1f));
        }

        private void BuildTabs()
        {
            string[] labels = { "LOBBY", "BROWSE", "JOIN" };
            float x0 = 0.03f, w = 0.20f, gap = 0.012f;
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                float xa = x0 + i * (w + gap);
                _tabBtns[i] = Ugui.Btn(_card, labels[i], new Vector2(xa, 0.805f), new Vector2(xa + w, 0.852f),
                    delegate { SelectTab(idx); }, 16);
            }
        }

        private void SelectTab(int tab)
        {
            _tab = Mathf.Clamp(tab, 0, 2);
            for (int i = 0; i < 3; i++)
            {
                if (_panels[i] != null) _panels[i].SetActive(i == _tab);
                if (_tabBtns[i] != null)
                {
                    var cb = _tabBtns[i].colors;
                    cb.normalColor = i == _tab ? Ugui.AccentBtn : Ugui.ButtonBg;
                    _tabBtns[i].colors = cb;
                    var img = _tabBtns[i].GetComponent<Image>();
                    if (img != null) img.color = cb.normalColor;
                }
            }
            if (_tab == 1) { Owner?.UiRefresh(); RefreshServerList(); }
        }

        // ---- LOBBY tab --------------------------------------------------------
        private GameObject BuildLobbyTab(Transform parent)
        {
            var go = Ugui.Panel(parent, "LobbyTab", Vector2.zero, Vector2.one, new Color(0, 0, 0, 0), false).gameObject;

            // Big code + copy
            Ugui.Label(go.transform, "LOBBY CODE", new Vector2(0.04f, 0.86f), new Vector2(0.5f, 0.94f), 13, Ugui.TextDim, TextAnchor.LowerLeft);
            _codeText = Ugui.Label(go.transform, "—", new Vector2(0.04f, 0.74f), new Vector2(0.5f, 0.87f), 46, Ugui.Accent, TextAnchor.MiddleLeft);
            Ugui.Btn(go.transform, "COPY CODE", new Vector2(0.52f, 0.79f), new Vector2(0.74f, 0.87f), OnCopyCode, 15);
            Ugui.Btn(go.transform, "INVITE (COPY ARGS)", new Vector2(0.755f, 0.79f), new Vector2(0.96f, 0.87f), OnInvite, 13);

            // host / ping / status row
            _hostText = Ugui.Label(go.transform, "Host: —", new Vector2(0.04f, 0.66f), new Vector2(0.4f, 0.73f), 15, Ugui.TextWhite, TextAnchor.MiddleLeft);
            _pingText = Ugui.Label(go.transform, "Ping: —", new Vector2(0.41f, 0.66f), new Vector2(0.62f, 0.73f), 15, Ugui.Good, TextAnchor.MiddleLeft);
            _statusText = Ugui.Label(go.transform, "Status: in menu", new Vector2(0.63f, 0.66f), new Vector2(0.96f, 0.73f), 15, Ugui.Warn, TextAnchor.MiddleLeft);

            // players header + box
            Ugui.Label(go.transform, "PLAYERS", new Vector2(0.04f, 0.58f), new Vector2(0.5f, 0.64f), 14, Ugui.TextDim, TextAnchor.LowerLeft);
            _playersBox = Ugui.Panel(go.transform, "Players", new Vector2(0.04f, 0.16f), new Vector2(0.96f, 0.58f), new Color(0, 0, 0, 0.18f), false).rectTransform;
            _playersEmpty = Ugui.Label(_playersBox, "No players detected yet.", Vector2.zero, Vector2.one, 14, Ugui.TextDim, TextAnchor.MiddleCenter, FontStyle.Normal);

            // action buttons
            Ugui.Btn(go.transform, "CREATE NEW LOBBY", new Vector2(0.04f, 0.03f), new Vector2(0.36f, 0.13f), OnCreate, 17, Ugui.AccentBtn, Ugui.AccentBtnHi);
            _startBtn = Ugui.Btn(go.transform, "START MATCH", new Vector2(0.37f, 0.03f), new Vector2(0.66f, 0.13f), OnStart, 17, Ugui.GoBtn, Ugui.GoBtnHi);
            _leaveBtn = Ugui.Btn(go.transform, "LEAVE", new Vector2(0.67f, 0.03f), new Vector2(0.96f, 0.13f), OnLeave, 17, new Color(0.4f, 0.22f, 0.24f, 1f), new Color(0.6f, 0.3f, 0.32f, 1f));
            return go;
        }

        // ---- BROWSE tab -------------------------------------------------------
        private GameObject BuildBrowseTab(Transform parent)
        {
            var go = Ugui.Panel(parent, "BrowseTab", Vector2.zero, Vector2.one, new Color(0, 0, 0, 0), false).gameObject;
            Ugui.Btn(go.transform, "REFRESH", new Vector2(0.04f, 0.88f), new Vector2(0.24f, 0.97f),
                delegate { Owner?.UiRefresh(); RefreshServerList(); }, 15);
            _browseStatus = Ugui.Label(go.transform, "", new Vector2(0.26f, 0.88f), new Vector2(0.7f, 0.97f), 14, Ugui.TextDim, TextAnchor.MiddleLeft, FontStyle.Normal);
            Ugui.Btn(go.transform, "CREATE", new Vector2(0.76f, 0.88f), new Vector2(0.96f, 0.97f), OnCreate, 15, Ugui.AccentBtn, Ugui.AccentBtnHi);
            _serverBox = Ugui.Panel(go.transform, "List", new Vector2(0.04f, 0.03f), new Vector2(0.96f, 0.85f), new Color(0, 0, 0, 0.18f), false).rectTransform;
            return go;
        }

        // ---- JOIN tab ---------------------------------------------------------
        private GameObject BuildJoinTab(Transform parent)
        {
            var go = Ugui.Panel(parent, "JoinTab", Vector2.zero, Vector2.one, new Color(0, 0, 0, 0), false).gameObject;
            Ugui.Label(go.transform, "ENTER A LOBBY CODE", new Vector2(0.1f, 0.74f), new Vector2(0.9f, 0.84f), 22, Ugui.TextWhite, TextAnchor.MiddleCenter);
            _joinInput = Ugui.Input(go.transform, "CODE", new Vector2(0.28f, 0.55f), new Vector2(0.72f, 0.70f), 40, 8);
            Ugui.Btn(go.transform, "JOIN LOBBY", new Vector2(0.34f, 0.40f), new Vector2(0.66f, 0.52f), OnJoinFromInput, 18, Ugui.GoBtn, Ugui.GoBtnHi);
            Ugui.Btn(go.transform, "PASTE", new Vector2(0.34f, 0.27f), new Vector2(0.49f, 0.37f), OnPaste, 14);
            Ugui.Label(go.transform, "Codes are 4 characters (A–Z, 0–9).", new Vector2(0.1f, 0.15f), new Vector2(0.9f, 0.24f), 14, Ugui.TextDim, TextAnchor.MiddleCenter, FontStyle.Normal);
            return go;
        }

        // ---- dynamic refresh --------------------------------------------------
        private void RefreshLobbyTab()
        {
            if (Owner == null) return;
            string code = Owner.UiCurrentLobbyCode();
            if (_codeText != null) _codeText.text = string.IsNullOrEmpty(code) ? "—" : code;
            if (_hostText != null) _hostText.text = "Host: " + (Owner.UiHasEndpoint ? Owner.UiEndpointHost : "—");
            if (_pingText != null)
            {
                int ms = Owner.UiPingMs;
                _pingText.text = ms > 0 ? "Ping: " + ms + " ms" : "Ping: —";
                _pingText.color = ms <= 0 ? Ugui.TextDim : (ms < 90 ? Ugui.Good : (ms < 180 ? Ugui.Warn : Ugui.Bad));
            }
            if (_statusText != null)
            {
                string joining = Owner.UiJoiningCode;
                if (!string.IsNullOrEmpty(joining)) { _statusText.text = "Status: connecting " + joining + "…"; _statusText.color = Ugui.Warn; }
                else if (Owner.UiInMatch) { _statusText.text = "Status: in match"; _statusText.color = Ugui.Good; }
                else { _statusText.text = "Status: in menu"; _statusText.color = Ugui.TextDim; }
            }
            RefreshPlayers();
            if (_startBtn != null) _startBtn.interactable = Owner.UiInMatch == false;   // start only from lobby/menu
        }

        private void RefreshPlayers()
        {
            if (_playersBox == null || Owner == null) return;
            var names = Owner.UiPlayerLines();
            // rebuild rows (cheap: lists are tiny)
            for (int i = _playersBox.childCount - 1; i >= 0; i--)
            {
                var ch = _playersBox.GetChild(i);
                if (ch != null && ch.GetComponent<Text>() != _playersEmpty) UnityEngine.Object.Destroy(ch.gameObject);
            }
            if (_playersEmpty != null) _playersEmpty.gameObject.SetActive(names.Count == 0);

            int max = Mathf.Min(names.Count, 8);
            float rowH = 1f / 8f;
            for (int i = 0; i < max; i++)
            {
                float yTop = 1f - i * rowH, yBot = 1f - (i + 1) * rowH;
                var row = Ugui.Panel(_playersBox, "P" + i, new Vector2(0.01f, yBot + 0.005f), new Vector2(0.99f, yTop - 0.005f),
                    (i % 2 == 0) ? Ugui.RowBg : Ugui.RowBgAlt, false).gameObject;
                Ugui.Panel(row.transform, "dot", new Vector2(0.015f, 0.28f), new Vector2(0.05f, 0.72f), Ugui.Accent, false);
                Ugui.Label(row.transform, "P" + (i + 1), new Vector2(0.07f, 0f), new Vector2(0.2f, 1f), 15, Ugui.Accent, TextAnchor.MiddleLeft);
                Ugui.Label(row.transform, names[i], new Vector2(0.21f, 0f), new Vector2(0.98f, 1f), 15, Ugui.TextWhite, TextAnchor.MiddleLeft);
            }
        }

        private void RefreshServerList()
        {
            if (_serverBox == null || Owner == null) return;
            Ugui.ClearChildren(_serverBox);

            var servers = Owner.UiServers;
            if (_browseStatus != null)
                _browseStatus.text = Owner.UiFetching ? "Searching…"
                    : (!Owner.UiHasEndpoint ? "Set SF_LOBBY_ENDPOINT" : (servers.Count + " online  ·  " + Owner.UiStatusText));

            if (servers.Count == 0)
            {
                Ugui.Label(_serverBox, Owner.UiHasEndpoint ? "No lobbies. Hit CREATE to start one." : "Server browser disabled (no endpoint).",
                    Vector2.zero, Vector2.one, 15, Ugui.TextDim, TextAnchor.MiddleCenter, FontStyle.Normal);
                return;
            }

            int max = Mathf.Min(servers.Count, 7);
            float rowH = 1f / 7f;
            for (int i = 0; i < max; i++)
            {
                var s = servers[i];
                float yTop = 1f - i * rowH, yBot = 1f - (i + 1) * rowH;
                var row = Ugui.Panel(_serverBox, "S" + i, new Vector2(0.008f, yBot + 0.006f), new Vector2(0.992f, yTop - 0.006f),
                    (i % 2 == 0) ? Ugui.RowBg : Ugui.RowBgAlt, false).gameObject;

                Ugui.Label(row.transform, s.Code, new Vector2(0.02f, 0f), new Vector2(0.22f, 1f), 22, Ugui.TextWhite, TextAnchor.MiddleLeft);
                Ugui.Label(row.transform, (string.IsNullOrEmpty(s.Host) ? "router" : s.Host) + ":" + s.Port,
                    new Vector2(0.22f, 0f), new Vector2(0.52f, 1f), 13, Ugui.TextDim, TextAnchor.MiddleLeft, FontStyle.Normal);

                Color cap = CapColor(s);
                Ugui.Pill(row.transform, s.Players + "/" + s.Capacity, new Vector2(0.54f, 0.24f), new Vector2(0.69f, 0.76f), cap);
                Ugui.Pill(row.transform, s.Alive ? "ALIVE" : "DOWN", new Vector2(0.70f, 0.24f), new Vector2(0.82f, 0.76f), s.Alive ? Ugui.Good : Ugui.Bad);

                bool full = s.Capacity > 0 && s.Players >= s.Capacity;
                string code = s.Code;
                var jb = Ugui.Btn(row.transform, full ? "FULL" : "JOIN", new Vector2(0.83f, 0.18f), new Vector2(0.985f, 0.82f),
                    delegate { Owner.UiJoinCode(code); SetOpen(false); }, 15, Ugui.GoBtn, Ugui.GoBtnHi);
                jb.interactable = !full && s.Alive;
            }
            if (servers.Count > max)
                Plugin.Log.LogInfo("[lobby-ui] " + (servers.Count - max) + " more lobbies not shown.");
        }

        private static Color CapColor(Plugin.ServerEntry s)
        {
            if (s.Capacity <= 0) return Ugui.Good;
            float f = (float)s.Players / s.Capacity;
            if (f >= 1f) return Ugui.Bad;
            if (f >= 0.6f) return Ugui.Warn;
            return Ugui.Good;
        }

        // ---- actions ----------------------------------------------------------
        private void OnCreate() { Owner?.UiCreate(4, true); }
        private void OnJoinFromInput()
        {
            if (_joinInput == null) return;
            Owner?.UiJoinCode(_joinInput.text);
            SetOpen(false);
        }
        private void OnPaste()
        {
            try { if (_joinInput != null) _joinInput.text = GUIUtility.systemCopyBuffer; } catch { }
        }
        private void OnCopyCode()
        {
            try { var c = Owner?.UiCurrentLobbyCode(); if (!string.IsNullOrEmpty(c)) GUIUtility.systemCopyBuffer = c; } catch { }
        }
        private void OnInvite()
        {
            try
            {
                var c = Owner != null ? Owner.UiCurrentLobbyCode() : null;
                string host = Owner != null ? Owner.UiEndpointHost : "SERVER_IP";
                string args = string.IsNullOrEmpty(c)
                    ? ("-address " + host + " -port 1337")
                    : ("Join ALKA lobby " + c + " — server " + host);
                GUIUtility.systemCopyBuffer = args;
            }
            catch { }
        }
        private void OnLeave()
        {
            // Best-effort: return to the main menu via GameManager.ReturnToMenu/RPC.
            SetOpen(false);
            try
            {
                var gm = AccessTools.TypeByName("GameManager");
                if (gm != null)
                {
                    var inst = AccessTools.Property(gm, "Instance")?.GetValue(null, null)
                               ?? UnityEngine.Object.FindObjectOfType(gm);
                    var m = AccessTools.Method(gm, "ReturnToMainMenu") ?? AccessTools.Method(gm, "GoToMainMenu");
                    if (inst != null && m != null) m.Invoke(inst, null);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[lobby-ui] leave: " + e.Message); }
        }
        private void OnStart()
        {
            // Best-effort match start: SF's lobby uses GameManager.StartMatch /
            // MatchmakingHandler. We try a couple of known entry points and fall
            // back to a log so this never throws or soft-locks.
            try
            {
                var gm = AccessTools.TypeByName("GameManager");
                if (gm != null)
                {
                    var inst = AccessTools.Property(gm, "Instance")?.GetValue(null, null)
                               ?? UnityEngine.Object.FindObjectOfType(gm);
                    var m = AccessTools.Method(gm, "StartMatch") ?? AccessTools.Method(gm, "StartCountDown");
                    if (inst != null && m != null) { m.Invoke(inst, null); Plugin.Log.LogInfo("[lobby-ui] START invoked."); SetOpen(false); return; }
                }
                Plugin.Log.LogInfo("[lobby-ui] START: no entry point found (host controls match flow).");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[lobby-ui] start: " + e.Message); }
        }
    }
}
