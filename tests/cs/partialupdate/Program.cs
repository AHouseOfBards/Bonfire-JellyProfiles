using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions.Json;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

// ─────────────────────────────────────────────────────────────────────────────
// A field left out of a request keeps its stored value — on every route that says so.
//
// The developer API promised that, with one exception that turned out to be three:
//
//  - R-1144, /update: a left-out maxParentalRating removed the profile's own rating, and a
//    left-out enabledFolders gave it every library. A client sending only
//    {profileId, profileName} — a rename — lifted a child's limits.
//  - R-1326, /limits/{id}: hoursEnabled, dailyMinutes and windows were plain bool, int and
//    list, so a client changing only the daily limit switched the hours off.
//  - R-1159, /bonfire/settings: the two hide flags were plain bool, so a body with only the
//    home-network setting un-hid the household's hidden profiles.
//
// "No rating limit" and "every library" are explicit values now: maxParentalRating "" and
// allLibraries true. This harness checks the partial requests keep, and that each explicit
// value still does what it says.
//
// Bodies are JSON, read with the options Jellyfin's MVC uses, then passed to the real
// route. Setting properties by reflection would skip the setter for a left-out field the
// same way, but it would not show what a real body binds to — and weekendMinutes tells
// "left out" from "null" by whether its setter ran.
//
// Against 83969c0 the keep assertions fail.
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
var hasher = asm.GetType("Jellyfin.Profiles.Auth.PinHasher", true);

var tempDir = Path.Combine(Path.GetTempPath(), "bonfire-partialupdate-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
var plugin = Activator.CreateInstance(pluginType, new StubPaths(tempDir), new StubXml());
var config = pluginType.GetProperty("Configuration").GetValue(plugin);
var mappings = (IList)cfgType.GetProperty("Mappings").GetValue(config);

// What Jellyfin.Server's AddJellyfinApi gives MVC: web defaults, then Jellyfin's own
// settings over them (as tests/cs/requestbinding does).
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
var L1 = Guid.NewGuid();
var L2 = Guid.NewGuid();
var L3 = Guid.NewGuid();
const string PIN = "1234";

var world = new World();
world.Libraries.AddRange(new[] { L1, L2, L3 });
world.Add(MAIN, "home", new UserPolicy { EnableAllFolders = true, MaxParentalRating = null });
world.Add(KID, "home_Kid", new UserPolicy());

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

(int status, string message) Call(string method, string body, object route = null)
{
    var r = ctrlType.GetMethod(method);
    if (r == null) return (-404, "no route " + method);
    var ctor = ctrlType.GetConstructors().First();
    var args = ctor.GetParameters().Select(p =>
        p.ParameterType == typeof(IUserManager) ? users
        : p.ParameterType == typeof(ILibraryManager) ? library
        : p.ParameterType.Name.StartsWith("ILogger") ? Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(ctrlType))
        : (object)null).ToArray();
    var c = (ControllerBase)ctor.Invoke(args);
    var http = new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", MAIN.ToString()) }, "test")),
    };
    http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7");
    http.Request.Headers["X-Bonfire-Master-Pin"] = PIN;
    c.ControllerContext = new ControllerContext { HttpContext = http };

    var invokeArgs = r.GetParameters().Select(p =>
        p.ParameterType == typeof(Guid) ? route ?? Guid.Empty
        : JsonSerializer.Deserialize(body, p.ParameterType, json)).ToArray();

    object result;
    try { result = r.Invoke(c, invokeArgs); }
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

UserPolicy Kid() => world.Policies[KID];
string Folders(UserPolicy p) => p.EnableAllFolders ? "all" : string.Join(",", (p.EnabledFolders ?? Array.Empty<Guid>()).Select(g => g == L1 ? "L1" : g == L2 ? "L2" : g == L3 ? "L3" : "?"));
object Row(object row, string prop) => mapType.GetProperty(prop)?.GetValue(row);
string P(UserPolicy p) => "rating=" + (p.MaxParentalRating?.ToString() ?? "none") + " folders=" + Folders(p);

// ── /update ──────────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── /update (R-1144) ─────────────────────────────────────────────");

var r = Call("UpdateProfile", $"{{\"profileId\":\"{KID}\",\"profileName\":\"Kid\",\"masterPin\":\"{PIN}\",\"maxParentalRating\":\"10\",\"enabledFolders\":[\"{L1}\"]}}");
Ok("setup: the form's full save sets a rating and one library", r.status == 200 && Kid().MaxParentalRating == 10 && Folders(Kid()) == "L1",
    r.status + " " + r.message + " " + P(Kid()));

