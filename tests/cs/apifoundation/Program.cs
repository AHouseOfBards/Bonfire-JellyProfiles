using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

// ─────────────────────────────────────────────────────────────────────────────
// What other apps and plugins build on (1.7.2).
//
//  - An API key from Dashboard → API Keys can call the admin routes. Jellyfin gives a key
//    the administrator role but no user, and the admin check looked the user up, so every
//    key was refused. A key is recognised by both claims together, never one.
//  - /list and /announcements carry an ETag, and answer 304 when it still matches.
//  - GET /me says who the caller is.
//  - IBonfireProfiles answers household questions for other plugins.
//  - What an administrator should see in Dashboard → Activity is written there, and a
//    lockout is written once, not once per guess.
//
// Against d6816e1 the API key, ETag, /me, lookup and activity assertions fail.
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
var annType = asm.GetType("Jellyfin.Profiles.Configuration.Announcement", true);
var hasher = asm.GetType("Jellyfin.Profiles.Auth.PinHasher", true);

var tempDir = Path.Combine(Path.GetTempPath(), "bonfire-api-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
var plugin = Activator.CreateInstance(pluginType, new StubPaths(tempDir), new StubXml());
var config = pluginType.GetProperty("Configuration").GetValue(plugin);
var mappings = (IList)cfgType.GetProperty("Mappings").GetValue(config);
var announcements = (IList)cfgType.GetProperty("Announcements").GetValue(config);

object Map(Guid profile, Guid master, string name, string pin = null)
{
    var m = Activator.CreateInstance(mapType);
    mapType.GetProperty("ProfileUserId").SetValue(m, profile);
    mapType.GetProperty("MasterUserId").SetValue(m, master);
    mapType.GetProperty("ProfileName").SetValue(m, name);
    if (pin != null) mapType.GetProperty("PinHash").SetValue(m, hasher.GetMethod("Hash").Invoke(null, new object[] { pin }));
    mappings.Add(m);
    return m;
}

var world = new World();
var activity = new List<ActivityLog>();
var services = new ServiceCollection()
    .AddSingleton(Recorder.Create(activity))
    .BuildServiceProvider();

// Who is calling, as Jellyfin's authentication handler would describe them.
ClaimsPrincipal UserPrincipal(Guid id) =>
    new(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", id.ToString("N")), new Claim(ClaimTypes.Role, "User") }, "test"));
ClaimsPrincipal KeyPrincipal(bool isKey, string role) =>
    new(new ClaimsIdentity(new[]
    {
        new Claim("Jellyfin-UserId", Guid.Empty.ToString("N")),
        new Claim(ClaimTypes.Role, role),
        new Claim("Jellyfin-IsApiKey", isKey ? "True" : "False"),
    }, "test"));

(int status, object value, HttpContext http) Call(string method, ClaimsPrincipal who, object[] args = null,
    Dictionary<string, string> headers = null)
{
    var route = ctrlType.GetMethod(method);
    if (route == null) return (-404, "no route " + method, null);

    var ctor = ctrlType.GetConstructors().First();
    var ctorArgs = ctor.GetParameters().Select(p =>
        p.ParameterType.Name.StartsWith("ILogger") ? Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(ctrlType))
        : p.ParameterType.IsInterface ? typeof(World).GetMethod("Create").MakeGenericMethod(p.ParameterType).Invoke(null, new object[] { world })
        : null).ToArray();
    var c = (ControllerBase)ctor.Invoke(ctorArgs);
    var http = new DefaultHttpContext { User = who, RequestServices = services };
    http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.9");
    foreach (var kv in headers ?? new Dictionary<string, string>()) http.Request.Headers[kv.Key] = kv.Value;
    c.ControllerContext = new ControllerContext { HttpContext = http };

    object result;
    try { result = route.Invoke(c, args ?? route.GetParameters().Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : Activator.CreateInstance(p.ParameterType)).ToArray()); }
    catch (TargetInvocationException ex) { return (-500, ex.InnerException?.GetType().Name + ": " + ex.InnerException?.Message, http); }
    if (result is Task task)
    {
        try { task.GetAwaiter().GetResult(); }
        catch (Exception ex) { return (-500, ex.GetType().Name + ": " + ex.Message, http); }
        result = task.GetType().GetProperty("Result")?.GetValue(task);
    }
    var inner = result?.GetType().GetProperty("Result")?.GetValue(result) ?? result;
    if (inner == null && result != null) inner = result.GetType().GetProperty("Value")?.GetValue(result);
    return inner switch
    {
        ObjectResult o => (o.StatusCode ?? 200, o.Value, http),
        StatusCodeResult s => (s.StatusCode, null, http),
        _ => (inner == null ? -1 : 200, inner, http),
    };
}
object Prop(object o, string name) => o?.GetType().GetProperty(name)?.GetValue(o);

