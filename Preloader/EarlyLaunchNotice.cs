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
    /// (the plugin itself only loads after most of the pack). Patches nothing. Reads the plugin's server settings
    /// file directly, and marks the AppDomain so the plugin doesn't send the message a second time.
    /// </summary>
    public static class EarlyLaunchNotice
    {
        public const string SentFlag = "CavemansDiscordBridge.LaunchSent";

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
                if (!notifications.TryGetValue("Webhook URL", out string url) || string.IsNullOrWhiteSpace(url)) return;
                if (notifications.TryGetValue("Server Launching", out string on) && !on.Equals("true", StringComparison.OrdinalIgnoreCase)) return;
                if (notifications.TryGetValue("Is Server", out string isServer) && !isServer.Equals("true", StringComparison.OrdinalIgnoreCase)) return;

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
