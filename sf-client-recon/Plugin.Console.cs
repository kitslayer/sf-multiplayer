using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SFClientRecon
{
    public partial class Plugin
    {
        private void InstallUnityConsoleTee()
        {
            try
            {
                string port = Environment.GetEnvironmentVariable("SFCLIENTRECON_PORT");
                if (string.IsNullOrEmpty(port)) port = "1339";
                string path = "/tmp/sf-console-" + port + ".log";
                _consoleTee = new System.IO.StreamWriter(path, false) { AutoFlush = true };
                Application.logMessageReceivedThreaded += OnUnityLogMessage;
                Log.LogInfo($"Unity console tee → {path}");
            }
            catch (Exception e) { Log.LogWarning($"[console-tee] {e.Message}"); }
        }

        private static void OnUnityLogMessage(string condition, string stackTrace, LogType type)
        {
            var w = _consoleTee;
            if (w == null) return;
            try
            {
                System.Threading.Monitor.Enter(_consoleTeeLock);
                try
                {
                    w.Write(DateTime.Now.ToString("HH:mm:ss.fff"));
                    w.Write(" [");
                    w.Write(type.ToString());
                    w.Write("] ");
                    w.WriteLine(condition);
                    if ((type == LogType.Exception || type == LogType.Error) && !string.IsNullOrEmpty(stackTrace))
                        w.WriteLine(stackTrace);
                }
                finally { System.Threading.Monitor.Exit(_consoleTeeLock); }
            }
            catch { }
        }
        private void TickChannelNullFill()
        {
            float now = Time.realtimeSinceStartup;
            if (_chanFillNextAt > 0f && now < _chanFillNextAt) return;
            _chanFillNextAt = now + 5f;
            try
            {
                if ((object)_ppTypeForFill == null) _ppTypeForFill = AccessTools.TypeByName("P2PPackageHandler");
                if ((object)_ppTypeForFill == null) return;
                var inst = UnityEngine.Object.FindObjectOfType(_ppTypeForFill);
                if (!RefOk(inst)) return;
                if (!_ppChannelsFillLookupTried)
                {
                    _ppChannelsFillLookupTried = true;
                    _ppChannelsFillField = AccessTools.Field(_ppTypeForFill, "channels");
                }
                if ((object)_ppChannelsFillField == null) return;
                var channels = _ppChannelsFillField.GetValue(inst) as Array;
                if (channels == null) return;
                var elemType = channels.GetType().GetElementType();
                if ((object)elemType == null || elemType.IsAbstract) return;
                int filled = 0;
                for (int i = 0; i < channels.Length; i++)
                {
                    if (channels.GetValue(i) != null) continue;
                    object q;
                    try { q = Activator.CreateInstance(elemType); }
                    catch { return; }   // no parameterless ctor — bail quietly
                    channels.SetValue(q, i);
                    filled++;
                }
                if (filled > 0)
                {
                    _chanFillTotal += filled;
                    Log.LogInfo($"[chan-fill] Filled {filled} null channel slot(s) with empty {elemType.Name} (total {_chanFillTotal}) — NRE storm source removed.");
                }
            }
            catch { }
        }
    }
}
