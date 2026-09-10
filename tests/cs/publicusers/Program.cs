using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MediaBrowser.Model.Querying;
using Jellyfin.Database.Implementations.Entities.Security;
using MediaBrowser.Controller.Devices;
using System.Text;
using System.Text.Json;
using Jellyfin.Extensions.Json;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging.Abstractions;

// ─────────────────────────────────────────────────────────────────────────────
// What we actually write into /Users/Public.
//
// This file exists because 1.6.1.2 shipped a crash. The injector took its UserDto
// from Jellyfin — which was the rule, and which tests/js/publicusers.js checks —
// and then serialized it with a JsonSerializerOptions built by hand: a naming
// policy and ignore-nulls, which looked like the whole job. It is not. Jellyfin's
// JsonDefaults carries nine converters, and two of them decide whether a client
// can read the result at all:
//
//   enums   UserPolicy.SyncPlayAccess, UserPolicy.BlockUnratedItems and
//           UserConfiguration.SubtitleMode serialize as INTEGERS without
//           JsonStringEnumConverter. Every Jellyfin client expects strings.
//   Guids   Jellyfin writes them "N" — dashless. The default writes dashes.
//
// The Android TV app crashed on the first profile it was handed. The source gate
// could not have seen it: the code read correctly and did the wrong thing.
//
// So this harness does not read the source. It serializes a real DTO through the
// real assembly and reads the bytes back.
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

// Which build of the plugin to load. This harness's own output sits in
// bin/Release/<tfm>/, so its folder name IS the framework the csproj resolved.
var tfm = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Name;
var asm = Assembly.LoadFrom(Path.Combine(RepoRoot(), "bin", "Release", tfm, "Jellyfin.Profiles.dll"));

var injectorType = asm.GetType("Jellyfin.Profiles.Auth.PublicUserInjector", true);
var inject = injectorType.GetMethod("Inject", BindingFlags.Public | BindingFlags.Static);

// A profile as Jellyfin would hand it to us, with the fields that broke it populated.
var profileId = Guid.Parse("8e3cdfa5-79a8-4bb9-bd9a-0e96b7dc974a");
var dto = new UserDto
{
    Name = "Bardkids",
    Id = profileId,
    ServerId = "abc123",
    Policy = new UserPolicy
    {
        SyncPlayAccess = SyncPlayUserAccessType.JoinGroups,
        BlockUnratedItems = new[] { UnratedItem.Movie, UnratedItem.Series },
        AuthenticationProviderId = "x",
        PasswordResetProviderId = "y"
    },
    Configuration = new UserConfiguration
    {
        SubtitleMode = SubtitlePlaybackMode.Always
    }
};

var userManager = UserManagerStub.Create(profileId, dto);

byte[] Run(string contentType, string body = "[]")
    => (byte[])inject.Invoke(null, new object[]
    {
        Encoding.UTF8.GetBytes(body),
        contentType,
        (IReadOnlyList<Guid>)new List<Guid> { profileId },
        userManager,
        "127.0.0.1",
        NullLogger.Instance
    });

Console.WriteLine();
Console.WriteLine("── The bytes a television has to parse ─────────────────────────");

var produced = Run("application/json; profile=\"PascalCase\"");
Ok("a profile is added to an empty list", produced != null);

var json = produced == null ? "" : Encoding.UTF8.GetString(produced);
Console.WriteLine("        " + (json.Length > 150 ? json.Substring(0, 150) + "…" : json));

// The two that crashed the app, checked as values rather than as "it looks like JSON".
Ok("enums are written as strings, not integers",
   json.Contains("\"JoinGroups\"", StringComparison.Ordinal),
   "SyncPlayAccess came out as a number — no JsonStringEnumConverter");
Ok("enum arrays too", json.Contains("\"Movie\"", StringComparison.Ordinal));
Ok("and nested configuration enums", json.Contains("\"Always\"", StringComparison.Ordinal));

