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
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

// ─────────────────────────────────────────────────────────────────────────────
// Copy one profile's rules onto another: POST /copy-rules.
//
// A household can copy a profile's rating limit and tags, libraries and viewing limits onto
// another profile in the same household. It does not copy allowed devices, name, picture
// or PIN. The route is a security boundary: it changes what a profile can watch, so it is
// held to what /update is held to, and each case below is its own check.
//
//  - what is copied, and what is left alone;
//  - the target ends exactly as if it had been edited to the same values (a twin profile is
//    edited with /update, and the two accounts are compared field by field);
//  - another household's profile, the main account, a standalone user and a profile that does
//    not exist are all refused, and all with the same answer;
//  - a library the main account cannot reach is never granted, and a library another plugin
//    manages is neither copied nor changed;
//  - the main PIN, and a required main PIN, are enforced as everywhere else.
//
// Against 5b126c9 there is no route, so every case fails.
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

var ctrlType = asm.GetType("Jellyfin.Profiles.Controllers.ProfilesController", true);
var pluginType = asm.GetType("Jellyfin.Profiles.Plugin", true);
var cfgType = asm.GetType("Jellyfin.Profiles.Configuration.PluginConfiguration", true);
var mapType = asm.GetType("Jellyfin.Profiles.Configuration.ProfileMapping", true);
var rulesType = asm.GetType("Jellyfin.Profiles.Configuration.ViewingRules", true);
var windowType = asm.GetType("Jellyfin.Profiles.Configuration.ViewingWindow", true);
var hasher = asm.GetType("Jellyfin.Profiles.Auth.PinHasher", true);
var ownType = asm.GetType("Campground.Shared.Registry.Ownership", true);
var regType = asm.GetType("Campground.Shared.Registry.OwnershipRegistration", true);
const BindingFlags Any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

var tempDir = Path.Combine(Path.GetTempPath(), "bonfire-copyrules-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
var plugin = Activator.CreateInstance(pluginType, new StubPaths(tempDir), new StubXml());
var config = pluginType.GetProperty("Configuration").GetValue(plugin);
// A new install starts with Bonfire off (2.0), and off every gate stands down, so a
// refusal here would prove nothing. This harness is about a server where it is on.
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

var MAIN = Guid.NewGuid();      // the household under test
var SRC = Guid.NewGuid();       // the profile whose rules are copied
var DST = Guid.NewGuid();       // the profile that receives them
var TWIN = Guid.NewGuid();      // a second target, edited by hand to the same values
var OTHER = Guid.NewGuid();     // another household's main account
var FOREIGN = Guid.NewGuid();   // a profile in that household
var STAND = Guid.NewGuid();     // a standalone user, no row at all
var L1 = Guid.NewGuid();
var L2 = Guid.NewGuid();
var L3 = Guid.NewGuid();        // a library the main account cannot reach
var LM = Guid.NewGuid();        // a library another plugin manages
const string PIN = "1234";

var world = new World();
world.Libraries.AddRange(new[] { L1, L2, L3, LM });
world.Add(MAIN, "home", new UserPolicy { EnableAllFolders = false, EnabledFolders = new[] { L1, L2, LM }, MaxParentalRating = null });
world.Add(SRC, "home_Src", new UserPolicy());
world.Add(DST, "home_Dst", new UserPolicy());
world.Add(TWIN, "home_Twin", new UserPolicy());
world.Add(OTHER, "other", new UserPolicy { EnableAllFolders = true });
world.Add(FOREIGN, "other_Kid", new UserPolicy());
world.Add(STAND, "stand", new UserPolicy());

object Map(Guid profile, Guid master, string name)
{
    var m = Activator.CreateInstance(mapType);
    mapType.GetProperty("ProfileUserId").SetValue(m, profile);
    mapType.GetProperty("MasterUserId").SetValue(m, master);
    mapType.GetProperty("ProfileName").SetValue(m, name);
    mappings.Add(m);
    return m;
}
void Set(object row, string prop, object value) => mapType.GetProperty(prop).SetValue(row, value);
object Row(object row, string prop) => mapType.GetProperty(prop)?.GetValue(row);

var mainRow = Map(MAIN, MAIN, "home");
Set(mainRow, "PinHash", hasher.GetMethod("Hash").Invoke(null, new object[] { PIN }));
var srcRow = Map(SRC, MAIN, "Src");
var dstRow = Map(DST, MAIN, "Dst");
var twinRow = Map(TWIN, MAIN, "Twin");
var otherRow = Map(OTHER, OTHER, "other");
var foreignRow = Map(FOREIGN, OTHER, "Kid");

var users = World.Create<IUserManager>(world);
var library = World.Create<ILibraryManager>(world);

void Register(params Guid[] folders)
    => ownType.GetMethod("Register", Any).Invoke(null, new[] { Activator.CreateInstance(regType,
        "other-plugin", new string[0], new string[0], folders.Select(f => f.ToString("N")).ToArray(), true) });
void Unregister() => ownType.GetMethod("Withdraw", Any).Invoke(null, new object[] { "other-plugin" });

Guid caller = MAIN;
string pinHeader = PIN;
(int status, string message) Call(string method, string body)
{
    var r = ctrlType.GetMethod(method);
    if (r == null) return (-404, "no route " + method);
    var ctor = ctrlType.GetConstructors().First();
    var args = ctor.GetParameters().Select(p =>
        p.ParameterType == typeof(IUserManager) ? users
        : p.ParameterType == typeof(ILibraryManager) ? library
        : p.ParameterType.Name.StartsWith("ILogger") ? Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(ctrlType))
        : (object)null).ToArray();
    var c = (ControllerBase)ctor.Invoke(args);
    var http = new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", caller.ToString()) }, "test")),
    };
    http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7");
    if (pinHeader != null) http.Request.Headers["X-Bonfire-Master-Pin"] = pinHeader;
    c.ControllerContext = new ControllerContext { HttpContext = http };

    var invokeArgs = r.GetParameters().Select(p => JsonSerializer.Deserialize(body, p.ParameterType, json)).ToArray();

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
        ObjectResult o => (o.StatusCode ?? 200, o.Value as string),
        StatusCodeResult s => (s.StatusCode, null),
        _ => (-1, inner?.GetType().Name),
    };
}

