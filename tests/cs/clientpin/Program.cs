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
// BonfirePinAuthenticationProvider — the thing that lets a television offer a
// profile at all.
//
// This is the highest-stakes code in the plugin. It is reached during Jellyfin's
// own authentication, and it is reached for accounts that have nothing to do with
// Bonfire: a user with no AuthenticationProviderId is offered EVERY enabled
// provider in turn, and the first success wins. A wrong "yes" here is not a bug in
// a switcher, it is an authentication bypass on somebody else's account.
//
// So the tests below are mostly about refusal, and every refusal is checked
// separately rather than as "it throws for bad input".
//
// The type-shape section is not decoration either. We ship ONE net9 assembly for
// both Jellyfin 10.11 and 12.0, and the two versions do not agree on the
// interface: 12.0 removed HasPassword from IAuthenticationProvider. An implicit
// implementation survives that — it is just an extra public method nobody calls —
// while an explicit one makes the runtime look for an interface member that does
// not exist, and the type fails to load. On a server, that means no authentication
// provider, for anybody.
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

// Which build of the plugin to load. This harness's own output sits in
// bin/Release/<tfm>/, so its folder name IS the framework the csproj resolved — there is
// no second place to keep in step, and it cannot say net9.0 while the <Reference> that
// compiled it pointed at net10.0. tests/run.sh cs10 runs the whole set against net10.0.
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

// An explicit interface implementation is compiled as a private method whose name
// carries the interface's full name. Finding one means the type would fail to load on
// whichever Jellyfin version does not declare that member.
var explicitImpls = providerType
    .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
    .Where(m => m.IsFinal && m.IsVirtual && m.Name.Contains('.'))
    .Select(m => m.Name)
    .ToArray();
Ok("no member is implemented explicitly", explicitImpls.Length == 0,
   explicitImpls.Length > 0
       ? string.Join(", ", explicitImpls) + " — would not load on a Jellyfin whose interface omits it"
       : null);

// 12.0 dropped this from the interface. Keeping it as a plain public method is what lets
// 10.11 still skip the password box for a profile with no PIN.
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
var enableProp = cfgType.GetProperty("EnableClientPinLogin", Any);
Ok("the configuration has an EnableClientPinLogin flag", enableProp != null);
Ok("and it is OFF by default — this changes how the server authenticates",
   (bool)enableProp.GetValue(config) == false);

// NullLogger<T> has both a public constructor and an Instance property depending on the
// abstractions version; take whichever is there rather than assuming.
var loggerType = typeof(NullLogger<>).MakeGenericType(providerType);
var instanceProp = loggerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
var logger = instanceProp != null
    ? instanceProp.GetValue(null)
    : Activator.CreateInstance(loggerType, nonPublic: true);
Ok("a logger can be supplied to the provider", logger != null);
// Both arguments, explicitly: Activator.CreateInstance does not apply default
// parameter values, so `IServiceProvider? services = null` is not enough to satisfy it.
// The provider resolves IUserManager from this rather than taking it in the constructor,
// because Jellyfin's UserManager takes IEnumerable<IAuthenticationProvider> and asking
// for it directly would be a dependency cycle.
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
mappings.Add(Mapping(KID, MASTER, "4821"));
mappings.Add(Mapping(GUEST, MASTER, null));

// The names a household actually typed, which are what a sign-in screen now shows and
// therefore what a client sends back. The system usernames stay Bardkids / Bardguest.
mappingType.GetProperty("ProfileName").SetValue(mappings[0], "bard");
mappingType.GetProperty("ProfileName").SetValue(mappings[1], "kids");
mappingType.GetProperty("ProfileName").SetValue(mappings[2], "guest");

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

// The master maps to itself. Returning it here would let the account that owns every
// profile — and on many servers administers the whole server — be opened with a PIN,
// as a side effect of a lookup rather than as anybody's decision.
Ok("the MASTER account is refused, even with its own correct PIN",
   Try(MakeUser(MASTER, "bard"), "9999") == "!AuthenticationException");

Console.WriteLine();
Console.WriteLine("── And accepts exactly one thing ──────────────────────────────");

Ok("a sub-profile opens with its PIN", Try(MakeUser(KID, "Bardkids"), "4821") == "Bardkids");
Ok("and the wrong PIN is refused", Try(MakeUser(KID, "Bardkids"), "4822") == "!AuthenticationException");
Ok("an empty PIN does not open a PIN-protected profile",
   Try(MakeUser(KID, "Bardkids"), "") == "!AuthenticationException");

