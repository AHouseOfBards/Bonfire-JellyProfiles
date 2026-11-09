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

var tempDir = Path.Combine(Path.GetTempPath(), "bonfire-pickerdiag-" + Guid.NewGuid().ToString("N"));
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
var kidsMapping = Map(KIDS, MASTER, "Kids", pin: "1234");
mappings.Add(kidsMapping);
mappings.Add(Map(OPEN, MASTER, "Open"));
Set("ProxyClients", new List<string> { "moonfin" });

var usernames = new Dictionary<Guid, string> { [MASTER] = "Bard", [KIDS] = "Bard_kids", [OPEN] = "Bard_open" };

const string MOON_TOKEN = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
const string ATV_TOKEN = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
const string FR_TOKEN = "cccccccccccccccccccccccccccccccc";
const string XX_TOKEN = "dddddddddddddddddddddddddddddddd";
const string NONE_TOKEN = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
var deviceTokens = new Dictionary<string, (Guid, string)>
{
    [MOON_TOKEN] = (MASTER, "Moonfin for Android TV"),
    [ATV_TOKEN] = (MASTER, "Jellyfin Android TV"),
    // More Moonfin sessions of the same household, for the picker's language (P2-706).
    [FR_TOKEN] = (MASTER, "Moonfin for Android TV"),
    [XX_TOKEN] = (MASTER, "Moonfin for Android TV"),
    [NONE_TOKEN] = (MASTER, "Moonfin for Android TV"),
};
var devices = DeviceManagerStub.Create(deviceTokens);
var sessions = SessionManagerStub.Create();
var sessionLog = (SessionManagerStub)(object)sessions;

var services = new ServiceCollection()
    .AddSingleton(devices)
    .AddSingleton(sessions)
    .AddSingleton(UserManagerStub.Create(usernames))
    .AddSingleton(NetworkStub.Create())
    // What Jellyfin registers from Networking → Known proxies: a reverse proxy on this host.
    .AddSingleton(Microsoft.Extensions.Options.Options.Create(KnownProxy("127.0.0.1")))
    .BuildServiceProvider();

static Microsoft.AspNetCore.Builder.ForwardedHeadersOptions KnownProxy(string address)
{
    var o = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
    {
        ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
            | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
            | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedHost,
        ForwardLimit = null,
    };
    o.KnownProxies.Clear();
    o.KnownProxies.Add(IPAddress.Parse(address));
    return o;
}

// ─────────────────────────────────────────────────────────────────────────────
// Temporary diagnostics (R-1032/R-1033): what the picker logs, for one beta, about
// requests made while a profile is acting. Facts only; never a token, id, name or URL.
// Against a build without it every assertion about a line fails.
// ─────────────────────────────────────────────────────────────────────────────

var diag = asm.GetType("Jellyfin.Profiles.Auth.PickerDiag");
var Lines = new List<(string level, string text)>();
CapLogger.Lines = Lines;
string DiagLog() => string.Join("\n", Lines.Where(l => l.text.Contains("[picker-diag]")).Select(l => l.text));
int DiagCount() => Lines.Count(l => l.text.Contains("[picker-diag]"));
bool Said(string tail) => Lines.Any(l => l.text == "ProfilesPlugin: [picker-diag] " + tail && l.level == "Information");

string seenPath = null, seenQuery = null, seenAuth = null, seenBody = null, seenAcceptEncoding = null, seenContentType = null;
string playlist = "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:3.0,\nhls1/main/0.ts?runtimeTicks=0&ApiKey=SECRETKEY999\n";
string playlistType = "application/vnd.apple.mpegurl";

