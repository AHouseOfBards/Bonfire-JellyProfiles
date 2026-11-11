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
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

// ─────────────────────────────────────────────────────────────────────────────
// Export, change, import: nothing may be lost (R-1146).
//
// Import restored a named list of about twenty-two fields and answered "success" for the
// rest, so the SMTP settings, confirmed email addresses, the PIN-reset-by-email switch,
// maintenance, first-time setup, starter avatars and the Quick Connect fields went missing
// from a restore without a word. Each had been added to PluginConfiguration after the
// import list was written, and nothing made anyone go back.
//
// So this compares EVERY public property of the configuration by reflection: a field added
// next month is covered the day it is added, or this fails and names it. The only things
// left out are an explicit list, each with its reason.
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
var pluginType = asm.GetType("Jellyfin.Profiles.Plugin", true);
var cfgType = asm.GetType("Jellyfin.Profiles.Configuration.PluginConfiguration", true);
var exportRoute = ctrlType.GetMethod("ExportConfiguration");
var importRoute = ctrlType.GetMethod("ImportConfiguration");
if (exportRoute == null || importRoute == null) { Ok("the build has export and import", false); return Finish(); }

Type T(string name) => asm.GetType("Jellyfin.Profiles.Configuration." + name, true);

var tempDir = Path.Combine(Path.GetTempPath(), "bonfire-configimport-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
var plugin = Activator.CreateInstance(pluginType, new StubPaths(tempDir), new StubXml());
var live = pluginType.GetProperty("Configuration").GetValue(plugin);

var ADMIN = Guid.NewGuid();
var MASTER = Guid.NewGuid();
var KID = Guid.NewGuid();
var OTHER = Guid.NewGuid();
var world = new World();
world.Add(ADMIN, "Admin", admin: true);
world.Add(MASTER, "Bard");
world.Add(KID, "Bard_Kid");
world.Add(OTHER, "Other");
var users = World.Create<IUserManager>(world);
var sessions = World.Create<ISessionManager>(world);

ControllerBase NewController()
{
    var ctor = ctrlType.GetConstructors().First();
    var args = ctor.GetParameters().Select(p =>
        p.ParameterType == typeof(IUserManager) ? users
        : p.ParameterType == typeof(ISessionManager) ? sessions
        : p.ParameterType.Name.StartsWith("ILogger") ? Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(ctrlType))
        : (object)null).ToArray();
    var c = (ControllerBase)ctor.Invoke(args);
    c.ControllerContext = new ControllerContext
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", ADMIN.ToString()) }, "test")),
        },
    };
    return c;
}

// ── What must come back ────────────────────────────────────────────────────

// Left alone on purpose, each with its reason. A property that is not here must survive.
var notRestored = new Dictionary<string, string>
{
    ["CrossHouseholdEntries"] = "which devices came in from outside, with their tokens: state of this server's sign-ins, not a setting",
};
// Not changed by this harness: each is checked against files or rules in its own route, and
// a backup holds the name, not the file.
var notChanged = new Dictionary<string, string>
{
    ["Announcements"] = "validated by Announcements.Validate",
    ["CustomFonts"] = "dropped when the font file is not on this server",
    ["CustomThemes"] = "validated against the theme schema",
    ["HiddenThemes"] = "only built-in themes, and the default comes out of it",
    ["LogoImage"] = "dropped when the file is not on this server",
    ["CrossHouseholdEntries"] = "see notRestored",
};

object New(string typeName) => Activator.CreateInstance(T(typeName));
void P(object o, string name, object value) => o.GetType().GetProperty(name).SetValue(o, value);
object Item(string typeName, params (string, object)[] values)
{
    var o = New(typeName);
    foreach (var (n, v) in values) P(o, n, v);
    return o;
}
IList ListOf(Type itemType, params object[] items)
{
    var l = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(itemType));
    foreach (var i in items) l.Add(i);
    return l;
}

IEnumerable<PropertyInfo> Settable() => cfgType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
    .Where(p => p.DeclaringType == cfgType && p.CanRead && p.CanWrite);

var options = new JsonSerializerOptions();
string Json(object v) => JsonSerializer.Serialize(v, options);

