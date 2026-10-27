using System;
using System.Collections.Concurrent;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace CaveheimDiscordBridge
{
    /// <summary>
    /// Two-way bridge between a Valheim server and Discord: shouts and Discord messages both ways, server and player
    /// notifications, DarwinAwards death lines, and admin commands from a Discord channel. Everything runs on the
    /// server; the optional client part adds death screenshots, a screenshot key and skill raising.
    /// </summary>
    [BepInPlugin(ModGUID, ModName, ModVersion)]
    [BepInDependency("org.bepinex.plugins.darwinawards", BepInDependency.DependencyFlags.SoftDependency)]
    public class DiscordBridgePlugin : BaseUnityPlugin
    {
        public const string ModGUID = "TheCaveman.CavemansDiscordBridge";
        public const string ModName = "Caveman's Discord Bridge";
        public const string ModVersion = "1.0.0";

        internal static DiscordBridgePlugin Instance;
        internal static ManualLogSource Log;

        // Work the background (HTTP) threads hand back to Unity's main thread
        private static readonly ConcurrentQueue<Action> s_mainThread = new ConcurrentQueue<Action>();
        internal static void RunOnMainThread(Action action) => s_mainThread.Enqueue(action);

        // --- Client settings (this file ships in the client pack: nothing secret in here) ---
        internal static ConfigEntry<bool> DeathScreenshots;
        internal static ConfigEntry<bool> ScreenshotKeyEnabled;
        internal static ConfigEntry<KeyboardShortcut> ScreenshotKey;

        // --- Server settings: a separate file that only the server ever creates, holding the token and webhooks ---
        internal static ServerSettings Server;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            DeathScreenshots = Config.Bind("Client", "Death Screenshots", true,
                "When you die, a screenshot goes to the server's Discord death feed (only on servers running this mod).");
            ScreenshotKeyEnabled = Config.Bind("Client", "Screenshot Key Enabled", true,
                "Lets the screenshot key send screenshots to the server's Discord chat channel.");
            ScreenshotKey = Config.Bind("Client", "Screenshot Key", new KeyboardShortcut(KeyCode.F10),
                "Sends a screenshot to the server's Discord chat channel (only on servers running this mod).");

            new Harmony(ModGUID).PatchAll(typeof(DiscordBridgePlugin).Assembly);

            // A dedicated server has no graphics device; it announces "launching" before the world even loads
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                EnsureServerSettings();
                // The preloader (patchers/) normally sent it already, long before any mod loaded
                if (!(AppDomain.CurrentDomain.GetData("CavemansDiscordBridge.LaunchSent") is bool sent && sent))
                    Notifications.ServerLaunching();
            }
            Log.LogInfo($"{ModName} {ModVersion} loaded");
        }

        /// <summary>Creates the server-only settings file the first time this game acts as a server.</summary>
        internal static void EnsureServerSettings()
        {
            if (Server != null) return;
            // Owner metadata writes the "## Plugin GUID" header, so tools can tell which mod the file belongs to
            Server = new ServerSettings(new ConfigFile(Path.Combine(Paths.ConfigPath, ModGUID + ".Server.cfg"), true,
                MetadataHelper.GetMetadata(typeof(DiscordBridgePlugin))));
            Discord.Start();
        }

        private void Update()
        {
            int budget = 50;
            while (budget-- > 0 && s_mainThread.TryDequeue(out Action action))
            {
                try { action(); }
                catch (Exception ex) { Log.LogWarning($"Main-thread task failed: {ex}"); }
            }
            ClientPart.Update();
            Deaths.Update();
        }

        // No network work here: by now Unity and Steam are torn down, and a late web request crashed the game on quit.
        // "Offline" goes out when the world closes instead (ZNet.OnDestroy).
        private void OnDestroy() => Discord.Stop();
    }

    internal class ServerSettings
    {
        public readonly ConfigEntry<string> BotToken;
        public readonly ConfigEntry<float> PollSeconds;
        public readonly ConfigEntry<string> SteamApiKey;
        public readonly ConfigEntry<bool> SteamAvatars;
        public readonly ConfigEntry<bool> ShoutsToDiscord, DiscordToGame, AllowDeathScreenshots, CommandsEnabled;

        public readonly ConfigEntry<bool> ChatEnabled;
        public readonly ConfigEntry<string> ChatChannelId;
        public readonly ConfigEntry<string> ChatWebhook;
        public readonly ConfigEntry<string> InGamePrefix;
        public readonly ConfigEntry<bool> AllowScreenshots;
        public readonly ConfigEntry<string> IgnoredShouts;

        public readonly ConfigEntry<string> NotifyWebhook;
        public readonly ConfigEntry<bool> NotifyLaunch, NotifyStarted, NotifyStopping, NotifyStopped;
        public readonly ConfigEntry<bool> NotifyJoin, NotifyLeave;
        public readonly ConfigEntry<bool> NotifyDeaths;
        public readonly ConfigEntry<string> DeathWebhook;

        public readonly ConfigEntry<string> CommandChannelId;
        public readonly ConfigEntry<string> CommandPrefix;
        public readonly ConfigEntry<string> AdminIds;

        public ServerSettings(ConfigFile cfg)
        {
            BotToken = cfg.Bind("1 - Bot", "Bot Token", "",
                "The Discord bot's token (Developer Portal > your app > Bot > Reset Token). Needed to read Discord messages and run commands. Keep it secret: this file never leaves the server.");
            PollSeconds = cfg.Bind("1 - Bot", "Poll Seconds", 2f,
                new ConfigDescription("How often the server checks the chat and command channels for new messages.", new AcceptableValueRange<float>(1f, 30f)));
            SteamApiKey = cfg.Bind("1 - Bot", "Steam API Key", "",
                "A Steam Web API key (steamcommunity.com/dev/apikey). With it, players' posts show their Steam profile picture. Empty keeps the webhook's logo. Keep it secret.");

            SteamAvatars = cfg.Bind("1 - Bot", "Steam Avatars", true, "Show players' Steam profile pictures on their posts (needs the Steam API Key).");

            ChatEnabled = cfg.Bind("2 - Chat", "Enabled", true, "The whole chat bridge. Off turns both directions off.");
            ShoutsToDiscord = cfg.Bind("2 - Chat", "Shouts To Discord", true, "Post in-game shouts to the Discord chat channel.");
            DiscordToGame = cfg.Bind("2 - Chat", "Discord To Game", true, "Show messages from the Discord chat channel in game chat.");
            ChatChannelId = cfg.Bind("2 - Chat", "Channel ID", "",
                "The chat channel's ID (Discord: turn on Developer Mode, right-click the channel > Copy Channel ID).");
            ChatWebhook = cfg.Bind("2 - Chat", "Webhook URL", "",
                "A webhook in the chat channel. Shouts are posted through it under the player's name.");
            InGamePrefix = cfg.Bind("2 - Chat", "In-game Prefix", "[D]", "Put in front of Discord names in the game's chat.");
            IgnoredShouts = cfg.Bind("2 - Chat", "Ignored Shouts", "Mines have been reset!",
                "Shouts never sent to Discord, separated by | (case doesn't matter). For mods that announce things by making a player shout, like OreMines.");
            AllowDeathScreenshots = cfg.Bind("3 - Notifications", "Death Screenshots", true,
                "Attach the screenshot players with the mod send when they die.");
            AllowScreenshots = cfg.Bind("2 - Chat", "Allow Screenshots", true,
                "Players with the client part can send screenshots (screenshot key) to the chat channel.");

            NotifyWebhook = cfg.Bind("3 - Notifications", "Webhook URL", "", "Webhook for server and player notifications.");
            NotifyLaunch = cfg.Bind("3 - Notifications", "Server Launching", true, "The server process has started.");
            NotifyStarted = cfg.Bind("3 - Notifications", "Server Online", true, "The world has loaded and players can join.");
            NotifyStopping = cfg.Bind("3 - Notifications", "Server Stopping", true, "The server has begun shutting down.");
            NotifyStopped = cfg.Bind("3 - Notifications", "Server Offline", true, "The server has stopped.");
            NotifyJoin = cfg.Bind("3 - Notifications", "Player Joined", false, "A player joined the server.");
            NotifyLeave = cfg.Bind("3 - Notifications", "Player Left", false, "A player left the server.");
            NotifyDeaths = cfg.Bind("3 - Notifications", "Deaths", true,
                "Post DarwinAwards' death line (with a screenshot from players who have the client part).");
            DeathWebhook = cfg.Bind("3 - Notifications", "Death Webhook URL", "",
                "Webhook for the death feed. Empty turns the death feed off.");

            CommandsEnabled = cfg.Bind("4 - Commands", "Enabled", true, "Run commands typed in the command channel.");
            CommandChannelId = cfg.Bind("4 - Commands", "Channel ID", "", "The channel admins type commands in. Empty turns commands off.");
            CommandPrefix = cfg.Bind("4 - Commands", "Prefix", "!", "What every command starts with.");
            AdminIds = cfg.Bind("4 - Commands", "Admin User IDs", "",
                "Discord user IDs (not names) allowed to run admin commands, comma separated. Right-click a user > Copy User ID.");
        }
    }
}
