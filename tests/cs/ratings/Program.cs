using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

// ─────────────────────────────────────────────────────────────────────────────
// RatingSystems.Parse against Jellyfin's own rating tables (tests/fixtures/ratings,
// copied from release-10.11.z Emby.Server.Implementations/Localization/Ratings).
//
// Each country is checked age by age: every age it has is offered, nothing else is, and
// each label is one of that age's own names. tests/js/ratings.js checks the client against
// the same tables, so the two halves are held to one source.
// ─────────────────────────────────────────────────────────────────────────────

int pass = 0;
var fails = new List<string>();
void Ok(string name, bool cond, string detail = null)
{
    if (cond) { pass++; Console.WriteLine("  PASS  " + name); }
    else { fails.Add(name); Console.WriteLine("  FAIL  " + name + (detail == null ? "" : "  — " + detail)); }
}

static string RepoRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d != null && !File.Exists(Path.Combine(d.FullName, "Jellyfin.Profiles.csproj"))) d = d.Parent;
    if (d == null) throw new InvalidOperationException("Could not find the repository root.");
    return d.FullName;
}

var asm = typeof(Jellyfin.Profiles.RatingChoice).Assembly;
var systems = asm.GetType("Jellyfin.Profiles.RatingSystems", true);
const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
var parse = systems.GetMethod("Parse", Flags);
var forCountry = systems.GetMethod("ForCountry", Flags);
Ok("Parse is reachable", parse != null);
Ok("ForCountry is reachable", forCountry != null);
if (parse == null || forCountry == null) { Console.WriteLine(pass + " passed, " + fails.Count + " failed"); return 1; }

List<Jellyfin.Profiles.RatingChoice> Parse(string json, out string country)
{
    var args = new object[] { json, null };
    var result = (List<Jellyfin.Profiles.RatingChoice>)parse.Invoke(null, args);
    country = (string)args[1];
    return result;
}

var prefix = (string)systems.GetField("ResourcePrefix", Flags)?.GetValue(null);
Ok("it looks for Jellyfin's resource names (" + prefix + ")",
   prefix == "Emby.Server.Implementations.Localization.Ratings.");

var dir = Path.Combine(RepoRoot(), "tests", "fixtures", "ratings");
var files = Directory.GetFiles(dir, "*.json").OrderBy(f => f).ToArray();
Ok("there are tables to check (" + files.Length + ")", files.Length >= 4);

foreach (var file in files)
{
    var code = Path.GetFileNameWithoutExtension(file);
    var json = File.ReadAllText(file);
    var choices = Parse(json, out var country);
    Ok(code + ": parses", choices != null);
    if (choices == null) continue;
    Ok(code + ": reads its own country code (" + country + ")", string.Equals(country, code, StringComparison.OrdinalIgnoreCase));

    using var doc = JsonDocument.Parse(json);
    var table = doc.RootElement.GetProperty("ratings").EnumerateArray()
        .Select(r => (Score: r.GetProperty("ratingScore").GetProperty("score").GetInt32(),
                      Sub: r.GetProperty("ratingScore").TryGetProperty("subScore", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : (int?)null,
                      Names: r.GetProperty("ratingStrings").EnumerateArray().Select(n => n.GetString()).ToList()))
        .ToList();
    var ages = table.Select(r => r.Score).Where(s => s < 1000).Distinct().OrderBy(s => s).ToList();
    var offered = choices.Select(c => c.Score).ToList();

    Ok(code + ": offers exactly its own ages (" + string.Join(", ", offered) + ")", offered.SequenceEqual(ages),
       "table has " + string.Join(", ", ages));
    foreach (var c in choices)
    {
        var own = table.Where(r => r.Score == c.Score).SelectMany(r => r.Names).ToList();
        var plain = table.Where(r => r.Score == c.Score && (r.Sub == null || r.Sub == 0)).SelectMany(r => r.Names).ToList();
        Ok(code + " " + c.Score + ": labelled with its own names (" + string.Join(" / ", c.Labels) + ")",
           c.Labels.Count > 0 && c.Labels.All(own.Contains));
        if (plain.Count > 0)
            Ok(code + " " + c.Score + ": and only the plain ones, not the content descriptors",
               c.Labels.All(plain.Contains));
    }
    Ok(code + ": nothing adult or banned", choices.All(c => c.Score < 1000));
}

Console.WriteLine();
Console.WriteLine("── Shapes that are not a table ──────────────────────────────────");
foreach (var bad in new[] { "", "not json", "[]", "{}", "{\"ratings\": 4}" })
{
    List<Jellyfin.Profiles.RatingChoice> r = null;
    bool threw = false;
    try { r = Parse(bad, out _); } catch (Exception) { threw = true; }
    Ok("'" + bad + "' gives nothing, without throwing", !threw && (r == null || r.Count == 0));
}

Console.WriteLine();
Console.WriteLine("── A server with no tables ──────────────────────────────────────");
// The plugin's own assembly carries no rating tables, which is what a future Jellyfin
// that renamed them would look like. The form then falls back to the US list.
var none = (List<Jellyfin.Profiles.RatingChoice>)forCountry.Invoke(null, new object[] { asm, "US", null });
Ok("no tables found means an empty list, not an error", none != null && none.Count == 0);
var blank = (List<Jellyfin.Profiles.RatingChoice>)forCountry.Invoke(null, new object[] { asm, "", null });
Ok("and so does no country", blank != null && blank.Count == 0);
// R-1132: the cache must not hand out its own list, or one caller's edit reaches the next.
var first = (List<Jellyfin.Profiles.RatingChoice>)forCountry.Invoke(null, new object[] { asm, "ZZ", null });
first.Add(new Jellyfin.Profiles.RatingChoice());
var again = (List<Jellyfin.Profiles.RatingChoice>)forCountry.Invoke(null, new object[] { asm, "ZZ", null });
Ok("a caller changing the list it got does not change the cached one", again.Count == 0, "second call returned " + again.Count + " item(s)");

Console.WriteLine();
if (fails.Count > 0)
{
    foreach (var f in fails) Console.WriteLine("   - " + f);
    Console.WriteLine(pass + " passed, " + fails.Count + " failed");
    return 1;
}
Console.WriteLine(pass + " passed, 0 failed");
return 0;
