using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging.Abstractions;

// ─────────────────────────────────────────────────────────────────────────────
// ProfilePolicySync.ApplyAsync, run for real (R-1286).
//
// This is the clamp behind "a profile never gets more than its main account". It runs
// from the web switch and from every sign-in on every client, and until this harness no
// test executed it: tests/cs/switchpolicy reads source text and replays the arithmetic on
// local values. A clamp nobody runs can be deleted with every check still green.
//
// Here the real ApplyAsync is called against a modelled Jellyfin — a user manager that
// stores each account's policy and records every write, and a library manager with three
// libraries — and the policy it writes is read back. What it is held to:
//
//   - a profile can't gain a library, tag, rating or schedule its main account lacks;
//   - what the main account lost, the profile loses;
//   - where the profile was narrower, it stays narrower;
//   - a main account's own row is never touched, and an account already right is not
//     rewritten (this runs twice on every sign-in);
//   - a library an administrator adds or removes in Dashboard -> Users stays that way,
//     and a reset is still repaired (R-1042: removals used to come back, because the
//     stored list won). Red against 6e74bac: 12 fail.
//
// Shown able to go red by deleting the line in ApplyCoreAsync that intersects the
// profile's libraries with the main account's (`authorityFolders = authorityFolders
// .Where(id => masterAccessible.Contains(id))`): the library assertions fail.
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