(int status, string message) Copy(Guid from, Guid to, string pin = null)
    => Call("CopyRules", $"{{\"sourceProfileId\":\"{from}\",\"targetProfileId\":\"{to}\"" + (pin != null ? $",\"masterPin\":\"{pin}\"" : "") + "}");

string Name(Guid id) => id == L1 ? "L1" : id == L2 ? "L2" : id == L3 ? "L3" : id == LM ? "LM" : "?";
string Show(IEnumerable<Guid> ids) => string.Join(",", (ids ?? Array.Empty<Guid>()).Select(Name).OrderBy(s => s));
string Acct(Guid id)
{
    var p = world.Policies[id];
    return $"rating={p.MaxParentalRating?.ToString() ?? "none"}/{p.MaxParentalSubRating?.ToString() ?? "-"} all={p.EnableAllFolders} " +
        $"enabled=[{Show(p.EnabledFolders)}] blocked=[{Show(p.BlockedMediaFolders)}] " +
        $"tags-block=[{string.Join(",", (p.BlockedTags ?? Array.Empty<string>()).OrderBy(s => s))}] tags-allow=[{string.Join(",", (p.AllowedTags ?? Array.Empty<string>()).OrderBy(s => s))}]";
}
IEnumerable<Guid> Stored(object row) => ((IEnumerable<Guid>)Row(row, "EnabledFolders") ?? new List<Guid>());
string Sig(object row)
{
    var v = Row(row, "Viewing");
    if (v == null) return "none";
    var w = ((IEnumerable)rulesType.GetProperty("Windows").GetValue(v)).Cast<object>()
        .Select(x => windowType.GetProperty("Days").GetValue(x) + "@" + windowType.GetProperty("Start").GetValue(x) + "-" + windowType.GetProperty("End").GetValue(x));
    return $"hours={rulesType.GetProperty("HoursEnabled").GetValue(v)} daily={rulesType.GetProperty("DailyMinutes").GetValue(v)} " +
        $"weekend={rulesType.GetProperty("WeekendMinutes").GetValue(v)} windows=[{string.Join(";", w)}]";
}

