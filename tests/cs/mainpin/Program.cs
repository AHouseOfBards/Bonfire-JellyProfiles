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
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

// ─────────────────────────────────────────────────────────────────────────────
// The main account's PIN, as the guard on changing a household.
//
// Runs the real routes. What it holds the plugin to:
//
//  - Every route that changes a household needs the main PIN when one is set: profiles,
//    devices, sharing (Bonfire) and switcher settings. Until 1.7.2 only five did; sharing,
//    devices and switcher settings took none, so a main account left open let anyone at
//    the switcher join or leave a Bonfire, allow PIN-less household switching, or forget
//    the device a child's profile was locked to.
//  - The PIN can come in a header, for routes with no body.
//  - No PIN sent is not a wrong guess, so it can't lock the owner out.
//  - "Require main PINs" refuses all of it until a PIN is set, except setting one.
//  - account/verify-pin checks the PIN even where opening the account needs none.
//  - "Ask for the PIN to open this account" is the main account's own, and stops at the
//    household's edge.
//
// Against b3b1825 the guard assertions fail: those routes answered without a PIN.
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
var baseType = asm.GetType("Jellyfin.Profiles.Controllers.ProfilesBaseController", true);
var pluginType = asm.GetType("Jellyfin.Profiles.Plugin", true);
var cfgType = asm.GetType("Jellyfin.Profiles.Configuration.PluginConfiguration", true);
var mapType = asm.GetType("Jellyfin.Profiles.Configuration.ProfileMapping", true);
var hasher = asm.GetType("Jellyfin.Profiles.Auth.PinHasher", true);

var tempDir = Path.Combine(Path.GetTempPath(), "bonfire-mainpin-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
var plugin = Activator.CreateInstance(pluginType, new StubPaths(tempDir), new StubXml());
var config = pluginType.GetProperty("Configuration").GetValue(plugin);
var mappings = (IList)cfgType.GetProperty("Mappings").GetValue(config);

object Map(Guid profile, Guid master, string pin = null)
{
    var m = Activator.CreateInstance(mapType);
    mapType.GetProperty("ProfileUserId").SetValue(m, profile);
    mapType.GetProperty("MasterUserId").SetValue(m, master);
    mapType.GetProperty("ProfileName").SetValue(m, "p");
    if (pin != null) mapType.GetProperty("PinHash").SetValue(m, hasher.GetMethod("Hash").Invoke(null, new object[] { pin }));
    mappings.Add(m);
    return m;
}
object Row(Guid id) => mappings.Cast<object>().FirstOrDefault(m => (Guid)mapType.GetProperty("ProfileUserId").GetValue(m) == id);
bool HasPin(Guid id) => !string.IsNullOrEmpty((string)mapType.GetProperty("PinHash").GetValue(Row(id)));
void SetConfig(string name, object value) => cfgType.GetProperty(name)?.SetValue(config, value);

var world = new World();
var users = World.Create<IUserManager>(world);

// One call into the real controller. Returns the status and, for a refusal, its text.
(int status, string message) Call(string method, Guid caller, Dictionary<string, object> body = null, string headerPin = null, object[] raw = null)
{
    var route = ctrlType.GetMethod(method);
    if (route == null) return (-404, "no route " + method);

    var ctor = ctrlType.GetConstructors().First();
    var args = ctor.GetParameters().Select(p =>
        p.ParameterType == typeof(IUserManager) ? users
        : p.ParameterType.Name.StartsWith("ILogger") ? Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(ctrlType))
        : (object)null).ToArray();
    var c = (ControllerBase)ctor.Invoke(args);
    var http = new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", caller.ToString()) }, "test")),
    };
    http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7");
    if (headerPin != null) http.Request.Headers["X-Bonfire-Master-Pin"] = headerPin;
    c.ControllerContext = new ControllerContext { HttpContext = http };

    object[] invokeArgs = raw;
    if (invokeArgs == null)
    {
        invokeArgs = route.GetParameters().Select(p =>
        {
            if (p.ParameterType.IsValueType) return Activator.CreateInstance(p.ParameterType);
            var req = Activator.CreateInstance(p.ParameterType);
            foreach (var kv in body ?? new Dictionary<string, object>())
            {
                var prop = p.ParameterType.GetProperty(kv.Key);
                if (prop == null) continue;
                prop.SetValue(req, prop.PropertyType == typeof(string) && kv.Value is Guid g ? g.ToString() : kv.Value);
            }
            return req;
        }).ToArray();
    }

    object result;
    try { result = route.Invoke(c, invokeArgs); }
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

const string MISSING = "Enter your account's PIN.";
const string REQUIRED = "Set a PIN for your account first.";
bool PinRefusal((int status, string message) r) =>
    r.message == MISSING || r.message == REQUIRED || (r.message ?? "").StartsWith("Invalid Master PIN") || r.status == 429;

