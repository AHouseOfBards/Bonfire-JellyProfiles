// configbackup: a configuration file that won't read must not cost every household (R-1127).
//
// Jellyfin's BasePlugin.LoadConfiguration, on any exception reading the file, creates a
// default configuration and SAVES it over the file. Every household, PIN, link and theme
// went, with no copy kept. A crash mid-write is enough, and so is an enum value the
// running Jellyfin doesn't know.
//
// Driven through the real Plugin and Jellyfin's real BasePlugin<T>, with a serializer that
// reads and writes XML the way Jellyfin's does, so the load path that does the damage is
// Jellyfin's own code, not a model of it.
//
//   dotnet run --project tests/cs/configbackup -c Release

using System;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Serialization;
using Jellyfin.Profiles;
using Jellyfin.Profiles.Configuration;

int passed = 0, failed = 0;
void Ok(string name, bool cond, string detail = null)
{
    if (cond) { passed++; Console.WriteLine("  PASS  " + name); }
    else { failed++; Console.WriteLine("  FAIL  " + name + (detail == null ? "" : "  — " + detail)); }
}

string NewDir()
{
    var d = Path.Combine(Path.GetTempPath(), "bonfire-configbackup-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(d);
    return d;
}

var HOUSE = Guid.NewGuid();
var KID = Guid.NewGuid();

Console.WriteLine();
Console.WriteLine("── The file breaks ─────────────────────────────────────────────");

var dir = NewDir();
var first = new Plugin(new StubPaths(dir), new RealXml());
var cfg = first.Configuration;
cfg.Mappings.Add(new ProfileMapping { ProfileUserId = HOUSE, MasterUserId = HOUSE, ProfileName = "Bard" });
cfg.Mappings.Add(new ProfileMapping { ProfileUserId = KID, MasterUserId = HOUSE, ProfileName = "kids" });
first.SaveConfiguration();
var path = first.ConfigurationFilePath;
Ok("the household is saved", File.ReadAllText(path).Contains(KID.ToString()));

// A write cut short.
var broken = File.ReadAllText(path);
broken = broken.Substring(0, broken.Length / 2);
File.WriteAllText(path, broken);

var second = new Plugin(new StubPaths(dir), new RealXml());
var loaded = second.Configuration;
Ok("on the next start the households are back", loaded.Mappings.Any(m => m.ProfileUserId == KID) && loaded.Mappings.Count == 2,
   loaded.Mappings.Count + " mappings");
Ok("and written back to the file, which reads again",
   new RealXml().DeserializeFromFile(typeof(PluginConfiguration), path) is PluginConfiguration again && again.Mappings.Count == 2);
var kept = Directory.GetFiles(dir, "*.unreadable-*");
Ok("the unreadable file is kept beside it, as it was", kept.Length == 1 && File.ReadAllText(kept[0]) == broken, kept.Length + " kept");

Console.WriteLine();
Console.WriteLine("── When it must do nothing ─────────────────────────────────────");

var fresh = NewDir();
var newInstall = new Plugin(new StubPaths(fresh), new RealXml());
Ok("a new install starts empty", newInstall.Configuration.Mappings.Count == 0);
Ok("and keeps nothing as unreadable", Directory.GetFiles(fresh, "*.unreadable-*").Length == 0);

var cleared = NewDir();
var p = new Plugin(new StubPaths(cleared), new RealXml());
p.Configuration.Mappings.Add(new ProfileMapping { ProfileUserId = HOUSE, MasterUserId = HOUSE, ProfileName = "Bard" });
p.SaveConfiguration();
p.Configuration.Mappings.Clear();
p.SaveConfiguration();
var reread = new Plugin(new StubPaths(cleared), new RealXml());
Ok("a household that really deleted everything stays deleted", reread.Configuration.Mappings.Count == 0,
   reread.Configuration.Mappings.Count + " mappings");

var nothing = NewDir();
var lone = new Plugin(new StubPaths(nothing), new RealXml());
_ = lone.Configuration;
File.WriteAllText(lone.ConfigurationFilePath, "<PluginConfiguration><Mappings><Prof");
File.Delete(lone.ConfigurationFilePath + ".lastgood");
var orphan = new Plugin(new StubPaths(nothing), new RealXml());
Ok("with no good copy to go back to it starts empty", orphan.Configuration.Mappings.Count == 0);
Ok("but the unreadable file is still kept", Directory.GetFiles(nothing, "*.unreadable-*").Length == 1);

Console.WriteLine();
Console.WriteLine("── The main PIN is required on a new install only (P2-703) ─────");

var brandNew = NewDir();
var newServer = new Plugin(new StubPaths(brandNew), new RealXml());
Ok("a new install requires a main PIN", newServer.Configuration.RequireMainPin);

// RequireMainPin arrived in 1.7.2, so a server that last saved under 1.7.1 has no element.
var older = NewDir();
var writer = new Plugin(new StubPaths(older), new RealXml());
_ = writer.Configuration;
var olderPath = writer.ConfigurationFilePath;
writer.Configuration.RequireMainPin = false;
writer.SaveConfiguration();
File.Delete(olderPath + ".lastgood");
var text = File.ReadAllText(olderPath);
var withoutElement = System.Text.RegularExpressions.Regex.Replace(text, @"\s*<RequireMainPin>[^<]*</RequireMainPin>", "");
Ok("the older file really has no element", withoutElement != text && !withoutElement.Contains("RequireMainPin"));
File.WriteAllText(olderPath, withoutElement);
var upgraded = new Plugin(new StubPaths(older), new RealXml());
Ok("a server upgrading from a file without the setting stays off", !upgraded.Configuration.RequireMainPin);

var turnedOff = NewDir();
var admin = new Plugin(new StubPaths(turnedOff), new RealXml());
admin.Configuration.RequireMainPin = false;
admin.SaveConfiguration();
var restarted = new Plugin(new StubPaths(turnedOff), new RealXml());
Ok("an administrator who turned it off is not overruled at the next start", !restarted.Configuration.RequireMainPin);

// Only the last good copy left: the configuration is being recovered, not created.
var recovering = NewDir();
var saver = new Plugin(new StubPaths(recovering), new RealXml());
saver.Configuration.RequireMainPin = false;
saver.SaveConfiguration();
Ok("the good copy exists", File.Exists(saver.ConfigurationFilePath + ".lastgood"));
File.Delete(saver.ConfigurationFilePath);
var recovered = new Plugin(new StubPaths(recovering), new RealXml());
Ok("a missing file with a good copy beside it is not a new install", !recovered.Configuration.RequireMainPin);

Console.WriteLine();
Console.WriteLine("── A different version starts: the file is copied first (R-1319) ──");

// Rolling the plugin back loads a file written by a newer one, and the first save writes it
// back without every setting the older version doesn't know. Re-upgrading then finds them
// gone. Nothing kept a copy. The version that last ran is written down beside the file, and
// when the running version differs, the file is copied to <file>.<old version>.bak before
// anything in this start can save. The newest few are kept.
//
// The version comes from an internal constructor so this can play several; against a build
// without it, the public one is used and the version never changes, which is the bug.
Plugin AtVersion(string folder, string version)
{
    var ctor = typeof(Plugin).GetConstructor(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
        new[] { typeof(MediaBrowser.Common.Configuration.IApplicationPaths), typeof(MediaBrowser.Model.Serialization.IXmlSerializer), typeof(string) }, null);
    return ctor != null
        ? (Plugin)ctor.Invoke(new object[] { new StubPaths(folder), new RealXml(), version })
        : new Plugin(new StubPaths(folder), new RealXml());
}
string[] Backups(string folder) => Directory.GetFiles(folder, "*.bak").Select(Path.GetFileName).OrderBy(n => n).ToArray();

var vdir = NewDir();
var v18 = AtVersion(vdir, "1.8.0");
v18.Configuration.Mappings.Add(new ProfileMapping { ProfileUserId = HOUSE, MasterUserId = HOUSE, ProfileName = "Bard" });
v18.Configuration.Mappings.Add(new ProfileMapping { ProfileUserId = KID, MasterUserId = HOUSE, ProfileName = "kids" });
v18.Configuration.MaxProfilesPerUser = 9;   // a setting the older version won't have
v18.SaveConfiguration();
var vpath = v18.ConfigurationFilePath;
var as18 = File.ReadAllText(vpath);
Ok("a first start has nothing to copy", Backups(vdir).Length == 0, string.Join(",", Backups(vdir)));

var again18 = AtVersion(vdir, "1.8.0");
Ok("the same version starting again copies nothing", Backups(vdir).Length == 0, string.Join(",", Backups(vdir)));

// Rolled back to 1.7.9, which saves a smaller file.
var v17 = AtVersion(vdir, "1.7.9");
var bak18 = Backups(vdir).Where(n => n.Contains("1.8.0")).ToArray();
Ok("rolling back copies the file the newer version wrote, named for it", bak18.Length == 1, string.Join(",", Backups(vdir)));
Ok("as it was, before anything could save", bak18.Length == 1 && File.ReadAllText(Path.Combine(vdir, bak18[0])) == as18);
v17.Configuration.Mappings.Clear();
v17.Configuration.MaxProfilesPerUser = 5;
v17.SaveConfiguration();
Ok("the older version's save does lose them from the live file", !File.ReadAllText(vpath).Contains(KID.ToString()));
Ok("the copy still has the households and the setting",
   bak18.Length == 1 && File.ReadAllText(Path.Combine(vdir, bak18[0])).Contains(KID.ToString())
   && new RealXml().DeserializeFromFile(typeof(PluginConfiguration), Path.Combine(vdir, bak18[0])) is PluginConfiguration old && old.MaxProfilesPerUser == 9);

// Upgraded again: the copy of the 1.7.9 file is added, the 1.8.0 one stays.
AtVersion(vdir, "1.8.0");
Ok("upgrading again keeps the first copy and adds the other",
   Backups(vdir).Any(n => n.Contains("1.8.0")) && Backups(vdir).Any(n => n.Contains("1.7.9")), string.Join(",", Backups(vdir)));

// Bounded.
foreach (var ver in new[] { "1.9.0", "1.9.1", "1.9.2", "1.9.3", "1.9.4" }) AtVersion(vdir, ver);
Ok("only the newest few copies are kept", Backups(vdir).Length is > 0 and <= 3, Backups(vdir).Length + ": " + string.Join(",", Backups(vdir)));
Ok("and the newest is the one just made", Backups(vdir).Any(n => n.Contains("1.9.3")), string.Join(",", Backups(vdir)));

// A file from a build that never wrote the version down.
var legacy = NewDir();
var seed = AtVersion(legacy, "1.0.0");
seed.Configuration.Mappings.Add(new ProfileMapping { ProfileUserId = HOUSE, MasterUserId = HOUSE, ProfileName = "Bard" });
seed.SaveConfiguration();
File.Delete(seed.ConfigurationFilePath + ".version");
AtVersion(legacy, "2.0.0");
Ok("a file from before the version was recorded is copied too", Backups(legacy).Length == 1, string.Join(",", Backups(legacy)));

foreach (var d in new[] { dir, fresh, cleared, nothing, vdir, legacy }) { try { Directory.Delete(d, true); } catch { } }

Console.WriteLine();
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

// Reads and writes as Jellyfin's MyXmlSerializer does: System.Xml.Serialization, and any
// failure to read surfaces as an exception.
sealed class RealXml : MediaBrowser.Model.Serialization.IXmlSerializer
{
    public void SerializeToStream(object obj, Stream stream) => new XmlSerializer(obj.GetType()).Serialize(stream, obj);
    public void SerializeToFile(object obj, string file)
    {
        using var w = XmlWriter.Create(file, new XmlWriterSettings { Indent = true });
        new XmlSerializer(obj.GetType()).Serialize(w, obj);
    }
    public object DeserializeFromFile(Type type, string file)
    {
        using var s = File.OpenRead(file);
        return DeserializeFromStream(type, s);
    }
    public object DeserializeFromStream(Type type, Stream stream) => new XmlSerializer(type).Deserialize(stream);
    public object DeserializeFromBytes(Type type, byte[] buffer) => DeserializeFromStream(type, new MemoryStream(buffer));
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
