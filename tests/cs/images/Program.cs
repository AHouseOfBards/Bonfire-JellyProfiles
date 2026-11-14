using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

// ─────────────────────────────────────────────────────────────────────────────
// The four image endpoints.
//
// All four did File(System.IO.File.ReadAllBytes(path), type): the whole file on the managed
// heap per image per request, with no cache validator. The profile gate draws every avatar
// in the household on every page load, so a family of six re-downloaded six pictures on each
// navigation. A 2 MB picture goes straight to the large object heap, which is not compacted
// by default.
//
// This runs the real helper against a real file rather than reading the source for
// PhysicalFile.
// ─────────────────────────────────────────────────────────────────────────────

static string RepoRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d != null && !File.Exists(Path.Combine(d.FullName, "Jellyfin.Profiles.csproj")))
        d = d.Parent;
    if (d == null) throw new InvalidOperationException("Could not find the repository root.");
    return d.FullName;
}

int pass = 0;
var fails = new List<string>();
void Ok(string name, bool cond)
{
    if (cond) { pass++; Console.WriteLine("  PASS  " + name); }
    else { fails.Add(name); Console.WriteLine("  FAIL  " + name); }
}

// Which build to load. This harness's output sits in bin/Release/<tfm>/, so its folder name
// is the framework the csproj resolved: no second place to keep in step. tests/run.sh cs10
// runs the whole set against net10.0.
var tfm = new System.IO.DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
    System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)).Name;
var asm = Assembly.LoadFrom(Path.Combine(RepoRoot(), "bin", "Release", tfm, "Jellyfin.Profiles.dll"));
var ctrlType = asm.GetType("Jellyfin.Profiles.Controllers.ProfilesController", true);
var baseType = asm.GetType("Jellyfin.Profiles.Controllers.ProfilesBaseController", true);

const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance
                       | BindingFlags.NonPublic | BindingFlags.Public;

Console.WriteLine();
Console.WriteLine("── Nothing loads an image into memory to send it ───────────────");

// The source check first, because it is the one that covers all four endpoints at once
// and names any that regresses. The behavioural checks below cover the helper they share.
var controllerSrc = File.ReadAllText(Path.Combine(RepoRoot(), "Controllers", "ProfilesController.cs"));
var offenders = controllerSrc
    .Split('\n')
    .Select((line, i) => (Line: line, No: i + 1))
    .Where(x => x.Line.Contains("File(System.IO.File.ReadAllBytes"))
    .ToArray();

foreach (var o in offenders)
    Ok("ProfilesController.cs:" + o.No + " still reads a whole file into memory", false);
if (offenders.Length == 0)
    Ok("no endpoint reads a whole file into memory to send it", true);

var uses = controllerSrc.Split("ImageFileResult(").Length - 1;
Ok("all four image endpoints go through the streaming helper (" + uses + " call sites)",
   uses == 4);

Console.WriteLine();
Console.WriteLine("── What the helper actually returns ────────────────────────────");

var helper = baseType.GetMethod("ImageFileResult", Any);
Ok("the helper is reachable", helper != null);

