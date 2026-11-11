namespace Jellyfin.Profiles.Models
{
    public class UpdatePreferencesRequest
    {
        /// <summary>
        /// Whether the "Who's Watching?" screen appears when the client loads. Null leaves it
        /// unchanged, so a caller can set one preference without having to know the other.
        /// </summary>
        public bool? AskOnStartup { get; set; }

        /// <summary>
        /// "button" or "menu". Anything unrecognised normalises to "button". Null leaves it
        /// unchanged.
        /// </summary>
        public string? SwitcherLocation { get; set; }
    }
}
