using System;
using System.Collections.Generic;
using System.IO;
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
// Uploaded pattern pictures (2.0): a custom theme's pattern may name a PNG or WebP the
// administrator uploaded, as well as one of Bonfire's ten.
//
//  - What an upload may be: PNG or WebP, by its bytes; not SVG (script), JPEG, GIF, an
//    animated WebP, or an APNG. The APNG check (an acTL chunk ahead of the first IDAT,
//    ledger r2-20-01) also holds for the logo and the background image.
//  - The validator takes a built-in name or an uploaded file's name, and nothing else.
//  - The routes, end to end, on a real Plugin with stub paths: admin-only upload, stored
//    by hash, anonymous serve with ETag/304, a theme naming a missing file refused, and
//    the file removed when the last theme that names it stops.
//
// Against the commit before this one it does not compile: ThemePattern does not exist.
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

// ── Images, chunk by chunk ──────────────────────────────────────────────────
void Put32(byte[] b, int at, uint v) { b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v; }
byte[] Chunk(string type, byte[] data)
{
    var b = new byte[12 + data.Length];
    Put32(b, 0, (uint)data.Length);
    Encoding.ASCII.GetBytes(type).CopyTo(b, 4);
    data.CopyTo(b, 8);
    return b; // the CRC is left zero: nothing here reads it
}
byte[] Png(int w, int h, bool animated = false, bool actlAfterData = false, int salt = 0)
{
    var ihdr = new byte[13];
    Put32(ihdr, 0, (uint)w); Put32(ihdr, 4, (uint)h); ihdr[8] = 8; ihdr[9] = 6;
    var parts = new List<byte[]>
    {
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A },
        Chunk("IHDR", ihdr),
    };
    if (animated && !actlAfterData) parts.Add(Chunk("acTL", new byte[8]));
    parts.Add(Chunk("IDAT", new byte[] { 1, 2, 3, (byte)salt, (byte)(salt >> 8) }));
    if (animated && actlAfterData) parts.Add(Chunk("acTL", new byte[8]));
    parts.Add(Chunk("IEND", Array.Empty<byte>()));
    return parts.SelectMany(p => p).ToArray();
}
byte[] Jpeg(int w, int h)
{
    var b = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0, 16 };
    b.AddRange(new byte[14]);
    b.AddRange(new byte[] { 0xFF, 0xC0, 0, 17, 8, (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w, 3 });
    b.AddRange(new byte[12]);
    return b.ToArray();
}
byte[] WebP(int w, int h, int salt = 0)
{
    var b = new byte[40];
    Encoding.ASCII.GetBytes("RIFF").CopyTo(b, 0); Encoding.ASCII.GetBytes("WEBP").CopyTo(b, 8); Encoding.ASCII.GetBytes("VP8X").CopyTo(b, 12);
    b[24] = (byte)(w - 1); b[25] = (byte)((w - 1) >> 8); b[27] = (byte)(h - 1); b[28] = (byte)((h - 1) >> 8);
    b[36] = (byte)salt;
    return b;
}
byte[] AnimatedWebP(int w, int h) { var b = WebP(w, h); b[20] = 0x02; return b; }

string Refusal(Action read)
{
    try { read(); return null; }
    catch (InvalidDataException e) { return e.Message; }
    catch (Exception e) { return "WRONG " + e.GetType().Name; }
}

