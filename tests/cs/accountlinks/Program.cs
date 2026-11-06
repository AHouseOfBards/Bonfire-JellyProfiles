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

// ─────────────────────────────────────────────────────────────────────────────
// Other accounts in a household's switcher (issue #39), and an account made a profile
// (1.7.2).
//
// What it holds the plugin to:
//
//  - The rule, through the one function /switch, /verify-pin and /list share: an added
//    account with no PIN opens with nothing (its password was the consent); one with a PIN
//    asks for it, or not at home if its owner allows that for Bonfires; an administrator
//    always asks, and is refused outright without a PIN. A stale link to what is now a
//    profile opens nothing.
//  - Adding: the main account only, with its main PIN, and the account's real password,
//    rate limited. Never your own account, a profile, twice, or an administrator with no
//    PIN. Either side can remove it; nobody else can.
//  - /list draws the account, marked as added, and not its profiles; /switch opens it.
//  - Making an account a profile: administrators only; never an administrator, an account
//    with profiles, one in a Bonfire, or past the household's limit. It is signed out,
//    hidden, put on Jellyfin's own provider, given a password nobody knows while it is
//    still an account, and only then mapped; it keeps its PIN, libraries and tags, loses
//    its main-account row, email and links, and the household's restrictions go on top.
//
// Against 77b0d04 the first assertion fails: there is no such route. Against 74aa924 the
// lockout checks fail: it checked the password through UserManager.AuthenticateUser.
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
Section("The rule");

var evaluate = baseType.GetMethod("EvaluateSwitch", Any);
var rowList = typeof(List<>).MakeGenericType(mapType);
string Outcome(Guid target, bool local, Dictionary<Guid, bool> added)
{
    var rows = (IList)Activator.CreateInstance(rowList);
    foreach (var m in mappings) rows.Add(m);
    var r = evaluate.Invoke(null, new object[] { rows, HOME, target, new HashSet<Guid> { HOME }, local, added });
    if ((bool)P(r, "Unauthorized")) return "unauthorized";
    if (P(r, "Refusal") != null) return "refused";
    var row = P(r, "Target");
    var hasPin = !string.IsNullOrEmpty((string)P(row, "PinHash"));
    return !hasPin ? "open" : (bool)P(r, "HouseholdLanBypass") ? "open at home" : "PIN";
}

var addedAll = new Dictionary<Guid, bool> { [DAD] = false, [MUM] = false, [ADMIN] = true, [ADMIN2] = true, [KID] = false };
Ok("an added account with no PIN opens with nothing", Outcome(DAD, false, addedAll) == "open");
Ok("one with a PIN asks for it", Outcome(MUM, false, addedAll) == "PIN");
mapType.GetProperty("AllowHouseholdLanBypass").SetValue(Row(MUM), true);
Ok("at home, its owner's Bonfire consent lets it open without", Outcome(MUM, true, addedAll) == "open at home");
Ok("but not away", Outcome(MUM, false, addedAll) == "PIN");
mapType.GetProperty("AllowHouseholdLanBypass").SetValue(Row(MUM), false);
mapType.GetProperty("AllowHouseholdLanBypass").SetValue(Row(ADMIN2), true);
Ok("an administrator asks for its PIN even at home with that consent", Outcome(ADMIN2, true, addedAll) == "PIN");
mapType.GetProperty("AllowHouseholdLanBypass").SetValue(Row(ADMIN2), false);
Ok("an administrator with no PIN is refused", Outcome(ADMIN, true, addedAll) == "refused");
Ok("a link to what is now a profile opens nothing", Outcome(OKID, true, new Dictionary<Guid, bool> { [OKID] = false }) == "unauthorized");
Ok("an account that isn't added is a stranger", Outcome(DAD, true, new Dictionary<Guid, bool>()) == "unauthorized");
Ok("and with no list at all, too", Outcome(DAD, true, null) == "unauthorized");

// ── Adding one ───────────────────────────────────────────────────────────────
Section("Adding one");

var r = Call("AddAccountLink", KID, B(("Username", "Dad"), ("Password", "dadpw"), ("MasterPin", "1234")));
Ok("a profile can't", Refused(r) && !Linked(HOME, DAD), Text(r));
r = Call("AddAccountLink", HOME, B(("Username", "Dad"), ("Password", "dadpw")));
Ok("the main account needs its PIN", r.status == 400 && !Linked(HOME, DAD), Text(r));
r = Call("AddAccountLink", HOME, B(("Username", "Dad"), ("Password", "wrong"), ("MasterPin", "1234")));
Ok("a wrong password is refused", r.status == 400 && (string)r.value == "That username or password isn't right." && !Linked(HOME, DAD), Text(r));
r = Call("AddAccountLink", HOME, B(("Username", "Nobody"), ("Password", "x"), ("MasterPin", "1234")));
Ok("so is a name that doesn't exist, in the same words", (string)r.value == "That username or password isn't right.", Text(r));
Ok("and no session was made by trying", world.Sessions == 0);

