using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using BepInEx;
using Mono.Cecil;

namespace CaveheimDiscordBridge.Preloader
{
    /// <summary>
    /// Sends "Server is starting up" the moment BepInEx starts on a dedicated server, before a single mod loads
    /// (the plugin itself only loads after most of the pack), and switches the admin panel to "starting up" at the same
    /// time. Patches nothing. Reads the plugin's server settings file directly, and marks the AppDomain so the plugin
    /// doesn't do either a second time.
    /// </summary>
    public static class EarlyLaunchNotice
    {
        public const string SentFlag = "CavemansDiscordBridge.LaunchSent";
        public const string PanelFlag = "CavemansDiscordBridge.PanelStartingSent";

        public static IEnumerable<string> TargetDLLs { get; } = new string[0];

        public static void Patch(AssemblyDefinition assembly) { }

        public static void Initialize()
        {
            try
            {
                if (!IsDedicatedServer()) return;
                string cfg = Path.Combine(Paths.ConfigPath, "TheCaveman.CavemansDiscordBridge.Server.cfg");
                if (!File.Exists(cfg)) return;

                Dictionary<string, string> notifications = ReadSection(cfg, "3 - Notifications");
                if (notifications.TryGetValue("Is Server", out string isServer) && !isServer.Equals("true", StringComparison.OrdinalIgnoreCase)) return;

                StartingPanel(cfg);

                if (!notifications.TryGetValue("Webhook URL", out string url) || string.IsNullOrWhiteSpace(url)) return;
                if (notifications.TryGetValue("Server Launching", out string on) && !on.Equals("true", StringComparison.OrdinalIgnoreCase)) return;

                AppDomain.CurrentDomain.SetData(SentFlag, true);
                const string json = "{\"allowed_mentions\":{\"parse\":[]},\"content\":\"🟡 **Server is starting up…**\"}";
                // Off this thread: BepInEx carries on loading while the post goes out
                Task.Run(() =>
                {
                    try
                    {
                        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
                            http.PostAsync(url.Trim(), new StringContent(json, Encoding.UTF8, "application/json")).GetAwaiter().GetResult().Dispose();
                    }
                    catch (Exception ex) { Console.WriteLine($"[Caveman's Discord Bridge] Early launch notice failed: {ex.Message}"); }
                });
            }
            catch (Exception ex) { Console.WriteLine($"[Caveman's Discord Bridge] Early launch notice skipped: {ex.Message}"); }
        }

        /// <summary>
        /// The admin panel shows "starting up" (no buttons) until the plugin, once the world is up, puts it back online.
        /// Same look as the plugin's own: AdminPanel.BuildPanel(State.Starting). The title must stay the same, it's how
        /// the plugin finds its panel.
        /// </summary>
        private static void StartingPanel(string cfg)
        {
            Dictionary<string, string> panel = ReadSection(cfg, "6 - Admin Panel");
            Dictionary<string, string> bot = ReadSection(cfg, "1 - Bot");
            if (panel.TryGetValue("Enabled", out string enabled) && !enabled.Equals("true", StringComparison.OrdinalIgnoreCase)) return;
            if (!panel.TryGetValue("Channel ID", out string channel) || string.IsNullOrWhiteSpace(channel)) return;
            if (!panel.TryGetValue("Message ID", out string message) || string.IsNullOrWhiteSpace(message)) return;
            if (!bot.TryGetValue("Bot Token", out string token) || string.IsNullOrWhiteSpace(token)) return;

            AppDomain.CurrentDomain.SetData(PanelFlag, true);
            string json = "{\"content\":\"\",\"allowed_mentions\":{\"parse\":[]},\"embeds\":[{\"title\":\"🛠️ Server Admin Panel\",\"color\":16705372," +
                          "\"description\":\"🟡 **Starting up…**\",\"fields\":[]," +
                          "\"footer\":{\"text\":\"The world is loading; the buttons come back once players can join.\"}," +
                          "\"timestamp\":\"" + DateTime.UtcNow.ToString("o") + "\"}],\"components\":[]}";
            string url = $"https://discord.com/api/v10/channels/{channel.Trim()}/messages/{message.Trim()}";
            Task.Run(() =>
            {
                try
                {
                    using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
                    using (var request = new HttpRequestMessage(new HttpMethod("PATCH"), url))
                    {
                        request.Headers.TryAddWithoutValidation("Authorization", "Bot " + token.Trim());
                        request.Headers.TryAddWithoutValidation("User-Agent", "DiscordBot (https://github.com/TheCavemanOfficial/CavemansDiscordBridge, preloader)");
                        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                        using (HttpResponseMessage response = http.SendAsync(request).GetAwaiter().GetResult())
                            if (!response.IsSuccessStatusCode) Console.WriteLine($"[Caveman's Discord Bridge] Early panel update refused ({(int)response.StatusCode}).");
                    }
                }
                catch (Exception ex) { Console.WriteLine($"[Caveman's Discord Bridge] Early panel update failed: {ex.Message}"); }
            });
        }

        // The game isn't loaded yet: tell a server from a player by the executable and the launch options
        private static bool IsDedicatedServer()
        {
            string exe = Process.GetCurrentProcess().ProcessName;
            if (exe.IndexOf("valheim_server", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return Environment.GetCommandLineArgs().Any(a => a.Equals("-nographics", StringComparison.OrdinalIgnoreCase));
        }

        private static Dictionary<string, string> ReadSection(string path, string section)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool inside = false;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.StartsWith("[")) { inside = line == "[" + section + "]"; continue; }
                if (!inside || line.StartsWith("#") || line.Length == 0) continue;
                int eq = line.IndexOf('=');
                if (eq > 0) values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return values;
        }
    }
}
