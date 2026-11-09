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
var FILM = Guid.NewGuid();
var SONG = Guid.NewGuid();
var OTHER = Guid.NewGuid();

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
const string KID_TOKEN = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
var devices = DeviceManagerStub.Create(new Dictionary<string, (Guid, string)>
{
    [ADMIN_TOKEN] = (ADMIN, "Jellyfin Web"),
    [MEMBER_TOKEN] = (MEMBER, "Jellyfin Web"),
    [MOON_TOKEN] = (ADMIN, "Moonfin for Android TV"),
    [KID_TOKEN] = (KID, "Jellyfin Web"),
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
string qcCode = "000000";
var next = new RequestDelegate(async c =>
{
    seenPath = c.Request.Path.Value;
    seenAuth = c.Request.Headers["Authorization"].ToString();
    using (var r = new StreamReader(c.Request.Body, Encoding.UTF8, false, 1024, leaveOpen: true))
        seenBody = await r.ReadToEndAsync();

    var body = "{}";
    // ASP.NET routes the trailing-slash form to the same action.
    if (seenPath.TrimEnd('/').EndsWith("/QuickConnect/Initiate", StringComparison.OrdinalIgnoreCase))
    {
        // What Jellyfin's QuickConnectManager answers: the code to show, and the device that asked.
        string Header(string key) => System.Text.RegularExpressions.Regex.Match(seenAuth, key + "=\"([^\"]*)\"").Groups[1].Value;
        body = "{\"Secret\":\"secret-" + qcCode + "\",\"Code\":\"" + qcCode + "\",\"DeviceId\":\"" + Header("DeviceId")
             + "\",\"DeviceName\":\"" + Header("Device") + "\",\"AppName\":\"" + Header("Client") + "\",\"AppVersion\":\"1.0\",\"Authenticated\":false}";
    }
    if (seenPath.EndsWith("/UserViews", StringComparison.OrdinalIgnoreCase))
        body = "{\"Items\":[{\"Name\":\"Movies\",\"Id\":\"11111111111111111111111111111111\",\"Type\":\"CollectionFolder\"}],\"TotalRecordCount\":1,\"StartIndex\":0}";
    c.Response.StatusCode = 200;
    c.Response.ContentType = "application/json";
    await c.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(body));
});
var instance = Activator.CreateInstance(mwType, next, Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(mwType)));