r = Call("AddAccountLink", HOME, B(("Username", "dad"), ("Password", "dadpw"), ("MasterPin", "1234")));
Ok("the right password adds it", r.status == 200 && Linked(HOME, DAD), Text(r));
// AuthenticateUser counts a wrong password towards the account's lockout, and with one set
// Jellyfin turns the account off: a household could disable someone by guessing. So the
// password goes to Jellyfin's provider, which only compares it.
Ok("checked by Jellyfin's own provider, with no session", world.Sessions == 0 && world.ProviderCalls.Count > 0, string.Join(" | ", world.ProviderCalls));
Ok("so a wrong password never counts towards the account's lockout", world.AuthCalls.Count == 0 && world.LockoutCount.Count == 0,
   string.Join(" | ", world.AuthCalls));
var rq = Call("AddAccountLink", HOME, B(("Username", "Gone"), ("Password", "gpw"), ("MasterPin", "1234")));
Ok("an account turned off in Jellyfin isn't added, in the same words", rq.status == 400 && (string)rq.value == "That username or password isn't right."
   && !Linked(HOME, GONE), Text(rq));
rq = Call("AddAccountLink", HOME, B(("Username", "Directory"), ("Password", "dpw"), ("MasterPin", "1234")));
// R-1069: a different message for an LDAP/SSO account needed no password, so it told anyone
// in a household which usernames exist. Every failure before a right password reads the same.
Ok("one that signs in through LDAP or SSO is refused in the same words, so a name can't be probed", rq.status == 400
   && (string)rq.value == "That username or password isn't right." && !Linked(HOME, DIRECTORY), Text(rq));
var nobody = Call("AddAccountLink", HOME, B(("Username", "NoSuchPerson"), ("Password", "dpw"), ("MasterPin", "1234")));
Ok("and the words are those for a name that does not exist", nobody.status == rq.status && (string)nobody.value == (string)rq.value, Text(nobody));
Ok("and its password isn't tried anywhere", !world.ProviderCalls.Contains("Directory") && world.AuthCalls.Count == 0);
var added = (IEnumerable)P(r.value, "Added");
Ok("and the reply lists it", added != null && added.Cast<object>().Any(a => (Guid)P(a, "UserId") == DAD && (string)P(a, "Name") == "Dad"));
r = Call("AddAccountLink", HOME, B(("Username", "Dad"), ("Password", "dadpw"), ("MasterPin", "1234")));
Ok("twice is refused", r.status == 400 && (string)r.value == "That account is already here.", Text(r));
// Every refusal above counted towards the limit, right password or not; only an add clears
// it (R-1068). Start the next cases from a clean count.
ResetLimits();
r = Call("AddAccountLink", HOME, B(("Username", "Parent"), ("Password", "parentpw"), ("MasterPin", "1234")));
Ok("its own account is refused", r.status == 400 && !Linked(HOME, HOME), Text(r));
world.Passwords[OKID] = "kidpw";
r = Call("AddAccountLink", HOME, B(("Username", "Neighbour_Kid"), ("Password", "kidpw"), ("MasterPin", "1234")));
Ok("so is a profile, even with its password", r.status == 400 && !Linked(HOME, OKID), Text(r));
r = Call("AddAccountLink", HOME, B(("Username", "Owner"), ("Password", "ownerpw"), ("MasterPin", "1234")));
Ok("an administrator with no PIN is refused", r.status == 400 && (string)r.value == "An administrator account needs a PIN before it can be added." && !Linked(HOME, ADMIN), Text(r));
r = Call("AddAccountLink", HOME, B(("Username", "Mum"), ("Password", "mumpw"), ("MasterPin", "1234")));
Ok("an account with a PIN is added", r.status == 200 && Linked(HOME, MUM), Text(r));

ResetLimits();
int lastStatus = 0;
for (int i = 0; i < 12 && lastStatus != 429; i++)
    lastStatus = Call("AddAccountLink", HOME, B(("Username", "Lodger"), ("Password", "guess" + i), ("MasterPin", "1234"))).status;
Ok("guessing passwords is stopped", lastStatus == 429, lastStatus.ToString());
int triesBefore = world.ProviderCalls.Count;
r = Call("AddAccountLink", HOME, B(("Username", "Lodger"), ("Password", "lpw"), ("MasterPin", "1234")));
Ok("and once stopped, even the right one isn't tried", r.status == 429 && world.ProviderCalls.Count == triesBefore, Text(r));
Ok("and none of that guessing counted towards the Lodger's lockout", !world.LockoutCount.ContainsKey(LODGER));
ResetLimits();

// A right password used to clear the count before the link was refused, and the caller
// always has one to hand: an account already on its switcher. So four guesses, one known
// password, four more, for ever (R-1068).
for (int i = 0; i < 4; i++)
    Call("AddAccountLink", HOME, B(("Username", "Lodger"), ("Password", "guess" + i), ("MasterPin", "1234")));
r = Call("AddAccountLink", HOME, B(("Username", "Dad"), ("Password", "dadpw"), ("MasterPin", "1234")));
Ok("a right password that adds nothing is still refused", r.status == 400 && (string)r.value == "That account is already here.", Text(r));
lastStatus = 0;
for (int i = 4; i < 9 && lastStatus != 429; i++)
    lastStatus = Call("AddAccountLink", HOME, B(("Username", "Lodger"), ("Password", "guess" + i), ("MasterPin", "1234"))).status;
