using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

// ─────────────────────────────────────────────────────────────────────────────
// 2.0 folds four reads of the profile form into one, and a theme save into /update.
//
//   GET /form-options   = what GET /libraries, /ratings, /avatars and /timezones answered,
//                         for the same caller, built by the same code.
//   POST /update        = what POST /theme enforced: the main account only, behind its PIN,
//                         a known theme only, and only while the server lets profiles choose.
//   the five old routes no longer exist.
//
// tests/golden/formoptions.json holds what the four old routes answered for a main account
// and for a profile's token, captured before they were removed:
//
//     dotnet run -c Release -- --capture <file>      (against a tree that still has them)
//
// Every run compares the new route to it part by part. Only this machine's time zone list
// and zone are left out of the comparison.
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
void Section(string s) { Console.WriteLine(); Console.WriteLine("── " + s + " " + new string('─', Math.Max(3, 60 - s.Length))); }

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
var avatarItemType = asm.GetType("Jellyfin.Profiles.Configuration.AvatarLibraryItem", true);
var hasher = asm.GetType("Jellyfin.Profiles.Auth.PinHasher", true);

var tempDir = Path.Combine(Path.GetTempPath(), "bonfire-formoptions-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
var plugin = Activator.CreateInstance(pluginType, new StubPaths(tempDir), new StubXml());
var config = pluginType.GetProperty("Configuration").GetValue(plugin);
config.GetType().GetProperty("BonfireOn").SetValue(config, true);
var mappings = (IList)cfgType.GetProperty("Mappings").GetValue(config);
void SetCfg(string name, object value) => cfgType.GetProperty(name).SetValue(config, value);

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

// Libraries: two the first household has, one only the other household has, and one whose
// id Jellyfin could not give (never scanned), which no form should offer.
var L1 = Guid.Parse("11111111-1111-1111-1111-111111111111"); var L2 = Guid.Parse("22222222-2222-2222-2222-222222222222"); var L3 = Guid.Parse("33333333-3333-3333-3333-333333333333");
var world = new World();
world.Add(L1, "Movies", null); world.Add(L2, "Shows", null); world.Add(L3, "Their shelf", null);
var users = World.Create<IUserManager>(world);
var libs = World.Create<ILibraryManager>(world);

static UserPolicy Policy(Action<UserPolicy> set = null)
{
    var u = new UserPolicy { AuthenticationProviderId = "x", PasswordResetProviderId = "x", EnableAllFolders = false,
                             EnabledFolders = Array.Empty<Guid>(), BlockedTags = Array.Empty<string>(),
                             AllowedTags = Array.Empty<string>() };
    set?.Invoke(u);
    return u;
}

const string PIN = "1234";
var MAIN = Guid.NewGuid(); var KID = Guid.NewGuid();
var OTHER = Guid.NewGuid(); var OTHERKID = Guid.NewGuid();
world.AddUser(MAIN, "home", Policy(u => u.EnableAllFolders = true));
world.AddUser(KID, "home_Kid", Policy(u => u.EnabledFolders = new[] { L1 }));
world.AddUser(OTHER, "away", Policy(u => u.EnabledFolders = new[] { L3 }));
world.AddUser(OTHERKID, "away_Pip", Policy(u => u.EnabledFolders = new[] { L3 }));

object Row(Guid profile, Guid main, string name, string pin = null)
{
    var row = Activator.CreateInstance(mapType);
    mapType.GetProperty("ProfileUserId").SetValue(row, profile);
    mapType.GetProperty("MasterUserId").SetValue(row, main);
    mapType.GetProperty("ProfileName").SetValue(row, name);
    if (pin != null) mapType.GetProperty("PinHash").SetValue(row, hasher.GetMethod("Hash").Invoke(null, new object[] { pin }));
    mappings.Add(row);
    return row;
}
Row(MAIN, MAIN, "home", PIN); Row(KID, MAIN, "Kid");
Row(OTHER, OTHER, "away", PIN); Row(OTHERKID, OTHER, "Pip");
object MapOf(Guid id) => mappings.Cast<object>().First(m => (Guid)mapType.GetProperty("ProfileUserId").GetValue(m) == id);
string ThemeOf(Guid id) => (string)mapType.GetProperty("Theme").GetValue(MapOf(id));
void SetTheme(Guid id, string v) => mapType.GetProperty("Theme").SetValue(MapOf(id), v);

// One picture of the administrator's own, beside the thirty starters.
{
    var a = Activator.CreateInstance(avatarItemType);
    avatarItemType.GetProperty("Id").SetValue(a, "a1b2c3d4e5f6");
    avatarItemType.GetProperty("DisplayName").SetValue(a, "Grandma");
    avatarItemType.GetProperty("Extension").SetValue(a, ".png");
    ((IList)cfgType.GetProperty("AvatarLibrary").GetValue(config)).Add(a);
}

// A caller: a user id, an API key, or nobody. The PIN goes in the header the clients send it in.
ControllerBase Controller(Guid? userId, string pin = PIN, bool apiKey = false)
{
    var ctor = ctrlType.GetConstructors().First();
    var args = ctor.GetParameters().Select(p =>
        p.ParameterType == typeof(IUserManager) ? users
        : p.ParameterType == typeof(ILibraryManager) ? libs
        : p.ParameterType.Name.StartsWith("ILogger") ? Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(ctrlType))
        : (object)null).ToArray();
    var c = (ControllerBase)ctor.Invoke(args);
    var claims = new List<Claim>();
    if (userId != null) claims.Add(new Claim("Jellyfin-UserId", userId.Value.ToString()));
    if (apiKey) { claims.Add(new Claim("Jellyfin-IsApiKey", "True")); claims.Add(new Claim(ClaimTypes.Role, "Administrator")); }
    var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
    http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7");
    if (pin != null) http.Request.Headers["X-Bonfire-Master-Pin"] = pin;
    c.ControllerContext = new ControllerContext { HttpContext = http };
    return c;
}

