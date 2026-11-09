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
var baseType = asm.GetType("Jellyfin.Profiles.Controllers.ProfilesBaseController", true);
var pluginType = asm.GetType("Jellyfin.Profiles.Plugin", true);
var cfgType = asm.GetType("Jellyfin.Profiles.Configuration.PluginConfiguration", true);
var mapType = asm.GetType("Jellyfin.Profiles.Configuration.ProfileMapping", true);
var hasher = asm.GetType("Jellyfin.Profiles.Auth.PinHasher", true);

var tempDir = Path.Combine(Path.GetTempPath(), "bonfire-mainpin-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
var plugin = Activator.CreateInstance(pluginType, new StubPaths(tempDir), new StubXml());
var config = pluginType.GetProperty("Configuration").GetValue(plugin);
var mappings = (IList)cfgType.GetProperty("Mappings").GetValue(config);

object Map(Guid profile, Guid master, string pin = null)
{
    var m = Activator.CreateInstance(mapType);
    mapType.GetProperty("ProfileUserId").SetValue(m, profile);
    mapType.GetProperty("MasterUserId").SetValue(m, master);
    mapType.GetProperty("ProfileName").SetValue(m, "p");
    if (pin != null) mapType.GetProperty("PinHash").SetValue(m, hasher.GetMethod("Hash").Invoke(null, new object[] { pin }));
    mappings.Add(m);
    return m;
}

var world = new World();
var users = World.Create<IUserManager>(world);

// One call into the real controller. Returns the status and, for a refusal, its text.
(int status, string message) Call(string method, Guid caller, Dictionary<string, object> body = null, string headerPin = null, object[] raw = null)
{
    var route = ctrlType.GetMethod(method);
    if (route == null) return (-404, "no route " + method);

    var ctor = ctrlType.GetConstructors().First();
    var args = ctor.GetParameters().Select(p =>
        p.ParameterType == typeof(IUserManager) ? users
        : p.ParameterType.Name.StartsWith("ILogger") ? Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(ctrlType))
        : (object)null).ToArray();
    var c = (ControllerBase)ctor.Invoke(args);
    var http = new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", caller.ToString()) }, "test")),
    };
    http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7");
    if (headerPin != null) http.Request.Headers["X-Bonfire-Master-Pin"] = headerPin;
    c.ControllerContext = new ControllerContext { HttpContext = http };

    object[] invokeArgs = raw;
    if (invokeArgs == null)
    {
        invokeArgs = route.GetParameters().Select(p =>
        {
            if (p.ParameterType.IsValueType) return Activator.CreateInstance(p.ParameterType);
            var req = Activator.CreateInstance(p.ParameterType);
            foreach (var kv in body ?? new Dictionary<string, object>())
            {
                var prop = p.ParameterType.GetProperty(kv.Key);
                if (prop == null) continue;
                prop.SetValue(req, prop.PropertyType == typeof(string) && kv.Value is Guid g ? g.ToString() : kv.Value);
            }
            return req;
        }).ToArray();
    }

    object result;
    try { result = route.Invoke(c, invokeArgs); }
    catch (TargetInvocationException ex) { return (-500, ex.InnerException?.GetType().Name + ": " + ex.InnerException?.Message); }

    if (result is Task task)
    {
        try { task.GetAwaiter().GetResult(); }
        catch (Exception ex) { return (-500, ex.GetType().Name + ": " + ex.Message); }
        result = task.GetType().GetProperty("Result")?.GetValue(task);
    }
    var inner = result?.GetType().GetProperty("Result")?.GetValue(result) ?? result;
    return inner switch
    {
        ObjectResult o => (o.StatusCode ?? 200, o.Value as string),
        StatusCodeResult s => (s.StatusCode, null),
        _ => (-1, inner?.GetType().Name),
    };
}

// ─────────────────────────────────────────────────────────────────────────────
// One PIN allowance per account, across every door.
//
// Runs the real UpdateProfile route (the main-PIN check) and the provider's own counter
// (another app's sign-in box). Against the build before the fix, five wrong PINs on the web
// do not touch the allowance another app sees, and twenty in a day do not close the PIN.
//
// Usage: dotnet run -c Release   (loads bin/Release/<tfm>/Jellyfin.Profiles.dll)
// ─────────────────────────────────────────────────────────────────────────────

const string MISSING = "Enter your account's PIN.";
const string REQUIRED = "Set a PIN for your account first.";
bool PinRefusal((int status, string message) r) =>
    r.message == MISSING || r.message == REQUIRED || (r.message ?? "").StartsWith("Invalid Master PIN") || r.status == 429;


