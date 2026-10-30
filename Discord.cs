using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using UnityEngine;

namespace CaveheimDiscordBridge
{
    /// <summary>
    /// All Discord traffic, off Unity's main thread: one sender thread works through outgoing posts in order (and
    /// waits out rate limits), one poller thread reads the chat and command channels with the bot token.
    /// </summary>
    internal static class Discord
    {
        private const string Api = "https://discord.com/api/v10";

        private class Outgoing
        {
            public string Url;           // webhook URL, or a bot channel-message URL
            public bool Bot;             // authorise with the bot token
            public string Json;
            public byte[] Image;         // optional attachment (JPG)
            public string Content, Username, SteamId;   // webhook posts: JSON built when sent, once the avatar is known
        }

        internal static HttpClient s_http;
        private static readonly BlockingCollection<Outgoing> s_queue = new BlockingCollection<Outgoing>(500);
        private static Thread s_sender, s_poller;
        private static volatile bool s_running;
        private static readonly CancellationTokenSource s_stop = new CancellationTokenSource();
        private static readonly Dictionary<string, string> s_lastSeen = new Dictionary<string, string>();
        private static readonly Dictionary<string, DateTime> s_retryAt = new Dictionary<string, DateTime>();

        internal static void Start()
        {
            if (s_running) return;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            s_http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            s_http.DefaultRequestHeaders.UserAgent.ParseAdd($"DiscordBot (https://github.com/TheCavemanOfficial/CavemansDiscordBridge, {DiscordBridgePlugin.ModVersion})");
            s_running = true;
            s_sender = new Thread(SendLoop) { IsBackground = true, Name = "DiscordBridge sender" };
            s_poller = new Thread(PollLoop) { IsBackground = true, Name = "DiscordBridge poller" };
            s_sender.Start();
            s_poller.Start();
        }

        /// <summary>Cancels any request in flight and lets both threads finish before the game shuts down.</summary>
        internal static void Stop()
        {
            if (!s_running) return;
            s_running = false;
            s_queue.CompleteAdding();
            s_stop.Cancel();
            s_sender?.Join(1000);
            s_poller?.Join(1000);
        }

        // --------------------------------------------------------------------- sending

        /// <summary>Posts through a webhook. Mentions are never parsed, so nothing typed in game can ping anyone.</summary>
        /// <param name="steamId">A Steam ID (digits) whose Steam avatar the post uses, when a Steam API key is set.</param>
        internal static void Webhook(string url, string content, string username = null, byte[] image = null, string steamId = null)
        {
            if (string.IsNullOrWhiteSpace(url) || (string.IsNullOrEmpty(content) && image == null)) return;
            Enqueue(new Outgoing { Url = url.Trim() + "?wait=false", Content = content, Username = username, SteamId = steamId, Image = image });
        }

        /// <summary>Posts as the bot in a channel (command replies).</summary>
        internal static void BotMessage(string channelId, string content)
        {
            if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrEmpty(content)) return;
            Enqueue(new Outgoing { Url = $"{Api}/channels/{channelId.Trim()}/messages", Bot = true, Json = MessageJson(content, null) });
        }

        /// <summary>Sends straight away on the calling thread, for the shutdown messages the queue would not get to.</summary>
        internal static void WebhookNow(string url, string content, string username = null)
        {
            if (string.IsNullOrWhiteSpace(url) || s_http == null) return;
            try { Send(new Outgoing { Url = url.Trim(), Json = MessageJson(content, username) }, TimeSpan.FromSeconds(4), urgent: true); }
            catch (Exception ex) { DiscordBridgePlugin.Log.LogWarning($"Discord post failed: {ex.Message}"); }
        }

        private static void Enqueue(Outgoing item)
        {
            if (!s_running || s_queue.IsAddingCompleted) return;
            if (!s_queue.TryAdd(item)) DiscordBridgePlugin.Log.LogWarning("Discord queue full; a message was dropped.");
        }

        internal static string MessageJson(string content, string username, string avatarUrl = null)
        {
            var sb = new StringBuilder("{\"allowed_mentions\":{\"parse\":[]}");
            if (!string.IsNullOrEmpty(content)) sb.Append(",\"content\":").Append(Quote(Truncate(content, 2000)));
            if (!string.IsNullOrEmpty(username)) sb.Append(",\"username\":").Append(Quote(Truncate(username, 80)));
            if (!string.IsNullOrEmpty(avatarUrl)) sb.Append(",\"avatar_url\":").Append(Quote(avatarUrl));
            return sb.Append('}').ToString();
        }

