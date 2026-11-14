using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

// ─────────────────────────────────────────────────────────────────────────────
// A library another Campground plugin manages is neither granted nor blocked by Bonfire
// (Campground-Coordination #31, item 2).
//
// FieldGuide writes who may open its "For You:" libraries itself. If Bonfire put those in
// BlockedMediaFolders, or granted them to "every library", or copied them into a new
// profile, the two plugins would fight over one policy and a child could end up with a
// library Bonfire promised to keep from them. So, for a library whose owner manages
// access (Ownership.LibraryOwnerOf says AccessManagedByOwner):
//
//   - ProfilePolicySync keeps what the account already has, in both lists, and adds nothing;
//   - it is never in "every library";
//   - it is not on GET form-options libraries, and not copied on create, update or make-profile.
//
// Every case below asks one thing on its own, with the real ApplyAsync and the real
// controller against a modelled Jellyfin. A library owned with AccessManagedByOwner false
// (GameTent's Shelf), and a server with nothing registered, are managed exactly as before.
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

const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
var ctrlType = asm.GetType("Jellyfin.Profiles.Controllers.ProfilesController", true);
var baseType = asm.GetType("Jellyfin.Profiles.Controllers.ProfilesBaseController", true);
var pluginType = asm.GetType("Jellyfin.Profiles.Plugin", true);
var cfgType = asm.GetType("Jellyfin.Profiles.Configuration.PluginConfiguration", true);
var mapType = asm.GetType("Jellyfin.Profiles.Configuration.ProfileMapping", true);
var syncType = asm.GetType("Jellyfin.Profiles.Auth.ProfilePolicySync", true);
var hasher = asm.GetType("Jellyfin.Profiles.Auth.PinHasher", true);
var ownType = asm.GetType("Campground.Shared.Registry.Ownership", true);
var regType = asm.GetType("Campground.Shared.Registry.OwnershipRegistration", true);
var apply = syncType.GetMethod("ApplyAsync", Any);

