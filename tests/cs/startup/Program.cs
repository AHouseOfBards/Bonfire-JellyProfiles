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
// GET /startup: one signed-in request that returns what a client needs to draw the
// profile picker. Its info, profiles, me, themes and announcements are each what the
// separate route returns for the same caller (compared as serialized JSON), for a
// main account and for a profile's token. No user is 401, an API key with no user is
// refused as /list refuses it, a caller never sees another household's profiles, and a
// part that fails fails the whole call.
//
// Against the tree before the route existed the first assertion fails: no route.
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
// A new install starts with Bonfire off (2.0), and off every gate stands down, so a
// refusal here would prove nothing. This harness is about a server where it is on.
config.GetType().GetProperty("BonfireOn").SetValue(config, true);
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

// ── Helpers for this harness ─────────────────────────────────────────────────
string Json(object o) => System.Text.Json.JsonSerializer.Serialize(o);
// /themes answers a string of JSON in a ContentResult, not an object.
string ThemesBody(ClaimsPrincipal who)
{
    var r = Call("GetThemes", who, new object[] { null });
    var content = (r.value as ContentResult)?.Content;
    return content == null ? null : Json(System.Text.Json.JsonDocument.Parse(content).RootElement);
}

var MAIN = Guid.NewGuid();
var KID = Guid.NewGuid();
var OTHER = Guid.NewGuid();
var OTHERKID = Guid.NewGuid();
world.Add(MAIN, "Robin");
world.Add(KID, "kid-account");
world.Add(OTHER, "Stranger");
world.Add(OTHERKID, "stranger-kid-account");
Map(MAIN, MAIN, "Robin", pin: "2468");
Map(KID, MAIN, "Kid");
Map(OTHER, OTHER, "Stranger");
Map(OTHERKID, OTHER, "StrangersChildQuillfeather");

var ann = Activator.CreateInstance(annType);
annType.GetProperty("Id").SetValue(ann, Guid.NewGuid());
annType.GetProperty("Message").SetValue(ann, "Back at six.");
announcements.Add(ann);

Console.WriteLine();
Console.WriteLine("── GET /startup ────────────────────────────────────────────────");

var probe = Call("GetStartup", UserPrincipal(MAIN));
Ok("the route exists", probe.status != -404, probe.value as string);
if (probe.status == -404) { Console.WriteLine($"{pass} passed, {fails.Count} failed"); return 1; }

void CheckCaller(string label, Guid caller)
{
    var who = UserPrincipal(caller);
    var s = Call("GetStartup", who);
    Ok(label + ": answers 200", s.status == 200, s.status + " " + s.value);
    if (s.status != 200) return;
    Ok(label + ": info equals /info", Json(Prop(s.value, "info")) == Json(Call("GetInfo", who).value));
    Ok(label + ": profiles equals /list", Json(Prop(s.value, "profiles")) == Json(Call("GetProfiles", who).value));
    // /me, /announcements and /onboarding are gone; these are what they answered for this caller.
    var me = Prop(s.value, "me");
    var isKid = caller == KID;
    Ok(label + ": me says who the caller is, as /me did",
       me != null && (Guid)Prop(me, "UserId") == caller && (bool)Prop(me, "IsProfile") == isKid
       && (Guid)Prop(me, "MainAccountId") == MAIN && (string)Prop(me, "MainAccountName") == "Robin"
       && (string)Prop(me, "Name") == (isKid ? "Kid" : "Robin") && (bool)Prop(me, "MainHasPin")
       && (int)Prop(me, "ProfileCount") == 1, Json(me));
    Ok(label + ": announcements holds the one showing, as /announcements did",
       Json(Prop(s.value, "announcements")).Contains("Back at six."), Json(Prop(s.value, "announcements")));
    var ob = Prop(s.value, "onboarding");
    Ok(label + ": onboarding says not pending while the setup is off, as /onboarding did",
       ob != null && !(bool)Prop(ob, "Pending") && ((IList)Prop(ob, "Steps")).Count == 0, Json(ob));
    var themes = ThemesBody(who);
    Ok(label + ": themes equals /themes", themes != null && Json(Prop(s.value, "themes")) == themes);
    Ok(label + ": the body holds those five parts and nothing else",
       string.Join(",", s.value.GetType().GetProperties().Select(p => p.Name).OrderBy(x => x))
       == "announcements,info,me,onboarding,profiles,themes");
    // Strictest part: /info is no-store.
    Ok(label + ": not cached, as /info is not", s.http.Response.Headers["Cache-Control"].ToString() == "no-store",
       s.http.Response.Headers["Cache-Control"].ToString());
    Ok(label + ": the profile list is not empty", Json(Prop(s.value, "profiles")).Length > 2);
}
CheckCaller("main account", MAIN);
CheckCaller("a profile's token", KID);

