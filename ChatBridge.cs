using System;
using System.Collections.Generic;
using HarmonyLib;
using Splatform;
using UnityEngine;

namespace CaveheimDiscordBridge
{
    /// <summary>
    /// Shouts travel as one "ChatMessage" routed RPC per recipient (vanilla checks each player's chat permission), so
    /// the server sees a copy per other player - but none at all when the shouter is alone, since a client handles
    /// its own copy locally. Copies are caught where the server routes them (dedicated) and where it handles them
    /// (a hosting player's own shouts), and folded into one post. The client part also reports its shouts, which
    /// covers the player-alone case.
    /// </summary>
    internal static class ChatBridge
    {
        private static readonly int s_chatHash = "ChatMessage".GetStableHashCode();
        private static readonly int s_sayHash = "Say".GetStableHashCode();
        private static readonly int s_darwinHash = "DarwinAwards IDied".GetStableHashCode();
        private const int ShoutType = 2;   // Talker.Type.Shout

        // Recently relayed (sender + text), so the per-recipient copies post once
        private static readonly Dictionary<string, float> s_recent = new Dictionary<string, float>();
        private static bool s_injecting;

        // Discord messages need a valid user ID or every client drops them ("UserID was invalid"); a fixed one that
        // belongs to nobody
        private static readonly PlatformUserID s_discordUser = new PlatformUserID("Steam_76561197960265729");

        [HarmonyPatch(typeof(ZRoutedRpc), "RouteRPC")]
        private static class RoutePatch
        {
            private static void Prefix(ZRoutedRpc.RoutedRPCData rpcData) => Inspect(rpcData);
        }

        [HarmonyPatch(typeof(ZRoutedRpc), "HandleRoutedRPC")]
        private static class HandlePatch
        {
            private static void Prefix(ZRoutedRpc.RoutedRPCData data) => Inspect(data);
        }

