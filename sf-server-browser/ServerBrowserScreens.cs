using System.Collections.Generic;
using UnityEngine;

namespace SFServerBrowser
{
    // ============================================================================
    //  ServerBrowserScreens — the presentation layer (what each screen looks like).
    //
    //  All drawing happens in design space (1920x1080 reference) inside the scaled
    //  matrix set by BeginScaledUI, so layout is resolution-independent. Screens
    //  pull data from the core (SFServerBrowser.cs) and render with UiWidgets.
    // ============================================================================
    public partial class Plugin
    {
        // ---- Main-menu entry button ------------------------------------------
        private void DrawEntryButton()
        {
            if (_currentSceneName != "MainScene" && _currentSceneName != "") return;
            if (_overlayOpen) return;

            float w = 230f, h = 64f;
            var r = new Rect(VW - w - 28f, 28f, w, h);
            // subtle attention pulse so players notice it
            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 2.2f);
            DropShadow(r, 12, 10f + pulse * 4f, 0.28f);
            if (ThemedButton(r, "PLAY ONLINE", 0, "entry", true, "Browse, create or join ALKA lobbies"))
            {
                _overlayOpen = true;
                _tab = Tab.Browse;
                _focusJoinField = false;
                RefreshServers();
            }
        }

        // ---- Overlay shell (scrim + panel + tabs) ----------------------------
        private void DrawOverlay()
        {
            float a = EaseOutCubic(_overlayAnim);

            // Scrim — dim the game and capture click-outside-to-close.
            var prevC = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, a);
            FillRect(new Rect(0, 0, VW, VH), new Color(ColScrim.r, ColScrim.g, ColScrim.b, ColScrim.a));
            if (_overlayOpen && GUI.Button(new Rect(0, 0, VW, VH), GUIContent.none, GUIStyle.none))
                _overlayOpen = false;

            // Panel — fixed design size, slides up slightly as it appears.
            float pw = 960f, ph = 640f;
            float slide = (1f - a) * 26f;
            var panel = new Rect((VW - pw) / 2f, (VH - ph) / 2f + slide, pw, ph);

            PanelSurface(panel, 22);
            // Catch-all over the panel so clicks on empty panel area are consumed
            // here (no-op) instead of falling through to the scrim's close button.
            // Real buttons are drawn after this and still win (later control = top).
            GUI.Button(panel, GUIContent.none, GUIStyle.none);

            // Header bar
            var header = new Rect(panel.x, panel.y, panel.width, 84f);
            RoundRect(new Rect(header.x, header.y, header.width, header.height), ColHeader, 22);
            RoundRect(new Rect(header.x, header.y + header.height - 22, header.width, 22), ColHeader, 1);
            GUI.Label(new Rect(header.x + S5, header.y, 520, header.height),
                "ALKA  ·  ONLINE", StLabel(FzTitle, ColPaperText, FontStyle.Bold, TextAnchor.MiddleLeft));
            GUI.Label(new Rect(header.x + S5, header.y + 50, 520, 24),
                "Centralized multiplayer", StLabel(FzSmall, ColInkFaint, FontStyle.Normal, TextAnchor.MiddleLeft));

            // Close X
            var xR = new Rect(header.xMax - 64, header.y + 18, 46, 46);
            if (ThemedButton(xR, "×", 1, "close", true, "Close (Esc)")) _overlayOpen = false;

            // Tabs
            var tabsR = new Rect(panel.x + S5, header.yMax + S4, 420, 48);
            string[] tabLabels = { "BROWSE", "CREATE", "JOIN" };
            _tab = (Tab)SegmentedTabs(tabsR, tabLabels, (int)_tab, "maintabs", 11, true);

            // Content region
            var content = new Rect(panel.x + S5, tabsR.yMax + S4, panel.width - S5 * 2,
                                   panel.yMax - (tabsR.yMax + S4) - S6);
            switch (_tab)
            {
                case Tab.Browse: DrawBrowseTab(content); break;
                case Tab.Create: DrawCreateTab(content); break;
                case Tab.Join:   DrawJoinTab(content); break;
            }

            // Footer status line
            var footer = new Rect(panel.x + S5, panel.yMax - S6 + 2, panel.width - S5 * 2, 22);
            string ep = string.IsNullOrEmpty(_lobbyEndpoint) ? "endpoint not set" : EndpointHost();
            GUI.Label(footer, "·  " + ep + "   " + _statusText,
                StLabel(FzTiny, ColInkFaint, FontStyle.Normal, TextAnchor.MiddleLeft));

