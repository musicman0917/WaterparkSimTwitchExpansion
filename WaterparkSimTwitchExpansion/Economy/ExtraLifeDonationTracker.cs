using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using BepInEx.Logging;
using Newtonsoft.Json;
using WaterparkSimTwitchExpansion.Core;

namespace WaterparkSimTwitchExpansion.Economy
{
    /// <summary>
    /// Polls the public DonorDrive API (the platform Extra Life runs on - no API key needed for
    /// read-only access: https://github.com/DonorDrive/PublicAPI) for new donations to a given
    /// participant and awards points the same way bits do - CentsToPointsRatio points per cent
    /// donated. Runs its own background polling thread (same convention as OverlayServer's
    /// listener thread), never touches UnityEngine directly - work that does (Announce, which
    /// hits OnScreenNotifier) is hopped onto the main thread via MainThreadDispatcher first.
    ///
    /// Extra Life donations carry no Twitch identity at all - just whatever display name/message
    /// the donor typed on the donation form - so attribution only works if the donor's Twitch
    /// handle shows up somewhere in one of those two fields (see ExtractTwitchUsername for exactly
    /// where/how it looks). A donation where neither field yields anything still gets celebrated
    /// in chat/on-screen, just without any points awarded, rather than silently dropped - there's
    /// no way to guess who to credit otherwise.
    ///
    /// The DonorDrive API always returns a participant's full donation history, not just what's
    /// new since last time, so this tracks a persisted high-water mark (the newest
    /// createdDateUTC actually processed) rather than re-deriving it from an in-memory set that
    /// wouldn't survive a restart - without that, every restart would silently re-award points for
    /// the participant's entire donation history. The very first successful poll ever (no saved
    /// state file) is a special case: it seeds the watermark from whatever's already there WITHOUT
    /// awarding anything, so turning this on for a participant who already has donations doesn't
    /// retroactively pay out their whole history.
    /// </summary>
    public sealed class ExtraLifeDonationTracker
    {
        private const string ApiBaseUrl = "https://extralife.donordrive.com/api/1.6";

        // No "twitch:" keyword required - donors are asked (see SETUP.md) to put JUST their bare
        // Twitch username in the donation message or display name, nothing else, and this matches
        // that directly. Word-bounded to a full-field match (Twitch usernames are 4-25 chars of
        // [a-zA-Z0-9_]) rather than searching within a longer string, so an actual sentence never
        // gets misread as a handle - see ExtractTwitchUsername.
        private static readonly Regex BareUsernamePattern = new Regex(
            @"^[a-zA-Z0-9_]{4,25}$", RegexOptions.Compiled);

        private readonly ManualLogSource _log;
        private readonly MainThreadDispatcher _dispatcher;
        private readonly PointsManager _points;
        private readonly Action<string> _announce;
        private readonly HttpClient _http;
        private readonly string _participantId;
        private readonly string _statePath;

        private Thread _pollThread;
        private volatile bool _running;
        private DateTime _lastProcessedUtc = DateTime.MinValue;

        public int CentsToPointsRatio { get; set; }
        public int PollIntervalSeconds { get; set; }

        public ExtraLifeDonationTracker(
            ManualLogSource log,
            MainThreadDispatcher dispatcher,
            PointsManager points,
            Action<string> announce,
            string participantId,
            string statePath,
            int centsToPointsRatio,
            int pollIntervalSeconds)
        {
            _log = log;
            _dispatcher = dispatcher;
            _points = points;
            _announce = announce;
            _participantId = participantId;
            _statePath = statePath;
            CentsToPointsRatio = centsToPointsRatio;
            PollIntervalSeconds = pollIntervalSeconds;

            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("WaterparkSimTwitchExpansion-ExtraLifeTracker");
        }

        /// <summary>No-op if ParticipantId is blank or this is already running. Safe to call from
        /// Plugin.Load() (Unity's main thread) - the actual polling happens on its own thread.</summary>
        public void Start()
        {
            if (string.IsNullOrWhiteSpace(_participantId) || _running)
            {
                return;
            }

            LoadState();
            _running = true;
            _pollThread = new Thread(PollLoop) { IsBackground = true, Name = "WaterparkExtraLifeTracker" };
            _pollThread.Start();
        }

        public void Stop()
        {
            _running = false;
        }

        private void PollLoop()
        {
            // Only cleared after a poll actually SUCCEEDS - if the very first poll throws (a
            // transient network error), the next attempt must still suppress grants, or a donation
            // history that was never safely seeded would get fully replayed as points the moment a
            // poll finally succeeds.
            var suppressGrants = _lastProcessedUtc == DateTime.MinValue;

            while (_running)
            {
                try
                {
                    PollOnce(suppressGrants);
                    suppressGrants = false;
                }
                catch (Exception e)
                {
                    var message = e.Message;
                    _dispatcher.Enqueue(() => _log.LogWarning($"ExtraLifeDonationTracker: poll failed - {message}"));
                }

                for (var waited = 0; waited < PollIntervalSeconds && _running; waited++)
                {
                    Thread.Sleep(1000);
                }
            }
        }

