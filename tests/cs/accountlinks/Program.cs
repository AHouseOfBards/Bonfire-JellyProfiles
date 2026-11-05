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
// Against 77b0d04 the first assertion fails: there is no such route.
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

var users = World.Create(world);
var sessions = SessionStub.Create();
var sessionStub = (SessionStub)(object)sessions;
SessionStub.OnRevoke = id => world.Log.Add("revoke " + id);
World.MappedNow = id => Row(id) is object mr && (Guid)mapType.GetProperty("MasterUserId").GetValue(mr) != id;

(int status, object value) Call(string method, Guid caller, Dictionary<string, object> body = null, string pinHeader = null)
{
    var r = ctrlType.GetMethod(method);
    if (r == null) return (-404, "no route " + method);
    var ctor = ctrlType.GetConstructors().First();
    var args = ctor.GetParameters().Select(p =>
        p.ParameterType == typeof(IUserManager) ? users
        : p.ParameterType == typeof(ISessionManager) ? sessions
        : p.ParameterType == typeof(INetworkManager) ? NetworkStub.Create()
        : p.ParameterType == typeof(ILibraryManager) ? Loose<ILibraryManager>.Create()
        : p.ParameterType.Name.StartsWith("ILogger") ? Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(ctrlType))
        : (object)null).ToArray();
    var c = (ControllerBase)ctor.Invoke(args);
    var http = new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(caller == Guid.Empty ? Array.Empty<Claim>()
            : new[] { new Claim("Jellyfin-UserId", caller.ToString()) }, caller == Guid.Empty ? null : "test")),
    };
    http.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.20");
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
Ok("checked without making a session", world.Sessions == 0 && world.AuthCalls.All(c => c.EndsWith(" session=False")), string.Join(" | ", world.AuthCalls));
var added = (IEnumerable)P(r.value, "Added");
Ok("and the reply lists it", added != null && added.Cast<object>().Any(a => (Guid)P(a, "UserId") == DAD && (string)P(a, "Name") == "Dad"));
r = Call("AddAccountLink", HOME, B(("Username", "Dad"), ("Password", "dadpw"), ("MasterPin", "1234")));
Ok("twice is refused", r.status == 400 && (string)r.value == "That account is already here.", Text(r));
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
int triesBefore = world.AuthCalls.Count;
r = Call("AddAccountLink", HOME, B(("Username", "Lodger"), ("Password", "lpw"), ("MasterPin", "1234")));
Ok("and once stopped, even the right one isn't tried", r.status == 429 && world.AuthCalls.Count == triesBefore, Text(r));
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
cfgType.GetProperty("MaxProfilesPerUser").SetValue(config, 1);
r = Call("MakeProfile", ADMIN, B(("UserId", DAD), ("HouseholdId", HOME)));
Ok("past the household's limit is refused", r.status == 400 && ((string)r.value).StartsWith("Maximum profile limit"), Text(r));
cfgType.GetProperty("MaxProfilesPerUser").SetValue(config, 5);
Ok("and none of that changed anything", world.Log.Count == 0 && P(Row(DAD), "MasterUserId") == null, string.Join(" | ", world.Log));

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
Ok("it was signed out first", At("revoke " + LODGER) == 0, string.Join(" | ", world.Log));
Ok("the password was changed while it was still an account",
   world.Log.Any(l => l.StartsWith("password " + LODGER + " mapped=False")), string.Join(" | ", world.Log));
// Set through the provider the account is on at that moment: Jellyfin's, not Bonfire's.
Ok("and on Jellyfin's provider by then, through which it is set",
   world.Log.Any(l => l == "password " + LODGER + " mapped=False provider=" + JELLYFIN), string.Join(" | ", world.Log));
Ok("then the household's restrictions were applied to it as a profile",
   world.Log.Any(l => l.StartsWith("policy " + LODGER + " mapped=True")), string.Join(" | ", world.Log));
Ok("returned with its name", (string)P(r.value, "ProfileName") == "Lodger");

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
    public int Sessions;
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
            IsAdministrator = W.Admins.Contains(id), IsHidden = p.IsHidden, EnableAllFolders = p.EnableAllFolders,
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
                    return Task.FromException<User>(new System.Security.Authentication.AuthenticationException("Invalid username or password entered."));
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
                W.Passwords[pid] = (string)args[1];
                W.Log.Add("password " + pid + " mapped=" + World.MappedNow(pid) + " provider=" + W.Users[pid].AuthenticationProviderId);
                return Task.CompletedTask;
        }
        return Loose.Default(m.ReturnType);
    }
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