// Calls a route by method name. status is -404 when there is no such method, so a harness
// pointed at a tree without a route fails instead of throwing.
(int status, JsonElement? value) Call(string method, Guid? caller, object body = null, string pin = PIN, bool apiKey = false)
{
    var r = ctrlType.GetMethod(method);
    if (r == null) return (-404, null);
    var c = Controller(caller, pin, apiKey);
    var invokeArgs = r.GetParameters().Select(p =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(body ?? new { }), p.ParameterType, json)).ToArray();
    object result;
    try { result = r.Invoke(c, invokeArgs); }
    catch (TargetInvocationException ex) { return (-500, JsonSerializer.SerializeToElement(ex.InnerException?.GetType().Name + ": " + ex.InnerException?.Message)); }
    if (result is Task task)
    {
        task.GetAwaiter().GetResult();
        result = task.GetType().GetProperty("Result")?.GetValue(task);
    }
    var inner = result?.GetType().GetProperty("Result")?.GetValue(result) ?? result;
    return inner switch
    {
        ObjectResult o => (o.StatusCode ?? 200, o.Value == null ? null : JsonSerializer.SerializeToElement(o.Value, json)),
        StatusCodeResult s => (s.StatusCode, null),
        _ => (-1, null),
    };
}
string Say((int status, JsonElement? value) r) => r.status + " " + (r.value?.ToString() ?? "");

// The two things that are this machine's rather than the plugin's.
JsonNode Portable(JsonNode part)
{
    if (part is JsonObject o && o.ContainsKey("Zones"))
    {
        o["Zones"] = (o["Zones"] as JsonArray)?.Count > 0 ? "[zones]" : "[none]";
        o["Server"] = "[this machine]";
        if ((string)o["Household"] == "" && (string)o["Default"] == "") o["Effective"] = "[this machine]";
    }
    return part;
}
string[] Parts = { "Libraries", "Ratings", "Avatars", "TimeZones" };