if (helper != null)
{
    // Built without its constructor: the real one wants Jellyfin's DI graph, and none of
    // it is reached by this method. The readonly logger is set by reflection, because any
    // logging branch would throw on null.
#pragma warning disable SYSLIB0050
    var controller = (ControllerBase)FormatterServices.GetUninitializedObject(ctrlType);
#pragma warning restore SYSLIB0050
    baseType.GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(controller, NullLogger.Instance);
    controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

    var dir = Path.Combine(Path.GetTempPath(), "bonfire-images-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        var png = Path.Combine(dir, "avatar.png");
        File.WriteAllBytes(png, new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3, 4 });
        var info = new FileInfo(png);

        var result = helper.Invoke(controller, new object[] { png, "image/png" });

        Ok("a real file streams from disk rather than being buffered (" + result.GetType().Name + ")",
           result is PhysicalFileResult);

        if (result is PhysicalFileResult pf)
        {
            Ok("pointing at the file itself", pf.FileName == png);
            Ok("with the content type it was given", pf.ContentType == "image/png");

            Ok("carrying an entity tag", pf.EntityTag != null);
            if (pf.EntityTag != null)
            {
                var tag = pf.EntityTag.Tag.ToString();
                // Length and last-write time, hex. A content hash would mean reading the
                // whole file to avoid reading the whole file.
                var expected = "\"" + info.Length.ToString("x")
                             + "-" + info.LastWriteTimeUtc.Ticks.ToString("x") + "\"";
                Ok("built from length and last-write time (" + tag + ")", tag == expected);
                Ok("and it is strong, so a 304 means the same bytes", !pf.EntityTag.IsWeak);
            }

            Ok("and a last-modified date, so If-Modified-Since works too",
               pf.LastModified.HasValue
               && pf.LastModified.Value.UtcDateTime == info.LastWriteTimeUtc);
        }

        var cacheControl = controller.Response.Headers["Cache-Control"].ToString();
        Ok("cached for an hour (" + cacheControl + ")",
           cacheControl.Contains("max-age=3600"));
        Ok("privately — these are one household's faces, and the endpoint is anonymous "
           + "so a shared proxy must not keep them",
           cacheControl.Contains("private") && !cacheControl.Contains("public"));

        // The tag has to move when the picture does, or a changed avatar never reaches
        // anyone who has the old one cached.
        System.Threading.Thread.Sleep(10);
        File.WriteAllBytes(png, new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 9, 9, 9, 9, 9 });
        var after = helper.Invoke(controller, new object[] { png, "image/png" }) as PhysicalFileResult;
        Ok("changing the picture changes the tag",
           after?.EntityTag != null
           && (result as PhysicalFileResult)?.EntityTag.Tag.ToString() != after.EntityTag.Tag.ToString());

        // Found by one request and deleted before the next is a real sequence: the
        // dashboard can remove an avatar while a gate is open somewhere else.
        var gone = Path.Combine(dir, "not-here.png");
        var missing = helper.Invoke(controller, new object[] { gone, "image/png" });
        Ok("a file that has gone answers 404 rather than throwing (" + missing.GetType().Name + ")",
           missing is NotFoundResult);

        // ── What is accepted, and what a failed save leaves behind ─────────────────
        // R-1111: the declared type was matched by substring and the bytes never looked at.
        // R-1112: a stale variant that cannot be deleted threw out of the save as a 500.
        // R-1113: the old image was deleted before the new one was written, so a failed
        //         write lost the picture the person already had.
        var decode = baseType.GetMethod("DecodeImageDataUrl", Any);
        var write = baseType.GetMethod("WriteImageFiles", Any);
        var deleteAll = baseType.GetMethod("DeleteImageFiles", Any);
        Ok("the save helpers are reachable", decode != null && write != null && deleteAll != null);

        string Url(string mime, params byte[] bytes) => "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
        byte[] PngBytes = { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 1, 2 };
        byte[] JpegBytes = { 0xFF, 0xD8, 0xFF, 0xE0, 0, 16, 1, 2 };
        byte[] GifBytes = { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 1, 2 };
        byte[] WebpBytes = { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 4, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P' };
        object Decode(string url) => decode.Invoke(controller, new object[] { url, "test" });

        Ok("a real PNG, JPEG, GIF and WebP are each accepted",
           Decode(Url("image/png", PngBytes)) != null && Decode(Url("image/jpeg", JpegBytes)) != null
           && Decode(Url("image/gif", GifBytes)) != null && Decode(Url("image/webp", WebpBytes)) != null);
        Ok("HTML declared as image/png is refused",
           Decode(Url("image/png", System.Text.Encoding.UTF8.GetBytes("<script>alert(1)</script>"))) == null);
        Ok("a JPEG declared as image/png is refused", Decode(Url("image/png", JpegBytes)) == null);
        Ok("a type that merely contains a known one is refused (image/png-evil)",
           Decode(Url("image/png-evil", PngBytes)) == null);

        // r2-22-02: the text is measured before it is decoded. A payload far over the
        // budget made of characters that are not base64 at all is refused as too large;
        // had it been decoded first it would have failed as unreadable.
        {
            var parse = baseType.GetMethod("ParseImageDataUrl", Any);
            Ok("ParseImageDataUrl is reachable", parse != null);
            string Refusal(string url)
            {
                var t = parse.Invoke(controller, new object[] { url, "test" });
                return (string)t.GetType().GetField("Item3").GetValue(t);
            }
            Ok("an oversize base64 payload is refused as too large, before it is decoded",
               Refusal("data:image/png;base64," + new string('!', 4_000_000)) == "That image is too large.");
            Ok("a short payload that is not base64 is still unreadable, so the bound is not a catch-all",
               Refusal("data:image/png;base64,!!!!") == "That image could not be read.");
        }

        var wdir = Path.Combine(dir, "write");
        Directory.CreateDirectory(wdir);
        object Write(string name, string url) => write.Invoke(controller, new object[] { wdir, name, url, null, "test" });

        Ok("a first save is written", (string)Write("a", Url("image/png", PngBytes)) == ".png"
           && File.Exists(Path.Combine(wdir, "a.png")));

        // A save that fails part way: the new JPEG cannot be created because a directory
        // sits where its file would go. The PNG already stored must still be there.
        Directory.CreateDirectory(Path.Combine(wdir, "a.jpg"));
        var threw = false;
        try { Write("a", Url("image/jpeg", JpegBytes)); }
        catch (Exception) { threw = true; }
        Ok("a save that fails leaves the stored picture in place (threw: " + threw + ")",
           File.Exists(Path.Combine(wdir, "a.png")));
        Directory.Delete(Path.Combine(wdir, "a.jpg"));

        Ok("a changed format replaces the old one rather than sitting beside it",
           (string)Write("a", Url("image/jpeg", JpegBytes)) == ".jpg"
           && File.Exists(Path.Combine(wdir, "a.jpg")) && !File.Exists(Path.Combine(wdir, "a.png")));

        // A file that cannot be deleted. Windows refuses to delete an open file; elsewhere
        // an open file deletes fine, so there is nothing to hold and the check is vacuous.
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(Path.Combine(wdir, "b.webp"), WebpBytes);
            using (new FileStream(Path.Combine(wdir, "b.webp"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var threw2 = false;
                try { deleteAll.Invoke(null, new object[] { wdir, "b" }); }
                catch (Exception) { threw2 = true; }
                Ok("a variant that cannot be deleted does not fail the request", !threw2);

                var threw3 = false;
                try { Write("b", Url("image/png", PngBytes)); }
                catch (Exception) { threw3 = true; }
                // r2-10-05: any stale variant that cannot be removed is a failed save, not a
                // silent success (the new file is written; the caller is told).
                Ok("saving over a name that has an undeletable variant reports a failed save", threw3);
            }

            // r2-10-05: a stale .jpg that cannot be deleted outranks a new .png in
            // FindImageFile, so the save has to report failure rather than succeed while the
            // old picture keeps showing.
            File.WriteAllBytes(Path.Combine(wdir, "c.jpg"), JpegBytes);
            using (new FileStream(Path.Combine(wdir, "c.jpg"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var threw4 = false;
                try { Write("c", Url("image/png", PngBytes)); }
                catch (Exception) { threw4 = true; }
                Ok("a stale higher-ranked file that cannot be deleted fails the save", threw4);
            }
        }

        // r2-10-05: the temp name was fixed per file, so two saves of one profile at once
        // moved each other's bytes into place. Ten at once must all succeed and leave no
        // temp file behind.
        {
            var cdir = Path.Combine(dir, "concurrent");
            Directory.CreateDirectory(cdir);
            var tasks = Enumerable.Range(0, 10).Select(_ => System.Threading.Tasks.Task.Run(() =>
            {
                try { write.Invoke(controller, new object[] { cdir, "p", Url("image/png", PngBytes), null, "test" }); return null; }
                catch (Exception ex) { return (ex.InnerException ?? ex).Message; }
            })).ToArray();
            System.Threading.Tasks.Task.WaitAll(tasks);
            var errors = tasks.Select(t => t.Result).Where(m => m != null).ToList();
            Ok("ten simultaneous saves of one name all succeed (" + string.Join("; ", errors.Take(1)) + ")", errors.Count == 0);
            Ok("and leave no temp file behind", !Directory.GetFiles(cdir, "*.tmp").Any());
        }

        // r2-10-07: the save and the check read a data URL through one parse, so a header
        // that only mentions a type is refused by both.
        {
            var check = baseType.GetMethod("CheckProfileImage", Any);
            Ok("CheckProfileImage is reachable", check != null);
            string Check(string url) => (string)check.Invoke(controller, new object[] { Guid.NewGuid(), url, null });
            Ok("the check accepts a real PNG", Check(Url("image/png", PngBytes)) == null);
            Ok("the check refuses a type that only contains a known one", Check(Url("image/png-evil", PngBytes)) != null);
            Ok("the check refuses a JPEG declared as PNG", Check(Url("image/png", JpegBytes)) != null);
            Ok("the check refuses text declared as PNG",
               Check(Url("image/png", System.Text.Encoding.UTF8.GetBytes("<script>"))) != null);
            Ok("the check refuses a parameter that mentions a type",
               Check("data:image/svg+xml;x=image/png;base64," + Convert.ToBase64String(PngBytes)) != null);
        }
    }
    finally
    {
        try { Directory.Delete(dir, true); } catch { }
    }
}

Console.WriteLine();
Console.WriteLine("── An external image URL cannot carry CSS or markup characters (R-1110) ──");
{
    var valid = baseType.GetMethod("IsValidImageUrl", Any);
    bool V(string s) => (bool)valid.Invoke(null, new object[] { s });
    Ok("a plain https URL is still accepted", V("https://example.com/a/b.png"));
    Ok("a plain http URL with a query is still accepted", V("http://example.com/a.png?x=1&y=2"));
    foreach (var c in new[] { ')', ';', '\\', ' ', '{', '}' })
        Ok("an http URL containing '" + c + "' is refused", !V("https://example.com/a" + c + "b.png"));
    Ok("an http URL containing a quote is still refused", !V("https://example.com/a\"b.png"));
    Ok("a non-http scheme is still refused", !V("ftp://example.com/a.png"));
    // r2-10-06: Uri.TryCreate drops a tab, CR or LF inside the URL; the raw string is what
    // gets stored.
    foreach (var c in new[] { '\t', '\n', '\r' })
        Ok("an http URL containing control character " + (int)c + " is refused", !V("https://example.com/a" + c + "b.png"));
}

Console.WriteLine();
if (fails.Count > 0)
{
    Console.WriteLine("  Failures:");
    foreach (var f in fails) Console.WriteLine("   - " + f);
    Console.WriteLine(pass + " passed, " + fails.Count + " failed");
    return 1;
}
Console.WriteLine(pass + " passed, 0 failed");
return 0;
