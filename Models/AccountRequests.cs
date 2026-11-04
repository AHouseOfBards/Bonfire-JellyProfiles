namespace Jellyfin.Profiles.Models
{
    /// <summary>A main account's own settings. Null leaves a field as it is.</summary>
    public class UpdateAccountRequest
    {
        /// <summary>The current main PIN, when one is set.</summary>
        public string? MasterPin { get; set; }

        /// <summary>A new main PIN, 4–8 digits. Empty clears it. Null leaves it.</summary>
        public string? Pin { get; set; }

        /// <summary>False opens the main account from its own switcher without the PIN.</summary>
        public bool? AskPinToOpen { get; set; }
    }

    /// <summary>A main PIN to check.</summary>
    public class VerifyAccountPinRequest
    {
        public string? Pin { get; set; }
    }
}
