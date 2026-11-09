using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Entities.Security;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

// ─────────────────────────────────────────────────────────────────────────────
// A kick, a leave or a deleted Bonfire signs out what was opened through it (R-1302).
//
// /switch into another household's profile returns an ordinary Jellyfin token, and
// Jellyfin's tokens don't expire. Taking the door away used to revoke nothing, so a removed
// member kept the other household's profile on their television indefinitely.
//
// Runs the real routes against a modelled Jellyfin whose Logout works as Jellyfin's does
// (the device row goes, and every session on that device id). What it holds the plugin to:
// after the door closes, the device that came in through it is signed out, that account on
// that device only. Its own household's devices, and members who are still members, stay.
//
// Against the build before the fix, nothing is signed out.
//
// Usage: dotnet run -c Release   (loads bin/Release/<tfm>/Jellyfin.Profiles.dll)
// ─────────────────────────────────────────────────────────────────────────────

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
void Section(string s) { Console.WriteLine(); Console.WriteLine("── " + s + " " + new string('─', Math.Max(4, 66 - s.Length))); }

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
var registry = asm.GetType("Jellyfin.Profiles.Auth.DeviceRegistry", true);

var tempDir = Path.Combine(Path.GetTempPath(), "bonfire-sessionlimit-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
var plugin = Activator.CreateInstance(pluginType, new StubPaths(tempDir), new StubXml());
var config = pluginType.GetProperty("Configuration").GetValue(plugin);
var mappings = (IList)cfgType.GetProperty("Mappings").GetValue(config);

void Map(Guid profile, Guid master, string name)
{
    var m = Activator.CreateInstance(mapType);
    mapType.GetProperty("ProfileUserId").SetValue(m, profile);
    mapType.GetProperty("MasterUserId").SetValue(m, master);
    mapType.GetProperty("ProfileName").SetValue(m, name);
    mappings.Add(m);
}

var world = new World();
var MAIN = Guid.NewGuid(); world.Add(MAIN, "Parent");
var KID = Guid.NewGuid(); world.Add(KID, "Parent_Kid");
var OTHER = Guid.NewGuid(); world.Add(OTHER, "Neighbour");
Map(MAIN, MAIN, "Parent");
Map(KID, MAIN, "Kid");
Map(OTHER, OTHER, "Neighbour");

var users = World.Create<IUserManager>(world);
var sessions = World.Create<ISessionManager>(world);
var devices = World.Create<IDeviceManager>(world);
world.SessionManager = sessions;


var groupType = asm.GetType("Jellyfin.Profiles.Configuration.BonfireGroup", true);
var hasher = asm.GetType("Jellyfin.Profiles.Auth.PinHasher", true);
var fires = (IList)cfgType.GetProperty("BonfireGroups").GetValue(config);
var KIDN = Guid.NewGuid(); world.Add(KIDN, "Neighbour_Kid");
Map(KIDN, OTHER, "Neighbour kid");
object Row(Guid id) => mappings.Cast<object>().First(m => (Guid)mapType.GetProperty("ProfileUserId").GetValue(m) == id);
void SetPin(Guid id, string pin) => mapType.GetProperty("PinHash").SetValue(Row(id), pin == null ? null : hasher.GetMethod("Hash").Invoke(null, new object[] { pin }));

// One call into a real route, as the caller, from a device.
(int status, object value) Call(string method, Guid caller, Dictionary<string, object> body, string deviceId = "main-phone")
{
    var r = ctrlType.GetMethod(method);
    var ctor = ctrlType.GetConstructors().First();
    var args = ctor.GetParameters().Select(p =>
        p.ParameterType == typeof(IUserManager) ? users
        : p.ParameterType == typeof(ISessionManager) ? sessions
        : p.ParameterType == typeof(IDeviceManager) ? devices
        : p.ParameterType == typeof(INetworkManager) ? World.Create<INetworkManager>(world)
        : p.ParameterType == typeof(ILibraryManager) ? World.Create<ILibraryManager>(world)
        : p.ParameterType.Name.StartsWith("ILogger") ? Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(ctrlType))
        : (object)null).ToArray();
    var c = (ControllerBase)ctor.Invoke(args);
    var http = new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", caller.ToString()) }, "test")),
    };
    http.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.20");
    http.Request.Headers["Authorization"] = "MediaBrowser Client=\"Jellyfin Web\", Device=\"Firefox\", DeviceId=\"" + deviceId + "\", Version=\"10.11.0\"";
    c.ControllerContext = new ControllerContext { HttpContext = http };

    var invokeArgs = r.GetParameters().Select(p =>
    {
        var req = Activator.CreateInstance(p.ParameterType);
        foreach (var kv in body ?? new Dictionary<string, object>()) p.ParameterType.GetProperty(kv.Key).SetValue(req, kv.Value);
        return req;
    }).ToArray();

    object result;
    try
    {
        result = r.Invoke(c, invokeArgs);
        if (result is Task t) { t.GetAwaiter().GetResult(); result = t.GetType().GetProperty("Result")?.GetValue(t); }
    }
    catch (TargetInvocationException ex) { return (-500, ex.InnerException?.GetType().Name + ": " + ex.InnerException?.Message); }
    if (result is IConvertToActionResult conv) result = conv.Convert();
    return result switch
    {
        ObjectResult o => (o.StatusCode ?? 200, o.Value),
        StatusCodeResult s => (s.StatusCode, null),
        _ => (-1, result?.GetType().Name),
    };
}
string Text((int status, object value) r) => r.status + " " + r.value;
bool SignedIn(Guid user, string device) => world.Sessions.Any(s => s.UserId == user && s.DeviceId == device);
object Join(Guid owner, Guid member)
{
    var g = Activator.CreateInstance(groupType);
    groupType.GetProperty("OwnerUserId").SetValue(g, owner);
    ((IList)groupType.GetProperty("MemberUserIds").GetValue(g)).Add(member);
    fires.Add(g);
    return g;
}
Dictionary<string, object> Body(params (string k, object v)[] kv) => kv.ToDictionary(x => x.k, x => x.v);

