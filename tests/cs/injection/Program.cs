using System;
using System.Reflection;
using System.Text.RegularExpressions;

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

// Exercises the 1.4.1 injection path against the compiled plugin:
//   - WebInjection.Inject / Remove / IsFullyInjected  — the shared HTML surgery
//   - IndexInjectionModes                             — which mechanism is live
//   - ProfilesIndexMiddleware.IsIndexPath             — what the middleware answers
//
// The point of most of these is the double-injection hazard: the on-disk patcher and the
// middleware can both act on the same document, and the failure mode is two copies of
// profiles.js at different versions, which means two gates fighting each other.

var asm = Assembly.LoadFrom(RepoPath("bin", "Release", "net9.0", "Jellyfin.Profiles.dll"));
var wi = asm.GetType("Jellyfin.Profiles.WebInjection", true);
var modes = asm.GetType("Jellyfin.Profiles.Configuration.IndexInjectionModes", true);
var mw = asm.GetType("Jellyfin.Profiles.ProfilesIndexMiddleware", true);

const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

var injectM = wi.GetMethod("Inject", Flags) ?? throw new Exception("Inject not found");
var removeM = wi.GetMethod("Remove", Flags) ?? throw new Exception("Remove not found");
var fullyM = wi.GetMethod("IsFullyInjected", Flags) ?? throw new Exception("IsFullyInjected not found");
var isPathM = mw.GetMethod("IsIndexPath", Flags) ?? throw new Exception("IsIndexPath not found");
var normM = modes.GetMethod("Normalize", Flags);
var patchesM = modes.GetMethod("PatchesFile", Flags);
var usesM = modes.GetMethod("UsesMiddleware", Flags);

string ScriptVersion = (string)wi.GetProperty("ScriptVersion", Flags).GetValue(null);

(bool Changed, string Html) Inject(string html)
{
    var args = new object[] { html, null };
    var changed = (bool)injectM.Invoke(null, args);
    return (changed, (string)args[1]);
}

(bool Changed, string Html) Remove(string html)
{
    var args = new object[] { html, null };
    var changed = (bool)removeM.Invoke(null, args);
    return (changed, (string)args[1]);
}

bool IsFullyInjected(string html) => (bool)fullyM.Invoke(null, new object[] { html });
bool IsIndexPath(string path, string baseUrl) => (bool)isPathM.Invoke(null, new object[] { path, baseUrl });

int pass = 0, fail = 0;
void Check(string name, object actual, object expected)
{
    var ok = Equals(actual, expected);
    if (ok) { pass++; Console.WriteLine($"  PASS  {name}"); }
    else { fail++; Console.WriteLine($"  FAIL  {name}\n          expected: {expected}\n          actual:   {actual}"); }
}

int CountOccurrences(string haystack, string needle)
{
    int n = 0, i = 0;
    while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
    return n;
}

int CountScriptTags(string html) =>
    Regex.Matches(html, @"<script[^>]*src=[""'][^""']*/plugins/profiles/profiles\.js", RegexOptions.IgnoreCase).Count;

int CountHeadScripts(string html) =>
    Regex.Matches(html, @"<script id=""jpf-eh""", RegexOptions.IgnoreCase).Count;

// A stand-in for Jellyfin's index.html: the two anchors, and nothing else that matters.
const string Clean =
    "<!DOCTYPE html><html lang=\"en\"><head>\n" +
    "<meta charset=\"utf-8\">\n" +
    "<title>Jellyfin</title>\n" +
    "</head>\n" +
    "<body>\n" +
    "<div id=\"reactRoot\"></div>\n" +
    "<script src=\"main.jellyfin.bundle.js\" defer></script>\n" +
    "</body></html>";

Console.WriteLine("── Injecting into a clean document ──────────────────────────");
{
    var (changed, html) = Inject(Clean);
    Check("reports a change", changed, true);
    Check("exactly one body tag", CountScriptTags(html), 1);
    Check("exactly one head script", CountHeadScripts(html), 1);
    Check("tag names this build", html.Contains($"profiles.js?v={ScriptVersion}"), true);
    Check("body tag sits before </body>",
        html.IndexOf("/plugins/profiles/profiles.js", StringComparison.Ordinal)
            < html.IndexOf("</body>", StringComparison.Ordinal), true);
    Check("head script sits inside <head>",
        html.IndexOf("jpf-eh", StringComparison.Ordinal)
            < html.IndexOf("</head>", StringComparison.Ordinal), true);
    Check("now reads as fully injected", IsFullyInjected(html), true);
    Check("Jellyfin's own bundle is untouched", html.Contains("main.jellyfin.bundle.js"), true);
}

