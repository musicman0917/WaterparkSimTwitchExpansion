# WaterparkSimTwitchExpansion

A BepInEx mod for Waterpark Simulator that lets Twitch chat spend points earned by watching the
stream to trigger chaos in the game ("Chat vs Streamer").

**Viewer-facing command list**: [`docs/index.html`](docs/index.html) is a standalone page listing
every `!buy` command and its cost, meant to be linked in the stream panel/description so viewers
don't have to ask "what commands are there?" in chat. It's plain, dependency-free HTML/CSS (no
build step) so it can be hosted directly via GitHub Pages:

1. On GitHub: **Settings → Pages → Source → Deploy from a branch**, pick the branch this merges
   into (usually `main`) and folder **`/docs`**, then **Save**.
2. GitHub publishes it at `https://<owner>.github.io/<repo>/` (for this repo:
   `https://musicman0917.github.io/WaterparkSimTwitchExpansion/`) within a minute or two.
3. Whenever the commands/prices change, update `docs/index.html` to match - it's a static
   snapshot, not generated from the mod's actual config, so it can drift if forgotten.

## Architecture

```
WaterparkSimTwitchExpansion/
├── Plugin.cs                    BasePlugin (IL2CPP) entry point - wires everything together
├── Core/
│   ├── MainThreadDispatcher.cs  Marshals actions from Twitch's background thread onto Unity's main thread
│   ├── UpdatePump.cs            Injected MonoBehaviour that gives BasePlugin a per-frame Update()
│   ├── OnScreenNotifier.cs      Fallback in-game OnGUI text for redemptions (local testing only)
│   ├── OverlayServer.cs         Local web server (HttpListener) for the OBS browser overlay
│   └── OverlayHtml.cs           The overlay page itself (waterpark-themed toasts via SSE)
├── Twitch/
│   ├── TwitchChatConnector.cs   Connects to a channel via TwitchLib, parses "!command args" messages
│   ├── ChatCommand.cs           Parsed command data (username, action, args, roles)
│   └── TwitchAvatarProvider.cs  Looks up a chatter's profile picture (Helix API) for the overlay
├── Economy/
│   ├── PointsManager.cs         Per-viewer balances, passive income, JSON save/load
│   └── UserAccount.cs           Persisted per-user record
└── Chaos/
    ├── ChaosController.cs       The actual gameplay effects (YeetGuest, SpawnPoop, SabotageSlide)
    ├── ChaosCommandRouter.cs    Maps "!buy <action>" -> price check -> chaos effect
    └── ChaosPollManager.cs      Free chat-vote polls ("!startpoll" + automatic timer) - see below
```

`Core/OnScreenNotifier.cs` is another injected `MonoBehaviour` (like `UpdatePump`) that draws a
short-lived line of text (via `OnGUI`) for every successful `!buy` redemption - a fallback that
only the streamer's own screen sees, kept mostly for local testing without OBS running.
`ChaosCommandRouter` also posts the same confirmation back to chat via
`TwitchChatConnector.SendMessage` (wired up in `Plugin.Load()` as
`_router.SendChatMessage = _twitch.SendMessage`). Both only fire once the chaos effect actually
succeeds (e.g. no message if `!buy yeet` finds no guest in camera view), so chat never gets a
false "success" for something that silently no-op'd.