SetPin(KIDN, "2468");
world.Open(MAIN, "Jellyfin Web", "main-phone");
world.Rows.Add(new Device(MAIN, "Jellyfin Web", "10.11.0", "Firefox", "main-phone"));

// ── A kick ───────────────────────────────────────────────────────────────────
Section("A kick");

var g1 = Join(OTHER, MAIN);
var r = Call("SwitchProfile", MAIN, Body(("ProfileId", KIDN), ("Pin", "2468")), "living-room");
Ok("a Bonfire member opens the neighbour's kid with its PIN", r.status == 200 && SignedIn(KIDN, "living-room"), Text(r));
r = Call("KickBonfireMember", OTHER, Body(("MemberId", MAIN)), "neighbour-phone");
Ok("kicking the member works", r.status == 200, Text(r));
Ok("and signs the neighbour's kid out on the TV that opened it", !SignedIn(KIDN, "living-room"), string.Join(", ", world.Sessions.Select(s => s.DeviceId)));
Ok("the member's own sessions are left alone", SignedIn(MAIN, "main-phone"));
fires.Remove(g1);

// ── A leave ──────────────────────────────────────────────────────────────────
Section("A leave");

world.Sessions.RemoveAll(s => s.UserId == KIDN);
g1 = Join(OTHER, MAIN);
r = Call("SwitchProfile", MAIN, Body(("ProfileId", KIDN), ("Pin", "2468")), "living-room");
Ok("opened again", r.status == 200 && SignedIn(KIDN, "living-room"), Text(r));
r = Call("LeaveBonfire", MAIN, null);
Ok("leaving signs it out on that TV", r.status == 200 && !SignedIn(KIDN, "living-room"), Text(r));
Ok("and the member's own phone stays signed in", SignedIn(MAIN, "main-phone"));
fires.Remove(g1);

