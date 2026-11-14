using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

// ─────────────────────────────────────────────────────────────────────────────
// Bonfire publishes who is limited to Campground.Shared (Limits.PublishLimited), so that
// a caller that gets no answer from Bonfire can still refuse them (limits_unavailable).
//
//  - A profile given a daily limit or viewing hours is in the published set.
//  - One whose limits are removed leaves it. A deleted profile leaves it.
//  - A main account never appears.
//  - The set is written at start-up, once the configuration is loaded.
//  - The emergency disable (Bonfire off) publishes the empty set, as the index reads empty.
//
// Against the build before the publish was added, nothing writes the set, so every
// assertion about its contents fails.
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
void Section(string s) { Console.WriteLine(); Console.WriteLine("── " + s + " " + new string('─', Math.Max(4, 66 - s.Length))); }
int Finish()
{
    Console.WriteLine();
    if (fails.Count > 0)
    {
        foreach (var f in fails) Console.WriteLine("   - " + f);
        Console.WriteLine(pass + " passed, " + fails.Count + " failed");
        return 1;
    }
    Console.WriteLine(pass + " passed, 0 failed");
    return 0;
}

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
const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

var pluginType = asm.GetType("Jellyfin.Profiles.Plugin", true);
var cfgType = asm.GetType("Jellyfin.Profiles.Configuration.PluginConfiguration", true);
var mapType = asm.GetType("Jellyfin.Profiles.Configuration.ProfileMapping", true);
var rulesType = asm.GetType("Jellyfin.Profiles.Configuration.ViewingRules", true);
var tracker = asm.GetType("Jellyfin.Profiles.Auth.ViewingTracker", true);
var provider = asm.GetType("Jellyfin.Profiles.Auth.LimitsProvider", true);
var limitsType = asm.GetType("Campground.Shared.Contracts.Limits", true);

object Static(Type t, string name, params object[] args)
{
    var m = t.GetMethods(Any).Where(x => x.Name == name && x.GetParameters().Length == args.Length)
        .FirstOrDefault(x => x.GetParameters().Select((p, i) => args[i] == null || p.ParameterType.IsInstanceOfType(args[i])).All(ok => ok));
    if (m == null) throw new MissingMethodException(t.Name + "." + name);
    try { return m.Invoke(null, args); }
    catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
}

// ── The world ────────────────────────────────────────────────────────────────
var tempDir = Path.Combine(Path.GetTempPath(), "bonfire-limitspublish-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
var plugin = Activator.CreateInstance(pluginType, new StubPaths(tempDir), new StubXml());
var config = pluginType.GetProperty("Configuration").GetValue(plugin);
cfgType.GetProperty("BonfireOn").SetValue(config, true);
var mappings = (IList)cfgType.GetProperty("Mappings").GetValue(config);

var MAIN = Guid.NewGuid();
var KID = Guid.NewGuid();
var TEEN = Guid.NewGuid();
var FREE = Guid.NewGuid();
object Rules(int dailyMinutes)
{
    var r = Activator.CreateInstance(rulesType);
    rulesType.GetProperty("DailyMinutes").SetValue(r, dailyMinutes);
    return r;
}
object Map(Guid profile, Guid master, string name, object rules = null)
{
    var m = Activator.CreateInstance(mapType);
    mapType.GetProperty("ProfileUserId").SetValue(m, profile);
    mapType.GetProperty("MasterUserId").SetValue(m, master);
    mapType.GetProperty("ProfileName").SetValue(m, name);
    if (rules != null) mapType.GetProperty("Viewing").SetValue(m, rules);
    mappings.Add(m);
    return m;
}
Ok("a daily limit is a limit", (bool)rulesType.GetProperty("IsLimited").GetValue(Rules(30)));

var mainRow = Map(MAIN, MAIN, "Parent", Rules(30));   // a main account with a rule must still not appear
var kidRow = Map(KID, MAIN, "Kid");
var teenRow = Map(TEEN, MAIN, "Teen", Rules(45));
Map(FREE, MAIN, "Free");

HashSet<Guid> Published() => ((IEnumerable<Guid>)Static(limitsType, "ReadLimited")).ToHashSet();
// What a save does: the rules are changed, then the tracker is told. Rebuilding is what
// publishes, and it is the same call the playback gate makes every 30 seconds.
void Saved() { Static(tracker, "Invalidate"); Static(tracker, "Index"); }

// ── Start-up ─────────────────────────────────────────────────────────────────
Section("Start-up");
Static(tracker, "Reset");
Static(provider, "Register");
var set = Published();
Ok("after start-up the profile with a limit is published", set.Contains(TEEN));
Ok("the main account is not published", !set.Contains(MAIN));
Ok("a profile with no rule is not published", !set.Contains(FREE) && !set.Contains(KID));
Ok("exactly the limited profiles are published", set.Count == 1, "got " + set.Count);

// ── Saves ────────────────────────────────────────────────────────────────────
Section("A limit given");
mapType.GetProperty("Viewing").SetValue(kidRow, Rules(60));
Saved();
set = Published();
Ok("a profile given a daily limit is published", set.Contains(KID) && set.Contains(TEEN));
Ok("the main account still is not", !set.Contains(MAIN));

Section("A limit removed");
mapType.GetProperty("Viewing").SetValue(teenRow, null);
Saved();
set = Published();
Ok("a profile whose limits are removed leaves the set", !set.Contains(TEEN));
Ok("the others stay", set.Contains(KID) && set.Count == 1);

Section("A profile deleted");
mappings.Remove(kidRow);
Saved();
set = Published();
Ok("a deleted profile leaves the set", !set.Contains(KID));
Ok("the set is empty", set.Count == 0, "got " + set.Count);

Section("Bonfire off");
mapType.GetProperty("Viewing").SetValue(teenRow, Rules(45));
Saved();
Ok("a limited profile is published again", Published().Contains(TEEN));
cfgType.GetProperty("BonfireOn").SetValue(config, false);
Static(pluginType, "ForgetOnOffCaches");
Static(tracker, "Index");
Ok("with Bonfire off the set is empty, as the index is", Published().Count == 0);

Section("Before the configuration is loaded");
cfgType.GetProperty("BonfireOn").SetValue(config, true);
Saved();
Ok("limited again", Published().Contains(TEEN));
var instanceProp = pluginType.GetProperty("Instance", Any);
var held = instanceProp.GetValue(null);
instanceProp.SetValue(null, null);
Static(tracker, "Reset");
Static(tracker, "Index");
instanceProp.SetValue(null, held);
Ok("no plugin instance publishes nothing, the last set stays", Published().Contains(TEEN));

try { Directory.Delete(tempDir, true); } catch { }
return Finish();

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
