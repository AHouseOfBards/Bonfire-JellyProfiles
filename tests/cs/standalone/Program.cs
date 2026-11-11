using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

// ─────────────────────────────────────────────────────────────────────────────
// Making a profile its own account (POST /admin/standalone).
//
// Runs the real route against a modelled Jellyfin and checks the order of what it does,
// because the order is the whole difficulty:
//
//  - the master's policy is applied while it is still a profile (afterwards nothing
//    would apply it, and it would keep whatever was on the account);
//  - its sessions are ended;
//  - it stops being a profile BEFORE the password is set, since Bonfire's provider
//    refuses a password change for a profile;
//  - the provider is its master's, never Bonfire's and never empty (an empty id once
//    made every user on a server unreadable), and is set before the password, which
//    goes through it;
//  - then the rename.
//
// And the refusals: not an administrator, no password, a main account, an unknown
// profile, a username Jellyfin would refuse or one already taken.
//
// Against the maintenance commit the first assertion fails: there is no such route.
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
        d = Path.GetDirectoryName(d);
    return d ?? throw new Exception("could not find the repository root");
}

var tfm = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Name;
var asm = Assembly.LoadFrom(Path.Combine(RepoRoot(), "bin", "Release", tfm, "Jellyfin.Profiles.dll"));

var ctrlType = asm.GetType("Jellyfin.Profiles.Controllers.ProfilesController", true);
var route = ctrlType.GetMethod("MakeStandalone");
var reqType = asm.GetType("Jellyfin.Profiles.Models.StandaloneRequest");
Ok("this build can make a profile its own account", route != null && reqType != null);
if (route == null || reqType == null) return Finish();

var pluginType = asm.GetType("Jellyfin.Profiles.Plugin", true);
var cfgType = asm.GetType("Jellyfin.Profiles.Configuration.PluginConfiguration", true);
var mapType = asm.GetType("Jellyfin.Profiles.Configuration.ProfileMapping", true);
const string BONFIRE = "Jellyfin.Profiles.Auth.BonfirePinAuthenticationProvider";
const string JELLYFIN = "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider";
const string LDAP = "Jellyfin.Plugin.LDAP_Auth.LdapAuthenticationProviderPlugin";

var tempDir = Path.Combine(Path.GetTempPath(), "bonfire-standalone-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
var plugin = Activator.CreateInstance(pluginType, new StubPaths(tempDir), new StubXml());
var config = pluginType.GetProperty("Configuration").GetValue(plugin);
var mappings = (IList)cfgType.GetProperty("Mappings").GetValue(config);
bool Mapped(Guid id) => mappings.Cast<object>().Any(m => (Guid)mapType.GetProperty("ProfileUserId").GetValue(m) == id);

var ADMIN = Guid.NewGuid();
var MASTER = Guid.NewGuid();
var KID = Guid.NewGuid();
var TEEN = Guid.NewGuid();
var LDAPMASTER = Guid.NewGuid();
var LDAPKID = Guid.NewGuid();

var world = new World();
world.Add(ADMIN, "Admin", JELLYFIN, admin: true);
world.Add(MASTER, "Bard", BONFIRE);          // bound for its PIN
world.Add(KID, "Bard_Kid", BONFIRE);
world.Add(TEEN, "Bard_Teen", BONFIRE);
world.Add(LDAPMASTER, "Ldap", LDAP);
world.Add(LDAPKID, "Ldap_Kid", LDAP);
world.MappedNow = Mapped;
// Every profile is hidden from the sign-in screen; LDAPMASTER's household hides its main account too.
world.Hidden.UnionWith(new[] { KID, TEEN, LDAPKID, LDAPMASTER });

void Map(Guid profile, Guid master, string name)
{
    var m = Activator.CreateInstance(mapType);
    mapType.GetProperty("ProfileUserId").SetValue(m, profile);
    mapType.GetProperty("MasterUserId").SetValue(m, master);
    mapType.GetProperty("ProfileName").SetValue(m, name);
    mappings.Add(m);
}
Map(MASTER, MASTER, "Bard");
Map(KID, MASTER, "Kid");
Map(TEEN, MASTER, "Teen");
Map(LDAPMASTER, LDAPMASTER, "Ldap");
Map(LDAPKID, LDAPMASTER, "Kid");

var users = World.Create<IUserManager>(world);
var sessions = World.Create<ISessionManager>(world);
var libraries = World.Create<ILibraryManager>(world);

(int status, object value) Call(Guid caller, Guid profile, string username, string password)
{
    var ctor = ctrlType.GetConstructors().First();
    var args = ctor.GetParameters().Select(p =>
        p.ParameterType == typeof(IUserManager) ? users
        : p.ParameterType == typeof(ISessionManager) ? sessions
        : p.ParameterType == typeof(ILibraryManager) ? libraries
        : p.ParameterType.Name.StartsWith("ILogger") ? Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(ctrlType))
        : (object)null).ToArray();
    var c = (ControllerBase)ctor.Invoke(args);
    var http = new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", caller.ToString()) }, "test")),
    };
    c.ControllerContext = new ControllerContext { HttpContext = http };

    var req = Activator.CreateInstance(reqType);
    reqType.GetProperty("ProfileId").SetValue(req, profile);
    reqType.GetProperty("Username").SetValue(req, username);
    reqType.GetProperty("Password").SetValue(req, password);
    var task = (Task)route.Invoke(c, new[] { req });
    task.GetAwaiter().GetResult();
    var result = task.GetType().GetProperty("Result").GetValue(task);
    var inner = result.GetType().GetProperty("Result")?.GetValue(result) ?? result;
    return inner switch
    {
        ObjectResult o => (o.StatusCode ?? 200, o.Value),
        StatusCodeResult s => (s.StatusCode, null),
        _ => (-1, inner),
    };
}

