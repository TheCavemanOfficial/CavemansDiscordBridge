using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;

namespace CaveheimDiscordBridge
{
    /// <summary>
    /// The bot's live connection to Discord, used only to hear button clicks and form submits on the admin panel
    /// (Discord delivers those over this connection; messages are still read by polling). Asks for no intents, so it
    /// receives nothing else. Reconnects on its own; a click made while it's reconnecting just shows as failed.
    /// </summary>
    internal static class Gateway
    {
        private const string Url = "wss://gateway.discord.gg/?v=10&encoding=json";

        internal static event Action<Dictionary<string, object>> Interaction;

        private static Thread s_thread;
        private static volatile bool s_running;
        private static ClientWebSocket s_socket;
        private static CancellationTokenSource s_stop;
        private static readonly object s_sendLock = new object();

        internal static void Start()
        {
            if (s_running) return;
            s_running = true;
            s_stop = new CancellationTokenSource();
            s_thread = new Thread(Run) { IsBackground = true, Name = "DiscordBridge gateway" };
            s_thread.Start();
        }

        internal static void Stop()
        {
            if (!s_running) return;
            s_running = false;
            s_stop.Cancel();
            try { s_socket?.Abort(); } catch { }
            s_thread?.Join(1000);
        }

        private static void Run()
        {
            int backoff = 5;
            while (s_running)
            {
                try
                {
                    Session();
                    backoff = 5;
                }
                catch (Exception) when (!s_running) { return; }
                catch (Exception ex)
                {
                    DiscordBridgePlugin.Log.LogWarning($"Discord connection (admin panel) dropped: {ex.Message}. Reconnecting in {backoff} s.");
                }
                s_stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(backoff));
                backoff = Math.Min(backoff * 2, 120);
            }
        }

        private static void Session()
        {
            string token = DiscordBridgePlugin.Server?.BotToken.Value?.Trim();
            if (string.IsNullOrEmpty(token)) throw new InvalidOperationException("no bot token");

            using (var socket = new ClientWebSocket())
            using (var session = CancellationTokenSource.CreateLinkedTokenSource(s_stop.Token))
            {
                s_socket = socket;
                socket.ConnectAsync(new Uri(Url), session.Token).GetAwaiter().GetResult();

                long? seq = null;
                Thread heartbeat = null;
                bool acked = true;
                try
                {
                    while (s_running && socket.State == WebSocketState.Open)
                    {
                        string text = Receive(socket, session.Token);
                        if (text == null) break;   // closed by Discord
                        if (!(Json.Parse(text) is Dictionary<string, object> msg)) continue;
                        if (msg.TryGetValue("s", out object s) && s is double d) seq = (long)d;

                        int op = msg.TryGetValue("op", out object o) && o is double od ? (int)od : -1;
                        switch (op)
                        {
                            case 10:   // hello: start the heartbeat, then log in
                                double interval = msg.Obj("d") != null && msg.Obj("d").TryGetValue("heartbeat_interval", out object hi) && hi is double h ? h : 41250;
                                heartbeat = new Thread(() =>
                                {
                                    // The first beat comes at a random point in the interval, as Discord asks
                                    if (session.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(interval * new Random().NextDouble()))) return;
                                    while (!session.IsCancellationRequested)
                                    {
                                        if (!acked) { try { socket.Abort(); } catch { } return; }   // a zombie connection
                                        acked = false;
                                        Send(socket, "{\"op\":1,\"d\":" + (seq.HasValue ? seq.Value.ToString() : "null") + "}", session.Token);
                                        if (session.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(interval))) return;
                                    }
                                }) { IsBackground = true, Name = "DiscordBridge heartbeat" };
                                heartbeat.Start();
                                Send(socket, "{\"op\":2,\"d\":{\"token\":" + Discord.Quote(token) + ",\"intents\":0," +
                                             "\"properties\":{\"os\":\"windows\",\"browser\":\"CavemansDiscordBridge\",\"device\":\"CavemansDiscordBridge\"}}}", session.Token);
                                break;
                            case 11:   // heartbeat ack
                                acked = true;
                                break;
                            case 1:    // Discord asks for a beat now
                                Send(socket, "{\"op\":1,\"d\":" + (seq.HasValue ? seq.Value.ToString() : "null") + "}", session.Token);
                                break;
                            case 7:    // reconnect
                            case 9:    // invalid session
                                return;
                            case 0:    // event
                                string type = msg.Str("t");
                                if (type == "READY") DiscordBridgePlugin.Log.LogInfo("Connected to Discord for the admin panel.");
                                else if (type == "INTERACTION_CREATE" && msg.Obj("d") is Dictionary<string, object> data) Interaction?.Invoke(data);
                                break;
                        }
                    }
                }
                finally
                {
                    session.Cancel();
                    heartbeat?.Join(500);
                    s_socket = null;
                    if (socket.State == WebSocketState.CloseReceived)
                    {
                        if (socket.CloseStatus == (WebSocketCloseStatus)4004) DiscordBridgePlugin.Log.LogWarning("Discord says the bot token is wrong; the admin panel can't connect.");
                    }
                }
            }
        }

        private static string Receive(ClientWebSocket socket, CancellationToken token)
        {
            var buffer = new ArraySegment<byte>(new byte[16 * 1024]);
            using (var ms = new MemoryStream())
            {
                while (true)
                {
                    WebSocketReceiveResult r = socket.ReceiveAsync(buffer, token).GetAwaiter().GetResult();
                    if (r.MessageType == WebSocketMessageType.Close) return null;
                    ms.Write(buffer.Array, 0, r.Count);
                    if (r.EndOfMessage) return Encoding.UTF8.GetString(ms.ToArray());
                }
            }
        }

        private static void Send(ClientWebSocket socket, string json, CancellationToken token)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            // Heartbeats and replies come from two threads; a socket takes one send at a time
            lock (s_sendLock) socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token).GetAwaiter().GetResult();
        }
    }
}
