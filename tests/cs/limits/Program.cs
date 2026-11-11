using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;

// ─────────────────────────────────────────────────────────────────────────────
// Bonfire provides campground.limits.v1, and its viewing timer counts what other
// Campground plugins are doing (Campground-Coordination #32).
//
//  - canPlayNow(userId) answers from the same rules the playback gate uses: no rule, an
//    admin or a non-profile is allowed; outside hours or over the day's time is refused,
//    with when it opens again and how long a running activity may carry on; the emergency
//    disable means allowed.
//  - The timer adds every activity that has a UserId and is not paused, once per tick
//    however many things the user is doing at once.
//
// Against 3af7a5e nothing offers the capability and the timer reads only Jellyfin's
// sessions, so every assertion below the first two fails.
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

var pluginType = asm.GetType("Jellyfin.Profiles.Plugin", true);
var cfgType = asm.GetType("Jellyfin.Profiles.Configuration.PluginConfiguration", true);
var mapType = asm.GetType("Jellyfin.Profiles.Configuration.ProfileMapping", true);
var rulesType = asm.GetType("Jellyfin.Profiles.Configuration.ViewingRules", true);
var windowType = asm.GetType("Jellyfin.Profiles.Configuration.ViewingWindow", true);
var tracker = asm.GetType("Jellyfin.Profiles.Auth.ViewingTracker", true);
var timer = asm.GetType("Jellyfin.Profiles.Auth.ViewingTimer", true);
var provider = asm.GetType("Jellyfin.Profiles.Auth.LimitsProvider");
var limitsType = asm.GetType("Campground.Shared.Contracts.Limits", true);
var sessionsType = asm.GetType("Campground.Shared.Contracts.Sessions", true);
var activityType = asm.GetType("Campground.Shared.Contracts.Activity", true);
var namesType = asm.GetType("Campground.Shared.Registry.CapabilityNames", true);
var capsType = asm.GetType("Campground.Shared.Registry.Capabilities", true);

object Get(object o, string prop) => o?.GetType().GetProperty(prop, Any)?.GetValue(o);
object Static(Type t, string name, params object[] args)
{
    var m = t.GetMethods(Any).Where(x => x.Name == name && x.GetParameters().Length == args.Length)
        .FirstOrDefault(x => x.GetParameters().Select((p, i) => args[i] == null || p.ParameterType.IsInstanceOfType(args[i])
            || (Nullable.GetUnderlyingType(p.ParameterType)?.IsInstanceOfType(args[i]) ?? false)).All(ok => ok));
    if (m == null) throw new MissingMethodException(t.Name + "." + name);
    try { return m.Invoke(null, args); }
    catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
}
DateTime Utc(int y, int mo, int d, int h, int mi = 0) => new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Utc);

