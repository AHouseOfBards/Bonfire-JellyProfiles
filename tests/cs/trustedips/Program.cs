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
// Addresses an administrator trusts (issue #42): each entry is an address or range and
// the main accounts it is for. From it, those households open their main account and
// their profiles without a PIN, whatever the household's own settings say.
//
// Never an administrator's account, a switch into another household, an account no entry
// names, or the main PIN for changing household settings. At most ten entries, none wider
// than /16 (IPv4) or /48 (IPv6): fixed limits, since a looser one lets a stranger past a PIN.
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

var entryType = asm.GetType("Jellyfin.Profiles.Configuration.TrustedAddressEntry");
var entriesProp = cfgType.GetProperty("TrustedAddressEntries");
Ok("this build keeps entries of an address and the accounts it is for",
   entryType != null && entriesProp != null && entryType.GetProperty("Address") != null && entryType.GetProperty("UserIds") != null);
Ok("and the settings route takes them", asm.GetType("Jellyfin.Profiles.Models.AdminSettingsRequest")?.GetProperty("TrustedAddressEntries") != null);
IList Entries() => entriesProp == null ? new ArrayList() : (IList)entriesProp.GetValue(config);
object MakeEntry(string address, Guid[] ids)
{
    var e = Activator.CreateInstance(entryType);
    entryType.GetProperty("Address").SetValue(e, address);
    var l = (IList)entryType.GetProperty("UserIds").GetValue(e);
    foreach (var id in ids) l.Add(id);
    return e;
}
// Written straight into the configuration, as a hand-edited file or a restore would: no validation.
void TrustNone() { Entries().Clear(); }
void AddEntry(string address, params Guid[] ids) { if (entryType != null) Entries().Add(MakeEntry(address, ids)); }
void Trust(string address, params Guid[] ids) { TrustNone(); AddEntry(address, ids); }
int EntryCount() => Entries().Count;
string FirstAddress() => Entries().Count == 0 ? null : (string)entryType.GetProperty("Address").GetValue(Entries()[0]);
// Through the settings route, as the dashboard does.
(int status, object value) Save(Guid? caller, params (string address, Guid[] ids)[] list)
{
    try
    {
        var typed = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(entryType));
        foreach (var (a, ids) in list) typed.Add(MakeEntry(a, ids));
        return Call("UpdateAdminSettings", caller ?? ADMIN, B(("TrustedAddressEntries", typed)));
    }
    catch (Exception e) { return (-500, e.Message); }
}

// Household HOME: a main account that asks, a profile that skips at home, one that does not,
// and an administrator account that was somehow made a profile. Household OTHER: a main
// account and a profile that skips at home. Household ADMIN2: an administrator's own.
var ON = Guid.NewGuid();   world.Add(ON, "Parent_On", null);
var OFF = Guid.NewGuid();  world.Add(OFF, "Parent_Off", null);
var AKID = Guid.NewGuid(); world.Add(AKID, "Root_Kid", null);
Map(ON, HOME, "On", pin: "1111");
Map(OFF, HOME, "Off", pin: "2222");
Map(ADMIN, HOME, "Boss", pin: "3333");
Map(AKID, ADMIN2, "Rootkid", pin: "7777");
mapType.GetProperty("PinHash").SetValue(Row(OTHER), Hash("5555"));
mapType.GetProperty("PinHash").SetValue(Row(OKID), Hash("6666"));
foreach (var id in new[] { ON, ADMIN, OKID, AKID })
    mapType.GetProperty("BypassPinOnLocalNetwork").SetValue(Row(id), true);
foreach (var id in new[] { HOME, OTHER, ADMIN2 })
{
    mapType.GetProperty("AskPinToOpen").SetValue(Row(id), true);
    mapType.GetProperty("BypassPinOnLocalNetwork").SetValue(Row(id), false);
}

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
bool Opens((int status, object value) r) => r.status == 200;

TrustNone();
Ok("nothing listed: a main account asks", Asks(HOME, HOME, TRUSTED4) == true);
Ok("nothing listed: a profile that skips at home asks away", Asks(HOME, ON, TRUSTED4) == true);
Ok("at home the profile with the skip is not asked, as before", Asks(HOME, ON, HERE) == false);