// The source: a rating of 10, one library the main account can reach and one it cannot, a
// tag each way, hours and a daily limit. The target starts with different values of its own,
// and a device list, a picture, a colour and a PIN that must survive.
var r = Call("UpdateProfile", $"{{\"profileId\":\"{SRC}\",\"profileName\":\"Src\",\"masterPin\":\"{PIN}\",\"maxParentalRating\":\"10\",\"enabledFolders\":[\"{L1}\",\"{L3}\"],\"blockedTags\":[\"gore\"],\"allowedTags\":[]}}");
Console.WriteLine("setup: source saved through /update -> " + r.status + " " + r.message);
Set(srcRow, "AllowedTags", new List<string>());
{
    var rules = Activator.CreateInstance(rulesType);
    rulesType.GetProperty("HoursEnabled").SetValue(rules, true);
    rulesType.GetProperty("DailyMinutes").SetValue(rules, 90);
    rulesType.GetProperty("WeekendMinutes").SetValue(rules, 150);
    var win = Activator.CreateInstance(windowType);
    windowType.GetProperty("Days").SetValue(win, 62);
    windowType.GetProperty("Start").SetValue(win, "07:00");
    windowType.GetProperty("End").SetValue(win, "20:30");
    var list = (IList)rulesType.GetProperty("Windows").GetValue(rules);
    list.Add(win);
    Set(srcRow, "Viewing", rules);
}

// The target before the copy.
world.Policies[DST] = new UserPolicy { MaxParentalRating = 3, EnableAllFolders = false, EnabledFolders = new[] { L2 }, BlockedTags = new[] { "own" } };
Set(dstRow, "ProfileName", "Dst");
Set(dstRow, "AllowedDeviceIds", new List<string> { "device-a", "device-b" });
Set(dstRow, "ProfileImage", "/img/dst.png");
Set(dstRow, "AvatarColor", "#112233");
Set(dstRow, "PinHash", hasher.GetMethod("Hash").Invoke(null, new object[] { "5678" }));
var dstPinBefore = (string)Row(dstRow, "PinHash");

// ── What is copied ───────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── What is copied ───────────────────────────────────────────────");

r = Copy(SRC, DST);
Ok("a copy within the household succeeds", r.status == 200, r.status + " " + r.message);
Ok("the rating is copied", world.Policies[DST].MaxParentalRating == 10, Acct(DST));
Ok("and the tags: blocked tags arrive", world.Policies[DST].BlockedTags.Contains("gore"), Acct(DST));
Ok("and the target's own blocked tag is replaced, not added to", !world.Policies[DST].BlockedTags.Contains("own"), Acct(DST));
Ok("and the stored tag list is the source's", (((IEnumerable<string>)Row(dstRow, "BlockedTags")) ?? new string[0]).SequenceEqual(new[] { "gore" }));
Ok("the libraries are copied", Show(world.Policies[DST].EnabledFolders) == "L1", Acct(DST));
Ok("and the stored list says so too", Show(Stored(dstRow)) == "L1" && Row(dstRow, "AllLibraries") is false, Show(Stored(dstRow)));
Ok("the viewing limits are copied: hours, windows, daily and weekend time", Sig(dstRow) == Sig(srcRow), Sig(dstRow) + " vs " + Sig(srcRow));
Ok("and the target's rules are a copy, not the source's object", !ReferenceEquals(Row(dstRow, "Viewing"), Row(srcRow, "Viewing")));

// ── What is not ──────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── What is not copied ───────────────────────────────────────────");

Ok("allowed devices are untouched", ((IEnumerable<string>)Row(dstRow, "AllowedDeviceIds")).SequenceEqual(new[] { "device-a", "device-b" }));
Ok("the name is untouched", (string)Row(dstRow, "ProfileName") == "Dst" && world.Users[DST].Username == "home_Dst");
Ok("the picture and colour are untouched", (string)Row(dstRow, "ProfileImage") == "/img/dst.png" && (string)Row(dstRow, "AvatarColor") == "#112233");
Ok("the PIN is untouched", (string)Row(dstRow, "PinHash") == dstPinBefore && !string.IsNullOrEmpty(dstPinBefore));
Ok("the source is untouched", Show(Stored(srcRow)) == "L1,L3" || Show(Stored(srcRow)) == "L1", Show(Stored(srcRow)));

