using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SFServerBrowser
{
    // ============================================================================
    //  Loc — tiny EN/ES localization for the ALKA lobby UI.
    //
    //  English is the DEFAULT (first) language; the Settings tab lets a user
    //  switch to Spanish. The choice persists to BepInEx/config/alka-lang.txt so
    //  it survives restarts. T("KEY") returns the string for the current language
    //  (falls back to the key itself if missing — safe for new keys).
    // ============================================================================
    internal static class Loc
    {
        internal enum Language { English = 0, Spanish = 1 }

        private static Language _lang = Language.English;
        private static bool _loaded;

        internal static Language Lang
        {
            get { Ensure(); return _lang; }
            set { _lang = value; Save(); }
        }

        internal static bool IsSpanish { get { return Lang == Language.Spanish; } }

        internal static void Toggle(Language l) { Lang = l; }

        // key -> { English, Spanish }
        private static readonly Dictionary<string, string[]> _t = new Dictionary<string, string[]>
        {
            { "TAB_LOBBY",      new[] { "LOBBY",        "SALA" } },
            { "TAB_BROWSE",     new[] { "BROWSE",       "PARTIDAS" } },
            { "TAB_JOIN",       new[] { "JOIN",         "UNIRSE" } },
            { "TAB_SETTINGS",   new[] { "SETTINGS",     "AJUSTES" } },
            { "SUBTITLE",       new[] { "Centralized multiplayer lobby", "Sala multijugador centralizada" } },
            { "LOBBY_CODE",     new[] { "LOBBY CODE",   "CÓDIGO DE SALA" } },
            { "COPY_CODE",      new[] { "COPY CODE",    "COPIAR CÓDIGO" } },
            { "INVITE",         new[] { "INVITE (COPY)", "INVITAR (COPIAR)" } },
            { "HOST",           new[] { "Host: ",       "Anfitrión: " } },
            { "PING",           new[] { "Ping: ",       "Ping: " } },
            { "STATUS",         new[] { "Status: ",     "Estado: " } },
            { "ST_MENU",        new[] { "in menu",      "en el menú" } },
            { "ST_MATCH",       new[] { "in match",     "en partida" } },
            { "ST_CONNECTING",  new[] { "connecting ",  "conectando " } },
            { "PLAYERS",        new[] { "PLAYERS",      "JUGADORES" } },
            { "NO_PLAYERS",     new[] { "No players detected yet.", "Sin jugadores todavía." } },
            { "CREATE_HOST",    new[] { "CREATE NEW LOBBY", "CREAR SALA NUEVA" } },
            { "START",          new[] { "START MATCH",  "INICIAR PARTIDA" } },
            { "LEAVE",          new[] { "LEAVE",        "SALIR" } },
            { "REFRESH",        new[] { "REFRESH",      "ACTUALIZAR" } },
            { "CREATE",         new[] { "CREATE",       "CREAR" } },
            { "SEARCHING",      new[] { "Searching…",   "Buscando…" } },
            { "NO_ENDPOINT",    new[] { "Server browser disabled (no endpoint).", "Buscador desactivado (sin endpoint)." } },
            { "NO_LOBBIES",     new[] { "No lobbies. Hit CREATE to start one.", "Sin salas. Pulsa CREAR para abrir una." } },
            { "ONLINE",         new[] { " online",      " en línea" } },
            { "ENTER_CODE",     new[] { "ENTER A LOBBY CODE", "INGRESA UN CÓDIGO" } },
            { "JOIN_LOBBY",     new[] { "JOIN LOBBY",   "UNIRSE A SALA" } },
            { "PASTE",          new[] { "PASTE",        "PEGAR" } },
            { "CODE_HINT",      new[] { "Codes are 4 characters (A-Z, 0-9).", "Los códigos son de 4 caracteres (A-Z, 0-9)." } },
            { "FULL",           new[] { "FULL",         "LLENA" } },
            { "ALIVE",          new[] { "ALIVE",        "ACTIVA" } },
            { "DOWN",           new[] { "DOWN",         "CAÍDA" } },
            { "FOOTER",         new[] { "F2 toggle  ·  Esc close  ·  ALKA Online", "F2 abrir/cerrar  ·  Esc cerrar  ·  ALKA Online" } },
            { "SET_LANGUAGE",   new[] { "LANGUAGE",     "IDIOMA" } },
            { "SET_LANG_HINT",  new[] { "Choose your interface language.", "Elige el idioma de la interfaz." } },
            { "ENGLISH",        new[] { "ENGLISH",      "INGLÉS" } },
            { "SPANISH",        new[] { "SPANISH",      "ESPAÑOL" } },
            { "SET_ABOUT",      new[] { "ALKA Online — native lobby. English is the default; Spanish optional.",
                                       "ALKA Online — sala nativa. Inglés por defecto; español opcional." } },
        };

        internal static string T(string key)
        {
            Ensure();
            string[] pair;
            if (_t.TryGetValue(key, out pair) && pair != null && pair.Length == 2)
                return pair[(int)_lang];
            return key;
        }

        // ---- persistence ------------------------------------------------------
        private static string ConfigPath()
        {
            try
            {
                string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                string dir = Path.Combine(Path.Combine(root, "BepInEx"), "config");
                return Path.Combine(dir, "alka-lang.txt");
            }
            catch { return null; }
        }

        private static void Ensure()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                string p = ConfigPath();
                if (p != null && File.Exists(p))
                {
                    string v = (File.ReadAllText(p) ?? "").Trim().ToLowerInvariant();
                    if (v.StartsWith("es") || v.StartsWith("spa")) _lang = Language.Spanish;
                    else _lang = Language.English;
                }
            }
            catch { }
        }

        private static void Save()
        {
            try
            {
                string p = ConfigPath();
                if (p == null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllText(p, _lang == Language.Spanish ? "es" : "en");
            }
            catch { }
        }
    }
}