object RatedKid()
{
    var kid = Item("ProfileMapping", ("ProfileUserId", KID), ("MasterUserId", MASTER), ("ProfileName", "Kid"));
    var mapType = T("ProfileMapping");
    var ratingType = asm.GetType("Jellyfin.Profiles.Configuration.ProfileRating");
    var own = mapType.GetProperty("OwnRating");
    var last = mapType.GetProperty("LastAppliedRating");
    Ok("a mapping carries the rating it keeps of its own and the one last written", ratingType != null && own != null && last != null);
    if (ratingType == null || own == null || last == null) return kid;
    var a = Activator.CreateInstance(ratingType);
    ratingType.GetProperty("Score").SetValue(a, (int?)10);
    var b = Activator.CreateInstance(ratingType);
    ratingType.GetProperty("Score").SetValue(b, (int?)7);
    ratingType.GetProperty("SubScore").SetValue(b, (int?)1);
    own.SetValue(kid, a);
    last.SetValue(kid, b);
    return kid;
}

// Every setting at its default, then every one moved off it.
object Defaults() => Activator.CreateInstance(cfgType);
var byHand = new Dictionary<string, object>
{
    ["Mappings"] = ListOf(T("ProfileMapping"),
        Item("ProfileMapping", ("ProfileUserId", MASTER), ("MasterUserId", MASTER), ("ProfileName", "Bard")),
        // The rating a profile keeps of its own and the one last written to its account
        // (R-1045): a backup that drops them sends every profile back to "no record".
        RatedKid()),
    ["KnownDevices"] = ListOf(T("KnownDevice"), Item("KnownDevice", ("DeviceId", "tv-1"), ("DeviceName", "Shield"))),
    ["BonfireGroups"] = ListOf(T("BonfireGroup"), Item("BonfireGroup", ("GroupId", "g1"), ("BonfireCode", "ABC123"), ("OwnerUserId", MASTER))),
    ["LinkedAccounts"] = ListOf(T("LinkedAccount"), Item("LinkedAccount", ("HouseholdId", MASTER), ("UserId", OTHER))),
    ["UserProfileLimitOverrides"] = ListOf(T("UserProfileLimitOverride"), Item("UserProfileLimitOverride", ("UserId", MASTER), ("MaxProfiles", 9))),
    ["AvatarLibrary"] = ListOf(T("AvatarLibraryItem"), Item("AvatarLibraryItem", ("Id", "a1"), ("DisplayName", "Fox"))),
    ["QuickConnectSharedDeviceNames"] = new List<string> { "SHIELD Android TV" },
    ["ProxyClients"] = new List<string> { "moonfin" },
    ["HouseholdsSeenLocally"] = new List<Guid> { MASTER },
    // Trusted addresses (2.0, #42): a backup that drops them puts the PIN back on Grandma's TV.
    ["TrustedAddressEntries"] = ListOf(T("TrustedAddressEntry"), Item("TrustedAddressEntry", ("Address", "192.168.1.0/24"), ("UserIds", new List<Guid> { MASTER }))),
    ["AccountEmails"] = ListOf(T("AccountEmail"), Item("AccountEmail", ("UserId", MASTER), ("Address", "bard@example.com"), ("Language", "de"), ("NotifyLimitReached", true))),
    ["Email"] = Item("EmailSettings", ("Host", "smtp.example.com"), ("Port", 2525), ("Username", "mailer"), ("Password", "app-password"),
        ("FromAddress", "bonfire@example.com"), ("Security", "none")),
    ["Maintenance"] = Item("MaintenanceMode", ("Enabled", true), ("Message", "Back at nine."), ("StopPlayback", true)),
    ["Onboarding"] = Item("OnboardingSettings", ("Enabled", true), ("Asked", true), ("Generation", 4)),
    ["DefaultTheme"] = "harbor",
    ["DefaultSwitcherLocation"] = "menu",
    ["ProfileImageQuality"] = "high",
    ["DefaultTimeZone"] = "UTC",
    ["PanicCodeHash"] = "panic-hash",
    ["LogoText"] = "Hearth",
    ["LogoAlt"] = "The Hearth logo",
};

var changed = Defaults();
var unmoved = new List<string>();
foreach (var p in Settable())
{
    if (notChanged.ContainsKey(p.Name)) continue;
    if (byHand.TryGetValue(p.Name, out var v)) { p.SetValue(changed, v); continue; }
    if (p.PropertyType == typeof(bool)) p.SetValue(changed, !(bool)p.GetValue(changed));
    else if (p.PropertyType == typeof(int)) p.SetValue(changed, p.Name == "PinGuessesPerDay" ? (int)p.GetValue(changed) - 1 : (int)p.GetValue(changed) + 1);
    else if (p.PropertyType == typeof(bool?)) p.SetValue(changed, !((bool?)p.GetValue(changed) ?? false));
    else unmoved.Add(p.Name);
}

