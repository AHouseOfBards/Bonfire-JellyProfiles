using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

// ─────────────────────────────────────────────────────────────────────────────
// First-time setup (1.7.2), through the real routes.
//
// What it holds the plugin to:
//
//  - Off by default. Turned on, it is for accounts made from then on: every account that
//    already existed counts as having seen it, once, and only the first time it goes on.
//  - Main accounts only. A profile is never pending, and its "done" records nothing.
//  - Once per account, not per device: done is stored on the server.
//  - "Show again to everyone" un-sees it for every main account at once.
//  - Every step is always offered, in order; none can be switched off and the welcome is
//    Bonfire's own. A step whose feature is off is not offered: theme needs profile
//    themes. An old dashboard's step list and welcome are ignored.
//  - The setting survives Jellyfin's XmlSerializer without its lists doubling.
//  - The admin routes are admin only.
//
// Against edf8518 every assertion past the first fails: the routes do not exist.
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
var pluginType = asm.GetType("Jellyfin.Profiles.Plugin", true);
var cfgType = asm.GetType("Jellyfin.Profiles.Configuration.PluginConfiguration", true);
var mapType = asm.GetType("Jellyfin.Profiles.Configuration.ProfileMapping", true);
var hasher = asm.GetType("Jellyfin.Profiles.Auth.PinHasher", true);
var obType = asm.GetType("Jellyfin.Profiles.Configuration.OnboardingSettings");

Ok("this build has first-time setup", obType != null && ctrlType.GetMethod("GetStartup") != null && ctrlType.GetMethod("GetOnboarding") == null
   && ctrlType.GetMethod("OnboardingDone") != null && ctrlType.GetMethod("ShowOnboardingAgain") != null);
if (obType == null)
{
    Console.WriteLine();
    Console.WriteLine(pass + " passed, " + fails.Count + " failed");
    return 1;
}

