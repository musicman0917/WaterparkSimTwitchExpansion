using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using WaterparkSimTwitchExpansion.Chaos;
using WaterparkSimTwitchExpansion.Core;
using WaterparkSimTwitchExpansion.Economy;
using WaterparkSimTwitchExpansion.Twitch;

namespace WaterparkSimTwitchExpansion
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
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
        private ConfigEntry<int> _autosaveIntervalSeconds;

        // --- Runtime pieces ---
        private MainThreadDispatcher _dispatcher;
        private PointsManager _points;
        private ChaosController _chaos;
        private ChaosCommandRouter _router;
        private TwitchChatConnector _twitch;

        private float _secondsSinceAutosave;

        private void Awake()
        {
            BindConfig();

            _dispatcher = new MainThreadDispatcher();

            var savePath = Path.Combine(Paths.ConfigPath, "waterpark_twitch_points.json");
            _points = new PointsManager(
                Logger,
                savePath,
                passiveIncomeAmount: _passiveIncomeAmount.Value,
                passiveIncomeInterval: TimeSpan.FromSeconds(_passiveIncomeIntervalSeconds.Value));
            _points.Load();

            _chaos = new ChaosController(Logger);

            var prices = new Dictionary<string, int>
            {
                ["yeet"] = _priceYeet.Value,
                ["poop"] = _pricePoop.Value,
                ["break"] = _priceBreak.Value,
            };
            _router = new ChaosCommandRouter(Logger, _points, _chaos, _dispatcher, prices);

            if (string.IsNullOrWhiteSpace(_channelName.Value) || string.IsNullOrWhiteSpace(_oauthToken.Value))
            {
                Logger.LogWarning("Twitch channel/OAuth token not configured yet - edit the config file and restart. Skipping connection.");
                return;
            }

            _twitch = new TwitchChatConnector(Logger, _botUsername.Value, _oauthToken.Value, _channelName.Value);
            _twitch.OnChatMessage += _router.HandleChatMessage;
            _twitch.OnChatCommand += _router.HandleChatCommand;
            _twitch.Connect();

            Logger.LogInfo($"{PluginName} v{PluginVersion} loaded.");
        }

        private void Update()
        {
            // Drain any chaos actions queued from Twitch background-thread events.
            _dispatcher.ProcessQueue();

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

        private void OnDestroy()
        {
            _points?.Save();
            if (_twitch != null)
            {
                _twitch.OnChatMessage -= _router.HandleChatMessage;
                _twitch.OnChatCommand -= _router.HandleChatCommand;
                _twitch.Dispose();
            }
        }

        private void BindConfig()
        {
            _channelName = Config.Bind("Twitch", "ChannelName", "", "Twitch channel to join, without the leading #.");
            _botUsername = Config.Bind("Twitch", "BotUsername", "", "Twitch account the bot logs in as (can be the streamer's own account).");
            _oauthToken = Config.Bind("Twitch", "OAuthToken", "", "OAuth token for BotUsername, e.g. 'oauth:xxxxxxxx' from https://twitchapps.com/tmi/. Keep this secret.");

            _passiveIncomeAmount = Config.Bind("Economy", "PassiveIncomeAmount", 10, "Points paid to each active chatter per interval.");
            _passiveIncomeIntervalSeconds = Config.Bind("Economy", "PassiveIncomeIntervalSeconds", 60, "How often (seconds) passive income is paid out.");
            _autosaveIntervalSeconds = Config.Bind("Economy", "AutosaveIntervalSeconds", 120, "How often (seconds) the points file is saved to disk.");

            _priceYeet = Config.Bind("Prices", "Yeet", 100, "Point cost of '!buy yeet'.");
            _pricePoop = Config.Bind("Prices", "Poop", 150, "Point cost of '!buy poop'.");
            _priceBreak = Config.Bind("Prices", "Break", 300, "Point cost of '!buy break'.");
        }
    }
}