// 1. A named household, from its address.
Trust(TRUSTED4, HOME);
Ok("a named household's main account from its address is not asked", Asks(HOME, HOME, TRUSTED4) == false);
Ok("its profile is not asked", Asks(HOME, ON, TRUSTED4) == false);
Ok("its profile with 'No PIN on the server's network' off is not asked either", Asks(HOME, OFF, TRUSTED4) == false);
Ok("entering its profile from there needs no PIN", Opens(Call("SwitchProfile", HOME, B(("ProfileId", OFF)), ip: TRUSTED4)));
Ok("entering its main account from a profile there needs no PIN", Opens(Call("SwitchProfile", KID, B(("ProfileId", HOME)), ip: TRUSTED4)));
Ok("the PIN check route agrees", Opens(Call("VerifyPin", HOME, B(("ProfileId", OFF)), ip: TRUSTED4)));

// 2. An unnamed household from the same address.
Ok("an unnamed household's main account from the same address is asked", Asks(OTHER, OTHER, TRUSTED4) == true);
Ok("and its profile, even with the skip on", Asks(OTHER, OKID, TRUSTED4) == true);
Ok("entering it needs the PIN", !Opens(Call("SwitchProfile", OTHER, B(("ProfileId", OKID)), ip: TRUSTED4)));

// 3. The same household from another address.
Ok("the named household's main account from elsewhere is asked", Asks(HOME, HOME, AWAY) == true);
Ok("its profile from elsewhere is asked", Asks(HOME, OFF, AWAY) == true);
Ok("the next address along is asked", Asks(HOME, OFF, "198.51.100.8") == true);
Ok("entering from elsewhere needs the PIN", !Opens(Call("SwitchProfile", HOME, B(("ProfileId", OFF)), ip: AWAY)));

// 4. Changing the household's settings still takes the main PIN.
var mgmt = Call("AddAccountLink", HOME, B(("Username", "Someone"), ("Password", "pw")), ip: TRUSTED4);
Ok("from its address, a household setting still asks for the main PIN", mgmt.status == 400 && Convert.ToString(mgmt.value).Contains("PIN"), Text(mgmt));
mgmt = Call("AddAccountLink", HOME, B(("Username", "Someone"), ("Password", "pw")), pinHeader: "0000", ip: TRUSTED4);
Ok("and a wrong main PIN is refused", mgmt.status != 200, Text(mgmt));

// 5. An administrator always asks, at every call site. The entries are written by hand
// (a restored file, or rights granted after the entry was saved): the save route refuses them.
TrustNone();
AddEntry(TRUSTED4, HOME, ADMIN2);
var proxyType = asm.GetType("Jellyfin.Profiles.Auth.ProxySwitcher", true);
var needsPin = proxyType.GetMethod("NeedsPin", Any);
var svcType = proxyType.GetNestedType("Services", Any);
var sessType = proxyType.GetNestedType("ProxySession", Any);
var svc = Activator.CreateInstance(svcType, Any, null, new object[] { new UsersServices(users) }, null);
bool NeedsPin(Guid master, Guid profile, string ip)
{
    var s = Activator.CreateInstance(sessType, true);
    sessType.GetField("MasterId").SetValue(s, master);
    var http = new DefaultHttpContext { RequestServices = new Services() };
    http.Connection.RemoteIpAddress = IPAddress.Parse(ip);
    return (bool)needsPin.Invoke(null, new object[] { http, s, profile, config, svc });
}
Ok("(control) the picker lets the named household's profile in from its address", NeedsPin(HOME, OFF, TRUSTED4) == false);
Ok("(control) and its main account", NeedsPin(HOME, HOME, TRUSTED4) == false);
Ok("web switcher tile: an administrator's profile in a named household asks", Asks(HOME, ADMIN, TRUSTED4) == true);
Ok("web switcher /switch: it asks", !Opens(Call("SwitchProfile", HOME, B(("ProfileId", ADMIN)), ip: TRUSTED4)));
Ok("PIN check route: it asks", !Opens(Call("VerifyPin", HOME, B(("ProfileId", ADMIN)), ip: TRUSTED4)));
Ok("web switcher tile: an administrator's own main account asks", Asks(ADMIN2, ADMIN2, TRUSTED4) == true);
Ok("and a profile in an administrator's household asks", Asks(ADMIN2, AKID, TRUSTED4) == true);
Ok("the picker: an administrator's profile asks", NeedsPin(HOME, ADMIN, TRUSTED4) == true);
Ok("the picker: an administrator's main account asks", NeedsPin(ADMIN2, ADMIN2, TRUSTED4) == true);
Ok("the picker: a profile in an administrator's household asks", NeedsPin(ADMIN2, AKID, TRUSTED4) == true);

