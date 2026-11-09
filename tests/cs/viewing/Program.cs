using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Entities.Security;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
using MediaBrowser.Model.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

// ─────────────────────────────────────────────────────────────────────────────
// Viewing hours and daily limits (1.7.2).
//
// What it holds the plugin to:
//
//  - The clock: windows per weekday, one that runs past midnight, two that touch, a day's
//    limit and a different one at weekends, extra time. Bedtime stays at 20:00 local on
//    both sides of a clock change, and the day resets at local midnight, not UTC's.
//  - The rules are a profile's, set by its main account with the main PIN (or an
//    administrator), never on a main account, and not switched on while the main account
//    has no PIN. Anyone in the household can read them; nobody outside it.
//  - More time: with the main PIN from any device in the household, added on to time
//    already given; 0 says no. Asking: once in ten minutes, a message on the parent's open
//    apps, and an email if they asked for those.
//  - The gate: new video refused with 403 and a code, music and browsing never; what is
//    already playing is let through until the grace ends. Nobody limited costs one count.
//  - The timer: counts unpaused video once per profile however many screens, warns five
//    minutes before, stops at the end; with no warning it gives five minutes first. A gap
//    between looks is never billed in full. The daily-limit email goes once.
//  - Usage survives a restart; the rules survive Jellyfin's XmlSerializer unchanged.
//
// Against de05710 the first assertion fails: there are no limits at all.
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
var hasher = asm.GetType("Jellyfin.Profiles.Auth.PinHasher", true);
var rulesType = asm.GetType("Jellyfin.Profiles.Configuration.ViewingRules");
var windowType = asm.GetType("Jellyfin.Profiles.Configuration.ViewingWindow");
var clockType = asm.GetType("Jellyfin.Profiles.Configuration.ViewingClock");
var tracker = asm.GetType("Jellyfin.Profiles.Auth.ViewingTracker");
var gate = asm.GetType("Jellyfin.Profiles.Auth.ViewingGate");
var timer = asm.GetType("Jellyfin.Profiles.Auth.ViewingTimer");
var mailerType = asm.GetType("Jellyfin.Profiles.Mail.Mailer", true);
var senderIface = asm.GetType("Jellyfin.Profiles.Mail.IMailSender", true);
var notesType = asm.GetType("Jellyfin.Profiles.Mail.Notifications", true);

Ok("this build has viewing limits", rulesType != null && windowType != null && clockType != null && tracker != null
   && gate != null && timer != null && ctrlType.GetMethod("SetViewingLimits") != null && ctrlType.GetMethod("GrantViewingTime") != null);
if (rulesType == null || windowType == null || clockType == null || tracker == null || gate == null || timer == null)
    return Finish();

// ── Helpers ──────────────────────────────────────────────────────────────────
object Get(object o, string prop) => o?.GetType().GetProperty(prop, Any)?.GetValue(o);
bool Bool(object o, string prop) => Get(o, prop) is bool b && b;
object Static(Type t, string name, params object[] args)
{
    var m = t.GetMethods(Any).Where(x => x.Name == name && x.GetParameters().Length == args.Length)
        .FirstOrDefault(x => x.GetParameters().Select((p, i) => args[i] == null || p.ParameterType.IsInstanceOfType(args[i])
            || (Nullable.GetUnderlyingType(p.ParameterType)?.IsInstanceOfType(args[i]) ?? false)).All(ok => ok));
    if (m == null) throw new MissingMethodException(t.Name + "." + name);
    try { return m.Invoke(null, args); }
    catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
}

object Window(int days, string start, string end)
{
    var w = Activator.CreateInstance(windowType);
    windowType.GetProperty("Days").SetValue(w, days);
    windowType.GetProperty("Start").SetValue(w, start);
    windowType.GetProperty("End").SetValue(w, end);
    return w;
}
object Rules(bool hours, int daily, int? weekend, params object[] windows)
{
    var r = Activator.CreateInstance(rulesType);
    rulesType.GetProperty("HoursEnabled").SetValue(r, hours);
    rulesType.GetProperty("DailyMinutes").SetValue(r, daily);
    rulesType.GetProperty("WeekendMinutes").SetValue(r, weekend);
    var list = (IList)rulesType.GetProperty("Windows").GetValue(r);
    foreach (var w in windows) list.Add(w);
    return r;
}
const int Sun = 1, Mon = 2, Tue = 4, Wed = 8, Thu = 16, Fri = 32, Sat = 64;
const int Weekdays = Mon | Tue | Wed | Thu | Fri, EveryDay = 127;

var london = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
DateTime Utc(int y, int mo, int d, int h, int mi = 0) => new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Utc);
DateTime LocalToUtc(TimeZoneInfo z, int y, int mo, int d, int h, int mi = 0) =>
    TimeZoneInfo.ConvertTimeToUtc(new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Unspecified), z);
object Eval(object rules, TimeZoneInfo zone, double used, DateTime? extra, DateTime now) =>
    Static(clockType, "Evaluate", rules, zone, used, extra, now);
string Show(object s) => s == null ? "null" : string.Join(", ",
    new[] { "Allowed", "Reason", "Binding", "MinutesLeft", "UntilUtc", "NextStartUtc", "UsedMinutes", "LimitMinutes", "Day" }
    .Select(p => p + "=" + Get(s, p)));

// ── The clock ────────────────────────────────────────────────────────────────
Section("The clock");

// 2026-11-04 is a Wednesday. London is on GMT, so local is UTC.
var school = Rules(true, 0, null, Window(Weekdays, "15:00", "20:00"), Window(Sat | Sun, "08:00", "21:00"));
var s1 = Eval(school, london, 0, null, Utc(2026, 11, 4, 14));
Ok("before the window: refused for hours", !Bool(s1, "Allowed") && (string)Get(s1, "Reason") == "hours", Show(s1));
Ok("and it opens at 15:00 the same day", (DateTime?)Get(s1, "NextStartUtc") == Utc(2026, 11, 4, 15), Show(s1));
s1 = Eval(school, london, 0, null, Utc(2026, 11, 4, 16));
Ok("inside it: allowed until 20:00, 240 minutes", Bool(s1, "Allowed") && (string)Get(s1, "Binding") == "hours"
   && (int?)Get(s1, "MinutesLeft") == 240 && (DateTime?)Get(s1, "UntilUtc") == Utc(2026, 11, 4, 20), Show(s1));
s1 = Eval(school, london, 0, null, Utc(2026, 11, 6, 21));
Ok("Friday night: next is Saturday 08:00", !Bool(s1, "Allowed") && (DateTime?)Get(s1, "NextStartUtc") == Utc(2026, 11, 7, 8), Show(s1));
s1 = Eval(school, london, 0, null, Utc(2026, 11, 4, 20));
Ok("the end itself is outside", !Bool(s1, "Allowed"), Show(s1));

var late = Rules(true, 0, null, Window(Fri, "21:00", "01:00"));
s1 = Eval(late, london, 0, null, Utc(2026, 11, 7, 0, 30));
Ok("a Friday window running past midnight still holds at 00:30 Saturday", Bool(s1, "Allowed")
   && (DateTime?)Get(s1, "UntilUtc") == Utc(2026, 11, 7, 1) && (int?)Get(s1, "MinutesLeft") == 30, Show(s1));
s1 = Eval(late, london, 0, null, Utc(2026, 11, 7, 1, 30));
Ok("and is over at 01:30", !Bool(s1, "Allowed") && (DateTime?)Get(s1, "NextStartUtc") == Utc(2026, 11, 13, 21), Show(s1));
s1 = Eval(late, london, 0, null, Utc(2026, 11, 7, 21, 30));
Ok("the window is Friday's only, not Saturday's", !Bool(s1, "Allowed"), Show(s1));

var touching = Rules(true, 0, null, Window(Fri, "18:00", "24:00"), Window(Sat, "00:00", "02:00"));
s1 = Eval(touching, london, 0, null, Utc(2026, 11, 6, 23));
Ok("two windows that touch at midnight run on: until 02:00", (DateTime?)Get(s1, "UntilUtc") == Utc(2026, 11, 7, 2)
   && (int?)Get(s1, "MinutesLeft") == 180, Show(s1));

