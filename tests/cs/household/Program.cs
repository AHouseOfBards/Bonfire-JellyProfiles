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
// What Bonfire tells the other Campground plugins: campground.household.v1 at minor 1
// (getHousehold, getSignInPolicy, and the profile events) and the events that go with it. Adoption step 4 in Campground-Coordination design/bonfire/campground.md.
//
// The questions are asked the way a caller asks them: through the shared registry, by
// capability name and operation, so what is checked is what another plugin would get,
// not what a Bonfire method returns.
//
// The trap this exists for: most main accounts have no mapping row of their own. A provider
// that answers from "the user's row" alone puts nearly every household at a household of
// one, and every test that only uses a profile would pass.
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
bool offered = (bool)capabilities.GetMethods(Any).First(m => m.Name == "IsAvailable").Invoke(null, new object[] { HOUSEHOLD, 1 });

Console.WriteLine();
Console.WriteLine("── The provider is offered ─────────────────────────────────────");
Ok("household.v1 is offered at minor 1 or later", offered);

Console.WriteLine();
Console.WriteLine("── getHousehold ────────────────────────────────────────────────");

var kid = GetHousehold(KID, 1);
Ok("a profile: its own name, its main account, and it is a profile",
   !IsNull(kid) && Flag(kid.Value, "isProfile") && Id(kid.Value, "mainUserId") == MAIN && Str(kid.Value, "displayName") == "Kids",
   kid?.ToString());
Ok("a profile's household lists the main account first (by username) and every profile",
   !IsNull(kid) && Members(kid.Value).Count == 3
   && Id(Members(kid.Value)[0], "userId") == MAIN && Str(Members(kid.Value)[0], "displayName") == "alice" && !Flag(Members(kid.Value)[0], "isProfile")
   && Members(kid.Value).Count(m => Flag(m, "isProfile")) == 2
   && Members(kid.Value).Any(m => Id(m, "userId") == TEEN && Str(m, "displayName") == "Teen"));

var main = GetHousehold(MAIN, 1);
Ok("a main account with NO row of its own, but profiles pointing at it, is a main account, not a household of one",
   !IsNull(main) && !Flag(main.Value, "isProfile") && Id(main.Value, "mainUserId") == MAIN && Members(main.Value).Count == 3,
   main?.ToString());
Ok("and its display name is its Jellyfin username",
   !IsNull(main) && Str(main.Value, "displayName") == "alice");

var ownrow = GetHousehold(OWNROW, 1);
Ok("a main account whose own row was written (switcher preferences) is still itself and not a profile",
   !IsNull(ownrow) && !Flag(ownrow.Value, "isProfile") && Id(ownrow.Value, "mainUserId") == OWNROW
   && Members(ownrow.Value).Count == 1 && Str(ownrow.Value, "displayName") == "carol",
   ownrow?.ToString());

var loner = GetHousehold(LONER, 1);
Ok("a user with no record is a household of one (the provider says nothing, the library's answer applies)",
   offered && IsNull(loner), loner?.ToString());
Ok("a user it has never heard of does not throw or invent a household",
   offered && IsNull(GetHousehold(Guid.NewGuid(), 1)));

Console.WriteLine();
Console.WriteLine("── The sharing group (minor 1) ─────────────────────────────────");

var g1 = main is { } mm && mm.TryGetProperty("sharingGroup", out var sg1) && sg1.ValueKind == JsonValueKind.Object ? sg1 : (JsonElement?)null;
Ok("the main account's group: id, owner, and every main account in it, owner first",
   g1 != null && Str(g1.Value, "groupId") == "grp-1" && Id(g1.Value, "ownerUserId") == MAIN
   && g1.Value.GetProperty("memberUserIds").EnumerateArray().Select(x => Guid.Parse(x.GetString())).SequenceEqual(new[] { MAIN, FRIEND }),
   g1?.ToString());
var kidGroup = !IsNull(kid) && kid.Value.TryGetProperty("sharingGroup", out var sg2) && sg2.ValueKind == JsonValueKind.Object;
Ok("a profile gets its main account's group", kidGroup);
var friend = GetHousehold(FRIEND, 1);
Ok("a main account with no row and no profiles is still in its group (not a household of one)",
   !IsNull(friend) && friend.Value.TryGetProperty("sharingGroup", out var sg3) && sg3.ValueKind == JsonValueKind.Object
   && Str(sg3, "groupId") == "grp-1" && Members(friend.Value).Count == 1,
   friend?.ToString());
