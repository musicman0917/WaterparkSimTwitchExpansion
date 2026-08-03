using System;
using System.Collections.Generic;
using System.IO;
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
        private ConfigEntry<int> _passiveIncomeAmount;
        private ConfigEntry<int> _passiveIncomeIntervalSeconds;
        private ConfigEntry<int> _priceYeet;
        private ConfigEntry<int> _pricePoop;
        private ConfigEntry<int> _priceBreak;
        private ConfigEntry<int> _priceRagdoll;
        private ConfigEntry<int> _priceInvert;
        private ConfigEntry<int> _priceNoJump;
        private ConfigEntry<int> _priceDrop;
        private ConfigEntry<int> _autosaveIntervalSeconds;
        private ConfigEntry<int> _invertDurationSeconds;
        private ConfigEntry<int> _noJumpDurationSeconds;
        private ConfigEntry<string> _horizontalAxisName;
        private ConfigEntry<string> _verticalAxisName;
        private ConfigEntry<string> _jumpButtonName;
        private ConfigEntry<KeyCode> _jumpKeyCode;
        private ConfigEntry<KeyCode> _dropKeyCode;
        private ConfigEntry<bool> _overlayEnabled;
        private ConfigEntry<int> _overlayPort;

        // --- Runtime pieces ---
        private MainThreadDispatcher _dispatcher;
        private PointsManager _points;
        private ChaosController _chaos;
        private ChaosCommandRouter _router;
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

            _chaos = new ChaosController(Log, _invertDurationSeconds.Value, _noJumpDurationSeconds.Value);

            // EXPERIMENTAL - see Chaos/PlayerInputSabotage.cs for what this can and can't do.
            PlayerInputSabotage.Apply(Log);
            PlayerInputSabotage.HorizontalAxisName = _horizontalAxisName.Value;
            PlayerInputSabotage.VerticalAxisName = _verticalAxisName.Value;
            PlayerInputSabotage.JumpButtonName = _jumpButtonName.Value;
            PlayerInputSabotage.JumpKeyCode = _jumpKeyCode.Value;
            PlayerInputSabotage.DropKeyCode = _dropKeyCode.Value;

            var prices = new Dictionary<string, int>
            {
                ["yeet"] = _priceYeet.Value,
                ["poop"] = _pricePoop.Value,
                ["break"] = _priceBreak.Value,
                ["ragdoll"] = _priceRagdoll.Value,
                ["invert"] = _priceInvert.Value,
                ["nojump"] = _priceNoJump.Value,
                ["drop"] = _priceDrop.Value,
            };
            // Inject a MonoBehaviour to draw an on-screen line for every redemption (see
            // OnScreenNotifier for why this needs to be a MonoBehaviour rather than plain C#).
            var notifier = AddComponent<Core.OnScreenNotifier>();

            if (_overlayEnabled.Value)
            {
                _overlay = new Core.OverlayServer(Log, _overlayPort.Value);
                _overlay.Start();
            }

            _router = new ChaosCommandRouter(Log, _points, _chaos, _dispatcher, notifier, _overlay, prices);

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
            _twitch.OnChatMessage += _router.HandleChatMessage;
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

            // EXPERIMENTAL - see Chaos/PlayerInputSabotage.cs. These only work if the game reads
            // input via Unity's legacy Input Manager; the names/keys below are just Unity's
            // common defaults, not confirmed for this game.
            _invertDurationSeconds = Config.Bind("PlayerSabotage", "InvertDurationSeconds", 15, "How long (seconds) '!buy invert' reverses movement for.");
            _noJumpDurationSeconds = Config.Bind("PlayerSabotage", "NoJumpDurationSeconds", 15, "How long (seconds) '!buy nojump' disables jumping for.");
            _horizontalAxisName = Config.Bind("PlayerSabotage", "HorizontalAxisName", "Horizontal", "Unity Input axis name to invert for '!buy invert'. Change if the game uses a different name.");
            _verticalAxisName = Config.Bind("PlayerSabotage", "VerticalAxisName", "Vertical", "Unity Input axis name to invert for '!buy invert'. Change if the game uses a different name.");
            _jumpButtonName = Config.Bind("PlayerSabotage", "JumpButtonName", "Jump", "Unity Input button name to disable for '!buy nojump'. Change if the game uses a different name.");
            _jumpKeyCode = Config.Bind("PlayerSabotage", "JumpKeyCode", KeyCode.Space, "Fallback key to disable for '!buy nojump' if the game reads Input.GetKey directly instead of a named button.");
            _dropKeyCode = Config.Bind("PlayerSabotage", "DropKeyCode", KeyCode.G, "Key simulated by '!buy drop'. Change this to match whatever key the game actually binds to dropping a held item.");

            _overlayEnabled = Config.Bind("Overlay", "Enabled", true, "Whether to run the local web overlay for OBS's Browser Source (see README).");
            _overlayPort = Config.Bind("Overlay", "Port", 9412, "Port for the local overlay web server. Point an OBS Browser Source at http://localhost:<port>/overlay.html.");
        }
    }
}
