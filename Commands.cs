using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;

namespace CaveheimDiscordBridge
{
    /// <summary>
    /// Commands typed in the Discord command channel. help and players are open to anyone who can see the channel;
    /// everything else needs a Discord user ID on the admin list (IDs can't be faked the way display names can).
    /// All of it runs on the server through the game's own RPCs, except raising skills, which needs the client part.
    /// </summary>
    internal static class Commands
    {
        private class Command
        {
            public string Usage, Help;
            public bool Admin;
            public Func<string[], string> Run;
        }

        private static readonly Dictionary<string, Command> s_commands = new Dictionary<string, Command>(StringComparer.OrdinalIgnoreCase)
        {
            // --- server basics
            ["help"] = new Command { Usage = "help", Help = "This list.", Run = Help },
            ["players"] = new Command { Usage = "players", Help = "Who's online, and where.", Admin = true, Run = Players },
            ["restart"] = new Command { Usage = "restart", Help = "When the next scheduled restart is.", Run = NextRestart },
            ["save"] = new Command { Usage = "save", Help = "Save the world and every character now.", Admin = true, Run = Save },
            ["broadcast"] = new Command { Usage = "broadcast <message>", Help = "Show a message in the middle of everyone's screen.", Admin = true, Run = Broadcast },
            // --- moderation
            ["kick"] = new Command { Usage = "kick <player>", Help = "Kick a player.", Admin = true, Run = a => Moderate(a, "kick") },
            ["ban"] = new Command { Usage = "ban <player or Steam ID>", Help = "Ban a player (also kicks them).", Admin = true, Run = a => Moderate(a, "ban") },
            ["unban"] = new Command { Usage = "unban <player or Steam ID>", Help = "Lift a ban.", Admin = true, Run = a => Moderate(a, "unban") },
            ["bans"] = new Command { Usage = "bans", Help = "List banned players.", Admin = true, Run = Bans },
            // --- player admin
            ["pos"] = new Command { Usage = "pos <player>", Help = "A player's position and biome.", Admin = true, Run = Pos },
            ["tp"] = new Command { Usage = "tp <player> <x> <z>  |  tp <player> <other player>", Help = "Teleport a player.", Admin = true, Run = Teleport },
            ["give"] = new Command { Usage = "give <player> <item> [amount] [quality]", Help = "Drop an item at a player's feet.", Admin = true, Run = Give },
            ["status"] = new Command { Usage = "status <player> <effect>", Help = "Give a player a status effect (e.g. Rested).", Admin = true, Run = Status },
            ["skill"] = new Command { Usage = "skill <player> <skill> <levels>", Help = "Raise a player's skill (they need the client part).", Admin = true, Run = Skill },
            ["kill"] = new Command { Usage = "kill <player>", Help = "Kill a player.", Admin = true, Run = Kill },
        };

        private static ServerSettings S => DiscordBridgePlugin.Server;

        internal static void Run(Discord.DiscordMessage msg)
        {
            string prefix = S.CommandPrefix.Value ?? "!";
            string content = msg.content.Trim();
            if (!content.StartsWith(prefix, StringComparison.Ordinal)) return;

            string[] parts = Split(content.Substring(prefix.Length));
            string who = $"{msg.author.username} ({msg.author.id})";
            void Answer(string text, bool keep = false)
            {
                CommandLog.Write("command channel", who, content, text);
                CommandLog.ReplyThenClean(S.CommandChannelId.Value, msg.id, text, keep);
            }

            if (parts.Length == 0 || !s_commands.TryGetValue(parts[0], out Command cmd))
            {
                Answer($"Unknown command. Type `{prefix}help` for the list.");
                return;
            }
            if (cmd.Admin && !IsAdmin(msg.author.id))
            {
                Answer("⛔ That command is for admins only.");
                return;
            }
            if (ZNet.instance == null || !ZNet.instance.IsServer())
            {
                Answer("The world isn't loaded yet.");
                return;
            }
            string result;
            try { result = cmd.Run(parts.Skip(1).ToArray()); }
            catch (Exception ex)
            {
                DiscordBridgePlugin.Log.LogWarning($"Command '{content}' failed: {ex}");
                result = "❌ That didn't work: " + ex.Message;
            }
            if (result == null) result = $"Usage: `{prefix}{cmd.Usage}`";
            DiscordBridgePlugin.Log.LogInfo($"[Discord] {msg.author.username} ran: {content}");
            // help stays, so it can be pinned
            Answer(result, keep: cmd.Run == (Func<string[], string>)Help);
        }

