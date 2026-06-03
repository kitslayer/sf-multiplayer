using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace SFServerBrowser
{
    // ============================================================================
    //  UguiKit — runtime uGUI factory for the in-game lobby overlay.
    //
    //  Unlike the IMGUI browser (UiTheme/UiWidgets), this builds REAL uGUI objects
    //  (Canvas / Image / Text / Button / InputField) at runtime — the same native
    //  approach the reference mod (Centauri "StickFightTeamsMod") uses, which is
    //  proven to render correctly on this exact game build. That gives crisp text
    //  with outlines, shadowed buttons and smooth hover colour transitions that
    //  IMGUI can't match. CanvasScaler keeps it resolution-independent.
    //
    //  Colours mirror the reference's dark-navy + cyan-accent language.
    // ============================================================================
    internal static class Ugui
    {
        // ---- Palette (reference-inspired) ------------------------------------
        public static readonly Color Scrim       = new Color(0.02f, 0.03f, 0.06f, 0.78f);
        public static readonly Color PanelBg      = new Color(0.06f, 0.07f, 0.12f, 0.985f);
        public static readonly Color PanelBgLight = new Color(0.10f, 0.12f, 0.18f, 0.96f);
        public static readonly Color Header       = new Color(0.09f, 0.11f, 0.17f, 1f);
        public static readonly Color RowBg        = new Color(0.11f, 0.13f, 0.19f, 0.96f);
        public static readonly Color RowBgAlt     = new Color(0.13f, 0.15f, 0.22f, 0.96f);
        public static readonly Color ButtonBg     = new Color(0.16f, 0.20f, 0.30f, 1f);
        public static readonly Color ButtonHover  = new Color(0.26f, 0.35f, 0.52f, 1f);
        public static readonly Color ButtonPress  = new Color(0.12f, 0.16f, 0.24f, 1f);
        public static readonly Color ButtonDisab  = new Color(0.18f, 0.20f, 0.24f, 0.55f);
        public static readonly Color Accent       = new Color(0.30f, 0.85f, 0.95f, 1f);
        public static readonly Color AccentBtn    = new Color(0.18f, 0.55f, 0.70f, 1f);
        public static readonly Color AccentBtnHi  = new Color(0.28f, 0.72f, 0.90f, 1f);
        public static readonly Color GoBtn        = new Color(0.20f, 0.55f, 0.32f, 1f);
        public static readonly Color GoBtnHi      = new Color(0.30f, 0.72f, 0.44f, 1f);
        public static readonly Color Border       = new Color(0.35f, 0.55f, 0.85f, 0.95f);
        public static readonly Color TextWhite    = Color.white;
        public static readonly Color TextDim      = new Color(0.70f, 0.75f, 0.85f, 1f);
        public static readonly Color Good         = new Color(0.30f, 0.82f, 0.46f, 1f);
        public static readonly Color Warn         = new Color(0.96f, 0.78f, 0.25f, 1f);
        public static readonly Color Bad          = new Color(0.95f, 0.32f, 0.32f, 1f);

        private static Font _font;
        public static Font UiFont
        {
            get
            {
                if (_font == null) _font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                return _font;
            }
        }

        // ---- Anchoring helpers ------------------------------------------------
        public static RectTransform Stretch(RectTransform rt, Vector2 min, Vector2 max)
        {
            rt.anchorMin = min; rt.anchorMax = max;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            rt.sizeDelta = Vector2.zero; rt.anchoredPosition = Vector2.zero;
            rt.localScale = Vector3.one;
            return rt;
        }

        public static RectTransform Rect(GameObject go)
        {
            var rt = go.GetComponent<RectTransform>();
            if (rt == null) rt = go.AddComponent<RectTransform>();
            return rt;
        }

        // ---- Canvas (full-screen overlay, scaled to a 1080 reference) ---------
        public static Canvas CreateCanvas(string name, int sortOrder)
        {
            var go = new GameObject(name);
            Object.DontDestroyOnLoad(go);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortOrder;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 1f;   // match height (like the IMGUI layer)
            go.AddComponent<GraphicRaycaster>();
            return canvas;
        }

        // ---- Image panel (with optional border) -------------------------------
        public static Image Panel(Transform parent, string name, Vector2 min, Vector2 max, Color color, bool border = true)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            Stretch(Rect(go), min, max);
            var img = go.AddComponent<Image>();
            img.color = color;
            if (border)
            {
                var o = go.AddComponent<Outline>();
                o.effectColor = Border;
                o.effectDistance = new Vector2(1.5f, -1.5f);
            }
            return img;
        }

        // Sized panel anchored by pixels at the canvas centre (for the main card).
        public static RectTransform CenteredCard(Transform parent, string name, float w, float h, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = Rect(go);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = Vector2.zero;
            var img = go.AddComponent<Image>();
            img.color = color;
            var o = go.AddComponent<Outline>();
            o.effectColor = Border; o.effectDistance = new Vector2(2f, -2f);
            var s = go.AddComponent<Shadow>();
            s.effectColor = new Color(0f, 0f, 0f, 0.55f); s.effectDistance = new Vector2(3f, -5f);
            return rt;
        }

        // ---- Label ------------------------------------------------------------
        public static Text Label(Transform parent, string text, Vector2 min, Vector2 max,
                                 int fontSize, Color color, TextAnchor anchor = TextAnchor.MiddleCenter,
                                 FontStyle style = FontStyle.Bold, bool outline = true)
        {
            var go = new GameObject("Lbl");
            go.transform.SetParent(parent, false);
            Stretch(Rect(go), min, max);
            var t = go.AddComponent<Text>();
            t.text = text;
            t.font = UiFont;
            t.fontSize = fontSize;
            t.fontStyle = style;
            t.color = color;
            t.alignment = anchor;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.supportRichText = true;
            t.raycastTarget = false;
            if (outline)
            {
                var o = go.AddComponent<Outline>();
                o.effectColor = new Color(0f, 0f, 0f, 0.75f);
                o.effectDistance = new Vector2(1f, -1f);
            }
            return t;
        }

        // ---- Button (hover/press colour transitions + shadow + outline) -------
        public static Button Btn(Transform parent, string label, Vector2 min, Vector2 max,
                                 UnityAction onClick, int fontSize = 18,
                                 Color? bg = null, Color? hover = null, Color? textCol = null)
        {
            var go = new GameObject("Btn_" + (label ?? "x").Replace(" ", "_"));
            go.transform.SetParent(parent, false);
            Stretch(Rect(go), min, max);

            var img = go.AddComponent<Image>();
            img.color = bg ?? ButtonBg;

            var shadow = go.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.55f);
            shadow.effectDistance = new Vector2(2f, -3f);
            var outline = go.AddComponent<Outline>();
            outline.effectColor = Border;
            outline.effectDistance = new Vector2(1f, -1f);

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            var cb = btn.colors;
            cb.normalColor = bg ?? ButtonBg;
            cb.highlightedColor = hover ?? ButtonHover;
            cb.pressedColor = ButtonPress;
            cb.disabledColor = ButtonDisab;
            cb.fadeDuration = 0.12f;
            btn.colors = cb;
            if (onClick != null) btn.onClick.AddListener(onClick);

            Label(go.transform, (label ?? "").ToUpperInvariant(), Vector2.zero, Vector2.one,
                  fontSize, textCol ?? TextWhite, TextAnchor.MiddleCenter);
            return btn;
        }

        // ---- Input field ------------------------------------------------------
        public static InputField Input(Transform parent, string placeholder, Vector2 min, Vector2 max, int fontSize, int charLimit)
        {
            var go = new GameObject("Input");
            go.transform.SetParent(parent, false);
            Stretch(Rect(go), min, max);
            var bg = go.AddComponent<Image>();
            bg.color = new Color(0.04f, 0.05f, 0.08f, 0.98f);
            var o = go.AddComponent<Outline>();
            o.effectColor = new Color(0.30f, 0.50f, 0.80f, 0.6f); o.effectDistance = new Vector2(1f, -1f);

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, false);
            Stretch(Rect(textGo), new Vector2(0.04f, 0.05f), new Vector2(0.96f, 0.95f));
            var text = textGo.AddComponent<Text>();
            text.font = UiFont; text.fontSize = fontSize; text.fontStyle = FontStyle.Bold;
            text.color = TextWhite; text.supportRichText = false;
            text.alignment = TextAnchor.MiddleCenter;

            var phGo = new GameObject("Placeholder");
            phGo.transform.SetParent(go.transform, false);
            Stretch(Rect(phGo), new Vector2(0.04f, 0.05f), new Vector2(0.96f, 0.95f));
            var ph = phGo.AddComponent<Text>();
            ph.font = UiFont; ph.fontSize = fontSize; ph.fontStyle = FontStyle.Italic;
            ph.color = new Color(0.5f, 0.55f, 0.65f, 0.8f); ph.text = placeholder;
            ph.alignment = TextAnchor.MiddleCenter;

            var input = go.AddComponent<InputField>();
            input.textComponent = text;
            input.placeholder = ph;
            input.lineType = InputField.LineType.SingleLine;
            input.characterLimit = charLimit;
            return input;
        }

        // ---- Status pill (rounded-ish colour chip via tinted image) -----------
        public static Text Pill(Transform parent, string text, Vector2 min, Vector2 max, Color col)
        {
            var go = new GameObject("Pill");
            go.transform.SetParent(parent, false);
            Stretch(Rect(go), min, max);
            var img = go.AddComponent<Image>();
            img.color = new Color(col.r, col.g, col.b, 0.20f);
            var o = go.AddComponent<Outline>();
            o.effectColor = new Color(col.r, col.g, col.b, 0.75f); o.effectDistance = new Vector2(1f, -1f);
            return Label(go.transform, text, Vector2.zero, Vector2.one, 13, col, TextAnchor.MiddleCenter);
        }

        // Convenience: destroy all children of a transform (rebuild lists).
        public static void ClearChildren(Transform t)
        {
            if (t == null) return;
            for (int i = t.childCount - 1; i >= 0; i--)
                Object.Destroy(t.GetChild(i).gameObject);
        }
    }
}
