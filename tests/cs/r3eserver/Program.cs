using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions.Json;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

// ─────────────────────────────────────────────────────────────────────────────
// Server-side fixes from the second review (r2-12-04, r2-13-01, r2-13-03, r2-13-04,
// r2-14-02, r2-14-03, r2-14-05, and the fixed .tmp names).
//
//  - /update: a profile that vanishes between the first check and the account write is
//    refused before anything is written; a policy write that throws takes a picture it just
//    saved with it.
//  - /library-artwork: refuses a library the profile cannot see, and writes nothing for a
//    refusal.
//  - GET /devices: legacy names are repaired once, not on every call.
//  - /admin/avatars: the cap holds under parallel adds; a failed save leaves no files and no
//    row; /admin/panic-code with no body is a 400.
//  - CopyLibraryAvatar stages under a name of its own, so a second save at once, or a stale
//    fixed-name file, does not fail the first.
//  - Making a profile its own account removes the original picture once copied.
//
// Run against a build of the commit before these fixes, the assertions marked in the
// output as red fail; that is how this harness was proven to be a test.
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

var ctrlType = asm.GetType("Jellyfin.Profiles.Controllers.ProfilesController", true);
var pluginType = asm.GetType("Jellyfin.Profiles.Plugin", true);
var cfgType = asm.GetType("Jellyfin.Profiles.Configuration.PluginConfiguration", true);
var mapType = asm.GetType("Jellyfin.Profiles.Configuration.ProfileMapping", true);
var deviceType = asm.GetType("Jellyfin.Profiles.Configuration.KnownDevice", true);
var avatarItemType = asm.GetType("Jellyfin.Profiles.Configuration.AvatarLibraryItem", true);
var hasher = asm.GetType("Jellyfin.Profiles.Auth.PinHasher", true);
var baseType = asm.GetType("Jellyfin.Profiles.Controllers.ProfilesBaseController", true);

var tempDir = Path.Combine(Path.GetTempPath(), "bonfire-r3eserver-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
var plugin = Activator.CreateInstance(pluginType, new StubPaths(tempDir), new StubXml());
var config = pluginType.GetProperty("Configuration").GetValue(plugin);
config.GetType().GetProperty("BonfireOn").SetValue(config, true);
var mappings = (IList)cfgType.GetProperty("Mappings").GetValue(config);
var knownDevices = (IList)cfgType.GetProperty("KnownDevices").GetValue(config);
var avatarLibrary = (IList)cfgType.GetProperty("AvatarLibrary").GetValue(config);
var configLock = baseType.GetField("ConfigLock", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null);

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
{
    var j = JsonDefaults.PascalCaseOptions;
    json.ReadCommentHandling = j.ReadCommentHandling;
    json.DefaultIgnoreCondition = j.DefaultIgnoreCondition;
    json.NumberHandling = j.NumberHandling;
    json.Converters.Clear();
    foreach (var c in j.Converters) json.Converters.Add(c);
    json.PropertyNamingPolicy = j.PropertyNamingPolicy;
}

var MAIN = Guid.NewGuid();
var KID = Guid.NewGuid();
var ADMIN = Guid.NewGuid();
var L1 = Guid.NewGuid();
var L2 = Guid.NewGuid();
const string PIN = "1234";

var world = new World();
world.Libraries.AddRange(new[] { L1, L2 });
world.Add(MAIN, "home", new UserPolicy { EnableAllFolders = true });
world.Add(KID, "home_Kid", new UserPolicy { EnableAllFolders = false, EnabledFolders = new[] { L1 } });
world.Add(ADMIN, "root", new UserPolicy { IsAdministrator = true, EnableAllFolders = true });

object Map(Guid profile, Guid master, string name)
{
    var m = Activator.CreateInstance(mapType);
    mapType.GetProperty("ProfileUserId").SetValue(m, profile);
    mapType.GetProperty("MasterUserId").SetValue(m, master);
    mapType.GetProperty("ProfileName").SetValue(m, name);
    mappings.Add(m);
    return m;
}
var mainRow = Map(MAIN, MAIN, "home");
mapType.GetProperty("PinHash").SetValue(mainRow, hasher.GetMethod("Hash").Invoke(null, new object[] { PIN }));
var kidRow = Map(KID, MAIN, "Kid");

var users = World.Create<IUserManager>(world);
var library = World.Create<ILibraryManager>(world);
var sessions = World.Create<ISessionManager>(world);

ControllerBase MakeController(Guid caller)
{
    var ctor = ctrlType.GetConstructors().First();
    var args = ctor.GetParameters().Select(p =>
        p.ParameterType == typeof(IUserManager) ? users
        : p.ParameterType == typeof(ILibraryManager) ? library
        : p.ParameterType == typeof(ISessionManager) ? sessions
        : p.ParameterType.Name.StartsWith("ILogger") ? Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(ctrlType))
        : (object)null).ToArray();
    var c = (ControllerBase)ctor.Invoke(args);
    var http = new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", caller.ToString()) }, "test")),
    };
    http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7");
    http.Request.Headers["X-Bonfire-Master-Pin"] = PIN;
    c.ControllerContext = new ControllerContext { HttpContext = http };
    return c;
}

