using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Entities.Security;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

// ─────────────────────────────────────────────────────────────────────────────
// Issue #40: a Roku opened a PIN-protected profile without asking for the PIN.
//
// Bonfire checked the PIN correctly. The Roku app saves the profile's access token on
// the set ("Save Credentials?", ticked by default), and on its user list it tries that
// token before anything else — and its "Change user" keeps it. So one PIN typed on a
// television opened that profile for whoever held the remote, from then on.
//
// The fix revokes those saved sign-ins when the set's picker asks for its user list
// (GET /Users/Public, anonymous). This harness drives the real middleware with that
// request and counts which of Jellyfin's device rows are signed out. Against 1.7.1 the
// first assertion fails: nothing is revoked, which is the bug.
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

var pluginType = asm.GetType("Jellyfin.Profiles.Plugin", true);
var cfgType = asm.GetType("Jellyfin.Profiles.Configuration.PluginConfiguration", true);
var mapType = asm.GetType("Jellyfin.Profiles.Configuration.ProfileMapping", true);
var devType = asm.GetType("Jellyfin.Profiles.Configuration.KnownDevice", true);
var mwType = asm.GetType("Jellyfin.Profiles.ProfilesIndexMiddleware", true);
var panicField = pluginType.GetField("_panicDisabled", BindingFlags.Static | BindingFlags.NonPublic);
Ok("the emergency-disable switch is still found by name (a rename must fail here, not skip its checks)", panicField != null);

var tempDir = Path.Combine(Path.GetTempPath(), "bonfire-savedsignins-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
var plugin = Activator.CreateInstance(pluginType, new StubPaths(tempDir), new StubXml());
var config = pluginType.GetProperty("Configuration").GetValue(plugin);
// A new install starts with Bonfire off (2.0); this harness is about a server where it is on.
config.GetType().GetProperty("BonfireOn").SetValue(config, true);

// ── The household ──────────────────────────────────────────────────────────
var MASTER = Guid.NewGuid();
var KIDS = Guid.NewGuid();      // has a PIN: the profile from the issue
var OPEN = Guid.NewGuid();      // no PIN: nothing to protect, so nothing to clear
var LAN = Guid.NewGuid();       // has a PIN, skipped on the home network by its owner
var OTHER = Guid.NewGuid();     // another household's master
var OTHERKIDS = Guid.NewGuid(); // its PIN profile, also called "kids"

object Map(Guid profile, Guid master, string name, string pin = null, bool lanBypass = false)
{
    var m = Activator.CreateInstance(mapType);
    mapType.GetProperty("ProfileUserId").SetValue(m, profile);
    mapType.GetProperty("MasterUserId").SetValue(m, master);
    mapType.GetProperty("ProfileName").SetValue(m, name);
    mapType.GetProperty("PinHash").SetValue(m, pin);
    mapType.GetProperty("BypassPinOnLocalNetwork").SetValue(m, lanBypass);
    return m;
}

var mappings = (System.Collections.IList)cfgType.GetProperty("Mappings").GetValue(config);
mappings.Add(Map(KIDS, MASTER, "kids", pin: "hash"));
mappings.Add(Map(OPEN, MASTER, "open"));
mappings.Add(Map(LAN, MASTER, "lan", pin: "hash", lanBypass: true));
mappings.Add(Map(OTHERKIDS, OTHER, "kids", pin: "hash"));

// Roku's bare id is its 36-character channel client id; the Android TV one is an ANDROID_ID.
const string ROKU = "3f9c2a10-5b7e-4d21-9c3a-8e1f0b2d4c6a";
const string OTHER_ROKU = "71aa0c42-0d3b-4e8f-a1c5-2b9e6f4d3a10";
const string ATV = "9a6dae35cc29c74f";

object Known(string id, Guid master)
{
    var d = Activator.CreateInstance(devType);
    devType.GetProperty("DeviceId").SetValue(d, id);
    devType.GetProperty("MasterUserId").SetValue(d, master);
    devType.GetProperty("LastSeen").SetValue(d, DateTime.UtcNow);
    return d;
}

var known = (System.Collections.IList)cfgType.GetProperty("KnownDevices").GetValue(config);
known.Add(Known(ROKU, MASTER));
known.Add(Known(ATV, MASTER));
known.Add(Known(OTHER_ROKU, OTHER));

static string Sha1(string v) => Convert.ToHexString(
    System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(v))).ToLowerInvariant();

