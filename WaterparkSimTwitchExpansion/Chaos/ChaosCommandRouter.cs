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
        // A mutable Dictionary (not IReadOnlyDictionary) - Core.ModMenu (the in-game F9 settings
        // panel) edits prices live via the Prices property below, same reference throughout.
        private readonly Dictionary<string, int> _prices;

        // Mutable public properties rather than constructor-only values - ModMenu sets these
        // directly at runtime so changes take effect immediately, no restart needed. Still seeded
        // from config at construction.
        public int StartingBalanceViewer { get; set; }
        public int StartingBalanceFollower { get; set; }
        public int StartingBalanceVipMod { get; set; }
        public int SubscriberPointsPerTier { get; set; }
        public int GiftedSubPointsPerTier { get; set; }
        public int BitsToPointsRatio { get; set; }

        /// <summary>Live, mutable action -> point cost map - same dictionary instance passed into
        /// the constructor, exposed so ModMenu can edit prices in place.</summary>
        public IDictionary<string, int> Prices => _prices;

        /// <summary>Actions ModMenu has turned off - checked by HandleBuy/ExecuteFree before
        /// spending points or running anything, so a disabled action is a clean no-op rather than
        /// something that fails partway through.</summary>
        public HashSet<string> DisabledActions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Chaos effects held by RunOrHoldForMenu/ExecuteFree because a menu appeared to
        /// be open when they were triggered - drained in order by ProcessHeldChaosActions once
        /// ChaosController.IsMenuOpen goes false again.</summary>
        private readonly Queue<Action> _heldWhileMenuOpen = new Queue<Action>();

        /// <summary>Optional - posts a reply to Twitch chat for every successful redemption. Set this
        /// after constructing TwitchChatConnector (e.g. to its SendMessage method). Left null, chat
        /// just doesn't get a reply.</summary>
        public Action<string> SendChatMessage { get; set; }

        /// <summary>Optional - set after construction (avoids a circular constructor dependency,
        /// since ChaosPollManager itself needs a ChaosCommandRouter to run its winning option and
        /// list poll options). Wires "!startpoll" through to it. Left null, "!startpoll" is a no-op.</summary>
        public ChaosPollManager PollManager { get; set; }

        /// <summary>Optional - set after construction, same reasoning as PollManager (
        /// ExtraLifeDonationTracker itself needs a ChaosCommandRouter to run its random-effect
        /// donations). Wires "!testdonation" through to it. Left null, "!testdonation" is a no-op.</summary>
        public ExtraLifeDonationTracker ExtraLifeTracker { get; set; }

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
            Dictionary<string, int> prices,
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
            StartingBalanceViewer = startingBalanceViewer;
            StartingBalanceFollower = startingBalanceFollower;
            StartingBalanceVipMod = startingBalanceVipMod;
            SubscriberPointsPerTier = subscriberPointsPerTier;
            GiftedSubPointsPerTier = giftedSubPointsPerTier;
            BitsToPointsRatio = bitsToPointsRatio;
        }

        /// <summary>How often an existing plain-viewer account gets re-checked for the one-time
        /// follow bonus. Bounds how often HandleChatMessage can trigger a blocking Helix call for a
        /// non-follower who chats a lot.</summary>
        private static readonly TimeSpan FollowBonusRecheckInterval = TimeSpan.FromMinutes(15);

        /// <summary>Subscribe to TwitchChatConnector.OnChatMessage with this.</summary>
        public void HandleChatMessage(ChatActivity activity)
        {
            // HasAccount is a cheap in-memory check - only bother computing a starting balance
            // (which, for a new viewer, may involve a blocking Helix follower-status call) the
            // one time it'll actually be used, not on every single message from every viewer.
            if (!_points.HasAccount(activity.Username))
            {
                var starting = StartingBalanceFor(activity);
                _points.RegisterActivity(activity.Username, activity.DisplayName, starting.Balance, starting.FollowBonusAlreadyApplied);
                return;
            }

            _points.RegisterActivity(activity.Username, activity.DisplayName);
            TryGrantFollowBonusIfDue(activity);
        }

        /// <summary>
        /// New-viewer starting balance by role - only ever called for a viewer's first-ever
        /// message (see HandleChatMessage), and only applied if PointsManager is creating their
        /// account for the first time; existing balances are never touched. VIP/mod/broadcaster
        /// takes priority over follower, which takes priority over the plain viewer amount. Also
        /// reports whether the future one-time follow bonus (see TryGrantFollowBonusIfDue) should
        /// be marked as already satisfied, since it wouldn't add anything for someone who's already
        /// at the follower tier or higher.
        /// </summary>
        private (int Balance, bool FollowBonusAlreadyApplied) StartingBalanceFor(ChatActivity activity)
        {
            if (activity.IsModerator || activity.IsVip || activity.IsBroadcaster)
            {
                return (StartingBalanceVipMod, true);
            }

            if (_followerProvider != null && _followerProvider.IsFollower(activity.Username))
            {
                return (StartingBalanceFollower, true);
            }

            return (StartingBalanceViewer, false);
        }

        /// <summary>
        /// Tops up an existing plain-viewer account by the follower/viewer difference the FIRST
        /// time they're seen following the channel, so following after your first message still
        /// earns the follower tier instead of being stuck at the plain-viewer starting balance
        /// forever. One-time per account (see PointsManager.TryGrantFollowBonus) - unfollowing and
        /// re-following will not grant it again. No-ops entirely if follower detection isn't
        /// configured (see TwitchFollowerProvider) or the account already has the bonus/doesn't
        /// need it.
        /// </summary>
        private void TryGrantFollowBonusIfDue(ChatActivity activity)
        {
            if (_followerProvider == null) return;
            if (activity.IsModerator || activity.IsVip || activity.IsBroadcaster) return;

            var bonus = StartingBalanceFollower - StartingBalanceViewer;
            if (bonus <= 0) return;

            if (!_points.ShouldCheckFollowBonus(activity.Username, FollowBonusRecheckInterval)) return;

            // Mark checked up-front regardless of outcome, so a non-follower who keeps chatting
            // only costs one Helix call per interval, not one per message.
            _points.MarkFollowChecked(activity.Username);

            if (!_followerProvider.IsFollower(activity.Username)) return;
            if (!_points.TryGrantFollowBonus(activity.Username, bonus)) return;

            _log.LogInfo($"{activity.DisplayName} is now following - awarded one-time +{bonus} point follow bonus.");
            // Announce() touches OnScreenNotifier, which is main-thread-only - this method runs on
            // the Twitch chat background thread, so hop over before calling it.
            _dispatcher.Enqueue(() => Announce($"@{activity.DisplayName} thanks for following! +{bonus} points."));
        }

        /// <summary>Subscribe to TwitchChatConnector.OnSubscription with this - fires for every new
        /// subscription AND every monthly resub.</summary>
        public void HandleSubscription(string username, string displayName, int tier)
        {
            var amount = tier * SubscriberPointsPerTier;
            _points.AddPoints(username, displayName, amount);
            _log.LogInfo($"{displayName} subscribed (tier {tier}) - awarded {amount} points.");
            Announce($"@{displayName} thanks for subscribing! +{amount} points.");
        }

        /// <summary>Subscribe to TwitchChatConnector.OnGiftedSub with this - fires once per gifted
        /// sub, for the GIFTER (not the recipient), including once per sub in a mass gift.</summary>
        public void HandleGiftedSub(string gifterUsername, string gifterDisplayName, int tier)
        {
            var amount = tier * GiftedSubPointsPerTier;
            _points.AddPoints(gifterUsername, gifterDisplayName, amount);
            _log.LogInfo($"{gifterDisplayName} gifted a tier {tier} sub - awarded {amount} points.");
            Announce($"@{gifterDisplayName} thanks for the gift sub! +{amount} points.");
        }

        /// <summary>Subscribe to TwitchChatConnector.OnBitsCheered with this.</summary>
        public void HandleBitsCheered(string username, string displayName, int bits)
        {
            var amount = bits * BitsToPointsRatio;
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

                case "waterparkcommands":
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

                case "testsub":
                    HandleTestSub(command);
                    break;

                case "testgift":
                    HandleTestGift(command);
                    break;

                case "testbits":
                    HandleTestBits(command);
                    break;

                case "testdonation":
                    HandleTestDonation(command);
                    break;

                case "give":
                    HandleGive(command);
                    break;

                case "startpoll":
                    HandleStartPoll(command);
                    break;
            }
        }

        /// <summary>"!waterparkcommands"/"!help" - everyone. Lists every "!buy &lt;action&gt;" and its
        /// point cost in chat, built from the same price table !buy itself checks against so it
        /// can never drift out of sync with the real prices (including any changes the streamer
        /// made in the config). Deliberately NOT named "!commands" - the streamer already runs
        /// other bots/tools that claim that name, so this mod stays out of the way instead of
        /// fighting over it.</summary>
        private void HandleListCommands(ChatCommand command)
        {
            var actionList = string.Join(", ", _prices.Select(kvp => $"!buy {kvp.Key} ({kvp.Value}pts)"));
            SendChatMessage?.Invoke($"Chaos commands: {actionList} | !balance to check your points, !startpoll for mods.");
            _log.LogInfo($"{command.DisplayName} used !waterparkcommands.");
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

        /// <summary>
        /// "!testsub [tier]" / "!testgift [tier]" / "!testbits [amount]" - moderator/broadcaster
        /// only. Real subscriptions, gifted subs, and bit cheers can't be triggered on demand for
        /// testing (unlike !buy actions) - these fire the exact same HandleSubscription/
        /// HandleGiftedSub/HandleBitsCheered code path TwitchChatConnector's real events call,
        /// just with fake data from whoever ran the command, so the point-award math, log lines,
        /// and chat announcement can all be verified without waiting for (or paying for) a real
        /// one. This only proves this mod's own logic is correct - it doesn't touch Twitch's
        /// actual event delivery, so if a test command works but a real sub/gift/cheer doesn't
        /// award points, the bug is in how TwitchLib parsed the real event, not in this code path.
        /// </summary>
        private void HandleTestSub(ChatCommand command)
        {
            if (!command.IsModerator && !command.IsBroadcaster)
            {
                _log.LogInfo($"{command.DisplayName} tried to use !testsub but isn't a mod/broadcaster.");
                return;
            }

            var tier = ParseTierArg(command.ArgOrDefault(0));
            _dispatcher.Enqueue(() => HandleSubscription(command.Username, command.DisplayName, tier));
        }

        /// <summary>See HandleTestSub's doc comment.</summary>
        private void HandleTestGift(ChatCommand command)
        {
            if (!command.IsModerator && !command.IsBroadcaster)
            {
                _log.LogInfo($"{command.DisplayName} tried to use !testgift but isn't a mod/broadcaster.");
                return;
            }

            var tier = ParseTierArg(command.ArgOrDefault(0));
            _dispatcher.Enqueue(() => HandleGiftedSub(command.Username, command.DisplayName, tier));
        }

        /// <summary>See HandleTestSub's doc comment.</summary>
        private void HandleTestBits(ChatCommand command)
        {
            if (!command.IsModerator && !command.IsBroadcaster)
            {
                _log.LogInfo($"{command.DisplayName} tried to use !testbits but isn't a mod/broadcaster.");
                return;
            }

            var bits = int.TryParse(command.ArgOrDefault(0), out var parsed) && parsed > 0 ? parsed : 100;
            _dispatcher.Enqueue(() => HandleBitsCheered(command.Username, command.DisplayName, bits));
        }

        /// <summary>"!testdonation [amount] [message...]" - moderator/broadcaster only, same
        /// reasoning as HandleTestSub's doc comment: a real Extra Life donation can't be triggered
        /// on demand, so this runs ExtraLifeDonationTracker's whole points/confetti/random-effect
        /// pipeline with fake data instead of waiting for (or paying for) a real one - including
        /// the username-matching heuristics, by passing whatever test message was typed. Amount
        /// defaults to $5, message defaults to empty (exercises the "no username found" path;
        /// pass a Twitch username as the message to test a successful match instead). No-ops
        /// quietly if ExtraLifeTracker was never wired up (see Plugin.Load()).</summary>
        private void HandleTestDonation(ChatCommand command)
        {
            if (!command.IsModerator && !command.IsBroadcaster)
            {
                _log.LogInfo($"{command.DisplayName} tried to use !testdonation but isn't a mod/broadcaster.");
                return;
            }

            if (ExtraLifeTracker == null)
            {
                SendChatMessage?.Invoke("Extra Life tracker isn't set up.");
                return;
            }

            var amount = decimal.TryParse(command.ArgOrDefault(0), out var parsedAmount) && parsedAmount > 0 ? parsedAmount : 5m;
            var message = command.Args.Length > 1 ? string.Join(' ', command.Args.Skip(1)) : string.Empty;
            ExtraLifeTracker.SimulateDonation(amount, message, command.DisplayName);
        }

        private static int ParseTierArg(string arg) => int.TryParse(arg, out var tier) && tier >= 1 && tier <= 3 ? tier : 1;

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

            if (DisabledActions.Contains(action))
            {
                _log.LogInfo($"{command.DisplayName} tried to buy '{action}' but it's currently disabled.");
                SendChatMessage?.Invoke($"@{command.DisplayName} '{action}' is turned off right now - try something else!");
                return;
            }

            if (!_points.TrySpendPoints(command.Username, cost))
            {
                _log.LogInfo($"{command.DisplayName} tried to buy '{action}' ({cost} pts) but has only {_points.GetBalance(command.Username)}.");
                return;
            }

            _log.LogInfo($"{command.DisplayName} bought '{action}' for {cost} points.");

            var displayName = command.DisplayName;
            var username = command.Username;

            // Blocking Helix HTTP call - done here (still on the Twitch background thread, not
            // Unity's) rather than inside the dispatched lambda below, so a slow/failed lookup
            // can never stall a game frame. Cached after the first hit per username.
            var avatarUrl = _avatarProvider?.GetProfileImageUrl(command.Username);

            // Hop onto Unity's main thread before touching any GameObject/Rigidbody/etc.
            _dispatcher.Enqueue(() => RunOrHoldForMenu(() => RunPurchase(action, displayName, username, cost, avatarUrl)));
        }

        /// <summary>Runs a chaos effect that's ready to fire (already on the main thread), or -
        /// if a menu appears to be open (see ChaosController.IsMenuOpen) - holds it and runs it
        /// later instead, once the menu closes (see ProcessHeldChaosActions, called from
        /// Plugin.Tick every frame). Applies to both paid !buy purchases and free chat-vote poll
        /// results, so neither fires behind a menu where the effect might not even be visible or
        /// could land on the wrong thing (e.g. a camera-relative target picked while the player's
        /// view is covered by a build/pause menu).</summary>
        private void RunOrHoldForMenu(Action runNow)
        {
            if (_chaos.IsMenuOpen())
            {
                _heldWhileMenuOpen.Enqueue(runNow);
                _log.LogInfo("A menu appears to be open - holding a chaos effect until it closes.");
                return;
            }

            runNow();
        }

        /// <summary>Call every frame from Plugin.Tick() - runs any chaos effects that were held
        /// by RunOrHoldForMenu, in the order they were triggered, once a menu is no longer open.</summary>
        public void ProcessHeldChaosActions()
        {
            if (_heldWhileMenuOpen.Count == 0 || _chaos.IsMenuOpen())
            {
                return;
            }

            while (_heldWhileMenuOpen.Count > 0)
            {
                _heldWhileMenuOpen.Dequeue()();
            }
        }

        private void RunPurchase(string action, string displayName, string username, int cost, string avatarUrl)
        {
            if (Execute(action, out var targetName))
            {
                var description = DescribeAction(action, targetName);
                _notifier?.Show($"{displayName} {description}! (-{cost} pts)");
                SendChatMessage?.Invoke($"@{displayName} {description}! (-{cost} pts)");
                _overlay?.Broadcast("redemption", JsonConvert.SerializeObject(new { displayName, description, action, cost, avatarUrl, targetName }));
            }
            else
            {
                // Execute() failing means nothing actually happened in-game (e.g. a park event
                // type that couldn't be found - see ChaosController.TriggerParkEvent's
                // warnings) - refund rather than silently keeping points for a no-op purchase.
                _points.AddPoints(username, displayName, cost);
                _log.LogWarning($"'{action}' failed to execute for {displayName} - refunded {cost} points.");
                SendChatMessage?.Invoke($"@{displayName} sorry, '{action}' didn't work this time - refunded your {cost} points.");
            }
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
            return ExecuteFreeWithMessage(action, targetName => $"{announcedAs} {DescribeAction(action, targetName)}!");
        }

        /// <summary>
        /// Same as ExecuteFree, but the full announcement text is built by the caller (given the
        /// action's target name, if any - null for actions that don't have one, see DescribeAction)
        /// instead of the fixed "{announcedAs} {description}!" shape - used by
        /// ExtraLifeDonationTracker for donation-flavored messages that weave in the donor's name
        /// and, where relevant, the target NPC's name. Must be called from Unity's main thread
        /// (same rule as ExecuteFree/everything in ChaosController).
        /// </summary>
        public bool ExecuteFreeWithMessage(string action, Func<string, string> buildMessage)
        {
            if (DisabledActions.Contains(action))
            {
                _log.LogInfo($"'{action}' skipped for a custom announcement - it's currently disabled.");
                return false;
            }

            if (_chaos.IsMenuOpen())
            {
                // Held rather than run now (see RunOrHoldForMenu) - report success since we can't
                // know yet whether it'll actually work once it runs; a real failure still gets
                // logged by Execute()/TriggerParkEvent() itself when it eventually fires.
                _heldWhileMenuOpen.Enqueue(() => RunFreeActionWithMessage(action, buildMessage));
                _log.LogInfo($"'{action}' held - a menu appears to be open, will run once it closes.");
                return true;
            }

            return RunFreeActionWithMessage(action, buildMessage);
        }

        private bool RunFreeActionWithMessage(string action, Func<string, string> buildMessage)
        {
            if (!Execute(action, out var targetName))
            {
                return false;
            }

            var message = buildMessage(targetName);
            _notifier?.Show(message);
            SendChatMessage?.Invoke(message);
            _overlay?.Broadcast("redemption", JsonConvert.SerializeObject(new { displayName = (string)null, description = message, action, cost = 0, avatarUrl = (string)null, targetName }));
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
            "addmoney" => "added money to the park",
            "removemoney" => "drained money from the park",
            "earthquake" => "ragdolled every guest in the park",
            "gravity" => "messed with the streamer's gravity",
            "shuffle" => "shuffled the streamer's held item",
            "firesale" => "crashed ticket prices to $0",
            "swarm" => "sent a seagull swarm after the streamer",
            "tornado" => "spun up a tornado in the park",
            "ufo" => "brought in a UFO",
            "mafia" => "sent the mafia after the park",
            "itemsrain" => "made it rain items",
            "caseoh" => "summoned CaseOh",
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
                case "addmoney":
                    success = _chaos.AddMoney();
                    break;
                case "removemoney":
                    success = _chaos.RemoveMoney();
                    break;
                case "earthquake":
                    success = _chaos.Earthquake();
                    break;
                case "gravity":
                    success = _chaos.ChaosGravity();
                    break;
                case "shuffle":
                    success = _chaos.ShuffleItem();
                    break;
                case "firesale":
                    success = _chaos.FireSale();
                    break;
                case "swarm":
                    success = _chaos.Swarm();
                    break;
                case "tornado":
                    success = _chaos.Tornado();
                    break;
                case "ufo":
                    success = _chaos.Ufo();
                    break;
                case "mafia":
                    success = _chaos.Mafia();
                    break;
                case "itemsrain":
                    success = _chaos.ItemsRain();
                    break;
                case "caseoh":
                    success = _chaos.Queso();
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