// 6. Switching into another household asks.
var canSkip = baseType.GetMethod("CanSkipPin", Any);
bool Skip(object row, bool local, bool cross, bool lan, bool trusted)
{
    var ps = canSkip.GetParameters();
    var args = new List<object> { row, local, cross, lan };
    if (ps.Length > 4) args.Add(trusted);
    return (bool)canSkip.Invoke(null, args.ToArray());
}
Ok("CanSkipPin takes whether the address is trusted for this household", canSkip.GetParameters().Length == 5);
Ok("a cross-household switch asks, trusted or not", !Skip(Row(OTHER), false, true, false, true));
Ok("a trusted profile row skips", Skip(Row(OFF), false, false, false, true));
Ok("a trusted main row skips, whatever its own settings", Skip(Row(HOME), false, false, false, true));
TrustNone();
AddEntry("198.51.100.0/24", HOME, OTHER);
{
    var link = Activator.CreateInstance(linkType);
    linkType.GetProperty("HouseholdId").SetValue(link, HOME);
    linkType.GetProperty("UserId").SetValue(link, OTHER);
    Links().Add(link);
    var cross = Call("SwitchProfile", HOME, B(("ProfileId", OTHER)), ip: TRUSTED4);
    Ok("from an address that names both, switching into the other household's account still asks", !Opens(cross), Text(cross));
    var crossTiles = Tiles(HOME, TRUSTED4).Where(x => (Guid)P(x, "ProfileUserId") == OTHER).ToList();
    Ok("and its tile says so", crossTiles.Count == 0 || crossTiles.All(x => (bool)P(x, "RequiresPin")));
    Links().Remove(link);
}

// A trusted address is not the server's network.
Trust(TRUSTED4, HOME, OTHER);
Tiles(OTHER, TRUSTED4);
Ok("a trusted address is not local: the household is not marked seen there",
   !((IList)cfgType.GetProperty("HouseholdsSeenLocally").GetValue(config)).Cast<Guid>().Contains(OTHER));

// 7. Saved sign-in lock: the same rule on the television's own sign-in screen.
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
bool Locked(Guid who, string ip, bool local = false, bool withAdminCheck = true)
{
    var ps = targets.GetParameters();
    var args = new List<object> { config, TV, local, (Func<Guid, string>)(id => world.Users.TryGetValue(id, out var u) ? u.Username : null) };
    if (ps.Length > 4) args.Add(IPAddress.Parse(ip));
    if (ps.Length > 5) args.Add(withAdminCheck ? (Func<Guid, bool>)(id => world.Admins.Contains(id)) : null);
    var res = (IEnumerable)targets.Invoke(null, args.ToArray());
    return res.Cast<object>().Any(t => (Guid)P(t, "UserId") == who);
}
Ok("the television's sign-in lock takes an address and a way to tell administrators", targets.GetParameters().Length == 6);
Trust(TRUSTED4, HOME);
Ok("lock: the named household's profile is left open from its address, with the skip off", !Locked(OFF, TRUSTED4));
Ok("lock: and with the skip on", !Locked(ON, TRUSTED4));
Ok("lock: and its main account", !Locked(HOME, TRUSTED4));
Ok("lock: from elsewhere they are locked", Locked(OFF, AWAY) && Locked(ON, AWAY) && Locked(HOME, AWAY));
Ok("lock: at home, a profile with the skip on is open and one without is locked", !Locked(ON, AWAY, local: true) && Locked(OFF, AWAY, local: true));
Ok("lock: an administrator's profile in the named household stays locked", Locked(ADMIN, TRUSTED4));
Ok("lock: nobody is trusted when administrators cannot be told apart", Locked(OFF, TRUSTED4, withAdminCheck: false));
Trust(TRUSTED4, OTHER);
Ok("lock: a household the address does not name stays locked", Locked(OFF, TRUSTED4));

// 8. What can be saved.
Section("What the administrator may save");
TrustNone();
var saved = Save(null, ("198.51.100.7", new[] { HOME }), ("203.0.113.0/24", new[] { HOME, OTHER }), ("2001:db8:abcd::/48", new[] { MUM }));
Ok("an address, a range and an IPv6 range, each with its accounts, are accepted", saved.status == 200 && EntryCount() == 3, Text(saved));
saved = Save(null);
Ok("an empty list turns it off", saved.status == 200 && EntryCount() == 0, Text(saved));
Trust("198.51.100.7", HOME);
saved = Save(null, Enumerable.Range(1, 10).Select(i => ("198.51.100." + i, new[] { HOME })).ToArray());
Ok("ten entries are accepted", saved.status == 200 && EntryCount() == 10, Text(saved));
Trust("198.51.100.7", HOME);
saved = Save(null, Enumerable.Range(1, 11).Select(i => ("198.51.100." + i, new[] { HOME })).ToArray());
Ok("eleven are refused, and nothing changes", saved.status == 400 && EntryCount() == 1 && FirstAddress() == "198.51.100.7", Text(saved));

