using System;
using System.Linq;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Devices;
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
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// Drives ProfilesIndexMiddleware through a real ASP.NET request. This is the path that
// produces /web/index.html for every user: if it writes the wrong thing, Jellyfin does not load.
//
// From 1.4.4 the middleware buffers the downstream response instead of answering itself, so
// jellyfin-plugin-file-transformation (which swaps the static file providers underneath us)
// still produces the document we edit. The capture has to survive SendFileAsync, a PipeWriter
// and another plugin's markup, and hand back anything it cannot transform exactly as it came.
//
// From 2.0 the Campground coordinator (Campground.Shared) edits the document from inside
// ProfilesIndexMiddleware. So Run() constructs neither middleware: it asks the plugin's
// PluginServiceRegistrator what to register and composes the startup filters as ASP.NET's
// GenericWebHostService does (last registered innermost).

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
var userManager = DispatchProxy.Create<IUserManager, ThrowingProxy>();
var deviceManager = DispatchProxy.Create<IDeviceManager, ThrowingProxy>();
var sessionManager = DispatchProxy.Create<MediaBrowser.Controller.Session.ISessionManager, ThrowingProxy>();
var networkManager = DispatchProxy.Create<MediaBrowser.Common.Net.INetworkManager, ThrowingProxy>();
var appHost = DispatchProxy.Create<MediaBrowser.Controller.IServerApplicationHost, AppHostProxy>();
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
// What Jellyfin's host would have: the plugin's registrations plus the services the
// middlewares take per request. The user-list injection takes the user, device, session and
// network managers; the index path never touches them, and the proxies throw on any call,
// so a change in that shows up here.
var services = new ServiceCollection();
new PluginServiceRegistrator().RegisterServices(services, appHost);
services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
services.AddSingleton(serverConfig);
services.AddSingleton(userManager);
services.AddSingleton(deviceManager);
services.AddSingleton(sessionManager);
services.AddSingleton(networkManager);
services.AddSingleton(appHost);
var provider = services.BuildServiceProvider();
var startupFilters = provider.GetServices<IStartupFilter>().ToList();

// GenericWebHostService: foreach (var filter in StartupFilters.Reverse()) configure =
// filter.Configure(configure). The first filter registered ends up outermost.
RequestDelegate Pipeline(RequestDelegate downstream)
{
    var app = new ApplicationBuilder(provider);
    Action<IApplicationBuilder> configure = a => a.Run(downstream);
    foreach (var filter in Enumerable.Reverse(startupFilters))
    {
        configure = filter.Configure(configure);
    }

    configure(app);
    return app.Build();
}

async Task<Result> Run(string path, RequestDelegate downstream, Action<HttpContext> arrange = null)
{
    var context = new DefaultHttpContext();
    context.RequestServices = provider;
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
        seen.Calls++;
        return downstream(ctx);
    };

    // Invoke's parameters are resolved from RequestServices by type, as in the server, so
    // this builds against an older middleware with a different Invoke as well as this one.
    await Pipeline(next)(context);

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
Console.WriteLine("── One coordinator for every Campground plugin ─────────────────");

// Bonfire's middleware must stay outermost: the sign-in gates, the proxy switcher and the
// user-list edits run there, ahead of everything, and the coordinator buffers the document
// from inside it. Named by type, because a count would pass with the two the wrong way round.
var filterNames = startupFilters.Select(f => f.GetType().Name).ToList();
Check("Bonfire's startup filter is registered first (outermost), got " + string.Join(", ", filterNames),
    filterNames.Count > 0 && filterNames[0] == "ProfilesStartupFilter");
Check("and the Campground coordinator's comes after it",
    filterNames.IndexOf("CampgroundStartupFilter") > 0);

// The browser runtime goes in once, ahead of Bonfire's head script.
Check("the Campground runtime arrives once", Count(sent.Body, "id=\"campground-runtime\"") == 1);
// 0.10.0 serves it as a file, so a Content-Security-Policy of script-src 'self' does not drop it,
// and Bonfire's theme head snippet the same.
Check("as a file, /Campground/runtime.js, not inline code",
    Count(sent.Body, "/Campground/runtime.js?v=") == 1 && sent.Body.IndexOf("window.Campground", StringComparison.Ordinal) < 0);