// ── The golden: what the old routes answered ─────────────────────────────────
var goldenPath = Path.Combine(RepoRoot(), "tests", "golden", "formoptions.json");

JsonObject Captured(Guid caller)
{
    var o = new JsonObject();
    var map = new[] { ("Libraries", "GetLibraries"), ("Ratings", "GetRatings"), ("Avatars", "GetAvatarLibrary"), ("TimeZones", "GetTimeZones") };
    foreach (var (part, method) in map)
    {
        var r = Call(method, caller);
        if (r.status != 200) throw new Exception(method + " answered " + r.status + " — capture on a tree that still has the old routes");
        o[part] = Portable(JsonNode.Parse(r.value.Value.GetRawText()));
    }
    return o;
}

if (args.Length == 2 && args[0] == "--capture")
{
    var golden = new JsonObject { ["main"] = Captured(MAIN), ["profile"] = Captured(KID), ["otherHousehold"] = Captured(OTHER) };
    File.WriteAllText(args[1], golden.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    Console.WriteLine("captured to " + args[1]);
    return 0;
}

// ── The routes ───────────────────────────────────────────────────────────────
Section("Which routes exist");
{
    var routes = ctrlType.GetMethods()
        .SelectMany(m => m.GetCustomAttributes<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>(false)
            .Select(a => a.HttpMethods.First() + " " + a.Template))
        .ToList();
    Ok("GET form-options is a route", routes.Contains("GET form-options"));
    foreach (var gone in new[] { "GET libraries", "GET ratings", "GET avatars", "GET timezones", "POST theme" })
        Ok(gone + " is gone", !routes.Contains(gone));
    Ok("GET avatars/{id}, the pictures themselves, stays", routes.Contains("GET avatars/{id}"));
}

// ── The same answers ─────────────────────────────────────────────────────────
Section("form-options says what the four routes said");
{
    var golden = JsonNode.Parse(File.ReadAllText(goldenPath)).AsObject();
    foreach (var (who, caller) in new[] { ("main", MAIN), ("profile", KID), ("otherHousehold", OTHER) })
    {
        var r = Call("GetFormOptions", caller);
        Ok(who + ": answers 200", r.status == 200, Say(r));
        if (r.status != 200) continue;
        var got = JsonNode.Parse(r.value.Value.GetRawText()).AsObject();
        Ok(who + ": has the four parts and nothing else",
           got.Select(p => p.Key).OrderBy(k => k).SequenceEqual(Parts.OrderBy(k => k)),
           string.Join(",", got.Select(p => p.Key)));
        foreach (var part in Parts)
        {
            var was = golden[who][part];
            var now = got[part] == null ? null : Portable(JsonNode.Parse(got[part].ToJsonString()));
            Ok(who + ": " + part + " is what its route answered", JsonNode.DeepEquals(was, now),
               "was " + was?.ToJsonString() + "  now " + now?.ToJsonString());
        }
    }
}

// ── Whose libraries ──────────────────────────────────────────────────────────
Section("A caller sees their own libraries and nobody else's");
{
    List<string> Ids(Guid caller)
    {
        var r = Call("GetFormOptions", caller);
        return r.value?.GetProperty("Libraries").EnumerateArray().Select(l => l.GetProperty("Id").GetString()).ToList() ?? new List<string>();
    }
    string N(Guid g) => g.ToString("N");
    var main = Ids(MAIN); var kid = Ids(KID); var other = Ids(OTHER); var otherKid = Ids(OTHERKID);
    Ok("a main account on every library is offered every library", main.Count == 3 && main.Contains(N(L1)) && main.Contains(N(L2)) && main.Contains(N(L3)), string.Join(",", main));
    Ok("a profile is offered only what it has", kid.SequenceEqual(new[] { N(L1) }), string.Join(",", kid));
    Ok("the other household is offered its own", other.SequenceEqual(new[] { N(L3) }), string.Join(",", other));
    Ok("and none of the first household's", !other.Contains(N(L1)) && !other.Contains(N(L2)) && !otherKid.Contains(N(L1)) && !otherKid.Contains(N(L2)));
    Ok("the first household's profile is not offered the other's", !kid.Contains(N(L3)));
}

Section("Who may ask");
{
    var anon = Call("GetFormOptions", null);
    Ok("nobody is refused", anon.status == 401, Say(anon));
    var stranger = Call("GetFormOptions", Guid.NewGuid());
    Ok("a user the server does not have is not found", stranger.status == 404, Say(stranger));
    var key = Call("GetFormOptions", null, apiKey: true);
    Ok("an API key is answered", key.status == 200, Say(key));
    if (key.status == 200)
    {
        var v = key.value.Value;
        // The old /libraries, /ratings and /avatars were 401 for a key and /timezones was not.
        Ok("with the time zones", v.GetProperty("TimeZones").ValueKind == JsonValueKind.Object);
        // Null parts may be left out of the body or written as null; either says "not for a key".
        bool Absent(string p) => !v.TryGetProperty(p, out var x) || x.ValueKind == JsonValueKind.Null;
        Ok("and none of the three it was refused", Absent("Libraries") && Absent("Ratings") && Absent("Avatars"));
    }
}

// ── Theme through /update ────────────────────────────────────────────────────
Section("/update sets a theme under the rules /theme had");
{
    SetCfg("AllowProfileThemes", true);
    SetCfg("HiddenThemes", new List<string> { "glass" });
    SetTheme(KID, "crimson");
    SetTheme(MAIN, null);

    (int status, JsonElement? value) Update(Guid caller, object body, string pin = PIN) => Call("UpdateProfile", caller, body, pin);

    var r = Update(MAIN, new { ProfileId = KID, ProfileName = "Kid", Theme = "harbor" });
    Ok("the main account sets a profile's theme", r.status == 200 && ThemeOf(KID) == "harbor", Say(r) + " stored=" + ThemeOf(KID));
    Ok("and is told what the profile now shows and what is stored",
       r.value?.GetProperty("Theme").GetString() == "harbor" && r.value?.GetProperty("Stored").GetString() == "harbor", Say(r));

    r = Update(MAIN, new { ProfileId = KID, ProfileName = "Kid" });
    Ok("a save that leaves the theme out keeps it", r.status == 200 && ThemeOf(KID) == "harbor", Say(r) + " stored=" + ThemeOf(KID));

    r = Update(MAIN, new { ProfileId = KID, ProfileName = "Kid", Theme = "" });
    Ok("an empty theme follows the server's default", r.status == 200 && r.value != null && ThemeOf(KID) == null && (!r.value.Value.TryGetProperty("Stored", out var st) || st.ValueKind == JsonValueKind.Null), Say(r) + " stored=" + ThemeOf(KID));

    SetTheme(KID, "crimson");
    r = Update(MAIN, new { ProfileId = KID, ProfileName = "Kid", Theme = "harbor" }, pin: "9999");
    Ok("a wrong main PIN is refused", r.status != 200 && ThemeOf(KID) == "crimson", Say(r) + " stored=" + ThemeOf(KID));

    r = Update(KID, new { ProfileId = KID, ProfileName = "Kid", Theme = "harbor" });
    // 401 and not 403: this harness calls the controller directly. On the wire ErrorCodes.cs
    // turns a 401 with a message into 403, which is what the doc says for /update.
    Ok("a profile may not set its own", r.status == 401 && ThemeOf(KID) == "crimson", Say(r) + " stored=" + ThemeOf(KID));

    r = Update(MAIN, new { ProfileId = KID, ProfileName = "Kid", Theme = "neon" });
    Ok("a theme the server does not have is refused, not stored as the default", r.status == 400 && ThemeOf(KID) == "crimson", Say(r) + " stored=" + ThemeOf(KID));

    r = Update(MAIN, new { ProfileId = KID, ProfileName = "Kid", Theme = "glass" });
    Ok("so is one the administrator has hidden", r.status == 400 && ThemeOf(KID) == "crimson", Say(r) + " stored=" + ThemeOf(KID));

    r = Update(MAIN, new { ProfileId = OTHERKID, ProfileName = "Pip", Theme = "harbor" });
    Ok("another household's profile is not theirs to change", r.status == 401 && ThemeOf(OTHERKID) == null, Say(r) + " stored=" + ThemeOf(OTHERKID));

    r = Update(MAIN, new { ProfileId = MAIN, Theme = "harbor" });
    Ok("the main account sets its own", r.status == 200 && ThemeOf(MAIN) == "harbor", Say(r) + " stored=" + ThemeOf(MAIN));

    SetCfg("AllowProfileThemes", false);
    r = Update(MAIN, new { ProfileId = KID, ProfileName = "Kid", Theme = "harbor" });
    Ok("with choosing turned off a theme is refused 403", r.status == 403 && ThemeOf(KID) == "crimson", Say(r) + " stored=" + ThemeOf(KID));
    r = Update(MAIN, new { ProfileId = KID, ProfileName = "Kid", Theme = "neon" });
    Ok("whatever it is, as /theme answered", r.status == 403, Say(r));
    r = Update(MAIN, new { ProfileId = KID, ProfileName = "Kid" });
    Ok("a save that says nothing of themes still works", r.status == 200 && ThemeOf(KID) == "crimson", Say(r));
    SetCfg("AllowProfileThemes", true);
}

// ── A theme-only /update changes nothing else ────────────────────────────────
// Settings -> Theme and first-time setup send profileId, profileName, theme and the main PIN
// only. /update also runs the sub-profile policy sync, so "left out keeps what is stored"
// must hold for every field it can change, in the mapping and in the Jellyfin account.
Section("A theme-only /update leaves every other field as it was");
{
    SetCfg("AllowProfileThemes", true);
    SetCfg("HiddenThemes", new List<string> { "glass" });
    var kidHash = hasher.GetMethod("Hash").Invoke(null, new object[] { "5678" });
    var libA = Guid.Parse("11111111-1111-1111-1111-111111111111");

    // Every mapping field set to something that is not its default, each from JSON so the
    // nested types need no reflection of their own.
    void Fill(Guid id, string fields)
    {
        var row = MapOf(id);
        foreach (var p in JsonDocument.Parse(fields).RootElement.EnumerateObject())
        {
            var prop = mapType.GetProperty(p.Name) ?? throw new Exception("no mapping field " + p.Name);
            prop.SetValue(row, JsonSerializer.Deserialize(p.Value.GetRawText(), prop.PropertyType));
        }
    }
    const string Common = @"{
        ""AvatarColor"": ""#AA33CC"", ""TransparentAvatar"": true, ""IsHidden"": false, ""KeepAccountOnDelete"": true,
        ""LockoutMinutes"": 17, ""BypassPinOnLocalNetwork"": true, ""AskPinToOpen"": false,
        ""AllowHouseholdLanBypass"": true, ""AllowedDeviceIds"": [""dev-one"", ""dev-two""],
        ""OwnLimits"": { ""RemoteClientBitrateLimit"": 2000000, ""MaxActiveSessions"": 2, ""ForceRemoteSourceTranscoding"": true, ""SyncPlayAccessName"": ""None"", ""BlockUnratedItemNames"": [""Movie""] },
        ""LastAppliedLimits"": { ""RemoteClientBitrateLimit"": 1000000, ""MaxActiveSessions"": 3, ""ForceRemoteSourceTranscoding"": false, ""SyncPlayAccessName"": ""JoinGroups"", ""BlockUnratedItemNames"": [] },
        ""OwnRating"": { ""Score"": 7, ""SubScore"": null }, ""LastAppliedRating"": { ""Score"": 7, ""SubScore"": null },
        ""LastAppliedSchedules"": [""mon"", ""tue""], ""OwnSchedules"": [""wed""],
        ""Viewing"": { ""HoursEnabled"": true, ""Windows"": [{ ""Days"": 62, ""Start"": ""15:00"", ""End"": ""19:30"" }], ""DailyMinutes"": 90, ""WeekendMinutes"": 150, ""Language"": ""fr"" },
        ""TimeZone"": ""Pacific/Auckland"", ""ProfileImage"": ""/plugins/profiles/profile-image/x.png"",
        ""LibraryArtwork"": [{ ""LibraryId"": ""11111111-1111-1111-1111-111111111111"", ""Mode"": ""none"" }],
        ""HideMySubProfilesFromOthers"": true, ""HideOthersSubProfilesFromMe"": true,
        ""AskOnStartup"": false, ""SwitcherLocation"": ""sidebar"", ""Theme"": ""crimson"" }";
    Fill(KID, Common);
    mapType.GetProperty("PinHash").SetValue(MapOf(KID), kidHash);
    mapType.GetProperty("ProfileName").SetValue(MapOf(KID), "Kid");
    Fill(KID, @"{ ""BlockedTags"": [""scary""], ""AllowedTags"": [""family""],
        ""EnabledFolders"": [""11111111-1111-1111-1111-111111111111""], ""AllLibraries"": false,
        ""LastAppliedFolders"": [""11111111-1111-1111-1111-111111111111""] }");
    Fill(MAIN, Common);

    // The account behind the profile: its own rating, tags, one library and the limits Bonfire
    // writes at sign-in, all non-default. Blocked tags are the profile's plus the main account's
    // ("adult"), as the sign-in sync leaves them; /update merges them again. The main account's
    // policy is never written.
    world.Policies[KID] = Policy(u =>
    {
        u.EnableAllFolders = false; u.EnabledFolders = new[] { libA };
        u.MaxParentalRating = 7; u.MaxParentalSubRating = null;
        u.BlockedTags = new[] { "scary", "adult" }; u.AllowedTags = new[] { "family" };
        u.RemoteClientBitrateLimit = 2000000; u.MaxActiveSessions = 2;
        u.ForceRemoteSourceTranscoding = true; u.EnableContentDeletion = true;
        u.SyncPlayAccess = Jellyfin.Database.Implementations.Enums.SyncPlayUserAccessType.None;
        u.BlockUnratedItems = new[] { Jellyfin.Data.Enums.UnratedItem.Movie };
        u.AccessSchedules = new[] { new Jellyfin.Database.Implementations.Entities.AccessSchedule(Jellyfin.Database.Implementations.Enums.DynamicDayOfWeek.Monday, 8, 20, KID) };
    });
    world.Policies[MAIN] = Policy(u => { u.EnableAllFolders = true; u.MaxParentalRating = null; u.BlockedTags = new[] { "adult" }; });

    string Snap(Guid id)
    {
        var m = JsonNode.Parse(JsonSerializer.Serialize(MapOf(id), mapType, json)).AsObject();
        m.Remove("theme"); m.Remove("Theme");
        return m.ToJsonString();
    }
    string PolicySnap(Guid id) => JsonSerializer.Serialize(world.Policies[id], json);

    void Check(string label, Guid target, object body, string expectedTheme)
    {
        var before = (kid: Snap(KID), main: Snap(MAIN), kidPolicy: PolicySnap(KID), mainPolicy: PolicySnap(MAIN),
                      kidName: world.Users[KID].Username, other: Snap(OTHERKID));
        var r = Call("UpdateProfile", MAIN, body);
        Ok(label + ": answers 200", r.status == 200, Say(r));
        Ok(label + ": the theme is stored", ThemeOf(target) == expectedTheme, "stored=" + ThemeOf(target));
        Ok(label + ": the profile's stored fields are as they were", before.kid == Snap(KID), "was " + before.kid + "\n    now " + Snap(KID));
        Ok(label + ": the main account's stored fields are as they were", before.main == Snap(MAIN), "was " + before.main + "\n    now " + Snap(MAIN));
        Ok(label + ": the profile's Jellyfin account policy is as it was", before.kidPolicy == PolicySnap(KID), "was " + before.kidPolicy + "\n    now " + PolicySnap(KID));
        Ok(label + ": the main account's Jellyfin policy is as it was", before.mainPolicy == PolicySnap(MAIN), "was " + before.mainPolicy + "\n    now " + PolicySnap(MAIN));
        Ok(label + ": the account name is as it was", before.kidName == world.Users[KID].Username);
        Ok(label + ": another household's profile is as it was", before.other == Snap(OTHERKID));
    }

    // Sent the way the client sends it: id, name, theme, PIN, and nothing else.
    Check("sub-profile, a theme", KID, new { ProfileId = KID, ProfileName = "Kid", Theme = "harbor", MasterPin = PIN }, "harbor");
    Check("sub-profile, back to the server default", KID, new { ProfileId = KID, ProfileName = "Kid", Theme = "", MasterPin = PIN }, null);
    Check("the main account's own", MAIN, new { ProfileId = MAIN, ProfileName = "home", Theme = "harbor", MasterPin = PIN }, "harbor");
}