bool Kept() => EntryCount() == 1 && FirstAddress() == "198.51.100.7";
foreach (var bad in new[] { "not an address", "1.2.3", "300.1.1.1", "198.51.100.7/33", "198.51.100.7/", "198.51.100.7/-1", "198.51.100.07", "0x7f.0.0.1", "::ffff:198.51.100.7", "fe80::1%eth0", "0.0.0.0", "::", "198.51.100.7 203.0.113.1", "" })
{
    Trust("198.51.100.7", HOME);
    saved = Save(null, (bad, new[] { HOME }));
    Ok("refused: '" + bad + "'", saved.status == 400 && Kept(), Text(saved));
}
foreach (var wide in new[] { "0.0.0.0/0", "10.0.0.0/8", "198.51.0.0/15", "::/0", "2001:db8::/32", "2001:db8::/47" })
{
    Trust("198.51.100.7", HOME);
    saved = Save(null, (wide, new[] { HOME }));
    Ok("too wide: " + wide, saved.status == 400 && Kept(), Text(saved));
}
TrustNone();
Ok("the widest allowed are /16 and /48", Save(null, ("198.51.0.0/16", new[] { HOME }), ("2001:db8:abcd::/48", new[] { HOME })).status == 200);

Trust("198.51.100.7", HOME);
saved = Save(null, ("203.0.113.1", new[] { Guid.NewGuid() }));
Ok("an id that is no account is refused", saved.status == 400 && Kept(), Text(saved));
saved = Save(null, ("203.0.113.1", new[] { KID }));
Ok("a profile's id is refused: it is not a main account", saved.status == 400 && Kept(), Text(saved));
saved = Save(null, ("203.0.113.1", new[] { ADMIN2 }));
Ok("an administrator's id is refused", saved.status == 400 && Kept(), Text(saved));
saved = Save(null, ("203.0.113.1", new[] { HOME, ADMIN2 }));
Ok("an administrator among others refuses the whole list", saved.status == 400 && Kept(), Text(saved));
saved = Save(null, ("203.0.113.1", new Guid[0]));
Ok("an address for nobody is refused", saved.status == 400 && Kept(), Text(saved));
saved = Save(null, ("203.0.113.1", new[] { Guid.Empty }));
Ok("an empty id is refused", saved.status == 400 && Kept(), Text(saved));
saved = Save(null, ("203.0.113.1", new[] { HOME }), ("203.0.113.1", new[] { OTHER }));
Ok("the same address twice is refused", saved.status == 400 && Kept(), Text(saved));
saved = Save(null, ("203.0.113.1/24", new[] { HOME }), ("203.0.113.0/24", new[] { OTHER }));
Ok("and so is the same range written two ways", saved.status == 400 && Kept(), Text(saved));
saved = Save(null, ("203.0.113.1", new[] { HOME }), ("not an address", new[] { HOME }));
Ok("one bad entry among good ones changes nothing", saved.status == 400 && Kept(), Text(saved));

Trust("198.51.100.7", HOME);
var nonAdmin = Save(HOME, ("203.0.113.1", new[] { HOME }));
Ok("a main account cannot set entries, not even its own", nonAdmin.status != 200 && Kept(), Text(nonAdmin));
nonAdmin = Save(KID, ("203.0.113.1", new[] { HOME }));
Ok("nor a profile", nonAdmin.status != 200 && Kept(), Text(nonAdmin));
nonAdmin = Save(Guid.Empty, ("203.0.113.1", new[] { HOME }));
Ok("nor somebody signed out", nonAdmin.status != 200 && Kept(), Text(nonAdmin));
nonAdmin = Save(HOME);
Ok("nor can one clear them", nonAdmin.status != 200 && Kept(), Text(nonAdmin));
foreach (var who in new[] { HOME, KID, OTHER })
{
    var infoText = System.Text.Json.JsonSerializer.Serialize(Call("GetInfo", who).value);
    Ok("what /info tells " + (who == HOME ? "a main account" : who == KID ? "a profile" : "another household") + " does not include them", !infoText.Contains("198.51.100") && !infoText.Contains("rustedAddress"), infoText);
}
if (entryType == null) Ok("an administrator's save is read back the way it was sent", false);
else
{
    var back = Save(ADMIN, ("203.0.113.0/24", new[] { HOME }));
    Ok("an administrator's save is read back the way it was sent", back.status == 200 && FirstAddress() == "203.0.113.0/24"
       && ((IList)entryType.GetProperty("UserIds").GetValue(Entries()[0])).Cast<Guid>().SequenceEqual(new[] { HOME }));
}