(int status, string message, object value) Call(string method, string body, Guid? caller = null)
{
    var r = ctrlType.GetMethod(method);
    if (r == null) return (-404, "no route " + method, null);
    var c = MakeController(caller ?? MAIN);
    var invokeArgs = r.GetParameters().Select(p => body == null ? null : JsonSerializer.Deserialize(body, p.ParameterType, json)).ToArray();

    object result;
    try { result = r.Invoke(c, invokeArgs); }
    catch (TargetInvocationException ex) { return (-500, ex.InnerException?.GetType().Name + ": " + ex.InnerException?.Message, null); }
    if (result is Task task)
    {
        try { task.GetAwaiter().GetResult(); }
        catch (Exception ex) { return (-500, ex.GetType().Name + ": " + ex.Message, null); }
        result = task.GetType().GetProperty("Result")?.GetValue(task);
    }
    var inner = result?.GetType().GetProperty("Result")?.GetValue(result) ?? result;
    return inner switch
    {
        ObjectResult o => (o.StatusCode ?? 200, o.Value as string, o.Value),
        StatusCodeResult s => (s.StatusCode, null, null),
        _ => (-1, inner?.GetType().Name, null),
    };
}

const string PNG = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";
var imageFolder = Path.Combine(tempDir, "plugins", "ProfilesManagement");
var artFolder = Path.Combine(imageFolder, "libraryart");
var avatarFolder = Path.Combine(imageFolder, "avatars");
string[] FilesIn(string folder, string pattern = "*") => Directory.Exists(folder) ? Directory.GetFiles(folder, pattern) : new string[0];
var gate = (SemaphoreSlim)asm.GetType("Jellyfin.Profiles.Auth.ProfilePolicySync", true)
    .GetField("Gate", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).GetValue(null);

// ── r2-12-04: /update ────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── /update: refusals and failures leave nothing behind (r2-12-04) ──");

world.FailPolicyWrite = true;
var r = Call("UpdateProfile", $"{{\"profileId\":\"{KID}\",\"profileName\":\"Kid\",\"masterPin\":\"{PIN}\",\"profileImage\":\"{PNG}\"}}");
world.FailPolicyWrite = false;
Ok("a save whose policy write fails is reported as failed", r.status == -500, r.status + " " + r.message);
Ok("and the picture it saved is taken back", FilesIn(imageFolder, KID + ".*").Length == 0, string.Join(",", FilesIn(imageFolder, KID + ".*")));
Ok("and the mapping has no picture", mapType.GetProperty("ProfileImage").GetValue(kidRow) == null);

