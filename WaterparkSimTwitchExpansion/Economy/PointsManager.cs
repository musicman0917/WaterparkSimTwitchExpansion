using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Logging;
using Newtonsoft.Json;

namespace WaterparkSimTwitchExpansion.Economy
{
    /// <summary>
    /// Tracks per-viewer point balances, hands out passive income to active chatters, grants a
    /// role-based starting balance the first time a viewer's account is created (see
    /// RegisterActivity's startingBalance parameter, computed by ChaosCommandRouter), and
    /// persists everything to a JSON file so points survive a game restart.
    ///
    /// Thread-safety note: chat events arrive on a TwitchLib background thread while Tick() is
    /// driven from Unity's main thread via Plugin.Update(). This class only touches plain data
    /// (no UnityEngine calls), so a ConcurrentDictionary is enough - it does not need to be
    /// routed through MainThreadDispatcher.
    /// </summary>
    public sealed class PointsManager
    {
        private readonly ConcurrentDictionary<string, UserAccount> _accounts =
            new ConcurrentDictionary<string, UserAccount>(StringComparer.OrdinalIgnoreCase);

        private readonly ManualLogSource _log;
        private readonly string _saveFilePath;

        // Mutable public properties rather than constructor-only values - Core.ModMenu (the
        // in-game F9 settings panel) sets these directly at runtime, no restart needed.
        public int PassiveIncomeAmount { get; set; }
        public bool PassiveIncomeEnabled { get; set; } = true;

        private TimeSpan _passiveIncomeInterval;
        public int PassiveIncomeIntervalSeconds
        {
            get => (int)_passiveIncomeInterval.TotalSeconds;
            set => _passiveIncomeInterval = TimeSpan.FromSeconds(Math.Max(1, value));
        }

        private readonly TimeSpan _activityWindow;

        private double _secondsSincePassiveIncome;

        /// <param name="saveFilePath">Full path to the JSON save file, e.g. Path.Combine(Paths.ConfigPath, "waterpark_points.json").</param>
        /// <param name="passiveIncomeAmount">Points awarded per interval to each active chatter.</param>
        /// <param name="passiveIncomeInterval">How often passive income is paid out (e.g. 60s).</param>
        /// <param name="activityWindow">A chatter is "active" if they sent a message within this window.</param>
        public PointsManager(
            ManualLogSource log,
            string saveFilePath,
            int passiveIncomeAmount = 10,
            TimeSpan? passiveIncomeInterval = null,
            TimeSpan? activityWindow = null)
        {
            _log = log;
            _saveFilePath = saveFilePath;
            PassiveIncomeAmount = passiveIncomeAmount;
            _passiveIncomeInterval = passiveIncomeInterval ?? TimeSpan.FromSeconds(60);
            _activityWindow = activityWindow ?? TimeSpan.FromMinutes(10);
        }

        /// <summary>Call from a chat-message handler for EVERY message (not just commands) to mark
        /// a user active. <paramref name="startingBalance"/> and <paramref name="followBonusAlreadyApplied"/>
        /// are only used the moment this viewer's account is first created (e.g. by role - see
        /// ChaosCommandRouter.StartingBalanceFor) - both are ignored for anyone who already has an
        /// account, so this never retroactively changes an existing balance or flag.</summary>
        public void RegisterActivity(string username, string displayName, int startingBalance = 0, bool followBonusAlreadyApplied = false)
        {
            var account = GetOrCreateAccount(username, displayName, startingBalance, followBonusAlreadyApplied);
            account.DisplayName = displayName;
            account.LastSeenUtc = DateTime.UtcNow;
        }

        /// <summary>Call once per frame (e.g. from Plugin.Update with Time.deltaTime) to accrue passive income.</summary>
        public void Tick(float deltaTimeSeconds)
        {
            if (!PassiveIncomeEnabled)
            {
                // Don't accrue time while disabled - re-enabling later starts a fresh interval
                // instead of instantly paying out whatever backlog built up while it was off.
                return;
            }

            _secondsSincePassiveIncome += deltaTimeSeconds;
            if (_secondsSincePassiveIncome < _passiveIncomeInterval.TotalSeconds)
            {
                return;
            }

            _secondsSincePassiveIncome = 0;
            PayActiveChatters();
        }

        private void PayActiveChatters()
        {
            var cutoff = DateTime.UtcNow - _activityWindow;
            var paidCount = 0;

            foreach (var account in _accounts.Values)
            {
                if (account.LastSeenUtc >= cutoff)
                {
                    account.Points += PassiveIncomeAmount;
                    paidCount++;
                }
            }

            if (paidCount > 0)
            {
                _log.LogInfo($"Paid {PassiveIncomeAmount} passive points to {paidCount} active chatter(s).");
            }
        }

