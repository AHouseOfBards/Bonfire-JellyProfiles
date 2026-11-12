using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Profiles.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

// ─────────────────────────────────────────────────────────────────────────────
// The theme editor's server half (1.7.2, beta 8): an administrator's own themes, the
// built-ins they hide, and the fonts they upload.
//
//  - Which themes are offered, from the built-ins, the hidden list and the custom list.
//    Bonfire can never be hidden; a hidden or deleted theme sends its profiles to the
//    default and keeps their choice.
//  - What a custom theme may be: everything a built-in must pass, plus an id no built-in
//    answers to, a name in text, no aliases, and only fonts the server has.
//  - That a custom theme is checked on the way out too, so a hand-edited configuration
//    cannot reach a client.
//  - The WOFF2 reader: every built-in font reads back as its own family and weights, and
//    anything else is refused with InvalidDataException and nothing else, whatever bytes
//    it is handed — including a file that decompresses without end.
//  - Every route, end to end, on a real Plugin with stub paths, as an API key (which
//    RequireAdministrator lets through) and as nobody (which it refuses).
//
// Against f432be3 this does not compile: none of it exists.
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

var root = AppContext.BaseDirectory;
while (root != null && !File.Exists(Path.Combine(root, "Jellyfin.Profiles.csproj"))) root = Path.GetDirectoryName(root);
if (root == null) throw new Exception("could not find the repository root");
var themesDir = Path.Combine(root, "Web", "themes");

JsonObject ThemeFile(string id) => JsonNode.Parse(File.ReadAllText(Path.Combine(themesDir, id + ".json")))!.AsObject();

// A built-in made into a custom theme: a new id and its name as text.
JsonObject Custom(string id, string from = "harvest")
{
    var t = ThemeFile(from);
    t["id"] = id;
    t["name"] = new JsonObject { ["text"] = "My " + id };
    return t;
}

Console.WriteLine("── Which themes are offered ─────────────────────────────────────");

var cfg = new PluginConfiguration();
cfg.HiddenThemes = new List<string> { "glass", "bonfire" };
cfg.CustomThemes = new List<CustomTheme> { new CustomTheme { Id = "mine", Json = Custom("mine").ToJsonString() } };

Ok("a hidden built-in is not offered", !Themes.IsKnown("glass", cfg) && !Themes.IsKnown("Glass", cfg));
Ok("but it is still a built-in", Themes.IsBuiltIn("glass"));
Ok("Bonfire is offered even when the list names it", Themes.IsKnown("bonfire", cfg));
Ok("a custom theme is offered", Themes.IsKnown("mine", cfg) && Themes.Normalize("MINE", cfg) == "mine");
Ok("a custom id another server has is not", !Themes.IsKnown("yours", cfg) && Themes.Normalize("yours", cfg) == Themes.Bonfire);
var offered = Themes.Offered(cfg);
Ok("the list: Bonfire first, built-ins less the hidden one, then custom (" + string.Join(", ", offered) + ")",
   offered.SequenceEqual(Themes.All.Where(id => id != "glass").Append("mine")));

ProfileMapping Row(string theme) => new ProfileMapping { ProfileUserId = Guid.NewGuid(), Theme = theme };
cfg.DefaultTheme = "harbor";
var chose = Row("glass");
Ok("a profile that chose a hidden theme sees the default", Themes.Resolve(chose, cfg) == "harbor");
Ok("and keeps its choice, for if the theme comes back", chose.Theme == "glass");
cfg.HiddenThemes.Clear();
Ok("which it then sees again", Themes.Resolve(chose, cfg) == "glass");
Ok("a profile can see a custom theme", Themes.Resolve(Row("mine"), cfg) == "mine");
cfg.DefaultTheme = "mine";
Ok("which can be the default", Themes.Resolve(Row(null), cfg) == "mine");
cfg.DefaultTheme = "glass";
cfg.HiddenThemes.Add("glass");
Ok("a hidden default falls back to Bonfire, not to nothing", Themes.Resolve(Row(null), cfg) == Themes.Bonfire);
Ok("a profile cannot choose a hidden theme",
   Themes.SetFor(new PluginConfiguration { HiddenThemes = new List<string> { "glass" }, Mappings = new List<ProfileMapping> { new ProfileMapping { ProfileUserId = Guid.Empty, MasterUserId = Guid.Empty } } },
       Guid.Empty, Guid.Empty, "glass") == ThemeChange.UnknownTheme);

Console.WriteLine();
Console.WriteLine("── What a custom theme may be ───────────────────────────────────");

var plain = new PluginConfiguration();
Ok("a built-in with a new id and a text name passes", ThemeCatalog.ValidateCustom(Custom("my-harvest"), plain).Count == 0,
   string.Join("; ", ThemeCatalog.ValidateCustom(Custom("my-harvest"), plain).Select(p => p.Path + " " + p.Detail)));
foreach (var id in Themes.All)
{
    var clash = ThemeCatalog.ValidateCustom(Custom(id), plain);
    Ok("the id " + id + " is a built-in's, and refused", clash.Any(p => p.Path == "id" && p.Code == "taken"));
}
var keyed = Custom("keyed");
keyed["name"] = new JsonObject { ["key"] = "theme.keyed" };
Ok("a name given as a key is refused: nobody wrote its translation",
   ThemeCatalog.ValidateCustom(keyed, plain).Any(p => p.Path == "name.key"));
var aliased = Custom("aliased");
aliased["aliases"] = new JsonArray("harbor");
Ok("aliases are refused", ThemeCatalog.ValidateCustom(aliased, plain).Any(p => p.Path == "aliases"));
// "There should be no restrictions on custom made themes" (the owner, 2026-11-12): no
// contrast rule applies to one. This was "text the colour of its surface is refused".
var bad = Custom("unreadable");
bad["colors"]!["text1"] = bad["colors"]!["surface1"]!.DeepClone();
bad["colors"]!["onAccent"] = bad["colors"]!["accent"]!.DeepClone();
bad["colors"]!["ring"] = bad["background"]!["fill"]!.DeepClone();
bad["colors"]!["danger"] = "#3a0000";
bad["parts"]!["button2"]!["text"] = bad["parts"]!["button2"]!["fill"]!.DeepClone();
var badProblems = ThemeCatalog.ValidateCustom(bad, plain);
Ok("a custom theme with no contrast anywhere passes: text on its surface, its accent, ring, danger and button",
   badProblems.Count == 0, string.Join("; ", badProblems.Select(p => p.Path + " " + p.Code + " " + p.Detail)));
Ok("while the built-ins are still held to contrast: the same colours on a built-in fail",
   ThemeCatalog.Validate(bad).Any(p => p.Code == "contrast"));
// Nor the hue rule (the owner, 2026-11-12): a custom theme's error colour may be blue and its
// warning green. The built-ins still must read as red and amber.
var hues = Custom("cool-alerts");
hues["colors"]!["danger"] = "#3366ff";
hues["colors"]!["warning"] = "#33cc33";
var hueProblems = ThemeCatalog.ValidateCustom(hues, plain);
Ok("a custom theme with a blue error colour and a green warning passes",
   hueProblems.Count == 0, string.Join("; ", hueProblems.Select(p => p.Path + " " + p.Code + " " + p.Detail)));
Ok("while a built-in with the same colours fails on hue",
   ThemeCatalog.Validate(hues).Count(p => p.Code == "hue") == 2);
// Everything that is not contrast or hue still applies.
JsonObject SetId(JsonObject t, string id) { var c = t.DeepClone().AsObject(); c["id"] = id; return c; }
var broken = Custom("broken");
broken["sparkle"] = true;
broken["colors"]!["panel"] = "#20202080";
var brokenProblems = ThemeCatalog.ValidateCustom(broken, plain);
Ok("an unknown field is still refused", brokenProblems.Any(p => p.Path == "sparkle" && p.Code == "unknown"));
Ok("and a see-through panel, a range rule", brokenProblems.Any(p => p.Path == "colors.panel" && p.Code == "range"));
Ok("not an object is refused", ThemeCatalog.ValidateCustom(JsonValue.Create(3), plain).Count > 0
   && ThemeCatalog.ValidateCustom(null, plain).Count > 0);

var fonted = Custom("fonted");
fonted["type"]!["font"]!["family"] = "Harness Sans";
Ok("a font the server does not have is refused", ThemeCatalog.ValidateCustom(fonted, plain).Any(p => p.Path == "type.font.family"));
var withFont = new PluginConfiguration { CustomFonts = new List<CustomFont> { new CustomFont { Family = "Harness Sans", File = new string('a', 32) + ".woff2" } } };
Ok("and accepted once it is uploaded", ThemeCatalog.ValidateCustom(fonted, withFont).Count == 0);

Console.WriteLine();
Console.WriteLine("── Checked on the way out too ───────────────────────────────────");

var hand = new PluginConfiguration
{
    CustomThemes = new List<CustomTheme>
    {
        new CustomTheme { Id = "good", Json = Custom("good").ToJsonString() },
        new CustomTheme { Id = "broken", Json = "{ not json" },
        new CustomTheme { Id = "liar", Json = Custom("other").ToJsonString() },
        new CustomTheme { Id = "sparkle", Json = SetId(broken, "sparkle").ToJsonString() },
        new CustomTheme { Id = "fonted", Json = fonted.ToJsonString() },
        new CustomTheme { Id = "good", Json = Custom("good", "crimson").ToJsonString() },
        // No contrast rules for a custom theme: this one was left out until 2026-11-12.
        new CustomTheme { Id = "unreadable", Json = bad.ToJsonString() },
    },
};
var served = ThemeCatalog.ValidCustomThemes(hand).Select(t => (string)t["id"]!).ToList();
Ok("only the valid ones are served, low contrast included (" + string.Join(", ", served) + ")", served.SequenceEqual(new[] { "good", "unreadable" }));
Ok("the first of two with one id", (string)ThemeCatalog.ValidCustomThemes(hand)[0]["background"]!["fill"]! == (string)ThemeFile("harvest")["background"]!["fill"]!);
hand.CustomFonts = withFont.CustomFonts;
Ok("a theme whose font is uploaded is served", ThemeCatalog.ValidCustomThemes(hand).Any(t => (string)t["id"]! == "fonted"));
hand.CustomFonts = new List<CustomFont>();
Ok("and dropped again when the font goes, though the text did not change",
   !ThemeCatalog.ValidCustomThemes(hand).Any(t => (string)t["id"]! == "fonted"));