// The routes that change a household and had no PIN check before 1.7.2.
var household = new (string method, Dictionary<string, object> body, object[] raw)[]
{
    ("UpdateBonfireSettings", new() { ["AllowHouseholdLanBypass"] = (bool?)true }, null),
    ("GenerateBonfireCode", null, null),
    ("JoinBonfire", new() { ["BonfireCode"] = "ZZZZZZ" }, null),
    ("KickBonfireMember", new() { ["MemberId"] = Guid.NewGuid() }, null),
    ("RegenerateBonfireCode", null, null),
    ("LeaveBonfire", null, null),
    ("DeleteBonfireGroup", null, null),
    ("UpdatePreferences", new() { ["AskOnStartup"] = (bool?)false }, null),
    ("DeleteDevice", new() { ["DeviceId"] = "tv-1" }, null),
    ("MergeDevices", new() { ["FromDeviceId"] = "tv-1", ["IntoDeviceId"] = "tv-2" }, null),
    ("RenameDevice", new() { ["DeviceId"] = "tv-1", ["DeviceName"] = "Lounge" }, null),
    ("MergeDuplicateDevices", null, new object[] { true }),
};
// The five that already asked for it. Artwork checks the profile is the caller's first.
var LOCKED_KID = Guid.NewGuid();
var profileRoutes = new (string method, Dictionary<string, object> body)[]
{
    ("CreateProfile", new() { ["ProfileName"] = "New" }),
    ("UpdateProfile", new() { ["ProfileId"] = Guid.NewGuid(), ["ProfileName"] = "Kid" }),
    ("DeleteProfile", new() { ["ProfileId"] = Guid.NewGuid() }),
    ("SetProfileTheme", new() { ["ProfileId"] = Guid.NewGuid(), ["Theme"] = "ember" }),
    ("SetLibraryArtwork", new() { ["ProfileId"] = LOCKED_KID, ["LibraryId"] = Guid.NewGuid() }),
};

// ── A main account with a PIN ────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── A main account with a PIN ───────────────────────────────────");

var LOCKED = Guid.NewGuid();
world.Add(LOCKED, "Locked");
Map(LOCKED, LOCKED, pin: "1234");
Map(LOCKED_KID, LOCKED);

foreach (var (method, body, raw) in household)
{
    var r = Call(method, LOCKED, body, raw: raw);
    Ok(method + " is refused without the PIN", r.status == 400 && r.message == MISSING, r.status + " " + r.message);
}
foreach (var (method, body) in profileRoutes)
{
    var r = Call(method, LOCKED, body);
    Ok(method + " says the PIN is missing, rather than wrong", r.status == 400 && r.message == MISSING, r.status + " " + r.message);
}

// Seventeen PIN-less calls above. Had they counted, the limiter would have shut the door.
foreach (var (method, body, raw) in household)
{
    var r = Call(method, LOCKED, body, headerPin: "1234", raw: raw);
    Ok(method + " goes ahead with the PIN in the header", !PinRefusal(r) && r.status != -404, r.status + " " + r.message);
}

var wrong = Call("UpdatePreferences", LOCKED, new() { ["AskOnStartup"] = (bool?)false }, headerPin: "9999");
Ok("a wrong PIN is refused as wrong", wrong.status == 400 && (wrong.message ?? "").StartsWith("Invalid Master PIN"), wrong.status + " " + wrong.message);

var preview = Call("MergeDuplicateDevices", LOCKED, raw: new object[] { false });
Ok("looking for duplicate devices without merging them needs no PIN", !PinRefusal(preview), preview.status + " " + preview.message);

// ── A main account without one ───────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── A main account without one ──────────────────────────────────");

var OPEN = Guid.NewGuid();
world.Add(OPEN, "Open");
Map(OPEN, OPEN);

var openPrefs = Call("UpdatePreferences", OPEN, new() { ["AskOnStartup"] = (bool?)true });
Ok("changes go ahead: nothing to check", !PinRefusal(openPrefs), openPrefs.status + " " + openPrefs.message);

var hasAccountRoutes = ctrlType.GetMethod("UpdateAccount") != null && ctrlType.GetMethod("VerifyAccountPin") != null;
Ok("this build has the account routes", hasAccountRoutes);

SetConfig("RequireMainPin", true);
foreach (var (method, body, raw) in household)
{
    var r = Call(method, OPEN, body, raw: raw);
    Ok("with main PINs required, " + method + " is refused until one is set", r.status == 400 && r.message == REQUIRED, r.status + " " + r.message);
}
var createRequired = Call("CreateProfile", OPEN, new() { ["ProfileName"] = "Kid" });
Ok("so is creating the first profile", createRequired.status == 400 && createRequired.message == REQUIRED, createRequired.status + " " + createRequired.message);
SetConfig("RequireMasterPinForCreation", false);
var createRequired2 = Call("CreateProfile", OPEN, new() { ["ProfileName"] = "Kid" });
Ok("even where creating skips the PIN check", createRequired2.status == 400 && createRequired2.message == REQUIRED, createRequired2.status + " " + createRequired2.message);
SetConfig("RequireMasterPinForCreation", true);

