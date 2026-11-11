using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
#pragma warning disable CS0219, CS8321 // the shared world setup has helpers this harness does not use
// ─────────────────────────────────────────────────────────────────────────────
// Addresses an administrator trusts (issue #42): a profile that has "No PIN on the
// server's network" turned on skips its PIN from them too, as it would at home.
//
// Each household still chooses per profile; the administrator only adds addresses that
// count. Never the main account, an administrator, or a switch into another household.
// At most ten entries, none wider than /16 (IPv4) or /48 (IPv6): fixed limits, since a
// looser one lets a stranger past a PIN.
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
var baseType = asm.GetType("Jellyfin.Profiles.Controllers.ProfilesBaseController", true);
var pluginType = asm.GetType("Jellyfin.Profiles.Plugin", true);
var cfgType = asm.GetType("Jellyfin.Profiles.Configuration.PluginConfiguration", true);
var mapType = asm.GetType("Jellyfin.Profiles.Configuration.ProfileMapping", true);
var linkType = asm.GetType("Jellyfin.Profiles.Configuration.LinkedAccount");
var groupType = asm.GetType("Jellyfin.Profiles.Configuration.BonfireGroup", true);
var emailType = asm.GetType("Jellyfin.Profiles.Configuration.AccountEmail", true);
var hasher = asm.GetType("Jellyfin.Profiles.Auth.PinHasher", true);
var rateLimiter = asm.GetType("Jellyfin.Profiles.Controllers.RateLimiter", true);

Ok("this build can add other accounts and make an account a profile", linkType != null
   && ctrlType.GetMethod("AddAccountLink") != null && ctrlType.GetMethod("MakeProfile") != null);
if (linkType == null || ctrlType.GetMethod("AddAccountLink") == null || ctrlType.GetMethod("MakeProfile") == null) return Finish();

// ── The world ────────────────────────────────────────────────────────────────
const string BONFIRE = "Jellyfin.Profiles.Auth.BonfirePinAuthenticationProvider";
const string JELLYFIN = "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider";