// Jellyfin's device rows: one per (account, device id), each holding a token.
List<(Guid user, string id)> Rows() => new()
{
    (KIDS, ROKU + "kids"),          // signed in from our list, which shows "kids"
    (KIDS, ROKU + "Bard_kids"),     // signed in by typing the real username
    (KIDS, "phone-1234"),           // the same profile on a phone: not this television
    (KIDS, OTHER_ROKU + "kids"),    // the same profile on another Roku
    (KIDS, Sha1(ATV + "+" + KIDS)), // the same profile on the Android TV
    (OPEN, ROKU + "open"),          // no PIN
    (LAN, ROKU + "lan"),            // PIN, skipped at home
    (MASTER, ROKU + "Bard"),        // the master: no PIN of its own
    (OTHERKIDS, ROKU + "kids"),     // another household's "kids", same suffix
};

var usernames = new Dictionary<Guid, string>
{
    [MASTER] = "Bard", [KIDS] = "Bard_kids", [OPEN] = "Bard_open", [LAN] = "Bard_lan",
    [OTHER] = "Other", [OTHERKIDS] = "Other_kids",
};

void Set(string prop, object value) => cfgType.GetProperty(prop).SetValue(config, value);
Set("EnableClientPinLogin", true);
Set("EnableClientProfileList", false); // the lock must not depend on the list

// Drives the real middleware with one request and returns the rows it signed out.
(List<(Guid user, string id)> revoked, string body) Drive(
    string deviceId, string token = null, bool local = false, bool sessionThrows = false,
    string viaProxy = null, string forwardedFor = null, string knownProxy = null)
{
    var sessions = SessionManagerStub.Create(sessionThrows);
    DeviceManagerStub.Calls = 0;
    var devices = DeviceManagerStub.Create(Rows());
    var users = UserManagerStub.Create(usernames);
    // Behind a proxy the stub judges the address it is actually given, as Jellyfin's does.
    var network = viaProxy == null ? NetworkStub.Create(local) : NetworkStub.ByAddress();

    var next = new RequestDelegate(async c =>
    {
        c.Response.StatusCode = 200;
        c.Response.ContentType = "application/json";
        await c.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("[]"));
    });
    var instance = Activator.CreateInstance(mwType, next, CreateLogger(mwType));

    var ctx = new DefaultHttpContext();
    ctx.Request.Method = "GET";
    ctx.Request.Path = "/Users/Public";
    ctx.Connection.RemoteIpAddress = IPAddress.Parse(viaProxy ?? (local ? "192.168.1.20" : "203.0.113.9"));
    if (viaProxy != null)
    {
        // What Jellyfin registers from Networking → Known proxies (ApiServiceCollectionExtensions
        // .ConfigureForwardHeaders): forward For, Proto and Host, trusting only that proxy.
        var fwd = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
        {
            ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
                | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedHost,
            ForwardLimit = null,
        };
        fwd.KnownProxies.Clear();
        fwd.KnownProxies.Add(IPAddress.Parse(knownProxy ?? viaProxy));
        var sc = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<
            Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>>(
            sc, Microsoft.Extensions.Options.Options.Create(fwd));
        ctx.RequestServices = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(sc);
        if (forwardedFor != null) ctx.Request.Headers["X-Forwarded-For"] = forwardedFor;
        ctx.Request.Headers["X-Forwarded-Proto"] = "https";
    }
    ctx.Request.Headers["Authorization"] =
        "MediaBrowser Client=\"Jellyfin Roku\", Device=\"Living Room\", Version=\"3.1.0\", DeviceId=\""
        + deviceId + "\"" + (token == null ? "" : ", Token=\"" + token + "\"");
    var sink = new MemoryStream();
    ctx.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>(
        new StreamResponseBodyFeature(sink));

    // Arguments by type, so this runs against a build whose Invoke takes fewer services
    // (1.7.1) - which is how it is shown to fail there.
    var invoke = mwType.GetMethod("Invoke");
    var args = invoke.GetParameters().Select(p =>
        p.ParameterType == typeof(HttpContext) ? ctx
        : p.ParameterType == typeof(IUserManager) ? users
        : p.ParameterType == typeof(IDeviceManager) ? devices
        : p.ParameterType == typeof(ISessionManager) ? sessions
        : p.ParameterType == typeof(INetworkManager) ? network
        : (object)null).ToArray();

    ((System.Threading.Tasks.Task)invoke.Invoke(instance, args)).GetAwaiter().GetResult();
    return (((SessionManagerStub)(object)sessions).Revoked, Encoding.UTF8.GetString(sink.ToArray()));
}

static object CreateLogger(Type forType)
    => Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(forType));

