using System.Collections.Generic;
using UnityEngine;

namespace CaveheimDiscordBridge
{
    /// <summary>
    /// DarwinAwards' death line, posted to Discord. A player with the client part also sends a screenshot taken at
    /// the moment of death; the line waits a few seconds for it so both go out as one post.
    /// </summary>
    internal static class Deaths
    {
        private const float ScreenshotWait = 8f;

        private class Pending { public string Text; public float Until; }
        private static readonly Dictionary<long, Pending> s_pending = new Dictionary<long, Pending>();
        private static readonly Dictionary<long, byte[]> s_earlyShots = new Dictionary<long, byte[]>();

        // No fallback to another webhook: an empty death webhook means the death feed is off
        private static string Webhook => DiscordBridgePlugin.Server.DeathWebhook.Value;

        internal static void Report(long peer, string text)
        {
            ServerSettings s = DiscordBridgePlugin.Server;
            if (s == null || !s.NotifyDeaths.Value || string.IsNullOrWhiteSpace(s.DeathWebhook.Value) || string.IsNullOrWhiteSpace(text)) return;
            string line = "💀 " + Discord.EscapeMarkdown(text);

            // The screenshot can arrive before the death line: post them together right away
            if (s_earlyShots.TryGetValue(peer, out byte[] shot))
            {
                s_earlyShots.Remove(peer);
                Discord.Webhook(Webhook, line, null, shot);
                return;
            }
            if (s.AllowDeathScreenshots.Value && ClientPart.HasClient(peer) && ClientPart.WantsDeathShots(peer))
                s_pending[peer] = new Pending { Text = line, Until = Time.realtimeSinceStartup + ScreenshotWait };
            else
                Discord.Webhook(Webhook, line);
        }

        internal static void Screenshot(long peer, byte[] jpg)
        {
            if (s_pending.TryGetValue(peer, out Pending p))
            {
                s_pending.Remove(peer);
                Discord.Webhook(Webhook, p.Text, null, jpg);
            }
            else s_earlyShots[peer] = jpg;
        }

        internal static void Update()
        {
            if (s_pending.Count == 0 && s_earlyShots.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            List<long> due = null;
            foreach (var kv in s_pending)
                if (now > kv.Value.Until) (due ??= new List<long>()).Add(kv.Key);
            if (due != null)
                foreach (long peer in due)
                {
                    Discord.Webhook(Webhook, s_pending[peer].Text);   // no screenshot came: post the line alone
                    s_pending.Remove(peer);
                }
            // A screenshot with no death line (DarwinAwards missing or off) is dropped after a while
            if (s_earlyShots.Count > 0 && Time.frameCount % 600 == 0) s_earlyShots.Clear();
        }
    }
}