Ok("and it doesn't clear the count: guessing is still stopped", lastStatus == 429, lastStatus.ToString());
ResetLimits();

// Every other limit here is per address or per household, and both are cheap to change.
// The account being guessed is the one thing every guess shares.
lastStatus = 0;
int lodgerTries = 0;
var callers = new[] { (HOME, "1234"), (MUM, "4321"), (ADMIN2, "9999") };
for (int i = 0; i < 30 && lastStatus != 429; i++)
{
    var (who, pin) = callers[i % 3];
    int before = world.ProviderCalls.Count;
    lastStatus = Call("AddAccountLink", who, B(("Username", "Lodger"), ("Password", "guess" + i), ("MasterPin", pin)),
        ip: "10.0.0." + (i + 1)).status;
    if (world.ProviderCalls.Count > before) lodgerTries++;
}
Ok("guessing one account from many households and addresses is stopped too", lastStatus == 429, lastStatus.ToString());
Ok("after at most ten tries of its password", lodgerTries <= 10, lodgerTries.ToString());
r = Call("AddAccountLink", OTHER, B(("Username", "Lodger"), ("Password", "lpw")), ip: "10.0.9.9");
Ok("and while it is, not even the right one from somewhere new is tried", r.status == 429 && !Linked(OTHER, LODGER), Text(r));
ResetLimits();

// ── Seeing and removing ──────────────────────────────────────────────────────
Section("Seeing and removing");

r = Call("GetAccountLinks", HOME, pinHeader: "1234");
Ok("the household sees who it added", r.status == 200 && ((IEnumerable)P(r.value, "Added")).Cast<object>().Count() == 2, Text(r));
r = Call("GetAccountLinks", HOME);
Ok("with its PIN", r.status == 400, Text(r));
r = Call("GetAccountLinks", MUM, pinHeader: "4321");
var shown = ((IEnumerable)P(r.value, "ShownIn"))?.Cast<object>().ToList();
Ok("the account sees where it is shown", r.status == 200 && shown?.Count == 1 && (Guid)P(shown[0], "UserId") == HOME
   && (string)P(shown[0], "Name") == "Parent", Text(r));

r = Call("RemoveAccountLink", OTHER, B(("UserId", MUM), ("HouseholdId", HOME)));
Ok("someone else can't remove it", Refused(r) && Linked(HOME, MUM), Text(r));
r = Call("RemoveAccountLink", MUM, B(("HouseholdId", HOME), ("MasterPin", "4321")));
Ok("the account can leave", r.status == 200 && !Linked(HOME, MUM), Text(r));
Call("AddAccountLink", HOME, B(("Username", "Mum"), ("Password", "mumpw"), ("MasterPin", "1234")));
r = Call("RemoveAccountLink", HOME, B(("UserId", MUM), ("MasterPin", "1234")));
Ok("the household can take it off", r.status == 200 && !Linked(HOME, MUM), Text(r));
r = Call("RemoveAccountLink", HOME, B(("UserId", MUM), ("MasterPin", "1234")));
Ok("removing what isn't there says so", r.status == 400, Text(r));

// ── The switcher ─────────────────────────────────────────────────────────────
Section("The switcher");

Call("AddAccountLink", HOME, B(("Username", "Mum"), ("Password", "mumpw"), ("MasterPin", "1234")));
Map(Guid.NewGuid(), MUM, "MumsKid");
r = Call("GetProfiles", HOME);
var tiles = (r.value as IEnumerable)?.Cast<object>().ToList() ?? new List<object>();
object Tile(Guid id) => tiles.FirstOrDefault(t => (Guid)P(t, "ProfileUserId") == id);
Ok("/list answers", r.status == 200, Text(r));
Ok("an added account is a tile, marked as added", Tile(DAD) != null && (bool)P(Tile(DAD), "IsLinked") && (bool)P(Tile(DAD), "IsBonfire"));
Ok("with no PIN asked when it has none", Tile(DAD) != null && !(bool)P(Tile(DAD), "RequiresPin"));
Ok("and its PIN asked when it has one", Tile(MUM) != null && (bool)P(Tile(MUM), "RequiresPin"));
Ok("its own profiles are not shown", !tiles.Any(t => (string)P(t, "ProfileName") == "MumsKid"));
Ok("the household's own still are, unmarked", Tile(KID) != null && !(bool)P(Tile(HOME), "IsLinked"));

r = Call("SwitchProfile", HOME, B(("ProfileId", DAD)));
Ok("/switch opens an added account with no PIN", r.status == 200 && sessionStub.Opened.Contains(DAD), Text(r));
r = Call("SwitchProfile", HOME, B(("ProfileId", MUM), ("Pin", "0000")));
Ok("one with a PIN refuses the wrong one", r.status == 400 && !sessionStub.Opened.Contains(MUM), Text(r));
r = Call("SwitchProfile", HOME, B(("ProfileId", MUM), ("Pin", "4321")));
Ok("and opens with the right one", r.status == 200 && sessionStub.Opened.Contains(MUM), Text(r));
r = Call("SwitchProfile", OTHER, B(("ProfileId", DAD)));
Ok("another household can't open it", Refused(r) && sessionStub.Opened.Count(x => x == DAD) == 1, Text(r));
r = Call("SwitchProfile", DAD, B(("ProfileId", HOME), ("Pin", "1234")));
Ok("and the added account sees nothing of the household", Refused(r), Text(r));