Console.WriteLine();
Console.WriteLine("── Injecting again is a no-op ───────────────────────────────");
{
    var once = Inject(Clean).Html;
    var (changed, twice) = Inject(once);
    Check("second pass reports no change", changed, false);
    Check("second pass changes nothing", twice, once);
    Check("still exactly one body tag", CountScriptTags(twice), 1);
    Check("still exactly one head script", CountHeadScripts(twice), 1);
}

Console.WriteLine();
Console.WriteLine("── A stale tag is replaced, not duplicated ──────────────────");
{
    // This is the case the middleware exists for: an index.html the server could not
    // rewrite, still carrying the tag from an older build.
    var stale = Clean.Replace("</body>",
        "<script src=\"/plugins/profiles/profiles.js?v=1.2.8.0\" defer></script>\n</body>");
    Check("stale document is not fully injected", IsFullyInjected(stale), false);

    var (changed, html) = Inject(stale);
    Check("reports a change", changed, true);
    Check("STILL exactly one body tag", CountScriptTags(html), 1);
    Check("old version is gone", html.Contains("v=1.2.8.0"), false);
    Check("new version is present", html.Contains($"profiles.js?v={ScriptVersion}"), true);
}

Console.WriteLine();
Console.WriteLine("── An old head script is replaced, not duplicated ───────────");
{
    var old = Clean.Replace("</head>",
        "<script id=\"jpf-eh\">/* an older build */</script>\n</head>");
    var (changed, html) = Inject(old);
    Check("reports a change", changed, true);
    Check("STILL exactly one head script", CountHeadScripts(html), 1);
    Check("old body is gone", html.Contains("an older build"), false);
}

Console.WriteLine();
Console.WriteLine("── A tag written by hand is recognised ──────────────────────");
{
    // Different quoting and attribute order — the version regex must still find it, or the
    // dashboard reports "stale" forever and the middleware injects a second copy.
    var hand = Clean.Replace("</body>",
        $"<script defer src='/plugins/profiles/profiles.js?v={ScriptVersion}'></script>\n</body>");
    var withHead = Inject(hand).Html;
    Check("only one body tag after injection", CountScriptTags(withHead), 1);
    Check("hand-written tag was left in place", withHead.Contains("defer src='"), true);
}

Console.WriteLine();
Console.WriteLine("── Documents we do not understand are left alone ────────────");
{
    var (changed, html) = Inject("<html><p>no anchors here</p></html>");
    Check("reports no change", changed, false);
    Check("document is untouched", html, "<html><p>no anchors here</p></html>");

    // A <head> but no </body>: the anti-flicker script goes in, the switcher tag cannot.
    var headOnly = Inject("<html><head></head><p>x</p></html>");
    Check("head-only document still gets the head script", CountHeadScripts(headOnly.Html), 1);
    Check("head-only document gets no body tag", CountScriptTags(headOnly.Html), 0);
    Check("head-only document is not fully injected", IsFullyInjected(headOnly.Html), false);
}

Console.WriteLine();
Console.WriteLine("── Remove puts the document back ────────────────────────────");
{
    var injected = Inject(Clean).Html;
    var (changed, cleaned) = Remove(injected);
    Check("reports a change", changed, true);
    Check("no body tag left", CountScriptTags(cleaned), 0);
    Check("no head script left", CountHeadScripts(cleaned), 0);
    Check("Jellyfin's own bundle survived", cleaned.Contains("main.jellyfin.bundle.js"), true);
    Check("round-trips back to the original", cleaned, Clean);

    var second = Remove(cleaned);
    Check("removing twice reports no change", second.Changed, false);
    Check("removing a clean document is a no-op", second.Html, Clean);

    // Clean has no blank lines, which is why this went unnoticed: the tidy-up pass that
    // used to run after the two removals was unanchored over the whole document, so it
    // only showed itself on a file that had blank lines to collapse. Jellyfin's real
    // index.html does. The consequence was not cosmetic — Remove reported a change for a
    // document it had never patched, and middleware-only mode (the default) took that as
    // reason to rewrite index.html on its first served page, in the mode whose entire
    // promise is that the file is never touched.
    const string Spaced =
        "<html>\n<head>\n\n<title>Jellyfin</title>\n\n</head>\n"
        + "<body>\n\n<div id=\"app\"></div>\n\n<script src=\"main.jellyfin.bundle.js\"></script>\n\n</body>\n</html>";

    var untouched = Remove(Spaced);
    Check("an unpatched document reports no change", untouched.Changed, false);
    Check("and comes back byte for byte", untouched.Html, Spaced);

    var spacedRound = Remove(Inject(Spaced).Html);
    Check("a patched document with blank lines still round-trips", spacedRound.Html, Spaced);
    Check("its blank lines are all still there",
        spacedRound.Html.Split("\n\n").Length, Spaced.Split("\n\n").Length);

    // CRLF is what a Windows server writes; the round trip has to hold there too.
    var crlf = Spaced.Replace("\n", "\r\n");
    Check("CRLF: unpatched is untouched", Remove(crlf).Html, crlf);
    Check("CRLF: patched round-trips", Remove(Inject(crlf).Html).Html, crlf);
}

