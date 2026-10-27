# Caveman's Discord Bridge

Hook your Valheim server up to your Discord. Shouts show up in a Discord channel, and messages typed in that channel show up in game. You also get server status posts, a death feed and a handful of admin commands you can run from Discord.

It runs on the server. Players don't need to install anything. If they do install it, they get a few extras (death screenshots and a screenshot key), but nobody is left out without it.

Built for my own server, Caveheim, and shared because it might be useful to yours.

---

## What it does

**Chat, both ways**
- A shout in game (`/s hello`) is posted in your Discord chat channel under the character's name, with their Steam profile picture if you set up a Steam API key.
- Anything typed in that Discord channel shows up in game chat as `[D] Name: message`.
- Nobody in game can ping `@everyone` or anyone else in your Discord. Mentions are switched off on every post.
- Messages mods send through chat (like "Mines have been reset!") can be kept out of Discord.

**Server status**
- 🟡 Starting up: sent the moment the server process starts, before any mods load.
- 🟢 Online: the world has loaded and people can join.
- 🟠 Shutting down
- 🔴 Offline
- Optional: player joined and player left.

**Death feed**
- Uses DarwinAwards' death messages if you have that mod.
- Without it, the bridge spots deaths itself and posts its own line ("Bjorn has fallen in the Black Forest.").
- Players who have the mod installed send a screenshot from the moment they died, attached to the post.

**Admin commands from Discord**
- Kick, ban, teleport, give items, broadcast messages and more, from a channel of your choosing.
- Only Discord users you list by ID can use them.

**No extra libraries**
- No ServerSync, no JSON library, no other mods required. It only uses the game's own systems, so other mods updating won't break it.

---

## Installing

**Server (required)**

Install it with your mod manager, or copy the files by hand:

```
BepInEx/plugins/TheCaveman-Cavemans_Discord_Bridge/CavemansDiscordBridge.dll
BepInEx/patchers/TheCaveman-Cavemans_Discord_Bridge/CavemansDiscordBridge.Preloader.dll
```

The patcher's only job is the early "Starting up" message. It doesn't change any game code. You can leave it out if you don't care about that message.

Start the server once. It creates the settings file you'll fill in:

```
BepInEx/config/TheCaveman.CavemansDiscordBridge.Server.cfg
```

**Players (optional)**

Players can install the same mod to get death screenshots and the screenshot key. On a server without the bridge, it does nothing.

If your server runs AzuAntiCheat, put `CavemansDiscordBridge.dll` on the **greylist** (allowed, not required), so players without it can still join.

---

## Setting up Discord

This takes about ten minutes, and you only do it once.

### 1. Turn on Developer Mode

In Discord, go to **User Settings → Advanced** and switch on **Developer Mode**. You can now right-click channels and users to copy their IDs.

### 2. Make the bot

The bot is what reads your Discord channels. Without it, chat only goes one way (game to Discord) and commands won't work.