Console.WriteLine();
if (fails.Count > 0)
{
    Console.WriteLine(pass + " passed, " + fails.Count + " failed");
    foreach (var f in fails) Console.WriteLine("  FAILED: " + f);
    return 1;
}
Console.WriteLine(pass + " passed, 0 failed");
return 0;

// ── A modeled Jellyfin ──────────────────────────────────────────────────────

public class World
{
    public Dictionary<Guid, User> Users = new();
    public Dictionary<Guid, UserPolicy> Policies = new();
    public Dictionary<Guid, Guid> Masters = new();
    public List<Guid> Libraries = new();
    public Dictionary<Guid, string> Names = new();
    public Dictionary<Guid, string> FolderPaths = new();
    public Guid LastCreated;
    public bool FailPolicyWrite;
    public List<Guid> Deleted = new();

    public Guid MasterOf(Guid id) => Masters.TryGetValue(id, out var m) ? m : Guid.Empty;

    /// <summary>A library: its name, and its own folder when that matters.</summary>
    public void Add(Guid id, string name, string folderPath)
    {
        Libraries.Add(id);
        Names[id] = name;
        if (folderPath != null) FolderPaths[id] = folderPath;
    }

    public void AddUser(Guid id, string name, UserPolicy policy)
    {
        var u = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty("Username").SetValue(u, name);
        typeof(User).GetProperty("Id").SetValue(u, id);
        typeof(User).GetProperty("AuthenticationProviderId").SetValue(u, "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider");
        Users[id] = u;
        Policies[id] = policy;
    }