bool Has(List<(Guid user, string id)> r, Guid user, string id) => r.Any(x => x.user == user && x.id == id);
string Show(List<(Guid user, string id)> r) => r.Count == 0 ? "(none)" : string.Join(", ", r.Select(x => x.id));

Console.WriteLine();
Console.WriteLine("── A Roku opens its user list ──────────────────────────────────");

var picker = Drive(ROKU);
Ok("the PIN profile's saved sign-in on this Roku is revoked (issue #40)",
   Has(picker.revoked, KIDS, ROKU + "kids"), Show(picker.revoked));
Ok("including one saved under its real username", Has(picker.revoked, KIDS, ROKU + "Bard_kids"), Show(picker.revoked));
Ok("not the same profile on a phone", !Has(picker.revoked, KIDS, "phone-1234"));
Ok("not the same profile on another Roku", !Has(picker.revoked, KIDS, OTHER_ROKU + "kids"));
Ok("not the Android TV, whose app shows an error on a revoked token instead of the PIN box",
   !Has(picker.revoked, KIDS, Sha1(ATV + "+" + KIDS)));
Ok("not a profile with no PIN", !Has(picker.revoked, OPEN, ROKU + "open"));
Ok("not the master, which has no PIN", !Has(picker.revoked, MASTER, ROKU + "Bard"));
Ok("not another household's profile with the same name", !Has(picker.revoked, OTHERKIDS, ROKU + "kids"));
Ok("a PIN skipped at home is still asked away from home", Has(picker.revoked, LAN, ROKU + "lan"), Show(picker.revoked));
Ok("exactly those three", picker.revoked.Count == 3, Show(picker.revoked));
Ok("and the user list is still answered", picker.body == "[]", picker.body);
Ok("three PIN accounts cost one device lookup, not one each (r2-06-05)", DeviceManagerStub.Calls == 1, DeviceManagerStub.Calls.ToString());

Console.WriteLine();
Console.WriteLine("── When it must do nothing ─────────────────────────────────────");

var home = Drive(ROKU, local: true);
Ok("at home, the profile whose owner skips the PIN there keeps its sign-in",
   !Has(home.revoked, LAN, ROKU + "lan"), Show(home.revoked));
Ok("while the other PIN profile is still locked", Has(home.revoked, KIDS, ROKU + "kids"), Show(home.revoked));

// Bonfire's middleware runs in front of Jellyfin's forwarded-headers handling, so behind a
// reverse proxy every request used to arrive from the proxy's own, local, address (R-1029).
var proxied = Drive(ROKU, viaProxy: "127.0.0.1", forwardedFor: "203.0.113.9");
Ok("behind a known proxy, a Roku on the internet is away from home",
   Has(proxied.revoked, LAN, ROKU + "lan"), Show(proxied.revoked));
var proxiedHome = Drive(ROKU, viaProxy: "127.0.0.1", forwardedFor: "192.168.1.20");
Ok("and one at home, through the same proxy, is home",
   !Has(proxiedHome.revoked, LAN, ROKU + "lan"), Show(proxiedHome.revoked));
var spoofed = Drive(ROKU, viaProxy: "203.0.113.9", knownProxy: "127.0.0.1", forwardedFor: "192.168.1.20");
Ok("a home address claimed by something that isn't the known proxy is not believed",
   Has(spoofed.revoked, LAN, ROKU + "lan"), Show(spoofed.revoked));

var signedIn = Drive(ROKU, token: "abc123");
Ok("a request carrying a token is somebody signed in, not a picker", signedIn.revoked.Count == 0, Show(signedIn.revoked));

var stranger = Drive("0000aaaa-unknown-roku-0000-000000000000");
Ok("a device no household uses touches nothing", stranger.revoked.Count == 0, Show(stranger.revoked));
Ok("and looks nothing up", DeviceManagerStub.Calls == 0, DeviceManagerStub.Calls.ToString());

var atvPicker = Drive(ATV);
Ok("an Android TV picker touches nothing", atvPicker.revoked.Count == 0, Show(atvPicker.revoked));

Set("EnableClientPinLogin", false);
var off = Drive(ROKU);
Ok("with PIN sign-in on apps off, nothing is revoked - the profile would have no way back in",
   off.revoked.Count == 0, Show(off.revoked));
Set("EnableClientPinLogin", true);

if (panicField != null)
{
    panicField.SetValue(null, true);
    try
    {
        var panic = Drive(ROKU);
        Ok("the emergency disable turns it off", panic.revoked.Count == 0, Show(panic.revoked));
    }
    finally { panicField.SetValue(null, false); }
}

