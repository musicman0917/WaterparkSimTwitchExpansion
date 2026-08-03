using System;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using TwitchLib.Client;
using TwitchLib.Client.Events;
using TwitchLib.Client.Models;
using TwitchLib.Communication.Clients;

namespace WaterparkSimTwitchExpansion.Twitch
{
    /// <summary>
    /// Thin wrapper around TwitchLib.Client that connects to a single channel and turns raw
    /// IRC/chat messages into two events:
    ///   - OnChatMessage: fired for every message (used to track "who is active" for the economy).
    ///   - OnChatCommand: fired only for messages that start with "!" (used to trigger purchases).
    ///
    /// All events fire on a TwitchLib background thread, NOT Unity's main thread. Never touch
    /// UnityEngine objects directly inside a handler for these events - route through
    /// Core.MainThreadDispatcher instead.
    /// </summary>
    public sealed class TwitchChatConnector : IDisposable
    {
        // Matches "!buy yeet", "!buy poop RandomTarget", "!balance", etc.
        private static readonly Regex CommandPattern = new Regex(@"^!(?<action>\w+)(?:\s+(?<args>.*))?$", RegexOptions.Compiled);

        private readonly ManualLogSource _log;
        private readonly TwitchClient _client;
        private readonly string _channel;

        public event Action<string, string> OnChatMessage; // (username, displayName)
        public event Action<ChatCommand> OnChatCommand;
        public event Action OnConnected;
        public event Action OnDisconnected;

        /// <param name="botUsername">The Twitch account the bot logs in as.</param>
        /// <param name="oauthToken">OAuth token for that account (chat:read + chat:edit scopes), e.g. "oauth:xxxxxxxx" from https://twitchtokengenerator.com/</param>
        /// <param name="channel">Channel to join, without the leading '#'.</param>
        public TwitchChatConnector(ManualLogSource log, string botUsername, string oauthToken, string channel)
        {
            _log = log;
            _channel = channel.TrimStart('#').ToLowerInvariant();

            var credentials = new ConnectionCredentials(botUsername, oauthToken);
            var webSocketClient = new WebSocketClient();

            _client = new TwitchClient(webSocketClient);
            _client.Initialize(credentials, _channel);

            _client.OnConnected += HandleConnected;
            _client.OnDisconnected += HandleDisconnected;
            _client.OnJoinedChannel += HandleJoinedChannel;
            _client.OnMessageReceived += HandleMessageReceived;
            _client.OnError += HandleError;
            _client.OnConnectionError += HandleConnectionError;
        }

        public void Connect() => _client.Connect();

        public void Disconnect()
        {
            if (_client.IsConnected)
            {
                _client.Disconnect();
            }
        }

        public void SendMessage(string message)
        {
            if (_client.IsConnected)
            {
                _client.SendMessage(_channel, message);
            }
        }

        private void HandleConnected(object sender, OnConnectedArgs e)
        {
            _log.LogInfo($"Connected to Twitch as {e.BotUsername}.");
            OnConnected?.Invoke();
        }

        private void HandleDisconnected(object sender, TwitchLib.Communication.Events.OnDisconnectedEventArgs e)
        {
            _log.LogWarning("Disconnected from Twitch chat.");
            OnDisconnected?.Invoke();
        }

        private void HandleJoinedChannel(object sender, OnJoinedChannelArgs e)
        {
            _log.LogInfo($"Joined channel #{e.Channel}.");
        }

        private void HandleError(object sender, TwitchLib.Communication.Events.OnErrorEventArgs e)
        {
            _log.LogError($"Twitch client error: {e.Exception}");
        }

        private void HandleConnectionError(object sender, OnConnectionErrorArgs e)
        {
            _log.LogError($"Twitch connection error: {e.Error?.Message}");
        }

        private void HandleMessageReceived(object sender, OnMessageReceivedArgs e)
        {
            var msg = e.ChatMessage;

            OnChatMessage?.Invoke(msg.Username, msg.DisplayName);

            var match = CommandPattern.Match(msg.Message.Trim());
            if (!match.Success)
            {
                return;
            }

            var action = match.Groups["action"].Value.ToLowerInvariant();
            var args = match.Groups["args"].Success
                ? match.Groups["args"].Value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                : Array.Empty<string>();

            OnChatCommand?.Invoke(new ChatCommand(
                msg.Username,
                msg.DisplayName,
                action,
                args,
                msg.IsSubscriber,
                msg.IsModerator));
        }

        public void Dispose()
        {
            _client.OnConnected -= HandleConnected;
            _client.OnDisconnected -= HandleDisconnected;
            _client.OnJoinedChannel -= HandleJoinedChannel;
            _client.OnMessageReceived -= HandleMessageReceived;
            _client.OnError -= HandleError;
            _client.OnConnectionError -= HandleConnectionError;
            Disconnect();
        }
    }
}
