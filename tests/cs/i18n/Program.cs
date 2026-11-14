using System;
using System.Collections;
using System.Linq;
using System.Reflection;

// Find the repository root by walking up from this binary to the plugin's project file.
// A hardcoded path made these harnesses fail on the first CI run.
static string RepoRoot()
{
    var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
    while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "Jellyfin.Profiles.csproj")))
        d = d.Parent;
    if (d == null) throw new InvalidOperationException("Could not find the repository root.");
    return d.FullName;
}
static string RepoPath(params string[] parts)
{
    var all = new System.Collections.Generic.List<string> { RepoRoot() };
    all.AddRange(parts);
    return System.IO.Path.Combine(all.ToArray());
}

// The server half of translations, against the compiled plugin:
//   - locale codes are discovered from embedded resources, not a hand-kept list
//   - the client's locale list is filled in as profiles.js is served
//
// Adding a language must stay one JSON file. A regression is silent: the file ships and the
// endpoint serves it, but no browser ever asks for it.

// Which build to load. This harness's output sits in bin/Release/<tfm>/, so its folder name
// is the framework the csproj resolved: no second place to keep in step. tests/run.sh cs10
// runs the whole set against net10.0.
var tfm = new System.IO.DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
    System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)).Name;
var dll = RepoPath("bin", "Release", tfm, "Jellyfin.Profiles.dll");
var asm = Assembly.LoadFrom(dll);
var ctrl = asm.GetType("Jellyfin.Profiles.Controllers.ProfilesController", true);

const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

int pass = 0;
var fails = new System.Collections.Generic.List<string>();
void Ok(string name, bool cond)
{
    if (cond) { pass++; Console.WriteLine("  PASS  " + name); }
    else { fails.Add(name); Console.WriteLine("  FAIL  " + name); }
}

Console.WriteLine();
Console.WriteLine("── Locales are discovered, not listed ─────────────────────────");