var allDay = Rules(true, 0, null, Window(Wed, "00:00", "00:00"));
s1 = Eval(allDay, london, 0, null, Utc(2026, 11, 4, 23, 59));
Ok("the same start and end is the whole day", Bool(s1, "Allowed"), Show(s1));

var never = Rules(true, 0, null);
s1 = Eval(never, london, 0, null, Utc(2026, 11, 4, 16));
Ok("hours on with no windows is never, and never opens", !Bool(s1, "Allowed") && Get(s1, "NextStartUtc") == null, Show(s1));
var broken = Rules(true, 0, null, Window(Wed, "25:00", "26:00"));
s1 = Eval(broken, london, 0, null, Utc(2026, 11, 4, 16));
Ok("a window that isn't a time is ignored, not read as all day", !Bool(s1, "Allowed"), Show(s1));
s1 = Eval(Rules(false, 0, null, Window(Wed, "15:00", "16:00")), london, 0, null, Utc(2026, 11, 4, 18));
Ok("windows with hours off limit nothing", !Bool(s1, "Limited") && Bool(s1, "Allowed"), Show(s1));

// Daylight saving. London goes to BST at 01:00 UTC on 2026-03-29. Bedtime is 20:00 on the
// wall both days, which is 20:00 UTC on the 28th and 19:00 UTC on the 29th.
var evening = Rules(true, 0, null, Window(EveryDay, "18:00", "20:00"));
s1 = Eval(evening, london, 0, null, Utc(2026, 3, 28, 19, 30));
Ok("before the change, 19:30 UTC is 19:30 on the wall: allowed until 20:00 UTC", Bool(s1, "Allowed")
   && (DateTime?)Get(s1, "UntilUtc") == Utc(2026, 3, 28, 20), Show(s1));
s1 = Eval(evening, london, 0, null, Utc(2026, 3, 29, 19, 30));
Ok("after it, 19:30 UTC is 20:30 on the wall: refused", !Bool(s1, "Allowed"), Show(s1));
s1 = Eval(evening, london, 0, null, Utc(2026, 3, 29, 18, 30));
Ok("and bedtime is 19:00 UTC", Bool(s1, "Allowed") && (DateTime?)Get(s1, "UntilUtc") == Utc(2026, 3, 29, 19), Show(s1));
// A window starting in the hour the change skips opens when the clock comes back.
var skipped = Rules(true, 0, null, Window(Sun, "01:30", "03:00"));
s1 = Eval(skipped, london, 0, null, Utc(2026, 3, 29, 0, 30));
Ok("a start the change skips is moved past the gap, not lost", !Bool(s1, "Allowed")
   && Get(s1, "NextStartUtc") is DateTime ns && ns >= Utc(2026, 3, 29, 1) && ns <= Utc(2026, 3, 29, 1, 15), Show(s1));

// The day. 03:00 UTC on Thursday is 22:00 Wednesday in New York.
var hour = Rules(false, 60, null);
s1 = Eval(hour, newYork, 0, null, Utc(2026, 11, 5, 3));
Ok("the day is the household's: 03:00 UTC Thursday is still Wednesday in New York", (string)Get(s1, "Day") == "2026-11-04", Show(s1));
Ok("and the limit resets at New York's midnight", (DateTime?)Get(Eval(hour, newYork, 3600, null, Utc(2026, 11, 5, 3)), "NextStartUtc")
   == LocalToUtc(newYork, 2026, 11, 5, 0), Show(Eval(hour, newYork, 3600, null, Utc(2026, 11, 5, 3))));

s1 = Eval(hour, london, 59 * 60, null, Utc(2026, 11, 4, 12));
Ok("59 of 60 minutes used: one left, the limit binds", Bool(s1, "Allowed") && (int?)Get(s1, "MinutesLeft") == 1
   && (string)Get(s1, "Binding") == "limit" && (int)Get(s1, "UsedMinutes") == 59, Show(s1));
s1 = Eval(hour, london, 60 * 60, null, Utc(2026, 11, 4, 12));
Ok("60 of 60: refused for the limit, until midnight", !Bool(s1, "Allowed") && (string)Get(s1, "Reason") == "limit"
   && (DateTime?)Get(s1, "NextStartUtc") == Utc(2026, 11, 5, 0), Show(s1));

var weekend = Rules(false, 60, 120);
Ok("Saturday's limit is the weekend one", (int?)Get(Eval(weekend, london, 0, null, Utc(2026, 11, 7, 12)), "LimitMinutes") == 120);
Ok("Wednesday's is the daily one", (int?)Get(Eval(weekend, london, 0, null, Utc(2026, 11, 4, 12)), "LimitMinutes") == 60);
var freeWeekend = Rules(false, 60, 0);
s1 = Eval(freeWeekend, london, 999 * 60, null, Utc(2026, 11, 8, 12));
Ok("a weekend limit of 0 is none", Bool(s1, "Allowed") && Get(s1, "LimitMinutes") == null && Get(s1, "MinutesLeft") == null, Show(s1));

var both = Rules(true, 60, null, Window(Weekdays, "15:00", "20:00"));
s1 = Eval(both, london, 60 * 60, null, Utc(2026, 11, 4, 16));
Ok("limit spent inside the hours: refused for the limit, open again tomorrow at 15:00", !Bool(s1, "Allowed")
   && (string)Get(s1, "Reason") == "limit" && (DateTime?)Get(s1, "NextStartUtc") == Utc(2026, 11, 5, 15), Show(s1));
s1 = Eval(both, london, 50 * 60, null, Utc(2026, 11, 4, 19, 55));
Ok("whichever ends first binds: 5 minutes of hours before 10 of limit", (string)Get(s1, "Binding") == "hours"
   && (int?)Get(s1, "MinutesLeft") == 5, Show(s1));

s1 = Eval(school, london, 0, Utc(2026, 11, 4, 21, 30), Utc(2026, 11, 4, 21));
Ok("extra time opens it outside the hours, for exactly that long", Bool(s1, "Allowed") && (string)Get(s1, "Binding") == "extra"
   && (int?)Get(s1, "MinutesLeft") == 30 && (DateTime?)Get(s1, "UntilUtc") == Utc(2026, 11, 4, 21, 30), Show(s1));
s1 = Eval(school, london, 0, Utc(2026, 11, 4, 20, 30), Utc(2026, 11, 4, 21));
Ok("and once it has run out it is gone", !Bool(s1, "Allowed"), Show(s1));
s1 = Eval(school, london, 0, Utc(2026, 11, 4, 16, 30), Utc(2026, 11, 4, 16));
Ok("extra time shorter than the hours left changes nothing", (string)Get(s1, "Binding") == "hours" && (int?)Get(s1, "MinutesLeft") == 240, Show(s1));

s1 = Eval(null, london, 0, null, Utc(2026, 11, 4, 16));
Ok("no rules: not limited", !Bool(s1, "Limited") && Bool(s1, "Allowed"));

// ── The plugin ───────────────────────────────────────────────────────────────
var sent = new List<Mail>();
var proxy = DispatchProxy.Create(senderIface, typeof(Recorder));
((Recorder)proxy).OnSend = m => { lock (sent) sent.Add(m); };
mailerType.GetField("Sender", Any).SetValue(null, proxy);