// The profile is removed while the save waits for the policy sync's gate.
gate.Wait();
var held = Task.Run(() => Call("UpdateProfile", $"{{\"profileId\":\"{KID}\",\"profileName\":\"Ghost\",\"masterPin\":\"{PIN}\",\"profileImage\":\"{PNG}\"}}"));
held.Wait(600);
mappings.Remove(kidRow);
gate.Release();
held.Wait(10000);
if (held.IsCompletedSuccessfully) r = held.Result; else r = (-2, "not finished", (object)null);
Ok("a profile deleted while its save waits is refused, not answered 200", r.status == 404 || r.status == 401, r.status + " " + r.message);
Ok("and its account is not renamed", world.Users[KID].Username == "home_Kid", world.Users[KID].Username);
Ok("and the picture it saved is taken back", FilesIn(imageFolder, KID + ".*").Length == 0, string.Join(",", FilesIn(imageFolder, KID + ".*")));
mappings.Add(kidRow);

// ── r2-13-01, r2-13-03: /library-artwork ─────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── /library-artwork (r2-13-01, r2-13-03) ────────────────────────");

r = Call("SetLibraryArtwork", $"{{\"profileId\":\"{KID}\",\"libraryId\":\"{L2}\",\"mode\":\"custom\",\"masterPin\":\"{PIN}\",\"image\":\"{PNG}\"}}");
Ok("artwork for a library the profile cannot see is refused", r.status == 400, r.status + " " + r.message);
Ok("and nothing is written for it", FilesIn(artFolder).Length == 0, string.Join(",", FilesIn(artFolder)));
Ok("and no row is stored", ((IEnumerable)(mapType.GetProperty("LibraryArtwork").GetValue(kidRow) ?? new object[0])).Cast<object>().Count() == 0);

r = Call("SetLibraryArtwork", $"{{\"profileId\":\"{KID}\",\"libraryId\":\"{L1}\",\"mode\":\"custom\",\"masterPin\":\"{PIN}\",\"image\":\"{PNG}\"}}");
Ok("artwork for a library it can see is saved", r.status == 200, r.status + " " + r.message);
Ok("as one file", FilesIn(artFolder, "*.png").Length == 1);

r = Call("SetLibraryArtwork", $"{{\"profileId\":\"{MAIN}\",\"libraryId\":\"{L2}\",\"mode\":\"custom\",\"masterPin\":\"{PIN}\",\"image\":\"{PNG}\"}}");
Ok("the main account, which sees every library, can set one", r.status == 200, r.status + " " + r.message);

r = Call("SetLibraryArtwork", $"{{\"profileId\":\"{KID}\",\"libraryId\":\"{L2}\",\"mode\":\"inherit\",\"masterPin\":\"{PIN}\"}}");
Ok("putting a library back to inherit is not refused, whatever the profile sees", r.status == 200, r.status + " " + r.message);

// ── r2-13-04: GET /devices ───────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── GET /devices (r2-13-04) ──────────────────────────────────────");

object Device(string id, string name)
{
    var d = Activator.CreateInstance(deviceType);
    deviceType.GetProperty("DeviceId").SetValue(d, id);
    deviceType.GetProperty("DeviceName").SetValue(d, name);
    deviceType.GetProperty("Client").SetValue(d, "Jellyfin Android");
    deviceType.GetProperty("LastSeen").SetValue(d, DateTime.UtcNow);
    deviceType.GetProperty("MasterUserId").SetValue(d, MAIN);
    knownDevices.Add(d);
    return d;
}
string NameOf(object d) => (string)deviceType.GetProperty("DeviceName").GetValue(d);
var first = Device("dev-one", "Pixel+8");
r = Call("GetDevices", null);
Ok("the first look repairs a stored legacy name", r.status == 200 && NameOf(first) == "Pixel 8", r.status + " " + NameOf(first));
var second = Device("dev-two", "Tab+9");
r = Call("GetDevices", null);
Ok("and later looks do not walk the list again", r.status == 200 && NameOf(second) == "Tab+9", r.status + " " + NameOf(second));
Ok("the list is still returned", r.status == 200 && r.value is IEnumerable<object> e && e.Count() >= 2);

// ── r2-14-03: /admin/panic-code ──────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── /admin/panic-code with no body (r2-14-03) ────────────────────");
r = Call("SetPanicCode", "null", ADMIN);
Ok("no body is a 400, not a crash", r.status == 400, r.status + " " + r.message);

