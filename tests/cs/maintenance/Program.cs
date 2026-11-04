using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Entities.Security;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
using MediaBrowser.Model.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

// ─────────────────────────────────────────────────────────────────────────────
// Closed for maintenance.
//
// Drives the real middleware, request by request, and checks who is turned away:
//
//  - nobody, while it is off;
//  - never an administrator, by any path, sign-in included;
//  - a household member's every request, sign-in by name included, with 503, the
//    administrator's message, X-Bonfire-Error: maintenance and a Retry-After;
//  - except what is already playing (unless asked to stop), the account's own record,
//    the web client's files, the public routes and Bonfire's anonymous ones;
//  - a profile the picker is acting as, though the token the app sends is an
//    administrator's; and the picker will not open one;
//  - and nobody once the emergency disable is used.
//
// A sign-in that is let through must reach Jellyfin with its body intact: the gate reads
// the name out of it.
//
// Against 1.7.2's announcements commit the first assertion fails: there is no gate.
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
        d = Path.GetDirectoryName(d);
    return d ?? throw new Exception("could not find the repository root");
}

var tfm = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Name;
var asm = Assembly.LoadFrom(Path.Combine(RepoRoot(), "bin", "Release", tfm, "Jellyfin.Profiles.dll"));

var gateType = asm.GetType("Jellyfin.Profiles.Auth.MaintenanceGate");
var modeType = asm.GetType("Jellyfin.Profiles.Configuration.MaintenanceMode");
Ok("this build can close for maintenance", gateType != null && modeType != null);
if (gateType == null || modeType == null) return Finish();

var pluginType = asm.GetType("Jellyfin.Profiles.Plugin", true);
var cfgType = asm.GetType("Jellyfin.Profiles.Configuration.PluginConfiguration", true);
var mapType = asm.GetType("Jellyfin.Profiles.Configuration.ProfileMapping", true);
var mwType = asm.GetType("Jellyfin.Profiles.ProfilesIndexMiddleware", true);
var panicField = pluginType.GetField("_panicDisabled", BindingFlags.Static | BindingFlags.NonPublic);

var tempDir = Path.Combine(Path.GetTempPath(), "bonfire-maintenance-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
var plugin = Activator.CreateInstance(pluginType, new StubPaths(tempDir), new StubXml());
var config = pluginType.GetProperty("Configuration").GetValue(plugin);
void Set(string prop, object value) => cfgType.GetProperty(prop).SetValue(config, value);
void Forget() => gateType.GetMethod("Forget").Invoke(null, null);

void Close(bool on, string message = "Back at 3.", bool stop = false, DateTime? until = null)
{
    var m = Activator.CreateInstance(modeType);
    modeType.GetProperty("Enabled").SetValue(m, on);
    modeType.GetProperty("Message").SetValue(m, message);
    modeType.GetProperty("StopPlayback").SetValue(m, stop);
    modeType.GetProperty("UntilUtc").SetValue(m, until);
    Set("Maintenance", m);
    Forget();
}

// ── The server ─────────────────────────────────────────────────────────────
var ADMIN = Guid.NewGuid();
var MEMBER = Guid.NewGuid();
var KID = Guid.NewGuid();      // a profile of ADMIN's, not an administrator
var N = (Guid g) => g.ToString("N");

var users = new Dictionary<Guid, (string name, bool admin)>
{
    [ADMIN] = ("Bard", true),
    [MEMBER] = ("Guest", false),
    [KID] = ("Bard_kid", false),
};

const string ADMIN_TOKEN = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
const string MEMBER_TOKEN = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
const string MOON_TOKEN = "cccccccccccccccccccccccccccccccc";
const string API_KEY = "dddddddddddddddddddddddddddddddd";
var devices = DeviceManagerStub.Create(new Dictionary<string, (Guid, string)>
{
    [ADMIN_TOKEN] = (ADMIN, "Jellyfin Web"),
    [MEMBER_TOKEN] = (MEMBER, "Jellyfin Web"),
    [MOON_TOKEN] = (ADMIN, "Moonfin for Android TV"),
});
var sessions = SessionManagerStub.Create();
var sessionLog = (SessionManagerStub)(object)sessions;
var userManager = UserManagerStub.Create(users);

var services = new ServiceCollection()
    .AddSingleton(devices)
    .AddSingleton(sessions)
    .AddSingleton(userManager)
    .AddSingleton(NetworkStub.Create())
    .BuildServiceProvider();

string seenPath = null, seenBody = null, seenAuth = null;
var next = new RequestDelegate(async c =>
{
    seenPath = c.Request.Path.Value;
    seenAuth = c.Request.Headers["Authorization"].ToString();
    using (var r = new StreamReader(c.Request.Body, Encoding.UTF8, false, 1024, leaveOpen: true))
        seenBody = await r.ReadToEndAsync();

    var body = "{}";
    if (seenPath.EndsWith("/UserViews", StringComparison.OrdinalIgnoreCase))
        body = "{\"Items\":[{\"Name\":\"Movies\",\"Id\":\"11111111111111111111111111111111\",\"Type\":\"CollectionFolder\"}],\"TotalRecordCount\":1,\"StartIndex\":0}";
    c.Response.StatusCode = 200;
    c.Response.ContentType = "application/json";
    await c.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(body));
});
var instance = Activator.CreateInstance(mwType, next, Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(mwType)));

