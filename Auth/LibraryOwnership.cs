using System;
using System.Collections.Generic;
using System.Linq;
using Campground.Shared.Registry;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Users;

namespace Jellyfin.Profiles.Auth
{
    /// <summary>
    /// Which libraries another Campground plugin manages access to (FieldGuide's "For You:" rows), and how
    /// Bonfire leaves them alone. Bonfire neither grants nor blocks such a library: not for a profile, not in
    /// "every library", not on its library screens, not when an account is copied into a profile.
    /// <para>
    /// This is the one place Bonfire asks. Everything else uses <see cref="ManagedByOwnerIds"/> or
    /// <see cref="ManagedFolderIds"/>, so every site gets the same answer. The question itself belongs to
    /// <see cref="Ownership.LibraryOwnerOf"/>; this only reads the four values it needs off Jellyfin.
    /// </para>
    /// <para>
    /// The failure direction is "hidden and untouched", never "open": an owned library is taken out of every
    /// list Bonfire builds, and an account's existing access to one is carried over exactly as it was
    /// (<see cref="KeptAccess"/>), never widened.
    /// </para>
    /// </summary>
    internal static class LibraryOwnership
    {
        /// <summary>True when the library's owner manages who may open it, so Bonfire must not.</summary>
        internal static bool ManagedByOwner(ILibraryManager libraryManager, VirtualFolderInfo folder)
        {
            // The library's own folder (the CollectionFolder's path) is not on the virtual folder; it is what
            // carries FieldGuide's "fieldguide-" marker, so it is read off the item. A lookup that fails only
            // loses the marker; the name and any registration still answer.
            string? folderPath = null;
            if (Guid.TryParse(folder.ItemId, out var id) && id != Guid.Empty)
            {
                try
                {
                    folderPath = libraryManager.GetItemById(id)?.Path;
                }
                catch (Exception)
                {
                    folderPath = null;
                }
            }

            var owner = Ownership.LibraryOwnerOf(folder.ItemId, folder.Name, folder.Locations, folderPath);
            return owner != null && owner.AccessManagedByOwner;
        }

        /// <summary>The ids of the libraries Bonfire leaves alone. Empty when nothing is registered or reserved.</summary>
        internal static HashSet<Guid> ManagedByOwnerIds(ILibraryManager libraryManager)
        {
            var ids = new HashSet<Guid>();
            foreach (var f in libraryManager.GetVirtualFolders())
            {
                if (Guid.TryParse(f.ItemId, out var id) && id != Guid.Empty && ManagedByOwner(libraryManager, f))
                {
                    ids.Add(id);
                }
            }

            return ids;
        }

        /// <summary>Every library Bonfire manages: all the server's libraries but the owned ones.</summary>
        internal static List<Guid> ManagedFolderIds(ILibraryManager libraryManager)
        {
            var ids = new List<Guid>();
            foreach (var f in libraryManager.GetVirtualFolders())
            {
                if (Guid.TryParse(f.ItemId, out var id) && id != Guid.Empty && !ManagedByOwner(libraryManager, f))
                {
                    ids.Add(id);
                }
            }

            return ids;
        }

        /// <summary>
        /// What an account has now of the owned libraries, in the two lists Bonfire writes, so a write can put
        /// the same back. "Every library" is read as every owned library listed, since Bonfire always writes
        /// an explicit list; the account's blocked ones stay blocked, so what it could open is unchanged.
        /// </summary>
        internal static (Guid[] Enabled, Guid[] Blocked) KeptAccess(HashSet<Guid> owned, UserPolicy account)
        {
            if (owned.Count == 0)
            {
                return (Array.Empty<Guid>(), Array.Empty<Guid>());
            }

            var enabled = account.EnableAllFolders
                ? owned.ToArray()
                : (account.EnabledFolders ?? Array.Empty<Guid>()).Where(owned.Contains).ToArray();
            var blocked = (account.BlockedMediaFolders ?? Array.Empty<Guid>()).Where(owned.Contains).ToArray();
            return (enabled, blocked);
        }
    }
}