Ok("a household outside every group has none",
   !IsNull(ownrow) && (!ownrow.Value.TryGetProperty("sharingGroup", out var sg4) || sg4.ValueKind == JsonValueKind.Null));
var everything = string.Join("\n", new[] { kid, main, friend, ownrow, SignInPolicy(MAIN), GetHousehold(OTHER, 1) }.Select(e => e?.ToString() ?? ""));
Ok("the join code is nowhere in any answer, under any field name",
   offered && !everything.Contains(JOINCODE) && !everything.ToLowerInvariant().Contains("code") && !everything.ToLowerInvariant().Contains("bonfirecode"));

Console.WriteLine();
Console.WriteLine("── getSignInPolicy ─────────────────────────────────────────────");

SetConfig("EnableClientPinLogin", true);
SetConfig("ClientPinLoginFromAnywhere", true);
var onPol = SignInPolicy(MAIN);
Ok("PIN login on other apps and from anywhere: pinLoginFromAnywhere is true",
   !IsNull(onPol) && Flag(onPol.Value, "pinLoginFromAnywhere"), onPol?.ToString());
SetConfig("ClientPinLoginFromAnywhere", false);
var offPol = SignInPolicy(MAIN);
Ok("home network only: pinLoginFromAnywhere is false, and the answer is there",
   !IsNull(offPol) && offPol.Value.TryGetProperty("pinLoginFromAnywhere", out var pf) && pf.ValueKind == JsonValueKind.False,
   offPol?.ToString());
SetConfig("ClientPinLoginFromAnywhere", true);
SetConfig("EnableClientPinLogin", false);
var disabledPol = SignInPolicy(MAIN);
Ok("PIN login on other apps turned off: nothing opens from anywhere, so false, even with the setting left on",
   !IsNull(disabledPol) && disabledPol.Value.TryGetProperty("pinLoginFromAnywhere", out var pd) && pd.ValueKind == JsonValueKind.False,
   disabledPol?.ToString());
SetConfig("EnableClientPinLogin", true);
Ok("it is asked at minor 1, so a provider without the op fails the caller open",
   offered && !IsNull(SignInPolicy(LONER)));

Console.WriteLine();
Console.WriteLine("── Events, each once per change ────────────────────────────────");

var seen = new Dictionary<string, List<JsonElement>>();
var subs = new List<IDisposable>();
foreach (var field in new[] { "ProfileCreated", "ProfileRenamed", "ProfileDeleted", "ProfileSwitched" })
{
    var name = Const(eventNames, field);
    seen[field] = new List<JsonElement>();
    var list = seen[field];
    Action<JsonElement> handler = e => { lock (list) list.Add(e.Clone()); };
    subs.Add((IDisposable)events.GetMethod("Subscribe", Any).Invoke(null, new object[] { name, handler }));
}

void Raise(string method, params object[] args)
{
    var m = provider?.GetMethod(method, Any);
    m?.Invoke(null, args);
}

Raise("RaiseCreated", MAIN, KID, "Kids");
Raise("RaiseRenamed", MAIN, KID, "Little kids");
Raise("RaiseDeleted", MAIN, TEEN, "Teen");
Ok("created: raised once, with the main account, the profile and its name",
   seen["ProfileCreated"].Count == 1 && Id(seen["ProfileCreated"][0], "mainUserId") == MAIN
   && Id(seen["ProfileCreated"][0], "profileUserId") == KID && Str(seen["ProfileCreated"][0], "displayName") == "Kids");
Ok("renamed: raised once, with the new name",
   seen["ProfileRenamed"].Count == 1 && Str(seen["ProfileRenamed"][0], "displayName") == "Little kids");
Ok("deleted: raised once",
   seen["ProfileDeleted"].Count == 1 && Id(seen["ProfileDeleted"][0], "profileUserId") == TEEN);
Ok("and no event answers another's change",
   seen["ProfileCreated"].Count == 1 && seen["ProfileRenamed"].Count == 1 && seen["ProfileDeleted"].Count == 1 && seen["ProfileSwitched"].Count == 0);