// ── a PIN-less profile, and the device it is being opened from ──────────────
//
// This used to be one line — "a profile with no PIN opens with an empty box" — and the
// provider obliged unconditionally. The reasoning came from the web switcher, where "no
// PIN" means "no extra challenge for somebody already signed in as the master", because
// the gate is only reached after the household has authenticated.
//
// A client sign-in screen has no prior authentication at all, and this provider is
// reached by anything that can POST /Users/AuthenticateByName. Sub-profile usernames are
// predictable (`<master>_<profile>`) and published by Bonfire's own /Users/Public
// injection, so on an internet-facing server a PIN-less profile was an account a stranger
// could enter by guessing one username. Logan found it by trying it.
//
// The behaviour that was wanted is kept: type nothing, get in — on a device the household
// signed in on. Everywhere else it is refused.
var devicesList = (System.Collections.IList)cfgType.GetProperty("KnownDevices").GetValue(config);
var knownType = asm.GetType("Jellyfin.Profiles.Configuration.KnownDevice", true);
var requestDevice = asm.GetType("Jellyfin.Profiles.Auth.RequestDevice", true);
var capture = requestDevice.GetMethod("Capture", BindingFlags.Public | BindingFlags.Static);

Ok("the provider can see which device a request came from", capture != null);

void Remember(string deviceId, Guid owner)
{
    var d = Activator.CreateInstance(knownType);
    knownType.GetProperty("DeviceId").SetValue(d, deviceId);
    knownType.GetProperty("MasterUserId").SetValue(d, owner);
    knownType.GetProperty("LastSeen").SetValue(d, DateTime.UtcNow);
    devicesList.Add(d);
}

void FromDevice(string deviceId) => capture.Invoke(null, new object[] { deviceId });

devicesList.Clear();
Remember("family-tv", MASTER);

FromDevice("family-tv");
Ok("a profile with no PIN opens with an empty box on a device the household uses",
   Try(MakeUser(GUEST, "Bardguest"), "") == "Bardguest");

// "No PIN" must not quietly become "any PIN" — somebody testing whether the box does
// anything would otherwise be let in.
Ok("but a typed value is still refused, even on that device",
   Try(MakeUser(GUEST, "Bardguest"), "1234") == "!AuthenticationException");

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

// A PIN is a real credential, so it stands on its own and is not device-bound. Someone
// away from home entering their PIN on a phone is the case this protects.
FromDevice("some-strangers-laptop");
Ok("a profile WITH a PIN still opens with it from any device",
   Try(MakeUser(KID, "Bardkids"), "4821") == "Bardkids");
Ok("and still refuses the wrong PIN there",
   Try(MakeUser(KID, "Bardkids"), "4822") == "!AuthenticationException");

Console.WriteLine();
Console.WriteLine("── The name the household typed ───────────────────────────────");

// The sign-in screen now offers "kids", so "kids" is what the client sends back — the
// picker puts the displayed name straight into the username field. Jellyfin cannot
// resolve it, and hands the provider a null user:
//
//     if (user is null)                                    // UserManager.AuthenticateUser
//     {
//         string updatedUsername = authResult.Username;
//         if (success && authenticationProvider is not DefaultAuthenticationProvider)
//         {
//             username = updatedUsername;                  // trust our answer
//             user = Users.FirstOrDefault(i => string.Equals(username, i.Username, ...));
//
// So the provider says which account "kids" meant, and Jellyfin looks that up instead.
//
// Which "kids" is decided by the DEVICE, not by the name: two households on one server
// can each have one, and the system usernames (Bardkids, Smithkids) are what keep them
// apart everywhere else.
var authNoUser = providerType.GetMethod("Authenticate", Any, null,
    new[] { typeof(string), typeof(string) }, null);

// The accounts the provider will look up once it has decided which profile a display
// name meant. These are the SYSTEM usernames, which is the whole point: the household
// typed "kids" and the account is "Bardkids".
UserManagerStub.Add(MASTER, MakeUser(MASTER, "bard"));
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

// The full system username keeps working — Jellyfin resolves it itself and never reaches
// this path, but a client that remembers the old name must not break.
Ok("the system username still opens the profile",
   Try(MakeUser(KID, "Bardkids"), "4821") == "Bardkids");

// Without a known device there is no household, so there is no way to say which "kids"
// was meant. Declining is the only honest answer.
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

// The master is reachable by its own real name and must not become reachable by PIN
// through this path as a side effect.
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

var messages = new[]
{
    Message(MakeUser(OUTSID, "someone-else"), "4821"),
    Message(MakeUser(MASTER, "bard"), "9999"),
    Message(MakeUser(KID, "Bardkids"), "4822"),
    Message(MakeUser(GUEST, "Bardguest"), "1234")
}.Distinct().ToArray();
Ok("all four refusals carry one identical message", messages.Length == 1,
   messages.Length > 1 ? string.Join(" | ", messages) : null);

Console.WriteLine();
Console.WriteLine("── HasPassword cannot hide a real account's password box ──────");