var next = new RequestDelegate(async c =>
{
    seenPath = c.Request.Path.Value;
    seenQuery = c.Request.QueryString.Value;
    seenAuth = c.Request.Headers["Authorization"].ToString();
    seenAcceptEncoding = c.Request.Headers["Accept-Encoding"].ToString();
    seenContentType = c.Request.ContentType;
    seenBody = null;
    if (HttpMethods.IsPost(c.Request.Method))
    {
        using var r = new StreamReader(c.Request.Body, Encoding.UTF8, false, 1024, leaveOpen: true);
        seenBody = await r.ReadToEndAsync();
    }

    string body = "{}";
    c.Response.ContentType = "application/json";
    if (seenPath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)) { body = playlist; c.Response.ContentType = playlistType; }
    else if (seenPath.EndsWith("/UserViews", StringComparison.OrdinalIgnoreCase))
        body = "{\"Items\":[{\"Name\":\"Movies\",\"Id\":\"11111111111111111111111111111111\",\"Type\":\"CollectionFolder\"}],\"TotalRecordCount\":1,\"StartIndex\":0}";
    else if (seenPath.Contains("/Users/", StringComparison.OrdinalIgnoreCase) && !seenPath.EndsWith("/Items", StringComparison.OrdinalIgnoreCase))
    {
        var who = usernames.Keys.FirstOrDefault(k => c.Request.Headers["Authorization"].ToString().Contains("tok-" + N(k)));
        if (who == Guid.Empty) who = MASTER;
        body = "{\"Id\":\"" + N(who) + "\",\"Name\":\"" + usernames[who] + "\",\"Policy\":{}}";
    }

    c.Response.StatusCode = 200;
    await c.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(body));
});

var instance = Activator.CreateInstance(mwType, next, Activator.CreateInstance(typeof(CapLogger<>).MakeGenericType(mwType)));

HttpResponse lastResponse = null;
MemoryStream lastBytes = null;

(int status, string body) Send(string pathAndQuery, string method = "GET", string payload = null, bool queryOnly = false, string token = MOON_TOKEN,
    string acceptEncoding = null)
{
    var ctx = new DefaultHttpContext { RequestServices = services };
    ctx.Request.Method = method;
    var q = pathAndQuery.IndexOf('?');
    ctx.Request.Path = q < 0 ? pathAndQuery : pathAndQuery[..q];
    if (q >= 0) ctx.Request.QueryString = new QueryString(pathAndQuery[q..]);
    ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
    if (acceptEncoding != null) ctx.Request.Headers["Accept-Encoding"] = acceptEncoding;
    if (!queryOnly)
        ctx.Request.Headers["Authorization"] =
            "MediaBrowser Client=\"Moonfin for Android TV\", Device=\"Shield\", DeviceId=\"moon-1\", Version=\"1.4.0\", Token=\"" + token + "\"";
    if (payload != null)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentType = "application/json";
        ctx.Request.ContentLength = bytes.Length;
    }

    var sink = new MemoryStream();
    lastResponse = ctx.Response;
    lastBytes = sink;
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
    return (ctx.Response.StatusCode, Encoding.UTF8.GetString(sink.ToArray()));
}

JsonArray Items(string body) => (JsonNode.Parse(body) as JsonObject)?["Items"] as JsonArray ?? new JsonArray();
string IdOf(string body, string name) => (string)Items(body).FirstOrDefault(i => (string)i["Name"] == name)?["Id"];

Console.WriteLine();
Console.WriteLine("── At the picker nothing is logged ─────────────────────────────");

var views = Send("/UserViews");
var profilesId = IdOf(views.body, "Profiles");
var tiles = Send("/Users/" + N(MASTER) + "/Items?ParentId=" + profilesId + "&Recursive=false").body;
Send("/Videos/abc/stream?static=true&api_key=" + MOON_TOKEN, queryOnly: true);
Send("/Videos/abc/master.m3u8?api_key=" + MOON_TOKEN, queryOnly: true);
Ok("a stream request before any profile is open logs nothing", DiagCount() == 0, DiagLog());

Console.WriteLine();
Console.WriteLine("── Question A: a token in a stream or playlist request ─────────");

Send("/Users/" + N(MASTER) + "/Items?ParentId=" + IdOf(tiles, "Open"));
Lines.Clear();
Send("/Videos/abc/stream?static=true&api_key=" + MOON_TOKEN, queryOnly: true);
Ok("a stream request with api_key in the query is logged as having one", Said("stream request, token in query: yes"), DiagLog());
Send("/Videos/abc/stream?static=true", token: MOON_TOKEN);
Ok("one without is logged as having none", Said("stream request, token in query: no"), DiagLog());
Send("/Videos/abc/stream?ApiKey=" + MOON_TOKEN, queryOnly: true);
Send("/Audio/abc/stream.mp3?ApiKey=" + MOON_TOKEN, queryOnly: true);

