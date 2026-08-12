using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using BepInEx.Logging;
using Newtonsoft.Json;

namespace WaterparkSimTwitchExpansion.Twitch
{
    /// <summary>
    /// Checks whether a viewer follows the channel, for the "follower" starting-balance tier (see
    /// ChaosCommandRouter.StartingBalanceFor). Twitch's chat/IRC connection (TwitchChatConnector)
    /// can't see follow status at all - no badge, no tag - so this hits Twitch's Helix API instead
    /// (GET /helix/channels/followers), which as of Twitch's 2023 API change requires a token
    /// belonging to the broadcaster themselves (or a moderator of the channel) with the
    /// moderator:read:followers scope - deliberately a SEPARATE app/token from the bot's own
    /// chat:read/chat:edit OAuthToken and TwitchAvatarProvider's Client ID, since those don't (and
    /// generally can't) carry this scope.
    ///
    /// Two Helix calls happen the first time a given viewer is checked (resolve their username to
    /// a numeric user ID, then check the follow relationship) - both results are cached
    /// indefinitely per-process, same reasoning as TwitchAvatarProvider's avatar cache: follow
    /// status/user IDs don't change often enough mid-stream to justify re-checking every message.
    /// </summary>
    public sealed class TwitchFollowerProvider
    {
        private readonly ManualLogSource _log;
        private readonly HttpClient _http = new HttpClient();
        private readonly string _clientId;
        private readonly string _accessToken;
        private readonly string _channelName;

        private readonly ConcurrentDictionary<string, string> _userIdCache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, bool> _followCache = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private string _broadcasterId;

        /// <param name="clientId">Client ID of a Twitch Developer Console app - separate from any
        /// Client ID used elsewhere in this mod.</param>
        /// <param name="oauthToken">User access token for the BROADCASTER's own Twitch account
        /// (not the bot's) with the moderator:read:followers scope. "oauth:" prefix optional.</param>
        /// <param name="channelName">The channel to check followers of - same as Twitch.ChannelName.</param>
        public TwitchFollowerProvider(ManualLogSource log, string clientId, string oauthToken, string channelName)
        {
            _log = log;
            _clientId = clientId;
            _accessToken = oauthToken.StartsWith("oauth:", StringComparison.OrdinalIgnoreCase)
                ? oauthToken.Substring("oauth:".Length)
                : oauthToken;
            _channelName = channelName;
        }

        /// <summary>
        /// Blocking (uses HttpClient.Send, not async) - call this from a background thread (e.g. a
        /// TwitchLib chat event handler), never from Unity's main thread, same rule as
        /// TwitchAvatarProvider.GetProfileImageUrl. Returns false (not a follower) on any failure
        /// (network error, unknown user, missing/invalid config) rather than throwing - a failed
        /// lookup should never block a viewer's starting balance grant, just fall back to the
        /// lower non-follower amount.
        /// </summary>
        public bool IsFollower(string username)
        {
            if (_followCache.TryGetValue(username, out var cached))
            {
                return cached;
            }

            try
            {
                var broadcasterId = GetBroadcasterId();
                var userId = GetUserId(username);
                if (broadcasterId == null || userId == null)
                {
                    return false;
                }

                var request = new HttpRequestMessage(HttpMethod.Get,
                    $"https://api.twitch.tv/helix/channels/followers?broadcaster_id={Uri.EscapeDataString(broadcasterId)}&user_id={Uri.EscapeDataString(userId)}");
                request.Headers.Add("Client-Id", _clientId);
                request.Headers.Add("Authorization", $"Bearer {_accessToken}");

                using var response = _http.Send(request);
                if (!response.IsSuccessStatusCode)
                {
                    _log.LogWarning($"TwitchFollowerProvider: follower check for '{username}' failed with HTTP {(int)response.StatusCode} - is the token still the broadcaster's own, with moderator:read:followers?");
                    return false;
                }

                using var stream = response.Content.ReadAsStream();
                using var reader = new StreamReader(stream);
                var body = JsonConvert.DeserializeObject<HelixFollowersResponse>(reader.ReadToEnd());
                var isFollower = body?.data != null && body.data.Count > 0;

                _followCache[username] = isFollower;
                return isFollower;
            }
            catch (Exception e)
            {
                _log.LogWarning($"TwitchFollowerProvider: failed to check follower status for '{username}': {e.Message}");
                return false;
            }
        }

        private string GetBroadcasterId()
        {
            if (_broadcasterId != null)
            {
                return _broadcasterId;
            }

            _broadcasterId = ResolveUserId(_channelName);
            return _broadcasterId;
        }

        private string GetUserId(string username)
        {
            if (_userIdCache.TryGetValue(username, out var cached))
            {
                return cached;
            }

            var id = ResolveUserId(username);
            if (id != null)
            {
                _userIdCache[username] = id;
            }

            return id;
        }

        private string ResolveUserId(string login)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.twitch.tv/helix/users?login={Uri.EscapeDataString(login)}");
                request.Headers.Add("Client-Id", _clientId);
                request.Headers.Add("Authorization", $"Bearer {_accessToken}");

                using var response = _http.Send(request);
                if (!response.IsSuccessStatusCode)
                {
                    _log.LogWarning($"TwitchFollowerProvider: Helix user lookup for '{login}' failed with HTTP {(int)response.StatusCode}.");
                    return null;
                }

                using var stream = response.Content.ReadAsStream();
                using var reader = new StreamReader(stream);
                var body = JsonConvert.DeserializeObject<HelixUsersResponse>(reader.ReadToEnd());
                return body?.data?.FirstOrDefault()?.id;
            }
            catch (Exception e)
            {
                _log.LogWarning($"TwitchFollowerProvider: failed to resolve Twitch user id for '{login}': {e.Message}");
                return null;
            }
        }

        private sealed class HelixUsersResponse
        {
            public List<HelixUser> data { get; set; }
        }

        private sealed class HelixUser
        {
            public string id { get; set; }
        }

        private sealed class HelixFollowersResponse
        {
            public List<HelixFollower> data { get; set; }
        }

        private sealed class HelixFollower
        {
            public string user_id { get; set; }
        }
    }
}
