using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Serialization;
using Jellyfin.Profiles.Configuration;

// ─────────────────────────────────────────────────────────────────────────────
// The server half of themes, against the compiled plugin.
//
//   - which theme an account sees, from its own row, the server default, and whether the
//     server lets profiles choose;
//   - what the create and update routes accept and store;
//   - that a configuration saved before themes existed loads with choosing ON and the
//     default Bonfire, so upgrading changes nobody's screen.
//
// The last one is the one that would be silent: Jellyfin reads PluginConfiguration.xml
// with XmlSerializer, and a missing element keeps the property initializer. Change an
// initializer and every existing server changes with it, with nothing in the log.
// ─────────────────────────────────────────────────────────────────────────────

int pass = 0;
var fails = new List<string>();
void Ok(string name, bool cond, string detail = null)
{
    if (cond) { pass++; Console.WriteLine("  PASS  " + name); }
    else { fails.Add(name); Console.WriteLine("  FAIL  " + name + (detail == null ? "" : "  — " + detail)); }
}

Console.WriteLine("── The theme list ───────────────────────────────────────────────");

var Plain = new PluginConfiguration();
PluginConfiguration Srv(string def, bool allow) => new PluginConfiguration { DefaultTheme = def, AllowProfileThemes = allow };

var expected = new[] { "bonfire", "crimson", "starlight", "harbor", "nightfall", "glass", "harvest", "snowfall", "bloom", "jellyfin-dark", "jellyfin-light", "high-contrast" };
Ok("twelve themes, Bonfire first (" + string.Join(", ", Themes.All) + ")",
   Themes.All.SequenceEqual(expected));
Ok("every id is lowercase, which is what the stylesheet keys on",
   Themes.All.All(id => id == id.ToLowerInvariant()));

foreach (var id in expected)
{
    Ok(id + " is known", Themes.IsKnown(id, Plain));
    Ok(id.ToUpperInvariant() + " is known too, and normalises to " + id,
       Themes.IsKnown(id.ToUpperInvariant(), Plain) && Themes.Normalize(id.ToUpperInvariant(), Plain) == id);
}
foreach (var bad in new[] { null, "", "   ", "default", "neon", "bonfire-dark" })
{
    Ok("'" + (bad ?? "null") + "' is not a theme, and normalises to bonfire",
       !Themes.IsKnown(bad, Plain) && Themes.Normalize(bad, Plain) == Themes.Bonfire);
}

Console.WriteLine();
Console.WriteLine("── Which theme an account sees ─────────────────────────────────");

ProfileMapping Row(string theme) => new ProfileMapping { ProfileUserId = Guid.NewGuid(), Theme = theme };

Ok("its own choice, when choosing is allowed",
   Themes.Resolve(Row("glass"), Srv("crimson", true)) == "glass");
Ok("the server default, when it has not chosen",
   Themes.Resolve(Row(null), Srv("crimson", true)) == "crimson");
Ok("the server default, when choosing is turned off — whatever it chose",
   Themes.Resolve(Row("glass"), Srv("crimson", false)) == "crimson");
Ok("the server default for an account with no row at all",
   Themes.Resolve(null, Srv("harbor", true)) == "harbor");
Ok("a stored choice in another case still counts",
   Themes.Resolve(Row("Starlight"), Srv("crimson", true)) == "starlight");
Ok("a stored id that is no longer a theme falls back to the default, not to nothing",
   Themes.Resolve(Row("retired"), Srv("nightfall", true)) == "nightfall");
Ok("and a broken default falls back to Bonfire",
   Themes.Resolve(Row(null), Srv("nonsense", true)) == Themes.Bonfire);

Console.WriteLine();
Console.WriteLine("── What the create and update routes accept ────────────────────");

var ctrl = typeof(Themes).Assembly.GetType("Jellyfin.Profiles.Controllers.ProfilesController", true);
const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
var validate = ctrl.GetMethod("ValidateTheme", Flags);
var toStore = ctrl.GetMethod("ThemeToStore", Flags);
Ok("ValidateTheme is reachable", validate != null);
Ok("ThemeToStore is reachable", toStore != null);