Check("and Bonfire's theme head snippet arrives as a file too, once",
    Count(sent.Body, "/Campground/head/jpf-eh.js?v=") == 1);
Check("ahead of Bonfire's head script",
    sent.Body.IndexOf("id=\"campground-runtime\"", StringComparison.Ordinal) >= 0
    && sent.Body.IndexOf("id=\"campground-runtime\"", StringComparison.Ordinal)
        < sent.Body.IndexOf("id=\"jpf-eh\"", StringComparison.Ordinal));
Check("the runtime is not added a second time either", Count(already.Body, "id=\"campground-runtime\"") == 1);

// Another plugin's script arrives in the same pass. Registered through the coordinator the
// way that plugin's own registrator would, then taken back out so the counts below hold.
var asmP = typeof(ProfilesIndexMiddleware).Assembly;
var coordinator = asmP.GetType("Campground.Shared.Injection.InjectionCoordinator");
var regType = asmP.GetType("Campground.Shared.Injection.ScriptRegistration");
Check("the coordinator is compiled into the plugin", coordinator != null && regType != null);
if (coordinator != null && regType != null)
{
    var otherPlugin = Activator.CreateInstance(regType, "gametent", "/plugins/gametent/gametent.js", "3.1.0", 200, null, null);
    const BindingFlags CoordFlags = BindingFlags.Static | BindingFlags.NonPublic;
    coordinator.GetMethod("Register", CoordFlags).Invoke(null, new[] { otherPlugin });
    try
    {
        var shared = await Run("/web/index.html", WriteBody(PlainIndex));
        Check("a second plugin's script is added in the same pass",
            Count(shared.Body, "/plugins/gametent/gametent.js?v=3.1.0") == 1);
        Check("beside Bonfire's, once each",
            Count(shared.Body, "/plugins/profiles/profiles.js") == 1 && Count(shared.Body, "id=\"jpf-eh\"") == 1);
        Check("in order: Bonfire (100) before it (200)",
            shared.Body.IndexOf("/plugins/profiles/profiles.js", StringComparison.Ordinal)
            < shared.Body.IndexOf("/plugins/gametent/gametent.js", StringComparison.Ordinal));
        Check("and the document was asked for once", shared.Seen.Calls == 1);
    }
    finally
    {
        coordinator.GetMethod("Unregister", CoordFlags).Invoke(null, new object[] { "gametent" });
    }
}

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
Console.WriteLine("── Which requests this middleware answers itself ──────────────");

// The middleware intercepts three paths besides the index document, each matched by a
// private predicate. These had no coverage while the functions around them had eighty-odd
// assertions: the code that decides whether a feature runs is the code nobody tests.
// Driven by reflection (they are private) with a real DefaultHttpContext.
var mwType = typeof(ProfilesIndexMiddleware);
const System.Reflection.BindingFlags Priv =
    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;

bool Ask(string methodName, string verb, string path)
{
    var m = mwType.GetMethod(methodName, Priv);
    if (m == null) throw new InvalidOperationException(methodName + " is gone");

    var ctx = new DefaultHttpContext();
    ctx.Request.Method = verb;
    ctx.Request.Path = path;
    return (bool)m.Invoke(null, new object[] { ctx });
}

Check("the own-user predicate exists", mwType.GetMethod("IsOwnUserPath", Priv) != null);
Check("the quick-connect predicate exists", mwType.GetMethod("IsQuickConnectInitiatePath", Priv) != null);

// Quick Connect. The obsolete GET form is routed by Jellyfin and simply calls the POST
// one, so leaving it out would be a way straight around the setting.
Check("POST /QuickConnect/Initiate is ours",
      Ask("IsQuickConnectInitiatePath", "POST", "/QuickConnect/Initiate"));