var tempDir = Path.Combine(Path.GetTempPath(), "bonfire-viewing-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
var plugin = Activator.CreateInstance(pluginType, new StubPaths(tempDir), new StubXml());
var config = pluginType.GetProperty("Configuration").GetValue(plugin);
var mappings = (IList)cfgType.GetProperty("Mappings").GetValue(config);

void Map(Guid profile, Guid master, string name, string pin = null)
{
    var m = Activator.CreateInstance(mapType);
    mapType.GetProperty("ProfileUserId").SetValue(m, profile);
    mapType.GetProperty("MasterUserId").SetValue(m, master);
    mapType.GetProperty("ProfileName").SetValue(m, name);
    if (pin != null) mapType.GetProperty("PinHash").SetValue(m, hasher.GetMethod("Hash").Invoke(null, new object[] { pin }));
    mappings.Add(m);
}
object Mapping(Guid id) => mappings.Cast<object>().First(m => (Guid)mapType.GetProperty("ProfileUserId").GetValue(m) == id);
object StoredRules(Guid id) => mapType.GetProperty("Viewing").GetValue(Mapping(id));

var world = new World();
var users = World.Create(world);
var sessions = SessionStub.Create();
var stub = (SessionStub)(object)sessions;
var library = LibraryStub.Create();
var libStub = (LibraryStub)(object)library;

var clockNow = Utc(2026, 11, 4, 16);
tracker.GetField("Now", Any).SetValue(null, (Func<DateTime>)(() => clockNow));
void Fresh() { Static(tracker, "Invalidate"); }

(int status, object value) Call(string method, Guid caller, Dictionary<string, object> body = null, object route = null)
{
    var r = ctrlType.GetMethod(method);
    if (r == null) return (-404, "no route " + method);
    var ctor = ctrlType.GetConstructors().First();
    var args = ctor.GetParameters().Select(p =>
        p.ParameterType == typeof(IUserManager) ? users
        : p.ParameterType == typeof(ISessionManager) ? sessions
        : p.ParameterType == typeof(INetworkManager) ? NetworkStub.Create()
        : p.ParameterType == typeof(ILibraryManager) ? library
        : p.ParameterType.Name.StartsWith("ILogger") ? Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(ctrlType))
        : (object)null).ToArray();
    var c = (ControllerBase)ctor.Invoke(args);
    var http = new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(caller == Guid.Empty ? Array.Empty<Claim>()
            : new[] { new Claim("Jellyfin-UserId", caller.ToString()) }, caller == Guid.Empty ? null : "test")),
    };
    c.ControllerContext = new ControllerContext { HttpContext = http };

    var invokeArgs = r.GetParameters().Select(p =>
    {
        if (p.ParameterType == typeof(Guid)) return route ?? Guid.Empty;
        if (p.ParameterType.IsValueType) return Activator.CreateInstance(p.ParameterType);
        if (p.ParameterType == typeof(string)) return null;
        var req = Activator.CreateInstance(p.ParameterType);
        foreach (var kv in body ?? new Dictionary<string, object>())
        {
            var prop = p.ParameterType.GetProperty(kv.Key);
            if (prop == null) throw new Exception(p.ParameterType.Name + " has no " + kv.Key);
            prop.SetValue(req, kv.Value);
        }
        return req;
    }).ToArray();

    object result;
    try
    {
        result = r.Invoke(c, invokeArgs);
        if (result is Task t) { t.GetAwaiter().GetResult(); result = t.GetType().GetProperty("Result")?.GetValue(t); }
    }
    catch (TargetInvocationException ex) { return (-500, ex.InnerException?.GetType().Name + ": " + ex.InnerException?.Message + ex.InnerException?.StackTrace); }

    if (result is IConvertToActionResult conv) result = conv.Convert();
    return result switch
    {
        ObjectResult o => (o.StatusCode ?? 200, o.Value),
        StatusCodeResult s => (s.StatusCode, null),
        _ => (-1, result?.GetType().Name),
    };
}
Dictionary<string, object> B(params (string k, object v)[] kv) => kv.ToDictionary(x => x.k, x => x.v);
IList WindowList(params object[] w)
{
    var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(windowType));
    foreach (var x in w) list.Add(x);
    return list;
}
// A signed-in caller who is refused gets a message: BonfireErrors sends a 401 with one as 403
// forbidden. A bare 401 tells the client its token is dead, and some clients sign out on it.
bool Refused((int status, object value) r) => r.status == 403 || (r.status == 401 && r.value is string s && s.Length > 0);

var ADMIN = Guid.NewGuid(); world.Add(ADMIN, "Admin", admin: true);
var MAIN = Guid.NewGuid(); world.Add(MAIN, "Parent");
var KID = Guid.NewGuid(); world.Add(KID, "Parent_kid");
var KID2 = Guid.NewGuid(); world.Add(KID2, "Parent_kid2");
var OTHER = Guid.NewGuid(); world.Add(OTHER, "Other");
var OKID = Guid.NewGuid(); world.Add(OKID, "Other_kid");
Map(MAIN, MAIN, "Parent", pin: "1234");
Map(KID, MAIN, "Kid");
Map(KID2, MAIN, "Teen");
Map(OTHER, OTHER, "Other");
Map(OKID, OTHER, "Littlest");
// London, so the times below don't depend on the machine running this.
cfgType.GetProperty("DefaultTimeZone").SetValue(config, "Europe/London");

// ── Setting the rules ────────────────────────────────────────────────────────
Section("Setting the rules");

var schoolBody = B(("HoursEnabled", true), ("Windows", WindowList(Window(Weekdays, "15:00", "20:00"))), ("DailyMinutes", 60), ("MasterPin", "1234"));
var r = Call("SetViewingLimits", KID, schoolBody, KID);
Ok("a profile cannot set its own", Refused(r) && StoredRules(KID) == null, r.status + " " + r.value);
r = Call("SetViewingLimits", OTHER, schoolBody, KID);
Ok("another household cannot", Refused(r) && StoredRules(KID) == null, r.status + " " + r.value);
r = Call("SetViewingLimits", Guid.Empty, schoolBody, KID);
Ok("nobody cannot", r.status == 401 && StoredRules(KID) == null, r.status + " " + r.value);
r = Call("SetViewingLimits", MAIN, B(("HoursEnabled", true), ("Windows", WindowList(Window(Weekdays, "15:00", "20:00"))), ("DailyMinutes", 60)), KID);
Ok("the main account needs its PIN", r.status == 400 && StoredRules(KID) == null, r.status + " " + r.value);
r = Call("SetViewingLimits", MAIN, B(("HoursEnabled", true), ("Windows", WindowList(Window(Weekdays, "15:00", "20:00"))), ("MasterPin", "9999")), KID);
Ok("and the right one", r.status == 400 && StoredRules(KID) == null, r.status + " " + r.value);
r = Call("SetViewingLimits", MAIN, B(("HoursEnabled", true), ("Windows", WindowList(Window(Weekdays, "15:00", "25:00"))), ("MasterPin", "1234")), KID);
Ok("a window that isn't a time is refused", r.status == 400 && StoredRules(KID) == null, r.status + " " + r.value);
r = Call("SetViewingLimits", MAIN, B(("Windows", WindowList(Window(0, "15:00", "20:00"))), ("MasterPin", "1234")), KID);
Ok("a window on no day is refused", r.status == 400, r.status + " " + r.value);
r = Call("SetViewingLimits", MAIN, B(("DailyMinutes", 2000), ("MasterPin", "1234")), KID);
Ok("a day longer than a day is refused", r.status == 400 && StoredRules(KID) == null, r.status + " " + r.value);
r = Call("SetViewingLimits", MAIN, B(("DailyMinutes", 60), ("MasterPin", "1234")), MAIN);
Ok("a main account cannot be limited", r.status == 400 && StoredRules(MAIN) == null, r.status + " " + r.value);
r = Call("SetViewingLimits", OTHER, B(("DailyMinutes", 60)), OKID);
Ok("not while the main account has no PIN: the child could switch them off", r.status == 400 && StoredRules(OKID) == null, r.status + " " + r.value);
r = Call("SetViewingLimits", MAIN, B(("HoursEnabled", true), ("Windows", WindowList(Window(Weekdays, "15:00", "20:00"))), ("DailyMinutes", 60), ("Language", "fr"), ("MasterPin", "1234")), KID);
Ok("the main account with its PIN can", r.status == 200 && StoredRules(KID) != null
   && (int)Get(StoredRules(KID), "DailyMinutes") == 60 && Bool(StoredRules(KID), "HoursEnabled"), r.status + " " + r.value);
Ok("and is told where the profile stands", Bool(r.value, "Limited") && Bool(r.value, "Allowed") && (int?)Get(r.value, "MinutesLeft") == 60
   && (string)Get(r.value, "Binding") == "limit", Show(r.value));