Console.WriteLine();
Console.WriteLine("── IsFullyInjected is strict about the version ──────────────");
{
    var injected = Inject(Clean).Html;
    Check("current tag + head script", IsFullyInjected(injected), true);

    var wrongVersion = injected.Replace($"?v={ScriptVersion}", "?v=0.0.0.1");
    Check("old version is NOT fully injected", IsFullyInjected(wrongVersion), false);

    var noHead = Regex.Replace(injected, @"<script id=""jpf-eh"">[\s\S]*?</script>", string.Empty);
    Check("missing head script is NOT fully injected", IsFullyInjected(noHead), false);
}

Console.WriteLine();
Console.WriteLine("── Which mechanism is live ──────────────────────────────────");
{
    string Norm(string v) => (string)normM.Invoke(null, new object[] { v });
    bool Patches(string v) => (bool)patchesM.Invoke(null, new object[] { v });
    bool Uses(string v) => (bool)usesM.Invoke(null, new object[] { v });

    // The default moved from "both" to "middleware" once the pipeline hook was
    // confirmed working on hardware. Not writing to index.html is the behaviour
    // worth defaulting to — that write is the whole reason #17, #11 and #3 exist.
    Check("null defaults to middleware", Norm(null), "middleware");
    Check("empty defaults to middleware", Norm(""), "middleware");
    Check("nonsense defaults to middleware", Norm("harmony"), "middleware");
    Check("case and padding tolerated", Norm("  MiddleWare "), "middleware");
    Check("file survives", Norm("file"), "file");
    Check("both is still selectable", Norm("both"), "both");

    // A configuration written before 1.4.1 has no value at all. It now stops
    // patching the file and is served from the pipeline instead.
    Check("upgrade from an older build stops patching", Patches(null), false);
    Check("both patches", Patches("both"), true);
    Check("file patches", Patches("file"), true);
    Check("middleware does NOT patch", Patches("middleware"), false);

    Check("both uses middleware", Uses("both"), true);
    Check("middleware uses middleware", Uses("middleware"), true);
    Check("file does NOT use middleware", Uses("file"), false);
    Check("upgrade from 1.4.0 gets the fallback", Uses(null), true);
}

Console.WriteLine();
Console.WriteLine("── What the middleware answers ──────────────────────────────");
{
    Check("/web/", IsIndexPath("/web/", null), true);
    Check("/web/index.html", IsIndexPath("/web/index.html", null), true);
    Check("/web/index.html cased oddly", IsIndexPath("/WEB/Index.HTML", null), true);

    // Deliberately not handled — the browser needs Jellyfin's redirect to "/web/" for the
    // document's relative URLs to resolve against the right directory.
    Check("/web is left to Jellyfin's redirect", IsIndexPath("/web", null), false);

    Check("a bundle is not the index", IsIndexPath("/web/main.jellyfin.bundle.js", null), false);
    Check("the API is not the index", IsIndexPath("/System/Info", null), false);
    Check("our own script is not the index", IsIndexPath("/plugins/profiles/profiles.js", null), false);
    Check("the root is not the index", IsIndexPath("/", null), false);
    Check("a deeper path is not the index", IsIndexPath("/web/some/index.html", null), false);

    // Served under a base URL. We sit ahead of UsePathBase, so the prefix is still on the
    // path when we look at it.
    Check("base url + /web/", IsIndexPath("/jellyfin/web/", "/jellyfin"), true);
    Check("base url + index.html", IsIndexPath("/jellyfin/web/index.html", "/jellyfin"), true);
    Check("base url with a trailing slash", IsIndexPath("/jellyfin/web/", "/jellyfin/"), true);
    Check("wrong base url does not match", IsIndexPath("/jellyfin/web/", "/emby"), false);
    Check("unprefixed path against a base url", IsIndexPath("/web/", "/jellyfin"), false);
}

