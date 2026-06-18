using System.Collections.Generic;
using UnityEngine;

namespace SFServerBrowser
{
    // ============================================================================
    //  UiWidgets — reusable immediate-mode widgets built on UiTheme.
    //
    //  These are the "components" of the overlay: rounded surfaces, shadowed
    //  panels, three button variants, status pills, segmented tabs, styled text
    //  fields, a loading spinner, a toast/notification queue and a hover-tooltip
    //  layer. Each is a plain method (no retained state beyond the toast queue and
    //  per-control hover animation), which keeps them cheap and Mono-2.0-safe.
    // ============================================================================
    public partial class Plugin
    {
        // ---- Easing -----------------------------------------------------------
        internal static float EaseOutCubic(float t) { t = Mathf.Clamp01(t); float u = 1f - t; return 1f - u * u * u; }
        // Frame-rate independent approach toward a target (for hover states etc.).
        internal static float Approach(float cur, float target, float speed)
        {
            return Mathf.MoveTowards(cur, target, speed * Time.unscaledDeltaTime);
        }

        // ---- Surfaces ---------------------------------------------------------
        internal static void FillRect(Rect r, Color c)
        {
            var prev = GUI.color; GUI.color = c;
            GUI.DrawTexture(r, Solid(Color.white));
            GUI.color = prev;
        }

        internal static void RoundRect(Rect r, Color fill, int radius)
        {
            var prev = GUI.backgroundColor; GUI.backgroundColor = fill;
            GUI.Box(r, GUIContent.none, RoundStyle(radius));
            GUI.backgroundColor = prev;
        }

        // Soft drop shadow behind a rounded surface.
        internal static void DropShadow(Rect r, int radius, float spread, float alpha)
        {
            var sh = SoftShadow();
            var prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, alpha);
            // draw the soft texture expanded + nudged down for a grounded feel
            var sr = new Rect(r.x - spread, r.y - spread + spread * 0.35f, r.width + spread * 2, r.height + spread * 2);
            GUI.DrawTexture(sr, sh, ScaleMode.StretchToFill, true);
            GUI.color = prev;
        }

        // Full panel: shadow + paper surface + subtle top edge highlight.
        internal static void PanelSurface(Rect r, int radius)
        {
            DropShadow(r, radius, 26f, ColShadow.a);
            RoundRect(r, ColPanel, radius);
            // hairline edge for definition on bright backgrounds
            RoundRect(new Rect(r.x, r.y, r.width, 2f), ColPanelEdge, 1);
        }

        // ---- Buttons ----------------------------------------------------------
        // Variant: 0 = primary (accent), 1 = ghost (paper), 2 = danger, 3 = blue.
        private static readonly Dictionary<string, float> _hoverT = new Dictionary<string, float>(64);
        private static float HoverState(string key, bool hot)
        {
            float v;
            if (!_hoverT.TryGetValue(key, out v)) v = 0f;
            v = Approach(v, hot ? 1f : 0f, 8f);
            _hoverT[key] = v;
            return v;
        }

        internal static bool ThemedButton(Rect r, string label, int variant, string id, bool enabled = true, string tooltip = null)
        {
            bool hot = enabled && r.Contains(VMouse());
            float h = HoverState(id, hot);
            float pop = EaseOutCubic(h);

            Color baseCol, hiCol, textCol;
            switch (variant)
            {
                case 1:  baseCol = new Color(1f, 1f, 1f, 0.10f); hiCol = new Color(1f, 1f, 1f, 0.20f); textCol = ColPaperText; break;
                case 2:  baseCol = ColBad;   hiCol = Color.Lerp(ColBad, Color.white, 0.18f);  textCol = Color.white; break;
                case 3:  baseCol = ColBlue;  hiCol = ColBlueHi;  textCol = Color.white; break;
                default: baseCol = ColAccent; hiCol = ColAccentHi; textCol = ColAccentInk; break;
            }
            if (!enabled) { baseCol.a *= 0.4f; textCol.a *= 0.5f; }

            // lift on hover
            Rect rr = r;
            if (pop > 0f) { float lift = pop * 2f; rr = new Rect(r.x, r.y - lift, r.width, r.height); }
            if (enabled && pop > 0.01f) DropShadow(rr, 10, 8f + pop * 6f, 0.22f * pop);
            RoundRect(rr, Color.Lerp(baseCol, hiCol, pop), 10);

            var st = StLabel(FzBody, textCol, FontStyle.Bold, TextAnchor.MiddleCenter);
            GUI.Label(rr, new GUIContent(label, tooltip), st);

            if (!enabled) return false;
            return ClickedThisEvent(rr);
        }

