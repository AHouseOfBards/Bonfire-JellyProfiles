using System;
using System.IO;
using MediaBrowser.Controller.Library;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Authentication;
using Microsoft.Extensions.Logging.Abstractions;

// ─────────────────────────────────────────────────────────────────────────────
// BonfirePinAuthenticationProvider: what lets a television offer a profile at all.
//
// Jellyfin offers an account with no AuthenticationProviderId every enabled provider in turn and the first success wins, so a wrong "yes" here is an authentication bypass on someone else's account. The tests are mostly refusals, each checked separately.
//
// Type shape matters too. One net9 assembly ships for 10.11 and 12.0, and 12.0 removed HasPassword from IAuthenticationProvider. An implicit implementation survives as an unused public method; an explicit one makes the type fail to load, leaving the server with no authentication provider.
// ─────────────────────────────────────────────────────────────────────────────

const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic
                       | BindingFlags.Instance | BindingFlags.Static;

int pass = 0;
var fails = new System.Collections.Generic.List<string>();
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

// Which build to load. Output sits in bin/Release/<tfm>/, so the folder name is the framework the csproj resolved. `tests/run.sh cs10` runs against net10.0.
var tfm = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Name;

var asm = Assembly.LoadFrom(Path.Combine(RepoRoot(), "bin", "Release", tfm, "Jellyfin.Profiles.dll"));

var providerType = asm.GetType("Jellyfin.Profiles.Auth.BonfirePinAuthenticationProvider", true);
var pluginType   = asm.GetType("Jellyfin.Profiles.Plugin", true);
var cfgType      = asm.GetType("Jellyfin.Profiles.Configuration.PluginConfiguration", true);
var mappingType  = asm.GetType("Jellyfin.Profiles.Configuration.ProfileMapping", true);
var hasherType   = asm.GetType("Jellyfin.Profiles.Auth.PinHasher", true);

Console.WriteLine();
Console.WriteLine("── It loads on both Jellyfin 10.11 and 12.0 ───────────────────");

Ok("it implements IAuthenticationProvider", typeof(IAuthenticationProvider).IsAssignableFrom(providerType));
Ok("and IRequiresResolvedUser, so it is handed the profile",
   typeof(IRequiresResolvedUser).IsAssignableFrom(providerType));

// An explicit interface implementation compiles to a private method named with the interface's full name. Finding one means the type fails to load on a Jellyfin without that member.
var explicitImpls = providerType
    .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
    .Where(m => m.IsFinal && m.IsVirtual && m.Name.Contains('.'))
    .Select(m => m.Name)
    .ToArray();
Ok("no member is implemented explicitly", explicitImpls.Length == 0,
   explicitImpls.Length > 0
       ? string.Join(", ", explicitImpls) + " — would not load on a Jellyfin whose interface omits it"
       : null);

// 12.0 dropped this from the interface. A plain public method lets 10.11 still skip the
// password box for a profile with no PIN.
var hasPassword = providerType.GetMethod("HasPassword", Any, null, new[] { typeof(User) }, null);
Ok("HasPassword is still present for 10.11", hasPassword != null && hasPassword.IsPublic);

Console.WriteLine();
Console.WriteLine("── Off unless an administrator turns it on ────────────────────");