// ── API keys on admin routes ─────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── An API key can call the admin routes ────────────────────────");

var adminRoutes = new[] { "GetPanicStatus", "GetAllAnnouncements", "GetMaintenance" };
foreach (var route in adminRoutes)
{
    var r = Call(route, KeyPrincipal(true, "Administrator"));
    Ok(route + " answers an API key", r.status == 200, r.status + " " + r.value);
}
var noRole = Call("GetPanicStatus", KeyPrincipal(true, "User"));
Ok("but not a key without the administrator role", noRole.status == 401, noRole.status.ToString());
var noKey = Call("GetPanicStatus", KeyPrincipal(false, "Administrator"));
Ok("nor the role without the key claim, which has no user behind it", noKey.status == 401, noKey.status.ToString());
var plainUser = Guid.NewGuid();
world.Add(plainUser, "Sam");
var asUser = Call("GetPanicStatus", UserPrincipal(plainUser));
Ok("and an ordinary account is still refused", asUser.status == 401, asUser.status.ToString());

// ── ETags ────────────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── /announcements and /list answer 304 when nothing changed ────");

var MAIN = Guid.NewGuid();
var KID = Guid.NewGuid();
world.Add(MAIN, "Robin");
world.Add(KID, "kid-account");
Map(MAIN, MAIN, "Robin", pin: "2468");
Map(KID, MAIN, "Kid");

foreach (var route in new[] { "GetAnnouncements", "GetProfiles" })
{
    var first = Call(route, UserPrincipal(MAIN));
    var tag = first.http?.Response.Headers["ETag"].ToString();
    Ok(route + ": answers with an ETag", first.status == 200 && !string.IsNullOrEmpty(tag), first.status + " tag=" + tag);
    Ok(route + ": kept, but asked about every time",
       first.http?.Response.Headers["Cache-Control"].ToString() == "private, no-cache",
       first.http?.Response.Headers["Cache-Control"].ToString());
    if (string.IsNullOrEmpty(tag)) continue;

    var again = Call(route, UserPrincipal(MAIN), headers: new() { ["If-None-Match"] = tag });
    Ok(route + ": the same tag gets 304", again.status == 304, again.status.ToString());
    var weak = Call(route, UserPrincipal(MAIN), headers: new() { ["If-None-Match"] = "\"other\", W/" + tag });
    Ok(route + ": so does a weak one in a list", weak.status == 304, weak.status.ToString());
    var stale = Call(route, UserPrincipal(MAIN), headers: new() { ["If-None-Match"] = "\"0000\"" });
    Ok(route + ": another tag gets the body", stale.status == 200, stale.status.ToString());
}