**The recommended way to show who caused chaos is the OBS overlay**, not the in-game text:
`Core/OverlayServer.cs` runs a small `System.Net.HttpListener` web server (no ASP.NET/Kestrel or
new NuGet dependency - `HttpListener` ships in the net6.0 shared framework) serving a
waterpark-themed page (`Core/OverlayHtml.cs`) at `http://localhost:<port>/overlay.html`
(`Overlay.Port` in the config, default `9412`). `ChaosCommandRouter` pushes a Server-Sent Event to
it for every successful redemption, and the page animates in a little splash/wave-styled toast
("DisplayName yeeted a guest! (-100 pts)", or "DisplayName just yeeted NPC <name>! (-100 pts)" for
`!buy yeet` specifically - see below) that fades out after a few seconds, bottom-left. It also
draws a live chaos-vote-poll widget, top-center, whenever a poll is running (see "Chat vote polls"
below) - so the Browser Source needs to be sized/positioned to cover both spots (e.g. the whole
stream canvas), not just a small corner, or one of the two will render outside it and never be
seen. Point an OBS **Browser Source** at that URL (leave "Shutdown source when not visible"
unchecked so it keeps listening while the scene isn't live) and it composites over anything,
independent of whatever capture method you use for the game itself - unlike the in-game `OnGUI`
text, which depends on it actually being included in that capture. Binding specifically to the
`localhost` hostname (not `+`/`*`/a real hostname) means `HttpListener` doesn't need
admin/elevation or a `netsh http add urlacl` reservation on Windows - that hostname is
special-cased.

Each toast also shows the redeemer's Twitch profile picture. Twitch's IRC feed (what
`TwitchChatConnector` uses for chat) doesn't carry avatars at all, so `Twitch/TwitchAvatarProvider.cs`
looks one up via Twitch's Helix API (`GET /helix/users`) instead, which needs a Client ID
(`Twitch.ClientId` in the config) alongside the existing OAuth token. Results are cached per
username. This lookup is a blocking HTTP call, deliberately made on the Twitch background thread
(in `ChaosCommandRouter.HandleBuy`, before the main-thread dispatch) rather than Unity's main
thread, so a slow or failed request can't stall a game frame. If `ClientId` is left blank, or a
lookup fails for any reason (including the browser failing to load the image at render time - see
`onerror` in `Core/OverlayHtml.cs`), the toast just falls back to its icon-only look instead of a
broken image.

Waterpark Simulator is an **IL2CPP** build (confirmed via `GameAssembly.dll` at the install root
and no `Assembly-CSharp.dll` under `WaterparkSimulator_Data\Managed`), so this targets
**BepInEx 6.x (IL2CPP)**, not the more commonly-documented BepInEx 5.x/Mono setup.

### Chat vote polls

Separate from `!buy`'s point economy, `Chaos/ChaosPollManager.cs` runs free chat-wide votes -
similar to games like 7 Days to Die letting chat vote on a "blood moon" mutator. A poll fires
automatically every `Poll.AutoIntervalMinutes` (default 20, set to `0` to disable automatic
polls), or on demand via `!startpoll` (moderator/broadcaster only). It picks `Poll.OptionCount`
(default 2 - a straight 1-vs-2 vote, though it can be raised) random actions from the same set
`!buy` prices out, posts them to chat numbered `1)`, `2)`, ..., and viewers vote by typing the
bare number (no `!`, no point cost) for
`Poll.DurationSeconds` (default 45s). Whichever option got the most votes fires for free when the
timer runs out; ties, and polls nobody voted in at all, both fall back to picking randomly among
the options rather than the poll being a no-op - announced to chat and, if that action succeeds,
shown on the OBS overlay the same way a `!buy` redemption is (via a new
`ChaosCommandRouter.ExecuteFree` that reuses the exact same execute/describe/announce path as
`!buy`, just skipping the point spend).

The overlay also shows the poll itself live, not just its outcome: `StartPoll` broadcasts a
`poll_started` SSE event (numbered options + duration), `RegisterVote` broadcasts a `poll_votes`
event with the current per-option tally every time a vote changes, and `ResolvePoll` broadcasts a
`poll_ended` event (winning option's index, or `-1` if nobody voted) - all via new
`ChaosCommandRouter.BroadcastPoll*` methods (`_overlay.Broadcast` is otherwise private to that
class). `Core/OverlayHtml.cs`'s poll widget listens for all three: it draws the options with a
live countdown and per-option vote bars/counts on `poll_started`, updates the bars in place on
`poll_votes`, and highlights the winner before fading out on `poll_ended`.

Bare-number votes are read from `TwitchChatConnector.OnChatMessage`, which now carries the raw
message text alongside username/display name (previously just the two), and are stashed via
`MainThreadDispatcher` like every other Twitch-thread-to-main-thread hop in this mod - `ChaosPollManager`
never has more than one thread touching its vote/option state, so it needs no locking.

### Threading model

TwitchLib raises chat events on a background thread. `PointsManager` only touches plain
dictionaries, so it's safe to call directly from those events. `ChaosController` touches
`GameObject`/`Rigidbody`/etc., which Unity only allows from the main thread - so
`ChaosCommandRouter` queues the actual effect via `MainThreadDispatcher`, and the injected
`UpdatePump` drains that queue every frame. `ChaosPollManager` follows the same rule for its own
state (see "Chat vote polls" above).

### Lifecycle wiring (`Plugin.cs`)

BepInEx.Unity.IL2CPP plugins derive from `BasePlugin`, not `BaseUnityPlugin` - there's no
`Awake`/`Update`/`OnDestroy` MonoBehaviour lifecycle to hook directly:

- **Load()**: runs once at startup (the IL2CPP equivalent of `Awake()`). Binds config, constructs
  `PointsManager` (and loads its save file), `ChaosController`, `ChaosCommandRouter`,
  `ChaosPollManager` (wired back into the router via a settable property to avoid a circular
  constructor dependency), injects `UpdatePump` via `AddComponent<UpdatePump>()` for a per-frame
  tick, then creates and connects `TwitchChatConnector`.
- **Tick()** (called every frame by `UpdatePump.OnUpdate`): drains `MainThreadDispatcher`, calls
  `PointsManager.Tick(Time.deltaTime)` for passive income, `ChaosPollManager.Tick(Time.deltaTime)`
  for the poll countdown/auto-trigger, and autosaves the economy periodically (there's no reliable
  "on quit" hook here - see `Plugin.cs` for why - so periodic autosave is what actually protects
  against data loss).

## Setup

### Option A: `install.ps1` (recommended for testing)

`install.ps1` automates everything except grabbing BepInEx itself (see why below), and is safe
to re-run repeatedly as you iterate:

```powershell
.\install.ps1 -GameDir "F:\SteamLibrary\steamapps\common\WaterPark Simulator" -LaunchGame
```

What it does:
1. Checks BepInEx's IL2CPP build is installed (`BepInEx\core\BepInEx.Unity.IL2CPP.dll`). If not:
   - **With Nexus Premium**: pass `-NexusApiKey` (your key from Account Settings > API Keys on
     Nexus) and it downloads and installs the **"BepInEx IL2CPP for Waterpark Simulator"** pack
     (https://www.nexusmods.com/waterparksimulator/mods/62) automatically via the Nexus API.
     Free accounts get a 403 from Nexus's `download_link` endpoint - Premium is required for
     this step specifically.
     ```powershell
     $env:NEXUS_API_KEY = 'your-personal-api-key'   # avoids putting it in shell history
     .\install.ps1 -GameDir "F:\SteamLibrary\steamapps\common\WaterPark Simulator" -LaunchGame
     ```
   - **Without a key** (or if the automated install fails for any reason): prints manual
     instructions and opens the mod page, then stops.
2. If `BepInEx\interop\UnityEngine.dll` doesn't exist yet, launches the game and waits for it to
   be generated (first run only; can take a few minutes - real boot time, don't assume it's
   stuck). These are also what the build step compiles against (see `WaterparkSimTwitchExpansion.csproj`)
   - BepInEx's plugin-loader API (`BasePlugin`, etc.) comes from a NuGet package, but the actual
   Unity engine types (`GameObject`, `MonoBehaviour`, ...) are inherently game-specific and have
   to come from here.
3. Runs `dotnet build` with `-p:GameDir` pointed at your install.
4. Copies the built DLL into `BepInEx\plugins\WaterparkSimTwitchExpansion\`.
5. Optionally seeds the Twitch config section if you pass `-TwitchChannel`, `-BotUsername`,
   and/or `-OAuthToken`:
   ```powershell
   .\install.ps1 -GameDir "F:\SteamLibrary\steamapps\common\WaterPark Simulator" `
       -TwitchChannel "mychannel" -BotUsername "mychannel" -OAuthToken "oauth:xxxxxxxx" -LaunchGame
   ```
6. Optionally launches the game (`-LaunchGame`).

Requires the .NET 6 SDK on PATH. Never commit your Nexus API key.

**Without Premium**: use the **"BepInEx IL2CPP for Waterpark Simulator"** pack manually:
https://www.nexusmods.com/waterparksimulator/mods/62 - download it, extract the zip, and move
its `winhttp` file and `BepInEx` folder into your game folder. `install.ps1` handles everything
after that (including running the game once to generate the interop assemblies, if the pack
hasn't already done that itself).

### Option B: manual

1. Install the pack above (or any IL2CPP build of BepInEx 6.x) into the game folder (same folder
   as `WaterparkSimulator.exe`), then **launch the game once** and let it sit for a bit before
   closing it - this is what generates `BepInEx\interop\UnityEngine*.dll`, which the build step
   below compiles the actual Unity engine types against (see the csproj comment for why that
   can't come from a generic NuGet package).
2. Build: `dotnet build -p:GameDir="F:\SteamLibrary\steamapps\common\WaterPark Simulator"`
   (adjust the path if yours differs).
3. Copy the **whole** `bin/Debug/net6.0/` folder's contents (not just
   `WaterparkSimTwitchExpansion.dll` - `CopyLocalLockFileAssemblies` in the csproj puts TwitchLib
   and its other dependencies there too, and BepInEx needs all of them present) into
   `BepInEx/plugins/WaterparkSimTwitchExpansion/`.
4. Launch the game once so BepInEx generates
   `BepInEx/config/com.musicman0917.waterparksimtwitchexpansion.cfg`, then fill in:
   - `Twitch.ChannelName` - the channel to join
   - `Twitch.BotUsername` / `Twitch.OAuthToken` - get a token (with `chat:read` + `chat:edit`
     scopes) at https://twitchtokengenerator.com/ (keep it secret; don't commit your cfg file).
     `twitchapps.com/tmi/`, the older tool most guides still point to, was discontinued in 2025.
   - `Prices.*` and `Economy.*` to taste

### Distributing a release (for other streamers, not just your own testing)

`install.ps1` is a dev loop - it needs the .NET SDK and deploys straight into one local game
install. For anyone else, use `package.ps1` instead, which builds Release and zips a
ready-to-extract package (no dev tooling needed on their end):

```powershell
.\package.ps1 -GameDir "F:\SteamLibrary\steamapps\common\WaterPark Simulator"
```

This writes `release\WaterparkSimTwitchExpansion-vX.Y.Z.zip`, shaped so extracting it directly
into a Waterpark Simulator install (that already has the BepInEx IL2CPP pack from Nexus mod #62)
just works - it drops straight into `BepInEx\plugins\WaterparkSimTwitchExpansion\`, dependency
DLLs included, and bundles [`SETUP.md`](SETUP.md) (the plain-language install guide for end
users - no PowerShell or GameDir involved on their side) at the zip root.

This matches how every other Waterpark Simulator IL2CPP mod (TwitchPark, the Crowd Control pack)
already expects BepInEx itself to be installed - a shared one-time prerequisite, not something
each individual mod bundles.

### In-game object requirements (both options)

Confirmed via the in-game `!scantags` diagnostic (see below) against a live session:

- **Guests**: tagged `Visitor` (not `Guest` - the only tags that exist anywhere in the scene are
  `CharacterModel`, `Ground`, `MainCamera`, `Player`, `Trash`, `Visitor`). The tag sits on child
  sub-components (e.g. `LegsWaterChecker`) rather than the character root, so `YeetGuest` looks
  for a `Rigidbody` on the tagged object first and falls back to a parent if needed. `YeetGuest`
  also filters candidates down to guests actually within `Camera.main`'s view frustum and not
  blocked by scenery (line-of-sight raycast) - so chat sees the yeet happen on stream instead of
  launching someone off in an unwatched corner of the park. Launch force is configurable
  (`Chaos.YeetUpForce`/`YeetSidewaysForce`, default 500/150) - the original 1500/300 sent guests
  flying far enough to land off the NavMesh, which the game then silently despawns (confirmed live
  via `Failed to create agent because it is not close enough to the NavMesh` right after a yeet) -
  the guest should fly, not vanish.
  - **Excludes background city pedestrians**: guests walking the sidewalk in the background city
    (outside the actual park) apparently also carry the `Visitor` tag. `YeetGuest` now filters
    those out via `IsInPark`, which walks each candidate's full ancestor chain and excludes anyone
    nested under an object named `StaticCityLayout` - confirmed as the background city's root via
    other objects' scene hierarchy paths seen in the log (e.g.
    `StaticCityLayout/City/Near City/MB_Orange_Lighthouse_Hotel/...`), though not yet confirmed
    specifically for guest/pedestrian objects. Best-effort and permissive by default (no match =
    counts as in-park), so a wrong guess here just risks occasionally still yeeting a pedestrian
    rather than breaking `!buy yeet` entirely. `!scan <term>` now logs each match's full hierarchy
    path (`path=Root/Child/.../object`) specifically so this kind of assumption can be checked
    directly - run `!scan visitor` and compare the path of an obvious sidewalk pedestrian against a
    guest actually inside the park if yeet keeps hitting pedestrians.
  - **Occlusion check bug fixed**: `FilterVisibleToCamera`'s line-of-sight raycast originally
    compared the raycast hit against the *tagged* sub-object specifically (`hit.transform ==
    go.transform`). Since that tagged object is often a small sub-part like `LegsWaterChecker`
    rather than the character root, a ray aimed at it almost always hit some *other* collider on
    the same guest's own body first (their torso, another limb, etc.) - which got miscounted as
    "blocked by something else" even for a guest standing in plain view. A live `!buy yeet` log
    showed this clearly: 116 candidates, 63 outside the frustum, the other 53 *all* rejected as
    occluded, 0 ever visible - `!buy yeet` failing with "no guest in view" essentially every time
    there wasn't a lucky raycast miss. Fixed by comparing whole-character roots instead
    (`hit.transform.root == go.transform.root`) - any hit on the same character now counts as
    "we can see them," regardless of which specific sub-part is tagged or got hit.
  - **Shows which NPC got yeeted**, on the overlay and in the chat reply. Parsing
    `Assembly-CSharp.dll`'s metadata turned up `AIBrain.OnNameChanged(FixedString64Bytes oldName,
    FixedString64Bytes newName)` - confirming visitors have a real networked name - and an
    overridden `AIBrain.ToString()`, which `GetVisitorDisplayName` tries first (via
    `GetComponentInParent<AIBrain>()`) before falling back to the launched object's own name (minus
    the `"(Clone)"` suffix) if no `AIBrain` is found or it comes back empty. `YeetGuest` now returns
    this via an `out string npcName` parameter, threaded through `ChaosCommandRouter.Execute`'s new
    `out string targetName` into `DescribeAction`, so the toast/chat text reads "DisplayName just
    yeeted NPC \<name\>!" instead of the generic "yeeted a guest". Confirmed live (via `!buy
    vomit`) that `AIBrain.ToString()` really is a debug dump, not a clean name, e.g.
    `[AIBrain Visitor Teen Male///ID770 "Braylen" visitor state=InteractWithAttraction
    target=0_DivingBoard(Clone) netId=770]` - `GetVisitorDisplayName` now regexes out the quoted
    token (`"Braylen"`) instead of announcing that whole string to chat, falling back to the full
    `ToString()` if the format ever doesn't match. Still a display-only lookup wrapped in a
    try/catch either way, so it can never break a chaos action that already succeeded.
- **Pools and waterslides aren't tagged at all.** The game tracks them through its own internal
  "Building" system instead. `ChaosController` finds them by object name (containing `"Pool"` /
  `"Slide"`), requiring the name to end in `"(Clone)"` - what Unity automatically appends to
  anything `Instantiate()`'d from a prefab at runtime, which is exactly how the game places
  buildings. A live `!scan pool` dump found 196 GameObjects containing `"Pool"`, of which only 4
  ended in `"(Clone)"` (`0_PoolRectangleSmall(Clone)` x2, `_DecorOldAttraction_Pool_1/2/3(Clone)`)
  - none of the other 192 (ladders, LOD meshes, outlines, decals, FX, spawners, even an unrelated
  object-pooling system called `PooledObjects` that has nothing to do with swimming pools) did.
  Requiring the suffix replaced an ever-growing blacklist (`NonInstanceNameHints`) that kept
  discovering new false-positives one at a time, live, sometimes only after something broke - that
  blacklist is kept as defense in depth, but the `"(Clone)"` requirement is what actually solved
  most of it. It turned out not to be a perfect filter on its own, though: the streamer confirmed
  `_DecorOldAttraction_Pool_1/2/3(Clone)` are decorative scenery sitting in an inactive/unused area
  of the map, not real player-built pools - they just happen to also end in `"(Clone)"`. Fixed by
  adding `"Decor"` to `NonInstanceNameHints` (this game's naming convention prefixes all
  non-functional set-dressing with `_Decor`, e.g. `_DecorLightPole`, `_DecorCity_Car1`, so this
  should hold up generally, not just for this one object). Current `NonInstanceNameHints`:
  `"Manager"`, `"FX"`, `"Decal"`, `"Spawner"`, `"Plug"`, `"Convex"`, `"Decor"`. The same
  `"(Clone)"` requirement is applied to waterslide matching too, though there's no equivalent
  `!scan slide` dump confirming it yet - run one if `!buy break` stops finding any slides.
- **Poop**: `SpawnPoop` tries the game's own spawn machinery first (`TrySpawnRealPoop`), falling
  back to cloning a static `Poop`/`sm2_poop` prop (found via `!scanpoop`) if that isn't available.
  **Confirmed live**: with a toilet built, repeated `!buy poop` calls logged
  `SpawnPoop: spawned the real PoopPrefab above '...' via PooledSpawnSystem` with no crash, no
  freeze, and no error spam, across several redemptions in the same session.
  - **Attempt 1 (reverted): raw-clone `ToiletInteraction.PoopPrefab`.** Found by decompiling
    BepInEx's interop-generated `Assembly-CSharp.dll` with ILSpy (searching "poop" turned up a
    whole bathroom-accident mechanic: `ToiletInteraction`, `PoopInteractable`, `ThrowingPoopItem`,
    `SpawnablePrefabType.Poop`), this looked like the "real" asset instead of a name-matched guess.
    It compiled and even cloned correctly by name in a live test with a toilet built - but its
    interactive script(s) then errored with an infinite per-frame `NullReferenceException`, exactly
    like the `Trash` incident below, because a raw `Object.Instantiate()` skips whatever lifecycle
    those scripts expect. `TryCloneSafely`'s `NetworkObject` check didn't catch this one either.
  - **Attempt 2 (current): go through `PooledSpawnSystem.SpawnObject` instead of cloning at all.**
    The streamer sent over the actual `Assembly-CSharp.dll` from their install, which let us parse
    its real metadata directly (type hierarchy and method signatures decoded straight from the
    ECMA-335 tables - no IL decompiler needed for that part) instead of just searching names in
    ILSpy. That explained attempt 1's crash: `PoopInteractable` extends `TrashInteractable` extends
    ... extends `NetworkBehaviour`, and carries a `wasTakenFromPool` field - it's built to come out
    of the game's own object pool, never a bare `Instantiate()`. The same metadata turned up
    `PooledSpawnSystem`, a scene singleton with `SpawnObject(GameObject prefab, Vector3 position,
    Quaternion rotation) : NetworkObject` - the same pooled, properly-Netcode-spawned path the game
    itself uses (there's also a `ConsoleSpawn(SpawnablePrefabType item)` that looks like a dev-cheat
    entry point, but it takes no position, so `SpawnObject` is the one that lets us place it above a
    pool). `TrySpawnRealPoop` looks up a live `PooledSpawnSystem`, confirms the prefab is already
    registered with it (`IsPrefabRegistered`), and only then calls `SpawnObject` - wrapped in a
    try/catch, falling back to the static-prop clone on any failure. Needs `Unity.Netcode.Runtime`
    referenced again too, since `SpawnObject` returns a `Unity.Netcode.NetworkObject` directly (used
    to `Despawn()` it properly after `Chaos.PoopLifetimeSeconds`, via `MainThreadDispatcher`, rather
    than a plain delayed `Object.Destroy()` which isn't safe for an actually-spawned networked
    object). Confirmed working live (see above) - despawn-after-`PoopLifetimeSeconds` itself isn't
    separately confirmed yet (that needs waiting out the full 90s and checking it actually cleans
    up), but the spawn side is solid.
  - **Lesson learned the hard way**: an earlier version of `SpawnPoop` cloned an existing
    `Trash`-tagged object instead (before the real poop objects were known), and it broke the
    game - an infinite `NullReferenceException` spam, every frame, forever. This game runs on
    Unity Netcode, and `Trash` items are spawned/tracked through it (see the constant
    `[Spawner] ... to SpawnerManager` log lines); cloning a *networked* object with a plain
    `Instantiate()` instead of properly spawning it through Netcode leaves the clone
    half-initialized and erroring forever. The fallback path still goes through `TryCloneSafely`,
    which checks the clone for a `NetworkObject` component (by type name, so it doesn't need a new
    `Unity.Netcode` assembly reference) and immediately destroys+rejects it if found - defense in
    depth for whatever ends up matching by name there, now that the primary path avoids raw cloning
    of real scripted objects entirely.
  - Template selection (fallback path) is deduped by name (`PoopTemplateExcludeHints` also strips
    `"(Clone)"` and `"_LOD"` variants) and requires an actual `Renderer` - a live `!scanpoop` dump
    found one `Poop` object sitting at the origin with nothing but a `Transform`, which would have
    spawned something completely invisible if dedup had picked it - so it picks fairly between
    genuinely distinct, actually-visible props instead.
  - The fallback statics aren't pickupable/cleanable in-game; each clone self-destructs after
    `Chaos.PoopLifetimeSeconds` (default 90s) instead of accumulating forever over a long stream.
    The real spawn (attempt 2) is despawned the same way, just via `NetworkObject.Despawn()` instead
    of `Object.Destroy()` - see above.
  - A live session froze (whole process went unresponsive, no C# exception logged) immediately
    after `SpawnPoop` targeted `Convex_Pool` - almost certainly a raw physics collision mesh (now
    excluded via the `"(Clone)"` requirement above, which it never had). Pool candidates are also
    filtered through `HasSanePosition` (rejects NaN/Infinity or absurdly-far-away transforms)
    before one gets used as a spawn point - a general backstop, not a fix tied to this specific
    object name.

If the game updates and any of this drifts, `!scantags` (wired to `ChaosController.ScanTags()`)
walks the live scene and logs every distinct tag in use with example object names - use it
again rather than guessing.

## Chat commands

- `!buy yeet` - launches a random guest (in view of the camera) into the air
- `!buy poop` - spawns poop above a random pool
- `!buy break` - sabotages a random waterslide
- `!buy ragdoll` - **confirmed working live** - flings the streamer's own character around
- `!buy vomit` - **confirmed working live** - makes a random visible in-park guest throw up
- `!buy pee` - **unverified**, see below - makes a random visible in-park guest pee
- `!buy trash` - **unverified**, see below - makes a random visible in-park guest litter
- `!buy invert` - **confirmed working live** - flips the game's own "Invert Y Axis (Player)"
  setting for a while
- `!buy nojump` - **confirmed working live** - disables the streamer's jump for a while
- `!buy drop` - **unverified**, see below - makes the streamer drop their currently held item
- `!balance`/`!points` - replies in chat with the caller's point balance. (Used to only log
  locally, not actually reply to the viewer who asked - that was a leftover stub, now fixed.)
- `!commands`/`!help` - replies in chat with every `!buy <action>` and its point cost, built
  from the same price table `!buy` itself checks against so it can never drift out of sync with
  whatever the streamer has configured.
- `!give <username> <amount>` - moderator/broadcaster only. Grants points to a viewer (e.g. for
  a giveaway, correcting a balance, or testing `!buy` without waiting on passive income).
- `!startpoll` - moderator/broadcaster only. Starts a free chat-vote poll on demand (see "Chat
  vote polls" above) - polls also fire automatically on a timer.
- `1`, `2`, `3`, ... - not a `!` command at all, just a bare number: votes in whatever poll is
  currently active (ignored otherwise).
- `!scantags` - diagnostic. Logs every distinct GameObject tag in the current scene with example
  object names; how `Guest`/`Pool`/`Waterslide` were actually identified (see below).
- `!scanmoney` - diagnostic. Logs any GameObject/component whose name looks money-related
  (`Money`/`Cash`/`Bank`/`Economy`/`Finance`/`Currency`/`Wallet`) - the discovery step needed
  before `!buy addmoney`/`!buy removemoney` (affecting the game's own in-park cash, not this
  mod's Twitch-points economy) can actually be implemented. Not done yet - see Roadmap.
- `!scanpoop` - diagnostic. Logs any GameObject/component whose name looks poop-related
  (`Poop`/`Feces`/`Turd`) - this is how the `Poop`/`sm2_poop` objects `!buy poop` uses were found;
  run it again if the game updates and this drifts.
- `!scan <term>` - diagnostic. Logs **every** GameObject whose name contains `<term>`
  (case-insensitive), with its tag, position (flagged if it fails `HasSanePosition`), full scene
  hierarchy path (from the scene root down, e.g. `StaticCityLayout/City/Near City/...`), and full
  component list - e.g. `!scan pool` to see every match at once. Unlike the hint-based scans
  above, this isn't curated at all, which is the point: `"Pool"`/`"Slide"` name matching kept
  turning up new false-positives one at a time, live, sometimes only after something broke
  (`CleanPoolDirtFX`, `PoolDirtDecal`, a `Spawner` marker, a `PoolPlug` collider, and finally
  `Convex_Pool`, suspected of freezing the game outright) - this lets every match for a given term
  get reviewed up front instead.

### `!buy vomit` (confirmed working) / `!buy pee` / `!buy trash`

Added by calling the game's own per-guest AI behavior directly instead of spawning/cloning
anything ourselves - found the same way as the `PooledSpawnSystem.SpawnObject` poop fix, by
decoding `Assembly-CSharp.dll`'s metadata (type hierarchy + method signatures from the ECMA-335
tables, no full IL decompiler). `AIBrain` (the same class behind the yeeted-NPC name lookup) has
public, parameterless-or-nearly-so, void instance methods for exactly this:

- `TryToPuke(bool ignoreCooldown)` - `!buy vomit` passes `ignoreCooldown: true` so a paid action
  always visibly does something instead of sometimes silently no-opping on the AI's own internal
  cooldown.
- `StartPeeing()` - `!buy pee`.
- `TrySpawnTrash()` - `!buy trash`.

All three go through the same guest-finding as `!buy yeet` (`FindRandomVisibleGuestInPark`: tagged
`Visitor`, excludes background city pedestrians via `IsInPark`, filtered to camera-visible via
`FilterVisibleToCamera`), then call the method on that guest's `AIBrain` inside a try/catch.
Unlike the `SpawnPoop` saga, this never spawns or clones anything itself - it invokes a method on
an already-alive, already-networked guest, the exact same way the game invokes it when an NPC
naturally does one of these things on its own. `!buy vomit` is **confirmed working live** - the
same log also confirmed `AIBrain.ToString()`'s real format, which led to the display-name regex
fix above. `!buy pee`/`!buy trash` haven't shown up in a log yet, so treat them with the same
caution as any new chaos action until confirmed.

### `!buy nojump` / `!buy invert` (both confirmed working) / `!buy drop`

These originally worked by Harmony-patching `UnityEngine.Input` itself (see
`Chaos/PlayerInputSabotage.cs`), on the assumption that the game read movement/jump through
Unity's legacy Input Manager. A live test confirmed `!buy nojump` did nothing - decoding
`Assembly-CSharp.dll`'s metadata (same ECMA-335-table technique used for the poop/vomit/pee/trash
fixes, no full IL decompiler) showed why: `PlayerMovementController` doesn't call
`UnityEngine.Input` at all. It reads a `jump` state off a small internal `InputSystem`
MonoBehaviour (unnamespaced, wired to a real `UnityEngine.InputSystem.PlayerInput` component) that
the new Input System pushes into via an `OnJump` callback calling `JumpInput(bool)` - the exact
pattern Unity's own "StarterAssetsInputs" template uses, just under a different class name. The
`UnityEngine.Input` patches were never being consulted at all.

A second attempt patched `InputSystem.JumpInput` directly (the small internal setter method
`OnJump` calls) - confirmed via the DLL's Param table that the parameter name Harmony needs to
bind to (`newJumpState`) matched exactly, but a live test showed `!buy nojump` *still* did
nothing. Most likely cause: IL2Cpp's AOT compiler frequently inlines short one-line internal calls
like `jump = newJumpState;` at the native level, so a call from `OnJump` straight into `JumpInput`
never actually passes through the managed interop shim Harmony patches - a real gotcha for this
style of IL2Cpp modding, distinct from the "wrong API family" problem that broke the original
`UnityEngine.Input` version.

`PlayerInputSabotage.cs` now patches `InputSystem.OnJump` itself instead - a guaranteed real call
boundary, since the new Input System invokes it through an actual C# event subscription no matter
which device (keyboard or gamepad) fired the action - and forces the resulting `jump` state back
to false immediately afterward via the confirmed-public `jump` property. That also makes it
keyboard/controller-agnostic: it operates on the merged input state both device types feed into,
not a specific physical key, so there's no reason to reach for something like an OS-level spacebar
block (which would only ever affect keyboard players, and still wouldn't touch a gamepad's jump
button). **This one's confirmed live - the streamer tested it directly and it works.**

`!buy invert` doesn't patch anything at all anymore. Rather than guess at another IL2Cpp call
boundary, the streamer pointed out the game already ships a real "Invert Y Axis (Player)" toggle
in its own Settings menu - so `ChaosController.InvertControls` just flips that setting directly:
`SettingsManager.Data.Game.InvertMouseY` (a static property, confirmed the hard way - CS0176 -
after first guessing it hung off `Instance` like everything else on that class), then calls
`SettingsManager.Instance.ApplyCameraSystemSettings()`
to push it live immediately (the same method the in-game Settings UI itself uses). It flips
relative to whatever the streamer's own preference already was, and restores that exact original
value when the timer expires - never calling `CommitSettings()`/`SaveSettings()`, so this never
gets written to the streamer's real save file. Reusing a setting the game already applies
correctly itself sidesteps the whole class of IL2Cpp-inlining risk that took two attempts to work
around for `nojump`. **This one's confirmed live too - works perfectly.**

`!buy drop` no longer simulates a keypress at all - it now calls the player's own
`InventorySystem.DropItem()` directly, found the same way as the `AIBrain` vomit/pee/trash
methods, which sidesteps the whole input-layer problem for that one entirely. The old
`[PlayerSabotage]` axis/button/key config options (`HorizontalAxisName`, `VerticalAxisName`,
`JumpButtonName`, `JumpKeyCode`, `DropKeyCode`) are gone since none of them apply anymore;
`InvertDurationSeconds` and `NoJumpDurationSeconds` still control how long invert/nojump last
before auto-reverting. **`!buy drop`'s confirmed live too.**

This also adds a build-time dependency: `0Harmony.dll` (HarmonyX, already shipped inside every
BepInEx install at `BepInEx\core\0Harmony.dll`) - referenced via HintPath in the csproj, same
reasoning as `Il2Cppmscorlib`/`UnityEngine*`.

### `!buy ragdoll`

Originally applied a raw impulse via `Rigidbody.AddForce`/`AddTorque` on whatever Rigidbody it
could find near the `Player`-tagged object. A live test confirmed this did nothing - exactly what
the old doc comment here already suspected, since the streamer's own controls screen shows
Ragdoll is triggered by double-tapping the jump key, and the player moves via
`CharacterController`, which `AddForce` can't touch (`CharacterController`-driven objects ignore
physics forces entirely - the same category of bug as `nojump`/`invert`/`drop` originally
guessing at the wrong mechanism, just for a different system).

Decoding `Assembly-CSharp.dll`'s metadata turned up the real one: `PlayerRagdollSystem` (extends
`BaseRagdoll`, itself a `NetworkBehaviour`) has `EnableRagdollTemp(Vector3 forceVector, Vector3
torqueVector)` - a small convenience method taking exactly a force+torque pair, almost certainly
what the game's own double-tap-jump control calls internally. `RagdollPlayer` now finds that
component on the player object and calls it directly with the same random-direction force/torque
calculation it already had, instead of touching a Rigidbody at all. **Confirmed working live** -
but the original 800/600 defaults flung the streamer high enough to clear map barriers and get
stuck outside the playable area. Force is now configurable (`Chaos.RagdollUpForce`/
`RagdollSidewaysForce`, default 250/150, same config pattern as `YeetUpForce`/`YeetSidewaysForce`)
- lower further if it's still too much, raise if it ends up too weak.

## Roadmap

- **Confirm `!buy pee`/`!buy trash` live** - `!buy vomit` is now confirmed working (see
  "`!buy vomit` (confirmed working)" above), but `!buy pee`/`!buy trash` (the same `AIBrain`
  approach, different method) haven't shown up in a log yet.
- **Confirm the lowered `!buy ragdoll` force doesn't still clear map barriers** - the fix itself
  (calling `PlayerRagdollSystem.EnableRagdollTemp()` instead of a raw Rigidbody) is confirmed
  working, but the streamer hasn't yet confirmed the new lower `RagdollUpForce`/
  `RagdollSidewaysForce` defaults (250/150, down from 800/600) keep the streamer inside the
  playable area.
- **Confirm the real poop despawns cleanly after `PoopLifetimeSeconds`** - the spawn side of
  `PooledSpawnSystem.SpawnObject` is confirmed working live (see "Attempt 2" under Poop above), but
  nobody's yet waited out the full 90s default to confirm `NetworkObject.Despawn(true)` actually
  cleans it up rather than leaving something behind or logging a Netcode warning.
- **`!buy addmoney` / `!buy removemoney`** - add to or drain the game's own in-park cash (not
  this mod's separate Twitch-points economy, which `!give` already covers). Blocked on knowing
  what actually tracks that money internally - run `!scanmoney` live and report back what it
  finds, same way `!scantags` found the real `Visitor` tag, before this gets implemented for
  real.
- **Twitch Channel Points integration** - let viewers trigger chaos by redeeming Twitch's own
  Channel Points, not just via `!buy` and our custom economy. This needs a registered Twitch
  Developer app (Client ID/Secret) regardless, since Channel Points redemptions come through
  Twitch's EventSub (websocket/webhook), not IRC chat - a new listener alongside
  `TwitchChatConnector`, not a replacement for it. Registering a real app also gets us refreshable
  OAuth tokens instead of the current `twitchapps.com` token that just expires (~60 days) and
  needs manual regeneration, plus a properly-branded authorization screen once other streamers
  are installing this.
  - Architecturally this should be a light lift: `ChaosCommandRouter.Execute(action)` already
    separates "something triggered a purchase" from "run the chaos effect," so a redemption
    listener just needs to feed into the same path `!buy` uses now.
  - Open design question to settle when this gets built: do redemptions spend Twitch's own
    Channel Points balance directly (bypassing `PointsManager` entirely), or do they convert into
    our custom economy somehow? Not decided yet.