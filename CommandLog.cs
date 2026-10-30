using System;
using System.IO;
using System.Threading.Tasks;
using BepInEx;

namespace CaveheimDiscordBridge
{
    /// <summary>
    /// A plain text record of every command, wherever it was used (command channel, admin panel, game chat, the Discord
    /// chat channel), kept on the server because the commands themselves are cleaned out of Discord. Also the cleanup:
    /// deleting command messages and answers after a while.
    /// </summary>
    internal static class CommandLog
    {
        private static readonly object s_lock = new object();

        // Cleanups still waiting out their delay; run straight away if the server shuts down first
        private class Cleanup { public string Channel, CommandId, ReplyId, AppId, Token; public int Done; }
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Cleanup, bool> s_cleanups =
            new System.Collections.Concurrent.ConcurrentDictionary<Cleanup, bool>();
        private static bool s_warnedPermission;

        private static ServerSettings S => DiscordBridgePlugin.Server;

        /// <summary>One line: when (UTC), where, who, what they ran, and the answer.</summary>
        internal static void Write(string source, string who, string command, string result)
        {
            string file = S?.CommandLogFile.Value?.Trim();
            if (string.IsNullOrEmpty(file)) return;
            string path = Path.IsPathRooted(file) ? file : Path.Combine(Paths.BepInExRootPath, file);
            string line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC  [{source}]  {who}: {command}" +
                          (string.IsNullOrEmpty(result) ? "" : "  ->  " + OneLine(result)) + Environment.NewLine;
            try
            {
                lock (s_lock) File.AppendAllText(path, line);
            }
            catch (Exception ex) { DiscordBridgePlugin.Log.LogWarning($"Couldn't write the command log: {ex.Message}"); }
        }

        private static string OneLine(string s) => Discord.Truncate(s.Replace("\r", "").Replace("\n", " / "), 300);

        /// <summary>
        /// Posts a command's answer in a channel, then after the configured seconds deletes the command message and the
        /// answer (in that order). Pinned messages are left alone. 0 seconds keeps both.
        /// </summary>
        internal static void ReplyThenClean(string channelId, string commandMessageId, string text, bool keep)
        {
            int seconds = keep ? 0 : S.CommandDeleteSeconds.Value;
            if (seconds <= 0 || string.IsNullOrEmpty(commandMessageId))
            {
                Discord.BotMessage(channelId, text);
                return;
            }
            string json = Discord.MessageJson(text, null);
            var c = new Cleanup { Channel = channelId.Trim(), CommandId = commandMessageId };
            s_cleanups[c] = true;
            Task.Run(async () =>
            {
                try
                {
                    int code = Discord.BotCall("POST", $"/channels/{c.Channel}/messages", json, out string body);
                    if (code >= 200 && code < 300 && Json.Parse(body) is System.Collections.Generic.Dictionary<string, object> m) c.ReplyId = m.Str("id");
                    else DiscordBridgePlugin.Log.LogWarning($"Couldn't answer a command ({code}): {Discord.Truncate(body, 200)}");

                    await Task.Delay(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
                    Run(c, urgent: false);
                }
                catch (Exception ex) { DiscordBridgePlugin.Log.LogWarning($"Command cleanup failed: {ex.Message}"); }
            });
        }

        /// <summary>The server is going down: does every waiting cleanup now, on this thread, instead of losing them.</summary>
        internal static void FlushNow()
        {
            foreach (Cleanup c in s_cleanups.Keys)
            {
                try { Run(c, urgent: true); }
                catch (Exception ex) { DiscordBridgePlugin.Log.LogWarning($"Cleanup at shutdown failed: {ex.Message}"); }
            }
        }

        private static void Run(Cleanup c, bool urgent)
        {
            if (System.Threading.Interlocked.Exchange(ref c.Done, 1) == 1) return;
            s_cleanups.TryRemove(c, out _);
            if (c.Token != null)
            {
                Discord.BotCall("DELETE", $"/webhooks/{c.AppId}/{c.Token}/messages/@original", null, out _, urgent);
                return;
            }
            DeleteUnlessPinned(c.Channel, c.CommandId, urgent);
            if (c.ReplyId != null) DeleteUnlessPinned(c.Channel, c.ReplyId, urgent);
        }

        private static void DeleteUnlessPinned(string channel, string messageId, bool urgent)
        {
            if (Discord.BotCall("GET", $"/channels/{channel}/messages/{messageId}", null, out string body, urgent) != 200) return;   // already gone
            if (Json.Parse(body) is System.Collections.Generic.Dictionary<string, object> m && m.Bool("pinned")) return;
            int code = Discord.BotCall("DELETE", $"/channels/{channel}/messages/{messageId}", null, out string err, urgent);
            if (code == 403 && !s_warnedPermission)
            {
                s_warnedPermission = true;
                DiscordBridgePlugin.Log.LogWarning("Can't delete people's command messages: give the bot Manage Messages in the command channel. (Its own answers still get cleaned up.)");
            }
            else if (code >= 300 && code != 403 && code != 404) DiscordBridgePlugin.Log.LogWarning($"Couldn't delete a command message ({code}): {Discord.Truncate(err, 200)}");
        }

        /// <summary>Removes the private answer to a panel click after the configured seconds (Discord allows up to 15 minutes).</summary>
        internal static void CleanInteraction(string applicationId, string token)
        {
            int seconds = S.CommandDeleteSeconds.Value;
            if (seconds <= 0 || string.IsNullOrEmpty(applicationId) || string.IsNullOrEmpty(token)) return;
            seconds = Math.Min(seconds, 14 * 60);
            var c = new Cleanup { AppId = applicationId, Token = token };
            s_cleanups[c] = true;
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
                    Run(c, urgent: false);
                }
                catch (Exception ex) { DiscordBridgePlugin.Log.LogWarning($"Panel reply cleanup failed: {ex.Message}"); }
            });
        }
    }
}