// r2-20-03: these objects are shared by concurrent anonymous requests, and a parsed JsonNode
// builds its children on first read. Every object and array in the shared trees must already
// be built. The detector is proved first: a node straight from Parse is not.
bool Built(JsonNode n)
{
    var flag = BindingFlags.NonPublic | BindingFlags.Instance;
    if (n is JsonObject o)
    {
        var f = typeof(JsonObject).GetFields(flag).First(x => x.Name.ToLowerInvariant().Contains("dictionary"));
        if (f.GetValue(o) == null) return false;
        return o.All(p => p.Value == null || Built(p.Value));
    }
    if (n is JsonArray a)
    {
        var f = typeof(JsonArray).GetFields(flag).First(x => x.Name == "_list");
        if (f.GetValue(a) == null) return false;
        return a.All(i => i == null || Built(i));
    }
    return true;
}
Ok("the detector sees a freshly parsed node as unbuilt", !Built(JsonNode.Parse("{\"a\":{\"b\":[1]}}")!));
Ok("every built-in theme is fully built before it is shared", ThemeCatalog.BuiltIn.All(t => Built(t)));
var sharedCfg = new PluginConfiguration { CustomThemes = new List<CustomTheme> { new CustomTheme { Id = "mine", Json = Custom("mine").ToJsonString() } } };
Ok("and so is a cached custom theme", ThemeCatalog.ValidCustomThemes(sharedCfg).Count == 1 && ThemeCatalog.ValidCustomThemes(sharedCfg).All(t => Built(t)));

Console.WriteLine();
Console.WriteLine("── GET /themes with custom and hidden themes ────────────────────");

var srv = new PluginConfiguration
{
    DefaultTheme = "mine",
    HiddenThemes = new List<string> { "glass", "bonfire", "nonsense" },
    CustomThemes = new List<CustomTheme> { new CustomTheme { Id = "mine", Json = Custom("mine").ToJsonString() } },
    CustomFonts = new List<CustomFont> { new CustomFont { Family = "Harness Sans", File = new string('b', 32) + ".woff2", WeightMin = 300, WeightMax = 700 } },
};
var pub = JsonNode.Parse(ThemeCatalog.PublicPayload(srv))!.AsObject();
var pubIds = pub["themes"]!.AsArray().Select(t => (string)t!["id"]!).ToList();
Ok("the hidden built-in is left out", !pubIds.Contains("glass"));
Ok("every other built-in is in, then the custom one (" + string.Join(", ", pubIds) + ")",
   pubIds.SequenceEqual(ThemeCatalog.BuiltInIds.Where(id => id != "glass").Append("mine")));
Ok("the default can be a custom theme", (string)pub["default"]! == "mine");
Ok("hidden lists the hidden built-in, never Bonfire or a name that is no theme",
   pub["hidden"]!.AsArray().Select(n => (string)n!).SequenceEqual(new[] { "glass" }));
var upFont = pub["fonts"]!.AsArray().FirstOrDefault(f => (string)f!["family"]! == "Harness Sans");
Ok("the uploaded font is listed after the built-in ones", upFont != null && pub["fonts"]!.AsArray().Last() == upFont);
Ok("as one file with no subset, at its address, with its weights",
   upFont != null && upFont["files"]!.AsArray().Count == 1
   && upFont["files"]![0]!["subset"] == null && upFont["files"]![0]!["unicodeRange"] == null
   && (string)upFont["files"]![0]!["url"]! == "/plugins/profiles/themes/fonts/" + new string('b', 32) + ".woff2"
   && (int)upFont["files"]![0]!["weight"]![0]! == 300 && (int)upFont["files"]![0]!["weight"]![1]! == 700);

var part = JsonNode.Parse(ThemeCatalog.PublicPayload(srv, customOnly: true))!.AsObject();
Ok("?part=custom carries only the custom theme", part["themes"]!.AsArray().Select(t => (string)t!["id"]!).SequenceEqual(new[] { "mine" }));
Ok("and only the uploaded font", part["fonts"]!.AsArray().Select(f => (string)f!["family"]!).SequenceEqual(new[] { "Harness Sans" }));
Ok("and the default and the hidden list, which the web client needs to draw its picker",
   (string)part["default"]! == "mine" && part["hidden"]!.AsArray().Count == 1);
Ok("with no custom themes, the built-in answer is what it was",
   JsonNode.DeepEquals(JsonNode.Parse(ThemeCatalog.PublicPayload(new PluginConfiguration()))!["themes"],
       new JsonArray(ThemeCatalog.BuiltIn.Select(t => (JsonNode)t.DeepClone()).ToArray())));
var withScript = new PluginConfiguration
{
    CustomThemes = new List<CustomTheme> { new CustomTheme { Id = "x", Json = Custom("x").ToJsonString() } },
};
var xs = Custom("x");
xs["name"] = new JsonObject { ["text"] = "</script>\u2028" };
withScript.CustomThemes[0].Json = xs.ToJsonString();
var escaped = ThemeCatalog.PublicPayload(withScript, customOnly: true);
Ok("a custom name reaches the client escaped: nothing outside ASCII, no </",
   escaped.Contains("\\u003C/script\\u003E", StringComparison.OrdinalIgnoreCase) && escaped.All(ch => ch < 0x80)
   && !escaped.Contains("</", StringComparison.Ordinal));

Console.WriteLine();
Console.WriteLine("── The WOFF2 reader, on every font Bonfire ships ────────────────");

var fontList = JsonNode.Parse(File.ReadAllText(Path.Combine(themesDir, "fonts.json")))!["fonts"]!.AsArray();
foreach (var f in fontList.Where(f => f!["files"] is JsonObject))
{
    var family = (string)f!["family"]!;
    var latin = File.ReadAllBytes(Path.Combine(themesDir, "fonts", (string)f["files"]!["latin"]!));
    Woff2Info info = null;
    string err = null;
    try { info = Woff2Font.Read(latin); } catch (Exception e) { err = e.GetType().Name + ": " + e.Message; }
    // A variable font from Google's subsetter names its default instance, not its family:
    // Outfit's file says "Outfit Thin", DM Sans's "DM Sans 9pt". The reader reports what
    // the file says, which is why the upload route lets the administrator name it.
    Ok(family + " reads as itself, or its default instance (" + info?.Family + ")",
       info != null && (info.Family == family || info.Family.StartsWith(family + " ", StringComparison.Ordinal)), err ?? info?.Family);
    Ok(family + " spans " + f["weight"]![0] + "–" + f["weight"]![1],
       info != null && info.WeightMin == (int)f["weight"]![0]! && info.WeightMax == (int)f["weight"]![1]!,
       info == null ? err : info.WeightMin + "–" + info.WeightMax);

    // The latin-ext half holds no A to Z: a font that cannot draw a name.
    var ext = File.ReadAllBytes(Path.Combine(themesDir, "fonts", (string)f["files"]!["latin-ext"]!));
    string extErr = null;
    try { Woff2Font.Read(ext); } catch (InvalidDataException e) { extErr = e.Message; }
    Ok(family + "'s latin-ext file is refused: it has no Latin letters", extErr != null && extErr.Contains("Latin"), extErr);
}

Console.WriteLine();
Console.WriteLine("── The WOFF2 reader, on files made to be wrong ──────────────────");

// A WOFF2 font built here, table by table, so each test changes one thing.
byte[] Font(string family = "Harness Sans", bool lower = true, int weight = 400, (int Min, int Max)? axis = null,
             uint? flavor = null, int lengthDelta = 0, byte[] extra = null, string extraTag = "zzzz", int os2Version = 0)
{
    var tables = new List<(string Tag, byte[] Data)>
    {
        ("cmap", Cmap(lower)),
        ("head", new byte[54]),
        ("name", Name(family)),
        ("OS/2", OS2(weight)),
    };
    if (axis != null) tables.Add(("fvar", Fvar(axis.Value.Min, axis.Value.Max)));
    if (extra != null) tables.Add((extraTag, extra));

    var dir = new List<byte>();
    foreach (var (tag, data) in tables)
    {
        var version = tag == "OS/2" ? os2Version : 0;
        dir.Add((byte)(63 | (version << 6)));
        dir.AddRange(Encoding.ASCII.GetBytes(tag));
        dir.AddRange(Base128((uint)data.Length));
        if (version != 0) dir.AddRange(Base128((uint)data.Length));
    }
    var raw = tables.SelectMany(t => t.Data).ToArray();
    byte[] packed;
    using (var ms = new MemoryStream())
    {
        using (var b = new BrotliStream(ms, CompressionLevel.Fastest, leaveOpen: true)) b.Write(raw, 0, raw.Length);
        packed = ms.ToArray();
    }

    var total = 48 + dir.Count + packed.Length;
    var head = new byte[48];
    Encoding.ASCII.GetBytes("wOF2").CopyTo(head, 0);
    Put32(head, 4, flavor ?? 0x00010000);
    Put32(head, 8, (uint)(total + lengthDelta));
    Put16(head, 12, (ushort)tables.Count);
    Put32(head, 16, (uint)(12 + raw.Length));
    Put32(head, 20, (uint)packed.Length);
    return head.Concat(dir).Concat(packed).ToArray();
}