            GUI.color = prevC;
        }

        // ---- BROWSE tab -------------------------------------------------------
        private void DrawBrowseTab(Rect c)
        {
            // toolbar
            var refreshR = new Rect(c.x, c.y, 150, 44);
            if (ThemedButton(refreshR, _isFetching ? "..." : "REFRESH", 1, "refresh", !_isFetching))
                RefreshServers();
            if (_isFetching) Spinner(new Vector2(refreshR.center.x, refreshR.center.y), 12f, ColAccent);

            GUI.Label(new Rect(c.x + 168, c.y, c.width - 168, 44),
                _servers.Count > 0 ? _servers.Count + " online" : "",
                StLabel(FzSmall, ColInkSoft, FontStyle.Bold, TextAnchor.MiddleLeft));

            // list area
            var listR = new Rect(c.x, c.y + 56, c.width, c.height - 56);
            RoundRect(listR, new Color(0f, 0f, 0f, 0.16f), 14);

            if (_servers.Count == 0)
            {
                string msg = string.IsNullOrEmpty(_lobbyEndpoint)
                    ? "Server browser disabled.\nSet SF_LOBBY_ENDPOINT to a serve-lobbies URL."
                    : (_isFetching ? "Searching for lobbies…" : "No lobbies running.\nHit CREATE to start one.");
                GUI.Label(listR, msg, StLabel(FzBody, ColInkFaint, FontStyle.Normal, TextAnchor.MiddleCenter));
                return;
            }

            float rowH = 78f, pad = 10f;
            var viewR = new Rect(listR.x + pad, listR.y + pad, listR.width - pad * 2, listR.height - pad * 2);
            float innerH = _servers.Count * (rowH + 8f);
            _serversScroll = GUI.BeginScrollView(viewR, _serversScroll,
                new Rect(0, 0, viewR.width - 18, innerH));
            for (int i = 0; i < _servers.Count; i++)
                DrawServerCard(new Rect(0, i * (rowH + 8f), viewR.width - 18, rowH), _servers[i], i);
            GUI.EndScrollView();
        }

        private void DrawServerCard(Rect r, ServerEntry s, int idx)
        {
            // Hover is handled by the JOIN button itself; the card stays calm so a
            // long list doesn't shimmer. (Scroll-local hit-testing in IMGUI is
            // unreliable across the scroll matrix, so we don't fake row hover.)
            RoundRect(r, ColRow, 12);
            RoundRect(new Rect(r.x, r.y, 5f, r.height), CapacityColor(s), 2); // status spine

            GUI.Label(new Rect(r.x + S4, r.y + 10, 240, 34),
                s.Code, StLabel(FzH2 + 4, ColPaperText, FontStyle.Bold, TextAnchor.MiddleLeft));
            string sub = (string.IsNullOrEmpty(s.Host) ? "router" : s.Host) + ":" + s.Port;
            GUI.Label(new Rect(r.x + S4, r.y + 44, 320, 24),
                sub, StLabel(FzSmall, ColInkFaint, FontStyle.Normal, TextAnchor.MiddleLeft));

            // player pill
            var pillR = new Rect(r.x + 280, r.y + (r.height - 30) / 2f, 132, 30);
            Pill(pillR, s.Players + " / " + s.Capacity + " players", CapacityColor(s));

            // status pill
            var stR = new Rect(pillR.xMax + 12, r.y + (r.height - 30) / 2f, 96, 30);
            Pill(stR, s.Alive ? "ALIVE" : "DOWN", s.Alive ? ColGood : ColBad);

            // join
            var joinR = new Rect(r.xMax - 132, r.y + (r.height - 46) / 2f, 120, 46);
            bool full = s.Capacity > 0 && s.Players >= s.Capacity;
            if (ThemedButton(joinR, full ? "FULL" : "JOIN", full ? 1 : 3, "join" + idx, !full && s.Alive))
                JoinLobby(s.Code);
        }

        private static Color CapacityColor(ServerEntry s)
        {
            if (s.Capacity <= 0) return ColGood;
            float f = (float)s.Players / s.Capacity;
            if (f >= 1f) return ColBad;
            if (f >= 0.6f) return ColWarn;
            return ColGood;
        }

        // ---- CREATE tab -------------------------------------------------------
        private void DrawCreateTab(Rect c)
        {
            float x = c.x + S2, w = c.width - S4;
            float y = c.y + S2;

            GUI.Label(new Rect(x, y, w, 30), "NEW LOBBY",
                StLabel(FzH2, ColInk, FontStyle.Bold, TextAnchor.MiddleLeft)); y += 44;

            // Max players stepper
            DrawFieldLabel(new Rect(x, y, w, 22), "MAX PLAYERS"); y += 28;
            var stepR = new Rect(x, y, 300, 52);
            RoundRect(stepR, ColField, 12);
            if (ThemedButton(new Rect(stepR.x + 4, stepR.y + 4, 48, 44), "-", 1, "pmin", _createMaxPlayers > 2))
                _createMaxPlayers = Mathf.Max(2, _createMaxPlayers - 1);
            GUI.Label(new Rect(stepR.x + 52, stepR.y, stepR.width - 104, stepR.height),
                _createMaxPlayers.ToString(), StLabel(FzTitle, ColAccentHi, FontStyle.Bold, TextAnchor.MiddleCenter));
            if (ThemedButton(new Rect(stepR.xMax - 52, stepR.y + 4, 48, 44), "+", 1, "pmax", _createMaxPlayers < 8))
                _createMaxPlayers = Mathf.Min(8, _createMaxPlayers + 1);
            y += 70;

            // Visibility
            DrawFieldLabel(new Rect(x, y, w, 22), "VISIBILITY"); y += 28;
            string[] vis = { "PUBLIC", "PRIVATE" };
            int visSel = SegmentedTabs(new Rect(x, y, 300, 48), vis, _createPublic ? 0 : 1, "vis", 9, true);
            _createPublic = visSel == 0;
            y += 66;

            // Mode (cosmetic hint passed to backend)
            DrawFieldLabel(new Rect(x, y, w, 22), "MODE"); y += 28;
            string[] modes = { "NORMAL", "MODDED", "CHAOS" };
            _createMode = SegmentedTabs(new Rect(x, y, 400, 48), modes, _createMode, "mode", 9, false);
            y += 70;

            // helper
            GUI.Label(new Rect(x, y, w, 40),
                _createPublic ? "Public lobbies appear in BROWSE for everyone."
                              : "Private lobbies are join-by-code only.",
                StLabel(FzSmall, ColInkFaint, FontStyle.Normal, TextAnchor.UpperLeft));

            // big create button (bottom)
            var createR = new Rect(c.x + S2, c.yMax - 64, c.width - S4, 56);
            bool canCreate = !_isCreating && !string.IsNullOrEmpty(_lobbyEndpoint);
            if (ThemedButton(createR, _isCreating ? "CREATING…" : "CREATE & HOST LOBBY", 0, "create", canCreate))
                CreateLobby();
            if (_isCreating) Spinner(new Vector2(createR.center.x - 96, createR.center.y), 12f, ColAccentInk);
        }

        private void DrawFieldLabel(Rect r, string text)
        {
            GUI.Label(r, text, StLabel(FzTiny, ColInkSoft, FontStyle.Bold, TextAnchor.MiddleLeft));
        }

        // ---- JOIN tab ---------------------------------------------------------
        private void DrawJoinTab(Rect c)
        {
            float fieldW = 460f, fieldH = 84f;
            var fr = new Rect(c.x + (c.width - fieldW) / 2f, c.y + 60, fieldW, fieldH);

            GUI.Label(new Rect(c.x, c.y + 8, c.width, 30), "ENTER LOBBY CODE",
                StLabel(FzH2, ColInk, FontStyle.Bold, TextAnchor.MiddleCenter));

            GUI.SetNextControlName("joinCode");
            bool focused = GUI.GetNameOfFocusedControl() == "joinCode";
            string before = _joinCodeInput ?? "";
            string after = ThemedField(fr, "joinCode", before, 8, FzDisplay, focused);
            _joinCodeInput = SanitizeCode(after);
            if (_focusJoinField) { GUI.FocusControl("joinCode"); _focusJoinField = false; }

            // length hint dots
            float dotY = fr.yMax + 16, dotR = 10, gap = 16;
            int shown = 4;
            float totalW = shown * dotR + (shown - 1) * gap;
            float dx = c.x + (c.width - totalW) / 2f;
            for (int i = 0; i < shown; i++)
            {
                Color dc = i < _joinCodeInput.Length ? ColAccent : new Color(1f, 1f, 1f, 0.18f);
                RoundRect(new Rect(dx + i * (dotR + gap), dotY, dotR, dotR), dc, 5);
            }

            GUI.Label(new Rect(c.x, dotY + 24, c.width, 24),
                _joinCodeInput.Length == 0 ? "Codes are 4 characters (A–Z, 0–9)."
                                           : "Press JOIN to connect.",
                StLabel(FzSmall, ColInkFaint, FontStyle.Normal, TextAnchor.MiddleCenter));

            // paste + join
            var pasteR = new Rect(c.x + (c.width - 460) / 2f, dotY + 60, 150, 50);
            if (ThemedButton(pasteR, "PASTE", 1, "paste"))
            {
                try { _joinCodeInput = SanitizeCode(GUIUtility.systemCopyBuffer); } catch { }
            }
            var joinR = new Rect(pasteR.xMax + 12, dotY + 60, 298, 50);
            if (ThemedButton(joinR, "JOIN LOBBY", 0, "joinbtn", _joinCodeInput.Length > 0))
                JoinLobby(_joinCodeInput);
        }

        // ---- In-match player list (F3) ---------------------------------------
        private void DrawHudPanel()
        {
            float pw = 460f, ph = 460f;
            var panel = new Rect(VW - pw - 28f, 90f, pw, ph);
            PanelSurface(panel, 18);

            var header = new Rect(panel.x, panel.y, panel.width, 64f);
            RoundRect(header, ColHeader, 18);
            RoundRect(new Rect(header.x, header.yMax - 18, header.width, 18), ColHeader, 1);
            GUI.Label(new Rect(header.x + S4, header.y, 300, header.height),
                "PLAYERS", StLabel(FzH2, ColPaperText, FontStyle.Bold, TextAnchor.MiddleLeft));
            GUI.Label(new Rect(header.xMax - 130, header.y, 110, header.height),
                "F3 to close", StLabel(FzTiny, ColInkFaint, FontStyle.Normal, TextAnchor.MiddleRight));

            var list = GetConnectedPlayers();
            var body = new Rect(panel.x + S3, header.yMax + S2, panel.width - S3 * 2, panel.yMax - header.yMax - S4);
            if (list.Count == 0)
            {
                GUI.Label(body, "No players detected yet.",
                    StLabel(FzSmall, ColInkFaint, FontStyle.Normal, TextAnchor.MiddleCenter));
                return;
            }
            float rowH = 52f;
            _hudScroll = GUI.BeginScrollView(body, _hudScroll,
                new Rect(0, 0, body.width - 18, list.Count * (rowH + 6f)));
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                var rr = new Rect(0, i * (rowH + 6f), body.width - 18, rowH);
                RoundRect(rr, ColRow, 10);
                RoundRect(new Rect(rr.x + 10, rr.y + (rowH - 28) / 2f, 28, 28), ColAccent, 8);
                GUI.Label(new Rect(rr.x + 10, rr.y, 28, rowH), p.Slot.ToString(),
                    StLabel(FzSmall, ColAccentInk, FontStyle.Bold, TextAnchor.MiddleCenter));
                GUI.Label(new Rect(rr.x + 50, rr.y, rr.width - 60, rowH), p.Name,
                    StLabel(FzBody, ColPaperText, FontStyle.Bold, TextAnchor.MiddleLeft));
            }
            GUI.EndScrollView();
        }

        // ---- Connecting overlay ----------------------------------------------
        private void DrawConnectingOverlay()
        {
            float pw = 420f, ph = 200f;
            var panel = new Rect((VW - pw) / 2f, (VH - ph) / 2f, pw, ph);
            FillRect(new Rect(0, 0, VW, VH), new Color(0.02f, 0.02f, 0.05f, 0.40f));
            PanelSurface(panel, 18);
            Spinner(new Vector2(panel.center.x, panel.y + 80), 26f, ColAccent);
            GUI.Label(new Rect(panel.x, panel.y + 120, panel.width, 30),
                "Connecting to " + (_joiningCode ?? "lobby"),
                StLabel(FzH2, ColInk, FontStyle.Bold, TextAnchor.MiddleCenter));
            GUI.Label(new Rect(panel.x, panel.y + 150, panel.width, 24),
                "Establishing UDP session…",
                StLabel(FzSmall, ColInkFaint, FontStyle.Normal, TextAnchor.MiddleCenter));
        }
    }
}
