using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Profiles.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Profiles.Auth
{
    /// <summary>
    /// Replaces a sub-profile's system username with the name its household gave it, in the
    /// two responses that tell a client who it is.
    ///
    /// <para><b>Why <c>/Users/Public</c> was not enough.</b> Renaming only the sign-in list
    /// worked until somebody used the profile, and then reverted. Android TV keeps its own
    /// copy of every account it has signed into, and the stored copy wins:</para>
    ///
    /// <code>
    /// val storedUserIds = storedUsers.map { it.id }
    /// val publicUsers = serverUserRepository.getPublicServerUsers(server)
    ///     .filterNot { it.id in storedUserIds }
    /// </code>
    ///
    /// <para>and the card paints <c>user.name</c> from that copy. It is written by
    /// <c>authenticateFinish</c> out of the account's real DTO — <c>getCurrentUser()</c> on a
    /// token restore, or the authentication response on a PIN login — neither of which was
    /// being rewritten. So the friendly name lasted exactly until the first sign-in, which is
    /// how Logan's <c>BardFamily</c> read "family" while <c>Bard_test</c>, which he had
    /// signed into on that television, did not.</para>
    ///
    /// <para><b>Why not rename the account.</b> The system username is
    /// <c>&lt;master&gt;_&lt;name&gt;</c> so two households on one server can both have a
    /// "kids", and it is what the switcher, the audit log, Jellyfin's own dashboard and every
    /// device record show. Only what a client is told about itself changes.</para>
    ///
    /// <para><b>Every uncertain case returns null</b>, meaning "leave Jellyfin's bytes exactly
    /// as they are". These are endpoints every client on the server depends on, and a
    /// half-rewritten body is far worse than an unfriendly name.</para>
    /// </summary>
    public static class ProfileNameRewriter
    {
        /// <summary>
        /// The name a household gave this profile, or null if there is none, if it is a
        /// master, or if Bonfire has never heard of the account.
        /// </summary>
        public static string? FriendlyNameFor(PluginConfiguration? config, Guid userId)
        {
            if (config?.Mappings == null || userId == Guid.Empty) return null;

            var row = config.Mappings.FirstOrDefault(m =>
                m.ProfileUserId == userId
                && m.MasterUserId != userId              // a master keeps its real username
                && !string.IsNullOrWhiteSpace(m.ProfileName));

            return row?.ProfileName;
        }

        /// <summary>
        /// The rewritten body, or null to leave the response untouched.
        /// <para>
        /// Handles both shapes this is used on: a bare <c>UserDto</c> (<c>GET /Users/Me</c>)
        /// and an <c>AuthenticationResult</c> with the user nested under <c>User</c>
        /// (<c>POST /Users/AuthenticateByName</c>).
        /// </para>
        /// </summary>
        public static byte[]? Rewrite(
            byte[] produced,
            string? responseContentType,
            PluginConfiguration? config,
            ILogger logger)
        {
            if (config == null || !config.EnableClientProfileList) return null;
            if (produced == null || produced.Length == 0) return null;

            JsonNode? root;
            try
            {
                root = JsonNode.Parse(Encoding.UTF8.GetString(produced));
            }
            catch (JsonException)
            {
                // Not JSON we can read. Common enough — an error page, a redirect body — and
                // never worth a warning on an endpoint this busy.
                return null;
            }

            if (root is not JsonObject obj) return null;

            // The user is either this object or nested one level down.
            var target = Pick(obj, "User", "user") as JsonObject ?? obj;

            var idText = Text(target, "Id", "id");
            if (string.IsNullOrEmpty(idText) || !Guid.TryParse(idText, out var userId)) return null;

            var friendly = FriendlyNameFor(config, userId);
            if (friendly == null) return null;

            var nameKey = target["Name"] != null ? "Name" : target["name"] != null ? "name" : null;
            if (nameKey == null) return null;

            var was = target[nameKey]?.GetValue<string>();
            if (string.Equals(was, friendly, StringComparison.Ordinal)) return null;

            target[nameKey] = friendly;

            logger.LogDebug(
                "ProfilesPlugin: told a client its profile is called {Friendly} rather than {System}.",
                friendly, was);

            return Encoding.UTF8.GetBytes(root.ToJsonString());
        }

        private static JsonNode? Pick(JsonObject obj, params string[] keys)
            => keys.Select(k => obj[k]).FirstOrDefault(v => v != null);

        private static string? Text(JsonObject obj, params string[] keys)
        {
            foreach (var key in keys)
            {
                var node = obj[key];
                if (node == null) continue;

                try
                {
                    return node.GetValue<string>();
                }
                catch (InvalidOperationException)
                {
                    // Present but not a string. Not ours to interpret.
                    return null;
                }
            }

            return null;
        }
    }
}