var tempDir = Path.Combine(Path.GetTempPath(), "bonfire-clientpin-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
var plugin = Activator.CreateInstance(pluginType, new StubPaths(tempDir), new StubXml());
Ok("the plugin can be constructed against stub paths", plugin != null);

var configProp = pluginType.GetProperty("Configuration", Any);
var config = configProp.GetValue(plugin);
// A new install starts with Bonfire off (2.0); this harness is about a server where it is on.
config.GetType().GetProperty("BonfireOn").SetValue(config, true);
var enableProp = cfgType.GetProperty("EnableClientPinLogin", Any);
Ok("the configuration has an EnableClientPinLogin flag", enableProp != null);
Ok("and it is OFF by default — this changes how the server authenticates",
   (bool)enableProp.GetValue(config) == false);

// NullLogger<T> has a public constructor or an Instance property depending on the abstractions version; use whichever exists.
var loggerType = typeof(NullLogger<>).MakeGenericType(providerType);
var instanceProp = loggerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
var logger = instanceProp != null
    ? instanceProp.GetValue(null)
    : Activator.CreateInstance(loggerType, nonPublic: true);
Ok("a logger can be supplied to the provider", logger != null);
// Both arguments explicit: Activator.CreateInstance does not apply default values. The provider resolves IUserManager from this, not the constructor, because UserManager takes IEnumerable<IAuthenticationProvider> and asking for it directly would be a dependency cycle.
var provider = Activator.CreateInstance(providerType, logger, ServiceStub.Create());
Ok("the provider can be constructed", provider != null);

var isEnabled = providerType.GetProperty("IsEnabled");
Ok("the provider reports itself disabled while the flag is off", (bool)isEnabled.GetValue(provider) == false);

enableProp.SetValue(config, true);
Ok("and enabled once it is on", (bool)isEnabled.GetValue(provider) == true);

Console.WriteLine();
Console.WriteLine("── The household ──────────────────────────────────────────────");

var MASTER = Guid.NewGuid();
var KID    = Guid.NewGuid();      // PIN 4821
var GUEST  = Guid.NewGuid();      // no PIN
var OUTSID = Guid.NewGuid();      // not a Bonfire user at all
var LONER  = Guid.NewGuid();      // has a PIN and a self-row, but owns no profiles

var hash = (Func<string, string>)(pin =>
    (string)hasherType.GetMethod("Hash", Any).Invoke(null, new object[] { pin }));

object Mapping(Guid profile, Guid master, string pin)
{
    var m = Activator.CreateInstance(mappingType);
    mappingType.GetProperty("ProfileUserId").SetValue(m, profile);
    mappingType.GetProperty("MasterUserId").SetValue(m, master);
    mappingType.GetProperty("ProfileName").SetValue(m, "test");
    mappingType.GetProperty("PinHash").SetValue(m, pin is null ? string.Empty : hash(pin));
    return m;
}

var mappings = (System.Collections.IList)cfgType.GetProperty("Mappings").GetValue(config);
mappings.Add(Mapping(MASTER, MASTER, "9999"));   // a master: maps to itself
mappings.Add(Mapping(LONER, LONER, "5555"));    // master-shaped, but owns no profiles
mappings.Add(Mapping(KID, MASTER, "4821"));
mappings.Add(Mapping(GUEST, MASTER, null));

// The names a household typed, which a sign-in screen shows and a client sends back. System usernames stay Bardkids / Bardguest. Set by identity, not list position: indexing renamed the wrong rows when a fixture was added above them.
void NameIt(Guid profileId, string name)
{
    foreach (var m in mappings)
    {
        if ((Guid)mappingType.GetProperty("ProfileUserId").GetValue(m) == profileId)
        {
            mappingType.GetProperty("ProfileName").SetValue(m, name);
            return;
        }
    }

    throw new InvalidOperationException("no mapping for " + profileId);
}

NameIt(MASTER, "bard");
NameIt(KID, "kids");
NameIt(GUEST, "guest");

// A PIN only works on a device the household has signed in on with its password (R-1344),
// so everything below that is not about devices runs on the family television.
var devicesList = (System.Collections.IList)cfgType.GetProperty("KnownDevices").GetValue(config);
var knownType = asm.GetType("Jellyfin.Profiles.Configuration.KnownDevice", true);
var requestDevice = asm.GetType("Jellyfin.Profiles.Auth.RequestDevice", true);
var capture = requestDevice.GetMethod("Capture", BindingFlags.Public | BindingFlags.Static, new[] { typeof(string) });
var captureAt = requestDevice.GetMethod("Capture", BindingFlags.Public | BindingFlags.Static, new[] { typeof(string), typeof(string), typeof(bool) });
devicesList.Clear();
Remember("family-tv", MASTER);
FromDevice("family-tv");

static User MakeUser(Guid id, string name)
{
    var u = new User(name, "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider",
                           "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider");
    typeof(User).GetProperty("Id").SetValue(u, id);
    return u;
}

var authWithUser = providerType.GetMethod("Authenticate", Any, null,
    new[] { typeof(string), typeof(string), typeof(User) }, null);

// Returns null on success (the username it resolved to), or the exception type name.
string Try(User user, string password)
{
    try
    {
        var task = authWithUser.Invoke(provider, new object[] { user?.Username ?? "x", password, user });
        var result = task.GetType().GetProperty("Result").GetValue(task);
        return (string)result.GetType().GetProperty("Username").GetValue(result);
    }
    catch (TargetInvocationException ex)
    {
        return "!" + ex.InnerException.GetType().Name;
    }
}

Console.WriteLine();
Console.WriteLine("── It refuses everything that is not a sub-profile ────────────");

Ok("a null resolved user is refused", Try(null, "4821") == "!AuthenticationException");
Ok("an account Bonfire has never heard of is refused",
   Try(MakeUser(OUTSID, "someone-else"), "4821") == "!AuthenticationException");

// This used to expect a refusal, on the grounds that a master administers the server. That holds for one account per server; a forty-household server has one admin among forty masters. Allowing administrators is deliberate, with a warning under the PIN field.
//
// A master keeps its real password. Jellyfin binds a user to exactly one provider (a set AuthenticationProviderId filters the list to that type), so a master bound to this provider would lose password sign-in everywhere. The provider delegates to Jellyfin's own to keep both, so a bug in the PIN path cannot lock anybody out.
Ok("a master with a PIN opens with that PIN",
   Try(MakeUser(MASTER, "bard"), "9999") == "bard");

Ok("and the wrong PIN does not open it",
   Try(MakeUser(MASTER, "bard"), "1111") == "!AuthenticationException");

// The stub stands in for Jellyfin's provider and accepts one password; this fails if the PIN path swallows the credential instead of passing it on.
Ok("a master's real password still works, through Jellyfin's own provider",
   Try(MakeUser(MASTER, "bard"), "the-real-password") == "bard");

Ok("and a wrong password is still refused",
   Try(MakeUser(MASTER, "bard"), "not-the-password") == "!AuthenticationException");

// An account with a self-row but no profiles pointing at it is not a household, and must
// not become a PIN-openable login because of a stray row.
Ok("an account with a PIN but no profiles of its own is refused",
   Try(MakeUser(LONER, "loner"), "5555") == "!AuthenticationException");

Console.WriteLine();
Console.WriteLine("── And accepts exactly one thing ──────────────────────────────");

Ok("a sub-profile opens with its PIN", Try(MakeUser(KID, "Bardkids"), "4821") == "Bardkids");
Ok("and the wrong PIN is refused", Try(MakeUser(KID, "Bardkids"), "4822") == "!AuthenticationException");
Ok("an empty PIN does not open a PIN-protected profile",
   Try(MakeUser(KID, "Bardkids"), "") == "!AuthenticationException");

// ── a PIN-less profile, and the device it is being opened from ──────────────
//
// This used to be "no PIN opens with an empty box", which came from the web switcher where the master is already signed in. A client sign-in screen has no prior authentication, and anything that can POST /Users/AuthenticateByName reaches this provider. Sub-profile usernames are predictable (`<master>_<profile>`) and published by the /Users/Public injection, so on an internet-facing server one guessed username opened a PIN-less profile. Found on a live server.
//
// Kept: type nothing, get in, on a device the household signed in on. Refused elsewhere.
Ok("the provider can see which device a request came from", capture != null);

// ── an app that gets the profile picker ─────────────────────────────────────
//
// On an app ticked for the picker the household signs in as the main account and picks there. A profile signed in directly would skip the keypad and leave a profile token the app keeps (the Roku problem, #40), so it is refused on that app only.
var captureClient = requestDevice.GetMethod("Capture", BindingFlags.Public | BindingFlags.Static, new[] { typeof(string), typeof(string) });
var proxyProp = cfgType.GetProperty("ProxyClients");
Ok("the provider can see which app a request came from", captureClient != null && proxyProp != null);
if (captureClient != null && proxyProp != null)
{
    proxyProp.SetValue(config, new List<string> { "moonfin" });
    captureClient.Invoke(null, new object[] { "family-tv", "Moonfin for Android TV" });
    Ok("a profile's PIN is refused on an app that gets the picker",
       Try(MakeUser(KID, "Bardkids"), "4821") == "!AuthenticationException");
    Ok("while the main account still signs in there with its own PIN",
       Try(MakeUser(MASTER, "bard"), "9999") == "bard");
    captureClient.Invoke(null, new object[] { "family-tv", "Jellyfin Android TV" });
    Ok("and the profile still opens on any other app", Try(MakeUser(KID, "Bardkids"), "4821") == "Bardkids");
    proxyProp.SetValue(config, new List<string>());
    captureClient.Invoke(null, new object[] { "family-tv", "Moonfin for Android TV" });
    Ok("and on Moonfin too once it is unticked", Try(MakeUser(KID, "Bardkids"), "4821") == "Bardkids");
    captureClient.Invoke(null, new object[] { "family-tv", null });
}

void Remember(string deviceId, Guid owner)
{
    var d = Activator.CreateInstance(knownType);
    knownType.GetProperty("DeviceId").SetValue(d, deviceId);
    knownType.GetProperty("MasterUserId").SetValue(d, owner);
    knownType.GetProperty("LastSeen").SetValue(d, DateTime.UtcNow);
    devicesList.Add(d);
}

// A request is at home unless a case says otherwise. Where the caller is belongs to the middleware (R-1334); a build without that records nothing, so only the 1-argument form exists.
void FromDevice(string deviceId, bool home = true)
{
    if (captureAt != null) captureAt.Invoke(null, new object[] { deviceId, null, home });
    else capture.Invoke(null, new object[] { deviceId });
}

devicesList.Clear();
Remember("family-tv", MASTER);

FromDevice("family-tv");
Ok("a profile with no PIN opens with an empty box on a device the household uses",
   Try(MakeUser(GUEST, "Bardguest"), "") == "Bardguest");

// "No PIN" must not quietly become "any PIN" — somebody testing whether the box does
// anything would otherwise be let in.
Ok("but a typed value is still refused, even on that device",
   Try(MakeUser(GUEST, "Bardguest"), "1234") == "!AuthenticationException");

// A PIN-less profile, or one with a device list, rests on a device id the client supplies. "PIN login on other apps" let it open from any network for anyone who had seen a household's id (R-1334). It now opens from the home network only, unless the administrator allows anywhere. A PIN stays a credential: a profile with one and no list is not asked where it is.
var fromAnywhere = cfgType.GetProperty("ClientPinLoginFromAnywhere");
void Anywhere(bool on) => fromAnywhere?.SetValue(config, on);
var kidRowHere = mappings.Cast<object>().First(m => (Guid)mappingType.GetProperty("ProfileUserId").GetValue(m) == KID);
var kidListHere = (System.Collections.IList)mappingType.GetProperty("AllowedDeviceIds").GetValue(kidRowHere);

Ok("the setting for it exists, and is off by default",
   fromAnywhere != null && (bool)fromAnywhere.GetValue(Activator.CreateInstance(cfgType)) == false);
FromDevice("family-tv", home: false);
Ok("a PIN-less profile is refused from outside the home network, on a device the household uses",
   Try(MakeUser(GUEST, "Bardguest"), "") == "!AuthenticationException");
Ok("a profile with a PIN and no device list still opens from outside: the PIN is the credential",
   Try(MakeUser(KID, "Bardkids"), "4821") == "Bardkids");
kidListHere.Add("family-tv");
Ok("a profile with a device list is refused from outside, on a device that is on it",
   Try(MakeUser(KID, "Bardkids"), "4821") == "!AuthenticationException");
Anywhere(true);
Ok("with the setting on 'anywhere', a PIN-less profile opens from outside",
   Try(MakeUser(GUEST, "Bardguest"), "") == "Bardguest");
Ok("and so does a profile with a device list, on a device that is on it",
   Try(MakeUser(KID, "Bardkids"), "4821") == "Bardkids");
Anywhere(false);
FromDevice("family-tv", home: true);
Ok("at home, a PIN-less profile opens", Try(MakeUser(GUEST, "Bardguest"), "") == "Bardguest");
Ok("and a profile with a device list opens on a device that is on it", Try(MakeUser(KID, "Bardkids"), "4821") == "Bardkids");
kidListHere.Clear();

// The hole, closed. This is the curl-from-anywhere case.
FromDevice("some-strangers-laptop");
Ok("the same empty box is refused from a device nobody in the household has used",
   Try(MakeUser(GUEST, "Bardguest"), "") == "!AuthenticationException");

FromDevice(null);
Ok("and refused when no device id is sent at all",
   Try(MakeUser(GUEST, "Bardguest"), "") == "!AuthenticationException");

// Another household's television must not open this household's PIN-less profiles.
Remember("neighbours-tv", OUTSID);
FromDevice("neighbours-tv");
Ok("and refused from a device belonging to a different household",
   Try(MakeUser(GUEST, "Bardguest"), "") == "!AuthenticationException");

// This used to expect the opposite ("a PIN stands on its own"). With the full system name it was four digits from any address on the internet, for an administrator as much as a child. Now (R-1344, every account) a PIN works only on a device the household has signed in on with a password. The password still works anywhere.
FromDevice("some-strangers-laptop");
Ok("a profile's PIN does not open it from a device the household has never used",
   Try(MakeUser(KID, "Bardkids"), "4821") == "!AuthenticationException");
Ok("nor from no device at all", ((Func<string>)(() => { FromDevice(null); return Try(MakeUser(KID, "Bardkids"), "4821"); }))() == "!AuthenticationException");
FromDevice("neighbours-tv");
Ok("nor from another household's television", Try(MakeUser(KID, "Bardkids"), "4821") == "!AuthenticationException");
FromDevice("some-strangers-laptop");
Ok("a main account's PIN does not open it from there either",
   Try(MakeUser(MASTER, "bard"), "9999") == "!AuthenticationException");
Ok("while its password still does", Try(MakeUser(MASTER, "bard"), DefaultAuthenticationProvider.ThePassword) == "bard");
for (int i = 0; i < 8; i++) Try(MakeUser(KID, "Bardkids"), "000" + i);
FromDevice("family-tv");
Ok("a stranger's guesses do not pause the PIN on the family television",
   Try(MakeUser(KID, "Bardkids"), "4821") == "Bardkids");
Ok("where the main account's PIN works too", Try(MakeUser(MASTER, "bard"), "9999") == "bard");

// A television two households share, the other one there last (R-1050). Its sign-in screen
// shows the neighbours' profiles, and this household's PINs still work on it.
Remember("shared-tv", OUTSID);
var ownersProp = knownType.GetProperty("Owners");
if (ownersProp != null) ownersProp.SetValue(devicesList[devicesList.Count - 1], new List<Guid> { MASTER, OUTSID });
FromDevice("shared-tv");
Ok("on a television shared with another household, a profile's PIN works",
   Try(MakeUser(KID, "Bardkids"), "4821") == "Bardkids");
Ok("and so does the main account's", Try(MakeUser(MASTER, "bard"), "9999") == "bard");
devicesList.RemoveAt(devicesList.Count - 1);
FromDevice("family-tv");

Console.WriteLine();
Console.WriteLine("── The name the household typed ───────────────────────────────");

// The sign-in screen offers "kids", so the client sends "kids" back as the username. Jellyfin cannot resolve it and hands the provider a null user; when the provider succeeds with a different username, UserManager.AuthenticateUser looks that one up instead. So the provider says which account "kids" meant.
//
// Which "kids" is decided by the DEVICE, not the name: two households can each have one, and the system usernames (Bardkids, Smithkids) keep them apart elsewhere.
var authNoUser = providerType.GetMethod("Authenticate", Any, null,
    new[] { typeof(string), typeof(string) }, null);

// The accounts the provider looks up once it has decided which profile a display name meant.
// These are the SYSTEM usernames: the household typed "kids" and the account is "Bardkids".
UserManagerStub.Add(MASTER, MakeUser(MASTER, "bard"));
UserManagerStub.Add(LONER, MakeUser(LONER, "loner"));
UserManagerStub.Add(KID, MakeUser(KID, "Bardkids"));
UserManagerStub.Add(GUEST, MakeUser(GUEST, "Bardguest"));

string TryName(string username, string password)
{
    try
    {
        var task = authNoUser.Invoke(provider, new object[] { username, password });
        var result = task.GetType().GetProperty("Result").GetValue(task);
        return (string)result.GetType().GetProperty("Username").GetValue(result);
    }
    catch (TargetInvocationException ex)
    {
        return "!" + ex.InnerException.GetType().Name;
    }
}

devicesList.Clear();
Remember("family-tv", MASTER);
FromDevice("family-tv");

Ok("a display name and the right PIN resolve to the real account",
   TryName("kids", "4821") == "Bardkids");

Ok("and the wrong PIN is still refused",
   TryName("kids", "4822") == "!AuthenticationException");

// A main account turned off in Dashboard -> Users shuts its profiles too (R-1135). Jellyfin
// checks the account being signed into, which is the profile's and was never turned off.
var offMaster = MakeUser(MASTER, "bard");
Jellyfin.Data.UserEntityExtensions.SetPermission(offMaster, Jellyfin.Database.Implementations.Enums.PermissionKind.IsDisabled, true);
UserManagerStub.Add(MASTER, offMaster);
Ok("with the main account turned off, the profile's PIN no longer opens it",
   Try(MakeUser(KID, "Bardkids"), "4821") == "!AuthenticationException");
Ok("nor by its display name", TryName("kids", "4821") == "!AuthenticationException");
UserManagerStub.Add(MASTER, MakeUser(MASTER, "bard"));
Ok("turned back on, it opens again", Try(MakeUser(KID, "Bardkids"), "4821") == "Bardkids");

// The full system username keeps working: Jellyfin resolves it itself and never reaches this
// path, but a client that remembers the old name must not break.
Ok("the system username still opens the profile",
   Try(MakeUser(KID, "Bardkids"), "4821") == "Bardkids");

// Without a known device there is no household, so no way to say which "kids" was meant.
FromDevice("some-strangers-laptop");
Ok("a display name from an unknown device is refused",
   TryName("kids", "4821") == "!AuthenticationException");

FromDevice(null);
Ok("and refused when no device id is sent",
   TryName("kids", "4821") == "!AuthenticationException");

// A name in a household that does not have it.
FromDevice("family-tv");
Ok("a display name no profile in this household has is refused",
   TryName("nobody-by-that-name", "4821") == "!AuthenticationException");

// The master is reachable by its own real name and must not become reachable by PIN through
// this path as a side effect.
Ok("the master's name does not open the master by PIN",
   TryName("bard", "9999") == "!AuthenticationException");

// A PIN-less profile through the same path still obeys the device rule.
Ok("a PIN-less profile opens by display name on a household device",
   TryName("guest", "") == "Bardguest");

devicesList.Clear();
FromDevice("family-tv");

Console.WriteLine();
Console.WriteLine("── Every refusal looks the same from outside ──────────────────");

// Different messages would enumerate which accounts on the server are Bonfire profiles.
string Message(User user, string password)
{
    try
    {
        authWithUser.Invoke(provider, new object[] { user?.Username ?? "x", password, user });
        return null;
    }
    catch (TargetInvocationException ex) { return ex.InnerException.Message; }
}

// The master's own correct PIN used to be listed as a refusal; it returned null and the check failed for the wrong reason. Replaced with LONER, a self-row with a PIN that owns no profiles.
var messages = new[]
{
    Message(MakeUser(OUTSID, "someone-else"), "4821"),
    Message(MakeUser(LONER, "loner"), "5555"),
    Message(MakeUser(KID, "Bardkids"), "4822"),
    Message(MakeUser(GUEST, "Bardguest"), "1234")
}.Distinct().ToArray();
// A master's failed PIN is refused by Jellyfin, since the provider hands the credential to DefaultAuthenticationProvider, so it reads like any ordinary failure. Kept out of the uniformity check above, which covers this provider's own refusals.
Ok("a master's failed PIN is refused by Jellyfin, so it looks like any other account",
   Message(MakeUser(MASTER, "bard"), "not-the-password") != null);

Ok("all four refusals carry one identical message", messages.Length == 1,
   messages.Length > 1 ? string.Join(" | ", messages) : null);

Console.WriteLine();
Console.WriteLine("── HasPassword cannot hide a real account's password box ──────");

// With a blank AuthenticationProviderId, GetAuthenticationProviders(user)[0] is whichever provider DI returns first, so Jellyfin may ask this method about an unrelated account. Answering false would remove a real user's password prompt.
Ok("an unknown account is reported as having a password",
   (bool)hasPassword.Invoke(provider, new object[] { MakeUser(OUTSID, "someone-else") }) == true);
Ok("a PIN-protected profile is reported as having one",
   (bool)hasPassword.Invoke(provider, new object[] { MakeUser(KID, "Bardkids") }) == true);
Ok("a profile with no PIN is reported as having none",
   (bool)hasPassword.Invoke(provider, new object[] { MakeUser(GUEST, "Bardguest") }) == false);

Console.WriteLine();
Console.WriteLine("── A PIN is not an account password ───────────────────────────");

// Jellyfin calls ChangePassword when an administrator sets a password on the account. Writing
// the PIN there would create a second place it lives, and the two would drift.
var changePassword = providerType.GetMethod("ChangePassword", Any, null,
    new[] { typeof(User), typeof(string) }, null);
string changed;
try
{
    changePassword.Invoke(provider, new object[] { MakeUser(KID, "Bardkids"), "hunter2" });
    changed = "returned";
}
catch (TargetInvocationException ex) { changed = ex.InnerException.GetType().Name; }
Ok("setting an account password on a profile is refused, not silently ignored",
   changed == "AuthenticationException", changed);

// A master's real password is Jellyfin's to change. Refusing it here left a master bound for
// its PIN unable to change its password or have an administrator reset it.
try
{
    changePassword.Invoke(provider, new object[] { MakeUser(MASTER, "bard"), "hunter2" });
    changed = "returned";
}
catch (TargetInvocationException ex) { changed = ex.InnerException.GetType().Name; }
Ok("but a master's own password change is handed to Jellyfin", changed == "returned", changed);

Console.WriteLine();
Console.WriteLine("── Guessing a PIN is limited, per account ─────────────────────");

// On the family television: anywhere else the PIN is refused before it is counted, and
// every assertion below would pass without the limit (R-1344).
devicesList.Clear();
Remember("family-tv", MASTER);
FromDevice("family-tv");

// Jellyfin's lockout never applied: LoginAttemptsBeforeLockout is null on every Bonfire account, which means never lock. A four-digit PIN had no limit, reachable by anything that can POST /Users/AuthenticateByName.
var rlType = asm.GetType("Jellyfin.Profiles.Controllers.RateLimiter", true);
// The account's one allowance (PinBudget): a quarter hour, then a day. Also read under its pre-R-1002 names so the file runs against an older build.
var limiters = new[] { ("AccountPin", "ClientPin"), ("AccountPinDaily", "ClientPinDaily") }
    .Select(n => rlType.GetField(n.Item1, Any)?.GetValue(null) ?? rlType.GetField(n.Item2, Any)?.GetValue(null))
    .ToArray();
var rlReset = rlType.GetMethod("Reset", Any);
Ok("the provider has its own PIN limits", limiters.All(l => l != null));

void ForgetAttempts(Guid id)
{
    foreach (var l in limiters.Where(l => l != null)) rlReset.Invoke(l, new object[] { id.ToString("N") });
}

ForgetAttempts(KID);
for (int i = 0; i < 5; i++) Try(MakeUser(KID, "Bardkids"), "000" + i);
Ok("after five wrong PINs, even the right one is refused for a while",
   Try(MakeUser(KID, "Bardkids"), "4821") == "!AuthenticationException");
// The lock is written down, as it is when the web switcher locks the same PIN: an app is
// the door most guessing comes through, and nobody read a log line.
Ok("an app lockout writes an Activity entry for the profile",
   ServiceStub.Activity.Count(a => a.Type == "BonfirePinLocked" && a.UserId == KID) == 1,
   string.Join(", ", ServiceStub.Activity.Select(a => a.Type)));
ForgetAttempts(KID);
Ok("and once the window has passed it opens again", Try(MakeUser(KID, "Bardkids"), "4821") == "Bardkids");

// An old unsalted hash is replaced by a salted one the first time its PIN is entered on an
// app, as it is on the web. A profile used only on a television kept the old hash for good.
{
    var OLDKID = Guid.NewGuid();
    var legacy = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes("2468"))).ToLowerInvariant();
    var row = Mapping(OLDKID, MASTER, null);
    mappingType.GetProperty("PinHash").SetValue(row, legacy);
    mappings.Add(row);
    string Stored() => (string)mappingType.GetProperty("PinHash").GetValue(row);
    Try(MakeUser(OLDKID, "Oldkid"), "1357");
    Ok("a wrong PIN leaves an old hash as it was", Stored() == legacy);
    Ok("the right PIN on an app still opens an old-hash profile", Try(MakeUser(OLDKID, "Oldkid"), "2468") == "Oldkid");
    Ok("and the stored hash is now the salted kind", Stored().StartsWith("pbkdf2.sha256$"), Stored());
    Ok("which the same PIN still opens", Try(MakeUser(OLDKID, "Oldkid"), "2468") == "Oldkid");
    ForgetAttempts(OLDKID);
}