Console.WriteLine();
Console.WriteLine("── Refusals ────────────────────────────────────────────────────");

Ok("a main account cannot do it, even its own profile's", Call(MASTER, KID, "", "pw").status == 401);
Ok("no password is refused", Call(ADMIN, KID, "", "").status == 400);
Ok("a main account is already its own", Call(ADMIN, MASTER, "", "pw").status == 400);
Ok("an unknown profile is 404", Call(ADMIN, Guid.NewGuid(), "", "pw").status == 404);
Ok("a username Jellyfin would refuse is refused", Call(ADMIN, KID, "kid/../x", "pw").status == 400);
Ok("so is one already taken", Call(ADMIN, KID, "admin", "pw").status == 400);
Ok("and none of that changed anything", Mapped(KID) && world.Log.Count == 0, string.Join(" | ", world.Log));

Console.WriteLine();
Console.WriteLine("── A profile whose main account is on Bonfire's provider ───────");

world.Log.Clear();
var done = Call(ADMIN, KID, "Sam", "hunter2");
Ok("it succeeds", done.status == 200, done.status + " " + done.value);
Ok("it is no longer a profile", !Mapped(KID));
Ok("its password is the one given", world.Passwords.TryGetValue(KID, out var pw) && pw == "hunter2");
Ok("it is on Jellyfin's own provider, not Bonfire's (its master's is Bonfire's)", world.Users[KID].AuthenticationProviderId == JELLYFIN,
   world.Users[KID].AuthenticationProviderId);
Ok("it is renamed", world.Users[KID].Username == "Sam");
Ok("it shows on the sign-in screen, as its main account does", !world.Hidden.Contains(KID));
Ok("and says so", done.value?.GetType().GetProperty("Username")?.GetValue(done.value) as string == "Sam");

int At(string prefix) => world.Log.FindIndex(l => l.StartsWith(prefix));
Ok("the master's restrictions were applied while it was still a profile",
   world.Log.Any(l => l.StartsWith("policy " + KID + " mapped=True")), string.Join(" | ", world.Log));
Ok("its sessions were ended", At("revoke " + KID) >= 0);
Ok("the password was set after it stopped being a profile",
   world.Log.Any(l => l.StartsWith("password " + KID + " mapped=False")), string.Join(" | ", world.Log));
Ok("and after the provider changed, through which it is set",
   At("provider " + KID) >= 0 && At("provider " + KID) < At("password " + KID), string.Join(" | ", world.Log));
Ok("the rename came last", At("rename " + KID) > At("password " + KID));
Ok("in that order: policy, sessions, provider, password",
   At("policy " + KID) < At("revoke " + KID) && At("revoke " + KID) < At("provider " + KID));

Console.WriteLine();
Console.WriteLine("── Keeping the name, and a master on another provider ──────────");

world.Log.Clear();
var kept = Call(ADMIN, TEEN, "  ", "pw");
Ok("a blank username keeps the current one", kept.status == 200 && world.Users[TEEN].Username == "Bard_Teen" && At("rename") < 0);