r = Call("UpdateProfile", $"{{\"profileId\":\"{KID}\",\"profileName\":\"Kid\",\"masterPin\":\"{PIN}\"}}");
Ok("a rename alone succeeds", r.status == 200, r.status + " " + r.message);
Ok("and keeps the profile's rating (it was removed)", Kid().MaxParentalRating == 10, P(Kid()));
Ok("and keeps its libraries (it was given every one)", Folders(Kid()) == "L1", P(Kid()));
Ok("and the stored record still says one library", Row(kidRow, "AllLibraries") is false
    && ((IEnumerable<Guid>)Row(kidRow, "EnabledFolders")).SequenceEqual(new[] { L1 }));

r = Call("UpdateProfile", $"{{\"profileId\":\"{KID}\",\"profileName\":\"Kid\",\"masterPin\":\"{PIN}\",\"maxParentalRating\":null,\"enabledFolders\":null}}");
Ok("explicit nulls keep both too", r.status == 200 && Kid().MaxParentalRating == 10 && Folders(Kid()) == "L1", r.status + " " + P(Kid()));

r = Call("UpdateProfile", $"{{\"profileId\":\"{KID}\",\"profileName\":\"Kid\",\"masterPin\":\"{PIN}\",\"maxParentalRating\":\"\"}}");
Ok("maxParentalRating \"\" removes the rating", r.status == 200 && Kid().MaxParentalRating == null, r.status + " " + r.message + " " + P(Kid()));
Ok("and leaves the libraries alone", Folders(Kid()) == "L1", P(Kid()));

r = Call("UpdateProfile", $"{{\"profileId\":\"{KID}\",\"profileName\":\"Kid\",\"masterPin\":\"{PIN}\",\"maxParentalRating\":\"14\"}}");
Ok("a score sets it", r.status == 200 && Kid().MaxParentalRating == 14, r.status + " " + P(Kid()));

r = Call("UpdateProfile", $"{{\"profileId\":\"{KID}\",\"profileName\":\"Kid\",\"masterPin\":\"{PIN}\",\"allLibraries\":true}}");
Ok("allLibraries true gives every library", r.status == 200 && Folders(Kid()) == "all", r.status + " " + r.message + " " + P(Kid()));
Ok("and is recorded as every library", Row(kidRow, "AllLibraries") is true);
Ok("and leaves the rating alone", Kid().MaxParentalRating == 14, P(Kid()));

r = Call("UpdateProfile", $"{{\"profileId\":\"{KID}\",\"profileName\":\"Kid\",\"masterPin\":\"{PIN}\",\"enabledFolders\":[\"{L2}\",\"{L3}\"]}}");
Ok("a list sets that list", r.status == 200 && Folders(Kid()) == "L2,L3" && Row(kidRow, "AllLibraries") is false, r.status + " " + P(Kid()));

r = Call("UpdateProfile", $"{{\"profileId\":\"{KID}\",\"profileName\":\"Kid\",\"masterPin\":\"{PIN}\",\"enabledFolders\":[]}}");
Ok("[] is no library at all", r.status == 200 && Folders(Kid()) == "", r.status + " " + P(Kid()));

r = Call("UpdateProfile", $"{{\"profileId\":\"{KID}\",\"profileName\":\"Kid\",\"masterPin\":\"{PIN}\",\"allLibraries\":true,\"enabledFolders\":[\"{L1}\"]}}");
Ok("allLibraries with a list is refused, not guessed at", r.status == 400, r.status + " " + r.message);
Ok("and changes nothing", Folders(Kid()) == "", P(Kid()));

r = Call("UpdateProfile", $"{{\"profileId\":\"{KID}\",\"profileName\":\"Kid\",\"masterPin\":\"{PIN}\",\"maxParentalRating\":\"PG\"}}");
Ok("a rating that isn't a score is refused (it read as no limit)", r.status == 400, r.status + " " + r.message);
Ok("and the rating stays", Kid().MaxParentalRating == 14, P(Kid()));

// ── /create ──────────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── /create ──────────────────────────────────────────────────────");

r = Call("CreateProfile", $"{{\"profileName\":\"Twin\",\"masterPin\":\"{PIN}\",\"allLibraries\":true,\"enabledFolders\":[\"{L1}\"]}}");
Ok("create refuses allLibraries with a list too", r.status == 400, r.status + " " + r.message);
r = Call("CreateProfile", $"{{\"profileName\":\"Twin\",\"masterPin\":\"{PIN}\",\"maxParentalRating\":\"PG\"}}");
Ok("and a rating that isn't a score", r.status == 400, r.status + " " + r.message);

// ── /limits/{id} ─────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── /limits/{id} (R-1326) ────────────────────────────────────────");

