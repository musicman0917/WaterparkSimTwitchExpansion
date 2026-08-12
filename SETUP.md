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
   - `ClientId` - also shown on that same token page. Optional, but without it the overlay can't
     show viewers' profile pictures (see step 4) - it'll still work, just icon-only.
3. Save the file and launch the game.

Your bot should join your channel's chat. Viewers can now use:

- `!buy yeet` - launches a random guest (in view of the camera) into the air
- `!buy poop` - spawns poop above a random pool
- `!buy break` - sabotages a random waterslide
- `!buy ragdoll` - flings the streamer's own character around (**confirmed working live**)
- `!buy vomit` - makes a random visible guest throw up (**confirmed working live**)
- `!buy pee` - makes a random visible guest pee (unverified - see below)
- `!buy trash` - makes a random visible guest litter (unverified, same caveat)
- `!buy invert` - flips the game's own "Invert Y Axis (Player)" setting for a while (**confirmed
  working live**)
- `!buy nojump` - disables the streamer's jump for a while (**confirmed working live**)
- `!buy drop` - makes the streamer drop whatever item they're holding (**confirmed working
  live**)
- `!buy addmoney` / `!buy removemoney` - adds/drains the park's own in-game money, not your
  Twitch points (unverified - see below)
- `!balance` - check your point balance (replies right in chat)
- `!waterparkcommands` - lists every `!buy` action and its point cost in chat
- `!give <username> <amount>` - for the streamer/moderators only. Hands out points to a viewer,
  e.g. for a giveaway or to fix a balance.
- `!startpoll` - for the streamer/moderators only. Starts a free chat vote on demand (see below) -
  polls also start on their own every 20 minutes by default.

`nojump`/`invert`/`drop`/`ragdoll`/`vomit` are all now confirmed working live - `nojump` took two
fixes to get there (the game reads jump through Unity's new Input System rather than the legacy
one the mod originally patched, and the first attempt at patching that turned out to be a no-op
too, likely inlined away by IL2Cpp), `invert` flips the game's own Settings-menu "Invert Y Axis"
toggle directly instead of patching anything, `drop` calls the game's own item-drop method
directly instead of simulating a keypress, and `ragdoll` calls the game's own
`PlayerRagdollSystem.EnableRagdollTemp()` directly instead of a raw physics force (which did
nothing against the `CharacterController`-driven player) - its launch force also got lowered after
the original default flung the streamer over map barriers. `pee`/`trash` use the same approach as
`vomit` (calling the game's own guest AI behavior directly) but haven't been confirmed against a
real build yet. `addmoney`/`removemoney` are new too, calling the game's real `FinanceSystem`
directly - also unverified.

Points are earned automatically just by chatting/watching (default: 10 points every 60 seconds
to anyone active in chat). Every successful redemption gets a confirmation reply in chat, so the
viewer who triggered it knows it worked.

### Starting balances and other ways to earn points

The first time someone's ever seen in your chat, they get a starting balance so they don't have
to wait around before they can afford anything: **250 points** normally, **500 points** if they
follow your channel, or **1000 points** if they're a VIP, moderator, or you (the broadcaster) -
highest tier wins. This never changes anyone's existing balance - it's only for brand-new viewers
going forward.

The follower tier needs a bit of one-time setup, since Twitch's chat connection can't see follow
status on its own:

1. Register an app at [dev.twitch.tv/console](https://dev.twitch.tv/console) - free, just needs
   your Twitch account. Leave **Client Type** as **Confidential** (the default), and add
   `http://localhost:3000` as an **OAuth Redirect URL**.
2. Grab your app's **Client ID** from that same page.
3. Get a token: open this in a browser (swap in your Client ID), **while logged into your own
   broadcaster account, not the bot's**:
   ```
   https://id.twitch.tv/oauth2/authorize?response_type=token&client_id=YOUR_CLIENT_ID&redirect_uri=http://localhost:3000&scope=moderator:read:followers
   ```
   Approve it. You'll land on a `localhost:3000` page that fails to load - that's expected, nothing's
   actually running there. Copy the `access_token=...` value out of the browser's address bar.
4. Put both values in the config's `[Twitch]` section as `FollowerCheckClientId` and
   `FollowerCheckOAuthToken`. These are separate from `ClientId`/`OAuthToken` above (which stay
   tied to your bot account). Leave either blank and everyone just gets the plain 250-point
   starting balance instead - no errors, the follower tier just doesn't apply.

On top of the starting balance, these are one-time bonuses whenever they happen:

- **Subscribing (or resubbing)** - 500 points × their tier (Tier 1/2/3, Prime counts as Tier 1).
- **Gifting subs** - 500 points × tier, credited to whoever gifted, once per sub (gifting 5 at
  once pays out 5 times).
- **Cheering bits** - 1 point per bit.

All of these amounts are adjustable in the config's `[Points]` section
(`StartingBalanceViewer`, `StartingBalanceFollower`, `StartingBalanceVipMod`,
`SubscriberPointsPerTier`, `GiftedSubPointsPerTier`, `BitsToPointsRatio`). Charity donations
aren't wired up yet either - that needs more research into how Twitch actually reports those
before it can be built.

**Unverified against a real build** - same caution as everything else new in this mod, this
hasn't been confirmed live yet.

### Chat vote polls

Separately from spending points, chat can also vote together on something crazy - similar to
games like 7 Days to Die letting chat vote on a "blood moon" mutator. Every so often (or whenever
a mod runs `!startpoll`), the bot posts a few numbered options in chat:

```
CHAOS VOTE! Type a number to vote (free, 45s): 1) yeeted a guest   2) made a guest throw up
```

Just type `1` or `2` in chat - no `!`, no points needed. Whichever option gets the most
votes happens automatically when the timer runs out. Adjust how often polls happen, how long
voting lasts, and how many options are offered in the config's `[Poll]` section
(`AutoIntervalMinutes`, `DurationSeconds`, `OptionCount`).

## 4. Show redemptions on stream (OBS overlay)

The mod runs a small local web page showing who caused each redemption, meant to be added as a
**Browser Source** in OBS (or Streamlabs, etc.) rather than relying on anything drawn in-game:

1. In OBS, add a new **Browser Source** to your scene.
2. Set the URL to `http://localhost:9412/overlay.html` (just change the port if you changed
   `Overlay.Port` in the config).
3. Size and position it to cover your whole stream canvas (e.g. 1920x1080 at 0,0) rather than
   just a small corner - it's fully transparent, so it won't cover anything, but it now draws in
   two different spots: `!buy` toasts bottom-left, and chat vote polls top-center. A smaller
   cropped source (e.g. just a bottom corner) will hide whichever one falls outside it. Leave
   **"Shutdown source when not visible"** unchecked.

It's transparent, so it composites over your gameplay capture without any extra setup, and shows
a little animated waterpark-themed toast for a few seconds every time someone spends points -
with that viewer's Twitch profile picture if you filled in `ClientId` in step 3, or just an icon
per action if not. It also shows a live chaos-vote-poll widget (numbered options, a countdown, and
vote counts that update in real time) whenever a poll is running - see "Chat vote polls" earlier
in this guide. If you'd rather not run it, set `Overlay.Enabled` to `false` in the config.

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
- **OBS Browser Source shows blank/won't load** - check `BepInEx\LogOutput.log` for a line like
  `OverlayServer: failed to start` - if present, something else on your PC is already using that
  port; change `Overlay.Port` in the config to a different number (and update the Browser Source's
  URL to match) and restart the game.
- **Still stuck?** - open an issue on the GitHub repo with your `BepInEx\LogOutput.log` attached.
