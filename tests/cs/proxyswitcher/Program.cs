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
Ok("the emergency-disable switch is still found by name (a rename must fail here, not skip its checks)", panicField != null);

var tempDir = Path.Combine(Path.GetTempPath(), "bonfire-proxyswitcher-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
var plugin = Activator.CreateInstance(pluginType, new StubPaths(tempDir), new StubXml());
var config = pluginType.GetProperty("Configuration").GetValue(plugin);
// A new install starts with Bonfire off (2.0); this harness is about a server where it is on.
config.GetType().GetProperty("BonfireOn").SetValue(config, true);
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

// What Jellyfin was handed, for the last request that reached it.
string seenPath = null, seenQuery = null, seenAuth = null;

// Faults to inject: a service the request's provider throws for, or a body that throws when written.
Type throwFor = null;
bool throwOnWrite = false;

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

// The last response and its raw bytes, for a picture.
HttpResponse lastResponse = null;
MemoryStream lastBytes = null;

// One request through the real middleware. Returns the body the app got, or null when
// Jellyfin answered it (seenPath says what Jellyfin saw).
(int status, string body, bool reachedJellyfin) Get(string pathAndQuery, string token = MOON_TOKEN, string client = "Moonfin for Android TV", string method = "GET",
    string remote = "203.0.113.9", string forwardedFor = null, string acceptLanguage = null, bool bare = false)
{
    seenPath = seenQuery = seenAuth = null;
    var ctx = new DefaultHttpContext { RequestServices = throwFor == null ? services : new ThrowingProvider(services, throwFor) };
    ctx.Request.Method = method;
    var q = pathAndQuery.IndexOf('?');
    ctx.Request.Path = q < 0 ? pathAndQuery : pathAndQuery[..q];
    if (q >= 0) ctx.Request.QueryString = new QueryString(pathAndQuery[q..]);
    ctx.Connection.RemoteIpAddress = IPAddress.Parse(remote);
    if (forwardedFor != null) ctx.Request.Headers["X-Forwarded-For"] = forwardedFor;
    if (acceptLanguage != null) ctx.Request.Headers["Accept-Language"] = acceptLanguage;
    // A bare request names no app and carries only the token, like an image or a stream URL.
    if (bare) ctx.Request.Headers["X-Emby-Token"] = token;
    else ctx.Request.Headers["Authorization"] =
        "MediaBrowser Client=\"" + client + "\", Device=\"Shield\", DeviceId=\"moon-1\", Version=\"1.4.0\", Token=\"" + token + "\"";
    var sink = new MemoryStream();
    lastResponse = ctx.Response;
    lastBytes = sink;
    ctx.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>(new StreamResponseBodyFeature(throwOnWrite ? new ThrowingStream() : sink));

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
Console.WriteLine("── Tiles drawn in the theme's colours ──────────────────────────");

// A profile with no picture, and the Profiles folder, used to have no image: the app drew
// its own blank folder. Bonfire now draws one (1.7.2), tagged by what it shows.
string TagOf(string body, string name) =>
    (string)Items(body).FirstOrDefault(i => (string)i["Name"] == name)?["ImageTags"]?["Primary"];
var openTag = TagOf(tiles, "Open");
var viewTag = TagOf(views.body, "Profiles");
Ok("a profile with no picture has an image tag (" + openTag + ")", openTag != null && openTag.StartsWith("b", StringComparison.Ordinal));
Ok("so does the Profiles folder (" + viewTag + ")", viewTag != null && viewTag.StartsWith("b", StringComparison.Ordinal));
byte[] Picture(string id)
{
    var r = Get("/Items/" + id + "/Images/Primary?tag=x&maxWidth=300");
    return r.status == 200 && lastResponse.ContentType == "image/png" && !r.reachedJellyfin ? lastBytes.ToArray() : null;
}
var openPng = Picture(IdOf(tiles, "Open"));
Ok("asking for it gets a PNG from Bonfire, not Jellyfin",
   openPng != null && openPng.Take(8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
var viewPng = Picture(profilesId);
Ok("a different one for the folder", viewPng != null && !viewPng.SequenceEqual(openPng));

// The profile's own theme: change it and the tag and picture change with it.
var openRow = mappings.Cast<object>().First(m => (Guid)mapType.GetProperty("ProfileUserId").GetValue(m) == OPEN);
mapType.GetProperty("Theme")?.SetValue(openRow, "harvest");
var retagged = TagOf(Children(profilesId), "Open");
Ok("a profile's tile follows its own theme: a new tag when it changes", retagged != null && retagged != openTag, retagged);
Ok("and a new picture", Picture(IdOf(tiles, "Open")) is byte[] harvestPng && !harvestPng.SequenceEqual(openPng));
Ok("the folder's stays the main account's", TagOf(Get("/UserViews").body, "Profiles") == viewTag);
mapType.GetProperty("Theme")?.SetValue(openRow, null);

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
Console.WriteLine("── On the picker, what it does not draw is refused ─────────────");

// A child gets here after Switch profile, a restart or a maintenance bounce, with detail
// pages still on the app's back stack. Everything the picker does not recognise used to
// pass on as the main account.
var someItem = Guid.NewGuid().ToString("N");
var strangers = new (string method, string path, string what)[]
{
    ("GET", "/Items/" + someItem, "an item's details"),
    ("GET", "/Users/" + N(MASTER) + "/Items/" + someItem, "an item's details, old route"),
    ("GET", "/Items/" + someItem + "/Similar", "similar items"),
    ("GET", "/Shows/" + someItem + "/Episodes", "a show's episodes"),
    ("GET", "/Shows/" + someItem + "/Seasons", "a show's seasons"),
    ("POST", "/Items/" + someItem + "/PlaybackInfo", "playback info"),
    ("GET", "/Videos/" + someItem + "/stream?static=true", "a video stream"),
    ("GET", "/Videos/" + someItem + "/master.m3u8", "an HLS playlist"),
    ("GET", "/Items/" + someItem + "/Download", "a download"),
    ("GET", "/Items/" + someItem + "/Images/Primary", "an item's picture that is not a tile"),
    ("POST", "/QuickConnect/Authorize?code=123456", "a Quick Connect authorize"),
    ("POST", "/Sessions/Playing", "a playback report"),
    ("GET", "/Sessions", "the session list"),
    ("GET", "/Users", "the user list"),
    ("GET", "/Users/" + N(KIDS), "another account's record"),
    ("POST", "/Users/Configuration", "a settings write on the main account"),
    ("POST", "/UserFavoriteItems/" + someItem, "a favourite on the main account"),
    ("GET", "/Library/VirtualFolders", "the library list"),
    ("GET", "/Moonfin/Seerr/Api/request", "the request proxy, as the main account"),
    ("POST", "/Moonfin/Settings", "a plugin settings write"),
    ("GET", "/Totally/New/Endpoint", "something a newer app calls"),
};
foreach (var (method, path, what) in strangers)
{
    var r = Get(path, method: method);
    Ok("on the picker, " + what + " (" + method + " " + path.Split('?')[0] + ") is refused, not passed to Jellyfin as the main account",
       !r.reachedJellyfin, "Jellyfin saw " + seenPath + " as " + seenAuth);
}

var stranger = Get("/Items/" + someItem);
Ok("a refusal is a 404, which the app treats as 'no such thing' (a 401 signs it out, a 403 starts its schedule check)",
   stranger.status == 404, stranger.status.ToString());
var similar = Get("/Items/" + someItem + "/Similar");
Ok("a list it asks for comes back empty, not an error",
   !similar.reachedJellyfin && similar.status == 200 && (JsonNode.Parse(similar.body) as JsonObject)?["TotalRecordCount"] is not null && Items(similar.body).Count == 0,
   similar.status + " " + similar.body);

// What the app needs to run and draw the picker still reaches Jellyfin, as the main account.
var needed = new (string method, string path)[]
{
    ("GET", "/System/Info"),
    ("GET", "/System/Info/Public"),
    ("GET", "/System/Ping"),
    ("GET", "/Users/Me"),
    ("GET", "/Users/" + N(MASTER)),
    ("GET", "/Users/" + N(MASTER) + "/Images/Primary"),
    ("POST", "/Sessions/Capabilities/Full"),
    ("GET", "/Moonfin/Ping"),
    ("GET", "/Moonfin/Settings/Resolved/tv"),
    ("GET", "/Moonfin/Seerr/Config"),
};
foreach (var (method, path) in needed)
{
    var r = Get(path, method: method);
    Ok("on the picker, " + method + " " + path.Replace(N(MASTER), "{main}") + " still reaches Jellyfin as the main account",
       r.reachedJellyfin && seenAuth.Contains(MOON_TOKEN), r.status + " " + seenPath);
}
var viewsAfter = Get("/UserViews");
Ok("and the picker still draws", Names(viewsAfter.body) == "Profiles", Names(viewsAfter.body));
Ok("a tile's own picture still works", Get("/Items/" + IdOf(viewsAfter.body, "Profiles") + "/Images/Primary?tag=x").status == 200);

Console.WriteLine();
Console.WriteLine("── A profile with a PIN: the keypad ────────────────────────────");

var kidsId = IdOf(back, "Kids");

// Its owner skips the PIN at home. Bonfire's middleware runs in front of Jellyfin's
// forwarded-headers handling, so behind a reverse proxy every request arrived from the
// proxy's own address and looked like home (R-1029).
mapType.GetProperty("BypassPinOnLocalNetwork").SetValue(kidsMapping, true);
var proxied = Get("/Users/" + N(MASTER) + "/Items?ParentId=" + kidsId + "&Recursive=false", remote: "127.0.0.1", forwardedFor: "203.0.113.9");
Ok("behind a reverse proxy, a Moonfin on the internet still gets the keypad",
   Names(proxied.body).StartsWith("1, 2, 3") && sessionLog.Minted.Count == 1, Names(proxied.body));
mapType.GetProperty("BypassPinOnLocalNetwork").SetValue(kidsMapping, false);
// (Home through the same proxy is savedsignins' case: opening it here would sign the
// picker in and change everything below.)

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

// These two ran with the app in the Kids profile, and asserted that turning Bonfire off
// handed the app to Jellyfin on the main account's token: the very thing the section
// below says must not happen to a child. They are about the picker being off, so they
// now run on the picker, which is what they were meant to show.
Children(IdOf(kidsHome.body, "Switch profile"));

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
// Turned off on the Status tab (2.0): the same, at ProxySwitcher and QuickConnectAuthorize.
Set("BonfireOn", false);
try
{
    var turnedOff = Get("/UserViews");
    Ok("turned off, it is off too", turnedOff.reachedJellyfin && seenAuth.Contains(MOON_TOKEN), seenAuth);
}
finally { Set("BonfireOn", true); }
{
    // With an app ticked, a profile's Quick Connect code from a device nobody knows is
    // refused, because some rule could have refused it.
    var qc = asm.GetType("Jellyfin.Profiles.Auth.QuickConnectAuthorize", true).GetMethod("Refusal");
    string Refused() => (string)qc.Invoke(null, new object[] { config, KIDS, "no-such-code" });
    Ok("(set-up) Quick Connect for a profile is refused with an app ticked", Refused() != null);
    panicField.SetValue(null, true);
    try { Ok("the emergency code lets it through", Refused() == null, Refused()); }
    finally { panicField.SetValue(null, false); }
    Set("BonfireOn", false);
    try { Ok("turned off lets it through", Refused() == null, Refused()); }
    finally { Set("BonfireOn", true); }
    Ok("turned back on, it is refused again", Refused() != null);
}

// Back in Kids, as the rest of this file expects.
void ToKids()
{
    var pk = Children(IdOf(Children(IdOf(Get("/UserViews").body, "Profiles")), "Kids"));
    foreach (var k in new[] { "1", "2", "3", "4" }) pk = Children(IdOf(pk, k));
    Children(IdOf(pk, "OK ••••"));
}
Children(IdOf(Get("/UserViews").body, "Switch profile"));
ToKids();
Ok("back in the Kids profile", Get("/UserViews").reachedJellyfin && seenAuth.Contains("tok-" + N(KIDS)), seenAuth);

Console.WriteLine();
Console.WriteLine("── A child's TV is never left on the main account ───────────────");

// Leaving the swap used to fail open: the emergency code, the app unticked and an error
// inside the picker each handed the request to Jellyfin on the main account's token, so a
// TV left in a child's profile became the unrestricted account, silently. Now it is
// signed out, and asks for the main password again.
void ProxyReset() => switcher.GetMethod("Reset", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
void Child()  // put the TV in Kids from a clean start
{
    ProxyReset();
    deviceTokens[MOON_TOKEN] = (MASTER, "Moonfin for Android TV");
    Set("ProxyClients", new List<string> { "moonfin" });
    ToKids();
}
bool SignedOut(int before) => sessionLog.LoggedOut.Skip(before).Contains(MOON_TOKEN) && sessionLog.LoggedOut.Skip(before).Contains("tok-" + N(KIDS));

if (panicField != null)
{
    Child();
    Ok("(set-up) the TV is in Kids", Get("/UserViews").reachedJellyfin && seenAuth.Contains("tok-" + N(KIDS)), seenAuth);
    var before = sessionLog.LoggedOut.Count;
    panicField.SetValue(null, true);
    try
    {
        var r = Get("/UserViews");
        Ok("the emergency code: a TV in a child's profile does not reach Jellyfin as the main account",
           !r.reachedJellyfin && !(seenAuth ?? "").Contains(MOON_TOKEN), "Jellyfin saw " + seenAuth);
        Ok("it is refused with a 401, so the app asks for the main password", r.status == 401, r.status.ToString());
        Ok("and the profile's session and the main account's token are both signed out", SignedOut(before), string.Join(",", sessionLog.LoggedOut.Skip(before)));
    }
    finally { panicField.SetValue(null, false); }
}
{
    Child();
    var before = sessionLog.LoggedOut.Count;
    Set("BonfireOn", false);
    try
    {
        var r = Get("/UserViews");
        Ok("turned off: a TV in a child's profile does not reach Jellyfin as the main account",
           !r.reachedJellyfin && !(seenAuth ?? "").Contains(MOON_TOKEN), "Jellyfin saw " + seenAuth);
        Ok("turned off: it is refused with a 401", r.status == 401, r.status.ToString());
        Ok("turned off: and both are signed out", SignedOut(before), string.Join(",", sessionLog.LoggedOut.Skip(before)));
    }
    finally { Set("BonfireOn", true); }
}

Child();
{
    var before = sessionLog.LoggedOut.Count;
    Set("ProxyClients", new List<string>());
    var r = Get("/UserViews");
    Ok("the app unticked, nothing else ticked: not passed on as the main account", !r.reachedJellyfin && r.status == 401, r.status + " " + seenAuth);
    Ok("and signed out", SignedOut(before), string.Join(",", sessionLog.LoggedOut.Skip(before)));
}

Child();
{
    var before = sessionLog.LoggedOut.Count;
    Set("ProxyClients", new List<string> { "some-other-app" });   // something is ticked, but not this app
    var r = Get("/UserViews");
    Ok("the app unticked while another is ticked: not passed on as the main account", !r.reachedJellyfin && r.status == 401, r.status + " " + seenAuth);
    Ok("and signed out", SignedOut(before), string.Join(",", sessionLog.LoggedOut.Skip(before)));
    Set("ProxyClients", new List<string> { "moonfin" });
}

Child();
{
    // The last profile of the household deleted while the TV is in it.
    var before = sessionLog.LoggedOut.Count;
    var held = mappings.Cast<object>().ToList();
    mappings.Clear();
    var r = Get("/UserViews");
    foreach (var m in held) mappings.Add(m);
    Ok("the profiles deleted while the TV is in one: not passed on as the main account", !r.reachedJellyfin && r.status == 401, r.status + " " + seenAuth);
    Ok("and signed out", SignedOut(before), string.Join(",", sessionLog.LoggedOut.Skip(before)));
}

Child();
{
    // An error inside the picker, before Jellyfin sees the request. Here: the session
    // manager cannot be reached, which the viewing-hours check on the profile needs.
    var before = sessionLog.LoggedOut.Count;
    throwFor = typeof(ISessionManager);
    var r = Get("/UserViews");
    throwFor = null;
    Ok("an error inside the picker: not passed on as the main account", !r.reachedJellyfin && !(seenAuth ?? "").Contains(MOON_TOKEN), "Jellyfin saw " + seenAuth);
    Ok("it is refused (503), not answered with the main account's libraries", r.status == 503, r.status.ToString());
    var later = Get("/UserViews");
    Ok("and the TV is still in Kids afterwards: nothing was signed out for an error",
       later.reachedJellyfin && seenAuth.Contains("tok-" + N(KIDS)) && !SignedOut(before), seenAuth);
}

// A Jellyfin that cannot be reached to sign out must leave the TV refused, not open.
Child();
{
    SessionManagerStub.FailLogout.Add(MOON_TOKEN);
    Set("ProxyClients", new List<string>());
    var first = Get("/UserViews");
    Set("ProxyClients", new List<string> { "moonfin" });
    var second = Get("/UserViews");
    Set("ProxyClients", new List<string>());
    var third = Get("/UserViews");
    Ok("when Jellyfin cannot sign it out, it is refused, and stays refused", first.status == 401 && !first.reachedJellyfin
       && second.status == 401 && !second.reachedJellyfin && third.status == 401 && !third.reachedJellyfin,
       first.status + "," + second.status + "," + third.status);
    SessionManagerStub.FailLogout.Clear();
    var fourth = Get("/UserViews");
    Ok("until the sign-out gets through", fourth.status == 401 && sessionLog.LoggedOut.Contains(MOON_TOKEN));
    Set("ProxyClients", new List<string> { "moonfin" });
}

// A TV asks for several things at once. One that arrives while the profile's sign-out is
// still in flight, with the app unticked, must not find the TV already let go.
Child();
{
    (int status, string body, bool reachedJellyfin) during = default;
    var kidsToken = "tok-" + N(KIDS);
    Set("ProxyClients", new List<string>());
    SessionManagerStub.DuringLogout = t =>
    {
        if (t != kidsToken) return;
        SessionManagerStub.DuringLogout = null;
        during = Get("/UserViews");
    };
    Get("/UserViews");
    SessionManagerStub.DuringLogout = null;
    Ok("a request during the sign-out is not passed on as the main account", !during.reachedJellyfin && during.status == 401, during.status + " " + seenAuth);
    Set("ProxyClients", new List<string> { "moonfin" });
}

// The picker itself, an adult, an app on the main account: not a child's.
ProxyReset();
Set("ProxyClients", new List<string> { "moonfin" });
{
    var before = sessionLog.LoggedOut.Count;
    Get("/UserViews");   // the picker
    Set("ProxyClients", new List<string>());
    var r = Get("/UserViews");
    Ok("a TV on the picker, with the app unticked, is passed on as before", r.reachedJellyfin && seenAuth.Contains(MOON_TOKEN), seenAuth);
    Ok("and nobody is signed out", sessionLog.LoggedOut.Count == before);
    Set("ProxyClients", new List<string> { "moonfin" });

    // An error on the picker: the main account's request must not be passed on either.
    throwOnWrite = true;
    (int status, string body, bool reachedJellyfin) e;
    // Passed on, the error surfaces again from Jellyfin's own write: a crash is also a fail.
    try { e = Get("/UserViews"); }
    catch (Exception) { e = (0, null, true); }
    throwOnWrite = false;
    Ok("an error while on the picker does not pass the request on as the main account", !e.reachedJellyfin && e.status == 503, e.status + " Jellyfin saw " + seenAuth);
}

// Off with nobody in a child's profile costs what it always did: no lookup, no read.
ProxyReset();
Set("ProxyClients", new List<string>());
{
    var calls0 = DeviceManagerStub.Calls;
    for (var i = 0; i < 50; i++) Get("/Videos/abc/hls1/main/" + i + ".ts");
    Ok("nothing ticked and nobody in a child's profile: no device lookups", DeviceManagerStub.Calls == calls0, (DeviceManagerStub.Calls - calls0) + " lookups");
}
Set("ProxyClients", new List<string> { "moonfin" });
ProxyReset();
// Put the session back where the rest of the file expects it: in Kids.
Child();

// An app seen before its account had a profile is remembered as not ours. Creating the
// first profile must not leave it without the picker for five minutes (R-1038).
{
    var NEWMASTER = Guid.NewGuid(); var NEWKID = Guid.NewGuid();
    const string NEW_TOKEN = "ffffffffffffffffffffffffffffffff";
    deviceTokens[NEW_TOKEN] = (NEWMASTER, "Moonfin for Android TV");
    usernames[NEWMASTER] = "Newbie"; usernames[NEWKID] = "Newbie_kid";
    Get("/UserViews", NEW_TOKEN);
    var calls1 = DeviceManagerStub.Calls;
    Get("/UserViews", NEW_TOKEN);
    Ok("an account with no profiles is remembered as not ours", DeviceManagerStub.Calls == calls1, (DeviceManagerStub.Calls - calls1) + " lookups");
    mappings.Add(Map(NEWKID, NEWMASTER, "Kid"));
    Get("/UserViews", NEW_TOKEN);
    Ok("and looked at again once it has a profile", DeviceManagerStub.Calls > calls1, (DeviceManagerStub.Calls - calls1) + " lookups");
    mappings.RemoveAt(mappings.Count - 1);
    ProxyReset(); Child();
}

// A live connection accepted after the switch back must not keep the old profile (R-1040).
{
    var sessType = switcher.GetNestedType("ProxySession", BindingFlags.NonPublic);
    var actType = switcher.GetNestedType("ActingAs", BindingFlags.NonPublic);
    var sess = Activator.CreateInstance(sessType);
    var acting = Activator.CreateInstance(actType, new object[] { KIDS, "kid-token" });
    sessType.GetField("Acting").SetValue(sess, acting);
    var hctx = new DefaultHttpContext();
    hctx.Request.Method = "GET";
    hctx.Request.Headers["Upgrade"] = "websocket";
    hctx.Request.Headers["Sec-WebSocket-Version"] = "13";
    hctx.Request.Headers["Sec-WebSocket-Key"] = Convert.ToBase64String(new byte[16]);
    hctx.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpUpgradeFeature>(new UpgradeStub());
    switcher.GetMethod("InstallSocket", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { hctx, sess, acting });
    // Back on the picker before the handshake finishes.
    sessType.GetField("Acting").SetValue(sess, null);
    switcher.GetMethod("CloseSockets", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { sess });
    var feat = hctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpWebSocketFeature>();
    var accepted = feat.AcceptAsync(new Microsoft.AspNetCore.Http.WebSocketAcceptContext()).GetAwaiter().GetResult();
    var left = ((System.Collections.IDictionary)sessType.GetField("Sockets").GetValue(sess)).Count;
    Ok("a connection accepted after leaving the profile is closed, not kept", accepted.State != WebSocketState.Open && left == 0,
       accepted.State + " " + left + " kept");
}

// A flood of keypad tiles must not drop the folders still on screen (r2-04-03).
{
    var sessType = switcher.GetNestedType("ProxySession", BindingFlags.NonPublic);
    var kindType = switcher.GetNestedType("TileKind", BindingFlags.NonPublic);
    var make = switcher.GetMethod("MakeTile", BindingFlags.Static | BindingFlags.NonPublic);
    var sess = Activator.CreateInstance(sessType);
    object Mk(string kind, string key) => make.Invoke(null, new object[] { sess, Enum.Parse(kindType, kind), Guid.Empty, Guid.Empty, "", key, key });
    Guid TileIdOf(object t) => (Guid)t.GetType().GetProperty("Id").GetValue(t);
    var tileTable = (System.Collections.IDictionary)sessType.GetField("Tiles").GetValue(sess);
    var rootId = TileIdOf(Mk("ProfilesView", "root"));
    for (int i = 0; i < 6000; i++) Mk("Key", "k" + i);
    var lastId = TileIdOf(Mk("Key", "last"));
    Ok("after thousands of keypad tiles the folder on screen is still known", tileTable.Contains(rootId),
       tileTable.Count + " tiles");
    Ok("and the newest keypad tile is too, and the table stays bounded", tileTable.Contains(lastId) && tileTable.Count <= 5001,
       tileTable.Count + " tiles");
}

var logout = Get("/Sessions/Logout", method: "POST");
Ok("signing out of the app signs the profile out too",
   sessionLog.LoggedOut.Contains("tok-" + N(KIDS)) && logout.reachedJellyfin && seenAuth.Contains(MOON_TOKEN),
   string.Join(",", sessionLog.LoggedOut));

Console.WriteLine();
Console.WriteLine("── Profile sessions from before a restart (R-1037) ─────────────");

// Jellyfin's tokens do not expire. The picker forgets the profile session it minted when the
// server restarts, so nothing ever signed it out: one valid token per device and profile,
// for good. What it minted is kept on disk and ended at the next start.
var marksType = asm.GetType("Jellyfin.Profiles.Auth.PickerMarks");
var switcherType = asm.GetType("Jellyfin.Profiles.Auth.ProxySwitcher");
void Restart() => switcherType.GetMethod("SimulateRestart", BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, null);
bool Marked(string token) => marksType?.GetMethod("Find", BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, new object[] { token }) != null;
void Sweep() => ((Task)marksType?.GetMethod("SweepAsync", BindingFlags.Static | BindingFlags.NonPublic)
    ?.Invoke(null, new object[] { sessions, devices, NullLogger.Instance }))?.GetAwaiter().GetResult();

Ok("this build remembers which TVs are in a child's profile", marksType != null);
Child();
Ok("a TV in a child's profile is on record", Marked(MOON_TOKEN));
{
    var before = sessionLog.LoggedOut.Count;
    Restart();
    Sweep();
    Ok("after a restart the profile session it made is ended", sessionLog.LoggedOut.Skip(before).Contains("tok-" + N(KIDS)), string.Join(",", sessionLog.LoggedOut.Skip(before)));
    Ok("and the main account's token is not touched", !sessionLog.LoggedOut.Skip(before).Contains(MOON_TOKEN));
    Ok("the TV is still on record, since its main token is still good", Marked(MOON_TOKEN));
    var again2 = sessionLog.LoggedOut.Count;
    Sweep();
    Ok("and a second sweep ends nothing twice", sessionLog.LoggedOut.Count == again2, string.Join(",", sessionLog.LoggedOut.Skip(again2)));
}

Child();
{
    Restart();
    deviceTokens.Remove(MOON_TOKEN);   // the TV signed out while the server was down
    Sweep();
    Ok("a record whose main token is gone is dropped", !Marked(MOON_TOKEN));
    deviceTokens[MOON_TOKEN] = (MASTER, "Moonfin for Android TV");
}

Child();
Children(IdOf(Get("/UserViews").body, "Switch profile"));
Ok("a TV back on the picker is no longer on record", !Marked(MOON_TOKEN));
Ok("and the picker's source no longer says its sessions are left to expire",
   !File.ReadAllText(Path.Combine(RepoRoot(), "Auth", "ProxySwitcher.cs")).Contains("left to expire"));
ProxyReset();
Set("ProxyClients", new List<string> { "moonfin" });

Console.WriteLine();
Console.WriteLine("── A request racing the first read of the marks file (r2-03-01) ─");

// Load used to say "loaded" before it had read anything, and Any read that flag without the
// lock, so a request arriving during the read saw no marks: a child's TV on the main token
// was served as the main account. A slow read is made with a big file.
{
    ProxyReset();
    var marksFile = Path.Combine(tempDir, "plugins", "ProfilesManagement", "proxy_picker.json");
    Directory.CreateDirectory(Path.GetDirectoryName(marksFile));
    var rows = new StringBuilder("[");
    for (int i = 0; i < 400000; i++)
    {
        if (i > 0) rows.Append(',');
        rows.Append("{\"MasterTokenHash\":\"H").Append(i).Append("\",\"MasterId\":\"" + Guid.Empty + "\",\"ProfileId\":\"" + Guid.Empty + "\",\"ProfileToken\":\"\"}");
    }
    rows.Append(']');
    File.WriteAllText(marksFile, rows.ToString());
    var anyProp = marksType.GetProperty("Any", BindingFlags.Static | BindingFlags.NonPublic);
    var loadedField = marksType.GetField("_loaded", BindingFlags.Static | BindingFlags.NonPublic);
    marksType.GetMethod("Reload", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
    var loader = Task.Run(() => (bool)anyProp.GetValue(null));
    // Wait until the loader is inside Load, whichever way it signals that.
    var spin = System.Diagnostics.Stopwatch.StartNew();
    while (!(bool)loadedField.GetValue(null) && !loader.IsCompleted && spin.ElapsedMilliseconds < 3000) Thread.Sleep(0);
    var racing = (bool)anyProp.GetValue(null);
    Ok("a request that arrives while the marks are being read waits for them and sees them", racing && loader.GetAwaiter().GetResult(), "racing=" + racing);
    File.Delete(marksFile);
    marksType.GetMethod("Clear", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
    marksType.GetMethod("Reload", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
    ProxyReset();
}

Console.WriteLine();
Console.WriteLine("── A restart does not let a child's TV onto the main account ───");

// The picker's sessions and its marks on a child's TV are in memory. A restart emptied both,
// so a TV that was in a child's profile sent its next request on the main account's token with
// nothing of ours looking at it, and whenever nothing of ours was going to answer (the app
// unticked, the profiles deleted, the emergency code, a bare image request) Jellyfin did,
// as the main account. What was kept on disk is what is checked here.
{
    var before = sessionLog.LoggedOut.Count;
    Child();
    Restart();
    Set("ProxyClients", new List<string>());
    var r = Get("/UserViews");
    Ok("after a restart, the app unticked: a TV that was in a child's profile is not passed on as the main account",
       !r.reachedJellyfin && r.status == 401, r.status + " " + seenAuth);
    Ok("and is signed out, the profile's session with it", sessionLog.LoggedOut.Skip(before).Contains(MOON_TOKEN)
       && sessionLog.LoggedOut.Skip(before).Contains("tok-" + N(KIDS)), string.Join(",", sessionLog.LoggedOut.Skip(before)));
    Ok("and is then no longer on record", !Marked(MOON_TOKEN));
    var later = Get("/UserViews");
    Ok("what is asked after that is the app's own, as it always was", later.reachedJellyfin && seenAuth.Contains(MOON_TOKEN), seenAuth);
    Set("ProxyClients", new List<string> { "moonfin" });
}

Child();
{
    Restart();
    Set("ProxyClients", new List<string> { "some-other-app" });
    var r = Get("/UserViews");
    Ok("after a restart, another app ticked: still not the main account", !r.reachedJellyfin && r.status == 401, r.status + " " + seenAuth);
    Set("ProxyClients", new List<string> { "moonfin" });
}

Child();
{
    Restart();
    var held = mappings.Cast<object>().ToList();
    mappings.Clear();
    var r = Get("/UserViews");
    foreach (var m in held) mappings.Add(m);
    Ok("after a restart, the profiles deleted: still not the main account", !r.reachedJellyfin && r.status == 401, r.status + " " + seenAuth);
}

if (panicField != null)
{
    Child();
    Restart();
    panicField.SetValue(null, true);
    try
    {
        var r = Get("/UserViews");
        Ok("after a restart, the emergency code: still not the main account", !r.reachedJellyfin && r.status == 401, r.status + " " + seenAuth);
    }
    finally { panicField.SetValue(null, false); }
}

Child();
{
    Restart();
    var r = Get("/Videos/abc/hls1/main/1.ts", bare: true);
    Ok("after a restart, a request that names no app (an image, a stream) is not the main account either", !r.reachedJellyfin && r.status == 401, r.status + " " + seenAuth);
}

Child();
{
    // The picker is on, as it was: the TV starts at the picker, and the profile it had is ended.
    var before = sessionLog.LoggedOut.Count;
    Restart();
    var r = Get("/UserViews");
    Ok("after a restart, with the picker on, the TV starts at the picker", !r.reachedJellyfin && Names(r.body) == "Profiles", Names(r.body) + " " + seenAuth);
    Ok("and the profile's old session is ended", sessionLog.LoggedOut.Skip(before).Contains("tok-" + N(KIDS)), string.Join(",", sessionLog.LoggedOut.Skip(before)));
    Ok("and is no longer on record", !Marked(MOON_TOKEN));
}

Child();
{
    // A Jellyfin that cannot sign it out: refused now, and still refused, not let through.
    SessionManagerStub.FailLogout.Add(MOON_TOKEN);
    Restart();
    Set("ProxyClients", new List<string>());
    var first = Get("/UserViews");
    var second = Get("/UserViews");
    SessionManagerStub.FailLogout.Clear();
    Ok("after a restart, when Jellyfin cannot sign it out, it is refused and stays refused", !first.reachedJellyfin && first.status == 401
       && !second.reachedJellyfin && second.status == 401, first.status + "," + second.status);
    Set("ProxyClients", new List<string> { "moonfin" });
}

// A TV that was only on the picker is not a child's, restart or not.
ProxyReset();
Set("ProxyClients", new List<string> { "moonfin" });
{
    Get("/UserViews");
    Restart();
    Set("ProxyClients", new List<string>());
    var r = Get("/UserViews");
    Ok("after a restart, a TV that was on the picker is passed on as before", r.reachedJellyfin && seenAuth.Contains(MOON_TOKEN), seenAuth);
    Set("ProxyClients", new List<string> { "moonfin" });
}
ProxyReset();

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

// A 401 is not always the token (r2-04-01): a plugin route that refuses a request answers
// 401 too, and that used to put the child back on the picker. Only a token Jellyfin no longer
// has does.
{
    Children(IdOf(Children(IdOf(after.body, "Profiles")), "Open"));
    var profileId = sessionLog.Minted.Last();
    var profileToken = "tok-" + N(profileId);
    deviceTokens[profileToken] = (profileId, "Moonfin for Android TV");
    var refusedRoute = Get("/Revoked");
    var still = Get("/UserViews");
    Ok("a route that answers 401 while the profile's token is good leaves the child in the profile",
       refusedRoute.status == 401 && still.reachedJellyfin && Names(still.body).StartsWith("Movies"), refusedRoute.status + " " + Names(still.body));
    deviceTokens.Remove(profileToken);
    Get("/Revoked");
    var gone = Get("/UserViews");
    Ok("and one whose token Jellyfin no longer has still goes back to the picker", !gone.reachedJellyfin && Names(gone.body) == "Profiles", Names(gone.body));
    after = gone;
}

// A main account turned off in Dashboard -> Users shuts its profiles too (R-1135). The
// picker checked only the profile's own account, which is never the one that was turned off.
var mintedBefore = sessionLog.Minted.Count;
UserManagerStub.Disabled.Add(MASTER);
var shut = Children(IdOf(Children(IdOf(after.body, "Profiles")), "Open"));
Ok("a profile whose main account is turned off is not opened", sessionLog.Minted.Count == mintedBefore, Names(shut));
Ok("and the keypad says why", Names(shut).Contains("turned off"), Names(shut));
UserManagerStub.Disabled.Remove(MASTER);

// Closed for maintenance. The picker refused a profile with "Closed for maintenance.", its
// own two words, while every other way in says the administrator's message. Text only: the
// profile still does not open, and nothing else about the keypad changes.
var maintType = asm.GetType("Jellyfin.Profiles.Configuration.MaintenanceMode", true);
object Closed(string message)
{
    var m = Activator.CreateInstance(maintType);
    maintType.GetProperty("Enabled").SetValue(m, true);
    maintType.GetProperty("Message").SetValue(m, message);
    return m;
}
// Only an administrator's token gets as far as the picker while closed, so the main account is one.
UserManagerStub.Admins.Add(MASTER);
cfgType.GetProperty("Maintenance").SetValue(config, Closed("Back at nine, after the upgrade."));
var mintedClosed = sessionLog.Minted.Count;
var closedPad = Children(IdOf(Children(IdOf(after.body, "Profiles")), "Open"));
Ok("closed for maintenance, the keypad says the administrator's message",
   Names(closedPad) == "Back at nine, after the upgrade.", Names(closedPad));
Ok("and the profile is still not opened", sessionLog.Minted.Count == mintedClosed);
cfgType.GetProperty("Maintenance").SetValue(config, Closed(string.Empty));
var closedDefault = Children(IdOf(Children(IdOf(after.body, "Profiles")), "Open"));
Ok("with no message written, it is the default one, from the app's translations", Names(closedDefault) == "Closed for maintenance.", Names(closedDefault));
cfgType.GetProperty("Maintenance").SetValue(config, Activator.CreateInstance(maintType));
var reopenedPad = Children(IdOf(Children(IdOf(after.body, "Profiles")), "Open"));
Ok("open again, the profile is not turned away", !Names(reopenedPad).Contains("maintenance") && sessionLog.Minted.Count == mintedClosed + 1, Names(reopenedPad));
Children(IdOf(Get("/UserViews").body, "Switch profile"));
UserManagerStub.Admins.Remove(MASTER);

// Issue #41, picker side: Jellyfin counts the profile's earlier session on this device
// against MaxActiveSessions before it replaces it, so the picker ends it first, as /switch does.
Console.WriteLine();
Console.WriteLine("── Earlier sessions on this device ─────────────────────────────");
var stale = new SessionInfo(sessions, NullLogger.Instance) { Client = "Moonfin for Android TV", DeviceId = "moon-1", Id = "stale-open", UserId = OPEN };
var theirs = new SessionInfo(sessions, NullLogger.Instance) { Client = "Moonfin for Android TV", DeviceId = "moon-1", Id = "main-own", UserId = MASTER };
var elsewhere = new SessionInfo(sessions, NullLogger.Instance) { Client = "Jellyfin Android TV", DeviceId = "living-room-tv", Id = "tv", UserId = OPEN };
SessionManagerStub.Existing.AddRange(new[] { stale, theirs, elsewhere });
var viewsNow = Get("/UserViews");
var order = SessionManagerStub.Order.Count;
Children(IdOf(Children(IdOf(viewsNow.body, "Profiles")), "Open"));
Ok("the profile's earlier session on this device is ended before it signs in",
   SessionManagerStub.EndedIds.Contains("stale-open"), string.Join(",", SessionManagerStub.EndedIds));
Ok("it is ended first, not after Jellyfin counted it",
   SessionManagerStub.Order.Skip(order).SkipWhile(x => x != "end:stale-open").Contains("auth"),
   string.Join(",", SessionManagerStub.Order.Skip(order)));
Ok("the main account's own session and the profile's on another device are left alone",
   !SessionManagerStub.EndedIds.Contains("main-own") && !SessionManagerStub.EndedIds.Contains("tv"),
   string.Join(",", SessionManagerStub.EndedIds));

// Back to the picker, then Jellyfin refuses with its own exception type (the catch named
// System.Security's, so every refusal read "Couldn't open this profile.").
Children(IdOf(Get("/UserViews").body, "Switch profile"));
SessionManagerStub.Existing.Clear();
SessionManagerStub.ThrowMessage = "User is at their maximum number of sessions.";
var limited = Children(IdOf(Children(IdOf(Get("/UserViews").body, "Profiles")), "Open"));
Ok("a session limit says too many screens use the profile", Names(limited).Contains("Too many screens use this profile"), Names(limited));
SessionManagerStub.ThrowMessage = "User is not allowed access from this device.";
var barred = Children(IdOf(Children(IdOf(Get("/UserViews").body, "Profiles")), "Open"));
Ok("a device refusal says not allowed on this device", Names(barred).Contains("Not allowed on this device"), Names(barred));
SessionManagerStub.ThrowMessage = null;

Console.WriteLine();
Console.WriteLine("── Another app's requests cost nothing ─────────────────────────");

var calls = DeviceManagerStub.Calls;
for (var i = 0; i < 20; i++) Get("/Videos/abc/hls1/main/" + i + ".ts", token: ATV_TOKEN, client: "Jellyfin Android TV");
Ok("requests naming another app never reach the database", DeviceManagerStub.Calls == calls, (DeviceManagerStub.Calls - calls) + " lookups");

Console.WriteLine();
Console.WriteLine("── The main account's own tile ─────────────────────────────────");

// The app stays signed in as the main account, so this tile is the way back into the
// unrestricted account from a child's profile. It used to open without the PIN.
var mainRow = Map(MASTER, MASTER, "Bard", pin: "5678");
mappings.Add(mainRow);
var mainList = Children(IdOf(Get("/UserViews").body, "Profiles"));
var mainPad = Children(IdOf(mainList, "Bard"));
Ok("with a PIN set, the main account's tile asks for it like any other",
   Names(mainPad) == "1, 2, 3, 4, 5, 6, 7, 8, 9, 0, Delete, OK", Names(mainPad));
var stillPicker = Get("/UserViews");
Ok("and opens nothing until it is typed", !stillPicker.reachedJellyfin && Names(stillPicker.body) == "Profiles", Names(stillPicker.body));
// OK before four digits is not a guess. It spent one of the five tries, so five presses on
// an empty keypad locked the profile on every app (R-1034). Each press is on the keypad the
// last one returned, as the app does; the same tile twice is the app's reload, not a press.
var shortOk = mainPad;
for (var i = 0; i < 6; i++) shortOk = Press(shortOk, "OK");
Ok("OK on an empty keypad just shows the keypad again", Names(shortOk) == Names(mainPad), Names(shortOk));
var twoDigits = Press(Press(mainPad, "1"), "2");
var twoThenOk = Press(twoDigits, "OK ••");
Ok("so does OK with too few digits, keeping what was typed", Names(twoThenOk) == Names(twoDigits), Names(twoThenOk));
var mainWrong = Press(Press(Press(Press(Press(mainPad, "1"), "2"), "3"), "4"), "OK ••••");
Ok("a profile's PIN does not open it", Names(mainWrong).StartsWith("Wrong PIN"), Names(mainWrong));
var mainIn = Press(Press(Press(Press(Press(mainWrong, "5"), "6"), "7"), "8"), "OK ••••");
var mainHome = Get("/UserViews");
Ok("its own PIN does", Names(mainIn) == "Movies" && mainHome.reachedJellyfin && seenAuth.Contains(MOON_TOKEN), Names(mainIn) + " " + seenAuth);

var askProp = mapType.GetProperty("AskPinToOpen");
if (askProp == null)
{
    Ok("its owner can choose not to be asked", false, "no AskPinToOpen in this build");
}
else
{
    Children(IdOf(mainHome.body, "Switch profile"));
    askProp.SetValue(mainRow, false);
    var openList = Children(IdOf(Get("/UserViews").body, "Profiles"));
    var direct = Children(IdOf(openList, "Bard"));
    Ok("with \"Ask for the PIN to open this account\" off, it opens without one", Names(direct) == "Movies", Names(direct));

    // A sub-profile's own row with the same flag off is still asked: it is a main-account setting.
    Children(IdOf(Get("/UserViews").body, "Switch profile"));
    var kidsRow = mappings.Cast<object>().First(m => (Guid)mapType.GetProperty("ProfileUserId").GetValue(m) == KIDS);
    askProp.SetValue(kidsRow, false);
    var kidsPad = Children(IdOf(Children(IdOf(Get("/UserViews").body, "Profiles")), "Kids"));
    Ok("the setting means nothing on a sub-profile, which is still asked", Names(kidsPad).EndsWith("Delete, OK"), Names(kidsPad));
    askProp.SetValue(kidsRow, true);
}
mappings.Remove(mainRow);

Console.WriteLine();
Console.WriteLine("── The main account signed out in Jellyfin ─────────────────────");

// R-1030: a known token was never asked about again. An administrator signing the TV out, or
// a password change, left the picker listing profiles and opening PIN-less ones until restart.
var clockField = switcher.GetField("Clock", BindingFlags.Static | BindingFlags.NonPublic);
if (clockField == null)
{
    Ok("the picker rechecks a known main token on a clock", false, "no Clock in this build");
}
else
{
    var realNow = DateTime.UtcNow;
    var skew = TimeSpan.Zero;
    clockField.SetValue(null, (Func<DateTime>)(() => realNow + skew));
    skew += TimeSpan.FromSeconds(60);
    Get("/UserViews");                       // the first look after a minute: one lookup, stamped
    var lookups = DeviceManagerStub.Calls;
    skew += TimeSpan.FromSeconds(10);
    for (var i = 0; i < 20; i++) Get("/UserViews");
    Ok("a token seen a few seconds ago is not asked about again, however many requests",
       DeviceManagerStub.Calls == lookups, (DeviceManagerStub.Calls - lookups) + " lookups");
    skew += TimeSpan.FromSeconds(60);
    Get("/UserViews");
    Ok("but a minute later it is asked, once", DeviceManagerStub.Calls == lookups + 1, (DeviceManagerStub.Calls - lookups) + " lookups");

    var minted = sessionLog.Minted.Count;
    deviceTokens.Remove(MOON_TOKEN);        // signed out in Dashboard -> Devices
    skew += TimeSpan.FromSeconds(60);
    var gone = Get("/UserViews");
    Ok("once Jellyfin has signed the app out, the picker stops answering for it",
       gone.reachedJellyfin, gone.reachedJellyfin ? "" : "the picker still answered: " + Names(gone.body));
    var goneAgain = Get("/UserViews");
    Ok("and keeps passing it to Jellyfin, which refuses it", goneAgain.reachedJellyfin);
    var tryOpen = Get("/Users/" + N(MASTER) + "/Items?ParentId=" + openId);
    Ok("a tile it was shown opens nothing", tryOpen.reachedJellyfin && sessionLog.Minted.Count == minted, sessionLog.Minted.Count + " vs " + minted);

    deviceTokens[MOON_TOKEN] = (MASTER, "Moonfin for Android TV");
    clockField.SetValue(null, (Func<DateTime>)(() => DateTime.UtcNow));
}

Console.WriteLine();
Console.WriteLine("── A TV that never comes back is forgotten ─────────────────────");
{
    var sweep = switcher.GetMethod("SweepIdle", BindingFlags.Static | BindingFlags.NonPublic);
    var sessionsField = switcher.GetField("Sessions", BindingFlags.Static | BindingFlags.NonPublic);
    Ok("the picker can forget idle sessions", sweep != null && sessionsField != null);
    if (sweep != null && sessionsField != null && clockField != null)
    {
        const string IDLE_TOKEN = "abababababababababababababababab";
        var dict = (System.Collections.IDictionary)sessionsField.GetValue(null);
        Child();                                           // the Moonfin TV, in Kids
        deviceTokens[IDLE_TOKEN] = (MASTER, "Moonfin for Android TV");
        Get("/UserViews", IDLE_TOKEN);                     // another TV, on the picker
        Ok("(set-up) two sessions", dict.Count == 2, dict.Count.ToString());

        var t0 = DateTime.UtcNow;
        clockField.SetValue(null, (Func<DateTime>)(() => t0 + TimeSpan.FromDays(10)));
        var early = (int)sweep.Invoke(null, null);
        Ok("nothing is forgotten after ten days", early == 0 && dict.Count == 2, early + " removed");

        clockField.SetValue(null, (Func<DateTime>)(() => t0 + TimeSpan.FromDays(40)));
        var late = (int)sweep.Invoke(null, null);
        Ok("an idle session on the picker is forgotten after forty", late == 1 && !dict.Contains(IDLE_TOKEN), late + " removed");
        Ok("one in a child's profile is not, however long it has been quiet", dict.Contains(MOON_TOKEN), dict.Count.ToString());

        clockField.SetValue(null, (Func<DateTime>)(() => DateTime.UtcNow));
        var returned = Get("/UserViews", IDLE_TOKEN);
        Ok("and the TV that returns is given the picker again", !returned.reachedJellyfin && dict.Contains(IDLE_TOKEN));
        deviceTokens.Remove(IDLE_TOKEN);
        ProxyReset(); Child();
    }
}

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

// ── The picker's own words, in the app's language (P2-706) ───────────────────
Console.WriteLine();
Console.WriteLine("── The picker speaks the app's language ────────────────────────");
{
    // The app's Accept-Language decides, fixed when the session starts. The French words
    // come from Web/i18n/fr.json, the same file the mail's are.
    var fr = Get("/UserViews", token: FR_TOKEN, acceptLanguage: "fr-FR,fr;q=0.9,en;q=0.8");
    Ok("the folder is named in French", Names(fr.body) == "Profils", Names(fr.body));
    var frTiles = Get("/Users/" + N(MASTER) + "/Items?ParentId=" + IdOf(fr.body, "Profils") + "&Recursive=false", token: FR_TOKEN, acceptLanguage: "fr-FR");
    var frKids = (string)Items(frTiles.body).FirstOrDefault(i => ((string)i["Name"]) == "Kids")?["Id"];
    var frPad = Get("/Users/" + N(MASTER) + "/Items?ParentId=" + frKids + "&Recursive=false", token: FR_TOKEN, acceptLanguage: "fr-FR");
    Ok("the keypad's Delete and OK are too", Names(frPad.body) == "1, 2, 3, 4, 5, 6, 7, 8, 9, 0, Effacer, OK", Names(frPad.body));

    // A language the plugin has no file for falls back to English, key by key.
    var xx = Get("/UserViews", token: XX_TOKEN, acceptLanguage: "tlh");
    Ok("a language with no file is English", Names(xx.body) == "Profiles", Names(xx.body));
    // And an app that sends none, with nothing stored, is as before.
    var none = Get("/UserViews", token: NONE_TOKEN);
    Ok("no Accept-Language is English", Names(none.body) == "Profiles", Names(none.body));
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

public sealed class UpgradeStub : Microsoft.AspNetCore.Http.Features.IHttpUpgradeFeature
{
    public bool IsUpgradableRequest => true;
    public Task<Stream> UpgradeAsync() => Task.FromResult<Stream>(new MemoryStream());
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
