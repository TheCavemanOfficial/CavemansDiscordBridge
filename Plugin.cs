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
    [BepInDependency(JoinReports.AzuAntiCheatGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public class DiscordBridgePlugin : BaseUnityPlugin
    {
        public const string ModGUID = "TheCaveman.CavemansDiscordBridge";
        public const string ModName = "Caveman's Discord Bridge";
        public const string ModVersion = "1.1.0";

        internal static DiscordBridgePlugin Instance;
        internal static ManualLogSource Log;

        // Work the background (HTTP) threads hand back to Unity's main thread
        private static readonly ConcurrentQueue<Action> s_mainThread = new ConcurrentQueue<Action>();
        internal static void RunOnMainThread(Action action) => s_mainThread.Enqueue(action);

        // --- Client settings (this file ships in the client pack: nothing secret in here) ---
        internal static ConfigEntry<bool> DeathScreenshots;
        internal static ConfigEntry<bool> ScreenshotKeyEnabled;
        internal static ConfigEntry<KeyboardShortcut> ScreenshotKey;
        internal static ConfigEntry<bool> SendJoinSnapshot;

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

            SendJoinSnapshot = Config.Bind("Client", "Join Snapshot", true,
                "When you connect to a server running this mod, your mod list (with file fingerprints), game version and the error and warning counts from your log go to that server. It only reaches Discord if you can't get in, so the admins can see why.");

            new Harmony(ModGUID).PatchAll(typeof(DiscordBridgePlugin).Assembly);

            // A dedicated server has no graphics device; it announces "launching" before the world even loads
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                EnsureServerSettings();
                // The preloader (patchers/) normally sent it already, long before any mod loaded
                if (!(AppDomain.CurrentDomain.GetData("CavemansDiscordBridge.LaunchSent") is bool sent && sent))
                    Notifications.ServerLaunching();
                // The panel too, unless the preloader already switched it
                if (Server.IsServer.Value && !(AppDomain.CurrentDomain.GetData("CavemansDiscordBridge.PanelStartingSent") is bool panelSent && panelSent))
                    AdminPanel.Starting();
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
            Restarts.Start(Server.File);
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
            Restarts.Update();
            AdminPanel.Update();
            JoinReports.Update();
        }

        // No network work here: by now Unity and Steam are torn down, and a late web request crashed the game on quit.
        // "Offline" goes out when the world closes instead (ZNet.OnDestroy).
        private void OnDestroy()
        {
            AdminPanel.Stop();
            Discord.Stop();
        }
    }

    internal class ServerSettings
    {
        public readonly ConfigFile File;
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
        public readonly ConfigEntry<bool> IsServer;
        public readonly ConfigEntry<bool> NotifyLaunch, NotifyStarted, NotifyStopping, NotifyStopped;
        public readonly ConfigEntry<bool> NotifyJoin, NotifyLeave;
        public readonly ConfigEntry<bool> NotifyDeaths;
        public readonly ConfigEntry<string> DeathWebhook;

        public readonly ConfigEntry<string> CommandChannelId;
        public readonly ConfigEntry<string> CommandPrefix;
        public readonly ConfigEntry<string> AdminIds;
        public readonly ConfigEntry<int> CommandDeleteSeconds;
        public readonly ConfigEntry<string> CommandLogFile;

        public readonly ConfigEntry<bool> RestartsEnabled, RestartShutDown, RestartToChat, RestartMaintenance, RestartImported;
        public readonly ConfigEntry<string> RestartSchedule, RestartWarnings, RestartMessage;
        public readonly ConfigEntry<string> RestartCenterFormat, RestartChatName, RestartChatFormat;
        public readonly ConfigEntry<string> RestartWebhook, RestartDiscordName, RestartDiscordFormat;
        public readonly ConfigEntry<int> RestartMaintenanceMinutes, RestartLogPeriod;

        public readonly ConfigEntry<string> JoinReportWebhook, JoinReportChannelId;
        public readonly ConfigEntry<bool> JoinReportWrongPassword;

        public readonly ConfigEntry<bool> PanelEnabled, MaintenanceOn;
        public readonly ConfigEntry<string> PanelChannelId, PanelMessageId;
        public readonly ConfigEntry<float> PanelUpdateSeconds;

        public ServerSettings(ConfigFile cfg)
        {
            File = cfg;
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
            IsServer = cfg.Bind("3 - Notifications", "Is Server", true,
                "Turn off on a test or single-player copy: the server starting, online, stopping and offline posts are skipped, so testing doesn't post them to your Discord. Chat, deaths and commands still work.");
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
            CommandDeleteSeconds = cfg.Bind("4 - Commands", "Delete After Seconds", 60,
                new ConfigDescription("Seconds after a command before the command message and the bot's answer are deleted, and the admin panel's private replies go away. Pinned messages and help are kept. 0 keeps everything. Deleting people's messages needs Manage Messages.", new AcceptableValueRange<int>(0, 840)));
            CommandLogFile = cfg.Bind("4 - Commands", "Log File", "CavemansDiscordBridge-Commands.log",
                "Every command (command channel, admin panel, game chat, chat channel) is written to this file, in the BepInEx folder unless it's a full path. Empty turns the log off.");

            const string R = "5 - Restarts";
            RestartsEnabled = cfg.Bind(R, "Enabled", true, "Scheduled restarts and their warnings. Stays off while Tristan's ServerRestart is installed.");
            RestartSchedule = cfg.Bind(R, "Schedule (UTC)", "04:00,16:00",
                "Restart times every day, in UTC, separated by commas (04:00 UTC is 11 PM US Central in winter).");
            RestartShutDown = cfg.Bind(R, "Shut Down", false,
                "Save, disconnect everyone and quit the server at the restart time. Leave off when your host restarts the server on the same schedule (it starts it again; this mod only warns).");
            RestartWarnings = cfg.Bind(R, "Warning Minutes", "60,30,10,5,4,3,2,1",
                "When to warn, in minutes before each restart, separated by commas. Any numbers work (120,45,15,1).");
            RestartMessage = cfg.Bind(R, "Warning Message", "Server restart in {time}",
                "The warning. {time} becomes \"1 hour\", \"30 minutes\", \"1 hour 30 minutes\" and so on.");
            RestartCenterFormat = cfg.Bind(R, "Screen Format", "{0}", "The message in the middle of everyone's screen; {0} is the warning. Empty skips it.");
            RestartToChat = cfg.Bind(R, "Send To Chat", true, "Also put the warning in game chat, as a shout.");
            RestartChatName = cfg.Bind(R, "Chat Name", "Restart", "Who the chat warning comes from.");
            RestartChatFormat = cfg.Bind(R, "Chat Format", "{0}", "The chat warning; {0} is the warning.");
            RestartWebhook = cfg.Bind(R, "Webhook URL", "", "Webhook the warnings are posted to. Empty means no Discord warnings.");
            RestartDiscordName = cfg.Bind(R, "Discord Name", "Restart", "The name the Discord warnings are posted under.");
            RestartDiscordFormat = cfg.Bind(R, "Discord Format", "⏰ **{0}**", "The Discord warning; {0} is the warning.");
            RestartMaintenance = cfg.Bind(R, "Maintenance Mode", false,
                "Put ServerCharacters in maintenance mode (only admins can join) shortly before each restart. Lifted again when the server starts.");
            RestartMaintenanceMinutes = cfg.Bind(R, "Maintenance Minutes", 7,
                new ConfigDescription("How many minutes before the restart maintenance mode starts.", new AcceptableValueRange<int>(0, 120)));
            RestartLogPeriod = cfg.Bind(R, "Log Period Seconds", 0,
                new ConfigDescription("Write the next restart time to the log this often. 0 = only when it changes.", new AcceptableValueRange<int>(0, 86400)));
            const string P = "6 - Admin Panel";
            PanelEnabled = cfg.Bind(P, "Enabled", true, "A live panel in a Discord channel: status, players, next restart, and buttons for restart, stop, save, broadcast, kick, ban and unban. Needs the bot token and a Channel ID.");
            PanelChannelId = cfg.Bind(P, "Channel ID", "",
                "The channel the panel lives in. Use a private admin channel: everyone who can see it sees the buttons (only Admin User IDs can use them). Empty turns the panel off.");
            PanelUpdateSeconds = cfg.Bind(P, "Update Seconds", 60f,
                new ConfigDescription("How often the panel refreshes on its own. It also refreshes soon after players come or go.", new AcceptableValueRange<float>(15f, 600f)));
            MaintenanceOn = cfg.Bind(P, "Maintenance", false,
                "Set by the panel's Maintenance mode button: only admins can join while it's on (ServerCharacters' maintenance mode when it's installed). Set false here to end it by hand.");
            PanelMessageId = cfg.Bind(P, "Message ID", "",
                "Filled in by the mod: the panel message it keeps editing. Clear it (or delete the message in Discord) to post a fresh panel.");

            const string J = "7 - Join Problems";
            JoinReportChannelId = cfg.Bind(J, "Channel ID", "",
                "The player-error channel. When someone connects but never gets into the world, the bot posts a report here: the reason, AzuAntiCheat's lines about them (when it's installed), and (with the client part) their mods compared with the server's, their game version and their log's errors. Needs the bot token. Empty (and no Webhook URL) turns reports off.");
            JoinReportWebhook = cfg.Bind(J, "Webhook URL", "",
                "Instead of the Channel ID: post the reports through this webhook. Used only when Channel ID is empty.");
            JoinReportWrongPassword = cfg.Bind(J, "Report Wrong Password", true, "Also report players who typed the wrong server password.");

            RestartImported = cfg.Bind(R, "Imported From ServerRestart", false,
                "Set once the settings above were copied from org.tristan.serverrestart.cfg. Set false to copy them again on the next start.");
        }
    }
}
