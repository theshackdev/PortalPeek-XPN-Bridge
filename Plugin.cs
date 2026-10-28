using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace TheShack.PortalPeekXpnBridge
{
    // Portal Peek shows, inside portal A, a recording of the portal A leads to (B). It only records a portal that
    // itself leads somewhere, which suits vanilla's two-way pairs. With XPortalNetworks links are one way: B may
    // lead elsewhere or nowhere, so it was never recorded and A stayed plain.
    //
    // While Portal Peek decides what to record (walking up to a portal, leaving through one, the re-record key),
    // this treats every placed portal as recordable. Showing is unchanged: A shows wherever its link (the one
    // XPortalNetworks sets) sends you.
    //
    // Day and night: a recording shows the light it was made in. Each recording's name gets a day or night tag
    // (from the light when it's made), and a portal shows the version for the light you're in now; if that one
    // doesn't exist yet, the other one, or an untagged recording from before. Standing at a portal in the other
    // light fills in the missing version. Client-only; nothing is sent, and the only files are Portal Peek's own.
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(PortalPeekGuid)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "theshack.portalpeek_xpn_bridge";
        public const string PluginName = "PortalPeek XPN Bridge";
        public const string PluginVersion = "1.1.0";
        private const string PortalPeekGuid = "tboods.PortalPeek";

        // Portal Peek's own methods that pick a portal to record.
        private static readonly string[] RecordingMethods = { "Update", "TryDepartureCapture", "Nearest" };

        internal static ManualLogSource Log;
        private static ConfigEntry<bool> recordEvery;
        private static ConfigEntry<bool> dayNight;
        private static int recording;

        private const string DayTag = "-1";
        private const string NightTag = "-0";
        private static FieldInfo libraryDir;

        private void Awake()
        {
            Log = Logger;
            recordEvery = Config.Bind("General", "Record every portal", true,
                "Record what's around any portal you stand by, even one that doesn't lead anywhere itself, so portals " +
                "leading to it (one-way links, like XPortalNetworks') can show it. Off: Portal Peek's own rule.");
            dayNight = Config.Bind("General", "Day and night versions", true,
                "Keep a daytime and a nighttime recording of each portal and show the one for the light you're in. " +
                "Off: one recording per portal, in whatever light it was made (Portal Peek's own way).");

            Type capturer = AccessTools.TypeByName("PortalPeek.PeekCapturer");
            Type portal = AccessTools.TypeByName("PortalPeek.PeekPortal");
            MethodInfo isConnected = portal == null ? null : AccessTools.PropertyGetter(portal, "IsConnected");
            if (capturer == null || isConnected == null)
            {
                Log.LogWarning("This Portal Peek version is laid out differently; the bridge does nothing.");
                return;
            }

            Harmony harmony = new Harmony(PluginGuid);
            int scopes = 0;
            foreach (string name in RecordingMethods)
            {
                MethodInfo method = AccessTools.Method(capturer, name);
                if (method == null)
                {
                    Log.LogWarning($"Portal Peek has no {name}; recording from there keeps Portal Peek's own rule.");
                    continue;
                }
                harmony.Patch(method,
                    prefix: new HarmonyMethod(typeof(Plugin), nameof(EnterRecording)),
                    finalizer: new HarmonyMethod(typeof(Plugin), nameof(LeaveRecording)));
                scopes++;
            }
            harmony.Patch(isConnected, postfix: new HarmonyMethod(typeof(Plugin), nameof(IsConnectedPostfix)));

            Type keyType = AccessTools.TypeByName("PortalPeek.PortalKey");
            Type library = AccessTools.TypeByName("PortalPeek.PeekLibrary");
            MethodInfo keyFor = keyType == null ? null : AccessTools.Method(keyType, "For", new[] { typeof(ZDO) });
            MethodInfo get = library == null ? null : AccessTools.Method(library, "Get", new[] { typeof(string), typeof(float) });
            libraryDir = library == null ? null : AccessTools.Field(library, "s_dir");
            string light = "day and night versions off (Portal Peek's layout differs)";
            if (keyFor != null && get != null && libraryDir != null)
            {
                harmony.Patch(keyFor, postfix: new HarmonyMethod(typeof(Plugin), nameof(KeyPostfix)));
                harmony.Patch(get, prefix: new HarmonyMethod(typeof(Plugin), nameof(GetPrefix)));
                light = "day and night versions";
            }
            Log.LogInfo($"{PluginName} {PluginVersion} ready ({scopes} of {RecordingMethods.Length} recording paths bridged, {light}).");
        }

        private static void EnterRecording() => recording++;

        private static Exception LeaveRecording(Exception __exception)
        {
            recording = Math.Max(0, recording - 1);
            return __exception;
        }

        private static bool Night => EnvMan.instance && EnvMan.IsNight();

        // Every portal key, for recording and for showing, carries the light it's for right now.
        private static void KeyPostfix(ref string __result)
        {
            if (dayNight.Value && !string.IsNullOrEmpty(__result))
            {
                __result += Night ? NightTag : DayTag;
            }
        }

        // Showing: if there's no recording for this light yet, use the other light's, then an untagged one.
        private static readonly Dictionary<string, (bool exists, float checkedAt)> onDisk =
            new Dictionary<string, (bool, float)>();
        private const float RecheckSeconds = 5f;

        private static void GetPrefix(ref string key)
        {
            if (!dayNight.Value || string.IsNullOrEmpty(key) || !(key.EndsWith(DayTag) || key.EndsWith(NightTag)))
            {
                return;
            }
            if (!(libraryDir.GetValue(null) is string dir))
            {
                return;
            }
            string bare = key.Substring(0, key.Length - 2);
            string other = bare + (key.EndsWith(DayTag) ? NightTag : DayTag);
            foreach (string candidate in new[] { key, other, bare })
            {
                if (Exists(dir, candidate))
                {
                    key = candidate;
                    return;
                }
            }
        }

        private static bool Exists(string dir, string key)
        {
            float now = Time.time;
            if (onDisk.TryGetValue(key, out var known) && now - known.checkedAt < RecheckSeconds)
            {
                return known.exists;
            }
            bool exists = File.Exists(Path.Combine(dir, key + ".ppk"));
            onDisk[key] = (exists, now);
            return exists;
        }

        // Only while Portal Peek is choosing what to record, and only for a portal that really exists in the world.
        private static void IsConnectedPostfix(MonoBehaviour __instance, ref bool __result)
        {
            if (__result || recording == 0 || !recordEvery.Value || !__instance)
            {
                return;
            }
            ZNetView view = __instance.GetComponent<ZNetView>();
            if (view && view.IsValid())
            {
                __result = true;
            }
        }
    }
}