// The anonymous script and theme routes answer the same way: a compressing proxy turns a
// strong tag into W/"..." and a list is legal, so an exact compare never matched.
foreach (var route in new[] { "GetProfilesJs", "GetThemes" })
{
    var rargs = route == "GetThemes" ? new object[] { null } : null;
    var f = Call(route, UserPrincipal(MAIN), rargs);
    var t = f.http?.Response.Headers["ETag"].ToString();
    if (string.IsNullOrEmpty(t)) { Ok(route + ": answers with an ETag", false, f.status + " tag=" + t); continue; }
    Ok(route + ": the same tag gets 304", Call(route, UserPrincipal(MAIN), rargs, new() { ["If-None-Match"] = t }).status == 304);
    Ok(route + ": a weak tag gets 304", Call(route, UserPrincipal(MAIN), rargs, new() { ["If-None-Match"] = "W/" + t }).status == 304);
    Ok(route + ": a weak tag in a list gets 304", Call(route, UserPrincipal(MAIN), rargs, new() { ["If-None-Match"] = "\"other\", W/" + t }).status == 304);
    Ok(route + ": another tag gets the body", Call(route, UserPrincipal(MAIN), rargs, new() { ["If-None-Match"] = "\"0000\"" }).status == 200);
}

// Turned off on the Status tab (2.0), profiles.js is the same script told to stay off (so the
// settings page keeps its theme preview), under its own tag, and every load asks again so the
// change reaches a browser on its next page load in either direction.
{
    var onProp = cfgType.GetProperty("BonfireOn");
    string Body((int status, object value, HttpContext http) r) => (r.value as ContentResult)?.Content ?? r.value as string;
    onProp.SetValue(config, true);
    var on = Call("GetProfilesJs", UserPrincipal(MAIN));
    var onTag = on.http?.Response.Headers["ETag"].ToString();
    Ok("on: the script says it is on", (Body(on) ?? "").Contains("const BONFIRE_OFF = false; // __BONFIRE_OFF__"),
       on.status + " " + on.value?.GetType().Name);
    Ok("on: every load asks again", on.http?.Response.Headers["Cache-Control"].ToString() == "public, no-cache",
       on.http?.Response.Headers["Cache-Control"].ToString());
    onProp.SetValue(config, false);
    var off = Call("GetProfilesJs", UserPrincipal(MAIN), null, new() { ["If-None-Match"] = onTag ?? "" });
    var offTag = off.http?.Response.Headers["ETag"].ToString();
    Ok("off: a browser holding the on copy gets the off one, not a 304", off.status == 200, off.status.ToString());
    Ok("off: under a tag of its own", !string.IsNullOrEmpty(offTag) && offTag != onTag, onTag + " / " + offTag);
    var offBody = Body(off) ?? "";
    Ok("off: the script says it is off", offBody.Contains("const BONFIRE_OFF = true; // __BONFIRE_OFF__")
       && !offBody.Contains("const BONFIRE_OFF = false;"));
    Ok("off: and it is the whole script, not the inert one, so the theme preview is there",
       offBody.Contains("window.BonfireThemePreview") && !offBody.Contains("emergency disable active"));
    Ok("off: every load asks again", off.http?.Response.Headers["Cache-Control"].ToString() == "public, no-cache");
    Ok("off: its own tag gets 304", Call("GetProfilesJs", UserPrincipal(MAIN), null, new() { ["If-None-Match"] = offTag ?? "" }).status == 304);
    onProp.SetValue(config, true);
    var back = Call("GetProfilesJs", UserPrincipal(MAIN), null, new() { ["If-None-Match"] = offTag ?? "" });
    Ok("turned back on: a browser holding the off copy gets the on one", back.status == 200
       && (Body(back) ?? "").Contains("const BONFIRE_OFF = false; // __BONFIRE_OFF__"), back.status.ToString());
    Ok("under the on tag again", back.http?.Response.Headers["ETag"].ToString() == onTag);
}

var before = Call("GetAnnouncements", UserPrincipal(MAIN)).http?.Response.Headers["ETag"].ToString();
var ann = Activator.CreateInstance(annType);
annType.GetProperty("Id").SetValue(ann, Guid.NewGuid());
annType.GetProperty("Message").SetValue(ann, "Back at six.");
announcements.Add(ann);
var after = Call("GetAnnouncements", UserPrincipal(MAIN), headers: new() { ["If-None-Match"] = before ?? "" });
Ok("a new announcement changes the tag, so it is sent", after.status == 200 && after.http.Response.Headers["ETag"].ToString() != before,
   after.status + " " + before);

