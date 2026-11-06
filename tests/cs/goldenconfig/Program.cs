using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;

// ─────────────────────────────────────────────────────────────────────────────
// The configuration file the last stable release wrote, read by today's build (R-1289).
//
// Jellyfin keeps the plugin's settings as XML written by System.Xml's XmlSerializer. A
// property renamed, a type changed or an element that stops being read does not fail
// anywhere visible: that setting, or the whole household list, comes back as its default.
// Nothing guarded it. About fifteen harnesses replace IXmlSerializer with a stub that does
// nothing, so no test had ever read a real file.
//
// tests/golden/PluginConfiguration.1.7.1.xml is what 1.7.1 (main) writes: its own
// PluginConfiguration, every property given a value that is not the default, every kind of
// row present, through the real XmlSerializer. Read here into today's class, then:
//
//   - every value in the file must come out again when today's class is written, except
//     the elements listed as dropped on purpose;
//   - writing and reading again, twice, must change nothing, compared property by
//     property by reflection.
//
// Regenerating it is not part of a release: it is the old file. A new golden file for a
// later stable release goes beside it, and this reads them all.
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
        d = Path.GetDirectoryName(d);
    return d ?? throw new Exception("could not find the repository root");
}

var tfm = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Name;
var asm = Assembly.LoadFrom(Path.Combine(RepoRoot(), "bin", "Release", tfm, "Jellyfin.Profiles.dll"));
var cfgType = asm.GetType("Jellyfin.Profiles.Configuration.PluginConfiguration", true);
var serializer = new XmlSerializer(cfgType);

// Elements the golden file has that today's class deliberately no longer keeps. Each is
// skipped on read, not an error. Anything else missing from the output is a bug.
var droppedOnPurpose = new Dictionary<string, string>
{
    ["PluginConfiguration/RequireMasterPinForCreation"] = "removed in 1.7.2: creating a profile asks for the main PIN like every other change",
};

string Write(object cfg)
{
    var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
    using var ms = new MemoryStream();
    using (var w = XmlWriter.Create(ms, settings)) serializer.Serialize(w, cfg);
    return Encoding.UTF8.GetString(ms.ToArray());
}
object Read(string xml)
{
    using var r = new StringReader(xml);
    return serializer.Deserialize(r);
}

// "path=value" for every leaf element, counted: lists repeat their paths.
Dictionary<string, int> Leaves(string xml)
{
    var counts = new Dictionary<string, int>();
    void Walk(XElement e, string path)
    {
        path = path.Length == 0 ? e.Name.LocalName : path + "/" + e.Name.LocalName;
        if (!e.Elements().Any())
        {
            var key = path + "=" + e.Value;
            counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
            return;
        }
        foreach (var c in e.Elements()) Walk(c, path);
    }
    Walk(XDocument.Parse(xml).Root, string.Empty);
    return counts;
}

// Property-by-property comparison by reflection, naming the first difference.
string Differ(object a, object b, string path)
{
    if (a == null || b == null) return a == b ? null : path + ": " + (a ?? "null") + " vs " + (b ?? "null");
    var t = a.GetType();
    if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(Guid) || t == typeof(DateTime) || t == typeof(decimal))
        return Equals(a, b) ? null : path + ": " + a + " vs " + b;
    if (a is IEnumerable ea && b is IEnumerable eb)
    {
        var la = ea.Cast<object>().ToList();
        var lb = eb.Cast<object>().ToList();
        if (la.Count != lb.Count) return path + ": " + la.Count + " items vs " + lb.Count;
        for (var i = 0; i < la.Count; i++)
        {
            var d = Differ(la[i], lb[i], path + "[" + i + "]");
            if (d != null) return d;
        }
        return null;
    }
    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
    {
        if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
        var d = Differ(p.GetValue(a), p.GetValue(b), path + "." + p.Name);
        if (d != null) return d;
    }
    return null;
}

var goldenFolder = Path.Combine(RepoRoot(), "tests", "golden");
var files = Directory.GetFiles(goldenFolder, "PluginConfiguration.*.xml").OrderBy(f => f).ToList();
Ok("there is a golden file from a stable release", files.Count > 0, goldenFolder);