byte[] Cmap(bool lower)
{
    var ranges = new List<(int Start, int End)> { (0x30, 0x39), (0x41, 0x5A) };
    if (lower) ranges.Add((0x61, 0x7A));
    ranges.Add((0xFFFF, 0xFFFF));
    var seg = ranges.Count;
    var sub = new List<byte>();
    void U16(int v) { sub.Add((byte)(v >> 8)); sub.Add((byte)v); }
    U16(4); U16(16 + seg * 8); U16(0); U16(seg * 2); U16(0); U16(0); U16(0);
    foreach (var r in ranges) U16(r.End);
    U16(0);
    foreach (var r in ranges) U16(r.Start);
    foreach (var r in ranges) U16(r.Start == 0xFFFF ? 1 : (1 - r.Start) & 0xffff);
    foreach (var _ in ranges) U16(0);
    var head = new byte[12];
    Put16(head, 2, 1); Put16(head, 4, 3); Put16(head, 6, 1); Put32(head, 8, 12);
    return head.Concat(sub).ToArray();
}

byte[] Name(string family)
{
    var s = Encoding.BigEndianUnicode.GetBytes(family);
    var t = new byte[6 + 12];
    Put16(t, 2, 1); Put16(t, 4, 18);
    Put16(t, 6, 3); Put16(t, 8, 1); Put16(t, 10, 0x409); Put16(t, 12, 1); Put16(t, 14, (ushort)s.Length); Put16(t, 16, 0);
    return t.Concat(s).ToArray();
}

byte[] OS2(int weight) { var t = new byte[96]; Put16(t, 4, (ushort)weight); return t; }

byte[] Fvar(int min, int max)
{
    var t = new byte[16 + 20];
    Put16(t, 0, 1); Put16(t, 4, 16); Put16(t, 6, 2); Put16(t, 8, 1); Put16(t, 10, 20);
    Encoding.ASCII.GetBytes("wght").CopyTo(t, 16);
    Put32(t, 20, (uint)(min << 16)); Put32(t, 24, (uint)(400 << 16)); Put32(t, 28, (uint)(max << 16));
    return t;
}

IEnumerable<byte> Base128(uint v)
{
    var parts = new List<byte>();
    do { parts.Insert(0, (byte)(v & 0x7f)); v >>= 7; } while (v > 0);
    for (var i = 0; i < parts.Count - 1; i++) parts[i] |= 0x80;
    return parts;
}

void Put16(byte[] b, int at, ushort v) { b[at] = (byte)(v >> 8); b[at + 1] = (byte)v; }
void Put32(byte[] b, int at, uint v) { b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v; }

string Refusal(byte[] file)
{
    try { Woff2Font.Read(file); return null; }
    catch (InvalidDataException e) { return e.Message; }
    catch (Exception e) { return "WRONG EXCEPTION " + e.GetType().Name + ": " + e.Message; }
}

// R-1081: a table listed twice, and a transform version the format does not define.
var dupRefusal = Refusal(Font(extra: Name("Harness Sans"), extraTag: "name"));
Ok("a table listed twice is refused (R-1081)", dupRefusal != null && !dupRefusal.StartsWith("WRONG"));
var verRefusal = Refusal(Font(os2Version: 2));
Ok("a transform version the format does not define is refused (R-1081)", verRefusal != null && !verRefusal.StartsWith("WRONG"));

var made = Woff2Font.Read(Font());
Ok("the made font reads: its family, weight 400 from OS/2", made.Family == "Harness Sans" && made.WeightMin == 400 && made.WeightMax == 400);
var heavy = Woff2Font.Read(Font(weight: 700));
Ok("a static bold reads as 700 to 700", heavy.WeightMin == 700 && heavy.WeightMax == 700);
var variable = Woff2Font.Read(Font(axis: (200, 800)));
Ok("a variable font reads its wght axis, 200 to 800", variable.WeightMin == 200 && variable.WeightMax == 800);

foreach (var (label, file, says) in new (string, byte[], string)[]
{
    ("no lowercase letters", Font(lower: false), "Latin"),
    ("a quote in the family name", Font(family: "Evil\"}body{"), "name"),
    ("a semicolon in the family name", Font(family: "a;b"), "name"),
    ("an empty family name", Font(family: ""), "name"),
    ("a family name of 65 letters", Font(family: new string('a', 65)), "name"),
    ("a font collection", Font(flavor: 0x74746366), "collection"),
    ("a length that is not the file's", Font(lengthDelta: 1), "incomplete"),
    ("a TrueType file, not WOFF2", new byte[] { 0, 1, 0, 0 }.Concat(new byte[200]).ToArray(), "WOFF2"),
    ("a WOFF (1) file", Encoding.ASCII.GetBytes("wOFF").Concat(new byte[200]).ToArray(), "WOFF2"),
    ("nothing", new byte[0], "WOFF2"),
    ("tables that decompress past 32 MB", Font(extra: new byte[33 * 1024 * 1024]), "too large"),
})
{
    var r = Refusal(file);
    Ok("refused: " + label + " (" + r + ")", r != null && !r.StartsWith("WRONG", StringComparison.Ordinal) && r.Contains(says, StringComparison.OrdinalIgnoreCase));
}

var good = Font();
var cut = good.Take(good.Length - 9).ToArray();
Put32(cut, 8, (uint)cut.Length);
var cutR = Refusal(cut);
Ok("refused: a file cut short, its length field made to match (" + cutR + ")", cutR != null && !cutR.StartsWith("WRONG", StringComparison.Ordinal));

// Whatever the bytes, the only exception is InvalidDataException: the route turns that
// into a 400, and anything else would be a 500 with a stack trace in the log.
var rng = new Random(1729);
var real = File.ReadAllBytes(Path.Combine(themesDir, "fonts", "noto-sans-latin.woff2"));
var wrongType = new List<string>();
for (var i = 0; i < 400; i++)
{
    var copy = (i % 2 == 0 ? real : good).ToArray();
    var flips = 1 + rng.Next(4);
    // Mostly the header and directory, where a reader trusts what it is told.
    for (var k = 0; k < flips; k++) copy[rng.Next(Math.Min(copy.Length, i % 4 < 2 ? 120 : copy.Length))] = (byte)rng.Next(256);
    var r = Refusal(copy);
    if (r != null && r.StartsWith("WRONG", StringComparison.Ordinal)) wrongType.Add(r);
}
Ok("400 damaged files: each read or refused, never another exception", wrongType.Count == 0,
   string.Join(" | ", wrongType.Distinct().Take(5)));

foreach (var (name, ok) in new[] { ("Noto Sans", true), ("Harness_Sans-2.0", true), ("Ünïcode", true), (" lead", false), ("a\"b", false), ("a\\b", false), ("a{b", false), ("a\nb", false), ("a<b", false) })
    Ok("family \"" + name.Replace("\n", "\\n") + "\" is " + (ok ? "accepted" : "refused"), Woff2Font.IsSafeFamily(name) == ok);

Console.WriteLine();
Console.WriteLine("── Uploaded font names ──────────────────────────────────────────");

var fileName = ThemeCatalog.CustomFontFileName(good);
Ok("named by its hash: 32 hex digits and .woff2 (" + fileName + ")", ThemeCatalog.IsCustomFontFileName(fileName));
Ok("the same bytes, the same name", ThemeCatalog.CustomFontFileName(Font()) == fileName);
foreach (var badName in new[] { null, "", "../x.woff2", new string('a', 32) + ".woff2/..", new string('A', 32) + ".woff2", new string('a', 31) + ".woff2", "noto-sans-latin.woff2", new string('a', 32) + ".ttf" })
    Ok("not an upload's name: \"" + badName + "\"", !ThemeCatalog.IsCustomFontFileName(badName));
Ok("an upload the configuration does not list is not served, even if its file exists",
   ThemeCatalog.CustomFontFile(fileName, new PluginConfiguration()) == null);

Console.WriteLine();
Console.WriteLine("── The routes, on a real plugin ─────────────────────────────────");

var asm = typeof(ThemeCatalog).Assembly;
var tempDir = Path.Combine(Path.GetTempPath(), "bonfire-themeeditor-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
var pluginType = asm.GetType("Jellyfin.Profiles.Plugin", true);
Activator.CreateInstance(pluginType, new StubPaths(tempDir), new StubXml());
var live = (PluginConfiguration)pluginType.GetProperty("Configuration").GetValue(pluginType.GetProperty("Instance").GetValue(null));
// A new install starts with Bonfire off (2.0), and off every gate stands down. This harness
// is about a server where it is on.
live.BonfireOn = true;
Ok("the plugin stands up, its fonts folder under the stub data path",
   live != null && ThemeCatalog.CustomFontFolder.StartsWith(tempDir, StringComparison.Ordinal));

// R-1078: GET /themes is anonymous and re-checks every custom theme it has not seen, so it
// must not do that while holding the lock an administrator's save needs.
{
    var forThemes = typeof(PluginConfiguration).GetMethod("SnapshotForThemes");
    Ok("a snapshot for GET /themes exists", forThemes != null);
    var forReading = (PluginConfiguration)typeof(PluginConfiguration).GetMethod("SnapshotForReading").Invoke(live, null);
    Ok("SnapshotForReading copies the theme and font lists, which the payload reads",
       !ReferenceEquals(forReading.CustomThemes, live.CustomThemes) && !ReferenceEquals(forReading.CustomFonts, live.CustomFonts)
       && !ReferenceEquals(forReading.HiddenThemes, live.HiddenThemes));
    if (forThemes != null)
    {
        live.CustomThemes.Add(new CustomTheme { Id = "snap", Json = "{}" });
        var snap = (PluginConfiguration)forThemes.Invoke(live, null);
        live.CustomThemes.Add(new CustomTheme { Id = "after", Json = "{}" });
        live.HiddenThemes.Add("after");
        Ok("a save after the snapshot does not reach it",
           snap.CustomThemes.Count == live.CustomThemes.Count - 1 && !snap.HiddenThemes.Contains("after"));
        live.CustomThemes.Clear();
        live.HiddenThemes.Clear();
    }

    var src = File.ReadAllText(Path.Combine(root, "Controllers", "ProfilesController.Themes.cs"));
    var start = src.IndexOf("public ActionResult GetThemes", StringComparison.Ordinal);
    var lockAt = src.IndexOf("lock (ConfigLock)", start, StringComparison.Ordinal);
    var open = src.IndexOf('{', lockAt);
    var depth = 0;
    var close = open;
    for (; close < src.Length; close++)
    {
        if (src[close] == '{') depth++;
        else if (src[close] == '}' && --depth == 0) break;
    }
    var inLock = src.Substring(open, close - open);
    Ok("GET /themes builds its payload outside ConfigLock", start > 0 && lockAt > start && !inLock.Contains("PublicPayload"));
}

var ctrlType = asm.GetType("Jellyfin.Profiles.Controllers.ProfilesController", true);
var ctor =ctrlType.GetConstructors().First();
var ctorArgs = ctor.GetParameters().Select(p => p.ParameterType.Name.StartsWith("ILogger")
    ? Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(ctrlType)) : null).ToArray();