// Checked and counted as one step. A check followed later by a count let every guess of a
// parallel burst through, because each passed the check before any failure was recorded.
ForgetAttempts(KID);
var burst = Enumerable.Range(0, 40).Select(i => Task.Run(() => Try(MakeUser(KID, "Bardkids"), (1000 + i).ToString()))).ToArray();
Task.WaitAll(burst);
Ok("forty guesses at once still use up the allowance",
   Try(MakeUser(KID, "Bardkids"), "4821") == "!AuthenticationException");
ForgetAttempts(KID);

// R-1287: the assertion above passes against a limiter that checks, lets a whole burst through and counts afterward (forty compared, forty counted, right PIN refused anyway). What matters is how many guesses were compared. The provider says when it turns one away ("too many wrong PINs"), so a counting logger gives the number: forty minus those. At most five, exactly five with forty distinct wrong PINs, may reach the hash.
{
    var counting = Activator.CreateInstance(typeof(CountingLogger<>).MakeGenericType(providerType));
    var burstProvider = Activator.CreateInstance(providerType, counting, ServiceStub.Create());
    ForgetAttempts(KID);
    var gate = new System.Threading.ManualResetEventSlim(false);
    var guesses = Enumerable.Range(0, 40).Select(i => Task.Run(() =>
    {
        gate.Wait();
        try { authWithUser.Invoke(burstProvider, new object[] { "Bardkids", (3000 + i).ToString(), MakeUser(KID, "Bardkids") }); }
        catch (TargetInvocationException) { }
    })).ToArray();
    gate.Set();
    Task.WaitAll(guesses);
    var refused = (int)counting.GetType().GetProperty("Refused").GetValue(counting);
    var compared = 40 - refused;
    Ok("of forty guesses at once, at most five reach the PIN comparison (" + compared + " did)", compared <= 5, compared + " compared");
    Ok("and the other thirty-five were turned away", refused == 35, refused + " refused");
    ForgetAttempts(KID);
}

// A guess the quarter hour refused must not spend the day (R-1001). Both were counted on every request, so twenty refused requests closed a profile's PIN for a day, renewable one request at a time.
for (int i = 0; i < 25; i++) Try(MakeUser(KID, "Bardkids"), (10000 + i).ToString());
rlReset.Invoke(limiters[0], new object[] { KID.ToString("N") });   // the quarter hour passes
Ok("refused guesses don't use up the day: five counted, and the right PIN opens after the quarter hour",
   Try(MakeUser(KID, "Bardkids"), "4821") == "Bardkids");
ForgetAttempts(KID);

// The day: twenty, however they are spread over the quarter hours.
for (int batch = 0; batch < 4; batch++)
{
    for (int i = 0; i < 5; i++) Try(MakeUser(KID, "Bardkids"), (20000 + batch * 10 + i).ToString());
    rlReset.Invoke(limiters[0], new object[] { KID.ToString("N") });
}
Ok("twenty wrong in a day close the PIN for the day", Try(MakeUser(KID, "Bardkids"), "4821") == "!AuthenticationException");
ForgetAttempts(KID);

// Something that cannot be a PIN is refused without spending an attempt on it.
for (int i = 0; i < 8; i++) Try(MakeUser(KID, "Bardkids"), "not-a-pin-" + i);
Ok("a password that is not PIN-shaped does not use up the allowance",
   Try(MakeUser(KID, "Bardkids"), "4821") == "Bardkids");

// A master's real password goes through this provider every time it signs in, on the web as
// much as anywhere. It must neither count against the PIN nor be blocked by it.
ForgetAttempts(MASTER);
for (int i = 0; i < 8; i++) Try(MakeUser(MASTER, "bard"), DefaultAuthenticationProvider.ThePassword);
Ok("a master signing in with its password does not use up its PIN allowance",
   Try(MakeUser(MASTER, "bard"), "9999") == "bard");

for (int i = 0; i < 5; i++) Try(MakeUser(MASTER, "bard"), "000" + i);
Ok("a master's PIN is limited too", Try(MakeUser(MASTER, "bard"), "9999") == "!AuthenticationException");
Ok("but its real password still works while the PIN is paused",
   Try(MakeUser(MASTER, "bard"), DefaultAuthenticationProvider.ThePassword) == "bard");
ForgetAttempts(MASTER);

Console.WriteLine();
Console.WriteLine("── A master bound here always keeps its password ──────────────");

// Jellyfin offers an account bound to this provider no other, so a refusal here is a refusal everywhere. A master that cleared its PIN or lost its last profile stayed bound, and every real-password sign-in was refused.
var DROPPED = Guid.NewGuid();
mappings.Add(Mapping(DROPPED, DROPPED, null));    // a master row with no PIN any more
User BoundUser(Guid id, string name)
{
    var u = MakeUser(id, name);
    u.AuthenticationProviderId = providerType.FullName;
    return u;
}

