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

    var served = (string)publish.Invoke(null, new object[] { real });
    Ok("the served script advertises French",
       served.Contains("let SUPPORTED_LOCALES = ['fr']", StringComparison.Ordinal));
    Ok("and is otherwise byte-identical",
       served.Replace("let SUPPORTED_LOCALES = ['fr']; // __BONFIRE_LOCALES__", marker) == real);
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
