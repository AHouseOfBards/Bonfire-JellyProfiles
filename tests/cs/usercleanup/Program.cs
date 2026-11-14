using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Profiles;
using Jellyfin.Profiles.Configuration;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

// ─────────────────────────────────────────────────────────────────────────────
// A user deleted in Dashboard → Users takes Bonfire's records about them along
// (Campground.Shared 1.0.1, UserDataCleanup; owner decision 2026-11-13).
//
// Until 2.0 nothing did: the rows stayed, counted against the household's profile limit,
// and kept a profile's image on disk until an administrator ran Leftover records → Scan.
//
//   - the services are registered: the start-up sweep and the user-deleted consumer;
//   - deleting a profile's account takes its row and image, and nothing of anyone else's;
//   - deleting a main account takes its profiles' rows, never their Jellyfin accounts;
//   - every kind of record Bonfire keeps a user id in is cleared, one kind at a time,
//     so a collection added to the stored set and not to the removal fails by name;
//   - the start-up sweep cleans a user deleted while Bonfire wasn't running.
//
// Usage: dotnet run -c Release   (loads bin/Release/<tfm>/Jellyfin.Profiles.dll)
// ─────────────────────────────────────────────────────────────────────────────

int pass = 0;
var fails = new List<string>();
void Ok(string name, bool cond, string detail = null)
{
    if (cond) { pass++; Console.WriteLine("  PASS  " + name); }
    else
    {
        fails.Add(name + (detail is null ? "" : "  — " + detail));
        Console.WriteLine("  FAIL  " + name + (detail is null ? "" : "  — " + detail));
    }
}

const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
var asm = typeof(Plugin).Assembly;
var recordsType = asm.GetType("Jellyfin.Profiles.UserRecords");
var startupType = asm.GetType("Jellyfin.Profiles.UserRecordsStartup");
var consumerType = asm.GetType("Jellyfin.Profiles.UserRecordsDeleted");

