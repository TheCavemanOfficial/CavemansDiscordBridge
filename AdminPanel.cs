using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace CaveheimDiscordBridge
{
    /// <summary>
    /// One message in an admin channel, kept up to date: server status, who's online, uptime and the next restart, with
    /// buttons to restart the server (now or in a few minutes), maintenance mode, skip the next restart, save, broadcast, and
    /// kick / ban / unban. Only the Admin User IDs can use it. Clicks reach the server over the Gateway connection and
    /// run on the main thread; risky ones (restart, maintenance, kick, ban) ask to confirm first, privately.
    /// </summary>
    internal static class AdminPanel
    {
        private static ServerSettings S => DiscordBridgePlugin.Server;

        private static bool s_started;
        private static DateTime s_since;
        private static float s_nextRefresh, s_nextCheck;
        private static string s_lastSig;
        private static volatile bool s_busy, s_dirty, s_offline;

        private static bool On => S != null && S.PanelEnabled.Value && !string.IsNullOrWhiteSpace(S.PanelChannelId.Value)
                                  && !string.IsNullOrWhiteSpace(S.BotToken.Value);

        /// <summary>When the world is up on the server.</summary>
        internal static void Start()
        {
            s_since = DateTime.UtcNow;
            s_offline = false;
            s_lastSig = null;
            s_nextRefresh = 0f;
            if (!On || s_started) return;
            s_started = true;
            Gateway.Interaction += d => DiscordBridgePlugin.RunOnMainThread(() => Handle(d));
            Gateway.Start();
        }

        internal static void Stop() => Gateway.Stop();

        /// <summary>
        /// The server process has started (sent with the "starting up" post, long before the world loads): the panel
        /// shows that, without buttons, until the world is up and the normal refresh takes over.
        /// </summary>
        internal static void Starting()
        {
            if (!On || string.IsNullOrWhiteSpace(S.PanelMessageId.Value)) return;
            string url = $"/channels/{S.PanelChannelId.Value.Trim()}/messages/{S.PanelMessageId.Value.Trim()}";
            string json = BuildPanel(State.Starting);
            s_busy = true;
            Task.Run(() =>
            {
                try
                {
                    int code = Discord.BotCall("PATCH", url, json, out string body);
                    if (code < 200 || code >= 300) DiscordBridgePlugin.Log.LogWarning($"Couldn't mark the admin panel as starting ({code}): {Discord.Truncate(body, 200)}");
                }
                catch (Exception ex) { DiscordBridgePlugin.Log.LogWarning($"Couldn't mark the admin panel as starting: {ex.Message}"); }
                finally { s_busy = false; }
            });
        }

        /// <summary>Main thread: refreshes on a timer, and soon after players come or go.</summary>
        internal static void Update()
        {
            if (!s_started || !On || s_offline || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            float now = Time.realtimeSinceStartup;
            if (now >= s_nextCheck)
            {
                s_nextCheck = now + 3f;
                string sig = string.Join(",", Commands.OnlineNames()) + "|" + Commands.OnlineCount() + "|" + Restarts.Next.Ticks + "|" + Restarts.ManualPending + "|" + Maintenance.On;
                if (sig != s_lastSig) { s_lastSig = sig; s_dirty = true; }
            }
            if ((s_dirty || now >= s_nextRefresh) && !s_busy)
            {
                s_dirty = false;
                s_nextRefresh = now + Mathf.Max(15f, S.PanelUpdateSeconds.Value);
                Publish(BuildPanel(State.Online));
            }
        }

        /// <summary>The server is going away: the panel shows offline, with its buttons gone. Sent straight away.</summary>
        internal static void Offline()
        {
            if (!s_started || !On || s_offline) return;
            // No more "online" refreshes from here, and let one already on its way land first, so it can't undo this
            s_offline = true;
            for (int i = 0; i < 30 && s_busy; i++) System.Threading.Thread.Sleep(100);
            if (string.IsNullOrWhiteSpace(S.PanelMessageId.Value)) return;
            try
            {
                int code = Discord.BotCall("PATCH", $"/channels/{S.PanelChannelId.Value.Trim()}/messages/{S.PanelMessageId.Value.Trim()}", BuildPanel(State.Offline), out string body, urgent: true);
                if (code >= 200 && code < 300) DiscordBridgePlugin.Log.LogInfo("Admin panel marked offline.");
                else DiscordBridgePlugin.Log.LogWarning($"Couldn't mark the admin panel offline ({code}): {Discord.Truncate(body, 200)}");
            }
            catch (Exception ex) { DiscordBridgePlugin.Log.LogWarning($"Couldn't mark the admin panel offline: {ex.Message}"); }
        }

        // Edits the panel message, or posts a new one when there's none (or it was deleted)
        private static void Publish(string json)
        {
            s_busy = true;
            string channel = S.PanelChannelId.Value.Trim(), id = S.PanelMessageId.Value.Trim();
            Task.Run(() =>
            {
                try
                {
                    // No ID of our own (another server posted it, or the config was reset): take over the bot's existing panel
                    if (id.Length == 0)
                    {
                        id = FindExisting(channel) ?? "";
                        if (id.Length > 0)
                        {
                            string found = id;
                            DiscordBridgePlugin.RunOnMainThread(() => S.PanelMessageId.Value = found);
                            DiscordBridgePlugin.Log.LogInfo("Found the existing admin panel in the channel; using it.");
                        }
                    }
                    if (s_offline) return;   // the server went down while this waited
                    if (id.Length > 0)
                    {
                        int code = Discord.BotCall("PATCH", $"/channels/{channel}/messages/{id}", json, out string err);
                        if (code >= 200 && code < 300) return;
                        if (code != 404) { DiscordBridgePlugin.Log.LogWarning($"Couldn't update the admin panel ({code}): {Discord.Truncate(err, 200)}"); return; }
                    }
                    int posted = Discord.BotCall("POST", $"/channels/{channel}/messages", json, out string body);
                    string newId = posted >= 200 && posted < 300 && Json.Parse(body) is Dictionary<string, object> m ? m.Str("id") : null;
                    if (newId == null) { DiscordBridgePlugin.Log.LogWarning($"Couldn't post the admin panel ({posted}): {Discord.Truncate(body, 200)}. Check the panel Channel ID and that the bot can send messages there."); return; }
                    DiscordBridgePlugin.RunOnMainThread(() => S.PanelMessageId.Value = newId);
                    DiscordBridgePlugin.Log.LogInfo("Posted the admin panel.");
                }
                catch (Exception ex) { DiscordBridgePlugin.Log.LogWarning($"Admin panel update failed: {ex.Message}"); }
                finally { s_busy = false; }
            });
        }

        private const string Title = "🛠️ Server Admin Panel";

        /// <summary>The newest panel this bot posted in the channel: pinned messages first, then the latest 50.</summary>
        private static string FindExisting(string channel)
        {
            if (Discord.BotCall("GET", "/users/@me", null, out string me) != 200 || !(Json.Parse(me) is Dictionary<string, object> bot)) return null;
            string botId = bot.Str("id");
            foreach (string path in new[] { $"/channels/{channel}/pins", $"/channels/{channel}/messages?limit=50" })
            {
                if (Discord.BotCall("GET", path, null, out string body) != 200 || !(Json.Parse(body) is List<object> list)) continue;
                foreach (object item in list)
                {
                    if (!(item is Dictionary<string, object> m) || m.Obj("author")?.Str("id") != botId) continue;
                    if (m.TryGetValue("embeds", out object e) && e is List<object> embeds && embeds.Count > 0 &&
                        embeds[0] is Dictionary<string, object> embed && embed.Str("title") == Title)
                        return m.Str("id");
                }
            }
            return null;
        }

        // ------------------------------------------------------------------ the message

        private enum State { Offline, Starting, Online }

        private static string BuildPanel(State state)
        {
            bool online = state == State.Online;
            var sb = new StringBuilder("{\"content\":\"\",\"allowed_mentions\":{\"parse\":[]},\"embeds\":[{");
            string world = ZNet.World?.m_name;
            sb.Append("\"title\":").Append(Discord.Quote(Title));
            bool maint = online && Maintenance.On;
            sb.Append(",\"color\":").Append(maint ? 0xE67E22 : online ? 0x57F287 : state == State.Starting ? 0xFEE75C : 0xED4245);
            string status = maint ? "🛠️ **Maintenance** (only admins can join)" : online ? "🟢 **Online**" : state == State.Starting ? "🟡 **Starting up…**" : "🔴 **Offline**";
            sb.Append(",\"description\":").Append(Discord.Quote(status + (string.IsNullOrEmpty(world) ? "" : " · " + Discord.EscapeMarkdown(world))));
            sb.Append(",\"fields\":[");
            if (online)
            {
                List<string> names = Commands.OnlineNames();
                int count = Commands.OnlineCount();
                string list = names.Count == 0 ? (count > 0 ? "*(only the host)*" : "*Nobody online*")
                    : Discord.Truncate(string.Join("\n", names.Select(n => "• " + Discord.EscapeMarkdown(n))), 1000);
                Field(sb, $"Players ({count})", list, false);
                Field(sb, "Up since", $"<t:{Restarts.Unix(s_since)}:R>", true);
                Field(sb, "Next restart", RestartText(), true);
            }
            string footer = online ? "Updates on its own. Only admins can use the buttons."
                : state == State.Starting ? "The world is loading; the buttons come back once players can join." : "The server is down.";
            sb.Append("],\"footer\":{\"text\":").Append(Discord.Quote(footer)).Append("}");
            sb.Append(",\"timestamp\":").Append(Discord.Quote(DateTime.UtcNow.ToString("o"))).Append("}]");
            sb.Append(",\"components\":").Append(online ? Components() : "[]");
            return sb.Append('}').ToString();
        }

        private static string RestartText()
        {
            if (Restarts.Blocked) return "Off (ServerRestart installed)";
            DateTime next = Restarts.Next;
            if (next == default) return "None scheduled";
            string when = $"<t:{Restarts.Unix(next)}:t> (<t:{Restarts.Unix(next)}:R>)";
            return Restarts.ManualPending ? (Restarts.ManualStop ? "⛔ Stop " : "🔄 Restart ") + "from the panel " + when : when;
        }

        private static void Field(StringBuilder sb, string name, string value, bool inline)
        {
            if (sb[sb.Length - 1] != '[') sb.Append(',');
            sb.Append("{\"name\":").Append(Discord.Quote(name)).Append(",\"value\":").Append(Discord.Quote(value)).Append(",\"inline\":").Append(inline ? "true" : "false").Append('}');
        }

        private static string Components()
        {
            List<string> names = Commands.OnlineNames();
            // With Shut Down off the host (AMP) restarts the server on its own schedule, so skipping isn't possible
            string third = Restarts.ManualPending ? Button("cdb:calloff", "Call off restart", 3, "✋")
                : S.RestartShutDown.Value ? Button("cdb:skip", "Skip next restart", 2, "⏭️", Restarts.Next == default)
                : null;
            return "[" +
                   Row(new[] { Button("cdb:restart", "Restart", 4, "🔄"), (Maintenance.On ? Button("cdb:maintoff", "End maintenance", 3, "✅") : Button("cdb:maint", "Maintenance mode", 2, "🛠️")), third }.Where(b => b != null).ToArray()) + "," +
                   Row(Button("cdb:save", "Save world", 1, "💾"), Button("cdb:broadcast", "Broadcast", 1, "📢"), Button("cdb:unban", "Unban", 2, "🔓")) + "," +
                   Row(Select("cdb:kick", "👢 Kick a player…", names)) + "," +
                   Row(Select("cdb:ban", "🔨 Ban a player…", names)) + "]";
        }

        private static string Row(params string[] items) => "{\"type\":1,\"components\":[" + string.Join(",", items) + "]}";

        // style: 1 blue, 2 grey, 3 green, 4 red
        private static string Button(string id, string label, int style, string emoji, bool disabled = false) =>
            "{\"type\":2,\"style\":" + style + ",\"label\":" + Discord.Quote(label) + ",\"custom_id\":" + Discord.Quote(id) +
            (emoji != null ? ",\"emoji\":{\"name\":" + Discord.Quote(emoji) + "}" : "") + (disabled ? ",\"disabled\":true" : "") + "}";

        private static string Select(string id, string placeholder, List<string> names)
        {
            // A list needs at least one entry: with nobody online it shows one, greyed out
            IEnumerable<string> options = names.Count == 0
                ? new[] { "{\"label\":\"Nobody online\",\"value\":\"-\"}" }
                : names.Take(25).Select(n => "{\"label\":" + Discord.Quote(Discord.Truncate(n, 100)) + ",\"value\":" + Discord.Quote(Discord.Truncate(n, 100)) + "}");
            return "{\"type\":3,\"custom_id\":" + Discord.Quote(id) + ",\"placeholder\":" + Discord.Quote(placeholder) +
                   ",\"options\":[" + string.Join(",", options) + "]" + (names.Count == 0 ? ",\"disabled\":true" : "") + "}";
        }

        // ------------------------------------------------------------------ clicks

        private static void Handle(Dictionary<string, object> d)
        {
            string id = d.Str("id"), token = d.Str("token");
            Dictionary<string, object> data = d.Obj("data");
            string customId = data.Str("custom_id") ?? "";
            if (id == null || token == null || !customId.StartsWith("cdb:")) return;

            Dictionary<string, object> user = d.Obj("member")?.Obj("user") ?? d.Obj("user");
            string userId = user.Str("id"), userName = user.Str("username") ?? "someone";
            if (!Commands.IsAdmin(userId))
            {
                Reply(id, token, "⛔ The admin panel is for admins only.");
                return;
            }
            if (ZNet.instance == null || !ZNet.instance.IsServer())
            {
                Reply(id, token, "The world isn't loaded yet.");
                return;
            }
            DiscordBridgePlugin.Log.LogInfo($"[Admin panel] {userName}: {customId}");
            string typed = customId.EndsWith(":modal") ? " \"" + FormValue(data) + "\"" : "";
            string picked = data.TryGetValue("values", out object pv) && pv is List<object> pl && pl.Count > 0 ? " " + pl[0] : "";
            CommandLog.Write("admin panel", $"{userName} ({userId})", customId + picked + typed, null);
            s_appId = d.Str("application_id");

            try { Act(id, token, d, data, customId, userName); }
            catch (Exception ex)
            {
                DiscordBridgePlugin.Log.LogWarning($"Admin panel action '{customId}' failed: {ex}");
                Reply(id, token, "❌ That didn't work: " + ex.Message);
            }
            s_dirty = true;
        }

        private static void Act(string id, string token, Dictionary<string, object> d, Dictionary<string, object> data, string customId, string userName)
        {
            string[] parts = customId.Split(new[] { ':' }, 4);
            string action = parts.Length > 1 ? parts[1] : "";
            string arg = parts.Length > 2 ? parts[2] : null;

            switch (action)
            {
                case "restart":
                case "stop":
                    bool stop = action == "stop";
                    if (arg == null)
                    {
                        string what = stop ? "Stop" : "Restart";
                        Ask(id, token, stop ? "⛔ **Stop the server?** Whether it comes back afterwards depends on your host." : "🔄 **Restart the server?**",
                            Button($"cdb:{action}:0", $"{what} now", 4, null), Button($"cdb:{action}:5", "In 5 minutes", 4, null),
                            Button($"cdb:{action}:15", "In 15 minutes", 2, null), Button("cdb:dismiss", "Never mind", 2, null));
                        return;
                    }
                    Done(id, token, Restarts.RestartIn(int.Parse(arg), stop) + $"\n*by {Discord.EscapeMarkdown(userName)}*");
                    return;
                case "calloff":
                    Reply(id, token, Restarts.CancelManual());
                    return;
                case "skip":
                    Reply(id, token, Restarts.SkipNext());
                    return;
                case "save":
                    Reply(id, token, Commands.Execute("save"));
                    return;
                case "broadcast":
                    if (arg == "modal") { Reply(id, token, Commands.Execute("broadcast", FormValue(data))); return; }
                    Form(id, token, "cdb:broadcast:modal", "Broadcast to everyone's screen", "Message", 2, 200);
                    return;
                case "unban":
                    if (arg == "modal") { Reply(id, token, Commands.Execute("unban", FormValue(data))); return; }
                    Form(id, token, "cdb:unban:modal", "Unban a player", "Player name or Steam ID", 1, 100);
                    return;
                case "kick":
                case "ban":
                    if (arg == "yes")
                    {
                        Done(id, token, Commands.Execute(action, parts.Length > 3 ? parts[3] : ""));
                        return;
                    }
                    string who = (data.TryGetValue("values", out object v) && v is List<object> vals && vals.Count > 0 ? vals[0] as string : null) ?? "-";
                    if (who == "-") { Reply(id, token, "Nobody to pick."); return; }
                    Ask(id, token, $"{(action == "ban" ? "🔨 **Ban" : "👢 **Kick")} {Discord.EscapeMarkdown(who)}?**",
                        Button($"cdb:{action}:yes:{who}", action == "ban" ? "Ban" : "Kick", 4, null), Button("cdb:dismiss", "Never mind", 2, null));
                    return;
                case "maint":
                    if (arg == "on") { Done(id, token, Maintenance.Set(true, userName)); return; }
                    Ask(id, token, "🛠️ **Put the server in maintenance?** The world is saved, everyone but admins is sent out, and only admins can join until you end it. The server keeps running.",
                        Button("cdb:maint:on", "Maintenance mode", 4, null), Button("cdb:dismiss", "Never mind", 2, null));
                    return;
                case "maintoff":
                    Reply(id, token, Maintenance.Set(false, userName));
                    return;
                case "dismiss":
                    Done(id, token, "Cancelled.");
                    return;
            }
        }

        // The text typed into a form's only box
        private static string FormValue(Dictionary<string, object> data)
        {
            if (data.TryGetValue("components", out object rows) && rows is List<object> rl && rl.Count > 0 &&
                rl[0] is Dictionary<string, object> row && row.TryGetValue("components", out object inputs) &&
                inputs is List<object> il && il.Count > 0 && il[0] is Dictionary<string, object> input)
                return (input.Str("value") ?? "").Trim();
            return "";
        }

        // ------------------------------------------------------------------ replies (only the clicking admin sees them)

        private static string s_appId;   // the bot's application, from the click being handled

        private static void Reply(string id, string token, string text)
        {
            Discord.InteractionReply(id, token, "{\"type\":4,\"data\":{\"flags\":64,\"allowed_mentions\":{\"parse\":[]},\"content\":" + Discord.Quote(Discord.Truncate(text ?? "Done.", 1900)) + "}}");
            CommandLog.CleanInteraction(s_appId, token);
        }

        private static void Ask(string id, string token, string text, params string[] buttons) =>
            Discord.InteractionReply(id, token, "{\"type\":4,\"data\":{\"flags\":64,\"allowed_mentions\":{\"parse\":[]},\"content\":" + Discord.Quote(text) + ",\"components\":[" + Row(buttons) + "]}}");

        // Replaces a confirm question with its outcome (its buttons go away)
        private static void Done(string id, string token, string text)
        {
            Discord.InteractionReply(id, token, "{\"type\":7,\"data\":{\"allowed_mentions\":{\"parse\":[]},\"content\":" + Discord.Quote(Discord.Truncate(text ?? "Done.", 1900)) + ",\"components\":[]}}");
            CommandLog.CleanInteraction(s_appId, token);
        }

        // style: 1 one line, 2 paragraph
        private static void Form(string id, string token, string formId, string title, string label, int style, int max) =>
            Discord.InteractionReply(id, token, "{\"type\":9,\"data\":{\"custom_id\":" + Discord.Quote(formId) + ",\"title\":" + Discord.Quote(title) +
                ",\"components\":[{\"type\":1,\"components\":[{\"type\":4,\"custom_id\":\"value\",\"label\":" + Discord.Quote(label) +
                ",\"style\":" + style + ",\"min_length\":1,\"max_length\":" + max + ",\"required\":true}]}]}}");
    }
}