world.Log.Clear();
var ldap = Call(ADMIN, LDAPKID, "", "pw");
// The directory has never heard of an account Bonfire made, and will not take its password.
Ok("a profile of an LDAP account still gets a password that works", ldap.status == 200 && world.Passwords.ContainsKey(LDAPKID),
   ldap.status + " " + ldap.value);
Ok("because it goes on Jellyfin's own provider, not the directory's", world.Users[LDAPKID].AuthenticationProviderId == JELLYFIN,
   world.Users[LDAPKID].AuthenticationProviderId);
Ok("and stays hidden, as its main account is", world.Hidden.Contains(LDAPKID));
Ok("no account was ever given an empty provider", world.Users.Values.All(u => !string.IsNullOrEmpty(u.AuthenticationProviderId)));

Console.WriteLine();
Console.WriteLine("── A server that only has RenameUser(Guid, string, string) ─────");

// R-1320: 10.11.11 and 12.0 removed RenameUser(User, string). Calling it threw
// MissingMethodException after the password was already set.
var SOLO = Guid.NewGuid();
world.Add(SOLO, "Bard_Solo", BONFIRE);
world.Hidden.Add(SOLO);
Map(SOLO, MASTER, "Solo");
world.GuidOnlyRename = true;
world.Log.Clear();
(int status, object value) guidOnly;
string guidOnlyError = null;
try { guidOnly = Call(ADMIN, SOLO, "Robin", "pw"); }
catch (Exception e) { guidOnly = (-1, null); guidOnlyError = (e.InnerException ?? e).GetType().Name; }
Ok("the rename works through the Guid overload", guidOnly.status == 200 && world.Users[SOLO].Username == "Robin",
   guidOnly.status + " " + guidOnlyError + " " + guidOnly.value);
Ok("it was given the old name as well", world.Log.Any(l => l == "rename " + SOLO + " from Bard_Solo"), string.Join(" | ", world.Log));
world.GuidOnlyRename = false;

Console.WriteLine();
Console.WriteLine("── A write fails after the profile has left its household ──────");

// R-1163: the mapping is gone by then, so a throw from the provider write used to escape as a
// bare 500: an account that is no profile, with no known password, and no word to the
// administrator. It answers like the password and rename failures do.
var FAILING = Guid.NewGuid();
world.Add(FAILING, "Bard_Failing", BONFIRE);
Map(FAILING, MASTER, "Failing");
world.FailUserUpdate = true;
(int status, object value) failed;
string failedError = null;
try { failed = Call(ADMIN, FAILING, "", "pw"); }
catch (Exception e) { failed = (-1, null); failedError = (e.InnerException ?? e).GetType().Name; }
world.FailUserUpdate = false;
Ok("a failed write answers 500 with what is left to do", failed.status == 500 && failed.value is string s500 && s500.Contains("Dashboard"),
   failed.status + " " + failedError + " " + failed.value);
Ok("and the profile is not a profile any more", !world.MappedNow(FAILING));

try { Directory.Delete(tempDir, true); } catch { }
return Finish();

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

// ── A modelled Jellyfin ────────────────────────────────────────────────────

public class World
{
    public Dictionary<Guid, User> Users = new();
    public HashSet<Guid> Admins = new();
    public Dictionary<Guid, string> Passwords = new();
    public List<string> Log = new();
    public HashSet<Guid> Hidden = new();
    public Func<Guid, bool> MappedNow = _ => false;
    public bool GuidOnlyRename;
    public bool FailUserUpdate;

    public void Add(Guid id, string name, string provider, bool admin = false)
    {
        var u = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty("Username").SetValue(u, name);
        typeof(User).GetProperty("Id").SetValue(u, id);
        typeof(User).GetProperty("AuthenticationProviderId").SetValue(u, provider);
        Users[id] = u;
        if (admin) Admins.Add(id);
    }

    public static T Create<T>(World w) where T : class
    {
        var p = DispatchProxy.Create<T, WorldProxy>();
        ((WorldProxy)(object)p).W = w;
        return p;
    }
}

public class WorldProxy : DispatchProxy
{
    public World W;

    // What 10.11.11's and 12.0's UserManager has. The 10.11.5 interface the build is compiled
    // against does not declare it, so it is a public method on the stub's own type, which is
    // where the plugin's reflection looks, as it does on the real server.
    public Task RenameUser(Guid userId, string oldName, string newName)
        => (Task)Invoke(typeof(WorldProxy).GetMethod("RenameUser"), new object[] { userId, oldName, newName });