// Every *.json under Web/i18n must be embedded by the csproj wildcard.
var resources = asm.GetManifestResourceNames();
var embedded = resources
    .Where(r => r.StartsWith("Jellyfin.Profiles.Web.i18n.", StringComparison.Ordinal)
             && r.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
    .Select(r => r.Substring("Jellyfin.Profiles.Web.i18n.".Length)
                  .Replace(".json", "", StringComparison.OrdinalIgnoreCase))
    .OrderBy(c => c, StringComparer.Ordinal)
    .ToArray();

var onDisk = System.IO.Directory
    .GetFiles(RepoPath("Web", "i18n"), "*.json")
    .Select(System.IO.Path.GetFileNameWithoutExtension)
    .OrderBy(c => c, StringComparer.Ordinal)
    .ToArray();

Ok("every translation file on disk is embedded in the assembly "
   + "(" + onDisk.Length + " on disk, " + embedded.Length + " embedded)",
   onDisk.SequenceEqual(embedded, StringComparer.Ordinal));
if (!onDisk.SequenceEqual(embedded, StringComparer.Ordinal))
{
    Console.WriteLine("        on disk : " + string.Join(", ", onDisk));
    Console.WriteLine("        embedded: " + string.Join(", ", embedded));
}

var supportedProp = ctrl.GetProperty("SupportedI18nLocales", Flags);
Ok("the plugin exposes the discovered set", supportedProp != null);

string[] discovered = Array.Empty<string>();
if (supportedProp != null)
{
    discovered = ((IEnumerable)supportedProp.GetValue(null)).Cast<string>()
        .OrderBy(c => c, StringComparer.Ordinal).ToArray();
    Ok("what it discovered matches what is embedded",
       discovered.SequenceEqual(embedded, StringComparer.OrdinalIgnoreCase));
    Ok("French is among them", discovered.Contains("fr", StringComparer.OrdinalIgnoreCase));
}

Console.WriteLine();
Console.WriteLine("── The client is told what exists ─────────────────────────────");

var publish = ctrl.GetMethod("PublishLocales", Flags);
Ok("PublishLocales exists", publish != null);

if (publish != null)
{
    const string marker = "let SUPPORTED_LOCALES = []; // __BONFIRE_LOCALES__";

    var js = (string)publish.Invoke(null, new object[] { "before\n    " + marker + "\nafter" });
    Ok("the marker is rewritten", !js.Contains(marker, StringComparison.Ordinal));
    Ok("with every discovered locale",
       discovered.All(c => js.Contains("'" + c + "'", StringComparison.Ordinal)));
    // The whole list as one array literal. Looking for "['fr'" went red when ca.json sorted
    // ahead of it: that tested which language came first, not the shape of the assignment.
    var expectedList = "let SUPPORTED_LOCALES = ["
        + string.Join(", ", discovered.Select(c => "'" + c + "'")) + "];";
    Ok("as a JS array assignment",
       discovered.Length > 0 && js.Contains(expectedList, StringComparison.OrdinalIgnoreCase));
    Ok("leaving the rest of the file alone",
       js.StartsWith("before\n", StringComparison.Ordinal)
       && js.EndsWith("\nafter", StringComparison.Ordinal));

    // If the marker is ever edited away the replace must be inert, not corrupting.
    var untouched = (string)publish.Invoke(null, new object[] { "no marker here at all" });
    Ok("a file without the marker is returned unchanged",
       untouched == "no marker here at all");

    // The real script has to carry the marker exactly once, or the client is handed an
    // empty list and silently renders English for everyone.
    var real = System.IO.File.ReadAllText(RepoPath("Web", "profiles.js"));
    var count = real.Split(marker).Length - 1;
    Ok("profiles.js carries the marker exactly once (found " + count + ")", count == 1);

    // Built from what is embedded. This asserted ['fr'] until Polish arrived and one new JSON
    // file broke the build; a test for "adding a language is one file" must not need editing
    // when a language is added.
    var expected = "let SUPPORTED_LOCALES = ["
        + string.Join(", ", discovered.OrderBy(c => c, StringComparer.Ordinal).Select(c => "'" + c + "'"))
        + "]";

    var served = (string)publish.Invoke(null, new object[] { real });
    Ok("the served script advertises every embedded locale (" + expected + ")",
       served.Contains(expected, StringComparison.Ordinal));
    Ok("and is otherwise byte-identical",
       served.Replace(expected + "; // __BONFIRE_LOCALES__", marker) == real);
}

Console.WriteLine();
Console.WriteLine("── A failed read is not remembered ─────────────────────────────");

// The loader was GetOrAdd with a factory returning null on failure. GetOrAdd stores that
// null, so one empty read lost the language until restart, and because t() falls back per
// key it showed as the interface quietly reverting to English.
//
// Driven directly: the failure is a caching contract, and an HTTP round trip would need the
// whole DI graph. Run against the old build the method does not exist, which proves
// nothing about the bug; here a cached null cannot be told from a cached answer.
var trap = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
trap.GetOrAdd("fr", _ => null);
Ok("GetOrAdd caches a null the factory returned — the mechanism behind the bug "
   + "(key present: " + trap.ContainsKey("fr") + ")",
   trap.ContainsKey("fr") && trap["fr"] == null);

var readLocale = ctrl.GetMethod("ReadLocaleJson", Flags);
Ok("the loader is reachable", readLocale != null);

var cachedCodes = ctrl.GetProperty("CachedLocaleCodes", Flags);
Ok("so is what it has cached", cachedCodes != null);

if (readLocale != null && cachedCodes != null)
{
    string[] Cached() => ((IEnumerable)cachedCodes.GetValue(null)).Cast<string>().ToArray();

    // A code with no embedded resource stands in for a read that comes back empty.
    const string missing = "zz-nonexistent";
    var first = readLocale.Invoke(null, new object[] { missing, null });
    Ok("a read that comes back empty returns null", first == null);
    Ok("and is NOT cached, so the next request tries again "
       + "(cached: " + string.Join(", ", Cached().DefaultIfEmpty("nothing")) + ")",
       !Cached().Contains(missing, StringComparer.Ordinal));

    var second = readLocale.Invoke(null, new object[] { missing, null });
    Ok("asking twice still returns null rather than a cached one", second == null);
    Ok("and still has not been cached", !Cached().Contains(missing, StringComparer.Ordinal));

    // A real one, to show the cache is doing its job rather than being switched off.
    if (embedded.Length > 0)
    {
        var real = embedded[0];
        var content = readLocale.Invoke(null, new object[] { real, null });
        Ok(real + " reads back as content", content is string s && s.Length > 0);
        Ok("and IS cached", Cached().Contains(real, StringComparer.Ordinal));

        var again = readLocale.Invoke(null, new object[] { real, null });
        Ok("a second read returns the same content", Equals(again, content));
    }

    // The endpoint matches a code without regard to case and the assembly's resource lookup is
    // case-sensitive, so "pt-br" for pt-BR.json passed the first and failed the second (503).
    // Each embedded code is asked for in both other cases, one at a time.
    foreach (var code in embedded)
    {
        foreach (var variant in new[] { code.ToUpperInvariant(), code.ToLowerInvariant() })
        {
            if (variant == code) continue;
            var read = readLocale.Invoke(null, new object[] { variant, null });
            Ok("asking for " + variant + " reads " + code + ".json", read is string v && v.Length > 0);
        }
    }
    Ok("and the cache holds each language once, under its file's own spelling "
       + "(cached: " + string.Join(", ", Cached()) + ")",
       Cached().All(c => embedded.Contains(c, StringComparer.Ordinal)));
}

Console.WriteLine();
Console.WriteLine("── Every embedded file is readable and complete ────────────────");

foreach (var code in embedded)
{
    using var stream = asm.GetManifestResourceStream("Jellyfin.Profiles.Web.i18n." + code + ".json");
    Ok(code + ".json is readable from the assembly", stream != null);
    if (stream == null) continue;

    using var reader = new System.IO.StreamReader(stream);
    var text = reader.ReadToEnd();
    Ok(code + ".json parses as JSON", TryJson(text));
}

Console.WriteLine();
Console.WriteLine("── Households and the settings page get their own half ─────────");

// One file per language holds both, and the endpoint serves them apart: every TV on the
// server fetches the household's half, and none of them has any use for the few hundred
// strings of the administrator's page. Checked key by key, not by count.
var readPart = ctrl.GetMethod("ReadLocalePart", Flags);
Ok("the server can split a file", readPart != null);
if (readPart != null)
{
    foreach (var code in embedded)
    {
        using var stream = asm.GetManifestResourceStream("Jellyfin.Profiles.Web.i18n." + code + ".json");
        using var reader = new System.IO.StreamReader(stream!);
        var all = System.Text.Json.JsonDocument.Parse(reader.ReadToEnd()).RootElement
            .EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());

        System.Collections.Generic.Dictionary<string, string?> Half(bool admin)
        {
            var text = (string)readPart.Invoke(null, new object?[] { code, admin, null })!;
            return System.Text.Json.JsonDocument.Parse(text).RootElement
                .EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
        }
        var app = Half(false);
        var adm = Half(true);

        Ok(code + ": the household half has no admin.* key",
           app.Keys.All(k => !k.StartsWith("admin.", StringComparison.Ordinal)));
        Ok(code + ": the settings half has nothing else",
           adm.Keys.All(k => k.StartsWith("admin.", StringComparison.Ordinal)));
        Ok(code + ": the settings half is translated (" + adm.Count + " keys)", adm.Count > 0);
        // The mail's words (email.*) and the messages to apps (notice.*) are read by the
        // server and go to neither half.
        Ok(code + ": neither half carries the mail's words",
           app.Keys.Concat(adm.Keys).All(k => !k.StartsWith("email.", StringComparison.Ordinal) && !k.StartsWith("notice.", StringComparison.Ordinal)));
        var missingKeys = all.Keys.Where(k => !k.StartsWith("email.", StringComparison.Ordinal) && !k.StartsWith("notice.", StringComparison.Ordinal)
                                              && !(app.ContainsKey(k) || adm.ContainsKey(k))).ToArray();
        Ok(code + ": between them they hold every other key, unchanged"
           + (missingKeys.Length > 0 ? " (lost: " + string.Join(", ", missingKeys.Take(3)) + ")" : ""),
           missingKeys.Length == 0
           && all.Where(p => !p.Key.StartsWith("email.", StringComparison.Ordinal) && !p.Key.StartsWith("notice.", StringComparison.Ordinal)).All(p => (app.TryGetValue(p.Key, out var v) || adm.TryGetValue(p.Key, out v)) && v == p.Value));
        // Relaxed escaping: an accented letter arrives as itself, not as an escape sequence, which
        // keeps a Polish or Turkish file from doubling in size. HTML-sensitive characters such as an
        // apostrophe are still escaped, which is wanted.
        var raw = (string)readPart.Invoke(null, new object?[] { code, false, null })!;
        var letters = app.Values.Where(v => v != null).SelectMany(v => v!)
            .Where(ch => ch > 127 && char.IsLetter(ch)).Distinct().ToArray();
        var escaped = letters.Where(ch => raw.IndexOf(ch) < 0).ToArray();
        Ok(code + ": accented letters are served as written (" + letters.Length + " checked"
           + (escaped.Length > 0 ? ", escaped: " + new string(escaped.Take(5).ToArray()) : "") + ")",
           escaped.Length == 0);
    }
}

static bool TryJson(string s)
{
    try { using var _ = System.Text.Json.JsonDocument.Parse(s); return true; }
    catch { return false; }
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
