using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Jellyfin.Profiles;
using Jellyfin.Profiles.Configuration;
using Jellyfin.Profiles.Controllers;
using Microsoft.Extensions.Logging.Abstractions;

// ─────────────────────────────────────────────────────────────────────────────
// A profile picture that is refused must not take the one the profile has (R-1108),
// and only this profile's own picture address is accepted (R-1109).
//
// SaveProfileImage and CopyLibraryAvatarToProfile answered a bare null for every failure
// (too large, bad base64, a type we don't store, custom avatars blocked, an unknown
// library avatar), and the edit stored that null as "no picture": a 3 MB upload deleted
// the one the profile had, answered 200, and said nothing. Any string beginning
// /plugins/profiles/image/ was stored as it came.
//
// Drives the real helpers the Create and Update routes call. Their answer is read the
// same way whatever shape it has, so pointed at the build with the bug this runs and
// fails rather than refusing to compile.
// ─────────────────────────────────────────────────────────────────────────────

int pass = 0;
var fails = new List<string>();
void Ok(string name, bool cond, string detail = null)
{
    if (cond) { pass++; Console.WriteLine("  PASS  " + name); }
    else { fails.Add(name); Console.WriteLine("  FAIL  " + name + (detail == null ? "" : " — " + detail)); }
}

const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance
                       | BindingFlags.NonPublic | BindingFlags.Public;