Ok("Guids are dashless, the way Jellyfin writes them",
   json.Contains("8e3cdfa579a84bb9bd9a0e96b7dc974a", StringComparison.OrdinalIgnoreCase),
   "the id carries dashes, so it disagrees with every other id the client has seen");
Ok("and no dashed Guid is present at all",
   !json.Contains("8e3cdfa5-79a8", StringComparison.OrdinalIgnoreCase));

Console.WriteLine();
Console.WriteLine("── And it survives the round trip ──────────────────────────────");

// The real question is not what the bytes look like but whether a client can read
// them. Deserialized with Jellyfin's own settings, which is what every client uses.
UserDto[] parsed = null;
string parseError = null;
try
{
    parsed = JsonSerializer.Deserialize<UserDto[]>(json, JsonDefaults.PascalCaseOptions);
}
catch (Exception ex) { parseError = ex.GetType().Name + ": " + ex.Message; }

Ok("Jellyfin's own deserializer reads it back", parsed != null, parseError);
if (parsed != null)
{
    Ok("with exactly one user in it", parsed.Length == 1, parsed.Length.ToString());
    Ok("the id survives", parsed[0].Id == profileId);
    Ok("the name survives", parsed[0].Name == "Bardkids");
    Ok("and the enum survives as its own value",
       parsed[0].Policy?.SyncPlayAccess == SyncPlayUserAccessType.JoinGroups);
}

Console.WriteLine();
Console.WriteLine("── Both casings, because the server negotiates them ────────────");

var camel = Encoding.UTF8.GetString(Run("application/json; profile=\"CamelCase\""));
Ok("camelCase is honoured when the response asked for it",
   camel.Contains("\"name\"", StringComparison.Ordinal)
   && !camel.Contains("\"Name\"", StringComparison.Ordinal));
Ok("and camelCase still writes enums as strings",
   camel.Contains("\"JoinGroups\"", StringComparison.Ordinal));

UserDto[] camelParsed = null;
try { camelParsed = JsonSerializer.Deserialize<UserDto[]>(camel, JsonDefaults.CamelCaseOptions); }
catch { /* reported below */ }
Ok("and it reads back too", camelParsed != null && camelParsed.Length == 1);

Console.WriteLine();
Console.WriteLine("── It leaves what Jellyfin returned alone ─────────────────────");

// A server that shows its users publicly already has entries here, and they must come
// through untouched — we are adding to somebody else's answer.
var existing = "[{\"Name\":\"someone-else\",\"Id\":\"11111111111111111111111111111111\"}]";
var merged = Encoding.UTF8.GetString(Run("application/json; profile=\"PascalCase\"", existing));
Ok("an existing user is still there", merged.Contains("someone-else", StringComparison.Ordinal));
Ok("and ours is added alongside", merged.Contains("Bardkids", StringComparison.Ordinal));

var mergedParsed = JsonSerializer.Deserialize<UserDto[]>(merged, JsonDefaults.PascalCaseOptions);
Ok("the merged list is two users", mergedParsed.Length == 2, mergedParsed.Length.ToString());

// Already present by id: adding a second copy would show one person twice on the
// sign-in screen, which on a public-users server is the normal case for the master.
var dup = "[{\"Name\":\"Bardkids\",\"Id\":\"8e3cdfa579a84bb9bd9a0e96b7dc974a\"}]";
var dupResult = Run("application/json; profile=\"PascalCase\"", dup);
Ok("a user Jellyfin already returned is not added twice", dupResult == null,
   "null means the response was left exactly as it arrived");

Console.WriteLine();
Console.WriteLine("── Whose television is this? ──────────────────────────────────");