        public int GetBalance(string username)
        {
            return _accounts.TryGetValue(Normalize(username), out var account) ? account.Points : 0;
        }

        /// <summary>Cheap in-memory check, no network - lets a caller skip expensive starting-balance
        /// work (e.g. a Helix follower-status lookup) for viewers who already have an account.</summary>
        public bool HasAccount(string username) => _accounts.ContainsKey(Normalize(username));

        /// <summary>Used by !give and the subscription/gifted-sub/bits point grants. Note: unlike
        /// RegisterActivity, this doesn't take a startingBalance - if it's what creates a viewer's
        /// account (e.g. a mod !gives points to someone who's never chatted), they start at 0 +
        /// amount rather than their role's starting balance. Edge case, not worth the extra
        /// parameter threading for how rarely that ordering would happen in practice.</summary>
        public void AddPoints(string username, string displayName, int amount)
        {
            if (amount <= 0) return;
            var account = GetOrCreateAccount(username, displayName);
            account.Points += amount;
        }

        /// <summary>Attempts to deduct <paramref name="cost"/> points. Returns false (no-op) if the balance is insufficient.</summary>
        public bool TrySpendPoints(string username, int cost)
        {
            if (cost <= 0) return true;

            var key = Normalize(username);
            if (!_accounts.TryGetValue(key, out var account) || account.Points < cost)
            {
                return false;
            }

            account.Points -= cost;
            return true;
        }

        /// <summary>Cheap, no network - tells ChaosCommandRouter whether it's worth spending a
        /// (blocking) Helix follower-status call on this existing viewer right now: only true if
        /// they haven't gotten the follow bonus yet AND at least <paramref name="minInterval"/> has
        /// passed since the last check, so a chatty non-follower doesn't get hit on every message.</summary>
        public bool ShouldCheckFollowBonus(string username, TimeSpan minInterval)
        {
            if (!_accounts.TryGetValue(Normalize(username), out var account) || account.FollowBonusGranted)
            {
                return false;
            }

            return account.LastFollowCheckUtc == null || DateTime.UtcNow - account.LastFollowCheckUtc >= minInterval;
        }

        /// <summary>Records that a follow-status check just happened, to throttle future checks
        /// regardless of whether it turned out they were following.</summary>
        public void MarkFollowChecked(string username)
        {
            if (_accounts.TryGetValue(Normalize(username), out var account))
            {
                account.LastFollowCheckUtc = DateTime.UtcNow;
            }
        }

        /// <summary>Grants the one-time "just followed" top-up and marks it granted so it can never
        /// fire again for this account, even after an unfollow/re-follow. Returns false (no-op) if
        /// the account doesn't exist or already received it.</summary>
        public bool TryGrantFollowBonus(string username, int amount)
        {
            if (amount <= 0) return false;
            if (!_accounts.TryGetValue(Normalize(username), out var account) || account.FollowBonusGranted)
            {
                return false;
            }

            account.Points += amount;
            account.FollowBonusGranted = true;
            return true;
        }

        private UserAccount GetOrCreateAccount(string username, string displayName, int startingBalance = 0, bool followBonusAlreadyApplied = false)
        {
            var key = Normalize(username);
            return _accounts.GetOrAdd(key, _ => new UserAccount
            {
                Username = key,
                DisplayName = displayName ?? username,
                Points = startingBalance,
                LastSeenUtc = DateTime.UtcNow,
                FollowBonusGranted = followBonusAlreadyApplied
            });
        }

        private static string Normalize(string username) => username?.Trim().ToLowerInvariant();

        public void Save()
        {
            try
            {
                var directory = Path.GetDirectoryName(_saveFilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonConvert.SerializeObject(_accounts.Values.ToList(), Formatting.Indented);
                File.WriteAllText(_saveFilePath, json);
                _log.LogInfo($"Saved {_accounts.Count} viewer account(s) to {_saveFilePath}.");
            }
            catch (Exception e)
            {
                _log.LogError($"Failed to save points file: {e}");
            }
        }

        public void Load()
        {
            if (!File.Exists(_saveFilePath))
            {
                _log.LogInfo("No existing points file found, starting with an empty economy.");
                return;
            }

            try
            {
                var json = File.ReadAllText(_saveFilePath);
                var accounts = JsonConvert.DeserializeObject<List<UserAccount>>(json) ?? new List<UserAccount>();

                _accounts.Clear();
                foreach (var account in accounts)
                {
                    _accounts[Normalize(account.Username)] = account;
                }

                _log.LogInfo($"Loaded {_accounts.Count} viewer account(s) from {_saveFilePath}.");
            }
            catch (Exception e)
            {
                _log.LogError($"Failed to load points file, starting empty: {e}");
            }
        }
    }
}