// An API key: what Jellyfin's own handler puts on one, and what RequireAdministrator lets
// through without asking the user manager, which a harness does not have.
ControllerBase As(bool admin, byte[] body = null)
{
    var c = (ControllerBase)ctor.Invoke(ctorArgs);
    var http = new DefaultHttpContext();
    if (admin)
    {
        http.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("Jellyfin-IsApiKey", "True"),
            new Claim(ClaimTypes.Role, "Administrator"),
        }, "Test"));
    }
    if (body != null) { http.Request.Body = new MemoryStream(body); http.Request.ContentLength = body.Length; }
    c.ControllerContext = new ControllerContext { HttpContext = http };
    return c;
}

object Call(ControllerBase c, string method, params object[] args)
{
    var m = ctrlType.GetMethod(method);
    var r = m.Invoke(c, args);
    if (r is System.Threading.Tasks.Task t) { t.GetAwaiter().GetResult(); r = t.GetType().GetProperty("Result").GetValue(t); }
    return r;
}
int Status(object r) => r switch { ObjectResult o => o.StatusCode ?? 200, StatusCodeResult s => s.StatusCode, _ => -1 };
JsonNode Body(object r) => r is ObjectResult o && o.Value != null
    ? (o.Value is string s ? JsonValue.Create(s) : JsonNode.Parse(JsonSerializer.Serialize(o.Value))) : null;
JsonElement Json(JsonNode n) => JsonDocument.Parse(n.ToJsonString()).RootElement;

