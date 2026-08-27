using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Profiles;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;

// Drives ProfilesIndexMiddleware through a real ASP.NET request, which nothing has ever
// done outside a live server. This is the path that produces /web/index.html for every
// user of the plugin: if it writes the wrong thing, nobody's Jellyfin loads at all.
//
// From 1.4.4 the middleware buffers the downstream response instead of answering the
// request itself, so that jellyfin-plugin-file-transformation — which swaps the static
// file providers underneath us — still gets to produce the document we then edit. Most of
// what follows is about that: the capture has to survive SendFileAsync, a PipeWriter, and
// another plugin's markup, and it has to hand back anything it cannot transform exactly
// as it arrived.

int pass = 0;
var fails = new List<string>();

void Check(string name, bool condition)
{
    if (condition) { pass++; Console.WriteLine("  PASS  " + name); }
    else { fails.Add(name); Console.WriteLine("  FAIL  " + name); }
}

// ── Stubs ──────────────────────────────────────────────────────────────────
// Only GetConfiguration("network") is ever reached; everything else would be a bug.
var networkConfig = new NetworkConfiguration();

var serverConfig = DispatchProxy.Create<IServerConfigurationManager, ThrowingProxy>();
((ThrowingProxy)(object)serverConfig).AnswerTo = "GetConfiguration";
((ThrowingProxy)(object)serverConfig).Answer = networkConfig;


// A downstream that behaves like Jellyfin's static file middleware: it puts a physical
// file on the wire through SendFileAsync and never touches Response.Body. That is the
// exact reason the capture must be an IHttpResponseBodyFeature and not a swapped stream.
var tempDir = Path.Combine(Path.GetTempPath(), "jpf-pipeline-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);

const string PlainIndex =
    "<!DOCTYPE html><html><head><title>Jellyfin</title></head><body><div id=\"app\"></div></body></html>";

// What File Transformation hands back: the same document with somebody else's script in
// it. Home screen sections and plugin pages both arrive this way.
const string TransformedIndex =
    "<!DOCTYPE html><html><head><title>Jellyfin</title>"
    + "<script src=\"/HomeScreenSections/ClientScript\"></script></head>"
    + "<body><div id=\"app\"></div></body></html>";

var indexFile = Path.Combine(tempDir, "index.html");
await File.WriteAllTextAsync(indexFile, PlainIndex);

// ── Harness ────────────────────────────────────────────────────────────────
async Task<Result> Run(string path, RequestDelegate downstream, Action<HttpContext> arrange = null)
{
    var context = new DefaultHttpContext();
    context.Request.Method = "GET";
    context.Request.Path = path;
    arrange?.Invoke(context);

    var body = new MemoryStream();
    context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(body));

    var seen = new SeenHeaders();
    RequestDelegate next = ctx =>
    {
        seen.AcceptEncoding = ctx.Request.Headers.ContainsKey("Accept-Encoding");
        seen.IfNoneMatch = ctx.Request.Headers.ContainsKey("If-None-Match");
        seen.Range = ctx.Request.Headers.ContainsKey("Range");
        seen.Called = true;
        return downstream(ctx);
    };

    var middleware = new ProfilesIndexMiddleware(next, NullLogger<ProfilesIndexMiddleware>.Instance);
    await middleware.Invoke(context, serverConfig);

    return new Result
    {
        Body = Encoding.UTF8.GetString(body.ToArray()),
        Length = body.Length,
        Status = context.Response.StatusCode,
        ContentType = context.Response.ContentType,
        ContentLength = context.Response.ContentLength,
        Headers = context.Response.Headers,
        Request = context.Request,
        Seen = seen
    };
}

// A static-file-style downstream, using SendFileAsync like the real one.
RequestDelegate SendFile(string file) => async ctx =>
{
    ctx.Response.StatusCode = 200;
    ctx.Response.ContentType = "text/html";
    ctx.Response.ContentLength = new FileInfo(file).Length;
    ctx.Response.Headers["ETag"] = "\"abc123\"";
    ctx.Response.Headers["Last-Modified"] = "Mon, 24 Aug 2026 00:00:00 GMT";
    await ctx.Features.Get<IHttpResponseBodyFeature>().SendFileAsync(file, 0, null, ctx.RequestAborted);
};

// One that writes through Response.Body.
RequestDelegate WriteBody(string html) => async ctx =>
{
    var bytes = Encoding.UTF8.GetBytes(html);
    ctx.Response.StatusCode = 200;
    ctx.Response.ContentType = "text/html; charset=utf-8";
    ctx.Response.ContentLength = bytes.Length;
    await ctx.Response.Body.WriteAsync(bytes);
};