        /// <summary>Runs one of the commands by name (the admin panel), on the main thread. Null when the arguments don't fit.</summary>
        internal static string Execute(string name, params string[] args)
        {
            if (!s_commands.TryGetValue(name, out Command cmd)) return "Unknown command.";
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return "The world isn't loaded yet.";
            try { return cmd.Run(args) ?? $"Usage: `{S.CommandPrefix.Value}{cmd.Usage}`"; }
            catch (Exception ex)
            {
                DiscordBridgePlugin.Log.LogWarning($"Panel command '{name}' failed: {ex}");
                return "❌ That didn't work: " + ex.Message;
            }
        }

        /// <summary>Names of the players online, for the panel's player lists (the host is left out: it can't be kicked).</summary>
        internal static List<string> OnlineNames() =>
            ZNet.instance == null ? new List<string>() : Online().Where(t => !t.IsHost).Select(t => t.m_playerName).OrderBy(n => n).ToList();

        internal static int OnlineCount() => ZNet.instance == null ? 0 : Online().Count;

        internal static bool IsAdmin(string userId) =>
            !string.IsNullOrEmpty(userId) && S.AdminIds.Value.Split(',').Any(id => id.Trim() == userId);

        /// <summary>Splits on spaces, keeping "quoted names" together.</summary>
        private static string[] Split(string s)
        {
            var parts = new List<string>();
            var cur = new StringBuilder();
            bool quoted = false;
            foreach (char c in s)
            {
                if (c == '"') { quoted = !quoted; continue; }
                if (char.IsWhiteSpace(c) && !quoted) { if (cur.Length > 0) { parts.Add(cur.ToString()); cur.Clear(); } continue; }
                cur.Append(c);
            }
            if (cur.Length > 0) parts.Add(cur.ToString());
            return parts.ToArray();
        }

        // ------------------------------------------------------------------ lookups

        /// <summary>A player the commands can act on: a connected player, or the player hosting the world.</summary>
        private class Target
        {
            public string m_playerName;
            public long m_uid;
            public ZDOID m_characterID;
            public bool IsHost;
            private ZNetPeer m_peer;

            public Vector3 GetRefPos() =>
                IsHost ? (Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero) : m_peer.GetRefPos();

            public static Target Of(ZNetPeer p) =>
                new Target { m_playerName = p.m_playerName, m_uid = p.m_uid, m_characterID = p.m_characterID, m_peer = p };
        }

        private static List<Target> Online()
        {
            List<Target> list = ZNet.instance.GetPeers().Where(p => p.IsReady() && !string.IsNullOrEmpty(p.m_playerName)).Select(Target.Of).ToList();
            // A world hosted from the game: the host isn't a connected peer, but is a player too
            if (!ZNet.instance.IsDedicated() && Player.m_localPlayer != null)
                list.Add(new Target { m_playerName = Player.m_localPlayer.GetPlayerName(), m_uid = ZNet.GetUID(), m_characterID = Player.m_localPlayer.GetZDOID(), IsHost = true });
            return list;
        }