        private static void Inspect(ZRoutedRpc.RoutedRPCData data)
        {
            if (s_injecting || DiscordBridgePlugin.Server == null || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            try
            {
                if (data.m_methodHash == s_chatHash) ReadShout(data);
                else if (data.m_methodHash == s_sayHash) ReadSay(data);
                else if (data.m_methodHash == s_darwinHash) ReadDeath(data);
            }
            catch (Exception ex) { DiscordBridgePlugin.Log.LogDebug($"Couldn't read a routed message: {ex.Message}"); }
        }

        private static void ReadShout(ZRoutedRpc.RoutedRPCData data)
        {
            var pkg = new ZPackage(data.m_parameters.GetArray());
            pkg.ReadVector3();
            int type = pkg.ReadInt();
            string name = pkg.ReadString();
            string platformId = pkg.ReadString();
            string text = pkg.ReadString();
            if (type != ShoutType) return;
            if (GameCommand(data.m_senderPeerID, text)) return;
            ShoutToDiscord(data.m_senderPeerID, name, text, SteamAvatars.FromPlatformId(platformId));
        }

        // Normal chat ("Say") goes player to player through the server; only read for chat commands
        private static void ReadSay(ZRoutedRpc.RoutedRPCData data)
        {
            var pkg = new ZPackage(data.m_parameters.GetArray());
            pkg.ReadInt();      // type
            pkg.ReadString();   // name
            pkg.ReadString();   // platform ID
            GameCommand(data.m_senderPeerID, pkg.ReadString());
        }

        /// <summary>
        /// A chat command typed in game by a player without the client part (with it, the line never leaves their game
        /// and comes as CDB_Command instead). Answered once however many copies pass through.
        /// </summary>
        internal static bool GameCommand(long sender, string text)
        {
            string reply = Commands.GameCommand(sender, text);
            if (reply == null) return false;

            float now = Time.realtimeSinceStartup;
            string key = "cmd\n" + sender + "\n" + text.Trim();
            if (s_recent.TryGetValue(key, out float at) && now - at < 3f) return true;
            s_recent[key] = now;
            CommandLog.Write("game chat", ClientPart.PlayerName(sender), text.Trim(), reply);
            ClientPart.Reply(sender, reply);
            return true;
        }

        private static void ReadDeath(ZRoutedRpc.RoutedRPCData data)
        {
            var pkg = new ZPackage(data.m_parameters.GetArray());
            pkg.ReadString();   // category
            string text = pkg.ReadString();
            Deaths.Report(data.m_senderPeerID, text);
        }

        /// <summary>Posts a shout once, however many copies of it come through.</summary>
        internal static void ShoutToDiscord(long sender, string name, string text, string steamId)
        {
            ServerSettings s = DiscordBridgePlugin.Server;
            if (s == null || !s.ChatEnabled.Value || !s.ShoutsToDiscord.Value || string.IsNullOrWhiteSpace(text)) return;
            if (!IsPlayer(sender, name)) return;   // messages mods send as the server, or under a made-up name
            if (IsIgnored(s, text)) return;        // mods that shout through a player's own chat (OreMines)

            float now = Time.realtimeSinceStartup;
            string key = sender + "\n" + text;
            if (s_recent.TryGetValue(key, out float at) && now - at < 3f) return;
            s_recent[key] = now;
            if (s_recent.Count > 200)
            {
                var old = new List<string>();
                foreach (var kv in s_recent) if (now - kv.Value > 10f) old.Add(kv.Key);
                foreach (string k in old) s_recent.Remove(k);
            }

            // Posted under the character's name (the webhook's logo stays)
            Discord.Webhook(s.ChatWebhook.Value, Discord.EscapeMarkdown(text), string.IsNullOrWhiteSpace(name) ? "Viking" : name, null, steamId ?? SteamIdOf(sender));
        }

        /// <summary>
        /// A shout from a real player: the sender is a connected player (or the host) going by that character name.
        /// Messages mods send through chat come from the server, or under a name no player has.
        /// </summary>
        private static bool IsPlayer(long sender, string name)
        {
            if (ZNet.instance == null || string.IsNullOrWhiteSpace(name)) return false;
            ZNetPeer peer = ZNet.instance.GetPeer(sender);
            if (peer != null) return string.Equals(peer.m_playerName, name, StringComparison.Ordinal);
            return !ZNet.instance.IsDedicated() && sender == ZNet.GetUID() && Player.m_localPlayer != null &&
                   string.Equals(Player.m_localPlayer.GetPlayerName(), name, StringComparison.Ordinal);
        }

        private static bool IsIgnored(ServerSettings s, string text)
        {
            string t = text.Trim();
            foreach (string ignored in s.IgnoredShouts.Value.Split('|'))
                if (ignored.Trim().Length > 0 && string.Equals(ignored.Trim(), t, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>A connected player's Steam ID (their connection's host name), or the hosting player's own.</summary>
        internal static string SteamIdOf(long peerId)
        {
            ZNetPeer peer = ZNet.instance?.GetPeer(peerId);
            if (peer != null) return SteamAvatars.FromPlatformId(peer.m_socket?.GetHostName());
            if (ZNet.instance != null && !ZNet.instance.IsDedicated() && peerId == ZNet.GetUID())
                return SteamAvatars.FromPlatformId(PlatformManager.DistributionPlatform.LocalUser.PlatformUserID.ToString());
            return null;
        }

        /// <summary>A message typed in the Discord chat channel, shown to every player as a shout.</summary>
        internal static void FromDiscord(Discord.DiscordMessage msg)
        {
            if (ZRoutedRpc.instance == null || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            string author = string.IsNullOrWhiteSpace(msg.author.global_name) ? msg.author.username : msg.author.global_name;
            string text = Clean(msg.content);
            if (text.Length == 0) return;

            // Chat commands are answered in the channel and stay out of the game
            string reply = Commands.ChatCommand(msg.content, forDiscord: true);
            if (reply != null)
            {
                CommandLog.Write("chat channel", $"{msg.author.username} ({msg.author.id})", msg.content.Trim(), reply);
                CommandLog.ReplyThenClean(DiscordBridgePlugin.Server.ChatChannelId.Value, msg.id, reply, keep: false);
                return;
            }

            // The config trims trailing spaces, so the gap after the prefix is added here
            string prefix = DiscordBridgePlugin.Server.InGamePrefix.Value.Trim();
            var user = new UserInfo { Name = (prefix.Length > 0 ? prefix + " " : "") + Clean(author), UserId = s_discordUser };
            s_injecting = true;
            try
            {
                // Far below the world, so no shout bubble appears over anyone's head
                ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "ChatMessage", new Vector3(0f, -10000f, 0f), ShoutType, user, text);
            }
            finally { s_injecting = false; }
        }

        /// <summary>A shout from the server itself (restart warnings) that isn't sent back to Discord.</summary>
        internal static void ServerShout(string name, string text)
        {
            if (ZRoutedRpc.instance == null || string.IsNullOrWhiteSpace(text)) return;
            var user = new UserInfo { Name = Clean(name), UserId = s_discordUser };
            s_injecting = true;
            try { ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "ChatMessage", new Vector3(0f, -10000f, 0f), ShoutType, user, Clean(text)); }
            finally { s_injecting = false; }
        }

        /// <summary>The game strips angle brackets (rich text) anyway; also keep Discord's custom emoji codes readable.</summary>
        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = System.Text.RegularExpressions.Regex.Replace(s, @"<a?(:\w+:)\d+>", "$1");
            s = s.Replace('<', ' ').Replace('>', ' ').Replace('\n', ' ').Trim();
            return Discord.Truncate(s, 300);
        }
    }
}
