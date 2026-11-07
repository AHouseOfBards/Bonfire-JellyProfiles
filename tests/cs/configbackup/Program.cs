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

foreach (var d in new[] { dir, fresh, cleared, nothing }) { try { Directory.Delete(d, true); } catch { } }

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