// ── A deleted Bonfire ────────────────────────────────────────────────────────
Section("A deleted Bonfire");

g1 = Join(OTHER, MAIN);
r = Call("SwitchProfile", MAIN, Body(("ProfileId", KIDN), ("Pin", "2468")), "living-room");
Ok("opened again", r.status == 200 && SignedIn(KIDN, "living-room"), Text(r));
r = Call("DeleteBonfireGroup", OTHER, null);
Ok("deleting the Bonfire signs it out on that TV", r.status == 200 && !SignedIn(KIDN, "living-room"), Text(r));

// ── The owner's home-network consent ─────────────────────────────────────────
Section("Home-network consent");

SetPin(KIDN, "2468");
mapType.GetProperty("AllowHouseholdLanBypass").SetValue(Row(OTHER), true);
g1 = Join(OTHER, MAIN);
r = Call("SwitchProfile", MAIN, Body(("ProfileId", KIDN)), "living-room");
Ok("with the owner's consent a member opens a PIN-protected profile on the network without it", r.status == 200 && SignedIn(KIDN, "living-room"), Text(r));
r = Call("UpdateBonfireSettings", OTHER, Body(("HideMySubProfilesFromOthers", false), ("HideOthersSubProfilesFromMe", false)));
Ok("a settings change that leaves the consent on signs nobody out", r.status == 200 && SignedIn(KIDN, "living-room"), Text(r));
r = Call("UpdateBonfireSettings", OTHER, Body(("HideMySubProfilesFromOthers", false), ("HideOthersSubProfilesFromMe", false), ("AllowHouseholdLanBypass", (bool?)false)));
Ok("withdrawing the consent signs out what came in on it alone", r.status == 200 && !SignedIn(KIDN, "living-room"), Text(r));
fires.Remove(g1);

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
    public List<SessionInfo> Sessions = new();
    public List<Device> Rows = new();
    public List<Guid> Ended = new();
    public string Refusal;
    public bool DeviceRefusal;
    public ISessionManager SessionManager;

    public void Add(Guid id, string name)
    {
        var u = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty("Username").SetValue(u, name);
        typeof(User).GetProperty("Id").SetValue(u, id);
        typeof(User).GetProperty("AuthenticationProviderId").SetValue(u, "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider");
        Users[id] = u;
    }

    public void SetMax(Guid id, int max) => typeof(User).GetProperty("MaxActiveSessions").SetValue(Users[id], max);

    public void Reset() { Sessions.Clear(); Rows.Clear(); Ended.Clear(); Refusal = null; }

    // SessionManager.GetSessionInfo: one session per app + device id, whoever it was.
    public SessionInfo Open(Guid user, string app, string deviceId)
    {
        var s = Sessions.FirstOrDefault(x => x.Client + x.DeviceId == app + deviceId);
        if (s == null)
        {
            s = new SessionInfo(SessionManager, NullLogger.Instance) { Client = app, DeviceId = deviceId, Id = Guid.NewGuid().ToString("N") };
            Sessions.Add(s);
        }
        s.UserId = user;
        return s;
    }

    public void End(string sessionId)
    {
        var s = Sessions.FirstOrDefault(x => x.Id == sessionId);
        if (s == null) return;
        Ended.Add(s.UserId);
        Sessions.Remove(s);
    }

    // SessionManager.Logout(Device): the row goes, and every session on its device id.
    public void Logout(Device row)
    {
        Rows.Remove(row);
        foreach (var s in Sessions.Where(x => string.Equals(x.DeviceId, row.DeviceId, StringComparison.OrdinalIgnoreCase)).ToList()) End(s.Id);
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
            // IUserManager
            case "GetUserById":
                return W.Users.TryGetValue((Guid)args[0], out var u) ? u : null;
            case "get_Users":
                return W.Users.Values.ToList();
            case "GetUserDto":
                var user = (User)args[0];
                return new UserDto { Id = user.Id, Name = user.Username, Configuration = new UserConfiguration(),
                    Policy = new UserPolicy { EnableAllFolders = true, AuthenticationProviderId = "x", PasswordResetProviderId = "x" } };

            // ISessionManager
            case "get_Sessions":
                return W.Sessions.ToList();
            case "ReportSessionEnded":
                W.End((string)args[0]);
                return default(ValueTask);
            case "Logout" when args[0] is Device d:
                W.Logout(d);
                return Task.CompletedTask;
            case "AuthenticateDirect":
                return Authenticate((AuthenticationRequest)args[0]);

            // IDeviceManager
            case "GetDevices":
                var q = (DeviceQuery)args[0];
                var rows = W.Rows.Where(x => (q.UserId is not Guid qu || x.UserId == qu)
                    && (string.IsNullOrEmpty(q.DeviceId) || x.DeviceId == q.DeviceId)).ToList();
                return new QueryResult<Device>(rows);
            case "DeleteDevice":
                W.Rows.Remove((Device)args[0]);
                return Task.CompletedTask;

            // INetworkManager
            case "IsInLocalNetwork":
                return true;
        }
        return Default(m.ReturnType);
    }

    // AuthenticateNewSessionInternal, in its order.
    private Task<MediaBrowser.Controller.Authentication.AuthenticationResult> Authenticate(AuthenticationRequest req)
    {
        var user = W.Users[req.UserId];
        if (W.DeviceRefusal)
            return Task.FromException<MediaBrowser.Controller.Authentication.AuthenticationResult>(
                new MediaBrowser.Controller.Net.SecurityException("User is not allowed access from this device."));
        var count = W.Sessions.Count(i => i.UserId.Equals(user.Id));
        var max = user.MaxActiveSessions;
        if (max >= 1 && count >= max)
        {
            W.Refusal = "Current/Max sessions " + count + "/" + max;
            return Task.FromException<MediaBrowser.Controller.Authentication.AuthenticationResult>(
                new MediaBrowser.Controller.Net.SecurityException("User is at their maximum number of sessions."));
        }
        foreach (var row in W.Rows.Where(x => x.DeviceId == req.DeviceId && x.UserId == user.Id).ToList()) W.Logout(row);
        var created = new Device(user.Id, req.App, req.AppVersion, req.DeviceName, req.DeviceId);
        W.Rows.Add(created);
        W.Open(user.Id, req.App, req.DeviceId);
        return Task.FromResult(new MediaBrowser.Controller.Authentication.AuthenticationResult { AccessToken = created.AccessToken });
    }

    private static object Default(Type t)
    {
        if (t == typeof(void)) return null;
        if (t == typeof(Task)) return Task.CompletedTask;
        if (t == typeof(ValueTask)) return default(ValueTask);
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var inner = t.GetGenericArguments()[0];
            return typeof(Task).GetMethod("FromResult").MakeGenericMethod(inner).Invoke(null, new[] { Value(inner) });
        }
        return Value(t);
    }

    private static object Value(Type t)
    {
        if (t.IsValueType) return Activator.CreateInstance(t);
        if (t.IsArray) return Array.CreateInstance(t.GetElementType(), 0);
        if (t.IsGenericType)
        {
            var def = t.GetGenericTypeDefinition();
            if (def == typeof(List<>) || def == typeof(IEnumerable<>) || def == typeof(IReadOnlyList<>)
                || def == typeof(IList<>) || def == typeof(IReadOnlyCollection<>) || def == typeof(ICollection<>))
                return Activator.CreateInstance(typeof(List<>).MakeGenericType(t.GetGenericArguments()[0]));
        }
        return null;
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
