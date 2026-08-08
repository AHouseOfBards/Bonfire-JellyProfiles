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

        /// <summary>
        /// Legacy 1.3.1-beta field: "gate" or "native". Accepted so a cached copy of the older
        /// profiles.js keeps working, and expanded into the two fields above. When both arrive
        /// the newer fields win — see UpdatePreferences.
        /// </summary>
        public string? SwitcherMode { get; set; }
    }
}