Console.WriteLine();
Console.WriteLine("── The harness itself ───────────────────────────────────────────");
Ok("every setting is moved off its default here, or named above with a reason",
   unmoved.Count == 0, "a new setting of a type this harness cannot move: " + string.Join(", ", unmoved));
var defaultsNow = Defaults();
var notMoved = Settable().Where(p => !notChanged.ContainsKey(p.Name) && Json(p.GetValue(changed)) == Json(p.GetValue(defaultsNow))).Select(p => p.Name).ToList();
Ok("and each really differs from its default", notMoved.Count == 0, string.Join(", ", notMoved));

// ── Export, reset, import ──────────────────────────────────────────────────

void Overwrite(object target, object source, IEnumerable<string> only = null)
{
    foreach (var p in Settable())
    {
        if (only != null && !only.Contains(p.Name)) continue;
        p.SetValue(target, p.GetValue(source));
    }
}

Overwrite(live, changed);
var exported = (ObjectResult)((OkObjectResult)exportRoute.Invoke(NewController(), null));
var body = JsonSerializer.SerializeToElement(exported.Value);
var snapshot = Settable().ToDictionary(p => p.Name, p => Json(p.GetValue(live)));

// The server as it is the day the backup is restored: everything back at its default.
Overwrite(live, Defaults());

var result = importRoute.Invoke(NewController(), new object[] { body });
var status = result is ObjectResult o ? (o.StatusCode ?? 200) : result is StatusCodeResult s ? s.StatusCode : -1;

Console.WriteLine();
Console.WriteLine("── Export, then import onto a fresh configuration ──────────────");
Ok("the import is accepted", status == 200, status.ToString());

var after = Settable().ToDictionary(p => p.Name, p => Json(p.GetValue(live)));
foreach (var p in Settable().Where(p => !notChanged.ContainsKey(p.Name) && !notRestored.ContainsKey(p.Name)))
{
    Ok(p.Name + " comes back", after[p.Name] == snapshot[p.Name],
       "was " + Trunc(snapshot[p.Name]) + " , now " + Trunc(after[p.Name]));
}

foreach (var name in notRestored.Keys)
    Ok(name + " is left as the server has it (" + notRestored[name] + ")", after[name] == Json(cfgType.GetProperty(name).GetValue(defaultsNow)));

// The SMTP password is restored as the export carries it. Whether the export should carry
// it is R-1312's question, not this one's.
Ok("the SMTP settings arrive whole", after["Email"] == snapshot["Email"]);

// A file that does not mention a setting leaves it alone: a partial or older export.
Overwrite(live, changed);
var partial = JsonSerializer.SerializeToElement(new { configuration = new { Mappings = new object[0], MaxProfilesPerUser = 3 } });
importRoute.Invoke(NewController(), new object[] { partial });
Ok("a file without a setting leaves the current one", Json(cfgType.GetProperty("Email").GetValue(live)) == snapshot["Email"]
   && Json(cfgType.GetProperty("Maintenance").GetValue(live)) == snapshot["Maintenance"]);

