using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Xml.Serialization;
using Jellyfin.Profiles;
using Jellyfin.Profiles.Configuration;
using Jellyfin.Profiles.Controllers;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

// ─────────────────────────────────────────────────────────────────────────────
// The starter avatars: thirty built-in pictures every server offers until the
// administrator switches them off (asked for in issue #32).
//
// The avatar library starts empty, and on a television an empty library is the
// whole picker — there is no file browser to upload from. So the set has to be
// there on a fresh install with nothing configured, has to be really inside the
// plugin (the release zip holds one DLL and nothing else), and has to go away
// completely when switched off: not listed, not served, not copyable.
//
// Everything new is reached by reflection, so pointed at a build without the
// feature this runs and fails rather than refusing to compile.
// ─────────────────────────────────────────────────────────────────────────────

static string RepoRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d != null && !File.Exists(Path.Combine(d.FullName, "Jellyfin.Profiles.csproj")))
        d = d.Parent;
    if (d == null) throw new InvalidOperationException("Could not find the repository root.");
    return d.FullName;
}

int pass = 0;
var fails = new List<string>();
void Ok(string name, bool cond)
{
    if (cond) { pass++; Console.WriteLine("  PASS  " + name); }
    else { fails.Add(name); Console.WriteLine("  FAIL  " + name); }
}

const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance
                       | BindingFlags.NonPublic | BindingFlags.Public;

var asm = typeof(PluginConfiguration).Assembly;
var starterType = asm.GetType("Jellyfin.Profiles.Configuration.StarterAvatars");
var folder = Path.Combine(RepoRoot(), "Web", "starter-avatars");

Console.WriteLine();
Console.WriteLine("── The set is inside the plugin ────────────────────────────────");

Ok("there is a starter set", starterType != null);

var catalogue = new List<(string Slug, string DisplayName)>();
if (starterType != null)
{
    var all = starterType.GetField("All", Any)?.GetValue(null) as System.Collections.IEnumerable;
    foreach (var entry in all ?? Array.Empty<object>())
    {
        var t = entry.GetType();
        catalogue.Add(((string)t.GetField("Item1").GetValue(entry), (string)t.GetField("Item2").GetValue(entry)));
    }
}