var listBefore = Call("GetProfiles", UserPrincipal(MAIN)).http?.Response.Headers["ETag"].ToString();
var KID2 = Guid.NewGuid();
world.Add(KID2, "kid2");
Map(KID2, MAIN, "Second");
var listAfter = Call("GetProfiles", UserPrincipal(MAIN), headers: new() { ["If-None-Match"] = listBefore ?? "" });
Ok("a new profile changes the list's tag", listAfter.status == 200, listAfter.status.ToString());
var kidView = Call("GetProfiles", UserPrincipal(KID)).http?.Response.Headers["ETag"].ToString();
var mainView = Call("GetProfiles", UserPrincipal(MAIN)).http?.Response.Headers["ETag"].ToString();
Ok("the owner and a profile get different lists, so different tags", kidView != mainView);

// ── GET /me ──────────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── GET /me ─────────────────────────────────────────────────────");

var meMain = Call("GetMe", UserPrincipal(MAIN));
Ok("the route exists", meMain.status != -404, meMain.value as string);
if (meMain.status != -404)
{
    Ok("a main account is not a profile, and is its own main account",
       meMain.status == 200 && (bool)Prop(meMain.value, "IsProfile") == false && (Guid)Prop(meMain.value, "MainAccountId") == MAIN,
       meMain.status + "");
    Ok("it has a PIN and two profiles", (bool)Prop(meMain.value, "MainHasPin") && (int)Prop(meMain.value, "ProfileCount") == 2);

    var meKid = Call("GetMe", UserPrincipal(KID));
    Ok("a profile is one, named as its household names it, under its main account",
       meKid.status == 200 && (bool)Prop(meKid.value, "IsProfile") && (string)Prop(meKid.value, "Name") == "Kid"
       && (Guid)Prop(meKid.value, "MainAccountId") == MAIN && (string)Prop(meKid.value, "MainAccountName") == "Robin");

    var meNone = Call("GetMe", new ClaimsPrincipal(new ClaimsIdentity()));
    Ok("nobody signed in gets 401", meNone.status == 401, meNone.status.ToString());

    Ok("its answer is a declared type, for OpenAPI",
       ctrlType.GetMethod("GetMe").GetCustomAttributes<ProducesResponseTypeAttribute>().Any(a => a.StatusCode == 200 && a.Type?.Name == "MeResponse"));
}
Ok("/info's answer is a declared type too",
   ctrlType.GetMethod("GetInfo").GetCustomAttributes<ProducesResponseTypeAttribute>().Any(a => a.StatusCode == 200 && a.Type?.Name == "InfoResponse"));

// ── The lookup service ───────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── IBonfireProfiles, for other plugins ─────────────────────────");

var iface = asm.GetType("Jellyfin.Profiles.IBonfireProfiles");
var impl = asm.GetType("Jellyfin.Profiles.BonfireProfiles");
Ok("the interface is public, under the documented name", iface != null && iface.IsPublic && iface.IsInterface);
if (iface != null && impl != null)
{
    var svc = Activator.CreateInstance(impl);
    bool IsProfile(Guid id) => (bool)iface.GetMethod("IsProfile").Invoke(svc, new object[] { id });
    Guid MainOf(Guid id) => (Guid)iface.GetMethod("GetMainAccount").Invoke(svc, new object[] { id });
    var house = ((IEnumerable<Guid>)iface.GetMethod("GetHousehold").Invoke(svc, new object[] { KID })).ToList();

    Ok("a profile is a profile", IsProfile(KID));
    Ok("a main account is not", !IsProfile(MAIN));
    Ok("nor is an account Bonfire has never seen", !IsProfile(plainUser) && MainOf(plainUser) == plainUser);
    Ok("a profile's main account", MainOf(KID) == MAIN);
    Ok("the household, main account first", house.Count == 3 && house[0] == MAIN && house.Contains(KID) && house.Contains(KID2),
       string.Join(",", house));

    var registrator = asm.GetType("Jellyfin.Profiles.PluginServiceRegistrator");
    var sc = new ServiceCollection();
    try { registrator.GetMethod("RegisterServices").Invoke(Activator.CreateInstance(registrator), new object[] { sc, null }); } catch { /* reported below */ }
    Ok("and Jellyfin is given it", sc.Any(d => d.ServiceType == iface && d.ImplementationType == impl));
}

