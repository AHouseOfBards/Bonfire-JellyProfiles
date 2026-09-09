using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
