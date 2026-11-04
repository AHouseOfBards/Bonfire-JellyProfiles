using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
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
using MediaBrowser.Model.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

// ─────────────────────────────────────────────────────────────────────────────
// The profile picker for apps with no switcher (Moonfin first).
//
// The household signs into its main account; the app's home shows a "Profiles" library
// with a folder per profile; opening one opens that profile, through a keypad of folders
// when it has a PIN. From then on every request on the main account's token reaches
// Jellyfin as the profile's.
//
// This drives the real middleware, request by request, as Moonfin would, and checks
// what Jellyfin is handed and what the app is told. Against 1.7.1.1 the first
// assertion fails: the app is shown the main account's libraries.
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

var pluginType = asm.GetType("Jellyfin.Profiles.Plugin", true);
var cfgType = asm.GetType("Jellyfin.Profiles.Configuration.PluginConfiguration", true);
var mapType = asm.GetType("Jellyfin.Profiles.Configuration.ProfileMapping", true);
var mwType = asm.GetType("Jellyfin.Profiles.ProfilesIndexMiddleware", true);
var hasher = asm.GetType("Jellyfin.Profiles.Auth.PinHasher", true);
var switcher = asm.GetType("Jellyfin.Profiles.Auth.ProxySwitcher");
var panicField = pluginType.GetField("_panicDisabled", BindingFlags.Static | BindingFlags.NonPublic);

var tempDir = Path.Combine(Path.GetTempPath(), "bonfire-proxyswitcher-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
var plugin = Activator.CreateInstance(pluginType, new StubPaths(tempDir), new StubXml());
var config = pluginType.GetProperty("Configuration").GetValue(plugin);
void Set(string prop, object value) => cfgType.GetProperty(prop)?.SetValue(config, value);

// ── The household ──────────────────────────────────────────────────────────
var MASTER = Guid.NewGuid();
var KIDS = Guid.NewGuid();   // PIN 1234
var OPEN = Guid.NewGuid();   // no PIN
var N = (Guid g) => g.ToString("N");

object Map(Guid profile, Guid master, string name, string pin = null)
{
    var m = Activator.CreateInstance(mapType);
    mapType.GetProperty("ProfileUserId").SetValue(m, profile);
    mapType.GetProperty("MasterUserId").SetValue(m, master);
    mapType.GetProperty("ProfileName").SetValue(m, name);
    mapType.GetProperty("PinHash").SetValue(m, pin == null ? null : hasher.GetMethod("Hash").Invoke(null, new object[] { pin }));
    return m;
}

var mappings = (System.Collections.IList)cfgType.GetProperty("Mappings").GetValue(config);
mappings.Add(Map(KIDS, MASTER, "Kids", pin: "1234"));
mappings.Add(Map(OPEN, MASTER, "Open"));
Set("ProxyClients", new List<string> { "moonfin" });

var usernames = new Dictionary<Guid, string> { [MASTER] = "Bard", [KIDS] = "Bard_kids", [OPEN] = "Bard_open" };

const string MOON_TOKEN = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
const string ATV_TOKEN = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
var devices = DeviceManagerStub.Create(new Dictionary<string, (Guid, string)>
{
    [MOON_TOKEN] = (MASTER, "Moonfin for Android TV"),
    [ATV_TOKEN] = (MASTER, "Jellyfin Android TV"),
});
var sessions = SessionManagerStub.Create();
var sessionLog = (SessionManagerStub)(object)sessions;

var services = new ServiceCollection()
    .AddSingleton(devices)
    .AddSingleton(sessions)
    .AddSingleton(UserManagerStub.Create(usernames))
    .AddSingleton(NetworkStub.Create())
    .BuildServiceProvider();

// What Jellyfin was handed, for the last request that reached it.
string seenPath = null, seenQuery = null, seenAuth = null;

var next = new RequestDelegate(async c =>
{
    seenPath = c.Request.Path.Value;
    seenQuery = c.Request.QueryString.Value;
    seenAuth = c.Request.Headers["Authorization"].ToString();

    if (seenPath.EndsWith("/Revoked")) { c.Response.StatusCode = 401; return; }

    string body = "{}";
    if (seenPath.EndsWith("/UserViews", StringComparison.OrdinalIgnoreCase))
        body = "{\"Items\":[{\"Name\":\"Movies\",\"Id\":\"11111111111111111111111111111111\",\"Type\":\"CollectionFolder\"}],\"TotalRecordCount\":1,\"StartIndex\":0}";
    else if (seenPath.Contains("/Users/", StringComparison.OrdinalIgnoreCase) && !seenPath.EndsWith("/Items", StringComparison.OrdinalIgnoreCase))
    {
        var who = usernames.Keys.FirstOrDefault(k => c.Request.Headers["Authorization"].ToString().Contains("tok-" + N(k)));
        if (who == Guid.Empty) who = MASTER;
        body = "{\"Id\":\"" + N(who) + "\",\"Name\":\"" + usernames[who] + "\",\"Policy\":{}}";
    }

    c.Response.StatusCode = 200;
    c.Response.ContentType = "application/json";
    await c.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(body));
});

