using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace CaveheimDiscordBridge
{
    /// <summary>
    /// "Why can't I get in?" reports. When someone connects but never makes it into the world (wrong version or
    /// password, banned, full, kicked by AzuAntiCheat for their mods, or gave up), one report goes to the player-error
    /// webhook: the game's own refusal reason, AzuAntiCheat's log lines about them, and - when they have the client
    /// part - their mod list with file fingerprints, game version, log error counts and the mods behind them, compared
    /// with the server's mods. Players with the client part send that snapshot the moment they connect, before any
    /// check can drop them. Logins that work post nothing. AzuAntiCheat is only read from the log, never hooked.
    /// </summary>
    internal static class JoinReports
    {
        private const int SnapshotVersion = 1;
        internal const string AzuAntiCheatGuid = "Azumatt.AzuAntiCheat";

        /// <summary>AzuAntiCheat is optional: without it, nothing here listens for it or mentions it.</summary>
        internal static bool HasAzuAntiCheat => Chainloader.PluginInfos.ContainsKey(AzuAntiCheatGuid);
        private const int MaxErrorText = 48 * 1024;

        private static ServerSettings S => DiscordBridgePlugin.Server;

        // ================================================================ shared: mods with fingerprints

        internal class Mod { public string Guid, Name, Version, File, Hash; }

        private static List<Mod> s_localMods;
        private static Task<List<Mod>> s_localModsTask;

        /// <summary>This game's mods. Fingerprinting ~100 DLLs takes a moment, so it starts early on a background thread.</summary>
        internal static void WarmUp()
        {
            if (s_localModsTask != null) return;
            List<PluginInfo> plugins = Chainloader.PluginInfos.Values.ToList();
            s_localModsTask = Task.Run(() =>
            {
                var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var list = new List<Mod>();
                foreach (PluginInfo p in plugins)
                {
                    string file = p.Location ?? "";
                    if (file.Length > 0 && !hashes.ContainsKey(file)) hashes[file] = Fingerprint(file);
                    list.Add(new Mod
                    {
                        Guid = p.Metadata.GUID, Name = p.Metadata.Name, Version = p.Metadata.Version.ToString(),
                        File = Path.GetFileName(file), Hash = file.Length > 0 ? hashes[file] : "",
                    });
                }
                // Libraries (Newtonsoft.Json, YamlDotNet, ...) have no plugin entry but are part of the install, and
                // AzuAntiCheat's lists include them
                try
                {
                    foreach (string file in Directory.GetFiles(Paths.PluginPath, "*.dll", SearchOption.AllDirectories))
                    {
                        if (hashes.ContainsKey(file)) continue;
                        list.Add(new Mod
                        {
                            Guid = "file:" + Path.GetFileName(file).ToLowerInvariant(), Name = Path.GetFileNameWithoutExtension(file),
                            Version = "", File = Path.GetFileName(file), Hash = Fingerprint(file),
                        });
                    }
                }
                catch (Exception) { }
                return list.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
            });
        }

        // Every mod has loaded by the main menu; fingerprint them then, before anyone connects anywhere
        [HarmonyPatch(typeof(FejdStartup), "Start")]
        private static class AtMainMenu
        {
            private static void Postfix() => WarmUp();
        }

        private static List<Mod> LocalMods()
        {
            if (s_localMods != null) return s_localMods;
            WarmUp();
            try { return s_localMods = s_localModsTask.GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                DiscordBridgePlugin.Log.LogWarning($"Couldn't list the mods: {ex.Message}");
                return s_localMods = new List<Mod>();
            }
        }

        private static string Fingerprint(string path)
        {
            try
            {
                using (var sha = SHA256.Create())
                using (FileStream f = File.OpenRead(path))
                    return BitConverter.ToString(sha.ComputeHash(f), 0, 6).Replace("-", "").ToLowerInvariant();
            }
            catch { return "?"; }
        }

        private static byte[] Zip(string text)
        {
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal))
                {
                    byte[] b = Encoding.UTF8.GetBytes(text ?? "");
                    gz.Write(b, 0, b.Length);
                }
                return ms.ToArray();
            }
        }

        private static string Unzip(byte[] data)
        {
            if (data == null || data.Length == 0) return "";
            using (var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress))
            using (var r = new StreamReader(gz, Encoding.UTF8))
                return r.ReadToEnd();
        }

        // ================================================================ connection hook (both sides)

        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        private static class NewConnection
        {
            private static void Postfix(ZNet __instance, ZNetPeer peer)
            {
                if (peer?.m_rpc == null) return;
                try
                {
                    if (__instance.IsServer()) ServerConnected(peer);
                    else peer.m_rpc.Register("CDB_WantSnapshot", new ZRpc.RpcMethod.Method(ClientSnapshotWanted));
                }
                catch (Exception ex) { DiscordBridgePlugin.Log.LogWarning($"Join report setup failed: {ex.Message}"); }
            }
        }

        // ================================================================ client

        /// <summary>The server (running the bridge) asks; the answer is built off the main thread and sent back.</summary>
        private static void ClientSnapshotWanted(ZRpc rpc)
        {
            if (!DiscordBridgePlugin.SendJoinSnapshot.Value) return;
            string player = Game.instance?.GetPlayerProfile()?.GetName() ?? "";
            string game = Version.GetVersionString();
            Task.Run(() =>
            {
                ZPackage pkg;
                try { pkg = BuildSnapshot(player, game); }
                catch (Exception ex) { DiscordBridgePlugin.Log.LogWarning($"Couldn't build the join snapshot: {ex.Message}"); return; }
                DiscordBridgePlugin.RunOnMainThread(() =>
                {
                    if (rpc != null && rpc.IsConnected()) rpc.Invoke("CDB_Snapshot", pkg);
                });
            });
        }

        private static ZPackage BuildSnapshot(string player, string game)
        {
            var pkg = new ZPackage();
            pkg.Write(SnapshotVersion);
            pkg.Write(player);
            pkg.Write(game);
            pkg.Write(typeof(Chainloader).Assembly.GetName().Version.ToString());
            List<Mod> mods = LocalMods();
            pkg.Write(mods.Count);
            foreach (Mod m in mods)
            {
                pkg.Write(m.Guid); pkg.Write(m.Name); pkg.Write(m.Version); pkg.Write(m.File); pkg.Write(m.Hash);
            }

            LogCounts log = ReadOwnLog();
            pkg.Write(log.Errors);
            pkg.Write(log.Warnings);
            pkg.Write(string.Join("\n", log.BySource.OrderByDescending(kv => kv.Value.errors).ThenByDescending(kv => kv.Value.warnings)
                .Take(30).Select(kv => $"{kv.Key}|{kv.Value.errors}|{kv.Value.warnings}")));
            pkg.Write(Zip(log.ErrorText));
            return pkg;
        }

        private class LogCounts
        {
            public int Errors, Warnings;
            public readonly Dictionary<string, (int errors, int warnings)> BySource = new Dictionary<string, (int, int)>();
            public string ErrorText = "";
        }

        /// <summary>This session's BepInEx log: error and warning counts per mod, and the latest error lines.</summary>
        private static LogCounts ReadOwnLog()
        {
            var result = new LogCounts();
            string path = Path.Combine(Paths.BepInExRootPath, "LogOutput.log");
            if (!File.Exists(path)) return result;

            var recent = new LinkedList<string>();
            int recentSize = 0;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fs))
            {
                string line;
                bool inError = false;
                int extra = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    bool error = line.StartsWith("[Error") || line.StartsWith("[Fatal");
                    bool warning = line.StartsWith("[Warning");
                    if (error || warning)
                    {
                        string source = SourceOf(line);
                        result.BySource.TryGetValue(source, out var c);
                        if (error) { result.Errors++; c.errors++; } else { result.Warnings++; c.warnings++; }
                        result.BySource[source] = c;
                    }
                    // Error lines plus the first few lines under them (the start of the stack trace)
                    if (error) { inError = true; extra = 4; }
                    else if (line.StartsWith("[")) inError = false;
                    else if (inError && extra-- <= 0) continue;
                    if (!inError) continue;

                    recent.AddLast(line);
                    recentSize += line.Length + 1;
                    while (recentSize > MaxErrorText && recent.Count > 0) { recentSize -= recent.First.Value.Length + 1; recent.RemoveFirst(); }
                }
            }
            result.ErrorText = string.Join("\n", recent);
            return result;
        }

        // "[Error  : Some Mod] text" -> "Some Mod"
        private static string SourceOf(string line)
        {
            int colon = line.IndexOf(':'), close = line.IndexOf(']');
            return colon > 0 && close > colon ? line.Substring(colon + 1, close - colon - 1).Trim() : "?";
        }

        // ================================================================ server

        private class Pending
        {
            public ZNetPeer Peer;
            public DateTime Connected = DateTime.UtcNow;
            public string Host = "";
            public int Status = -1;   // ZNet.ConnectionStatus the server refused with
            public Snapshot Snap;
            public bool SnapshotAsked;
        }

        private class Snapshot
        {
            public string Player, Game, BepInEx, Sources, ErrorText;
            public int Errors, Warnings;
            public List<Mod> Mods = new List<Mod>();
        }

        private static readonly Dictionary<ZRpc, Pending> s_pending = new Dictionary<ZRpc, Pending>();
        private static readonly List<(float at, Pending p)> s_toPost = new List<(float, Pending)>();

        private static void ServerConnected(ZNetPeer peer)
        {
            if (S == null || (string.IsNullOrWhiteSpace(S.JoinReportChannelId.Value) && string.IsNullOrWhiteSpace(S.JoinReportWebhook.Value))) return;
            if (HasAzuAntiCheat) AzuLog.Start();
            WarmUp();
            var p = new Pending { Peer = peer };
            try { p.Host = peer.m_socket?.GetHostName() ?? ""; } catch { }
            s_pending[peer.m_rpc] = p;
            peer.m_rpc.Register<ZPackage>("CDB_Snapshot", (rpc, pkg) => ReceiveSnapshot(p, pkg));
            // Clients without the bridge (or an old one) just don't answer
            peer.m_rpc.Invoke("CDB_WantSnapshot");
            p.SnapshotAsked = true;
        }

        private static void ReceiveSnapshot(Pending p, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != SnapshotVersion) return;
                var s = new Snapshot { Player = pkg.ReadString(), Game = pkg.ReadString(), BepInEx = pkg.ReadString() };
                int count = Mathf.Min(pkg.ReadInt(), 2000);
                for (int i = 0; i < count; i++)
                    s.Mods.Add(new Mod { Guid = pkg.ReadString(), Name = pkg.ReadString(), Version = pkg.ReadString(), File = pkg.ReadString(), Hash = pkg.ReadString() });
                s.Errors = pkg.ReadInt();
                s.Warnings = pkg.ReadInt();
                s.Sources = pkg.ReadString();
                s.ErrorText = Unzip(pkg.ReadByteArray());
                p.Snap = s;
            }
            catch (Exception ex) { DiscordBridgePlugin.Log.LogWarning($"A join snapshot couldn't be read: {ex.Message}"); }
        }

        // The server's refusals go out as rpc.Invoke("Error", status) - note which one for the report
        [HarmonyPatch(typeof(ZRpc), nameof(ZRpc.Invoke), typeof(string), typeof(object[]))]
        private static class RefusalSeen
        {
            private static void Prefix(ZRpc __instance, string method, object[] parameters)
            {
                if (method != "Error" || s_pending.Count == 0 || parameters == null || parameters.Length == 0) return;
                if (s_pending.TryGetValue(__instance, out Pending p) && parameters[0] is int status) p.Status = status;
            }
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect))]
        private static class Left
        {
            private static void Prefix(ZNet __instance, ZNetPeer peer)
            {
                if (peer?.m_rpc == null || !__instance.IsServer() || !s_pending.TryGetValue(peer.m_rpc, out Pending p)) return;
                s_pending.Remove(peer.m_rpc);
                if (!peer.m_characterID.IsNone()) return;   // they got in: nothing to report
                // A moment for AzuAntiCheat's last lines about them to reach the log
                s_toPost.Add((Time.realtimeSinceStartup + 3f, p));
            }
        }

        // Players who did get in are forgotten once they spawn
        [HarmonyPatch(typeof(ZNet), "RPC_CharacterID")]
        private static class GotIn
        {
            private static void Postfix(ZNet __instance, ZRpc rpc)
            {
                if (__instance.IsServer() && s_pending.TryGetValue(rpc, out Pending p) && !p.Peer.m_characterID.IsNone()) s_pending.Remove(rpc);
            }
        }

        internal static void Update()
        {
            if (s_toPost.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            for (int i = s_toPost.Count - 1; i >= 0; i--)
            {
                if (now < s_toPost[i].at) continue;
                Pending p = s_toPost[i].p;
                s_toPost.RemoveAt(i);
                try { Post(p); }
                catch (Exception ex) { DiscordBridgePlugin.Log.LogWarning($"Join report failed: {ex}"); }
            }
        }

        // ================================================================ the report

        private static readonly string[] StatusText =
        {
            "", "", "", "Wrong game version", "Disconnected", "Couldn't connect", "Wrong password", "Already connected",
            "Banned, or not on the allowed list", "Server full", "Their platform isn't allowed", "Crossplay not allowed for them",
        };

        private static void Post(Pending p)
        {
            Snapshot s = p.Snap;
            string name = !string.IsNullOrEmpty(p.Peer.m_playerName) ? p.Peer.m_playerName : s?.Player;
            if (string.IsNullOrEmpty(name)) name = "Unknown player";
            string steam = SteamAvatars.FromPlatformId(p.Host);
            if (p.Status == 6 && !S.JoinReportWrongPassword.Value) return;
            List<string> azu = HasAzuAntiCheat ? AzuLog.About(p.Connected.AddSeconds(-2), DateTime.UtcNow, p.Host, steam, name) : new List<string>();

            List<Mod> server = LocalMods();
            Diff diff = s != null ? Compare(s.Mods, server) : null;
            AzuVerdict verdict = s != null && HasAzuAntiCheat ? AzuLists.Check(s.Mods) : null;
            bool sameGame = s != null && SameVersion(s.Game, Version.GetVersionString());

            // AzuAntiCheat refuses players with the game's "wrong version" code, so with matching game versions that
            // code is really its kick
            bool azuKick = HasAzuAntiCheat && (azu.Any(l => l.IndexOf("kick", StringComparison.OrdinalIgnoreCase) >= 0) || (p.Status == 3 && sameGame));
            AzuKick kick = AzuKick.From(azu);
            string reason = azuKick ? "Kicked by AzuAntiCheat" + (kick != null ? ": " + kick.Headline
                    : verdict != null && verdict.Problems > 0 ? $": {verdict.Problems} mod problem(s), see below" : "")
                : p.Status >= 3 && p.Status < StatusText.Length ? StatusText[p.Status]
                : azu.Count > 0 ? "Dropped during AzuAntiCheat's checks"
                : "Left before getting into the world";
            TimeSpan connected = DateTime.UtcNow - p.Connected;

            var fields = new List<(string, string, bool)>
            {
                ("👤 Player", Discord.EscapeMarkdown(name), true),
                ("🆔 ID", steam != null ? $"[{steam}](https://steamcommunity.com/profiles/{steam})" : (p.Host.Length > 0 ? p.Host : "unknown"), true),
                ("⏱️ Connected for", connected.TotalSeconds < 90 ? $"{connected.TotalSeconds:0} s" : $"{connected.TotalMinutes:0.#} min", true),
                ("❓ Why", reason, false),
            };
            // What AzuAntiCheat told the player, one clean line per mod: the quickest way to the fix
            if (kick != null && kick.Items.Count > 0) fields.Add(("🔧 What to fix", Cap(kick.Items, 15), false));
            if (s != null)
            {
                string gameLine = sameGame ? s.Game : $"{s.Game} (server {Version.GetVersionString()}) ⚠️";
                fields.Add(("🎮 Game / BepInEx", $"{gameLine} · BepInEx {s.BepInEx}", false));
                fields.Add(("🧩 Mods", $"theirs {s.Mods.Count} · server {server.Count}", true));
                fields.Add(("🔴 Errors / ⚠️ Warnings", $"{s.Errors} / {s.Warnings}", true));
                string problems = ProblemMods(s.Sources);
                if (problems.Length > 0) fields.Add(("🚨 Mods logging problems", problems, false));
                if (verdict != null)
                {
                    // AzuAntiCheat's own lists decide what's wrong; the full comparison stays in the attached file
                    fields.Add(("❌ To fix (AzuAntiCheat's lists)", verdict.Lines.Count == 0 ? "✅ Nothing: every required mod is there, nothing extra, files match" : Cap(verdict.Lines, 15), false));
                    List<string> versions = diff.Lines.Where(l => l.StartsWith("🔁")).ToList();
                    if (versions.Count > 0) fields.Add(("🔁 Other versions than the server", Cap(versions, 8), false));
                }
                else
                {
                    fields.Add(("🔄 Compared with the server", diff.Summary(), false));
                    if (diff.Lines.Count > 0) fields.Add(("🔧 Differences", Cap(diff.Lines, 12), false));
                }
            }
            else
            {
                fields.Add(("🧩 Their mods", "No details from their game: that needs Discord Bridge 1.1.0 or newer on their side.", false));
            }

            var files = new List<(string, string)>();
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            string tag = (steam ?? "player") + "_" + stamp;
            if (s != null)
            {
                files.Add(($"mods_{tag}.txt", ModListText(name, s)));
                files.Add(($"compare_{tag}.txt", (verdict != null ? verdict.Report + "\n\n" : "") + diff.Report));
                if (s.ErrorText.Length > 0) files.Add(($"errors_{tag}.txt", s.ErrorText));
            }
            if (azu.Count > 0) files.Add(($"azuanticheat_{tag}.txt", string.Join("\n", azu.Select(AzuKick.Clean))));

            string json = EmbedJson($"🚫 Couldn't join: {name}", 0xED4245, fields);
            if (!string.IsNullOrWhiteSpace(S.JoinReportChannelId.Value)) Discord.BotWithFiles(S.JoinReportChannelId.Value, json, files);
            else Discord.WebhookWithFiles(S.JoinReportWebhook.Value, json, files);
            CommandLog.Write("join report", name + (steam != null ? $" ({steam})" : ""), reason, null);
            DiscordBridgePlugin.Log.LogInfo($"Join report posted for {name}: {reason}");
        }

        /// <summary>
        /// AzuAntiCheat's kick, read from its log: "Kicking &lt;id&gt; (Compliance): &lt;headline&gt;" followed by one line per mod
        /// ("Name  you have 1.10.5, server runs 1.10.6"). The same message is logged up to three times, with terminal
        /// colour codes in some copies; this keeps one clean copy.
        /// </summary>
        private class AzuKick
        {
            public string Headline;
            public readonly List<string> Items = new List<string>();

            private static readonly System.Text.RegularExpressions.Regex Ansi = new System.Text.RegularExpressions.Regex(@"\x1b?\[[0-9;]*m");
            private static readonly System.Text.RegularExpressions.Regex Version =
                new System.Text.RegularExpressions.Regex(@"^(.+?)\s+you have (.+?), server runs (.+?)$");

            internal static string Clean(string s) => Ansi.Replace(s ?? "", "").Replace("\r", "");

            internal static AzuKick From(List<string> lines)
            {
                foreach (string raw in lines)
                {
                    string entry = Clean(raw);
                    int at = entry.IndexOf("Kicking ", StringComparison.Ordinal);
                    int colon = at >= 0 ? entry.IndexOf("): ", at, StringComparison.Ordinal) : -1;
                    if (colon < 0) continue;

                    string[] rows = entry.Substring(colon + 3).Split('\n');
                    var kick = new AzuKick { Headline = Tidy(rows[0]) };
                    // "Client Missing Mods: Missing Mod(s):" is a wrapper around the real headline on the next row
                    int first = 1;
                    if (kick.Headline.EndsWith(":") && rows.Length > 1) { kick.Headline = Tidy(rows[1]); first = 2; }
                    for (int i = first; i < rows.Length; i++)
                    {
                        string row = Tidy(rows[i]);
                        if (row.Length == 0) continue;
                        var m = Version.Match(row);
                        string item = m.Success
                            ? $"• **{Discord.EscapeMarkdown(m.Groups[1].Value.Trim())}**: has {m.Groups[2].Value.Trim()}, server runs {m.Groups[3].Value.Trim()}"
                            : "• " + Discord.EscapeMarkdown(row);
                        if (!kick.Items.Contains(item)) kick.Items.Add(item);
                    }
                    if (kick.Headline.Length == 0) kick.Headline = "see the attached file";
                    return kick;
                }
                return null;
            }

            // Azu ends the message with what happens to the character; not part of the fix
            private static string Tidy(string s)
            {
                s = (s ?? "").Trim();
                foreach (string tail in new[] { "Character will be saved first.", "Character will not be saved." })
                    if (s.EndsWith(tail, StringComparison.Ordinal)) s = s.Substring(0, s.Length - tail.Length).Trim();
                return s;
            }
        }

        // The dedicated server calls its version "l-1.0.21", the game "1.0.21"
        private static bool SameVersion(string a, string b)
        {
            string Clean(string v)
            {
                v = (v ?? "").Trim();
                int dash = v.IndexOf('-');
                return dash > 0 && dash < 4 && char.IsLetter(v[0]) ? v.Substring(dash + 1) : v;
            }
            return Clean(a) == Clean(b);
        }

        private class AzuVerdict
        {
            public readonly List<string> Lines = new List<string>();
            public int Problems;
            public string Report;
        }

        /// <summary>
        /// The player's mods checked the way AzuAntiCheat checks them, against its Whitelist (every player needs them)
        /// and Greylist (allowed) folders on the server: missing required DLLs, DLLs on neither list, and DLLs whose
        /// file differs from the list's copy. Read only; the folders are AzuAntiCheat's.
        /// </summary>
        private static class AzuLists
        {
            internal static AzuVerdict Check(List<Mod> theirs)
            {
                var v = new AzuVerdict();
                string root = Path.Combine(Paths.ConfigPath, "AzuAntiCheat");
                Dictionary<string, string> white = Folder(Path.Combine(root, "Whitelist")), grey = Folder(Path.Combine(root, "Greylist"));
                var sb = new StringBuilder("Their mod files checked against AzuAntiCheat's Whitelist (required) and Greylist (allowed)\n\n");
                if (white.Count == 0 && grey.Count == 0)
                {
                    v.Report = sb.Append("The server has no AzuAntiCheat lists.").ToString();
                    return v;
                }

                Dictionary<string, Mod> files = theirs.Where(m => !string.IsNullOrEmpty(m.File))
                    .GroupBy(m => m.File, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                foreach (var kv in white.Where(kv => !files.ContainsKey(kv.Key)).OrderBy(kv => kv.Key))
                {
                    v.Problems++;
                    v.Lines.Add($"❌ missing: **{Discord.EscapeMarkdown(Path.GetFileNameWithoutExtension(kv.Key))}**");
                    sb.AppendLine($"MISSING (required)  {kv.Key}");
                }
                foreach (var kv in files.Where(f => !white.ContainsKey(f.Key) && !grey.ContainsKey(f.Key)).OrderBy(f => f.Key))
                {
                    v.Problems++;
                    v.Lines.Add($"⛔ not on AzuAntiCheat's lists: **{Discord.EscapeMarkdown(kv.Value.Name)}** {kv.Value.Version}".TrimEnd() + $" ({Discord.EscapeMarkdown(kv.Key)})");
                    sb.AppendLine($"NOT ON THE LISTS    {kv.Key}  ({kv.Value.Name} {kv.Value.Version}, {kv.Value.Guid})");
                }
                List<string> versionOnly = VersionOnlyMods();
                foreach (var kv in files.OrderBy(f => f.Key))
                {
                    // AzuAntiCheat's "Version Only Mods" are checked by version, not by file contents
                    if (versionOnly.Any(n => kv.Key.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0 || kv.Value.Name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                    string listed = white.TryGetValue(kv.Key, out string w) ? w : grey.TryGetValue(kv.Key, out string g) ? g : null;
                    if (listed == null || kv.Value.Hash == "?" || listed == "?" || kv.Value.Hash == listed) continue;
                    v.Problems++;
                    v.Lines.Add($"🧬 different file: **{Discord.EscapeMarkdown(kv.Value.Name)}** {kv.Value.Version}");
                    sb.AppendLine($"DIFFERENT FILE      {kv.Key}  theirs #{kv.Value.Hash}, list #{listed}  ({kv.Value.Name} {kv.Value.Version})");
                }
                if (v.Problems == 0) sb.AppendLine("No problems: every required file is there, nothing extra, and every file matches.");
                v.Report = sb.ToString();
                return v;
            }

            // [4 - Server Guards] Version Only Mods in AzuAntiCheat's config, read from the file
            private static List<string> VersionOnlyMods()
            {
                try
                {
                    string cfg = Path.Combine(Paths.ConfigPath, "Azumatt.AzuAntiCheat.cfg");
                    if (!File.Exists(cfg)) return new List<string>();
                    string line = File.ReadAllLines(cfg).FirstOrDefault(l => l.TrimStart().StartsWith("Version Only Mods", StringComparison.OrdinalIgnoreCase));
                    if (line == null || line.IndexOf('=') < 0) return new List<string>();
                    return line.Substring(line.IndexOf('=') + 1).Split(',').Select(n => n.Trim()).Where(n => n.Length > 0).ToList();
                }
                catch (Exception) { return new List<string>(); }
            }

            // DLL name -> fingerprint, cached by the folder's last change
            private static readonly Dictionary<string, (DateTime at, Dictionary<string, string> files)> s_cache =
                new Dictionary<string, (DateTime, Dictionary<string, string>)>();

            private static Dictionary<string, string> Folder(string dir)
            {
                if (!Directory.Exists(dir)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                DateTime changed = Directory.GetFiles(dir, "*.dll").Select(File.GetLastWriteTimeUtc).DefaultIfEmpty().Max();
                if (s_cache.TryGetValue(dir, out var c) && c.at == changed) return c.files;
                var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string f in Directory.GetFiles(dir, "*.dll")) files[Path.GetFileName(f)] = Fingerprint(f);
                s_cache[dir] = (changed, files);
                return files;
            }
        }

        private static string ProblemMods(string sources)
        {
            var lines = new List<string>();
            foreach (string row in (sources ?? "").Split('\n'))
            {
                string[] f = row.Split('|');
                if (f.Length != 3 || f[1] == "0") continue;   // only mods with errors here; warnings are in the totals
                lines.Add($"• {Discord.EscapeMarkdown(f[0])}: {f[1]} errors, {f[2]} warnings");
            }
            return Cap(lines, 8);
        }

        private static string Cap(List<string> lines, int max)
        {
            string text = string.Join("\n", lines.Take(max));
            if (lines.Count > max) text += $"\n… and {lines.Count - max} more (see the attached file)";
            return Discord.Truncate(text, 1000);
        }

        private class Diff
        {
            public readonly List<string> Lines = new List<string>();
            public int TheirsOnly, ServerOnly, OtherVersion, OtherFile;
            public string Report;
            public string Summary() => TheirsOnly + ServerOnly + OtherVersion + OtherFile == 0
                ? "✅ Same mods, versions and files"
                : $"only theirs {TheirsOnly} · only the server's {ServerOnly} · other version {OtherVersion} · same version, different file {OtherFile}";
        }

        // By plugin GUID. Server-only mods (server tools) show up too; that's expected.
        private static Diff Compare(List<Mod> theirs, List<Mod> server)
        {
            var d = new Diff();
            var sb = new StringBuilder("Their mods compared with the server's (by plugin GUID)\n\n");
            Dictionary<string, Mod> s = server.GroupBy(m => m.Guid).ToDictionary(g => g.Key, g => g.First());
            Dictionary<string, Mod> t = theirs.GroupBy(m => m.Guid).ToDictionary(g => g.Key, g => g.First());

            foreach (Mod m in theirs.Where(m => !s.ContainsKey(m.Guid)))
            {
                d.TheirsOnly++;
                d.Lines.Add($"➕ only theirs: **{Discord.EscapeMarkdown(m.Name)}** {m.Version}");
                sb.AppendLine($"ONLY THEIRS     {m.Name} {m.Version}  ({m.Guid}, {m.File})");
            }
            foreach (Mod m in theirs.Where(m => s.ContainsKey(m.Guid)))
            {
                Mod sv = s[m.Guid];
                if (m.Version != sv.Version)
                {
                    d.OtherVersion++;
                    d.Lines.Add($"🔁 **{Discord.EscapeMarkdown(m.Name)}**: theirs {m.Version}, server {sv.Version}");
                    sb.AppendLine($"OTHER VERSION   {m.Name}: theirs {m.Version}, server {sv.Version}  ({m.Guid})");
                }
                else if (m.Hash != sv.Hash && m.Hash != "?" && sv.Hash != "?")
                {
                    d.OtherFile++;
                    d.Lines.Add($"🧬 **{Discord.EscapeMarkdown(m.Name)}** {m.Version}: different file");
                    sb.AppendLine($"DIFFERENT FILE  {m.Name} {m.Version}: theirs {m.File} #{m.Hash}, server {sv.File} #{sv.Hash}  ({m.Guid})");
                }
            }
            foreach (Mod m in server.Where(m => !t.ContainsKey(m.Guid)))
            {
                d.ServerOnly++;
                sb.AppendLine($"ONLY SERVER     {m.Name} {m.Version}  ({m.Guid}, {m.File})");
            }
            // Missing mods are usually the cause, but the server's own tools make the list long: listed after the rest
            foreach (Mod m in server.Where(m => !t.ContainsKey(m.Guid)).Take(10))
                d.Lines.Add($"➖ not theirs: {Discord.EscapeMarkdown(m.Name)} {m.Version}");
            d.Report = sb.ToString();
            return d;
        }

        private static string ModListText(string name, Snapshot s)
        {
            var sb = new StringBuilder($"{name}: {s.Mods.Count} mods · game {s.Game} · BepInEx {s.BepInEx}\n\n");
            foreach (Mod m in s.Mods) sb.AppendLine($"{m.Name} {m.Version}  ({m.Guid})  {m.File} #{m.Hash}");
            return sb.ToString();
        }

        private static string EmbedJson(string title, int color, List<(string name, string value, bool inline)> fields)
        {
            var sb = new StringBuilder("{\"allowed_mentions\":{\"parse\":[]},\"embeds\":[{\"title\":").Append(Discord.Quote(Discord.Truncate(title, 250)));
            sb.Append(",\"color\":").Append(color).Append(",\"fields\":[");
            for (int i = 0; i < fields.Count && i < 25; i++)
            {
                if (i > 0) sb.Append(',');
                string value = string.IsNullOrEmpty(fields[i].value) ? "-" : Discord.Truncate(fields[i].value, 1024);
                sb.Append("{\"name\":").Append(Discord.Quote(fields[i].name)).Append(",\"value\":").Append(Discord.Quote(value))
                  .Append(",\"inline\":").Append(fields[i].inline ? "true" : "false").Append('}');
            }
            sb.Append("],\"timestamp\":").Append(Discord.Quote(DateTime.UtcNow.ToString("o"))).Append("}]}");
            return sb.ToString();
        }

        // ================================================================ AzuAntiCheat's log lines (read only)

        /// <summary>Keeps AzuAntiCheat's recent log lines, to quote what it said about a player who didn't get in.</summary>
        private class AzuLog : ILogListener
        {
            private static AzuLog s_instance;
            private static readonly LinkedList<(DateTime at, string text)> s_lines = new LinkedList<(DateTime, string)>();

            internal static void Start()
            {
                if (s_instance != null) return;
                s_instance = new AzuLog();
                BepInEx.Logging.Logger.Listeners.Add(s_instance);
            }

            public void LogEvent(object sender, LogEventArgs e)
            {
                string source = e.Source?.SourceName ?? "";
                if (source.IndexOf("AzuAntiCheat", StringComparison.OrdinalIgnoreCase) < 0) return;
                string text = $"[{e.Level}] {e.Data}";
                lock (s_lines)
                {
                    s_lines.AddLast((DateTime.UtcNow, text));
                    while (s_lines.Count > 300) s_lines.RemoveFirst();
                }
            }

            /// <summary>Lines from that window that name the player (ID or name); all of the window's warnings if none do.</summary>
            internal static List<string> About(DateTime from, DateTime to, string host, string steam, string name)
            {
                List<string> window;
                lock (s_lines) window = s_lines.Where(l => l.at >= from && l.at <= to).Select(l => l.text).ToList();
                var keys = new[] { host, steam, name }.Where(k => !string.IsNullOrEmpty(k)).ToList();
                List<string> named = window.Where(l => keys.Any(k => l.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
                return named.Count > 0 ? named : window.Where(l => !l.StartsWith("[Info")).ToList();
            }

            public void Dispose() { }
        }
    }
}
