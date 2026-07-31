namespace WaterparkSimTwitchExpansion.Twitch
{
    /// <summary>A parsed "!command arg1 arg2" chat message.</summary>
    public readonly struct ChatCommand
    {
        public string Username { get; }
        public string DisplayName { get; }
        public string Action { get; }
        public string[] Args { get; }
        public bool IsSubscriber { get; }
        public bool IsModerator { get; }

        public ChatCommand(string username, string displayName, string action, string[] args, bool isSubscriber, bool isModerator)
        {
            Username = username;
            DisplayName = displayName;
            Action = action;
            Args = args;
            IsSubscriber = isSubscriber;
            IsModerator = isModerator;
        }

        public string ArgOrDefault(int index, string fallback = null)
        {
            return index >= 0 && index < Args.Length ? Args[index] : fallback;
        }
    }
}
