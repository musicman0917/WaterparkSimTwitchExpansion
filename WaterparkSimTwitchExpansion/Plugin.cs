using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using UnityEngine;
using WaterparkSimTwitchExpansion.Chaos;
using WaterparkSimTwitchExpansion.Core;
using WaterparkSimTwitchExpansion.Economy;
using WaterparkSimTwitchExpansion.Twitch;

namespace WaterparkSimTwitchExpansion
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Plugin : BasePlugin
    {
        private const string PluginGuid = "com.musicman0917.waterparksimtwitchexpansion";
        private const string PluginName = "WaterparkSim Twitch Expansion";
        private const string PluginVersion = "0.1.0";

        // --- Config ---
        private ConfigEntry<string> _channelName;
        private ConfigEntry<string> _botUsername;
        private ConfigEntry<string> _oauthToken;
        private ConfigEntry<string> _clientId;
        private ConfigEntry<int> _passiveIncomeAmount;
        private ConfigEntry<int> _passiveIncomeIntervalSeconds;
        private ConfigEntry<int> _priceYeet;
        private ConfigEntry<int> _pricePoop;
        private ConfigEntry<int> _priceBreak;
        private ConfigEntry<int> _priceRagdoll;
        private ConfigEntry<int> _priceInvert;
        private ConfigEntry<int> _priceNoJump;
        private ConfigEntry<int> _priceDrop;
        private ConfigEntry<int> _priceVomit;
        private ConfigEntry<int> _pricePee;
        private ConfigEntry<int> _priceTrash;
        private ConfigEntry<int> _autosaveIntervalSeconds;
        private ConfigEntry<int> _invertDurationSeconds;
        private ConfigEntry<int> _noJumpDurationSeconds;
        private ConfigEntry<bool> _overlayEnabled;
        private ConfigEntry<int> _overlayPort;
        private ConfigEntry<int> _poopLifetimeSeconds;
        private ConfigEntry<float> _yeetUpForce;
        private ConfigEntry<float> _yeetSidewaysForce;
        private ConfigEntry<int> _pollDurationSeconds;
        private ConfigEntry<int> _pollAutoIntervalMinutes;
        private ConfigEntry<int> _pollOptionCount;

        // --- Runtime pieces ---
        private MainThreadDispatcher _dispatcher;
        private PointsManager _points;
        private ChaosController _chaos;
        private ChaosCommandRouter _router;
        private ChaosPollManager _pollManager;
        private TwitchChatConnector _twitch;
        private Core.OverlayServer _overlay;

        private float _secondsSinceAutosave;

        // BasePlugin.Load() runs once at plugin startup - the IL2CPP equivalent of Awake().
        public override void Load()
        {
            BindConfig();

            _dispatcher = new MainThreadDispatcher();

            var savePath = Path.Combine(Paths.ConfigPath, "waterpark_twitch_points.json");
            _points = new PointsManager(
                Log,
                savePath,
                passiveIncomeAmount: _passiveIncomeAmount.Value,
                passiveIncomeInterval: TimeSpan.FromSeconds(_passiveIncomeIntervalSeconds.Value));
            _points.Load();

            _chaos = new ChaosController(Log, _dispatcher, _invertDurationSeconds.Value, _noJumpDurationSeconds.Value, _poopLifetimeSeconds.Value, _yeetUpForce.Value, _yeetSidewaysForce.Value);

            // See Chaos/PlayerInputSabotage.cs for what this can and can't do.
            PlayerInputSabotage.Apply(Log);

            var prices = new Dictionary<string, int>
            {
                ["yeet"] = _priceYeet.Value,
                ["poop"] = _pricePoop.Value,
                ["break"] = _priceBreak.Value,
                ["ragdoll"] = _priceRagdoll.Value,
                ["invert"] = _priceInvert.Value,
                ["nojump"] = _priceNoJump.Value,
                ["drop"] = _priceDrop.Value,
                ["vomit"] = _priceVomit.Value,
                ["pee"] = _pricePee.Value,
                ["trash"] = _priceTrash.Value,
            };
            // Inject a MonoBehaviour to draw an on-screen line for every redemption (see
            // OnScreenNotifier for why this needs to be a MonoBehaviour rather than plain C#).
            var notifier = AddComponent<Core.OnScreenNotifier>();

            if (_overlayEnabled.Value)
            {
                _overlay = new Core.OverlayServer(Log, _overlayPort.Value);
                _overlay.Start();
            }

            // Looks up chatters' Twitch profile pictures for the overlay. Needs both a Client ID
            // and OAuth token, so it's skipped (toast just falls back to its icon) until both are
            // filled in - this is a nice-to-have, not required for the mod to work.
            TwitchAvatarProvider avatarProvider = null;
            if (!string.IsNullOrWhiteSpace(_clientId.Value) && !string.IsNullOrWhiteSpace(_oauthToken.Value))
            {
                avatarProvider = new TwitchAvatarProvider(Log, _clientId.Value, _oauthToken.Value);
            }

            _router = new ChaosCommandRouter(Log, _points, _chaos, _dispatcher, notifier, _overlay, avatarProvider, prices);

            // Free chat-vote polls (separate from !buy's point economy) - offers a random subset
            // of the same actions !buy prices out. Constructed after _router (needs it to run the
            // winning option and list poll options) but wired back via a settable property to
            // avoid a circular constructor dependency.
            _pollManager = new ChaosPollManager(Log, _router, prices.Keys.ToList(), _pollDurationSeconds.Value, _pollAutoIntervalMinutes.Value * 60f, _pollOptionCount.Value);
            _router.PollManager = _pollManager;

            // Inject a MonoBehaviour to get a per-frame tick (see UpdatePump for why).
            var pump = AddComponent<UpdatePump>();
            pump.OnUpdate = Tick;

            // No Application.quitting hook: UnityEngine's IL2CPP interop events use
            // Il2CppSystem.Action, not System.Action, and don't accept a plain C# lambda via +=
            // directly. Periodic autosave in Tick() covers data loss well enough without it.

            if (string.IsNullOrWhiteSpace(_channelName.Value) || string.IsNullOrWhiteSpace(_oauthToken.Value))
            {
                Log.LogWarning("Twitch channel/OAuth token not configured yet - edit the config file and restart. Skipping connection.");
                return;
            }

            _twitch = new TwitchChatConnector(Log, _botUsername.Value, _oauthToken.Value, _channelName.Value);
            _twitch.OnChatMessage += (username, displayName, message) =>
            {
                _router.HandleChatMessage(username, displayName);

                // Poll-vote bookkeeping touches no UnityEngine objects, but it does share state
                // with Tick()/StartPoll() (both main-thread-only) - route through the dispatcher
                // like every other Twitch-thread-to-main-thread hop in this mod, rather than
                // reasoning about it as a special "safe" case.
                _dispatcher.Enqueue(() => _pollManager.RegisterVote(username, message));
            };
            _twitch.OnChatCommand += _router.HandleChatCommand;
            _router.SendChatMessage = _twitch.SendMessage;
            _twitch.Connect();

            Log.LogInfo($"{PluginName} v{PluginVersion} loaded.");
        }

        private void Tick()
        {
            // Drain any chaos actions queued from Twitch background-thread events.
            _dispatcher.ProcessQueue();

            // Auto-revert timed sabotage effects (invert controls / disable jump).
            _chaos.TickSabotageTimers();

            // Passive income tick (every N seconds, defined by _passiveIncomeIntervalSeconds).
            _points.Tick(Time.deltaTime);

            // Chat-vote poll countdown/auto-trigger.
            _pollManager.Tick(Time.deltaTime);

            // Periodic autosave so a crash doesn't wipe the economy.
            _secondsSinceAutosave += Time.deltaTime;
            if (_secondsSinceAutosave >= _autosaveIntervalSeconds.Value)
            {
                _secondsSinceAutosave = 0f;
                _points.Save();
            }
        }

        private void BindConfig()
        {
            _channelName = Config.Bind("Twitch", "ChannelName", "", "Twitch channel to join, without the leading #.");
            _botUsername = Config.Bind("Twitch", "BotUsername", "", "Twitch account the bot logs in as (can be the streamer's own account).");
            _oauthToken = Config.Bind("Twitch", "OAuthToken", "", "OAuth token for BotUsername (chat:read + chat:edit scopes), e.g. 'oauth:xxxxxxxx' from https://twitchtokengenerator.com/. Keep this secret.");
            _clientId = Config.Bind("Twitch", "ClientId", "", "Twitch application Client ID (shown alongside the token on https://twitchtokengenerator.com/) - only needed to show chatters' profile pictures on the OBS overlay. Leave blank to skip avatars.");

            _passiveIncomeAmount = Config.Bind("Economy", "PassiveIncomeAmount", 10, "Points paid to each active chatter per interval.");
            _passiveIncomeIntervalSeconds = Config.Bind("Economy", "PassiveIncomeIntervalSeconds", 60, "How often (seconds) passive income is paid out.");
            _autosaveIntervalSeconds = Config.Bind("Economy", "AutosaveIntervalSeconds", 120, "How often (seconds) the points file is saved to disk.");

            _priceYeet = Config.Bind("Prices", "Yeet", 100, "Point cost of '!buy yeet'.");
            _pricePoop = Config.Bind("Prices", "Poop", 150, "Point cost of '!buy poop'.");
            _priceBreak = Config.Bind("Prices", "Break", 300, "Point cost of '!buy break'.");
            _priceRagdoll = Config.Bind("Prices", "Ragdoll", 200, "Point cost of '!buy ragdoll'.");
            _priceInvert = Config.Bind("Prices", "Invert", 250, "Point cost of '!buy invert'.");
            _priceNoJump = Config.Bind("Prices", "NoJump", 200, "Point cost of '!buy nojump'.");
            _priceDrop = Config.Bind("Prices", "Drop", 150, "Point cost of '!buy drop'.");
            _priceVomit = Config.Bind("Prices", "Vomit", 150, "Point cost of '!buy vomit' - triggers a random visible guest's own AIBrain.TryToPuke().");
            _pricePee = Config.Bind("Prices", "Pee", 120, "Point cost of '!buy pee' - triggers a random visible guest's own AIBrain.StartPeeing().");
            _priceTrash = Config.Bind("Prices", "Trash", 100, "Point cost of '!buy trash' - triggers a random visible guest's own AIBrain.TrySpawnTrash().");

            _poopLifetimeSeconds = Config.Bind("Chaos", "PoopLifetimeSeconds", 90, "How long (seconds) a '!buy poop' clone stays in the world before despawning - it can't be picked up/cleaned by anything in-game, so it self-destructs instead.");
            _yeetUpForce = Config.Bind("Chaos", "YeetUpForce", 500f, "Upward impulse force for '!buy yeet'. The original 1500 sent guests flying far enough to land off the NavMesh and get silently despawned by the game - lower this further if guests still disappear, raise it if the yeet looks too weak.");
            _yeetSidewaysForce = Config.Bind("Chaos", "YeetSidewaysForce", 150f, "Random horizontal impulse force for '!buy yeet' (see YeetUpForce).");

            // See Chaos/PlayerInputSabotage.cs - patches the game's real InputSystem.JumpInput/
            // MoveInput methods directly, found by decoding Assembly-CSharp.dll after the
            // original UnityEngine.Input-based approach was confirmed live to do nothing.
            _invertDurationSeconds = Config.Bind("PlayerSabotage", "InvertDurationSeconds", 15, "How long (seconds) '!buy invert' reverses movement for.");
            _noJumpDurationSeconds = Config.Bind("PlayerSabotage", "NoJumpDurationSeconds", 15, "How long (seconds) '!buy nojump' disables jumping for.");

            _overlayEnabled = Config.Bind("Overlay", "Enabled", true, "Whether to run the local web overlay for OBS's Browser Source (see README).");
            _overlayPort = Config.Bind("Overlay", "Port", 9412, "Port for the local overlay web server. Point an OBS Browser Source at http://localhost:<port>/overlay.html.");

            _pollDurationSeconds = Config.Bind("Poll", "DurationSeconds", 45, "How long (seconds) a chaos vote poll stays open for voting once started.");
            _pollAutoIntervalMinutes = Config.Bind("Poll", "AutoIntervalMinutes", 20, "How often (minutes) a chaos vote poll starts automatically. Set to 0 to disable automatic polls - moderators/broadcaster can still start one on demand with '!startpoll'.");
            _pollOptionCount = Config.Bind("Poll", "OptionCount", 2, "How many options to offer per chaos vote poll (minimum 2) - default is a straight 1-vs-2 vote.");
        }
    }
}