if (validate != null && toStore != null)
{
    string V(string v) => (string)validate.Invoke(null, new object[] { v, Plain });
    string S(string v) => (string)toStore.Invoke(null, new object[] { v, Plain });

    foreach (var good in new[] { null, "", "glass", "Glass", "CRIMSON" })
        Ok("'" + (good ?? "null") + "' is accepted", V(good) == null);
    var refusal = V("neon");
    Ok("an unknown theme is refused, naming the ones there are (" + refusal + ")",
       refusal != null && expected.All(id => refusal.Contains(id)));

    Ok("\"\" is stored as null — follow the server default", S("") == null);
    Ok("null is stored as null", S(null) == null);
    Ok("a theme is stored lowercase", S("Glass") == "glass");
}

Console.WriteLine();
Console.WriteLine("── Upgrading changes nobody's screen ───────────────────────────");

var fresh = new PluginConfiguration();
Ok("a new server defaults to Bonfire", fresh.DefaultTheme == Themes.Bonfire);
Ok("and lets profiles choose", fresh.AllowProfileThemes);

// A configuration from before themes: no DefaultTheme, no AllowProfileThemes, and a
// mapping with no Theme. Written by serialising a current one and deleting the elements,
// so the rest of the document is exactly what the serializer expects.
var serializer = new XmlSerializer(typeof(PluginConfiguration));
var before = new PluginConfiguration();
before.Mappings.Add(new ProfileMapping { ProfileUserId = Guid.NewGuid(), MasterUserId = Guid.NewGuid(), ProfileName = "Kids" });
string xml;
using (var w = new StringWriter()) { serializer.Serialize(w, before); xml = w.ToString(); }
var old = System.Xml.Linq.XDocument.Parse(xml);
old.Descendants().Where(e => e.Name.LocalName is "DefaultTheme" or "AllowProfileThemes" or "Theme"
       // 1.7.2's lists did not exist then either.
       or "CustomThemes" or "HiddenThemes" or "CustomFonts").ToList()
   .ForEach(e => e.Remove());
Ok("the old document really has no theme elements",
   !old.Descendants().Any(e => e.Name.LocalName.Contains("Theme")));

PluginConfiguration loaded;
using (var r = new StringReader(old.ToString())) loaded = (PluginConfiguration)serializer.Deserialize(r);
Ok("it loads with the default Bonfire", loaded.DefaultTheme == Themes.Bonfire);
Ok("with choosing on", loaded.AllowProfileThemes);
Ok("and its profile following the default", loaded.Mappings.Count == 1 && loaded.Mappings[0].Theme == null);

// And a choice survives the round trip, including through the copy every read takes.
var chosen = new PluginConfiguration { DefaultTheme = Themes.Harbor, AllowProfileThemes = false };
chosen.Mappings.Add(new ProfileMapping { ProfileUserId = Guid.NewGuid(), Theme = Themes.Glass });
using (var w = new StringWriter()) { serializer.Serialize(w, chosen); xml = w.ToString(); }
using (var r = new StringReader(xml)) loaded = (PluginConfiguration)serializer.Deserialize(r);
Ok("the default and the switch are saved", loaded.DefaultTheme == Themes.Harbor && !loaded.AllowProfileThemes);
Ok("and so is a profile's choice", loaded.Mappings[0].Theme == Themes.Glass);
Ok("which the locked read copy keeps", loaded.Mappings[0].CopyForReading().Theme == Themes.Glass);

Console.WriteLine();
Console.WriteLine("── Settings → Theme sets one profile, and only the owner's ────");

// The route behind the Settings page. It exists because update rewrites every field from
// what it is sent; this one must touch the theme and nothing else, and only inside the
// caller's own household.
Guid kidsLibrary = Guid.NewGuid();
Guid master = Guid.NewGuid(), child = Guid.NewGuid(), otherMaster = Guid.NewGuid(), otherChild = Guid.NewGuid();
PluginConfiguration House() => new PluginConfiguration
{
    AllowProfileThemes = true,
    Mappings = new List<ProfileMapping>
    {
        new ProfileMapping { ProfileUserId = master, MasterUserId = master, Theme = null },
        new ProfileMapping { ProfileUserId = child, MasterUserId = master, Theme = "crimson", PinHash = "hash", EnabledFolders = new List<Guid> { kidsLibrary } },
        new ProfileMapping { ProfileUserId = otherMaster, MasterUserId = otherMaster },
        new ProfileMapping { ProfileUserId = otherChild, MasterUserId = otherMaster, Theme = "glass" }
    }
};

var h = House();
Ok("a master sets its own theme", Themes.SetFor(h, master, master, "Nightfall") == ThemeChange.Changed
   && h.Mappings[0].Theme == "nightfall");