r = Call("SetViewingLimits", ADMIN, B(("DailyMinutes", 30)), OKID);
Ok("an administrator can, PIN or not", r.status == 200 && (int)Get(StoredRules(OKID), "DailyMinutes") == 30, r.status + " " + r.value);
r = Call("SetViewingLimits", ADMIN, B(("DailyMinutes", 0)), OKID);
Ok("and switching everything off leaves no rules", r.status == 200 && StoredRules(OKID) == null && !Bool(r.value, "Limited"), r.status + " " + r.value);

// ── Reading them ─────────────────────────────────────────────────────────────
Section("Reading them");

r = Call("GetViewingLimits", KID, null, KID);
Ok("a profile can read its own, rules included", r.status == 200 && Bool(r.value, "Limited") && Get(r.value, "Rules") != null
   && ((IList)Get(Get(r.value, "Rules"), "Windows")).Count == 1, r.status + " " + r.value);
Ok("with the zone it is read in", (string)Get(r.value, "TimeZone") != "", (string)Get(r.value, "TimeZone"));
r = Call("GetViewingLimits", KID2, null, KID);
Ok("so can its brother", r.status == 200);
r = Call("GetViewingLimits", OTHER, null, KID);
Ok("another household cannot", Refused(r), r.status.ToString());
r = Call("GetViewingLimits", ADMIN, null, KID);
Ok("an administrator can", r.status == 200 && Bool(r.value, "Limited"));
r = Call("GetViewingLimits", MAIN, null, KID2);
Ok("a profile with no rules reads as not limited", r.status == 200 && !Bool(r.value, "Limited") && Bool(r.value, "Allowed"));

r = Call("GetProfiles", MAIN);
var tiles = ((IEnumerable)r.value).Cast<object>().ToList();
var kidTile = tiles.FirstOrDefault(t => (Guid)Get(t, "ProfileUserId") == KID);
Ok("the household's list carries the tile's state", kidTile != null && Get(kidTile, "Viewing") != null
   && (int?)Get(Get(kidTile, "Viewing"), "MinutesLeft") == 60, r.status + " " + Get(kidTile, "Viewing"));
Ok("and nothing for a profile with no limits", Get(tiles.First(t => (Guid)Get(t, "ProfileUserId") == KID2), "Viewing") == null);
Ok("and not the rules themselves", Get(Get(kidTile, "Viewing"), "Rules") == null);

// ── The household's clock ────────────────────────────────────────────────────
Section("The household's clock");

r = Call("UpdateAccount", MAIN, B(("TimeZone", "Mars/Olympus_Mons"), ("MasterPin", "1234")));
Ok("a zone the server doesn't know is refused", r.status == 400, r.status + " " + r.value);
r = Call("UpdateAccount", MAIN, B(("TimeZone", "America/New_York")));
Ok("the zone is the main account's, behind its PIN", r.status == 400 && mapType.GetProperty("TimeZone").GetValue(Mapping(MAIN)) == null, r.status + " " + r.value);
r = Call("UpdateAccount", MAIN, B(("TimeZone", "America/New_York"), ("MasterPin", "1234")));
Ok("it can choose one", r.status == 200 && (string)mapType.GetProperty("TimeZone").GetValue(Mapping(MAIN)) == "America/New_York", r.status + " " + r.value);
r = Call("GetViewingLimits", KID, null, KID);
Ok("and its profiles are read in it at once", (string)Get(r.value, "TimeZone") == "America/New_York", (string)Get(r.value, "TimeZone"));
r = Call("GetTimeZones", KID);
Ok("the zone list says which is in use, for anyone in the household", r.status == 200 && (string)Get(r.value, "Household") == "America/New_York"
   && (string)Get(r.value, "Effective") == "America/New_York" && ((IEnumerable<string>)Get(r.value, "Zones")).Contains("Europe/London"), r.status + " " + r.value);
r = Call("GetTimeZones", Guid.Empty);
Ok("but not for nobody", r.status == 401);
cfgType.GetProperty("DefaultTimeZone").SetValue(config, "Europe/London");
r = Call("UpdateAccount", MAIN, B(("TimeZone", ""), ("MasterPin", "1234")));
r = Call("GetViewingLimits", KID, null, KID);
Ok("an empty zone follows the administrator's default", (string)Get(r.value, "TimeZone") == "Europe/London", (string)Get(r.value, "TimeZone"));
r = Call("UpdateAdminSettings", ADMIN, B(("DefaultTimeZone", "Nowhere/Special")));
Ok("the administrator's default must be a real zone too", r.status == 400 && (string)cfgType.GetProperty("DefaultTimeZone").GetValue(config) == "Europe/London", r.status + " " + r.value);

// Richer theme motion (2.0): off by default, an administrator's to turn on, and a save that
// does not mention it leaves it alone. Here because this is the harness that already drives
// UpdateAdminSettings as an administrator and as someone who is not.
var motion = cfgType.GetProperty("RichThemeMotion");
Ok("richer theme motion exists and starts off", motion != null && !(bool)motion.GetValue(Activator.CreateInstance(cfgType)));
r = Call("UpdateAdminSettings", MAIN, B(("RichThemeMotion", true)));
Ok("someone who is not an administrator cannot turn it on", r.status == 401 && !(bool)motion.GetValue(config), r.status + " " + r.value);
r = Call("UpdateAdminSettings", ADMIN, B(("RichThemeMotion", true)));
Ok("an administrator can", r.status == 200 && (bool)motion.GetValue(config), r.status + " " + r.value);
r = Call("UpdateAdminSettings", ADMIN, B(("DefaultTimeZone", "Europe/London")));
Ok("a save that does not mention it leaves it on", r.status == 200 && (bool)motion.GetValue(config), r.status + " " + r.value);
r = Call("UpdateAdminSettings", ADMIN, B(("RichThemeMotion", false)));
Ok("and it can be turned off again", r.status == 200 && !(bool)motion.GetValue(config), r.status + " " + r.value);

// ── More time ────────────────────────────────────────────────────────────────
Section("More time");

// Wednesday 21:00 in London: outside Kid's hours.
clockNow = Utc(2026, 11, 4, 21); Fresh();
Ok("Kid is outside its hours", Get(Static(tracker, "State", KID, clockNow), "Allowed") is false);
r = Call("GrantViewingTime", KID, B(("Minutes", 30), ("MasterPin", "0000")), KID);
Ok("from the child's own device, a wrong PIN gives nothing", r.status == 400, r.status + " " + r.value);
r = Call("GrantViewingTime", KID, B(("Minutes", 30)), KID);
Ok("nor does no PIN", r.status == 400, r.status + " " + r.value);
r = Call("GrantViewingTime", OTHER, B(("Minutes", 30)), KID);
Ok("another household cannot give it", Refused(r), r.status + " " + r.value);
r = Call("GrantViewingTime", KID, B(("Minutes", 300), ("MasterPin", "1234")), KID);
Ok("more than four hours at once is refused", r.status == 400, r.status + " " + r.value);
r = Call("GrantViewingTime", MAIN, B(("Minutes", 30), ("MasterPin", "1234")), KID2);
Ok("a profile with no limits needs no more time", r.status == 400, r.status + " " + r.value);
r = Call("GrantViewingTime", KID, B(("Minutes", 30), ("MasterPin", "1234")), KID);
Ok("the main PIN on the child's device gives 30 minutes", r.status == 200 && Bool(r.value, "Allowed")
   && (string)Get(r.value, "Binding") == "extra" && (int?)Get(r.value, "MinutesLeft") == 30, r.status + " " + Show(r.value));
r = Call("GrantViewingTime", MAIN, B(("Minutes", 15), ("MasterPin", "1234")), KID);
Ok("and more is added on to the end, not from now", (int?)Get(r.value, "MinutesLeft") == 45
   && (DateTime?)Get(r.value, "ExtraUntilUtc") == Utc(2026, 11, 4, 21, 45), Show(r.value));
