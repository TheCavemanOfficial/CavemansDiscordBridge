# Changelog

## 1.1.0

- Scheduled restarts, replacing Tristan's ServerRestart. The settings are in the server config under `[5 - Restarts]`:
  - Daily restart times in UTC.
  - Warnings at any minutes you choose before each restart (`Warning Minutes`), in the middle of the screen, in game chat and on Discord.
  - Optional ServerCharacters maintenance mode before each restart.
  - Optionally saves, disconnects everyone and shuts the server down itself. Leave that off if your host restarts the server.
- On first start, the settings are copied from `org.tristan.serverrestart.cfg` if it exists. While ServerRestart is still installed, the bridge's restarts stay off so warnings don't come twice.
- Changes to the restart settings apply without restarting the server.
- New command `restart`: shows when the next restart is. It works in the command channel, the chat channel (answered there, not passed into the game) and in game chat. In game, players with the mod get the answer in their chat and nobody else sees the command; players without it get the answer top-left.
- Commands clean up after themselves: `[4 - Commands] Delete After Seconds` (60) later, the command message and the bot's answer are deleted (pinned messages and `help` are kept), and the admin panel's private replies go away. Every command, from any channel, the panel or game chat, is written to `BepInEx/CavemansDiscordBridge-Commands.log` (`Log File`).
- If a server has no saved panel, it takes over the panel the bot already posted in the channel instead of posting another.
- Join problem reports (`[7 - Join Problems] Channel ID`, posted by the bot, or a `Webhook URL`): when someone connects but never gets into the world, one report goes to your player-error channel: the reason (wrong version or password, banned, full, kicked by AzuAntiCheat, or gave up), with AzuAntiCheat's kick message turned into a clean "What to fix" list (one line per mod: what they have, what the server runs), and, for players with the client part, their mods compared with the server's (only theirs, missing, other version, same version but a different file), their game and BepInEx version, and the error and warning counts from their log with the mods behind them. With AzuAntiCheat, the report checks their mods against its own Whitelist and Greylist, the same rules it kicks by, and leads with what to fix: missing required mods, mods on neither list, and files that differ. Mod list, comparison, errors and AzuAntiCheat's full log lines (without colour codes) come as .txt files in a reply right under the report. Players can turn their snapshot off (`[Client] Join Snapshot`). AzuAntiCheat is optional: when it's installed it is only read from the log, never changed; without it, that part is simply left out.
- Admin panel (`[6 - Admin Panel]`): one message in a channel you choose, kept up to date with the server's status, who's online, uptime and the next restart. Its buttons restart the server (now, in 5 or in 15 minutes, with warnings), call that off again, put the server in maintenance mode and end it again (the world is saved, everyone but admins is sent out and only admins can join; ServerCharacters' maintenance mode when it's installed, the bridge's own check against `adminlist.txt` otherwise; the server keeps running, so a host like AMP doesn't restart it, and it lasts through restarts), skip the next scheduled restart (only when the bridge does the shutdown itself: with Shut Down off your host restarts on its own schedule, so that button is hidden), save the world, broadcast a message, and kick, ban or unban players. Only the Admin User IDs can use them; restart, stop, kick and ban ask to confirm first, and only the admin who clicked sees the replies. It shows 🟡 starting up while the server loads (switched at the same moment as the "starting up" post, before any mod loads), and 🔴 offline when it goes down.
- Every command now works from game chat too. Admin commands need the player on the game's own admin list (`adminlist.txt`); the player hosting the world counts as an admin. `help` in game lists only the commands that player may use. Answers are plain text, and lines that aren't commands stay ordinary chat.
- `players` is now admin-only. `restart` and `help` are the only commands open to everyone.
- Discord's own notices in the command or chat channel (such as "pinned a message") are no longer read as empty messages, so they don't log a false "turn on Message Content Intent" warning.

## 1.0.1

- New setting `[3 - Notifications] Is Server`. Turn it off on a test or single-player copy and the server starting, online, stopping and offline posts are skipped, so testing doesn't post them to your Discord. Chat, deaths and commands still work.

## 1.0.0

First release.

- Shouts go to a Discord channel under the character's name, and messages from that channel show up in game chat.
- Steam profile pictures on chat posts, with an optional Steam API key.
- Server status posts: starting up (sent before any mods load), online, shutting down and offline. Player joined and left posts are optional.
- Death feed using DarwinAwards' messages when it's installed, or the bridge's own when it isn't.
- Admin commands from a Discord channel: help, players, save, broadcast, kick, ban, unban, bans, pos, tp, give, status, skill and kill.
- Ignored Shouts list, to keep mod announcements out of Discord.
- Optional for players: death screenshots and a screenshot key (F10).
- Every feature has its own on/off switch.
- Works on a plain server with only BepInEx. No other mods required.