// A main account turned off in Dashboard -> Users shuts its profiles too (R-1135). /switch
// checked only the target's own account, and a profile's is never the one that was turned off.
var kidOpened = sessionStub.Opened.Count(x => x == KID);
world.Disabled.Add(HOME);
r = Call("SwitchProfile", HOME, B(("ProfileId", KID)));
Ok("a profile whose main account is turned off cannot be entered", r.status == 400 && sessionStub.Opened.Count(x => x == KID) == kidOpened, Text(r));
Ok("and says the account is turned off, so the app can show why", Text(r).Contains("account is turned off"), Text(r));
world.Disabled.Remove(HOME);
r = Call("SwitchProfile", HOME, B(("ProfileId", KID)));
Ok("turned back on, it opens again", r.status == 200 && sessionStub.Opened.Count(x => x == KID) == kidOpened + 1, Text(r));

// ── Taking the door away ─────────────────────────────────────────────────────
Section("Taking the door away");

// /switch hands back an ordinary Jellyfin token, and those don't expire. Removing a link or
// kicking a member took the door away and left whoever was through it inside (R-1302).
SessionStub.SignedOut.Clear();
r = Call("SwitchProfile", HOME, B(("ProfileId", MUM), ("Pin", "4321")), deviceId: "living-room");
Ok("the household opens Mum on the living-room TV", r.status == 200, Text(r));
Call("SwitchProfile", HOME, B(("ProfileId", DAD)), deviceId: "living-room");
Call("SwitchProfile", HOME, B(("ProfileId", KID)), deviceId: "living-room");
r = Call("RemoveAccountLink", HOME, B(("UserId", MUM), ("MasterPin", "1234")));
Ok("taking Mum off the switcher signs Mum out on the TV that opened her",
   r.status == 200 && SessionStub.SignedOut.Contains((MUM, "living-room")), string.Join(", ", SessionStub.SignedOut));
Ok("only Mum: an account still added stays signed in", !SessionStub.SignedOut.Any(x => x.user == DAD));
Ok("and the household's own profiles are never touched", !SessionStub.SignedOut.Any(x => x.user == KID || x.user == HOME));
ResetLimits();
Call("AddAccountLink", HOME, B(("Username", "Mum"), ("Password", "mumpw"), ("MasterPin", "1234")));

SessionStub.SignedOut.Clear();
mapType.GetProperty("PinHash").SetValue(Row(OKID), Hash("2468"));
var fire = Activator.CreateInstance(groupType);
groupType.GetProperty("OwnerUserId").SetValue(fire, OTHER);
((IList)groupType.GetProperty("MemberUserIds").GetValue(fire)).Add(HOME);
var fires = (IList)cfgType.GetProperty("BonfireGroups").GetValue(config);
fires.Add(fire);
r = Call("SwitchProfile", HOME, B(("ProfileId", OKID), ("Pin", "2468")), deviceId: "living-room");
Ok("a Bonfire member opens the neighbour's kid with its PIN", r.status == 200, Text(r));
r = Call("KickBonfireMember", OTHER, B(("MemberId", HOME)));
Ok("kicking the member signs the neighbour's kid out on that TV",
   r.status == 200 && SessionStub.SignedOut.Contains((OKID, "living-room")), Text(r) + " | " + string.Join(", ", SessionStub.SignedOut));

// A new PIN is the other door: knowing the old one is all a device that came in proved
// (R-1302b). The member is back in the Bonfire for this.
groupType.GetProperty("MemberUserIds").GetValue(fire).GetType().GetMethod("Add").Invoke(groupType.GetProperty("MemberUserIds").GetValue(fire), new object[] { HOME });
SessionStub.SignedOut.Clear();
r = Call("SwitchProfile", HOME, B(("ProfileId", OKID), ("Pin", "2468")), deviceId: "den");
Ok("the member opens the neighbour's kid again, in the den", r.status == 200, Text(r));
// The same for a Bonfire target: its own household's main account decides (R-1135).
var okidOpened = sessionStub.Opened.Count(x => x == OKID);
world.Disabled.Add(OTHER);
r = Call("SwitchProfile", HOME, B(("ProfileId", OKID), ("Pin", "2468")), deviceId: "den");
Ok("the neighbour's kid is shut when the neighbour's main account is turned off", r.status == 400 && sessionStub.Opened.Count(x => x == OKID) == okidOpened, Text(r));
world.Disabled.Remove(OTHER);
r = Call("UpdateProfile", OTHER, B(("ProfileId", OKID), ("ProfileName", "Kid")));
Ok("renaming the kid signs nobody out", r.status == 200 && SessionStub.SignedOut.Count == 0, Text(r) + " | " + string.Join(", ", SessionStub.SignedOut));
r = Call("UpdateProfile", OTHER, B(("ProfileId", OKID), ("ProfileName", "Kid"), ("Pin", "1357")));
Ok("a new PIN for the kid signs the den out",
   r.status == 200 && SessionStub.SignedOut.Contains((OKID, "den")), Text(r) + " | " + string.Join(", ", SessionStub.SignedOut));
