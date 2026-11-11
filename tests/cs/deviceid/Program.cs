using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

// ─────────────────────────────────────────────────────────────────────────────
// ─────────────────────────────────────────────────────────────────────────────
// campground.device.v1: what Bonfire answers to getDevice from its own device record
// (KnownDevices), asked the way a caller asks, through the shared registry.
//
// StableKey is the recorded id after Bonfire's merges; DisplayName is the household's own
// name for it, else the client's. It is a grouping key and never an access boundary: Bonfire's
// device restrictions match the id the client sent, so the section at the end proves a key two
// ids share opens nothing, and that no restriction code reads the capability.
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

const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

var root = RepoRoot();
var tfm = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Name;
var asm = Assembly.LoadFrom(Path.Combine(root, "bin", "Release", tfm, "Jellyfin.Profiles.dll"));

var pluginType = asm.GetType("Jellyfin.Profiles.Plugin", true);
var cfgType = asm.GetType("Jellyfin.Profiles.Configuration.PluginConfiguration", true);
var mapType = asm.GetType("Jellyfin.Profiles.Configuration.ProfileMapping", true);
var groupType = asm.GetType("Jellyfin.Profiles.Configuration.BonfireGroup", true);
var deviceType = asm.GetType("Jellyfin.Profiles.Configuration.KnownDevice", true);
var baseCtl = asm.GetType("Jellyfin.Profiles.Controllers.ProfilesBaseController", true);
var provider = asm.GetType("Jellyfin.Profiles.BonfireCampground");
var capabilities = asm.GetType("Campground.Shared.Registry.Capabilities", true);
var events = asm.GetType("Campground.Shared.Registry.CampgroundEvents", true);
var eventNames = asm.GetType("Campground.Shared.Registry.EventNames", true);
var capNames = asm.GetType("Campground.Shared.Registry.CapabilityNames", true);

string Const(Type t, string name) => (string)t.GetField(name, Any).GetValue(null);
var HOUSEHOLD = Const(capNames, "Household");
var DEVICE = Const(capNames, "Device");