var tempDir = Path.Combine(Path.GetTempPath(), "bonfire-pictures-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
try
{
    var plugin = (Plugin)Activator.CreateInstance(typeof(Plugin), new StubPaths(tempDir), new StubXml());
    var config = plugin.Configuration;

#pragma warning disable SYSLIB0050
    var c = (ProfilesController)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(ProfilesController));
#pragma warning restore SYSLIB0050
    typeof(ProfilesBaseController).GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(c, NullLogger.Instance);

    var imageFolder = Path.Combine(tempDir, "plugins", "ProfilesManagement");
    var libraryFolder = Path.Combine(imageFolder, "avatars");
    Directory.CreateDirectory(libraryFolder);

    // (Url, Error) whatever the shape: a tuple on the fixed build, a bare string before it.
    (string Url, string Error) Run(string method, params object[] args)
    {
        var m = typeof(ProfilesBaseController).GetMethod(method, Any);
        if (m == null) return (null, null);
        // The build with the bug takes fewer arguments.
        var r = m.Invoke(c, args.Take(m.GetParameters().Length).ToArray());
        if (r is System.Runtime.CompilerServices.ITuple t) return ((string)t[0], (string)t[1]);
        return ((string)r, null);
    }

    var profile = Guid.NewGuid();
    var kidFile = Path.Combine(imageFolder, profile + ".png");
    var original = new byte[] { 1, 2, 3, 4 };
    File.WriteAllBytes(kidFile, original);
    var current = "/plugins/profiles/image/" + profile + "?v=100";
    bool Untouched() => File.Exists(kidFile) && File.ReadAllBytes(kidFile).SequenceEqual(original);
    string Png(int bytes) => "data:image/png;base64," + Convert.ToBase64String(new byte[bytes]);

    // What the Update route does with the answer: a refusal is a 400 and nothing is stored.
    bool Refused((string Url, string Error) r) => r.Error != null && r.Url == null;

    Console.WriteLine();
    Console.WriteLine("── A refused picture says why, and the one it has stays ────────");

    var big = Run("SaveProfileImage", profile, Png(3 * 1024 * 1024), "", current);
    Ok("a 3 MB picture is refused with a reason", Refused(big) && (big.Error ?? "").Contains("too large"), big.Error);
    Ok("and the picture it has is untouched", Untouched());

    var bad64 = Run("SaveProfileImage", profile, "data:image/png;base64,@@@not base64@@@", null, current);
    Ok("so is one that isn't base64", Refused(bad64) && (bad64.Error ?? "").Contains("could not be read") && Untouched(), bad64.Error);

    var svg = Run("SaveProfileImage", profile, "data:image/svg+xml;base64," + Convert.ToBase64String(new byte[] { 60, 115, 118, 103 }), null, current);
    Ok("so is a type we don't store", Refused(svg) && (svg.Error ?? "").Contains("supported image") && Untouched(), svg.Error);

    config.DisallowCustomAvatarUploads = true;
    var blocked = Run("SaveProfileImage", profile, Png(10), null, current);
    Ok("a custom picture is refused while the server allows only library avatars",
       Refused(blocked) && (blocked.Error ?? "").Contains("avatar library") && Untouched(), blocked.Error);
    config.DisallowCustomAvatarUploads = false;

    var unknown = Run("CopyLibraryAvatarToProfile", profile, "no-such-avatar");
    Ok("an avatar that isn't in the library is refused", Refused(unknown) && (unknown.Error ?? "").Contains("No such avatar") && Untouched(), unknown.Error);

    var check = typeof(ProfilesBaseController).GetMethod("CheckLibraryAvatar", Any);
    Ok("and can be refused before anything else in the request is done",
       check != null && (string)check.Invoke(c, new object[] { "no-such-avatar" }) != null);

    Console.WriteLine();
    Console.WriteLine("── Only this profile's own picture address is accepted ─────────");

    foreach (var bad in new[]
    {
        "/plugins/profiles/image/" + Guid.NewGuid(),
        "/plugins/profiles/image/" + profile + "\" onerror=\"x",
        "/plugins/profiles/image/../../../etc/passwd",
        "/plugins/profiles/image/" + profile + "?v=1&x=2",
        "/plugins/profiles/image/" + profile + "?v=abc",
    })
    {
        var r = Run("SaveProfileImage", profile, bad, null, current);
        Ok("a made-up picture address is refused: " + bad, Refused(r) && Untouched(), r.Url ?? r.Error);
    }

    var same = Run("SaveProfileImage", profile, current, null, current);
    Ok("what it already has is accepted", same.Error == null && same.Url == current, same.Error);
    var buster = Run("SaveProfileImage", profile, "/plugins/profiles/image/" + profile + "?v=200", null, current);
    Ok("and its own address with a new cache-buster", buster.Error == null && buster.Url == "/plugins/profiles/image/" + profile + "?v=200", buster.Error);

    Console.WriteLine();
    Console.WriteLine("── What still works ────────────────────────────────────────────");

    var good = Run("SaveProfileImage", profile, Png(10), null, current);
    Ok("a good picture is still saved", good.Error == null && good.Url != null && good.Url.StartsWith("/plugins/profiles/image/" + profile), good.Error);

    File.WriteAllBytes(Path.Combine(libraryFolder, "fox.png"), new byte[] { 9, 9, 9 });
    config.AvatarLibrary.Add(new AvatarLibraryItem { Id = "fox", DisplayName = "Fox", Extension = ".png" });
    foreach (var f in Directory.GetFiles(imageFolder, profile + "*")) File.Delete(f);
    var fox = Run("CopyLibraryAvatarToProfile", profile, "fox");
    Ok("a library avatar is copied", fox.Error == null && fox.Url != null && File.Exists(kidFile) && File.ReadAllBytes(kidFile).SequenceEqual(new byte[] { 9, 9, 9 }), fox.Error);

    File.Delete(Path.Combine(libraryFolder, "fox.png"));
    var gone = Run("CopyLibraryAvatarToProfile", profile, "fox");
    Ok("one whose file has gone is refused and the copy it had stays",
       Refused(gone) && File.Exists(kidFile) && File.ReadAllBytes(kidFile).SequenceEqual(new byte[] { 9, 9, 9 }), gone.Error);

    var removed = Run("SaveProfileImage", profile, "", null, null);
    Ok("an empty picture is still a deliberate removal", removed.Error == null && removed.Url == null && !File.Exists(kidFile), removed.Error);
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