// ResolveHousehold decides whether to touch the response at all, and it shipped in
// 1.6.1.4 with NO coverage. It required the signed-in account to have a mapping row of
// its own — which a MASTER usually does not have, because creating a profile writes a row
// for the profile only and a master's own row is written just once, lazily, the first time
// switcher preferences are saved. So signing in as yourself on a new television, the very
// first step of the whole feature, produced an empty list.
var resolve = injectorType.GetMethod("ResolveHousehold", BindingFlags.Public | BindingFlags.Static);
Ok("the injector exposes its household lookup", resolve != null);

if (resolve != null)
{
    var pluginType = asm.GetType("Jellyfin.Profiles.Plugin", true);
    var cfgType = asm.GetType("Jellyfin.Profiles.Configuration.PluginConfiguration", true);
    var mappingType = asm.GetType("Jellyfin.Profiles.Configuration.ProfileMapping", true);

    var tempDir = Path.Combine(Path.GetTempPath(), "bonfire-pubusers-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDir);
    var plugin = Activator.CreateInstance(pluginType, new StubPaths(tempDir), new StubXml());
    var config = pluginType.GetProperty("Configuration", BindingFlags.Public | BindingFlags.Instance)
        .GetValue(plugin);
    cfgType.GetProperty("EnableClientProfileList").SetValue(config, true);

    var MASTER = Guid.NewGuid();
    var KID = Guid.NewGuid();
    var GUEST = Guid.NewGuid();
    var STRANGER = Guid.NewGuid();

    object Mapping(Guid profile, Guid master)
    {
        var m = Activator.CreateInstance(mappingType);
        mappingType.GetProperty("ProfileUserId").SetValue(m, profile);
        mappingType.GetProperty("MasterUserId").SetValue(m, master);
        mappingType.GetProperty("ProfileName").SetValue(m, "p");
        return m;
    }

    var mappings = (System.Collections.IList)cfgType.GetProperty("Mappings").GetValue(config);
    // Deliberately NO row for MASTER — this is the shape a real household actually has.
    mappings.Add(Mapping(KID, MASTER));
    mappings.Add(Mapping(GUEST, MASTER));

    const string HEADER_FMT = "MediaBrowser Client=\"tv\", Device=\"telly\", DeviceId=\"{0}\", Version=\"1\"";

    IReadOnlyList<Guid> Resolve(string deviceId, Guid? lastUser, DateTime? when = null)
    {
        var dm = DeviceManagerStub.Create(deviceId, lastUser, when ?? DateTime.UtcNow);
        return (IReadOnlyList<Guid>)resolve.Invoke(null, new object[]
        {
            string.Format(HEADER_FMT, deviceId), null, dm, config, NullLogger.Instance
        });
    }

    // Silence was its own defect. 1.6.1.4 and 1.6.1.5 declined for six different reasons
    // and logged none of them, so a household seeing nothing had no way to learn whether
    // the request even arrived. Asserted rather than trusted, because a logger parameter is
    // exactly the kind of thing a later refactor drops as unused.
    var loggerParam = resolve.GetParameters().LastOrDefault();
    Ok("the lookup accepts a logger, so it can say why it declined",
       loggerParam != null && typeof(Microsoft.Extensions.Logging.ILogger).IsAssignableFrom(loggerParam.ParameterType),
       loggerParam?.ParameterType.Name);

    // The case that shipped broken.
    var afterMasterSignIn = Resolve("tv-1", MASTER);
    Ok("a master with no mapping row of its own is still recognised",
       afterMasterSignIn.Count == 3, afterMasterSignIn.Count + " user(s)");
    Ok("and the household is the master plus both profiles",
       afterMasterSignIn.Contains(MASTER) && afterMasterSignIn.Contains(KID)
       && afterMasterSignIn.Contains(GUEST));

    // Switching into a sub-profile makes IT the newest session; the household must not
    // empty, which is why the resolution goes through the master rather than the user.
    var afterProfileSignIn = Resolve("tv-1", KID);
    Ok("after switching into a profile the household is unchanged",
       afterProfileSignIn.Count == 3 && afterProfileSignIn.Contains(MASTER));

    // Everyone else on the server.
    Ok("an account Bonfire does not know resolves to nothing",
       Resolve("tv-1", STRANGER).Count == 0);
    Ok("a device nobody has signed in on resolves to nothing",
       Resolve("tv-unknown", null).Count == 0);

    // The header is the only way we learn the device. Moonfin sends none at all.
    var noHeader = (IReadOnlyList<Guid>)resolve.Invoke(null, new object[]
    {
        null, null, DeviceManagerStub.Create("tv-1", MASTER, DateTime.UtcNow), config, NullLogger.Instance
    });
    Ok("a request with no Authorization header resolves to nothing", noHeader.Count == 0);

    // Off means off, whatever else is true.
    cfgType.GetProperty("EnableClientProfileList").SetValue(config, false);
    Ok("and nothing resolves while the setting is off", Resolve("tv-1", MASTER).Count == 0);
    cfgType.GetProperty("EnableClientProfileList").SetValue(config, true);
}

Console.WriteLine();
if (fails.Count > 0)
{
    foreach (var f in fails) Console.WriteLine("   - " + f);
    Console.WriteLine(pass + " passed, " + fails.Count + " failed");
    Environment.Exit(1);
}
Console.WriteLine(pass + " passed, 0 failed");

// IUserManager gains and loses members between patch releases, so it is proxied rather
// than implemented — the same reason tests/cs/pipeline does it.
public class UserManagerStub : DispatchProxy
{
    private Guid _id;
    private UserDto _dto;

    public static IUserManager Create(Guid id, UserDto dto)
    {
        var proxy = Create<IUserManager, UserManagerStub>();
        var stub = (UserManagerStub)(object)proxy;
        stub._id = id;
        stub._dto = dto;
        return proxy;
    }

    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        switch (targetMethod.Name)
        {
            // Returns a non-null placeholder: the injector only checks it for null before
            // asking for the DTO, and constructing a real User here would tie this harness
            // to an entity shape that has moved twice.
            case "GetUserById":
                return (Guid)args[0] == _id
                    ? System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                        typeof(Jellyfin.Database.Implementations.Entities.User))
                    : null;

            case "GetUserDto":
                return _dto;

            default:
                throw new NotSupportedException(
                    "the injector called IUserManager." + targetMethod.Name
                    + ", which this harness does not model");
        }
    }
}