r = Call("SwitchProfile", HOME, B(("ProfileId", OKID), ("Pin", "2468")), deviceId: "den");
Ok("and the old PIN no longer opens it", r.status != 200, Text(r));

SessionStub.SignedOut.Clear();
r = Call("SwitchProfile", HOME, B(("ProfileId", MUM), ("Pin", "4321")), deviceId: "den");
Ok("the household opens Mum in the den", r.status == 200, Text(r));
r = Call("UpdateAccount", MUM, B(("LockoutMinutes", (int?)10), ("MasterPin", "4321")));
Ok("Mum changing her lockout signs nobody out", r.status == 200 && SessionStub.SignedOut.Count == 0, Text(r) + " | " + string.Join(", ", SessionStub.SignedOut));
r = Call("UpdateAccount", MUM, B(("Pin", "8765"), ("MasterPin", "4321")));
Ok("Mum giving herself a new PIN signs the den out",
   r.status == 200 && SessionStub.SignedOut.Contains((MUM, "den")), Text(r) + " | " + string.Join(", ", SessionStub.SignedOut));
Ok("and only Mum", SessionStub.SignedOut.All(x => x.user == MUM), string.Join(", ", SessionStub.SignedOut));
Call("UpdateAccount", MUM, B(("Pin", "4321"), ("MasterPin", "8765")));
ResetLimits();

fires.Remove(fire);
mapType.GetProperty("PinHash").SetValue(Row(OKID), null);
world.Log.Clear(); // the switches above synced the profiles' policies; nothing below is about them

// ── Guessing a Bonfire code ──────────────────────────────────────────────────
Section("Guessing a Bonfire code");

// A join cleared the count of wrong codes, and a household can always join a Bonfire whose
// code it knows: leave it and join again. Two guesses, a rejoin, two more, for ever (R-1303).
ResetLimits();
var camp = Activator.CreateInstance(groupType);
groupType.GetProperty("OwnerUserId").SetValue(camp, OTHER);
groupType.GetProperty("BonfireCode").SetValue(camp, "KNOWN1");
fires.Add(camp);
Call("JoinBonfire", HOME, B(("Code", "WRONG1")), pinHeader: "1234");
Call("JoinBonfire", HOME, B(("Code", "WRONG2")), pinHeader: "1234");
r = Call("JoinBonfire", HOME, B(("Code", "KNOWN1")), pinHeader: "1234");
Ok("a code it knows joins", r.status == 200 && ((IList)groupType.GetProperty("MemberUserIds").GetValue(camp)).Contains(HOME), Text(r));
Call("LeaveBonfire", HOME, pinHeader: "1234");
r = Call("JoinBonfire", HOME, B(("Code", "WRONG3")), pinHeader: "1234");
Ok("and doesn't clear the count of wrong ones", r.status == 429, Text(r));
fires.Remove(camp);
ResetLimits();

// ── The administrator ────────────────────────────────────────────────────────
Section("The administrator");

r = Call("AdminAddAccountLink", HOME, B(("HouseholdId", OTHER), ("UserId", LODGER)));
Ok("only an administrator", Refused(r) && !Linked(OTHER, LODGER), Text(r));
r = Call("AdminAddAccountLink", ADMIN, B(("HouseholdId", OTHER), ("UserId", LODGER)));
Ok("adds without a password", r.status == 200 && Linked(OTHER, LODGER), Text(r));
r = Call("AdminAddAccountLink", ADMIN, B(("HouseholdId", OKID), ("UserId", DAD)));
Ok("but not to a profile's household", r.status == 400 && !Linked(OKID, DAD), Text(r));
r = Call("AdminAddAccountLink", ADMIN, B(("HouseholdId", OTHER), ("UserId", ADMIN)));
Ok("and the same rules hold: an administrator needs a PIN", r.status == 400 && !Linked(OTHER, ADMIN), Text(r));
r = Call("AdminRemoveAccountLink", ADMIN, B(("HouseholdId", OTHER), ("UserId", LODGER)));
Ok("and removes", r.status == 200 && !Linked(OTHER, LODGER), Text(r));
r = Call("GetAdminMappings", ADMIN);
var homeRow = ((IEnumerable)P(r.value, "MasterUsers"))?.Cast<object>().FirstOrDefault(m => (Guid)P(m, "ProfileUserId") == HOME);
Ok("the settings page sees who each household added", homeRow != null
   && ((IEnumerable)P(homeRow, "Added")).Cast<object>().Count() == 2, Text(r));

// ── Making an account a profile ──────────────────────────────────────────────
Section("Making an account a profile");

// The lodger: a PIN and an email from its time as a main account, two libraries of three,
// a blocked tag, an address, and shown in Parent's switcher.
var LIB1 = Guid.NewGuid(); var LIB2 = Guid.NewGuid();
Map(LODGER, LODGER, "Lodger", pin: "5555");
world.Policies[LODGER] = new UserPolicy { EnableAllFolders = false, EnabledFolders = new[] { LIB1, LIB2 }, BlockedTags = new[] { "horror" },
    AuthenticationProviderId = BONFIRE, PasswordResetProviderId = "x" };
