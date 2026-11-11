using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging.Abstractions;

// ─────────────────────────────────────────────────────────────────────────────
// A change to a main account reaches its profiles when it is made (R-1301).
//
// Until MasterUpdateListener, a library taken from a main account in Dashboard -> Users, or
// a lower rating, reached a profile only when the profile was next opened. The listener
// subscribes to IUserManager.OnUserUpdated and re-syncs that household's profiles off the
// event thread. Our own policy writes raise the same event (for the profile), so it must
// not trigger itself: checked three ways below, including the worst case of something else
// writing the main account on every pass.
//
// Red against the build without the listener: every assertion about it fails.
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
var listenerType = asm.GetType("Jellyfin.Profiles.Auth.MasterUpdateListener");
Ok("the listener exists", listenerType != null);
if (listenerType == null)
{
    Console.WriteLine(pass + " passed, " + fails.Count + " failed");
    return 1;
}

var tempDir = Path.Combine(Path.GetTempPath(), "bonfire-masterupdate-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
var plugin = Activator.CreateInstance(pluginType, new StubPaths(tempDir), new StubXml());
var config = pluginType.GetProperty("Configuration").GetValue(plugin);
var mappings = (IList)cfgType.GetProperty("Mappings").GetValue(config);

void Map(Guid profile, Guid master)
{
    var m = Activator.CreateInstance(mapType);
    mapType.GetProperty("ProfileUserId").SetValue(m, profile);
    mapType.GetProperty("MasterUserId").SetValue(m, master);
    mapType.GetProperty("ProfileName").SetValue(m, "p");
    mappings.Add(m);
}

var world = new World();
var MAIN = Guid.NewGuid(); var KID = Guid.NewGuid(); var TEEN = Guid.NewGuid();
var STRANGER = Guid.NewGuid(); var OTHER = Guid.NewGuid();
foreach (var id in new[] { MAIN, KID, TEEN, STRANGER, OTHER }) world.Add(id);
Map(MAIN, MAIN); Map(KID, MAIN); Map(TEEN, MAIN); Map(OTHER, OTHER);

var users = (IUserManager)World.Create(typeof(IUserManager), world);
var services = new Services(world);
var loggerType = typeof(NullLogger<>).MakeGenericType(listenerType);
var listener = Activator.CreateInstance(listenerType, users, Activator.CreateInstance(loggerType), services);
var hosted = (Microsoft.Extensions.Hosting.IHostedService)listener;
hosted.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

void Raise(Guid id)
{
    foreach (var h in world.Handlers.TryGetValue("OnUserUpdated", out var list) ? list.ToList() : new List<Delegate>())
        h.DynamicInvoke(users, new GenericEventArgs<User>(world.Users[id]));
}
int Reads(Guid id) => world.Lookups.Count(x => x == id);
void Settle() { Thread.Sleep(700); }
void Reset() { world.Lookups.Clear(); world.OnLookup = null; }

Console.WriteLine();
Console.WriteLine("── The listener subscribes ─────────────────────────────────────");
Ok("to OnUserUpdated", world.Handlers.TryGetValue("OnUserUpdated", out var subs) && subs.Count == 1);

Console.WriteLine();
Console.WriteLine("── A change to a main account reaches its profiles ─────────────");
{
    Reset();
    Raise(MAIN);
    Settle();
    Ok("each profile of the household is synced (" + Reads(KID) + ", " + Reads(TEEN) + ")", Reads(KID) >= 1 && Reads(TEEN) >= 1);
    Ok("another household's profile is not touched", Reads(OTHER) == 0);
}

Console.WriteLine();
Console.WriteLine("── One pass over the profiles reads what they share once ────────");
{
    // Each profile used to list the server's libraries three or four times and ask for the main
    // account's policy again: a save of a main account with N profiles cost about 4N and 2N reads.
    Reset();
    world.FolderReads = 0; world.Dtos.Clear();
    Raise(MAIN);
    Settle();
    Ok("both profiles were synced (" + Reads(KID) + ", " + Reads(TEEN) + ")", Reads(KID) >= 1 && Reads(TEEN) >= 1);
    Ok("the server's library list is read once for two profiles (read " + world.FolderReads + ")", world.FolderReads == 1);
    Ok("the main account's policy is built once for two profiles (built " + world.Dtos.Count(x => x == MAIN) + ")",
        world.Dtos.Count(x => x == MAIN) == 1);
    Ok("each profile's own policy is still read (" + world.Dtos.Count(x => x == KID) + ", " + world.Dtos.Count(x => x == TEEN) + ")",
        world.Dtos.Count(x => x == KID) == 1 && world.Dtos.Count(x => x == TEEN) == 1);
}

Console.WriteLine();
Console.WriteLine("── Changes that are not a main account's do nothing ────────────");
{
    Reset();
    Raise(KID);
    Raise(STRANGER);
    Raise(OTHER);   // a main account with no profile of its own
    Settle();
    Ok("a profile's own update starts nothing", Reads(KID) == 0 && Reads(TEEN) == 0);
    Ok("nor does an account outside every household", Reads(MAIN) == 0 && Reads(STRANGER) == 0);
}

Console.WriteLine();
Console.WriteLine("── It runs off the event thread ────────────────────────────────");
{
    Reset();
    var release = new ManualResetEventSlim(false);
    world.OnLookup = id => { if (id == KID) release.Wait(5000); };
    var sw = System.Diagnostics.Stopwatch.StartNew();
    Raise(MAIN);
    var returnedIn = sw.ElapsedMilliseconds;
    Ok("the handler returns while the sync is still waiting (" + returnedIn + " ms)", returnedIn < 1000);
    release.Set();
    Settle();
    Ok("and the sync then completes", Reads(KID) >= 1);
}

Console.WriteLine();
Console.WriteLine("── It cannot trigger itself ────────────────────────────────────");
{
    // Our own write to a profile raises the event for the profile. Modelled as a raise from
    // inside the lookup the sync makes.
    Reset();
    world.OnLookup = id => { if (id == KID || id == TEEN) Raise(id); };
    Raise(MAIN);
    Settle();
    var first = Reads(KID);
    Settle();
    Ok("a profile's own update raised during the sync does not start another (" + first + " read)", first == 1 && Reads(KID) == first);

    // Worst case: something else writes the main account on every pass.
    Reset();
    world.OnLookup = id => { if (id == KID) Raise(MAIN); };
    Raise(MAIN);
    Settle();
    var a = Reads(KID);
    Settle();
    Ok("the main account re-raised on every pass ends after two passes (" + a + ")", a == 2 && Reads(KID) == a);

    // And a burst while one is running is one more pass, not one each.
    Reset();
    var gate = new ManualResetEventSlim(false);
    world.OnLookup = id => { if (id == KID) gate.Wait(5000); };
    Raise(MAIN);
    Thread.Sleep(150);
    for (int i = 0; i < 10; i++) Raise(MAIN);
    gate.Set();
    Settle();
    Ok("ten updates during a pass cost one more pass (" + Reads(KID) + ")", Reads(KID) == 2);

    // After it has finished, a fresh change starts a fresh run.
    Reset();
    Raise(MAIN);
    Settle();
    Ok("a later change is not swallowed by the earlier ones", Reads(KID) == 1);
}

hosted.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
Ok("stopping unsubscribes", world.Handlers.Values.All(l => l.Count == 0));

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
    public Action<Guid> OnLookup;
    public int FolderReads;
    public ConcurrentBag<Guid> Dtos = new();

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
                W.Lookups.Add(id); W.OnLookup?.Invoke(id);
                return W.Users.TryGetValue(id, out var u) ? u : null;
            case "GetUserDto":
                var user = (User)args[0];
                W.Dtos.Add(user.Id);
                return new UserDto
                {
                    Id = user.Id,
                    Name = user.Username,
                    Policy = new UserPolicy { AuthenticationProviderId = "x", PasswordResetProviderId = "x" },
                    Configuration = new UserConfiguration(),
                };
            case "GetVirtualFolders":
                System.Threading.Interlocked.Increment(ref W.FolderReads);
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