// ── The world ────────────────────────────────────────────────────────────────
var tempDir = Path.Combine(Path.GetTempPath(), "bonfire-limits-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
var plugin = Activator.CreateInstance(pluginType, new StubPaths(tempDir), new StubXml());
var config = pluginType.GetProperty("Configuration").GetValue(plugin);
cfgType.GetProperty("DefaultTimeZone").SetValue(config, "Europe/London");
var mappings = (IList)cfgType.GetProperty("Mappings").GetValue(config);

var MAIN = Guid.NewGuid();
var KID = Guid.NewGuid();    // 15:00-20:00 on weekdays, 60 minutes a day
var FREE = Guid.NewGuid();   // a profile with no rule
var STRANGER = Guid.NewGuid();
void Map(Guid profile, Guid master, string name, object rules = null)
{
    var m = Activator.CreateInstance(mapType);
    mapType.GetProperty("ProfileUserId").SetValue(m, profile);
    mapType.GetProperty("MasterUserId").SetValue(m, master);
    mapType.GetProperty("ProfileName").SetValue(m, name);
    if (rules != null) mapType.GetProperty("Viewing").SetValue(m, rules);
    mappings.Add(m);
}
var w = Activator.CreateInstance(windowType);
windowType.GetProperty("Days").SetValue(w, 2 | 4 | 8 | 16 | 32);
windowType.GetProperty("Start").SetValue(w, "15:00");
windowType.GetProperty("End").SetValue(w, "20:00");
var rules = Activator.CreateInstance(rulesType);
rulesType.GetProperty("HoursEnabled").SetValue(rules, true);
rulesType.GetProperty("DailyMinutes").SetValue(rules, 60);
((IList)rulesType.GetProperty("Windows").GetValue(rules)).Add(w);
Map(MAIN, MAIN, "Parent");
Map(KID, MAIN, "Kid", rules);
Map(FREE, MAIN, "Free");

// 2026-11-04 is a Wednesday; London is on GMT.
var clockNow = Utc(2026, 11, 4, 16);
tracker.GetField("Now", Any).SetValue(null, (Func<DateTime>)(() => clockNow));
void Fresh() { Static(tracker, "Reset"); Static(tracker, "Invalidate"); }
Fresh();

var sessions = SessionStub.Create();
var stub = (SessionStub)(object)sessions;
var lastTick = timer.GetField("LastTickUtc", Any);
void Tick() => ((Task)Static(timer, "TickAsync", sessions, clockNow, NullLogger.Instance)).GetAwaiter().GetResult();
int Used(Guid id) => (int)Get(Static(tracker, "State", id, clockNow), "UsedMinutes");

// ── The provider ─────────────────────────────────────────────────────────────
Section("The provider");

Ok("Bonfire has a LimitsProvider", provider != null);
if (provider != null) Static(provider, "Register");
var limitsName = (string)namesType.GetField("Limits", Any).GetValue(null);
var offered = (bool)Static(capsType, "IsAvailable", limitsName, 0);
Ok("and offers campground.limits.v1", offered);

// Through the registry, as another plugin calls it. Null when nobody answers: the shared
// helper fails open, so an unanswered question would read as "allowed" and prove nothing.
object Ask(Guid user)
{
    if (!offered) return null;
    var t = (Task)Static(limitsType, "CanPlayNowAsync", user, null, TimeSpan.FromSeconds(5), CancellationToken.None);
    t.GetAwaiter().GetResult();
    return t.GetType().GetProperty("Result").GetValue(t);
}
string Show(object d) => d == null ? "no answer" : string.Join(", ", new[] { "Allowed", "Reason", "Until", "GraceSeconds" }.Select(p => p + "=" + Get(d, p)));
bool Allowed(object d) => d != null && (bool)Get(d, "Allowed");

var d1 = Ask(FREE);
Ok("a profile with no rule is allowed", Allowed(d1) && Get(d1, "Reason") == null && (int)Get(d1, "GraceSeconds") == 0, Show(d1));
d1 = Ask(MAIN);
Ok("a main account is allowed", Allowed(d1), Show(d1));
d1 = Ask(STRANGER);
Ok("a user Bonfire has never heard of is allowed", Allowed(d1), Show(d1));

clockNow = Utc(2026, 11, 4, 19, 30); Fresh();   // 30 minutes of hours left, 60 of limit: the hours bind
d1 = Ask(KID);
Ok("when the hours end first: allowed, until they do", Allowed(d1) && (DateTimeOffset?)Get(d1, "Until") == new DateTimeOffset(Utc(2026, 11, 4, 20)), Show(d1));

clockNow = Utc(2026, 11, 4, 14); Fresh();
d1 = Ask(KID);
Ok("outside the hours: refused with the reason", d1 != null && !Allowed(d1) && (string)Get(d1, "Reason") == "outside_viewing_hours", Show(d1));
Ok("and when the hours open", d1 != null && (DateTimeOffset?)Get(d1, "Until") == new DateTimeOffset(Utc(2026, 11, 4, 15)), Show(d1));
Ok("a running activity gets Bonfire's five minutes", d1 != null && (int)Get(d1, "GraceSeconds") == 300, Show(d1));

clockNow = Utc(2026, 11, 4, 16); Fresh();
Static(tracker, "AddSeconds", KID, "2026-11-04", 3600.0);
d1 = Ask(KID);
Ok("over the day's time: refused with the reason", d1 != null && !Allowed(d1) && (string)Get(d1, "Reason") == "daily_limit_reached", Show(d1));
Ok("and when it opens again: tomorrow at 15:00", d1 != null && (DateTimeOffset?)Get(d1, "Until") == new DateTimeOffset(Utc(2026, 11, 5, 15)), Show(d1));

// A video that was warned about already has a grace running; a game asked about now gets what
// is left of it, so both stop together. A grace left over from an earlier day must not cut a
// game that starts later.
var entry = Static(tracker, "EntryFor", KID);
var graceField = entry.GetType().GetField("GraceEndsUtc", Any);
graceField.SetValue(entry, (DateTime?)clockNow.AddMinutes(2));
d1 = Ask(KID);
Ok("inside Bonfire's grace the answer carries what is left of it", d1 != null && !Allowed(d1) && (int)Get(d1, "GraceSeconds") == 120, Show(d1));
graceField.SetValue(entry, (DateTime?)clockNow.AddDays(-1));
d1 = Ask(KID);
Ok("a grace left over from an earlier day is not read as 'now'", d1 != null && (int)Get(d1, "GraceSeconds") == 300, Show(d1));
graceField.SetValue(entry, null);

var panic = pluginType.GetField("_panicDisabled", Any);
panic.SetValue(null, true); Fresh();
d1 = Ask(KID);
Ok("the emergency disable means allowed", Allowed(d1) && offered, Show(d1));
panic.SetValue(null, false); Fresh();

// ── Maintenance ──────────────────────────────────────────────────────────────
// Maintenance refuses everyone but administrators. Whether a user is one is not a call Jellyfin
// can answer quickly (UserManager.GetUserById is a database query), so the provider keeps the
// ids of the administrators and refreshes them off the call; the call reads memory only.
Section("Maintenance");

var ADMIN = Guid.NewGuid();
var hasSetAdmins = provider != null && provider.GetMethod("SetAdmins", Any) != null;
var hasForgetAdmins = provider != null && provider.GetMethod("ForgetAdmins", Any) != null;
Ok("the provider keeps a set of administrator ids it can be handed", hasSetAdmins && hasForgetAdmins);
void SetAdmins(params Guid[] ids) { if (hasSetAdmins) Static(provider, "SetAdmins", ids.AsEnumerable(), DateTime.UtcNow); }

var mmType = asm.GetType("Jellyfin.Profiles.Configuration.MaintenanceMode", true);
void Maintenance(bool on, DateTime? until = null, bool auto = false)
{
    var m = Activator.CreateInstance(mmType);
    mmType.GetProperty("Enabled").SetValue(m, on);
    mmType.GetProperty("UntilUtc").SetValue(m, until);
    mmType.GetProperty("ReopenAutomatically").SetValue(m, auto);
    cfgType.GetProperty("Maintenance").SetValue(config, m);
}

clockNow = Utc(2026, 11, 4, 16); Fresh();
SetAdmins(ADMIN);
Maintenance(false);
d1 = Ask(FREE);
Ok("maintenance off: a profile is allowed", Allowed(d1), Show(d1));

Maintenance(true);
d1 = Ask(FREE);
Ok("maintenance on: a non-administrator is refused with the reason",
   d1 != null && !Allowed(d1) && (string)Get(d1, "Reason") == "maintenance", Show(d1));
Ok("with no end set, there is no Until", d1 != null && Get(d1, "Until") == null, Show(d1));
Ok("a running activity gets Bonfire's five minutes", d1 != null && (int)Get(d1, "GraceSeconds") == 300, Show(d1));
d1 = Ask(ADMIN);
Ok("an administrator is allowed", Allowed(d1), Show(d1));
d1 = Ask(STRANGER);
Ok("a user Bonfire has never heard of is refused too", d1 != null && !Allowed(d1) && (string)Get(d1, "Reason") == "maintenance", Show(d1));
d1 = Ask(KID);
Ok("maintenance is said before viewing hours", d1 != null && (string)Get(d1, "Reason") == "maintenance", Show(d1));

Maintenance(true, clockNow.AddHours(2), true);
d1 = Ask(FREE);
Ok("with an end set: Until is when the server reopens",
   d1 != null && (string)Get(d1, "Reason") == "maintenance" && (DateTimeOffset?)Get(d1, "Until") == new DateTimeOffset(clockNow.AddHours(2)), Show(d1));

Maintenance(true, clockNow.AddMinutes(-1), true);
d1 = Ask(FREE);
Ok("a closure set to reopen by itself, and past its time, is not on", Allowed(d1), Show(d1));

Maintenance(true, clockNow.AddMinutes(-1), false);
d1 = Ask(FREE);
Ok("an end time that has passed, with no automatic reopening, is not offered as Until",
   d1 != null && !Allowed(d1) && Get(d1, "Until") == null, Show(d1));

Maintenance(true);
if (hasForgetAdmins) Static(provider, "ForgetAdmins");
d1 = Ask(FREE);
Ok("before the administrators are known the answer is allowed, not a guess", hasForgetAdmins && Allowed(d1), Show(d1));
SetAdmins(ADMIN);

panic.SetValue(null, true); Fresh();
d1 = Ask(FREE);
Ok("the emergency disable means allowed", Allowed(d1) && offered, Show(d1));
panic.SetValue(null, false); Fresh();
Maintenance(false);

// ── The timer ────────────────────────────────────────────────────────────────
Section("The timer");

void Publish(string id, Guid? user, bool paused, string plugin = "gametent")
{
    var a = Activator.CreateInstance(activityType, id, plugin, "game", user, null, "A game",
        DateTime.UtcNow, DateTime.UtcNow, (long?)null, false, null, paused);
    Static(sessionsType, "PublishActivity", a);
}
void ClearActivities()
{
    var all = (IEnumerable)Static(sessionsType, "ListActivities", (object)null);
    foreach (var a in all.Cast<object>().ToList()) Static(sessionsType, "EndActivity", (string)Get(a, "Id"));
}
void Minutes(int ticks) { for (var i = 0; i < ticks; i++) { clockNow = clockNow.AddSeconds(30); Tick(); } }
void Restart()
{
    ClearActivities();
    stub.Sessions.Clear();
    clockNow = Utc(2026, 11, 5, 16); Fresh();
    lastTick.SetValue(null, null);
    Tick();
}

Restart();
Publish("g1", KID, false);
Minutes(8);
Ok("a game running adds four minutes in four minutes", Used(KID) == 4, Used(KID).ToString());

Restart();
Publish("g1", KID, true);
Minutes(8);
Ok("a paused game adds nothing", Used(KID) == 0, Used(KID).ToString());
Publish("g1", KID, false);
Minutes(4);
Ok("and counts again once it runs", Used(KID) == 2, Used(KID).ToString());

Restart();
Publish("dl", null, false);
Minutes(8);
Ok("an activity with no user counts against nobody", Used(KID) == 0, Used(KID).ToString());

Restart();
Publish("g1", KID, false);
stub.Sessions.Add(SessionStub.Make("kid-tv", KID, Guid.NewGuid()));
Minutes(8);
Ok("a video and a game at once count once, not twice", Used(KID) == 4, Used(KID).ToString());

Restart();
Publish("g1", KID, false);
Publish("g2", KID, false);
Minutes(8);
Ok("two games at once count once", Used(KID) == 4, Used(KID).ToString());

Restart();
Publish("own", KID, false, "bonfire");
Minutes(8);
Ok("an activity Bonfire published itself is not counted a second time", Used(KID) == 0, Used(KID).ToString());

Restart();
Publish("g1", FREE, false);
Minutes(8);
Ok("a game for a profile with no rule is not tracked", Static(tracker, "State", FREE, clockNow) == null);

ClearActivities();
return Finish();

public class SessionStub : DispatchProxy
{
    public List<SessionInfo> Sessions = new();

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

    protected override object Invoke(MethodInfo m, object[] args)
    {
        switch (m.Name)
        {
            case "get_Sessions": return Sessions;
            case "SendMessageCommand": return Task.CompletedTask;
            case "SendPlaystateCommand": return Task.CompletedTask;
        }
        throw new NotSupportedException("ISessionManager." + m.Name + " is not modelled");
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