Ok("and one of its profiles'", Themes.SetFor(h, master, child, "harbor") == ThemeChange.Changed
   && h.Mappings[1].Theme == "harbor");
Ok("and nothing else on that profile moves", h.Mappings[1].PinHash == "hash"
   && h.Mappings[1].EnabledFolders.SequenceEqual(new[] { kidsLibrary }));
Ok("an empty theme goes back to following the server default",
   Themes.SetFor(h, master, child, "") == ThemeChange.Changed && h.Mappings[1].Theme == null);

h = House();
Ok("another household's profile is refused", Themes.SetFor(h, master, otherChild, "harbor") == ThemeChange.NotInHousehold);
Ok("and left as it was", h.Mappings[3].Theme == "glass");
Ok("another household's master too", Themes.SetFor(h, master, otherMaster, "harbor") == ThemeChange.NotInHousehold
   && h.Mappings[2].Theme == null);
Ok("an account with no row is refused", Themes.SetFor(h, master, Guid.NewGuid(), "harbor") == ThemeChange.NotInHousehold);
Ok("an unknown theme is refused, and stores nothing",
   Themes.SetFor(h, master, child, "neon") == ThemeChange.UnknownTheme && h.Mappings[1].Theme == "crimson");

h = House();
h.AllowProfileThemes = false;
Ok("nothing is set while the server does not let profiles choose",
   Themes.SetFor(h, master, child, "harbor") == ThemeChange.NotAllowed && h.Mappings[1].Theme == "crimson");