var instance = Activator.CreateInstance(mwType, next, Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(mwType)));

// One request through the real middleware. Returns the body the app got, or null when
// Jellyfin answered it (seenPath says what Jellyfin saw).
(int status, string body, bool reachedJellyfin) Get(string pathAndQuery, string token = MOON_TOKEN, string client = "Moonfin for Android TV", string method = "GET")
{
    seenPath = seenQuery = seenAuth = null;
    var ctx = new DefaultHttpContext { RequestServices = services };
    ctx.Request.Method = method;
    var q = pathAndQuery.IndexOf('?');
    ctx.Request.Path = q < 0 ? pathAndQuery : pathAndQuery[..q];
    if (q >= 0) ctx.Request.QueryString = new QueryString(pathAndQuery[q..]);
    ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
    ctx.Request.Headers["Authorization"] =
        "MediaBrowser Client=\"" + client + "\", Device=\"Shield\", DeviceId=\"moon-1\", Version=\"1.4.0\", Token=\"" + token + "\"";
    var sink = new MemoryStream();
    ctx.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>(new StreamResponseBodyFeature(sink));

    var invoke = mwType.GetMethod("Invoke");
    var args = invoke.GetParameters().Select(p =>
        p.ParameterType == typeof(HttpContext) ? ctx
        : p.ParameterType == typeof(IUserManager) ? services.GetService<IUserManager>()
        : p.ParameterType == typeof(IDeviceManager) ? devices
        : p.ParameterType == typeof(ISessionManager) ? sessions
        : p.ParameterType == typeof(INetworkManager) ? services.GetService<INetworkManager>()
        : (object)null).ToArray();
    ((Task)invoke.Invoke(instance, args)).GetAwaiter().GetResult();
    return (ctx.Response.StatusCode, Encoding.UTF8.GetString(sink.ToArray()), seenPath != null);
}

JsonArray Items(string body) => (JsonNode.Parse(body) as JsonObject)?["Items"] as JsonArray ?? new JsonArray();
string Names(string body) => string.Join(", ", Items(body).Select(i => (string)i["Name"]));
string IdOf(string body, string name) => (string)Items(body).FirstOrDefault(i => (string)i["Name"] == name)?["Id"];
string Children(string id) => Get("/Users/" + N(MASTER) + "/Items?ParentId=" + id + "&Recursive=false").body;

Console.WriteLine();
Console.WriteLine("── Signed in as the main account: the picker ───────────────────");

var views = Get("/UserViews");
Ok("the home screen shows only the Profiles library", !views.reachedJellyfin && Names(views.body) == "Profiles",
   views.reachedJellyfin ? "Jellyfin answered: the main account's libraries" : Names(views.body));

var resume = Get("/UserItems/Resume?Limit=12");
Ok("Continue Watching is empty, not the main account's", !resume.reachedJellyfin && Items(resume.body).Count == 0, resume.body);

var latest = Get("/Users/" + N(MASTER) + "/Items/Latest?ParentId=x");
Ok("Latest is empty", !latest.reachedJellyfin && latest.body == "[]", latest.body);

var info = Get("/System/Info");
Ok("server information still comes from Jellyfin, as the main account",
   info.reachedJellyfin && seenAuth.Contains(MOON_TOKEN), seenAuth);

var profilesId = IdOf(views.body, "Profiles");
var tiles = Children(profilesId);
Ok("opening Profiles lists the main account and its profiles", Names(tiles) == "Bard, Kids, Open", Names(tiles));
Ok("listing them opens nothing", sessionLog.Minted.Count == 0, string.Join(",", sessionLog.Minted));