var tempDir = Path.Combine(Path.GetTempPath(), "bonfire-libraryownership-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
// The reserved names count only while FieldGuide is installed (Campground.Shared 1.0.1, #61),
// and Ownership reads that from the plugins folder Plugin's constructor records. StubPaths
// gives tempDir as that folder, so FieldGuide is installed here, as Jellyfin writes it: one
// folder per version with a meta.json. The last section takes it away.
var fieldGuideDir = Path.Combine(tempDir, "FieldGuide_1.0.0.0");
Directory.CreateDirectory(fieldGuideDir);
File.WriteAllText(Path.Combine(fieldGuideDir, "meta.json"),
    "{\"guid\":\"f7188173-3d03-4c95-8ba2-0dd8999a61ab\",\"name\":\"FieldGuide\",\"status\":\"Active\"}");
var plugin = Activator.CreateInstance(pluginType, new StubPaths(tempDir), new StubXml());
var config = pluginType.GetProperty("Configuration").GetValue(plugin);
// A new install starts with Bonfire off (2.0) and every gate stands down; this harness needs it on.
config.GetType().GetProperty("BonfireOn").SetValue(config, true);
var mappings = (IList)cfgType.GetProperty("Mappings").GetValue(config);

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
{
    var j = JsonDefaults.PascalCaseOptions;
    json.ReadCommentHandling = j.ReadCommentHandling;
    json.DefaultIgnoreCondition = j.DefaultIgnoreCondition;
    json.NumberHandling = j.NumberHandling;
    json.Converters.Clear();
    foreach (var c in j.Converters) json.Converters.Add(c);
    json.PropertyNamingPolicy = j.PropertyNamingPolicy;
}

// L1, L2: ordinary libraries.
// FY: "For You: Sam" - reserved name, owned before any plugin has registered.
// FM: an ordinary name, but its own folder is "fieldguide-xyz" (FieldGuide F41).
// RG: registered by folder id, owner manages access.   GT: registered, the admin chooses (GameTent's Shelf).
var L1 = Guid.NewGuid(); var L2 = Guid.NewGuid();
var FY = Guid.NewGuid(); var FM = Guid.NewGuid(); var RG = Guid.NewGuid(); var GT = Guid.NewGuid();
var world = new World();
world.Add(L1, "Movies", null); world.Add(L2, "Shows", null);
world.Add(FY, "For You: Sam", null);
world.Add(FM, "Quiet shelf", "/data/root/default/fieldguide-ab12");
world.Add(RG, "Plain name", null); world.Add(GT, "Shelf", null);

var users = World.Create<IUserManager>(world);
var libs = World.Create<ILibraryManager>(world);

string Name(Guid id) => id == L1 ? "L1" : id == L2 ? "L2" : id == FY ? "FY" : id == FM ? "FM" : id == RG ? "RG" : id == GT ? "GT" : "?";
string Show(IEnumerable<Guid> ids) => string.Join(",", (ids ?? Array.Empty<Guid>()).Select(Name).OrderBy(s => s));
bool In(IEnumerable<Guid> ids, Guid id) => (ids ?? Array.Empty<Guid>()).Contains(id);
string Lists(UserPolicy p) => $"all={p.EnableAllFolders} enabled=[{Show(p.EnabledFolders)}] blocked=[{Show(p.BlockedMediaFolders)}]";

void Register(bool managedByOwner, params Guid[] folders)
    => ownType.GetMethod("Register", Any).Invoke(null, new[] { Activator.CreateInstance(regType,
        "other-plugin", new string[0], new string[0], folders.Select(f => f.ToString("N")).ToArray(), managedByOwner) });
void Unregister() => ownType.GetMethod("Withdraw", Any).Invoke(null, new object[] { "other-plugin" });

static UserPolicy Policy(Action<UserPolicy> set = null)
{
    var u = new UserPolicy { AuthenticationProviderId = "x", PasswordResetProviderId = "x", EnableAllFolders = false,
                             EnabledFolders = Array.Empty<Guid>(), BlockedTags = Array.Empty<string>(),
                             AllowedTags = Array.Empty<string>() };
    set?.Invoke(u);
    return u;
}

// A main account and one profile under it. Returns the profile's id.
Guid Household(UserPolicy master, UserPolicy account, List<Guid> folders, bool all)
{
    var m = Guid.NewGuid(); var p = Guid.NewGuid();
    world.AddUser(m, "m" + m.ToString("N").Substring(0, 6), master);
    world.AddUser(p, "p" + p.ToString("N").Substring(0, 6), account);
    void Row(Guid profile, Guid mainId, List<Guid> f, bool a)
    {
        var row = Activator.CreateInstance(mapType);
        mapType.GetProperty("ProfileUserId").SetValue(row, profile);
        mapType.GetProperty("MasterUserId").SetValue(row, mainId);
        mapType.GetProperty("ProfileName").SetValue(row, "p");
        mapType.GetProperty("EnabledFolders").SetValue(row, f);
        mapType.GetProperty("AllLibraries").SetValue(row, a);
        mappings.Add(row);
    }
    Row(m, m, null, false);
    Row(p, m, folders, all);
    return p;
}

UserPolicy Sync(Guid profile)
{
    var task = (Task<bool>)apply.Invoke(null, new object[] { users, libs, NullLogger.Instance, profile });
    task.GetAwaiter().GetResult();
    return world.Policies[profile];
}

Ok("ApplyAsync exists", apply != null);
if (apply == null) return 1;

// ── ProfilePolicySync: the library is kept as the account has it ────────────
Console.WriteLine();
Console.WriteLine("── ProfilePolicySync: a library its owner manages ──────────────");
{
    var master = Policy(u => u.EnableAllFolders = true);

    // Nothing registered: FY is owned by its name alone, FM by its folder alone.
    var p = Sync(Household(master, Policy(u => { u.EnabledFolders = new[] { L1, FY }; u.BlockedMediaFolders = new[] { L2 }; }),
        new List<Guid> { L1 }, false));
    Ok("an owned library the account has stays granted", In(p.EnabledFolders, FY), Lists(p));
    Ok("and is not blocked", !In(p.BlockedMediaFolders, FY), Lists(p));
    Ok("an ordinary library is still managed (L1 granted, L2 blocked)", In(p.EnabledFolders, L1) && In(p.BlockedMediaFolders, L2), Lists(p));

    p = Sync(Household(master, Policy(u => { u.EnabledFolders = new[] { L1 }; u.BlockedMediaFolders = new[] { L2 }; }),
        new List<Guid> { L1 }, false));
    Ok("an owned library the account lacks stays not granted", !In(p.EnabledFolders, FY) && !In(p.EnabledFolders, FM), Lists(p));
    Ok("and is not blocked either", !In(p.BlockedMediaFolders, FY) && !In(p.BlockedMediaFolders, FM), Lists(p));

    p = Sync(Household(master, Policy(u => { u.EnabledFolders = new[] { L1 }; u.BlockedMediaFolders = new[] { FY, FM }; }),
        new List<Guid> { L1 }, false));
    Ok("an owned library the account has blocked stays blocked", In(p.BlockedMediaFolders, FY) && In(p.BlockedMediaFolders, FM), Lists(p));
    Ok("and is not granted", !In(p.EnabledFolders, FY) && !In(p.EnabledFolders, FM), Lists(p));

    p = Sync(Household(master, Policy(u => { u.EnabledFolders = Array.Empty<Guid>(); }), null, true));
    Ok("\"every library\" does not include an owned one", !In(p.EnabledFolders, FY) && !In(p.EnabledFolders, FM), Lists(p));
    Ok("\"every library\" is not blocked on it either", !In(p.BlockedMediaFolders, FY) && !In(p.BlockedMediaFolders, FM), Lists(p));
    Ok("\"every library\" still has the ordinary ones", In(p.EnabledFolders, L1) && In(p.EnabledFolders, L2), Lists(p));

    // Jellyfin's default account: every library by the flag. Bonfire writes a list, so what the
    // account could open (an owned library, unless blocked) is written out, no more.
    p = Sync(Household(master, Policy(u => { u.EnableAllFolders = true; }), new List<Guid> { L1 }, false));
    Ok("an account on \"all\" can still open the owned library (granted in the list it is given)", In(p.EnabledFolders, FY) && !In(p.BlockedMediaFolders, FY), Lists(p));
    p = Sync(Household(master, Policy(u => { u.EnableAllFolders = true; u.BlockedMediaFolders = new[] { FY }; }), new List<Guid> { L1 }, false));
    Ok("an account on \"all\" with the owned library blocked keeps it blocked", In(p.BlockedMediaFolders, FY) && In(p.EnabledFolders, FY) == true, Lists(p));

    // The main account blocking it must not leak into the profile as Bonfire's own block.
    p = Sync(Household(Policy(u => { u.EnableAllFolders = true; u.BlockedMediaFolders = new[] { FY }; }),
        Policy(u => { u.EnabledFolders = new[] { L1 }; }), new List<Guid> { L1 }, false));
    Ok("the main account's block on an owned library is not copied", !In(p.BlockedMediaFolders, FY), Lists(p));

    // An old stored list that names it (from before this rule) grants nothing.
    p = Sync(Household(master, Policy(u => { u.EnabledFolders = new[] { L1 }; }), new List<Guid> { L1, FY }, false));
    Ok("a stored list that still names an owned library does not grant it", !In(p.EnabledFolders, FY), Lists(p));

    var accessible = (IEnumerable<Guid>)baseType.GetMethods(Any)
        .First(m => m.Name == "MasterAccessibleFolders" && m.GetParameters().Length == 2)
        .Invoke(null, new object[] { libs, master });
    Ok("the main account's reachable set leaves it out", !In(accessible, FY) && !In(accessible, FM) && In(accessible, L1), Show(accessible));
}

// ── Registered owners ───────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── Registered owners ───────────────────────────────────────────");
{
    var master = Policy(u => u.EnableAllFolders = true);

    Register(true, RG);
    var p = Sync(Household(master, Policy(u => { u.EnabledFolders = Array.Empty<Guid>(); }), null, true));
    Ok("a registered library managed by its owner is not in \"every library\"", !In(p.EnabledFolders, RG) && !In(p.BlockedMediaFolders, RG), Lists(p));
    p = Sync(Household(master, Policy(u => { u.EnabledFolders = new[] { L1, RG }; }), new List<Guid> { L1 }, false));
    Ok("and one the account has stays granted, unblocked", In(p.EnabledFolders, RG) && !In(p.BlockedMediaFolders, RG), Lists(p));

    // GameTent's Shelf: owned, but the admin chooses, so Bonfire manages it as it does any library.
    Unregister();
    Register(false, GT);
    p = Sync(Household(master, Policy(u => { u.EnabledFolders = Array.Empty<Guid>(); }), null, true));
    Ok("a library owned with AccessManagedByOwner false is in \"every library\"", In(p.EnabledFolders, GT), Lists(p));
    p = Sync(Household(master, Policy(u => { u.EnabledFolders = new[] { L1 }; }), new List<Guid> { L1 }, false));
    Ok("and is blocked when the profile has not got it", In(p.BlockedMediaFolders, GT) && !In(p.EnabledFolders, GT), Lists(p));
    Unregister();
}

// ── Standalone ──────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── Standalone: nothing registered, no reserved name ─────────────");
{
    var saved = world.Libraries.ToList();
    world.Libraries.Clear(); world.Libraries.AddRange(new[] { L1, L2, GT, RG });
    var master = Policy(u => u.EnableAllFolders = true);
    var p = Sync(Household(master, Policy(u => { u.EnabledFolders = Array.Empty<Guid>(); }), null, true));
    Ok("\"every library\" is every library", Show(p.EnabledFolders) == "GT,L1,L2,RG" && (p.BlockedMediaFolders ?? Array.Empty<Guid>()).Length == 0, Lists(p));
    p = Sync(Household(master, Policy(u => { u.EnabledFolders = new[] { L1 }; }), new List<Guid> { L1 }, false));
    Ok("a list blocks the rest", Show(p.EnabledFolders) == "L1" && Show(p.BlockedMediaFolders) == "GT,L2,RG", Lists(p));
    world.Libraries.Clear(); world.Libraries.AddRange(saved);
}

// ── The controller ──────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── The library screens and create ──────────────────────────────");
{
    var MAIN = Guid.NewGuid();
    const string PIN = "1234";
    world.AddUser(MAIN, "home", Policy(u => u.EnableAllFolders = true));
    var mainRow = Activator.CreateInstance(mapType);
    mapType.GetProperty("ProfileUserId").SetValue(mainRow, MAIN);
    mapType.GetProperty("MasterUserId").SetValue(mainRow, MAIN);
    mapType.GetProperty("ProfileName").SetValue(mainRow, "home");
    mapType.GetProperty("PinHash").SetValue(mainRow, hasher.GetMethod("Hash").Invoke(null, new object[] { PIN }));
    mappings.Add(mainRow);

    object Controller()
    {
        var ctor = ctrlType.GetConstructors().First();
        var args = ctor.GetParameters().Select(p =>
            p.ParameterType == typeof(IUserManager) ? users
            : p.ParameterType == typeof(ILibraryManager) ? libs
            : p.ParameterType.Name.StartsWith("ILogger") ? Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(ctrlType))
            : (object)null).ToArray();
        var c = (ControllerBase)ctor.Invoke(args);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", MAIN.ToString()) }, "test")),
        };
        http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7");
        http.Request.Headers["X-Bonfire-Master-Pin"] = PIN;
        c.ControllerContext = new ControllerContext { HttpContext = http };
        return c;
    }

    (int status, object value) Call(string method, string body, object route = null)
    {
        var r = ctrlType.GetMethod(method);
        if (r == null) return (-404, "no route " + method);
        var c = Controller();
        var invokeArgs = r.GetParameters().Select(p =>
            p.ParameterType == typeof(Guid) ? route ?? Guid.Empty
            : JsonSerializer.Deserialize(body, p.ParameterType, json)).ToArray();
        object result;
        try { result = r.Invoke(c, invokeArgs); }
        catch (TargetInvocationException ex) { return (-500, ex.InnerException?.GetType().Name + ": " + ex.InnerException?.Message); }
        if (result is Task task)
        {
            try { task.GetAwaiter().GetResult(); }
            catch (Exception ex) { return (-500, ex.GetType().Name + ": " + ex.Message); }
            result = task.GetType().GetProperty("Result")?.GetValue(task);
        }
        var inner = result?.GetType().GetProperty("Result")?.GetValue(result) ?? result;
        return inner switch
        {
            ObjectResult o => (o.StatusCode ?? 200, o.Value),
            StatusCodeResult s => (s.StatusCode, null),
            _ => (-1, inner?.GetType().Name),
        };
    }

    // GET form-options libraries: the owned ones are not offered, whoever the caller is.
    Register(false, GT);
    var formOptions = Call("GetFormOptions", "{}");
    var listed = (status: formOptions.status, value: formOptions.value?.GetType().GetProperty("Libraries")?.GetValue(formOptions.value));
    var ids = listed.value is IEnumerable e
        ? e.Cast<object>().Select(o => (string)o.GetType().GetProperty("Id").GetValue(o)).ToList()
        : new List<string>();
    string Id(Guid g) => g.ToString("N");
    Ok("GET form-options libraries answers", listed.status == 200 && ids.Count > 0, listed.status + " " + listed.value);
    Ok("GET form-options libraries lists an ordinary library", ids.Contains(Id(L1)) && ids.Contains(Id(L2)));
    Ok("GET form-options libraries does not list a \"For You:\" library", !ids.Contains(Id(FY)), string.Join(",", ids.Select(i => Name(Guid.Parse(i)))));
    Ok("GET form-options libraries does not list one in a fieldguide- folder", !ids.Contains(Id(FM)));
    Ok("GET form-options libraries lists a library its owner leaves to the admin (GameTent's Shelf)", ids.Contains(Id(GT)));
    Unregister();

    // R-1136: a create that fails after the account exists takes the account away again, or
    // every retry says "already exists" about an account no profile owns.
    world.FailPolicyWrite = true;
    var failed = Call("CreateProfile", $"{{\"profileName\":\"Rollback\",\"masterPin\":\"{PIN}\"}}");
    world.FailPolicyWrite = false;
    Ok("setup: a create that fails part way reports it", failed.status != 200, failed.status + " " + failed.value);
    Ok("and does not leave its account behind", world.Deleted.Contains(world.LastCreated) && !world.Users.ContainsKey(world.LastCreated), "deleted " + world.Deleted.Count);
    var retry = Call("CreateProfile", $"{{\"profileName\":\"Rollback\",\"masterPin\":\"{PIN}\"}}");
    Ok("so the same name can be tried again", retry.status == 200, retry.status + " " + retry.value);

    // CreateProfile: nothing owned is copied from the main account or from the request.
    UserPolicy Created() => world.Policies[world.LastCreated];
    var r = Call("CreateProfile", $"{{\"profileName\":\"Allbooks\",\"masterPin\":\"{PIN}\"}}");
    Ok("setup: a profile is created with every library", r.status == 200, r.status + " " + r.value);
    if (r.status == 200)
    {
        Ok("create with every library does not copy an owned one", !In(Created().EnabledFolders, FY) && !In(Created().EnabledFolders, FM), Lists(Created()));
        Ok("and does not block one", !In(Created().BlockedMediaFolders, FY) && !In(Created().BlockedMediaFolders, FM), Lists(Created()));
        Ok("and leaves the account's \"all\" off, so it can open none", !Created().EnableAllFolders, Lists(Created()));
    }
    r = Call("CreateProfile", $"{{\"profileName\":\"Listed\",\"masterPin\":\"{PIN}\",\"enabledFolders\":[\"{L1}\",\"{FY}\"]}}");
    Ok("setup: a profile is created with a list that names an owned library", r.status == 200, r.status + " " + r.value);
    if (r.status == 200)
    {
        Ok("create with a list does not grant the owned library", In(Created().EnabledFolders, L1) && !In(Created().EnabledFolders, FY), Lists(Created()));
        Ok("and does not block it", !In(Created().BlockedMediaFolders, FY), Lists(Created()));
    }

    // UpdateProfile: the list on the form is the managed libraries; what the account has of an owned one stays.
    var kid = Household(Policy(u => u.EnableAllFolders = true), Policy(u => { u.EnabledFolders = new[] { L1, FY }; }), new List<Guid> { L1 }, false);
    var kidMaster = world.MasterOf(kid);
    // The controller acts as MAIN, so move the profile under it.
    foreach (var row in mappings.Cast<object>().Where(m => (Guid)mapType.GetProperty("ProfileUserId").GetValue(m) == kid))
        mapType.GetProperty("MasterUserId").SetValue(row, MAIN);
    r = Call("UpdateProfile", $"{{\"profileId\":\"{kid}\",\"profileName\":\"Kid\",\"masterPin\":\"{PIN}\",\"enabledFolders\":[\"{L1}\"]}}");
    Ok("setup: an edit that sets the libraries", r.status == 200, r.status + " " + r.value);
    Ok("an edit keeps an owned library the account has", In(world.Policies[kid].EnabledFolders, FY) && !In(world.Policies[kid].BlockedMediaFolders, FY), Lists(world.Policies[kid]));
    Ok("and blocks the ordinary one it left out", In(world.Policies[kid].BlockedMediaFolders, L2), Lists(world.Policies[kid]));
    r = Call("UpdateProfile", $"{{\"profileId\":\"{kid}\",\"profileName\":\"Kid\",\"masterPin\":\"{PIN}\",\"enabledFolders\":[\"{L1}\",\"{FY}\"]}}");
    Ok("an edit that names an owned library does not grant it", In(world.Policies[kid].EnabledFolders, FY) == true && !In(world.Policies[kid].BlockedMediaFolders, FY), Lists(world.Policies[kid]));
    var lean = Household(Policy(u => u.EnableAllFolders = true), Policy(u => { u.EnabledFolders = new[] { L1 }; }), new List<Guid> { L1 }, false);
    foreach (var row in mappings.Cast<object>().Where(m => (Guid)mapType.GetProperty("ProfileUserId").GetValue(m) == lean))
        mapType.GetProperty("MasterUserId").SetValue(row, MAIN);
    r = Call("UpdateProfile", $"{{\"profileId\":\"{lean}\",\"profileName\":\"Lean\",\"masterPin\":\"{PIN}\",\"enabledFolders\":[\"{L1}\",\"{FY}\"]}}");
    Ok("an edit that names one the account lacks does not grant it", !In(world.Policies[lean].EnabledFolders, FY) && !In(world.Policies[lean].BlockedMediaFolders, FY), Lists(world.Policies[lean]));
    r = Call("UpdateProfile", $"{{\"profileId\":\"{lean}\",\"profileName\":\"Lean\",\"masterPin\":\"{PIN}\",\"allLibraries\":true}}");
    Ok("an edit to \"every library\" does not grant it either", !In(world.Policies[lean].EnabledFolders, FY) && !In(world.Policies[lean].BlockedMediaFolders, FY)
        && !world.Policies[lean].EnableAllFolders, Lists(world.Policies[lean]));

    // Make-profile copies an account's libraries into the profile's own list.
    var fromMethod = ctrlType.GetMethod("ProfileFoldersFrom", Any);
    Ok("the make-profile copy exists", fromMethod != null);
    if (fromMethod != null)
    {
        var household = Policy(u => u.EnableAllFolders = true);
        object[] a = { Policy(u => u.EnabledFolders = new[] { L1, FY, FM }), household, false };
        var copied = (List<Guid>)fromMethod.Invoke(Controller(), a);
        Ok("an account's list is copied without an owned library", Show(copied) == "L1", Show(copied));
        a = new object[] { Policy(u => { u.EnableAllFolders = true; u.BlockedMediaFolders = new[] { L2 }; }), household, false };
        copied = (List<Guid>)fromMethod.Invoke(Controller(), a);
        Ok("an account on \"all\" with one blocked is copied without an owned library", !In(copied, FY) && !In(copied, FM) && In(copied, L1), Show(copied));
        a = new object[] { Policy(u => u.EnableAllFolders = true), household, false };
        copied = (List<Guid>)fromMethod.Invoke(Controller(), a);
        Ok("an account on \"all\" is every library, and \"every library\" has no owned one", (bool)a[2] && !In(copied, FY) && !In(copied, FM), Show(copied));
    }
}