object Viewing() => Row(kidRow, "Viewing");
object V(string prop) { var v = Viewing(); return v?.GetType().GetProperty(prop)?.GetValue(v); }
int WindowCount() => V("Windows") is IList l ? l.Count : -1;
string Rules() => Viewing() == null ? "none" : $"hours={V("HoursEnabled")} windows={WindowCount()} daily={V("DailyMinutes")} weekend={V("WeekendMinutes") ?? "same"}";

r = Call("SetViewingLimits", $"{{\"masterPin\":\"{PIN}\",\"hoursEnabled\":true,\"windows\":[{{\"days\":31,\"start\":\"15:00\",\"end\":\"20:00\"}}],\"dailyMinutes\":120,\"weekendMinutes\":240}}", KID);
Ok("setup: the form's full save", r.status == 200 && Rules() == "hours=True windows=1 daily=120 weekend=240", r.status + " " + r.message + " " + Rules());

r = Call("SetViewingLimits", $"{{\"masterPin\":\"{PIN}\",\"dailyMinutes\":90}}", KID);
Ok("changing only the daily limit succeeds", r.status == 200, r.status + " " + r.message);
Ok("and keeps the hours switched on (they went off)", V("HoursEnabled") is true, Rules());
Ok("and keeps the windows (they were emptied)", WindowCount() == 1, Rules());
Ok("and keeps the weekend limit", V("WeekendMinutes") is int w && w == 240, Rules());
Ok("and sets the daily limit", V("DailyMinutes") is int d && d == 90, Rules());

r = Call("SetViewingLimits", $"{{\"masterPin\":\"{PIN}\",\"hoursEnabled\":false}}", KID);
Ok("switching only the hours off keeps the daily limit (it went to 0)", r.status == 200 && V("DailyMinutes") is int d2 && d2 == 90 && V("HoursEnabled") is false,
    r.status + " " + Rules());

r = Call("SetViewingLimits", $"{{\"masterPin\":\"{PIN}\"}}", KID);
Ok("a body with nothing in it changes nothing", r.status == 200 && Rules() == "hours=False windows=1 daily=90 weekend=240", r.status + " " + Rules());

r = Call("SetViewingLimits", $"{{\"masterPin\":\"{PIN}\",\"weekendMinutes\":null}}", KID);
Ok("weekendMinutes null still means the same as every day", r.status == 200 && V("WeekendMinutes") == null && V("DailyMinutes") is int d3 && d3 == 90,
    r.status + " " + Rules());

r = Call("SetViewingLimits", $"{{\"masterPin\":\"{PIN}\",\"windows\":[],\"dailyMinutes\":0,\"hoursEnabled\":false}}", KID);
Ok("explicit off, 0 and [] still turn it all off", r.status == 200 && Viewing() == null, r.status + " " + Rules());

// ── /bonfire/settings ────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── /bonfire/settings (R-1159) ───────────────────────────────────");

r = Call("UpdateBonfireSettings", "{\"hideMySubProfilesFromOthers\":true,\"hideOthersSubProfilesFromMe\":true,\"allowHouseholdLanBypass\":false}");
Ok("setup: both hide flags on", r.status == 200 && Row(mainRow, "HideMySubProfilesFromOthers") is true && Row(mainRow, "HideOthersSubProfilesFromMe") is true,
    r.status + " " + r.message);

r = Call("UpdateBonfireSettings", "{\"allowHouseholdLanBypass\":true}");
Ok("changing only the home-network setting succeeds", r.status == 200 && Row(mainRow, "AllowHouseholdLanBypass") is true, r.status + " " + r.message);
Ok("and keeps my profiles hidden from others (they were shown)", Row(mainRow, "HideMySubProfilesFromOthers") is true);
Ok("and keeps others' profiles hidden from me", Row(mainRow, "HideOthersSubProfilesFromMe") is true);

r = Call("UpdateBonfireSettings", "{\"hideMySubProfilesFromOthers\":false}");
Ok("an explicit false still un-hides, and only that one", r.status == 200 && Row(mainRow, "HideMySubProfilesFromOthers") is false
    && Row(mainRow, "HideOthersSubProfilesFromMe") is true && Row(mainRow, "AllowHouseholdLanBypass") is true, r.status + " " + r.message);

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
    public Dictionary<Guid, UserPolicy> Policies = new();
    public List<Guid> Libraries = new();

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
    public void SerializeToFile(object obj, string file) { }
    public object DeserializeFromFile(Type type, string file)
        => throw new FileNotFoundException("stub serializer", file);
    public object DeserializeFromStream(Type type, Stream stream)
        => throw new NotSupportedException();
    public object DeserializeFromBytes(Type type, byte[] buffer)
        => throw new NotSupportedException();
}
