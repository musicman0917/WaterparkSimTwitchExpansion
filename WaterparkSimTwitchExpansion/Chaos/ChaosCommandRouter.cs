using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using Newtonsoft.Json;
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
        private readonly Core.OnScreenNotifier _notifier;
        private readonly Core.OverlayServer _overlay;
        private readonly TwitchAvatarProvider _avatarProvider;
        private readonly TwitchFollowerProvider _followerProvider;
        private readonly IReadOnlyDictionary<string, int> _prices;
        private readonly int _startingBalanceViewer;
        private readonly int _startingBalanceFollower;
        private readonly int _startingBalanceVipMod;
        private readonly int _subscriberPointsPerTier;
        private readonly int _giftedSubPointsPerTier;
        private readonly int _bitsToPointsRatio;

        /// <summary>Optional - posts a reply to Twitch chat for every successful redemption. Set this
        /// after constructing TwitchChatConnector (e.g. to its SendMessage method). Left null, chat
        /// just doesn't get a reply.</summary>
        public Action<string> SendChatMessage { get; set; }

        /// <summary>Optional - set after construction (avoids a circular constructor dependency,
        /// since ChaosPollManager itself needs a ChaosCommandRouter to run its winning option and
        /// list poll options). Wires "!startpoll" through to it. Left null, "!startpoll" is a no-op.</summary>
        public ChaosPollManager PollManager { get; set; }

        /// <param name="prices">action name -> point cost, e.g. { "yeet", 100 }, { "poop", 150 }, { "break", 300 }.</param>
        /// <param name="notifier">Optional - draws an on-screen line for every redemption. Null is fine (just skips the on-screen text).</param>
        /// <param name="overlay">Optional - pushes a themed toast to the OBS browser overlay for every redemption. Null is fine (just skips it).</param>
        /// <param name="avatarProvider">Optional - looks up the redeemer's Twitch profile picture for the overlay toast. Null is fine (toast just shows the action icon instead).</param>
        /// <param name="followerProvider">Optional - checks follower status for the starting-balance follower tier. Null skips that tier entirely (everyone who isn't VIP/mod/broadcaster gets startingBalanceViewer) - see TwitchFollowerProvider's doc comment for the setup this needs.</param>
        /// <param name="startingBalanceViewer">Starting point balance for a brand-new viewer with no other role.</param>
        /// <param name="startingBalanceFollower">Starting point balance for a brand-new viewer who follows the channel (requires followerProvider - see above).</param>
        /// <param name="startingBalanceVipMod">Starting point balance for a brand-new VIP/moderator/broadcaster.</param>
        /// <param name="subscriberPointsPerTier">Points granted per subscription tier (1/2/3, Prime counted as 1) on every new sub AND every monthly resub.</param>
        /// <param name="giftedSubPointsPerTier">Points granted to the GIFTER per subscription tier, per sub gifted (including each sub in a mass gift).</param>
        /// <param name="bitsToPointsRatio">Points granted per bit cheered.</param>
        public ChaosCommandRouter(
            ManualLogSource log,
            PointsManager points,
            ChaosController chaos,
            MainThreadDispatcher dispatcher,
            Core.OnScreenNotifier notifier,
            Core.OverlayServer overlay,
            TwitchAvatarProvider avatarProvider,
            TwitchFollowerProvider followerProvider,
            IReadOnlyDictionary<string, int> prices,
            int startingBalanceViewer = 250,
            int startingBalanceFollower = 500,
            int startingBalanceVipMod = 1000,
            int subscriberPointsPerTier = 500,
            int giftedSubPointsPerTier = 500,
            int bitsToPointsRatio = 1)
        {
            _log = log;
            _points = points;
            _chaos = chaos;
            _dispatcher = dispatcher;
            _notifier = notifier;
            _overlay = overlay;
            _avatarProvider = avatarProvider;
            _followerProvider = followerProvider;
            _prices = prices;
            _startingBalanceViewer = startingBalanceViewer;
            _startingBalanceFollower = startingBalanceFollower;
            _startingBalanceVipMod = startingBalanceVipMod;
            _subscriberPointsPerTier = subscriberPointsPerTier;
            _giftedSubPointsPerTier = giftedSubPointsPerTier;
            _bitsToPointsRatio = bitsToPointsRatio;
        }

        /// <summary>Subscribe to TwitchChatConnector.OnChatMessage with this.</summary>
        public void HandleChatMessage(ChatActivity activity)
        {
            // HasAccount is a cheap in-memory check - only bother computing a starting balance
            // (which, for a new viewer, may involve a blocking Helix follower-status call) the
            // one time it'll actually be used, not on every single message from every viewer.
            var startingBalance = _points.HasAccount(activity.Username) ? 0 : StartingBalanceFor(activity);
            _points.RegisterActivity(activity.Username, activity.DisplayName, startingBalance);
        }

        /// <summary>
        /// New-viewer starting balance by role - only ever called for a viewer's first-ever
        /// message (see HandleChatMessage), and only applied if PointsManager is creating their
        /// account for the first time; existing balances are never touched. VIP/mod/broadcaster
        /// takes priority over follower, which takes priority over the plain viewer amount.
        /// </summary>
        private int StartingBalanceFor(ChatActivity activity)
        {
            if (activity.IsModerator || activity.IsVip || activity.IsBroadcaster)
            {
                return _startingBalanceVipMod;
            }

            if (_followerProvider != null && _followerProvider.IsFollower(activity.Username))
            {
                return _startingBalanceFollower;
            }

            return _startingBalanceViewer;
        }

        /// <summary>Subscribe to TwitchChatConnector.OnSubscription with this - fires for every new
        /// subscription AND every monthly resub.</summary>
        public void HandleSubscription(string username, string displayName, int tier)
        {
            var amount = tier * _subscriberPointsPerTier;
            _points.AddPoints(username, displayName, amount);
            _log.LogInfo($"{displayName} subscribed (tier {tier}) - awarded {amount} points.");
            Announce($"@{displayName} thanks for subscribing! +{amount} points.");
        }

        /// <summary>Subscribe to TwitchChatConnector.OnGiftedSub with this - fires once per gifted
        /// sub, for the GIFTER (not the recipient), including once per sub in a mass gift.</summary>
        public void HandleGiftedSub(string gifterUsername, string gifterDisplayName, int tier)
        {
            var amount = tier * _giftedSubPointsPerTier;
            _points.AddPoints(gifterUsername, gifterDisplayName, amount);
            _log.LogInfo($"{gifterDisplayName} gifted a tier {tier} sub - awarded {amount} points.");
            Announce($"@{gifterDisplayName} thanks for the gift sub! +{amount} points.");
        }

        /// <summary>Subscribe to TwitchChatConnector.OnBitsCheered with this.</summary>
        public void HandleBitsCheered(string username, string displayName, int bits)
        {
            var amount = bits * _bitsToPointsRatio;
            _points.AddPoints(username, displayName, amount);
            _log.LogInfo($"{displayName} cheered {bits} bits - awarded {amount} points.");
            Announce($"@{displayName} thanks for the bits! +{amount} points.");
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
                    // Balance lookups don't touch UnityEngine, so no dispatcher hop is needed here.
                    var balance = _points.GetBalance(command.Username);
                    _log.LogInfo($"{command.DisplayName} has {balance} points.");
                    SendChatMessage?.Invoke($"@{command.DisplayName} you have {balance} points.");
                    break;

                case "commands":
                case "help":
                    HandleListCommands(command);
                    break;

                case "scantags":
                    // Diagnostic only - see ChaosController.ScanTags(). Not gated behind points/roles
                    // since it's read-only and temporary; remove once real tags are confirmed.
                    _dispatcher.Enqueue(() => _chaos.ScanTags());
                    break;

                case "scanmoney":
                    // Diagnostic only - see ChaosController.ScanMoney().
                    _dispatcher.Enqueue(() => _chaos.ScanMoney());
                    break;

                case "scanpoop":
                    // Diagnostic only - see ChaosController.ScanPoop().
                    _dispatcher.Enqueue(() => _chaos.ScanPoop());
                    break;

                case "scan":
                    // Diagnostic only - see ChaosController.Scan(). "!scan <term>" logs every
                    // GameObject matching <term> with its tag/position/components, e.g.
                    // "!scan pool" to see every false-positive-prone match up front.
                    var scanTerm = command.ArgOrDefault(0);
                    _dispatcher.Enqueue(() => _chaos.Scan(scanTerm));
                    break;

                case "give":
                    HandleGive(command);
                    break;

                case "startpoll":
                    HandleStartPoll(command);
                    break;
            }
        }

        /// <summary>"!commands"/"!help" - everyone. Lists every "!buy &lt;action&gt;" and its point
        /// cost in chat, built from the same price table !buy itself checks against so it can
        /// never drift out of sync with the real prices (including any changes the streamer made
        /// in the config).</summary>
        private void HandleListCommands(ChatCommand command)
        {
            var actionList = string.Join(", ", _prices.Select(kvp => $"!buy {kvp.Key} ({kvp.Value}pts)"));
            SendChatMessage?.Invoke($"Chaos commands: {actionList} | !balance to check your points, !startpoll for mods.");
            _log.LogInfo($"{command.DisplayName} used !commands.");
        }

        /// <summary>"!startpoll" - moderator/broadcaster only. Kicks off a free chat-vote poll
        /// on-demand (polls also fire automatically on a timer - see ChaosPollManager).</summary>
        private void HandleStartPoll(ChatCommand command)
        {
            if (!command.IsModerator && !command.IsBroadcaster)
            {
                _log.LogInfo($"{command.DisplayName} tried to use !startpoll but isn't a mod/broadcaster.");
                return;
            }

            _dispatcher.Enqueue(() => PollManager?.StartPoll());
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

            var targetUsername = command.ArgOrDefault(0)?.TrimStart('@');
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

            var displayName = command.DisplayName;

            // Blocking Helix HTTP call - done here (still on the Twitch background thread, not
            // Unity's) rather than inside the dispatched lambda below, so a slow/failed lookup
            // can never stall a game frame. Cached after the first hit per username.
            var avatarUrl = _avatarProvider?.GetProfileImageUrl(command.Username);

            // Hop onto Unity's main thread before touching any GameObject/Rigidbody/etc.
            _dispatcher.Enqueue(() =>
            {
                if (Execute(action, out var targetName))
                {
                    var description = DescribeAction(action, targetName);
                    _notifier?.Show($"{displayName} {description}! (-{cost} pts)");
                    SendChatMessage?.Invoke($"@{displayName} {description}! (-{cost} pts)");
                    _overlay?.Broadcast("redemption", JsonConvert.SerializeObject(new { displayName, description, action, cost, avatarUrl, targetName }));
                }
            });
        }

        /// <summary>
        /// Runs a chaos action for free (bypassing !buy's point cost) and announces it the same
        /// way a successful !buy does (on-screen notifier, chat message, overlay toast) - used by
        /// ChaosPollManager to fire a poll's winning option. Must be called from Unity's main
        /// thread (same rule as everything in ChaosController).
        /// </summary>
        /// <param name="announcedAs">Shown in place of a Twitch display name, e.g. "Chat vote".</param>
        public bool ExecuteFree(string action, string announcedAs)
        {
            if (!Execute(action, out var targetName))
            {
                return false;
            }

            var description = DescribeAction(action, targetName);
            _notifier?.Show($"{announcedAs} {description}!");
            SendChatMessage?.Invoke($"{announcedAs} {description}!");
            _overlay?.Broadcast("redemption", JsonConvert.SerializeObject(new { displayName = announcedAs, description, action, cost = 0, avatarUrl = (string)null, targetName }));
            return true;
        }

        /// <summary>Generic (no specific target yet) description of an action, e.g. "yeeted a
        /// guest" - used by ChaosPollManager to list poll options in chat before anyone's voted.</summary>
        public string DescribeActionForPoll(string action) => DescribeAction(action, targetName: null);

        /// <summary>Pushes a "poll_started" SSE event so the overlay can draw the live poll widget
        /// (options + a countdown) - used by ChaosPollManager.</summary>
        public void BroadcastPollStarted(string[] optionDescriptions, float durationSeconds)
        {
            _overlay?.Broadcast("poll_started", JsonConvert.SerializeObject(new { options = optionDescriptions, durationSeconds }));
        }

        /// <summary>Pushes a "poll_votes" SSE event with the current tally (same order as the
        /// options passed to BroadcastPollStarted) so the overlay's poll widget updates live as
        /// votes come in - used by ChaosPollManager.</summary>
        public void BroadcastPollVotes(int[] counts)
        {
            _overlay?.Broadcast("poll_votes", JsonConvert.SerializeObject(new { counts }));
        }

        /// <summary>Pushes a "poll_ended" SSE event with the final tally and winning option's index
        /// (-1 if nobody voted) so the overlay's poll widget can highlight the winner before fading
        /// out - used by ChaosPollManager.</summary>
        public void BroadcastPollEnded(int winnerIndex, int[] counts)
        {
            _overlay?.Broadcast("poll_ended", JsonConvert.SerializeObject(new { winnerIndex, counts }));
        }

        /// <summary>Posts a plain announcement (on-screen notifier + chat), not tied to any specific
        /// redemption - used by ChaosPollManager for poll start/result messages.</summary>
        public void Announce(string message)
        {
            _notifier?.Show(message);
            SendChatMessage?.Invoke(message);
        }

        /// <param name="targetName">The specific NPC/thing the action landed on, if any (e.g. the
        /// yeeted guest's name) - null for actions that don't have one.</param>
        private static string DescribeAction(string action, string targetName) => action switch
        {
            "yeet" => targetName != null ? $"just yeeted NPC {targetName}" : "yeeted a guest",
            "poop" => "dropped poop in a pool",
            "break" => "broke a waterslide",
            "ragdoll" => "ragdolled the streamer",
            "invert" => "inverted the streamer's camera controls",
            "nojump" => "disabled the streamer's jump",
            "drop" => "made the streamer drop their item",
            "vomit" => targetName != null ? $"made NPC {targetName} throw up" : "made a guest throw up",
            "pee" => targetName != null ? $"made NPC {targetName} pee" : "made a guest pee",
            "trash" => targetName != null ? $"made NPC {targetName} litter" : "made a guest litter",
            _ => $"triggered '{action}'",
        };

        /// <param name="targetName">See DescribeAction - set only by actions that have a specific
        /// target (currently just yeet), null otherwise.</param>
        private bool Execute(string action, out string targetName)
        {
            bool success;
            targetName = null;
            switch (action)
            {
                case "yeet":
                    success = _chaos.YeetGuest(out targetName);
                    break;
                case "poop":
                    success = _chaos.SpawnPoop();
                    break;
                case "break":
                    success = _chaos.SabotageSlide();
                    break;
                case "ragdoll":
                    success = _chaos.RagdollPlayer();
                    break;
                case "invert":
                    success = _chaos.InvertControls();
                    break;
                case "nojump":
                    success = _chaos.DisableJump();
                    break;
                case "drop":
                    success = _chaos.DropItem();
                    break;
                case "vomit":
                    success = _chaos.MakeGuestVomit(out targetName);
                    break;
                case "pee":
                    success = _chaos.MakeGuestPee(out targetName);
                    break;
                case "trash":
                    success = _chaos.MakeGuestLitter(out targetName);
                    break;
                default:
                    success = false;
                    break;
            }

            if (!success)
            {
                _log.LogWarning($"Chaos action '{action}' failed to execute (see warnings above).");
            }

            return success;
        }
    }
}
