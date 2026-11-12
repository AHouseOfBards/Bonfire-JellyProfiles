using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging.Abstractions;

// ─────────────────────────────────────────────────────────────────────────────
// A second account on the same app and device, without a sign-out (R-1008).
//
// Jellyfin 10.11 keys a session by app name + device id (SessionManager.GetSessionKey).
// SessionStarted is raised only when GetOrAdd creates the record; when the key exists, the
// new account's UserId and UserName are written over the old ones in place and nothing is
// raised. A household's profile signing in on a television somebody else last used therefore
// never reached BonfireSessionListener: no policy sync (so its main account's limits did not
// reach it) and no device row (so its profiles were never offered on that sign-in screen).
//
// The listener now also watches SessionActivity — raised from LogSessionActivity at most
// once per ten seconds per session — and treats a change of account on a session it has
// already seen as a sign-in. This harness drives the real listener with a modelled session
// manager and raises the events the way 10.11 does.
//
// Red against 2ea6ccc: the handover assertions fail (nothing listens to SessionActivity).
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
        d = Path.GetDirectoryName(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    return d ?? throw new Exception("could not find the repository root");
}

var tfm = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Name;
var asm = Assembly.LoadFrom(Path.Combine(RepoRoot(), "bin", "Release", tfm, "Jellyfin.Profiles.dll"));

var pluginType = asm.GetType("Jellyfin.Profiles.Plugin", true);
var cfgType = asm.GetType("Jellyfin.Profiles.Configuration.PluginConfiguration", true);
var mapType = asm.GetType("Jellyfin.Profiles.Configuration.ProfileMapping", true);
var listenerType = asm.GetType("Jellyfin.Profiles.Auth.BonfireSessionListener", true);
var knownType = asm.GetType("Jellyfin.Profiles.Configuration.KnownDevice", true);

var tempDir = Path.Combine(Path.GetTempPath(), "bonfire-sessionhandover-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
var plugin = Activator.CreateInstance(pluginType, new StubPaths(tempDir), new StubXml());
var config = pluginType.GetProperty("Configuration").GetValue(plugin);
// A new install starts with Bonfire off (2.0), and off every gate stands down, so a
// refusal here would prove nothing. This harness is about a server where it is on.
config.GetType().GetProperty("BonfireOn").SetValue(config, true);
var mappings = (IList)cfgType.GetProperty("Mappings").GetValue(config);
var known = (IList)cfgType.GetProperty("KnownDevices").GetValue(config);

void Map(Guid profile, Guid master)
{
    var m = Activator.CreateInstance(mapType);
    mapType.GetProperty("ProfileUserId").SetValue(m, profile);
    mapType.GetProperty("MasterUserId").SetValue(m, master);
    mapType.GetProperty("ProfileName").SetValue(m, "p");
    mappings.Add(m);
}
bool DeviceOwnedBy(string deviceId, Guid household) => known.Cast<object>().Any(d =>
    (string)knownType.GetProperty("DeviceId").GetValue(d) == deviceId
    && (bool)knownType.GetMethod("OwnedBy").Invoke(d, new object[] { household }));
bool DeviceKnown(string deviceId) => known.Cast<object>().Any(d => (string)knownType.GetProperty("DeviceId").GetValue(d) == deviceId);

var world = new World();
var MAIN = Guid.NewGuid(); var KID = Guid.NewGuid(); var STRANGER = Guid.NewGuid();
var OTHER = Guid.NewGuid();
foreach (var id in new[] { MAIN, KID, STRANGER, OTHER }) world.Add(id);
Map(MAIN, MAIN); Map(KID, MAIN); Map(OTHER, OTHER);

var sessions = (ISessionManager)World.Create(typeof(ISessionManager), world);
var services = new Services(world);
var loggerType = typeof(NullLogger<>).MakeGenericType(listenerType);
var listener = Activator.CreateInstance(listenerType, sessions, Activator.CreateInstance(loggerType), services);
((Microsoft.Extensions.Hosting.IHostedService)listener).StartAsync(CancellationToken.None).GetAwaiter().GetResult();

SessionInfo Session(string id, string deviceId, Guid user)
{
    var ctor = typeof(SessionInfo).GetConstructors().OrderBy(c => c.GetParameters().Length).First();
    var s = (SessionInfo)ctor.Invoke(ctor.GetParameters().Select(p =>
        p.ParameterType == typeof(ISessionManager) ? (object)sessions
        : p.ParameterType.IsInterface ? Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(typeof(SessionInfo)))
        : null).ToArray());
    s.Id = id;
    s.DeviceId = deviceId;
    s.DeviceName = "Living room";
    s.Client = "Android TV";
    s.UserId = user;
    s.UserName = "u";
    return s;
}
void Raise(string evt, SessionInfo s)
{
    foreach (var h in world.Handlers.TryGetValue(evt, out var list) ? list.ToList() : new List<Delegate>())
        h.DynamicInvoke(sessions, new SessionEventArgs { SessionInfo = s });
}
// The policy sync runs on its own task; give it a moment, and count the profile's reads.
int Syncs(Guid id) { Thread.Sleep(400); return world.Lookups.Count(x => x == id); }