var item = Get("/Users/" + N(MASTER) + "/Items/" + profilesId);
Ok("the Profiles library answers its own details", (string)JsonNode.Parse(item.body)?["Name"] == "Profiles", item.body);

Console.WriteLine();
Console.WriteLine("── Opening a profile with no PIN ───────────────────────────────");

var openId = IdOf(tiles, "Open");
var opened = Get("/Users/" + N(MASTER) + "/Items?ParentId=" + openId);
Ok("opening the folder signs into that profile", sessionLog.Minted.SequenceEqual(new[] { OPEN }), string.Join(",", sessionLog.Minted));
Ok("with the app's own device, so Jellyfin lists it as this television",
   sessionLog.Requests.LastOrDefault()?.DeviceId == "moon-1" && sessionLog.Requests.Last().App == "Moonfin for Android TV");
Ok("and the folder shows the profile's libraries, from Jellyfin, as the profile",
   opened.reachedJellyfin && seenPath.EndsWith("/UserViews") && seenAuth.Contains("tok-" + N(OPEN)) && Names(opened.body) == "Movies",
   seenPath + " " + seenAuth + " " + opened.body);

Get("/Users/" + N(MASTER) + "/Items?ParentId=" + openId);
Ok("the app's second load of the same folder does not sign in twice", sessionLog.Minted.Count == 1, string.Join(",", sessionLog.Minted));

Console.WriteLine();
Console.WriteLine("── Using the profile ───────────────────────────────────────────");

var browse = Get("/Users/" + N(MASTER) + "/Items?ParentId=11111111111111111111111111111111&UserId=" + N(MASTER));
Ok("a request on the main token reaches Jellyfin with the profile's token", seenAuth.Contains("tok-" + N(OPEN)) && !seenAuth.Contains(MOON_TOKEN), seenAuth);
Ok("and the profile's id in the path", seenPath == "/Users/" + N(OPEN) + "/Items", seenPath);
Ok("and in the query", seenQuery.Contains("UserId=" + N(OPEN)) && !seenQuery.Contains(N(MASTER)), seenQuery);

var home = Get("/UserViews");
Ok("the home screen shows the profile's libraries and Switch profile", Names(home.body) == "Movies, Switch profile", Names(home.body));

var me = Get("/Users/Me");
var meObj = JsonNode.Parse(me.body) as JsonObject;
Ok("the app is told the main account's id, so nothing it stored goes stale", (string)meObj?["Id"] == N(MASTER), me.body);
Ok("and the profile's own name", (string)meObj?["Name"] == "Open", me.body);

var byId = Get("/Users/" + N(MASTER));
Ok("the same for its user record by id", (string)(JsonNode.Parse(byId.body) as JsonObject)?["Id"] == N(MASTER), byId.body);

Console.WriteLine();
Console.WriteLine("── Switching back ──────────────────────────────────────────────");

var back = Children(IdOf(home.body, "Switch profile"));
Ok("Switch profile signs the profile out", sessionLog.LoggedOut.Contains("tok-" + N(OPEN)), string.Join(",", sessionLog.LoggedOut));
Ok("and lists the profiles again", Names(back) == "Bard, Kids, Open", Names(back));
var again = Get("/UserViews");
Ok("the home screen is the picker again", !again.reachedJellyfin && Names(again.body) == "Profiles", Names(again.body));

Console.WriteLine();
Console.WriteLine("── A profile with a PIN: the keypad ────────────────────────────");

var kidsId = IdOf(back, "Kids");
var pad = Children(kidsId);
Ok("opening it shows a keypad, and signs nothing in",
   Names(pad) == "1, 2, 3, 4, 5, 6, 7, 8, 9, 0, Delete, OK" && sessionLog.Minted.Count == 1, Names(pad));

string Press(string padBody, string key) => Children(IdOf(padBody, key));
var p1 = Press(pad, "1");
var p1again = Children(IdOf(pad, "1"));
Ok("the app's second load of a key is the same press, not another digit", Names(p1) == Names(p1again) && Names(p1).EndsWith("OK •"), Names(p1again));
var p4 = Press(Press(Press(p1, "2"), "3"), "4");
Ok("four presses show four dots", Names(p4).EndsWith("OK ••••"), Names(p4));
var pDel = Press(p4, "Delete");
Ok("Delete takes one off", Names(pDel).EndsWith("OK •••"), Names(pDel));

