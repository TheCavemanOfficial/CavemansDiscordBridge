using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using UnityEngine;

namespace CaveheimDiscordBridge
{
    /// <summary>
    /// Scheduled restarts: daily UTC times, with warnings at configurable minutes before each one (centre of the screen,
    /// game chat and Discord), optional ServerCharacters maintenance mode shortly before, and optionally the shutdown
    /// itself (save, drop players, quit) for a host that starts the server again. Replaces Tristan's ServerRestart;
    /// its settings are imported once from org.tristan.serverrestart.cfg. Edits to the settings file apply live.
    /// </summary>
    internal static class Restarts
    {
        private const string ServerRestartGuid = "org.tristan.serverrestart";
        private const string ServerCharactersGuid = "org.bepinex.plugins.servercharacters";

        private static ServerSettings S => DiscordBridgePlugin.Server;

        private static DateTime s_next;                                   // UTC; default = nothing scheduled
        private static readonly List<int> s_warnings = new List<int>();   // minutes before s_next still to announce
        private static DateTime s_maintenanceAt;
        private static bool s_maintenanceOn;
        private static float s_nextTick, s_nextLog;
        private static bool s_shuttingDown, s_blocked;
        private static bool s_manual, s_manualStop;   // a restart (or stop) an admin started from the panel
        private static FileSystemWatcher s_watcher;
        private static volatile bool s_reload;

        internal static DateTime Next => s_next;
        internal static bool ManualPending => s_manual;
        internal static bool ManualStop => s_manualStop;
        internal static bool Blocked => s_blocked;

        /// <summary>Called once the server settings exist (server only).</summary>
        internal static void Start(ConfigFile cfg)
        {
            ImportServerRestart(cfg);

            // Both running would announce every warning twice and race to shut down
            if (Chainloader.PluginInfos.ContainsKey(ServerRestartGuid))
            {
                s_blocked = true;
                DiscordBridgePlugin.Log.LogWarning("Tristan's ServerRestart is installed, so the bridge's scheduled restarts stay off. " +
                                                   "Its settings are now in the bridge's .Server.cfg [5 - Restarts]; remove ServerRestart to use them.");
                return;
            }

            try
            {
                s_watcher = new FileSystemWatcher(Path.GetDirectoryName(cfg.ConfigFilePath), Path.GetFileName(cfg.ConfigFilePath))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true,
                };
                s_watcher.Changed += (_, __) => s_reload = true;
            }
            catch (Exception ex) { DiscordBridgePlugin.Log.LogWarning($"Can't watch the settings file for changes: {ex.Message}"); }

            Schedule();
        }

        /// <summary>Works out the next restart and its warnings. Again after every settings change.</summary>
        internal static void Schedule()
        {
            if (s_blocked || S == null || s_manual) return;

            ClearMaintenance();
            s_warnings.Clear();
            s_next = S.RestartsEnabled.Value ? NextTime(S.RestartSchedule.Value, DateTime.UtcNow) : default;
            if (s_next == default)
            {
                DiscordBridgePlugin.Log.LogInfo("No scheduled restarts.");
                return;
            }

            // Warnings already in the past are skipped, so a server started 20 minutes before a restart only gets 10, 5...
            double left = (s_next - DateTime.UtcNow).TotalMinutes;
            s_warnings.AddRange(ParseMinutes(S.RestartWarnings.Value).Where(m => m < left).OrderByDescending(m => m));
            s_maintenanceAt = S.RestartMaintenance.Value ? s_next.AddMinutes(-Math.Max(0, S.RestartMaintenanceMinutes.Value)) : default;
            s_nextLog = 0f;
            DiscordBridgePlugin.Log.LogInfo($"Next restart {s_next:yyyy-MM-dd HH:mm} UTC (in {Span(s_next - DateTime.UtcNow)}).");
        }