// ── FieldGuide uninstalled ──────────────────────────────────────────────────
// Before 1.0.1 a "For You:" name or a fieldguide- folder kept Bonfire's hands off a library
// for good, so a household could name a library "For You: Sam" and Bonfire never managed it:
// every profile kept whatever it had. Without FieldGuide they are ordinary libraries.
Console.WriteLine();
Console.WriteLine("── FieldGuide not installed: the reserved names are ordinary ───");
{
    Directory.Delete(fieldGuideDir, true);
    // The installed answer is cached for a short while; 1.0.0 has no cache and no method.
    ownType.GetMethod("ResetInstalledCache", Any)?.Invoke(null, null);
    var master = Policy(u => u.EnableAllFolders = true);
    // The account has only L1. (One it was granted in Dashboard → Users is kept, issue #30, so
    // giving it FY here would prove nothing about ownership.)
    var p = Sync(Household(master, Policy(u => { u.EnabledFolders = new[] { L1 }; }), new List<Guid> { L1 }, false));
    Ok("a \"For You:\" library the profile wasn't given is blocked", In(p.BlockedMediaFolders, FY) && !In(p.EnabledFolders, FY), Lists(p));
    Ok("and so is one in a fieldguide- folder", In(p.BlockedMediaFolders, FM) && !In(p.EnabledFolders, FM), Lists(p));
    p = Sync(Household(master, Policy(u => { u.EnabledFolders = Array.Empty<Guid>(); }), null, true));
    Ok("\"every library\" includes both", In(p.EnabledFolders, FY) && In(p.EnabledFolders, FM), Lists(p));
    // A registered owner is not a reserved name: it still counts.
    Register(true, RG);
    p = Sync(Household(master, Policy(u => { u.EnabledFolders = Array.Empty<Guid>(); }), null, true));
    Ok("a library a running plugin registered is still left to it", !In(p.EnabledFolders, RG) && !In(p.BlockedMediaFolders, RG), Lists(p));
    Unregister();
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
    public Dictionary<Guid, UserPolicy> Policies = new();
    public Dictionary<Guid, Guid> Masters = new();
    public List<Guid> Libraries = new();
    public Dictionary<Guid, string> Names = new();
    public Dictionary<Guid, string> FolderPaths = new();
    public Guid LastCreated;
    public bool FailPolicyWrite;
    public List<Guid> Deleted = new();

    public Guid MasterOf(Guid id) => Masters.TryGetValue(id, out var m) ? m : Guid.Empty;

    /// <summary>A library: its name, and its own folder when that matters.</summary>
    public void Add(Guid id, string name, string folderPath)
    {
        Libraries.Add(id);
        Names[id] = name;
        if (folderPath != null) FolderPaths[id] = folderPath;
    }

    public void AddUser(Guid id, string name, UserPolicy policy)
    {
        var u = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty("Username").SetValue(u, name);
        typeof(User).GetProperty("Id").SetValue(u, id);
        typeof(User).GetProperty("AuthenticationProviderId").SetValue(u, "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider");
        Users[id] = u;
        Policies[id] = policy;
    }

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
            case "get_Users":
                return W.Users.Values.ToList();
            case "GetUserDto":
                var user = (User)args[0];
                return new UserDto
                {
                    Id = user.Id,
                    Name = user.Username,
                    Policy = World.Copy(W.Policies[user.Id]),
                    Configuration = new UserConfiguration(),
                };
            case "CreateUserAsync":
                // Jellyfin's new account: every library by the flag, nothing blocked.
                var id = Guid.NewGuid();
                W.AddUser(id, (string)args[0], new UserPolicy { AuthenticationProviderId = "x", PasswordResetProviderId = "x", EnableAllFolders = true });
                W.LastCreated = id;
                return Task.FromResult(W.Users[id]);
            case "DeleteUserAsync":
                W.Deleted.Add((Guid)args[0]);
                W.Users.Remove((Guid)args[0]);
                return Task.CompletedTask;
            case "UpdatePolicyAsync":
                if (W.FailPolicyWrite) throw new InvalidOperationException("disk full");
                W.Policies[(Guid)args[0]] = World.Copy((UserPolicy)args[1]);
                return Task.CompletedTask;
            case "GetVirtualFolders":
                return W.Libraries.Select(l => new VirtualFolderInfo
                {
                    ItemId = l.ToString("N"),
                    Name = W.Names[l],
                    Locations = new[] { "/media/" + W.Names[l] },
                }).ToList();
            case "GetItemById" when !m.IsGenericMethod && args.Length > 0 && args[0] is Guid itemId:
                if (!W.FolderPaths.TryGetValue(itemId, out var path)) return null;
                var folder = (CollectionFolder)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(CollectionFolder));
                typeof(BaseItem).GetProperty("Path").SetValue(folder, path);
                return folder;
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