(int status, string body, bool reached, HttpResponse response) Send(
    string pathAndQuery, string token = null, string method = "GET", string body = null, string client = "Jellyfin Web")
{
    seenPath = seenBody = seenAuth = null;
    var ctx = new DefaultHttpContext { RequestServices = services };
    ctx.Request.Method = method;
    var q = pathAndQuery.IndexOf('?');
    ctx.Request.Path = q < 0 ? pathAndQuery : pathAndQuery[..q];
    if (q >= 0) ctx.Request.QueryString = new QueryString(pathAndQuery[q..]);
    ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
    ctx.Request.Headers["Authorization"] =
        "MediaBrowser Client=\"" + client + "\", Device=\"Shield\", DeviceId=\"dev-1\", Version=\"1.0\""
        + (token == null ? "" : ", Token=\"" + token + "\"");
    if (body != null)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentLength = bytes.Length;
        ctx.Request.ContentType = "application/json";
    }
    var sink = new MemoryStream();
    ctx.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>(new StreamResponseBodyFeature(sink));

    var invoke = mwType.GetMethod("Invoke");
    var args = invoke.GetParameters().Select(p =>
        p.ParameterType == typeof(HttpContext) ? ctx
        : p.ParameterType == typeof(IUserManager) ? userManager
        : p.ParameterType == typeof(IDeviceManager) ? devices
        : p.ParameterType == typeof(ISessionManager) ? sessions
        : p.ParameterType == typeof(INetworkManager) ? services.GetService<INetworkManager>()
        : (object)null).ToArray();
    ((Task)invoke.Invoke(instance, args)).GetAwaiter().GetResult();
    return (ctx.Response.StatusCode, Encoding.UTF8.GetString(sink.ToArray()), seenPath != null, ctx.Response);
}

bool Refused((int status, string body, bool reached, HttpResponse response) r)
    => !r.reached && r.status == 503 && r.response.Headers["X-Bonfire-Error"].ToString() == "maintenance";
bool Through((int status, string body, bool reached, HttpResponse response) r) => r.reached;
string Show((int status, string body, bool reached, HttpResponse response) r)
    => r.reached ? "reached Jellyfin" : r.status + " " + r.response.Headers["X-Bonfire-Error"] + " " + r.body;

Console.WriteLine();
Console.WriteLine("── Open ────────────────────────────────────────────────────────");

Close(false);
Ok("a household member's request goes through", Through(Send("/Users/" + N(MEMBER) + "/Items", MEMBER_TOKEN)));
Ok("and their sign-in", Through(Send("/Users/AuthenticateByName", null, "POST", "{\"Username\":\"Guest\",\"Pw\":\"x\"}")));

Console.WriteLine();
Console.WriteLine("── Closed: a household member ──────────────────────────────────");

Close(true, "Upgrading. Back at 3.", until: DateTime.UtcNow.AddMinutes(90));
var browse = Send("/Users/" + N(MEMBER) + "/Items", MEMBER_TOKEN);
Ok("browsing is refused before Jellyfin sees it", Refused(browse), Show(browse));
Ok("with the administrator's message as plain text",
   browse.body == "Upgrading. Back at 3." && browse.response.ContentType.StartsWith("text/plain"), browse.body);
