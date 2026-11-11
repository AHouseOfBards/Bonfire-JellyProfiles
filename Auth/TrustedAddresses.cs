using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Jellyfin.Profiles.Configuration;

namespace Jellyfin.Profiles.Auth
{
    /// <summary>
    /// The addresses an administrator has said count as home for "No PIN on the server's
    /// network" (issue #42: a parent's house the server is not in). Off while the list is
    /// empty.
    ///
    /// <para><b>What it feeds.</b> Only a profile's own skip, <c>CanSkipPin</c>, and only for a
    /// profile whose household turned that skip on. It is not "local": the cross-household
    /// bypass, the "seen at home" memory and the device records never read it. And it never
    /// reaches a main account or an administrator, which is decided where it is used.</para>
    ///
    /// <para><b>Fixed limits, not settings.</b> A looser value lets a stranger past a PIN, so an
    /// administrator can only be stricter:
    /// at most <see cref="MaxEntries"/> entries, and no range wider than /16 for IPv4 or /48
    /// for IPv6. A /16 is 65,536 addresses, already more than any one household has; /48 is the
    /// smallest block an ISP routinely hands to a single customer, and a /64 is what a home
    /// network is given. A /0 or /8 typed by mistake would trust a large part of the internet.</para>
    ///
    /// <para><b>Strict parsing.</b> <see cref="IPAddress.TryParse(string, out IPAddress)"/> accepts
    /// "1", "10.1", "0x7f.1" and a leading zero as octal; each of those is an address nobody
    /// meant. Only dotted decimal with four parts, no leading zeros, and colon-form IPv6
    /// without a zone are taken. An IPv4-mapped IPv6 entry is refused (write it as IPv4);
    /// the same mapped form arriving on a request is turned back into IPv4 before the
    /// comparison, so a dual-stack socket cannot hide a trusted address nor smuggle one.</para>
    /// </summary>
    public static class TrustedAddresses
    {
        /// <summary>Most entries the list may hold.</summary>
        public const int MaxEntries = 10;

        /// <summary>Widest IPv4 range allowed: /16.</summary>
        public const int MinPrefixV4 = 16;

        /// <summary>Widest IPv6 range allowed: /48.</summary>
        public const int MinPrefixV6 = 48;

        /// <summary>
        /// Reads one entry: an address, or an address and a prefix length ("203.0.113.0/24").
        /// On success <paramref name="canonical"/> is the network written back in full, with
        /// the host bits cleared.
        /// </summary>
        public static bool TryParse(string? text, out byte[] network, out int prefix, out string canonical, out string? error)
        {
            network = Array.Empty<byte>();
            prefix = 0;
            canonical = string.Empty;
            error = null;

            var entry = text?.Trim();
            if (string.IsNullOrEmpty(entry)) { error = "An address is empty."; return false; }

            var slash = entry.IndexOf('/');
            var addressText = slash < 0 ? entry : entry.Substring(0, slash);
            var prefixText = slash < 0 ? null : entry.Substring(slash + 1);

            if (addressText.Length == 0 || addressText.Any(char.IsWhiteSpace) || addressText.Contains('%'))
            {
                error = $"'{entry}' is not an address.";
                return false;
            }

            IPAddress? address;
            bool v6 = addressText.Contains(':');
            if (v6)
            {
                if (!IPAddress.TryParse(addressText, out address) || address.AddressFamily != AddressFamily.InterNetworkV6)
                {
                    error = $"'{entry}' is not an address.";
                    return false;
                }
                if (address.IsIPv4MappedToIPv6)
                {
                    error = $"'{entry}': write an IPv4 address as IPv4.";
                    return false;
                }
            }
            else
            {
                if (!TryParseDottedQuad(addressText, out address))
                {
                    error = $"'{entry}' is not an address.";
                    return false;
                }
            }

            var bytes = address!.GetAddressBytes();
            int bits = bytes.Length * 8;
            int length = bits;

            if (prefixText != null)
            {
                // Digits only: int.TryParse would take "+24" and " 24".
                if (prefixText.Length == 0 || prefixText.Length > 3 || !prefixText.All(c => c >= '0' && c <= '9')
                    || !int.TryParse(prefixText, out length))
                {
                    error = $"'{entry}' has no valid range length.";
                    return false;
                }
            }

            int floor = v6 ? MinPrefixV6 : MinPrefixV4;
            if (length > bits || length < floor)
            {
                error = $"'{entry}' is too wide. Ranges go no wider than /{floor}.";
                return false;
            }

            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            {
                error = $"'{entry}' is not an address.";
                return false;
            }

            network = Mask(bytes, length);
            prefix = length;
            canonical = new IPAddress(network) + (length == bits ? string.Empty : "/" + length);
            return true;
        }