        private static void SendLoop()
        {
            try
            {
                foreach (Outgoing item in s_queue.GetConsumingEnumerable())
                {
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        try
                        {
                            TimeSpan? wait = Send(item, TimeSpan.FromSeconds(15));
                            if (wait == null) break;
                            s_stop.Token.WaitHandle.WaitOne(wait.Value);   // rate limited: wait and resend
                        }
                        catch (Exception) when (!s_running) { return; }
                        catch (Exception ex)
                        {
                            DiscordBridgePlugin.Log.LogWarning($"Discord post failed: {ex.Message}");
                            s_stop.Token.WaitHandle.WaitOne(2000);
                        }
                    }
                }
            }
            catch (InvalidOperationException) { }   // queue closed at shutdown
        }

        /// <summary>Returns how long to wait before retrying when Discord rate-limits the post, otherwise null.</summary>
        /// <param name="urgent">The shutdown messages: not cancelled when the bridge stops, only by their own timeout.</param>
        private static TimeSpan? Send(Outgoing item, TimeSpan timeout, bool urgent = false)
        {
            if (item.Json == null) item.Json = MessageJson(item.Content, item.Username, SteamAvatars.Get(item.SteamId));
            using (var request = new HttpRequestMessage(HttpMethod.Post, item.Url))
            using (var cts = urgent ? new CancellationTokenSource() : CancellationTokenSource.CreateLinkedTokenSource(s_stop.Token))
            {
                if (item.Bot) request.Headers.Authorization = new AuthenticationHeaderValue("Bot", DiscordBridgePlugin.Server.BotToken.Value.Trim());
                if (item.Image == null)
                {
                    request.Content = new StringContent(item.Json, Encoding.UTF8, "application/json");
                }
                else
                {
                    var form = new MultipartFormDataContent();
                    form.Add(new StringContent(item.Json, Encoding.UTF8, "application/json"), "payload_json");
                    var file = new ByteArrayContent(item.Image);
                    file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                    form.Add(file, "files[0]", "screenshot.jpg");
                    request.Content = form;
                }
                cts.CancelAfter(timeout);
                using (HttpResponseMessage response = s_http.SendAsync(request, cts.Token).GetAwaiter().GetResult())
                {
                    if ((int)response.StatusCode == 429) return RetryAfter(response);
                    if (!response.IsSuccessStatusCode)
                        DiscordBridgePlugin.Log.LogWarning($"Discord refused a post ({(int)response.StatusCode}): {Truncate(response.Content.ReadAsStringAsync().GetAwaiter().GetResult(), 300)}");
                    return null;
                }
            }
        }

        /// <summary>
        /// One bot API call, on the calling (background) thread: returns the HTTP status and the reply body. Rate limits
        /// are waited out once. For the admin panel, which needs the message ID back and edits messages in place.
        /// </summary>
        internal static int BotCall(string method, string path, string json, out string body, bool urgent = false)
        {
            body = null;
            ServerSettings s = DiscordBridgePlugin.Server;
            if (s_http == null || s == null || string.IsNullOrWhiteSpace(s.BotToken.Value)) return 0;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using (var request = new HttpRequestMessage(new HttpMethod(method), path.StartsWith("http") ? path : Api + path))
                using (var cts = urgent ? new CancellationTokenSource() : CancellationTokenSource.CreateLinkedTokenSource(s_stop.Token))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bot", s.BotToken.Value.Trim());
                    if (json != null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    cts.CancelAfter(urgent ? TimeSpan.FromSeconds(4) : TimeSpan.FromSeconds(15));
                    using (HttpResponseMessage response = s_http.SendAsync(request, cts.Token).GetAwaiter().GetResult())
                    {
                        body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        if ((int)response.StatusCode == 429 && attempt == 0 && !urgent)
                        {
                            s_stop.Token.WaitHandle.WaitOne(RetryAfter(response));
                            continue;
                        }
                        return (int)response.StatusCode;
                    }
                }
            }
            return 429;
        }

        /// <summary>A webhook post with embeds (JSON) and text files attached. Sent off the main thread, once more if rate limited.</summary>
        internal static void WebhookWithFiles(string url, string json, List<(string name, string text)> files) => PostWithFiles(url, false, json, files);

        /// <summary>The same, posted by the bot in a channel.</summary>
        internal static void BotWithFiles(string channelId, string json, List<(string name, string text)> files) =>
            PostWithFiles($"{Api}/channels/{channelId.Trim()}/messages", true, json, files);

        // The report first, then its files in a second message right under it (a reply, when the bot posts): Discord
        // shows a message's files above its embed, which buried the readable report under the raw logs
        private static void PostWithFiles(string url, bool bot, string json, List<(string name, string text)> files)
        {
            if (string.IsNullOrWhiteSpace(url) || s_http == null) return;
            url = url.Trim();
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    string reportUrl = bot ? url : url + (url.Contains("?") ? "&" : "?") + "wait=true";
                    if (!SendForm(reportUrl, bot, json, null, out string body)) return;
                    if (files == null || files.Count == 0) return;

                    string id = Json.Parse(body) is Dictionary<string, object> m ? m.Str("id") : null;
                    var sb = new StringBuilder("{\"allowed_mentions\":{\"parse\":[]},\"content\":\"📎 Files for the report above\"");
                    if (bot && id != null) sb.Append(",\"message_reference\":{\"message_id\":").Append(Quote(id)).Append(",\"fail_if_not_exists\":false}");
                    SendForm(url, bot, sb.Append('}').ToString(), files, out _);
                }
                catch (Exception ex) { DiscordBridgePlugin.Log.LogWarning($"Discord report failed: {ex.Message}"); }
            });
        }

        // One multipart post (JSON plus optional text files), once more if rate limited. True when Discord took it.
        private static bool SendForm(string url, bool bot, string json, List<(string name, string text)> files, out string body)
        {
            body = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    if (bot) request.Headers.Authorization = new AuthenticationHeaderValue("Bot", DiscordBridgePlugin.Server.BotToken.Value.Trim());
                    var form = new MultipartFormDataContent();
                    form.Add(new StringContent(json, Encoding.UTF8, "application/json"), "payload_json");
                    for (int i = 0; files != null && i < files.Count && i < 10; i++)
                    {
                        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(files[i].text ?? ""));
                        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
                        form.Add(file, $"files[{i}]", files[i].name);
                    }
                    request.Content = form;
                    using (HttpResponseMessage response = s_http.SendAsync(request, s_stop.Token).GetAwaiter().GetResult())
                    {
                        body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        if ((int)response.StatusCode == 429 && attempt == 0) { s_stop.Token.WaitHandle.WaitOne(RetryAfter(response)); continue; }
                        if (response.IsSuccessStatusCode) return true;
                        DiscordBridgePlugin.Log.LogWarning($"Discord refused a report ({(int)response.StatusCode}): {Truncate(body, 300)}");
                        return false;
                    }
                }
            }
            return false;
        }

        /// <summary>Answers a button click or form: Discord wants this within 3 seconds, so it skips the post queue.</summary>
        internal static void InteractionReply(string id, string token, string json)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Post, $"{Api}/interactions/{id}/{token}/callback"))
                    {
                        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                        using (HttpResponseMessage response = s_http.SendAsync(request).GetAwaiter().GetResult())
                        {
                            if (!response.IsSuccessStatusCode)
                                DiscordBridgePlugin.Log.LogWarning($"Discord refused a panel reply ({(int)response.StatusCode}): {Truncate(response.Content.ReadAsStringAsync().GetAwaiter().GetResult(), 300)}");
                        }
                    }
                }
                catch (Exception ex) { DiscordBridgePlugin.Log.LogWarning($"Panel reply failed: {ex.Message}"); }
            });
        }

        private static TimeSpan RetryAfter(HttpResponseMessage response)
        {
            double seconds = 1;
            if (response.Headers.TryGetValues("Retry-After", out IEnumerable<string> values))
                double.TryParse(values.FirstOrDefault(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out seconds);
            return TimeSpan.FromSeconds(Math.Max(0.5, Math.Min(seconds, 30)));
        }

        // --------------------------------------------------------------------- reading

        internal class DiscordMessage { public string id; public string content; public string webhook_id; public int type; public Author author; }
        internal class Author { public string id; public string username; public string global_name; public bool bot; }

        private static DiscordMessage[] ReadMessages(string body)
        {
            var result = new List<DiscordMessage>();
            if (!(Json.Parse(body) is List<object> list)) return result.ToArray();
            foreach (object item in list)
            {
                if (!(item is Dictionary<string, object> o)) continue;
                Dictionary<string, object> a = o.Obj("author");
                result.Add(new DiscordMessage
                {
                    id = o.Str("id"),
                    content = o.Str("content"),
                    webhook_id = o.Str("webhook_id"),
                    type = o.TryGetValue("type", out object t) && t is double td ? (int)td : 0,
                    author = a == null ? null : new Author { id = a.Str("id"), username = a.Str("username"), global_name = a.Str("global_name"), bot = a.Bool("bot") }
                });
            }
            result.RemoveAll(m => m.id == null);
            return result.ToArray();
        }

        private static void PollLoop()
        {
            while (s_running)
            {
                ServerSettings s = DiscordBridgePlugin.Server;
                float seconds = s?.PollSeconds.Value ?? 2f;
                try
                {
                    if (s != null && !string.IsNullOrWhiteSpace(s.BotToken.Value))
                    {
                        if (s.ChatEnabled.Value && s.DiscordToGame.Value) Poll(s.ChatChannelId.Value, msg => DiscordBridgePlugin.RunOnMainThread(() => ChatBridge.FromDiscord(msg)));
                        if (s.CommandsEnabled.Value) Poll(s.CommandChannelId.Value, msg => DiscordBridgePlugin.RunOnMainThread(() => Commands.Run(msg)));
                    }
                }
                catch (Exception ex) when (!s_running) { _ = ex; return; }   // cancelled at shutdown
                catch (Exception ex) { DiscordBridgePlugin.Log.LogWarning($"Discord poll failed: {ex}"); seconds = Math.Max(seconds, 10f); }
                s_stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(seconds));
            }
        }

        private static void Poll(string channelId, Action<DiscordMessage> handle)
        {
            if (string.IsNullOrWhiteSpace(channelId)) return;
            channelId = channelId.Trim();
            // A channel Discord refused waits on its own, without holding up the other channel
            if (s_retryAt.TryGetValue(channelId, out DateTime retry) && DateTime.UtcNow < retry) return;
            s_lastSeen.TryGetValue(channelId, out string after);

            // First look at a channel: only note where it is now, so old history isn't replayed into the game
            string url = after == null ? $"{Api}/channels/{channelId}/messages?limit=1" : $"{Api}/channels/{channelId}/messages?after={after}&limit=50";
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bot", DiscordBridgePlugin.Server.BotToken.Value.Trim());
                using (HttpResponseMessage response = s_http.SendAsync(request, s_stop.Token).GetAwaiter().GetResult())
                {
                    if ((int)response.StatusCode == 429) { s_retryAt[channelId] = DateTime.UtcNow + RetryAfter(response); return; }
                    string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (!response.IsSuccessStatusCode)
                    {
                        DiscordBridgePlugin.Log.LogWarning($"Can't read channel {channelId} ({(int)response.StatusCode}): {Truncate(body, 200)}. Check the bot token, that the bot is in the server and can see the channel, and that Message Content Intent is on. Trying again in 30 s.");
                        s_retryAt[channelId] = DateTime.UtcNow.AddSeconds(30);
                        return;
                    }
                    DiscordMessage[] messages = ReadMessages(body);
                    if (after == null) DiscordBridgePlugin.Log.LogInfo($"Listening to channel {channelId}.");
                    if (messages.Length == 0) { if (after == null) s_lastSeen[channelId] = "0"; return; }

                    // Discord's order differs between queries: work from the IDs (they grow over time), oldest first
                    Array.Sort(messages, (a, b) => CompareIds(a.id, b.id));
                    s_lastSeen[channelId] = messages[messages.Length - 1].id;
                    if (after == null) return;
                    foreach (DiscordMessage m in messages)
                    {
                        if (m.author == null || m.author.bot || !string.IsNullOrEmpty(m.webhook_id)) continue;   // our own posts, other bots
                        if (m.type != 0 && m.type != 19) continue;   // Discord's notices ("pinned a message", joins); 19 = a reply
                        if (string.IsNullOrWhiteSpace(m.content))
                        {
                            DiscordBridgePlugin.Log.LogWarning($"A message in channel {channelId} came through empty: turn on Message Content Intent for the bot.");
                            continue;
                        }
                        DiscordBridgePlugin.Log.LogInfo($"[Discord #{channelId}] {m.author.username}: {Truncate(m.content, 120)}");
                        handle(m);
                    }
                }
            }
        }

        // --------------------------------------------------------------------- helpers

        internal static string Quote(string s)
        {
            var sb = new StringBuilder(s.Length + 2).Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        // Snowflake IDs: longer is newer, same length compares as text
        private static int CompareIds(string a, string b) =>
            a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);

        internal static string Truncate(string s, int max) => s == null || s.Length <= max ? s : s.Substring(0, max - 1) + "…";

        /// <summary>Stops Discord markdown in player text from formatting (or hiding) anything.</summary>
        internal static string EscapeMarkdown(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if ("\\*_~`|>#[]()-".IndexOf(c) >= 0) sb.Append('\\');
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