// The switch route raises it, and so does the session listener, and the route's own sign-in
// starts a session the listener hears about. Once per switch, with the previous account.
Raise("ExpectSwitch", KID, "dev-A");
Raise("RaiseSwitched", MAIN, KID, "Kids", "dev-A", (Guid?)MAIN);       // the route, after it signed in
Raise("RaiseSwitchedFromSession", MAIN, KID, "Kids", "dev-A", (Guid?)null); // the listener, for the session that route started
Ok("a switch from the web switcher is one event, not one from the route and one from the listener",
   seen["ProfileSwitched"].Count == 1, "got " + seen["ProfileSwitched"].Count);
Ok("it carries the device and who it switched from",
   seen["ProfileSwitched"].Count == 1 && Str(seen["ProfileSwitched"][0], "deviceId") == "dev-A"
   && Id(seen["ProfileSwitched"][0], "previousUserId") == MAIN && Id(seen["ProfileSwitched"][0], "profileUserId") == KID);
Raise("RaiseSwitchedFromSession", MAIN, TEEN, "Teen", "tv-1", (Guid?)null); // a native app: no route involved
Ok("a switch on a native TV app (the listener alone) is one event",
   seen["ProfileSwitched"].Count == 2 && Id(seen["ProfileSwitched"][1], "profileUserId") == TEEN);

// The routes and the listener must actually call them, once per change site. Read from source:
// the routes take a live Jellyfin to run, which no harness here stands up.
string Source(params string[] rel) => string.Join("\n", rel.Select(r => File.ReadAllText(Path.Combine(root, r))));
int Count(string text, string pattern) => Regex.Matches(text, pattern).Count;
var controllers = Source(Directory.GetFiles(Path.Combine(root, "Controllers"), "*.cs").Select(f => Path.GetRelativePath(root, f)).ToArray());
Ok("two routes make a profile (create, make-profile) and each raises created",
   Count(controllers, @"BonfireCampground\.RaiseCreated\(") == 2, "found " + Count(controllers, @"BonfireCampground\.RaiseCreated\("));
Ok("two routes rename one (update, the profile's own edit) and each raises renamed",
   Count(controllers, @"BonfireCampground\.RaiseRenamed\(") == 2, "found " + Count(controllers, @"BonfireCampground\.RaiseRenamed\("));
Ok("three places remove one (delete, make-its-own-account, clean-up) and each raises deleted",
   Count(controllers, @"BonfireCampground\.RaiseDeleted\(") == 3, "found " + Count(controllers, @"BonfireCampground\.RaiseDeleted\("));
Ok("the switch route expects its own session, then raises switched once",
   Count(controllers, @"BonfireCampground\.ExpectSwitch\(") == 1 && Count(controllers, @"BonfireCampground\.RaiseSwitched\(") == 1);
Ok("the session listener raises switched for a native app",
   Count(Source("Auth/BonfireSessionListener.cs"), @"BonfireCampground\.RaiseSwitchedFromSession\(") == 1);

Console.WriteLine();
Console.WriteLine("── Emergency disable ───────────────────────────────────────────");

// Once tripped, only a restart clears it, so the flag is put back by hand afterwards.
var panicField = pluginType.GetField("_panicDisabled", Any);
var before = seen["ProfileSwitched"].Count + seen["ProfileCreated"].Count;
pluginType.GetMethod("TripPanicDisable", Any).Invoke(null, null);
try
{
    Ok("while Bonfire is emergency-disabled, a profile answers as a household of one (no answer)",
       offered && IsNull(GetHousehold(KID, 1)) && IsNull(GetHousehold(MAIN, 1)));
    Ok("the sign-in policy is not answered either, so a caller fails open",
       offered && IsNull(SignInPolicy(MAIN)));
    Raise("RaiseCreated", MAIN, OTHERKID, "x");
    Raise("RaiseSwitchedFromSession", MAIN, KID, "Kids", "dev-Z", (Guid?)null);
    Ok("and no event is raised",
       offered && seen["ProfileSwitched"].Count + seen["ProfileCreated"].Count == before);
}
finally
{
    panicField.SetValue(null, false);
}
Ok("restarting Bonfire restores every answer",
   offered && !IsNull(GetHousehold(KID, 1)));

foreach (var s in subs) s.Dispose();

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
