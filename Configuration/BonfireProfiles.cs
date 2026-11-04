using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Profiles.Configuration;
using Jellyfin.Profiles.Controllers;

namespace Jellyfin.Profiles
{
    /// <summary>
    /// Bonfire's households, for other plugins running in the same server.
    /// <para>
    /// Ask Jellyfin's service provider for it. A plugin that doesn't reference Bonfire's
    /// assembly can find it by the interface's full name,
    /// <c>Jellyfin.Profiles.IBonfireProfiles</c>. Read-only: changes go through the HTTP
    /// API, where they are authorised.</para>
    /// <para>
    /// Every call reads the configuration as it is at that moment, so an answer is never
    /// stale, and none of them waits on Jellyfin.</para>
    /// </summary>
    public interface IBonfireProfiles
    {
        /// <summary>True when the user is a profile of some other account.</summary>
        bool IsProfile(Guid userId);

        /// <summary>
        /// The main account of the household the user belongs to: its main account for a
        /// profile, itself for anyone else.
        /// </summary>
        Guid GetMainAccount(Guid userId);

        /// <summary>The main account first, then each of its profiles.</summary>
        IReadOnlyList<Guid> GetHousehold(Guid userId);
    }

    /// <inheritdoc />
    public sealed class BonfireProfiles : IBonfireProfiles
    {
        /// <inheritdoc />
        public bool IsProfile(Guid userId) => MainOf(Snapshot(), userId) != userId;

        /// <inheritdoc />
        public Guid GetMainAccount(Guid userId) => MainOf(Snapshot(), userId);

        /// <inheritdoc />
        public IReadOnlyList<Guid> GetHousehold(Guid userId)
        {
            var config = Snapshot();
            var main = MainOf(config, userId);
            var list = new List<Guid> { main };
            if (config != null)
            {
                list.AddRange(config.Mappings
                    .Where(m => m.MasterUserId == main && m.ProfileUserId != main)
                    .Select(m => m.ProfileUserId));
            }
            return list;
        }

        private static PluginConfiguration? Snapshot() => ProfilesBaseController.ReadConfigSnapshot();

        private static Guid MainOf(PluginConfiguration? config, Guid userId)
        {
            var row = config?.Mappings.FirstOrDefault(m => m.ProfileUserId == userId);
            return row != null && row.MasterUserId != Guid.Empty ? row.MasterUserId : userId;
        }
    }
}