// ── The same as an edit ──────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── The same as an edit ──────────────────────────────────────────");

world.Policies[TWIN] = new UserPolicy { MaxParentalRating = 3, EnableAllFolders = false, EnabledFolders = new[] { L2 }, BlockedTags = new[] { "own" } };
r = Call("UpdateProfile", $"{{\"profileId\":\"{TWIN}\",\"profileName\":\"Twin\",\"masterPin\":\"{PIN}\",\"maxParentalRating\":\"10\",\"enabledFolders\":[\"{L1}\",\"{L3}\"],\"blockedTags\":[\"gore\"],\"allowedTags\":[]}}");
Ok("setup: the twin is edited to the same values", r.status == 200, r.status + " " + r.message);
Ok("the target's account matches the edited twin's, field for field", Acct(DST) == Acct(TWIN), Acct(DST) + "  vs  " + Acct(TWIN));
Ok("and its own rating is recorded the same way", Row(dstRow, "OwnRating") != null && Row(twinRow, "OwnRating") != null
    && Row(dstRow, "OwnRating").GetType().GetProperty("Score").GetValue(Row(dstRow, "OwnRating")) is 10);
Ok("and so is the record of what was written to its libraries", Show((IEnumerable<Guid>)Row(dstRow, "LastAppliedFolders")) == Show((IEnumerable<Guid>)Row(twinRow, "LastAppliedFolders")));

// ── Boundaries ───────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── Whose profiles ───────────────────────────────────────────────");

var before = Acct(DST) + Sig(dstRow);
var beforeForeign = Acct(FOREIGN) + Sig(foreignRow);
var refused = new List<(int, string)>();
r = Copy(SRC, FOREIGN); refused.Add(r);
Ok("another household's profile as target is refused, and changed nothing", r.status >= 400 && Acct(FOREIGN) + Sig(foreignRow) == beforeForeign, r.status + " " + r.message);
r = Copy(FOREIGN, DST); refused.Add(r);
Ok("another household's profile as source is refused, and changed nothing", r.status >= 400 && Acct(DST) + Sig(dstRow) == before, r.status + " " + r.message);
r = Copy(MAIN, DST); refused.Add(r);
Ok("the main account as source is refused", r.status >= 400 && Acct(DST) + Sig(dstRow) == before, r.status + " " + r.message);
var mainBefore = Acct(MAIN);
r = Copy(SRC, MAIN); refused.Add(r);
Ok("the main account as target is refused", r.status >= 400 && Acct(MAIN) == mainBefore, r.status + " " + r.message);
var standBefore = Acct(STAND);
r = Copy(SRC, STAND); refused.Add(r);
Ok("a standalone user as target is refused, and changed nothing", r.status >= 400 && Acct(STAND) == standBefore, r.status + " " + r.message);
r = Copy(STAND, DST); refused.Add(r);
Ok("a standalone user as source is refused", r.status >= 400 && Acct(DST) + Sig(dstRow) == before, r.status + " " + r.message);
r = Copy(SRC, Guid.NewGuid()); refused.Add(r);
Ok("a profile that does not exist is refused", r.status >= 400, r.status + " " + r.message);
r = Copy(SRC, SRC);
Ok("a profile copied onto itself is refused", r.status >= 400, r.status + " " + r.message);
Ok("every refusal about whose profile it is says the same thing", refused.Select(x => x.Item1 + x.Item2).Distinct().Count() == 1,
    string.Join(" | ", refused.Select(x => x.Item1 + " " + x.Item2).Distinct()));

caller = SRC;
r = Copy(DST, TWIN);
Ok("a profile signed in on its own cannot copy rules", r.status == 401 || r.status == 403, r.status + " " + r.message);
caller = OTHER;
r = Copy(SRC, DST);
Ok("another household's main account cannot copy this household's profiles", r.status >= 400 && Acct(DST) + Sig(dstRow) == before, r.status + " " + r.message);
caller = FOREIGN;
r = Copy(SRC, DST);
Ok("nor can another household's profile", r.status >= 400 && Acct(DST) + Sig(dstRow) == before, r.status + " " + r.message);
caller = MAIN;