Ok("thirty pictures (" + catalogue.Count + ")", catalogue.Count == 30);
Ok("no slug twice", catalogue.Select(c => c.Slug).Distinct(StringComparer.OrdinalIgnoreCase).Count() == catalogue.Count);
Ok("no name twice, since the name is the tooltip and the screen-reader label",
   catalogue.Select(c => c.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == catalogue.Count);
Ok("slugs are plain lower-case words, safe in a URL and a file name",
   catalogue.Count > 0 && catalogue.All(c => System.Text.RegularExpressions.Regex.IsMatch(c.Slug, "^[a-z]+(-[a-z]+)*$")));

// The release zip holds Jellyfin.Profiles.dll and nothing else, so a picture that is a
// file in the repository but not a resource in the assembly does not exist for users.
var resources = new HashSet<string>(asm.GetManifestResourceNames());
var notEmbedded = new List<string>();
var notWebp = new List<string>();
var wrongSize = new List<string>();
foreach (var (slug, _) in catalogue)
{
    using var s = asm.GetManifestResourceStream("Jellyfin.Profiles.StarterAvatars." + slug + ".webp");
    if (s == null) { notEmbedded.Add(slug); continue; }
    var head = new byte[30];
    var read = s.Read(head, 0, head.Length);
    bool riff = read == 30 && head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F'
             && head[8] == 'W' && head[9] == 'E' && head[10] == 'B' && head[11] == 'P';
    if (!riff) { notWebp.Add(slug); continue; }
    // VP8X carries the canvas size: 24-bit little-endian, minus one.
    if (head[12] == 'V' && head[13] == 'P' && head[14] == '8' && head[15] == 'X')
    {
        int w = 1 + (head[24] | head[25] << 8 | head[26] << 16);
        int h = 1 + (head[27] | head[28] << 8 | head[29] << 16);
        if (w != 256 || h != 256) wrongSize.Add(slug + " " + w + "x" + h);
    }
    else wrongSize.Add(slug + " (no VP8X header — has the alpha been lost?)");
}
Ok("every picture is embedded in the assembly", catalogue.Count > 0 && notEmbedded.Count == 0);
foreach (var n in notEmbedded) Console.WriteLine("        not embedded: " + n);
Ok("and is a WebP", catalogue.Count > 0 && notWebp.Count == 0);
foreach (var n in notWebp) Console.WriteLine("        not a WebP: " + n);
Ok("256 square, with an alpha channel", catalogue.Count > 0 && wrongSize.Count == 0);
foreach (var n in wrongSize) Console.WriteLine("        " + n);

var onDisk = Directory.Exists(folder)
    ? Directory.GetFiles(folder, "*.webp").Select(f => Path.GetFileNameWithoutExtension(f)).ToList()
    : new List<string>();
var orphans = onDisk.Where(f => !catalogue.Any(c => c.Slug == f)).ToList();
Ok("no picture in Web/starter-avatars that the catalogue does not list", onDisk.Count > 0 && orphans.Count == 0);
foreach (var o in orphans) Console.WriteLine("        unlisted: " + o);

// MIT asks for the notice to travel with every copy. The copy users get is the DLL.
string licence = null;
using (var ls = asm.GetManifestResourceStream("Jellyfin.Profiles.StarterAvatars.LICENSE.txt"))
    if (ls != null) licence = new StreamReader(ls).ReadToEnd();
Ok("the licence travels inside the assembly with them", licence != null);
Ok("and it is Microsoft's MIT notice, in full",
   licence != null && licence.Contains("MIT License") && licence.Contains("Copyright (c) Microsoft Corporation")
   && licence.Contains("The above copyright notice and this permission notice shall be included"));

Console.WriteLine();
Console.WriteLine("── On by default, including for an existing server ─────────────");

var enabledProp = typeof(PluginConfiguration).GetProperty("EnableStarterAvatars");
Ok("there is a setting", enabledProp != null && enabledProp.PropertyType == typeof(bool));
if (enabledProp != null)
{
    Ok("a new configuration has it on", (bool)enabledProp.GetValue(new PluginConfiguration()));

    // What an upgrade looks like: Jellyfin reads the saved XML with XmlSerializer, and a
    // server that saved its settings before this existed has no element for it.
    var xs = new XmlSerializer(typeof(PluginConfiguration));
    var old = "<?xml version=\"1.0\"?><PluginConfiguration xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">"
            + "<MaxProfilesPerUser>4</MaxProfilesPerUser><DisallowCustomAvatarUploads>true</DisallowCustomAvatarUploads>"
            + "</PluginConfiguration>";
    var upgraded = (PluginConfiguration)xs.Deserialize(new StringReader(old));
    Ok("an existing server's saved settings come up with it on", (bool)enabledProp.GetValue(upgraded));

    var off = old.Replace("</PluginConfiguration>", "<EnableStarterAvatars>false</EnableStarterAvatars></PluginConfiguration>");
    Ok("and switched off stays off across a restart",
       !(bool)enabledProp.GetValue((PluginConfiguration)xs.Deserialize(new StringReader(off))));
}

Console.WriteLine();
Console.WriteLine("── What users are offered, and what the switch takes away ──────");

var tempDir = Path.Combine(Path.GetTempPath(), "bonfire-starter-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
try
{
    var plugin = (Plugin)Activator.CreateInstance(typeof(Plugin), new StubPaths(tempDir), new StubXml());
    var config = plugin.Configuration;

    // One avatar the administrator uploaded, with its file where the library keeps them.
    var libraryFolder = Path.Combine(tempDir, "plugins", "ProfilesManagement", "avatars");
    Directory.CreateDirectory(libraryFolder);
    File.WriteAllBytes(Path.Combine(libraryFolder, "a1b2c3d4e5f6.png"), new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 1 });
    config.AvatarLibrary.Add(new AvatarLibraryItem { Id = "a1b2c3d4e5f6", DisplayName = "Grandma", Extension = ".png" });

    var adminId = Guid.NewGuid();
    var controller = MakeController(adminId, UserManagerStub.Create(adminId, new UserDto { Policy = new UserPolicy { IsAdministrator = true } }));

    List<JsonElement> Listed()
    {
        var res = controller.GetAvatarLibrary();
        var value = (res.Result as OkObjectResult)?.Value ?? res.Value;
        var json = JsonSerializer.SerializeToElement(value);
        return json.GetProperty("Avatars").EnumerateArray().ToList();
    }
    bool IsStarter(JsonElement a) => a.TryGetProperty("IsStarter", out var v) && v.ValueKind == JsonValueKind.True;

    var on = Listed();
    var starters = on.Where(IsStarter).ToList();
    Ok("a fresh server offers all thirty (" + starters.Count + ")", starters.Count == 30);
    Ok("after the administrator's own, which are listed first",
       on.Count == 31 && on[0].GetProperty("Id").GetString() == "a1b2c3d4e5f6" && !IsStarter(on[0]));
    Ok("each with an id that cannot collide with an uploaded one",
       starters.Count > 0 && starters.All(a => a.GetProperty("Id").GetString().StartsWith("starter-")));
    Ok("in the catalogue's order",
       starters.Select(a => a.GetProperty("Id").GetString()).SequenceEqual(catalogue.Select(c => "starter-" + c.Slug)));

    // Served: the picker draws every thumbnail as an <img>.
    var fox = controller.GetLibraryAvatar("starter-fox") as FileStreamResult;
    Ok("a starter picture is served", fox != null);
    if (fox != null)
    {
        Ok("as image/webp", fox.ContentType == "image/webp");
        using var ms = new MemoryStream();
        fox.FileStream.CopyTo(ms);
        Ok("byte for byte the file in the repository",
           ms.ToArray().SequenceEqual(File.ReadAllBytes(Path.Combine(folder, "fox.webp"))));
        Ok("with a cache validator, so the picker does not refetch thirty pictures every time",
           fox.EntityTag != null && !fox.EntityTag.IsWeak);
        var cc = controller.Response.Headers["Cache-Control"].ToString();
        Ok("cached the same way as every other avatar (" + cc + ")", cc.Contains("private") && cc.Contains("max-age=3600"));
    }
    Ok("the id is matched case-blind, as uploaded ones are",
       controller.GetLibraryAvatar("STARTER-Fox") is FileStreamResult);
    Ok("an id that is not in the catalogue is a 404",
       controller.GetLibraryAvatar("starter-velociraptor") is NotFoundResult);
    Ok("and so is one that tries to walk out of it",
       controller.GetLibraryAvatar("starter-../../LICENSE") is NotFoundResult
       && controller.GetLibraryAvatar("starter-") is NotFoundResult);

    // Chosen: on a server limited to the library, a choice is copied server-side by id.
    var copy = typeof(ProfilesBaseController).GetMethod("CopyLibraryAvatar", Any);
    var profileFolder = Path.Combine(tempDir, "profiles");
    Directory.CreateDirectory(profileFolder);
    var profileId = Guid.NewGuid().ToString();
    File.WriteAllBytes(Path.Combine(profileFolder, profileId + ".jpg"), new byte[] { 0xFF, 0xD8, 0xFF, 1 });
    var copied = copy != null && (bool)copy.Invoke(controller, new object[] { "starter-owl", profileFolder, profileId });
    Ok("choosing one copies it to the profile", copied);
    Ok("as the profile's own file, byte for byte",
       File.Exists(Path.Combine(profileFolder, profileId + ".webp"))
       && File.ReadAllBytes(Path.Combine(profileFolder, profileId + ".webp"))
              .SequenceEqual(File.ReadAllBytes(Path.Combine(folder, "owl.webp"))));
    Ok("replacing the picture it had, not sitting beside it",
       !File.Exists(Path.Combine(profileFolder, profileId + ".jpg")));
    var find = typeof(ProfilesBaseController).GetMethod("FindImageFile", Any);
    var found = find?.Invoke(null, new object[] { profileFolder, profileId, true });
    Ok("and the profile's thumbnail request finds it (there is one size, and it is thumbnail-sized)",
       found != null && found.ToString().Contains(".webp") && found.ToString().Contains("image/webp"));

    // Switched off, through the endpoint the settings page uses.
    var settingsType = asm.GetType("Jellyfin.Profiles.Models.AdminSettingsRequest", true);
    var settings = Activator.CreateInstance(settingsType);
    var settingsProp = settingsType.GetProperty("EnableStarterAvatars");
    Ok("the settings page can send the switch", settingsProp != null);
    settingsProp?.SetValue(settings, (bool?)false);
    var saved = typeof(ProfilesController).GetMethod("UpdateAdminSettings").Invoke(controller, new[] { settings });
    Ok("an administrator can switch them off (" + saved?.GetType().Name + ")", saved is OkResult || saved is OkObjectResult);
    Ok("which saves", enabledProp != null && !(bool)enabledProp.GetValue(config));

    var off2 = Listed();
    Ok("switched off, none are listed", off2.Count(IsStarter) == 0);
    Ok("and the administrator's own still are", off2.Count == 1);
    Ok("none are served", controller.GetLibraryAvatar("starter-fox") is NotFoundResult);
    // Whatever the profile has now — the owl, if the copy above worked — must survive.
    var current = Directory.GetFiles(profileFolder, profileId + ".*").FirstOrDefault();
    var before = current != null ? File.ReadAllBytes(current) : null;
    var copiedOff = copy != null && (bool)copy.Invoke(controller, new object[] { "starter-dog", profileFolder, profileId });
    Ok("none can be chosen by id", !copiedOff);
    Ok("and a refused choice leaves the profile's current picture alone",
       current != null && File.Exists(current) && File.ReadAllBytes(current).SequenceEqual(before));

    // The avatar tab's own endpoint carries it too.
    var avatarSettingsType = asm.GetType("Jellyfin.Profiles.Models.AvatarSettingsRequest", true);
    var avatarSettings = Activator.CreateInstance(avatarSettingsType);
    var avatarProp = avatarSettingsType.GetProperty("EnableStarterAvatars");
    avatarProp?.SetValue(avatarSettings, (bool?)true);
    typeof(ProfilesController).GetMethod("UpdateAvatarSettings").Invoke(controller, new[] { avatarSettings });
    Ok("and switch them back on from the avatar settings endpoint",
       avatarProp != null && enabledProp != null && (bool)enabledProp.GetValue(config) && Listed().Count(IsStarter) == 30);

    // Not an administrator: the switch is refused, the setting untouched.
    var userId = Guid.NewGuid();
    var userController = MakeController(userId, UserManagerStub.Create(userId, new UserDto { Policy = new UserPolicy { IsAdministrator = false } }));
    var denied = Activator.CreateInstance(settingsType);
    settingsProp?.SetValue(denied, (bool?)false);
    var refused = typeof(ProfilesController).GetMethod("UpdateAdminSettings").Invoke(userController, new[] { denied });
    Ok("someone who is not an administrator cannot switch them off",
       refused is UnauthorizedObjectResult || refused is UnauthorizedResult);
    Ok("and the setting is untouched", enabledProp != null && (bool)enabledProp.GetValue(config));
}
finally
{
    try { Directory.Delete(tempDir, true); } catch { }
}

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

// Built without its constructor: the real one wants Jellyfin's whole DI graph, and none
// of it is reached by these endpoints apart from the user manager, which is stubbed.
static ProfilesController MakeController(Guid userId, IUserManager users)
{
#pragma warning disable SYSLIB0050
    var c = (ProfilesController)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(ProfilesController));
#pragma warning restore SYSLIB0050
    typeof(ProfilesBaseController).GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(c, NullLogger.Instance);
    typeof(ProfilesBaseController).GetField("_userManager", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(c, users);
    var http = new DefaultHttpContext();
    http.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", userId.ToString()) }, "test"));
    c.ControllerContext = new ControllerContext { HttpContext = http };
    return c;
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

// Never reads, never writes: the configuration is built fresh and saves are no-ops.
sealed class StubXml : MediaBrowser.Model.Serialization.IXmlSerializer
{
    public void SerializeToStream(object obj, Stream stream) { }
    public void SerializeToFile(object obj, string file) { }
    public object DeserializeFromFile(Type type, string file) => throw new FileNotFoundException("stub serializer", file);
    public object DeserializeFromStream(Type type, Stream stream) => throw new NotSupportedException();
    public object DeserializeFromBytes(Type type, byte[] buffer) => throw new NotSupportedException();
}

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
            case "GetUserById":
                return (Guid)args[0] == _id
                    ? System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                        typeof(Jellyfin.Database.Implementations.Entities.User))
                    : null;
            case "GetUserDto":
                return _dto;
            default:
                throw new NotSupportedException("IUserManager." + targetMethod.Name + " is not modelled here");
        }
    }
}
