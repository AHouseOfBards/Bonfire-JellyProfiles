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
// Issue #41: with MaxActiveSessions set, a profile's leftover sessions on the same device
// made /switch fail, and the page reloaded into the picker in a loop.
//
// What Jellyfin does (release-10.11.z, SessionManager.AuthenticateNewSessionInternal), and
// what the stub below does exactly:
//
//   int sessionsCount = Sessions.Count(i => i.UserId.Equals(user.Id));
//   if (maxActiveSessions >= 1 && sessionsCount >= maxActiveSessions)
//       throw new SecurityException("User is at their maximum number of sessions.");
//   var token = await GetAuthorizationToken(...)   // only now: Logout(row) for this
//                                                  // (user, deviceId), which ends every
//                                                  // session on that deviceId
//   LogSessionActivity(...)                        // session keyed by app + deviceId
//
// So the session being replaced counts against the limit first. And Jellyfin for Android
// signs the profile in under base + mainId from the picker, while the profile's own
// requests after the reload carry base + profileId: two sessions for one phone, and
// another with each restart.
//
// This holds the plugin to: before the switch, the profile's own sessions and device rows
// on this physical device are ended. Another account's session on the same device, and
// the profile's own session on another device, are left alone.
//
// Against 7455aa6 the switch fails at the limit.
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
const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

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

const string ANDROID = "Jellyfin Android";
const string BASE = "6044d6840404e4e8";   // an ANDROID_ID, as in the report
string Appended(Guid user) => BASE + user;  // Jellyfin for Android: base + userId, no separator