    protected override object Invoke(MethodInfo m, object[] args)
    {
        switch (m.Name)
        {
            case "GetUserById":
                return W.Users.TryGetValue((Guid)args[0], out var u) ? u : null;
            case "GetUserByName":
                return W.Users.Values.FirstOrDefault(x => string.Equals(x.Username, (string)args[0], StringComparison.OrdinalIgnoreCase));
            case "GetUserDto":
                var user = (User)args[0];
                return new UserDto
                {
                    Id = user.Id,
                    Name = user.Username,
                    Policy = new UserPolicy { IsAdministrator = W.Admins.Contains(user.Id), IsHidden = W.Hidden.Contains(user.Id), AuthenticationProviderId = user.AuthenticationProviderId, PasswordResetProviderId = "x" },
                    Configuration = new UserConfiguration(),
                };
            case "UpdatePolicyAsync":
                W.Log.Add("policy " + args[0] + " mapped=" + W.MappedNow((Guid)args[0]));
                var policy = (UserPolicy)args[1];
                if (policy.IsHidden) W.Hidden.Add((Guid)args[0]); else W.Hidden.Remove((Guid)args[0]);
                // Jellyfin's UpdatePolicyAsync writes the policy's provider onto the account.
                if (W.Users.TryGetValue((Guid)args[0], out var pu))
                    typeof(User).GetProperty("AuthenticationProviderId").SetValue(pu, policy.AuthenticationProviderId);
                return Task.CompletedTask;
            case "UpdateUserAsync":
                if (W.FailUserUpdate) return Task.FromException(new InvalidOperationException("database is locked"));
                var changed = (User)args[0];
                W.Log.Add("provider " + changed.Id + " " + changed.AuthenticationProviderId);
                return Task.CompletedTask;
            case "ChangePassword":
                var id = args[0] is User cu ? cu.Id : (Guid)args[0];
                // A directory provider does not keep passwords for accounts it did not make.
                if (W.Users[id].AuthenticationProviderId.Contains("LDAP"))
                    return Task.FromException(new NotSupportedException("LDAP: password changes are not supported"));
                W.Passwords[id] = (string)args[1];
                W.Log.Add("password " + id + " mapped=" + W.MappedNow(id));
                return Task.CompletedTask;
            case "RenameUser":
                // Jellyfin 10.11.11 and 12.0 have only RenameUser(Guid, string, string); the
                // build is compiled against 10.11.5's RenameUser(User, string). A DispatchProxy
                // cannot drop a method from the interface, so on those servers this one throws
                // what the runtime throws for a method that is not there.
                if (args.Length == 2 && W.GuidOnlyRename)
                    throw new MissingMethodException("IUserManager.RenameUser(User, string)");
                var rid = args[0] is User ru ? ru.Id : (Guid)args[0];
                W.Log.Add("rename " + rid + (args.Length == 3 ? " from " + args[1] : ""));
                typeof(User).GetProperty("Username").SetValue(W.Users[rid], (string)args[args.Length - 1]);
                return Task.CompletedTask;
            case "RevokeUserTokens":
                W.Log.Add("revoke " + args[0]);
                return Task.CompletedTask;
        }

        // Anything else: an empty answer of the right shape.
        var t = m.ReturnType;
        if (t == typeof(void)) return null;
        if (t == typeof(Task)) return Task.CompletedTask;
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var inner = t.GetGenericArguments()[0];
            var value = inner.IsValueType ? Activator.CreateInstance(inner) : Empty(inner);
            return typeof(Task).GetMethod("FromResult").MakeGenericMethod(inner).Invoke(null, new[] { value });
        }
        return t.IsValueType ? Activator.CreateInstance(t) : Empty(t);
    }

    private static object Empty(Type t)
    {
        if (t.IsArray) return Array.CreateInstance(t.GetElementType(), 0);
        if (t.IsGenericType && (t.GetGenericTypeDefinition() == typeof(List<>)
            || t.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            || t.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)
            || t.GetGenericTypeDefinition() == typeof(IList<>)
            || t.GetGenericTypeDefinition() == typeof(IReadOnlyCollection<>)))
        {
            return Activator.CreateInstance(typeof(List<>).MakeGenericType(t.GetGenericArguments()[0]));
        }
        return null;
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