// ── One allowance per account ────────────────────────────────────────────────
// Three doors to the same PIN each had their own count, and only another app's sign-in box
// had a daily cap: the web switcher took ten a quarter hour with no day, the main-PIN check
// on a profile edit took ten more, and apps took five and twenty a day. About 1,900 guesses
// a day at one main PIN, four digits in about three days. Now one allowance per account,
// five a quarter hour and twenty a day, counted on the account whose PIN it is.
Console.WriteLine();
Console.WriteLine("── One allowance per account ───────────────────────────────────");
{
    var rl = asm.GetType("Jellyfin.Profiles.Controllers.RateLimiter", true);
    var flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    object Limiter(string name) => rl.GetField(name, flags)?.GetValue(null);
    void ResetKey(object limiter, string key) => limiter?.GetType().GetMethod("Reset")?.Invoke(limiter, new object[] { key });
    var provider = asm.GetType("Jellyfin.Profiles.Auth.BonfirePinAuthenticationProvider", true);
    var appBegin = provider.GetMethod("BeginPinAttempt", flags);

    var SHARED = Guid.NewGuid(); world.Add(SHARED, "Shared"); Map(SHARED, SHARED, pin: "1234");
    var n = SHARED.ToString("N");
    // The quarter hour passing, under the names before and after.
    void QuarterHourPasses() { ResetKey(Limiter("AccountPin"), n); ResetKey(Limiter("PinPerProfile"), "master-pin:" + n); }
    void ForgetAll()
    {
        foreach (var f in rl.GetFields(flags).Where(f => f.FieldType == rl))
            foreach (var key in new[] { n, "master-pin:" + n }) ResetKey(f.GetValue(null), key);
    }
    (int status, string message) Edit(string pin) =>
        Call("UpdateProfile", SHARED, new Dictionary<string, object> { ["ProfileId"] = SHARED, ["ProfileName"] = "Shared", ["MasterPin"] = pin });

    ForgetAll();
    for (int i = 0; i < 5; i++) Edit("9999");
    Ok("five wrong on the web use up the same allowance as another app's sign-in box",
       appBegin != null && !(bool)appBegin.Invoke(null, new object[] { SHARED }));

    ForgetAll();
    for (int batch = 0; batch < 4; batch++)
    {
        for (int i = 0; i < 5; i++) Edit("9999");
        QuarterHourPasses();
    }
    var right = Edit("1234");
    Ok("twenty wrong in a day close the main PIN for the day on the web too", right.status == 429, right.status + " " + right.message);
    ForgetAll();
    right = Edit("1234");
    Ok("and the right PIN works again once the day is over", !PinRefusal(right), right.status + " " + right.message);
}

if (fails.Count > 0)
{
    Console.WriteLine(pass + " passed, " + fails.Count + " failed");
    return 1;
}
Console.WriteLine(pass + " passed, 0 failed");
return 0;

// ── A modelled Jellyfin ──────────────────────────────────────────────────────

public class World
{
    public Dictionary<Guid, User> Users = new();

    public void Add(Guid id, string name)
    {
        var u = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty("Username").SetValue(u, name);
        typeof(User).GetProperty("Id").SetValue(u, id);
        typeof(User).GetProperty("AuthenticationProviderId").SetValue(u, "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider");
        Users[id] = u;
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

    protected override object Invoke(MethodInfo m, object[] args)
    {
        switch (m.Name)
        {
            case "GetUserById":
                return W.Users.TryGetValue((Guid)args[0], out var u) ? u : null;
            case "GetUserDto":
                var user = (User)args[0];
                return new UserDto
                {
                    Id = user.Id,
                    Name = user.Username,
                    Policy = new UserPolicy { AuthenticationProviderId = user.AuthenticationProviderId, PasswordResetProviderId = "x" },
                    Configuration = new UserConfiguration(),
                };
        }

        var t = m.ReturnType;
        if (t == typeof(void)) return null;
        if (t == typeof(Task)) return Task.CompletedTask;
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var inner = t.GetGenericArguments()[0];
            var value = inner.IsValueType ? Activator.CreateInstance(inner) : null;
            return typeof(Task).GetMethod("FromResult").MakeGenericMethod(inner).Invoke(null, new[] { value });
        }
        return t.IsValueType ? Activator.CreateInstance(t) : null;
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
