using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace CaveheimDiscordBridge
{
    /// <summary>
    /// The optional side on players' games, and the server's record of who has it. A client says hello once it has
    /// spawned; only a server running the bridge answers, so on any other server the client part stays silent.
    /// With it: death screenshots, the screenshot key, shouts reported straight to the server, and skill raising.
    /// </summary>
    internal static class ClientPart
    {
        private const int ChunkSize = 48 * 1024;      // well under any per-message limits on the network side
        private const int MaxShotBytes = 4 * 1024 * 1024;

        // ---------------- server side
        private static readonly Dictionary<long, bool> s_clients = new Dictionary<long, bool>();   // peer -> wants death shots
        private class Upload { public string Kind; public byte[][] Parts; public int Received; public float Started; }
        private static readonly Dictionary<string, Upload> s_uploads = new Dictionary<string, Upload>();

        internal static bool HasClient(long peer) => s_clients.ContainsKey(peer);
        internal static bool WantsDeathShots(long peer) => s_clients.TryGetValue(peer, out bool w) && w;
        internal static void Forget(long peer) => s_clients.Remove(peer);

        // ---------------- client side
        private static bool s_serverHasBridge;
        private static bool s_screensAllowed;
        private static float s_lastShot = -100f;
        private static int s_uploadId;

        [HarmonyPatch(typeof(ZNet), "Awake")]
        private static class RegisterRpcs
        {
            private static void Postfix(ZNet __instance)
            {
                s_clients.Clear();
                s_uploads.Clear();
                s_serverHasBridge = false;
                ZRoutedRpc rpc = ZRoutedRpc.instance;
                if (__instance.IsServer())
                {
                    rpc.Register<bool>("CDB_Hello", RPC_Hello);
                    rpc.Register<string, string>("CDB_Shout", RPC_Shout);
                    rpc.Register<int, int, int, string, ZPackage>("CDB_ShotPart", RPC_ShotPart);
                }
                // A hosting player is both: register the client side too
                if (!__instance.IsDedicated())
                {
                    rpc.Register<bool>("CDB_Welcome", RPC_Welcome);
                    rpc.Register<string, float>("CDB_RaiseSkill", RPC_RaiseSkill);
                }
            }
        }

        // ================================================================ server

        private static void RPC_Hello(long sender, bool deathShots)
        {
            s_clients[sender] = deathShots;
            ServerSettings s = DiscordBridgePlugin.Server;
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, "CDB_Welcome", s != null && s.AllowScreenshots.Value);
        }

        private static void RPC_Shout(long sender, string name, string text) => ChatBridge.ShoutToDiscord(sender, name, text, null);

        private static void RPC_ShotPart(long sender, int id, int index, int count, string kind, ZPackage data)
        {
            if (count <= 0 || count > MaxShotBytes / ChunkSize + 1 || index < 0 || index >= count) return;
            string key = sender + ":" + id;
            if (!s_uploads.TryGetValue(key, out Upload up))
            {
                // Drop uploads that never finished
                float now = Time.realtimeSinceStartup;
                var stale = new List<string>();
                foreach (var kv in s_uploads) if (now - kv.Value.Started > 60f) stale.Add(kv.Key);
                foreach (string k in stale) s_uploads.Remove(k);
                up = s_uploads[key] = new Upload { Kind = kind, Parts = new byte[count][], Started = now };
            }
            if (up.Parts.Length != count || up.Parts[index] != null) return;
            up.Parts[index] = data.GetArray();
            if (++up.Received < count) return;
            s_uploads.Remove(key);

            int total = 0;
            foreach (byte[] p in up.Parts) total += p.Length;
            if (total > MaxShotBytes) return;
            var jpg = new byte[total];
            int at = 0;
            foreach (byte[] p in up.Parts) { Buffer.BlockCopy(p, 0, jpg, at, p.Length); at += p.Length; }

            ServerSettings s = DiscordBridgePlugin.Server;
            if (s == null) return;
            if (up.Kind == "death") { if (s.AllowDeathScreenshots.Value) Deaths.Screenshot(sender, jpg); }
            else if (s.AllowScreenshots.Value && s.ChatEnabled.Value)
            {
                ZNetPeer peer = ZNet.instance.GetPeer(sender);
                string name = peer != null ? peer.m_playerName : Game.instance?.GetPlayerProfile()?.GetName() ?? "Viking";
                Discord.Webhook(s.ChatWebhook.Value, "📸", name, jpg, ChatBridge.SteamIdOf(sender));
            }
        }

        // ================================================================ client

        [HarmonyPatch(typeof(Game), "SpawnPlayer")]
        private static class SayHello
        {
            private static void Postfix()
            {
                if (ZRoutedRpc.instance == null || ZNet.instance == null || ZNet.instance.IsDedicated()) return;
                ZRoutedRpc.instance.InvokeRoutedRPC("CDB_Hello", DiscordBridgePlugin.DeathScreenshots.Value);
            }
        }

        private static void RPC_Welcome(long sender, bool screensAllowed)
        {
            s_serverHasBridge = true;
            s_screensAllowed = screensAllowed;
        }

        private static void RPC_RaiseSkill(long sender, string skill, float amount)
        {
            if (Player.m_localPlayer == null) return;
            Player.m_localPlayer.GetSkills().CheatRaiseSkill(skill, amount);
        }

        [HarmonyPatch(typeof(Chat), nameof(Chat.SendText))]
        private static class ReportShout
        {
            private static void Postfix(Talker.Type type, string text)
            {
                if (!s_serverHasBridge || type != Talker.Type.Shout || Player.m_localPlayer == null) return;
                ZRoutedRpc.instance.InvokeRoutedRPC("CDB_Shout", Game.instance.GetPlayerProfile().GetName(), text);
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.OnDeath))]
        private static class DeathShot
        {
            private static void Prefix(Player __instance)
            {
                if (__instance != Player.m_localPlayer || !s_serverHasBridge || !DiscordBridgePlugin.DeathScreenshots.Value) return;
                DiscordBridgePlugin.Instance.StartCoroutine(Capture("death"));
            }
        }

        internal static void Update()
        {
            if (!s_serverHasBridge || !s_screensAllowed || Player.m_localPlayer == null) return;
            if (!DiscordBridgePlugin.ScreenshotKeyEnabled.Value || !DiscordBridgePlugin.ScreenshotKey.Value.IsDown()) return;
            if ((Chat.instance != null && Chat.instance.HasFocus()) || Console.IsVisible() || TextInput.IsVisible()) return;
            if (Time.realtimeSinceStartup - s_lastShot < 10f)
            {
                Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, "Wait a few seconds before the next screenshot.");
                return;
            }
            s_lastShot = Time.realtimeSinceStartup;
            DiscordBridgePlugin.Instance.StartCoroutine(Capture("shot"));
            Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, "Screenshot sent to Discord.");
        }

        private static IEnumerator Capture(string kind)
        {
            yield return new WaitForEndOfFrame();
            Texture2D shot = null, small = null;
            RenderTexture rt = null;
            byte[] jpg;
            try
            {
                shot = ScreenCapture.CaptureScreenshotAsTexture();
                // At most 1280 wide: plenty for Discord, and a fraction of the upload
                int w = Math.Min(1280, shot.width), h = Mathf.RoundToInt(shot.height * (w / (float)shot.width));
                if (w != shot.width)
                {
                    // sRGB like the screen itself: a linear target washed the shots out (far too bright)
                    rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                    Graphics.Blit(shot, rt);
                    RenderTexture prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    small = new Texture2D(w, h, TextureFormat.RGB24, false);
                    small.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                    small.Apply();
                    RenderTexture.active = prev;
                }
                jpg = (small ?? shot).EncodeToJPG(80);
            }
            catch (Exception ex)
            {
                DiscordBridgePlugin.Log.LogWarning($"Screenshot failed: {ex.Message}");
                yield break;
            }
            finally
            {
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (shot != null) UnityEngine.Object.Destroy(shot);
                if (small != null) UnityEngine.Object.Destroy(small);
            }
            if (jpg == null || jpg.Length > MaxShotBytes || ZRoutedRpc.instance == null) yield break;

            int id = ++s_uploadId, count = (jpg.Length + ChunkSize - 1) / ChunkSize;
            for (int i = 0; i < count; i++)
            {
                int len = Math.Min(ChunkSize, jpg.Length - i * ChunkSize);
                var part = new byte[len];
                Buffer.BlockCopy(jpg, i * ChunkSize, part, 0, len);
                ZRoutedRpc.instance.InvokeRoutedRPC("CDB_ShotPart", id, i, count, kind, new ZPackage(part));
                yield return null;   // one part a frame, so a big shot never stalls the connection
            }
        }
    }
}