clockNow = Utc(2026, 11, 4, 21, 46); Fresh();
Ok("then it runs out", Get(Static(tracker, "State", KID, clockNow), "Allowed") is false);
var oKidNoPin = Call("SetViewingLimits", ADMIN, B(("DailyMinutes", 30)), OKID);
r = Call("GrantViewingTime", OKID, B(("Minutes", 30)), OKID);
Ok("a child cannot give itself time when the main account has no PIN", r.status == 400, r.status + " " + r.value);
r = Call("GrantViewingTime", OTHER, B(("Minutes", 30)), OKID);
Ok("its main account can", r.status == 200, r.status + " " + r.value);

// ── Asking ───────────────────────────────────────────────────────────────────
Section("Asking for more");

// Email working, with the parent's address confirmed and asking-notices on.
var email = cfgType.GetProperty("Email").GetValue(config);
email.GetType().GetProperty("Host").SetValue(email, "smtp.example.com");
email.GetType().GetProperty("FromAddress").SetValue(email, "bonfire@example.com");
email.GetType().GetProperty("VerifiedFingerprint").SetValue(email, email.GetType().GetMethod("Fingerprint").Invoke(email, null));
var accountType = asm.GetType("Jellyfin.Profiles.Configuration.AccountEmail", true);
var account = Activator.CreateInstance(accountType);
accountType.GetProperty("UserId").SetValue(account, MAIN);
accountType.GetProperty("Address").SetValue(account, "parent@example.com");
((IList)cfgType.GetProperty("AccountEmails").GetValue(config)).Add(account);
Ok("a new address asks to hear about requests, not about every limit", (bool)accountType.GetProperty("NotifyTimeAsked").GetValue(account)
   && !(bool)accountType.GetProperty("NotifyLimitReached").GetValue(account));

void Drain()
{
    var q = (System.Collections.Concurrent.ConcurrentQueue<Task>)notesType.GetField("Pending", Any).GetValue(null);
    Task.WaitAll(q.ToArray(), 10000);
}
int Mails() { Drain(); lock (sent) return sent.Count; }

stub.Sessions.Add(SessionStub.Make("parent-phone", MAIN, null));
stub.Sessions.Add(SessionStub.Make("other-tv", OTHER, null));
var mailsBefore = Mails();
r = Call("AskForViewingTime", OTHER, null, KID);
Ok("another household cannot ask for Kid", Refused(r), r.status.ToString());
r = Call("AskForViewingTime", KID, null, KID2);
Ok("nor for a profile with no limits", r.status == 400, r.status.ToString());
r = Call("AskForViewingTime", KID, null, KID);
Ok("Kid can ask", r.status == 200 && Get(r.value, "AskedUtc") != null, r.status + " " + r.value);
Ok("the parent's open app is told", stub.Messages.Any(m => m.session == "parent-phone" && m.text.Contains("Kid")),
   string.Join(" | ", stub.Messages.Select(m => m.session + ": " + m.text)));
Ok("no other household's is", !stub.Messages.Any(m => m.session == "other-tv"));
Ok("and the parent gets an email", Mails() == mailsBefore + 1 && sent.Last().To == "parent@example.com" && sent.Last().Subject.Contains("Kid"),
   sent.LastOrDefault()?.Subject);
r = Call("AskForViewingTime", KID, null, KID);
Ok("asking again straight away is refused", r.status == 429 && Mails() == mailsBefore + 1, r.status.ToString());
r = Call("GetViewingLimits", MAIN, null, KID);
Ok("the parent sees the request waiting", Get(r.value, "AskedUtc") != null);
r = Call("GrantViewingTime", MAIN, B(("Minutes", 0), ("MasterPin", "1234")), KID);
Ok("0 minutes says no: the request is answered, nothing given", r.status == 200 && Get(r.value, "AskedUtc") == null
   && !Bool(r.value, "Allowed"), r.status + " " + Show(r.value));
clockNow = clockNow.AddMinutes(11);
r = Call("AskForViewingTime", KID, null, KID);
Ok("after ten minutes it can ask again", r.status == 200);
r = Call("GrantViewingTime", MAIN, B(("Minutes", 20), ("MasterPin", "1234")), KID);
Ok("and a yes answers it too", Get(r.value, "AskedUtc") == null && Bool(r.value, "Allowed"));
accountType.GetProperty("NotifyTimeAsked").SetValue(account, false);
clockNow = clockNow.AddMinutes(11);
mailsBefore = Mails();
Call("AskForViewingTime", KID, null, KID);
Ok("with the notice off, no email", Mails() == mailsBefore);

// ── The gate ─────────────────────────────────────────────────────────────────
Section("The gate");

string Kind(string path) => Static(gate, "Classify", path).ToString();
var item = Guid.NewGuid();
Ok("PlaybackInfo is what starts playback", Kind("/Items/" + item + "/PlaybackInfo") == "PlaybackInfo");
Ok("so is a video stream", Kind("/Videos/" + item + "/stream.mkv") == "VideoStream" && Kind("/Videos/" + item + "/main.m3u8") == "VideoStream");
Ok("an item page's extra parts and trickplay are browsing", Kind("/Videos/" + item + "/AdditionalParts") == "None"
   && Kind("/Videos/" + item + "/Trickplay/320/0.jpg") == "None");
Ok("music is not limited", Kind("/Audio/" + item + "/stream.mp3") == "None");
Ok("nor is browsing", Kind("/Users/" + KID + "/Items") == "None" && Kind("/Items/" + item) == "None");
Ok("nor reports", Kind("/Sessions/Playing/Progress") == "None");
// The original file is playback too: Download and File serve the whole thing (R-1019, R-1296).
Ok("a download of the original file is playback", Kind("/Items/" + item + "/Download") == "Download"
   && Kind("/Items/" + item + "/File") == "Download" && Kind("/items/" + item + "/download") == "Download");
Ok("but not what an item page reads", Kind("/Items/" + item + "/Images/Primary") == "None"
   && Kind("/Items/" + item + "/Similar") == "None" && Kind("/Items/" + item + "/Download/x") == "None"
   && Kind("/Items/not-a-guid/Download") == "None");
// A trailing slash is the same route to ASP.NET (R-1020).
Ok("a trailing slash changes nothing", Kind("/Items/" + item + "/PlaybackInfo/") == "PlaybackInfo"
   && Kind("/Items/" + item + "/Download/") == "Download" && Kind("/Videos/" + item + "/stream.mkv//") == "VideoStream"
   && Kind("/Videos/" + item + "/AdditionalParts/") == "None");

// Kid: hours 15:00–20:00 on weekdays and 60 minutes. Wednesday 22:00, no extra time left.
cfgType.GetProperty("DefaultTimeZone").SetValue(config, "Europe/London");

// "Already playing" is what the gate let start, not what a session says it is playing: any
// client can report an item with POST /Sessions/Playing and nothing checks it (R-1297). So
// Kid starts the film at 16:00, inside its hours, through the gate; the rest of this section
// is at 23:00, with that film the only thing the gate has seen start.
clockNow = Utc(2026, 11, 4, 16); Fresh();
var film = Guid.NewGuid();
libStub.Items[film] = MediaType.Video;
var started = Through("/Items/" + film + "/PlaybackInfo", "tok-kid", "dev-kid").GetAwaiter().GetResult();
Ok("Kid starts a film inside its hours", !(bool)started.Items["handled"]);
clockNow = Utc(2026, 11, 4, 23); Fresh();
object Check(string path, Guid who) => Static(gate, "Check", path, who, sessions, library, clockNow);
string Code(object refusal) => refusal == null ? null : (string)refusal.GetType().GetField("Item1").GetValue(refusal);

Ok("outside the hours, PlaybackInfo is refused", Code(Check("/Items/" + item + "/PlaybackInfo", KID)) == "outside_viewing_hours");
Ok("so is any video stream", Code(Check("/Videos/" + item + "/stream.mkv", KID)) == "outside_viewing_hours");
Ok("browsing is not", Check("/Users/" + KID + "/Items", KID) == null);
Ok("nor is a profile with no limits", Check("/Items/" + item + "/PlaybackInfo", KID2) == null);
Ok("nor the main account", Check("/Items/" + item + "/PlaybackInfo", MAIN) == null);
var song = Guid.NewGuid();
libStub.Items[song] = MediaType.Audio;
libStub.Items[item] = MediaType.Video;
Ok("PlaybackInfo for a song is let through", Check("/Items/" + song + "/PlaybackInfo", KID) == null);
var unknownItem = Guid.NewGuid();
Ok("one the library can't find is judged as video", Code(Check("/Items/" + unknownItem + "/PlaybackInfo", KID)) == "outside_viewing_hours");