        // Deterministic click: handle the MouseDown event by rect hit-test and
        // consume it. Immune to IMGUI control-id shifts — the server-list/fetch
        // state changed the control count between MouseDown and MouseUp, so
        // GUI.Button mapped clicks to the wrong control ("clicking closed the menu").
        internal static bool ClickedThisEvent(Rect r)
        {
            var e = Event.current;
            if (e != null && e.type == EventType.MouseDown && e.button == 0 && r.Contains(VMouse()))
            {
                e.Use();
                return true;
            }
            return false;
        }

        // ---- Status pill (player count / state) -------------------------------
        internal static void Pill(Rect r, string text, Color col)
        {
            RoundRect(r, new Color(col.r, col.g, col.b, 0.18f), 9);
            // little dot
            float d = r.height * 0.34f;
            RoundRect(new Rect(r.x + 8f, r.y + (r.height - d) / 2f, d, d), col, (int)(d / 2f) + 1);
            var st = StLabel(FzSmall, col, FontStyle.Bold, TextAnchor.MiddleLeft);
            GUI.Label(new Rect(r.x + 8f + d + 6f, r.y, r.width - (8f + d + 10f), r.height), text, st);
        }

        // ---- Segmented tabs ---------------------------------------------------
        // Returns the (possibly new) selected index. Animated sliding indicator,
        // tracked per-id so multiple segmented controls don't share one indicator.
        private static readonly Dictionary<string, float> _segIndicator = new Dictionary<string, float>(8);
        internal static int SegmentedTabs(Rect r, string[] labels, int selected, string id, int radius = 9, bool accentSel = true)
        {
            RoundRect(r, new Color(0f, 0f, 0f, 0.22f), radius + 3);
            int n = labels.Length;
            float pad = 4f;
            float segW = (r.width - pad * 2) / n;
            float ind;
            if (!_segIndicator.TryGetValue(id, out ind)) ind = selected;
            ind = Mathf.Lerp(ind, selected, 1f - Mathf.Exp(-16f * Time.unscaledDeltaTime));
            _segIndicator[id] = ind;
            var indR = new Rect(r.x + pad + ind * segW, r.y + pad, segW, r.height - pad * 2);
            Color indCol = accentSel ? ColAccent : ColBlue;
            RoundRect(indR, indCol, radius);

            int result = selected;
            for (int i = 0; i < n; i++)
            {
                var segR = new Rect(r.x + pad + i * segW, r.y + pad, segW, r.height - pad * 2);
                bool isSel = i == selected;
                bool hot = segR.Contains(VMouse());
                Color selInk = accentSel ? ColAccentInk : Color.white;
                Color tc = isSel ? selInk : (hot ? ColPaperText : ColInkFaint);
                var st = StLabel(FzSmall, tc, FontStyle.Bold, TextAnchor.MiddleCenter);
                GUI.Label(segR, labels[i], st);
                if (ClickedThisEvent(segR)) result = i;
            }
            return result;
        }

        // ---- Styled text field ------------------------------------------------
        internal static string ThemedField(Rect r, string ctrlName, string value, int maxLen, int fontSize, bool focused)
        {
            RoundRect(new Rect(r.x - 2, r.y - 2, r.width + 4, r.height + 4),
                      focused ? ColAccent : new Color(1f, 1f, 1f, 0.10f), 11);
            RoundRect(r, focused ? ColFieldFocus : ColField, 10);
            if (_tfStyle == null)
            {
                _tfStyle = new GUIStyle(GUI.skin.textField);
                _tfStyle.normal.background = null;
                _tfStyle.focused.background = null;
                _tfStyle.active.background = null;
                _tfStyle.fontStyle = FontStyle.Bold;
                _tfStyle.alignment = TextAnchor.MiddleCenter;
                _tfStyle.normal.textColor = ColPaperText;
                _tfStyle.focused.textColor = ColAccentHi;
            }
            _tfStyle.fontSize = fontSize;
            GUI.SetNextControlName(ctrlName);
            return GUI.TextField(r, value ?? "", maxLen, _tfStyle);
        }
        private static GUIStyle _tfStyle;