var retry = int.TryParse(browse.response.Headers["Retry-After"].ToString(), out var secs) ? secs : -1;
Ok("and Retry-After counts down to the time given", retry > 5300 && retry <= 5400, retry.ToString());
Ok("and is not cached", browse.response.Headers["Cache-Control"].ToString() == "no-store");

Ok("starting playback is refused", Refused(Send("/Items/abc/PlaybackInfo", MEMBER_TOKEN, "POST", "{}")));
Ok("what is playing keeps playing: video segments", Through(Send("/videos/abc/hls1/main/3.mp4", MEMBER_TOKEN)));
Ok("audio", Through(Send("/Audio/abc/stream.mp3", MEMBER_TOKEN)));
Ok("and progress reports", Through(Send("/Sessions/Playing/Progress", MEMBER_TOKEN, "POST", "{}")));
Ok("the account's own record still answers (/Users/Me)", Through(Send("/Users/Me", MEMBER_TOKEN)));
Ok("and by id", Through(Send("/Users/" + N(MEMBER), MEMBER_TOKEN)));
Ok("but not someone else's", Refused(Send("/Users/" + N(ADMIN), MEMBER_TOKEN)));
Ok("signing out works", Through(Send("/Sessions/Logout", MEMBER_TOKEN, "POST", "")));
Ok("the live connection is kept, so open apps get the message", Through(Send("/socket", MEMBER_TOKEN)));
Ok("the web client's files load", Through(Send("/web/index.html")) && Through(Send("/web/main.bundle.js", MEMBER_TOKEN)));
Ok("public server info answers", Through(Send("/System/Info/Public")));
Ok("Bonfire's /info answers, to say why", Through(Send("/plugins/profiles/info", MEMBER_TOKEN)));
Ok("and its script", Through(Send("/plugins/profiles/profiles.js", MEMBER_TOKEN)));
Ok("but its other routes do not", Refused(Send("/plugins/profiles/list", MEMBER_TOKEN)));
Ok("a request with no token is left to Jellyfin", Through(Send("/Users/" + N(MEMBER) + "/Items")));

var signIn = Send("/Users/AuthenticateByName", null, "POST", "{\"Username\":\"Guest\",\"Pw\":\"x\"}");
Ok("signing in by name is refused, before the password is checked", Refused(signIn), Show(signIn));
Ok("whatever case the name's field is in", Refused(Send("/Users/AuthenticateByName", null, "POST", "{\"username\":\"Guest\",\"pw\":\"x\"}")));
Ok("and by id", Refused(Send("/Users/" + N(MEMBER) + "/Authenticate?pw=x", null, "POST", "")));
Ok("a name nobody has is left to Jellyfin", Through(Send("/Users/AuthenticateByName", null, "POST", "{\"Username\":\"Nobody\",\"Pw\":\"x\"}")));
Ok("so is a body that is not JSON", Through(Send("/Users/AuthenticateByName", null, "POST", "Username=Guest")));
Ok("Quick Connect is left alone (the account is not known yet)", Through(Send("/QuickConnect/Initiate", null, "POST", "")));

Console.WriteLine();
Console.WriteLine("── Closed: an administrator ────────────────────────────────────");

Ok("browses", Through(Send("/Users/" + N(ADMIN) + "/Items", ADMIN_TOKEN)));
Ok("starts playback", Through(Send("/Items/abc/PlaybackInfo", ADMIN_TOKEN, "POST", "{}")));
Ok("manages Bonfire", Through(Send("/plugins/profiles/admin/maintenance", ADMIN_TOKEN, "POST", "{\"enabled\":false}")));
var adminIn = Send("/Users/AuthenticateByName", null, "POST", "{\"Username\":\"Bard\",\"Pw\":\"secret\"}");
Ok("signs in", Through(adminIn));
Ok("and Jellyfin reads the same body the gate read", seenBody == "{\"Username\":\"Bard\",\"Pw\":\"secret\"}", seenBody);
Ok("an API key (no account) is left alone", Through(Send("/Users/" + N(MEMBER) + "/Items", API_KEY)));

Console.WriteLine();
Console.WriteLine("── Asked to stop playback ──────────────────────────────────────");