var broken = Drive(ROKU, sessionThrows: true);
Ok("a failure while revoking still answers the user list", broken.body == "[]", broken.body);

Console.WriteLine();
try { Directory.Delete(tempDir, true); } catch { }
if (fails.Count > 0)
{
    foreach (var f in fails) Console.WriteLine("   - " + f);
    Console.WriteLine(pass + " passed, " + fails.Count + " failed");
    return 1;
}
Console.WriteLine(pass + " passed, 0 failed");
return 0;

// ── Stubs ────────────────────────────────────────────────────────────────────
// DispatchProxy throughout: these interfaces gain and lose members between Jellyfin
// releases, and a hand implementation would tie the harness to one of them.

public class SessionManagerStub : DispatchProxy
{
    public List<(Guid user, string id)> Revoked = new();
    private bool _throws;

    public static ISessionManager Create(bool throws)
    {
        var proxy = Create<ISessionManager, SessionManagerStub>();
        ((SessionManagerStub)(object)proxy)._throws = throws;
        return proxy;
    }

    protected override object Invoke(MethodInfo m, object[] args)
    {
        if (m.Name == "Logout" && args.Length == 1 && args[0] is Device d)
        {
            if (_throws) throw new InvalidOperationException("database is locked");
            Revoked.Add((d.UserId, d.DeviceId));
            return System.Threading.Tasks.Task.CompletedTask;
        }

        throw new NotSupportedException("ISessionManager." + m.Name + " is not modelled");
    }
}

public class DeviceManagerStub : DispatchProxy
{
    private List<(Guid user, string id)> _rows;
    public static int Calls;

    public static IDeviceManager Create(List<(Guid user, string id)> rows)
    {
        var proxy = Create<IDeviceManager, DeviceManagerStub>();
        ((DeviceManagerStub)(object)proxy)._rows = rows;
        return proxy;
    }

    protected override object Invoke(MethodInfo m, object[] args)
    {
        if (m.Name != "GetDevices") throw new NotSupportedException("IDeviceManager." + m.Name + " is not modelled");

        Calls++;
        var q = args[0];
        var wantId = (string)q.GetType().GetProperty("DeviceId").GetValue(q);
        var wantUser = (Guid?)q.GetType().GetProperty("UserId").GetValue(q);

        var items = _rows
            .Where(r => (wantId == null || r.id == wantId) && (wantUser == null || r.user == wantUser.Value))
            .Select(r =>
            {
                var d = (Device)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Device));
                typeof(Device).GetProperty("UserId").SetValue(d, r.user);
                typeof(Device).GetProperty("DeviceId").SetValue(d, r.id);
                typeof(Device).GetProperty("DateLastActivity").SetValue(d, DateTime.UtcNow);
                return d;
            })
            .ToList();

        return new QueryResult<Device>(items);
    }
}

public class UserManagerStub : DispatchProxy
{
    private Dictionary<Guid, string> _names;

    public static IUserManager Create(Dictionary<Guid, string> names)
    {
        var proxy = Create<IUserManager, UserManagerStub>();
        ((UserManagerStub)(object)proxy)._names = names;
        return proxy;
    }

    protected override object Invoke(MethodInfo m, object[] args)
    {
        if (m.Name != "GetUserById") throw new NotSupportedException("IUserManager." + m.Name + " is not modelled");
        if (!_names.TryGetValue((Guid)args[0], out var name)) return null;

        var u = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty("Username").SetValue(u, name);
        return u;
    }
}

public class NetworkStub : DispatchProxy
{
    private bool _local;

    public static INetworkManager Create(bool local)
    {
        var proxy = Create<INetworkManager, NetworkStub>();
        ((NetworkStub)(object)proxy)._local = local;
        return proxy;
    }

    private bool _byAddress;

    /// <summary>Local means loopback or 192.168.x.x, judged on the address passed in.</summary>
    public static INetworkManager ByAddress()
    {
        var proxy = Create<INetworkManager, NetworkStub>();
        ((NetworkStub)(object)proxy)._byAddress = true;
        return proxy;
    }

    protected override object Invoke(MethodInfo m, object[] args)
    {
        if (m.Name != "IsInLocalNetwork") throw new NotSupportedException("INetworkManager." + m.Name + " is not modelled");
        if (!_byAddress) return _local;
        var text = args[0]?.ToString() ?? string.Empty;
        return text == "127.0.0.1" || text.StartsWith("192.168.", StringComparison.Ordinal);
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
