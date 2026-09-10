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

byte[] Run(string contentType, string body = "[]", object nameConfig = null)
    => (byte[])inject.Invoke(null, new object[]
    {
        Encoding.UTF8.GetBytes(body),
        contentType,
        (IReadOnlyList<Guid>)new List<Guid> { profileId },
        userManager,
        "127.0.0.1",
        NullLogger.Instance,
        nameConfig
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

// Case is not cosmetic here, and the two assertions above cannot see it because they are
// both OrdinalIgnoreCase.
//
// Android TV parses every id through the Kotlin SDK's UUIDSerializer, which re-inserts
// the dashes with this regex before handing the string to UUID.fromString:
//
//     "^([a-z\d]{8})([a-z\d]{4})([a-z\d]{4})([a-z\d]{4})([a-z\d]{12})$"
//
// [a-z\d] does not match uppercase hex, so an uppercase id matches nothing, keeps its
// dashless 32-character form, and UUID.fromString throws. `id` is a non-null UUID field,
// so deserialize() throws rather than returning null — which fails the WHOLE response,
// not our row: getPublicServerUsers catches ApiClientException and returns emptyList(),
// so the picker shows nothing at all, real accounts included, with only a Timber line to
// say why. .NET's "N" format is lowercase and this has never been wrong; it is asserted
// because nothing else would notice if it became wrong.
Ok("Guids are lowercase, which is what the Kotlin SDK's [a-z\\d] regex will accept",
   json.Contains("8e3cdfa579a84bb9bd9a0e96b7dc974a", StringComparison.Ordinal),
   "an uppercase id throws in UUIDSerializer and empties the entire user list");

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

    object MappingNamed(Guid profile, Guid master, string name)
    {
        var m = Activator.CreateInstance(mappingType);
        mappingType.GetProperty("ProfileUserId").SetValue(m, profile);
        mappingType.GetProperty("MasterUserId").SetValue(m, master);
        mappingType.GetProperty("ProfileName").SetValue(m, name);
        return m;
    }

    object MappingWithPin(Guid profile, Guid master, string pinHash)
    {
        var m = Activator.CreateInstance(mappingType);
        mappingType.GetProperty("ProfileUserId").SetValue(m, profile);
        mappingType.GetProperty("MasterUserId").SetValue(m, master);
        mappingType.GetProperty("ProfileName").SetValue(m, "p");
        mappingType.GetProperty("PinHash").SetValue(m, pinHash);
        return m;
    }

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

    Console.WriteLine();
    Console.WriteLine("-- After the television signs out -----------------------------");

    // THE BLOCKER, and the reason 1.6.1.4 through 1.6.1.6 could never work on the client
    // they were built for.
    //
    // Jellyfin treats a Device row as belonging to a SESSION, not to a device:
    //
    //     public async Task Logout(Device device)          // SessionManager
    //     { await _deviceManager.DeleteDevice(device); }
    //
    // and Android TV's "Switch account" destroys the session BEFORE opening the picker:
    //
    //     sessionRepository.destroyCurrentSession()
    //     activity?.startActivity(ActivityDestinations.startup(activity))
    //
    // So the exact action that opens the picker deletes the only record we keyed on. It
    // showed up in Logan's log seconds after a successful sign-in on that same set:
    //
    //     user list requested by device "9a6dae35cc29c74f", which nobody has signed in on yet
    //
    // Bonfire keeps its own DeviceId -> master record instead, which survives a sign-out
    // precisely because it is not a session artefact. Every assertion below drives the
    // device manager EMPTY - the way a real television presents itself at the picker.
    var devicesList = (System.Collections.IList)cfgType.GetProperty("KnownDevices").GetValue(config);
    var knownType = asm.GetType("Jellyfin.Profiles.Configuration.KnownDevice", true);

    void Remember(string deviceId, Guid owner, DateTime? seen = null)
    {
        var d = Activator.CreateInstance(knownType);
        knownType.GetProperty("DeviceId").SetValue(d, deviceId);
        knownType.GetProperty("MasterUserId").SetValue(d, owner);
        knownType.GetProperty("LastSeen").SetValue(d, seen ?? DateTime.UtcNow);
        devicesList.Add(d);
    }

    IReadOnlyList<Guid> ResolveSignedOut(string deviceId)
    {
        var dm = DeviceManagerStub.Create(deviceId, null, DateTime.UtcNow);
        return (IReadOnlyList<Guid>)resolve.Invoke(null, new object[]
        {
            string.Format(HEADER_FMT, deviceId), null, dm, config, NullLogger.Instance
        });
    }

    Remember("tv-signedout", MASTER);
    var signedOut = ResolveSignedOut("tv-signedout");
    Ok("a signed-out television still resolves to its household",
       signedOut.Count == 3, signedOut.Count + " user(s)");
    Ok("and to the same household the deleted Device row would have given",
       signedOut.Contains(MASTER) && signedOut.Contains(KID) && signedOut.Contains(GUEST));

    // Ownership is the whole content of the record. Rows written before MasterUserId
    // existed carry Guid.Empty, and those identify nobody.
    Remember("tv-unowned", Guid.Empty);
    Ok("a remembered device with no owner resolves to nothing",
       ResolveSignedOut("tv-unowned").Count == 0);

    // Our own record must not become a way into a household Bonfire does not run.
    Remember("tv-stranger", STRANGER);
    Ok("a remembered device owned by a non-Bonfire account resolves to nothing",
       ResolveSignedOut("tv-stranger").Count == 0);

    // A device we have no record of stays nothing. This is what stops a forged DeviceId
    // from walking the server's households.
    Ok("a television Bonfire has never seen resolves to nothing",
       ResolveSignedOut("tv-never").Count == 0);

    // Revocation has to mean something once the record is ours rather than Jellyfin's:
    // removing the row in the dashboard is the only way to forget a television.
    var savedRows = new List<object>();
    foreach (var d in devicesList) savedRows.Add(d);
    devicesList.Clear();
    Ok("forgetting the device forgets the household",
       ResolveSignedOut("tv-signedout").Count == 0);
    foreach (var d in savedRows) devicesList.Add(d);

    // And the live path must keep working. A set somebody IS signed in on has to resolve
    // from Jellyfin's own record, with nothing of ours to help it.
    devicesList.Clear();
    Ok("a signed-in device still resolves with no remembered record",
       Resolve("tv-1", MASTER).Count == 3);

    Console.WriteLine();
    Console.WriteLine("-- Who fills the record in ------------------------------------");

    // KnownDevices has existed since the device-restrictions work and already had
    // everything the map needed: a DeviceId, a MasterUserId, a LastSeen, pruning, and a
    // dashboard row an administrator can delete. What it did not have was a writer that a
    // television ever reaches - both call sites were authenticated Bonfire routes, which
    // only the web switcher calls. The map was populated exclusively by the one client
    // that had no use for it.
    var registry = asm.GetType("Jellyfin.Profiles.Auth.DeviceRegistry", false);
    Ok("device recording is reachable without a controller", registry != null);

    var record = registry?.GetMethod("Record", BindingFlags.Public | BindingFlags.Static);
    Ok("the registry exposes Record", record != null);

    if (record != null)
    {
        devicesList.Clear();
        var now = DateTime.UtcNow;
        record.Invoke(null, new object[] { config, "tv-fresh", "Living Room TV", "Android TV", MASTER, now, null });
        Ok("authenticating on a new television records it", devicesList.Count == 1);
        Ok("and the household then resolves from that record alone",
           ResolveSignedOut("tv-fresh").Count == 3);

        // The name rules are shipped behaviour being moved, not rewritten, so they are
        // pinned here: a client that sends nothing must never blank a name that was good.
        record.Invoke(null, new object[] { config, "tv-fresh", "", "Android TV", MASTER, now, null });
        Ok("a later sign-in with no name does not blank the name we had",
           (string)knownType.GetProperty("DeviceName").GetValue(devicesList[0]) == "Living Room TV");

        // Signing in as a sub-profile must attribute the set to the household, not to the
        // profile - otherwise the second person to use the television takes it over and
        // the master's own profiles stop appearing.
        devicesList.Clear();
        record.Invoke(null, new object[] { config, "tv-kid", "Bedroom", "Android TV", MASTER, now, null });
        Ok("a device is attributed to the master, so the household survives a switch",
           (Guid)knownType.GetProperty("MasterUserId").GetValue(devicesList[0]) == MASTER);

        // An id with surrounding space is a different string to every ordinal comparison
        // in the plugin, including the whitelist checks. It is trimmed at the door.
        devicesList.Clear();
        record.Invoke(null, new object[] { config, "  tv-spaced  ", "TV", "Android TV", MASTER, now, null });
        Ok("a device id is trimmed before it is stored",
           devicesList.Count == 1
           && (string)knownType.GetProperty("DeviceId").GetValue(devicesList[0]) == "tv-spaced");

        // Nothing to key on is nothing to record.
        devicesList.Clear();
        record.Invoke(null, new object[] { config, null, "TV", "Android TV", MASTER, now, null });
        Ok("a sign-in with no device id records nothing", devicesList.Count == 0);
    }

    Console.WriteLine();
    Console.WriteLine("-- One television, two device ids ------------------------------");

    // The defect that made 1.6.1.7 useless, and the reason it looked like nothing was
    // recorded when the recording was perfect.
    //
    // Android TV uses a DIFFERENT device id once a user is chosen. SessionRepository
    // applies `defaultDeviceInfo.forUser(userId)`, and forUser is:
    //
    //     fun DeviceInfo.forUser(user: String): DeviceInfo = copy(
    //         id = SHA-1("${id}+$user") as lowercase hex)
    //
    // At the picker there is no session, so `defaultDeviceInfo` is used unchanged - the
    // raw ANDROID_ID. So we record a per-user hash and are then asked about the base id,
    // and the two can never be equal.
    //
    // These are Logan's real values, from the 2026-09-10 15:09 log. Keeping the actual
    // triple means this test fails if the derivation ever changes shape, rather than only
    // if my re-implementation of it disagrees with itself.
    static string Sha1(string value) => Convert.ToHexString(
        System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(value)))
        .ToLowerInvariant();

    const string TV_BASE = "9a6dae35cc29c74f";
    const string TV_DERIVED = "7629ac2570f4154e81c8ce3f99a6d4764eb7e734";
    var LOG_MASTER = Guid.Parse("8615867e-3617-4ad3-8d62-639b3bdc1305");

    devicesList.Clear();
    mappings.Clear();
    mappings.Add(Mapping(KID, LOG_MASTER));
    Remember(TV_DERIVED, LOG_MASTER);

    var viaHash = (IReadOnlyList<Guid>)resolve.Invoke(null, new object[]
    {
        string.Format(HEADER_FMT, TV_BASE), null,
        DeviceManagerStub.Create(TV_BASE, null, DateTime.UtcNow), config, NullLogger.Instance
    });
    Ok("a television recorded under its per-user hash resolves from the base id",
       viaHash.Count == 2 && viaHash.Contains(LOG_MASTER),
       viaHash.Count + " user(s)");

    // Jellyfin for Android derives differently - it appends the user id with no hash and
    // no separator. Same log, device 6044d6840404e4e8.
    devicesList.Clear();
    Remember("6044d6840404e4e8" + LOG_MASTER.ToString(), LOG_MASTER);
    var viaConcat = (IReadOnlyList<Guid>)resolve.Invoke(null, new object[]
    {
        string.Format(HEADER_FMT, "6044d6840404e4e8"), null,
        DeviceManagerStub.Create("6044d6840404e4e8", null, DateTime.UtcNow), config, NullLogger.Instance
    });
    Ok("and one recorded with the user id appended resolves too",
       viaConcat.Count == 2 && viaConcat.Contains(LOG_MASTER));

    // A sub-profile is what gets signed into after the first switch, so the hash is taken
    // over the PROFILE's id while the record still belongs to the master.
    devicesList.Clear();
    Remember(Sha1(TV_BASE + "+" + KID.ToString()), LOG_MASTER);
    var viaProfile = (IReadOnlyList<Guid>)resolve.Invoke(null, new object[]
    {
        string.Format(HEADER_FMT, TV_BASE), null,
        DeviceManagerStub.Create(TV_BASE, null, DateTime.UtcNow), config, NullLogger.Instance
    });
    Ok("a hash taken over a sub-profile's id still resolves to the household",
       viaProfile.Count == 2 && viaProfile.Contains(LOG_MASTER));

    // The derivation must not become a way to guess at households. A hash over an account
    // Bonfire knows nothing about matches no record.
    devicesList.Clear();
    Remember(Sha1(TV_BASE + "+" + STRANGER.ToString()), Guid.Empty);
    var viaStranger = (IReadOnlyList<Guid>)resolve.Invoke(null, new object[]
    {
        string.Format(HEADER_FMT, TV_BASE), null,
        DeviceManagerStub.Create(TV_BASE, null, DateTime.UtcNow), config, NullLogger.Instance
    });
    Ok("a hash over an account Bonfire does not know resolves to nothing",
       viaStranger.Count == 0);

    // And the plain case must not regress: most clients send one id and never derive it.
    devicesList.Clear();
    Remember("plain-tv", LOG_MASTER);
    var viaExact = (IReadOnlyList<Guid>)resolve.Invoke(null, new object[]
    {
        string.Format(HEADER_FMT, "plain-tv"), null,
        DeviceManagerStub.Create("plain-tv", null, DateTime.UtcNow), config, NullLogger.Instance
    });
    Ok("a client that does not derive its device id still matches exactly",
       viaExact.Count == 2 && viaExact.Contains(LOG_MASTER));

    // Restore the fixture the later sections expect.
    devicesList.Clear();
    mappings.Clear();
    mappings.Add(Mapping(KID, MASTER));
    mappings.Add(Mapping(GUEST, MASTER));

    Console.WriteLine();
    Console.WriteLine("-- Both kinds of profile are offered ---------------------------");

    // A PIN-less profile is still listed, because on a device the household uses it still
    // opens - see BonfirePinAuthenticationProvider. Listing only PIN-protected ones was
    // considered and rejected: it would have hidden the profiles most households set up
    // first, on the one screen this feature exists to serve.
    devicesList.Clear();
    mappings.Clear();
    var PINNED = Guid.NewGuid();
    var PINLESS = Guid.NewGuid();
    mappings.Add(MappingWithPin(PINNED, MASTER, "hashed-pin"));
    mappings.Add(MappingWithPin(PINLESS, MASTER, string.Empty));
    Remember("tv-pins", MASTER);

    var offered = ResolveSignedOut("tv-pins");
    Ok("a sub-profile with a PIN is offered", offered.Contains(PINNED));
    Ok("a sub-profile with no PIN is offered too", offered.Contains(PINLESS),
       offered.Count + " user(s) offered");
    Ok("and so is the master", offered.Contains(MASTER));

    devicesList.Clear();
    mappings.Clear();
    mappings.Add(Mapping(KID, MASTER));
    mappings.Add(Mapping(GUEST, MASTER));

    Console.WriteLine();
    Console.WriteLine("-- The name a household actually uses -------------------------");

    // Sub-profiles are created with a system username of `<master>_<profile>` to avoid
    // colliding with every other household on the server, so the sign-in screen was
    // offering "Bardkids" where the household calls that person "kids". The name is
    // rewritten to the one they chose.
    //
    // This has to be done in the response rather than by renaming the account: the system
    // username is what keeps two households' "kids" apart, and the web switcher, the audit
    // log and Jellyfin's own dashboard all show it.
    devicesList.Clear();
    mappings.Clear();
    mappings.Add(MappingNamed(profileId, MASTER, "kids"));

    var friendly = Encoding.UTF8.GetString(Run("application/json", "[]", config));
    Ok("a profile is offered under the name its household gave it",
       friendly.Contains("\"kids\"", StringComparison.Ordinal),
       friendly.Length > 400 ? friendly.Substring(0, 200) : friendly);
    Ok("and the system username is not shown",
       !friendly.Contains("Bardkids", StringComparison.Ordinal));

    // camelCase is the other half of the content negotiation and must be rewritten too -
    // the property is "name" there, and missing it would show the raw username on exactly
    // the clients that ask for camelCase.
    var friendlyCamel = Encoding.UTF8.GetString(
        Run("application/json; profile=\"CamelCase\"", "[]", config));
    Ok("the same rewrite happens in a camelCase response",
       friendlyCamel.Contains("\"kids\"", StringComparison.Ordinal)
       && !friendlyCamel.Contains("Bardkids", StringComparison.Ordinal));

    // Nothing to rename is not an error - a master, or a server that has not upgraded its
    // mappings, must come back exactly as Jellyfin wrote it.
    mappings.Clear();
    var untouched = Encoding.UTF8.GetString(Run("application/json", "[]", config));
    Ok("an account with no mapping keeps the name Jellyfin gave it",
       untouched.Contains("Bardkids", StringComparison.Ordinal));

    var noConfig = Encoding.UTF8.GetString(Run("application/json", "[]", null));
    Ok("and so does every account when there is no configuration at all",
       noConfig.Contains("Bardkids", StringComparison.Ordinal));

    devicesList.Clear();
    mappings.Clear();
    mappings.Add(Mapping(KID, MASTER));
    mappings.Add(Mapping(GUEST, MASTER));

    // Recording must be scoped to households Bonfire actually runs.
    //
    // Every account on the server signs in, not just households with profiles. The
    // listener asks HouseholdOf for the owning master, and HouseholdOf answers with the
    // user's OWN id when it finds no mapping — a sensible answer to the question it was
    // asked, and a wrong one to key a filter on, because it is never Guid.Empty. Left
    // that way, a forty-user server would write a KnownDevices row for every phone and
    // browser that ever signed in, into an XML file that is rewritten whole.
    var isHousehold = registry?.GetMethod("IsHousehold", BindingFlags.Public | BindingFlags.Static);
    Ok("the registry can say whether an account is a Bonfire household at all",
       isHousehold != null);

    if (isHousehold != null)
    {
        Ok("a master with profiles is a household",
           (bool)isHousehold.Invoke(null, new object[] { config, MASTER }));
        Ok("a sub-profile is a household",
           (bool)isHousehold.Invoke(null, new object[] { config, KID }));
        Ok("an account with no profiles and no master is not",
           !(bool)isHousehold.Invoke(null, new object[] { config, STRANGER }));
        Ok("and neither is nobody",
           !(bool)isHousehold.Invoke(null, new object[] { config, Guid.Empty }));
    }

    // The writer is only half of it. A listener nobody registered does nothing at all,
    // which is the silent-failure shape that has cost this project four releases, so the
    // subscription is asserted rather than assumed.
    var listener = asm.GetType("Jellyfin.Profiles.Auth.BonfireSessionListener", false);
    Ok("something subscribes to authentication so every client feeds the map",
       listener != null);
    Ok("and it is a hosted service, so Jellyfin actually starts it",
       listener != null && typeof(Microsoft.Extensions.Hosting.IHostedService).IsAssignableFrom(listener));

    var registrar = File.ReadAllText(Path.Combine(RepoRoot(), "PluginServiceRegistrator.cs"));
    Ok("and it is registered with the DI container",
       registrar.Contains("BonfireSessionListener"));

    // The writing half has to be as loud as the reading half, and in 1.6.1.7 it was not:
    // the recording logged at Debug, so a household that saw no profiles could not be told
    // apart from a listener that never fired or never started. That is the same defect
    // 1.6.1.6 shipped to fix on the reading side, repeated one release later on the other
    // side of the same feature.
    var listenerSrc = File.ReadAllText(
        Path.Combine(RepoRoot(), "Auth", "BonfireSessionListener.cs"));
    Ok("the listener announces itself at startup, so silence means it did not start",
       listenerSrc.Contains("StartAsync")
       && listenerSrc.Split(new[] { "StartAsync" }, StringSplitOptions.None)[1]
                     .Split(new[] { "StopAsync" }, StringSplitOptions.None)[0]
                     .Contains("LogInformation"));
    Ok("and reports what it recorded at Information, not Debug",
       listenerSrc.Contains("noted, so its profiles can be offered")
       && !listenerSrc.Contains("LogDebug"));

    devicesList.Clear();
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