Close(true, "", stop: true);
var seg = Send("/videos/abc/hls1/main/4.mp4", MEMBER_TOKEN);
Ok("video segments are refused too", Refused(seg), Show(seg));
Ok("an empty message gives the default", seg.body == "The server is closed for maintenance.", seg.body);
Ok("with no end time, Retry-After is five minutes", seg.response.Headers["Retry-After"].ToString() == "300");
Ok("an administrator still streams", Through(Send("/videos/abc/hls1/main/4.mp4", ADMIN_TOKEN)));

Console.WriteLine();
Console.WriteLine("── Cost ────────────────────────────────────────────────────────");

Close(true);
DeviceManagerStub.Calls = 0;
for (int i = 0; i < 50; i++) Send("/videos/abc/hls1/main/" + i + ".mp4", MEMBER_TOKEN);
Ok("fifty segments look the token up once", DeviceManagerStub.Calls == 1, DeviceManagerStub.Calls.ToString());
Close(false);
DeviceManagerStub.Calls = 0;
for (int i = 0; i < 50; i++) Send("/Users/" + N(MEMBER) + "/Items", MEMBER_TOKEN);
Ok("while open, never", DeviceManagerStub.Calls == 0, DeviceManagerStub.Calls.ToString());

Console.WriteLine();
Console.WriteLine("── A profile on the picker ─────────────────────────────────────");

// ADMIN's household, on Moonfin, with the kid's profile open: the app's token is the
// administrator's, the requests reach Jellyfin as the kid's.
var mappings = (System.Collections.IList)cfgType.GetProperty("Mappings").GetValue(config);
var map = Activator.CreateInstance(mapType);
mapType.GetProperty("ProfileUserId").SetValue(map, KID);
mapType.GetProperty("MasterUserId").SetValue(map, ADMIN);
mapType.GetProperty("ProfileName").SetValue(map, "Kid");
mappings.Add(map);
Set("ProxyClients", new List<string> { "moonfin" });

JsonArray Items(string body) => (JsonNode.Parse(body) as JsonObject)?["Items"] as JsonArray ?? new JsonArray();
string IdOf(string body, string name) => (string)Items(body).FirstOrDefault(i => (string)i["Name"] == name)?["Id"];
(int status, string body, bool reached, HttpResponse response) Moon(string p) => Send(p, MOON_TOKEN, client: "Moonfin for Android TV");

Close(false);
var views = Moon("/UserViews");
var tiles = Moon("/Users/" + N(ADMIN) + "/Items?ParentId=" + IdOf(views.body, "Profiles")).body;
var kidTile = IdOf(tiles, "Kid");
Moon("/Users/" + N(ADMIN) + "/Items?ParentId=" + kidTile);
Ok("(the kid's profile is open)", sessionLog.Minted.Contains(KID), string.Join(",", sessionLog.Minted));

Close(true);
var seg9 = Moon("/videos/abc/hls1/main/9.mp4");
Ok("what the kid is watching keeps playing, as the kid", Through(seg9) && seenAuth.Contains("tok-" + N(KID)), seenAuth);

var kidBrowse = Moon("/Users/" + N(ADMIN) + "/Items?ParentId=11111111111111111111111111111111");
Ok("once closed, browsing never reaches Jellyfin as the kid, though the app's token is an administrator's",
   !(seenAuth ?? "").Contains("tok-" + N(KID)), seenAuth);
Ok("the kid's session is ended", sessionLog.LoggedOut.Contains("tok-" + N(KID)), string.Join(",", sessionLog.LoggedOut));
var home = Moon("/UserViews");
Ok("and the app is back at the picker", Items(home.body).Any(i => (string)i["Name"] == "Profiles"), home.body);

// Try to open the kid again.
var tiles2 = Moon("/Users/" + N(ADMIN) + "/Items?ParentId=" + IdOf(home.body, "Profiles")).body;
var minted = sessionLog.Minted.Count;
var again = Moon("/Users/" + N(ADMIN) + "/Items?ParentId=" + IdOf(tiles2, "Kid"));
Ok("the picker will not open the kid's profile", sessionLog.Minted.Count == minted, string.Join(",", sessionLog.Minted));
Ok("and says why", Items(again.body).Any(i => ((string)i["Name"] ?? "").Contains("maintenance")), again.body);