var email = Activator.CreateInstance(emailType);
emailType.GetProperty("UserId").SetValue(email, LODGER);
((IList)cfgType.GetProperty("AccountEmails").GetValue(config)).Add(email);
Call("AdminAddAccountLink", ADMIN, B(("HouseholdId", HOME), ("UserId", LODGER)));

r = Call("MakeProfile", HOME, B(("UserId", LODGER), ("HouseholdId", HOME)));
Ok("only an administrator", Refused(r) && P(Row(LODGER), "MasterUserId") is Guid g0 && g0 == LODGER, Text(r));
r = Call("MakeProfile", ADMIN, B(("UserId", ADMIN2), ("HouseholdId", HOME)));
Ok("never an administrator", r.status == 400, Text(r));
r = Call("MakeProfile", ADMIN, B(("UserId", OTHER), ("HouseholdId", HOME)));
Ok("never an account with profiles", r.status == 400 && (string)r.value == "It has profiles of its own. Move or delete them first.", Text(r));
r = Call("MakeProfile", ADMIN, B(("UserId", KID), ("HouseholdId", OTHER)));
Ok("never what is already a profile", r.status == 400, Text(r));
r = Call("MakeProfile", ADMIN, B(("UserId", DAD), ("HouseholdId", KID)));
Ok("never into a profile's household", r.status == 400, Text(r));
var group = Activator.CreateInstance(groupType);
groupType.GetProperty("OwnerUserId").SetValue(group, OTHER);
((IList)groupType.GetProperty("MemberUserIds").GetValue(group)).Add(DAD);
var groups = (IList)cfgType.GetProperty("BonfireGroups").GetValue(config);
groups.Add(group);
r = Call("MakeProfile", ADMIN, B(("UserId", DAD), ("HouseholdId", HOME)));
Ok("never one in a Bonfire", r.status == 400 && (string)r.value == "It's in a Bonfire. Leave it first.", Text(r));
groups.Remove(group);
r = Call("MakeProfile", ADMIN, B(("UserId", DAD), ("HouseholdId", HOME), ("ProfileName", "kid")));
Ok("a name the household already has is refused", r.status == 400 && (string)r.value == "A profile with this name already exists.", Text(r));
// A display name that is someone's username (R-1336). Jellyfin looks a typed name up as a
// username first, so a "Mum" tile reached the real Mum: it could never sign in, and every
// wrong PIN counted against her. Refused wherever a name is set; an account's own name is
// the one it is allowed to keep.
r = Call("MakeProfile", ADMIN, B(("UserId", DAD), ("HouseholdId", HOME), ("ProfileName", "mum")));
Ok("a name that is another account's username is refused, whatever its case", r.status == 400 && ((string)r.value).Contains("already exists"), Text(r));
// Creating one gets as far as making the account when it is not refused, which this
// world cannot do: that surfaces as an exception, and a failed case rather than a crash.
try { r = Call("CreateProfile", HOME, B(("ProfileName", "Dad"), ("MasterPin", "1234"))); }
catch (Exception ex) { r = (-1, "threw " + (ex.InnerException ?? ex).GetType().Name); }
Ok("so is creating a profile with it", r.status == 400 && ((string)r.value).Contains("already exists"), Text(r));
r = Call("UpdateProfile", HOME, B(("ProfileId", KID), ("ProfileName", "mum"), ("MasterPin", "1234")));
Ok("so is renaming one to it", r.status == 400 && ((string)r.value).Contains("already exists"), Text(r));
cfgType.GetProperty("MaxProfilesPerUser").SetValue(config, 1);
r = Call("MakeProfile", ADMIN, B(("UserId", DAD), ("HouseholdId", HOME)));
Ok("past the household's limit is refused", r.status == 400 && ((string)r.value).StartsWith("Maximum profile limit"), Text(r));
cfgType.GetProperty("MaxProfilesPerUser").SetValue(config, 5);
Ok("and none of that changed anything", world.Log.Count == 0 && P(Row(DAD), "MasterUserId") == null, string.Join(" | ", world.Log));
// (After that check: this one is a real update and logs its writes.)
r = Call("UpdateProfile", HOME, B(("ProfileId", KID), ("ProfileName", "Kid"), ("MasterPin", "1234")));
Ok("a profile keeping its own name is not stopped by the name check", r.status == 200, Text(r));
world.Log.Clear();

// R-1070: the password change is the step that can fail, and it came after the account had
// been signed out and hidden, with no Bonfire row to show for it. A failure must leave it
// exactly as it was: still shown, still on its provider, never signed out.
world.Log.Clear();
var dadProvider = world.Users[DAD].AuthenticationProviderId;
var dadHidden = world.Policies[DAD].IsHidden;
world.FailPassword = true;
r = Call("MakeProfile", ADMIN, B(("UserId", DAD), ("HouseholdId", HOME)));
world.FailPassword = false;
Ok("a password that cannot be changed fails the request", r.status == 500, Text(r));
Ok("the account is not left hidden", world.Policies[DAD].IsHidden == dadHidden, "hidden=" + world.Policies[DAD].IsHidden);
Ok("nor moved to another provider", world.Users[DAD].AuthenticationProviderId == dadProvider, world.Users[DAD].AuthenticationProviderId);
Ok("nor signed out", At("revoke " + DAD) < 0, string.Join(" | ", world.Log));
Ok("and it is still no profile", P(Row(DAD), "MasterUserId") == null);
Ok("its password is untouched", world.Passwords[DAD] == "dadpw");