        /// <summary>The reason <paramref name="entries"/> cannot be saved, or null when they can.</summary>
        public static string? Validate(IEnumerable<string>? entries)
        {
            if (entries == null) return null;
            var list = entries.ToList();
            if (list.Count > MaxEntries) return $"At most {MaxEntries} addresses.";
            foreach (var entry in list)
            {
                if (!TryParse(entry, out _, out _, out _, out var error)) return error;
            }

            return null;
        }

        /// <summary>
        /// The entries that are valid, written in full, once each, no more than
        /// <see cref="MaxEntries"/>. For a backup file: a restore keeps what it can and never
        /// widens past the limits.
        /// </summary>
        public static List<string> Normalize(IEnumerable<string>? entries)
        {
            var kept = new List<string>();
            if (entries == null) return kept;
            foreach (var entry in entries)
            {
                if (kept.Count >= MaxEntries) break;
                if (TryParse(entry, out _, out _, out var canonical, out _) && !kept.Contains(canonical)) kept.Add(canonical);
            }

            return kept;
        }

        /// <summary>
        /// Whether <paramref name="address"/>, the one Jellyfin resolved after its known
        /// proxies, is inside a listed range. False for no list, no address, or an entry that no
        /// longer parses (a hand-edited file); never throws.
        /// </summary>
        public static bool Contains(PluginConfiguration? config, IPAddress? address)
        {
            var list = config?.TrustedPinAddresses;
            if (list == null || list.Count == 0 || address == null) return false;

            try
            {
                if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
                var bytes = address.GetAddressBytes();

                // Only the first MaxEntries, so a file edited by hand cannot grow the work.
                foreach (var entry in list.Take(MaxEntries))
                {
                    if (!TryParse(entry, out var network, out var prefix, out _, out _)) continue;
                    if (network.Length != bytes.Length) continue;
                    if (Mask(bytes, prefix).AsSpan().SequenceEqual(network)) return true;
                }
            }
            catch (Exception)
            {
                return false;
            }

            return false;
        }

        private static byte[] Mask(byte[] bytes, int prefix)
        {
            var result = new byte[bytes.Length];
            for (int i = 0; i < bytes.Length; i++)
            {
                int bitsHere = Math.Max(0, Math.Min(8, prefix - i * 8));
                int mask = bitsHere == 0 ? 0 : (0xFF << (8 - bitsHere)) & 0xFF;
                result[i] = (byte)(bytes[i] & mask);
            }

            return result;
        }

        private static bool TryParseDottedQuad(string text, out IPAddress? address)
        {
            address = null;
            var parts = text.Split('.');
            if (parts.Length != 4) return false;
            var bytes = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                var p = parts[i];
                if (p.Length == 0 || p.Length > 3 || !p.All(c => c >= '0' && c <= '9')) return false;
                if (p.Length > 1 && p[0] == '0') return false;
                var n = int.Parse(p);
                if (n > 255) return false;
                bytes[i] = (byte)n;
            }

            address = new IPAddress(bytes);
            return true;
        }
    }
}