var routes = new (string Method, string Verb, string Template, Func<object[]> Args)[]
{
    ("GetThemeManagement", "HttpGetAttribute", "admin/themes", () => Array.Empty<object>()),
    ("ValidateCustomTheme", "HttpPostAttribute", "admin/themes/validate", () => new object[] { Json(Custom("v")) }),
    ("SaveCustomTheme", "HttpPutAttribute", "admin/themes/custom/{id}", () => new object[] { "v", Json(Custom("v")) }),
    ("DeleteCustomTheme", "HttpDeleteAttribute", "admin/themes/custom/{id}", () => new object[] { "v" }),
    ("SetHiddenThemes", "HttpPutAttribute", "admin/themes/hidden", () => new object[] { Json(new JsonObject { ["hidden"] = new JsonArray() }) }),
    ("UploadThemeFont", "HttpPostAttribute", "admin/themes/fonts", () => new object[] { "sans-serif", true, null }),
    ("DeleteThemeFont", "HttpDeleteAttribute", "admin/themes/fonts/{file}", () => new object[] { fileName }),
};
foreach (var (method, verb, template, argsFor) in routes)
{
    var m = ctrlType.GetMethod(method);
    var attr = m?.GetCustomAttributes().FirstOrDefault(a => a.GetType().Name == verb);
    Ok(method + " at " + verb.Replace("Attribute", "").Replace("Http", "").ToUpperInvariant() + " /" + template,
       (string)attr?.GetType().GetProperty("Template")?.GetValue(attr) == template);
    if (m == null) continue;
    var r = Call(As(false, good), method, argsFor());
    Ok("  refuses a caller with no token", Status(r) == 401, Status(r).ToString());
}
// R-1091: the 64 KB cap on a theme was checked after the whole body had been read.
foreach (var sized in new[] { "ValidateCustomTheme", "SaveCustomTheme" })
{
    var limit = ctrlType.GetMethod(sized)?.GetCustomAttributes().FirstOrDefault(a => a.GetType().Name == "RequestSizeLimitAttribute");
    var bytes = limit == null ? -1L : (long)(limit.GetType().GetField("_bytes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(limit) ?? -1L);
    Ok(sized + " stops reading at the theme size cap, not after the whole body",
       bytes > 0 && bytes <= 2L * 64 * 1024, bytes.ToString());
}
Ok("nothing was changed by a refused caller",
   live.CustomThemes.Count == 0 && live.CustomFonts.Count == 0 && !Directory.Exists(ThemeCatalog.CustomFontFolder));

// Save, read back, edit, refuse.
var saved = Call(As(true), "SaveCustomTheme", "mine", Json(Custom("mine")));
Ok("an administrator saves a theme", Status(saved) == 200 && (bool)Body(saved)!["created"]!, Body(saved)?.ToJsonString());
Ok("it is stored, by its id", live.CustomThemes.Count == 1 && live.CustomThemes[0].Id == "mine");
var edited = Custom("mine", "crimson");
edited["revision"] = 100;   // what the page sends: one past the one it loaded (R-1089)
var again = Call(As(true), "SaveCustomTheme", "mine", Json(edited));
Ok("saving it again edits it in place", Status(again) == 200 && !(bool)Body(again)!["created"]!
   && live.CustomThemes.Count == 1 && JsonNode.DeepEquals(JsonNode.Parse(live.CustomThemes[0].Json), edited));
var mismatch = Call(As(true), "SaveCustomTheme", "other", Json(Custom("mine")));
Ok("an id that is not the address's is refused", Status(mismatch) == 400
   && Body(mismatch)!["problems"]!.AsArray().Any(p => (string)p!["path"]! == "id"));
// Was the low-contrast theme; contrast is no reason to refuse a custom theme any more.
var invalid = Call(As(true), "SaveCustomTheme", "broken", Json(broken));
Ok("an invalid theme is refused with what is wrong, and stores nothing",
   Status(invalid) == 400 && Body(invalid)!["problems"]!.AsArray().Any(p => (string)p!["code"]! == "unknown") && live.CustomThemes.Count == 1);
var built = Call(As(true), "SaveCustomTheme", "harvest", Json(Custom("harvest")));
Ok("a built-in's id is refused", Status(built) == 400);
var check = Call(As(true), "ValidateCustomTheme", Json(broken));
Ok("validate answers the problems without saving", Status(check) == 200 && Body(check)!["problems"]!.AsArray().Count > 0 && live.CustomThemes.Count == 1);
var checkGood = Call(As(true), "ValidateCustomTheme", Json(Custom("fine")));
Ok("and none for a good one", Status(checkGood) == 200 && Body(checkGood)!["problems"]!.AsArray().Count == 0);
var checkFaint = Call(As(true), "ValidateCustomTheme", Json(bad));
Ok("and none for one with no contrast anywhere: no warning either",
   Status(checkFaint) == 200 && Body(checkFaint)!["problems"]!.AsArray().Count == 0, Body(checkFaint)?.ToJsonString());
var savedFaint = Call(As(true), "SaveCustomTheme", "unreadable", Json(bad));
Ok("and it saves", Status(savedFaint) == 200 && live.CustomThemes.Count == 2 && live.CustomThemes.Any(c => c.Id == "unreadable"),
   Status(savedFaint) + " " + Body(savedFaint)?.ToJsonString());
// Taken out again, so what follows starts from the one theme it always had.
Ok("and deletes like any other", Status(Call(As(true), "DeleteCustomTheme", "unreadable")) == 204 && live.CustomThemes.Count == 1);

// A stale tab must not overwrite a newer save (R-1089). A theme's revision goes up on every
// save and the page sends the one after the one it loaded, so a save whose revision is not
// above the stored one was made from an older copy. One with no revision is still taken.
Console.WriteLine("── Saving over a newer save ─────────────────────────────────────");
JsonObject AtRevision(string id, string from, int? revision, string name)
{
    var t = Custom(id, from);
    t["name"] = new JsonObject { ["text"] = name };
    if (revision == null) t.Remove("revision"); else t["revision"] = revision.Value;
    return t;
}
int StoredRevision(string id) => (int)JsonNode.Parse(live.CustomThemes.First(c => c.Id == id).Json)!["revision"]!;
string StoredName(string id) => (string)JsonNode.Parse(live.CustomThemes.First(c => c.Id == id).Json)!["name"]!["text"]!;

Ok("a new theme at revision 1 is saved", Status(Call(As(true), "SaveCustomTheme", "race", Json(AtRevision("race", "harvest", 1, "First")))) == 200);
Ok("an edit one revision on is saved", Status(Call(As(true), "SaveCustomTheme", "race", Json(AtRevision("race", "harvest", 2, "Second")))) == 200
   && StoredRevision("race") == 2);
var staleTab = Call(As(true), "SaveCustomTheme", "race", Json(AtRevision("race", "harvest", 2, "From a stale tab")));
Ok("a tab that loaded revision 1 and saves after revision 2 is refused with 409", Status(staleTab) == 409, Status(staleTab).ToString());
Ok("and the newer save is untouched", StoredRevision("race") == 2 && StoredName("race") == "Second");
Ok("the refusal has its own code", Status(staleTab) == 409 && Body(staleTab) != null
   && Jellyfin.Profiles.Controllers.ErrorCodes.For(409, (string)Body(staleTab)) == "theme_changed", Body(staleTab)?.ToJsonString());
Ok("an older revision is refused too", Status(Call(As(true), "SaveCustomTheme", "race", Json(AtRevision("race", "harvest", 1, "Older")))) == 409
   && StoredName("race") == "Second");
Ok("a save with no revision is still accepted", Status(Call(As(true), "SaveCustomTheme", "race", Json(AtRevision("race", "harvest", null, "No revision")))) == 200
   && StoredName("race") == "No revision");
Ok("and takes the next revision", StoredRevision("race") == 3, StoredRevision("race").ToString());
Ok("a new theme with no revision is accepted at revision 1", Status(Call(As(true), "SaveCustomTheme", "norev", Json(AtRevision("norev", "harvest", null, "New")))) == 200
   && StoredRevision("norev") == 1);
live.CustomThemes.RemoveAll(c => c.Id == "race" || c.Id == "norev");

// The limit.
for (var i = live.CustomThemes.Count; i < ThemeCatalog.MaxCustomThemes; i++)
    Call(As(true), "SaveCustomTheme", "fill-" + i, Json(Custom("fill-" + i)));
var over = Call(As(true), "SaveCustomTheme", "one-more", Json(Custom("one-more")));
Ok("at " + ThemeCatalog.MaxCustomThemes + " themes a new one is refused", Status(over) == 400 && live.CustomThemes.Count == ThemeCatalog.MaxCustomThemes);
var editAgain = Custom("mine"); editAgain["revision"] = 101;
Ok("but an existing one can still be edited", Status(Call(As(true), "SaveCustomTheme", "mine", Json(editAgain))) == 200);
live.CustomThemes.RemoveAll(c => c.Id.StartsWith("fill-", StringComparison.Ordinal));

// Hidden.
var hide = Call(As(true), "SetHiddenThemes", Json(new JsonObject { ["hidden"] = new JsonArray("Glass", "glass", "starlight") }));
Ok("built-ins are hidden, lower case, once each", Status(hide) == 200 && live.HiddenThemes.SequenceEqual(new[] { "glass", "starlight" }));
var hideBonfire = Call(As(true), "SetHiddenThemes", Json(new JsonObject { ["hidden"] = new JsonArray("bonfire") }));
Ok("Bonfire is refused", Status(hideBonfire) == 400 && live.HiddenThemes.Count == 2);
var hideCustom = Call(As(true), "SetHiddenThemes", Json(new JsonObject { ["hidden"] = new JsonArray("mine") }));
Ok("a custom theme is refused: delete it instead", Status(hideCustom) == 400);
live.DefaultTheme = "harbor";
var hideDefault = Call(As(true), "SetHiddenThemes", Json(new JsonObject { ["hidden"] = new JsonArray("harbor") }));
Ok("the default is refused", Status(hideDefault) == 400 && !live.HiddenThemes.Contains("harbor"));
Ok("no list is refused", Status(Call(As(true), "SetHiddenThemes", Json(new JsonObject()))) == 400);

// Delete.
live.DefaultTheme = "mine";
var delDefault = Call(As(true), "DeleteCustomTheme", "mine");
Ok("the default custom theme cannot be deleted", Status(delDefault) == 400 && live.CustomThemes.Any(c => c.Id == "mine"));
live.DefaultTheme = Themes.Bonfire;
Ok("another can", Status(Call(As(true), "DeleteCustomTheme", "mine")) == 204 && !live.CustomThemes.Any(c => c.Id == "mine"));
Ok("one that is not there is a 404", Status(Call(As(true), "DeleteCustomTheme", "mine")) == 404);

// Fonts.
Ok("an upload without the rights box is refused", Status(Call(As(true, good), "UploadThemeFont", "sans-serif", false, null)) == 400);
Ok("an upload with an unknown fallback is refused", Status(Call(As(true, good), "UploadThemeFont", "cursive", true, null)) == 400);
var notFont = Call(As(true, Encoding.ASCII.GetBytes("<svg onload=alert(1)>")), "UploadThemeFont", "sans-serif", true, null);
Ok("a file that is not a font is refused, saying so (" + Body(notFont) + ")", Status(notFont) == 400 && live.CustomFonts.Count == 0);
var big = Call(As(true, new byte[ThemeCatalog.MaxFontBytes + 1]), "UploadThemeFont", "sans-serif", true, null);
Ok("a file over 2 MB is refused", Status(big) == 400);
var builtinFamily = Call(As(true, Font(family: "Inter")), "UploadThemeFont", "sans-serif", true, null);
Ok("a font with a built-in family's name is refused", Status(builtinFamily) == 400 && live.CustomFonts.Count == 0);

var up = Call(As(true, good), "UploadThemeFont", "serif", true, null);
Ok("a good font is accepted", Status(up) == 200 && (string)Body(up)!["family"]! == "Harness Sans", Body(up)?.ToJsonString());
Ok("listed with its file, weights and fallback",
   live.CustomFonts.Count == 1 && live.CustomFonts[0].File == fileName && live.CustomFonts[0].Fallback == "serif"
   && live.CustomFonts[0].WeightMin == 400 && live.CustomFonts[0].WeightMax == 400);
Ok("and stored, byte for byte, under its hash", File.Exists(Path.Combine(ThemeCatalog.CustomFontFolder, fileName))
   && File.ReadAllBytes(Path.Combine(ThemeCatalog.CustomFontFolder, fileName)).SequenceEqual(good));
Ok("the same file again is the font already here", Status(Call(As(true, good), "UploadThemeFont", "serif", true, null)) == 200 && live.CustomFonts.Count == 1);
Ok("another file with the same family is refused",
   Status(Call(As(true, Font(weight: 700)), "UploadThemeFont", "serif", true, null)) == 400 && live.CustomFonts.Count == 1);

var servedFont = Call(As(false), "GetThemeFont", fileName);
Ok("GET /themes/fonts serves it, to anyone, as font/woff2",
   servedFont is FileContentResult fc && fc.ContentType == "font/woff2" && fc.FileContents.SequenceEqual(good));

// r2-15-03, r2-20-02: a revalidation is answered from the name alone, and a font file that
// cannot be read (replaced or locked between Exists and the read) is a 404, not a 500.
{
    var tag = "\"" + fileName.Substring(0, 32) + "\"";
    var fontPath = Path.Combine(ThemeCatalog.CustomFontFolder, fileName);
    using (var held = new FileStream(fontPath, FileMode.Open, FileAccess.Read, FileShare.None))
    {
        var cond = As(false);
        cond.Request.Headers["If-None-Match"] = tag;
        object condResult = null;
        try { condResult = Call(cond, "GetThemeFont", fileName); } catch (Exception) { }
        Ok("a font the browser already has is a 304 without opening the file", Status(condResult) == 304);
        object lockedResult = null;
        try { lockedResult = Call(As(false), "GetThemeFont", fileName); } catch (Exception) { }
        Ok("a font file that cannot be read is a 404, not a 500", Status(lockedResult) == 404);
    }
}

var renamed = Call(As(true, Font(family: "Outfit Thin")), "UploadThemeFont", "sans-serif", true, "Shop Sign");
Ok("the administrator can name a font: the file's own name is only the suggestion",
   Status(renamed) == 200 && live.CustomFonts.Any(cf => cf.Family == "Shop Sign"), Body(renamed)?.ToJsonString());
var renamedBad = Call(As(true, Font(family: "Other")), "UploadThemeFont", "sans-serif", true, "a\"b");
Ok("a name with a quote is refused", Status(renamedBad) == 400 && !live.CustomFonts.Any(cf => cf.Family.Contains('"')));
var renamedBuiltIn = Call(As(true, Font(family: "Other")), "UploadThemeFont", "sans-serif", true, "inter");
Ok("and so is a built-in's name, in any case", Status(renamedBuiltIn) == 400);
Call(As(true), "DeleteThemeFont", live.CustomFonts.First(cf => cf.Family == "Shop Sign").File);

var usesFont = Custom("uses-font");
usesFont["type"]!["font"]!["family"] = "Harness Sans";
Ok("a theme can now use the font", Status(Call(As(true), "SaveCustomTheme", "uses-font", Json(usesFont))) == 200);
var manage = Body(Call(As(true), "GetThemeManagement"));
Ok("manage lists it, and which theme uses it",
   manage!["fonts"]!.AsArray().Count == 1 && manage["fonts"]![0]!["usedBy"]!.AsArray().Select(n => (string)n!).SequenceEqual(new[] { "uses-font" }));
Ok("manage lists every built-in with whether it is hidden",
   manage["builtIn"]!.AsArray().Count == Themes.All.Length
   && manage["builtIn"]!.AsArray().Where(b => (bool)b!["hidden"]!).Select(b => (string)b!["id"]!).SequenceEqual(new[] { "starlight", "glass" }.OrderBy(i => Array.IndexOf(Themes.All, i))));
Ok("a font a theme uses cannot be deleted", Status(Call(As(true), "DeleteThemeFont", fileName)) == 400 && live.CustomFonts.Count == 1);
Call(As(true), "DeleteCustomTheme", "uses-font");
Ok("once the theme is gone, it can", Status(Call(As(true), "DeleteThemeFont", fileName)) == 204 && live.CustomFonts.Count == 0);
Ok("and its file goes with it", !File.Exists(Path.Combine(ThemeCatalog.CustomFontFolder, fileName)));
Ok("and it is no longer served", Status(Call(As(false), "GetThemeFont", fileName)) == 404);
Ok("deleting a name that is not a hash is a 404, and touches nothing", Status(Call(As(true), "DeleteThemeFont", "../PluginConfiguration.xml")) == 404);

// r2-15-01: a body that is not an object is a 400, not a 500.
foreach (var raw in new[] { "[1]", "\"text\"", "5", "null" })
{
    object r = null; Exception thrown = null;
    try { r = Call(As(true), "SaveCustomTheme", "oddbody", JsonDocument.Parse(raw).RootElement); } catch (Exception e) { thrown = e; }
    Ok("a theme body of " + raw + " is a 400", thrown == null && Status(r) == 400, thrown?.InnerException?.Message ?? thrown?.Message);
}

// r2-15-04: a revision at the top of the range cannot be saved on (stored + 1 overflowed an int).
Ok("a revision of int.MaxValue is refused", Status(Call(As(true), "SaveCustomTheme", "bigrev", Json(AtRevision("bigrev", "harvest", int.MaxValue, "Big")))) == 400);
Ok("a revision a billion is still fine", Status(Call(As(true), "SaveCustomTheme", "bigrev", Json(AtRevision("bigrev", "harvest", 1000000000, "Big")))) == 200);
Call(As(true), "DeleteCustomTheme", "bigrev");

// r2-15-02: a stored theme whose type is not an object does not take the Themes page down.
{
    live.CustomThemes.Add(new CustomTheme { Id = "oddtype", Json = "{\"id\":\"oddtype\",\"type\":\"serif\"}" });
    live.CustomThemes.Add(new CustomTheme { Id = "oddfont", Json = "{\"id\":\"oddfont\",\"type\":{\"font\":5}}" });
    Call(As(true, good), "UploadThemeFont", "serif", true, null); // usedBy is only worked out per font
    object r = null; Exception thrown = null;
    try { r = Call(As(true), "GetThemeManagement"); } catch (Exception e) { thrown = e; }
    Ok("manage still answers with a stored theme whose type is not an object", thrown == null && Status(r) == 200, thrown?.InnerException?.Message ?? thrown?.Message);
    live.CustomThemes.RemoveAll(c => c.Id == "oddtype" || c.Id == "oddfont");
    Call(As(true), "DeleteThemeFont", fileName);
}

// R-1092: the file is written before the configuration names it; if the save then throws,
// the file must not be left behind and the list must not name it.
StubXml.Fail = true;
try { Call(As(true, Font(family: "Fails To Save")), "UploadThemeFont", "sans-serif", true, null); } catch (Exception) { }
StubXml.Fail = false;
Ok("a font whose save throws leaves no file and no list entry",
   live.CustomFonts.Count == 0
   && (!Directory.Exists(ThemeCatalog.CustomFontFolder) || Directory.GetFiles(ThemeCatalog.CustomFontFolder).Length == 0));

// r2-15-05: a save that throws leaves the live configuration as it was, on every route
// that changes it (the upload routes already did).
{
    string Failing(Func<object> call)
    {
        StubXml.Fail = true;
        try { call(); } catch (Exception) { }
        finally { StubXml.Fail = false; }
        return "";
    }

    Failing(() => Call(As(true), "SaveCustomTheme", "rb-new", Json(Custom("rb-new"))));
    Ok("a new theme whose save throws is not listed", !live.CustomThemes.Any(c => c.Id == "rb-new"));

    Call(As(true), "SaveCustomTheme", "rb-edit", Json(AtRevision("rb-edit", "harvest", 1, "First")));
    var jsonBefore = live.CustomThemes.First(c => c.Id == "rb-edit").Json;
    var edited2 = AtRevision("rb-edit", "harvest", null, "Changed");
    Failing(() => Call(As(true), "SaveCustomTheme", "rb-edit", Json(edited2)));
    Ok("an edited theme whose save throws keeps its old text", live.CustomThemes.First(c => c.Id == "rb-edit").Json == jsonBefore);

    Failing(() => Call(As(true), "DeleteCustomTheme", "rb-edit"));
    Ok("a deleted theme whose save throws is still listed", live.CustomThemes.Any(c => c.Id == "rb-edit"));
    Call(As(true), "DeleteCustomTheme", "rb-edit");

    var hiddenBefore = live.HiddenThemes.ToList();
    Failing(() => Call(As(true), "SetHiddenThemes", Json(new JsonObject { ["hidden"] = new JsonArray("glass") })));
    Ok("a hidden list whose save throws is unchanged", live.HiddenThemes.SequenceEqual(hiddenBefore));

    var textBefore = live.LogoText;
    Failing(() => Call(As(true), "SetThemeLogo", Json(new JsonObject { ["text"] = "Lost" })));
    Ok("logo text whose save throws is unchanged", live.LogoText == textBefore);

    Call(As(true, Png(400, 120)), "UploadThemeLogo");
    var logoBefore = live.LogoImage;
    Failing(() => Call(As(true), "DeleteThemeLogo"));
    Ok("a removed logo whose save throws is still named", logoBefore != null && live.LogoImage == logoBefore);
    Call(As(true), "DeleteThemeLogo");

    Call(As(true, good), "UploadThemeFont", "serif", true, null);
    Failing(() => Call(As(true), "DeleteThemeFont", fileName));
    Ok("a deleted font whose save throws is still listed", live.CustomFonts.Any(f => f.File == fileName));
    Call(As(true), "DeleteThemeFont", fileName);
}

// GET /themes answers with what the routes made.
Call(As(true), "SaveCustomTheme", "after", Json(Custom("after")));
var getThemes = Call(As(false), "GetThemes", (string)null);
var getBody = getThemes is ContentResult cr ? JsonNode.Parse(cr.Content) : null;
Ok("GET /themes, anonymous, now carries the custom theme and leaves the hidden ones out",
   getBody != null && getBody["themes"]!.AsArray().Any(t => (string)t!["id"]! == "after")
   && !getBody["themes"]!.AsArray().Any(t => (string)t!["id"]! == "glass"));
var getPart = Call(As(false), "GetThemes", "custom");
var partBody = getPart is ContentResult pr ? JsonNode.Parse(pr.Content) : null;
Ok("and ?part=custom only the custom one", partBody != null && partBody["themes"]!.AsArray().Count == 1);

Console.WriteLine();
Console.WriteLine("── The logo ─────────────────────────────────────────────────────");

StubXml.Fail = true;
try { Call(As(true, Png(400, 120)), "UploadThemeLogo"); } catch (Exception) { }
StubXml.Fail = false;
Ok("a logo whose save throws leaves no file and no name in the configuration",
   live.LogoImage == null
   && (!Directory.Exists(ThemeLogo.Folder) || Directory.GetFiles(ThemeLogo.Folder).Length == 0));

// Images built here, header by header: each format the reader knows, and the ones it must not.
byte[] Png(int w, int h)
{
    var b = new byte[64];
    new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(b, 0);
    Put32(b, 16, (uint)w); Put32(b, 20, (uint)h);
    return b;
}
byte[] Jpeg(int w, int h)
{
    var b = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0, 16 };
    b.AddRange(new byte[14]);
    b.AddRange(new byte[] { 0xFF, 0xC0, 0, 17, 8, (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w, 3 });
    b.AddRange(new byte[12]);
    return b.ToArray();
}
byte[] WebP(int w, int h)
{
    var b = new byte[40];
    Encoding.ASCII.GetBytes("RIFF").CopyTo(b, 0); Encoding.ASCII.GetBytes("WEBP").CopyTo(b, 8); Encoding.ASCII.GetBytes("VP8X").CopyTo(b, 12);
    b[24] = (byte)(w - 1); b[25] = (byte)((w - 1) >> 8); b[27] = (byte)(h - 1); b[28] = (byte)((h - 1) >> 8);
    return b;
}
byte[] AnimatedWebP(int w, int h) { var b = WebP(w, h); b[20] = 0x02; return b; }
string LogoRefusal(byte[] bytes)
{
    try { ThemeLogo.Read(bytes); return null; }
    catch (InvalidDataException e) { return e.Message; }
    catch (Exception e) { return "WRONG " + e.GetType().Name; }
}

var pngInfo = ThemeLogo.Read(Png(400, 120));
Ok("a PNG reads as one, with its size", pngInfo.Extension == ".png" && pngInfo.Width == 400 && pngInfo.Height == 120);
var jpgInfo = ThemeLogo.Read(Jpeg(300, 90));
Ok("a JPEG reads its size from the frame header", jpgInfo.Extension == ".jpg" && jpgInfo.Width == 300 && jpgInfo.Height == 90);
var animRefusal = LogoRefusal(AnimatedWebP(512, 128));
Ok("an animated WebP is refused, as an animated GIF is (R-1079)", animRefusal != null && !animRefusal.StartsWith("WRONG"));
var webpInfo = ThemeLogo.Read(WebP(512, 128));
Ok("a WebP too", webpInfo.Extension == ".webp" && webpInfo.Width == 512 && webpInfo.Height == 128);
foreach (var (label, bytes) in new (string, byte[])[]
{
    ("an SVG, which can carry script", Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"/>")),
    ("a GIF", Encoding.ASCII.GetBytes("GIF89a").Concat(new byte[40]).ToArray()),
    ("a PNG 8 pixels wide", Png(8, 100)),
    ("a PNG 5000 pixels wide", Png(5000, 100)),
    ("a PNG cut off in its header", Png(100, 100).Take(18).ToArray()),
    ("nothing", new byte[0]),
})
{
    var r = LogoRefusal(bytes);
    Ok("refused: " + label + " (" + r + ")", r != null && !r.StartsWith("WRONG", StringComparison.Ordinal));
}
var logoNoise = new List<string>();
for (var i = 0; i < 300; i++)
{
    var copy = (i % 3 == 0 ? Png(200, 60) : i % 3 == 1 ? Jpeg(200, 60) : WebP(200, 60)).ToArray();
    for (var k = 0; k < 1 + rng.Next(3); k++) copy[rng.Next(copy.Length)] = (byte)rng.Next(256);
    if (rng.Next(4) == 0) copy = copy.Take(rng.Next(copy.Length)).ToArray();
    var r = LogoRefusal(copy);
    if (r != null && r.StartsWith("WRONG", StringComparison.Ordinal)) logoNoise.Add(r);
}
Ok("300 damaged images: each read or refused, never another exception", logoNoise.Count == 0, string.Join(" | ", logoNoise.Distinct()));

Ok("logo text: trimmed, one line, up to 40", ThemeLogo.CleanText("  The Hayes House ", 40) == "The Hayes House" && ThemeLogo.CleanText(" ", 40) == null);
foreach (var badText in new[] { new string('x', 41), "two\nlines", "tab\there" })
{
    var threw = false;
    try { ThemeLogo.CleanText(badText, 40); } catch (ArgumentException) { threw = true; }
    Ok("logo text refused: " + badText.Replace("\n", "\\n").Replace("\t", "\\t").Substring(0, Math.Min(12, badText.Length)), threw);
}

foreach (var (method, verb, template, argsFor) in new (string, string, string, Func<object[]>)[]
{
    ("SetThemeLogo", "HttpPutAttribute", "admin/themes/logo", () => new object[] { Json(new JsonObject { ["text"] = "x" }) }),
    ("UploadThemeLogo", "HttpPostAttribute", "admin/themes/logo/image", () => Array.Empty<object>()),
    ("DeleteThemeLogo", "HttpDeleteAttribute", "admin/themes/logo/image", () => Array.Empty<object>()),
})
{
    var m = ctrlType.GetMethod(method);
    var attr = m?.GetCustomAttributes().FirstOrDefault(a => a.GetType().Name == verb);
    Ok(method + " at " + verb.Replace("Attribute", "").Replace("Http", "").ToUpperInvariant() + " /" + template,
       (string)attr?.GetType().GetProperty("Template")?.GetValue(attr) == template);
    if (m == null) continue;
    var r = Call(As(false, Png(100, 40)), method, argsFor());
    Ok("  refuses a caller with no token", Status(r) == 401, Status(r).ToString());
}
Ok("and changed nothing", live.LogoImage == null && live.LogoText == null);

var setText = Call(As(true), "SetThemeLogo", Json(new JsonObject { ["text"] = " The Hayes House ", ["alt"] = "Hayes" }));
Ok("an administrator sets the text and alt text", Status(setText) == 200 && live.LogoText == "The Hayes House" && live.LogoAlt == "Hayes");
Ok("text over 40 characters is refused, and nothing changes",
   Status(Call(As(true), "SetThemeLogo", Json(new JsonObject { ["text"] = new string('x', 41) }))) == 400 && live.LogoText == "The Hayes House");
var logoPub = JsonNode.Parse(ThemeCatalog.PublicPayload(live))!["logo"]!;
Ok("GET /themes gives the text, the alt and no image",
   (string)logoPub["text"]! == "The Hayes House" && (string)logoPub["alt"]! == "Hayes" && logoPub["image"] == null);

// A field the body does not have is left alone; "" or null clears it (R-1090).
Ok("setting only the text leaves the alt text",
   Status(Call(As(true), "SetThemeLogo", Json(new JsonObject { ["text"] = "The Hayes Home" }))) == 200
   && live.LogoText == "The Hayes Home" && live.LogoAlt == "Hayes", live.LogoText + " / " + live.LogoAlt);
Ok("setting only the alt text leaves the text",
   Status(Call(As(true), "SetThemeLogo", Json(new JsonObject { ["alt"] = "Hayes logo" }))) == 200
   && live.LogoText == "The Hayes Home" && live.LogoAlt == "Hayes logo", live.LogoText + " / " + live.LogoAlt);
Ok("an empty body changes nothing",
   Status(Call(As(true), "SetThemeLogo", Json(new JsonObject()))) == 200
   && live.LogoText == "The Hayes Home" && live.LogoAlt == "Hayes logo");
Ok("an empty string clears only that field",
   Status(Call(As(true), "SetThemeLogo", Json(new JsonObject { ["text"] = "" }))) == 200
   && live.LogoText == null && live.LogoAlt == "Hayes logo");
Ok("null clears only that field",
   Status(Call(As(true), "SetThemeLogo", Json(new JsonObject { ["alt"] = null }))) == 200
   && live.LogoText == null && live.LogoAlt == null);
Ok("a field that is not text is refused, and nothing changes",
   Status(Call(As(true), "SetThemeLogo", Json(new JsonObject { ["text"] = "Kept", ["alt"] = 5 }))) == 400
   && live.LogoText == null && live.LogoAlt == null);
Call(As(true), "SetThemeLogo", Json(new JsonObject { ["text"] = "The Hayes House", ["alt"] = "Hayes" }));

// The corners (2.0): rounded until an administrator says square; only those two values.
Ok("the logo's corners are rounded by default, in the payload too",
   (string)JsonNode.Parse(ThemeCatalog.PublicPayload(live))!["logo"]!["corners"]! == "rounded");
Ok("an administrator sets square corners, and GET /themes gives them",
   Status(Call(As(true), "SetThemeLogo", Json(new JsonObject { ["corners"] = "square" }))) == 200
   && (string)JsonNode.Parse(ThemeCatalog.PublicPayload(live))!["logo"]!["corners"]! == "square" && live.LogoText == "The Hayes House");
Ok("any other value is refused, and the corners stay",
   Status(Call(As(true), "SetThemeLogo", Json(new JsonObject { ["corners"] = "round" }))) == 400
   && Status(Call(As(true), "SetThemeLogo", Json(new JsonObject { ["corners"] = 1 }))) == 400
   && Status(Call(As(true), "SetThemeLogo", Json(new JsonObject { ["corners"] = null }))) == 400
   && (string)JsonNode.Parse(ThemeCatalog.PublicPayload(live))!["logo"]!["corners"]! == "square");
Call(As(true), "SetThemeLogo", Json(new JsonObject { ["corners"] = "rounded" }));

var firstLogo = Png(400, 120);
var up1 = Call(As(true, firstLogo), "UploadThemeLogo");
var firstName = ThemeLogo.FileNameFor(firstLogo, ThemeLogo.Read(firstLogo));
Ok("an administrator uploads an image, stored under its hash", Status(up1) == 200 && live.LogoImage == firstName
   && File.ReadAllBytes(Path.Combine(ThemeLogo.Folder, firstName)).SequenceEqual(firstLogo), Body(up1)?.ToJsonString());
Ok("GET /themes now gives its address, which changes when the image does",
   (string)JsonNode.Parse(ThemeCatalog.PublicPayload(live))!["logo"]!["image"]! == "/plugins/profiles/themes/logo/" + firstName);
var servedLogo = Call(As(false), "GetThemeLogo", firstName);
Ok("served to anyone, as image/png", servedLogo is PhysicalFileResult pf && pf.ContentType == "image/png");
var svgUp = Call(As(true, Encoding.UTF8.GetBytes("<svg onload=alert(1)>")), "UploadThemeLogo");
Ok("an SVG upload is refused, and the logo stays", Status(svgUp) == 400 && live.LogoImage == firstName);

var secondLogo = Jpeg(300, 90);
Call(As(true, secondLogo), "UploadThemeLogo");
var secondName = ThemeLogo.FileNameFor(secondLogo, ThemeLogo.Read(secondLogo));
Ok("a new image replaces it, and the old file goes", live.LogoImage == secondName && !File.Exists(Path.Combine(ThemeLogo.Folder, firstName)));
// Put back as if its delete had failed: only the current logo is served, file or no file.
File.WriteAllBytes(Path.Combine(ThemeLogo.Folder, firstName), firstLogo);
Ok("the old address is no longer served, even with its file still there", Status(Call(As(false), "GetThemeLogo", firstName)) == 404);
File.Delete(Path.Combine(ThemeLogo.Folder, firstName));
Ok("nor any name that is not the current logo's", Status(Call(As(false), "GetThemeLogo", "../PluginConfiguration.xml")) == 404);
Ok("deleting it leaves the text", Status(Call(As(true), "DeleteThemeLogo")) == 204 && live.LogoImage == null
   && live.LogoText == "The Hayes House" && !File.Exists(Path.Combine(ThemeLogo.Folder, secondName)));

live.LogoText = "two\nlines";
live.LogoImage = "../../evil.png";
var handEdited = JsonNode.Parse(ThemeCatalog.PublicPayload(live))!["logo"]!;
Ok("a hand-edited configuration's bad text and bad image name are left out", handEdited["text"] == null && handEdited["image"] == null);
live.LogoText = null;
live.LogoImage = null;

Console.WriteLine();
Console.WriteLine("── A custom theme's background image ────────────────────────────");

// The file and the theme that names it. Until 2026-11-12 the validator held the text against
// every picture there could be under the theme's fill at `dim`, and refused the weak ones.
// The owner decided custom themes have no contrast rules, so every dim in range now passes;
// the picture's shape (file name, dim range, no unknown field) is still checked.
JsonObject WithImage(string id, string file, double dim)
{
    var t = Custom(id);
    t["background"]!["image"] = new JsonObject { ["file"] = file, ["dim"] = dim };
    t.Remove("revision");   // the server takes the next, so one test's saves never conflict
    return t;
}
JsonObject Plain(string id) { var t = Custom(id); t.Remove("revision"); return t; }
List<ThemeProblem> Problems(JsonNode t)
{
    var copy = t.DeepClone().AsObject();
    copy["revision"] = 1;
    return ThemeCatalog.ValidateCustom(copy, live).ToList();
}
string Paths(List<ThemeProblem> p) => string.Join(", ", p.Select(x => x.Path + " " + x.Code + " " + x.Detail));
var hash = new string('a', 32) + ".jpg";
Ok("a theme with no picture still passes", Problems(Custom("plain")).Count == 0, Paths(Problems(Custom("plain"))));
var strong = Problems(WithImage("strong", hash, ThemeBackground.MaxDim));
Ok("a picture under the strongest dim passes", strong.Count == 0, Paths(strong));
var none = Problems(WithImage("none", hash, 0));
Ok("a picture with no dim at all passes: no contrast rule for a custom theme", none.Count == 0, Paths(none));
// These were refused at 1:1 on a grey picture of their own lightness.
var midAccent = WithImage("midaccent", hash, 0.3);
midAccent["background"] = new JsonObject { ["fill"] = "#000000", ["image"] = new JsonObject { ["file"] = hash, ["dim"] = 0.3 } };
midAccent["colors"]!["accent"] = "#5f5f5f";
var midA = Problems(midAccent);
Ok("an accent the lightness of some picture passes", midA.Count == 0, Paths(midA));
var midText = WithImage("midtext", hash, 0);
midText["background"] = new JsonObject { ["fill"] = "#000000", ["image"] = new JsonObject { ["file"] = hash, ["dim"] = 0 } };
midText["colors"]!["text2"] = "#767676";
var midT = Problems(midText);
Ok("and so does body text", midT.Count == 0, Paths(midT));
Ok("a dim above " + ThemeBackground.MaxDim + " is refused", Problems(WithImage("over", hash, 1)).Any(p => p.Path == "background.image.dim"));
Ok("a name that is not a stored picture's is refused", Problems(WithImage("path", "../evil.png", 0.9)).Any(p => p.Path == "background.image.file" && p.Code == "range"));
Ok("an address is refused", Problems(WithImage("addr", "https://example.com/a.jpg", 0.9)).Any(p => p.Code == "address" || p.Path == "background.image.file"));
var extra = WithImage("extra", hash, 0.9);
extra["background"]!["image"]!["size"] = "contain";
Ok("an unknown field in it is refused", Problems(extra).Any(p => p.Path == "background.image.size" && p.Code == "unknown"));
var noDim = Custom("nodim");
noDim["background"]!["image"] = new JsonObject { ["file"] = hash };
Ok("dim is required", Problems(noDim).Any(p => p.Path == "background.image.dim" && p.Code == "missing"));

// The routes: the same refusals as the logo's.
foreach (var (method, verb, template) in new[]
{
    ("UploadThemeBackground", "HttpPostAttribute", "admin/themes/backgrounds"),
    ("GetThemeBackground", "HttpGetAttribute", "themes/backgrounds/{file}"),
})
{
    var m = ctrlType.GetMethod(method);
    var attr = m?.GetCustomAttributes().FirstOrDefault(a => a.GetType().Name == verb);
    Ok(method + " at " + verb.Replace("Attribute", "").Replace("Http", "").ToUpperInvariant() + " /" + template,
       (string)attr?.GetType().GetProperty("Template")?.GetValue(attr) == template);
}
var anonUp = ctrlType.GetMethod("UploadThemeBackground") == null ? null : Call(As(false, Png(1920, 1080)), "UploadThemeBackground");
Ok("an upload with no token is refused, and stores nothing", Status(anonUp) == 401
   && (!Directory.Exists(ThemeBackground.Folder) || Directory.GetFiles(ThemeBackground.Folder).Length == 0), Status(anonUp).ToString());
Ok("an SVG is refused", Status(Call(As(true, Encoding.UTF8.GetBytes("<svg onload=alert(1)>")), "UploadThemeBackground")) == 400);
Ok("a picture too small to fill a screen is refused", Status(Call(As(true, Png(200, 120)), "UploadThemeBackground")) == 400);
Ok("an animated WebP is refused", Status(Call(As(true, AnimatedWebP(1280, 720)), "UploadThemeBackground")) == 400);
Ok("a file over 2 MB is refused", Status(Call(As(true, new byte[ThemeBackground.MaxBytes + 1]), "UploadThemeBackground")) == 400);

var pic1 = Png(1920, 1080);
var bgUp = Call(As(true, pic1), "UploadThemeBackground");
var bg1 = (string)Body(bgUp)?["file"];
Ok("an administrator uploads one, stored under its hash and not yet named by any theme",
   Status(bgUp) == 200 && bg1 == ThemeLogo.FileNameFor(pic1, ThemeLogo.Sniff(pic1))
   && File.ReadAllBytes(Path.Combine(ThemeBackground.Folder, bg1)).SequenceEqual(pic1)
   && (string)Body(bgUp)?["url"] == "/plugins/profiles/themes/backgrounds/" + bg1, Body(bgUp)?.ToJsonString());
var servedBg = Call(As(false), "GetThemeBackground", bg1);
Ok("served to anyone, as image/png", servedBg is PhysicalFileResult bpf && bpf.ContentType == "image/png");
Ok("a name that is not a hash is a 404", Status(Call(As(false), "GetThemeBackground", "../PluginConfiguration.xml")) == 404);
Ok("a hash with no file is a 404", Status(Call(As(false), "GetThemeBackground", new string('b', 32) + ".png")) == 404);

var missingSave = Call(As(true), "SaveCustomTheme", "photo", Json(WithImage("photo", new string('c', 32) + ".png", 0.9)));
Ok("a theme naming a picture this server does not have is refused, and says so",
   Status(missingSave) == 400 && Body(missingSave)?["problems"]?.AsArray().Any(p => (string)p!["path"] == "background.image.file" && (string)p!["code"] == "image") == true,
   Body(missingSave)?.ToJsonString());
Ok("a theme naming the upload saves", Status(Call(As(true), "SaveCustomTheme", "photo", Json(WithImage("photo", bg1, 0.9)))) == 200);
Ok("GET /themes carries it", JsonNode.Parse(((ContentResult)Call(As(false), "GetThemes", "custom")).Content)!["themes"]!.AsArray()
   .Any(t => (string)t!["id"]! == "photo" && (string)t!["background"]!["image"]!["file"]! == bg1));

var pic2 = Jpeg(1280, 720);
var bg2 = (string)Body(Call(As(true, pic2), "UploadThemeBackground"))?["file"];
var photo2 = WithImage("photo", bg2, 0.9);
Call(As(true), "SaveCustomTheme", "photo", Json(photo2));
Ok("replacing the picture removes the old file", bg2 != null && File.Exists(Path.Combine(ThemeBackground.Folder, bg2))
   && !File.Exists(Path.Combine(ThemeBackground.Folder, bg1)));

Call(As(true), "SaveCustomTheme", "photo-two", Json(WithImage("photo-two", bg2, 0.9)));
var plainAgain = Plain("photo");
Call(As(true), "SaveCustomTheme", "photo", Json(plainAgain));
Ok("taking it out keeps a file another theme still names", File.Exists(Path.Combine(ThemeBackground.Folder, bg2)));
Ok("deleting the last theme naming it removes the file", Status(Call(As(true), "DeleteCustomTheme", "photo-two")) == 204
   && !File.Exists(Path.Combine(ThemeBackground.Folder, bg2)));

// A save that throws keeps the picture the configuration still names.
var bg3 = (string)Body(Call(As(true, Png(1024, 768)), "UploadThemeBackground"))?["file"];
Call(As(true), "SaveCustomTheme", "photo", Json(WithImage("photo", bg3, 0.9)));
StubXml.Fail = true;
try { Call(As(true), "SaveCustomTheme", "photo", Json(Plain("photo"))); } catch (Exception) { }
StubXml.Fail = false;
Ok("a save that throws leaves the named picture's file", File.Exists(Path.Combine(ThemeBackground.Folder, bg3)));
// The route does not put the old text back after a failed save; name it again for what follows.
Call(As(true), "SaveCustomTheme", "photo", Json(WithImage("photo", bg3, 0.9)));

// An upload never saved is left for the orphan cleanup, which names it first.
var stray = (string)Body(Call(As(true, Png(800, 600)), "UploadThemeBackground"))?["file"];
// Not one uploaded a minute ago, though: that is an editor still open, and the cleanup
// would delete the picture it is showing before its Save names it.
var fresh = Call(As(true), "CleanupOrphans", false);
Ok("the orphan cleanup leaves an image uploaded within the hour", (int?)Body(fresh)?["unusedImages"] == 0, Body(fresh)?.ToJsonString());
try { Call(As(true), "CleanupOrphans", true); } catch (Exception) { /* the reconcile after it needs a server */ }
Ok("and does not delete it", File.Exists(Path.Combine(ThemeBackground.Folder, stray)));
// Two hours old: an editor long since closed without saving.
if (File.Exists(Path.Combine(ThemeBackground.Folder, stray)))
    File.SetLastWriteTimeUtc(Path.Combine(ThemeBackground.Folder, stray), DateTime.UtcNow.AddHours(-2));
var preview = Call(As(true), "CleanupOrphans", false);
Ok("the orphan cleanup counts an image no theme names", (int?)Body(preview)?["unusedImages"] == 1, Body(preview)?.ToJsonString());
try { Call(As(true), "CleanupOrphans", true); } catch (Exception) { /* the reconcile after it needs a server */ }
Ok("and deletes it, leaving the named one", !File.Exists(Path.Combine(ThemeBackground.Folder, stray))
   && File.Exists(Path.Combine(ThemeBackground.Folder, bg3)));

var carried = WithImage("carried", new string('d', 32) + ".png", 0.9);
Ok("a theme from elsewhere loses a picture this server does not have", ThemeBackground.DropMissing(carried)
   && carried["background"]!["image"] == null && Problems(carried).Count == 0);
var kept = WithImage("kept", bg3, 0.9);
Ok("and keeps one it has", !ThemeBackground.DropMissing(kept) && kept["background"]!["image"] != null);
live.CustomThemes.RemoveAll(c => c.Id == "photo" || c.Id == "photo-two");

Console.WriteLine();
Console.WriteLine("── Error codes ──────────────────────────────────────────────────");
var codes = asm.GetType("Jellyfin.Profiles.Controllers.ErrorCodes", true).GetMethod("For");
string Code(int status, string msg) => (string)codes.Invoke(null, new object[] { status, msg });
foreach (var (msg, code) in new[]
{
    ("Delete a custom theme first. A server can keep up to 40.", "theme_limit"),
    ("No such theme.", "theme_not_found"),
    ("That's the default theme. Choose another default first.", "theme_in_use"),
    ("Confirm you have the right to use this font.", "font_rights"),
    ("Bonfire already has a font called Inter.", "font_taken"),
    ("A theme uses that font. Change or delete the theme first.", "font_in_use"),
    ("No such font.", "font_not_found"),
})
    Ok("\"" + msg + "\" is " + code, Code(400, msg) == code, Code(400, msg));

try { Directory.Delete(tempDir, true); } catch { }

Console.WriteLine();
if (fails.Count > 0)
{
    foreach (var f in fails) Console.WriteLine("   - " + f);
    Console.WriteLine(pass + " passed, " + fails.Count + " failed");
    return 1;
}
Console.WriteLine(pass + " passed, 0 failed");
return 0;

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

// Never reads, never writes: the configuration starts fresh and a save goes nowhere.
sealed class StubXml : MediaBrowser.Model.Serialization.IXmlSerializer
{
    public void SerializeToStream(object obj, Stream stream) { }
    public static bool Fail;
    public void SerializeToFile(object obj, string file) { if (Fail) throw new IOException("disk full"); }
    public object DeserializeFromFile(Type type, string file) => throw new FileNotFoundException("stub serializer", file);
    public object DeserializeFromStream(Type type, Stream stream) => throw new NotSupportedException();
    public object DeserializeFromBytes(Type type, byte[] buffer) => throw new NotSupportedException();
}