world.Log.Clear();
r = Call("MakeProfile", ADMIN, B(("UserId", LODGER), ("HouseholdId", HOME), ("ProfileName", " Lodger ")));
Ok("it succeeds", r.status == 200, Text(r));
var lodgerRow = Row(LODGER);
Ok("it is a profile of the household now", lodgerRow != null && (Guid)P(lodgerRow, "MasterUserId") == HOME
   && (string)P(lodgerRow, "ProfileName") == "Lodger");
Ok("one row, not two", mappings.Cast<object>().Count(m => (Guid)mapType.GetProperty("ProfileUserId").GetValue(m) == LODGER) == 1);
Ok("its PIN is the profile's PIN", (string)P(lodgerRow, "PinHash") is string ph && ph.Length > 0
   && hasher.GetMethods().First(m => m.Name == "Verify" && m.GetParameters().Length == 2).Invoke(null, new object[] { "5555", ph }).ToString() == "Match");
var folders = ((IEnumerable)P(lodgerRow, "EnabledFolders"))?.Cast<Guid>().ToList();
Ok("its libraries are kept as its own", folders != null && folders.Count == 2 && folders.Contains(LIB1) && !(bool)P(lodgerRow, "AllLibraries"));
Ok("and its blocked tags", ((IEnumerable)P(lodgerRow, "BlockedTags")).Cast<string>().Contains("horror"));
Ok("its email went with its main account", !((IList)cfgType.GetProperty("AccountEmails").GetValue(config)).Cast<object>()
   .Any(a => (Guid)emailType.GetProperty("UserId").GetValue(a) == LODGER));
Ok("and so did every link to it", !Links().Cast<object>().Any(l => (Guid)linkType.GetProperty("UserId").GetValue(l) == LODGER));
Ok("it is hidden from the sign-in screen", world.Policies[LODGER].IsHidden);
Ok("on Jellyfin's own provider", world.Users[LODGER].AuthenticationProviderId == JELLYFIN, world.Users[LODGER].AuthenticationProviderId);
Ok("its old password no longer opens it", world.Passwords[LODGER] != "lpw" && world.Passwords[LODGER].Length >= 32);

int At(string prefix) => world.Log.FindIndex(l => l.StartsWith(prefix));
// Was "signed out first" (At == 0). That order is the R-1070 bug: sign-out cannot be undone,
// so it now waits for the password change, the one step that can fail.
Ok("it was signed out", At("revoke " + LODGER) >= 0, string.Join(" | ", world.Log));
Ok("once the password was changed, not before", At("revoke " + LODGER) > At("password " + LODGER), string.Join(" | ", world.Log));
Ok("the password was changed while it was still an account",
   world.Log.Any(l => l.StartsWith("password " + LODGER + " mapped=False")), string.Join(" | ", world.Log));
// Set through the provider the account is on at that moment: Jellyfin's, not Bonfire's.
Ok("and on Jellyfin's provider by then, through which it is set",
   world.Log.Any(l => l == "password " + LODGER + " mapped=False provider=" + JELLYFIN), string.Join(" | ", world.Log));
Ok("then the household's restrictions were applied to it as a profile",
   world.Log.Any(l => l.StartsWith("policy " + LODGER + " mapped=True")), string.Join(" | ", world.Log));
Ok("returned with its name", (string)P(r.value, "ProfileName") == "Lodger");

Section("A picture that is refused leaves the one the profile has");

