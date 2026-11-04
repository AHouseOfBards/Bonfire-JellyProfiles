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

// ── Dashboard → Activity ─────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── Dashboard → Activity ────────────────────────────────────────");

activity.Clear();
var reqType = ctrlType.GetMethod("ResetPinAdmin").GetParameters()[0].ParameterType;
var reset = Activator.CreateInstance(reqType);
reqType.GetProperty("ProfileId").SetValue(reset, KID);
var resetResult = Call("ResetPinAdmin", KeyPrincipal(true, "Administrator"), new[] { reset });
Ok("an API key can clear a PIN", resetResult.status == 200, resetResult.status + " " + resetResult.value);
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
