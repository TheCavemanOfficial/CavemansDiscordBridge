using System.Collections.Generic;
using HarmonyLib;

namespace CaveheimDiscordBridge
{
    /// <summary>Server lifecycle and player join/leave posts to the notification webhook.</summary>
    internal static class Notifications
    {
        private static bool s_online, s_stopping;
        private static readonly HashSet<long> s_joined = new HashSet<long>();

        private static ServerSettings S => DiscordBridgePlugin.Server;

        internal static void ServerLaunching()
        {
            if (S.NotifyLaunch.Value) Discord.Webhook(S.NotifyWebhook.Value, "🟡 **Server is starting up…**");
        }

        internal static void ServerStopped()
        {
            if (!s_online || !S.NotifyStopped.Value) return;
            s_online = false;
            Discord.WebhookNow(S.NotifyWebhook.Value, "🔴 **Server is offline.**");
        }

        // The world is loaded inside ZNet.Awake on a server: from here players can join
        [HarmonyPatch(typeof(ZNet), "Awake")]
        private static class ZNetAwake
        {
            private static void Postfix(ZNet __instance)
            {
                if (!__instance.IsServer()) return;
                DiscordBridgePlugin.EnsureServerSettings();
                s_online = true;
                s_stopping = false;
                s_joined.Clear();
                if (S.NotifyStarted.Value) Discord.Webhook(S.NotifyWebhook.Value, "🟢 **Server is online!** Come on in.");
            }
        }

        // The world has closed (quit, or back to the main menu), while the game is still fully running
        [HarmonyPatch(typeof(ZNet), "OnDestroy")]
        private static class ZNetDestroy
        {
            private static void Prefix(ZNet __instance)
            {
                if (S != null && __instance.IsServer()) ServerStopped();
            }
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
        private static class ZNetShutdown
        {
            private static void Prefix(ZNet __instance)
            {
                if (S == null || !__instance.IsServer() || s_stopping) return;
                s_stopping = true;
                if (S.NotifyStopping.Value) Discord.WebhookNow(S.NotifyWebhook.Value, "🟠 **Server is shutting down…**");
            }

            // Shutdown has saved the world and dropped every player: the server is gone from here on. Sent now rather
            // than when the world object is destroyed, which a quitting server may never get to.
            private static void Postfix(ZNet __instance)
            {
                if (S != null && __instance.IsServer()) ServerStopped();
            }
        }

        // A player's character ID arrives once they've spawned in: that's the moment they've really joined
        [HarmonyPatch(typeof(ZNet), "RPC_CharacterID")]
        private static class CharacterId
        {
            private static void Postfix(ZNet __instance, ZRpc rpc)
            {
                if (S == null || !__instance.IsServer()) return;
                ZNetPeer peer = __instance.GetPeers().Find(p => p.m_rpc == rpc);
                if (peer == null || peer.m_characterID.IsNone() || !s_joined.Add(peer.m_uid)) return;
                if (S.NotifyJoin.Value) Discord.Webhook(S.NotifyWebhook.Value, $"➡️ **{Discord.EscapeMarkdown(peer.m_playerName)}** joined the server.");
            }
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect))]
        private static class Disconnect
        {
            private static void Prefix(ZNet __instance, ZNetPeer peer)
            {
                if (S == null || peer == null || !__instance.IsServer()) return;
                ClientPart.Forget(peer.m_uid);
                if (!s_joined.Remove(peer.m_uid) || s_stopping) return;
                if (S.NotifyLeave.Value) Discord.Webhook(S.NotifyWebhook.Value, $"⬅️ **{Discord.EscapeMarkdown(peer.m_playerName)}** left the server.");
            }
        }
    }
}