var gotPlaylist = Send("/Videos/abc/master.m3u8?api_key=" + MOON_TOKEN, queryOnly: true, acceptEncoding: "gzip, br");
Ok("a playlist request is logged", Said("playlist request, token in query: yes"), DiagLog());
Ok("a playlist that hands back URLs with a token is logged as doing so", Said("playlist response, token in its URLs: yes"), DiagLog());
Ok("the app still gets the playlist byte for byte", gotPlaylist.status == 200 && gotPlaylist.body == playlist, gotPlaylist.body);
Ok("with the content type Jellyfin gave it", lastResponse.ContentType == playlistType, lastResponse.ContentType);
Ok("and Jellyfin was asked exactly as the app asked, encodings included", seenAcceptEncoding == "gzip, br", seenAcceptEncoding);

playlist = "#EXTM3U\n#EXTINF:3.0,\nhls1/main/0.ts?runtimeTicks=0\n";
var plain = Send("/Videos/abc/main.m3u8", token: MOON_TOKEN);
Ok("a playlist with no token in it is logged as having none", Said("playlist response, token in its URLs: no"), DiagLog());
Ok("and still arrives unchanged", plain.body == playlist);
Send("/Videos/abc/hls1/main/0.ts?api_key=" + MOON_TOKEN, queryOnly: true);
Ok("a media segment is logged as a request only", Said("segment request, token in query: yes"), DiagLog());

Console.WriteLine();
Console.WriteLine("── Question B: a UserId in a PlaybackInfo or LiveStreams/Open body ──");

Guid STRANGER = Guid.NewGuid();
string PlaybackBody(string json, string path = "/Items/abc/PlaybackInfo?MediaSourceId=x")
{
    var r = Send(path, method: "POST", payload: json);
    return seenBody;
}

var sent = "{\"UserId\":\"" + N(MASTER) + "\",\"MaxStreamingBitrate\":1000}";
var got = PlaybackBody(sent);
Ok("PlaybackInfo naming the main account is logged as main", Said("PlaybackInfo body, UserId: main"), DiagLog());
Ok("and Jellyfin reads the body as the app wrote it", got == sent, got);
Ok("with its content type", seenContentType == "application/json", seenContentType);
PlaybackBody("{\"UserId\":\"" + N(OPEN) + "\"}");
Ok("one naming the profile is logged as profile", Said("PlaybackInfo body, UserId: profile"), DiagLog());
PlaybackBody("{\"userId\":\"" + STRANGER + "\"}");
Ok("one naming someone else is logged as other", Said("PlaybackInfo body, UserId: other"), DiagLog());
PlaybackBody("{\"MaxStreamingBitrate\":1}");
Ok("one naming nobody is logged as absent", Said("PlaybackInfo body, UserId: absent"), DiagLog());
PlaybackBody("{\"UserId\":\"" + MASTER + "\"}");
Ok("a hyphenated id is still the main account", DiagLog().Split('\n').Count(l => l.EndsWith("PlaybackInfo body, UserId: main")) == 1, DiagLog());
var open = PlaybackBody("{\"UserId\":\"" + N(MASTER) + "\",\"OpenToken\":\"x\"}", "/LiveStreams/Open");
Ok("LiveStreams/Open is logged the same way", Said("LiveStreams/Open body, UserId: main"), DiagLog());
Ok("and its body forwards unchanged", open == "{\"UserId\":\"" + N(MASTER) + "\",\"OpenToken\":\"x\"}", open);
PlaybackBody("not json at all");
Ok("a body that is not JSON is logged as unreadable and still forwarded", Said("PlaybackInfo body, UserId: unreadable") && seenBody == "not json at all");
var before = DiagCount();
Send("/Items/abc/PlaybackInfo?UserId=" + N(MASTER), method: "GET");
Send("/Items/abc/Other", method: "POST", payload: "{\"UserId\":\"" + N(MASTER) + "\"}");
Ok("a GET, or a POST to anything else, has its body left alone", DiagCount() == before && seenBody == "{\"UserId\":\"" + N(MASTER) + "\"}", DiagLog());

Console.WriteLine();
Console.WriteLine("── What is logged ──────────────────────────────────────────────");