// A backup written by a version that still had a setting that has since been cut. The old
// field rides along in the file; the import must take everything else and not refuse it.
// Each cut adds its field to this one list.
Console.WriteLine();
Console.WriteLine("── A backup from a version that had settings since cut ──────────");
{
    var oldFields = new Dictionary<string, object>
    {
        ["IndexInjectionMode"] = "file",
    };
    var fileConfig = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body.GetProperty("configuration").GetRawText());
    var older = new Dictionary<string, object>();
    foreach (var kv in fileConfig) older[kv.Key] = kv.Value;
    foreach (var kv in oldFields) older[kv.Key] = kv.Value;

    // ProfileMapping.SwitcherMode: every row of every older export carries it, "gate" unless
    // somebody chose native in 1.3.1-beta. Added to each mapping here.
    object WithSwitcherMode(string legacy)
    {
        var rows = new List<Dictionary<string, JsonElement>>();
        foreach (var item in fileConfig["Mappings"].EnumerateArray())
        {
            var row = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.GetRawText());
            row["SwitcherMode"] = JsonSerializer.SerializeToElement(legacy);
            rows.Add(row);
        }
        return rows;
    }
    older["Mappings"] = WithSwitcherMode("gate");

    // EmailSettings.FromName: the name mail was sent under. It sits inside the Email section.
    var emailSection = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(fileConfig["Email"].GetRawText());
    var emailWithName = new Dictionary<string, object>();
    foreach (var kv in emailSection) emailWithName[kv.Key] = kv.Value;
    emailWithName["FromName"] = "Hearth";
    older["Email"] = emailWithName;

    // OnboardingSettings.RequiredSteps: the "can't skip" list, inside the Onboarding section.
    var onboardingSection = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(fileConfig["Onboarding"].GetRawText());
    var onboardingWithRequired = new Dictionary<string, object>();
    foreach (var kv in onboardingSection) onboardingWithRequired[kv.Key] = kv.Value;
    onboardingWithRequired["RequiredSteps"] = new[] { "mainPin" };
    older["Onboarding"] = onboardingWithRequired;
    var olderBody = JsonSerializer.SerializeToElement(new { exportedUtc = "2026-01-01T00:00:00Z", pluginVersion = "1.7.4.0", configuration = older });

    Overwrite(live, Defaults());
    var olderResult = importRoute.Invoke(NewController(), new object[] { olderBody });
    var olderStatus = olderResult is ObjectResult oo ? (oo.StatusCode ?? 200) : olderResult is StatusCodeResult ss ? ss.StatusCode : -1;
    Ok("the import is accepted", olderStatus == 200, olderStatus.ToString());
    var olderAfter = Settable().ToDictionary(p => p.Name, p => Json(p.GetValue(live)));
    var olderLost = Settable().Where(p => !notChanged.ContainsKey(p.Name) && !notRestored.ContainsKey(p.Name) && olderAfter[p.Name] != snapshot[p.Name]).Select(p => p.Name).ToList();
    Ok("and every other setting comes back", olderLost.Count == 0, string.Join(", ", olderLost));
    foreach (var name in oldFields.Keys)
        Ok(name + " is not a setting any more", cfgType.GetProperty(name) == null);
    Ok("FromName is not an email setting any more", asm.GetType("Jellyfin.Profiles.Configuration.EmailSettings", true).GetProperty("FromName") == null);
    Ok("and the rest of the email section came back", olderAfter["Email"] == snapshot["Email"]);
    Ok("RequiredSteps is not a first-time setup setting any more", asm.GetType("Jellyfin.Profiles.Configuration.OnboardingSettings", true).GetProperty("RequiredSteps") == null);
    Ok("and the rest of first-time setup came back", olderAfter["Onboarding"] == snapshot["Onboarding"]);

    // A native choice from 1.3.1-beta, on rows that never chose the two newer settings, is
    // carried into them rather than dropped with the field.
    older["Mappings"] = WithSwitcherMode("native");
    Overwrite(live, Defaults());
    importRoute.Invoke(NewController(), new object[] { JsonSerializer.SerializeToElement(new { configuration = older }) });
    var mapRows = ((System.Collections.IList)cfgType.GetProperty("Mappings").GetValue(live)).Cast<object>().ToList();
    var mapT = asm.GetType("Jellyfin.Profiles.Configuration.ProfileMapping", true);
    Ok("a native row that never chose keeps the menu and no startup prompt",
       mapRows.Count > 0 && mapRows.All(r => Equals(mapT.GetProperty("AskOnStartup").GetValue(r), false) && Equals(mapT.GetProperty("SwitcherLocation").GetValue(r), "menu")));
    Ok("and the old field is not written back", !JsonSerializer.Serialize(mapRows).Contains("SwitcherMode"));
}

try { Directory.Delete(tempDir, true); } catch { }
return Finish();

string Trunc(string s) => s.Length > 90 ? s.Substring(0, 90) + "…" : s;

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

// ── A modelled Jellyfin: every account exists, one is an administrator ─────

public class World
{
    public Dictionary<Guid, User> Users = new();
    public HashSet<Guid> Admins = new();

    public void Add(Guid id, string name, bool admin = false)
    {
        var u = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty("Username").SetValue(u, name);
        typeof(User).GetProperty("Id").SetValue(u, id);
        typeof(User).GetProperty("AuthenticationProviderId").SetValue(u, "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider");
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
                    Policy = new UserPolicy { IsAdministrator = W.Admins.Contains(user.Id), AuthenticationProviderId = user.AuthenticationProviderId, PasswordResetProviderId = "x" },
                    Configuration = new UserConfiguration(),
                };
        }

        var t = m.ReturnType;
        if (t == typeof(void)) return null;
        if (t == typeof(Task)) return Task.CompletedTask;
        if (t.IsValueType) return Activator.CreateInstance(t);
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