static string RepoRoot()
{
    var d = AppContext.BaseDirectory;
    while (d != null && !File.Exists(Path.Combine(d, "Jellyfin.Profiles.csproj")))
        d = Path.GetDirectoryName(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    return d ?? throw new Exception("could not find the repository root");
}

var tfm = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Name;
var asm = Assembly.LoadFrom(Path.Combine(RepoRoot(), "bin", "Release", tfm, "Jellyfin.Profiles.dll"));

var pluginType = asm.GetType("Jellyfin.Profiles.Plugin", true);
var cfgType = asm.GetType("Jellyfin.Profiles.Configuration.PluginConfiguration", true);
var mapType = asm.GetType("Jellyfin.Profiles.Configuration.ProfileMapping", true);
var syncType = asm.GetType("Jellyfin.Profiles.Auth.ProfilePolicySync", true);
var apply = syncType.GetMethod("ApplyAsync", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
var noTags = (string)asm.GetType("Jellyfin.Profiles.Controllers.ProfilesBaseController", true)
    .GetField("NoAllowedTagMatches", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);

var tempDir = Path.Combine(Path.GetTempPath(), "bonfire-policysync-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
var plugin = Activator.CreateInstance(pluginType, new StubPaths(tempDir), new StubXml());
var config = pluginType.GetProperty("Configuration").GetValue(plugin);
var mappings = (IList)cfgType.GetProperty("Mappings").GetValue(config);

var L1 = Guid.NewGuid(); var L2 = Guid.NewGuid(); var L3 = Guid.NewGuid();
var world = new World { Libraries = { L1, L2, L3 } };
var users = World.Create<IUserManager>(world);
var libraries = World.Create<ILibraryManager>(world);

object Map(Guid profile, Guid master, List<Guid> folders = null, bool all = false,
           List<string> blocked = null, List<string> allowed = null)
{
    var m = Activator.CreateInstance(mapType);
    mapType.GetProperty("ProfileUserId").SetValue(m, profile);
    mapType.GetProperty("MasterUserId").SetValue(m, master);
    mapType.GetProperty("ProfileName").SetValue(m, "p");
    mapType.GetProperty("EnabledFolders").SetValue(m, folders);
    mapType.GetProperty("AllLibraries").SetValue(m, all);
    mapType.GetProperty("BlockedTags").SetValue(m, blocked);
    mapType.GetProperty("AllowedTags").SetValue(m, allowed);
    mappings.Add(m);
    return m;
}

bool Apply(Guid profile)
{
    var task = (Task<bool>)apply.Invoke(null, new object[] { users, libraries, NullLogger.Instance, profile });
    return task.GetAwaiter().GetResult();
}

// A main account and one profile under it, each with a policy; returns the profile's id.
Guid Household(UserPolicy master, UserPolicy account, List<Guid> folders = null, bool all = false,
               List<string> blocked = null, List<string> allowed = null)
{
    var m = Guid.NewGuid(); var p = Guid.NewGuid();
    world.Add(m, master); world.Add(p, account);
    Map(m, m);
    Map(p, m, folders, all, blocked, allowed);
    world.Masters[p] = m;
    return p;
}

static UserPolicy Policy(Action<UserPolicy> set = null)
{
    var u = new UserPolicy { AuthenticationProviderId = "x", PasswordResetProviderId = "x", EnableAllFolders = false,
                             EnabledFolders = Array.Empty<Guid>(), BlockedTags = Array.Empty<string>(),
                             AllowedTags = Array.Empty<string>() };
    set?.Invoke(u);
    return u;
}
string Show(IEnumerable<Guid> ids) => string.Join(",", (ids ?? Array.Empty<Guid>())
    .Select(id => id == L1 ? "L1" : id == L2 ? "L2" : id == L3 ? "L3" : "?").OrderBy(s => s));
static string Tags(IEnumerable<string> t) => string.Join(",", (t ?? Array.Empty<string>()).OrderBy(s => s));

Ok("ApplyAsync exists", apply != null);
if (apply == null) return 1;

// ── Libraries ────────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── Libraries ───────────────────────────────────────────────────");
{
    // Stored list and account both name L3; the main account doesn't have it.
    var p = Household(Policy(u => u.EnabledFolders = new[] { L1, L2 }),
                      Policy(u => u.EnabledFolders = new[] { L1, L2, L3 }),
                      folders: new() { L1, L2, L3 });
    Ok("ApplyAsync runs for a sub-profile", Apply(p));
    var w = world.Written(p);
    Ok("a profile can't keep a library its main account lacks (" + Show(w?.EnabledFolders) + ")",
       w != null && !w.EnableAllFolders && !w.EnabledFolders.Contains(L3) && w.EnabledFolders.Contains(L1) && w.EnabledFolders.Contains(L2));
    Ok("and that library is blocked outright", w != null && (w.BlockedMediaFolders ?? Array.Empty<Guid>()).Contains(L3));

    // "Every library" means every library the main account has, not every library.
    p = Household(Policy(u => u.EnabledFolders = new[] { L1 }),
                  Policy(u => u.EnableAllFolders = true), all: true);
    Apply(p);
    w = world.Written(p);
    Ok("a profile on \"every library\" gets only the main account's (" + Show(w?.EnabledFolders) + ")",
       w != null && !w.EnableAllFolders && Show(w.EnabledFolders) == "L1");

    // The main account loses one; the profile, set to both, loses it too.
    var master = Policy(u => u.EnabledFolders = new[] { L1, L2 });
    p = Household(master, Policy(u => u.EnabledFolders = new[] { L1, L2 }), folders: new() { L1, L2 });
    Apply(p);
    world.Policies[world.Masters[p]] = Policy(u => u.EnabledFolders = new[] { L1 });
    Apply(p);
    w = world.Written(p);
    Ok("a library the main account lost is taken from the profile (" + Show(w?.EnabledFolders) + ")",
       w != null && Show(w.EnabledFolders) == "L1");

    // The main account sees everything; the profile is set to one library.
    p = Household(Policy(u => u.EnableAllFolders = true),
                  Policy(u => u.EnabledFolders = new[] { L2 }), folders: new() { L2 });
    Apply(p);
    w = world.Written(p);
    Ok("a profile narrower than its main account stays narrower (" + Show(w?.EnabledFolders) + ")",
       w != null && !w.EnableAllFolders && Show(w.EnabledFolders) == "L2");
    Ok("with the rest blocked", w != null && Show(w.BlockedMediaFolders) == "L1,L3");
}

// ── Libraries changed in Dashboard -> Users (R-1042) ────────────────────────
// A library an administrator took off a profile's account came back at the next sign-in:
// the stored list won, because a removal and a reset looked the same. Bonfire now records
// what it last wrote, so a change on the account is adopted both ways and a reset is
// still repaired.
Console.WriteLine();
Console.WriteLine("── Libraries changed in Dashboard -> Users (R-1042) ────────────");
{
    var lastProp = mapType.GetProperty("LastAppliedFolders");
    object RowOf(Guid id) => mappings.Cast<object>().First(m => (Guid)mapType.GetProperty("ProfileUserId").GetValue(m) == id);
    List<Guid> Stored(Guid id) => (List<Guid>)mapType.GetProperty("EnabledFolders").GetValue(RowOf(id));
    void AdminSets(Guid id, params Guid[] folders)
    {
        var pol = World.Copy(world.Policies[id]);
        pol.EnableAllFolders = false;
        pol.EnabledFolders = folders;
        world.Policies[id] = pol;
    }
    UserPolicy Now(Guid id) => world.Policies[id];

    Ok("the mapping records the libraries Bonfire last applied", lastProp != null);

    var master = Policy(u => u.EnableAllFolders = true);
    var p = Household(master, Policy(u => u.EnabledFolders = new[] { L1, L2 }), folders: new() { L1, L2 });
    Apply(p);
    Ok("the first sign-in records what it applied (" + Show(lastProp?.GetValue(RowOf(p)) as List<Guid>) + ")",
       lastProp != null && Show(lastProp.GetValue(RowOf(p)) as List<Guid>) == "L1,L2");

    AdminSets(p, L1);
    Apply(p);
    Ok("a library an administrator removed stays removed (" + Show(Now(p).EnabledFolders) + ")", Show(Now(p).EnabledFolders) == "L1");
    Ok("and the profile's own list agrees, so its editor shows it (" + Show(Stored(p)) + ")", Show(Stored(p)) == "L1");
    Apply(p);
    Ok("and it holds at the sign-in after (" + Show(Now(p).EnabledFolders) + ")", Show(Now(p).EnabledFolders) == "L1");

    AdminSets(p, L1, L3);
    Apply(p);
    Ok("a library an administrator granted stays granted (" + Show(Now(p).EnabledFolders) + ")", Show(Now(p).EnabledFolders) == "L1,L3");
    Ok("and joins the profile's own list (" + Show(Stored(p)) + ")", Show(Stored(p)) == "L1,L3");

    // A reset: Jellyfin's default policy, every library, no list.
    var reset = World.Copy(Now(p)); reset.EnableAllFolders = true; reset.EnabledFolders = Array.Empty<Guid>();
    world.Policies[p] = reset;
    Apply(p);
    Ok("a reset account is still repaired to the profile's list (" + Show(Now(p).EnabledFolders) + ")",
       !Now(p).EnableAllFolders && Show(Now(p).EnabledFolders) == "L1,L3");
    Ok("and the reset is not taken as a change (" + Show(Stored(p)) + ")", Show(Stored(p)) == "L1,L3");

    // Taking a library away from the main account still takes it from the profile, and
    // that is Bonfire's own write, not an administrator's change to the profile.
    var lim = Household(Policy(u => u.EnabledFolders = new[] { L1, L2 }),
                        Policy(u => u.EnabledFolders = new[] { L1, L2 }), folders: new() { L1, L2 });
    Apply(lim);
    world.Policies[world.Masters[lim]] = Policy(u => u.EnabledFolders = new[] { L1 });
    Apply(lim);
    world.Policies[world.Masters[lim]] = Policy(u => u.EnabledFolders = new[] { L1, L2 });
    Apply(lim);
    Ok("a library the main account lost and got back returns to the profile (" + Show(Now(lim).EnabledFolders) + ")",
       Show(Now(lim).EnabledFolders) == "L1,L2");

    // A configuration from before the record: the old rule once, then recorded.
    var old = Household(Policy(u => u.EnableAllFolders = true),
                        Policy(u => u.EnabledFolders = new[] { L1 }), folders: new() { L1, L2 });
    lastProp?.SetValue(RowOf(old), null);
    Apply(old);
    Ok("an old configuration with no record keeps today's behaviour once (" + Show(Now(old).EnabledFolders) + ")",
       Show(Now(old).EnabledFolders) == "L1,L2");
    Ok("and is recorded", lastProp != null && Show(lastProp.GetValue(RowOf(old)) as List<Guid>) == "L1,L2");
    AdminSets(old, L2);
    Apply(old);
    Ok("after which an administrator's removal holds there too (" + Show(Now(old).EnabledFolders) + ")",
       Show(Now(old).EnabledFolders) == "L2");

    // Old configurations must still load, and the record must survive a save.
    var xml = new System.Xml.Serialization.XmlSerializer(cfgType);
    var cfg = Activator.CreateInstance(cfgType);
    var row = Activator.CreateInstance(mapType);
    mapType.GetProperty("ProfileUserId").SetValue(row, Guid.NewGuid());
    lastProp?.SetValue(row, new List<Guid> { L1, L3 });
    ((IList)cfgType.GetProperty("Mappings").GetValue(cfg)).Add(row);
    var sw = new StringWriter();
    xml.Serialize(sw, cfg);
    var back = xml.Deserialize(new StringReader(sw.ToString()));
    var backRow = ((IList)cfgType.GetProperty("Mappings").GetValue(back))[0];
    Ok("the record survives a save and a load (" + Show(lastProp?.GetValue(backRow) as List<Guid>) + ")",
       lastProp != null && Show(lastProp.GetValue(backRow) as List<Guid>) == "L1,L3");
    var oldXml = System.Text.RegularExpressions.Regex.Replace(sw.ToString(), @"<LastAppliedFolders>.*?</LastAppliedFolders>", "",
        System.Text.RegularExpressions.RegexOptions.Singleline);
    object oldBack = null;
    try { oldBack = xml.Deserialize(new StringReader(oldXml)); } catch (Exception ex) { Console.WriteLine("    " + ex.Message); }
    var oldRow = oldBack == null ? null : ((IList)cfgType.GetProperty("Mappings").GetValue(oldBack))[0];
    // XmlSerializer hands back an EMPTY list, not null, for a list element that is absent,
    // so "no record" arrives as empty. ProfilePolicySync treats the two alike.
    Ok("a configuration saved before it existed loads, with no record",
       oldRow != null && !oldXml.Contains("LastAppliedFolders")
       && (lastProp == null || ((lastProp.GetValue(oldRow) as List<Guid>)?.Count ?? 0) == 0),
       "loaded=" + (oldRow != null) + " stripped=" + !oldXml.Contains("LastAppliedFolders")
       + " record=" + (lastProp == null ? "n/a" : Show(lastProp.GetValue(oldRow) as List<Guid>) + (lastProp.GetValue(oldRow) == null ? "(null)" : "")));
}

// ── Tags ─────────────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── Tags ────────────────────────────────────────────────────────");
{
    var p = Household(Policy(u => { u.EnableAllFolders = true; u.BlockedTags = new[] { "horror" }; u.AllowedTags = new[] { "family", "kids" }; }),
                      Policy(u => u.EnableAllFolders = true), all: true,
                      blocked: new() { "gore" }, allowed: new() { "kids", "adult" });
    Apply(p);
    var w = world.Written(p);
    Ok("the main account's blocked tag is blocked on the profile (" + Tags(w?.BlockedTags) + ")",
       w != null && w.BlockedTags.Contains("horror"));
    Ok("and the profile's own stays blocked", w != null && w.BlockedTags.Contains("gore"));
    Ok("a profile can't allow a tag its main account doesn't (" + Tags(w?.AllowedTags) + ")",
       w != null && !w.AllowedTags.Contains("adult"));
    Ok("and keeps its narrower allow-list rather than taking the main account's",
       w != null && Tags(w.AllowedTags) == "kids");

    // The main account stops allowing "kids": the profile is not widened to "family".
    world.Policies[world.Masters[p]] = Policy(u => { u.EnableAllFolders = true; u.BlockedTags = new[] { "horror" }; u.AllowedTags = new[] { "family" }; });
    Apply(p);
    w = world.Written(p);
    Ok("a tag the main account no longer allows is taken away, not swapped for its list (" + Tags(w?.AllowedTags) + ")",
       w != null && !w.AllowedTags.Contains("kids") && !w.AllowedTags.Contains("family")
       && noTags != null && w.AllowedTags.Contains(noTags));

    // Blocked on the account by Bonfire's last write only: removed when the main account
    // unblocks it — but the profile's own block is kept.
    world.Policies[world.Masters[p]] = Policy(u => u.EnableAllFolders = true);
    Apply(p);
    w = world.Written(p);
    Ok("a block the main account dropped leaves the profile, its own stays (" + Tags(w?.BlockedTags) + ")",
       w != null && Tags(w.BlockedTags) == "gore");
}

// ── Rating ───────────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── Rating ──────────────────────────────────────────────────────");
{
    var p = Household(Policy(u => { u.EnableAllFolders = true; u.MaxParentalRating = 13; }),
                      Policy(u => { u.EnableAllFolders = true; u.MaxParentalRating = 17; }), all: true);
    Apply(p);
    Ok("a profile can't have a higher rating than its main account (" + world.Written(p)?.MaxParentalRating + ")",
       world.Written(p)?.MaxParentalRating == 13);

    p = Household(Policy(u => { u.EnableAllFolders = true; u.MaxParentalRating = 13; }),
                  Policy(u => { u.EnableAllFolders = true; u.MaxParentalRating = null; }), all: true);
    Apply(p);
    Ok("nor no limit under one that has one (" + world.Written(p)?.MaxParentalRating + ")",
       world.Written(p)?.MaxParentalRating == 13);

    p = Household(Policy(u => { u.EnableAllFolders = true; u.MaxParentalRating = 17; }),
                  Policy(u => { u.EnableAllFolders = true; u.MaxParentalRating = 15; }), all: true);
    Apply(p);
    world.Policies[world.Masters[p]] = Policy(u => { u.EnableAllFolders = true; u.MaxParentalRating = 13; });
    Apply(p);
    Ok("lowering the main account's rating lowers the profile's (" + world.Written(p)?.MaxParentalRating + ")",
       world.Written(p)?.MaxParentalRating == 13);

    p = Household(Policy(u => { u.EnableAllFolders = true; u.MaxParentalRating = null; }),
                  Policy(u => { u.EnableAllFolders = true; u.MaxParentalRating = 10; }), all: true);
    Apply(p);
    Ok("a profile's lower rating stays lower (" + world.Written(p)?.MaxParentalRating + ")",
       world.Policies[p].MaxParentalRating == 10);

    p = Household(Policy(u => { u.EnableAllFolders = true; u.MaxParentalRating = 13; u.MaxParentalSubRating = 1; }),
                  Policy(u => { u.EnableAllFolders = true; u.MaxParentalRating = 13; u.MaxParentalSubRating = 5; }), all: true);
    Apply(p);
    Ok("and the sub-rating is clamped too (" + world.Written(p)?.MaxParentalSubRating + ")",
       world.Written(p)?.MaxParentalSubRating == 1);
}

// ── Schedules, limits, permissions ───────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── Schedules, limits and permissions ───────────────────────────");
{
    AccessSchedule Sched(Guid user, int start, int end)
        => new AccessSchedule(Jellyfin.Database.Implementations.Enums.DynamicDayOfWeek.Everyday, start, end, user);

    var mid = Guid.NewGuid();
    var p = Household(Policy(u => { u.EnableAllFolders = true; u.AccessSchedules = new[] { Sched(mid, 8, 20) }; }),
                      Policy(u => u.EnableAllFolders = true), all: true);
    Apply(p);
    var w = world.Written(p);
    Ok("a profile with no hours gets its main account's (" + (w?.AccessSchedules?.Length ?? 0) + " schedule)",
       w?.AccessSchedules?.Length == 1 && w.AccessSchedules[0].StartHour == 8 && w.AccessSchedules[0].EndHour == 20);
    Ok("as its own rows", w?.AccessSchedules?.Length == 1 && w.AccessSchedules[0].UserId == p);

    world.Policies[world.Masters[p]] = Policy(u => { u.EnableAllFolders = true; u.AccessSchedules = new[] { Sched(mid, 9, 17) }; });
    Apply(p);
    w = world.Written(p);
    Ok("narrowing the main account's hours narrows the profile's",
       w?.AccessSchedules?.Length == 1 && w.AccessSchedules[0].StartHour == 9 && w.AccessSchedules[0].EndHour == 17);

    p = Household(Policy(u => { u.EnableAllFolders = true; u.RemoteClientBitrateLimit = 8_000_000; u.MaxActiveSessions = 3; }),
                  Policy(u => { u.EnableAllFolders = true; u.RemoteClientBitrateLimit = 0; u.MaxActiveSessions = 1; }), all: true);
    Apply(p);
    w = world.Written(p);
    Ok("an unlimited profile takes its main account's bitrate (" + w?.RemoteClientBitrateLimit + ")",
       w?.RemoteClientBitrateLimit == 8_000_000);
    Ok("and keeps its own tighter session count (" + w?.MaxActiveSessions + ")", w?.MaxActiveSessions == 1);

    p = Household(Policy(u => { u.EnableAllFolders = true; u.EnableContentDeletion = false; u.EnableContentDownloading = false; }),
                  Policy(u => { u.EnableAllFolders = true; u.IsAdministrator = true; u.EnableContentDeletion = true; u.EnableContentDownloading = true; }), all: true);
    Apply(p);
    w = world.Written(p);
    Ok("a profile is never an administrator", w != null && !w.IsAdministrator);
    Ok("and can't delete or download where its main account can't",
       w != null && !w.EnableContentDeletion && !w.EnableContentDownloading);
}

// ── What it must leave alone ─────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── What it leaves alone ────────────────────────────────────────");
{
    var p = Household(Policy(u => u.EnabledFolders = new[] { L1 }),
                      Policy(u => u.EnabledFolders = new[] { L1, L2 }), folders: new() { L1, L2 });
    var m = world.Masters[p];
    world.Writes.Clear();
    Ok("a main account's own row is not a sub-profile", !Apply(m));
    Ok("and its policy is not written", !world.Writes.Contains(m));

    Apply(p);
    world.Writes.Clear();
    Apply(p);
    Ok("an account that is already right is not rewritten (" + world.Writes.Count + " writes)", world.Writes.Count == 0);

    var stranger = Guid.NewGuid();
    world.Add(stranger, Policy(u => u.EnableAllFolders = true));
    Ok("an account with no row is left alone", !Apply(stranger) && !world.Writes.Contains(stranger));
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

// ── A modelled Jellyfin ──────────────────────────────────────────────────────

public class World
{
    public Dictionary<Guid, User> Users = new();
    // Each account's policy as Jellyfin holds it. GetUserDto hands out a copy, the way the
    // real one builds a fresh DTO: a change only lands through UpdatePolicyAsync.
    public Dictionary<Guid, UserPolicy> Policies = new();
    public Dictionary<Guid, Guid> Masters = new();
    public List<Guid> Libraries = new();
    public List<Guid> Writes = new();

    public void Add(Guid id, UserPolicy policy)
    {
        var u = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty("Username").SetValue(u, "u" + id.ToString("N").Substring(0, 6));
        typeof(User).GetProperty("Id").SetValue(u, id);
        Users[id] = u;
        Policies[id] = policy;
    }

    /// <summary>The account's policy after ApplyAsync, or null when nothing was ever written.</summary>
    public UserPolicy Written(Guid id) => Writes.Contains(id) ? Policies[id] : null;

    public static UserPolicy Copy(UserPolicy p)
        => (UserPolicy)typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(p, null);

    public static T Create<T>(World w) where T : class
    {
        var p = DispatchProxy.Create<T, WorldProxy>();
        ((WorldProxy)(object)p).W = w;
        return p;
    }
}

public class WorldProxy : DispatchProxy
{
    public World W;

    protected override object Invoke(MethodInfo m, object[] args)
    {
        switch (m.Name)
        {
            case "GetUserById":
                return W.Users.TryGetValue((Guid)args[0], out var u) ? u : null;
            case "GetUserDto":
                var user = (User)args[0];
                return new UserDto
                {
                    Id = user.Id,
                    Name = user.Username,
                    Policy = World.Copy(W.Policies[user.Id]),
                    Configuration = new UserConfiguration(),
                };
            case "UpdatePolicyAsync":
                var id = (Guid)args[0];
                W.Policies[id] = World.Copy((UserPolicy)args[1]);
                W.Writes.Add(id);
                return Task.CompletedTask;
            case "GetVirtualFolders":
                return W.Libraries.Select(l => new VirtualFolderInfo { ItemId = l.ToString("N"), Name = "lib" }).ToList();
        }

        var t = m.ReturnType;
        if (t == typeof(void)) return null;
        if (t == typeof(Task)) return Task.CompletedTask;
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var inner = t.GetGenericArguments()[0];
            var value = inner.IsValueType ? Activator.CreateInstance(inner) : null;
            return typeof(Task).GetMethod("FromResult").MakeGenericMethod(inner).Invoke(null, new[] { value });
        }
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
