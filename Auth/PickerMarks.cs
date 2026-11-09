using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Data.Queries;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Profiles.Auth
{
    /// <summary>
    /// Which TVs were in a child's profile, kept on disk so a restart cannot forget it.
    ///
    /// <para><b>What went wrong.</b> The picker keeps its sessions, and the set of tokens it
    /// must never serve as the main account, in memory. A restart empties both. A TV that was
    /// in a child's profile then sends its next request on the main account's token, which is
    /// what the app holds; with the app no longer ticked, the profiles deleted, or the emergency
    /// code in use, nothing of ours is looking at it, and Jellyfin answers it as the main
    /// account: the child is on the unrestricted account.</para>
    ///
    /// <para><b>What is kept</b> is one row per TV in a child's profile, written when the
    /// profile is opened and removed when the TV is back on the picker or signed out: the
    /// main account's token (as a hash, since it is only ever compared), who it belongs to, and
    /// the profile's own session token, which is what has to be signed out and is no more
    /// secret than the row Jellyfin keeps for it. After a restart a request on a marked token
    /// is signed out like any other child's TV, or, when the picker is on, is let back to the
    /// picker with the profile's session ended.</para>
    ///
    /// <para><b>At start-up</b> <see cref="SweepAsync"/> ends the profile sessions the picker
    /// made before the restart (Jellyfin tokens do not expire, so they would otherwise stay
    /// valid for good), and drops a row whose main token no longer exists.</para>
    /// </summary>
    internal static class PickerMarks
    {
        internal sealed class Mark
        {
            public string MasterTokenHash { get; set; } = string.Empty;
            public Guid MasterId { get; set; }
            public Guid ProfileId { get; set; }

            /// <summary>The profile's session token; empty once it has been ended.</summary>
            public string ProfileToken { get; set; } = string.Empty;
        }

        private static readonly ConcurrentDictionary<string, Mark> Marks = new(StringComparer.Ordinal);
        private static readonly object PickerFileLock = new();
        private static volatile bool _loaded;

        /// <summary>Set when the start-up service is built; where a file that cannot be read or written is said.</summary>
        internal static ILogger? Log = null;

        internal static string Hash(string token)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

        /// <summary>
        /// True when any TV is marked. One flag read once the file has been read, which is the
        /// whole cost on a request while nobody is: callers ask this before anything else.
        /// </summary>
        internal static bool Any
        {
            get
            {
                if (!_loaded) Load();
                return !Marks.IsEmpty;
            }
        }

        internal static Mark? Find(string? token)
        {
            if (string.IsNullOrEmpty(token) || !Any) return null;
            return Marks.TryGetValue(Hash(token), out var mark) ? mark : null;
        }

        internal static IReadOnlyList<Mark> All()
        {
            if (!_loaded) Load();
            return Marks.Values.ToList();
        }

        /// <summary>The TV is in a child's profile: written before the profile is used.</summary>
        internal static void Add(string masterToken, Guid masterId, Guid profileId, string profileToken)
        {
            if (!_loaded) Load();
            Marks[Hash(masterToken)] = new Mark
            {
                MasterTokenHash = Hash(masterToken),
                MasterId = masterId,
                ProfileId = profileId,
                ProfileToken = profileToken ?? string.Empty,
            };
            Save();
        }

        /// <summary>The TV is back on the picker, or signed out.</summary>
        internal static void Remove(string masterToken)
        {
            if (!_loaded) Load();
            if (Marks.TryRemove(Hash(masterToken), out _)) Save();
        }

        /// <summary>For harnesses: forget everything, on disk too.</summary>
        internal static void Clear()
        {
            Marks.Clear();
            _loaded = true;
            Save();
        }

        /// <summary>For harnesses: a restart. What is in memory is lost and read back from disk.</summary>
        internal static void Reload()
        {
            Marks.Clear();
            _loaded = false;
        }

        private static string? FilePath()
        {
            var paths = Plugin.Instance?.AppPaths;
            return paths == null ? null : Path.Combine(paths.DataPath, "plugins", "ProfilesManagement", "proxy_picker.json");
        }

        private static void Load()
        {
            lock (PickerFileLock)
            {
                if (_loaded) return;
                _loaded = true;
                var path = FilePath();
                if (path == null || !File.Exists(path)) return;
                try
                {
                    var rows = JsonSerializer.Deserialize<List<Mark>>(File.ReadAllText(path));
                    if (rows == null) return;
                    foreach (var row in rows.Where(r => !string.IsNullOrEmpty(r?.MasterTokenHash))) Marks[row.MasterTokenHash] = row;
                }
                catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
                {
                    // Unreadable: the TVs it named are served as the picker would serve them
                    // once they next ask, and nothing worse than before this existed.
                    Log?.LogWarning(ex, "ProfilesPlugin: {File} could not be read; which TVs were in a child's profile is forgotten.", path);
                }
            }
        }

        private static void Save()
        {
            var path = FilePath();
            if (path == null) return;
            lock (PickerFileLock)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var temp = path + ".tmp";
                    File.WriteAllText(temp, JsonSerializer.Serialize(Marks.Values.ToList()));
                    File.Move(temp, path, true);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Log?.LogWarning(ex, "ProfilesPlugin: could not write {File}; a restart would forget which TVs are in a child's profile.", path);
                }
            }
        }

        /// <summary>
        /// At start-up: ends the profile sessions the picker made before the restart, and drops
        /// a row whose main token Jellyfin no longer has (the TV signed out in the meantime).
        /// A row is kept after its profile session is ended: the TV's main token still has to be
        /// recognised the next time it asks. Best effort; never throws.
        /// </summary>
        internal static async Task SweepAsync(ISessionManager? sessions, IDeviceManager? devices, ILogger logger)
        {
            try
            {
                foreach (var mark in All())
                {
                    if (devices != null && !MainTokenExists(devices, mark))
                    {
                        Marks.TryRemove(mark.MasterTokenHash, out _);
                        Save();
                    }

                    if (sessions == null || string.IsNullOrEmpty(mark.ProfileToken)) continue;
                    try
                    {
                        await sessions.Logout(mark.ProfileToken).ConfigureAwait(false);
                        mark.ProfileToken = string.Empty;
                        Save();
                        logger.LogInformation("ProfilesPlugin: ended a profile session the picker made before the restart.");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogDebug(ex, "ProfilesPlugin: a profile session from before the restart was already gone.");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "ProfilesPlugin: could not look at the profile sessions from before the restart.");
            }
        }

        private static bool MainTokenExists(IDeviceManager devices, Mark mark)
        {
            try
            {
                var rows = devices.GetDevices(new DeviceQuery { UserId = mark.MasterId })?.Items;
                if (rows == null) return true;
                return rows.Any(d => !string.IsNullOrEmpty(d.AccessToken) && Hash(d.AccessToken) == mark.MasterTokenHash);
            }
            catch (Exception)
            {
                // Cannot tell: keep it.
                return true;
            }
        }
    }
}