Console.WriteLine();
Console.WriteLine("── The listener subscribes ─────────────────────────────────────");
Ok("to SessionStarted", world.Handlers.ContainsKey("SessionStarted"));
Ok("and to SessionActivity, where a handover shows", world.Handlers.ContainsKey("SessionActivity"));

Console.WriteLine();
Console.WriteLine("── An ordinary sign-in still works ─────────────────────────────");
{
    var s = Session("s-new", "phone-1", KID);
    Raise("SessionStarted", s);
    Ok("a new session for a profile records its device for the household", DeviceOwnedBy("phone-1", MAIN));
    Ok("and syncs the profile's policy (" + Syncs(KID) + ")", world.Lookups.Contains(KID));
}

Console.WriteLine();
Console.WriteLine("── A second account on the same app and device ─────────────────");
{
    world.Lookups.Clear();
    // Somebody outside any household signs in on the television first.
    var s = Session("s-tv", "tv-9", STRANGER);
    Raise("SessionStarted", s);
    Ok("the first account, outside any household, records nothing", !DeviceKnown("tv-9"));

    // The profile signs in on the same app and device. Jellyfin reuses the session and
    // overwrites the account on it; SessionStarted is NOT raised. Activity is.
    s.UserId = KID; s.UserName = "kid";
    Raise("SessionActivity", s);
    Ok("the profile taking the session over records the device for its household", DeviceOwnedBy("tv-9", MAIN));
    var n = Syncs(KID);
    Ok("and syncs the profile's policy (" + n + ")", n >= 1);

    world.Lookups.Clear();
    Raise("SessionActivity", s);
    Raise("SessionActivity", s);
    Ok("further activity from the same account does nothing more (" + Syncs(KID) + ")", Syncs(KID) == 0);

    // And over again, to another household.
    s.UserId = OTHER; s.UserName = "other";
    Raise("SessionActivity", s);
    Ok("a third account taking it over is noted too", DeviceOwnedBy("tv-9", OTHER));
}

Console.WriteLine();
Console.WriteLine("── What it must not do ─────────────────────────────────────────");
{
    world.Lookups.Clear();
    // A session that was already open when the plugin started: nothing is known of it, so
    // its activity is a first sighting, not a handover. Acting on it would fire for every
    // open session at once on each server start.
    var s = Session("s-old", "tv-old", KID);
    Raise("SessionActivity", s);
    Ok("a session first seen through activity is only remembered", !DeviceKnown("tv-old") && Syncs(KID) == 0);
    s.UserId = OTHER;
    Raise("SessionActivity", s);
    Ok("but a change after that is a handover", DeviceOwnedBy("tv-old", OTHER));

    // An ended session is forgotten, so the list does not grow without bound.
    var gone = Session("s-gone", "tv-gone", STRANGER);
    Raise("SessionStarted", gone);
    Raise("SessionEnded", gone);
    gone.UserId = KID;
    Raise("SessionActivity", gone);
    Ok("an ended session is forgotten: its id coming back is a first sighting", !DeviceKnown("tv-gone"));

    var signedOut = Session("s-out", "tv-out", STRANGER);
    Raise("SessionStarted", signedOut);
    signedOut.UserId = Guid.Empty;
    Raise("SessionActivity", signedOut);
    Ok("a session left with no account does nothing", !DeviceKnown("tv-out"));
}