Console.WriteLine("── What an upload may be ────────────────────────────────────────");
Ok("a PNG is accepted", Refusal(() => ThemePattern.Read(Png(240, 240))) == null);
Ok("a WebP is accepted", Refusal(() => ThemePattern.Read(WebP(240, 240))) == null);
Ok("a JPEG is refused", Refusal(() => ThemePattern.Read(Jpeg(240, 240))) != null);
Ok("an SVG is refused", Refusal(() => ThemePattern.Read(Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' onload='alert(1)'/>"))) != null);
Ok("an animated WebP is refused, and says why", Refusal(() => ThemePattern.Read(AnimatedWebP(240, 240)))?.Contains("animated") == true);
Ok("an APNG is refused, and says why", Refusal(() => ThemePattern.Read(Png(240, 240, animated: true)))?.Contains("animated") == true);
Ok("a PNG whose acTL comes after the data is not an APNG", Refusal(() => ThemePattern.Read(Png(240, 240, animated: true, actlAfterData: true))) == null);
Ok("a side under " + ThemePattern.MinSide + " is refused", Refusal(() => ThemePattern.Read(Png(8, 240))) != null);
Ok("a side over " + ThemePattern.MaxSide + " is refused", Refusal(() => ThemePattern.Read(Png(240, ThemePattern.MaxSide + 1))) != null);
Ok("the largest side is accepted", Refusal(() => ThemePattern.Read(Png(ThemePattern.MaxSide, ThemePattern.MaxSide))) == null);
Ok("a file that is neither is refused", Refusal(() => ThemePattern.Read(new byte[100])) != null);
Ok("a PNG cut off in its chunk list is not a crash", Refusal(() => ThemePattern.Read(Png(240, 240).Take(40).ToArray())) is null or "That image couldn't be read.");

// The same check, wherever a PNG is read (r2-20-01).
Ok("the logo refuses an APNG", Refusal(() => ThemeLogo.Read(Png(256, 256, animated: true)))?.Contains("animated") == true);
Ok("the background image refuses an APNG", Refusal(() => ThemeBackground.Read(Png(1920, 1080, animated: true)))?.Contains("animated") == true);
Ok("the logo still takes a still PNG", Refusal(() => ThemeLogo.Read(Png(256, 256))) == null);
Ok("the background image still takes a still PNG", Refusal(() => ThemeBackground.Read(Png(1920, 1080))) == null);

var hex32 = new string('a', 32);
Ok("a hash and .png is a name", ThemePattern.IsFileName(hex32 + ".png"));
Ok("a hash and .webp is a name", ThemePattern.IsFileName(hex32 + ".webp"));
foreach (var bad in new[] { null, "", "snow", hex32 + ".jpg", hex32 + ".svg", hex32 + ".png\n", hex32 + ".png/..", "../" + hex32 + ".png", new string('a', 31) + ".png", new string('A', 32) + ".png" })
    Ok("not a name: \"" + bad + "\"", !ThemePattern.IsFileName(bad));

Console.WriteLine();
Console.WriteLine("── The validator ────────────────────────────────────────────────");
JsonObject Custom(string id, string image)
{
    var t = JsonNode.Parse(File.ReadAllText(Path.Combine(themesDir, "harvest.json")))!.AsObject();
    t["id"] = id;
    t["name"] = new JsonObject { ["text"] = "My " + id };
    foreach (var d in t["decorations"]!.AsArray())
        if ((string)d!["name"]! == "pattern") d["image"] = image;
    return t;
}
// What an editor sends: a file with no revision takes the next one, so a second save is not a conflict.
JsonObject Save(string id, string image) { var t = Custom(id, image); t.Remove("revision"); return t; }
IEnumerable<ThemeProblem> Problems(JsonObject t) => ThemeCatalog.ValidateCustom(t, new PluginConfiguration());
Ok("a built-in name passes", !Problems(Custom("a", "snow")).Any());
Ok("an uploaded .png name passes", !Problems(Custom("a", hex32 + ".png")).Any());
Ok("an uploaded .webp name passes", !Problems(Custom("a", hex32 + ".webp")).Any());
Ok("a .jpg name is refused", Problems(Custom("a", hex32 + ".jpg")).Any(p => p.Code == "pattern"));
Ok("a path is refused", Problems(Custom("a", "../" + hex32 + ".png")).Any(p => p.Code == "pattern"));
Ok("an address is refused", Problems(Custom("a", "https://example.com/" + hex32 + ".png")).Any(p => p.Code == "pattern"));
Ok("a name Bonfire does not hold is refused", Problems(Custom("a", "nope")).Any(p => p.Code == "pattern"));
Ok("the ten built-in names are the files", ThemeCatalog.Patterns.Keys.OrderBy(k => k, StringComparer.Ordinal)
    .SequenceEqual(new[] { "blossoms", "bubbles", "confetti", "embers", "hearts", "leaves", "moons", "snow", "stars", "waves" }),
    string.Join(",", ThemeCatalog.Patterns.Keys));
foreach (var n in ThemeCatalog.Patterns.Keys)
    Ok("a theme can use " + n, !Problems(Custom("a", n)).Any());
Ok("FileOf finds an uploaded name", ThemePattern.FileOf(Custom("a", hex32 + ".png")) == hex32 + ".png");
Ok("FileOf finds none for a built-in name", ThemePattern.FileOf(Custom("a", "snow")) == null);

Console.WriteLine();
Console.WriteLine("── The routes, on a real plugin ─────────────────────────────────");
var asm = typeof(ThemeCatalog).Assembly;
var tempDir = Path.Combine(Path.GetTempPath(), "bonfire-userpatterns-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
var pluginType = asm.GetType("Jellyfin.Profiles.Plugin", true);
Activator.CreateInstance(pluginType, new StubPaths(tempDir), new StubXml());
var live = (PluginConfiguration)pluginType.GetProperty("Configuration").GetValue(pluginType.GetProperty("Instance").GetValue(null));
live.BonfireOn = true;
Ok("the pictures folder is under the stub data path", ThemePattern.Folder.StartsWith(tempDir, StringComparison.Ordinal));

var ctrlType = asm.GetType("Jellyfin.Profiles.Controllers.ProfilesController", true);
var ctor = ctrlType.GetConstructors().First();
var ctorArgs = ctor.GetParameters().Select(p => p.ParameterType.Name.StartsWith("ILogger")
    ? Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(ctrlType)) : null).ToArray();
ControllerBase As(bool admin, byte[] body = null, string ifNoneMatch = null)
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
    if (ifNoneMatch != null) http.Request.Headers["If-None-Match"] = ifNoneMatch;
    c.ControllerContext = new ControllerContext { HttpContext = http };
    return c;
}
object Call(ControllerBase c, string method, params object[] args)
{
    var m = ctrlType.GetMethod(method);
    if (m == null) return null;
    var r = m.Invoke(c, args);
    if (r is System.Threading.Tasks.Task t) { t.GetAwaiter().GetResult(); r = t.GetType().GetProperty("Result").GetValue(t); }
    return r;
}
int Status(object r) => r switch { ObjectResult o => o.StatusCode ?? 200, StatusCodeResult s => s.StatusCode, PhysicalFileResult => 200, ContentResult cr => cr.StatusCode ?? 200, null => -1, _ => -2 };
JsonNode Body(object r) => r is ObjectResult o && o.Value != null
    ? (o.Value is string s ? JsonValue.Create(s) : JsonNode.Parse(JsonSerializer.Serialize(o.Value))) : null;
JsonElement Json(JsonNode n) => JsonDocument.Parse(n.ToJsonString()).RootElement;
string Stored(string file) => Path.Combine(ThemePattern.Folder, file);
int StoredCount() => Directory.Exists(ThemePattern.Folder) ? Directory.GetFiles(ThemePattern.Folder).Length : 0;

{
    var m = ctrlType.GetMethod("UploadThemePattern");
    var attr = m?.GetCustomAttributes().FirstOrDefault(a => a.GetType().Name == "HttpPostAttribute");
    Ok("UploadThemePattern is POST /admin/themes/patterns", (string)attr?.GetType().GetProperty("Template")?.GetValue(attr) == "admin/themes/patterns");
}

var pic1 = Png(240, 240, salt: 1);
var anon = Call(As(false, pic1), "UploadThemePattern");
Ok("an upload with no token is refused, and stores nothing", Status(anon) == 401 && StoredCount() == 0, Status(anon).ToString());
Ok("an SVG is refused", Status(Call(As(true, Encoding.UTF8.GetBytes("<svg onload=alert(1)>")), "UploadThemePattern")) == 400);
Ok("a JPEG is refused", Status(Call(As(true, Jpeg(240, 240)), "UploadThemePattern")) == 400);
Ok("an APNG is refused", Status(Call(As(true, Png(240, 240, animated: true)), "UploadThemePattern")) == 400);
Ok("an animated WebP is refused", Status(Call(As(true, AnimatedWebP(240, 240)), "UploadThemePattern")) == 400);
Ok("an empty body is refused", Status(Call(As(true, new byte[0]), "UploadThemePattern")) == 400);
Ok("a file over the cap is refused", Status(Call(As(true, new byte[ThemePattern.MaxBytes + 1]), "UploadThemePattern")) == 400);
Ok("none of those stored anything", StoredCount() == 0, StoredCount().ToString());

var up = Call(As(true, pic1), "UploadThemePattern");
var file1 = (string)Body(up)?["file"];
Ok("an administrator's PNG is stored under its hash", Status(up) == 200 && ThemePattern.IsFileName(file1) && file1.EndsWith(".png")
   && File.ReadAllBytes(Stored(file1)).SequenceEqual(pic1)
   && (string)Body(up)?["url"] == "/plugins/profiles/themes/pattern-files/" + file1, Body(up)?.ToJsonString());
var again = (string)Body(Call(As(true, pic1), "UploadThemePattern"))?["file"];
Ok("the same picture twice is one file", again == file1 && StoredCount() == 1);

var served = Call(As(false), "GetThemePatternFile", file1);
Ok("anyone can fetch it, as image/png", Status(served) == 200 && (served as PhysicalFileResult)?.ContentType == "image/png");
var tag = "\"" + file1 + "\"";
var withTag = As(false, null, tag);
var notModified = Call(withTag, "GetThemePatternFile", file1);
Ok("a matching If-None-Match is a 304", Status(notModified) == 304, Status(notModified).ToString());
Ok("an ETag and a long cache come with it", withTag.Response.Headers["ETag"].ToString() == tag
   && withTag.Response.Headers["Cache-Control"].ToString().Contains("immutable"));
Ok("a different If-None-Match is a 200", Status(Call(As(false, null, "\"other\""), "GetThemePatternFile", file1)) == 200);
var webpBytes = WebP(240, 240, salt: 2);
var webpFile = (string)Body(Call(As(true, webpBytes), "UploadThemePattern"))?["file"];
Ok("a WebP is served as image/webp", webpFile != null && webpFile.EndsWith(".webp") && (Call(As(false), "GetThemePatternFile", webpFile) as PhysicalFileResult)?.ContentType == "image/webp");
Ok("a hash with no file is a 404", Status(Call(As(false), "GetThemePatternFile", new string('b', 32) + ".png")) == 404);
Ok("a path is a 404", Status(Call(As(false), "GetThemePatternFile", "../PluginConfiguration.xml")) == 404);
Ok("a built-in picture is still served as SVG", Call(As(false), "GetThemePattern", "snow") is ContentResult { ContentType: "image/svg+xml; charset=utf-8" } or ContentResult { ContentType: "image/svg+xml" });
Ok("and a name that is neither is a 404", Status(Call(As(false), "GetThemePattern", "nope")) == 404);

// ── A theme names it ────────────────────────────────────────────────────────
var missing = Call(As(true), "SaveCustomTheme", "mine", Json(Save("mine", new string('c', 32) + ".png")));
Ok("a theme naming a picture this server lacks is refused", Status(missing) == 400 && live.CustomThemes.Count == 0, Body(missing)?.ToJsonString());
var saved = Call(As(true), "SaveCustomTheme", "mine", Json(Save("mine", file1)));
Ok("a theme naming the upload is saved", Status(saved) == 200 && live.CustomThemes.Count == 1, Body(saved)?.ToJsonString());
Ok("and the picture is kept", File.Exists(Stored(file1)));
Ok("it is the theme's named picture", ThemePattern.Named(live).Contains(file1) && !ThemePattern.Named(live).Contains(webpFile));

var second = Call(As(true), "SaveCustomTheme", "other", Json(Save("other", file1)));
Ok("a second theme can name the same picture", Status(second) == 200);

var swap = Call(As(true), "SaveCustomTheme", "mine", Json(Save("mine", "snow")));
Ok("saving one theme with another picture keeps a file another theme still names", Status(swap) == 200 && File.Exists(Stored(file1)), Status(swap) + " " + Body(swap)?.ToJsonString());
var drop = Call(As(true), "SaveCustomTheme", "other", Json(Save("other", "stars")));
Ok("saving the last one away from it deletes the file", Status(drop) == 200 && !File.Exists(Stored(file1)));

var file3 = (string)Body(Call(As(true, Png(300, 300, salt: 3)), "UploadThemePattern"))?["file"];
Call(As(true), "SaveCustomTheme", "other", Json(Save("other", file3)));
Ok("a theme names a new upload", File.Exists(Stored(file3)));
var del = Call(As(true), "DeleteCustomTheme", "other");
Ok("deleting the theme deletes its picture", Status(del) == 204 && !File.Exists(Stored(file3)), Status(del).ToString());

// ── Never saved ─────────────────────────────────────────────────────────────
var stray = (string)Body(Call(As(true, Png(100, 100, salt: 4)), "UploadThemePattern"))?["file"];
Ok("an upload no theme names is not offered for cleanup within the hour", !ThemePattern.Unused(live).Contains(stray));
File.SetLastWriteTimeUtc(Stored(stray), DateTime.UtcNow - ThemeBackground.UnusedGrace - TimeSpan.FromMinutes(5));
Ok("it is once it is old", ThemePattern.Unused(live).Contains(stray));
Ok("deleting it works", ThemePattern.DeleteIfUnused(stray, live) && !File.Exists(Stored(stray)));

// ── A backup or import from a server that lacks it ─────────────────────────
var foreign = Custom("foreign", new string('d', 32) + ".png");
Ok("DropMissing takes out a pattern whose file is not here", ThemePattern.DropMissing(foreign)
   && !foreign["decorations"]!.AsArray().Any(d => (string)d!["name"]! == "pattern"));
Ok("and the theme is valid without it", !Problems(foreign).Any(), string.Join(";", Problems(foreign).Select(p => p.Path + " " + p.Code)));
var keep = Custom("keep", "snow");
Ok("DropMissing leaves a built-in pattern", !ThemePattern.DropMissing(keep));

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
    public void SerializeToFile(object obj, string file) { }
    public object DeserializeFromFile(Type type, string file) => throw new FileNotFoundException("stub serializer", file);
    public object DeserializeFromStream(Type type, Stream stream) => throw new NotSupportedException();
    public object DeserializeFromBytes(Type type, byte[] buffer) => throw new NotSupportedException();
}