Console.WriteLine();
Console.WriteLine("-- Which version is running, and which one is waiting (issue #25) --");
{
    // "A restart is required" on its own is unfalsifiable from the reader's side. An
    // administrator who has already restarted — or who pressed Jellyfin's own Restart
    // button, which on most Docker images does not restart the process — cannot tell a
    // stale banner from a live one. Two version numbers can be checked against what they
    // just did. Jellyfin keeps every installed version in its own {Name}_{Version} folder
    // and loads one of them, so the answer is in the folder names.
    var task = asm.GetType("Jellyfin.Profiles.ProfilesBootstrapTask", true);
    var newestBeside = task.GetMethod("NewestVersionBeside",
        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic
        | System.Reflection.BindingFlags.Public);

    Check("the version scan is reachable", newestBeside != null, true);

    if (newestBeside != null)
    {
        string? Scan(string dir) => (string?)newestBeside.Invoke(null, new object[] { dir });

        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "bonfire-versions-" + Guid.NewGuid().ToString("N"));
        try
        {
            void Folder(string name) => System.IO.Directory.CreateDirectory(
                System.IO.Path.Combine(root, name));

            Folder("Bonfire_1.5.4.0");
            Folder("Bonfire_1.5.6.0");
            Folder("Bonfire_1.5.10.0");   // must beat 1.5.6 — string ordering would not
            Folder("Bonfire_not-a-version");
            Folder("SomeOtherPlugin_9.9.9.0");
            Folder("Bonfire");           // no version suffix at all

            var running = System.IO.Path.Combine(root, "Bonfire_1.5.4.0");

            Check("finds the newest sibling", Scan(running), "1.5.10.0");
            Check("compares numerically, not as text (1.5.10 beats 1.5.6)",
                  Scan(running) != "1.5.6.0", true);

            // Another plugin's folders must not be read as ours. The prefix comes from the
            // folder we are in, so this is really a test that it is not matching loosely.
            var other = System.IO.Path.Combine(root, "SomeOtherPlugin_9.9.9.0");
            Check("another plugin's versions are not ours", Scan(other), "9.9.9.0");

            // Not a plugin folder: a harness, or a single-file publish. Nothing to compare
            // against is not an error, and must not be reported as "no newer version".
            Check("a directory with no version suffix yields nothing",
                  Scan(System.IO.Path.Combine(root, "Bonfire")), null);
            Check("a directory that does not exist yields nothing",
                  Scan(System.IO.Path.Combine(root, "nope_1.0.0.0")), null);
            Check("null yields nothing", Scan(null), null);
        }
        finally
        {
            try { System.IO.Directory.Delete(root, true); } catch { }
        }
    }

    var find = task.GetMethod("FindInstalledVersions",
        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic
        | System.Reflection.BindingFlags.Public);
    Check("the pair the dashboard reports is reachable", find != null, true);

    if (find != null)
    {
        var pair = find.Invoke(null, null);
        var runningVersion = (string)pair.GetType().GetField("Item1").GetValue(pair);
        Check("it reports a running version", !string.IsNullOrWhiteSpace(runningVersion), true);
        Check("which is this build's informational version, label and all",
              runningVersion,
              System.Reflection.CustomAttributeExtensions
                  .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(asm)
                  ?.InformationalVersion);
    }

    // A newer version installed and a newer version *not* installed are different answers
    // from "cannot tell". Returning false for the third would tell an administrator their
    // restart is done when nothing knows whether it is.
    var newer = task.GetMethod("NewerVersionInstalled",
        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic
        | System.Reflection.BindingFlags.Public);
    Check("the comparison is reachable", newer != null, true);
    if (newer != null)
    {
        Check("running outside a plugin folder answers \"cannot tell\", not \"no\"",
              newer.Invoke(null, null), null);
    }
}

Console.WriteLine();
Console.WriteLine($"{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;