        /// <summary>From the plugin's Update: once a second is plenty for minute-sized warnings.</summary>
        internal static void Update()
        {
            // Not on a test or single-player copy ([3 - Notifications] Is Server off): no warnings, and never a quit
            if (s_blocked || S == null || !S.IsServer.Value || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (s_reload)
            {
                s_reload = false;
                S.File.Reload();
                Schedule();
            }

            float now = Time.realtimeSinceStartup;
            if (now < s_nextTick || s_next == default || s_shuttingDown) return;
            s_nextTick = now + 1f;

            DateTime utc = DateTime.UtcNow;
            while (s_warnings.Count > 0 && utc >= s_next.AddMinutes(-s_warnings[0]))
            {
                int minutes = s_warnings[0];
                s_warnings.RemoveAt(0);
                // Only the latest one if several came due at once (the server was busy or asleep)
                if (s_warnings.Count == 0 || utc < s_next.AddMinutes(-s_warnings[0])) Announce(minutes);
            }

            if (s_maintenanceAt != default && !s_maintenanceOn && utc >= s_maintenanceAt) SetMaintenance();

            if (S.RestartLogPeriod.Value > 0 && now >= s_nextLog)
            {
                s_nextLog = now + S.RestartLogPeriod.Value;
                DiscordBridgePlugin.Log.LogInfo($"Next restart {s_next:yyyy-MM-dd HH:mm} UTC, in {Span(s_next - utc)}.");
            }

            if (utc < s_next) return;
            if (S.RestartShutDown.Value || s_manual) DiscordBridgePlugin.Instance.StartCoroutine(ShutDown());
            else Schedule();   // the host restarts the server; look ahead to the one after
        }

        /// <summary>
        /// An admin's restart (or stop) from the panel: always shuts the server down, in that many minutes, with the
        /// configured warnings that still fit before it. Whether a stopped server comes back is up to the host.
        /// </summary>
        internal static string RestartIn(int minutes, bool stop)
        {
            if (s_blocked) return "Restarts are off while Tristan's ServerRestart is installed.";
            if (s_shuttingDown) return "The server is already shutting down.";
            s_manual = true;
            s_manualStop = stop;
            s_next = DateTime.UtcNow.AddMinutes(Math.Max(0, minutes));
            s_warnings.Clear();
            s_warnings.AddRange(ParseMinutes(S.RestartWarnings.Value).Where(m => m < minutes).OrderByDescending(m => m));
            s_maintenanceAt = default;
            s_nextTick = 0f;
            Announce(minutes);
            DiscordBridgePlugin.Log.LogInfo($"Admin panel: server {(stop ? "stop" : "restart")} in {minutes} min.");
            return minutes == 0 ? (stop ? "⛔ Stopping the server now." : "🔄 Restarting the server now.")
                : $"{(stop ? "⛔ Stopping" : "🔄 Restarting")} the server in {TimeText(minutes)}. Players are being warned.";
        }

        /// <summary>Calls off an admin's restart or stop that is still counting down, back to the normal schedule.</summary>
        internal static string CancelManual()
        {
            if (!s_manual) return "There's no restart from the panel to cancel.";
            if (s_shuttingDown) return "Too late: the server is already shutting down.";
            s_manual = s_manualStop = false;
            Announce(-1);
            Schedule();
            return "✅ Called off. Back to the normal schedule.";
        }

        /// <summary>Skips the next scheduled restart: the warnings move to the one after it.</summary>
        internal static string SkipNext()
        {
            if (s_blocked) return "Restarts are off while Tristan's ServerRestart is installed.";
            if (!S.RestartShutDown.Value) return "Your host restarts the server on its own schedule (Shut Down is off), so a restart can't be skipped from here.";
            if (s_manual) return "A restart from the panel is counting down; cancel that first.";
            if (s_next == default) return "No restart is scheduled.";
            DateTime skipped = s_next;
            DateTime after = NextTime(S.RestartSchedule.Value, skipped);
            ClearMaintenance();
            s_next = after;
            s_warnings.Clear();
            double left = (s_next - DateTime.UtcNow).TotalMinutes;
            s_warnings.AddRange(ParseMinutes(S.RestartWarnings.Value).Where(m => m < left).OrderByDescending(m => m));
            s_maintenanceAt = S.RestartMaintenance.Value ? s_next.AddMinutes(-Math.Max(0, S.RestartMaintenanceMinutes.Value)) : default;
            DiscordBridgePlugin.Log.LogInfo($"Admin panel: skipped the {skipped:HH:mm} UTC restart; next {s_next:yyyy-MM-dd HH:mm} UTC.");
            return $"⏭️ Skipped the <t:{Unix(skipped)}:t> restart. Next one <t:{Unix(s_next)}:t> (<t:{Unix(s_next)}:R>).";
        }

        internal static long Unix(DateTime utc) => (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

        private static void Announce(int minutes)
        {
            string text = minutes < 0 ? "The restart has been called off."
                : minutes == 0 ? (s_manualStop ? "Server shutting down now" : "Server restarting now")
                : s_manualStop ? $"Server shutting down in {TimeText(minutes)}"
                : S.RestartMessage.Value.Replace("{time}", TimeText(minutes));
            if (text.Trim().Length == 0) return;
            DiscordBridgePlugin.Log.LogInfo($"Restart warning: {text}");

            if (ZRoutedRpc.instance != null)
            {
                string center = Format(S.RestartCenterFormat.Value, text);
                if (center.Length > 0) ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "ShowMessage", (int)MessageHud.MessageType.Center, center);
                if (S.RestartToChat.Value) ChatBridge.ServerShout(S.RestartChatName.Value, Format(S.RestartChatFormat.Value, text));
            }
            Discord.Webhook(S.RestartWebhook.Value, Format(S.RestartDiscordFormat.Value, text), S.RestartDiscordName.Value);
        }

        private static System.Collections.IEnumerator ShutDown()
        {
            s_shuttingDown = true;
            DiscordBridgePlugin.Log.LogInfo("Scheduled restart: saving, then disconnecting players.");
            ZNet.instance.Save(true, true);
            // Private in the game: tells every client the server is closing, so they leave instead of timing out
            HarmonyLib.AccessTools.Method(typeof(ZNet), "SendDisconnect", Type.EmptyTypes)?.Invoke(ZNet.instance, null);

            // Give everyone a moment to leave cleanly (their characters save on the way out), but not forever
            float until = Time.realtimeSinceStartup + 30f;
            while (ZNet.instance != null && ZNet.instance.GetPeers().Count > 0 && Time.realtimeSinceStartup < until) yield return null;
            yield return new WaitForSecondsRealtime(3f);

            DiscordBridgePlugin.Log.LogInfo("Scheduled restart: shutting down.");
            if (Game.instance != null) HarmonyLib.AccessTools.Method(typeof(Game), "Shutdown", new[] { typeof(bool) })?.Invoke(Game.instance, new object[] { true });
            yield return new WaitForSecondsRealtime(5f);
            Application.Quit();
        }

        // ------------------------------------------------------------------ ServerCharacters maintenance

        // ServerCharacters goes into maintenance (only admins may join) while a file named "maintenance" sits next to it
        internal static string MaintenanceFile()
        {
            return Chainloader.PluginInfos.TryGetValue(ServerCharactersGuid, out PluginInfo info) && !string.IsNullOrEmpty(info.Location)
                ? Path.Combine(Path.GetDirectoryName(info.Location), "maintenance")
                : null;
        }

        private static void SetMaintenance()
        {
            s_maintenanceOn = true;
            string file = MaintenanceFile();
            if (file == null)
            {
                DiscordBridgePlugin.Log.LogWarning("Restart maintenance mode is on, but ServerCharacters isn't installed.");
                return;
            }
            try
            {
                File.WriteAllText(file, "");
                DiscordBridgePlugin.Log.LogInfo("ServerCharacters maintenance mode on until the restart.");
            }
            catch (Exception ex) { DiscordBridgePlugin.Log.LogWarning($"Couldn't turn on maintenance mode: {ex.Message}"); }
        }

        // A fresh start (or a new schedule) lifts the maintenance the last restart left behind
        private static void ClearMaintenance()
        {
            s_maintenanceOn = false;
            // Maintenance an admin turned on from the panel stays until they end it
            if (S == null || !S.RestartMaintenance.Value || Maintenance.On) return;
            string file = MaintenanceFile();
            try
            {
                if (file != null && File.Exists(file))
                {
                    File.Delete(file);
                    DiscordBridgePlugin.Log.LogInfo("ServerCharacters maintenance mode off.");
                }
            }
            catch (Exception ex) { DiscordBridgePlugin.Log.LogWarning($"Couldn't turn off maintenance mode: {ex.Message}"); }
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>The soonest of the comma-separated daily UTC times ("04:00,16:00:00") still ahead of now.</summary>
        internal static DateTime NextTime(string schedule, DateTime nowUtc)
        {
            DateTime best = default;
            foreach (string part in (schedule ?? "").Split(','))
            {
                if (!TimeSpan.TryParse(part.Trim(), CultureInfo.InvariantCulture, out TimeSpan t) || t < TimeSpan.Zero || t >= TimeSpan.FromDays(1))
                {
                    if (part.Trim().Length > 0) DiscordBridgePlugin.Log.LogWarning($"Restart schedule: \"{part.Trim()}\" isn't a time like 04:00.");
                    continue;
                }
                DateTime at = nowUtc.Date + t;
                if (at <= nowUtc) at = at.AddDays(1);
                if (best == default || at < best) best = at;
            }
            return best;
        }

        private static IEnumerable<int> ParseMinutes(string list)
        {
            var seen = new HashSet<int>();
            foreach (string part in (list ?? "").Split(','))
                if (int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int m) && m > 0 && seen.Add(m)) yield return m;
        }

        /// <summary>60 = "1 hour", 90 = "1 hour 30 minutes", 1 = "1 minute".</summary>
        internal static string TimeText(int minutes)
        {
            int h = minutes / 60, m = minutes % 60;
            string hours = h == 0 ? "" : h == 1 ? "1 hour" : $"{h} hours";
            string mins = m == 0 ? "" : m == 1 ? "1 minute" : $"{m} minutes";
            return (hours + " " + mins).Trim();
        }

        internal static string Span(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{Math.Max(0, t.Minutes)}m {Math.Max(0, t.Seconds)}s";

        // "{0}" is the message, like ServerRestart's formats
        private static string Format(string format, string text)
        {
            if (string.IsNullOrEmpty(format)) return "";
            return format.Replace("{0}", text);
        }

        // ------------------------------------------------------------------ import from ServerRestart

        /// <summary>
        /// Copies the restart settings from Tristan's ServerRestart config, once. Its fixed messages ("Server restart in
        /// 30 minutes") become the warning minutes; a message left empty there drops that warning.
        /// </summary>
        private static void ImportServerRestart(ConfigFile cfg)
        {
            if (S.RestartImported.Value) return;
            S.RestartImported.Value = true;

            string path = Path.Combine(Paths.ConfigPath, ServerRestartGuid + ".cfg");
            if (!File.Exists(path)) return;

            Dictionary<string, string> old;
            try { old = ReadCfg(path); }
            catch (Exception ex)
            {
                DiscordBridgePlugin.Log.LogWarning($"Couldn't read {path}: {ex.Message}");
                return;
            }

            void Take(string key, Action<string> set)
            {
                if (old.TryGetValue(key, out string v)) set(v);
            }

            Take("1. Restart/Schedule (utc)", v => S.RestartSchedule.Value = v);
            Take("1. Restart/Shut down", v => S.RestartShutDown.Value = Bool(v, S.RestartShutDown.Value));

            var minutes = new List<int>();
            foreach (var (key, mins) in new[] { ("1 hour", 60), ("30 minutes", 30), ("10 minutes", 10), ("5 minutes", 5),
                                                ("4 minutes", 4), ("3 minutes", 3), ("2 minutes", 2), ("1 minute", 1) })
            {
                if (!old.TryGetValue("2. Messages/" + key, out string msg) || msg.Trim().Length > 0) minutes.Add(mins);
            }
            S.RestartWarnings.Value = string.Join(",", minutes);
            // Their default wording is exactly ours; custom wording keeps its text with the time swapped for {time}
            if (old.TryGetValue("2. Messages/30 minutes", out string sample) && sample.Contains("30 minutes"))
                S.RestartMessage.Value = sample.Replace("30 minutes", "{time}");

            Take("2. Messages/Anounce format", v => S.RestartCenterFormat.Value = v);
            Take("2. Messages/Chat name", v => S.RestartChatName.Value = v);
            Take("2. Messages/Send to chat", v => S.RestartToChat.Value = Bool(v, S.RestartToChat.Value));
            Take("2. Messages/Chat format", v => S.RestartChatFormat.Value = v);
            Take("3. ServerCharacters/Maintenance mode", v => S.RestartMaintenance.Value = Bool(v, S.RestartMaintenance.Value));
            Take("3. ServerCharacters/Maintenance time", v => { if (int.TryParse(v, out int m)) S.RestartMaintenanceMinutes.Value = m; });
            Take("4. Discord/Webhook", v => S.RestartWebhook.Value = v);
            Take("4. Discord/Display name", v => S.RestartDiscordName.Value = v);
            Take("4. Discord/Discord format", v => S.RestartDiscordFormat.Value = v);
            Take("5. Logs/Print logs", v => { if (!Bool(v, true)) S.RestartLogPeriod.Value = 0; });
            Take("5. Logs/Period", v => { if (S.RestartLogPeriod.Value > 0 && int.TryParse(v, out int p)) S.RestartLogPeriod.Value = p; });

            cfg.Save();
            DiscordBridgePlugin.Log.LogInfo($"Imported the restart settings from {Path.GetFileName(path)} into [5 - Restarts].");
        }

        private static bool Bool(string v, bool fallback) => bool.TryParse(v.Trim(), out bool b) ? b : fallback;

        /// <summary>A BepInEx .cfg as "Section/Key" -> value, read without loading it (that would rewrite the file).</summary>
        private static Dictionary<string, string> ReadCfg(string path)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string section = "";
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    section = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq > 0) values[section + "/" + line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return values;
        }
    }
}
