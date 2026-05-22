using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SFLocalControlFix
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.stickfightdev.local-control-fix";
        public const string PluginName = "SFLocalControlFix";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo($"{PluginName} {PluginVersion} loading...");

            var harmony = new Harmony(PluginGuid);

            // Patch both MultiplayerManager and MultiplayerManagerSockets — the
            // patched srv DLL routes through one of them depending on the
            // transport. We don't know which without inspecting the runtime,
            // so patch both and let the inert one no-op.
            TryPatch(harmony, "MultiplayerManager");
            TryPatch(harmony, "Landfall.Network.Sockets.MultiplayerManagerSockets");

            Log.LogInfo($"{PluginName} ready.");
        }

        private static void TryPatch(Harmony harmony, string typeName)
        {
            try
            {
                var t = AccessTools.TypeByName(typeName);
                if (t == null)
                {
                    Log.LogWarning($"Type not found: {typeName} (skipping patch)");
                    return;
                }
                var m = AccessTools.Method(t, "OnPlayerSpawned", new[] { typeof(byte[]) });
                if (m == null)
                {
                    Log.LogWarning($"OnPlayerSpawned(byte[]) not found on {typeName}");
                    return;
                }
                var postfix = new HarmonyMethod(
                    typeof(Patches).GetMethod(nameof(Patches.OnPlayerSpawned_Postfix),
                        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public));
                harmony.Patch(m, postfix: postfix);
                Log.LogInfo($"Patched {typeName}.OnPlayerSpawned");
            }
            catch (Exception e)
            {
                Log.LogError($"Failed to patch {typeName}: {e}");
            }
        }
    }

    internal static class Patches
    {
        internal static void OnPlayerSpawned_Postfix(object __instance, byte[] data)
        {
            try
            {
                if (data == null || data.Length < 1) return;
                byte spawnedIndex = data[0];

                var t = __instance.GetType();

                var localIndexField = AccessTools.Field(t, "mLocalPlayerIndex");
                if (localIndexField == null)
                {
                    Plugin.Log.LogWarning("mLocalPlayerIndex field not found");
                    return;
                }
                byte localIndex = (byte)localIndexField.GetValue(__instance);

                if (spawnedIndex != localIndex)
                {
                    // Remote player — nothing to do.
                    return;
                }

                var connectedField = AccessTools.Field(t, "mConnectedClients");
                if (connectedField == null)
                {
                    Plugin.Log.LogWarning("mConnectedClients field not found");
                    return;
                }
                var clients = connectedField.GetValue(__instance) as Array;
                if (clients == null || spawnedIndex >= clients.Length)
                {
                    Plugin.Log.LogWarning($"mConnectedClients empty or index {spawnedIndex} out of range");
                    return;
                }
                var clientData = clients.GetValue(spawnedIndex);
                if (clientData == null)
                {
                    Plugin.Log.LogWarning($"ConnectedClientData[{spawnedIndex}] is null");
                    return;
                }

                var playerObjField = AccessTools.Field(clientData.GetType(), "PlayerObject");
                var playerObj = playerObjField?.GetValue(clientData) as GameObject;
                if (playerObj == null)
                {
                    Plugin.Log.LogWarning($"PlayerObject for slot {spawnedIndex} is null in postfix — was UpdateLocalClientsData called?");
                    return;
                }

                // Check current state: if already has local control, skip (re-spawn,
                // already-good path). Use HasLocalControl property on NetworkPlayer.
                var networkPlayer = playerObj.GetComponent("NetworkPlayer") as Component;
                if (networkPlayer == null)
                {
                    Plugin.Log.LogWarning("NetworkPlayer component missing on spawned player");
                    return;
                }

                var hasLocalProp = AccessTools.Property(networkPlayer.GetType(), "HasLocalControl");
                if (hasLocalProp != null)
                {
                    bool already = (bool)hasLocalProp.GetValue(networkPlayer, null);
                    if (already)
                    {
                        // The patched-srv DLL's ControlledLocally path actually worked
                        // (or we already ran). Don't double-call.
                        return;
                    }
                }

                // 1) Controller.TakeLocalControl(CharacterActions) — give it the
                //    next saved input device, same as the original code path.
                var controller = playerObj.GetComponent("Controller") as Component;
                if (controller != null)
                {
                    CallControllerTakeLocalControl(controller);
                }
                else
                {
                    Plugin.Log.LogWarning("Controller component missing — skipping Controller.TakeLocalControl");
                }

                // 2) NetworkPlayer.TakeLocalControl() — the critical one; this
                //    flips mHasLocalControl=true and unblocks playerUpdate streaming.
                var npTake = AccessTools.Method(networkPlayer.GetType(), "TakeLocalControl", Type.EmptyTypes);
                if (npTake != null)
                {
                    npTake.Invoke(networkPlayer, null);
                    Plugin.Log.LogInfo($"Forced TakeLocalControl on local player {spawnedIndex}");
                }
                else
                {
                    Plugin.Log.LogError("NetworkPlayer.TakeLocalControl() not found");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"OnPlayerSpawned_Postfix threw: {e}");
            }
        }

        private static void CallControllerTakeLocalControl(Component controller)
        {
            // Resolve CharacterActions via GameManager.Instance.GetNextSavedDeviceForNetwork().
            // CharacterActions is the parameter type of Controller.TakeLocalControl.
            try
            {
                var controllerType = controller.GetType();
                var takeMethod = controllerType.GetMethod("TakeLocalControl",
                    BindingFlags.Public | BindingFlags.Instance);
                if (takeMethod == null)
                {
                    Plugin.Log.LogWarning("Controller.TakeLocalControl not found");
                    return;
                }
                var ps = takeMethod.GetParameters();
                if (ps.Length != 1)
                {
                    Plugin.Log.LogWarning($"Controller.TakeLocalControl has {ps.Length} params (expected 1)");
                    return;
                }

                object actions = null;
                var gmType = AccessTools.TypeByName("GameManager");
                if (gmType != null)
                {
                    var instanceProp = AccessTools.Property(gmType, "Instance");
                    var gm = instanceProp?.GetValue(null, null);
                    if (gm != null)
                    {
                        var getDev = AccessTools.Method(gmType, "GetNextSavedDeviceForNetwork");
                        if (getDev != null)
                        {
                            actions = getDev.Invoke(gm, null);
                        }
                    }
                }

                if (actions == null)
                {
                    Plugin.Log.LogWarning("Could not resolve CharacterActions from GameManager; calling Controller.TakeLocalControl(null)");
                }

                takeMethod.Invoke(controller, new[] { actions });
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"CallControllerTakeLocalControl: {e}");
            }
        }
    }
}
