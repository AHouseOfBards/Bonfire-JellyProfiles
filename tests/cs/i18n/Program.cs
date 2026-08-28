using System;
using System.Collections;
using System.Linq;
using System.Reflection;

// Locate the repository root by walking up from this binary until the plugin's project
// file appears. This was a hardcoded absolute path, so these harnesses could only run on
// one machine — they failed on the first CI run.
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
// The point of both is that adding a language is one JSON file. A regression here is
// silent — the file ships, the endpoint serves it, and no browser ever asks for it.

var dll = RepoPath("bin", "Release", "net9.0", "Jellyfin.Profiles.dll");
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
    Ok("as a JS array assignment",
       js.Contains("let SUPPORTED_LOCALES = ['fr']", StringComparison.Ordinal)
       || js.Contains("let SUPPORTED_LOCALES = ['fr',", StringComparison.Ordinal));
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

    // Built from what is actually embedded, not hardcoded to one language. This asserted
    // ['fr'] literally until Polish arrived, at which point adding a single JSON file
    // broke the build — which is precisely the promise the rest of this harness exists to
    // defend. A test for "adding a language is one file" must not itself need editing
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

// The loader was GetOrAdd with a factory returning null on failure, and GetOrAdd stores
// what the factory returns — so one empty read put a permanent null in the dictionary and
// that language was gone until the server restarted. Because t() falls back per key, the
// visible symptom was the interface quietly reverting to English.
//
// Driven directly rather than through the endpoint: the failure is a caching contract,
// and asserting it through an HTTP round trip would need the whole DI graph to say
// something the loader can say on its own.
// The trap itself, shown rather than described. Pointing this harness at the old build
// only proves the method did not exist yet, which says nothing about whether the bug was
// real; this says it was. GetOrAdd stores what the factory returns, null included, and
// there is then no way to tell a cached failure from a cached answer.
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