        // ---- Loading spinner --------------------------------------------------
        // Blades are drawn around the centre; each blade is a rounded bar rotated
        // about the centre via GUI.matrix (saved/restored per blade).
        internal static void Spinner(Vector2 center, float radius, Color col, int blades = 8)
        {
            float t = Time.unscaledTime * 6f;
            for (int i = 0; i < blades; i++)
            {
                float deg = (float)i / blades * 360f + t * Mathf.Rad2Deg;
                float fade = (float)((i + 1) % blades) / blades;
                float bw = radius * 0.30f, bh = radius * 0.62f;
                var br = new Rect(center.x - bw / 2f, center.y - radius, bw, bh);

                Matrix4x4 save = GUI.matrix;
                GUIUtility.RotateAroundPivot(deg, center);
                var prev = GUI.color;
                GUI.color = new Color(col.r, col.g, col.b, col.a * (0.25f + 0.75f * fade));
                GUI.DrawTexture(br, RoundedWhite(3));
                GUI.color = prev;
                GUI.matrix = save;
            }
        }

        // ---- Toast / notification queue --------------------------------------
        // kind: 0 info, 1 success, 2 error
        internal struct Toast { public string Text; public int Kind; public float Born; public float Ttl; }
        private static readonly List<Toast> _toasts = new List<Toast>(8);
        internal static void PushToast(string text, int kind, float ttl = 3.2f)
        {
            _toasts.Add(new Toast { Text = text, Kind = kind, Born = Time.unscaledTime, Ttl = ttl });
            if (_toasts.Count > 5) _toasts.RemoveAt(0);
        }

        internal static void DrawToasts()
        {
            if (_toasts.Count == 0) return;
            float now = Time.unscaledTime;
            float w = 460f, h = 56f, gap = 10f;
            float x = (VW - w) / 2f;
            float baseY = VH - 120f;
            for (int i = _toasts.Count - 1; i >= 0; i--)
            {
                var t = _toasts[i];
                float age = now - t.Born;
                if (age > t.Ttl) { _toasts.RemoveAt(i); continue; }
                float appear = EaseOutCubic(Mathf.Clamp01(age / 0.22f));
                float fade = age > t.Ttl - 0.5f ? Mathf.Clamp01((t.Ttl - age) / 0.5f) : 1f;
                int slot = _toasts.Count - 1 - i;
                float y = baseY - slot * (h + gap) + (1f - appear) * 18f;

                Color accent = t.Kind == 2 ? ColBad : (t.Kind == 1 ? ColGood : ColBlueHi);
                var prevA = GUI.color; GUI.color = new Color(1f, 1f, 1f, fade);
                var r = new Rect(x, y, w, h);
                DropShadow(r, 12, 14f, 0.30f * fade);
                RoundRect(r, new Color(0.10f, 0.11f, 0.14f, 0.98f), 12);
                RoundRect(new Rect(r.x, r.y + 8, 4f, r.height - 16f), accent, 2); // accent bar
                // font-proof icon: a coloured dot (no glyph that Arial may lack)
                float dot = 14f;
                RoundRect(new Rect(r.x + 22, r.y + (r.height - dot) / 2f, dot, dot), accent, 7);
                GUI.Label(new Rect(r.x + 52, r.y, r.width - 64, r.height), t.Text, StLabel(FzSmall, ColPaperText, FontStyle.Normal, TextAnchor.MiddleLeft));
                GUI.color = prevA;
            }
        }

        // ---- Tooltip layer ----------------------------------------------------
        // Reads GUI.tooltip (set via GUIContent above) and draws a small chip near
        // the cursor on the Repaint pass.
        internal static void DrawTooltipLayer()
        {
            if (Event.current.type != EventType.Repaint) return;
            string tip = GUI.tooltip;
            if (string.IsNullOrEmpty(tip)) return;
            var st = StLabel(FzTiny, ColPaperText, FontStyle.Normal, TextAnchor.MiddleCenter);
            Vector2 sz = st.CalcSize(new GUIContent(tip));
            float w = Mathf.Min(sz.x + 20f, 360f), h = sz.y + 12f;
            Vector2 m = VMouse();
            float x = Mathf.Clamp(m.x + 14f, 8f, VW - w - 8f);
            float y = Mathf.Clamp(m.y + 20f, 8f, VH - h - 8f);
            var r = new Rect(x, y, w, h);
            DropShadow(r, 8, 8f, 0.30f);
            RoundRect(r, new Color(0.06f, 0.07f, 0.09f, 0.98f), 8);
            GUI.Label(r, tip, st);
        }
    }
}