Ok("outside the hours, a download of a film is refused", Code(Check("/Items/" + item + "/Download", KID)) == "outside_viewing_hours");
Ok("and so is the file route", Code(Check("/Items/" + item + "/File", KID)) == "outside_viewing_hours");
Ok("a download of a song is let through", Check("/Items/" + song + "/Download", KID) == null);
Ok("a download the library can't find is judged as video", Code(Check("/Items/" + unknownItem + "/Download", KID)) == "outside_viewing_hours");
Ok("a download by a profile with no limits is let through", Check("/Items/" + item + "/Download", KID2) == null);
Ok("PlaybackInfo with a trailing slash is refused", Code(Check("/Items/" + item + "/PlaybackInfo/", KID)) == "outside_viewing_hours");

stub.Sessions.Add(SessionStub.Make("kid-tv", KID, item, deviceId: "dev-kid"));
Ok("a download is not 'what is playing': it goes on no grace", Code(Check("/Items/" + item + "/Download", KID)) == "outside_viewing_hours");
// The film Kid started at 16:00 (above), not `item`: what counts is what the gate let start.
Ok("a stream of what is already playing goes on until the grace is over", Check("/Videos/" + film + "/hls1/main/3.ts", KID) == null);
Ok("a stream of anything else does not", Code(Check("/Videos/" + Guid.NewGuid() + "/stream.mkv", KID)) == "outside_viewing_hours");
// What a session says it is playing proves nothing: Kid reports an item with POST
// /Sessions/Playing, never having been allowed it, and then asks for the stream.
var forgedItem = Guid.NewGuid();
var forgedSession = SessionStub.Make("kid-forged", KID, forgedItem, deviceId: "dev-kid");
stub.Sessions.Add(forgedSession);
Ok("an item the app only says it is playing is refused", Code(Check("/Videos/" + forgedItem + "/hls1/main/3.ts", KID)) == "outside_viewing_hours");
Ok("and so is its main stream", Code(Check("/Videos/" + forgedItem + "/stream.mkv", KID)) == "outside_viewing_hours");
stub.Sessions.Remove(forgedSession);

// The record is bounded and expires, in memory.
var record = asm.GetType("Jellyfin.Profiles.Auth.PlaybackRecord");
Ok("the gate keeps a record of what it let start", record != null);
if (record != null)
{
    var recNow = DateTime.UtcNow;
    record.GetField("Now", Any).SetValue(null, (Func<DateTime>)(() => recNow));
    var u = Guid.NewGuid(); var it = Guid.NewGuid();
    Static(record, "Note", u, it);
    Ok("an item noted is playing for that account", (bool)Static(record, "Has", u, it));
    Ok("not for another account", !(bool)Static(record, "Has", Guid.NewGuid(), it));
    recNow = recNow.AddHours(24);
    Ok("and it expires", !(bool)Static(record, "Has", u, it));
    var max = (int)record.GetField("MaxEntries", Any).GetValue(null);
    for (var i = 0; i < max + 50; i++) Static(record, "Note", Guid.NewGuid(), Guid.NewGuid());
    Ok("and it is bounded", (int)Static(record, "Count") <= max, ((int)Static(record, "Count")).ToString());
    record.GetField("Now", Any).SetValue(null, (Func<DateTime>)(() => DateTime.UtcNow));
}

// Spent limit, inside the hours: daily_limit_reached.
clockNow = Utc(2026, 11, 5, 16); Fresh();
Static(tracker, "AddSeconds", KID, "2026-11-05", 3600.0);
Ok("over the limit inside the hours, the code says so", Code(Check("/Items/" + item + "/PlaybackInfo", KID)) == "daily_limit_reached");

// Through HTTP: the 403, its headers, the message on the app that asked.
async Task<HttpContext> Through(string path, string token, string device)
{
    var ctx = new DefaultHttpContext();
    ctx.Request.Method = "GET";
    ctx.Request.Path = path;
    ctx.Request.Headers["Authorization"] = "MediaBrowser Client=\"Jellyfin Web\", Device=\"TV\", DeviceId=\"" + device + "\", Version=\"10.11.5\", Token=\"" + token + "\"";
    ctx.Response.Body = new MemoryStream();
    ctx.RequestServices = new ServiceCollection().AddSingleton<ILibraryManager>(library).BuildServiceProvider();
    var devices = DeviceStub.Create(new Dictionary<string, Guid> { ["tok-kid"] = KID, ["tok-main"] = MAIN });
    var handled = await (Task<bool>)gate.GetMethod("TryRefuseAsync").Invoke(null, new object[] { ctx, devices, sessions, NullLogger.Instance });
    ctx.Items["handled"] = handled;
    return ctx;
}
stub.Messages.Clear();
var http = Through("/Items/" + item + "/PlaybackInfo", "tok-kid", "dev-kid").GetAwaiter().GetResult();
Ok("over HTTP it is a 403", (bool)http.Items["handled"] && http.Response.StatusCode == 403, http.Response.StatusCode.ToString());
Ok("with the code in X-Bonfire-Error", http.Response.Headers["X-Bonfire-Error"].ToString() == "daily_limit_reached");
Ok("and when to try again: tomorrow at 15:00, when its hours open", http.Response.Headers["Retry-After"].ToString() == ((long)(Utc(2026, 11, 6, 15) - clockNow).TotalSeconds).ToString(), http.Response.Headers["Retry-After"].ToString());
async Task<string> BodyOf(HttpContext c) { c.Response.Body.Position = 0; return await new StreamReader(c.Response.Body).ReadToEndAsync(); }
var limitBody = BodyOf(http).GetAwaiter().GetResult();
Ok("the refusal body names the limit and says what to do (apps with no toast show only this)", limitBody == "Ask a parent for more time, or try again tomorrow. The daily viewing limit is reached.", limitBody);
Ok("the app that asked is told why, in the language the rules were saved in", stub.Messages.Count == 1 && stub.Messages[0].session == "kid-tv"
   && !stub.Messages[0].text.Contains("That's all for today"), string.Join(" | ", stub.Messages.Select(m => m.session + ": " + m.text)));
Through("/Items/" + item + "/PlaybackInfo", "tok-kid", "dev-kid").GetAwaiter().GetResult();
Ok("once a minute, not once a retry", stub.Messages.Count == 1);
http = Through("/Items/" + item + "/PlaybackInfo", "tok-main", "dev-main").GetAwaiter().GetResult();
Ok("the parent's own request goes through", !(bool)http.Items["handled"]);

// Who a request is from is Jellyfin's to say. The gate read the headers itself and
// disagreed with Jellyfin four ways; where it found no user it let the request through,
// and Jellyfin then served it as the child (R-1295). Here the authorization context
// answers as Jellyfin does for each header shape; the gate must follow it.
async Task<bool> Crafted(Action<HttpRequest> shape, Guid? jellyfinSays, bool apiKey = false)
{
    var ctx = new DefaultHttpContext();
    ctx.Request.Method = "GET";
    ctx.Request.Path = "/Items/" + item + "/PlaybackInfo";
    shape(ctx.Request);
    ctx.Response.Body = new MemoryStream();
    ctx.RequestServices = new ServiceCollection()
        .AddSingleton<ILibraryManager>(library)
        .AddSingleton<MediaBrowser.Controller.Net.IAuthorizationContext>(new JellyfinSays(jellyfinSays, apiKey))
        .BuildServiceProvider();
    var devices = DeviceStub.Create(new Dictionary<string, Guid> { ["tok-kid"] = KID, ["tok-main"] = MAIN });
    return await (Task<bool>)gate.GetMethod("TryRefuseAsync").Invoke(null, new object[] { ctx, devices, sessions, NullLogger.Instance });
}
Ok("the Emby scheme, which Jellyfin reads with legacy authorization on, is the child",
   Crafted(r => r.Headers["Authorization"] = "Emby Token=\"tok-kid\", Client=\"x\"", KID).GetAwaiter().GetResult());