// ── A server ─────────────────────────────────────────────────────────────────
var tempDir = Path.Combine(Path.GetTempPath(), "bonfire-household-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
var plugin = Activator.CreateInstance(pluginType, new StubPaths(tempDir), new StubXml());
var config = pluginType.GetProperty("Configuration").GetValue(plugin);
// A new install starts with Bonfire off (2.0); this harness is about a server where it is on.
config.GetType().GetProperty("BonfireOn").SetValue(config, true);
var mappings = (IList)cfgType.GetProperty("Mappings").GetValue(config);
var groups = (IList)cfgType.GetProperty("BonfireGroups").GetValue(config);
var devices = (IList)cfgType.GetProperty("KnownDevices").GetValue(config);

Guid MAIN = Guid.NewGuid();      // a main account with profiles and NO row of its own
Guid KID = Guid.NewGuid();
Guid TEEN = Guid.NewGuid();
Guid OWNROW = Guid.NewGuid();    // a main account whose row was written by saving switcher preferences
Guid FRIEND = Guid.NewGuid();    // a main account in MAIN's sharing group, nothing else
Guid LONER = Guid.NewGuid();     // no record anywhere
Guid OTHER = Guid.NewGuid();     // somebody else's household
Guid OTHERKID = Guid.NewGuid();

const string JOINCODE = "XK7Q2M";
var names = new Dictionary<Guid, string>
{
    [MAIN] = "alice", [OWNROW] = "carol", [FRIEND] = "bob", [LONER] = "dave", [OTHER] = "erin",
    [KID] = "Bard_kids", [TEEN] = "Bard_teen", [OTHERKID] = "erin_k",
};
Func<Guid, string> nameOf = id => names.TryGetValue(id, out var n) ? n : null;

void Row(Guid profile, Guid master, string name)
{
    var m = Activator.CreateInstance(mapType);
    mapType.GetProperty("ProfileUserId").SetValue(m, profile);
    mapType.GetProperty("MasterUserId").SetValue(m, master);
    mapType.GetProperty("ProfileName").SetValue(m, name);
    mappings.Add(m);
}
Row(KID, MAIN, "Kids");
Row(TEEN, MAIN, "Teen");
Row(OWNROW, OWNROW, "carol");
Row(OTHERKID, OTHER, "Erin's kid");

var group = Activator.CreateInstance(groupType);
groupType.GetProperty("GroupId").SetValue(group, "grp-1");
groupType.GetProperty("BonfireCode").SetValue(group, JOINCODE);
groupType.GetProperty("OwnerUserId").SetValue(group, MAIN);
groupType.GetProperty("MemberUserIds").SetValue(group, new List<Guid> { FRIEND });
groups.Add(group);

KnownDeviceRow("dev-A", "Chrome", "Jellyfin Web", MAIN, new List<string> { "dev-B" }, false);
KnownDeviceRow("tv-1", "Living room TV", "Android TV", MAIN, new List<string>(), true);
KnownDeviceRow("tv-2", "Shield", "Android TV", MAIN, new List<string>(), false);
object KnownDeviceRow(string id, string name, string client, Guid owner, List<string> aliases, bool custom)
{
    var d = Activator.CreateInstance(deviceType);
    deviceType.GetProperty("DeviceId").SetValue(d, id);
    deviceType.GetProperty("DeviceName").SetValue(d, name);
    deviceType.GetProperty("Client").SetValue(d, client);
    deviceType.GetProperty("MasterUserId").SetValue(d, owner);
    deviceType.GetProperty("Owners").SetValue(d, new List<Guid> { owner });
    deviceType.GetProperty("Aliases").SetValue(d, aliases);
    deviceType.GetProperty("NameIsCustom").SetValue(d, custom);
    devices.Add(d);
    return d;
}

// ── Asking the way a caller does ─────────────────────────────────────────────
var tryInvoke = capabilities.GetMethods(Any)
    .First(m => m.Name == "TryInvokeAsync" && !m.IsGenericMethodDefinition);

JsonElement? Ask(string capability, string op, object args, int minMinor = 0)
{
    var task = (Task)tryInvoke.Invoke(null, new object[] { capability, op, args, minMinor, null, default(System.Threading.CancellationToken) });
    task.GetAwaiter().GetResult();
    return (JsonElement?)task.GetType().GetProperty("Result").GetValue(task);
}

JsonElement? GetHousehold(Guid id, int minMinor = 0) => Ask(HOUSEHOLD, "getHousehold", new { userId = id }, minMinor);
JsonElement? GetDevice(string deviceId, string client = null, string deviceName = null, Guid? user = null)
    => Ask(DEVICE, "getDevice", new { deviceId, appName = client, deviceName, userId = user });
JsonElement? SignInPolicy(Guid id) => Ask(HOUSEHOLD, "getSignInPolicy", new { userId = id }, 1);

bool IsNull(JsonElement? e) => e == null || e.Value.ValueKind == JsonValueKind.Null;
string Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
Guid Id(JsonElement e, string p) => Guid.TryParse(Str(e, p), out var g) ? g : Guid.Empty;
bool Flag(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.True;
List<JsonElement> Members(JsonElement e) => e.TryGetProperty("members", out var m) && m.ValueKind == JsonValueKind.Array ? m.EnumerateArray().ToList() : new List<JsonElement>();

void SetConfig(string prop, object value) => cfgType.GetProperty(prop).SetValue(config, value);

// Offered under test: the provider the plugin registers at startup, wired to a user lookup that
// needs no Jellyfin. Absent today; Ok() below reports each case that depends on it.
if (provider != null)
{
    var provide = provider.GetMethod("Provide", Any);
    provide?.Invoke(null, new object[] { nameOf });
}
bool offered = (bool)capabilities.GetMethods(Any).First(m => m.Name == "IsAvailable").Invoke(null, new object[] { DEVICE, 0 });

string Source(params string[] rel) => string.Join("\n", rel.Select(r => File.ReadAllText(Path.Combine(root, r))));
Ok("device.v1 is offered", (bool)capabilities.GetMethods(Any).First(m => m.Name == "IsAvailable").Invoke(null, new object[] { DEVICE, 0 }));

Console.WriteLine();
Console.WriteLine("── getDevice ───────────────────────────────────────────────────");

var merged = GetDevice("dev-B", "Jellyfin Web", "Chrome", MAIN);
Ok("an id an administrator merged into another answers with the one Bonfire kept",
   !IsNull(merged) && Str(merged.Value, "stableKey") == "dev-A", merged?.ToString());
var direct = GetDevice("dev-A", "Jellyfin Web", "Firefox", MAIN);
Ok("the kept id answers as itself", !IsNull(direct) && Str(direct.Value, "stableKey") == "dev-A");
Ok("its name is what the client reports when nobody renamed it",
   !IsNull(direct) && Str(direct.Value, "displayName") == "Firefox");
var tv = GetDevice("tv-1", "Android TV", "SHIELD Android TV", MAIN);
Ok("a name the household gave it sticks over what the client reports",
   !IsNull(tv) && Str(tv.Value, "displayName") == "Living room TV" && Str(tv.Value, "stableKey") == "tv-1", tv?.ToString());
var stranger = GetDevice("tv-1", "Android TV", "SHIELD Android TV", OTHER);
Ok("another household is given the stable key, not the name this household chose",
   !IsNull(stranger) && Str(stranger.Value, "stableKey") == "tv-1" && Str(stranger.Value, "displayName") == "SHIELD Android TV", stranger?.ToString());
Ok("a device Bonfire has never recorded is not invented: the library's fallback key applies",
   offered && IsNull(GetDevice("never-seen", "Android TV", "Shield", MAIN)));
Ok("no device id, no answer", offered && IsNull(GetDevice(null, "Android TV", "Shield", MAIN)));

Console.WriteLine();
Console.WriteLine("── The key is a grouping key, never an access boundary ─────────");

// Restrictions match the id the client sent. A profile allowed only on dev-B (which is
// merged into dev-A, so getDevice calls both "dev-A") must not let dev-A in on the strength
// of the shared key. And dev-A, the kept one, is not on a list that names only dev-B.
var allowedMap = Activator.CreateInstance(mapType);
mapType.GetProperty("ProfileUserId").SetValue(allowedMap, KID);
mapType.GetProperty("MasterUserId").SetValue(allowedMap, MAIN);
mapType.GetProperty("AllowedDeviceIds").SetValue(allowedMap, new List<string> { "dev-B" });
var evaluate = baseCtl.GetMethod("EvaluateDeviceRestriction", Any);
string Access(object map, string deviceId) => evaluate.Invoke(null, new object[] { map, deviceId, devices, mappings }).ToString();
var keyOfB = GetDevice("dev-B", "Jellyfin Web", "Chrome", MAIN);
Ok("two ids share one stable key, and a profile allowed on only one of them is still refused on the other",
   !IsNull(keyOfB) && Str(keyOfB.Value, "stableKey") == "dev-A"
   && Access(allowedMap, "dev-A") != "Allowed" && Access(allowedMap, "dev-B") == "Allowed",
   "dev-A: " + Access(allowedMap, "dev-A") + ", dev-B: " + Access(allowedMap, "dev-B"));
var allowedTv = Activator.CreateInstance(mapType);
mapType.GetProperty("ProfileUserId").SetValue(allowedTv, KID);
mapType.GetProperty("MasterUserId").SetValue(allowedTv, MAIN);
mapType.GetProperty("AllowedDeviceIds").SetValue(allowedTv, new List<string> { "tv-1" });
Ok("a stable key that is no id the client sent opens nothing: a list naming it matches no device",
   Access(allowedTv, "tv-2") != "Allowed" && Access(allowedTv, "fb:0123456789abcdef01234567") != "Allowed");
var guarded = Source("Controllers/ProfilesBaseController.cs", "Auth/DeviceRegistry.cs", "Auth/BonfirePinAuthenticationProvider.cs",
                     "Auth/ProxySwitcher.cs", "Auth/QuickConnectAuthorize.cs", "Auth/PublicUserInjector.cs");
Ok("no restriction code reads the stable key, the device capability or the provider",
   !Regex.IsMatch(guarded, @"StableKey|DeviceClient|DeviceIdentity|BonfireCampground|CapabilityNames\.Device"));

Console.WriteLine();
Console.WriteLine("── Emergency disable ───────────────────────────────────────────");
var panicField = pluginType.GetField("_panicDisabled", Any);
pluginType.GetMethod("TripPanicDisable", Any).Invoke(null, null);
try
{
    Ok("while Bonfire is emergency-disabled a device is not answered, so the library's fallback key applies",
       offered && IsNull(GetDevice("dev-B", "Jellyfin Web", "Chrome", MAIN)));
}
finally { panicField.SetValue(null, false); }
Ok("restarting Bonfire restores the answer", offered && !IsNull(GetDevice("dev-B", "Jellyfin Web", "Chrome", MAIN)));

// Turned off on the Status tab (2.0): the same as the emergency code, at getDevice.
var bonfireOn = config.GetType().GetProperty("BonfireOn");
bonfireOn.SetValue(config, false);
try
{
    Ok("turned off: a device is not answered either, so the library's fallback key applies",
       offered && IsNull(GetDevice("dev-B", "Jellyfin Web", "Chrome", MAIN)));
}
finally { bonfireOn.SetValue(config, true); }
Ok("turned back on, without a restart, the answer is back", offered && !IsNull(GetDevice("dev-B", "Jellyfin Web", "Chrome", MAIN)));

Console.WriteLine();
Console.WriteLine($"{pass} passed, {fails.Count} failed");
if (fails.Count > 0)
{
    foreach (var f in fails) Console.WriteLine("  FAILED: " + f);
    return 1;
}
return 0;

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