// R-1108: SaveProfileImage and CopyLibraryAvatarToProfile answered a bare null for every
// failure, and the edit stored that null as "no picture". A 3 MB upload deleted the profile's
// picture and answered 200. R-1109: any string under /plugins/profiles/image/ was stored as it came.
{
    var imageFolder = Path.Combine(tempDir, "plugins", "ProfilesManagement");
    Directory.CreateDirectory(imageFolder);
    var kidFile = Path.Combine(imageFolder, OKID.ToString() + ".png");
    var original = new byte[] { 1, 2, 3, 4 };
    File.WriteAllBytes(kidFile, original);
    var kidRow = Row(OKID);
    var current = "/plugins/profiles/image/" + OKID + "?v=100";
    mapType.GetProperty("ProfileImage").SetValue(kidRow, current);

    string Stored() => (string)P(Row(OKID), "ProfileImage");
    bool Untouched() => Stored() == current && File.Exists(kidFile) && File.ReadAllBytes(kidFile).SequenceEqual(original);
    string Png(int bytes) => "data:image/png;base64," + Convert.ToBase64String(new byte[bytes]);
    (int status, object value) Edit(params (string, object)[] fields)
    {
        var body = new List<(string, object)> { ("ProfileId", OKID), ("ProfileName", "Kid") };
        body.AddRange(fields);
        // The later of two values for one field wins, so a case can change the name.
        return Call("UpdateProfile", OTHER, B(body.GroupBy(f => f.Item1).Select(g => g.Last()).ToArray()));
    }

    r = Edit(("ProfileImage", Png(3 * 1024 * 1024)), ("ProfileImageThumb", ""));
    Ok("a 3 MB picture is refused with a reason", r.status == 400 && ((string)r.value ?? "").Contains("too large"), Text(r));
    Ok("and the picture it has is untouched", Untouched(), Stored());

    r = Edit(("ProfileImage", "data:image/png;base64,@@@not base64@@@"));
    Ok("so is one that isn't base64", r.status == 400 && ((string)r.value ?? "").Contains("could not be read") && Untouched(), Text(r));
    r = Edit(("ProfileImage", "data:image/svg+xml;base64," + Convert.ToBase64String(new byte[] { 60, 115, 118, 103 })));
    Ok("so is a type we don't store", r.status == 400 && ((string)r.value ?? "").Contains("supported image") && Untouched(), Text(r));

    r = Edit(("AvatarLibraryId", "no-such-avatar"));
    Ok("an avatar that isn't in the library is refused", r.status == 400 && ((string)r.value ?? "").Contains("No such avatar") && Untouched(), Text(r));

    cfgType.GetProperty("DisallowCustomAvatarUploads").SetValue(config, true);
    r = Edit(("ProfileImage", Png(10)));
    Ok("a custom picture is refused while the server allows only library avatars",
       r.status == 400 && ((string)r.value ?? "").Contains("avatar library") && Untouched(), Text(r));
    cfgType.GetProperty("DisallowCustomAvatarUploads").SetValue(config, false);

    r = Edit(("ProfileName", "Kidlet"), ("ProfileImage", Png(3 * 1024 * 1024)));
    Ok("a refused picture stops the rest of the edit too: the name is not changed",
       r.status == 400 && (string)P(Row(OKID), "ProfileName") == "Kid" && Untouched(), Text(r));

    // R-1109: only this profile's own picture address, or what is already stored.
    foreach (var bad in new[]
    {
        "/plugins/profiles/image/" + Guid.NewGuid(),
        "/plugins/profiles/image/" + OKID + "\" onerror=\"x",
        "/plugins/profiles/image/../../../etc/passwd",
        "/plugins/profiles/image/" + OKID + "?v=1&x=2",
        "/plugins/profiles/image/" + OKID + "?v=abc",
    })
    {
        r = Edit(("ProfileImage", bad));
        Ok("a made-up picture address is refused: " + bad, r.status == 400 && Untouched(), Text(r));
    }

    r = Edit(("ProfileImage", current));
    Ok("what it already has is accepted", r.status == 200 && Stored() == current, Text(r));
    r = Edit(("ProfileImage", "/plugins/profiles/image/" + OKID + "?v=200"));
    Ok("and its own address with a new cache-buster", r.status == 200 && Stored() == "/plugins/profiles/image/" + OKID + "?v=200", Text(r));
    current = Stored();

    r = Edit(("ProfileImage", Png(10)));
    Ok("a good picture is still saved", r.status == 200 && Stored() != null && Stored() != current && Stored().StartsWith("/plugins/profiles/image/" + OKID), Text(r));

    // A library avatar chosen, then a missing one: the chosen stays.
    var libraryFolder = Path.Combine(imageFolder, "avatars");
    Directory.CreateDirectory(libraryFolder);
    File.WriteAllBytes(Path.Combine(libraryFolder, "fox.png"), new byte[] { 9, 9, 9 });
    var libType = asm.GetType("Jellyfin.Profiles.Configuration.AvatarLibraryItem", true);
    var libItem = Activator.CreateInstance(libType);
    libType.GetProperty("Id").SetValue(libItem, "fox");
    libType.GetProperty("DisplayName").SetValue(libItem, "Fox");
    libType.GetProperty("Extension").SetValue(libItem, ".png");
    ((IList)cfgType.GetProperty("AvatarLibrary").GetValue(config)).Add(libItem);
    r = Edit(("AvatarLibraryId", "fox"));
    Ok("a library avatar is copied", r.status == 200 && File.ReadAllBytes(kidFile).SequenceEqual(new byte[] { 9, 9, 9 }), Text(r));
    var afterFox = Stored();

    File.Delete(Path.Combine(libraryFolder, "fox.png"));
    r = Edit(("AvatarLibraryId", "fox"));
    Ok("one whose file has gone is refused and the copy it had stays",
       r.status == 400 && Stored() == afterFox && File.Exists(kidFile) && File.ReadAllBytes(kidFile).SequenceEqual(new byte[] { 9, 9, 9 }), Text(r));

    r = Edit(("ProfileImage", ""));
    Ok("an empty picture is still a deliberate removal", r.status == 200 && Stored() == null && !File.Exists(kidFile), Text(r));

    // Creating: refused before the account exists.
    var usersBefore = world.Users.Count;
    try { r = Call("CreateProfile", OTHER, B(("ProfileName", "Fresh"), ("ProfileImage", Png(3 * 1024 * 1024)))); }
    catch (Exception e) { r = (-1, (e.InnerException ?? e).GetType().Name); }   // the stub makes no accounts: getting that far is the failure
    Ok("creating with a refused picture answers 400 and makes no account", r.status == 400 && world.Users.Count == usersBefore, Text(r));
}

try { Directory.Delete(tempDir, true); } catch { }
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
    protected override object Invoke(MethodInfo m, object[] args)
        => m.Name == "IsInLocalNetwork" ? false : Loose.Default(m.ReturnType);
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
