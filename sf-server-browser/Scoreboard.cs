using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace SFServerBrowser
{
    // ============================================================================
    //  Scoreboard — a polished top-of-screen team score bar for the match.
    //
    //  Stock SF only shows little crowns. This renders RED vs BLUE team chips with
    //  win totals, and any extra players drop into a row BELOW the two teams as
    //  their own distinct chips (in their real in-game colour). Always visible
    //  during a match; toggle with F4. Reads CharacterStats.wins + the player's
    //  CharacterInformation.myMaterial colour by reflection.
    // ============================================================================
    internal class Scoreboard : MonoBehaviour
    {
        internal Plugin Owner;

        private bool _hidden;            // F4 toggle
        private bool _built;
        private float _nextPoll;
        private Canvas _canvas;
        private GameObject _root;
        private RectTransform _redBox, _blueBox, _extraBox;
        private Text _redWins, _blueWins;

        // --- reflection cache ---
        private static bool _reflTried;
        private static Type _ctrlType, _statsType, _charInfoType;
        private static FieldInfo _pidField, _winsField, _myMatField;

        private struct SP { public int Slot; public int Wins; public Color Col; }

        // Team colours (chip backgrounds).
        private static readonly Color RedTeam  = new Color(0.85f, 0.22f, 0.26f, 1f);
        private static readonly Color RedTeamHi = new Color(0.30f, 0.10f, 0.12f, 0.96f);
        private static readonly Color BlueTeam = new Color(0.22f, 0.45f, 0.90f, 1f);
        private static readonly Color BlueTeamHi = new Color(0.10f, 0.16f, 0.32f, 0.96f);

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F4)) { _hidden = !_hidden; }

            bool inMatch = Owner != null && Owner.UiInMatch && !_hidden;
            if (_root != null && _root.activeSelf != inMatch) _root.SetActive(inMatch);
            if (!inMatch) return;

            if (Time.unscaledTime >= _nextPoll)
            {
                _nextPoll = Time.unscaledTime + 0.5f;
                EnsureBuilt();
                try { Refresh(); } catch (Exception e) { Plugin.Log.LogWarning("[scoreboard] " + e.Message); }
            }
        }

        private void EnsureBuilt()
        {
            if (_built) return;
            try { Build(); _built = true; }
            catch (Exception e) { Plugin.Log.LogWarning("[scoreboard] build: " + e.Message); }
        }

        private void Build()
        {
            _canvas = Ugui.CreateCanvas("ALKAScoreboard", 4000);
            _root = _canvas.gameObject;

            // RED chip (top-left of centre), VS, BLUE chip (top-right of centre).
            _redBox  = Ugui.Panel(_root.transform, "Red",  new Vector2(0.345f, 0.915f), new Vector2(0.475f, 0.985f), RedTeamHi).rectTransform;
            Ugui.Panel(_redBox, "tag", new Vector2(0f, 0f), new Vector2(0.06f, 1f), RedTeam, false);
            Ugui.Label(_redBox, "RED",  new Vector2(0.10f, 0f), new Vector2(0.62f, 1f), 22, RedTeam, TextAnchor.MiddleLeft);
            _redWins = Ugui.Label(_redBox, "0", new Vector2(0.62f, 0f), new Vector2(0.95f, 1f), 26, Ugui.TextWhite, TextAnchor.MiddleRight);

            Ugui.Label(_root.transform, "VS", new Vector2(0.478f, 0.915f), new Vector2(0.522f, 0.985f), 18, Ugui.TextDim, TextAnchor.MiddleCenter);

            _blueBox = Ugui.Panel(_root.transform, "Blue", new Vector2(0.525f, 0.915f), new Vector2(0.655f, 0.985f), BlueTeamHi).rectTransform;
            _blueWins = Ugui.Label(_blueBox, "0", new Vector2(0.05f, 0f), new Vector2(0.38f, 1f), 26, Ugui.TextWhite, TextAnchor.MiddleLeft);
            Ugui.Label(_blueBox, "BLUE", new Vector2(0.38f, 0f), new Vector2(0.90f, 1f), 22, BlueTeam, TextAnchor.MiddleRight);
            Ugui.Panel(_blueBox, "tag", new Vector2(0.94f, 0f), new Vector2(1f, 1f), BlueTeam, false);

            // Extra players row (below the two teams).
            _extraBox = Ugui.Panel(_root.transform, "Extras", new Vector2(0.37f, 0.862f), new Vector2(0.63f, 0.912f), new Color(0, 0, 0, 0f), false).rectTransform;
        }

        private void Refresh()
        {
            var players = Gather();
            int redWins = 0, blueWins = 0;
            for (int i = 0; i < players.Count; i++)
            {
                if (players[i].Slot == 0) redWins += players[i].Wins;
                else if (players[i].Slot == 1) blueWins += players[i].Wins;
            }
            if (_redWins != null) _redWins.text = redWins.ToString();
            if (_blueWins != null) _blueWins.text = blueWins.ToString();

            // Rebuild the extras row (slots 2+): each a distinct colour chip "P# • wins".
            if (_extraBox == null) return;
            Ugui.ClearChildren(_extraBox);
            var extras = new List<SP>();
            for (int i = 0; i < players.Count; i++) if (players[i].Slot >= 2) extras.Add(players[i]);
            int n = Mathf.Min(extras.Count, 6);
            if (n == 0) return;
            float w = 1f / n;
            for (int i = 0; i < n; i++)
            {
                var p = extras[i];
                float xa = i * w, xb = (i + 1) * w;
                var chip = Ugui.Panel(_extraBox, "E" + p.Slot, new Vector2(xa + 0.01f, 0.08f), new Vector2(xb - 0.01f, 0.92f),
                    new Color(p.Col.r * 0.35f, p.Col.g * 0.35f, p.Col.b * 0.35f, 0.96f)).gameObject;
                Ugui.Panel(chip.transform, "dot", new Vector2(0.05f, 0.28f), new Vector2(0.18f, 0.72f), p.Col, false);
                Ugui.Label(chip.transform, "P" + (p.Slot + 1) + "  " + p.Wins, new Vector2(0.2f, 0f), new Vector2(0.95f, 1f),
                    16, Ugui.TextWhite, TextAnchor.MiddleCenter);
            }
        }

        private List<SP> Gather()
        {
            var outl = new List<SP>();
            EnsureRefl();
            if ((object)_ctrlType == null || (object)_pidField == null) return outl;
            var ctrls = UnityEngine.Object.FindObjectsOfType(_ctrlType);
            if (ctrls == null) return outl;
            var seen = new HashSet<int>();
            foreach (var c in ctrls)
            {
                try
                {
                    var comp = c as Component;
                    if ((object)comp == null) continue;
                    int slot = (int)_pidField.GetValue(c);
                    if (slot < 0 || seen.Contains(slot)) continue;
                    seen.Add(slot);
                    var root = comp.transform.root;

                    int wins = 0;
                    if ((object)_statsType != null && (object)_winsField != null)
                    {
                        var st = root.GetComponentInChildren(_statsType, true);
                        if ((object)st != null) wins = (int)_winsField.GetValue(st);
                    }
                    Color col = Color.white;
                    if ((object)_charInfoType != null && (object)_myMatField != null)
                    {
                        var ci = root.GetComponentInChildren(_charInfoType, true);
                        if ((object)ci != null)
                        {
                            var mat = _myMatField.GetValue(ci) as Material;
                            if ((object)mat != null) col = mat.color;
                        }
                    }
                    outl.Add(new SP { Slot = slot, Wins = wins, Col = col });
                }
                catch { }
            }
            outl.Sort(delegate (SP a, SP b) { return a.Slot.CompareTo(b.Slot); });
            return outl;
        }

        private static void EnsureRefl()
        {
            if (_reflTried) return;
            _reflTried = true;
            try
            {
                _ctrlType = AccessTools.TypeByName("Controller");
                if ((object)_ctrlType != null) _pidField = AccessTools.Field(_ctrlType, "playerID");
                _statsType = AccessTools.TypeByName("CharacterStats");
                if ((object)_statsType != null) _winsField = AccessTools.Field(_statsType, "wins");
                _charInfoType = AccessTools.TypeByName("CharacterInformation");
                if ((object)_charInfoType != null) _myMatField = AccessTools.Field(_charInfoType, "myMaterial");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[scoreboard] refl: " + e.Message); }
        }
    }
}