// ── A new profile's colour ───────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── A profile made without a colour gets one nobody has ─────────");

var next = asm.GetType("Jellyfin.Profiles.Controllers.ProfilesBaseController")
    .GetMethod("NextAvatarColor", BindingFlags.NonPublic | BindingFlags.Static);
Ok("the server can pick one", next != null);
if (next != null)
{
    string Pick(Guid main) => (string)next.Invoke(null, new[] { main, config });
    var FRESH = Guid.NewGuid();
    Ok("a household with no rows skips the main account's default blue", Pick(FRESH) == "#E50914", Pick(FRESH));

    // MAIN, KID and KID2 above were stored with the mapping default, which is that blue.
    Ok("rows stored with the default count as using it", Pick(MAIN) == "#E50914", Pick(MAIN));
    var cfgRow = mappings.Cast<object>().First(m => (Guid)mapType.GetProperty("ProfileUserId").GetValue(m) == KID);
    mapType.GetProperty("AvatarColor").SetValue(cfgRow, "#e50914");
    Ok("a colour in use is skipped, whatever its case", Pick(MAIN) == "#22C55E", Pick(MAIN));
    Ok("the stored default is the client's default, not #1F77B4",
       (string)mapType.GetProperty("AvatarColor").GetValue(Activator.CreateInstance(mapType)) == "#00A4DC");
}

// ── Dashboard → Activity ─────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── Dashboard → Activity ────────────────────────────────────────");

activity.Clear();
var reqType = ctrlType.GetMethod("ResetPinAdmin").GetParameters()[0].ParameterType;
var reset = Activator.CreateInstance(reqType);
reqType.GetProperty("ProfileId").SetValue(reset, KID);
// A profile whose PIN was guessed at until it locked (P2-705): clearing the PIN has to end
// the lock too, or the new one it is given is refused for the rest of the quarter hour.
var budget = asm.GetType("Jellyfin.Profiles.Controllers.PinBudget", true);
bool BudgetOpen() => (bool)budget.GetMethod("TryBegin", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { KID });
for (int i = 0; i < 8; i++) BudgetOpen();
Ok("setup: the profile's PIN is locked by wrong guesses", !BudgetOpen());
var resetResult = Call("ResetPinAdmin", KeyPrincipal(true, "Administrator"), new[] { reset });
Ok("an API key can clear a PIN", resetResult.status == 200, resetResult.status + " " + resetResult.value);
Ok("and clearing it ends the lock on that profile", BudgetOpen());
Ok("which is written to the Activity log, naming the profile and who did it",
   activity.Count == 1 && activity[0].Type == "BonfirePinReset" && activity[0].Name.Contains("Kid") && activity[0].Name.StartsWith("An API key")
   && activity[0].UserId == KID,
   string.Join(" | ", activity.Select(a => a.Type + ": " + a.Name)));

activity.Clear();
var verifyType = ctrlType.GetMethod("VerifyAccountPin").GetParameters()[0].ParameterType;
object Wrong() { var v = Activator.CreateInstance(verifyType); verifyType.GetProperty("Pin").SetValue(v, "0000"); return v; }
var refusedAt = -1;
for (int i = 0; i < 14; i++)
{
    var r = Call("VerifyAccountPin", UserPrincipal(MAIN), new[] { Wrong() });
    if (r.status == 429 && refusedAt < 0) refusedAt = i;
}
var locks = activity.Where(a => a.Type == "BonfirePinLocked").ToList();
Ok("too many wrong tries lock the PIN", refusedAt > 0, "first 429 at " + refusedAt);
Ok("and the lock is written once, not once per refused try", locks.Count == 1 && locks[0].UserId == MAIN,
   locks.Count + " lines");