var tempDir = Path.Combine(Path.GetTempPath(), "bonfire-usercleanup-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
var plugin = new Plugin(new StubPaths(tempDir), new StubXml());
var config = plugin.Configuration;
var imageFolder = Path.Combine(tempDir, "plugins", "ProfilesManagement");
Directory.CreateDirectory(imageFolder);

var live = new HashSet<Guid>();
User UserOf(Guid id)
{
    var u = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
    typeof(User).GetProperty("Id").SetValue(u, id);
    typeof(User).GetProperty("Username").SetValue(u, "u" + id.ToString("N").Substring(0, 6));
    return u;
}
Guid NewUser() { var id = Guid.NewGuid(); live.Add(id); return id; }
string Image(Guid id) => Path.Combine(imageFolder, id + ".png");
void Row(Guid profile, Guid main, string name)
{
    config.Mappings.Add(new ProfileMapping { ProfileUserId = profile, MasterUserId = main, ProfileName = name });
    File.WriteAllBytes(Image(profile), new byte[] { 1 });
}
bool HasRow(Guid profile) => config.Mappings.Any(m => m.ProfileUserId == profile);

// The consumer Jellyfin calls, as Jellyfin's UserManager does: the user is gone first.
Task Delete(Guid id)
{
    live.Remove(id);
    var consumer = (IEventConsumer<UserDeletedEventArgs>)Activator.CreateInstance(consumerType);
    return consumer.OnEvent(new UserDeletedEventArgs(UserOf(id)));
}

// ── Registered ──────────────────────────────────────────────────────────────
Console.WriteLine("── Registered with Jellyfin ────────────────────────────────────");
Ok("UserRecords, the start-up service and the consumer exist", recordsType != null && startupType != null && consumerType != null);
if (recordsType == null || startupType == null || consumerType == null)
{
    Console.WriteLine(pass + " passed, " + fails.Count + " failed");
    return 1;
}
{
    var services = new ServiceCollection();
    new PluginServiceRegistrator().RegisterServices(services, null);
    Ok("the user-deleted consumer is registered",
        services.Any(s => s.ServiceType == typeof(IEventConsumer<UserDeletedEventArgs>) && s.ImplementationType == consumerType));
    Ok("the start-up sweep is a hosted service",
        services.Any(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == startupType));
}

// Start-up registers the cleaner; nothing is stored yet, so the sweep has nothing to do.
var users = Stub.Users(live);
var startup = (IHostedService)Activator.CreateInstance(startupType, users, NullLogger.Instance.ForType(startupType));
await startup.StartAsync(CancellationToken.None);

// ── A profile, then a main account ──────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── Deleted in Dashboard → Users ────────────────────────────────");
var M = NewUser(); var P1 = NewUser(); var P2 = NewUser();
var N = NewUser(); var Q = NewUser();
Row(P1, M, "Sam"); Row(P2, M, "Ada"); Row(Q, N, "Lee");
config.TrustedAddressEntries.Add(new TrustedAddressEntry { Address = "203.0.113.7", UserIds = new List<Guid> { M, N } });

await Delete(P1);
Ok("a deleted profile's row goes", !HasRow(P1));
Ok("and its image", !File.Exists(Image(P1)));
Ok("its household's other profile keeps its row and image", HasRow(P2) && File.Exists(Image(P2)));
Ok("another household is untouched", HasRow(Q) && File.Exists(Image(Q)));

await Delete(M);
Ok("a deleted main account's profiles leave the household", !HasRow(P2));
Ok("their Jellyfin accounts are not deleted", live.Contains(P2));
Ok("another household is still untouched", HasRow(Q) && File.Exists(Image(Q)));
var entry = config.TrustedAddressEntries.Single();
Ok("an address keeps skipping the PIN for the accounts still there", entry.UserIds.SequenceEqual(new[] { N }), string.Join(",", entry.UserIds));

// ── Every kind of record, one at a time ─────────────────────────────────────
// For each kind: a record naming a user, that user deleted, and the user is then nowhere in
// what Bonfire stores. StoredUserIds is the list the sweep and the scan both start from, so
// a kind stored there and missing from the removal fails here by name.
Console.WriteLine();
Console.WriteLine("── Every record that names a user ──────────────────────────────");
var stored = recordsType.GetMethod("StoredUserIds", Any);
var configLock = asm.GetType("Jellyfin.Profiles.Controllers.ProfilesBaseController").GetField("ConfigLock", Any).GetValue(null);
bool Stored(Guid id) { lock (configLock) { return ((HashSet<Guid>)stored.Invoke(null, new object[] { config })).Contains(id); } }
var kinds = new (string Name, Action<Guid> Add)[]
{
    ("a profile row", id => config.Mappings.Add(new ProfileMapping { ProfileUserId = id, MasterUserId = id, ProfileName = "x" })),
    ("a main account's row", id => config.Mappings.Add(new ProfileMapping { ProfileUserId = NewUser(), MasterUserId = id, ProfileName = "x" })),
    ("a Bonfire group it owns", id => config.BonfireGroups.Add(new BonfireGroup { GroupId = Guid.NewGuid().ToString("N"), OwnerUserId = id })),
    ("a Bonfire group it joined", id => config.BonfireGroups.Add(new BonfireGroup { GroupId = Guid.NewGuid().ToString("N"), OwnerUserId = NewUser(), MemberUserIds = new List<Guid> { id } })),
    ("a linked account", id => config.LinkedAccounts.Add(new LinkedAccount { UserId = id, HouseholdId = NewUser() })),
    ("a household it is linked into", id => config.LinkedAccounts.Add(new LinkedAccount { UserId = NewUser(), HouseholdId = id })),
    ("first-time setup seen", id => config.Onboarding.Seen.Add(new OnboardingRecord { UserId = id })),
    ("a confirmed email address", id => config.AccountEmails.Add(new AccountEmail { UserId = id, Address = "a@example.com" })),
    ("a profile-limit override", id => config.UserProfileLimitOverrides.Add(new UserProfileLimitOverride { UserId = id, MaxProfiles = 3 })),
    ("an address that skips its PIN", id => config.TrustedAddressEntries.Add(new TrustedAddressEntry { Address = "198.51.100.1", UserIds = new List<Guid> { id } })),
};
foreach (var (name, add) in kinds)
{
    var id = NewUser();
    add(id);
    Ok(name + ": stored", Stored(id));
    await Delete(id);
    Ok(name + ": gone once the user is deleted", !Stored(id));
}

// ── The start-up sweep ──────────────────────────────────────────────────────
// Deleted while Bonfire wasn't running: no event, so only the sweep can find it.
Console.WriteLine();
Console.WriteLine("── Deleted while Bonfire was not running ───────────────────────");
{
    var gone = NewUser(); var kept = NewUser();
    Row(gone, gone, "Old"); Row(kept, kept, "New");
    live.Remove(gone);
    var again = (IHostedService)Activator.CreateInstance(startupType, users, NullLogger.Instance.ForType(startupType));
    await again.StartAsync(CancellationToken.None);
    // Not awaited by StartAsync, on purpose: give it a few seconds.
    for (var i = 0; i < 50 && HasRow(gone); i++) await Task.Delay(100);
    Ok("the sweep at start removes a user Jellyfin no longer has", !HasRow(gone) && !File.Exists(Image(gone)));
    Ok("and leaves one it still has", HasRow(kept) && File.Exists(Image(kept)));
}

try { Directory.Delete(tempDir, true); } catch { }
Console.WriteLine();
if (fails.Count > 0)
{
    foreach (var f in fails) Console.WriteLine("   - " + f);
    Console.WriteLine(pass + " passed, " + fails.Count + " failed");
    return 1;
}
Console.WriteLine(pass + " passed, 0 failed");
return 0;

static class LoggerExt
{
    public static object ForType(this NullLogger _, Type t)
        => typeof(NullLogger<>).MakeGenericType(t).GetField("Instance").GetValue(null);
}

public class Stub : DispatchProxy
{
    public HashSet<Guid> Live;

    public static IUserManager Users(HashSet<Guid> live)
    {
        var p = Create<IUserManager, Stub>();
        ((Stub)(object)p).Live = live;
        return p;
    }

    protected override object Invoke(MethodInfo m, object[] args)
    {
        if (m.Name == "GetUserById" && args[0] is Guid id)
        {
            if (!Live.Contains(id)) return null;
            var u = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
            typeof(User).GetProperty("Id").SetValue(u, id);
            return u;
        }
        var t = m.ReturnType;
        if (t == typeof(void)) return null;
        if (t == typeof(Task)) return Task.CompletedTask;
        return t.IsValueType ? Activator.CreateInstance(t) : null;
    }
}

sealed class StubPaths : MediaBrowser.Common.Configuration.IApplicationPaths
{
    private readonly string _root;
    public StubPaths(string root) { _root = root; }
    public string ProgramDataPath => _root;
    public string WebPath => _root;
    public string ProgramSystemPath => _root;
    public string DataPath => _root;
    public string ImageCachePath => _root;
    public string PluginsPath => _root;
    public string PluginConfigurationsPath => _root;
    public string LogDirectoryPath => _root;
    public string ConfigurationDirectoryPath => _root;
    public string SystemConfigurationFilePath => Path.Combine(_root, "system.xml");
    public string CachePath { get => _root; set { } }
    public string TempDirectory => _root;
    public string VirtualDataPath => _root;
    public string TrickplayPath => _root;
    public string BackupPath => _root;
    public void MakeSanityCheckOrThrow() { }
    public void CreateAndCheckMarker(string path, string markerName, bool recursive = false) { }
}

sealed class StubXml : MediaBrowser.Model.Serialization.IXmlSerializer
{
    public void SerializeToStream(object obj, Stream stream) { }
    public void SerializeToFile(object obj, string file) { }
    public object DeserializeFromFile(Type type, string file)
        => throw new FileNotFoundException("stub serializer", file);
    public object DeserializeFromStream(Type type, Stream stream)
        => throw new NotSupportedException();
    public object DeserializeFromBytes(Type type, byte[] buffer)
        => throw new NotSupportedException();
}