(int status, string body, bool reached, HttpResponse response) Send(
    string pathAndQuery, string token = null, string method = "GET", string body = null, string client = "Jellyfin Web",
    string deviceId = "dev-1")
{
    seenPath = seenBody = seenAuth = null;
    var ctx = new DefaultHttpContext { RequestServices = services };
    ctx.Request.Method = method;
    var q = pathAndQuery.IndexOf('?');
    ctx.Request.Path = q < 0 ? pathAndQuery : pathAndQuery[..q];
    if (q >= 0) ctx.Request.QueryString = new QueryString(pathAndQuery[q..]);
    ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
    ctx.Request.Headers["Authorization"] =
        "MediaBrowser Client=\"" + client + "\", Device=\"Shield\", DeviceId=\"" + deviceId + "\", Version=\"1.0\""
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
// What the member starts now is what keeps playing once it closes. "Already playing" is what
// the gate itself let start, not what a session says it is playing: any client can say that
// with POST /Sessions/Playing, no PlaybackInfo needed (R-1297).
Ok("the member starts a film", Through(Send("/Items/" + FILM + "/PlaybackInfo", MEMBER_TOKEN, "POST", "{}")));
Ok("and a song", Through(Send("/Items/" + SONG + "/PlaybackInfo", MEMBER_TOKEN, "POST", "{}")));

Console.WriteLine();
Console.WriteLine("── Closed: a household member ──────────────────────────────────");

Close(true, "Upgrading. Back at 3.", until: DateTime.UtcNow.AddMinutes(90));
var browse = Send("/Users/" + N(MEMBER) + "/Items", MEMBER_TOKEN);
Ok("browsing is refused before Jellyfin sees it", Refused(browse), Show(browse));
Ok("with the administrator's message as plain text",
   browse.body == "Upgrading. Back at 3. Try again later." && browse.response.ContentType.StartsWith("text/plain"), browse.body);
var retry = int.TryParse(browse.response.Headers["Retry-After"].ToString(), out var secs) ? secs : -1;
Ok("and Retry-After counts down to the time given", retry > 5300 && retry <= 5400, retry.ToString());
Ok("and is not cached", browse.response.Headers["Cache-Control"].ToString() == "no-store");

Ok("starting playback is refused", Refused(Send("/Items/abc/PlaybackInfo", MEMBER_TOKEN, "POST", "{}")));
// The member's app is playing FILM; SONG is playing on another of its sessions.
sessionLog.Sessions.Clear();
sessionLog.Sessions.Add(SessionManagerStub.Make("s-member-tv", MEMBER, playing: true, item: FILM));
sessionLog.Sessions.Add(SessionManagerStub.Make("s-member-phone", MEMBER, playing: true, item: SONG));
sessionLog.Sessions.Add(SessionManagerStub.Make("s-admin", ADMIN, playing: true, item: OTHER));
Ok("what is playing keeps playing: video segments", Through(Send("/videos/" + N(FILM) + "/hls1/main/3.mp4", MEMBER_TOKEN)));
Ok("a direct stream of it", Through(Send("/Videos/" + FILM + "/stream.mkv?static=true", MEMBER_TOKEN)));
Ok("audio", Through(Send("/Audio/" + N(SONG) + "/universal", MEMBER_TOKEN)));
Ok("progress reports", Through(Send("/Sessions/Playing/Progress", MEMBER_TOKEN, "POST", "{}")));
Ok("and the report that it stopped", Through(Send("/Sessions/Playing/Stopped", MEMBER_TOKEN, "POST", "{}")));
var fresh = Send("/Videos/" + N(OTHER) + "/stream.mkv?static=true", MEMBER_TOKEN);
Ok("but a direct stream of anything else is refused, PlaybackInfo or not", Refused(fresh), Show(fresh));
Ok("even what someone else is playing", Refused(Send("/videos/" + N(OTHER) + "/hls1/main/0.mp4", MEMBER_TOKEN)));
// POST /Sessions/Playing sets NowPlayingItem on the session with nothing checked. It used to
// be the whole test of "already playing", so reporting an item first let anyone stream it.
sessionLog.Sessions.Add(SessionManagerStub.Make("s-member-forged", MEMBER, playing: true, item: OTHER));
var forged = Send("/Videos/" + OTHER + "/stream.mkv?static=true", MEMBER_TOKEN);
Ok("an item the app only says it is playing is refused", Refused(forged), Show(forged));
Ok("and so is its audio", Refused(Send("/Audio/" + N(OTHER) + "/universal", MEMBER_TOKEN)));
Ok("a stream URL with no item id is refused", Refused(Send("/Audio/stream.mp3", MEMBER_TOKEN)));
Ok("and so is the report that starts something", Refused(Send("/Sessions/Playing", MEMBER_TOKEN, "POST", "{}")));
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
var seg = Send("/videos/" + N(FILM) + "/hls1/main/4.mp4", MEMBER_TOKEN);
Ok("video segments are refused too", Refused(seg), Show(seg));
Ok("an empty message gives the default", seg.body == "The server is closed for maintenance. Try again later.", seg.body);
Ok("with no end time, Retry-After is five minutes", seg.response.Headers["Retry-After"].ToString() == "300");
Ok("an administrator still streams", Through(Send("/videos/" + N(OTHER) + "/hls1/main/4.mp4", ADMIN_TOKEN)));

Console.WriteLine();
Console.WriteLine("── Cost ────────────────────────────────────────────────────────");

Close(true);
DeviceManagerStub.Calls = 0;
for (int i = 0; i < 50; i++) Send("/videos/" + N(FILM) + "/hls1/main/" + i + ".mp4", MEMBER_TOKEN);
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
Moon("/Items/" + FILM + "/PlaybackInfo");   // the kid starts the film while it is open

Close(true);
var signInByShownName = Send("/Users/AuthenticateByName", null, "POST", "{\"Username\":\"Kid\",\"Pw\":\"1234\"}");
Ok("signing in by a profile's shown name, which no account has, is refused too", Refused(signInByShownName), Show(signInByShownName));

sessionLog.Sessions.Clear();
sessionLog.Sessions.Add(SessionManagerStub.Make("s-kid", KID, playing: true, item: FILM));
var seg9 = Moon("/videos/" + N(FILM) + "/hls1/main/9.mp4");
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
Ok("and says why", Items(again.body).Any(i => ((string)i["Name"] ?? "").Contains("Back at 3")), again.body);

Console.WriteLine();
Console.WriteLine("── Quick Connect: a profile authorizing a device ───────────────");

// Authorizing a code makes Jellyfin create the session itself (QuickConnectManager calls
// AuthenticateDirect), for the device that asked, so it never reaches the PIN provider and
// none of its rules. A profile on its allowed device could sign in a device its household
// had not allowed, or one of the apps that only get the picker (R-1335). The device is the
// one that asked for the code, not the one authorizing it.
Close(false);
var allowedList = new List<string> { "dev-good" };
mapType.GetProperty("AllowedDeviceIds").SetValue(map, allowedList);
string Initiated(string code, string device, string client = "Jellyfin Web")
{
    qcCode = code;
    Send("/QuickConnect/Initiate", null, "POST", "", client: client, deviceId: device);
    return code;
}
(int status, string body, bool reached, HttpResponse response) Authorize(string code, string token, string extra = "", string client = "Jellyfin Web")
    => Send("/QuickConnect/Authorize?code=" + code + extra, token, "POST", "", client: client);

var qcBad = Authorize(Initiated("111111", "dev-bad"), KID_TOKEN);
Ok("a profile cannot authorize a device that is not on its list", !qcBad.reached && qcBad.status == 403, Show(qcBad));
Ok("and the refusal says so, in a code an app can read", qcBad.response.Headers["X-Bonfire-Error"].ToString() == "device_not_allowed", qcBad.response.Headers["X-Bonfire-Error"].ToString());
Ok("a device on its list is authorized", Through(Authorize(Initiated("222222", "dev-good"), KID_TOKEN)));
var qcUnknown = Authorize("333333", KID_TOKEN);
Ok("a code this server never saw asked for is refused: its device cannot be judged", !qcUnknown.reached && qcUnknown.status == 403, Show(qcUnknown));
Ok("the main account authorizes any device", Through(Authorize(Initiated("444444", "dev-bad"), ADMIN_TOKEN)));
var qcFor = Authorize(Initiated("555555", "dev-bad"), ADMIN_TOKEN, "&userId=" + KID);
Ok("and is judged as the profile when it authorizes for one", !qcFor.reached && qcFor.status == 403, Show(qcFor));

allowedList.Clear();
Ok("a profile with no list authorizes any device", Through(Authorize(Initiated("666666", "dev-bad"), KID_TOKEN)));
// A code the server never saw (after a restart) is refused only where a rule could have
// refused it. With no list and no picker app ticked nothing could, and refusing would break
// an ordinary household's Quick Connect for ten minutes after every restart.
var qcUnknownTicked = Authorize("333334", KID_TOKEN);
Ok("an unknown code is still refused to an unrestricted profile while a picker app is ticked", !qcUnknownTicked.reached && qcUnknownTicked.status == 403, Show(qcUnknownTicked));
Set("ProxyClients", new List<string>());
Ok("but allowed to an unrestricted profile when no rule could apply", Through(Authorize("333335", KID_TOKEN)));
Set("ProxyClients", new List<string> { "moonfin" });
var qcMoon = Authorize(Initiated("777777", "dev-tv", "Moonfin for Android TV"), KID_TOKEN);
Ok("but not an app that gets the picker instead", !qcMoon.reached && qcMoon.status == 403, Show(qcMoon));
Ok("nor, for the main account, is that app refused", Through(Authorize(Initiated("888888", "dev-tv", "Moonfin for Android TV"), ADMIN_TOKEN)));

// Through the picker: the app's token is the main account's, the request reaches Jellyfin as the kid's.
var views3 = Moon("/UserViews");
var tiles3 = Moon("/Users/" + N(ADMIN) + "/Items?ParentId=" + IdOf(views3.body, "Profiles")).body;
Moon("/Users/" + N(ADMIN) + "/Items?ParentId=" + IdOf(tiles3, "Kid"));
allowedList.Add("dev-good");   // the kid is open on this television; the list is for the next device
var qcPicker = Send("/QuickConnect/Authorize?code=" + Initiated("999999", "dev-bad"), MOON_TOKEN, "POST", "", client: "Moonfin for Android TV");
Ok("a profile open on the picker is judged too", !qcPicker.reached && qcPicker.status == 403, Show(qcPicker));
Ok("and a device on its list goes through the picker as it would directly",
   Through(Send("/QuickConnect/Authorize?code=" + Initiated("101010", "dev-good"), MOON_TOKEN, "POST", "", client: "Moonfin for Android TV")), seenAuth);
allowedList.Clear();

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
Console.WriteLine("── Who is asking is Jellyfin's to say ──────────────────────────");

// The gate read the headers itself and disagreed with Jellyfin four ways; where it found
// no user it let the request through, and Jellyfin then served it as the member (R-1295).
// Here the authorization context answers as Jellyfin does for each shape.
(int status, string body, bool reached, HttpResponse response) Crafted(Action<HttpRequest> shape, Guid? jellyfinSays, bool apiKey = false)
{
    seenPath = seenBody = seenAuth = null;
    var sp = new ServiceCollection()
        .AddSingleton(devices)
        .AddSingleton(sessions)
        .AddSingleton(userManager)
        .AddSingleton(NetworkStub.Create())
        .AddSingleton<MediaBrowser.Controller.Net.IAuthorizationContext>(new JellyfinSays(jellyfinSays, apiKey))
        .BuildServiceProvider();
    var ctx = new DefaultHttpContext { RequestServices = sp };
    ctx.Request.Method = "GET";
    ctx.Request.Path = "/Users/" + N(MEMBER) + "/Items";
    ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
    shape(ctx.Request);
    var sink = new MemoryStream();
    ctx.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>(new StreamResponseBodyFeature(sink));
    var invoke = mwType.GetMethod("Invoke");
    var args = invoke.GetParameters().Select(p =>
        p.ParameterType == typeof(HttpContext) ? ctx
        : p.ParameterType == typeof(IUserManager) ? userManager
        : p.ParameterType == typeof(IDeviceManager) ? devices
        : p.ParameterType == typeof(ISessionManager) ? sessions
        : p.ParameterType == typeof(INetworkManager) ? sp.GetService<INetworkManager>()
        : (object)null).ToArray();
    ((Task)invoke.Invoke(instance, args)).GetAwaiter().GetResult();
    return (ctx.Response.StatusCode, Encoding.UTF8.GetString(sink.ToArray()), seenPath != null, ctx.Response);
}

Close(true);
var crafted = Crafted(r => r.Headers["Authorization"] = "Emby Token=\"" + MEMBER_TOKEN + "\", Client=\"x\"", MEMBER);
Ok("the Emby scheme, which Jellyfin reads with legacy authorization on, is the member", Refused(crafted), Show(crafted));
crafted = Crafted(r => { r.Headers["Authorization"] = "Foo Client=\"x\", Token=\"junk\""; r.QueryString = new QueryString("?ApiKey=" + MEMBER_TOKEN); }, MEMBER);
Ok("so is a junk scheme with the member's token as ?ApiKey", Refused(crafted), Show(crafted));
crafted = Crafted(r => r.Headers["Authorization"] = "MediaBrowser Token=\"" + ADMIN_TOKEN + "\", Token=\"" + MEMBER_TOKEN + "\"", MEMBER);
Ok("so are two tokens, where Jellyfin takes the last", Refused(crafted), Show(crafted));
crafted = Crafted(r => r.Headers["Authorization"] = "MediaBrowser Token=\"" + MEMBER_TOKEN + "\"", ADMIN);
Ok("and an administrator stays an administrator, whatever the header says", Through(crafted), Show(crafted));
crafted = Crafted(r => r.QueryString = new QueryString("?ApiKey=" + API_KEY), null, apiKey: true);
Ok("an API key is no account and is let through", Through(crafted), Show(crafted));
Close(false);

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

Console.WriteLine();
Console.WriteLine("── Reopening by itself ─────────────────────────────────────────");

// The administrator's page is never open in any of this: nothing here calls a route. The
// clock alone has to do it.
var reopenProp = modeType.GetProperty("ReopenAutomatically");
Ok("a closure can ask to reopen at its end time", reopenProp != null);
if (reopenProp != null)
{
    bool SavedOpen() => !(bool)modeType.GetProperty("Enabled").GetValue(cfgType.GetProperty("Maintenance").GetValue(config));
    void CloseUntil(DateTime until, bool reopen)
    {
        Close(true, "Upgrading.", until: until);
        var m = cfgType.GetProperty("Maintenance").GetValue(config);
        reopenProp.SetValue(m, reopen);
    }
    var memberRequest = "/Users/" + N(MEMBER) + "/Items";

    CloseUntil(DateTime.UtcNow.AddHours(1), true);
    Ok("before its time it is closed as usual", Refused(Send(memberRequest, MEMBER_TOKEN)));

    CloseUntil(DateTime.UtcNow.AddSeconds(-1), true);
    var lapsedInfo = JsonNode.Parse(Info());
    Ok("at its time /info stops saying it is closed, before anything has saved that",
       lapsedInfo is JsonObject lo && lo["Maintenance"] == null && !SavedOpen(), lapsedInfo?.ToJsonString());
    var after = Send(memberRequest, MEMBER_TOKEN);
    Ok("and a household member gets through, with nobody having touched the settings", Through(after), Show(after));
    Ok("their sign-in too", Through(Send("/Users/AuthenticateByName", null, "POST", "{\"Username\":\"Guest\",\"Pw\":\"x\"}")));
    Ok("and the first request writes it down as open", SavedOpen());
    Ok("the administrator is as welcome as ever", Through(Send(memberRequest.Replace(N(MEMBER), N(ADMIN)), ADMIN_TOKEN)));

    // The other half: a time that is only shown is still only shown.
    CloseUntil(DateTime.UtcNow.AddSeconds(-1), false);
    var shown = Send(memberRequest, MEMBER_TOKEN);
    Ok("without the tick a time that has passed changes nothing", Refused(shown), Show(shown));
    Ok("and nothing is written down", !SavedOpen());
    Close(false);
}

Console.WriteLine();
Console.WriteLine("── Paths that are open ─────────────────────────────────────────");

Close(true);
// An open path is matched where Jellyfin routes it, at the start of the path after the base
// URL, not anywhere in it: a segment that happens to read "web" in the middle of a stream
// URL must not make the request open.
var innerWeb = Send("/Videos/" + N(OTHER) + "/hls1/web/3.ts", MEMBER_TOKEN);
Ok("a stream of something else is refused whatever its segments are called", Refused(innerWeb), Show(innerWeb));
var innerMe = Send("/Users/" + N(MEMBER) + "/Items/Users/Me", MEMBER_TOKEN);
Ok("and so is a path that only ends in an open one", Refused(innerMe), Show(innerMe));
Ok("a path with the open one in the middle is refused", Refused(Send("/Items/" + N(OTHER) + "/Images/socket/0", MEMBER_TOKEN)));
Ok("the web client's own files still load", Through(Send("/web/index.html")));

var classify = gateType.GetMethod("Classify", BindingFlags.Static | BindingFlags.NonPublic);
string Kind(string method, string path, string baseUrl)
{
    var ps = classify.GetParameters();
    var args = ps.Length >= 3 ? new object[] { method, path, baseUrl } : new object[] { method, path };
    return classify.Invoke(null, args).ToString();
}
Ok("under a base URL the web client's files are still open", Kind("GET", "/jf/web/index.html", "/jf") == "Open", Kind("GET", "/jf/web/index.html", "/jf"));
Ok("and a segment named web under it is not", Kind("GET", "/jf/Videos/" + N(OTHER) + "/hls1/web/3.ts", "/jf") != "Open", Kind("GET", "/jf/Videos/" + N(OTHER) + "/hls1/web/3.ts", "/jf"));

Console.WriteLine();
Console.WriteLine("── The sign-in check ───────────────────────────────────────────");

Close(true);
Ok("a sign-in under a base URL is still a sign-in", Kind("POST", "/jf/Users/AuthenticateByName", "/jf") == "SignIn", Kind("POST", "/jf/Users/AuthenticateByName", "/jf"));
var guestBody = "{\"Username\":\"Guest\",\"Pw\":\"x\"}";
Ok("a sign-in by name with a trailing slash is refused", Refused(Send("/Users/AuthenticateByName/", null, "POST", guestBody)));
Ok("an administrator's is not", Through(Send("/Users/AuthenticateByName/", null, "POST", "{\"Username\":\"Bard\",\"Pw\":\"x\"}")));
// Jellyfin binds the body with System.Text.Json, where a repeated key keeps the last value.
var twice = Send("/Users/AuthenticateByName", null, "POST", "{\"Username\":\"Bard\",\"Pw\":\"x\",\"Username\":\"Guest\"}");
Ok("a name given twice is the last one, as Jellyfin reads it", Refused(twice), Show(twice));
var twiceAdmin = Send("/Users/AuthenticateByName", null, "POST", "{\"Username\":\"Guest\",\"Pw\":\"x\",\"Username\":\"Bard\"}");
Ok("and an administrator named last is let through", Through(twiceAdmin), Show(twiceAdmin));
var huge = Send("/Users/AuthenticateByName", null, "POST", "{\"Username\":\"Bard\",\"Pw\":\"" + new string('x', 70000) + "\"}");
Ok("a sign-in body too large to read is refused rather than passed", Refused(huge), Show(huge));
Ok("a body that is not JSON is still left to Jellyfin", Through(Send("/Users/AuthenticateByName", null, "POST", "Username=Guest")));
Close(true);

Console.WriteLine();
Console.WriteLine("── Quick Connect with a trailing slash ─────────────────────────");

Close(false);
var qcList = new List<string> { "dev-good" };
mapType.GetProperty("AllowedDeviceIds").SetValue(map, qcList);
Set("ProxyClients", new List<string> { "moonfin" });
qcCode = "121212";
Send("/QuickConnect/Initiate/", null, "POST", "", deviceId: "dev-good");
var slashAuth = Send("/QuickConnect/Authorize?code=121212", KID_TOKEN, "POST", "");
Ok("the code asked for on a trailing-slash Initiate is remembered, so its device can be judged", Through(slashAuth), Show(slashAuth));
qcList.Clear();

Console.WriteLine();
Console.WriteLine("── A stream on the older playback routes ───────────────────────");

Close(true);
var legacyProgress = Send("/Users/" + N(MEMBER) + "/PlayingItems/" + N(FILM) + "/Progress", MEMBER_TOKEN, "POST", "{}");
Ok("progress on the user-scoped route keeps going", Through(legacyProgress), Show(legacyProgress));
Ok("and on the plain one", Through(Send("/PlayingItems/" + N(FILM) + "/Progress", MEMBER_TOKEN, "POST", "{}")));
Ok("the stop report on the user-scoped route", Through(Send("/Users/" + N(MEMBER) + "/PlayingItems/" + N(FILM), MEMBER_TOKEN, "DELETE")));
Ok("and on the plain one", Through(Send("/PlayingItems/" + N(FILM), MEMBER_TOKEN, "DELETE")));
var legacyStart = Send("/Users/" + N(MEMBER) + "/PlayingItems/" + N(OTHER), MEMBER_TOKEN, "POST", "{}");
Ok("the report that starts something on it is refused", Refused(legacyStart), Show(legacyStart));
Ok("and on the plain one", Refused(Send("/PlayingItems/" + N(OTHER), MEMBER_TOKEN, "POST", "{}")));
Close(true, "", stop: true);
Ok("asked to stop playback, its progress is refused too", Refused(Send("/PlayingItems/" + N(FILM) + "/Progress", MEMBER_TOKEN, "POST", "{}")));
Close(true);

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

    public static SessionInfo Make(string id, Guid user, bool playing, Guid item = default)
    {
        var t = typeof(SessionInfo);
        var s = (SessionInfo)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t);
        t.GetProperty("Id").SetValue(s, id);
        t.GetProperty("UserId").SetValue(s, user);
        t.GetProperty("NowPlayingItem").SetValue(s, playing ? new BaseItemDto { Id = item } : null);
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

// Jellyfin's IAuthorizationContext, answering as Jellyfin decided for the request in hand:
// the account the token belongs to, an API key, or nobody.
sealed class JellyfinSays : MediaBrowser.Controller.Net.IAuthorizationContext
{
    private readonly Guid? _user;
    private readonly bool _apiKey;
    public JellyfinSays(Guid? user, bool apiKey) { _user = user; _apiKey = apiKey; }

    public Task<MediaBrowser.Controller.Net.AuthorizationInfo> GetAuthorizationInfo(HttpContext requestContext)
        => GetAuthorizationInfo(requestContext.Request);

    public Task<MediaBrowser.Controller.Net.AuthorizationInfo> GetAuthorizationInfo(HttpRequest requestContext)
    {
        var info = new MediaBrowser.Controller.Net.AuthorizationInfo { Token = "t", IsAuthenticated = _user != null || _apiKey, IsApiKey = _apiKey };
        if (_user is Guid id)
        {
            var u = new User("someone", "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider",
                "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider");
            typeof(User).GetProperty("Id").SetValue(u, id);
            info.User = u;
        }
        return Task.FromResult(info);
    }
}
