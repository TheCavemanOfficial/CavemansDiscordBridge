using System;
using System.Collections.Generic;
using HarmonyLib;

namespace CaveheimDiscordBridge
{
    /// <summary>
    /// Maintenance mode from the admin panel: saves the world, sends everyone but admins out, and only lets admins join
    /// until it's ended. With ServerCharacters installed this is its own maintenance mode (its "maintenance" file, the same
    /// one the restart warnings use); without it, the bridge turns non-admins (the game's adminlist.txt) away itself.
    /// The server keeps running, so a host that restarts servers that exit (AMP) doesn't bring it straight back the way a
    /// stop did. Kept in the settings file, so it lasts through restarts.
    /// </summary>
    internal static class Maintenance
    {
        private static ServerSettings S => DiscordBridgePlugin.Server;

        internal static bool On => S != null && S.MaintenanceOn.Value;

        internal static string Set(bool on, string by)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return "The world isn't loaded yet.";
            if (on == On) return on ? "The server is already in maintenance." : "The server isn't in maintenance.";
            S.MaintenanceOn.Value = on;
            DiscordBridgePlugin.Log.LogInfo($"Maintenance mode {(on ? "on" : "off")} (by {by}).");

            ApplyFile();
            if (on)
            {
                ZNet.instance.Save(false, true);
                Announce("Server going into maintenance. Only admins can stay.");
                int sent = 0;
                foreach (ZNetPeer peer in new List<ZNetPeer>(ZNet.instance.GetPeers()))
                {
                    if (IsAdmin(peer)) continue;
                    peer.m_rpc?.Invoke("Error", (int)ZNet.ConnectionStatus.ErrorDisconnected);
                    ZNet.instance.Disconnect(peer);
                    sent++;
                }
                if (S.IsServer.Value) Discord.Webhook(S.NotifyWebhook.Value, "🛠️ **Server is in maintenance.** Only admins can join for now.");
                return $"🛠️ Maintenance mode on. The world is saved{(sent > 0 ? $", {sent} player(s) sent out" : "")}, and only admins can join until you end it.\n*by {Discord.EscapeMarkdown(by)}*";
            }

            Announce("Maintenance is over: the server is open again.");
            if (S.IsServer.Value) Discord.Webhook(S.NotifyWebhook.Value, "✅ **Server is open again.** Come on in.");
            return $"✅ Maintenance ended: everyone can join again.\n*by {Discord.EscapeMarkdown(by)}*";
        }

        /// <summary>ServerCharacters' maintenance file follows the setting (also at every start, after restarts cleared it).</summary>
        internal static void ApplyFile()
        {
            string file = Restarts.MaintenanceFile();
            if (file == null) return;
            try
            {
                if (On && !System.IO.File.Exists(file)) System.IO.File.WriteAllText(file, "");
                else if (!On && System.IO.File.Exists(file)) System.IO.File.Delete(file);
            }
            catch (Exception ex) { DiscordBridgePlugin.Log.LogWarning($"Couldn't switch ServerCharacters' maintenance mode: {ex.Message}"); }
        }

        private static void Announce(string text)
        {
            if (ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "ShowMessage", (int)MessageHud.MessageType.Center, text);
            ChatBridge.ServerShout("Server", text);
        }

        private static bool IsAdmin(ZNetPeer peer)
        {
            string host = null;
            try { host = peer?.m_socket?.GetHostName(); } catch { }
            return !string.IsNullOrEmpty(host) && ZNet.instance.IsAdmin(host);
        }

        // While in maintenance, players who aren't admins are turned away as they connect
        [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
        private static class JoinGate
        {
            private static bool Prefix(ZNet __instance, ZRpc rpc)
            {
                // ServerCharacters does this itself when it's installed
                if (!On || !__instance.IsServer() || rpc == null || Restarts.MaintenanceFile() != null) return true;
                string host = null;
                try { host = rpc.GetSocket()?.GetHostName(); } catch { }
                if (!string.IsNullOrEmpty(host) && __instance.IsAdmin(host)) return true;

                DiscordBridgePlugin.Log.LogInfo($"Maintenance: turned away {host ?? "a player"}.");
                rpc.Invoke("Error", (int)ZNet.ConnectionStatus.ErrorDisconnected);
                ZNetPeer peer = __instance.GetPeers().Find(p => p.m_rpc == rpc);
                if (peer != null) __instance.Disconnect(peer);
                return false;
            }
        }
    }
}
