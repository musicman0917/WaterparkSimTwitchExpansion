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

        /// <summary>True once this viewer has received the one-time "just followed" point top-up
        /// (or never needed one - e.g. they started as a follower or VIP/mod already). Prevents
        /// unfollowing and re-following from farming the bonus repeatedly.</summary>
        public bool FollowBonusGranted { get; set; }

        /// <summary>Throttles how often ChaosCommandRouter re-checks follower status for someone
        /// who hasn't gotten the bonus yet, so a chatty non-follower doesn't trigger a Helix call
        /// on every message.</summary>
        public DateTime? LastFollowCheckUtc { get; set; }
    }
}