var tempDir = Path.Combine(Path.GetTempPath(), "bonfire-onboarding-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
var plugin = Activator.CreateInstance(pluginType, new StubPaths(tempDir), new StubXml());
var config = pluginType.GetProperty("Configuration").GetValue(plugin);
var mappings = (IList)cfgType.GetProperty("Mappings").GetValue(config);
object Onboarding() => cfgType.GetProperty("Onboarding").GetValue(config);
object Ob(string prop) => obType.GetProperty(prop).GetValue(Onboarding());

void Map(Guid profile, Guid master, string pin = null)
{
    var m = Activator.CreateInstance(mapType);
    mapType.GetProperty("ProfileUserId").SetValue(m, profile);
    mapType.GetProperty("MasterUserId").SetValue(m, master);
    mapType.GetProperty("ProfileName").SetValue(m, "p");
    if (pin != null) mapType.GetProperty("PinHash").SetValue(m, hasher.GetMethod("Hash").Invoke(null, new object[] { pin }));
    mappings.Add(m);
}

var world = new World();
var users = World.Create(world);

(int status, object value) Call(string method, Guid caller, Dictionary<string, object> body = null, string json = null)
{
    var route = ctrlType.GetMethod(method);
    if (route == null) return (-404, "no route " + method);

    var ctor = ctrlType.GetConstructors().First();
    var args = ctor.GetParameters().Select(p =>
        p.ParameterType == typeof(IUserManager) ? users
        : p.ParameterType.Name.StartsWith("ILogger") ? Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(ctrlType))
        : (object)null).ToArray();
    var c = (ControllerBase)ctor.Invoke(args);
    c.ControllerContext = new ControllerContext
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", caller.ToString()) }, "test")),
        },
    };

    var invokeArgs = route.GetParameters().Select(p =>
    {
        if (p.ParameterType.IsValueType) return Activator.CreateInstance(p.ParameterType);
        // A raw JSON body binds the way the framework binds it: a property the type no
        // longer has is ignored, which is what an old dashboard's request now meets.
        if (json != null)
            return System.Text.Json.JsonSerializer.Deserialize(json, p.ParameterType,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
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
    try { result = route.Invoke(c, invokeArgs); }
    catch (TargetInvocationException ex) { return (-500, ex.InnerException?.GetType().Name + ": " + ex.InnerException?.Message); }

    var inner = result?.GetType().GetProperty("Result")?.GetValue(result) ?? result;
    return inner switch
    {
        ObjectResult o => (o.StatusCode ?? 200, o.Value),
        StatusCodeResult s => (s.StatusCode, null),
        _ => (-1, inner?.GetType().Name),
    };
}

object Get(object o, string prop) => o?.GetType().GetProperty(prop)?.GetValue(o);

bool Pending(Guid who)
{
    var r = Call("GetStartup", who);
    return r.status == 200 && (bool)Get(Get(r.value, "onboarding"), "Pending");
}
List<(string id, bool done)> Steps(Guid who)
{
    var r = Call("GetStartup", who);
    return ((IEnumerable)Get(Get(r.value, "onboarding"), "Steps")).Cast<object>()
        .Select(s => ((string)Get(s, "Id"), (bool)Get(s, "Done"))).ToList();
}
bool PrefsPending(Guid who)
{
    var r = Call("GetPreferences", who);
    var v = Get(r.value, "OnboardingPending");
    return v is bool b && b;
}
var ADMIN = Guid.NewGuid(); world.Add(ADMIN, "Admin", admin: true);
(int status, object value) Admin(Dictionary<string, object> body, Guid? caller = null) => Call("UpdateAdminSettings", caller ?? ADMIN, body);

// ── Before it is turned on ───────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── Off by default ──────────────────────────────────────────────");

var OLD = Guid.NewGuid(); world.Add(OLD, "Old");
var OLD_KID = Guid.NewGuid(); world.Add(OLD_KID, "Old_kid");
Map(OLD, OLD); Map(OLD_KID, OLD);

Ok("nobody is pending while it is off", !Pending(OLD) && !Pending(ADMIN));
Ok("and /preferences says so", Call("GetPreferences", OLD).status == 200 && !PrefsPending(OLD));

// ── Turning it on ────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── Turning it on ───────────────────────────────────────────────");

var notAdmin = Admin(new() { ["OnboardingEnabled"] = (bool?)true }, OLD);
Ok("only an administrator can turn it on", notAdmin.status == 401 && !(bool)Ob("Enabled"), notAdmin.status + " " + notAdmin.value);

var on = Admin(new() { ["OnboardingEnabled"] = (bool?)true });
Ok("an administrator can", on.status == 200 && (bool)Ob("Enabled"), on.status + " " + on.value);
Ok("accounts that already existed count as having seen it", !Pending(OLD) && !Pending(ADMIN));

var NEW = Guid.NewGuid(); world.Add(NEW, "New");
Ok("an account made afterwards is pending", Pending(NEW));
Ok("and /preferences says so, where the web client looks first", PrefsPending(NEW));
Ok("a profile never is", !Pending(OLD_KID) && !PrefsPending(OLD_KID));

var ids = Steps(NEW).Select(s => s.id).ToList();
Ok("every step is offered by default, in order",
   ids.SequenceEqual(new[] { "welcome", "mainPin", "firstProfile", "switcher", "theme" }), string.Join(",", ids));
// tests/cs/email covers it once email works; here it never does.
Ok("an email address is not asked for while email isn't working", !ids.Contains("resetEmail"));

cfgType.GetProperty("AllowProfileThemes").SetValue(config, false);
Ok("theme is not offered while profiles can't have their own", !Steps(NEW).Any(s => s.id == "theme"));
cfgType.GetProperty("AllowProfileThemes").SetValue(config, true);

// Owner, 2026-11-12: a step with one card has nothing to choose. Bonfire can't be hidden,
// so one theme on offer is every other built-in hidden and no custom theme.
{
    var hidden = (List<string>)cfgType.GetProperty("HiddenThemes").GetValue(config);
    var builtIns = (string[])asm.GetType("Jellyfin.Profiles.Configuration.Themes", true).GetField("All").GetValue(null);
    var hiddenBefore = hidden.ToList();
    hidden.Clear();
    hidden.AddRange(builtIns.Where(b => b != "bonfire" && b != "crimson"));
    Ok("two themes on offer: the theme step is offered", Steps(NEW).Any(s => s.id == "theme"));
    hidden.Add("crimson");
    Ok("only Bonfire on offer: the theme step is not", !Steps(NEW).Any(s => s.id == "theme"));
    hidden.Clear();
    hidden.AddRange(hiddenBefore);
}

// ── The question on a new install ────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── The question on a new install ───────────────────────────────");

var askedProp = obType.GetProperty("Asked");
Ok("the setting remembers that the administrator has been asked", askedProp != null);
if (askedProp != null)
{
    Ok("not asked until answered", !(bool)Ob("Asked"));
    var enabledBefore = (bool)Ob("Enabled");
    var answerNo = Admin(new() { ["OnboardingAsked"] = (bool?)true });
    Ok("answering says so and changes nothing else",
       answerNo.status == 200 && (bool)Ob("Asked") && (bool)Ob("Enabled") == enabledBefore,
       answerNo.status + " " + answerNo.value);
    var leaveAlone = Admin(new());
    Ok("a save that doesn't mention it leaves it alone", leaveAlone.status == 200 && (bool)Ob("Asked"));
    var notAdminAsk = Admin(new() { ["OnboardingAsked"] = (bool?)false }, OLD);
    Ok("only an administrator can answer", notAdminAsk.status == 401 && (bool)Ob("Asked"), notAdminAsk.status + " " + notAdminAsk.value);
    // XmlSerializer is how Jellyfin keeps it between starts.
    var xmlAsked = new XmlSerializer(obType);
    var wasAsked = Onboarding();
    using (var sw = new StringWriter()) { xmlAsked.Serialize(sw, wasAsked); using var sr = new StringReader(sw.ToString());
        Ok("and it survives the serializer", (bool)obType.GetProperty("Asked").GetValue(xmlAsked.Deserialize(sr))); }
    Admin(new() { ["OnboardingAsked"] = (bool?)false });
}

// ── Getting started, dismissed ───────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── Getting started, dismissed ──────────────────────────────────");
// Owner, 2026-11-12: every Getting started item can be dismissed. Kept on the server so
// it holds for every administrator.
var dismissedProp = obType.GetProperty("Dismissed");
Ok("the setting keeps what was dismissed", dismissedProp != null);
if (dismissedProp != null)
{
    List<string> Dismissed() => ((IEnumerable<string>)Ob("Dismissed")).ToList();
    var d1 = Admin(new() { ["GettingStartedDismiss"] = "pins" });
    Ok("dismissing one item keeps it", d1.status == 200 && Dismissed().SequenceEqual(new[] { "pins" }), d1.status + " " + d1.value);
    Admin(new() { ["GettingStartedDismiss"] = "pins" });
    Ok("twice is kept once", Dismissed().Count == 1);
    var bad = Admin(new() { ["GettingStartedDismiss"] = "nonsense" });
    Ok("an unknown item is refused and nothing changes", bad.status == 400 && Dismissed().Count == 1, bad.status + " " + bad.value);
    var notAdminD = Admin(new() { ["GettingStartedDismiss"] = "zone" }, OLD);
    Ok("only an administrator can dismiss", notAdminD.status == 401 && !Dismissed().Contains("zone"));
    Admin(new());
    Ok("a save that doesn't mention it leaves it alone", Dismissed().SequenceEqual(new[] { "pins" }));
    var xmlD = new XmlSerializer(obType);
    using (var sw = new StringWriter()) { xmlD.Serialize(sw, Onboarding()); using var sr = new StringReader(sw.ToString());
        Ok("and it survives the serializer", ((IEnumerable<string>)dismissedProp.GetValue(xmlD.Deserialize(sr))).SequenceEqual(new[] { "pins" })); }
    ((List<string>)Ob("Dismissed")).Clear();
}

// ── Steps ────────────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── Choosing steps ──────────────────────────────────────────────");

// The administrator could once pick which steps to show (five "show this step" boxes) and
// write the welcome. Both are cut: every step is always offered, each can be skipped, and
// the welcome is Bonfire's own. The boxes had no entry for resetEmail, so saving the tab
// dropped the email step for good; with no list there is nothing to drop it from.
var reqType = asm.GetType("Jellyfin.Profiles.Models.AdminSettingsRequest", true);
Ok("there is no step list on the request", reqType.GetProperty("OnboardingSteps") == null);
Ok("and no welcome text", reqType.GetProperty("OnboardingWelcomeText") == null);
Ok("nor a required-steps field (cut earlier)", reqType.GetProperty("OnboardingRequiredSteps") == null);
Ok("none stored: no steps, no chosen flag, no welcome, no length limit",
   obType.GetProperty("Steps") == null && obType.GetProperty("StepsChosen") == null
   && obType.GetProperty("WelcomeText") == null && obType.GetField("MaxWelcomeLength") == null
   && obType.GetProperty("RequiredSteps") == null);
Ok("the client is sent no welcome text", asm.GetType("Jellyfin.Profiles.Models.OnboardingResponse", true).GetProperty("WelcomeText") == null);
Ok("and no 'required' flag on a step", asm.GetType("Jellyfin.Profiles.Models.OnboardingStepInfo", true).GetProperty("Required") == null);

List<string> OfferedNow() => ((IEnumerable<string>)obType.GetMethod("OfferedSteps").Invoke(Onboarding(), null)).ToList();
// Exactly what the 1.7 dashboard's Save sent: the ticked steps, which never included
// resetEmail, and a welcome. A client that still sends it is ignored.
var oldSave = Call("UpdateAdminSettings", ADMIN, null,
    "{\"onboardingSteps\":[\"welcome\",\"mainPin\",\"firstProfile\",\"switcher\",\"theme\"],\"onboardingWelcomeText\":\"  Welcome home.  \"}");
Ok("an old dashboard's save is accepted", oldSave.status == 200, oldSave.status + " " + oldSave.value);
Ok("and every step is still offered, the email step included",
   OfferedNow().SequenceEqual(new[] { "welcome", "mainPin", "resetEmail", "firstProfile", "switcher", "theme" }), string.Join(",", OfferedNow()));
var badStep = Call("UpdateAdminSettings", ADMIN, null, "{\"onboardingSteps\":[\"welcome\",\"nonsense\"]}");
Ok("a step list is not read at all, so even a bad one changes nothing", badStep.status == 200 && OfferedNow().Count == 6, badStep.status + " " + badStep.value);
Ok("the steps the client is sent are the offered ones, in order", Steps(NEW).Select(s => s.id).SequenceEqual(new[] { "welcome", "mainPin", "firstProfile", "switcher", "theme" }));

// ── What is already done ─────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── What is already done ────────────────────────────────────────");

var s0 = Steps(NEW);
Ok("an account with no PIN and no profiles has nothing done", s0.All(s => !s.done));
Map(NEW, NEW, pin: "2468");
var NEW_KID = Guid.NewGuid(); world.Add(NEW_KID, "New_kid"); Map(NEW_KID, NEW);
var s1 = Steps(NEW);
Ok("a PIN marks mainPin done", s1.Single(s => s.id == "mainPin").done);
Ok("a profile marks firstProfile done", s1.Single(s => s.id == "firstProfile").done);
Ok("being done doesn't stop it being offered", Pending(NEW));

// ── Done ─────────────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── Done ────────────────────────────────────────────────────────");

var kidDone = Call("OnboardingDone", NEW_KID);
Ok("a profile's done is accepted and records nothing",
   kidDone.status == 200 && !((IEnumerable)Ob("Seen")).Cast<object>().Any(r => (Guid)Get(r, "UserId") == NEW_KID));
var done = Call("OnboardingDone", NEW);
Ok("done ends it for the account", done.status == 200 && !Pending(NEW) && !PrefsPending(NEW));
Call("OnboardingDone", NEW);
Ok("done twice keeps one record", ((IEnumerable)Ob("Seen")).Cast<object>().Count(r => (Guid)Get(r, "UserId") == NEW) == 1);
Ok("an API key (no user) is refused, not recorded", Call("OnboardingDone", Guid.Empty).status == 401);

// ── Off and on again ─────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── Off and on again ────────────────────────────────────────────");

Admin(new() { ["OnboardingEnabled"] = (bool?)false });
Ok("off means nobody is pending", !Pending(NEW));
var BETWEEN = Guid.NewGuid(); world.Add(BETWEEN, "Between");
Admin(new() { ["OnboardingEnabled"] = (bool?)true });
Ok("an account made while it was off still sees it: existing accounts are excused once", Pending(BETWEEN));

// ── Show again ───────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── Show again to everyone ──────────────────────────────────────");

var gen = (int)Ob("Generation");
var refused = Call("ShowOnboardingAgain", OLD);
Ok("only an administrator can", refused.status == 401 && (int)Ob("Generation") == gen, refused.status + " " + refused.value);
var again = Call("ShowOnboardingAgain", ADMIN);
Ok("it moves to the next generation", again.status == 200 && (int)Ob("Generation") == gen + 1 && (int)Get(again.value, "Generation") == gen + 1,
   again.status + " " + again.value);
Ok("every main account sees it again: old, new, administrator", Pending(OLD) && Pending(NEW) && Pending(ADMIN));
Ok("profiles still don't", !Pending(OLD_KID) && !Pending(NEW_KID));
Call("OnboardingDone", OLD);
Ok("and done ends it again", !Pending(OLD) && Pending(NEW));

// ── Stored ───────────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── Stored ──────────────────────────────────────────────────────");

var ser = new XmlSerializer(cfgType);
object RoundTrip(object c)
{
    using var ms = new MemoryStream();
    ser.Serialize(ms, c);
    ms.Position = 0;
    return ser.Deserialize(ms);
}
var back = RoundTrip(config);
var backOb = cfgType.GetProperty("Onboarding").GetValue(back);
Ok("who has seen it",
   ((IEnumerable)obType.GetProperty("Seen").GetValue(backOb)).Cast<object>().Count() == ((IEnumerable)Ob("Seen")).Cast<object>().Count());
Ok("and the generation and the one-time excuse",
   (int)obType.GetProperty("Generation").GetValue(backOb) == (int)Ob("Generation")
   && (bool)obType.GetProperty("ExistingAccountsExempted").GetValue(backOb));

// The first draft used a null list for "the default steps". The serializer creates every
// list it reads, so after one save it was an empty list: enabled, and offering nothing.
// There is no list now; this keeps it that way.
List<string> Offered(object cfg) => ((IEnumerable<string>)obType.GetMethod("OfferedSteps")
    .Invoke(cfgType.GetProperty("Onboarding").GetValue(cfg), null)).ToList();
var fresh = Activator.CreateInstance(cfgType);
Ok("the default offers every step after two saves",
   string.Join(",", Offered(RoundTrip(RoundTrip(fresh)))) == "welcome,mainPin,resetEmail,firstProfile,switcher,theme",
   string.Join(",", Offered(RoundTrip(RoundTrip(fresh)))));

var snap = cfgType.GetMethod("SnapshotForReading").Invoke(config, null);
var snapSeen = (IList)obType.GetProperty("Seen").GetValue(cfgType.GetProperty("Onboarding").GetValue(snap));
var before = ((IList)Ob("Seen")).Count;
snapSeen.Clear();
Ok("a read snapshot has its own copy", ((IList)Ob("Seen")).Count == before && before > 0);

// IUserManager.Users became GetUsers() in 10.11.7, so a direct call throws there. The
// first draft of this feature made one. GetAllUsers resolves whichever exists.
var direct = Directory.GetFiles(Path.Combine(RepoRoot(), "Controllers"), "*.cs")
    .SelectMany(f => File.ReadAllLines(f).Select((l, i) => (f, i, l)))
    .Where(x => System.Text.RegularExpressions.Regex.IsMatch(x.l, @"_userManager\.Users\b") && !x.l.TrimStart().StartsWith("//"))
    .Select(x => Path.GetFileName(x.f) + ":" + (x.i + 1)).ToList();
Ok("no controller reads _userManager.Users directly", direct.Count == 0, string.Join(", ", direct));

// PIN tries a day (R-1002): a security limit an administrator may lower, never raise.
Console.WriteLine();
Console.WriteLine("── PIN tries a day ─────────────────────────────────────────────");
var triesProp = cfgType.GetProperty("PinGuessesPerDay");
Ok("it is twenty unless lowered", triesProp != null && (int)triesProp.GetValue(Activator.CreateInstance(cfgType)) == 20);
var lowered = Admin(new() { ["PinGuessesPerDay"] = (int?)5 });
Ok("an administrator can lower it", lowered.status == 200 && triesProp != null && (int)triesProp.GetValue(config) == 5, lowered.status + " " + lowered.value);
var raised = Admin(new() { ["PinGuessesPerDay"] = (int?)21 });
Ok("but not raise it past twenty", raised.status == 400 && (int)(triesProp?.GetValue(config) ?? 0) == 5, raised.status + " " + raised.value);
Ok("nor set it to nothing", Admin(new() { ["PinGuessesPerDay"] = (int?)0 }).status == 400);
Ok("and only an administrator", Admin(new() { ["PinGuessesPerDay"] = (int?)1 }, OLD).status != 200 && (int)(triesProp?.GetValue(config) ?? 0) == 5);

// "Turn on Bonfire" (2.0): admin/settings, administrators only. Refused is 401 or 403
// (401 vs 403 is being settled on another branch); either way nothing changes.
Console.WriteLine();
Console.WriteLine("── Turn on Bonfire ─────────────────────────────────────────────");
var onProp = cfgType.GetProperty("BonfireOn");
bool IsOn() => onProp != null && (bool)onProp.GetValue(config);
bool Refused(int status) => status == 401 || status == 403;
var plugIsOff = pluginType.GetProperty("IsOff");
bool Off() => plugIsOff != null && (bool)plugIsOff.GetValue(null);
onProp?.SetValue(config, false);
var notAdminOn = Admin(new() { ["BonfireOn"] = (bool?)true }, OLD);
Ok("a main account cannot turn it on", Refused(notAdminOn.status) && !IsOn() && Off(), notAdminOn.status + " " + notAdminOn.value);
var keyOn = Admin(new() { ["BonfireOn"] = (bool?)true }, Guid.Empty);
Ok("nor can a call with no user", Refused(keyOn.status) && !IsOn(), keyOn.status + " " + keyOn.value);
var turnOn = Admin(new() { ["BonfireOn"] = (bool?)true });
Ok("an administrator can, and it is on at once", turnOn.status == 200 && IsOn() && !Off(), turnOn.status + " " + turnOn.value);
var notAdminOff = Admin(new() { ["BonfireOn"] = (bool?)false }, OLD);
Ok("a main account cannot turn it off", Refused(notAdminOff.status) && IsOn() && !Off(), notAdminOff.status + " " + notAdminOff.value);
var turnOff = Admin(new() { ["BonfireOn"] = (bool?)false });
Ok("an administrator can, and it is off at once", turnOff.status == 200 && !IsOn() && Off(), turnOff.status + " " + turnOff.value);
var other = Admin(new() { ["PinGuessesPerDay"] = (int?)4 });
Ok("a save that does not name it leaves it alone", other.status == 200 && !IsOn(), other.status + " " + other.value);
onProp?.SetValue(config, true);

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

// ── A modeled Jellyfin ──────────────────────────────────────────────────────

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