Ok("a bound master with no PIN still signs in with its real password",
   Try(BoundUser(DROPPED, "dropped"), DefaultAuthenticationProvider.ThePassword) == "dropped");
Ok("and a wrong password is still refused",
   Try(BoundUser(DROPPED, "dropped"), "not-the-password") == "!AuthenticationException");
Ok("an account that is not bound here is still declined outright",
   Try(MakeUser(OUTSID, "someone-else"), DefaultAuthenticationProvider.ThePassword) == "!AuthenticationException");

Console.WriteLine();
Console.WriteLine("── The emergency disable must not lock anyone out ─────────────");

// It used to report the provider disabled, and Jellyfin then gave every bound account InvalidAuthProvider, so a bound master lost its password on the day an administrator most needs to sign in.
var panicField = pluginType.GetField("_panicDisabled", Any);
// On the family television, so it is the disable that refuses the PINs below (R-1344).
devicesList.Clear();
Remember("family-tv", MASTER);
FromDevice("family-tv");
Ok("(the PINs open there with Bonfire on)", Try(MakeUser(KID, "Bardkids"), "4821") == "Bardkids" && Try(BoundUser(MASTER, "bard"), "9999") == "bard");
panicField.SetValue(null, true);
try
{
    Ok("the provider stays enabled while Bonfire is disabled", (bool)isEnabled.GetValue(provider));
    Ok("a bound master's password still works",
       Try(BoundUser(MASTER, "bard"), DefaultAuthenticationProvider.ThePassword) == "bard");
    Ok("but its PIN does not", Try(BoundUser(MASTER, "bard"), "9999") == "!AuthenticationException");
    Ok("and no sub-profile opens with its PIN", Try(MakeUser(KID, "Bardkids"), "4821") == "!AuthenticationException");
}
finally
{
    panicField.SetValue(null, false);
}
ForgetAttempts(MASTER);
ForgetAttempts(KID);

// Turned off on the Status tab (2.0): the same as the emergency code, at the PIN provider.
var bonfireOn = config.GetType().GetProperty("BonfireOn");
Ok("(the PINs open again with Bonfire on)", Try(MakeUser(KID, "Bardkids"), "4821") == "Bardkids");
bonfireOn.SetValue(config, false);
try
{
    Ok("turned off: the provider stays enabled", (bool)isEnabled.GetValue(provider));
    Ok("turned off: a bound master's password still works",
       Try(BoundUser(MASTER, "bard"), DefaultAuthenticationProvider.ThePassword) == "bard");
    Ok("turned off: but its PIN does not", Try(BoundUser(MASTER, "bard"), "9999") == "!AuthenticationException");
    Ok("turned off: and no sub-profile opens with its PIN", Try(MakeUser(KID, "Bardkids"), "4821") == "!AuthenticationException");
}
finally
{
    bonfireOn.SetValue(config, true);
}
Ok("turned back on, without a restart, the PINs open", Try(MakeUser(KID, "Bardkids"), "4821") == "Bardkids");
ForgetAttempts(MASTER);
ForgetAttempts(KID);

Console.WriteLine();
Console.WriteLine("── An empty provider id makes a user row UNREADABLE ────────────");

// The constraint 1.6.1.2 broke. Clearing AuthenticationProviderId makes Jellyfin try every provider, but EF Core validates the field when it materializes the row, on every load:
//
//   ArgumentException: The value cannot be an empty string. (Parameter 'authenticationProviderId')
//      at Jellyfin.Database.Implementations.Entities.User..ctor(...)
//      at Jellyfin.Server.Implementations.Users.UserManager.GetUsers()
//
// GetUsers() enumerates every user, so two blanked sub-profiles took down user lookup on a 40-user server: HTTP 400 on every endpoint that lists users, unfixable from the plugin because the read path throws too. It needed manual SQL. The read path tolerating empty is why this was missed.
string emptyProviderResult;
try
{
    _ = new User("probe", string.Empty, "reset");
    emptyProviderResult = "accepted";
}
catch (ArgumentException) { emptyProviderResult = "rejected"; }
Ok("Jellyfin itself refuses an empty AuthenticationProviderId", emptyProviderResult == "rejected",
   emptyProviderResult);

Console.WriteLine();
Console.WriteLine("── So the reconciliation must never write one ──────────────────");

// Drives the real ProfilesBootstrapTask against a stub user manager and records every value it writes. A source scan could not have caught the original bug: the code read correctly and wrote a value the database would not accept back.
var taskType = asm.GetType("Jellyfin.Profiles.ProfilesBootstrapTask", true);
var reconcile = taskType.GetMethod("ReconcileAuthProviders", Any);
Ok("the bootstrap task exposes its reconciliation", reconcile != null);

if (reconcile != null)
{
    var writes = new List<string>();
    var users = new Dictionary<Guid, User>
    {
        [MASTER] = MakeUser(MASTER, "bard"),
        [KID] = MakeUser(KID, "Bardkids"),
        [GUEST] = MakeUser(GUEST, "Bardguest"),
        [LONER] = MakeUser(LONER, "loner"),
    };
    var recordingManager = RecordingUserManager.Create(users, writes);

    var taskLoggerType = typeof(NullLogger<>).MakeGenericType(taskType);
    var taskLoggerProp = taskLoggerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
    var taskLogger = taskLoggerProp != null
        ? taskLoggerProp.GetValue(null)
        : Activator.CreateInstance(taskLoggerType, nonPublic: true);

    var task = Activator.CreateInstance(
        taskType, new StubPaths(tempDir), recordingManager, taskLogger);

    // Two reconciliations at once (startup and a settings save) must not interleave: an older
    // run finishing last would write what the newer one already corrected (r2-16-03).
    enableProp.SetValue(config, false);
    RecordingUserManager.DelayMs = 25;
    var racers = new[] { new System.Threading.Thread(() => reconcile.Invoke(task, null)), new System.Threading.Thread(() => reconcile.Invoke(task, null)) };
    foreach (var t in racers) t.Start();
    foreach (var t in racers) t.Join();
    RecordingUserManager.DelayMs = 0;
    Ok("two reconciliations at once run one after the other", RecordingUserManager.MaxActive == 1, RecordingUserManager.MaxActive.ToString());

    // Feature ON: profiles point at our provider, and at nothing else.
    enableProp.SetValue(config, true);
    writes.Clear();
    reconcile.Invoke(task, null);

    Ok("with the feature on, it writes something for the sub-profiles", writes.Count > 0,
       writes.Count.ToString());
    Ok("and never an empty value", writes.All(w => !string.IsNullOrEmpty(w)),
       "an empty id makes the row unreadable and takes the whole server's user lookup with it");
    Ok("it points them at Bonfire's provider",
       writes.All(w => w == providerType.FullName), string.Join(", ", writes.Distinct()));

    // A master that has set a PIN is bound too, or Jellyfin would only offer it DefaultAuthenticationProvider and the PIN could not be reached (a set AuthenticationProviderId filters to that provider, as above). The password keeps working because the provider delegates, asserted above.
    Ok("a master that set a PIN is bound too, so its PIN can be reached",
       users[MASTER].AuthenticationProviderId == providerType.FullName,
       users[MASTER].AuthenticationProviderId);

    // A self-row on an account no profiles point at is not a household. Binding it would
    // put a provider in front of an ordinary account for no reason.
    Ok("an account with a PIN but no profiles is left alone",
       users[LONER].AuthenticationProviderId
           == "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider",
       users[LONER].AuthenticationProviderId);

    // A master that clears its PIN must come off this provider at once. It stayed bound because reconciliation only visited masters that had a PIN.
    var masterRow = mappings.Cast<object>().First(m =>
        (Guid)mappingType.GetProperty("ProfileUserId").GetValue(m) == MASTER);
    var masterPin = mappingType.GetProperty("PinHash").GetValue(masterRow);
    mappingType.GetProperty("PinHash").SetValue(masterRow, string.Empty);
    reconcile.Invoke(task, null);
    Ok("a master that clears its PIN is put back on Jellyfin's provider",
       users[MASTER].AuthenticationProviderId
           == "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider",
       users[MASTER].AuthenticationProviderId);
    Ok("while its profiles stay where the feature wants them",
       users[KID].AuthenticationProviderId == providerType.FullName, users[KID].AuthenticationProviderId);
    mappingType.GetProperty("PinHash").SetValue(masterRow, masterPin);
    reconcile.Invoke(task, null);
    Ok("and bound again once it sets one", users[MASTER].AuthenticationProviderId == providerType.FullName,
       users[MASTER].AuthenticationProviderId);

    // ni-S-A1: an administrator is never bound (a removed or unloaded Bonfire would refuse its
    // real password everywhere), and one already bound is put back. Uninstall restores all.
    var ADM = Guid.NewGuid();
    var ADMKID = Guid.NewGuid();
    var admUser = MakeUser(ADM, "admin");
    Jellyfin.Data.UserEntityExtensions.SetPermission(admUser, Jellyfin.Database.Implementations.Enums.PermissionKind.IsAdministrator, true);
    users[ADM] = admUser;
    users[ADMKID] = MakeUser(ADMKID, "admin_kids");
    mappings.Add(Mapping(ADM, ADM, "1357"));
    mappings.Add(Mapping(ADMKID, ADM, null));
    reconcile.Invoke(task, null);
    Ok("an administrator master is never bound", users[ADM].AuthenticationProviderId
       == "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", users[ADM].AuthenticationProviderId);
    users[ADM].AuthenticationProviderId = providerType.FullName;
    reconcile.Invoke(task, null);
    Ok("an administrator already bound is put back on Jellyfin's provider", users[ADM].AuthenticationProviderId
       == "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", users[ADM].AuthenticationProviderId);
    Ok("its profiles stay bound", users[ADMKID].AuthenticationProviderId == providerType.FullName,
       users[ADMKID].AuthenticationProviderId);
    var restore = taskType.GetMethod("RestoreProvidersForUninstall", Any);
    Ok("the task exposes the uninstall restore", restore != null);
    if (restore != null)
    {
        Ok("(precondition) the feature is on and accounts are bound",
           users[MASTER].AuthenticationProviderId == providerType.FullName
           && users[KID].AuthenticationProviderId == providerType.FullName);
        restore.Invoke(null, null);
        var stillBound = users.Where(kv => kv.Value.AuthenticationProviderId == providerType.FullName).Select(kv => kv.Value.Username).ToList();
        Ok("uninstall puts Jellyfin's provider back on every bound account", stillBound.Count == 0, string.Join(", ", stillBound));
        reconcile.Invoke(task, null);
    }
    foreach (var m in mappings.Cast<object>().Where(m => (Guid)mappingType.GetProperty("MasterUserId").GetValue(m) == ADM).ToList()) mappings.Remove(m);
    users.Remove(ADM); users.Remove(ADMKID);

    // Delegation goes to Jellyfin's own provider, so a master that signs in another way (LDAP, SSO) must never be bound: a provider that has never heard of it would check its real password.
    const string Ldap = "Jellyfin.Plugin.LDAP_Auth.LdapAuthenticationProviderPlugin";
    var LDAPM = Guid.NewGuid();
    var LDAPKID = Guid.NewGuid();
    var ldapUser = MakeUser(LDAPM, "ldapuser");
    ldapUser.AuthenticationProviderId = Ldap;
    users[LDAPM] = ldapUser;
    users[LDAPKID] = MakeUser(LDAPKID, "ldapuser_kids");
    mappings.Add(Mapping(LDAPM, LDAPM, "2468"));
    mappings.Add(Mapping(LDAPKID, LDAPM, null));
    reconcile.Invoke(task, null);
    Ok("a master that signs in through another provider is never bound",
       users[LDAPM].AuthenticationProviderId == Ldap, users[LDAPM].AuthenticationProviderId);

    // R-1102: with client PIN off, a profile of an LDAP master was pointed at the master's provider, which has never heard of it. It goes on Jellyfin's own, as StandaloneProviderId says.
    enableProp.SetValue(config, false);
    users[LDAPKID].AuthenticationProviderId = providerType.FullName;
    reconcile.Invoke(task, null);
    Ok("with the feature off, a profile of an LDAP master goes to Jellyfin's own provider, not LDAP's",
       users[LDAPKID].AuthenticationProviderId
           == "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider",
       users[LDAPKID].AuthenticationProviderId);
    enableProp.SetValue(config, true);
    reconcile.Invoke(task, null);

    // R-1103: an exception out of the reconciliation at startup aborted Jellyfin's host
    // start, so a bad row meant a server that would not boot.
    mappings.Add(null);
    string startThrew = null;
    try { ((System.Threading.Tasks.Task)taskType.GetMethod("StartAsync").Invoke(task, new object[] { System.Threading.CancellationToken.None })).GetAwaiter().GetResult(); }
    catch (Exception e) { startThrew = (e.InnerException ?? e).GetType().Name; }
    mappings.Remove(null);
    Ok("a reconciliation that throws does not stop the server starting", startThrew == null, startThrew);

    // Out of the fixture again: the end-state checks below expect Jellyfin's own
    // provider everywhere.
    foreach (var id in new[] { LDAPM, LDAPKID })
    {
        users.Remove(id);
        var row = mappings.Cast<object>().First(m => (Guid)mappingType.GetProperty("ProfileUserId").GetValue(m) == id);
        mappings.Remove(row);
    }

    // Feature OFF: they go back to whatever the master uses, read from the master so an upstream rename cannot strand them.
    enableProp.SetValue(config, false);
    users[KID].AuthenticationProviderId = providerType.FullName;
    users[GUEST].AuthenticationProviderId = providerType.FullName;
    writes.Clear();
    reconcile.Invoke(task, null);

    Ok("with the feature off, it puts them back", writes.Count > 0, writes.Count.ToString());
    Ok("still never an empty value", writes.All(w => !string.IsNullOrEmpty(w)));
    Ok("and back to the master's own provider",
       writes.All(w => w == users[MASTER].AuthenticationProviderId),
       string.Join(", ", writes.Distinct()));

    // Nobody may be left pointing at this provider once it is switched off. A disabled provider is filtered out of GetAuthenticationProviders, so a bound account matches nothing, gets InvalidAuthProvider and cannot sign in.
    //
    // Ordering hazard: sub-profiles are restored from their master's current provider, and a bound master reads as this one, so restoring a sub-profile first copies the id being retired. Asserted on the end state, not the order.
    Ok("no account is left on Bonfire's provider once the feature is off",
       users.Values.All(u => u.AuthenticationProviderId != providerType.FullName),
       string.Join(", ", users.Values
           .Where(u => u.AuthenticationProviderId == providerType.FullName)
           .Select(u => u.Username)));

    Ok("and every one of them is back on Jellyfin's own",
       users.Values.All(u => u.AuthenticationProviderId
           == "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider"),
       string.Join(" | ", users.Values.Select(u => u.Username + "=" + u.AuthenticationProviderId)));

    // This assertion was vacuous once masters were bound: it ran after the feature was off and everything restored, so it passed whether or not a master was ever bound. The state is now asserted while the feature is ON, above.
    //
    // Restoring a master goes to Jellyfin's own provider, not MasterProviderId, which reads back the provider it is bound to now (this one) and would pin it there.
    Ok("switching the feature off puts the master back on Jellyfin's provider",
       users[MASTER].AuthenticationProviderId
           == "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider",
       users[MASTER].AuthenticationProviderId);

    // Accounts bound here that the configuration no longer knows about (R-1318). Jellyfin replaces an unreadable PluginConfiguration.xml with defaults and a rollback drops what an older build can't read: the mappings are gone, the accounts are still bound, and with the feature off they are offered no provider, so a main account's real password stopped working.
    var ORPHANM = Guid.NewGuid();
    var ORPHANKID = Guid.NewGuid();
    users[ORPHANM] = MakeUser(ORPHANM, "forgotten");
    users[ORPHANM].AuthenticationProviderId = providerType.FullName;
    users[ORPHANKID] = MakeUser(ORPHANKID, "forgotten_kids");
    users[ORPHANKID].AuthenticationProviderId = providerType.FullName;
    reconcile.Invoke(task, null);
    Ok("a main account bound here that no household lists any more is put back on Jellyfin's",
       users[ORPHANM].AuthenticationProviderId == "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider",
       users[ORPHANM].AuthenticationProviderId);
    Ok("and so is a forgotten profile", users[ORPHANKID].AuthenticationProviderId
       == "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", users[ORPHANKID].AuthenticationProviderId);

    // The same with the configuration reset to its defaults: no mappings, feature off.
    var saved = mappings.Cast<object>().ToList();
    mappings.Clear();
    enableProp.SetValue(config, false);
    users[ORPHANM].AuthenticationProviderId = providerType.FullName;
    users[MASTER].AuthenticationProviderId = providerType.FullName;
    reconcile.Invoke(task, null);
    Ok("after a reset to defaults, no account is left on a provider that is switched off",
       users.Values.All(u => u.AuthenticationProviderId != providerType.FullName),
       string.Join(", ", users.Values.Where(u => u.AuthenticationProviderId == providerType.FullName).Select(u => u.Username)));
    foreach (var row in saved) mappings.Add(row);
    users.Remove(ORPHANM);
    users.Remove(ORPHANKID);

    enableProp.SetValue(config, true);
}