var wrong = Press(Press(pDel, "9"), "OK ••••");
Ok("a wrong PIN says so and signs nothing in", Names(wrong).StartsWith("Wrong PIN") && sessionLog.Minted.Count == 1, Names(wrong));

var right = Press(Press(Press(Press(Press(wrong, "1"), "2"), "3"), "4"), "OK ••••");
Ok("the right PIN opens the profile", sessionLog.Minted.LastOrDefault() == KIDS && Names(right) == "Movies", Names(right));
var kidsHome = Get("/UserViews");
Ok("and the app is now that profile", kidsHome.reachedJellyfin && seenAuth.Contains("tok-" + N(KIDS)), seenAuth);
var kidsMe = Get("/Users/Me");
Ok("its name is the profile's", (string)(JsonNode.Parse(kidsMe.body) as JsonObject)?["Name"] == "Kids", kidsMe.body);

Console.WriteLine();
Console.WriteLine("── When it must do nothing ─────────────────────────────────────");

var atv = Get("/UserViews", token: ATV_TOKEN, client: "Jellyfin Android TV");
Ok("another app on the same account is untouched", atv.reachedJellyfin && seenAuth.Contains(ATV_TOKEN) && Names(atv.body) == "Movies", seenAuth);

var anon = Get("/System/Info/Public", token: "");
Ok("a request with no token is untouched", anon.reachedJellyfin);

Set("ProxyClients", new List<string>());
var off = Get("/UserViews");
Ok("with no app ticked, Moonfin is untouched", off.reachedJellyfin && seenAuth.Contains(MOON_TOKEN), seenAuth);
Set("ProxyClients", new List<string> { "moonfin" });

if (panicField != null)
{
    panicField.SetValue(null, true);
    try
    {
        var panic = Get("/UserViews");
        Ok("the emergency disable turns it off", panic.reachedJellyfin && seenAuth.Contains(MOON_TOKEN), seenAuth);
    }
    finally { panicField.SetValue(null, false); }
}

var logout = Get("/Sessions/Logout", method: "POST");
Ok("signing out of the app signs the profile out too",
   sessionLog.LoggedOut.Contains("tok-" + N(KIDS)) && logout.reachedJellyfin && seenAuth.Contains(MOON_TOKEN),
   string.Join(",", sessionLog.LoggedOut));

Console.WriteLine();
Console.WriteLine("── A profile signed out behind the app's back ──────────────────");

var fresh = Get("/UserViews");
Ok("signing in again starts at the picker", Names(fresh.body) == "Profiles", Names(fresh.body));
Children(IdOf(Children(IdOf(fresh.body, "Profiles")), "Open"));
var refused = Get("/Revoked");
Ok("Jellyfin refusing the profile's token is passed on as it is", refused.status == 401);
var after = Get("/UserViews");
Ok("and the next request finds the picker, not a dead profile",
   !after.reachedJellyfin && Names(after.body) == "Profiles", Names(after.body));

Console.WriteLine();
Console.WriteLine("── Another app's requests cost nothing ─────────────────────────");

var calls = DeviceManagerStub.Calls;
for (var i = 0; i < 20; i++) Get("/Videos/abc/hls1/main/" + i + ".ts", token: ATV_TOKEN, client: "Jellyfin Android TV");
Ok("requests naming another app never reach the database", DeviceManagerStub.Calls == calls, (DeviceManagerStub.Calls - calls) + " lookups");

Console.WriteLine();
Console.WriteLine("── The live connection ─────────────────────────────────────────");