Console.WriteLine();
Console.WriteLine("── Turning it on tells people ──────────────────────────────────");

sessionLog.Sessions.Clear();
sessionLog.Sessions.Add(SessionManagerStub.Make("s-admin", ADMIN, playing: true));
sessionLog.Sessions.Add(SessionManagerStub.Make("s-member", MEMBER, playing: true));
sessionLog.Sessions.Add(SessionManagerStub.Make("s-kid", KID, playing: false));
sessionLog.Sessions.Add(SessionManagerStub.Make("s-anon", Guid.Empty, playing: false));

var mode = cfgType.GetProperty("Maintenance").GetValue(config);
var notify = gateType.GetMethod("NotifyAsync");
var told = ((Task)notify.Invoke(null, new object[] { sessions, userManager, mode, false, NullLogger.Instance }));
told.GetAwaiter().GetResult();
Ok("every household member's open app gets the message", sessionLog.Messaged.OrderBy(s => s).SequenceEqual(new[] { "s-kid", "s-member" }),
   string.Join(",", sessionLog.Messaged));
Ok("an administrator's does not", !sessionLog.Messaged.Contains("s-admin"));
Ok("nothing is stopped unless asked", sessionLog.Stopped.Count == 0);
sessionLog.Messaged.Clear();
((Task)notify.Invoke(null, new object[] { sessions, userManager, mode, true, NullLogger.Instance })).GetAwaiter().GetResult();
Ok("asked to stop, only what a household member is playing stops", sessionLog.Stopped.SequenceEqual(new[] { "s-member" }),
   string.Join(",", sessionLog.Stopped));

Console.WriteLine();
Console.WriteLine("── Emergency disable ───────────────────────────────────────────");

Close(true);
panicField.SetValue(null, true);
Ok("turns the gate off", Through(Send("/Users/" + N(MEMBER) + "/Items", MEMBER_TOKEN)));
panicField.SetValue(null, false);
Ok("(and it is back after a restart)", Refused(Send("/Users/" + N(MEMBER) + "/Items", MEMBER_TOKEN)));

Console.WriteLine();
Console.WriteLine("── GET /info ───────────────────────────────────────────────────");

var ctrlType = asm.GetType("Jellyfin.Profiles.Controllers.ProfilesController", true);
var ctor = ctrlType.GetConstructors().First();
var ctorArgs = ctor.GetParameters().Select(p => p.ParameterType.Name.StartsWith("ILogger")
    ? Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(ctrlType)) : null).ToArray();
string Info()
{
    var c = (ControllerBase)ctor.Invoke(ctorArgs);
    c.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
    var r = ctrlType.GetMethod("GetInfo").Invoke(c, null);
    return JsonSerializer.Serialize((r.GetType().GetProperty("Result")?.GetValue(r) as ObjectResult)?.Value);
}
var until = new DateTime(2026, 11, 5, 3, 0, 0, DateTimeKind.Utc);
Close(true, "Upgrading.", until: until);
var info = JsonNode.Parse(Info());
Ok("says it is closed, with the message and the time, to anyone",
   (string)info?["Maintenance"]?["Message"] == "Upgrading." && info?["Maintenance"]?["UntilUtc"] != null, info?.ToJsonString());
Close(false);
info = JsonNode.Parse(Info());
Ok("and null once open", info is JsonObject o && o.ContainsKey("Maintenance") && o["Maintenance"] == null, info?.ToJsonString());

try { Directory.Delete(tempDir, true); } catch { }
return Finish();

int Finish()
{
    Console.WriteLine();
    if (fails.Count > 0)
    {
        foreach (var f in fails) Console.WriteLine("   - " + f);
        Console.WriteLine(pass + " passed, " + fails.Count + " failed");
        return 1;
    }
    Console.WriteLine(pass + " passed, 0 failed");
    return 0;
}

// ── Stubs ──────────────────────────────────────────────────────────────────

public class SessionManagerStub : DispatchProxy
{
    public List<Guid> Minted = new();
    public List<string> LoggedOut = new();
    public List<SessionInfo> Sessions = new();
    public List<string> Messaged = new();
    public List<string> Stopped = new();

    public static ISessionManager Create() => Create<ISessionManager, SessionManagerStub>();