// ── Routes read the configuration as a copy ──────────────────────────────────
Console.WriteLine();
Console.WriteLine("── A signed-in route does not enumerate the live lists ─────────");
{
    // A writer adds and removes a mapping under the configuration lock while the route
    // runs. A route that walks the live list throws "collection was modified" now and then;
    // one that reads a copy never does.
    var lockField = asm.GetType("Jellyfin.Profiles.Controllers.ProfilesBaseController", true)
        .GetField("ConfigLock", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
    var theLock = lockField.GetValue(null);
    var filler = new List<object>();
    lock (theLock)
        for (var i = 0; i < 4000; i++) { var f = Map(Guid.NewGuid(), Guid.NewGuid(), "filler" + i); filler.Add(f); }
    var stop = false;
    var writer = new System.Threading.Thread(() =>
    {
        while (!stop)
        {
            var extra = Activator.CreateInstance(mapType);
            mapType.GetProperty("ProfileUserId").SetValue(extra, Guid.NewGuid());
            mapType.GetProperty("MasterUserId").SetValue(extra, Guid.NewGuid());
            lock (theLock) { mappings.Add(extra); mappings.Remove(extra); }
        }
    });
    writer.Start();
    var raced = 0;
    var firstError = "";
    for (var i = 0; i < 400; i++)
    {
        var r = Call("GetProfiles", UserPrincipal(MAIN));
        if (r.status == -500) { raced++; if (firstError == "") firstError = r.value as string; }
    }
    stop = true;
    writer.Join();
    lock (theLock) foreach (var f in filler) mappings.Remove(f);
    Ok("GetProfiles never fails because the list changed under it (" + raced + " of 400)", raced == 0, firstError);
}

// ── POST /admin/cleanup-orphans ──────────────────────────────────────────────
// R-1151: each Jellyfin user is looked up once (not once per row, per test, inside the
// lock), the preview and the result count what is actually removed, and a member of a
// group that is itself removed is not counted twice.
{
    Console.WriteLine();
    Console.WriteLine("── POST /admin/cleanup-orphans ─────────────────────────────────");
    mappings.Clear();
    var groupType = asm.GetType("Jellyfin.Profiles.Configuration.BonfireGroup", true);
    var groups = (IList)cfgType.GetProperty("BonfireGroups").GetValue(config);
    groups.Clear();
    object Group(string id, Guid owner, params Guid[] members)
    {
        var g = Activator.CreateInstance(groupType);
        groupType.GetProperty("GroupId").SetValue(g, id);
        groupType.GetProperty("OwnerUserId").SetValue(g, owner);
        groupType.GetProperty("MemberUserIds").SetValue(g, members.ToList());
        groups.Add(g);
        return g;
    }

    var liveMaster = Guid.NewGuid(); var liveKid = Guid.NewGuid(); var liveKid2 = Guid.NewGuid();
    var goneKid = Guid.NewGuid(); var goneMaster = Guid.NewGuid(); var goneA = Guid.NewGuid(); var goneB = Guid.NewGuid();
    world.Add(liveMaster, "om"); world.Add(liveKid, "ok"); world.Add(liveKid2, "ok2");
    Map(liveKid, liveMaster, "Fine");
    Map(goneKid, liveMaster, "Gone");
    Map(liveKid2, goneMaster, "Masterless");
    Group("g-dead", goneMaster, liveMaster, goneA);          // owner gone: the whole group goes
    Group("g-live", liveMaster, liveMaster, goneA, goneB);   // two members gone

    System.Text.Json.JsonElement Json(object o) =>
        System.Text.Json.JsonSerializer.SerializeToElement(o);

    world.Lookups = 0;
    var preview = Call("CleanupOrphans", KeyPrincipal(true, "Administrator"), new object[] { false });
    var pj = Json(preview.value);
    Ok("cleanup preview: lists the two orphaned mappings", pj.GetProperty("orphans").GetArrayLength() == 2,
       preview.status + " " + preview.value);
    Ok("cleanup preview: one group goes", pj.GetProperty("deadGroups").GetArrayLength() == 1);
    Ok("cleanup preview: counts only members that are removed on their own (not those of a group that goes)",
       pj.GetProperty("deadMembers").GetArrayLength() == 2, pj.GetProperty("deadMembers").GetArrayLength().ToString());
    Ok("cleanup preview: each user is looked up once", world.Lookups <= 7, world.Lookups + " lookups for 7 users");

    var applied = Call("CleanupOrphans", KeyPrincipal(true, "Administrator"), new object[] { true });
    var aj = Json(applied.value);
    Ok("cleanup: reports the mappings removed", aj.GetProperty("removedMappings").GetInt32() == 2, applied.value?.ToString());
    Ok("cleanup: reports the group removed", aj.GetProperty("removedGroups").GetInt32() == 1);
    Ok("cleanup: reports the members removed, not those of the removed group",
       aj.GetProperty("removedMembers").GetInt32() == 2, aj.GetProperty("removedMembers").GetInt32().ToString());
    Ok("cleanup: what is left is the live mapping and the live group", mappings.Count == 1 && groups.Count == 1,
       mappings.Count + " mappings, " + groups.Count + " groups");
    Ok("cleanup: the live group keeps only its live member",
       ((System.Collections.Generic.List<Guid>)groupType.GetProperty("MemberUserIds").GetValue(groups[0])).Count == 1);
}

Console.WriteLine();
try { Directory.Delete(tempDir, true); } catch { }
if (fails.Count > 0)
{
    foreach (var f in fails) Console.WriteLine("   - " + f);
    Console.WriteLine($"{pass} passed, {fails.Count} failed");
    return 1;
}
Console.WriteLine($"{pass} passed, 0 failed");
return 0;

// ─────────────────────────────────────────────────────────────────────────────

public class Recorder : DispatchProxy
{
    public List<ActivityLog> Entries;

    public static IActivityManager Create(List<ActivityLog> entries)
    {
        var p = DispatchProxy.Create<IActivityManager, Recorder>();
        ((Recorder)(object)p).Entries = entries;
        return p;
    }

    protected override object Invoke(MethodInfo m, object[] args)
    {
        if (m.Name == "CreateAsync") { lock (Entries) Entries.Add((ActivityLog)args[0]); return Task.CompletedTask; }
        var t = m.ReturnType;
        if (t == typeof(void)) return null;
        if (t == typeof(Task)) return Task.CompletedTask;
        return null;
    }
}

public class World
{
    public Dictionary<Guid, User> Users = new();
    public int Lookups;

    public void Add(Guid id, string name)
    {
        var u = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty("Username").SetValue(u, name);
        typeof(User).GetProperty("Id").SetValue(u, id);
        typeof(User).GetProperty("AuthenticationProviderId").SetValue(u, "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider");
        Users[id] = u;
    }

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
                System.Threading.Interlocked.Increment(ref W.Lookups);
                return W.Users.TryGetValue((Guid)args[0], out var u) ? u : null;
            case "GetUserDto":
                var user = (User)args[0];
                return new UserDto
                {
                    Id = user.Id,
                    Name = user.Username,
                    Policy = new UserPolicy { AuthenticationProviderId = user.AuthenticationProviderId, PasswordResetProviderId = "x" },
                    Configuration = new UserConfiguration(),
                };
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
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
            return Array.CreateInstance(t.GetGenericArguments()[0], 0);
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            return Array.CreateInstance(t.GetGenericArguments()[0], 0);
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