// ── r2-14-02: /admin/avatars ─────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── /admin/avatars (r2-14-02) ────────────────────────────────────");

StubXml.Fail = true;
r = Call("AddLibraryAvatar", $"{{\"image\":\"{PNG}\"}}", ADMIN);
StubXml.Fail = false;
Ok("an add whose save throws is reported as failed", r.status == -500 || r.status >= 400, r.status + " " + r.message);
Ok("and leaves no row", avatarLibrary.Count == 0, avatarLibrary.Count.ToString());
Ok("and no files", FilesIn(avatarFolder).Length == 0, string.Join(",", FilesIn(avatarFolder)));

r = Call("AddLibraryAvatar", $"{{\"image\":\"{PNG}\",\"displayName\":\"Fox\"}}", ADMIN);
Ok("an add works", r.status == 200 && avatarLibrary.Count == 1, r.status + " " + r.message);
var libraryAvatarId = (string)avatarItemType.GetProperty("Id").GetValue(avatarLibrary[0]);

const int Cap = 200;
for (var i = avatarLibrary.Count; i < Cap - 10; i++)
{
    var it = Activator.CreateInstance(avatarItemType);
    avatarItemType.GetProperty("Id").SetValue(it, "pre" + i);
    avatarItemType.GetProperty("DisplayName").SetValue(it, "x");
    avatarItemType.GetProperty("Extension").SetValue(it, ".png");
    avatarLibrary.Add(it);
}
var before = avatarLibrary.Count;
var filesBefore = FilesIn(avatarFolder).Length;
var barrier = new Barrier(40);
var adds = Enumerable.Range(0, 40).Select(_ => Task.Run(() =>
{
    barrier.SignalAndWait();
    return Call("AddLibraryAvatar", $"{{\"image\":\"{PNG}\"}}", ADMIN);
})).ToArray();
Task.WaitAll(adds);
Ok("forty adds at once do not take the library past its cap", avatarLibrary.Count == Cap, avatarLibrary.Count + " rows, cap " + Cap);
Ok("and the ones turned away leave no files", FilesIn(avatarFolder).Length - filesBefore == Cap - before,
   (FilesIn(avatarFolder).Length - filesBefore) + " new files for " + (Cap - before) + " new rows");
Ok("the ones turned away were told so", adds.Count(a => a.Result.status == 200) == Cap - before, adds.Count(a => a.Result.status == 200).ToString());

// ── fixed .tmp names in CopyLibraryAvatar ────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── CopyLibraryAvatar stages under a name of its own ─────────────");

string ArtBase(Guid lib) => KID.ToString("N") + "_" + lib.ToString("N");
Directory.CreateDirectory(artFolder);
foreach (var f in FilesIn(artFolder)) File.Delete(f);

