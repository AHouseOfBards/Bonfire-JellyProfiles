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
    ["PluginConfiguration/IndexInjectionMode"] = "removed in 2.0: only the middleware remains",
    ["PluginConfiguration/Mappings/ProfileMapping/SwitcherMode"] = "removed in 2.0: AskOnStartup and SwitcherLocation replaced it in 1.3.2; a native one is carried into them on load",
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

// Settings cut before 2.0. A file written by 1.6, 1.7 or a 1.9 beta still carries them, with
// whatever value the household had chosen. Each must load, leave every other value exactly as
// it was, and be gone from the next save. The golden file is the baseline; each case turns it
// into the file an older version would have written and compares what comes out.
Console.WriteLine();
Console.WriteLine("── Settings cut before 2.0 ─────────────────────────────────────────");
if (files.Count > 0)
{
    var baseline = File.ReadAllText(files[0]);
    var baselineLeaves = Leaves(Write(Read(baseline)));

    void CutCase(string name, string oldFile, params string[] mustBeGone)
    {
        Ok(name + ": the old file really carries it", mustBeGone.All(m => oldFile.Contains(m)), string.Join(",", mustBeGone));
        object cfg = null;
        string error = null;
        try { cfg = Read(oldFile); }
        catch (Exception e) { error = (e.InnerException ?? e).Message; }
        Ok(name + ": it loads", cfg != null, error);
        if (cfg == null) return;
        var written = Write(cfg);
        Ok(name + ": the next save leaves it out", mustBeGone.All(m => !written.Contains(m.Split('>')[0].TrimStart('<'))),
           string.Join(",", mustBeGone.Where(m => written.Contains(m.Split('>')[0].TrimStart('<')))));
        var now = Leaves(written);
        var gone = mustBeGone.Select(m => m.Split('>')[0].TrimStart('<')).ToList();
        bool IsCut(string key) => gone.Any(g => { var path = key.Substring(0, key.IndexOf('=')); return path.EndsWith("/" + g) || path.Contains("/" + g + "/"); });
        var lostOthers = baselineLeaves.Where(kv => !IsCut(kv.Key) && (!now.TryGetValue(kv.Key, out var n) || n < kv.Value)).Select(kv => kv.Key).ToList();
        Ok(name + ": every other value is kept", lostOthers.Count == 0, lostOthers.Count + " lost, first: " + string.Join(" | ", lostOthers.Take(3)));
        var addedOthers = now.Where(kv => !baselineLeaves.ContainsKey(kv.Key)).Select(kv => kv.Key).ToList();
        Ok(name + ": and nothing new appears", addedOthers.Count == 0, string.Join(" | ", addedOthers.Take(3)));
        Ok(name + ": a second round trip writes the same file", Write(Read(written)) == written);
    }

    foreach (var mode in new[] { "file", "both", "middleware" })
        CutCase("injection mode " + mode,
            baseline.Replace("<IndexInjectionMode>middleware</IndexInjectionMode>", "<IndexInjectionMode>" + mode + "</IndexInjectionMode>"),
            "<IndexInjectionMode>" + mode + "</IndexInjectionMode>");

    // EmailSettings.FromName. The 1.7.1 golden file predates the email settings, so the
    // section is added here, as a 1.9 beta wrote it, with and without the name. The setup
    // was tested, so it also carries its fingerprint; cutting the name must not undo that,
    // or every administrator's PIN reset by email would stop on upgrade.
    {
        const string EmailTail = "<Host>smtp.example.com</Host><Port>2525</Port><Security>none</Security>"
            + "<Username>mailer</Username><Password>app-password</Password><FromAddress>bonfire@example.com</FromAddress>";
        string WithEmail(string extra, string fingerprint) =>
            baseline.Replace("</PluginConfiguration>", "<Email>" + EmailTail + extra
                + (fingerprint == null ? "" : "<VerifiedFingerprint>" + fingerprint + "</VerifiedFingerprint>") + "</Email></PluginConfiguration>");
        var emailProp = cfgType.GetProperty("Email");
        var fingerprint = (string)emailProp.PropertyType.GetMethod("Fingerprint").Invoke(emailProp.GetValue(Read(WithEmail("", null))), null);
        baselineLeaves = Leaves(Write(Read(WithEmail("", fingerprint))));
        CutCase("email From name", WithEmail("<FromName>Hearth</FromName>", fingerprint), "<FromName>Hearth</FromName>");
        var loadedEmail = emailProp.GetValue(Read(WithEmail("<FromName>Hearth</FromName>", fingerprint)));
        Ok("a tested email setup is still tested", (bool)emailProp.PropertyType.GetProperty("IsVerified").GetValue(loadedEmail));
        baselineLeaves = Leaves(Write(Read(baseline)));
    }

    // OnboardingSettings.RequiredSteps, the "can't skip" list of first-time setup. Also newer
    // than the golden file, so the section is added here as a 1.9 beta wrote it.
    {
        string WithOnboarding(string required) =>
            baseline.Replace("</PluginConfiguration>", "<Onboarding><Enabled>true</Enabled><StepsChosen>true</StepsChosen>"
                + "<Steps><string>welcome</string><string>mainPin</string><string>theme</string></Steps>"
                + required + "<WelcomeText>Welcome home.</WelcomeText><Generation>4</Generation></Onboarding></PluginConfiguration>");
        baselineLeaves = Leaves(Write(Read(WithOnboarding(""))));
        CutCase("first-time setup required steps",
            WithOnboarding("<RequiredSteps><string>mainPin</string><string>theme</string></RequiredSteps>"),
            "<RequiredSteps>");
        baselineLeaves = Leaves(Write(Read(baseline)));
    }

    // OnboardingSettings.Steps, StepsChosen and WelcomeText: the "show this step" choice and
    // the administrator's own welcome. Cut: every step is always offered and the welcome is
    // Bonfire's. The 1.7.1 golden file predates first-time setup, so there is no element to
    // list in droppedOnPurpose; the section is added here as a 1.9 beta wrote it, with the
    // choice made. It must load, lose exactly these three and keep the rest.
    {
        string WithChoice(bool choice) =>
            baseline.Replace("</PluginConfiguration>", "<Onboarding><Enabled>true</Enabled>"
                + (choice ? "<StepsChosen>true</StepsChosen><Steps><string>welcome</string><string>mainPin</string><string>theme</string></Steps>"
                           + "<WelcomeText>Welcome home.</WelcomeText>" : "")
                + "<Generation>4</Generation></Onboarding></PluginConfiguration>");
        baselineLeaves = Leaves(Write(Read(WithChoice(false))));
        CutCase("first-time setup steps and welcome", WithChoice(true),
            "<StepsChosen>true</StepsChosen>", "<Steps>", "<WelcomeText>Welcome home.</WelcomeText>");
        baselineLeaves = Leaves(Write(Read(baseline)));
    }

    // ProfileMapping.SwitcherMode. The golden file has gate on both mappings, beside an
    // explicit AskOnStartup and SwitcherLocation. Whatever the old field said, an explicit
    // choice stands, so every other value is the same.
    foreach (var legacy in new[] { "gate", "native" })
        CutCase("switcher mode " + legacy,
            baseline.Replace("<SwitcherMode>gate</SwitcherMode>", "<SwitcherMode>" + legacy + "</SwitcherMode>"),
            "<SwitcherMode>" + legacy + "</SwitcherMode>");

    // An account that never chose either of the newer two, only the old one. Only "native"
    // was ever a choice (every row carried "gate"), and it must not be lost: it becomes the
    // two settings it meant. "gate" must not invent either, or the administrator's default
    // would stop reaching that account.
    string Bare(string legacy) => System.Text.RegularExpressions.Regex.Replace(
        baseline.Replace("<SwitcherMode>gate</SwitcherMode>", "<SwitcherMode>" + legacy + "</SwitcherMode>"),
        @"\s*<(AskOnStartup|SwitcherLocation)>[^<]*</\1>", string.Empty);
    var mapType = asm.GetType("Jellyfin.Profiles.Configuration.ProfileMapping", true);
    (object ask, object loc) Prefs(string xml)
    {
        var m = (IList)cfgType.GetProperty("Mappings").GetValue(Read(xml));
        return (mapType.GetProperty("AskOnStartup").GetValue(m[0]), mapType.GetProperty("SwitcherLocation").GetValue(m[0]));
    }
    var nativeOnly = Prefs(Bare("native"));
    Ok("an account that only had native keeps it: no startup prompt", Equals(nativeOnly.ask, false), nativeOnly.ask?.ToString());
    Ok("and the switcher stays in Jellyfin's menu", Equals(nativeOnly.loc, "menu"), nativeOnly.loc?.ToString());
    var gateOnly = Prefs(Bare("gate"));
    Ok("an account that only had gate chose nothing: no startup answer", gateOnly.ask == null, gateOnly.ask?.ToString());
    Ok("and no location", gateOnly.loc == null, gateOnly.loc?.ToString());
    var nativeThenAsk = Prefs(Bare("native").Replace("</SwitcherMode>", "</SwitcherMode><AskOnStartup>true</AskOnStartup>"));
    Ok("native beside an explicit startup prompt keeps the explicit one", Equals(nativeThenAsk.ask, true), nativeThenAsk.ask?.ToString());
    var askThenNative = Prefs(Bare("native").Replace("<SwitcherMode>", "<AskOnStartup>true</AskOnStartup><SwitcherMode>"));
    Ok("and so does an explicit one listed before it", Equals(askThenNative.ask, true), askThenNative.ask?.ToString());
    Ok("what it was told is not written back as the old field", !Write(Read(Bare("native"))).Contains("SwitcherMode"));
}