Ok("so is a junk scheme with the child's token as ?ApiKey",
   Crafted(r => { r.Headers["Authorization"] = "Foo Client=\"x\", Token=\"junk\""; r.QueryString = new QueryString("?ApiKey=tok-kid"); }, KID).GetAwaiter().GetResult());
Ok("so are two tokens, where Jellyfin takes the last",
   Crafted(r => r.Headers["Authorization"] = "MediaBrowser Token=\"tok-main\", Token=\"tok-kid\"", KID).GetAwaiter().GetResult());
Ok("and the main account's token stays the main account's, whatever the header says",
   !Crafted(r => r.Headers["Authorization"] = "MediaBrowser Token=\"tok-kid\"", MAIN).GetAwaiter().GetResult());
Ok("an API key is no account and is let through",
   !Crafted(r => r.QueryString = new QueryString("?ApiKey=server-key"), null, apiKey: true).GetAwaiter().GetResult());

// Nobody limited: the gate costs a count and asks Jellyfin nothing.
// Every part said: since R-1326 a part left out keeps what was set, so "DailyMinutes 0" on
// its own would leave KID's school-day hours on.
foreach (var id in new[] { KID, OKID }) Call("SetViewingLimits", ADMIN, B(("HoursEnabled", false), ("Windows", WindowList()), ("DailyMinutes", 0)), id);
Fresh();
DeviceStub.Calls = 0;
http = Through("/Items/" + item + "/PlaybackInfo", "tok-kid", "dev-kid").GetAwaiter().GetResult();
Ok("with nobody limited, nothing is refused and no token is looked up", !(bool)http.Items["handled"] && DeviceStub.Calls == 0, DeviceStub.Calls.ToString());

// ── The timer ────────────────────────────────────────────────────────────────
Section("The timer");

Static(tracker, "Reset");
tracker.GetField("Now", Any).SetValue(null, (Func<DateTime>)(() => clockNow));
var lastTick = timer.GetField("LastTickUtc", Any);
void Tick() => ((Task)Static(timer, "TickAsync", sessions, clockNow, NullLogger.Instance)).GetAwaiter().GetResult();
double Used(Guid id) => (int)Get(Static(tracker, "State", id, clockNow), "UsedMinutes");

// Kid: 10 minutes a day, no hours. English this time.
// The language is said too: left out, it now keeps the French set above (R-1326).
Call("SetViewingLimits", MAIN, B(("HoursEnabled", false), ("Windows", WindowList()), ("DailyMinutes", 10), ("Language", ""), ("MasterPin", "1234")), KID);
Fresh();
stub.Sessions.Clear();
stub.Messages.Clear();
stub.Stopped.Clear();
var kidTv = SessionStub.Make("kid-tv", KID, item, deviceId: "dev-kid");
var kidTablet = SessionStub.Make("kid-tablet", KID, Guid.NewGuid(), deviceId: "dev-tab");
stub.Sessions.Add(kidTv);
stub.Sessions.Add(kidTablet);
clockNow = Utc(2026, 11, 5, 9);
lastTick.SetValue(null, null);
Tick();
Ok("the first look bills nothing", Used(KID) == 0);
for (var i = 0; i < 8; i++) { clockNow = clockNow.AddSeconds(30); Tick(); }
Ok("four minutes on two screens is four minutes", Used(KID) == 4, Used(KID).ToString());
SessionStub.Pause(kidTv, true);
SessionStub.Pause(kidTablet, true);
for (var i = 0; i < 4; i++) { clockNow = clockNow.AddSeconds(30); Tick(); }
Ok("paused time doesn't count", Used(KID) == 4, Used(KID).ToString());
SessionStub.Pause(kidTv, false);
stub.Sessions.Remove(kidTablet);
clockNow = clockNow.AddHours(1); Tick();
Ok("a long gap between looks bills a minute at most", Used(KID) == 5, Used(KID).ToString());
Ok("with five minutes left, a warning", stub.Messages.Count(m => m.session == "kid-tv") == 1
   && stub.Messages[0].header == "Time's almost up", string.Join(" | ", stub.Messages.Select(m => m.header + ": " + m.text)));
for (var i = 0; i < 4; i++) { clockNow = clockNow.AddSeconds(30); Tick(); }
Ok("the warning is given once", stub.Messages.Count(m => m.header == "Time's almost up") == 1, string.Join(" | ", stub.Messages.Select(m => m.header)));
Ok("and says when", stub.Messages[0].text.Contains("5 min"), stub.Messages[0].text);
Ok("nothing stopped yet", stub.Stopped.Count == 0);
for (var i = 0; i < 6; i++) { clockNow = clockNow.AddSeconds(30); Tick(); }
Ok("at the limit it stops, with no second five minutes", stub.Stopped.Contains("kid-tv") && Used(KID) >= 10, Used(KID) + " used, stopped " + string.Join(",", stub.Stopped));
Ok("and says so once", stub.Messages.Count(m => m.text.Contains("over")) == 1, string.Join(" | ", stub.Messages.Select(m => m.text)));
Ok("the daily-limit email is off unless asked for", Mails() == mailsBefore);

// Limits switched on under a running film: no warning was given, so five minutes are.
Static(tracker, "Reset");
accountType.GetProperty("NotifyLimitReached").SetValue(account, true);
Call("SetViewingLimits", MAIN, B(("DailyMinutes", 0), ("MasterPin", "1234")), KID);
Fresh();
stub.Messages.Clear(); stub.Stopped.Clear();
clockNow = Utc(2026, 11, 6, 9);
lastTick.SetValue(null, null);
Tick();
Static(tracker, "AddSeconds", KID, "2026-11-06", 120.0);
Call("SetViewingLimits", MAIN, B(("DailyMinutes", 1), ("MasterPin", "1234")), KID);
Fresh();
mailsBefore = Mails();
clockNow = clockNow.AddSeconds(30); Tick();
Ok("over with no warning: five minutes' notice", stub.Messages.Any(m => m.header == "Time's up" && m.text.Contains("5 min")) && stub.Stopped.Count == 0,
   string.Join(" | ", stub.Messages.Select(m => m.header + ": " + m.text)));
var mailsNow = Mails();
Ok("the limit email goes, now that it was asked for", mailsNow == mailsBefore + 1 && sent.Last().Subject.Contains("Kid"), sent.Last().Subject);
for (var i = 0; i < 8; i++) { clockNow = clockNow.AddSeconds(30); Tick(); }
Ok("not before the five minutes", stub.Stopped.Count == 0);
for (var i = 0; i < 3; i++) { clockNow = clockNow.AddSeconds(30); Tick(); }
Ok("and stopped after them", stub.Stopped.Contains("kid-tv"));
Ok("the limit email went once", Mails() == mailsNow);
Ok("the stream is refused once the grace is over, for an app that ignored the stop",
   Code(Static(gate, "Check", "/Videos/" + item + "/hls1/main/9.ts", KID, sessions, library, clockNow.AddMinutes(2))) == "daily_limit_reached");

var songSession = SessionStub.Make("kid-speaker", KID2, song, deviceId: "dev-speaker", media: MediaType.Audio);
Call("SetViewingLimits", MAIN, B(("DailyMinutes", 10), ("MasterPin", "1234")), KID2);
Fresh();
stub.Sessions.Add(songSession);
for (var i = 0; i < 4; i++) { clockNow = clockNow.AddSeconds(30); Tick(); }
Ok("music is not counted", Used(KID2) == 0);

// ── Kept ─────────────────────────────────────────────────────────────────────
Section("Kept");

Static(tracker, "AddSeconds", KID2, ViewingDay(clockNow), 300.0);
Static(tracker, "Save", new object[] { null });
var file = (string)Static(tracker, "FilePath");
Ok("usage is written to the plugin's folder", file != null && File.Exists(file), file);
Static(tracker, "Reset");
Ok("and read back after a restart", Used(KID2) == 5, Used(KID2).ToString());

