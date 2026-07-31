# WaterparkSimTwitchExpansion

A BepInEx mod for Waterpark Simulator that lets Twitch chat spend points earned by watching the
stream to trigger chaos in the game ("Chat vs Streamer").

## Architecture

```
WaterparkSimTwitchExpansion/
├── Plugin.cs                    BasePlugin (IL2CPP) entry point - wires everything together
├── Core/
│   ├── MainThreadDispatcher.cs  Marshals actions from Twitch's background thread onto Unity's main thread
│   └── UpdatePump.cs            Injected MonoBehaviour that gives BasePlugin a per-frame Update()
├── Twitch/
│   ├── TwitchChatConnector.cs   Connects to a channel via TwitchLib, parses "!command args" messages
│   └── ChatCommand.cs           Parsed command data (username, action, args, roles)
├── Economy/
│   ├── PointsManager.cs         Per-viewer balances, passive income, JSON save/load
│   └── UserAccount.cs           Persisted per-user record
└── Chaos/
    ├── ChaosController.cs       The actual gameplay effects (YeetGuest, SpawnPoop, SabotageSlide)
    └── ChaosCommandRouter.cs    Maps "!buy <action>" -> price check -> chaos effect
```

Waterpark Simulator is an **IL2CPP** build (confirmed via `GameAssembly.dll` at the install root
and no `Assembly-CSharp.dll` under `WaterparkSimulator_Data\Managed`), so this targets
**BepInEx 6.x (IL2CPP)**, not the more commonly-documented BepInEx 5.x/Mono setup.

### Threading model

TwitchLib raises chat events on a background thread. `PointsManager` only touches plain
dictionaries, so it's safe to call directly from those events. `ChaosController` touches
`GameObject`/`Rigidbody`/etc., which Unity only allows from the main thread - so
`ChaosCommandRouter` queues the actual effect via `MainThreadDispatcher`, and the injected
`UpdatePump` drains that queue every frame.

### Lifecycle wiring (`Plugin.cs`)

BepInEx.Unity.IL2CPP plugins derive from `BasePlugin`, not `BaseUnityPlugin` - there's no
`Awake`/`Update`/`OnDestroy` MonoBehaviour lifecycle to hook directly:

- **Load()**: runs once at startup (the IL2CPP equivalent of `Awake()`). Binds config, constructs
  `PointsManager` (and loads its save file), `ChaosController`, `ChaosCommandRouter`, injects
  `UpdatePump` via `AddComponent<UpdatePump>()` for a per-frame tick, hooks `Application.quitting`
  to save on exit, then creates and connects `TwitchChatConnector`.
- **Tick()** (called every frame by `UpdatePump.OnUpdate`): drains `MainThreadDispatcher`, calls
  `PointsManager.Tick(Time.deltaTime)` for passive income, and autosaves the economy periodically.
- **Application.quitting**: saves points and disconnects from Twitch.

## Setup

1. Install the **IL2CPP** build of BepInEx 6.x into the game folder (same folder as
   `WaterparkSimulator.exe`), then **launch the game once** and let it sit at the main menu for a
   bit before closing it - this is what generates `BepInEx\interop\UnityEngine*.dll` from the
   game's IL2CPP metadata. Skipping this step means the project won't have anything to build against.
2. Build: `dotnet build -p:GameDir="F:\SteamLibrary\steamapps\common\WaterPark Simulator"`
   (adjust the path if yours differs).
3. Copy `bin/Debug/net6.0/WaterparkSimTwitchExpansion.dll` into `BepInEx/plugins/`.
4. Launch the game once so BepInEx generates
   `BepInEx/config/com.musicman0917.waterparksimtwitchexpansion.cfg`, then fill in:
   - `Twitch.ChannelName` - the channel to join
   - `Twitch.BotUsername` / `Twitch.OAuthToken` - get a token at https://twitchapps.com/tmi/
     (keep it secret; don't commit your cfg file)
   - `Prices.*` and `Economy.*` to taste
5. In-game objects need tags/prefabs matching what `ChaosController` expects:
   `Guest` (with `Rigidbody`), `Pool`, `Waterslide`, and a prefab at
   `Resources/Prefabs/Interactables/Poop`. These are placeholders based on the request that
   scaffolded this mod - check the actual tags/prefab paths used by Waterpark Simulator's own
   assets (e.g. with a decompiler or by inspecting the scene at runtime) and adjust
   `ChaosController.cs`'s constants accordingly.

## Chat commands

- `!buy yeet` - launches a random guest into the air
- `!buy poop` - spawns poop above a random pool
- `!buy break` - sabotages a random waterslide
- `!balance` - logs the caller's point balance