// Picture quality across an upgrade. A fresh install starts on maximum; a server that saved a
// level keeps it, whatever it is. A file with no ProfileImageQuality at all (one written before
// the setting existed) is the one case that takes the new default: XmlSerializer leaves the
// property at its initialiser when the element is absent. A value this build cannot read is
// kept as written but behaves as the safest level (ImageQualities.Normalize), not as maximum.
Console.WriteLine();
Console.WriteLine("── Picture quality across an upgrade ───────────────────────────────");
if (files.Count > 0)
{
    var baseline = File.ReadAllText(files[0]);
    var qualityProp = cfgType.GetProperty("ProfileImageQuality");
    var tiers = asm.GetType("Jellyfin.Profiles.Configuration.ImageQualities", true);
    string QualityOf(string xml) => (string)qualityProp.GetValue(Read(xml));
    string WithQuality(string value) => value == null
        ? System.Text.RegularExpressions.Regex.Replace(baseline, @"\s*<ProfileImageQuality>[^<]*</ProfileImageQuality>", string.Empty)
        : baseline.Replace("<ProfileImageQuality>high</ProfileImageQuality>", "<ProfileImageQuality>" + value + "</ProfileImageQuality>");

    Ok("a fresh install starts on maximum", (string)qualityProp.GetValue(Activator.CreateInstance(cfgType)) == "maximum");
    Ok("the golden file really saved high, and keeps it", QualityOf(baseline) == "high");
    Ok("a saved standard stays standard", QualityOf(WithQuality("standard")) == "standard");
    Ok("a saved maximum stays maximum", QualityOf(WithQuality("maximum")) == "maximum");
    Ok("a file with no saved level takes the new default, maximum", QualityOf(WithQuality(null)) == "maximum");
    var normalize = tiers.GetMethod("Normalize");
    Ok("a value it cannot read is kept as written", QualityOf(WithQuality("enormous")) == "enormous");
    Ok("and behaves as standard, the safest level", (string)normalize.Invoke(null, new object[] { "enormous" }) == "standard");
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