(int status, object value) Switch(Guid caller, Guid target, string app, string deviceId)
{
    var r = ctrlType.GetMethod("SwitchProfile");
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
    http.Request.Headers["Authorization"] = "MediaBrowser Client=\"" + app + "\", Device=\"Pixel\", DeviceId=\"" + deviceId + "\", Version=\"2.7.3\"";
    c.ControllerContext = new ControllerContext { HttpContext = http };

    var req = Activator.CreateInstance(r.GetParameters()[0].ParameterType);
    req.GetType().GetProperty("ProfileId").SetValue(req, target);

    object result;
    try
    {
        result = r.Invoke(c, new[] { req });
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
string Who(Guid id) => id == MAIN ? "main" : id == KID ? "kid" : id == OTHER ? "other" : "?";
string Live() => string.Join(", ", world.Sessions.Select(s => Who(s.UserId) + "@" + s.DeviceId.Replace(BASE, "base").Replace(MAIN.ToString(), "+main").Replace(KID.ToString(), "+kid")));

// ── 1. The report: Jellyfin for Android, after a few restarts ────────────────
Section("Android, MaxActiveSessions 2, after restarts");

world.SetMax(KID, 2);
// Left by earlier switches and reloads: the profile under the picker's id (with its token
// row) and under its own id (no row; deleting devices in the dashboard never ended it).
world.Open(KID, ANDROID, Appended(MAIN));
world.Rows.Add(new Device(KID, ANDROID, "2.7.3", "Pixel", Appended(MAIN)));
world.Open(KID, ANDROID, Appended(KID));
// The profile is also watching on the living-room TV: a real session elsewhere.
world.Open(KID, "Jellyfin Android TV", "living-room-tv");
world.Rows.Add(new Device(KID, "Jellyfin Android TV", "0.18", "TV", "living-room-tv"));
// The main account's own session on this phone, signed in under the plain id.
world.Open(MAIN, ANDROID, BASE);
world.Rows.Add(new Device(MAIN, ANDROID, "2.7.3", "Pixel", Appended(MAIN)));
// Someone else entirely, on another phone with its own appended id.
world.Open(OTHER, ANDROID, "aaaabbbbccccdddd" + OTHER);

Console.WriteLine("  before: " + Live());
var r = Switch(MAIN, KID, ANDROID, Appended(MAIN));
Console.WriteLine("  after:  " + Live());
Ok("the switch succeeds (it hit \"maximum number of sessions\")", r.status == 200, Text(r) + " | " + world.Refusal);
Ok("the profile's session under base + kid is gone", !world.Sessions.Any(s => s.UserId == KID && s.DeviceId == Appended(KID)), Live());
Ok("it has one session on this phone, the new one", world.Sessions.Count(s => s.UserId == KID && s.DeviceId.StartsWith(BASE)) == 1, Live());
Ok("its session on the TV is left alone", world.Sessions.Any(s => s.UserId == KID && s.DeviceId == "living-room-tv"), Live());
Ok("and so is the TV's device row", world.Rows.Any(d => d.UserId == KID && d.DeviceId == "living-room-tv"));
Ok("the main account's session on this phone is left alone", world.Sessions.Any(s => s.UserId == MAIN && s.DeviceId == BASE), Live());
Ok("and its device row", world.Rows.Any(d => d.UserId == MAIN && d.DeviceId == Appended(MAIN)));
Ok("another household's session is left alone", world.Sessions.Any(s => s.UserId == OTHER), Live());
Ok("nothing but the profile's was ended", world.Ended.All(id => id == KID), string.Join(",", world.Ended.Select(Who)));

// The app restarts and the profile's page reloads: base + kid again. Then the picker
// switches once more. It must not climb back to the limit.
world.Open(KID, ANDROID, Appended(KID));
for (var i = 0; i < 3; i++)
{
    r = Switch(MAIN, KID, ANDROID, Appended(MAIN));
    world.Open(KID, ANDROID, Appended(KID));
}
Ok("three more restarts later it still switches", r.status == 200, Text(r) + " | " + Live());
Ok("and the profile never holds more than this phone's two ids and the TV",
    world.Sessions.Count(s => s.UserId == KID) <= 3, Live());

// ── 2. One device, the same id: the session being replaced counts first ──────
Section("The same id, at a limit of 1");

world.Reset();
world.SetMax(KID, 1);
world.Open(KID, "Jellyfin Web", "browser-1");
world.Rows.Add(new Device(KID, "Jellyfin Web", "10.11.5", "Firefox", "browser-1"));
r = Switch(MAIN, KID, "Jellyfin Web", "browser-1");
Ok("re-entering the profile on the same browser succeeds", r.status == 200, Text(r) + " | " + world.Refusal);

world.Reset();
world.SetMax(KID, 1);
world.Open(KID, "Jellyfin Web", "browser-2");
r = Switch(MAIN, KID, "Jellyfin Web", "browser-1");
Ok("but a limit still holds against another device", r.status == 400, Text(r));
// Jellyfin throws MediaBrowser.Controller.Net.SecurityException, not System.Security's: the
// catch named the wrong type and every refusal read "Couldn't sign in".
Ok("and says so: too many active sessions", Text(r).Contains("too many active sessions"), Text(r));
Ok("and the other device's session was not ended to make room", world.Sessions.Any(s => s.UserId == KID && s.DeviceId == "browser-2"), Live());

world.Reset();
world.DeviceRefusal = true;
r = Switch(MAIN, KID, "Jellyfin Web", "browser-1");
Ok("a device refusal says it isn't allowed on this device", r.status == 400 && Text(r).Contains("isn't allowed on this device"), Text(r));
world.DeviceRefusal = false;

// ── 3. Which ids are one device ──────────────────────────────────────────────
Section("Which ids are one device");

var same = registry.GetMethod("IsSamePhysicalDevice", Any);
Ok("DeviceRegistry can tell", same != null);
if (same != null)
{
    var snap = cfgType.GetMethod("SnapshotForReading").Invoke(config, null);
    bool Same(string a, string b) => (bool)same.Invoke(null, new[] { a, b, snap });
    Ok("base + main and base + kid", Same(Appended(MAIN), Appended(KID)));
    Ok("base + kid and base", Same(Appended(KID), BASE));
    Ok("base and Android TV's sha1(base + \"+\" + kid)", Same(BASE, Sha1(BASE + "+" + KID)));
    Ok("not another phone", !Same(Appended(MAIN), "aaaabbbbccccdddd" + MAIN));
    Ok("not an id that ends in a stranger's id", !Same(BASE + Guid.NewGuid(), Appended(KID)));
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

static string Sha1(string s)
{
    var hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(s));
    return Convert.ToHexString(hash).ToLowerInvariant();
}

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
