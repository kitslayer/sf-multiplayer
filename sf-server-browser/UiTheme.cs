using System.Collections.Generic;
using UnityEngine;

namespace SFServerBrowser
{
    // ============================================================================
    //  UiTheme — design system for the ALKA server-browser / lobby overlay.
    //
    //  This game is Unity 5.6.3 (Mono 2.0). Runtime UI Toolkit / UIElements does
    //  NOT exist on this engine, and the vanilla menus are baked prefabs we have
    //  no source for. The ONLY runtime UI we own is this IMGUI (OnGUI) overlay —
    //  so we make it look intentional: a single accent colour, a paper-white
    //  panel on a dimmed scrim, a strict spacing scale, rounded 9-slice surfaces,
    //  soft shadows and resolution-independent scaling (the IMGUI equivalent of a
    //  uGUI CanvasScaler set to "Scale With Screen Size").
    //
    //  Everything is procedural (no texture assets to ship) and cached, so the
    //  per-frame cost is just GUI draw calls. No LINQ / tuples / Action<T> fields
    //  (absent or unreliable on net35 / Mono 2.0).
    // ============================================================================
    public partial class Plugin
    {
        // ---- Palette ----------------------------------------------------------
        // Bold, high-contrast, Stick-Fight-flavoured: ink on paper with one warm
        // accent. Greens/ambers/reds are reserved for status semantics only.
        internal static readonly Color ColScrim      = new Color(0.02f, 0.02f, 0.05f, 0.74f);
        internal static readonly Color ColPanel      = new Color(0.965f, 0.957f, 0.925f, 1f);
        internal static readonly Color ColPanelEdge  = new Color(0f, 0f, 0f, 0.16f);
        internal static readonly Color ColHeader     = new Color(0.12f, 0.13f, 0.17f, 1f);
        internal static readonly Color ColInk        = new Color(0.10f, 0.10f, 0.13f, 1f);
        internal static readonly Color ColInkSoft    = new Color(0.36f, 0.37f, 0.42f, 1f);
        internal static readonly Color ColInkFaint   = new Color(0.55f, 0.56f, 0.60f, 1f);
        internal static readonly Color ColPaperText  = new Color(0.93f, 0.94f, 0.97f, 1f);
        internal static readonly Color ColAccent     = new Color(0.96f, 0.76f, 0.18f, 1f); // SF yellow
        internal static readonly Color ColAccentHi   = new Color(1.00f, 0.86f, 0.34f, 1f);
        internal static readonly Color ColAccentInk  = new Color(0.14f, 0.11f, 0.02f, 1f);
        internal static readonly Color ColBlue       = new Color(0.24f, 0.52f, 0.96f, 1f);
        internal static readonly Color ColBlueHi     = new Color(0.40f, 0.64f, 1.00f, 1f);
        internal static readonly Color ColRow        = new Color(0.13f, 0.14f, 0.19f, 0.96f);
        internal static readonly Color ColField      = new Color(0.06f, 0.07f, 0.10f, 1f);
        internal static readonly Color ColFieldFocus = new Color(0.09f, 0.11f, 0.17f, 1f);
        internal static readonly Color ColGood       = new Color(0.34f, 0.80f, 0.44f, 1f);
        internal static readonly Color ColWarn       = new Color(0.96f, 0.71f, 0.24f, 1f);
        internal static readonly Color ColBad        = new Color(0.92f, 0.36f, 0.36f, 1f);
        internal static readonly Color ColShadow     = new Color(0f, 0f, 0f, 0.40f);

        // ---- Spacing scale (4px base grid, design units) ----------------------
        internal const float S1 = 4f, S2 = 8f, S3 = 12f, S4 = 16f, S5 = 24f, S6 = 32f, S7 = 48f;

        // ---- Type scale (design units, before UiScale) ------------------------
        internal const int FzDisplay = 40, FzTitle = 28, FzH2 = 20, FzBody = 16, FzSmall = 13, FzTiny = 11;

        // ---- Resolution-independent scaling -----------------------------------
        // Reference canvas is 1920x1080. We scale the whole overlay by height so a
        // 4K monitor and a 720p laptop both get the same *relative* layout — the
        // IMGUI analogue of CanvasScaler.ScreenMatchMode (match height).
        internal const float RefHeight = 1080f;
        internal static float UiScale = 1f;
        internal static float VW = 1920f;   // virtual (design-space) width
        internal static float VH = 1080f;   // virtual (design-space) height

        // Call once per OnGUI before drawing. Returns the saved matrix so the
        // caller can restore it afterwards (EndScaledUI).
        internal static Matrix4x4 BeginScaledUI()
        {
            UiScale = Mathf.Clamp(Screen.height / RefHeight, 0.60f, 2.4f);
            VW = Screen.width / UiScale;
            VH = Screen.height / UiScale;
            Matrix4x4 prev = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(UiScale, UiScale, 1f));
            return prev;
        }
        internal static void EndScaledUI(Matrix4x4 prev) { GUI.matrix = prev; }