1. Go to the [Discord Developer Portal](https://discord.com/developers/applications) and click **New Application**. Give it your server's name.
2. Open **Bot** on the left.
3. Under **Privileged Gateway Intents**, switch on **Message Content Intent**. Without it, the bot sees every message as empty.
4. Click **Reset Token**, copy the token, and keep it somewhere safe. This is the bot's password.
5. Open **OAuth2** on the left and copy the **Client ID**.

### 3. Invite the bot to your Discord

Open this link with your Client ID swapped in:

```
https://discord.com/oauth2/authorize?client_id=YOUR_CLIENT_ID&scope=bot&permissions=68608
```

Pick your Discord and click **Authorize**. That gives the bot exactly three permissions: View Channels, Send Messages and Read Message History.

If your chat or command channels are private, open each channel's **Edit Channel → Permissions** and add the bot there too. The most common setup problem is a bot that can see one channel but not the other.

### 4. Make the webhooks

Webhooks are how the server posts into Discord. Make one in each channel you want posts in: **Edit Channel → Integrations → Webhooks → New Webhook**, then **Copy Webhook URL**.

| Webhook | What it posts | Suggested channel |
|---|---|---|
| Chat | Shouts and screenshots | `#server-chat` |
| Notifications | Starting up, online, shutting down, offline, joins, leaves | `#server-status` |
| Deaths | The death feed | `#deaths`, or the chat channel |

Give each webhook the name and picture you want on its posts. Chat posts use the player's name instead, and their Steam picture if you set that up.

Leaving a webhook empty switches that feature off. Nothing falls back to a different webhook.

### 5. Get a Steam API key (optional)

With a Steam Web API key, shouts and screenshots in Discord show the player's Steam profile picture. Without one, they use the chat webhook's picture. Only the server owner needs a key; players don't need anything.

1. Go to [steamcommunity.com/dev/apikey](https://steamcommunity.com/dev/apikey) and sign in with your Steam account.
2. For **Domain Name**, type anything. Your website or your server's name both work; Steam doesn't check it.
3. Tick the box to agree to the terms and click **Register**.
4. Copy the **Key**. It's a long string of letters and numbers.

Steam only gives keys to accounts that have spent at least $5 on Steam. If you made one before for another tool, the same key works here. You can see and revoke it on that same page.

Treat the key like a password: it belongs in the server settings file and nowhere else. Players with a private Steam profile get the webhook's picture instead.

### 6. Copy your IDs

Right-click and **Copy ID** on:
- the chat channel,
- the channel you want admins to type commands in (a private one is best),
- yourself and anyone else who should run admin commands.

---

## The server settings file

`BepInEx/config/TheCaveman.CavemansDiscordBridge.Server.cfg`

**This file holds your bot token, webhooks and Steam key. Keep it on the server.** Don't put it in a modpack, a public repo or a screenshot. The bridge never sends it to players.

### 1 - Bot

| Setting | Default | |
|---|---|---|
| Bot Token | *(empty)* | Your bot's token. Needed for Discord → game chat and for commands. |
| Poll Seconds | 2 | How often the server checks Discord for new messages (1 to 30). |
| Steam API Key | *(empty)* | Optional. See step 5 above. |
| Steam Avatars | true | Shows players' Steam profile pictures on their posts. Needs the key. |

### 2 - Chat

| Setting | Default | |
|---|---|---|
| Enabled | true | The whole chat bridge. Off switches both directions off. |
| Shouts To Discord | true | In-game shouts are posted in Discord. |
| Discord To Game | true | Messages from the Discord chat channel show up in game. |
| Channel ID | *(empty)* | The Discord chat channel to read from. |
| Webhook URL | *(empty)* | The chat webhook. Empty means shouts aren't posted. |
| In-game Prefix | [D] | Put in front of Discord names in game chat. |
| Ignored Shouts | Mines have been reset! | Shouts never sent to Discord, separated by `\|`. Case doesn't matter. |
| Allow Screenshots | true | Lets players with the mod send screenshots to the chat channel. |

### 3 - Notifications

| Setting | Default | |
|---|---|---|
| Webhook URL | *(empty)* | The notifications webhook. |
| Is Server | true | Turn off on a test or single-player copy of your setup: the server starting, online, stopping and offline posts are skipped. |
| Server Launching | true | 🟡 Starting up |
| Server Online | true | 🟢 Online |
| Server Stopping | true | 🟠 Shutting down |
| Server Offline | true | 🔴 Offline |
| Player Joined | false | When a player spawns in. |
| Player Left | false | When a player disconnects. |
| Deaths | true | The death feed (needs a death webhook). |
| Death Screenshots | true | Attach the screenshot players with the mod send when they die. |
| Death Webhook URL | *(empty)* | The death webhook. Empty means no death feed. |

### 4 - Commands

| Setting | Default | |
|---|---|---|
| Enabled | true | Commands on or off. |
| Channel ID | *(empty)* | The channel commands are typed in. Empty turns commands off. |
| Prefix | ! | What every command starts with. |
| Admin User IDs | *(empty)* | Discord user IDs, comma separated. Use IDs, not names: names can be copied, IDs can't. |

---

## Commands

Type these in your command channel. `help` and `players` work for anyone who can see the channel. Everything else is for the admins you listed.

| Command | What it does |
|---|---|
| `!help` | Lists every command. |
| `!players` | Who's online, with their biome and coordinates. |
| `!save` | Saves the world and every character. |
| `!broadcast <message>` | Shows a message in the middle of everyone's screen. |
| `!kick <player>` | Kicks a player. |
| `!ban <player or Steam ID>` | Bans a player. |
| `!unban <player or Steam ID>` | Lifts a ban. |
| `!bans` | Lists banned players. |
| `!pos <player>` | Where a player is. |
| `!tp <player> <x> <z>` | Teleports a player to coordinates. |
| `!tp <player> <other player>` | Teleports a player to someone else. |
| `!give <player> <item> [amount] [quality]` | Drops items at a player's feet. Use the item's prefab name, like `SwordIron` or `Wood`. |
| `!status <player> <effect>` | Gives a player a status effect, like `Rested`. |
| `!skill <player> <skill> <levels>` | Raises a skill. The player needs the mod installed for this one. |
| `!kill <player>` | Kills a player. |

Player names don't have to be exact. `!pos cave` finds "Caveman" as long as no one else's name starts with "cave". Names with spaces go in quotes: `!kick "Old Bjorn"`.

---

## For players with the mod

| Setting | Default | |
|---|---|---|
| Death Screenshots | true | Sends a screenshot when you die, attached to the death post. |
| Screenshot Key Enabled | true | Lets the screenshot key work at all. |
| Screenshot Key | F10 | Sends a screenshot to the server's Discord chat channel. Once every 10 seconds at most. |

These are in `BepInEx/config/TheCaveman.CavemansDiscordBridge.cfg` and only affect you. They only do anything on servers running the bridge, and the server owner can switch screenshots off for everyone.

Shouts from players with the mod also reach Discord when they're alone on the server. Without the mod, a shout from a lone player never passes through the server, so it can't be relayed.

---

## Good to know

- **Only shouts are relayed, not normal chat.** Normal chat only reaches players standing nearby and never goes through the server, so there's nothing to relay. Shouts go to everyone.
- **Old Discord messages aren't replayed.** When the server starts, it only picks up messages sent from then on.
- **Testing on a copy of your setup?** Set `Is Server = false` in that copy's server settings, so your test loads don't post "server online" and "offline" to your Discord.
- **Hosting from your own game works too.** The host counts as a player for every command except kick and ban.
- **DarwinAwards** is optional. With it, deaths get its messages. Without it, the bridge writes its own.
- **ServerRestart** isn't needed, but it has its own Discord webhook setting. Point it at your notifications webhook and its restart warnings land next to the bridge's status posts.
- **Mods that shout as a player** (OreMines does this for its reset message) look exactly like a real player to the server. Add their messages to **Ignored Shouts**.

---

## Troubleshooting

Everything the bridge does is in `BepInEx/LogOutput.log`, on lines starting with `[Caveman's Discord Bridge]`.

| You see | What it means |
|---|---|
| `Listening to channel …` | The bot can read that channel. You want one of these for chat and one for commands. |
| `Can't read channel … (403): Missing Access` | The bot can't see the channel. Add it in the channel's permissions. |
| `Can't read channel … (401)` | The bot token is wrong or was reset. |
| `A message … came through empty` | Message Content Intent is off. Switch it on in the Developer Portal. |
| `Discord refused a post (404)` | The webhook was deleted. Make a new one and paste its URL. |

**Commands don't answer:** check that the command channel ID is right, the bot can see that channel, and your user ID is in the admin list (for admin commands).

**Shouts don't show in Discord:** check the chat webhook. Shouts from a player who is alone on the server and doesn't have the mod can't be seen by the server.

---

## Links

- Source and issues: [github.com/TheCavemanOfficial/CavemansDiscordBridge](https://github.com/TheCavemanOfficial/CavemansDiscordBridge)
