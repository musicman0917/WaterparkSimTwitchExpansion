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
    /// Looks up a Twitch chatter's profile picture URL for the OBS overlay - IRC (what
    /// TwitchChatConnector uses for chat) doesn't carry avatars at all, so this hits Twitch's
    /// Helix API (GET /helix/users) instead, which needs a Client ID alongside the bot's OAuth
    /// token. Results are cached per-username since avatars rarely change and this avoids hitting
    /// Twitch's API on every single redemption from the same viewer.
    /// </summary>
    public sealed class TwitchAvatarProvider
    {
        private readonly ManualLogSource _log;
        private readonly HttpClient _http = new HttpClient();
        private readonly ConcurrentDictionary<string, string> _cache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly string _clientId;
        private readonly string _accessToken;

        public TwitchAvatarProvider(ManualLogSource log, string clientId, string oauthToken)
        {
            _log = log;
            _clientId = clientId;
            _accessToken = oauthToken.StartsWith("oauth:", StringComparison.OrdinalIgnoreCase)
                ? oauthToken.Substring("oauth:".Length)
                : oauthToken;
        }

        /// <summary>
        /// Blocking (uses HttpClient.Send, not async) - call this from a background thread (e.g.
        /// a TwitchLib chat event handler), never from Unity's main thread, since a stalled HTTP
        /// request would otherwise freeze the game for a frame or more. Returns null on any
        /// failure (network error, unknown user, missing config) rather than throwing - a missing
        /// avatar just means the overlay falls back to its icon-only look.
        /// </summary>
        public string GetProfileImageUrl(string username)
        {
            if (_cache.TryGetValue(username, out var cached))
            {
                return cached;
            }

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.twitch.tv/helix/users?login={Uri.EscapeDataString(username)}");
                request.Headers.Add("Client-Id", _clientId);
                request.Headers.Add("Authorization", $"Bearer {_accessToken}");

                using var response = _http.Send(request);
                if (!response.IsSuccessStatusCode)
                {
                    _log.LogWarning($"TwitchAvatarProvider: Helix lookup for '{username}' failed with HTTP {(int)response.StatusCode}.");
                    return null;
                }

                using var stream = response.Content.ReadAsStream();
                using var reader = new StreamReader(stream);
                var body = JsonConvert.DeserializeObject<HelixUsersResponse>(reader.ReadToEnd());
                var url = body?.data?.FirstOrDefault()?.profile_image_url;

                if (string.IsNullOrEmpty(url))
                {
                    _log.LogWarning($"TwitchAvatarProvider: Helix returned no user data for '{username}'.");
                    return null;
                }

                _cache[username] = url;
                return url;
            }
            catch (Exception e)
            {
                _log.LogWarning($"TwitchAvatarProvider: failed to fetch avatar for '{username}': {e.Message}");
                return null;
            }
        }

        private sealed class HelixUsersResponse
        {
            public List<HelixUser> data { get; set; }
        }

        private sealed class HelixUser
        {
            public string profile_image_url { get; set; }
        }
    }
}