// A restored file keeps only what is valid and within the limits.
if (entryType == null) Ok("a restore keeps ten valid entries, once each, and drops the rest", false);
else
{
    var normalize = asm.GetType("Jellyfin.Profiles.Auth.TrustedAddresses", true).GetMethod("Normalize");
    var typed = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(entryType));
    typed.Add(MakeEntry("198.51.100.07", new[] { HOME }));          // bad
    typed.Add(MakeEntry("10.0.0.0/8", new[] { HOME }));             // too wide
    typed.Add(MakeEntry("198.51.100.0/24", new Guid[0]));           // nobody
    typed.Add(MakeEntry("198.51.100.77", new[] { HOME, HOME, Guid.Empty }));
    typed.Add(MakeEntry("198.51.100.77", new[] { OTHER }));         // twice
    for (int i = 1; i <= 12; i++) typed.Add(MakeEntry("203.0.113." + i, new[] { HOME }));
    var kept = ((IEnumerable)normalize.Invoke(null, new object[] { typed })).Cast<object>().ToList();
    Ok("a restore keeps ten valid entries, once each, and drops the rest", kept.Count == 10
       && (string)P(kept[0], "Address") == "198.51.100.77" && ((IList)P(kept[0], "UserIds")).Count == 1);
}

// ── Red team: who counts as the address ──────────────────────────────────────
Section("Who counts as trusted");
Trust("198.51.100.0/24", HOME);
Ok("an IPv4-mapped IPv6 form of a trusted address counts, as the IPv4 it is", Asks(HOME, OFF, "::ffff:198.51.100.7") == false);
Ok("an IPv4-mapped form of an untrusted one does not", Asks(HOME, OFF, "::ffff:203.0.113.9") == true);
Trust("2001:db8:abcd::/48", HOME);
Ok("from a trusted IPv6 range it is not asked", Asks(HOME, OFF, TRUSTED6) == false);
Ok("from outside that range it is asked", Asks(HOME, OFF, "2001:db8:abce::5") == true);
Ok("an IPv4 address does not match an IPv6 range", Asks(HOME, OFF, "198.51.100.7") == true);
Trust("198.51.100.0/24", HOME);
Ok("from a trusted IPv4 range it is not asked", Asks(HOME, OFF, "198.51.100.200") == false && Asks(HOME, OFF, "198.51.101.1") == true);
Trust("198.51.100.7", HOME);
Ok("an IPv6 address does not match an IPv4 entry", Asks(HOME, OFF, "2001:db8::c633:6407") == true);
Ok("the IPv4-compatible form does not", Asks(HOME, OFF, "::198.51.100.7") == true);
TrustNone();
AddEntry("not an address", HOME);
AddEntry("198.51.100.0/8", HOME);
AddEntry("198.51.100.7", Guid.Empty);
Ok("a hand-edited file with bad entries trusts nothing", Asks(HOME, OFF, "198.51.100.7") == true && Asks(HOME, OFF, "198.99.1.1") == true);
TrustNone();
for (int i = 1; i <= 15; i++) AddEntry("203.0.113." + i, HOME);
AddEntry("198.51.100.7", HOME);
Ok("a hand-edited file past ten entries does not read the eleventh", Asks(HOME, OFF, "198.51.100.7") == true);

// A header does not move the address: only what Jellyfin resolved counts.
Trust("198.51.100.7", HOME);
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
    var tile = rows.FirstOrDefault(x => (Guid)P(x, "ProfileUserId") == OFF);
    var mainTile = rows.FirstOrDefault(x => (Guid)P(x, "ProfileUserId") == HOME);
    Ok("forwarding headers on the request do not make a stranger trusted",
       tile != null && (bool)P(tile, "RequiresPin") && mainTile != null && (bool)P(mainTile, "RequiresPin"));
}

return Finish();

// ── A modelled Jellyfin ────────────────────────────────────────────────────

// What the picker finds in the request's services: the user manager and the network.
public sealed class UsersServices : IServiceProvider
{
    private readonly IUserManager _users;
    public UsersServices(IUserManager users) { _users = users; }
    public object GetService(Type t) => t == typeof(IUserManager) ? _users
        : t == typeof(INetworkManager) ? NetworkStub.Create() : null;
}

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