Console.WriteLine();
Console.WriteLine("── The theme files, as the web client is sent them ──────────────");
// ThemeCatalog.ClientPayload is what PublishThemes puts into profiles.js. tests/_lib.js
// builds the same thing from the folder for the JS harnesses, and bundle.js holds the
// script side of that to _lib; this holds the server side to the same rules, so the two
// cannot drift into checking a payload nobody is sent: every file, Bonfire first and the
// rest by id, each pattern escaped by Uri.EscapeDataString, nothing outside ASCII.
{
    var root = AppContext.BaseDirectory;
    while (root != null && !File.Exists(Path.Combine(root, "Jellyfin.Profiles.csproj"))) root = Path.GetDirectoryName(root);
    Ok("the repository is found from the harness", root != null);
    if (root != null)
    {
        var dir = Path.Combine(root, "Web", "themes");
        var files = Directory.GetFiles(dir, "*.json").Where(f => Path.GetFileName(f) != "fonts.json").ToList();
        var payloadText = ThemeCatalog.ClientPayload();
        var payload = System.Text.Json.Nodes.JsonNode.Parse(payloadText)!.AsObject();
        var sent = payload["themes"]!.AsArray().Select(n => n!.AsObject()).ToList();
        var ids = sent.Select(t => (string)t["id"]!).ToList();

        Ok("every theme file is sent (" + sent.Count + " of " + files.Count + ")", sent.Count == files.Count && sent.Count >= 9);
        var want = ids.Where(i => i != "bonfire").OrderBy(i => i, StringComparer.Ordinal).Prepend("bonfire").ToList();
        Ok("Bonfire first, then by id (" + string.Join(", ", ids) + ")", ids.SequenceEqual(want));
        foreach (var f in files)
        {
            var file = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(f))!.AsObject();
            var id = (string)file["id"]!;
            var match = sent.FirstOrDefault(t => (string)t["id"]! == id);
            Ok(Path.GetFileName(f) + " is sent exactly as written",
               match != null && System.Text.Json.Nodes.JsonNode.DeepEquals(match, file));
        }
        foreach (var id in Themes.All)
            Ok("the server theme " + id + " has a file", ids.Contains(id));

        var fonts = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "fonts.json")))!["fonts"];
        Ok("the font list is sent as written", System.Text.Json.Nodes.JsonNode.DeepEquals(payload["fonts"], fonts));
        var fontListNode = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "fonts.json")))!;
        Ok("and the subsets' character ranges with it",
           System.Text.Json.Nodes.JsonNode.DeepEquals(payload["subsets"], fontListNode["subsets"])
           && payload["subsets"]!.AsObject().Count == 2);

        var pats = payload["patterns"]!.AsObject();
        var svgs = Directory.GetFiles(Path.Combine(dir, "patterns"), "*.svg");
        Ok("every pattern is sent (" + pats.Count + " of " + svgs.Length + ")", pats.Count == svgs.Length && svgs.Length >= 3);
        foreach (var svg in svgs)
        {
            var name = Path.GetFileNameWithoutExtension(svg);
            Ok(name + " is a data: URL escaped by EscapeDataString",
               (string)pats[name]! == "data:image/svg+xml," + Uri.EscapeDataString(File.ReadAllText(svg).Trim()));
        }
        Ok("nothing outside ASCII reaches the script, so no U+2028 either",
           payloadText.All(ch => ch < 0x80));
        Ok("and nothing that can end the script it sits in", payloadText.IndexOf("</", StringComparison.Ordinal) == -1);

        Console.WriteLine();
        Console.WriteLine("── GET /themes, for other apps ──────────────────────────────────");
        var pubText = ThemeCatalog.PublicPayload(Srv("harbor", false));
        var pub = System.Text.Json.Nodes.JsonNode.Parse(pubText)!.AsObject();
        Ok("it says schema 1", (int)pub["schema"]! == 1);
        Ok("and the server's default and whether profiles may choose, as given",
           (string)pub["default"]! == "harbor" && (bool)pub["profilesCanChoose"]! == false);
        Ok("the same theme files the web client gets",
           System.Text.Json.Nodes.JsonNode.DeepEquals(pub["themes"], payload["themes"]));
        var pubPatterns = pub["patterns"]!.AsArray();
        Ok("each pattern as an address to fetch, not a data: URL",
           pubPatterns.Count == svgs.Length && pubPatterns.All(p =>
               (string)p!["url"]! == "/plugins/profiles/themes/patterns/" + (string)p!["name"]!));
        // The names a font goes by when installed are the web client's business; an app
        // given them would try to match local fonts that differ device to device.
        Ok("fonts carry a family, a fallback, files and a licence when served, and nothing else",
           pub["fonts"]!.AsArray().All(f => f!.AsObject().Select(p => p.Key).OrderBy(k => k)
               .SequenceEqual(f!["files"]!.AsArray().Count > 0
                   ? new[] { "fallback", "family", "files", "licence" } : new[] { "fallback", "family", "files" })));

        Console.WriteLine();
        Console.WriteLine("── The served fonts ─────────────────────────────────────────────");
        var fontDir = Path.Combine(dir, "fonts");
        var servedFonts = pub["fonts"]!.AsArray().Where(f => f!["files"]!.AsArray().Count > 0).ToList();
        Ok("Noto Sans first, and ten served (" + servedFonts.Count + ")",
           servedFonts.Count == 10 && (string)servedFonts[0]!["family"]! == "Noto Sans");
        foreach (var f in servedFonts)
        {
            var family = (string)f!["family"]!;
            var fileEntries = f["files"]!.AsArray();
            Ok(family + ": a latin and a latin-ext file",
               fileEntries.Select(x => (string)x!["subset"]!).SequenceEqual(new[] { "latin", "latin-ext" }));
            foreach (var x in fileEntries)
            {
                var url = (string)x!["url"]!;
                var fileName = url.Substring(url.LastIndexOf('/') + 1);
                var served = ThemeCatalog.FontFile(fileName);
                Ok(fileName + " is served, byte for byte the repository's file, as font/woff2",
                   served != null && served.Value.ContentType == "font/woff2"
                   && served.Value.Bytes.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(fontDir, fileName))));
                Ok(fileName + " carries its subset's characters and its weights",
                   System.Text.Json.Nodes.JsonNode.DeepEquals(x["unicodeRange"], fontListNode["subsets"]![(string)x["subset"]!])
                   && x["weight"]!.AsArray().Count == 2);
            }

            var lic = (string)f["licence"]!;
            var licFile = ThemeCatalog.FontFile(lic.Substring(lic.LastIndexOf('/') + 1));
            Ok(family + "'s licence is served too, and is the OFL",
               licFile != null && licFile.Value.ContentType.StartsWith("text/plain", StringComparison.Ordinal)
               && System.Text.Encoding.UTF8.GetString(licFile.Value.Bytes).Contains("SIL OPEN FONT LICENSE", StringComparison.Ordinal));
        }

        foreach (var bad in new[] { "", "../fonts.json", "fonts.json", "noto-sans-latin", "NOTO-SANS-LATIN.WOFF2", "..%2Ffonts.json", "x.woff2" })
            Ok("no file for \"" + bad + "\"", ThemeCatalog.FontFile(bad) == null);
        Ok("the font route looks the file up, never builds a path from it",
           File.ReadAllText(Path.Combine(root, "Controllers", "ProfilesController.Themes.cs")).Contains("ThemeCatalog.FontFile(file)", StringComparison.Ordinal));
        Ok("and lets a packaged client, whose page is another origin, use the font",
           File.ReadAllText(Path.Combine(root, "Controllers", "ProfilesController.Themes.cs")).Contains("Response.Headers[\"Access-Control-Allow-Origin\"] = \"*\"", StringComparison.Ordinal));
        Ok("every theme it sends passes the validator but for the known entries",
           pub["themes"]!.AsArray().All(t => ThemeCatalog.Validate(t).All(p =>
               (string)t!["id"]! == "bonfire" && p.Path == "colors.onAccent")));
        var tag = ThemeCatalog.ETagFor(pubText);
        Ok("its ETag is a quoted hash", System.Text.RegularExpressions.Regex.IsMatch(tag, "^\"[0-9a-f]{32}\"$"));
        Ok("the same answer, the same ETag", ThemeCatalog.ETagFor(ThemeCatalog.PublicPayload(Srv("harbor", false))) == tag);
        Ok("a new default, a new ETag, so no app keeps the old one",
           ThemeCatalog.ETagFor(ThemeCatalog.PublicPayload(Srv("glass", false))) != tag
           && ThemeCatalog.ETagFor(ThemeCatalog.PublicPayload(Srv("harbor", true))) != tag);

        // The routes themselves: anonymous, but checked by apidocs.js against the table that
        // says so. Here, that they read the catalog and nothing else.
        var ctl = File.ReadAllText(Path.Combine(root, "Controllers", "ProfilesController.Themes.cs"));
        Ok("the pattern route looks the name up, never builds a path from it",
           ctl.Contains("ThemeCatalog.Patterns.TryGetValue(name", StringComparison.Ordinal)
           && !ctl.Contains("Path.Combine", StringComparison.Ordinal) && !ctl.Contains("File.", StringComparison.Ordinal));
        Ok("and serves SVG with a policy that runs no script",
           ctl.Contains("\"default-src 'none'; style-src 'unsafe-inline'\"", StringComparison.Ordinal)
           && ctl.Contains("nosniff", StringComparison.Ordinal));
        Ok("/themes reads the settings inside ConfigLock", System.Text.RegularExpressions.Regex.IsMatch(ctl,
           @"lock \(ConfigLock\)\s*\{\s*var config = Plugin\.Instance\?\.Configuration;"));

        Console.WriteLine();
        Console.WriteLine("── The validator: every built-in passes it ──────────────────────");
        // A problem here is a theme the settings page would refuse to save if an
        // administrator duplicated it unchanged. Known ones are listed with the decision they
        // wait on, and each entry FAILS once the problem is gone, so none outlives its cause.
        // Empty since the 1.7.2 theme review fixed Bonfire's button (white on #00a4dc).
        var known = new Dictionary<string, string>();
        foreach (var f in files)
        {
            var file = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(f))!;
            var id = (string)file["id"]!;
            var problems = ThemeCatalog.Validate(file);
            foreach (var p in problems)
            {
                var key = id + " " + p.Path + " " + p.Code;
                if (known.ContainsKey(key)) continue;
                Ok(id + ": " + p.Path + " (" + p.Code + ") " + p.Detail, false);
            }

            Ok(id + " has no problem the list below does not name",
               problems.All(p => known.ContainsKey(id + " " + p.Path + " " + p.Code)));
        }

        foreach (var k in known)
        {
            var id = k.Key.Split(' ')[0];
            var f = files.FirstOrDefault(x => Path.GetFileNameWithoutExtension(x) == id);
            var still = f != null && ThemeCatalog.Validate(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(f)))
                .Any(p => id + " " + p.Path + " " + p.Code == k.Key);
            Ok("known: " + k.Key + " (" + k.Value + "); remove the entry if it is fixed", still);
        }

        Console.WriteLine();
        Console.WriteLine("── The validator: each broken file is caught, at the value at fault ");
        // Harvest, which passes, broken one way at a time. Each must give the problem named,
        // at the path named: a validator that said "invalid" without saying where would leave
        // an administrator guessing, and one that missed a case lets it through to the screen.
        var harvestText = File.ReadAllText(Path.Combine(dir, "harvest.json"));
        System.Text.Json.Nodes.JsonObject Harvest() => System.Text.Json.Nodes.JsonNode.Parse(harvestText)!.AsObject();
        void Caught(string what, Action<System.Text.Json.Nodes.JsonObject> breakIt, string path, string code)
        {
            var t = Harvest();
            breakIt(t);
            var problems = ThemeCatalog.Validate(t);
            Ok(what + " → " + path + " (" + code + ")", problems.Any(p => p.Path == path && p.Code == code),
               problems.Count == 0 ? "no problems" : string.Join("; ", problems.Select(p => p.Path + " " + p.Code)));
        }

        Ok("Harvest itself passes", ThemeCatalog.Validate(Harvest()).Count == 0);
        Caught("a field schema 1 does not have", t => t["css"] = "body{}", "css", "unknown");
        Caught("one inside a section", t => t["colors"]!["glow"] = "#ffffff", "colors.glow", "unknown");
        Caught("a required colour missing", t => t["colors"]!.AsObject().Remove("accent"), "colors.accent", "missing");
        Caught("a colour that is not one", t => t["colors"]!["ring"] = "orange", "colors.ring", "type");
        Caught("a number given as text", t => t["shape"]!["ringWidth"] = "3", "shape.ringWidth", "type");
        Caught("a number out of range", t => t["focus"]!["scale"] = 3, "focus.scale", "range");
        Caught("an id with capitals and a space", t => t["id"] = "Bad Id", "id", "range");
        Caught("a later schema", t => t["schema"] = 2, "schema", "range");
        Caught("faint secondary text", t => t["colors"]!["text2"] = "#ffffff40", "colors.text2", "contrast");
        // One layer whose LAST stop is white: the fill and the other stops are fine, so only
        // a check of every stop on its own sees it.
        Caught("a gradient unreadable only at one end",
               t => t["background"]!["layers"]![0]!["stops"]![2]!["color"] = "#f0f0f0", "colors.text1", "contrast");
        Caught("a name too faint at its opacity",
               t => t["type"]!["name"]!["opacity"] = 0.5, "colors.name", "contrast");
        Caught("button text too close to the hovered accent only",
               t => t["colors"]!["accentFocus"] = "#5a2a10", "colors.onAccent", "contrast");
        Caught("second-button text on its see-through fill",
               t => t["parts"]!["button2"]!["text"] = "#a07040", "parts.button2.text", "contrast");
        Caught("an error colour that is blue", t => t["colors"]!["danger"] = "#3366ff", "colors.danger", "hue");
        Caught("a warning colour that is green", t => t["colors"]!["warning"] = "#33cc33", "colors.warning", "hue");
        Caught("a font Bonfire does not serve", t => t["type"]!["font"]!["family"] = "Comic Sans MS", "type.font.family", "font");
        Caught("a pattern Bonfire does not hold", t => t["decorations"]![0]!["image"] = "skulls", "decorations[0].image", "pattern");
        Caught("a decoration schema 1 does not know",
               t => t["decorations"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject { ["name"] = "sparkle" }), "decorations[1].name", "range");
        Caught("an address in the name", t => t["name"] = new System.Text.Json.Nodes.JsonObject { ["text"] = "https://example.com" }, "name.text", "address");
        Caught("an address in a field nothing reads",
               t => t["colors"]!["glow"] = "url(//x)", "colors.glow", "address");
        Caught("a name with a key and a text", t => t["name"]!["text"] = "Mine", "name", "missing");
        Caught("a see-through background", t => t["background"]!["fill"] = "#30170b80", "background.fill", "range");
        Caught("a file too big to be a theme",
               t => t["aliases"] = new System.Text.Json.Nodes.JsonArray(Enumerable.Range(0, 7000).Select(i => (System.Text.Json.Nodes.JsonNode)("alias-" + i)).ToArray()), "", "size");
        Caught("High contrast held to 7:1 where others need 4.5",
               t => { t["id"] = ThemeValidator.HighContrastId; t["colors"]!["text2"] = "#ffffff99"; }, "colors.text2", "contrast");
        var notHigh = Harvest();
        notHigh["colors"]!["text2"] = "#ffffff99";
        Ok("and the same text2 passes for any other theme", !ThemeCatalog.Validate(notHigh).Any(p => p.Path == "colors.text2"));
        Ok("not an object at all", ThemeCatalog.Validate(System.Text.Json.Nodes.JsonNode.Parse("[1]")).Any(p => p.Code == "type"));
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