var all = Lines.Where(l => l.text.Contains("[picker-diag]")).ToList();
Ok("there are lines to inspect", all.Count >= 10, all.Count.ToString());
Ok("every one is Information, with the prefix to grep for", all.All(l => l.level == "Information" && l.text.StartsWith("ProfilesPlugin: [picker-diag] ")));
var forbidden = new List<string> { MOON_TOKEN, "tok-", "SECRETKEY999", N(MASTER), N(OPEN), N(KIDS), MASTER.ToString(), OPEN.ToString(), N(STRANGER), STRANGER.ToString(),
    "Bard", "Moonfin", "moon-1", "Shield", "abc", "/Videos", "http", "static=true" };
var leaks = all.SelectMany(l => forbidden.Where(f => l.text.Contains(f, StringComparison.OrdinalIgnoreCase)).Select(f => f + " in: " + l.text)).ToList();
Ok("no token, id, name or URL value is in any of them", leaks.Count == 0, string.Join(" | ", leaks));
Ok("nothing else the plugin logged carries a token or id either",
   Lines.All(l => !l.text.Contains(MOON_TOKEN) && !l.text.Contains("tok-") && !l.text.Contains("SECRETKEY999")), string.Join(" | ", Lines.Where(l => l.text.Contains(MOON_TOKEN) || l.text.Contains("tok-")).Select(l => l.text)));

var say = diag?.GetMethod("Say", BindingFlags.Static | BindingFlags.NonPublic);
var reset = diag?.GetMethod("Reset", BindingFlags.Static | BindingFlags.NonPublic);
var cap = (int?)diag?.GetField("Cap", BindingFlags.Static | BindingFlags.NonPublic)?.GetRawConstantValue();
Ok("the cap is fifty lines", cap == 50, cap?.ToString());
if (say != null && reset != null)
{
    reset.Invoke(null, null);
    Lines.Clear();
    var log = (Microsoft.Extensions.Logging.ILogger)Activator.CreateInstance(typeof(CapLogger<>).MakeGenericType(mwType));
    for (var i = 0; i < 120; i++) say.Invoke(null, new object[] { log, "line " + i });
    say.Invoke(null, new object[] { log, "line 1" });
    Ok("at most fifty lines per server start, however many are asked for", DiagCount() == 50, DiagCount().ToString());
    reset.Invoke(null, null);
    Lines.Clear();
    for (var i = 0; i < 400; i++) say.Invoke(null, new object[] { log, "same" });
    Ok("a line already said is not said again", DiagCount() == 1, DiagCount().ToString());
}
else Ok("the cap can be exercised", false, "no PickerDiag.Say / Reset");

Console.WriteLine();
Console.WriteLine("── Removable ───────────────────────────────────────────────────");

var marker = "// TEMPORARY (R-1032/R-1033): remove before 2.0";
var diagFile = Path.Combine(RepoRoot(), "Auth", "PickerDiag.cs");
var unmarked = File.Exists(diagFile)
    ? File.ReadAllLines(diagFile).Select((l, i) => (l.Trim(), i + 1))
        .Where(t => t.Item1.Length > 0 && !t.Item1.StartsWith("//") && !t.Item1.StartsWith("/*") && !t.Item1.StartsWith("*") && !t.Item1.Contains(marker))
        .Select(t => t.Item2.ToString()).ToList()
    : new List<string> { "no file" };
Ok("every line of PickerDiag.cs carries the removal marker", unmarked.Count == 0, "lines " + string.Join(",", unmarked));
var hooks = File.ReadAllLines(Path.Combine(RepoRoot(), "Auth", "ProxySwitcher.cs")).Where(l => l.Contains("PickerDiag")).ToList();
Ok("so does every line in the picker that calls it", hooks.Count > 0 && hooks.All(l => l.Contains(marker)), hooks.Count + " call sites");

Console.WriteLine();
Console.WriteLine(pass + " passed, " + fails.Count + " failed");
foreach (var f in fails) Console.WriteLine("  FAILED: " + f);
return fails.Count == 0 ? 0 : 1;

public sealed class ThrowingProvider : IServiceProvider
{
    private readonly IServiceProvider _inner;
    private readonly Type _blocked;
    public ThrowingProvider(IServiceProvider inner, Type blocked) { _inner = inner; _blocked = blocked; }
    public object GetService(Type t) => t == _blocked ? throw new InvalidOperationException("injected: " + t.Name) : _inner.GetService(t);
}

