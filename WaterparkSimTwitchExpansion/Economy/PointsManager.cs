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
    /// Tracks per-viewer point balances, hands out passive income to active chatters, and
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

        private readonly int _passiveIncomeAmount;
        private readonly TimeSpan _passiveIncomeInterval;
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
            _passiveIncomeAmount = passiveIncomeAmount;
            _passiveIncomeInterval = passiveIncomeInterval ?? TimeSpan.FromSeconds(60);
            _activityWindow = activityWindow ?? TimeSpan.FromMinutes(10);
        }

        /// <summary>Call from a chat-message handler for EVERY message (not just commands) to mark a user active.</summary>
        public void RegisterActivity(string username, string displayName)
        {
            var account = GetOrCreateAccount(username, displayName);
            account.DisplayName = displayName;
            account.LastSeenUtc = DateTime.UtcNow;
        }

        /// <summary>Call once per frame (e.g. from Plugin.Update with Time.deltaTime) to accrue passive income.</summary>
        public void Tick(float deltaTimeSeconds)
        {
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
                    account.Points += _passiveIncomeAmount;
                    paidCount++;
                }
            }

            if (paidCount > 0)
            {
                _log.LogInfo($"Paid {_passiveIncomeAmount} passive points to {paidCount} active chatter(s).");
            }
        }

        public int GetBalance(string username)
        {
            return _accounts.TryGetValue(Normalize(username), out var account) ? account.Points : 0;
        }

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

        private UserAccount GetOrCreateAccount(string username, string displayName)
        {
            var key = Normalize(username);
            return _accounts.GetOrAdd(key, _ => new UserAccount
            {
                Username = key,
                DisplayName = displayName ?? username,
                Points = 0,
                LastSeenUtc = DateTime.UtcNow
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