// ── The main PIN ─────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── The main PIN ─────────────────────────────────────────────────");

world.Policies[TWIN] = new UserPolicy { MaxParentalRating = 3 };
var twinBefore = Acct(TWIN);
pinHeader = null;
r = Copy(SRC, TWIN);
Ok("with a main PIN set, none sent is refused", r.status == 400 && Acct(TWIN) == twinBefore, r.status + " " + r.message);
r = Copy(SRC, TWIN, "0000");
Ok("a wrong PIN is refused", r.status >= 400 && Acct(TWIN) == twinBefore, r.status + " " + r.message);
r = Copy(SRC, TWIN, PIN);
Ok("the PIN in the body is accepted", r.status == 200 && Acct(TWIN) != twinBefore, r.status + " " + r.message);
pinHeader = PIN;

{
    var pinRow = Row(mainRow, "PinHash");
    Set(mainRow, "PinHash", "");
    cfgType.GetProperty("RequireMainPin").SetValue(config, true);
    world.Policies[TWIN] = new UserPolicy { MaxParentalRating = 3 };
    twinBefore = Acct(TWIN);
    r = Copy(SRC, TWIN);
    Ok("while main PINs are required and the account has none, it is refused", r.status == 400 && Acct(TWIN) == twinBefore, r.status + " " + r.message);
    cfgType.GetProperty("RequireMainPin").SetValue(config, false);

    // Limits with no PIN to switch them off again are refused, as /limits/{id} refuses them.
    Set(twinRow, "Viewing", null);
    r = Copy(SRC, TWIN);
    Ok("viewing limits are not given to a profile when the main account has no PIN", r.status == 400 && Sig(twinRow) == "none", r.status + " " + r.message + " " + Sig(twinRow));
    Set(mainRow, "PinHash", pinRow);
}

// ── Libraries ────────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── Libraries ────────────────────────────────────────────────────");

Ok("a library the main account cannot reach is not granted", !world.Policies[DST].EnabledFolders.Contains(L3) && !Stored(dstRow).Contains(L3), Acct(DST));
{
    // The source's stored list is made to name L3 (the main account later lost it), and the
    // target must still not get it.
    Set(srcRow, "EnabledFolders", new List<Guid> { L1, L3 });
    Set(srcRow, "AllLibraries", false);
    world.Policies[DST] = new UserPolicy { EnableAllFolders = false, EnabledFolders = new[] { L2 } };
    r = Copy(SRC, DST);
    Ok("even when the source's own list names it", r.status == 200 && !world.Policies[DST].EnabledFolders.Contains(L3) && !Stored(dstRow).Contains(L3), Acct(DST));
}

{
    // A library another plugin manages: not copied from the source, and what the target has of it stays.
    Register(LM);
    Set(srcRow, "EnabledFolders", new List<Guid> { L1, LM });
    Set(srcRow, "AllLibraries", false);
    world.Policies[SRC] = new UserPolicy { EnableAllFolders = false, EnabledFolders = new[] { L1, LM } };

    world.Policies[DST] = new UserPolicy { EnableAllFolders = false, EnabledFolders = new[] { L2 } };
    r = Copy(SRC, DST);
    Ok("a managed library is not copied to a target that lacks it", r.status == 200 && !world.Policies[DST].EnabledFolders.Contains(LM) && !Stored(dstRow).Contains(LM), Acct(DST));
    Ok("and is not blocked for it either", !(world.Policies[DST].BlockedMediaFolders ?? Array.Empty<Guid>()).Contains(LM), Acct(DST));

    world.Policies[DST] = new UserPolicy { EnableAllFolders = false, EnabledFolders = new[] { L2, LM } };
    r = Copy(SRC, DST);
    Ok("a managed library a target already has is left as it was", r.status == 200 && world.Policies[DST].EnabledFolders.Contains(LM) && !world.Policies[DST].EnabledFolders.Contains(L2), Acct(DST));

    // "Every library" is the main account's libraries Bonfire manages, never a managed one.
    Set(srcRow, "AllLibraries", true);
    world.Policies[DST] = new UserPolicy { EnableAllFolders = false, EnabledFolders = new[] { L2 } };
    r = Copy(SRC, DST);
    Ok("\"every library\" copied does not include a managed library", r.status == 200 && !world.Policies[DST].EnabledFolders.Contains(LM) && !world.Policies[DST].EnableAllFolders, Acct(DST));
    Ok("and the target is on every library too", (bool)Row(dstRow, "AllLibraries"));
    Unregister();
}