// And one through the PipeWriter, which is why CompleteAsync has to be called.
RequestDelegate WritePipe(string html) => async ctx =>
{
    var bytes = Encoding.UTF8.GetBytes(html);
    ctx.Response.StatusCode = 200;
    ctx.Response.ContentType = "text/html; charset=utf-8";
    await ctx.Response.BodyWriter.WriteAsync(bytes);
};

Console.WriteLine();
Console.WriteLine("── The document comes from the pipeline, not the disk ──────────");

var sent = await Run("/web/index.html", SendFile(indexFile));
Check("SendFileAsync downstream is captured", sent.Body.Contains("<div id=\"app\">"));
Check("and the script tag is added to it", sent.Body.Contains("/plugins/profiles/profiles.js"));
Check("along with the head script", sent.Body.Contains("jpf-eh"));

var written = await Run("/web/", WriteBody(PlainIndex));
Check("a Response.Body downstream is captured", written.Body.Contains("/plugins/profiles/profiles.js"));

var piped = await Run("/web/", WritePipe(PlainIndex));
Check("a PipeWriter downstream is captured", piped.Body.Contains("/plugins/profiles/profiles.js"));

Console.WriteLine();
Console.WriteLine("── The reported bug: another plugin's work must survive ────────");

var both = await Run("/web/index.html", WriteBody(TransformedIndex));
Check("file-transformation's script is still there",
    both.Body.Contains("/HomeScreenSections/ClientScript"));
Check("and ours was added alongside it",
    both.Body.Contains("/plugins/profiles/profiles.js"));
Check("downstream was actually invoked", both.Seen.Called);

// Reading index.html off disk was the second half of the bug: the file on disk is the
// pristine one, so even reaching the pipeline is not enough if we ignore what it said.
var disk = await File.ReadAllTextAsync(indexFile);
Check("the file on disk has no such script (so the document did come from downstream)",
    !disk.Contains("/HomeScreenSections/ClientScript"));

Console.WriteLine();
Console.WriteLine("── Anything it cannot transform is handed back untouched ───────");

var missing = await Run("/web/index.html", ctx =>
{
    ctx.Response.StatusCode = 404;
    ctx.Response.ContentType = "text/plain";
    return ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("not found")).AsTask();
});
Check("a 404 keeps its status", missing.Status == 404);
Check("a 404 keeps its body", missing.Body == "not found");
Check("and nothing was injected into it", !missing.Body.Contains("profiles.js"));

var json = await Run("/web/index.html", ctx =>
{
    ctx.Response.StatusCode = 200;
    ctx.Response.ContentType = "application/json";
    return ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("{\"a\":1}")).AsTask();
});
Check("a non-HTML 200 is passed through", json.Body == "{\"a\":1}");

var compressed = await Run("/web/index.html", ctx =>
{
    ctx.Response.StatusCode = 200;
    ctx.Response.ContentType = "text/html";
    ctx.Response.Headers["Content-Encoding"] = "gzip";
    return ctx.Response.Body.WriteAsync(new byte[] { 1, 2, 3 }).AsTask();
});
Check("an encoded body is not decoded and not touched", compressed.Length == 3);

var empty = await Run("/web/index.html", ctx =>
{
    ctx.Response.StatusCode = 304;
    return Task.CompletedTask;
});
Check("a bodyless response stays bodyless", empty.Length == 0);
Check("and keeps its status", empty.Status == 304);

Console.WriteLine();
Console.WriteLine("── No double injection ─────────────────────────────────────────");

// "both" mode, or anyone else who has already added the same tags.
var already = await Run("/web/index.html", WriteBody(sent.Body));
Check("an already-injected document is passed through",
    Count(already.Body, "/plugins/profiles/profiles.js") == 1);
Check("and its head script is not duplicated either",
    Count(already.Body, "id=\"jpf-eh\"") == 1);

Console.WriteLine();
Console.WriteLine("── Content negotiation is suspended, then restored ─────────────");

var negotiated = await Run("/web/index.html", SendFile(indexFile), ctx =>
{
    ctx.Request.Headers["Accept-Encoding"] = "gzip, br";
    ctx.Request.Headers["If-None-Match"] = "\"abc123\"";
    ctx.Request.Headers["Range"] = "bytes=0-10";
});
Check("downstream saw no Accept-Encoding", !negotiated.Seen.AcceptEncoding);
Check("downstream saw no If-None-Match", !negotiated.Seen.IfNoneMatch);
Check("downstream saw no Range", !negotiated.Seen.Range);
Check("Accept-Encoding is put back", negotiated.Request.Headers["Accept-Encoding"] == "gzip, br");
Check("If-None-Match is put back", negotiated.Request.Headers["If-None-Match"] == "\"abc123\"");
Check("Range is put back", negotiated.Request.Headers["Range"] == "bytes=0-10");
Check("and a conditional request still got a real document",
    negotiated.Body.Contains("/plugins/profiles/profiles.js"));

