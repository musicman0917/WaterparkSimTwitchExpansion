using System;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using TwitchLib.Client;
using TwitchLib.Client.Enums;
using TwitchLib.Client.Events;
using TwitchLib.Client.Models;
using TwitchLib.Communication.Clients;

namespace WaterparkSimTwitchExpansion.Twitch
{
    /// <summary>
    /// Thin wrapper around TwitchLib.Client that connects to a single channel and turns raw
    /// IRC/chat activity into plain events:
    ///   - OnChatMessage: fired for every message (used to track "who is active" for the economy,
    ///     look for bare-number chaos-poll votes like "1"/"2", and compute a brand-new viewer's
    ///     starting point balance from their role).
    ///   - OnChatCommand: fired only for messages that start with "!" (used to trigger purchases).
    ///   - OnSubscription/OnGiftedSub/OnBitsCheered: fired for the economy's role/event-based point
    ///     grants - see each event's own doc comment.
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

        public event Action<ChatActivity> OnChatMessage;
        public event Action<ChatCommand> OnChatCommand;
        public event Action OnConnected;
        public event Action OnDisconnected;

        /// <summary>Fired for every new subscription AND every monthly resub - (username,
        /// displayName, tier: 1/2/3, Prime counted as tier 1).</summary>
        public event Action<string, string, int> OnSubscription;

        /// <summary>Fired once per gifted sub, for the GIFTER (not the recipient) - (username,
        /// displayName, tier). Deliberately only listens to TwitchLib's per-recipient gift event,
        /// not the separate "mass gift" community-sub event: Twitch's IRC fires BOTH for a mass
        /// gift (one community-sub event, then one gift event per recipient), so listening to only
        /// the per-recipient event already covers mass gifts correctly without double-counting -
        /// see the doc comment on HandleGiftedSubscription.</summary>
        public event Action<string, string, int> OnGiftedSub;

        /// <summary>Fired whenever a chat message includes bits - (username, displayName, bits).</summary>
        public event Action<string, string, int> OnBitsCheered;

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
            _client.OnNewSubscriber += HandleNewSubscriber;
            _client.OnReSubscriber += HandleReSubscriber;
            _client.OnGiftedSubscription += HandleGiftedSubscription;
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

            OnChatMessage?.Invoke(new ChatActivity(msg.Username, msg.DisplayName, msg.Message, msg.IsModerator, msg.IsVip, msg.IsBroadcaster));

            if (msg.Bits > 0)
            {
                OnBitsCheered?.Invoke(msg.Username, msg.DisplayName, msg.Bits);
            }

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
                msg.IsModerator,
                msg.IsBroadcaster));
        }

        /// <summary>Twitch counts a Prime sub as the tier-1 equivalent for payout purposes; this
        /// mirrors that for the points economy since SubscriptionPlan has no numeric tier for it.</summary>
        private static int TierNumber(SubscriptionPlan plan) => plan switch
        {
            SubscriptionPlan.Tier1 => 1,
            SubscriptionPlan.Tier2 => 2,
            SubscriptionPlan.Tier3 => 3,
            SubscriptionPlan.Prime => 1,
            _ => 1,
        };

        private void HandleNewSubscriber(object sender, OnNewSubscriberArgs e)
        {
            OnSubscription?.Invoke(e.Subscriber.Login, e.Subscriber.DisplayName, TierNumber(e.Subscriber.SubscriptionPlan));
        }

        private void HandleReSubscriber(object sender, OnReSubscriberArgs e)
        {
            OnSubscription?.Invoke(e.ReSubscriber.Login, e.ReSubscriber.DisplayName, TierNumber(e.ReSubscriber.SubscriptionPlan));
        }

        /// <summary>
        /// Deliberately does NOT also listen to TwitchClient.OnCommunitySubscription (the "mass
        /// gift" event, e.g. someone gifting 5 subs at once): Twitch's IRC fires one community-sub
        /// event for the batch AND one individual gift event (this one) per recipient, so a mass
        /// gift of 5 already fires this handler 5 times on its own. Listening to both would double
        /// (or worse) the points awarded for every mass gift.
        /// </summary>
        private void HandleGiftedSubscription(object sender, OnGiftedSubscriptionArgs e)
        {
            if (e.GiftedSubscription.IsAnonymous)
            {
                return;
            }

            OnGiftedSub?.Invoke(e.GiftedSubscription.Login, e.GiftedSubscription.DisplayName, TierNumber(e.GiftedSubscription.MsgParamSubPlan));
        }

        public void Dispose()
        {
            _client.OnConnected -= HandleConnected;
            _client.OnDisconnected -= HandleDisconnected;
            _client.OnJoinedChannel -= HandleJoinedChannel;
            _client.OnMessageReceived -= HandleMessageReceived;
            _client.OnError -= HandleError;
            _client.OnConnectionError -= HandleConnectionError;
            _client.OnNewSubscriber -= HandleNewSubscriber;
            _client.OnReSubscriber -= HandleReSubscriber;
            _client.OnGiftedSubscription -= HandleGiftedSubscription;
            Disconnect();
        }
    }
}