// Another household's profiles never appear, in /startup or anywhere in its body.
foreach (var (label, caller) in new[] { ("main account", MAIN), ("a profile", KID) })
{
    var s = Call("GetStartup", UserPrincipal(caller));
    var all = Json(s.value);
    Ok(label + ": never sees another household's profiles",
       s.status == 200 && !all.Contains("Quillfeather") && !all.Contains(OTHERKID.ToString(), StringComparison.OrdinalIgnoreCase)
       && !all.Contains(OTHERKID.ToString("N"), StringComparison.OrdinalIgnoreCase));
}
var theirs = Call("GetStartup", UserPrincipal(OTHER));
Ok("the other household sees its own profile and not Robin's",
   theirs.status == 200 && Json(theirs.value).Contains("Quillfeather") && !Json(theirs.value).Contains("\"Kid\""));

// Who may call it.
Ok("no user: 401", Call("GetStartup", new ClaimsPrincipal(new ClaimsIdentity())).status == 401);
Ok("an API key with no user is refused, as /list refuses it",
   Call("GetStartup", KeyPrincipal(true, "Administrator")).status == 401
   && Call("GetProfiles", KeyPrincipal(true, "Administrator")).status == 401);

// A part that fails fails the call, as its own route would: no partial body.
var gone = Guid.NewGuid();
var ghost = Call("GetStartup", UserPrincipal(gone));
var ghostList = Call("GetProfiles", UserPrincipal(gone));
Ok("a signed-in account the server cannot find is refused as /list refuses it, with no body",
   ghost.status == ghostList.status && ghost.status != 200 && !(ghost.value is IDictionary) && Prop(ghost.value, "info") == null,
   ghost.status + " vs " + ghostList.status);

// Switched off: whatever each route says in that state, and /info says so.
{
    var onProp = cfgType.GetProperty("BonfireOn");
    onProp.SetValue(config, false);
    var who = UserPrincipal(MAIN);
    var s = Call("GetStartup", who);
    Ok("Bonfire off: still answers, with /info's word and the other routes' bodies",
       s.status == 200 && Json(Prop(s.value, "info")) == Json(Call("GetInfo", who).value)
       && Json(Prop(s.value, "profiles")) == Json(Call("GetProfiles", who).value)
       && Prop(s.value, "me") != null && (Guid)Prop(Prop(s.value, "me"), "UserId") == MAIN, s.status + "");
    Ok("Bonfire off: /info says disabled", s.status == 200 && (bool)Prop(Prop(s.value, "info"), "Disabled"));
    onProp.SetValue(config, true);
}

// First-time setup on: a main account is told it is pending, with steps; a profile never is.
{
    var obType = asm.GetType("Jellyfin.Profiles.Configuration.OnboardingSettings", true);
    var settings = Activator.CreateInstance(obType);
    obType.GetProperty("Enabled").SetValue(settings, true);
    cfgType.GetProperty("Onboarding").SetValue(config, settings);
    var main = Prop(Call("GetStartup", UserPrincipal(MAIN)).value, "onboarding");
    var kid = Prop(Call("GetStartup", UserPrincipal(KID)).value, "onboarding");
    Ok("onboarding on: a main account is pending, with steps", main != null && (bool)Prop(main, "Pending") && ((IList)Prop(main, "Steps")).Count > 0, Json(main));
    Ok("onboarding on: a profile is told it is not pending", kid != null && !(bool)Prop(kid, "Pending") && ((IList)Prop(kid, "Steps")).Count == 0, Json(kid));
    Ok("onboarding on: the generation is carried", main != null && (int)Prop(main, "Generation") == 1);
    cfgType.GetProperty("Onboarding").SetValue(config, null);
}

// The three reads startup replaced are gone, and the two writes beside them stay.
foreach (var gone3 in new[] { "GetMe", "GetAnnouncements", "GetOnboarding" })
    Ok(gone3 + " is removed", ctrlType.GetMethod(gone3) == null);
Ok("POST /me and POST /onboarding/done stay", ctrlType.GetMethod("OnboardingDone") != null
   && ctrlType.GetMethods().Any(m => m.GetCustomAttributes(typeof(HttpPostAttribute), false).Cast<HttpPostAttribute>().Any(a => a.Template == "me")));

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
