using System;
using System.Collections.Generic;
using Jellyfin.Profiles.Configuration;

namespace Jellyfin.Profiles.Models
{
    /// <summary>POST /limits/{profileId}. Replaces the profile's rules.</summary>
    public class SetViewingRulesRequest
    {
        public bool HoursEnabled { get; set; }
        public List<ViewingWindow>? Windows { get; set; }

        /// <summary>Minutes a day; 0 is no limit.</summary>
        public int DailyMinutes { get; set; }

        /// <summary>Minutes on Saturday and Sunday. Null is the same as every day; 0 is no limit.</summary>
        public int? WeekendMinutes { get; set; }

        /// <summary>The language the messages to the profile's apps are written in.</summary>
        public string? Language { get; set; }

        public string? MasterPin { get; set; }
    }

    /// <summary>POST /limits/{profileId}/grant. 0 minutes answers a request with no.</summary>
    public class GrantTimeRequest
    {
        public int Minutes { get; set; }
        public string? MasterPin { get; set; }
    }

    /// <summary>GET /limits/{profileId}, and the viewing part of /list.</summary>
    public class ViewingResponse
    {
        public Guid ProfileId { get; set; }
        public bool Limited { get; set; }
        public bool Allowed { get; set; } = true;

        /// <summary>Why it is refused: "hours", "limit", or empty.</summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>What ends it first: "hours", "limit", "extra", or empty for nothing.</summary>
        public string Binding { get; set; } = string.Empty;

        public int? MinutesLeft { get; set; }
        public DateTime? UntilUtc { get; set; }
        public DateTime? NextStartUtc { get; set; }
        public int UsedMinutes { get; set; }
        public int? LimitMinutes { get; set; }
        public DateTime? ExtraUntilUtc { get; set; }

        /// <summary>When the profile asked for more time, while nobody has answered.</summary>
        public DateTime? AskedUtc { get; set; }

        /// <summary>The household's zone, as an IANA id.</summary>
        public string TimeZone { get; set; } = string.Empty;

        /// <summary>The rules. Null in /list.</summary>
        public ViewingRulesView? Rules { get; set; }
    }

    public class ViewingRulesView
    {
        public bool HoursEnabled { get; set; }
        public List<ViewingWindow> Windows { get; set; } = new();
        public int DailyMinutes { get; set; }
        public int? WeekendMinutes { get; set; }
    }

    /// <summary>GET /timezones.</summary>
    public class TimeZonesResponse
    {
        /// <summary>Every zone the server knows, as IANA ids.</summary>
        public IReadOnlyList<string> Zones { get; set; } = Array.Empty<string>();

        /// <summary>The server's own zone.</summary>
        public string Server { get; set; } = string.Empty;

        /// <summary>The administrator's default, or empty for the server's own.</summary>
        public string Default { get; set; } = string.Empty;

        /// <summary>The caller's household's choice, or empty for the default.</summary>
        public string Household { get; set; } = string.Empty;

        /// <summary>The zone in use for the caller's household.</summary>
        public string Effective { get; set; } = string.Empty;
    }
}