var socketType = asm.GetType("Jellyfin.Profiles.Auth.RewritingWebSocket");
if (socketType == null)
{
    Ok("the live connection rewrites the profile's id", false, "no RewritingWebSocket in this build");
}
else
{
    var swap = switcher.GetMethod("SwapIds", BindingFlags.Static | BindingFlags.NonPublic);
    Func<string, string> rewrite = t => (string)swap.Invoke(null, new object[] { t, KIDS, MASTER });
    var onDispose = Expression.Lambda(
        typeof(Action<>).MakeGenericType(socketType),
        Expression.Empty(),
        Expression.Parameter(socketType)).Compile();
    var inner = new RecordingSocket();
    var wrapped = (WebSocket)Activator.CreateInstance(socketType, BindingFlags.Instance | BindingFlags.NonPublic, null,
        new object[] { inner, rewrite, onDispose }, null);

    var message = "{\"MessageType\":\"UserDataChanged\",\"Data\":{\"UserId\":\"" + N(KIDS) + "\"}}";
    var bytes = Encoding.UTF8.GetBytes(message);
    wrapped.SendAsync(new ArraySegment<byte>(bytes, 0, 30), WebSocketMessageType.Text, false, CancellationToken.None).GetAwaiter().GetResult();
    wrapped.SendAsync(new ArraySegment<byte>(bytes, 30, bytes.Length - 30), WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult();
    Ok("a watched-state message carries the main account's id, so the app keeps it",
       inner.Sent.Count == 1 && inner.Sent[0].Contains(N(MASTER)) && !inner.Sent[0].Contains(N(KIDS)), string.Join(" | ", inner.Sent));
}

Console.WriteLine();
try { Directory.Delete(tempDir, true); } catch { }
if (fails.Count > 0)
{
    foreach (var f in fails) Console.WriteLine("   - " + f);
    Console.WriteLine(pass + " passed, " + fails.Count + " failed");
    return 1;
}
Console.WriteLine(pass + " passed, 0 failed");
return 0;

// ── Stubs ────────────────────────────────────────────────────────────────────

public class SessionManagerStub : DispatchProxy
{
    public List<Guid> Minted = new();
    public List<AuthenticationRequest> Requests = new();
    public List<string> LoggedOut = new();

    public static ISessionManager Create() => Create<ISessionManager, SessionManagerStub>();

    protected override object Invoke(MethodInfo m, object[] args)
    {
        if (m.Name == "AuthenticateDirect")
        {
            var r = (AuthenticationRequest)args[0];
            Minted.Add(r.UserId);
            Requests.Add(r);
            return Task.FromResult(new AuthenticationResult { AccessToken = "tok-" + r.UserId.ToString("N") });
        }

        if (m.Name == "Logout" && args.Length == 1 && args[0] is string token)
        {
            LoggedOut.Add(token);
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
            P("DeviceId", "moon-1");
            items.Add(d);
        }

        return new QueryResult<Device>(items);
    }
}

public class UserManagerStub : DispatchProxy
{
    private Dictionary<Guid, string> _names;

    public static IUserManager Create(Dictionary<Guid, string> names)
    {
        var proxy = Create<IUserManager, UserManagerStub>();
        ((UserManagerStub)(object)proxy)._names = names;
        return proxy;
    }

    protected override object Invoke(MethodInfo m, object[] args)
    {
        if (m.Name == "GetUserById")
        {
            if (!_names.TryGetValue((Guid)args[0], out var name)) return null;
            var u = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
            typeof(User).GetProperty("Username").SetValue(u, name);
            typeof(User).GetProperty("Id").SetValue(u, (Guid)args[0]);
            return u;
        }

        if (m.Name == "GetUserDto") return new UserDto { Policy = new UserPolicy() };

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

public sealed class RecordingSocket : WebSocket
{
    public List<string> Sent = new();
    public override WebSocketCloseStatus? CloseStatus => null;
    public override string CloseStatusDescription => null;
    public override WebSocketState State => WebSocketState.Open;
    public override string SubProtocol => null;
    public override void Abort() { }
    public override Task CloseAsync(WebSocketCloseStatus s, string d, CancellationToken c) => Task.CompletedTask;
    public override Task CloseOutputAsync(WebSocketCloseStatus s, string d, CancellationToken c) => Task.CompletedTask;
    public override void Dispose() { }
    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken c) => throw new NotSupportedException();
    public override Task SendAsync(ArraySegment<byte> b, WebSocketMessageType t, bool end, CancellationToken c)
    {
        Sent.Add(Encoding.UTF8.GetString(b.Array, b.Offset, b.Count));
        return Task.CompletedTask;
    }
    public override ValueTask SendAsync(ReadOnlyMemory<byte> b, WebSocketMessageType t, bool end, CancellationToken c)
    {
        Sent.Add(Encoding.UTF8.GetString(b.Span));
        return ValueTask.CompletedTask;
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
