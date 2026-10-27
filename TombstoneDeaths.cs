using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace CaveheimDiscordBridge
{
    /// <summary>
    /// Deaths on a server without DarwinAwards: a fresh tombstone appearing on the server means its owner just died.
    /// With DarwinAwards installed this stays quiet, and its death lines are used instead.
    /// </summary>
    internal static class TombstoneDeaths
    {
        private const string DarwinGuid = "org.bepinex.plugins.darwinawards";
        private static readonly HashSet<ZDOID> s_seen = new HashSet<ZDOID>();

        private static readonly string[] s_lines =
        {
            "{0} has fallen in the {1}.",
            "{0} met their end in the {1}.",
            "The {1} claimed {0}.",
            "{0} is on their way to Valhalla (from the {1}).",
            "{0} died in the {1}. Their tombstone awaits.",
        };

        [HarmonyPatch(typeof(TombStone), "Start")]
        private static class TombStoneStart
        {
            private static void Postfix(TombStone __instance, ZNetView ___m_nview)
            {
                if (DiscordBridgePlugin.Server == null || ZNet.instance == null || !ZNet.instance.IsServer()) return;
                if (Chainloader.PluginInfos.ContainsKey(DarwinGuid)) return;
                if (___m_nview == null || !___m_nview.IsValid()) return;
                try
                {
                    ZDO zdo = ___m_nview.GetZDO();
                    if (!s_seen.Add(zdo.m_uid)) return;

                    // Only new tombstones: an old one loads in whenever someone walks past it
                    long died = zdo.GetLong(ZDOVars.s_timeOfDeath, 0L);
                    if (died != 0L && (ZNet.instance.GetTime() - new DateTime(died)).TotalSeconds > 120) return;

                    string name = zdo.GetString(ZDOVars.s_ownerName);
                    if (string.IsNullOrWhiteSpace(name)) return;
                    Vector3 pos = __instance.transform.position;
                    string biome = WorldGenerator.instance != null ? WorldGenerator.instance.GetBiome(pos).ToString() : "wilds";
                    string line = string.Format(s_lines[UnityEngine.Random.Range(0, s_lines.Length)], name, biome);

                    ZNetPeer peer = ZNet.instance.GetPeers().FirstOrDefault(p => p.m_playerName == name);
                    Deaths.Report(peer?.m_uid ?? ZNet.GetUID(), line);
                }
                catch (Exception ex) { DiscordBridgePlugin.Log.LogDebug($"Tombstone death check failed: {ex.Message}"); }
            }
        }
    }
}
