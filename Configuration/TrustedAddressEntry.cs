using System;
using System.Collections.Generic;

namespace Jellyfin.Profiles.Configuration
{
    /// <summary>
    /// One trusted address (issue #42): an address or range, and the main accounts whose
    /// households open without a PIN from it. Set by an administrator only. See
    /// <see cref="Auth.TrustedAddresses"/> for the rule and the limits.
    /// </summary>
    public class TrustedAddressEntry
    {
        /// <summary>An IPv4 or IPv6 address or range: "203.0.113.7", "203.0.113.0/24", "2001:db8:abcd::/48".</summary>
        public string Address { get; set; } = string.Empty;

        /// <summary>The main accounts it is for. Never an administrator.</summary>
        public List<Guid> UserIds { get; set; } = new();
    }
}