        private void PollOnce(bool suppressGrants)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBaseUrl}/participants/{Uri.EscapeDataString(_participantId)}/donations");
            using var response = _http.Send(request);

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                _dispatcher.Enqueue(() => _log.LogWarning($"ExtraLifeDonationTracker: donations lookup failed with HTTP {status} - check ExtraLife.ParticipantId."));
                return;
            }

            using var stream = response.Content.ReadAsStream();
            using var reader = new StreamReader(stream);
            var donations = JsonConvert.DeserializeObject<List<Donation>>(reader.ReadToEnd()) ?? new List<Donation>();

            // The API returns newest-first by default - reversed so multiple donations that
            // arrived between polls get processed oldest-first, same order they actually happened.
            donations.Reverse();

            var newWatermark = _lastProcessedUtc;
            foreach (var donation in donations)
            {
                if (donation.createdDateUTC <= _lastProcessedUtc)
                {
                    continue;
                }

                if (donation.createdDateUTC > newWatermark)
                {
                    newWatermark = donation.createdDateUTC;
                }

                if (!suppressGrants)
                {
                    ProcessDonation(donation);
                }
            }

            if (newWatermark > _lastProcessedUtc)
            {
                _lastProcessedUtc = newWatermark;
                SaveState();
            }
        }

        private void ProcessDonation(Donation donation)
        {
            // Registration-fee entries and the like can come back with a null/zero amount (see
            // DonorDrive's own docs) - nothing to award, and no point announcing a $0 "donation".
            if (donation.amount is not > 0)
            {
                return;
            }

            var amount = donation.amount.Value;
            var points = (int)Math.Round(amount * 100m) * CentsToPointsRatio;
            var username = ExtractTwitchUsername(donation.message, donation.displayName);
            var donorName = string.IsNullOrWhiteSpace(donation.displayName) ? "Someone" : donation.displayName;

            _dispatcher.Enqueue(() =>
            {
                if (username != null)
                {
                    _points.AddPoints(username, username, points);
                    _log.LogInfo($"ExtraLifeDonationTracker: {donorName} donated ${amount:0.00} (matched Twitch user: {username}) - awarded {points} points.");
                    _announce($"@{username} thank you for donating ${amount:0.00} to Extra Life! +{points} points.");
                }
                else
                {
                    _log.LogInfo($"ExtraLifeDonationTracker: {donorName} donated ${amount:0.00} - no Twitch username found (message: \"{donation.message}\", displayName: \"{donation.displayName}\") - no points awarded.");
                    _announce($"{donorName} just donated ${amount:0.00} to Extra Life! Thank you! (put just your Twitch username in the donation message to earn points next time)");
                }
            });
        }

        /// <summary>Treats the donation message, or failing that the display name, as the Twitch
        /// username directly - no "twitch:" keyword/prefix required, per SETUP.md's instruction to
        /// donors to just type their bare username and nothing else into one of those two fields.
        /// Only matches if the field, once trimmed, IS a username-shaped token in its entirety
        /// (nothing else around it) - a longer message/display name with other words in it never
        /// matches, so an actual sentence doesn't get misread as a handle.</summary>
        private static string ExtractTwitchUsername(string message, string displayName)
        {
            var trimmedMessage = message?.Trim() ?? string.Empty;
            if (BareUsernamePattern.IsMatch(trimmedMessage))
            {
                return trimmedMessage;
            }

            var trimmedDisplayName = displayName?.Trim() ?? string.Empty;
            if (BareUsernamePattern.IsMatch(trimmedDisplayName))
            {
                return trimmedDisplayName;
            }

            return null;
        }

        private void LoadState()
        {
            if (!File.Exists(_statePath))
            {
                return;
            }

            try
            {
                var state = JsonConvert.DeserializeObject<State>(File.ReadAllText(_statePath));
                if (state != null)
                {
                    _lastProcessedUtc = state.LastProcessedUtc;
                }
            }
            catch (Exception e)
            {
                _log.LogWarning($"ExtraLifeDonationTracker: failed to load saved state, starting from scratch (will not retroactively award past donations): {e.Message}");
            }
        }

        private void SaveState()
        {
            try
            {
                var directory = Path.GetDirectoryName(_statePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(_statePath, JsonConvert.SerializeObject(new State { LastProcessedUtc = _lastProcessedUtc }));
            }
            catch (Exception e)
            {
                _log.LogWarning($"ExtraLifeDonationTracker: failed to save state - a restart before this succeeds could re-award recent donations: {e.Message}");
            }
        }

        private sealed class Donation
        {
            public string donationID { get; set; }
            public string displayName { get; set; }
            public decimal? amount { get; set; }
            public string message { get; set; }
            public DateTime createdDateUTC { get; set; }
        }

        private sealed class State
        {
            public DateTime LastProcessedUtc { get; set; }
        }
    }
}
