# WaterparkSimTwitchExpansion - Setup Guide

Let your Twitch chat cause chaos in Waterpark Simulator. Viewers earn points just by watching,
then spend them to launch guests into the air, spawn poop in your pools, or break a waterslide.

## What you need

- Waterpark Simulator on Steam
- A Twitch account for your bot to chat from (this can be your own streaming account)

## 1. Install BepInEx (one-time, shared by every Waterpark Simulator mod)

1. Download **"BepInEx IL2CPP for Waterpark Simulator"** from Nexus Mods:
   https://www.nexusmods.com/waterparksimulator/mods/62
2. Extract that zip, then copy its `winhttp` file and `BepInEx` folder into your Waterpark
   Simulator install folder - the same folder as `WaterparkSimulator.exe` (in Steam: right-click
   the game > Manage > Browse local files).
3. Launch the game once and let it sit for a minute or two before closing it. This is BepInEx
   doing one-time setup work - it can take a while the first time, that's normal.

## 2. Install this mod

1. Download the latest `WaterparkSimTwitchExpansion-vX.Y.Z.zip` from the Releases page.
2. Extract it into the same game folder as step 1 (it merges into the `BepInEx` folder that's
   already there - don't extract it somewhere else first).
3. Launch the game once, then close it again. This generates a config file at:
   `BepInEx\config\com.musicman0917.waterparksimtwitchexpansion.cfg`

## 3. Connect your Twitch account

1. Get an OAuth token for your bot account at https://twitchtokengenerator.com/ - log in as
   whichever Twitch account you want the bot to chat from (this can be your own account), and
   make sure the **`chat:read`** and **`chat:edit`** checkboxes are ticked before generating the
   token.
2. Open `BepInEx\config\com.musicman0917.waterparksimtwitchexpansion.cfg` in Notepad and fill in:
   - `ChannelName` - your Twitch channel (the one people watch)
   - `BotUsername` - the Twitch account from step 1
   - `OAuthToken` - the token from step 1 (starts with `oauth:`)
3. Save the file and launch the game.

Your bot should join your channel's chat. Viewers can now use:

- `!buy yeet` - launches a random guest into the air
- `!buy poop` - spawns poop above a random pool
- `!buy break` - sabotages a random waterslide
- `!balance` - check your point balance

Points are earned automatically just by chatting/watching (default: 10 points every 60 seconds
to anyone active in chat).

## Adjusting prices / income rate

Everything is configurable in that same `.cfg` file - point costs for each chaos action, how
many points chatters earn and how often, and how often progress is saved. Open it in Notepad,
change a value, save, and restart the game.

## Troubleshooting

- **Nothing seems different after launching the game** - make sure BepInEx installed correctly
  first (step 1). After running the game once you should see a `BepInEx\LogOutput.log` file in
  your game folder - if it's missing, BepInEx never loaded.
- **Bot doesn't join chat** - double-check `OAuthToken` starts with `oauth:` and hasn't expired
  (regenerate it at the link above if unsure), and that `BotUsername`/`ChannelName` are spelled
  correctly (no `#`, no spaces).
- **Still stuck?** - open an issue on the GitHub repo with your `BepInEx\LogOutput.log` attached.