Check("the obsolete GET form is ours too",
      Ask("IsQuickConnectInitiatePath", "GET", "/QuickConnect/Initiate"));
Check("under a base url it is still ours",
      Ask("IsQuickConnectInitiatePath", "POST", "/jellyfin/QuickConnect/Initiate"));
Check("but not the endpoint that reads a code back",
      !Ask("IsQuickConnectInitiatePath", "GET", "/QuickConnect/Connect"));
Check("and not a path that merely starts the same way",
      !Ask("IsQuickConnectInitiatePath", "POST", "/QuickConnect/InitiateSomething"));
Check("and not another verb entirely",
      !Ask("IsQuickConnectInitiatePath", "DELETE", "/QuickConnect/Initiate"));

// The device id is captured for the provider to read during authentication, and only on
// user routes. The first version captured on every request, which put a UrlDecode scan in
// front of every video segment and image. Too narrow is worse: a missed capture makes the
// provider see no device and refuse a PIN-less profile, silently.
Check("the capture predicate exists", mwType.GetMethod("IsUserRoute", Priv) != null);

Check("POST /Users/AuthenticateByName is captured for",
      Ask("IsUserRoute", "POST", "/Users/AuthenticateByName"));
Check("the obsolete per-user authenticate is captured for",
      Ask("IsUserRoute", "POST", "/Users/8e3cdfa5/Authenticate"));
Check("a password change is captured for",
      Ask("IsUserRoute", "POST", "/Users/8e3cdfa5/Password"));
Check("and quick connect authentication is too",
      Ask("IsUserRoute", "POST", "/Users/AuthenticateWithQuickConnect"));
Check("under a base url as well",
      Ask("IsUserRoute", "POST", "/jellyfin/Users/AuthenticateByName"));

// The traffic this exists to stop scanning.
Check("a video segment is not", !Ask("IsUserRoute", "GET", "/Videos/8e3cdfa5/hls1/main/0.mp4"));
Check("an item image is not", !Ask("IsUserRoute", "GET", "/Items/8e3cdfa5/Images/Primary"));
Check("a web asset is not", !Ask("IsUserRoute", "GET", "/web/index.html"));
Check("an audio stream is not", !Ask("IsUserRoute", "GET", "/Audio/8e3cdfa5/universal"));
Check("and the plugin's own script is not",
      !Ask("IsUserRoute", "GET", "/plugins/profiles/profiles.js"));

// The own-user responses are gated on the setting, and with no plugin configured every
// answer must be false. A predicate that said yes would buffer every /Users/Me for nothing.
Check("with no configuration, /Users/Me is not ours",
      !Ask("IsOwnUserPath", "GET", "/Users/Me"));
Check("nor is the authentication endpoint",
      !Ask("IsOwnUserPath", "POST", "/Users/AuthenticateByName"));

Console.WriteLine();
Console.WriteLine("── The emergency disable reaches all of it ────────────────────");

