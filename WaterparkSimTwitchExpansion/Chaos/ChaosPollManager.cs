using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;

namespace WaterparkSimTwitchExpansion.Chaos
{
    /// <summary>
    /// Periodic (and mod-triggerable) chat polls: posts a few random chaos options to chat,
    /// viewers vote by typing the option number (e.g. "1", "2" - no "!" prefix, no point cost),
    /// and the winning option fires for free when the timer runs out - similar to games like 7
    /// Days to Die that let chat vote on a "blood moon" mutator.
    ///
    /// Voting is entirely separate from !buy's point economy - it's a free, communal way to cause
    /// chaos alongside it, not a replacement.
    ///
    /// Every public method here must only ever be called from Unity's main thread. In practice
    /// that's automatic: Plugin.cs enqueues RegisterVote (and ChaosCommandRouter enqueues
    /// StartPoll) through Core.MainThreadDispatcher the same way every other Twitch-thread-to-
    /// main-thread hop in this mod works, and Tick() is only ever called from Plugin.Update().
    /// That also means no locking is needed on the vote/option state below - it's never touched
    /// from more than one thread.
    /// </summary>
    public sealed class ChaosPollManager
    {
        private readonly ManualLogSource _log;
        private readonly ChaosCommandRouter _router;
        private readonly IReadOnlyList<string> _actionPool;
        private readonly System.Random _random = new System.Random();

        private readonly float _pollDurationSeconds;
        private readonly float _autoIntervalSeconds;
        private readonly int _optionCount;

        private readonly Dictionary<string, string> _votes = new Dictionary<string, string>(); // username -> chosen action
        private string[] _currentOptions;
        private float? _pollTimeRemaining;
        private float _timeUntilNextAutoPoll;

        public bool IsPollActive => _pollTimeRemaining.HasValue;

        /// <param name="actionPool">Action names a poll can offer as options - typically the same
        /// set of chaos actions !buy prices out.</param>
        /// <param name="pollDurationSeconds">How long voting stays open once a poll starts.</param>
        /// <param name="autoIntervalSeconds">How often a poll starts on its own. 0 or less disables
        /// automatic polls (still triggerable via !startpoll).</param>
        /// <param name="optionCount">How many options to offer per poll (clamped to at least 2, and
        /// to the pool size).</param>
        public ChaosPollManager(
            ManualLogSource log,
            ChaosCommandRouter router,
            IReadOnlyList<string> actionPool,
            float pollDurationSeconds,
            float autoIntervalSeconds,
            int optionCount)
        {
            _log = log;
            _router = router;
            _actionPool = actionPool;
            _pollDurationSeconds = pollDurationSeconds;
            _autoIntervalSeconds = autoIntervalSeconds;
            _optionCount = Math.Max(2, optionCount);
            _timeUntilNextAutoPoll = autoIntervalSeconds;
        }

        /// <summary>Call once per frame from Plugin.Update() (same pattern as PointsManager.Tick).</summary>
        public void Tick(float deltaTime)
        {
            if (IsPollActive)
            {
                _pollTimeRemaining -= deltaTime;
                if (_pollTimeRemaining <= 0f)
                {
                    ResolvePoll();
                }

                return;
            }

            if (_autoIntervalSeconds <= 0f)
            {
                return;
            }

            _timeUntilNextAutoPoll -= deltaTime;
            if (_timeUntilNextAutoPoll <= 0f)
            {
                StartPoll();
            }
        }

        /// <summary>Starts a poll if one isn't already running. Returns false (and logs why) if a
        /// poll is already active or there aren't enough distinct actions to offer.</summary>
        public bool StartPoll()
        {
            if (IsPollActive)
            {
                _log.LogInfo("ChaosPollManager: StartPoll requested but a poll is already active.");
                return false;
            }

            if (_actionPool.Count < 2)
            {
                _log.LogWarning("ChaosPollManager: not enough actions configured to run a poll.");
                return false;
            }

            _currentOptions = _actionPool
                .OrderBy(_ => _random.Next())
                .Take(Math.Min(_optionCount, _actionPool.Count))
                .ToArray();
            _votes.Clear();
            _pollTimeRemaining = _pollDurationSeconds;
            _timeUntilNextAutoPoll = _autoIntervalSeconds;

            var descriptions = _currentOptions.Select(_router.DescribeActionForPoll).ToArray();
            var optionsText = string.Join("   ", descriptions.Select((desc, i) => $"{i + 1}) {desc}"));
            _router.Announce($"CHAOS VOTE! Type a number to vote (free, {_pollDurationSeconds:0}s): {optionsText}");
            _router.BroadcastPollStarted(descriptions, _pollDurationSeconds);

            _log.LogInfo($"ChaosPollManager: started poll with options [{string.Join(", ", _currentOptions)}].");
            return true;
        }

        /// <summary>Registers/updates <paramref name="username"/>'s vote if a poll is currently
        /// active and <paramref name="messageText"/> is a bare number matching one of the current
        /// options (e.g. "1", "2"). Silently ignored otherwise - most chat messages aren't votes,
        /// this is expected to be called for every chat message unconditionally.</summary>
        public void RegisterVote(string username, string messageText)
        {
            if (!IsPollActive || string.IsNullOrWhiteSpace(messageText))
            {
                return;
            }

            if (!int.TryParse(messageText.Trim(), out var choice) || choice < 1 || choice > _currentOptions.Length)
            {
                return;
            }

            _votes[username] = _currentOptions[choice - 1];
            _router.BroadcastPollVotes(TallyVotes(_currentOptions));
        }

        /// <summary>Vote count per option, in the same order as <paramref name="options"/> - shared
        /// by RegisterVote's live update and ResolvePoll's final tally.</summary>
        private int[] TallyVotes(string[] options) => options
            .Select(action => _votes.Values.Count(vote => vote == action))
            .ToArray();

        private void ResolvePoll()
        {
            var options = _currentOptions;
            _pollTimeRemaining = null;
            _currentOptions = null;

            if (_votes.Count == 0)
            {
                _router.Announce("Chaos vote ended with no votes - maybe next time!");
                _router.BroadcastPollEnded(-1, new int[options.Length]);
                return;
            }

            var counts = TallyVotes(options);

            // Ties broken randomly rather than by "whoever voted first" - fairer for a chat-wide vote.
            var winner = _votes.Values
                .GroupBy(action => action)
                .OrderByDescending(group => group.Count())
                .ThenBy(_ => _random.Next())
                .First()
                .Key;
            var winnerIndex = Array.IndexOf(options, winner);
            var voteCount = counts[winnerIndex];
            var totalVotes = _votes.Count;
            _votes.Clear();

            _log.LogInfo($"ChaosPollManager: poll resolved - '{winner}' won with {voteCount}/{totalVotes} vote(s).");
            _router.Announce($"Chaos vote result: '{winner}' wins with {voteCount}/{totalVotes} vote(s)!");
            _router.BroadcastPollEnded(winnerIndex, counts);

            if (!_router.ExecuteFree(winner, "Chat vote"))
            {
                _log.LogWarning($"ChaosPollManager: winning action '{winner}' failed to execute (see warnings above).");
            }
        }
    }
}