var xs = new XmlSerializer(cfgType);
string xml;
using (var w = new StringWriter()) { xs.Serialize(w, config); xml = w.ToString(); }
object back;
using (var rd = new StringReader(xml)) back = xs.Deserialize(rd);
var backKid = ((IList)cfgType.GetProperty("Mappings").GetValue(back)).Cast<object>().First(m => (Guid)mapType.GetProperty("ProfileUserId").GetValue(m) == KID);
var backRules = mapType.GetProperty("Viewing").GetValue(backKid);
Ok("the rules survive a save", backRules != null && (int)Get(backRules, "DailyMinutes") == 1);
Ok("usage and extra time are not in the configuration", !xml.Contains("ExtraUntil") && !xml.Contains("Seconds"));
Call("SetViewingLimits", MAIN, B(("HoursEnabled", true), ("Windows", WindowList(Window(Weekdays, "15:00", "20:00"), Window(Sat | Sun, "09:00", "12:00"))), ("WeekendMinutes", (int?)0), ("MasterPin", "1234")), KID);
using (var w = new StringWriter()) { xs.Serialize(w, config); xml = w.ToString(); }
using (var rd = new StringReader(xml)) back = xs.Deserialize(rd);
backKid = ((IList)cfgType.GetProperty("Mappings").GetValue(back)).Cast<object>().First(m => (Guid)mapType.GetProperty("ProfileUserId").GetValue(m) == KID);
backRules = mapType.GetProperty("Viewing").GetValue(backKid);
Ok("windows come back once each, not doubled", ((IList)Get(backRules, "Windows")).Count == 2, ((IList)Get(backRules, "Windows")).Count.ToString());
Ok("a weekend of 0 comes back as 0, not as the same as weekdays", (int?)Get(backRules, "WeekendMinutes") == 0);
Ok("the default zone survives", (string)cfgType.GetProperty("DefaultTimeZone").GetValue(back) == "Europe/London");
Ok("the notice choices survive", !(bool)accountType.GetProperty("NotifyTimeAsked").GetValue(((IList)cfgType.GetProperty("AccountEmails").GetValue(back))[0])
   && (bool)accountType.GetProperty("NotifyLimitReached").GetValue(((IList)cfgType.GetProperty("AccountEmails").GetValue(back))[0]));

var snap = cfgType.GetMethod("SnapshotForReading").Invoke(config, null);
var snapKid = ((IList)cfgType.GetProperty("Mappings").GetValue(snap)).Cast<object>().First(m => (Guid)mapType.GetProperty("ProfileUserId").GetValue(m) == KID);
((IList)Get(mapType.GetProperty("Viewing").GetValue(snapKid), "Windows")).Clear();
Ok("a read snapshot has its own copy of the rules", ((IList)Get(StoredRules(KID), "Windows")).Count == 2);

string ViewingDay(DateTime utc) => (string)Static(clockType, "DayOf", london, utc);

try { Directory.Delete(tempDir, true); } catch { }
return Finish();

// ── Stubs ────────────────────────────────────────────────────────────────────

public class Mail
{
    public string To, Subject, Body;
}

public class Recorder : DispatchProxy
{
    public Action<Mail> OnSend;

    protected override object Invoke(MethodInfo m, object[] args)
    {
        if (m.Name != "SendAsync") throw new NotSupportedException(m.Name);
        OnSend(new Mail { To = (string)args[1], Subject = (string)args[2], Body = (string)args[3] });
        return Task.CompletedTask;
    }
}

public class SessionStub : DispatchProxy
{
    public List<SessionInfo> Sessions = new();
    public List<(string session, string header, string text)> Messages = new();
    public List<string> Stopped = new();

    public static ISessionManager Create() => Create<ISessionManager, SessionStub>();

    public static SessionInfo Make(string id, Guid user, Guid? item, string deviceId = null, MediaType media = MediaType.Video)
    {
        var t = typeof(SessionInfo);
        var s = (SessionInfo)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t);
        t.GetProperty("Id").SetValue(s, id);
        t.GetProperty("UserId").SetValue(s, user);
        t.GetProperty("DeviceId").SetValue(s, deviceId ?? id);
        t.GetProperty("NowPlayingItem").SetValue(s, item.HasValue ? new BaseItemDto { Id = item.Value, MediaType = media } : null);
        t.GetProperty("PlayState").SetValue(s, new PlayerStateInfo { IsPaused = false });
        return s;
    }

    public static void Pause(SessionInfo s, bool paused) => s.PlayState.IsPaused = paused;

    protected override object Invoke(MethodInfo m, object[] args)
    {
        switch (m.Name)
        {
            case "get_Sessions":
                return Sessions;
            case "SendMessageCommand":
                var cmd = (MessageCommand)args[2];
                Messages.Add(((string)args[1], cmd.Header, cmd.Text));
                return Task.CompletedTask;
            case "SendPlaystateCommand":
                if (((PlaystateRequest)args[2]).Command == PlaystateCommand.Stop) Stopped.Add((string)args[1]);
                return Task.CompletedTask;
        }
        throw new NotSupportedException("ISessionManager." + m.Name + " is not modelled");
    }
}

public class LibraryStub : DispatchProxy
{
    public Dictionary<Guid, MediaType> Items = new();

    public static ILibraryManager Create() => Create<ILibraryManager, LibraryStub>();

    protected override object Invoke(MethodInfo m, object[] args)
    {
        if (m.Name == "GetItemById" && args.Length == 1 && args[0] is Guid id)
        {
            if (!Items.TryGetValue(id, out var type)) return null;
            var t = type == MediaType.Audio ? typeof(MediaBrowser.Controller.Entities.Audio.Audio) : typeof(MediaBrowser.Controller.Entities.Movies.Movie);
            return System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t);
        }
        throw new NotSupportedException("ILibraryManager." + m.Name + " is not modelled");
    }
}

public class DeviceStub : DispatchProxy
{
    public static int Calls;
    private Dictionary<string, Guid> _tokens;

    public static IDeviceManager Create(Dictionary<string, Guid> tokens)
    {
        var proxy = Create<IDeviceManager, DeviceStub>();
        ((DeviceStub)(object)proxy)._tokens = tokens;
        return proxy;
    }

    protected override object Invoke(MethodInfo m, object[] args)
    {
        if (m.Name != "GetDevices") throw new NotSupportedException("IDeviceManager." + m.Name + " is not modelled");
        Calls++;
        var token = (string)args[0].GetType().GetProperty("AccessToken")?.GetValue(args[0]);
        var items = new List<Device>();
        if (token != null && _tokens.TryGetValue(token, out var user))
        {
            var d = (Device)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Device));
            typeof(Device).GetProperty("UserId").SetValue(d, user);
            items.Add(d);
        }
        return new QueryResult<Device>(items);
    }
}

public class NetworkStub : DispatchProxy
{
    public static INetworkManager Create() => Create<INetworkManager, NetworkStub>();

    protected override object Invoke(MethodInfo m, object[] args)
        => m.Name == "IsInLocalNetwork" ? false
           : throw new NotSupportedException("INetworkManager." + m.Name + " is not modelled");
}

public class World
{
    public Dictionary<Guid, (User user, bool admin)> Users = new();

    public void Add(Guid id, string name, bool admin = false)
    {
        var u = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty("Username").SetValue(u, name);
        typeof(User).GetProperty("Id").SetValue(u, id);
        Users[id] = (u, admin);
    }

    public static IUserManager Create(World w)
    {
        var p = DispatchProxy.Create<IUserManager, WorldProxy>();
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
                return W.Users.TryGetValue((Guid)args[0], out var row) ? row.user : null;
            case "GetUserDto":
                var user = (User)args[0];
                var admin = W.Users.TryGetValue(user.Id, out var r2) && r2.admin;
                return new UserDto { Id = user.Id, Name = user.Username, Policy = new UserPolicy { IsAdministrator = admin }, Configuration = new UserConfiguration() };
            case "get_Users":
            case "GetUsers":
                return W.Users.Values.Select(v => v.user).ToList();
        }
        throw new NotSupportedException("IUserManager." + m.Name + " is not modelled");
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