Console.WriteLine();
Console.WriteLine("── Headers describe the body we actually wrote ─────────────────");

Check("Content-Length matches the transformed body", sent.ContentLength == sent.Length);
Check("Content-Type is html", sent.ContentType == "text/html; charset=utf-8");
Check("the file's ETag is gone", !sent.Headers.ContainsKey("ETag"));
Check("the file's Last-Modified is gone", !sent.Headers.ContainsKey("Last-Modified"));
Check("the document is marked no-store",
    sent.Headers["Cache-Control"].ToString().Contains("no-store"));

Console.WriteLine();
Console.WriteLine("── Everything else goes straight through ───────────────────────");

var other = await Run("/web/main.js", WriteBody("console.log(1)"));
Check("a different path is not intercepted", other.Body == "console.log(1)");
Check("but downstream still ran", other.Seen.Called);

var post = await Run("/web/index.html", WriteBody(PlainIndex), ctx => ctx.Request.Method = "POST");
Check("a POST is not intercepted", !post.Body.Contains("profiles.js"));

Console.WriteLine();
Console.WriteLine("── The dashboard is told about THIS request ──────────────────");

// LastError is rendered on the settings page as "the last problem". It used to be
// cleared only on the successful transform path, so one transient failure stayed
// appended to the green success banner until the server was restarted.
var errorProp = typeof(ProfilesIndexMiddleware).GetProperty("LastError",
    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic
    | System.Reflection.BindingFlags.Public);
string LastError() => (string)errorProp.GetValue(null);

// Neither anchor present: a document we genuinely cannot inject into.
await Run("/web/index.html", WriteBody("<p>not a document we understand</p>"));
Check("a document we cannot inject into records why", !string.IsNullOrEmpty(LastError()));

await Run("/web/index.html", SendFile(indexFile));
Check("a later success clears it", LastError() == null);

await Run("/web/index.html", WriteBody("<p>broken</p>"));
Check("failure is recorded again", !string.IsNullOrEmpty(LastError()));
await Run("/web/index.html", WriteBody(sent.Body));
Check("passing an already-injected page through clears it too", LastError() == null);

Console.WriteLine();
Console.WriteLine("── Counters ────────────────────────────────────────────────────");

var servedProp = typeof(ProfilesIndexMiddleware)
    .GetProperty("ServedCountValue", System.Reflection.BindingFlags.Static
        | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
var served = (long)servedProp.GetValue(null);
// Five documents were actually rewritten above: SendFile, Response.Body, PipeWriter, the
// file-transformation one, and the conditional request. The 404, the JSON, the encoded
// body, the 304, the already-injected document, /web/main.js and the POST must not count
// — the dashboard reports this number as "pages served".
Check("served count is one per document actually rewritten (got " + served + ")", served == 6);

try { Directory.Delete(tempDir, true); } catch { /* best effort */ }

Console.WriteLine();
Console.WriteLine(pass + " passed, " + fails.Count + " failed");
foreach (var f in fails) Console.WriteLine("  FAIL  " + f);
return fails.Count == 0 ? 0 : 1;

static int Count(string haystack, string needle)
{
    var n = 0;
    var i = haystack.IndexOf(needle, StringComparison.Ordinal);
    while (i >= 0) { n++; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal); }
    return n;
}

sealed class SeenHeaders
{
    public bool Called;
    public bool AcceptEncoding;
    public bool IfNoneMatch;
    public bool Range;
}

sealed class Result
{
    public string Body;
    public long Length;
    public int Status;
    public string ContentType;
    public long? ContentLength;
    public IHeaderDictionary Headers;
    public HttpRequest Request;
    public SeenHeaders Seen;
}

// Both interfaces gain and lose members between Jellyfin patch releases, and the harness
// has no interest in any of them. A proxy answers the one call the middleware actually
// makes and throws on everything else. The middleware no longer takes IApplicationPaths
// at all, which is the strongest possible form of "it does not read index.html off disk".
public class ThrowingProxy : DispatchProxy
{
    public object Answer;
    public string AnswerTo;

    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        if (targetMethod.Name == AnswerTo) return Answer;
        throw new NotImplementedException(targetMethod.Name + " should not be reached");
    }
}