    public static UserPolicy Copy(UserPolicy p)
        => (UserPolicy)typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(p, null);

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
            case "CreateUserAsync":
                // Jellyfin's new account: every library by the flag, nothing blocked.
                var id = Guid.NewGuid();
                W.AddUser(id, (string)args[0], new UserPolicy { AuthenticationProviderId = "x", PasswordResetProviderId = "x", EnableAllFolders = true });
                W.LastCreated = id;
                return Task.FromResult(W.Users[id]);
            case "DeleteUserAsync":
                W.Deleted.Add((Guid)args[0]);
                W.Users.Remove((Guid)args[0]);
                return Task.CompletedTask;
            case "UpdatePolicyAsync":
                if (W.FailPolicyWrite) throw new InvalidOperationException("disk full");
                W.Policies[(Guid)args[0]] = World.Copy((UserPolicy)args[1]);
                return Task.CompletedTask;
            case "GetVirtualFolders":
                return W.Libraries.Select(l => new VirtualFolderInfo
                {
                    ItemId = l.ToString("N"),
                    Name = W.Names[l],
                    Locations = new[] { "/media/" + W.Names[l] },
                }).ToList();
            case "GetItemById" when !m.IsGenericMethod && args.Length > 0 && args[0] is Guid itemId:
                if (!W.FolderPaths.TryGetValue(itemId, out var path)) return null;
                var folder = (CollectionFolder)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(CollectionFolder));
                typeof(BaseItem).GetProperty("Path").SetValue(folder, path);
                return folder;
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
