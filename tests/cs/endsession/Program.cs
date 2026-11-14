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
// POST /switch with endPreviousSession: one call instead of two. After the new token exists,
// the token that made the request is ended through ISessionManager.Logout(token), the call
// Jellyfin's own POST /Sessions/Logout makes. Only that token: the account's other sessions
// stay. A switch that fails ends nothing; absent or false ends nothing. If the logout throws,
// the switch still succeeds.
//
// Red against e5bf2845: the field does not exist, so the "true" case ends nothing.
//
// Usage: dotnet run -c Release   (loads bin/Release/<tfm>/Jellyfin.Profiles.dll)
// ─────────────────────────────────────────────────────────────────────────────

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


// ── The world ────────────────────────────────────────────────────────────────
const string BONFIRE = "Jellyfin.Profiles.Auth.BonfirePinAuthenticationProvider";
const string JELLYFIN = "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider";

var tempDir = Path.Combine(Path.GetTempPath(), "bonfire-links-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
var plugin = Activator.CreateInstance(pluginType, new StubPaths(tempDir), new StubXml());
var config = pluginType.GetProperty("Configuration").GetValue(plugin);
// A new install starts with Bonfire off (2.0) and every gate stands down; this harness needs it on.
config.GetType().GetProperty("BonfireOn").SetValue(config, true);
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
    string deviceId = null, string token = null)
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
    if (deviceId != null || token != null)
        http.Request.Headers["Authorization"] = "MediaBrowser Client=\"Jellyfin Web\", Device=\"Firefox\", DeviceId=\"" + (deviceId ?? "dev-1") + "\", Version=\"10.11.0\"" + (token != null ? ", Token=\"" + token + "\"" : "");
    if (pinHeader != null) http.Request.Headers["X-Bonfire-Master-Pin"] = pinHeader;
    c.ControllerContext = new ControllerContext { HttpContext = http };

    var invokeArgs = r.GetParameters().Select(p =>
    {
        var req = Activator.CreateInstance(p.ParameterType);
        foreach (var kv in body ?? new Dictionary<string, object>())
        {
            var prop = p.ParameterType.GetProperty(kv.Key);
            // A build that predates the field ignores it, as the server would; the assertions then fail.
            if (prop == null && kv.Key == "EndPreviousSession") continue;
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
// A signed-in caller who is refused gets a message: BonfireErrors sends a 401 with one as 403.
bool Refused((int status, object value) r) => r.status == 403 || (r.status == 401 && r.value is string s && s.Length > 0);
string Text((int status, object value) r) => r.status + " " + r.value;

// ── The tests ───────────────────────────────────────────────────────────────
var PROFILE = Guid.NewGuid(); world.Add(PROFILE, "Parent_Locked", null);
Map(PROFILE, HOME, "Locked", pin: "2468");

const string OLD = "old-token-of-this-device";
const string OTHERDEV = "token-of-the-tv";

Section("The field is read");
Ok("the request has an EndPreviousSession field",
   asm.GetType("Jellyfin.Profiles.Models.SwitchProfileRequest", true).GetProperty("EndPreviousSession") != null);

Section("true ends the caller's token, and only that");
SessionStub.LoggedOut.Clear();
var r = Call("SwitchProfile", HOME, B(("ProfileId", KID), ("EndPreviousSession", true)), deviceId: "dev-1", token: OLD);
Ok("the switch succeeds", r.status == 200, Text(r));
Ok("the token that made the request is logged out", SessionStub.LoggedOut.Contains(OLD), string.Join(",", SessionStub.LoggedOut));
Ok("and nothing else is: another device's token stays", !SessionStub.LoggedOut.Contains(OTHERDEV));
Ok("exactly one logout", SessionStub.LoggedOut.Count == 1, SessionStub.LoggedOut.Count.ToString());
Ok("the new token is not the one ended", !SessionStub.LoggedOut.Contains("token-" + KID.ToString("N")));

Section("A second session of the same account survives");
SessionStub.LoggedOut.Clear();
Call("SwitchProfile", HOME, B(("ProfileId", KID), ("EndPreviousSession", true)), deviceId: "dev-1", token: OLD);
Ok("the other session's token was never passed to Logout", !SessionStub.LoggedOut.Contains(OTHERDEV));
Ok("and no user-wide revocation was made", sessionStub.Revoked.Count == 0, sessionStub.Revoked.Count.ToString());

Section("A switch that fails ends nothing");
SessionStub.LoggedOut.Clear();
r = Call("SwitchProfile", HOME, B(("ProfileId", PROFILE), ("Pin", "0000"), ("EndPreviousSession", true)), deviceId: "dev-1", token: OLD);
Ok("a wrong PIN is refused", r.status == 401 || r.status == 403 || r.status == 400, Text(r));
Ok("and the old token is still alive", SessionStub.LoggedOut.Count == 0, string.Join(",", SessionStub.LoggedOut));
SessionStub.LoggedOut.Clear();
r = Call("SwitchProfile", OTHER, B(("ProfileId", KID), ("EndPreviousSession", true)), deviceId: "dev-1", token: OLD);
Ok("a stranger's switch is refused", r.status != 200, Text(r));
Ok("and ends nothing", SessionStub.LoggedOut.Count == 0);

Section("False or absent ends nothing");
SessionStub.LoggedOut.Clear();
r = Call("SwitchProfile", HOME, B(("ProfileId", KID), ("EndPreviousSession", false)), deviceId: "dev-1", token: OLD);
Ok("false: the switch succeeds", r.status == 200, Text(r));
Ok("false: nothing is logged out", SessionStub.LoggedOut.Count == 0);
r = Call("SwitchProfile", HOME, B(("ProfileId", KID)), deviceId: "dev-1", token: OLD);
Ok("absent: the switch succeeds", r.status == 200, Text(r));
Ok("absent: nothing is logged out", SessionStub.LoggedOut.Count == 0);

Section("When ending the old session fails, the switch still succeeds");
SessionStub.LoggedOut.Clear();
SessionStub.ThrowOnLogout = true;
r = Call("SwitchProfile", HOME, B(("ProfileId", KID), ("EndPreviousSession", true)), deviceId: "dev-1", token: OLD);
SessionStub.ThrowOnLogout = false;
Ok("the logout was tried", SessionStub.LoggedOut.Contains(OLD));
Ok("and the switch still answers 200 with a token", r.status == 200 && P(r.value, "ActiveProfileToken") != null, Text(r));

Section("No token on the request");
SessionStub.LoggedOut.Clear();
r = Call("SwitchProfile", HOME, B(("ProfileId", KID), ("EndPreviousSession", true)), deviceId: "dev-1");
Ok("true with no token to end: the switch succeeds, nothing is logged out", r.status == 200 && SessionStub.LoggedOut.Count == 0, Text(r));
return Finish();

// ── A modelled Jellyfin ────────────────────────────────────────────────────

public sealed class CaptureLog : Microsoft.Extensions.Logging.ILogger
{
    public readonly List<string> Lines = new();
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
    {
        lock (Lines) Lines.Add(formatter(state, exception));
    }
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
    public static readonly List<ActivityLog> Activity = new();
    static readonly MediaBrowser.Model.Activity.IActivityManager Recorder = ActivityRecorder.Create(Activity);
    public object GetService(Type t) => t == typeof(IEnumerable<MediaBrowser.Controller.Authentication.IAuthenticationProvider>)
        ? new MediaBrowser.Controller.Authentication.IAuthenticationProvider[] { new InvalidAuthProvider(), new DefaultAuthenticationProvider() }
        : t == typeof(MediaBrowser.Model.Activity.IActivityManager) ? Recorder
        : null;
}

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
        return m.ReturnType == typeof(Task) ? Task.CompletedTask : null;
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
            case "Logout" when args.Length == 1 && args[0] is string tok:
                LoggedOut.Add(tok);
                if (ThrowOnLogout) return Task.FromException(new InvalidOperationException("logout failed"));
                return Task.CompletedTask;
            case "Logout" when args.Length == 1 && args[0] is Jellyfin.Database.Implementations.Entities.Security.Device d:
                SignedOut.Add((d.UserId, d.DeviceId));
                return Task.CompletedTask;
        }
        return Loose.Default(m.ReturnType);
    }

    /// <summary>(account, device) pairs signed out one device at a time.</summary>
    public static List<(Guid user, string device)> SignedOut = new();

    /// <summary>Access tokens passed to ISessionManager.Logout(string), the call POST /Sessions/Logout makes.</summary>
    public static List<string> LoggedOut = new();
    public static bool ThrowOnLogout;
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