Console.WriteLine();
Console.WriteLine("── A profile limited to certain devices ───────────────────────");

// Device restrictions were enforced in the switcher and verify-pin but nowhere on the sign-in-screen path, so a profile limited to the living room television opened with its PIN from any phone on the internet.
devicesList.Clear();
Remember("family-tv", MASTER);
knownType.GetProperty("DeviceName").SetValue(devicesList[0], "Living Room TV");

var kidMapping = mappings.Cast<object>().First(m =>
    (Guid)mappingType.GetProperty("ProfileUserId").GetValue(m) == KID);
var allowed = (System.Collections.IList)mappingType.GetProperty("AllowedDeviceIds").GetValue(kidMapping);

allowed.Clear();
allowed.Add("family-tv");

FromDevice("family-tv");
Ok("the allowed device opens the profile with its PIN",
   Try(MakeUser(KID, "Bardkids"), "4821") == "Bardkids");

// A household device, so it is the list that refuses it: a stranger's laptop is refused
// before the list is read (R-1344) and would pass this without testing it.
Remember("bedroom-tv", MASTER);
FromDevice("bedroom-tv");
Ok("a household device not on the list is refused, correct PIN and all",
   Try(MakeUser(KID, "Bardkids"), "4821") == "!AuthenticationException");

FromDevice(null);
Ok("and a client that sends no device id is refused",
   Try(MakeUser(KID, "Bardkids"), "4821") == "!AuthenticationException");

// The refusal must look like every other one, or the response says which profiles carry
// device restrictions.
FromDevice("some-strangers-laptop");
Ok("and it looks like every other refusal",
   Message(MakeUser(KID, "Bardkids"), "4821")
       == Message(MakeUser(OUTSID, "someone-else"), "4821"));

// An empty list has always meant "any device". Removing the last entry must not quietly
// become a lockout.
allowed.Clear();
FromDevice("bedroom-tv");
Ok("with no list at all, any of the household's devices opens it again",
   Try(MakeUser(KID, "Bardkids"), "4821") == "Bardkids");

devicesList.Clear();
FromDevice("family-tv");

Console.WriteLine();
Console.WriteLine("── Quick Connect, after the first sign-in ─────────────────────");

// Android TV opens its sign-in screen on Quick Connect and cannot be told not to: ARG_SKIP_QUICKCONNECT exists in UserLoginFragment and no caller sets it. The only lever is the response to POST /QuickConnect/Initiate: 401 sends the client to its credentials form.
//
// It cannot be decided per profile. initiateQuickConnect() takes no arguments and the client randomizes the device id for it (forUser(UUID.randomUUID())), so the request carries no user. The device name is not randomized, so this matches on that.
//
// Not the client name: the test app reports "Jellyfin Android TV" while current source builds "Jellyfin for Android TV".
//
// The rule as asked for: leave Quick Connect alone the first time, because there is no profile list before anyone signs in; afterward go straight to the PIN. "This account's first time" is not observable (the request has no account), so it is "a device we have never seen a household sign in on".
var gateType = asm.GetType("Jellyfin.Profiles.Auth.QuickConnectGate", false);
Ok("there is a gate for Quick Connect", gateType != null);

var shouldDeny = gateType?.GetMethod("ShouldDeny", BindingFlags.Public | BindingFlags.Static);
Ok("and it can be asked about one request", shouldDeny != null);