    public static SessionInfo Make(string id, Guid user, bool playing)
    {
        var t = typeof(SessionInfo);
        var s = (SessionInfo)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t);
        t.GetProperty("Id").SetValue(s, id);
        t.GetProperty("UserId").SetValue(s, user);
        t.GetProperty("NowPlayingItem").SetValue(s, playing ? new BaseItemDto() : null);
        return s;
    }

    protected override object Invoke(MethodInfo m, object[] args)
    {
        switch (m.Name)
        {
            case "AuthenticateDirect":
                var r = (AuthenticationRequest)args[0];
                Minted.Add(r.UserId);
                return Task.FromResult(new AuthenticationResult { AccessToken = "tok-" + r.UserId.ToString("N") });
            case "Logout" when args.Length == 1 && args[0] is string token:
                LoggedOut.Add(token);
                return Task.CompletedTask;
            case "get_Sessions":
                return Sessions;
            case "SendMessageCommand":
                Messaged.Add((string)args[1]);
                return Task.CompletedTask;
            case "SendPlaystateCommand":
                if (((PlaystateRequest)args[2]).Command == PlaystateCommand.Stop) Stopped.Add((string)args[1]);
                return Task.CompletedTask;
        }

        throw new NotSupportedException("ISessionManager." + m.Name + " is not modelled");
    }
}

public class DeviceManagerStub : DispatchProxy
{
    public static int Calls;
    private Dictionary<string, (Guid user, string app)> _tokens;

    public static IDeviceManager Create(Dictionary<string, (Guid, string)> tokens)
    {
        var proxy = Create<IDeviceManager, DeviceManagerStub>();
        ((DeviceManagerStub)(object)proxy)._tokens = tokens;
        return proxy;
    }

    protected override object Invoke(MethodInfo m, object[] args)
    {
        if (m.Name != "GetDevices") throw new NotSupportedException("IDeviceManager." + m.Name + " is not modelled");

        Calls++;
        var q = args[0];
        var token = (string)q.GetType().GetProperty("AccessToken")?.GetValue(q);
        var items = new List<Device>();
        if (token != null && _tokens.TryGetValue(token, out var row))
        {
            var d = (Device)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Device));
            void P(string name, object v) => typeof(Device).GetProperty(name).SetValue(d, v);
            P("UserId", row.user);
            P("AppName", row.app);
            P("AppVersion", "1.4.0");
            P("DeviceName", "Shield");
            P("DeviceId", "dev-1");
            items.Add(d);
        }

        return new QueryResult<Device>(items);
    }
}

public class UserManagerStub : DispatchProxy
{
    private Dictionary<Guid, (string name, bool admin)> _users;

    public static IUserManager Create(Dictionary<Guid, (string, bool)> users)
    {
        var proxy = Create<IUserManager, UserManagerStub>();
        ((UserManagerStub)(object)proxy)._users = users;
        return proxy;
    }

    private User Make(Guid id, string name)
    {
        var u = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty("Username").SetValue(u, name);
        typeof(User).GetProperty("Id").SetValue(u, id);
        return u;
    }

    protected override object Invoke(MethodInfo m, object[] args)
    {
        switch (m.Name)
        {
            case "GetUserById":
                return _users.TryGetValue((Guid)args[0], out var row) ? Make((Guid)args[0], row.name) : null;
            case "GetUserByName":
                var hit = _users.FirstOrDefault(kv => string.Equals(kv.Value.name, (string)args[0], StringComparison.OrdinalIgnoreCase));
                return hit.Key == Guid.Empty ? null : Make(hit.Key, hit.Value.name);
            case "GetUserDto":
                var user = (User)args[0];
                var admin = _users.TryGetValue(user.Id, out var r2) && r2.admin;
                return new UserDto { Policy = new UserPolicy { IsAdministrator = admin } };
        }

        throw new NotSupportedException("IUserManager." + m.Name + " is not modelled");
    }
}

public class NetworkStub : DispatchProxy
{
    public static INetworkManager Create() => Create<INetworkManager, NetworkStub>();

    protected override object Invoke(MethodInfo m, object[] args)
        => m.Name == "IsInLocalNetwork" ? false
           : throw new NotSupportedException("INetworkManager." + m.Name + " is not modelled");
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