// A file already sitting at the old fixed staging name, held open: the old code created
// its temp at exactly that name and failed.
using (var hold = new FileStream(Path.Combine(artFolder, ArtBase(L1) + ".tmp"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
{
    r = Call("SetLibraryArtwork", $"{{\"profileId\":\"{KID}\",\"libraryId\":\"{L1}\",\"mode\":\"custom\",\"masterPin\":\"{PIN}\",\"avatarLibraryId\":\"starter-fox\"}}");
    Ok("a starter avatar is copied though the fixed staging name is taken", r.status == 200, r.status + " " + r.message);
    Ok("and lands", FilesIn(artFolder, ArtBase(L1) + ".*").Any(f => !f.EndsWith(".tmp")));
}
using (var hold = new FileStream(Path.Combine(artFolder, ArtBase(L1) + ".png.tmp"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
{
    r = Call("SetLibraryArtwork", $"{{\"profileId\":\"{KID}\",\"libraryId\":\"{L1}\",\"mode\":\"custom\",\"masterPin\":\"{PIN}\",\"avatarLibraryId\":\"{libraryAvatarId}\"}}");
    Ok("a library avatar is copied though the fixed staging name is taken", r.status == 200, r.status + " " + r.message);
}

// ── r2-14-05: release removes the original picture ───────────────────────────
Console.WriteLine();
Console.WriteLine("── making a profile its own account (r2-14-05) ──────────────────");

Directory.CreateDirectory(imageFolder);
File.WriteAllBytes(Path.Combine(imageFolder, KID + ".png"), new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 });
File.WriteAllBytes(Path.Combine(imageFolder, KID + "_t.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47, 4 });
var copy = ctrlType.GetMethod("CopyPictureToAccountAsync", BindingFlags.Instance | BindingFlags.NonPublic);
Ok("the copy step is there", copy != null);
if (copy != null)
{
    ((Task)copy.Invoke(MakeController(ADMIN), new object[] { KID })).GetAwaiter().GetResult();
    var account = Path.Combine(tempDir, "users", world.Users[KID].Username, "profile.png");
    Ok("the account gets the picture", File.Exists(account), account);
    Ok("and the original is not left in Bonfire's folder", FilesIn(imageFolder, KID + "*").Length == 0, string.Join(",", FilesIn(imageFolder, KID + "*")));
}

try { Directory.Delete(tempDir, true); } catch { }

Console.WriteLine();
if (fails.Count > 0)
{
    Console.WriteLine("  Failures:");
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
    public Dictionary<Guid, UserPolicy> Policies = new();
    public List<Guid> Libraries = new();
    public bool FailPolicyWrite;

    public void Add(Guid id, string name, UserPolicy policy)
    {
        var u = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty("Username").SetValue(u, name);
        typeof(User).GetProperty("Id").SetValue(u, id);
        typeof(User).GetProperty("AuthenticationProviderId").SetValue(u, "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider");
        Users[id] = u;
        Policies[id] = policy;
    }

    // A copy each time, as Jellyfin builds a fresh DTO: a change only sticks if the route
    // writes it back through UpdatePolicyAsync.
    public static UserPolicy Copy(UserPolicy p) => new UserPolicy
    {
        AuthenticationProviderId = "x",
        PasswordResetProviderId = "x",
        IsAdministrator = p.IsAdministrator,
        MaxParentalRating = p.MaxParentalRating,
        MaxParentalSubRating = p.MaxParentalSubRating,
        EnableAllFolders = p.EnableAllFolders,
        EnabledFolders = p.EnabledFolders?.ToArray() ?? Array.Empty<Guid>(),
        BlockedMediaFolders = p.BlockedMediaFolders?.ToArray(),
        BlockedTags = p.BlockedTags?.ToArray() ?? Array.Empty<string>(),
        AllowedTags = p.AllowedTags?.ToArray() ?? Array.Empty<string>(),
    };

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
            case "get_Sessions":
                return new List<SessionInfo>();
            case "get_Users":
                return W.Users.Values.ToList();
            case "GetUserDto":
                var user = (User)args[0];
                return new UserDto
                {
                    Id = user.Id,
                    Name = user.Username,
                    Policy = World.Copy(W.Policies[user.Id]),
                    Configuration = new UserConfiguration(),
                };
            case "UpdatePolicyAsync":
                if (W.FailPolicyWrite) return Task.FromException(new InvalidOperationException("policy write failed"));
                W.Policies[(Guid)args[0]] = World.Copy((UserPolicy)args[1]);
                return Task.CompletedTask;
            case "GetVirtualFolders":
                return W.Libraries.Select(id => new MediaBrowser.Model.Entities.VirtualFolderInfo { ItemId = id.ToString("N"), Name = "lib" }).ToList();
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
    public static bool Fail;
    public void SerializeToFile(object obj, string file) { if (Fail) throw new IOException("disk full"); }
    public object DeserializeFromFile(Type type, string file)
        => throw new FileNotFoundException("stub serializer", file);
    public object DeserializeFromStream(Type type, Stream stream)
        => throw new NotSupportedException();
    public object DeserializeFromBytes(Type type, byte[] buffer)
        => throw new NotSupportedException();
}
