using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SFHeadlessHost
{

    // Phase 6.22 — log listener that tees every BepInEx log line to a
    // per-lobby file. Lets multiple oracles share the same install without
    // their plugin logs trampling each other in BepInEx/LogOutput.log.
    // Catches lines from ALL sources (SFHeadlessHost, BepInEx itself, any
    // other plugin), so the per-lobby file is a superset of LogOutput.
    //
    // 2026-05-23 fix: removed `lock (_lock)` — the C# compiler emits
    // `Monitor.Enter(obj, ref bool)` (2-arg) which SF's old Mono 2.0
    // runtime DOESN'T HAVE. The MissingMethodException was caught and
    // re-logged, hitting our listener again, recursively, dumping 400MB
    // of log per oracle in ~10 minutes. Replaced with a ThreadStatic
    // re-entry guard + no locking. BepInEx log events come from the
    // Unity main thread; concurrent writes are not a real concern here.
    // The re-entry guard means even if WriteLine itself throws, the
    // listener immediately returns instead of recursing.
    internal class PerLobbyLogListener : BepInEx.Logging.ILogListener
    {
        private readonly System.IO.StreamWriter _writer;
        [System.ThreadStatic] private static bool _reentryGuard;

        public PerLobbyLogListener(string path)
        {
            // Append mode so a restart doesn't wipe history. Truncate is
            // handled by the launcher (which deletes stale files itself).
            var fs = new System.IO.FileStream(path, System.IO.FileMode.Append, System.IO.FileAccess.Write, System.IO.FileShare.Read);
            _writer = new System.IO.StreamWriter(fs) { AutoFlush = true };
            _writer.WriteLine($"--- per-lobby log opened {DateTime.UtcNow:O} ---");
        }

        public void LogEvent(object sender, BepInEx.Logging.LogEventArgs eventArgs)
        {
            if (_reentryGuard) return;
            _reentryGuard = true;
            try
            {
                _writer.WriteLine($"[{eventArgs.Level,-7}:{eventArgs.Source.SourceName}] {eventArgs.Data}");
            }
            catch { /* never let logging crash the plugin OR recurse */ }
            finally { _reentryGuard = false; }
        }

        public void Dispose()
        {
            try { _writer.Dispose(); } catch { }
        }
    }
}