// Panic used to do one thing: serve an inert client script, so the switcher disappears on
// the next page load. That is not enough now that the plugin edits Jellyfin's own responses
// (the sign-in user list, the two responses naming the signed-in account, Quick Connect).
// If one of those misbehaves, an inert script recovers nothing.
var panicField = typeof(Plugin).GetField("_panicDisabled",
    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
Check("the panic flag is reachable", panicField != null);

if (panicField != null)
{
    // Set directly, not through TripPanicDisable, which has no way back. The harness has to
    // leave the process as it found it.
    panicField.SetValue(null, true);
    try
    {
        // Only Quick Connect is asserted here. The other two predicates also need
        // EnableClientProfileList, which this harness cannot switch on, so they read false either
        // way. They live in tests/cs/publicusers, where panic is the only thing left deciding.
        Check("panic stops refusing Quick Connect",
              !Ask("IsQuickConnectInitiatePath", "POST", "/QuickConnect/Initiate"));
    }
    finally
    {
        panicField.SetValue(null, false);
    }

    Check("and clearing it puts Quick Connect matching back",
          Ask("IsQuickConnectInitiatePath", "POST", "/QuickConnect/Initiate"));
}

Console.WriteLine();
Console.WriteLine("── Counters ────────────────────────────────────────────────────");

var servedProp = typeof(ProfilesIndexMiddleware)
    .GetProperty("ServedCountValue", System.Reflection.BindingFlags.Static
        | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
var served = (long)servedProp.GetValue(null);
// Seven documents were rewritten above: SendFile, Response.Body, PipeWriter, the
// file-transformation one, the one with a second plugin's script, the conditional request,
// and the success that clears the last error. The 404, the JSON, the encoded body, the 304,
// the already-injected documents, the two that cannot be injected into, /web/main.js and the
// POST must not count: the dashboard shows this as "pages served", read through its own property.
Check("served count is one per document actually rewritten (got " + served + ")", served == 7);

// ni-S-B1: with Bonfire off nothing of Bonfire's goes into index.html; on, it does. (The
// emergency flag stands in for the setting: both reach every reader through Plugin.IsOff.)
if (panicField != null)
{
    panicField.SetValue(null, true);
    try
    {
        var offPage = await Run("/web/index.html", SendFile(indexFile));
        Check("off: no Bonfire script tag in index.html", !offPage.Body.Contains("/plugins/profiles/profiles.js"));
        Check("off: no Bonfire head script either", !offPage.Body.Contains("jpf-eh"));
        Check("off: the page itself is still served", offPage.Body.Contains("<div id=\"app\">"));
    }
    finally
    {
        panicField.SetValue(null, false);
    }

    var onPage = await Run("/web/index.html", SendFile(indexFile));
    Check("on again: the script tag is back", onPage.Body.Contains("/plugins/profiles/profiles.js"));
    Check("on again: and the head script", onPage.Body.Contains("jpf-eh"));
}

Console.WriteLine();
Console.WriteLine("── A Base URL (R-1093) ─────────────────────────────────────────");

// Jellyfin's BaseUrlRedirectionMiddleware sends any path without the base to <base>/web/,
// so a script tag that names /plugins/profiles/profiles.js is answered with HTML.
networkConfig.BaseUrl = "/jf";
var based = await Run("/jf/web/index.html", WriteBody(PlainIndex));
Check("under /jf the script tag is prefixed with it",
    based.Body.Contains("src=\"/jf/plugins/profiles/profiles.js?v="));
Check("and no unprefixed tag is left beside it",
    Count(based.Body, "/plugins/profiles/profiles.js") == 1);

// A file patched on disk before the Base URL was set carries the bare path. That is not
// "already injected": it has to be corrected, not passed through.
var bare = PlainIndex.Replace("</body>",
    "<script src=\"/plugins/profiles/profiles.js?v=" + ScriptVersionNow() + "\" defer></script></body>");
var fixedUp = await Run("/jf/web/", WriteBody(bare));
Check("an unprefixed tag already in the document is corrected",
    fixedUp.Body.Contains("src=\"/jf/plugins/profiles/profiles.js?v=")
    && Count(fixedUp.Body, "/plugins/profiles/profiles.js") == 1);

networkConfig.BaseUrl = "/jf/";
var slash = await Run("/jf/web/", WriteBody(PlainIndex));
Check("a trailing slash on the setting does not double up",
    slash.Body.Contains("src=\"/jf/plugins/profiles/profiles.js?v="));

networkConfig.BaseUrl = "";
var none = await Run("/web/", WriteBody(PlainIndex));
Check("with no Base URL the tag is the bare path, as before",
    none.Body.Contains("src=\"/plugins/profiles/profiles.js?v="));

static string ScriptVersionNow() =>
    (string)typeof(ProfilesIndexMiddleware).Assembly
        .GetType("Jellyfin.Profiles.WebInjection", true)
        .GetProperty("ScriptVersion", System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
        .GetValue(null);

Console.WriteLine();
Console.WriteLine("── HTTPS required (R-1094) ─────────────────────────────────────");

// Jellyfin redirects to HTTPS only when RequireHttps is set and it is itself listening with
// HTTPS (Startup.cs: config.RequireHttps && ListenWithHttps). This middleware runs before
// forwarded headers are applied, so behind a TLS-terminating proxy IsHttps is false; stepping
// aside on RequireHttps alone meant nothing was ever injected.
networkConfig.BaseUrl = "";
networkConfig.RequireHttps = true;

AppHostProxy.ListenWithHttps = false;
var proxied = await Run("/web/index.html", WriteBody(PlainIndex));
Check("behind a proxy (RequireHttps, Jellyfin not listening on HTTPS) the page is injected",
    proxied.Body.Contains("/plugins/profiles/profiles.js"));

AppHostProxy.ListenWithHttps = true;
var wouldRedirect = await Run("/web/index.html", WriteBody(PlainIndex));
Check("when Jellyfin will redirect the plain request, we step aside",
    !wouldRedirect.Body.Contains("/plugins/profiles/profiles.js") && wouldRedirect.Seen.Called);

var secure = await Run("/web/index.html", WriteBody(PlainIndex), c => c.Request.Scheme = "https");
Check("an HTTPS request is injected whatever the setting",
    secure.Body.Contains("/plugins/profiles/profiles.js"));

AppHostProxy.ListenWithHttps = false;
networkConfig.RequireHttps = false;

Console.WriteLine();
Console.WriteLine("── Jellyfin's body when the response has already started (R-1095) ──");

// A downstream that has sent its headers. The body it wrote went into our buffer, so
// returning without writing it back gave the client a 200 with nothing in it.
RequestDelegate Started(string content, string type) => async ctx =>
{
    var bytes = Encoding.UTF8.GetBytes(content);
    ctx.Response.StatusCode = 200;
    ctx.Response.ContentType = type;
    await ctx.Response.Body.WriteAsync(bytes);
    ((StartableResponse)ctx.Features.Get<IHttpResponseFeature>()).Started = true;
};

async Task<string> RunPrivate(string method, string path, string verb, RequestDelegate downstream)
{
    var context = new DefaultHttpContext();
    context.Request.Method = verb;
    context.Request.Path = path;
    context.Features.Set<IHttpResponseFeature>(new StartableResponse());
    var body = new MemoryStream();
    context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(body));
    var mw = new ProfilesIndexMiddleware(downstream, NullLogger<ProfilesIndexMiddleware>.Instance);
    var m = typeof(ProfilesIndexMiddleware).GetMethod(method, System.Reflection.BindingFlags.Instance
        | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
    if (m == null) throw new InvalidOperationException(method + " is gone");
    await (Task)m.Invoke(mw, method == "Invoke"
        ? new object[] { context, serverConfig, userManager, deviceManager, sessionManager, networkManager }
        : new object[] { context });
    return Encoding.UTF8.GetString(body.ToArray());
}

var started = await Run("/web/index.html", Started(PlainIndex, "text/html"),
    c => c.Features.Set<IHttpResponseFeature>(new StartableResponse()));
Check("the index: Jellyfin's document reaches the client", started.Body == PlainIndex);

const string UserJson = "{\"Name\":\"Mum\",\"Id\":\"11111111111111111111111111111111\"}";
Check("/Users/Me: Jellyfin's body reaches the client",
    await RunPrivate("RewriteOwnNameAsync", "/Users/Me", "GET", Started(UserJson, "application/json")) == UserJson);
Check("AuthenticateByName: Jellyfin's body reaches the client",
    await RunPrivate("RewriteOwnNameAsync", "/Users/AuthenticateByName", "POST", Started(UserJson, "application/json")) == UserJson);
Check("QuickConnect/Initiate: Jellyfin's body reaches the client",
    await RunPrivate("RememberQuickConnectAsync", "/QuickConnect/Initiate", "POST", Started(UserJson, "application/json")) == UserJson);

Console.WriteLine();
Console.WriteLine("── The on-disk cleanup waits for a page that was served (R-1101) ──");

// The old tags come out of index.html once the middleware is known to work. That used to
// be on the first index request, before it was answered: a request that then failed left
// neither the file nor the middleware supplying the script.
var cleanedFlag = typeof(ProfilesBootstrapTask).GetField("_cleanedForMiddleware",
    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
Check("the cleanup's one-shot flag is where this harness looks", cleanedFlag != null);
if (cleanedFlag != null)
{
    cleanedFlag.SetValue(null, 0);
    await Run("/web/index.html", async ctx => { ctx.Response.StatusCode = 500; await ctx.Response.WriteAsync("broken"); });
    Check("a failed index request does not trigger it", (int)cleanedFlag.GetValue(null) == 0);
    await Run("/web/index.html", WriteBody(PlainIndex));
    Check("a served one does", (int)cleanedFlag.GetValue(null) == 1);
}

Console.WriteLine();
Console.WriteLine("── The sign-in capture runs only for requests that can sign somebody in ──");

// RequestDevice is read by the authentication provider and the session listener, never on a
// GET. Capture costs an address check, two header parses and three AsyncLocal sets, and a
// client makes tens of /Users GETs a page, so a read must not pay it. Every POST under
// /Users still does: AuthenticateByName, {id}/Authenticate and the password change.
async Task<(string Device, string Client, bool HasNote)> Captured(string verb, string path)
{
    Jellyfin.Profiles.Auth.RequestDevice.Capture(null, null, false);
    (string, string, bool) seenInside = (null, null, false);
    await Run(path, ctx =>
    {
        seenInside = (Jellyfin.Profiles.Auth.RequestDevice.Current,
            Jellyfin.Profiles.Auth.RequestDevice.Client,
            Jellyfin.Profiles.Auth.RequestDevice.SignIn != null);
        ctx.Response.StatusCode = 200;
        return Task.CompletedTask;
    }, c =>
    {
        c.Request.Method = verb;
        c.Request.Headers["Authorization"] =
            "MediaBrowser Client=\"Jellyfin Web\", Device=\"Chrome\", DeviceId=\"dev-123\", Version=\"10.11.0\"";
    });
    return seenInside;
}

foreach (var read in new[] { "/Users/11111111111111111111111111111111/Items", "/Users/Me", "/Users/Public" })
{
    var got = await Captured("GET", read);
    Check("GET " + read + " does not capture the device", got.Device == null && got.Client == null);
    Check("GET " + read + " does not start a sign-in note", !got.HasNote);
}

foreach (var signIn in new[] { "/Users/AuthenticateByName", "/Users/11111111111111111111111111111111/Authenticate",
    "/Users/11111111111111111111111111111111/Password", "/users/authenticatebyname" })
{
    var got = await Captured("POST", signIn);
    Check("POST " + signIn + " captures the device and the app", got.Device == "dev-123" && got.Client == "Jellyfin Web");
    Check("POST " + signIn + " starts a sign-in note", got.HasNote);
}

var postElsewhere = await Captured("POST", "/Sessions/Playing/Progress");
Check("a POST outside /Users does not capture", postElsewhere.Device == null && !postElsewhere.HasNote);

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
    public int Calls;
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

// Both interfaces gain and lose members between Jellyfin patch releases. A proxy answers the
// one call the middleware makes and throws on everything else. The middleware no longer takes
// IApplicationPaths, so it cannot read index.html off disk.
// IServerApplicationHost answers only ListenWithHttps: true when Jellyfin has a certificate
// and HTTPS enabled, the only time its HTTPS redirect is in the pipeline.
public class AppHostProxy : DispatchProxy
{
    public static bool ListenWithHttps;

    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        if (targetMethod.Name == "get_ListenWithHttps") return ListenWithHttps;
        throw new NotImplementedException(targetMethod.Name + " should not be reached");
    }
}

// A response whose headers have gone out, as when something downstream calls StartAsync or
// flushes. DefaultHttpContext never reports HasStarted on its own.
public class StartableResponse : Microsoft.AspNetCore.Http.Features.HttpResponseFeature
{
    public bool Started;
    public override bool HasStarted => Started;
}

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