Console.WriteLine();
Console.WriteLine("── Only a password makes a device known ────────────────────────");
{
    // The sign-in request's note, as the middleware starts it and the provider marks it.
    // Jellyfin raises SessionStarted through Task.Run, which carries that request's
    // context, so the listener reads the same note. A build without the note has no
    // BeginSignIn, and then nothing can tell the listener, which is the bug.
    var rd = asm.GetType("Jellyfin.Profiles.Auth.RequestDevice", true);
    var begin = rd.GetMethod("BeginSignIn", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
    void Note(bool byPin) => begin?.Invoke(null, new object[] { byPin });

    Note(true);
    Raise("SessionStarted", Session("s-pin", "pin-tv", KID));
    Ok("a PIN sign-in on a device nobody signed in on records nothing", !DeviceKnown("pin-tv"));

    // An existing record another household owns: a PIN sign-in must not add this one.
    Note(false);
    Raise("SessionStarted", Session("s-other", "shared-tv", OTHER));
    Note(true);
    Raise("SessionStarted", Session("s-pin2", "shared-tv", KID));
    Ok("nor adds its household to a device another household signed in on", !DeviceOwnedBy("shared-tv", MAIN));

    Note(false);
    Raise("SessionStarted", Session("s-pw", "pw-tv", MAIN));
    Ok("a password sign-in does record the device", DeviceOwnedBy("pw-tv", MAIN));

    Note(true);
    Raise("SessionStarted", Session("s-pw2", "pw-tv", KID));
    Ok("and a PIN sign-in there afterwards keeps it", DeviceOwnedBy("pw-tv", MAIN));
}

Console.WriteLine();
Console.WriteLine("── A Quick Connect approval (R2-01-01) ─────────────────────────");
{
    // Jellyfin creates the session inside the approver's own POST /QuickConnect/Authorize;
    // the middleware starts this note for it. A profile approving a code for a device the
    // household never signed in on must not make that device known; a main account may.
    var rd = asm.GetType("Jellyfin.Profiles.Auth.RequestDevice", true);
    var qc = rd.GetMethod("BeginQuickConnectAuthorize", BindingFlags.Public | BindingFlags.Static);
    Ok("a Quick Connect note exists to start", qc != null);

    qc?.Invoke(null, null);
    Raise("SessionStarted", Session("s-qc1", "qc-foreign", KID));
    Ok("a profile approving a code does not make the device known", !DeviceKnown("qc-foreign"));

    qc?.Invoke(null, null);
    Raise("SessionStarted", Session("s-qc2", "qc-tv", MAIN));
    Ok("a main account approving a code still does", DeviceOwnedBy("qc-tv", MAIN));
}

Console.WriteLine();
Console.WriteLine("── The player table is bounded (R2-01-04) ──────────────────────");
{
    var noted = (ConcurrentDictionary<string, bool>)listenerType
        .GetField("_playerNoted", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(listener);
    for (var i = 0; i < 10000; i++) noted["fill-" + i] = true;
    Raise("SessionStarted", Session("s-bound", "bound-tv", STRANGER));
    Ok("a full player table is cleared by the next new session (" + noted.Count + ")", noted.Count < 10000);
}

((Microsoft.Extensions.Hosting.IHostedService)listener).StopAsync(CancellationToken.None).GetAwaiter().GetResult();
Ok("stopping unsubscribes from both", world.Handlers.Values.All(l => l.Count == 0));

try { Directory.Delete(tempDir, true); } catch { }
Console.WriteLine();
if (fails.Count > 0)
{
    foreach (var f in fails) Console.WriteLine("   - " + f);
    Console.WriteLine(pass + " passed, " + fails.Count + " failed");
    return 1;
}
Console.WriteLine(pass + " passed, 0 failed");
return 0;

// ── A modelled Jellyfin ──────────────────────────────────────────────────────

public class World
{
    public Dictionary<Guid, User> Users = new();
    public Dictionary<string, List<Delegate>> Handlers = new();
    public ConcurrentBag<Guid> Lookups = new();

    public void Add(Guid id)
    {
        var u = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty("Username").SetValue(u, "u" + id.ToString("N").Substring(0, 6));
        typeof(User).GetProperty("Id").SetValue(u, id);
        Users[id] = u;
    }

    public static object Create(Type t, World w)
    {
        var p = typeof(DispatchProxy).GetMethods().First(m => m.Name == "Create" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 2)
            .MakeGenericMethod(t, typeof(WorldProxy)).Invoke(null, null);
        ((WorldProxy)p).W = w;
        return p;
    }
}

public sealed class Services : IServiceProvider
{
    private readonly World _w;
    public Services(World w) { _w = w; }
    public object GetService(Type t) => t.IsInterface ? World.Create(t, _w) : null;
}

public class WorldProxy : DispatchProxy
{
    public World W;

    protected override object Invoke(MethodInfo m, object[] args)
    {
        if (m.Name.StartsWith("add_"))
        {
            var name = m.Name.Substring(4);
            lock (W.Handlers) { if (!W.Handlers.TryGetValue(name, out var l)) W.Handlers[name] = l = new(); l.Add((Delegate)args[0]); }
            return null;
        }
        if (m.Name.StartsWith("remove_"))
        {
            var name = m.Name.Substring(7);
            lock (W.Handlers) { if (W.Handlers.TryGetValue(name, out var l)) l.Remove((Delegate)args[0]); }
            return null;
        }
        switch (m.Name)
        {
            case "GetUserById":
                var id = (Guid)args[0];
                W.Lookups.Add(id);
                return W.Users.TryGetValue(id, out var u) ? u : null;
            case "GetUserDto":
                var user = (User)args[0];
                return new UserDto
                {
                    Id = user.Id,
                    Name = user.Username,
                    Policy = new UserPolicy { AuthenticationProviderId = "x", PasswordResetProviderId = "x" },
                    Configuration = new UserConfiguration(),
                };
            case "GetVirtualFolders":
                return new List<MediaBrowser.Model.Entities.VirtualFolderInfo>();
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
