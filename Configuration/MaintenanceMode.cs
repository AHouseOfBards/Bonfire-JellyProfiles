using System;

namespace Jellyfin.Profiles.Configuration
{
    /// <summary>
    /// Closed for maintenance: only administrators can use the server.
    ///
    /// <para><b>It does not end by itself.</b> <see cref="UntilUtc"/> is what the household is
    /// told, and the Retry-After an app is sent. An upgrade that overruns should not reopen
    /// the server half-finished because a clock said so.</para>
    /// </summary>
    public class MaintenanceMode
    {
        public bool Enabled { get; set; }

        /// <summary>Shown to everyone who is turned away. Plain text. Empty means the default.</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>When the administrator expects to be done. Shown, never acted on.</summary>
        public DateTime? UntilUtc { get; set; }

        /// <summary>
        /// Stop what is playing and refuse video and audio too. Off by default: a film
        /// already playing finishes, and nothing new can start.
        /// </summary>
        public bool StopPlayback { get; set; }

        /// <summary>When it was turned on.</summary>
        public DateTime? StartedUtc { get; set; }

        /// <summary>What is shown when the administrator wrote nothing.</summary>
        public const string DefaultMessage = "The server is closed for maintenance.";

        /// <summary>Longest message accepted, the same as an announcement.</summary>
        public const int MaxMessageLength = Announcements.MaxMessageLength;

        /// <summary>
        /// The message to show: the administrator's, or the default. A method, not a property,
        /// so it is never written into the saved configuration or an export.
        /// </summary>
        public string ShownMessage() => string.IsNullOrWhiteSpace(Message) ? DefaultMessage : Message.Trim();
    }
}