if (shouldDeny != null)
{
    // Defaulted to the client that actually needs this, so every rule below still reads as
    // a statement about the device rather than about the app.
    bool Deny(string deviceName, string client = "Jellyfin Android TV") =>
        (bool)shouldDeny.Invoke(null, new object[] { config, deviceName, client });

    var skipProp = cfgType.GetProperty("SkipQuickConnectOnKnownDevices");
    Ok("the setting exists", skipProp != null);

    devicesList.Clear();
    Remember("family-tv", MASTER);
    knownType.GetProperty("DeviceName").SetValue(devicesList[0], "Living Room TV");

    // Off is off. Nothing about this feature may change behaviour until it is turned on.
    skipProp.SetValue(config, false);
    Ok("while the setting is off, Quick Connect is never touched", !Deny("Living Room TV"));

    skipProp.SetValue(config, true);

    // Rule 2: a device a household has signed in on goes straight to the PIN screen.
    Ok("a device a household has used is sent to the PIN screen", Deny("Living Room TV"));

    // Rule 1 and 3: the first time, Quick Connect is left alone, because the profile list
    // does not exist yet and this is how somebody gets in at all.
    Ok("a device nobody has signed in on keeps Quick Connect", !Deny("Brand New TV"));

    // A record with no owner names no household, so it cannot count as "somebody has
    // signed in here".
    devicesList.Clear();
    Remember("unowned-tv", Guid.Empty);
    knownType.GetProperty("DeviceName").SetValue(devicesList[0], "Orphan TV");
    Ok("a device record with no owner keeps Quick Connect", !Deny("Orphan TV"));

    // Names arrive from a header and go out through Jellyfin's session, so neither casing
    // nor stray spaces may decide this.
    devicesList.Clear();
    Remember("case-tv", MASTER);
    knownType.GetProperty("DeviceName").SetValue(devicesList[0], "Living Room TV");
    Ok("the name is matched without regard to case", Deny("living room tv"));
    Ok("and without regard to surrounding space", Deny("  Living Room TV  "));

    // Nothing to match on is not a match. A device that sends no name must keep the easy
    // way in rather than be locked out of both.
    Ok("a request with no device name keeps Quick Connect", !Deny(""));
    Ok("and so does one with a null name", !Deny(null));

    // A blank stored name must not match every nameless request.
    devicesList.Clear();
    Remember("nameless-tv", MASTER);
    knownType.GetProperty("DeviceName").SetValue(devicesList[0], string.Empty);
    Ok("a stored record with no name matches nothing", !Deny(""));

    // ── only the client that needs it ──────────────────────────────────────────
    //
    // Refusing Quick Connect exists to reach a password field the app is standing in front of. Other clients have their own: Roku's Quick Connect is a button beside the password box, Swiftfin focuses the password field, Findroid and Wholphin open on a credentials form. Denying theirs fixes nothing.
    //
    // The gate used to match the device name alone, so turning this on for a television broke Quick Connect on a Roku on the same network once both were recorded. Paired with the positive case above.
    devicesList.Clear();
    Remember("shared-tv", MASTER);
    knownType.GetProperty("DeviceName").SetValue(devicesList[0], "Living Room TV");

    Ok("Android TV, which opens on Quick Connect, is still sent to the PIN screen",
       Deny("Living Room TV", "Jellyfin Android TV"));
    Ok("and so is the other spelling of it, which is why this is a substring match",
       Deny("Living Room TV", "Jellyfin for Android TV"));

    Ok("Roku keeps its Quick Connect button", !Deny("Living Room TV", "Jellyfin Roku"));
    Ok("Swiftfin keeps Quick Connect", !Deny("Living Room TV", "Swiftfin tvOS"));
    Ok("Findroid keeps Quick Connect", !Deny("Living Room TV", "Findroid"));
    Ok("Wholphin keeps Quick Connect", !Deny("Living Room TV", "Wholphin"));

    // An unknown client keeps it too. This setting can only take something away, so the
    // uncertain answer is the one that leaves an app as its authors built it.
    Ok("an unrecognised client keeps Quick Connect", !Deny("Living Room TV", "Some New App"));
    Ok("and a request naming no client keeps it", !Deny("Living Room TV", ""));
    Ok("and so does one with a null client", !Deny("Living Room TV", null));

    // The phone app is a web-client wrapper and gets the full switcher, so it must not be
    // caught by a loose match on "Android".
    Ok("the Android phone app is not mistaken for Android TV",
       !Deny("Living Room TV", "Jellyfin for Android"));

    // ── other people's televisions of the same model (1.6.3.3) ─────────────────
    //
    // Android TV reports its model as its name, so every set of one make shares it. Until 1.6.3.3 a household's "SHIELD Android TV" took Quick Connect from every other SHIELD on the server, including people who never use Bonfire. Now a sign-in by an account outside every household notes the name, and a noted name keeps Quick Connect for everybody.
    var sharedProp = cfgType.GetProperty("QuickConnectSharedDeviceNames");
    var note = gateType.GetMethod("NoteSharedName", BindingFlags.Public | BindingFlags.Static);
    Ok("there is a record of television names used outside every household", sharedProp != null && note != null);

    if (sharedProp != null && note != null)
    {
        bool Note(string name, string client) => (bool)note.Invoke(null, new object[] { config, name, client });
        var shared = (System.Collections.IList)sharedProp.GetValue(config);

        devicesList.Clear();
        shared.Clear();
        Remember("household-shield", MASTER);
        knownType.GetProperty("DeviceName").SetValue(devicesList[0], "SHIELD Android TV");
        Ok("a household's own television goes to the PIN screen", Deny("SHIELD Android TV"));

        Ok("somebody outside every household signing in on a set of that name is noted",
           Note("SHIELD Android TV", "Jellyfin Android TV"));
        Ok("after which nobody's set of that name loses Quick Connect", !Deny("SHIELD Android TV"));
        Ok("matched without regard to case", !Deny("shield android tv"));
        Ok("and noting the same name again changes nothing to save",
           !Note("  shield ANDROID tv ", "Jellyfin for Android TV"));

        Remember("household-kitchen", MASTER);
        knownType.GetProperty("DeviceName").SetValue(devicesList[1], "Kitchen TV");
        Ok("a name nobody else uses still goes to the PIN screen", Deny("Kitchen TV"));

        Ok("only the client this applies to is noted", !Note("Bedroom Roku", "Jellyfin Roku"));
        Ok("never a blank name", !Note("   ", "Jellyfin Android TV") && !Note(null, "Jellyfin Android TV"));

        // A name that no household device carries is not recorded: it cannot change the gate,
        // and each one would be a configuration save for any account to cause (r2-06-04).
        var before = shared.Count;
        for (int i = 0; i < 50; i++) Note("Invented " + i, "Jellyfin Android TV");
        Ok("names no household device has are not recorded (" + (shared.Count - before) + " added)", shared.Count == before);

        for (int i = 0; i < 600; i++)
        {
            Remember("household-set-" + i, MASTER);
            knownType.GetProperty("DeviceName").SetValue(devicesList[devicesList.Count - 1], "Set " + i);
        }

        for (int i = 0; i < 600; i++) Note("Set " + i, "Jellyfin Android TV");
        Ok("and the record is bounded (" + shared.Count + " names)", shared.Count > 100 && shared.Count <= 500);

        // The listener is what calls it, for the sign-ins it otherwise ignores. Driving the
        // listener needs a Jellyfin session manager; this pins the call to the right branch.
        var listenerSrc = File.ReadAllText(Path.Combine(RepoRoot(), "Auth", "BonfireSessionListener.cs"));
        var branch = listenerSrc.IndexOf("if (master == Guid.Empty)", StringComparison.Ordinal);
        var noted = listenerSrc.IndexOf("NoteSharedNameAndSave", StringComparison.Ordinal);
        // RecordSightingAndSave since 48d74b7. A missing anchor is its own failure, so a rename
        // reads as a rename and not as the call having moved.
        var recorded = listenerSrc.IndexOf("DeviceRegistry.RecordSightingAndSave", StringComparison.Ordinal);
        Ok("the household branch's device record is still found in the listener", recorded > 0);
        Ok("the sign-in listener notes names for accounts outside every household, and only there",
           branch > 0 && noted > branch && noted < recorded);

        shared.Clear();
    }

    // ── an account with no password to type (SSO) ──────────────────────────────
    //
    // The refusal sends the app to its password field, and an SSO account has none: its AuthenticationProviderId names no installed provider, so SSO works only through the web UI or Quick Connect. The request names no account, so the household that owns the matching device is asked. Judged by the provider, never by a plugin's name.
    var denyFor = gateType.GetMethod("ShouldDenyFor", BindingFlags.Public | BindingFlags.Static);
    var noPath = gateType.GetMethod("HasNoPasswordPath", BindingFlags.Public | BindingFlags.Static);
    Ok("the gate can be told which accounts have no password", denyFor != null && noPath != null);

    if (denyFor != null && noPath != null)
    {
        const string Default = "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider";
        const string Ldap = "Jellyfin.Plugin.LDAP_Auth.LDAPAuthenticationProviderPlugin";
        var enabled = new[] { Default, Ldap };

        var ssoMaster = Guid.NewGuid();
        var ldapMaster = Guid.NewGuid();
        var localMaster = Guid.NewGuid();
        var blankMaster = Guid.NewGuid();
        var accounts = new Dictionary<Guid, User>
        {
            [ssoMaster] = new User("sso-mum", "Jellyfin.Plugin.SSO_Auth.Api.SSOController", "reset"),
            [ldapMaster] = new User("ldap-dad", Ldap, "reset"),
            [localMaster] = new User("local", Default, "reset"),
            [blankMaster] = new User("blank", Default, "reset"),
        };
        accounts[blankMaster].AuthenticationProviderId = string.Empty;
        var users = ProviderUserManager.Create(accounts, enabled);

        bool NoPath(Guid id) => (bool)noPath.Invoke(null, new object[] { users, id });
        Ok("an account bound to a provider nobody has installed has no password path", NoPath(ssoMaster));
        Ok("an LDAP account does: its provider is installed", !NoPath(ldapMaster));
        Ok("a local account does", !NoPath(localMaster));
        Ok("an account bound to nothing is tried against every provider, so it does", !NoPath(blankMaster));
        Ok("an account that cannot be found is not judged to have one", NoPath(Guid.NewGuid()));

        bool DenyHousehold(Guid master)
        {
            devicesList.Clear();
            Remember("hh-tv", master);
            knownType.GetProperty("DeviceName").SetValue(devicesList[0], "Hall TV");
            Func<Guid, bool> p = id => NoPath(id);
            return (bool)denyFor.Invoke(null, new object[] { config, "Hall TV", "Jellyfin Android TV", p });
        }

        Ok("an SSO household's television keeps Quick Connect", !DenyHousehold(ssoMaster));
        Ok("an LDAP household's television is still sent to the PIN screen", DenyHousehold(ldapMaster));
        Ok("and so is a local one", DenyHousehold(localMaster));

        // Two households on one model of set: the one with no password keeps it for both.
        devicesList.Clear();
        Remember("hh-a", localMaster);
        Remember("hh-b", ssoMaster);
        knownType.GetProperty("DeviceName").SetValue(devicesList[0], "Hall TV");
        knownType.GetProperty("DeviceName").SetValue(devicesList[1], "Hall TV");
        Func<Guid, bool> q = id => NoPath(id);
        Ok("when one household on the name has no password, the name keeps Quick Connect",
           !(bool)denyFor.Invoke(null, new object[] { config, "Hall TV", "Jellyfin Android TV", q }));
        Ok("told nothing, it behaves exactly as before",
           (bool)denyFor.Invoke(null, new object[] { config, "Hall TV", "Jellyfin Android TV", null }));

        var mwSrc = File.ReadAllText(Path.Combine(RepoRoot(), "Injection", "ProfilesIndexMiddleware.cs"));
        Ok("the middleware asks, rather than calling the plain gate",
           mwSrc.Contains("QuickConnectGate.ShouldDenyFor(") && mwSrc.Contains("QuickConnectGate.HasNoPasswordPath("));
    }

    skipProp.SetValue(config, false);
    devicesList.Clear();
}

// ── a sign-in with no note ───────────────────────────────────────────────────
//
// The middleware starts a sign-in note only for POSTs under /Users. SSO signs in through its own endpoint (POST /sso/OID/Auth/{provider}), so it reaches the session listener with no note and must count as a password sign-in. A PIN, an empty box or a switch always starts a note.
{
    var reqDevice = asm.GetType("Jellyfin.Profiles.Auth.RequestDevice", false);
    var may = reqDevice?.GetMethod("MayMakeDeviceKnown", BindingFlags.Public | BindingFlags.Static);
    var begin = reqDevice?.GetMethod("BeginSignIn", BindingFlags.Public | BindingFlags.Static);
    var notByPw = reqDevice?.GetMethod("NoteNotByPassword", BindingFlags.Public | BindingFlags.Static);
    Ok("the sign-in note can be asked whether a device may be made known",
       may != null && begin != null && notByPw != null);

    if (may != null && begin != null && notByPw != null)
    {
        // Each case on its own thread: the note is an AsyncLocal, and the harness's own
        // flow must not carry one into the next case.
        bool InFreshFlow(Func<bool> body)
        {
            bool result = false;
            var t = new System.Threading.Thread(() => result = body());
            t.Start();
            t.Join();
            return result;
        }

        Ok("a sign-in with no note at all (SSO's own endpoint) may make a device known",
           InFreshFlow(() => (bool)may.Invoke(null, null)));
        Ok("a password sign-in under /Users, noted and unmarked, may",
           InFreshFlow(() => { begin.Invoke(null, new object[] { false }); return (bool)may.Invoke(null, null); }));
        Ok("a sign-in a PIN let in may not",
           !InFreshFlow(() => { begin.Invoke(null, new object[] { false }); notByPw.Invoke(null, null); return (bool)may.Invoke(null, null); }));
        Ok("nor one that started as a switch",
           !InFreshFlow(() => { begin.Invoke(null, new object[] { true }); return (bool)may.Invoke(null, null); }));

        var listenerText = File.ReadAllText(Path.Combine(RepoRoot(), "Auth", "BonfireSessionListener.cs"));
        Ok("the session listener asks it, rather than reading the note itself",
           listenerText.Contains("RequestDevice.MayMakeDeviceKnown()") && !listenerText.Contains("SignIn?.ByPin"));
    }
}

