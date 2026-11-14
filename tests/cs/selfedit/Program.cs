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
// A profile changes its own name and picture (P2-713), and nothing else.
//
// POST /me is the route. The caller is the profile, with no main PIN, so the whole point is
// what it cannot do: every request below tries to get past that, and the profile's rating,
// PIN, libraries, limits, devices, theme, lockout and every other household's rows are read
// back afterwards.
//
// Also ST-11: /update wrote a new picture and then refused the rename (a name another
// account has), so a refused save still changed the picture.
//
// Bodies are JSON, read with the options Jellyfin's MVC uses, then passed to the real
// route, so an extra field is dropped by the binder exactly as it would be live.
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

var tempDir = Path.Combine(Path.GetTempPath(), "bonfire-selfedit-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
var plugin = Activator.CreateInstance(pluginType, new StubPaths(tempDir), new StubXml());
var config = pluginType.GetProperty("Configuration").GetValue(plugin);
// A new install starts with Bonfire off (2.0), and off every gate stands down, so a
// refusal here would prove nothing. This harness is about a server where it is on.
config.GetType().GetProperty("BonfireOn").SetValue(config, true);
var mappings = (IList)cfgType.GetProperty("Mappings").GetValue(config);

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
var SIBLING = Guid.NewGuid();
var OTHER_MAIN = Guid.NewGuid();
var OTHER_KID = Guid.NewGuid();
var STRANGER = Guid.NewGuid();
var L1 = Guid.NewGuid();
var L2 = Guid.NewGuid();
const string PIN = "1234";
const string KID_PIN = "5678";

var world = new World();
world.Libraries.AddRange(new[] { L1, L2 });
world.Add(MAIN, "home", new UserPolicy { EnableAllFolders = true });
world.Add(KID, "home_Kid", new UserPolicy { MaxParentalRating = 10, EnableAllFolders = false, EnabledFolders = new[] { L1 } });
world.Add(SIBLING, "home_Sib", new UserPolicy { MaxParentalRating = 10 });
world.Add(OTHER_MAIN, "away", new UserPolicy { EnableAllFolders = true });
world.Add(OTHER_KID, "away_Tot", new UserPolicy { MaxParentalRating = 6 });
world.Add(STRANGER, "stranger", new UserPolicy());

object Map(Guid profile, Guid master, string name)
{
    var m = Activator.CreateInstance(mapType);
    mapType.GetProperty("ProfileUserId").SetValue(m, profile);
    mapType.GetProperty("MasterUserId").SetValue(m, master);
    mapType.GetProperty("ProfileName").SetValue(m, name);
    mappings.Add(m);
    return m;
}
void Set(object row, string prop, object value) => mapType.GetProperty(prop).SetValue(row, value);
object Row(object row, string prop) => mapType.GetProperty(prop)?.GetValue(row);

var mainRow = Map(MAIN, MAIN, "home");
Set(mainRow, "PinHash", hasher.GetMethod("Hash").Invoke(null, new object[] { PIN }));
var kidRow = Map(KID, MAIN, "Kid");
Set(kidRow, "PinHash", hasher.GetMethod("Hash").Invoke(null, new object[] { KID_PIN }));
Set(kidRow, "LockoutMinutes", 15);
Set(kidRow, "AvatarColor", "#00A4DC");
Set(kidRow, "AllowedDeviceIds", new List<string> { "tv-1" });
Set(kidRow, "EnabledFolders", new List<Guid> { L1 });
var sibRow = Map(SIBLING, MAIN, "Sib");
var otherMainRow = Map(OTHER_MAIN, OTHER_MAIN, "away");
var otherKidRow = Map(OTHER_KID, OTHER_MAIN, "Tot");

var users = World.Create<IUserManager>(world);
var library = World.Create<ILibraryManager>(world);

(int status, string message) Call(string method, string body, Guid? caller)
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
    var http = new DefaultHttpContext();
    if (caller != null)
        http.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", caller.Value.ToString()) }, "test"));
    http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7");
    http.Request.Headers["X-Bonfire-Master-Pin"] = PIN;
    c.ControllerContext = new ControllerContext { HttpContext = http };

    var invokeArgs = r.GetParameters().Select(p => JsonSerializer.Deserialize(body, p.ParameterType, json)).ToArray();

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

string Png(int bytes) => "data:image/png;base64," + Convert.ToBase64String(PngBytes(bytes));
// A PNG signature, then zeroes: uploads are checked by their first bytes (R-1111).
static byte[] PngBytes(int n) { var b = new byte[Math.Max(n, 8)]; new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(b, 0); return b; }
string Name(object row) => (string)Row(row, "ProfileName");
string Image(object row) => (string)Row(row, "ProfileImage");
string Username(Guid id) => world.Users[id].Username;
// The picture is a file, written before the row says anything about it, so the file is
// what shows whether a picture was saved.
string OnDisk() => string.Join(",", Directory.GetFiles(tempDir, KID + "*", SearchOption.AllDirectories)
    .OrderBy(f => f).Select(f => Path.GetFileName(f) + ":" + new FileInfo(f).Length));

// Everything about a profile that is not its name or picture, as one string, so a change
// to any of it shows up in a single comparison per account.
string Rest(Guid id, object row) =>
    "pin=" + Row(row, "PinHash")
    + " lockout=" + Row(row, "LockoutMinutes")
    + " bypass=" + Row(row, "BypassPinOnLocalNetwork")
    + " devices=" + string.Join(",", (IEnumerable<string>)(Row(row, "AllowedDeviceIds") ?? new List<string>()))
    + " libs=" + string.Join(",", ((IEnumerable<Guid>)(Row(row, "EnabledFolders") ?? new List<Guid>())).Select(g => g == L1 ? "L1" : g == L2 ? "L2" : "?"))
    + " all=" + Row(row, "AllLibraries")
    + " theme=" + Row(row, "Theme")
    + " blocked=" + string.Join(",", (IEnumerable<string>)(Row(row, "BlockedTags") ?? new List<string>()))
    + " viewing=" + (Row(row, "Viewing") == null ? "none" : "set")
    + " master=" + Row(row, "MasterUserId")
    + " policy=rating:" + world.Policies[id].MaxParentalRating + "/all:" + world.Policies[id].EnableAllFolders
    + "/folders:" + string.Join(",", (world.Policies[id].EnabledFolders ?? Array.Empty<Guid>()).Select(g => g == L1 ? "L1" : g == L2 ? "L2" : "?"));

var restKid = Rest(KID, kidRow);
var restSib = Rest(SIBLING, sibRow);
var restMain = Rest(MAIN, mainRow);
var restOtherKid = Rest(OTHER_KID, otherKidRow);
var restOtherMain = Rest(OTHER_MAIN, otherMainRow);
string Untouched() =>
    Rest(KID, kidRow) == restKid && Rest(SIBLING, sibRow) == restSib && Rest(MAIN, mainRow) == restMain
    && Rest(OTHER_KID, otherKidRow) == restOtherKid && Rest(OTHER_MAIN, otherMainRow) == restOtherMain
    && Name(sibRow) == "Sib" && Name(mainRow) == "home" && Name(otherKidRow) == "Tot" ? "" : "CHANGED";

// ── The route, from the profile ──────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── POST /me, as the profile ─────────────────────────────────────");

var r = Call("UpdateOwnProfile", "{\"profileName\":\"Kiddo\"}", KID);
Ok("a profile renames itself, with no main PIN", r.status == 200, r.status + " " + r.message);
Ok("its row has the new name", Name(kidRow) == "Kiddo", Name(kidRow));
Ok("and its account's username follows, as /update does", Username(KID) == "home_Kiddo", Username(KID));
Ok("nothing else of the household changed", Untouched() == "", Untouched());

r = Call("UpdateOwnProfile", "{\"avatarColor\":\"#E50914\",\"transparentAvatar\":true}", KID);
Ok("colour and background are the profile's to change", r.status == 200 && (string)Row(kidRow, "AvatarColor") == "#E50914"
    && Row(kidRow, "TransparentAvatar") is true, r.status + " " + r.message);
Ok("and the name was kept (a left-out field keeps)", Name(kidRow) == "Kiddo", Name(kidRow));

r = Call("UpdateOwnProfile", "{\"profileImage\":\"" + Png(300) + "\"}", KID);
Ok("a picture upload is accepted", r.status == 200 && Image(kidRow) != null && Image(kidRow).StartsWith("/plugins/profiles/image/" + KID),
    r.status + " " + r.message + " " + Image(kidRow));
var firstImage = Image(kidRow);

r = Call("UpdateOwnProfile", "{\"profileImage\":\"\"}", KID);
Ok("an empty picture removes it", r.status == 200 && string.IsNullOrEmpty(Image(kidRow)), r.status + " " + Image(kidRow));

// ── Trying to get past it ────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── Everything else, sent anyway ─────────────────────────────────");

var evil = "{\"profileName\":\"Kiddo\","
    + "\"profileId\":\"" + MAIN + "\",\"ProfileUserId\":\"" + SIBLING + "\",\"masterUserId\":\"" + OTHER_MAIN + "\","
    + "\"maxParentalRating\":\"\",\"allLibraries\":true,\"enabledFolders\":[\"" + L2 + "\"],"
    + "\"pin\":\"\",\"masterPin\":\"" + PIN + "\",\"lockoutMinutes\":0,\"bypassPinOnLocalNetwork\":true,"
    + "\"allowedDeviceIds\":[],\"theme\":\"x\",\"blockedTags\":[\"x\"],\"allowedTags\":[\"y\"],"
    + "\"askPinToOpen\":false,\"isHidden\":true,\"viewing\":{\"hoursEnabled\":false,\"dailyMinutes\":0},"
    + "\"viewingRules\":null,\"isMaster\":true}";
r = Call("UpdateOwnProfile", evil, KID);
Ok("a body carrying every other field is answered, not rejected", r.status == 200, r.status + " " + r.message);
Ok("and not one of them took effect, on any account", Untouched() == "", Untouched());

r = Call("UpdateOwnProfile", "{\"profileName\":\"Sib\"}", KID);
Ok("it cannot take a sibling's name", r.status == 400, r.status + " " + r.message);
Ok("and changes nothing", Name(kidRow) == "Kiddo" && Username(KID) == "home_Kiddo" && Untouched() == "");

// The request has nowhere to say who. A hand-written one that names someone else is read
// as a request about the caller, never about them.
r = Call("UpdateOwnProfile", "{\"profileId\":\"" + SIBLING + "\",\"profileName\":\"Hijacked\"}", KID);
Ok("naming another profile in the body renames the caller, not them", r.status == 200 && Name(kidRow) == "Hijacked" && Name(sibRow) == "Sib" && Username(SIBLING) == "home_Sib",
    Name(kidRow) + " / " + Name(sibRow) + " / " + Username(SIBLING));
r = Call("UpdateOwnProfile", "{\"profileName\":\"Kiddo\"}", KID);

// ── Who may call ─────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── Who may call it ──────────────────────────────────────────────");

r = Call("UpdateOwnProfile", "{\"profileName\":\"Boss\"}", null);
Ok("no token is refused", r.status == 401, r.status + " " + r.message);
r = Call("UpdateOwnProfile", "{\"profileName\":\"Boss\"}", MAIN);
Ok("a main account is refused (its name is its username)", r.status == 401 && Name(mainRow) == "home" && Username(MAIN) == "home", r.status + " " + r.message);
r = Call("UpdateOwnProfile", "{\"profileName\":\"Boss\"}", OTHER_MAIN);
Ok("so is another household's main account", r.status == 401 && Name(otherMainRow) == "away", r.status + " " + r.message);
r = Call("UpdateOwnProfile", "{\"profileName\":\"Boss\"}", STRANGER);
Ok("an account that is in no household is refused", r.status == 401 && Username(STRANGER) == "stranger", r.status + " " + r.message);
Ok("none of those changed anything", Untouched() == "", Untouched());

r = Call("UpdateProfile", "{\"profileId\":\"" + KID + "\",\"profileName\":\"Kid\"}", KID);
Ok("/update is still the main account's: a profile is refused", r.status == 401, r.status + " " + r.message);

// ── A refusal changes nothing ────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── A refused change leaves the profile as it was ─────────────────");

r = Call("UpdateOwnProfile", "{\"profileImage\":\"" + Png(300) + "\"}", KID);
var before = Image(kidRow);
var diskNow = OnDisk();
r = Call("UpdateOwnProfile", "{\"profileName\":\"Sib\",\"avatarColor\":\"#22C55E\",\"profileImage\":\"" + Png(500) + "\"}", KID);
Ok("a taken name is refused", r.status == 400, r.status + " " + r.message);
Ok("and the picture in the same request was not saved", Image(kidRow) == before && OnDisk() == diskNow, diskNow + " -> " + OnDisk());
Ok("nor the colour", (string)Row(kidRow, "AvatarColor") == "#E50914");

r = Call("UpdateOwnProfile", "{\"profileName\":\"Fine\",\"profileImage\":\"data:image/png;base64,@@@\"}", KID);
Ok("an unreadable picture is refused", r.status == 400, r.status + " " + r.message);
Ok("and the name in the same request was not changed", Name(kidRow) == "Kiddo" && Username(KID) == "home_Kiddo", Name(kidRow) + " " + Username(KID));

r = Call("UpdateOwnProfile", "{\"profileName\":\"\"}", KID);
Ok("an empty name is refused", r.status == 400 && Name(kidRow) == "Kiddo", r.status + " " + r.message);
r = Call("UpdateOwnProfile", "{\"profileName\":\"a/b\\\\c\"}", KID);
Ok("a name with characters an account cannot have is refused", r.status == 400 && Name(kidRow) == "Kiddo", r.status + " " + r.message);
r = Call("UpdateOwnProfile", "{\"profileName\":\"away\"}", KID);
Ok("a name that is an account's username is refused", r.status == 400 && Name(kidRow) == "Kiddo", r.status + " " + r.message);

// ── ST-11: /update ───────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── /update: a refused rename keeps the picture (ST-11) ───────────");

r = Call("UpdateProfile", "{\"profileId\":\"" + KID + "\",\"profileName\":\"Kid\",\"masterPin\":\"" + PIN + "\",\"profileImage\":\"" + Png(300) + "\"}", MAIN);
Ok("setup: a picture saved through /update", r.status == 200 && OnDisk() != "", r.status + " " + r.message + " [" + OnDisk() + "]");
var diskBefore = OnDisk();
r = Call("UpdateProfile", "{\"profileId\":\"" + KID + "\",\"profileName\":\"Sib\",\"masterPin\":\"" + PIN + "\",\"profileImage\":\"" + Png(400) + "\"}", MAIN);
Ok("a rename onto an existing profile's account is refused", r.status == 400, r.status + " " + r.message);
Ok("and the picture sent with it was not saved", OnDisk() == diskBefore, diskBefore + " -> " + OnDisk());

// ── r2-15-06: the re-check is under the lock, before anything is written ─────
Console.WriteLine();
Console.WriteLine("── /me: the row is re-checked under the lock before the picture ──");
{
    var diskStart = OnDisk();
    var imageStart = Image(kidRow);
    // Someone else deletes the profile's row after the route has read it and before it writes.
    var hit = false;
    world.OnGetUserById = id =>
    {
        if (id != KID || hit) return;
        hit = true;
        mappings.Remove(kidRow);
    };
    var gone = Call("UpdateOwnProfile", "{\"profileImage\":\"" + Png(321) + "\"}", KID);
    world.OnGetUserById = null;
    Ok("the row went missing mid-call (the setup took)", hit);
    Ok("the call is a 404", gone.status == 404, gone.status + " " + gone.message);
    Ok("and no picture was written for a profile that no longer exists", OnDisk() == diskStart, diskStart + " -> " + OnDisk());
    mappings.Add(kidRow);
    Set(kidRow, "ProfileImage", imageStart);
}

// ── r3-03-01: a delete after the checks and before the save ──────────────────
// The checks passed and the picture is written; the profile is deleted while the account is
// being renamed. The save finds no row, and the picture it wrote must go with the profile.
{
    var diskStart = OnDisk();
    var imageStart = Image(kidRow);
    var nameStart = Name(kidRow);
    var userStart = world.Users[KID].Username;
    var hit = false;
    world.OnUpdateUser = u =>
    {
        if (u.Id != KID || hit) return;
        hit = true;
        mappings.Remove(kidRow);
    };
    var late = Call("UpdateOwnProfile", "{\"profileName\":\"Latecomer\",\"profileImage\":\"" + Png(333) + "\"}", KID);
    world.OnUpdateUser = null;
    Ok("the row went missing during the rename (the setup took)", hit);
    Ok("the call is a 404", late.status == 404, late.status + " " + late.message);
    Ok("and the picture it wrote is taken back", OnDisk() == "", diskStart + " -> " + OnDisk());
    mappings.Add(kidRow);
    Set(kidRow, "ProfileImage", imageStart);
    Set(kidRow, "ProfileName", nameStart);
    world.Users[KID].Username = userStart;
}

// ── P2-705: a new PIN ends the lock the old one was guessed into ─────────────
Console.WriteLine();
Console.WriteLine("── /update: a new PIN starts the guess count again (P2-705) ──────");
var budget = asm.GetType("Jellyfin.Profiles.Controllers.PinBudget", true);
bool BudgetOpen() => (bool)budget.GetMethod("TryBegin", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { KID });
for (int i = 0; i < 8; i++) BudgetOpen();
Ok("setup: the profile's PIN is locked by wrong guesses", !BudgetOpen());
r = Call("UpdateProfile", "{\"profileId\":\"" + KID + "\",\"profileName\":\"Kiddo\",\"masterPin\":\"" + PIN + "\",\"pin\":\"9999\"}", MAIN);
Ok("the main account sets it a new PIN", r.status == 200, r.status + " " + r.message);
Ok("and the lock is gone", BudgetOpen());

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
    public Action<Guid> OnGetUserById;
    public Action<User> OnUpdateUser;

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
                W.OnGetUserById?.Invoke((Guid)args[0]);
                return W.Users.TryGetValue((Guid)args[0], out var u) ? u : null;
            case "UpdateUserAsync":
                W.OnUpdateUser?.Invoke((User)args[0]);
                return Task.CompletedTask;
            case "GetUserByName":
                return W.Users.Values.FirstOrDefault(x => string.Equals(x.Username, (string)args[0], StringComparison.OrdinalIgnoreCase));
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
