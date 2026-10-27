using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace CaveheimDiscordBridge
{
    /// <summary>
    /// Players' Steam profile pictures for their Discord posts, looked up with the server owner's Steam Web API key
    /// (players need nothing). Runs on the sender thread right before a post goes out; each answer is kept for a few
    /// hours. No key, a private profile or a failed lookup leaves the webhook's own logo.
    /// </summary>
    internal static class SteamAvatars
    {
        private static readonly TimeSpan Keep = TimeSpan.FromHours(6);
        private static readonly Dictionary<string, (string url, DateTime at)> s_cache = new Dictionary<string, (string, DateTime)>();

        internal static string Get(string steamId)
        {
            ServerSettings s = DiscordBridgePlugin.Server;
            if (s == null || !s.SteamAvatars.Value) return null;
            string key = s.SteamApiKey.Value?.Trim();
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(steamId) || !steamId.All(char.IsDigit)) return null;

            lock (s_cache)
                if (s_cache.TryGetValue(steamId, out var hit) && DateTime.UtcNow - hit.at < Keep) return hit.url;

            string url = null;
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                {
                    string body = Discord.s_http.GetStringAsync(
                        $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={Uri.EscapeDataString(key)}&steamids={steamId}")
                        .GetAwaiter().GetResult();
                    var players = (Json.Parse(body) as Dictionary<string, object>).Obj("response");
                    if (players != null && players.TryGetValue("players", out object list) && list is List<object> l && l.Count > 0)
                        url = (l[0] as Dictionary<string, object>).Str("avatarfull");
                }
            }
            catch (Exception ex) { DiscordBridgePlugin.Log.LogDebug($"Steam avatar lookup failed for {steamId}: {ex.Message}"); }

            lock (s_cache) s_cache[steamId] = (url, DateTime.UtcNow);
            return url;
        }

        /// <summary>"Steam_76561197..." (the game's platform ID) or a bare ID, as digits; null for other platforms.</summary>
        internal static string FromPlatformId(string platformId)
        {
            if (string.IsNullOrEmpty(platformId)) return null;
            string id = platformId.StartsWith("Steam_", StringComparison.OrdinalIgnoreCase) ? platformId.Substring(6) : platformId;
            return id.Length > 0 && id.All(char.IsDigit) ? id : null;
        }
    }
}
