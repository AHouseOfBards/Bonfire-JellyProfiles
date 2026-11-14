using System;

namespace Jellyfin.Profiles.Configuration
{
    /// <summary>
    /// Closed for maintenance: only administrators can use the server.
    ///
    /// <para><b>It does not end by itself</b>, unless the administrator asked it to.
    /// <see cref="UntilUtc"/> is what the household is told, and the Retry-After an app is sent.
    /// An upgrade that overruns should not reopen the server half-finished because a clock said
    /// so, so reopening then is chosen each time it is closed (<see cref="ReopenAutomatically"/>),
    /// off unless ticked.</para>
    /// </summary>
    public class MaintenanceMode
    {
        public bool Enabled { get; set; }

        /// <summary>Shown to everyone who is turned away. Plain text. Empty means the default.</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>When the administrator expects to be done. Shown, and acted on only
        /// when <see cref="ReopenAutomatically"/> is set.</summary>
        public DateTime? UntilUtc { get; set; }

        /// <summary>
        /// Open again at <see cref="UntilUtc"/>, by itself. The server does it: the gate stops
        /// refusing at that moment and settles the saved state on the next request, so it does not
        /// depend on the administrator's page, a browser or a timer a restart would lose. Meaningless
        /// without an end time.
        /// </summary>
        public bool ReopenAutomatically { get; set; }

        /// <summary>True when it was asked to reopen by itself and that time has come.</summary>
        public bool HasLapsed(DateTime nowUtc)
            => Enabled && ReopenAutomatically && UntilUtc.HasValue && UntilUtc.Value <= nowUtc;

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
