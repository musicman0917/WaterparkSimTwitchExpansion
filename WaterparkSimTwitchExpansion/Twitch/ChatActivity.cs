namespace WaterparkSimTwitchExpansion.Twitch
{
    /// <summary>A raw chat message plus the role info needed to compute a brand-new viewer's
    /// starting point balance (see ChaosCommandRouter.HandleChatMessage) - fired for every chat
    /// message, not just "!" commands (that's ChatCommand).</summary>
    public readonly struct ChatActivity
    {
        public string Username { get; }
        public string DisplayName { get; }
        public string Message { get; }
        public bool IsModerator { get; }
        public bool IsVip { get; }
        public bool IsBroadcaster { get; }

        public ChatActivity(string username, string displayName, string message, bool isModerator, bool isVip, bool isBroadcaster)
        {
            Username = username;
            DisplayName = displayName;
            Message = message;
            IsModerator = isModerator;
            IsVip = isVip;
            IsBroadcaster = isBroadcaster;
        }
    }
}