// ── Never more than the main account ────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── Never more than the main account ─────────────────────────────");
{
    // A source set looser than the main account now allows (the main account tightened after).
    world.Policies[MAIN].MaxParentalRating = 8;
    world.Policies[SRC] = new UserPolicy { EnableAllFolders = false, EnabledFolders = new[] { L1 }, MaxParentalRating = 12 };
    Set(srcRow, "AllLibraries", false);
    Set(srcRow, "EnabledFolders", new List<Guid> { L1 });
    world.Policies[DST] = new UserPolicy { MaxParentalRating = 3 };
    r = Copy(SRC, DST);
    Ok("a rating above the main account's is brought down to it", r.status == 200 && world.Policies[DST].MaxParentalRating == 8, Acct(DST));
    world.Policies[MAIN].MaxParentalRating = null;
}

// ── A source with nothing set ───────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── A source with no rules ───────────────────────────────────────");
{
    Set(srcRow, "Viewing", null);
    Set(srcRow, "BlockedTags", new List<string>());
    Set(srcRow, "AllLibraries", true);
    world.Policies[SRC] = new UserPolicy { EnableAllFolders = true, MaxParentalRating = null };
    var rules = Activator.CreateInstance(rulesType);
    rulesType.GetProperty("DailyMinutes").SetValue(rules, 30);
    Set(dstRow, "Viewing", rules);
    world.Policies[DST] = new UserPolicy { MaxParentalRating = 3, BlockedTags = new[] { "own" } };
    r = Copy(SRC, DST);
    Ok("copying a profile with no limits removes the target's", r.status == 200 && Row(dstRow, "Viewing") == null, r.status + " " + r.message + " " + Sig(dstRow));
    Ok("and its rating limit and tags", world.Policies[DST].MaxParentalRating == null && !world.Policies[DST].BlockedTags.Contains("own"), Acct(DST));
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
    public List<Guid> Libraries = new();

    public void Add(Guid id, string name, UserPolicy policy)
    {
        var u = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty("Username").SetValue(u, name);
        typeof(User).GetProperty("Id").SetValue(u, id);
        typeof(User).GetProperty("AuthenticationProviderId").SetValue(u, "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider");
        Users[id] = u;
        Policies[id] = policy;
    }

    // A copy each time, as Jellyfin builds a fresh DTO: a change only sticks if the route
    // writes it back through UpdatePolicyAsync.
    public static UserPolicy Copy(UserPolicy p) => new UserPolicy
    {
        AuthenticationProviderId = "x",
        PasswordResetProviderId = "x",
        IsAdministrator = p.IsAdministrator,
        MaxParentalRating = p.MaxParentalRating,
        MaxParentalSubRating = p.MaxParentalSubRating,
        EnableAllFolders = p.EnableAllFolders,
        EnabledFolders = p.EnabledFolders?.ToArray() ?? Array.Empty<Guid>(),
        BlockedMediaFolders = p.BlockedMediaFolders?.ToArray(),
        BlockedTags = p.BlockedTags?.ToArray() ?? Array.Empty<string>(),
        AllowedTags = p.AllowedTags?.ToArray() ?? Array.Empty<string>(),
    };

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
            case "UpdatePolicyAsync":
                W.Policies[(Guid)args[0]] = World.Copy((UserPolicy)args[1]);
                return Task.CompletedTask;
            case "GetVirtualFolders":
                return W.Libraries.Select(id => new MediaBrowser.Model.Entities.VirtualFolderInfo { ItemId = id.ToString("N"), Name = "lib" }).ToList();
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
