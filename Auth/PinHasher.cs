using System;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Profiles.Auth
{
    /// <summary>
    /// PIN hashing and verification, in one place.
    /// <para>
    /// This lived as protected members on <c>ProfilesBaseController</c> until the
    /// authentication provider needed it too, and a provider is not a controller. The
    /// alternative was a second implementation of a security primitive, which is how two
    /// implementations drift and one of them stops being constant-time.
    /// </para>
    /// <para>
    /// PINs are 4-8 digits, so the entire keyspace (10^4 - 10^8) is trivially enumerable
    /// against a fast unsalted digest. Hashes are therefore PBKDF2-SHA256 with a per-PIN
    /// random salt, stored as:
    /// </para>
    /// <code>pbkdf2.sha256$&lt;iterations&gt;$&lt;base64 salt&gt;$&lt;base64 hash&gt;</code>
    /// <para>
    /// Hashes written before that change are bare 64-char SHA-256 hex. Those are still
    /// accepted on verification and transparently re-hashed on the next successful entry,
    /// so no existing PIN is invalidated.
    /// </para>
    /// </summary>
    public static class PinHasher
    {
        public const int Iterations = 150_000;
        private const int SaltBytes = 16;
        private const int HashBytes = 32;
        public const string Prefix = "pbkdf2.sha256$";

        /// <summary>Why a verification failed, so a caller can log the malformed cases.</summary>
        public enum PinResult
        {
            /// <summary>The PIN matches the stored hash.</summary>
            Match,

            /// <summary>The PIN does not match, or one of the two was empty.</summary>
            NoMatch,

            /// <summary>The stored hash could not be parsed. Never treated as a match.</summary>
            MalformedHash
        }

        public static string Hash(string? pin)
        {
            if (string.IsNullOrEmpty(pin)) return string.Empty;

            var salt = RandomNumberGenerator.GetBytes(SaltBytes);
            var hash = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(pin), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);

            return $"{Prefix}{Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
        }

        /// <summary>Legacy (pre-PBKDF2) unsalted SHA-256 hex digest. Verification only.</summary>
        private static string LegacyHash(string pin)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pin))).ToLowerInvariant();

        /// <summary>True when the stored hash still uses the legacy format and should be upgraded.</summary>
        public static bool IsLegacy(string? storedHash)
            => !string.IsNullOrEmpty(storedHash) && !storedHash.StartsWith(Prefix, StringComparison.Ordinal);

        /// <summary>
        /// Constant-time comparison of a candidate PIN against a stored hash of either
        /// format. An empty stored hash is <see cref="PinResult.NoMatch"/> — "no PIN set" is
        /// a decision for the caller, and answering it here would let an empty PIN open a
        /// profile that simply has none configured.
        /// </summary>
        public static PinResult Verify(string? pin, string? storedHash)
        {
            if (string.IsNullOrEmpty(pin) || string.IsNullOrEmpty(storedHash)) return PinResult.NoMatch;

            if (IsLegacy(storedHash))
            {
                return CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(LegacyHash(pin)),
                    Encoding.UTF8.GetBytes(storedHash))
                    ? PinResult.Match
                    : PinResult.NoMatch;
            }

            // pbkdf2.sha256$<iterations>$<salt>$<hash>
            var parts = storedHash.Substring(Prefix.Length).Split('$');
            if (parts.Length != 3
                || !int.TryParse(parts[0], out var iterations)
                || iterations <= 0)
            {
                return PinResult.MalformedHash;
            }

            try
            {
                var salt = Convert.FromBase64String(parts[1]);
                var expected = Convert.FromBase64String(parts[2]);
                var actual = Rfc2898DeriveBytes.Pbkdf2(
                    Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
                return CryptographicOperations.FixedTimeEquals(actual, expected)
                    ? PinResult.Match
                    : PinResult.NoMatch;
            }
            catch (FormatException)
            {
                return PinResult.MalformedHash;
            }
        }
    }
}