        // Mouse position translated into design space (GUI.matrix-aware).
        internal static Vector2 VMouse()
        {
            Vector3 m = Input.mousePosition;
            return new Vector2(m.x / UiScale, (Screen.height - m.y) / UiScale);
        }

        // ---- Procedural texture cache -----------------------------------------
        private static readonly Dictionary<int, Texture2D> _solidCache = new Dictionary<int, Texture2D>(32);
        private static readonly Dictionary<int, Texture2D> _roundCache = new Dictionary<int, Texture2D>(16);
        private static Texture2D _softShadowTex;

        internal static Texture2D Solid(Color c)
        {
            int key = (Mathf.RoundToInt(c.r * 255) << 24) | (Mathf.RoundToInt(c.g * 255) << 16)
                    | (Mathf.RoundToInt(c.b * 255) << 8) | Mathf.RoundToInt(c.a * 255);
            Texture2D t;
            if (_solidCache.TryGetValue(key, out t) && t != null) return t;
            t = new Texture2D(2, 2, TextureFormat.ARGB32, false);
            var px = new Color[4];
            for (int i = 0; i < 4; i++) px[i] = c;
            t.SetPixels(px); t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            _solidCache[key] = t;
            return t;
        }

        // White rounded-rect texture for 9-slice surfaces. Tinted at draw time via
        // GUI.backgroundColor so one texture serves every panel/button colour.
        internal static Texture2D RoundedWhite(int radius)
        {
            radius = Mathf.Clamp(radius, 1, 40);
            Texture2D t;
            if (_roundCache.TryGetValue(radius, out t) && t != null) return t;
            int size = radius * 2 + 2;
            t = new Texture2D(size, size, TextureFormat.ARGB32, false);
            var px = new Color[size * size];
            float r = radius;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // distance into the nearest corner; antialias the last pixel.
                    float dx = Mathf.Min(x + 0.5f, size - x - 0.5f);
                    float dy = Mathf.Min(y + 0.5f, size - y - 0.5f);
                    float a = 1f;
                    if (dx < r && dy < r)
                    {
                        float d = Mathf.Sqrt((r - dx) * (r - dx) + (r - dy) * (r - dy));
                        a = Mathf.Clamp01(r - d + 0.5f);
                    }
                    px[y * size + x] = new Color(1f, 1f, 1f, a);
                }
            }
            t.SetPixels(px); t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            t.wrapMode = TextureWrapMode.Clamp;
            _roundCache[radius] = t;
            return t;
        }

        // Soft radial shadow (white→transparent) used as a 9-slice drop shadow.
        internal static Texture2D SoftShadow()
        {
            if (_softShadowTex != null) return _softShadowTex;
            int size = 64, c = size / 2;
            var t = new Texture2D(size, size, TextureFormat.ARGB32, false);
            var px = new Color[size * size];
            float maxd = c;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = Mathf.Abs(x - c + 0.5f), dy = Mathf.Abs(y - c + 0.5f);
                    float d = Mathf.Max(dx, dy) / maxd;
                    float a = Mathf.Clamp01(1f - d);
                    a = a * a;   // soften the falloff
                    px[y * size + x] = new Color(1f, 1f, 1f, a);
                }
            }
            t.SetPixels(px); t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            t.wrapMode = TextureWrapMode.Clamp;
            _softShadowTex = t;
            return t;
        }

        // ---- GUIStyle cache ---------------------------------------------------
        // Rounded 9-slice box style keyed by radius. The background is the white
        // rounded texture; callers tint via GUI.backgroundColor.
        private static readonly Dictionary<int, GUIStyle> _roundStyleCache = new Dictionary<int, GUIStyle>(8);
        internal static GUIStyle RoundStyle(int radius)
        {
            GUIStyle s;
            if (_roundStyleCache.TryGetValue(radius, out s) && s != null) return s;
            s = new GUIStyle();
            s.normal.background = RoundedWhite(radius);
            s.border = new RectOffset(radius, radius, radius, radius);
            _roundStyleCache[radius] = s;
            return s;
        }

        // Shared label styles built lazily (depend on GUI.skin being ready).
        internal static GUIStyle StLabel(int fontSize, Color color, FontStyle fs, TextAnchor anchor)
        {
            var s = new GUIStyle(GUI.skin.label);
            s.fontSize = fontSize;
            s.fontStyle = fs;
            s.normal.textColor = color;
            s.alignment = anchor;
            s.wordWrap = true;
            s.richText = true;
            return s;
        }
    }
}
