using System;

namespace WaterparkSimTwitchExpansion.Economy
{
    /// <summary>Plain-data record persisted to the points JSON file. One per Twitch user.</summary>
    public sealed class UserAccount
    {
        public string Username { get; set; }
        public string DisplayName { get; set; }
        public int Points { get; set; }
        public DateTime LastSeenUtc { get; set; }
    }
}