Console.WriteLine();
Console.WriteLine("── The way Jellyfin calls it (1.6.3.3) ────────────────────────");

// Everything above calls the provider's methods directly; Jellyfin does not. This copies its dispatch from Jellyfin.Server.Implementations/Users/UserManager.cs at v10.11.5, line 852:
//
//     var authenticationResult = provider is IRequiresResolvedUser requiresResolvedUser
//         ? await requiresResolvedUser.Authenticate(username, password, resolvedUser)
//         : await provider.Authenticate(username, password);
//
// When a name does not resolve ("kids" off a sign-in screen), resolvedUser is null and the three-argument form is still called. It refused a null user, so from 1.6.1.11 to 1.6.3.2 every sub-profile chosen from a sign-in screen was refused while the section above, calling the two-argument overload, passed.
async Task<(string Username, bool Success)> AuthenticateWithProvider(
    IAuthenticationProvider p, string username, string password, User? resolvedUser)
{
    try
    {
        var authenticationResult = p is IRequiresResolvedUser requiresResolvedUser
            ? await requiresResolvedUser.Authenticate(username, password, resolvedUser).ConfigureAwait(false)
            : await p.Authenticate(username, password).ConfigureAwait(false);
        return (authenticationResult.Username, true);
    }
    catch (AuthenticationException)
    {
        return (username, false);
    }
}

(string Username, bool Success) Sign(string username, string password, User? resolved = null)
    => AuthenticateWithProvider((IAuthenticationProvider)provider, username, password, resolved).GetAwaiter().GetResult();

// A clean allowance: sections above spend wrong guesses on these accounts.
var rateLimiter = asm.GetType("Jellyfin.Profiles.Controllers.RateLimiter", true);
void ResetAllowance(Guid id)
{
    foreach (var f in new[] { "AccountPin", "AccountPinDaily", "ClientPin", "ClientPinDaily" })
    {
        var limiter = rateLimiter.GetField(f, Any)?.GetValue(null) ?? rateLimiter.GetProperty(f, Any)?.GetValue(null);
        limiter?.GetType().GetMethod("Reset", Any)?.Invoke(limiter, new object[] { id.ToString("N") });
    }
}
ResetAllowance(KID);

devicesList.Clear();
Remember("family-tv", MASTER);
FromDevice("family-tv");

var picked = Sign("kids", "4821");
Ok("choosing the \"kids\" card and typing its PIN opens the profile (" + picked.Username + ")",
   picked.Success && picked.Username == "Bardkids");
Ok("the wrong PIN is refused", !Sign("kids", "4822").Success);
ResetAllowance(KID);
var pinless = Sign("guest", "");
Ok("a profile with no PIN opens with an empty box on its household's television (" + pinless.Username + ")",
   pinless.Success && pinless.Username == "Bardguest");
Ok("a name that is no profile of this household is refused", !Sign("stranger", "4821").Success);
Ok("a master is never reached through a short name", !Sign("bard", "9999").Success);

FromDevice("some-strangers-laptop");
Ok("from a device no household has signed in on, a short name opens nothing", !Sign("kids", "4821").Success);
FromDevice(null);
Ok("nor with no device at all", !Sign("kids", "4821").Success);

Console.WriteLine();
Console.WriteLine("── What an Android TV actually sends (1.6.3.5) ─────────────────");

// The section above sends the television's plain id. Jellyfin Android TV never does: signing in by name it builds a one-off id from the typed name:
//     jellyfin.createApi(server.address, deviceInfo = defaultDeviceInfo.forUser(username))
//     // forUser(user: String) = copy(id = SHA-1("${id}+$user") as lowercase hex)
//
// 1.6.3.3 did not recognize that id, so every profile chosen from an Android TV sign-in screen was refused as "a device no household has signed in on". Replayed from that server's log.
//
// 1.6.3.4 did not either: this section then recorded the set's plain id and checked the hash against it, but a real set never causes that record. Its plain id is sent only by the sign-in screen; it signs in as sha1(id + "+" + name) and browses as sha1(id + "+" + accountId), the only two the session listener writes. The fixture is what Bonfire held for the television in that log, in the app's order: the list, then the sign-in.
static string Sha1Hex(string v) => Convert.ToHexString(
    System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(v))).ToLowerInvariant();

const string TV = "9a6dae35cc29c74f";     // the set's own id, from the log
Ok("the derivation matches the production log: sha1(\"9a6dae35cc29c74f+Family\") is the id that set signed in with",
   Sha1Hex(TV + "+Family") == "24053dca55e8da2b71665754eac6a3887ae1c6f7");

// What the session listener recorded when the master signed in on this set by name and
// then browsed: never the plain id.
devicesList.Clear();
Remember(Sha1Hex(TV + "+bard"), MASTER);
Remember(Sha1Hex(TV + "+" + MASTER), MASTER);
ResetAllowance(KID);

var registry = asm.GetType("Jellyfin.Profiles.Auth.DeviceRegistry", true);
registry.GetMethod("ForgetSignInScreens", Any)?.Invoke(null, null);

// No sign-in screen seen: nothing ties this hash to any household. Refused, as it must be.
FromDevice(Sha1Hex(TV + "+kids"));
Ok("a name-derived id from a set whose sign-in screen was never seen places no household",
   !Sign("kids", "4821").Success);

// The sign-in screen asks for the list with the plain id — through the real code path.
var injectorType = asm.GetType("Jellyfin.Profiles.Auth.PublicUserInjector", true);
var listFlag = cfgType.GetProperty("EnableClientProfileList");
var listWas = (bool)listFlag.GetValue(config);
listFlag.SetValue(config, true);
var listed = (System.Collections.Generic.IEnumerable<Guid>)injectorType.GetMethod("ResolveHousehold", Any)
    .Invoke(null, new object[] { "MediaBrowser Client=\"Jellyfin Android TV\", DeviceId=\"" + TV + "\"", null, null, config, null });
listFlag.SetValue(config, listWas);
Ok("the sign-in screen, asking with the plain id, is offered the household", listed.Contains(KID));

ResetAllowance(KID);
FromDevice(Sha1Hex(TV + "+kids"));
var fromCard = Sign("kids", "4821");
Ok("choosing the \"kids\" card on that television opens the profile (" + fromCard.Username + ")",
   fromCard.Success && fromCard.Username == "Bardkids");

FromDevice(Sha1Hex(TV + "+guest"));
var pinlessCard = Sign("guest", "");
Ok("and a profile with no PIN opens there with an empty box (" + pinlessCard.Username + ")",
   pinlessCard.Success && pinlessCard.Username == "Bardguest");

// Typing the full account name hashes that name instead, and reaches Jellyfin with a
// resolved user. A profile restricted to this television must still be let in.
var kidRow = mappings.Cast<object>().First(m => (Guid)mappingType.GetProperty("ProfileUserId").GetValue(m) == KID);
var kidAllowed = (System.Collections.IList)mappingType.GetProperty("AllowedDeviceIds").GetValue(kidRow);
kidAllowed.Clear();
kidAllowed.Add(Sha1Hex(TV + "+" + MASTER));     // the row an administrator sees for this set
ResetAllowance(KID);
FromDevice(Sha1Hex(TV + "+Bardkids"));
var typed = Sign("Bardkids", "4821", MakeUser(KID, "Bardkids"));
Ok("typing the full account name on the one television a profile is restricted to is let in",
   typed.Success);

// Nothing wider. Another set's derivation is another set, and a hash of a different name
// than the one sent is not this sign-in.
ResetAllowance(KID);
FromDevice(Sha1Hex("0123456789abcdef" + "+Bardkids"));
Ok("the same name typed on a television nobody has signed in on is still refused",
   !Sign("Bardkids", "4821", MakeUser(KID, "Bardkids")).Success);
kidAllowed.Clear();

FromDevice(Sha1Hex("0123456789abcdef" + "+kids"));
Ok("a short name from a television no household uses still opens nothing", !Sign("kids", "4821").Success);
FromDevice(Sha1Hex(TV + "+guest"));
Ok("an id hashed from one name does not admit another", !Sign("kids", "4821").Success);

Console.WriteLine();
Console.WriteLine("── Read under the lock, not while sign-ins write (1.6.3.3) ────");

// The provider walked Mappings and KnownDevices unlocked, and a television signing in is when the session listener appends a device row: "collection was modified", which Jellyfin does not catch from a provider, so a refusal became a 500. A race cannot be reproduced on demand, so this asks what it reduces to: does a sign-in walk either list without holding ConfigLock? Tripwire<T> counts those walks. (LINQ's span fast path is only for an exact List<T>, so a subclass is enumerated.)
var lockObj = asm.GetType("Jellyfin.Profiles.Controllers.ProfilesBaseController", true)
    .GetField("ConfigLock", Any)?.GetValue(null);
Ok("there is a configuration lock to hold", lockObj != null);

var mappingsProp = cfgType.GetProperty("Mappings");
var knownProp = cfgType.GetProperty("KnownDevices");
var liveMappings = mappingsProp.GetValue(config);
var liveDevices = knownProp.GetValue(config);
var mapTrip = (System.Collections.IList)Activator.CreateInstance(typeof(Tripwire<>).MakeGenericType(mappingType));
foreach (var m in (System.Collections.IEnumerable)liveMappings) mapTrip.Add(m);
var devTrip = (System.Collections.IList)Activator.CreateInstance(typeof(Tripwire<>).MakeGenericType(knownType));
foreach (var d in (System.Collections.IEnumerable)liveDevices) devTrip.Add(d);

try
{
    mappingsProp.SetValue(config, mapTrip);
    knownProp.SetValue(config, devTrip);
    TripwireState.Lock = lockObj;
    TripwireState.Unlocked = 0;

    FromDevice("family-tv");
    ResetAllowance(KID);
    Sign("kids", "4821");
    Sign("Bardkids", "4821", MakeUser(KID, "Bardkids"));
    Sign("Bardguest", "", MakeUser(GUEST, "Bardguest"));

    Ok("a sign-in walks the profile and device lists only while holding ConfigLock ("
       + TripwireState.Unlocked + " unlocked walks)", lockObj != null && TripwireState.Unlocked == 0);
}
finally
{
    TripwireState.Lock = null;
    mappingsProp.SetValue(config, liveMappings);
    knownProp.SetValue(config, liveDevices);
}

