using System;
using System.Collections.Generic;
using BepInEx.Logging;
using WaterparkSimTwitchExpansion.Core;
using WaterparkSimTwitchExpansion.Economy;
using WaterparkSimTwitchExpansion.Twitch;

namespace WaterparkSimTwitchExpansion.Chaos
{
    /// <summary>
    /// Wires "!buy [action]" chat commands to the point economy and, once a purchase is
    /// affordable, to the actual chaos effect. This is the "glue" layer referenced from Plugin.cs.
    /// </summary>
    public sealed class ChaosCommandRouter
    {
        private readonly ManualLogSource _log;
        private readonly PointsManager _points;
        private readonly ChaosController _chaos;
        private readonly MainThreadDispatcher _dispatcher;
        private readonly IReadOnlyDictionary<string, int> _prices;

        /// <param name="prices">action name -> point cost, e.g. { "yeet", 100 }, { "poop", 150 }, { "break", 300 }.</param>
        public ChaosCommandRouter(
            ManualLogSource log,
            PointsManager points,
            ChaosController chaos,
            MainThreadDispatcher dispatcher,
            IReadOnlyDictionary<string, int> prices)
        {
            _log = log;
            _points = points;
            _chaos = chaos;
            _dispatcher = dispatcher;
            _prices = prices;
        }

        /// <summary>Subscribe to TwitchChatConnector's events with this.</summary>
        public void HandleChatMessage(string username, string displayName)
        {
            _points.RegisterActivity(username, displayName);
        }

        /// <summary>Subscribe to TwitchChatConnector.OnChatCommand with this.</summary>
        public void HandleChatCommand(ChatCommand command)
        {
            switch (command.Action)
            {
                case "buy":
                    HandleBuy(command);
                    break;

                case "balance":
                case "points":
                    // Balance lookups don't touch UnityEngine, so no dispatcher hop is needed here;
                    // wire this into your own chat-reply/whisper logic if desired.
                    var balance = _points.GetBalance(command.Username);
                    _log.LogInfo($"{command.DisplayName} has {balance} points.");
                    break;

                case "scantags":
                    // Diagnostic only - see ChaosController.ScanTags(). Not gated behind points/roles
                    // since it's read-only and temporary; remove once real tags are confirmed.
                    _dispatcher.Enqueue(() => _chaos.ScanTags());
                    break;

                case "give":
                    HandleGive(command);
                    break;
            }
        }

        /// <summary>"!give &lt;username&gt; &lt;amount&gt;" - moderator/broadcaster only, e.g. to correct a
        /// balance or hand out points for a giveaway without waiting on passive income.</summary>
        private void HandleGive(ChatCommand command)
        {
            if (!command.IsModerator && !command.IsBroadcaster)
            {
                _log.LogInfo($"{command.DisplayName} tried to use !give but isn't a mod/broadcaster.");
                return;
            }

            var targetUsername = command.ArgOrDefault(0);
            var amountText = command.ArgOrDefault(1);
            if (string.IsNullOrEmpty(targetUsername) || !int.TryParse(amountText, out var amount) || amount <= 0)
            {
                _log.LogInfo($"{command.DisplayName} sent !give with bad arguments (usage: !give <username> <amount>).");
                return;
            }

            _points.AddPoints(targetUsername, targetUsername, amount);
            _log.LogInfo($"{command.DisplayName} gave {amount} points to {targetUsername} (new balance: {_points.GetBalance(targetUsername)}).");
        }

        private void HandleBuy(ChatCommand command)
        {
            var action = command.ArgOrDefault(0)?.ToLowerInvariant();
            if (string.IsNullOrEmpty(action))
            {
                _log.LogInfo($"{command.DisplayName} sent !buy with no action.");
                return;
            }

            if (!_prices.TryGetValue(action, out var cost))
            {
                _log.LogInfo($"{command.DisplayName} tried to buy unknown action '{action}'.");
                return;
            }

            if (!_points.TrySpendPoints(command.Username, cost))
            {
                _log.LogInfo($"{command.DisplayName} tried to buy '{action}' ({cost} pts) but has only {_points.GetBalance(command.Username)}.");
                return;
            }

            _log.LogInfo($"{command.DisplayName} bought '{action}' for {cost} points.");

            // Hop onto Unity's main thread before touching any GameObject/Rigidbody/etc.
            _dispatcher.Enqueue(() => Execute(action));
        }

        private void Execute(string action)
        {
            bool success;
            switch (action)
            {
                case "yeet":
                    success = _chaos.YeetGuest();
                    break;
                case "poop":
                    success = _chaos.SpawnPoop();
                    break;
                case "break":
                    success = _chaos.SabotageSlide();
                    break;
                default:
                    success = false;
                    break;
            }

            if (!success)
            {
                _log.LogWarning($"Chaos action '{action}' failed to execute (see warnings above).");
            }
        }
    }
}