public sealed class ThrowingStream : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => 0;
    public override long Position { get => 0; set { } }
    public override void Flush() { }
    public override int Read(byte[] b, int o, int c) => 0;
    public override long Seek(long o, SeekOrigin s) => 0;
    public override void SetLength(long v) { }
    public override void Write(byte[] b, int o, int c) => throw new InvalidOperationException("injected: write");
    public override Task WriteAsync(byte[] b, int o, int c, CancellationToken t) => throw new InvalidOperationException("injected: write");
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> b, CancellationToken t = default) => throw new InvalidOperationException("injected: write");
}

public class SessionManagerStub : DispatchProxy
{
    /// <summary>Tokens Jellyfin cannot sign out right now.</summary>
    public static HashSet<string> FailLogout = new();
    /// <summary>Runs inside Logout, so a test can send a request while one is in flight.</summary>
    public static Action<string> DuringLogout;
    public List<Guid> Minted = new();
    public List<AuthenticationRequest> Requests = new();
    public List<string> LoggedOut = new();
    public static List<SessionInfo> Existing = new();
    public static List<string> EndedIds = new();
    public static List<string> Order = new();
    public static string ThrowMessage;

    public static ISessionManager Create() => Create<ISessionManager, SessionManagerStub>();

    protected override object Invoke(MethodInfo m, object[] args)
    {
        if (m.Name == "get_Sessions") return Existing.ToList();
        if (m.Name == "ReportSessionEnded")
        {
            EndedIds.Add((string)args[0]);
            Order.Add("end:" + (string)args[0]);
            return default(ValueTask);
        }

        if (m.Name == "AuthenticateDirect")
        {
            Order.Add("auth");
            if (ThrowMessage != null)
                return Task.FromException<AuthenticationResult>(new MediaBrowser.Controller.Net.SecurityException(ThrowMessage));
            var r = (AuthenticationRequest)args[0];
            Minted.Add(r.UserId);
            Requests.Add(r);
            return Task.FromResult(new AuthenticationResult { AccessToken = "tok-" + r.UserId.ToString("N") });
        }

        if (m.Name == "Logout" && args.Length == 1 && args[0] is string token)
        {
            DuringLogout?.Invoke(token);
            if (FailLogout.Contains(token)) return Task.FromException(new InvalidOperationException("Jellyfin unreachable"));
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
        var userId = q.GetType().GetProperty("UserId")?.GetValue(q) as Guid? ?? Guid.Empty;
        var items = new List<Device>();
        // By token, or (a later query) every row of one account.
        foreach (var pair in _tokens.Where(p => token != null ? p.Key == token : userId != Guid.Empty && p.Value.user == userId))
        {
            var d = (Device)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Device));
            void P(string name, object v) => typeof(Device).GetProperty(name).SetValue(d, v);
            P("UserId", pair.Value.user);
            P("AppName", pair.Value.app);
            P("AppVersion", "1.4.0");
            P("DeviceName", "Shield");
            P("DeviceId", "moon-1");
            P("AccessToken", pair.Key);
            items.Add(d);
        }

        return new QueryResult<Device>(items);
    }
}

public class UserManagerStub : DispatchProxy
{
    private Dictionary<Guid, string> _names;

    /// <summary>Accounts turned off in Dashboard -> Users.</summary>
    public static readonly HashSet<Guid> Disabled = new();

    /// <summary>Administrators. Closed for maintenance, only they get past the gate in front of the picker.</summary>
    public static readonly HashSet<Guid> Admins = new();

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

        if (m.Name == "GetUserDto") return new UserDto { Policy = new UserPolicy { IsDisabled = Disabled.Contains(((User)args[0]).Id), IsAdministrator = Admins.Contains(((User)args[0]).Id) } };

        throw new NotSupportedException("IUserManager." + m.Name + " is not modelled");
    }
}

public class NetworkStub : DispatchProxy
{
    public static INetworkManager Create() => Create<INetworkManager, NetworkStub>();

    // Loopback and 192.168.x.x are home, judged on the address passed in, as Jellyfin's is.
    protected override object Invoke(MethodInfo m, object[] args)
    {
        if (m.Name != "IsInLocalNetwork") throw new NotSupportedException("INetworkManager." + m.Name + " is not modelled");
        var text = args[0]?.ToString() ?? string.Empty;
        return text == "127.0.0.1" || text.StartsWith("192.168.", StringComparison.Ordinal);
    }
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

