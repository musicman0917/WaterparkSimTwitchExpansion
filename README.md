# WaterparkSimTwitchExpansion

A BepInEx mod for Waterpark Simulator that lets Twitch chat spend points earned by watching the
stream to trigger chaos in the game ("Chat vs Streamer").

## Architecture

```
WaterparkSimTwitchExpansion/
├── Plugin.cs                    BaseUnityPlugin entry point - wires everything together
├── Core/
│   └── MainThreadDispatcher.cs  Marshals actions from Twitch's background thread onto Unity's main thread
├── Twitch/
│   ├── TwitchChatConnector.cs   Connects to a channel via TwitchLib, parses "!command args" messages
│   └── ChatCommand.cs           Parsed command data (username, action, args, roles)
├── Economy/
│   ├── PointsManager.cs         Per-viewer balances, passive income, JSON save/load
│   └── UserAccount.cs           Persisted per-user record
└── Chaos/
    ├── ChaosController.cs       The actual gameplay effects (YeetGuest, SpawnPoop, SabotageSlide)
    ├── ChaosCommandRouter.cs    Maps "!buy <action>" -> price check -> chaos effect
    └── IBreakable.cs            Optional hook so real slide components can define their own Break()
```

### Threading model

TwitchLib raises chat events on a background thread. `PointsManager` only touches plain
dictionaries, so it's safe to call directly from those events. `ChaosController` touches
`GameObject`/`Rigidbody`/etc., which Unity only allows from the main thread - so
`ChaosCommandRouter` queues the actual effect via `MainThreadDispatcher`, and `Plugin.Update()`
drains that queue every frame.

### Lifecycle wiring (`Plugin.cs`)

- **Awake()**: binds config (channel, OAuth token, prices, income rate), constructs
  `PointsManager` (and loads its save file), `ChaosController`, `ChaosCommandRouter`, then
  creates and connects `TwitchChatConnector`.
- **Update()**: drains `MainThreadDispatcher`, calls `PointsManager.Tick(Time.deltaTime)` for
  passive income, and autosaves the economy periodically.
- **OnDestroy()**: saves points and disconnects from Twitch.

## Setup

1. Install BepInEx into your Waterpark Simulator install once (via `.exe` install or manual copy).
2. Build: `dotnet build -p:GameDir="C:\Games\Waterpark Simulator"` (adjust the path and, if the
   game's data folder isn't `WaterparkSimulator_Data`, edit the `HintPath`s in the `.csproj`).
3. Copy `bin/Debug/net472/WaterparkSimTwitchExpansion.dll` into `BepInEx/plugins/`.
4. Launch the game once so BepInEx generates
   `BepInEx/config/com.musicman0917.waterparksimtwitchexpansion.cfg`, then fill in:
   - `Twitch.ChannelName` - the channel to join
   - `Twitch.BotUsername` / `Twitch.OAuthToken` - get a token at https://twitchapps.com/tmi/
     (keep it secret; the cfg file is git-ignored via the points save file convention, but the
     token itself is not encrypted, so don't commit your cfg)
   - `Prices.*` and `Economy.*` to taste
5. In-game objects need tags/prefabs matching what `ChaosController` expects:
   `Guest` (with `Rigidbody`), `Pool`, `Waterslide`, and a prefab at
   `Resources/Prefabs/Interactables/Poop`.

## Chat commands

- `!buy yeet` - launches a random guest into the air
- `!buy poop` - spawns poop above a random pool
- `!buy break` - sabotages a random waterslide
- `!balance` - logs the caller's point balance