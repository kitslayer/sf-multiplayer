using System;
using System.Collections;
using System.Net;
using UnityEngine;

namespace SFClientRecon
{
    /// <summary>
    /// Host-a-new-lobby flow. Makes Stick Fight's "Host Match" button (and the
    /// server browser's CREATE button) spin up a REAL backend lobby on demand —
    /// the same kind serve-lobbies.py creates — then SELECT + oracle-connect to
    /// it, so a fresh public lobby appears in the browser list and the host walks
    /// straight into it. Falls back to the MAIN oracle lobby when no create
    /// endpoint/token is configured, preserving the previous behavior.
    /// </summary>
    public partial class Plugin
    {
        private static string LobbyEndpoint()
        {
            var e = Environment.GetEnvironmentVariable("SF_LOBBY_ENDPOINT");
            return string.IsNullOrEmpty(e) ? null : e.Trim();
        }
        private static string LobbyControlToken()
        {
            return Environment.GetEnvironmentVariable("SF_CONTROL_TOKEN") ?? "";
        }

        // ---- Cross-assembly bridge: host an ALREADY-CREATED lobby by code -----
        // Called by SFServerBrowser after it POSTs to create a lobby. The client
        // action is identical to joining (SELECT + connect via the router) — the
        // headless backend process is the authoritative owner of the lobby.
        internal static void RequestHostLobby(string code)
        {
            code = (code ?? "").Trim().ToUpper();
            if (code.Length == 0) { Log.LogWarning("[oracle-lobby] RequestHostLobby: empty code ignored."); return; }
            Log.LogInfo($"[oracle-lobby] RequestHostLobby({code}) — SELECT + connect as host.");
            RequestJoinLobby(code);   // same SELECT+connect path; backend owns the lobby
        }

        // ---- Host Match button → create a NEW lobby, then host it -------------
        private static bool _hostCreateInFlight;
        internal static void CreateAndHostLobby()
        {
            if (_hostCreateInFlight) { Log.LogInfo("[oracle-lobby] host-create already in flight — ignoring."); return; }

            string endpoint = LobbyEndpoint();
            string token = LobbyControlToken();
            // No control plane configured → keep the old Host Match behavior
            // (join the default MAIN oracle lobby). This never throws offline.
            if (string.IsNullOrEmpty(endpoint) || token.Length == 0 || !RefOk(Instance))
            {
                Log.LogInfo("[oracle-lobby] No SF_LOBBY_ENDPOINT/SF_CONTROL_TOKEN — Host Match → MAIN lobby (fallback).");
                BeginOracleLobbyConnect("HostMatch");
                return;
            }

            _hostCreateInFlight = true;
            SetBanner("Creating lobby…");
            Instance.StartCoroutine(CreateLobbyThenHostCoroutine(endpoint, token));
        }

        private static void SetBanner(string text)
        {
            try { if (Instance != null) Instance._bannerText = text; } catch { }
        }

        // WebClient with a finite timeout — launch-lobby.sh waits up to ~30s for
        // the headless host to bind its UDP port before serve-lobbies replies.
        private sealed class HostTimedWebClient : WebClient
        {
            protected override WebRequest GetWebRequest(Uri address)
            {
                var req = base.GetWebRequest(address);
                if (req != null) req.Timeout = 45000;
                return req;
            }
        }

        private static IEnumerator CreateLobbyThenHostCoroutine(string url, string token)
        {
            string body = null, err = null;
            var th = new System.Threading.Thread(new System.Threading.ThreadStart(delegate
            {
                try
                {
                    using (var wc = new HostTimedWebClient())
                    {
                        wc.Headers.Add("User-Agent", "SFClientRecon/host");
                        wc.Headers.Add("Content-Type", "application/json");
                        wc.Headers.Add("X-SF-Token", token);
                        body = wc.UploadString(url, "POST", "{\"source\":\"host-match\"}");
                    }
                }
                catch (Exception e) { err = e.Message; }
            }));
            th.IsBackground = true;
            th.Start();
            while (th.IsAlive) yield return null;

            _hostCreateInFlight = false;

            if (err != null)
            {
                Log.LogWarning("[oracle-lobby] host-create failed: " + err + " → MAIN fallback.");
                SetBanner("Create failed — joining MAIN.");
                BeginOracleLobbyConnect("HostMatch");
                yield break;
            }
            string code = ExtractJsonString(body, "code");
            if (string.IsNullOrEmpty(code))
            {
                Log.LogWarning("[oracle-lobby] host-create: no code in response → MAIN fallback.");
                SetBanner("Create failed — joining MAIN.");
                BeginOracleLobbyConnect("HostMatch");
                yield break;
            }
            code = code.Trim().ToUpper();
            Log.LogInfo("[oracle-lobby] host-create OK → lobby " + code + " — connecting as host.");
            SetBanner("Lobby " + code + " — connecting…");
            RequestHostLobby(code);
        }

        // Tiny dependency-free string extractor for {"code":"AAAA",...}.
        private static string ExtractJsonString(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            int i = json.IndexOf("\"" + key + "\"");
            if (i < 0) return null;
            int colon = json.IndexOf(':', i);
            if (colon < 0) return null;
            int q1 = json.IndexOf('"', colon + 1);
            if (q1 < 0) return null;
            int q2 = json.IndexOf('"', q1 + 1);
            if (q2 < 0) return null;
            return json.Substring(q1 + 1, q2 - q1 - 1);
        }
    }
}