var tempDir = Path.Combine(Path.GetTempPath(), "bonfire-links-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
var plugin = Activator.CreateInstance(pluginType, new StubPaths(tempDir), new StubXml());
var config = pluginType.GetProperty("Configuration").GetValue(plugin);
// A server that already has Bonfire; a new install is tested in configbackup.
config.GetType().GetProperty("RequireMainPin")?.SetValue(config, false);
var mappings = (IList)cfgType.GetProperty("Mappings").GetValue(config);
IList Links() => (IList)cfgType.GetProperty("LinkedAccounts").GetValue(config);
bool Linked(Guid household, Guid user) => Links().Cast<object>().Any(l =>
    (Guid)linkType.GetProperty("HouseholdId").GetValue(l) == household && (Guid)linkType.GetProperty("UserId").GetValue(l) == user);
object Row(Guid id) => mappings.Cast<object>().FirstOrDefault(m => (Guid)mapType.GetProperty("ProfileUserId").GetValue(m) == id);
object P(object o, string prop) => o?.GetType().GetProperty(prop, Any)?.GetValue(o);
string Hash(string pin) => (string)hasher.GetMethod("Hash").Invoke(null, new object[] { pin });

void Map(Guid profile, Guid master, string name, string pin = null, bool lanConsent = false)
{
    var m = Activator.CreateInstance(mapType);
    mapType.GetProperty("ProfileUserId").SetValue(m, profile);
    mapType.GetProperty("MasterUserId").SetValue(m, master);
    mapType.GetProperty("ProfileName").SetValue(m, name);
    if (pin != null) mapType.GetProperty("PinHash").SetValue(m, Hash(pin));
    mapType.GetProperty("AllowHouseholdLanBypass").SetValue(m, lanConsent);
    mappings.Add(m);
}
void ResetLimits()
{
    foreach (var f in rateLimiter.GetFields(Any).Where(f => f.IsStatic && f.FieldType == rateLimiter))
    {
        var limiter = f.GetValue(null);
        var store = rateLimiter.GetFields(Any).FirstOrDefault(x => !x.IsStatic && typeof(IDictionary).IsAssignableFrom(x.FieldType));
        (store?.GetValue(limiter) as IDictionary)?.Clear();
    }
}

var world = new World();
var HOME = Guid.NewGuid();    world.Add(HOME, "Parent", "parentpw");
var KID = Guid.NewGuid();     world.Add(KID, "Parent_Kid", null);
var DAD = Guid.NewGuid();     world.Add(DAD, "Dad", "dadpw");                 // no Bonfire row at all
var MUM = Guid.NewGuid();     world.Add(MUM, "Mum", "mumpw");                 // has a PIN
var ADMIN = Guid.NewGuid();   world.Add(ADMIN, "Owner", "ownerpw", admin: true);
var ADMIN2 = Guid.NewGuid();  world.Add(ADMIN2, "Root", "rootpw", admin: true);
var OTHER = Guid.NewGuid();   world.Add(OTHER, "Neighbour", "npw");
var OKID = Guid.NewGuid();    world.Add(OKID, "Neighbour_Kid", null);
var LODGER = Guid.NewGuid();  world.Add(LODGER, "Lodger", "lpw");
Map(HOME, HOME, "Parent", pin: "1234");
Map(KID, HOME, "Kid");
Map(MUM, MUM, "Mum", pin: "4321");
Map(ADMIN2, ADMIN2, "Root", pin: "9999");
Map(OTHER, OTHER, "Neighbour");
Map(OKID, OTHER, "Kid");

var DIRECTORY = Guid.NewGuid(); world.Add(DIRECTORY, "Directory", "dpw");  // signs in through LDAP
typeof(User).GetProperty("AuthenticationProviderId").SetValue(world.Users[DIRECTORY], "Jellyfin.Plugin.LDAP_Auth.LdapAuthenticationProviderPlugin");
var GONE = Guid.NewGuid();    world.Add(GONE, "Gone", "gpw");                 // turned off in Jellyfin
world.Disabled.Add(GONE);
DefaultAuthenticationProvider.W = world;

var users = World.Create(world);
var sessions = SessionStub.Create();
var sessionStub = (SessionStub)(object)sessions;
SessionStub.OnRevoke = id => world.Log.Add("revoke " + id);
World.MappedNow = id => Row(id) is object mr && (Guid)mapType.GetProperty("MasterUserId").GetValue(mr) != id;

(int status, object value) Call(string method, Guid caller, Dictionary<string, object> body = null, string pinHeader = null, string ip = "192.168.1.20",
    string deviceId = null)
{
    var r = ctrlType.GetMethod(method);
    if (r == null) return (-404, "no route " + method);
    var ctor = ctrlType.GetConstructors().First();
    var args = ctor.GetParameters().Select(p =>
        p.ParameterType == typeof(IUserManager) ? users
        : p.ParameterType == typeof(ISessionManager) ? sessions
        : p.ParameterType == typeof(MediaBrowser.Controller.Devices.IDeviceManager) ? DeviceStub.Create()
        : p.ParameterType == typeof(INetworkManager) ? NetworkStub.Create()
        : p.ParameterType == typeof(ILibraryManager) ? Loose<ILibraryManager>.Create()
        : p.ParameterType.Name.StartsWith("ILogger") ? Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(ctrlType))
        : (object)null).ToArray();
    var c = (ControllerBase)ctor.Invoke(args);
    var http = new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(caller == Guid.Empty ? Array.Empty<Claim>()
            : new[] { new Claim("Jellyfin-UserId", caller.ToString()) }, caller == Guid.Empty ? null : "test")),
        RequestServices = new Services(),
    };
    http.Connection.RemoteIpAddress = IPAddress.Parse(ip);
    if (deviceId != null)
        http.Request.Headers["Authorization"] = "MediaBrowser Client=\"Jellyfin Web\", Device=\"Firefox\", DeviceId=\"" + deviceId + "\", Version=\"10.11.0\"";
    if (pinHeader != null) http.Request.Headers["X-Bonfire-Master-Pin"] = pinHeader;
    c.ControllerContext = new ControllerContext { HttpContext = http };

    var invokeArgs = r.GetParameters().Select(p =>
    {
        var req = Activator.CreateInstance(p.ParameterType);
        foreach (var kv in body ?? new Dictionary<string, object>())
        {
            var prop = p.ParameterType.GetProperty(kv.Key) ?? throw new Exception(p.ParameterType.Name + " has no " + kv.Key);
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
// A signed-in caller who is refused gets a message: BonfireErrors sends a 401 with one as 403.
bool Refused((int status, object value) r) => r.status == 403 || (r.status == 401 && r.value is string s && s.Length > 0);
string Text((int status, object value) r) => r.status + " " + r.value;

// ── The rule ─────────────────────────────────────────────────────────────────
Section("Trusted addresses");

const string AWAY = "203.0.113.9";    // NetworkStub: not local
const string HERE = "10.0.0.20";      // NetworkStub: local
const string TRUSTED4 = "198.51.100.7";
const string TRUSTED6 = "2001:db8:abcd:1::5";

Ok("this build keeps a list of trusted addresses", cfgType.GetProperty("TrustedPinAddresses") != null);
Ok("and the settings route takes it", typeof(object).Assembly != null && asm.GetType("Jellyfin.Profiles.Models.AdminSettingsRequest")?.GetProperty("TrustedPinAddresses") != null);
IList Trusted() => (IList)cfgType.GetProperty("TrustedPinAddresses")?.GetValue(config) ?? new List<string>();
void SetTrusted(params string[] a) { Trusted().Clear(); foreach (var x in a) Trusted().Add(x); }
(int status, object value) SaveList(IEnumerable<string> list, Guid? caller = null)
    { try { return Call("UpdateAdminSettings", caller ?? ADMIN, B(("TrustedPinAddresses", list.ToList()))); } catch (Exception e) { return (-500, e.Message); } }

// A household with one profile that skips its PIN at home, one that does not, an
// administrator account that was somehow made a profile, and a main account that skips at home.
var ON = Guid.NewGuid();   world.Add(ON, "Parent_On", null);
var OFF = Guid.NewGuid();  world.Add(OFF, "Parent_Off", null);
Map(ON, HOME, "On", pin: "1111");
Map(OFF, HOME, "Off", pin: "2222");
Map(ADMIN, HOME, "Boss", pin: "3333");
foreach (var id in new[] { ON, ADMIN, HOME, ADMIN2 })
    mapType.GetProperty("BypassPinOnLocalNetwork").SetValue(Row(id), true);

List<object> Tiles(Guid caller, string ip)
{
    var res = Call("GetProfiles", caller, ip: ip);
    return (res.value as IEnumerable)?.Cast<object>().ToList() ?? new List<object>();
}
bool? Asks(Guid caller, Guid profile, string ip)
{
    var t = Tiles(caller, ip).FirstOrDefault(x => (Guid)P(x, "ProfileUserId") == profile);
    return t == null ? (bool?)null : (bool)P(t, "RequiresPin");
}

SetTrusted();
Ok("at home the profile with the skip is not asked", Asks(HOME, ON, HERE) == false);
Ok("away with nothing listed it is asked", Asks(HOME, ON, TRUSTED4) == true);
Ok("an empty list changes nothing for a profile with the skip off", Asks(HOME, OFF, TRUSTED4) == true);

SetTrusted(TRUSTED4);
Ok("a profile with the skip on, from a trusted IPv4 address, is not asked", Asks(HOME, ON, TRUSTED4) == false);
Ok("an untrusted remote address is asked", Asks(HOME, ON, AWAY) == true);
Ok("the next address along is asked", Asks(HOME, ON, "198.51.100.8") == true);
Ok("a profile with the skip off is asked even from a trusted address", Asks(HOME, OFF, TRUSTED4) == true);
Ok("the main account is asked from a trusted address", Asks(HOME, HOME, TRUSTED4) == true);
Ok("an administrator's PIN is asked from a trusted address", Asks(HOME, ADMIN, TRUSTED4) == true);

SetTrusted("2001:db8:abcd::/48");
Ok("from a trusted IPv6 range it is not asked", Asks(HOME, ON, TRUSTED6) == false);
Ok("from outside that range it is asked", Asks(HOME, ON, "2001:db8:abce::5") == true);
SetTrusted("198.51.100.0/24");
Ok("from a trusted IPv4 range it is not asked", Asks(HOME, ON, "198.51.100.200") == false && Asks(HOME, ON, "198.51.101.1") == true);

// Entering one: the same rule through /switch.
SetTrusted(TRUSTED4);
var sw = Call("SwitchProfile", HOME, B(("ProfileId", ON)), ip: TRUSTED4);
Ok("switching to it from a trusted address needs no PIN", sw.status == 200, Text(sw));
sw = Call("SwitchProfile", HOME, B(("ProfileId", ON)), ip: AWAY);
Ok("and from elsewhere it does", sw.status != 200, Text(sw));
sw = Call("SwitchProfile", HOME, B(("ProfileId", OFF)), ip: TRUSTED4);
Ok("a profile with the skip off still needs it", sw.status != 200, Text(sw));
sw = Call("SwitchProfile", HOME, B(("ProfileId", ADMIN)), ip: TRUSTED4);
Ok("an administrator account never skips it", sw.status != 200, Text(sw));

// Across a household: the owner's consent is the only thing that opens it.
var canSkip = baseType.GetMethod("CanSkipPin", Any);
bool Skip(object row, bool local, bool cross, bool lan, bool trusted)
{
    var ps = canSkip.GetParameters();
    var args = new List<object> { row, local, cross, lan };
    if (ps.Length > 4) args.Add(trusted);
    return (bool)canSkip.Invoke(null, args.ToArray());
}
Ok("CanSkipPin takes whether the address is trusted", canSkip.GetParameters().Length == 5);
Ok("a cross-household switch asks, trusted or not", !Skip(Row(ON), false, true, false, true));
Ok("a main account's row asks, trusted or not", !Skip(Row(HOME), false, false, false, true));
Ok("and a profile row skips when trusted", Skip(Row(ON), false, false, false, true));

// Trusted is not local.
SetTrusted(TRUSTED4);
Tiles(OTHER, TRUSTED4);
Ok("a trusted address is not the server's network: the household is not marked seen there",
   !((IList)cfgType.GetProperty("HouseholdsSeenLocally").GetValue(config)).Cast<Guid>().Contains(OTHER));

// Saved sign-in lock: the same skip on the television's own sign-in screen.
var lockType = asm.GetType("Jellyfin.Profiles.Auth.SavedSignInLock", true);
var targets = lockType.GetMethod("Targets");
var devType = asm.GetType("Jellyfin.Profiles.Configuration.KnownDevice", true);
const string TV = "3f9c2a10-5b7e-4d21-9c3a-8e1f0b2d4c6a";
var dev = Activator.CreateInstance(devType);
devType.GetProperty("DeviceId").SetValue(dev, TV);
devType.GetProperty("MasterUserId").SetValue(dev, HOME);
devType.GetProperty("LastSeen").SetValue(dev, DateTime.UtcNow);
((IList)cfgType.GetProperty("KnownDevices").GetValue(config)).Add(dev);
cfgType.GetProperty("EnableClientPinLogin").SetValue(config, true);
int LockedNames(string ip, bool local)
{
    var ps = targets.GetParameters();
    var args = new List<object> { config, TV, local, (Func<Guid, string>)(id => world.Users.TryGetValue(id, out var u) ? u.Username : null) };
    if (ps.Length > 4) args.Add(IPAddress.Parse(ip));
    var res = (IEnumerable)targets.Invoke(null, args.ToArray());
    return res.Cast<object>().Count(t => (Guid)P(t, "UserId") == ON);
}
Ok("the television's sign-in lock takes an address", targets.GetParameters().Length == 5);
Ok("a profile with the skip on is locked from an untrusted address", LockedNames(AWAY, false) == 1);
Ok("and left open from a trusted one", LockedNames(TRUSTED4, false) == 0);

// ── What can be saved ────────────────────────────────────────────────────────
Section("What the administrator may save");
SetTrusted();
var saved = SaveList(new[] { "198.51.100.7", "203.0.113.0/24", "2001:db8:abcd::/48" });
Ok("an address, a range and an IPv6 range are accepted", saved.status == 200 && Trusted().Count == 3, Text(saved));
saved = SaveList(new string[0]);
Ok("an empty list turns it off", saved.status == 200 && Trusted().Count == 0, Text(saved));
SetTrusted("198.51.100.7");
saved = SaveList(Enumerable.Range(1, 10).Select(i => "198.51.100." + i));
Ok("ten entries are accepted", saved.status == 200 && Trusted().Count == 10, Text(saved));
SetTrusted("198.51.100.7");
saved = SaveList(Enumerable.Range(1, 11).Select(i => "198.51.100." + i));
Ok("eleven are refused, and nothing changes", saved.status == 400 && Trusted().Count == 1, Text(saved));
foreach (var bad in new[] { "not an address", "1.2.3", "300.1.1.1", "198.51.100.7/33", "198.51.100.7/", "198.51.100.7/-1", "198.51.100.07", "0x7f.0.0.1", "::ffff:198.51.100.7", "fe80::1%eth0", "0.0.0.0", "::", "198.51.100.7 203.0.113.1" })
{
    SetTrusted("198.51.100.7");
    saved = SaveList(new[] { bad });
    Ok("refused: " + bad, saved.status == 400 && Trusted().Count == 1 && Trusted().Cast<string>().FirstOrDefault() == "198.51.100.7", Text(saved));
}
foreach (var wide in new[] { "0.0.0.0/0", "10.0.0.0/8", "198.51.0.0/15", "::/0", "2001:db8::/32", "2001:db8::/47" })
{
    SetTrusted("198.51.100.7");
    saved = SaveList(new[] { wide });
    Ok("too wide: " + wide, saved.status == 400 && Trusted().Count == 1, Text(saved));
}
SetTrusted();
Ok("the widest allowed are /16 and /48", SaveList(new[] { "198.51.0.0/16", "2001:db8:abcd::/48" }).status == 200);

SetTrusted("198.51.100.7");
var nonAdmin = SaveList(new[] { "203.0.113.1" }, HOME);
Ok("a main account cannot set the list", nonAdmin.status != 200 && Trusted().Count == 1 && Trusted().Cast<string>().FirstOrDefault() == "198.51.100.7", Text(nonAdmin));
nonAdmin = SaveList(new[] { "203.0.113.1" }, KID);
Ok("nor a profile", nonAdmin.status != 200 && Trusted().Cast<string>().FirstOrDefault() == "198.51.100.7", Text(nonAdmin));
nonAdmin = SaveList(new[] { "203.0.113.1" }, Guid.Empty);
Ok("nor somebody signed out", nonAdmin.status != 200 && Trusted().Cast<string>().FirstOrDefault() == "198.51.100.7", Text(nonAdmin));
var infoText = System.Text.Json.JsonSerializer.Serialize(Call("GetInfo", HOME).value);
Ok("and what /info tells a non-administrator does not include it", !infoText.Contains("198.51.100"), infoText);

// ── Who gets counted ─────────────────────────────────────────────────────────
Section("Who counts as trusted");
SetTrusted("198.51.100.0/24");
Ok("an IPv4-mapped IPv6 form of a trusted address counts, as the IPv4 it is", Asks(HOME, ON, "::ffff:198.51.100.7") == false);
Ok("an IPv4-mapped form of an untrusted one does not", Asks(HOME, ON, "::ffff:203.0.113.9") == true);
SetTrusted("2001:db8:abcd::/48");
Ok("an IPv4 address does not match an IPv6 range", Asks(HOME, ON, "198.51.100.7") == true);
SetTrusted("198.51.100.7");
Ok("an IPv6 address does not match an IPv4 entry", Asks(HOME, ON, "2001:db8::c633:6407") == true);
Ok("the IPv4-compatible form does not", Asks(HOME, ON, "::198.51.100.7") == true);
SetTrusted("not an address", "198.51.100.0/8");
Ok("a hand-edited file with bad entries trusts nothing", Asks(HOME, ON, "198.51.100.7") == true && Asks(HOME, ON, "198.99.1.1") == true);

// A header does not move the address: only what Jellyfin resolved counts.
SetTrusted("198.51.100.7");
{
    var r = ctrlType.GetMethod("GetProfiles");
    var ctor = ctrlType.GetConstructors().First();
    var cargs = ctor.GetParameters().Select(p =>
        p.ParameterType == typeof(IUserManager) ? users
        : p.ParameterType == typeof(ISessionManager) ? sessions
        : p.ParameterType == typeof(MediaBrowser.Controller.Devices.IDeviceManager) ? DeviceStub.Create()
        : p.ParameterType == typeof(INetworkManager) ? NetworkStub.Create()
        : p.ParameterType == typeof(ILibraryManager) ? Loose<ILibraryManager>.Create()
        : p.ParameterType.Name.StartsWith("ILogger") ? Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(ctrlType))
        : (object)null).ToArray();
    var c = (ControllerBase)ctor.Invoke(cargs);
    var http = new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", HOME.ToString()) }, "test")),
        RequestServices = new Services(),
    };
    http.Connection.RemoteIpAddress = IPAddress.Parse(AWAY);
    http.Request.Headers["X-Forwarded-For"] = "198.51.100.7";
    http.Request.Headers["X-Real-IP"] = "198.51.100.7";
    http.Request.Headers["Forwarded"] = "for=198.51.100.7";
    c.ControllerContext = new ControllerContext { HttpContext = http };
    var res = r.Invoke(c, new object[0]);
    if (res is IConvertToActionResult conv) res = conv.Convert();
    var rows = ((res as ObjectResult)?.Value as IEnumerable)?.Cast<object>().ToList() ?? new List<object>();
    var tile = rows.FirstOrDefault(x => (Guid)P(x, "ProfileUserId") == ON);
    Ok("forwarding headers on the request do not make a stranger trusted", tile != null && (bool)P(tile, "RequiresPin"));
}

// A different household's own setting still decides.
Ok("the household's choice is the other half: skip off stays asked from a trusted address", Asks(HOME, OFF, "198.51.100.7") == true);

return Finish();

// ── A modelled Jellyfin ────────────────────────────────────────────────────

public class World
{
    public Dictionary<Guid, User> Users = new();
    public HashSet<Guid> Admins = new();
    public Dictionary<Guid, string> Passwords = new();
    public Dictionary<Guid, UserPolicy> Policies = new();
    public List<string> Log = new();
    public List<string> AuthCalls = new();
    public List<string> ProviderCalls = new();
    public HashSet<Guid> Disabled = new();
    public Dictionary<Guid, int> LockoutCount = new();
    public int Sessions;
    public bool FailPassword;
    public static Func<Guid, bool> MappedNow = _ => false;

    public void Add(Guid id, string name, string password, bool admin = false)
    {
        var u = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty("Username").SetValue(u, name);
        typeof(User).GetProperty("Id").SetValue(u, id);
        typeof(User).GetProperty("AuthenticationProviderId").SetValue(u, "Jellyfin.Profiles.Auth.BonfirePinAuthenticationProvider");
        Users[id] = u;
        if (password != null) Passwords[id] = password;
        if (admin) Admins.Add(id);
        Policies[id] = new UserPolicy { EnableAllFolders = true, AuthenticationProviderId = u.AuthenticationProviderId, PasswordResetProviderId = "x" };
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

    private UserPolicy Copy(Guid id)
    {
        var p = W.Policies[id];
        return new UserPolicy
        {
            IsAdministrator = W.Admins.Contains(id), IsDisabled = W.Disabled.Contains(id), IsHidden = p.IsHidden, EnableAllFolders = p.EnableAllFolders,
            EnabledFolders = p.EnabledFolders, BlockedMediaFolders = p.BlockedMediaFolders, BlockedTags = p.BlockedTags,
            AllowedTags = p.AllowedTags, AuthenticationProviderId = W.Users[id].AuthenticationProviderId, PasswordResetProviderId = "x",
            MaxParentalRating = p.MaxParentalRating,
        };
    }

    protected override object Invoke(MethodInfo m, object[] args)
    {
        switch (m.Name)
        {
            case "GetUserById":
                return W.Users.TryGetValue((Guid)args[0], out var u) ? u : null;
            case "GetUserByName":
                return W.Users.Values.FirstOrDefault(x => string.Equals(x.Username, (string)args[0], StringComparison.OrdinalIgnoreCase));
            case "get_Users":
            case "GetUsers":
                return W.Users.Values.ToList();
            case "GetUserDto":
                var user = (User)args[0];
                return new UserDto { Id = user.Id, Name = user.Username, Policy = Copy(user.Id), Configuration = new UserConfiguration() };
            case "AuthenticateUser":
                var name = (string)args[0];
                W.AuthCalls.Add(name + " session=" + args[3]);
                var found = W.Users.Values.FirstOrDefault(x => string.Equals(x.Username, name, StringComparison.OrdinalIgnoreCase));
                if (found == null || !W.Passwords.TryGetValue(found.Id, out var pw) || pw != (string)args[1])
                {
                    // What Jellyfin does here: IncrementInvalidLoginAttemptCount, and with a
                    // lockout set, the account is turned off at the limit.
                    if (found != null) W.LockoutCount[found.Id] = W.LockoutCount.GetValueOrDefault(found.Id) + 1;
                    return Task.FromException<User>(new System.Security.Authentication.AuthenticationException("Invalid username or password entered."));
                }
                if ((bool)args[3]) W.Sessions++;
                return Task.FromResult(found);
            case "UpdatePolicyAsync":
                var id = (Guid)args[0];
                W.Log.Add("policy " + id + " mapped=" + World.MappedNow(id));
                var policy = (UserPolicy)args[1];
                W.Policies[id] = policy;
                if (W.Users.TryGetValue(id, out var pu) && !string.IsNullOrEmpty(policy.AuthenticationProviderId))
                    typeof(User).GetProperty("AuthenticationProviderId").SetValue(pu, policy.AuthenticationProviderId);
                return Task.CompletedTask;
            case "UpdateUserAsync":
                var changed = (User)args[0];
                W.Log.Add("provider " + changed.Id + " " + changed.AuthenticationProviderId);
                return Task.CompletedTask;
            case "DeleteUserAsync":
                // What takes the account and its watch history with it.
                W.Log.Add("delete " + (Guid)args[0]);
                W.Users.Remove((Guid)args[0]);
                return Task.CompletedTask;
            case "UpdateConfigurationAsync":
                return Task.CompletedTask;
            case "ChangePassword":
                var pid = args[0] is User cu ? cu.Id : (Guid)args[0];
                if (W.FailPassword) return Task.FromException(new InvalidOperationException("the provider refused the password"));
                W.Passwords[pid] = (string)args[1];
                W.Log.Add("password " + pid + " mapped=" + World.MappedNow(pid) + " provider=" + W.Users[pid].AuthenticationProviderId);
                return Task.CompletedTask;
        }
        return Loose.Default(m.ReturnType);
    }
}

// Jellyfin's two registered providers, as the plugin finds them in the request's services.
// The real DefaultAuthenticationProvider compares the hash and nothing else; the lockout
// count lives in UserManager, which is what the add route must not go through.
public sealed class DefaultAuthenticationProvider : MediaBrowser.Controller.Authentication.IAuthenticationProvider,
    MediaBrowser.Controller.Authentication.IRequiresResolvedUser
{
    public static World W;
    public string Name => "Default";
    public bool IsEnabled => true;
    public Task<MediaBrowser.Controller.Authentication.ProviderAuthenticationResult> Authenticate(string username, string password)
        => throw new InvalidOperationException("the plugin must hand over the resolved user");
    public Task<MediaBrowser.Controller.Authentication.ProviderAuthenticationResult> Authenticate(string username, string password, User resolvedUser)
    {
        W.ProviderCalls.Add(username);
        if (resolvedUser == null || !W.Passwords.TryGetValue(resolvedUser.Id, out var pw) || pw != password)
            throw new MediaBrowser.Controller.Authentication.AuthenticationException("Invalid username or password");
        return Task.FromResult(new MediaBrowser.Controller.Authentication.ProviderAuthenticationResult { Username = username });
    }
    public bool HasPassword(User user) => true;
    public Task ChangePassword(User user, string newPassword) => Task.CompletedTask;
}

public sealed class InvalidAuthProvider : MediaBrowser.Controller.Authentication.IAuthenticationProvider
{
    public string Name => "InvalidOrMissingAuthenticationProvider";
    public bool IsEnabled => false;
    public Task<MediaBrowser.Controller.Authentication.ProviderAuthenticationResult> Authenticate(string username, string password)
        => throw new MediaBrowser.Controller.Authentication.AuthenticationException("refused");
    public bool HasPassword(User user) => true;
    public Task ChangePassword(User user, string newPassword) => Task.CompletedTask;
}

public sealed class Services : IServiceProvider
{
    public object GetService(Type t) => t == typeof(IEnumerable<MediaBrowser.Controller.Authentication.IAuthenticationProvider>)
        ? new MediaBrowser.Controller.Authentication.IAuthenticationProvider[] { new InvalidAuthProvider(), new DefaultAuthenticationProvider() }
        : null;
}

public static class Loose
{
    public static object Default(Type t)
    {
        if (t == typeof(void)) return null;
        if (t == typeof(Task)) return Task.CompletedTask;
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

public class Loose<T> : DispatchProxy where T : class
{
    public static T Create() => DispatchProxy.Create<T, Loose<T>>();
    protected override object Invoke(MethodInfo m, object[] args) => Loose.Default(m.ReturnType);
}

public class SessionStub : DispatchProxy
{
    public List<Guid> Opened = new();
    public List<Guid> Revoked = new();
    public static Action<Guid> OnRevoke = _ => { };
    public static ISessionManager Create() => Create<ISessionManager, SessionStub>();

    protected override object Invoke(MethodInfo m, object[] args)
    {
        switch (m.Name)
        {
            case "get_Sessions":
                return new List<SessionInfo>();
            case "AuthenticateDirect":
                var req = (MediaBrowser.Controller.Session.AuthenticationRequest)args[0];
                Opened.Add(req.UserId);
                return Task.FromResult(new MediaBrowser.Controller.Authentication.AuthenticationResult { AccessToken = "token-" + req.UserId.ToString("N") });
            case "RevokeUserTokens":
                Revoked.Add((Guid)args[0]);
                OnRevoke((Guid)args[0]);
                return Task.CompletedTask;
            case "Logout" when args.Length == 1 && args[0] is Jellyfin.Database.Implementations.Entities.Security.Device d:
                SignedOut.Add((d.UserId, d.DeviceId));
                return Task.CompletedTask;
        }
        return Loose.Default(m.ReturnType);
    }

    /// <summary>(account, device) pairs signed out one device at a time.</summary>
    public static List<(Guid user, string device)> SignedOut = new();
}

/// <summary>Jellyfin's device rows: every (account, device) pair asked about has one.</summary>
public class DeviceStub : DispatchProxy
{
    public static MediaBrowser.Controller.Devices.IDeviceManager Create()
        => Create<MediaBrowser.Controller.Devices.IDeviceManager, DeviceStub>();

    protected override object Invoke(MethodInfo m, object[] args)
    {
        if (m.Name == "GetDevices" && args[0] is Jellyfin.Data.Queries.DeviceQuery q && q.UserId is Guid u && !string.IsNullOrEmpty(q.DeviceId))
        {
            var d = new Jellyfin.Database.Implementations.Entities.Security.Device(u, "Jellyfin Web", "10.11.0", "Firefox", q.DeviceId);
            return new MediaBrowser.Model.Querying.QueryResult<Jellyfin.Database.Implementations.Entities.Security.Device>(
                new List<Jellyfin.Database.Implementations.Entities.Security.Device> { d });
        }
        return Loose.Default(m.ReturnType);
    }
}

public class NetworkStub : DispatchProxy
{
    public static INetworkManager Create() => Create<INetworkManager, NetworkStub>();
    // 10.0.0.x is "the server's network"; anything else is away.
    protected override object Invoke(MethodInfo m, object[] args)
        => m.Name == "IsInLocalNetwork" ? args[0].ToString().StartsWith("10.0.0.") : Loose.Default(m.ReturnType);
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