// With a blank AuthenticationProviderId, GetAuthenticationProviders(user)[0] is whichever
// provider DI happened to hand back first — so this method can be the one Jellyfin asks
// about an account that is nothing to do with us. Answering false would remove the
// password prompt from a real user's login.
Ok("an unknown account is reported as having a password",
   (bool)hasPassword.Invoke(provider, new object[] { MakeUser(OUTSID, "someone-else") }) == true);
Ok("a PIN-protected profile is reported as having one",
   (bool)hasPassword.Invoke(provider, new object[] { MakeUser(KID, "Bardkids") }) == true);
Ok("a profile with no PIN is reported as having none",
   (bool)hasPassword.Invoke(provider, new object[] { MakeUser(GUEST, "Bardguest") }) == false);

Console.WriteLine();
Console.WriteLine("── A PIN is not an account password ───────────────────────────");

// Jellyfin calls ChangePassword when an administrator sets a password on the account.
// Writing the PIN there would create a second place it lives, and the two would drift.
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

Console.WriteLine();
Console.WriteLine("── An empty provider id makes a user row UNREADABLE ────────────");

// This is the constraint 1.6.1.2 broke, asserted here so nobody re-derives the reasoning
// that led to breaking it. Clearing AuthenticationProviderId makes Jellyfin try every
// provider, which is genuinely what GetAuthenticationProviders does — but the same field
// is validated when EF Core materialises the row, on every single load:
//
//   ArgumentException: The value cannot be an empty string. (Parameter 'authenticationProviderId')
//      at Jellyfin.Database.Implementations.Entities.User..ctor(...)
//      at Jellyfin.Server.Implementations.Users.UserManager.GetUsers()
//
// GetUsers() enumerates EVERY user, so two blanked sub-profiles took down user lookup for
// a whole 40-user server: HTTP 400 on every endpoint that lists users, on every client,
// unfixable from inside the plugin because the read path throws too. It needed manual SQL.
//
// The read path tolerating empty is exactly why this was missed. Checking one path and
// calling the question settled is what the rules in CLAUDE.md are written against.
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

// Drives the REAL ProfilesBootstrapTask against a stub user manager and records every
// value it writes. A source scan could not have caught the original bug: the code read
// correctly and wrote a value the database would not accept back.
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
    };
    var recordingManager = RecordingUserManager.Create(users, writes);

    var taskLoggerType = typeof(NullLogger<>).MakeGenericType(taskType);
    var taskLoggerProp = taskLoggerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
    var taskLogger = taskLoggerProp != null
        ? taskLoggerProp.GetValue(null)
        : Activator.CreateInstance(taskLoggerType, nonPublic: true);

    var task = Activator.CreateInstance(
        taskType, new StubPaths(tempDir), recordingManager, taskLogger);

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

    // Feature OFF: they go back to whatever the MASTER uses — read from the master rather
    // than hardcoded, so an upstream rename cannot strand them.
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

    // The master is not a sub-profile and must never be re-pointed: it is a real account,
    // often the server's administrator.
    Ok("the master account is never touched",
       users[MASTER].AuthenticationProviderId
           == "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider");

    enableProp.SetValue(config, true);
}

Console.WriteLine();
if (fails.Count > 0)
{
    foreach (var f in fails) Console.WriteLine("   - " + f);
    Console.WriteLine(pass + " passed, " + fails.Count + " failed");
    Environment.Exit(1);
}
Console.WriteLine(pass + " passed, 0 failed");

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

// Records every AuthenticationProviderId the reconciliation writes. DispatchProxy rather
// than an implementation because IUserManager gains and loses members between patch
// releases — the same reason tests/cs/pipeline uses it.
public class RecordingUserManager : DispatchProxy
{
    private Dictionary<Guid, User> _users;
    private List<string> _writes;

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
                return _users.TryGetValue((Guid)args[0], out var u) ? u : null;

            case "UpdateUserAsync":
                _writes.Add(((User)args[0]).AuthenticationProviderId);
                return Task.CompletedTask;

            default:
                throw new NotSupportedException(
                    "the reconciliation called IUserManager." + targetMethod.Name
                    + ", which this harness does not model");
        }
    }
}

// Just enough of a service provider to hand back a user manager, and just enough of a
// user manager to answer GetUserById. DispatchProxy because IUserManager gains and loses
// members between patch releases and a hand-written implementation would not compile
// against two of them.
sealed class ServiceStub : IServiceProvider
{
    private readonly IUserManager _users;

    private ServiceStub(IUserManager users) { _users = users; }

    public static IServiceProvider Create() => new ServiceStub(UserManagerStub.Create());

    public object? GetService(Type serviceType)
        => serviceType == typeof(IUserManager) ? _users : null;
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