// One device, one last-known user. DispatchProxy because IDeviceManager gains and loses
// members between patch releases.
public class DeviceManagerStub : DispatchProxy
{
    private string _deviceId;
    private Guid? _userId;
    private DateTime _when;

    public static IDeviceManager Create(string deviceId, Guid? userId, DateTime when)
    {
        var proxy = Create<IDeviceManager, DeviceManagerStub>();
        var stub = (DeviceManagerStub)(object)proxy;
        stub._deviceId = deviceId;
        stub._userId = userId;
        stub._when = when;
        return proxy;
    }

    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        if (targetMethod.Name != "GetDevices")
        {
            throw new NotSupportedException(
                "the injector called IDeviceManager." + targetMethod.Name
                + ", which this harness does not model");
        }

        var query = args[0];
        var wanted = (string)query.GetType().GetProperty("DeviceId").GetValue(query);

        var items = new List<Device>();
        if (_userId.HasValue && string.Equals(wanted, _deviceId, StringComparison.Ordinal))
        {
            var d = (Device)System.Runtime.CompilerServices.RuntimeHelpers
                .GetUninitializedObject(typeof(Device));
            typeof(Device).GetProperty("UserId").SetValue(d, _userId.Value);
            typeof(Device).GetProperty("DeviceId").SetValue(d, _deviceId);
            typeof(Device).GetProperty("DateLastActivity").SetValue(d, _when);
            items.Add(d);
        }

        return new QueryResult<Device>(items);
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