// ─────────────────────────────────────────────────────────────────────────────
// Added by the test audit. Each block is a break to the provider that stayed green against everything above; several were a way in rather than out.
// ─────────────────────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── Test audit: what a change to the provider could let in ──────");
bonfireOn.SetValue(config, true);
enableProp.SetValue(config, true);
Anywhere(false);
proxyProp.SetValue(config, new List<string>());
devicesList.Clear();
Remember("family-tv", MASTER);
FromDevice("family-tv");

// A main account with no PIN whose profile has one. FindMasterWithPin must use the main account's own row: matching any row of the household found the child's PIN and opened the main account from its television.
var MASTNP = Guid.NewGuid();
var KIDNP = Guid.NewGuid();
mappings.Add(Mapping(MASTNP, MASTNP, null));
mappings.Add(Mapping(KIDNP, MASTNP, "6060"));
NameIt(KIDNP, "npkid");
UserManagerStub.Add(MASTNP, MakeUser(MASTNP, "np"));
UserManagerStub.Add(KIDNP, MakeUser(KIDNP, "Npkid"));
Remember("np-tv", MASTNP);
FromDevice("np-tv");
Ok("a profile's PIN does not open a main account that has none of its own",
   Try(MakeUser(MASTNP, "np"), "6060") == "!AuthenticationException");
Ok("while the profile's PIN still opens the profile there", Try(MakeUser(KIDNP, "Npkid"), "6060") == "Npkid");

// Two households, one name. "Which kids" is the device's to say, and a lookup that did not
// stay inside the household would hand every device the first "kids" in the list.
var MASTB = Guid.NewGuid();
var KIDB = Guid.NewGuid();
mappings.Add(Mapping(MASTB, MASTB, "8888"));
mappings.Add(Mapping(KIDB, MASTB, "7777"));
NameIt(KIDB, "kids");
UserManagerStub.Add(MASTB, MakeUser(MASTB, "other"));
UserManagerStub.Add(KIDB, MakeUser(KIDB, "Otherkids"));
Remember("b-tv", MASTB);
FromDevice("b-tv");
Ok("two households that each have a \"kids\": the second household's television opens its own",
   TryName("kids", "7777") == "Otherkids");
Ok("and not with the first household's PIN", TryName("kids", "4821") == "!AuthenticationException");
FromDevice("family-tv");
ResetAllowance(KID);
Ok("while the first household's television still opens the first household's",
   TryName("kids", "4821") == "Bardkids");
Ok("and not with the second household's PIN", TryName("kids", "7777") == "!AuthenticationException");

// The sign-in by name checks the switch itself. Jellyfin only asks an enabled provider, so
// this is the second lock on the same door rather than the first.
enableProp.SetValue(config, false);
Ok("with PIN login on other apps off, a display name opens nothing", TryName("kids", "4821") == "!AuthenticationException");
enableProp.SetValue(config, true);
ResetAllowance(KID);
Ok("and opens again when it is on", TryName("kids", "4821") == "Bardkids");

// What counts as a PIN. A stored hash for something that is not four to eight digits
// cannot be written by Bonfire, but if one is there it must not open.
foreach (var (pinText, label) in new[] { ("123", "three digits"), ("123456789", "nine digits"), ("ab12", "letters") })
{
    var SHAPE = Guid.NewGuid();
    mappings.Add(Mapping(SHAPE, MASTER, pinText));
    NameIt(SHAPE, "shape");
    UserManagerStub.Add(SHAPE, MakeUser(SHAPE, "Bardshape"));
    FromDevice("family-tv");
    Ok("a profile whose stored PIN is " + label + " is not opened by it",
       Try(MakeUser(SHAPE, "Bardshape"), pinText) == "!AuthenticationException");
    mappings.RemoveAt(mappings.Count - 1);
}

// A main account's password that only looks a little like a PIN is not a PIN guess.
foreach (var notPin in new[] { "abcd", "123", "123456789" })
{
    ResetAllowance(MASTER);
    for (int i = 0; i < 6; i++) Try(MakeUser(MASTER, "bard"), notPin);
    Ok("six wrong passwords like \"" + notPin + "\" do not pause the main account's PIN",
       Try(MakeUser(MASTER, "bard"), "9999") == "bard");
}
ResetAllowance(MASTER);

Console.WriteLine();
if (fails.Count > 0)
{
    foreach (var f in fails) Console.WriteLine("   - " + f);
    Console.WriteLine(pass + " passed, " + fails.Count + " failed");
    Environment.Exit(1);
}
Console.WriteLine(pass + " passed, 0 failed");

/// <summary>Counts the provider's "too many wrong PINs" refusals: guesses that never reached the comparison.</summary>
public sealed class CountingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    private int _refused;
    public int Refused => _refused;
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
        Exception exception, Func<TState, Exception, string> formatter)
    {
        if (formatter(state, exception).Contains("too many wrong PINs", StringComparison.Ordinal))
            System.Threading.Interlocked.Increment(ref _refused);
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

// Answers GetUserById and the enabled provider list, the two things the Quick Connect gate
// asks about an account.
public class ProviderUserManager : DispatchProxy
{
    private Dictionary<Guid, User> _users;
    private string[] _providers;

    public static IUserManager Create(Dictionary<Guid, User> users, string[] providers)
    {
        var proxy = Create<IUserManager, ProviderUserManager>();
        var stub = (ProviderUserManager)(object)proxy;
        stub._users = users;
        stub._providers = providers;
        return proxy;
    }

    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        switch (targetMethod.Name)
        {
            case "GetUserById":
                return _users.TryGetValue((Guid)args[0], out var u) ? u : null;
            case "GetAuthenticationProviders":
                return _providers.Select(p => new MediaBrowser.Model.Dto.NameIdPair { Name = p, Id = p }).ToArray();
            default:
                throw new NotSupportedException("the gate called IUserManager." + targetMethod.Name);
        }
    }
}

// Records every AuthenticationProviderId the reconciliation writes. DispatchProxy, not an implementation, because IUserManager gains and loses members between patch releases (as in tests/cs/pipeline).
public class RecordingUserManager : DispatchProxy
{
    private Dictionary<Guid, User> _users;
    private List<string> _writes;

    public static int Active, MaxActive, DelayMs;

    public static IUserManager Create(Dictionary<Guid, User> users, List<string> writes)
    {
        var proxy = Create<IUserManager, RecordingUserManager>();
        var stub = (RecordingUserManager)(object)proxy;
        stub._users = users;
        stub._writes = writes;
        return proxy;
    }

    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        switch (targetMethod.Name)
        {
            case "GetUserById":
                var now = System.Threading.Interlocked.Increment(ref Active);
                lock (_users) MaxActive = Math.Max(MaxActive, now);
                if (DelayMs > 0) System.Threading.Thread.Sleep(DelayMs);
                System.Threading.Interlocked.Decrement(ref Active);
                return _users.TryGetValue((Guid)args[0], out var u) ? u : null;

            case "UpdateUserAsync":
                _writes.Add(((User)args[0]).AuthenticationProviderId);
                return Task.CompletedTask;

            case "get_Users":
                return _users.Values.ToList();

            default:
                throw new NotSupportedException(
                    "the reconciliation called IUserManager." + targetMethod.Name
                    + ", which this harness does not model");
        }
    }
}

// Just enough of a service provider and user manager to answer GetUserById. DispatchProxy because IUserManager changes between patch releases and a hand-written implementation would not compile against two of them.
public class ActivityRecorder : DispatchProxy
{
    public List<ActivityLog> Entries;

    public static MediaBrowser.Model.Activity.IActivityManager Create(List<ActivityLog> entries)
    {
        var p = DispatchProxy.Create<MediaBrowser.Model.Activity.IActivityManager, ActivityRecorder>();
        ((ActivityRecorder)(object)p).Entries = entries;
        return p;
    }

    protected override object Invoke(MethodInfo m, object[] args)
    {
        if (m.Name == "CreateAsync") { lock (Entries) Entries.Add((ActivityLog)args[0]); return Task.CompletedTask; }
        if (m.ReturnType == typeof(Task)) return Task.CompletedTask;
        return null;
    }
}

sealed class ServiceStub : IServiceProvider
{
    private readonly IUserManager _users;

    private ServiceStub(IUserManager users) { _users = users; }

    public static IServiceProvider Create() => new ServiceStub(UserManagerStub.Create());

    /// <summary>What was written to the Activity log, through the provider's own service provider.</summary>
    public static readonly List<ActivityLog> Activity = new();

    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(IUserManager)) return _users;
        if (serviceType == typeof(MediaBrowser.Model.Activity.IActivityManager))
            return ActivityRecorder.Create(Activity);

        // What the provider asks for to check a real password. Both of Jellyfin's own are here because InvalidAuthProvider refuses everything, so "whichever is not ours" would pick the wrong one about half the time.
        if (serviceType == typeof(IEnumerable<IAuthenticationProvider>))
        {
            return new IAuthenticationProvider[]
            {
                new InvalidAuthProvider(),
                new DefaultAuthenticationProvider(),
            };
        }

        return null;
    }
}

public class UserManagerStub : DispatchProxy
{
    private static readonly Dictionary<Guid, User> _known = new();

    public static void Add(Guid id, User user) => _known[id] = user;

    public static IUserManager Create() => Create<IUserManager, UserManagerStub>();

    protected override object? Invoke(MethodInfo targetMethod, object?[]? args)
    {
        if (targetMethod.Name != "GetUserById")
        {
            throw new NotSupportedException(
                "the provider called IUserManager." + targetMethod.Name
                + ", which this harness does not model");
        }

        return _known.TryGetValue((Guid)args![0]!, out var user) ? user : null;
    }
}

// Counts walks of a list made without the configuration lock held. See "Read under the
// lock". Non-generic state, because each closed Tripwire<T> would otherwise have its own.
public static class TripwireState
{
    public static object? Lock;
    public static int Unlocked;
}

public class Tripwire<T> : List<T>, IEnumerable<T>
{
    IEnumerator<T> IEnumerable<T>.GetEnumerator()
    {
        if (TripwireState.Lock != null && !System.Threading.Monitor.IsEntered(TripwireState.Lock))
        {
            TripwireState.Unlocked++;
        }

        return GetEnumerator();
    }
}

// Stands in for Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider. The
// real one is internal to the server assembly, and the provider matches on the type's
// short name precisely so this can take its place.
sealed class DefaultAuthenticationProvider : IAuthenticationProvider, IRequiresResolvedUser
{
    public const string ThePassword = "the-real-password";

    public string Name => "Default";
    public bool IsEnabled => true;

    public Task<ProviderAuthenticationResult> Authenticate(string username, string password)
        => Authenticate(username, password, null);

    public Task<ProviderAuthenticationResult> Authenticate(string username, string password, User? resolvedUser)
    {
        if (!string.Equals(password, ThePassword, StringComparison.Ordinal))
        {
            throw new AuthenticationException("Invalid username or password");
        }

        return Task.FromResult(new ProviderAuthenticationResult { Username = username });
    }

    public bool HasPassword(User user) => true;

    public Task ChangePassword(User user, string newPassword) => Task.CompletedTask;
}

// Registered alongside it in Jellyfin and refuses everything, which is why the provider
// must not pick "the one that is not ours".
sealed class InvalidAuthProvider : IAuthenticationProvider
{
    public string Name => "InvalidAuthProvider";
    public bool IsEnabled => true;

    public Task<ProviderAuthenticationResult> Authenticate(string username, string password)
        => throw new AuthenticationException("This user account cannot be used");

    public bool HasPassword(User user) => true;

    public Task ChangePassword(User user, string newPassword) => Task.CompletedTask;
}