        /// <summary>Exact name first, then a unique start-of-name match ("cave" finds "Caveman").</summary>
        private static Target FindPlayer(string name, out string error)
        {
            error = null;
            List<Target> peers = Online();
            Target exact = peers.FirstOrDefault(p => p.m_playerName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            List<Target> starts = peers.Where(p => p.m_playerName.StartsWith(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (starts.Count == 1) return starts[0];
            error = starts.Count == 0 ? $"No player online called **{name}**." : $"**{name}** matches {string.Join(", ", starts.Select(p => p.m_playerName))}. Be more specific.";
            return null;
        }

        private static string Where(Vector3 p)
        {
            string biome = WorldGenerator.instance != null ? WorldGenerator.instance.GetBiome(p).ToString() : "?";
            return $"{biome} ({p.x:0}, {p.z:0})";
        }

        private static bool TryFloat(string s, out float v) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

        // The player's character, and the peer that owns (runs) it
        private static bool Character(Target peer, out ZDOID id, out long owner)
        {
            id = peer.m_characterID;
            ZDO zdo = id.IsNone() ? null : ZDOMan.instance.GetZDO(id);
            owner = zdo?.GetOwner() ?? 0;
            return zdo != null && owner != 0;
        }

        // ------------------------------------------------------------------ commands

        private static string Help(string[] _)
        {
            string prefix = S.CommandPrefix.Value;
            var sb = new StringBuilder("**Commands**\n");
            foreach (Command c in s_commands.Values)
                sb.Append('`').Append(prefix).Append(c.Usage).Append("` ").Append(c.Help).Append(c.Admin ? " *(admin)*" : "").Append('\n');
            return sb.ToString();
        }

        private static string Players(string[] _)
        {
            List<Target> peers = Online();
            if (peers.Count == 0) return "Nobody's online right now.";
            var sb = new StringBuilder($"**{peers.Count} online**\n");
            foreach (Target p in peers.OrderBy(p => p.m_playerName))
                sb.Append("• ").Append(Discord.EscapeMarkdown(p.m_playerName)).Append(" · ").Append(Where(p.GetRefPos())).Append('\n');
            return sb.ToString();
        }

        // ------------------------------------------------------------------ chat commands

        /// <summary>Commands anyone can also use from game chat and the Discord chat channel.</summary>
        internal static readonly string[] ChatCommands = { "restart" };

        /// <summary>
        /// The answer when a chat line ("!restart") is one of the chat commands, else null. Discord answers can use
        /// Discord's own formatting (time stamps); game answers are plain text.
        /// </summary>
        internal static string ChatCommand(string text, bool forDiscord)
        {
            if (S == null || string.IsNullOrWhiteSpace(text)) return null;
            string prefix = S.CommandPrefix.Value ?? "!";
            string t = text.Trim();
            if (prefix.Length == 0 || !t.StartsWith(prefix, StringComparison.Ordinal)) return null;
            string name = t.Substring(prefix.Length).Trim();
            if (!ChatCommands.Contains(name, StringComparer.OrdinalIgnoreCase)) return null;

            // Only "restart" so far
            if (forDiscord) return NextRestart(null);
            DateTime next = Restarts.Next;
            return next == default ? "No restart is scheduled."
                : $"Next restart in {Restarts.Span(next - DateTime.UtcNow)} ({next:HH:mm} UTC).";
        }

        /// <summary>Every command name, for the client part to recognise them in game chat.</summary>
        internal static IEnumerable<string> AllNames => s_commands.Keys;

        /// <summary>
        /// A command typed in game chat: anyone can use the open ones, the admin ones need the player on the game's own
        /// admin list (adminlist.txt). Null when the line isn't a command, so it stays ordinary chat.
        /// </summary>
        internal static string GameCommand(long sender, string text)
        {
            if (S == null || string.IsNullOrWhiteSpace(text)) return null;
            string prefix = S.CommandPrefix.Value ?? "!";
            string t = text.Trim();
            if (prefix.Length == 0 || !t.StartsWith(prefix, StringComparison.Ordinal)) return null;
            string[] parts = Split(t.Substring(prefix.Length));
            if (parts.Length == 0 || !s_commands.TryGetValue(parts[0], out Command cmd)) return null;

            bool admin = IsGameAdmin(sender);
            if (cmd.Run == (Func<string[], string>)Help)
                return "Commands: " + string.Join(", ", s_commands.Where(c => admin || !c.Value.Admin).Select(c => prefix + c.Value.Usage));
            if (parts[0].Equals("restart", StringComparison.OrdinalIgnoreCase)) return ChatCommand(t, forDiscord: false);
            if (cmd.Admin && !admin) return "That command is for admins only.";

            string result = Execute(parts[0], parts.Skip(1).ToArray());
            return Plain(result);
        }

        /// <summary>On the game's admin list (any ID form adminlist.txt accepts), or the player hosting the world.</summary>
        private static bool IsGameAdmin(long sender)
        {
            if (ZNet.instance == null) return false;
            if (!ZNet.instance.IsDedicated() && sender == ZNet.GetUID()) return true;
            ZNetPeer peer = ZNet.instance.GetPeer(sender);
            string host = peer?.m_socket?.GetHostName();
            return !string.IsNullOrEmpty(host) && ZNet.instance.IsAdmin(host);
        }

        // Command answers are written for Discord: drop the bold, code marks and escapes for game chat
        private static string Plain(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\\([\\*_~`|>#\[\]()\-])", "$1");
            return s.Replace("**", "").Replace("`", "").Replace("\n", "  ");
        }

        private static string NextRestart(string[] _)
        {
            DateTime next = Restarts.Next;
            if (next == default) return "No restart is scheduled.";
            long unix = (long)(next - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            // Discord shows <t:...> in each reader's own time zone
            return $"🔁 Next restart <t:{unix}:t> (<t:{unix}:R>).";
        }

        private static string Save(string[] _)
        {
            ZNet.instance.Save(false, true);
            return "💾 Saving the world and all characters.";
        }

        private static string Broadcast(string[] args)
        {
            if (args.Length == 0) return null;
            string text = string.Join(" ", args).Replace('<', ' ').Replace('>', ' ');
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "ShowMessage", (int)MessageHud.MessageType.Center, text);
            return "📢 Sent.";
        }

        private static string Moderate(string[] args, string action)
        {
            if (args.Length == 0) return null;
            string who = string.Join(" ", args);
            // A unique partial name of someone online becomes their full name
            Target peer = action == "unban" ? null : FindPlayer(who, out _);
            if (peer != null && peer.IsHost) return "The host can't be kicked or banned from their own world.";
            if (peer != null) who = peer.m_playerName;
            switch (action)
            {
                case "kick":
                    if (peer == null) return $"No player online called **{who}**.";
                    ZNet.instance.Kick(who);
                    return $"👢 Kicked **{who}**.";
                case "ban":
                    ZNet.instance.Ban(who);
                    return $"🔨 Banned **{who}**.";
                default:
                    ZNet.instance.Unban(who);
                    return $"✅ Unbanned **{who}**.";
            }
        }

        private static string Bans(string[] _)
        {
            List<string> bans = ZNet.instance.Banned;
            return bans.Count == 0 ? "Nobody is banned." : "**Banned:**\n" + string.Join("\n", bans.Select(b => "• " + Discord.EscapeMarkdown(b)));
        }

        private static string Pos(string[] args)
        {
            if (args.Length == 0) return null;
            Target peer = FindPlayer(string.Join(" ", args), out string error);
            return peer == null ? error : $"📍 **{peer.m_playerName}** is in {Where(peer.GetRefPos())}.";
        }

        private static string Teleport(string[] args)
        {
            if (args.Length < 2) return null;
            Target peer = FindPlayer(args[0], out string error);
            if (peer == null) return error;

            Vector3 to;
            string where;
            if (args.Length >= 3 && TryFloat(args[1], out float x) && TryFloat(args[2], out float z))
            {
                float y = WorldGenerator.instance != null ? WorldGenerator.instance.GetHeight(x, z) : 0f;
                to = new Vector3(x, Mathf.Max(y, ZoneSystem.instance.m_waterLevel) + 1f, z);
                where = $"({x:0}, {z:0})";
            }
            else
            {
                Target target = FindPlayer(string.Join(" ", args.Skip(1)), out error);
                if (target == null) return error;
                to = target.GetRefPos() + Vector3.up;
                where = target.m_playerName;
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, "RPC_TeleportPlayer", to, Quaternion.identity, true);
            return $"✨ Teleporting **{peer.m_playerName}** to {where}.";
        }

        private static string Give(string[] args)
        {
            if (args.Length < 2) return null;
            Target peer = FindPlayer(args[0], out string error);
            if (peer == null) return error;

            GameObject prefab = ObjectDB.instance.GetItemPrefab(args[1])
                ?? ObjectDB.instance.m_items.FirstOrDefault(i => i.name.Equals(args[1], StringComparison.OrdinalIgnoreCase));
            ItemDrop drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (drop == null) return $"There's no item called **{args[1]}** (use the prefab name, e.g. `SwordIron`).";

            int amount = args.Length > 2 && int.TryParse(args[2], out int a) ? Mathf.Clamp(a, 1, 9999) : 1;
            int quality = args.Length > 3 && int.TryParse(args[3], out int q) ? Mathf.Clamp(q, 1, drop.m_itemData.m_shared.m_maxQuality) : 1;

            // Dropped in stacks at their feet: the server can't put things straight into a player's inventory
            int maxStack = Mathf.Max(1, drop.m_itemData.m_shared.m_maxStackSize), left = amount;
            Vector3 at = peer.GetRefPos() + Vector3.up * 1.5f;
            while (left > 0)
            {
                int n = Mathf.Min(left, maxStack);
                ItemDrop.ItemData item = drop.m_itemData.Clone();
                item.m_quality = quality;
                item.m_dropPrefab = prefab;
                ItemDrop.DropItem(item, n, at, Quaternion.identity);
                left -= n;
            }
            return $"🎁 Dropped {amount}× **{prefab.name}**{(quality > 1 ? $" (quality {quality})" : "")} at **{peer.m_playerName}**'s feet.";
        }

        private static string Status(string[] args)
        {
            if (args.Length < 2) return null;
            Target peer = FindPlayer(args[0], out string error);
            if (peer == null) return error;
            StatusEffect se = ObjectDB.instance.m_StatusEffects.FirstOrDefault(e => e.name.Equals(args[1], StringComparison.OrdinalIgnoreCase));
            if (se == null) return $"There's no status effect called **{args[1]}**.";
            if (!Character(peer, out ZDOID id, out long owner)) return $"**{peer.m_playerName}** hasn't spawned in yet.";
            ZRoutedRpc.instance.InvokeRoutedRPC(owner, id, "RPC_AddStatusEffect", se.NameHash(), true, 0, 0f, 0);
            return $"✨ Gave **{peer.m_playerName}** {se.name}.";
        }

        private static string Skill(string[] args)
        {
            if (args.Length < 3 || !TryFloat(args[2], out float levels)) return null;
            Target peer = FindPlayer(args[0], out string error);
            if (peer == null) return error;
            if (!ClientPart.HasClient(peer.m_uid)) return $"**{peer.m_playerName}** doesn't have Caveman's Discord Bridge installed, so their skills can't be changed from here.";
            ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, "CDB_RaiseSkill", args[1], levels);
            return $"📈 Raising **{peer.m_playerName}**'s {args[1]} by {levels:0.#}.";
        }

        private static string Kill(string[] args)
        {
            if (args.Length == 0) return null;
            Target peer = FindPlayer(string.Join(" ", args), out string error);
            if (peer == null) return error;
            if (!Character(peer, out ZDOID id, out long owner)) return $"**{peer.m_playerName}** hasn't spawned in yet.";
            var hit = new HitData { m_point = peer.GetRefPos() };
            hit.m_damage.m_damage = 1e9f;
            ZRoutedRpc.instance.InvokeRoutedRPC(owner, id, "RPC_Damage", hit);
            return $"☠️ **{peer.m_playerName}** has been struck down.";
        }
    }
}