foreach (var file in files)
{
    var label = Path.GetFileName(file);
    Console.WriteLine();
    Console.WriteLine("── " + label + " ──────────────────────────────────────");

    var golden = File.ReadAllText(file);
    object a = null;
    string readError = null;
    try { a = Read(golden); }
    catch (Exception e) { readError = (e.InnerException ?? e).Message; }
    Ok("today's build reads it", a != null, readError);
    if (a == null) continue;

    var xml1 = Write(a);
    var have = Leaves(xml1);
    var want = Leaves(golden);

    var lost = new List<string>();
    foreach (var kv in want)
    {
        var element = kv.Key.Substring(0, kv.Key.IndexOf('='));
        if (droppedOnPurpose.ContainsKey(element)) continue;
        if (!have.TryGetValue(kv.Key, out var n) || n < kv.Value) lost.Add(kv.Key + " (" + (have.TryGetValue(kv.Key, out var m) ? m : 0) + " of " + kv.Value + ")");
    }
    Ok("every value in it survives being read and written again", lost.Count == 0,
       lost.Count + " lost, first: " + string.Join(" | ", lost.Take(4)));

    foreach (var path in droppedOnPurpose.Keys)
        Ok(path + " is skipped, as intended (" + droppedOnPurpose[path] + ")", want.Keys.Any(k => k.StartsWith(path + "=")) && !have.Keys.Any(k => k.StartsWith(path + "=")));

    var b = Read(xml1);
    var xml2 = Write(b);
    var c = Read(xml2);
    var xml3 = Write(c);
    Ok("a second round trip writes the same file", xml1 == xml2 && xml2 == xml3);
    var d1 = Differ(a, b, "config");
    var d2 = Differ(b, c, "config");
    Ok("and every property is equal after the first round trip, by reflection", d1 == null, d1);
    Ok("and after the second", d2 == null, d2);
}

// R-1127: two Jellyfin enums used to be stored by name, so a Jellyfin that renamed or dropped a
// member made the whole file unreadable: not the one limit, every household. They are plain
// text now, converted where they are used, and a name this build does not know is kept as it
// was written (the server that wrote it may know it) and ignored where it is used.
Console.WriteLine();
Console.WriteLine("── A name Jellyfin no longer has ───────────────────────────────────");
if (files.Count > 0)
{
    var changedNames = File.ReadAllText(files[0])
        .Replace("<SyncPlayAccess>JoinGroups</SyncPlayAccess>", "<SyncPlayAccess>FutureMode</SyncPlayAccess>")
        .Replace("<UnratedItem>Series</UnratedItem>", "<UnratedItem>Hologram</UnratedItem>");
    Ok("the test file really has the unknown names", changedNames.Contains("FutureMode") && changedNames.Contains("Hologram"));

    object cfg = null;
    string error = null;
    try { cfg = Read(changedNames); }
    catch (Exception e) { error = (e.InnerException ?? e).Message; }
    Ok("the whole file still loads", cfg != null, error);
    if (cfg != null)
    {
        var mappings = (IList)cfgType.GetProperty("Mappings").GetValue(cfg);
        Ok("with every household in it", mappings.Count == 2, mappings.Count.ToString());
        var limits = mappings[0].GetType().GetProperty("OwnLimits").GetValue(mappings[0]);
        var limitsType = limits.GetType();
        var access = limitsType.GetProperty("SyncPlayAccess").GetValue(limits);
        Ok("the access type it can't name counts as the enum's first value, not an error", Convert.ToInt32(access) == 0, access?.ToString());
        var blocked = ((IEnumerable)limitsType.GetProperty("BlockUnratedItems").GetValue(limits)).Cast<object>().Select(o => o.ToString()).ToList();
        Ok("the kinds it can name are kept and the one it can't is left out", blocked.SequenceEqual(new[] { "Movie", "Trailer" }), string.Join(",", blocked));
        var again = Write(cfg);
        Ok("written back, the names it didn't know are still there", again.Contains("<SyncPlayAccess>FutureMode</SyncPlayAccess>") && again.Contains("<UnratedItem>Hologram</UnratedItem>"));
        Ok("and the known ones are written as they always were", again.Contains("<UnratedItem>Movie</UnratedItem>") && again.Contains("<UnratedItem>Trailer</UnratedItem>"));
    }
}

Console.WriteLine();
if (fails.Count > 0)
{
    foreach (var f in fails) Console.WriteLine("   - " + f);
    Console.WriteLine(pass + " passed, " + fails.Count + " failed");
    return 1;
}
Console.WriteLine(pass + " passed, 0 failed");
return 0;
