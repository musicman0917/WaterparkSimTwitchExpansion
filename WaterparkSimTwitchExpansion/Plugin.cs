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
        private ConfigEntry<int> _priceAddMoney;
        private ConfigEntry<int> _priceRemoveMoney;
        private ConfigEntry<float> _addMoneyAmount;
        private ConfigEntry<float> _removeMoneyAmount;
        private ConfigEntry<int> _priceEarthquake;
        private ConfigEntry<int> _priceGravity;
        private ConfigEntry<int> _priceShuffle;
        private ConfigEntry<int> _priceFireSale;
        private ConfigEntry<int> _priceSwarm;
        private ConfigEntry<int> _priceTornado;
        private ConfigEntry<int> _priceUfo;
        private ConfigEntry<int> _priceMafia;
        private ConfigEntry<int> _priceItemsRain;
        private ConfigEntry<int> _priceQueso;
        private ConfigEntry<float> _earthquakeRagdollUpForce;
        private ConfigEntry<float> _earthquakeRagdollSidewaysForce;
        private ConfigEntry<int> _gravityDurationSeconds;
        private ConfigEntry<float> _gravityLowMultiplier;
        private ConfigEntry<float> _gravityHighMultiplier;
        private ConfigEntry<int> _fireSaleDurationSeconds;
        private ConfigEntry<bool> _holdEffectsWhileMenuOpen;
        private ConfigEntry<int> _autosaveIntervalSeconds;
        private ConfigEntry<int> _invertDurationSeconds;
        private ConfigEntry<int> _noJumpDurationSeconds;
        private ConfigEntry<bool> _overlayEnabled;
        private ConfigEntry<int> _overlayPort;
        private ConfigEntry<int> _poopLifetimeSeconds;
        private ConfigEntry<float> _yeetUpForce;
        private ConfigEntry<float> _yeetSidewaysForce;
        private ConfigEntry<float> _ragdollUpForce;
        private ConfigEntry<float> _ragdollSidewaysForce;
        private ConfigEntry<int> _pollDurationSeconds;
        private ConfigEntry<int> _pollAutoIntervalMinutes;
        private ConfigEntry<int> _pollOptionCount;
        private ConfigEntry<int> _startingBalanceViewer;
        private ConfigEntry<int> _startingBalanceFollower;
        private ConfigEntry<int> _startingBalanceVipMod;
        private ConfigEntry<int> _subscriberPointsPerTier;
        private ConfigEntry<int> _giftedSubPointsPerTier;
        private ConfigEntry<int> _bitsToPointsRatio;
        private ConfigEntry<string> _followerCheckClientId;
        private ConfigEntry<string> _followerCheckOAuthToken;
        private ConfigEntry<string> _disabledActions;

        // action name -> its point-cost ConfigEntry, built once in BindConfig - used by
        // SaveMenuChangesToConfig to write ModMenu's live price edits back to the .cfg file.
        private Dictionary<string, ConfigEntry<int>> _priceConfigEntries;

        // --- Runtime pieces ---
        private MainThreadDispatcher _dispatcher;
        private PointsManager _points;
        private ChaosController _chaos;
        private ChaosCommandRouter _router;
        private ChaosPollManager _pollManager;
        private TwitchChatConnector _twitch;
        private Core.OverlayServer _overlay;
        private Core.ModMenu _modMenu;

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

            _chaos = new ChaosController(
                Log, _dispatcher, _invertDurationSeconds.Value, _noJumpDurationSeconds.Value, _poopLifetimeSeconds.Value,
                _yeetUpForce.Value, _yeetSidewaysForce.Value, _ragdollUpForce.Value, _ragdollSidewaysForce.Value,
                _addMoneyAmount.Value, _removeMoneyAmount.Value,
                _earthquakeRagdollUpForce.Value, _earthquakeRagdollSidewaysForce.Value,
                _gravityDurationSeconds.Value, _gravityLowMultiplier.Value, _gravityHighMultiplier.Value,
                _fireSaleDurationSeconds.Value, _holdEffectsWhileMenuOpen.Value);

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
                ["addmoney"] = _priceAddMoney.Value,
                ["removemoney"] = _priceRemoveMoney.Value,
                ["earthquake"] = _priceEarthquake.Value,
                ["gravity"] = _priceGravity.Value,
                ["shuffle"] = _priceShuffle.Value,
                ["firesale"] = _priceFireSale.Value,
                ["swarm"] = _priceSwarm.Value,
                ["tornado"] = _priceTornado.Value,
                ["ufo"] = _priceUfo.Value,
                ["mafia"] = _priceMafia.Value,
                ["itemsrain"] = _priceItemsRain.Value,
                ["queso"] = _priceQueso.Value,
            };
            // Inject a MonoBehaviour to draw an on-screen line for every redemption (see
            // OnScreenNotifier for why this needs to be a MonoBehaviour rather than plain C#).
            var notifier = AddComponent<Core.OnScreenNotifier>();

            // Always constructed now (not just when enabled) so ModMenu's overlay toggle can
            // Start()/Stop() the same instance live - constructing an HttpListener and adding a
            // prefix doesn't actually bind a socket until Start() is called, so this is free.
            _overlay = new Core.OverlayServer(Log, _overlayPort.Value);
            if (_overlayEnabled.Value)
            {
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

            // Checks follower status for the "follower" starting-balance tier. Deliberately a
            // SEPARATE Client ID/token from ClientId/OAuthToken above: this needs a token
            // belonging to the broadcaster themselves (or a channel moderator) with the
            // moderator:read:followers scope, which the bot's chat:read/chat:edit token doesn't
            // (and generally can't) have - see TwitchFollowerProvider's doc comment. Skipped
            // entirely (everyone gets StartingBalanceViewer) until both are filled in.
            TwitchFollowerProvider followerProvider = null;
            if (!string.IsNullOrWhiteSpace(_followerCheckClientId.Value) && !string.IsNullOrWhiteSpace(_followerCheckOAuthToken.Value))
            {
                followerProvider = new TwitchFollowerProvider(Log, _followerCheckClientId.Value, _followerCheckOAuthToken.Value, _channelName.Value);
            }

            _router = new ChaosCommandRouter(
                Log, _points, _chaos, _dispatcher, notifier, _overlay, avatarProvider, followerProvider, prices,
                _startingBalanceViewer.Value, _startingBalanceFollower.Value, _startingBalanceVipMod.Value,
                _subscriberPointsPerTier.Value, _giftedSubPointsPerTier.Value, _bitsToPointsRatio.Value);

            // Restore any actions ModMenu had turned off before the last restart.
            foreach (var action in _disabledActions.Value.Split(','))
            {
                var trimmed = action.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                {
                    _router.DisabledActions.Add(trimmed);
                }
            }

            // Free chat-vote polls (separate from !buy's point economy) - offers a random subset
            // of the same actions !buy prices out. Constructed after _router (needs it to run the
            // winning option and list poll options) but wired back via a settable property to
            // avoid a circular constructor dependency.
            _pollManager = new ChaosPollManager(Log, _router, prices.Keys.ToList(), _pollDurationSeconds.Value, _pollAutoIntervalMinutes.Value * 60f, _pollOptionCount.Value);
            _router.PollManager = _pollManager;

            // Inject the in-game F9 settings panel - see ModMenu's doc comment. Wired up last,
            // once every runtime piece it reads/edits actually exists.
            _modMenu = AddComponent<Core.ModMenu>();
            _modMenu.Init(Log, _chaos, _router, _points, _pollManager, _overlay, SaveMenuChangesToConfig);

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
            _twitch.OnChatMessage += activity =>
            {
                // Balance/starting-balance bookkeeping touches no UnityEngine objects, so this can
                // run directly on the Twitch thread (see PointsManager's thread-safety note).
                _router.HandleChatMessage(activity);

                // Poll-vote bookkeeping touches no UnityEngine objects, but it does share state
                // with Tick()/StartPoll() (both main-thread-only) - route through the dispatcher
                // like every other Twitch-thread-to-main-thread hop in this mod, rather than
                // reasoning about it as a special "safe" case.
                _dispatcher.Enqueue(() => _pollManager.RegisterVote(activity.Username, activity.Message));
            };
            _twitch.OnChatCommand += _router.HandleChatCommand;

            // Subscription/gift/bits point grants call Announce(), which touches OnScreenNotifier
            // (UnityEngine) - unlike HandleChatMessage above, these must hop onto the main thread.
            _twitch.OnSubscription += (username, displayName, tier) =>
                _dispatcher.Enqueue(() => _router.HandleSubscription(username, displayName, tier));
            _twitch.OnGiftedSub += (username, displayName, tier) =>
                _dispatcher.Enqueue(() => _router.HandleGiftedSub(username, displayName, tier));
            _twitch.OnBitsCheered += (username, displayName, bits) =>
                _dispatcher.Enqueue(() => _router.HandleBitsCheered(username, displayName, bits));

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

            // Run any chaos effects that were held because a menu looked open when they were
            // triggered (see ChaosController.IsMenuOpen), now that it may have closed.
            _router.ProcessHeldChaosActions();

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

        /// <summary>Wired into ModMenu.Init as its "Save to config file" button - copies every
        /// live, menu-editable value (see ModMenu's doc comment for exactly what that covers) back
        /// into its ConfigEntry so it survives a restart, then writes the .cfg file once. Without
        /// this, ModMenu's changes only apply until the game closes.</summary>
        private void SaveMenuChangesToConfig()
        {
            // Batches every ConfigEntry.Value write below into a single file save instead of one
            // per assignment (BepInEx's default is to save on every individual .Value set).
            Config.SaveOnConfigSet = false;

            foreach (var entry in _priceConfigEntries)
            {
                if (_router.Prices.TryGetValue(entry.Key, out var price))
                {
                    entry.Value.Value = price;
                }
            }
            _disabledActions.Value = string.Join(",", _router.DisabledActions);

            _invertDurationSeconds.Value = (int)_chaos.InvertDurationSeconds;
            _noJumpDurationSeconds.Value = (int)_chaos.NoJumpDurationSeconds;
            _poopLifetimeSeconds.Value = (int)_chaos.PoopLifetimeSeconds;
            _yeetUpForce.Value = _chaos.YeetUpForce;
            _yeetSidewaysForce.Value = _chaos.YeetSidewaysForce;
            _ragdollUpForce.Value = _chaos.RagdollUpForce;
            _ragdollSidewaysForce.Value = _chaos.RagdollSidewaysForce;
            _addMoneyAmount.Value = _chaos.AddMoneyAmount;
            _removeMoneyAmount.Value = _chaos.RemoveMoneyAmount;
            _earthquakeRagdollUpForce.Value = _chaos.EarthquakeRagdollUpForce;
            _earthquakeRagdollSidewaysForce.Value = _chaos.EarthquakeRagdollSidewaysForce;
            _gravityDurationSeconds.Value = (int)_chaos.GravityDurationSeconds;
            _gravityLowMultiplier.Value = _chaos.GravityLowMultiplier;
            _gravityHighMultiplier.Value = _chaos.GravityHighMultiplier;
            _fireSaleDurationSeconds.Value = (int)_chaos.FireSaleDurationSeconds;
            _holdEffectsWhileMenuOpen.Value = _chaos.HoldEffectsWhileMenuOpen;

            _passiveIncomeAmount.Value = _points.PassiveIncomeAmount;
            _passiveIncomeIntervalSeconds.Value = _points.PassiveIncomeIntervalSeconds;
            _startingBalanceViewer.Value = _router.StartingBalanceViewer;
            _startingBalanceFollower.Value = _router.StartingBalanceFollower;
            _startingBalanceVipMod.Value = _router.StartingBalanceVipMod;
            _subscriberPointsPerTier.Value = _router.SubscriberPointsPerTier;
            _giftedSubPointsPerTier.Value = _router.GiftedSubPointsPerTier;
            _bitsToPointsRatio.Value = _router.BitsToPointsRatio;

            _pollDurationSeconds.Value = (int)_pollManager.PollDurationSeconds;
            _pollAutoIntervalMinutes.Value = (int)(_pollManager.AutoIntervalSeconds / 60f);
            _pollOptionCount.Value = _pollManager.OptionCount;

            _overlayEnabled.Value = _overlay.IsRunning;

            Config.SaveOnConfigSet = true;
            Config.Save();
            Log.LogInfo("ModMenu: current settings saved to the config file.");
        }

        private void BindConfig()
        {
            _channelName = Config.Bind("Twitch", "ChannelName", "", "Twitch channel to join, without the leading #.");
            _botUsername = Config.Bind("Twitch", "BotUsername", "", "Twitch account the bot logs in as (can be the streamer's own account).");
            _oauthToken = Config.Bind("Twitch", "OAuthToken", "", "OAuth token for BotUsername (chat:read + chat:edit scopes), e.g. 'oauth:xxxxxxxx' from https://twitchtokengenerator.com/. Keep this secret.");
            _clientId = Config.Bind("Twitch", "ClientId", "", "Twitch application Client ID (shown alongside the token on https://twitchtokengenerator.com/) - only needed to show chatters' profile pictures on the OBS overlay. Leave blank to skip avatars.");
            _followerCheckClientId = Config.Bind("Twitch", "FollowerCheckClientId", "", "Client ID of a Twitch Developer Console app (https://dev.twitch.tv/console) - only needed for the 'follower' starting-balance tier. Separate from ClientId above. Leave blank to skip follower detection (everyone gets StartingBalanceViewer instead).");
            _followerCheckOAuthToken = Config.Bind("Twitch", "FollowerCheckOAuthToken", "", "User access token for the BROADCASTER's own Twitch account (not the bot's) with the moderator:read:followers scope - see README for how to generate one. Keep this secret.");

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
            _priceAddMoney = Config.Bind("Prices", "AddMoney", 200, "Point cost of '!buy addmoney' - adds AddMoneyAmount to the game's own in-park money via FinanceSystem.");
            _priceRemoveMoney = Config.Bind("Prices", "RemoveMoney", 200, "Point cost of '!buy removemoney' - drains RemoveMoneyAmount from the game's own in-park money via FinanceSystem.");
            _priceEarthquake = Config.Bind("Prices", "Earthquake", 350, "Point cost of '!buy earthquake' - ragdolls every in-park guest at once.");
            _priceGravity = Config.Bind("Prices", "Gravity", 300, "Point cost of '!buy gravity' - randomly makes the streamer floaty or heavy for a while.");
            _priceShuffle = Config.Bind("Prices", "Shuffle", 180, "Point cost of '!buy shuffle' - cycles the streamer to their next held item.");
            _priceFireSale = Config.Bind("Prices", "FireSale", 150, "Point cost of '!buy firesale' - crashes ticket price to $0 for a while.");
            _priceSwarm = Config.Bind("Prices", "Swarm", 200, "Point cost of '!buy swarm' - triggers the game's own seagull attack park event.");
            _priceTornado = Config.Bind("Prices", "Tornado", 300, "Point cost of '!buy tornado' - triggers the game's own tornado park event.");
            _priceUfo = Config.Bind("Prices", "Ufo", 300, "Point cost of '!buy ufo' - triggers the game's own UFO park event.");
            _priceMafia = Config.Bind("Prices", "Mafia", 300, "Point cost of '!buy mafia' - triggers the game's own mafia park event.");
            _priceItemsRain = Config.Bind("Prices", "ItemsRain", 250, "Point cost of '!buy itemsrain' - triggers the game's own items-raining-from-the-sky park event.");
            _priceQueso = Config.Bind("Prices", "Queso", 250, "Point cost of '!buy queso' - triggers the game's own Queso park event.");

            _poopLifetimeSeconds = Config.Bind("Chaos", "PoopLifetimeSeconds", 90, "How long (seconds) a '!buy poop' clone stays in the world before despawning - it can't be picked up/cleaned by anything in-game, so it self-destructs instead.");
            _yeetUpForce = Config.Bind("Chaos", "YeetUpForce", 500f, "Upward impulse force for '!buy yeet'. The original 1500 sent guests flying far enough to land off the NavMesh and get silently despawned by the game - lower this further if guests still disappear, raise it if the yeet looks too weak.");
            _yeetSidewaysForce = Config.Bind("Chaos", "YeetSidewaysForce", 150f, "Random horizontal impulse force for '!buy yeet' (see YeetUpForce).");
            _ragdollUpForce = Config.Bind("Chaos", "RagdollUpForce", 250f, "Upward force for '!buy ragdoll' (passed to PlayerRagdollSystem.EnableRagdollTemp). The original 800 sent the streamer flying high enough to clear map barriers and get stuck outside the playable area - lower this further if that still happens, raise it if the ragdoll looks too weak.");
            _ragdollSidewaysForce = Config.Bind("Chaos", "RagdollSidewaysForce", 150f, "Random horizontal force + torque for '!buy ragdoll' (see RagdollUpForce).");
            _addMoneyAmount = Config.Bind("Chaos", "AddMoneyAmount", 500f, "In-game money added by '!buy addmoney' (via FinanceSystem.ForceChangeMoney) - separate from the point cost above. The original 5000 default was confirmed live to be excessive - lower further if 500 still feels like too much.");
            _removeMoneyAmount = Config.Bind("Chaos", "RemoveMoneyAmount", 500f, "In-game money drained by '!buy removemoney' (via FinanceSystem.ForceChangeMoney) - separate from the point cost above. The original 5000 default was confirmed live to be excessive - lower further if 500 still feels like too much.");
            _earthquakeRagdollUpForce = Config.Bind("Chaos", "EarthquakeRagdollUpForce", 150f, "Upward force applied to EVERY in-park guest for '!buy earthquake' (see RagdollUpForce for the equivalent player-only setting) - kept lower by default since it affects the whole park at once.");
            _earthquakeRagdollSidewaysForce = Config.Bind("Chaos", "EarthquakeRagdollSidewaysForce", 100f, "Random horizontal force + torque for '!buy earthquake' (see EarthquakeRagdollUpForce).");
            _gravityDurationSeconds = Config.Bind("Chaos", "GravityDurationSeconds", 15, "How long (seconds) '!buy gravity' lasts before reverting.");
            _gravityLowMultiplier = Config.Bind("Chaos", "GravityLowMultiplier", 0.2f, "Gravity multiplier for '!buy gravity's floaty outcome (randomly picked 50/50 against GravityHighMultiplier each time).");
            _gravityHighMultiplier = Config.Bind("Chaos", "GravityHighMultiplier", 3f, "Gravity multiplier for '!buy gravity's heavy outcome (see GravityLowMultiplier).");
            _fireSaleDurationSeconds = Config.Bind("Chaos", "FireSaleDurationSeconds", 60, "How long (seconds) '!buy firesale' keeps ticket price at $0 before reverting to whatever it actually was.");
            _holdEffectsWhileMenuOpen = Config.Bind("Chaos", "HoldEffectsWhileMenuOpen", true, "If true, chaos effects (paid or free chat-vote wins) are held until a menu that appears to be open closes, instead of firing behind it. Uses Cursor lock state as a heuristic - unconfirmed against this game's actual menu system, so turn this off if it misfires (e.g. holds effects while just walking around).");

            // '!buy invert' flips the game's own Settings menu "Invert Y Axis (Player)" toggle
            // directly (see ChaosController.InvertControls); '!buy nojump' patches the game's
            // real InputSystem.OnJump (see Chaos/PlayerInputSabotage.cs), found by decoding
            // Assembly-CSharp.dll after the original UnityEngine.Input-based approach was
            // confirmed live to do nothing.
            _invertDurationSeconds = Config.Bind("PlayerSabotage", "InvertDurationSeconds", 15, "How long (seconds) '!buy invert' reverses the camera's Y axis for.");
            _noJumpDurationSeconds = Config.Bind("PlayerSabotage", "NoJumpDurationSeconds", 15, "How long (seconds) '!buy nojump' disables jumping for.");

            _overlayEnabled = Config.Bind("Overlay", "Enabled", true, "Whether to run the local web overlay for OBS's Browser Source (see README).");
            _overlayPort = Config.Bind("Overlay", "Port", 9412, "Port for the local overlay web server. Point an OBS Browser Source at http://localhost:<port>/overlay.html.");

            _pollDurationSeconds = Config.Bind("Poll", "DurationSeconds", 45, "How long (seconds) a chaos vote poll stays open for voting once started.");
            _pollAutoIntervalMinutes = Config.Bind("Poll", "AutoIntervalMinutes", 20, "How often (minutes) a chaos vote poll starts automatically. Set to 0 to disable automatic polls - moderators/broadcaster can still start one on demand with '!startpoll'.");
            _pollOptionCount = Config.Bind("Poll", "OptionCount", 2, "How many options to offer per chaos vote poll (minimum 2) - default is a straight 1-vs-2 vote.");

            // Role/event-based point grants, on top of passive income (see PassiveIncome above).
            // The follower tier only applies if Twitch.FollowerCheckClientId/OAuthToken are both
            // filled in (see TwitchFollowerProvider) - until then, anyone who isn't
            // VIP/mod/broadcaster gets StartingBalanceViewer.
            _startingBalanceViewer = Config.Bind("Points", "StartingBalanceViewer", 250, "Starting point balance for a brand-new viewer (no follower/VIP/mod role) the first time they're ever seen in chat. Doesn't retroactively change anyone's existing balance.");
            _startingBalanceFollower = Config.Bind("Points", "StartingBalanceFollower", 500, "Starting point balance for a brand-new viewer who follows the channel, the first time they're ever seen in chat. Requires Twitch.FollowerCheckClientId/OAuthToken to be set - see README.");
            _startingBalanceVipMod = Config.Bind("Points", "StartingBalanceVipMod", 1000, "Starting point balance for a brand-new VIP, moderator, or the broadcaster themself, the first time they're ever seen in chat.");
            _subscriberPointsPerTier = Config.Bind("Points", "SubscriberPointsPerTier", 500, "Points awarded per subscription tier (1/2/3 - Prime counts as tier 1) on every new subscription AND every monthly resub.");
            _giftedSubPointsPerTier = Config.Bind("Points", "GiftedSubPointsPerTier", 500, "Points awarded to the GIFTER (not the recipient) per subscription tier, per sub gifted - including once per sub in a mass/community gift.");
            _bitsToPointsRatio = Config.Bind("Points", "BitsToPointsRatio", 1, "Points awarded per bit cheered.");

            // Normally only ever written by ModMenu's "Save to config file" button (see
            // SaveMenuChangesToConfig) - hand-editing is fine too, comma-separated action names
            // (e.g. "break,swarm"), matching the same names used in the [Prices] section above.
            _disabledActions = Config.Bind("Enabled", "DisabledActions", "", "Comma-separated list of chaos action names currently turned off - normally managed from the in-game F9 settings menu rather than edited here directly.");

            // action name -> its point-cost ConfigEntry - used by SaveMenuChangesToConfig to write
            // ModMenu's live price edits back to the .cfg file.
            _priceConfigEntries = new Dictionary<string, ConfigEntry<int>>(StringComparer.OrdinalIgnoreCase)
            {
                ["yeet"] = _priceYeet,
                ["poop"] = _pricePoop,
                ["break"] = _priceBreak,
                ["ragdoll"] = _priceRagdoll,
                ["invert"] = _priceInvert,
                ["nojump"] = _priceNoJump,
                ["drop"] = _priceDrop,
                ["vomit"] = _priceVomit,
                ["pee"] = _pricePee,
                ["trash"] = _priceTrash,
                ["addmoney"] = _priceAddMoney,
                ["removemoney"] = _priceRemoveMoney,
                ["earthquake"] = _priceEarthquake,
                ["gravity"] = _priceGravity,
                ["shuffle"] = _priceShuffle,
                ["firesale"] = _priceFireSale,
                ["swarm"] = _priceSwarm,
                ["tornado"] = _priceTornado,
                ["ufo"] = _priceUfo,
                ["mafia"] = _priceMafia,
                ["itemsrain"] = _priceItemsRain,
                ["queso"] = _priceQueso,
            };
        }
    }
}