if (hasAccountRoutes)
{
    var bad = Call("UpdateAccount", OPEN, new() { ["Pin"] = "12a4" });
    Ok("a PIN that isn't 4-8 digits is refused", bad.status == 400 && (bad.message ?? "").StartsWith("PIN code must be"), bad.status + " " + bad.message);

    var set = Call("UpdateAccount", OPEN, new() { ["Pin"] = "4321" });
    Ok("but setting one is allowed: it is how a required PIN gets set", set.status == 200 && HasPin(OPEN), set.status + " " + set.message);

    var clear = Call("UpdateAccount", OPEN, new() { ["Pin"] = "", ["MasterPin"] = "4321" });
    Ok("and it can't be cleared while required", clear.status == 400 && clear.message == REQUIRED && HasPin(OPEN), clear.status + " " + clear.message);

    var change = Call("UpdateAccount", OPEN, new() { ["Pin"] = "5555" });
    Ok("changing it needs the current one", change.status == 400 && change.message == MISSING, change.status + " " + change.message);

    var after = Call("UpdatePreferences", OPEN, new() { ["AskOnStartup"] = (bool?)true }, headerPin: "4321");
    Ok("once set, the household can be changed with it", !PinRefusal(after), after.status + " " + after.message);
}
SetConfig("RequireMainPin", false);

// ── Checking the PIN ─────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── Checking the PIN, and not being asked to open ───────────────");

var SOFA = Guid.NewGuid();
world.Add(SOFA, "Sofa");
var sofaRow = Map(SOFA, SOFA, pin: "2468");
var KID = Guid.NewGuid();
world.Add(KID, "Sofa_kid");
var kidRow = Map(KID, SOFA, pin: "1357");

if (hasAccountRoutes)
{
    mapType.GetProperty("AskPinToOpen")?.SetValue(sofaRow, false);
    mapType.GetProperty("BypassPinOnLocalNetwork").SetValue(sofaRow, true);
    var anyPin = Call("VerifyAccountPin", SOFA, new() { ["Pin"] = "0000" });
    Ok("account/verify-pin refuses a wrong PIN even where opening needs none", anyPin.status == 400, anyPin.status + " " + anyPin.message);
    var right = Call("VerifyAccountPin", SOFA, new() { ["Pin"] = "2468" });
    Ok("and accepts the right one", right.status == 200, right.status + " " + right.message);

    var fromKid = Call("VerifyAccountPin", KID, new() { ["Pin"] = "2468" });
    Ok("a profile can't use it", fromKid.status == 401, fromKid.status + " " + fromKid.message);
    var kidSets = Call("UpdateAccount", KID, new() { ["AskPinToOpen"] = (bool?)true });
    Ok("or change the main account's settings", kidSets.status == 401, kidSets.status + " " + kidSets.message);

    var ask = Call("UpdateAccount", SOFA, new() { ["AskPinToOpen"] = (bool?)true, ["MasterPin"] = "2468" });
    Ok("the owner can turn asking back on, with the PIN", ask.status == 200 && (bool)mapType.GetProperty("AskPinToOpen").GetValue(sofaRow), ask.status + " " + ask.message);
}

var canSkip = baseType.GetMethod("CanSkipPin", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
bool Skip(object row, bool local, bool cross, bool householdLan) => (bool)canSkip.Invoke(null, new[] { row, local, cross, householdLan });

var askProp = mapType.GetProperty("AskPinToOpen");
Ok("main accounts are asked for their PIN by default", askProp != null && (bool)askProp.GetValue(Activator.CreateInstance(mapType)));
if (askProp != null)
{
    mapType.GetProperty("BypassPinOnLocalNetwork").SetValue(sofaRow, false);
    askProp.SetValue(sofaRow, true);
    Ok("asked: away from home, the main account needs its PIN", !Skip(sofaRow, false, false, false));
    askProp.SetValue(sofaRow, false);
    Ok("not asked: it opens without one, away from home too", Skip(sofaRow, false, false, false));
    Ok("but never from another household", !Skip(sofaRow, false, true, false) && !Skip(sofaRow, true, true, false));
    Ok("where only its owner's household-switching setting counts", Skip(sofaRow, true, true, true));
    askProp.SetValue(kidRow, false);
    Ok("on a sub-profile the setting does nothing", !Skip(kidRow, false, false, false) && !Skip(kidRow, true, false, false));
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
