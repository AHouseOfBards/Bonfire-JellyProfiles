using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;

// ─────────────────────────────────────────────────────────────────────────────
// Every lock that guards the plugin configuration must be taken on ONE object
// that outlives the configuration.
//
// Twenty-six sites used to read `var config = Plugin.Instance?.Configuration;`
// and then `lock (config)`. That looks like mutual exclusion and is not. When an
// administrator saves the plugin's settings page, Jellyfin calls
// BasePlugin<T>.UpdateConfiguration, which assigns a *new* configuration
// instance. Every monitor already held on the old instance is now on an object
// nothing else will ever lock, and every request that arrives afterwards locks
// the new one. Two writers end up inside the critical section at once, and the
// writes made against the stale reference are thrown away with it.
//
// This harness does not take that on trust. It:
//   1. instantiates the real plugin and calls the real UpdateConfiguration, to
//      show the instance is genuinely replaced;
//   2. drives two threads through the old pattern, deterministically, and shows
//      both get inside at once — then drives the same two threads through the
//      real ConfigLock and shows they do not;
//   3. checks ConfigLock is static, so the transient controllers share it;
//   4. enumerates every `lock (...)` in the plugin source, one at a time, and
//      names any that is taken on something other than a known static object.
//
// (4) is the regression gate. It is deliberately an enumeration and not a count:
// a total tells you something moved without telling you which thing, and this
// repository has shipped three bugs past a check that answered the coarser
// question.
// ─────────────────────────────────────────────────────────────────────────────

static string RepoRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d != null && !File.Exists(Path.Combine(d.FullName, "Jellyfin.Profiles.csproj")))
        d = d.Parent;
    if (d == null) throw new InvalidOperationException("Could not find the repository root.");
    return d.FullName;
}
static string RepoPath(params string[] parts)
{
    var all = new List<string> { RepoRoot() };
    all.AddRange(parts);
    return Path.Combine(all.ToArray());
}

int pass = 0;
var fails = new List<string>();
void Ok(string name, bool cond)
{
    if (cond) { pass++; Console.WriteLine("  PASS  " + name); }
    else { fails.Add(name); Console.WriteLine("  FAIL  " + name); }
}

var dll = RepoPath("bin", "Release", "net9.0", "Jellyfin.Profiles.dll");
var asm = Assembly.LoadFrom(dll);
var cfgType = asm.GetType("Jellyfin.Profiles.Configuration.PluginConfiguration", true);
var baseCtl = asm.GetType("Jellyfin.Profiles.Controllers.ProfilesBaseController", true);

const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance
                       | BindingFlags.NonPublic | BindingFlags.Public;

Console.WriteLine();
Console.WriteLine("── The configuration instance really is replaced ───────────────");

// Proof by execution, not by reading Jellyfin's source. The two stubs below are
// the whole of what BasePlugin needs to be constructed: somewhere to pretend to
// write, and a serializer that never succeeds, so the configuration is built
// fresh rather than read off a disk we do not have.
var pluginType = asm.GetType("Jellyfin.Profiles.Plugin", true);
var tempDir = Path.Combine(Path.GetTempPath(), "bonfire-configlock-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);

object plugin = null;
try
{
    plugin = Activator.CreateInstance(pluginType, new StubPaths(tempDir), new StubXml());
}
catch (Exception ex)
{
    Console.WriteLine("        could not construct the plugin: " + ex.GetBaseException().Message);
}
Ok("the plugin can be constructed against stub paths", plugin != null);

if (plugin != null)
{
    var configProp = pluginType.GetProperty("Configuration", Any);
    Ok("BasePlugin exposes Configuration", configProp != null);

    var update = pluginType.GetMethod("UpdateConfiguration", Any, null,
        new[] { typeof(MediaBrowser.Model.Plugins.BasePluginConfiguration) }, null);
    Ok("BasePlugin exposes UpdateConfiguration — the settings-page save path",
       update != null);

    if (configProp != null && update != null)
    {
        var before = configProp.GetValue(plugin);
        Ok("a configuration is available before the save", before != null);

        // Exactly what the dashboard's save does.
        var replacement = Activator.CreateInstance(cfgType);
        update.Invoke(plugin, new[] { replacement });
        var after = configProp.GetValue(plugin);

        Ok("after UpdateConfiguration, Plugin.Instance.Configuration is a DIFFERENT object "
           + "(this is the whole defect: a monitor held on the old one guards nothing)",
           !ReferenceEquals(before, after));
        Ok("and it is the object that was handed in", ReferenceEquals(after, replacement));
    }
}

try { Directory.Delete(tempDir, true); } catch { /* best effort */ }

Console.WriteLine();
Console.WriteLine("── Locking the instance gives no mutual exclusion ──────────────");

// Deterministic, not a stress test. A race that has to be won by luck is a test
// that passes by luck too. Thread A enters the critical section and waits; the
// configuration is then replaced exactly as UpdateConfiguration replaces it; then
// thread B enters. If the lock is the configuration itself, B walks straight in.
var holder = new Holder { Cfg = Activator.CreateInstance(cfgType) };

var brokenMax = RunTwoThreads(holder, cfgType, useInstanceAsLock: true, lockObject: null);
Ok("with `lock (config)`, two threads are inside the critical section at once "
   + "(observed " + brokenMax + " concurrent)", brokenMax == 2);

var lockField = baseCtl.GetField("ConfigLock", Any);
Ok("ProfilesBaseController declares ConfigLock", lockField != null);

if (lockField != null)
{
    Ok("ConfigLock is static — controllers are transient, so an instance field "
       + "would give every request its own lock", lockField.IsStatic);
    Ok("ConfigLock is readonly — it must not be reassignable", lockField.IsInitOnly);

    var theLock = lockField.GetValue(null);
    Ok("ConfigLock is a live object", theLock != null);

    if (theLock != null)
    {
        holder.Cfg = Activator.CreateInstance(cfgType);
        var fixedMax = RunTwoThreads(holder, cfgType, useInstanceAsLock: false, lockObject: theLock);
        Ok("with `lock (ConfigLock)`, only one thread is inside at a time across the "
           + "same replacement (observed " + fixedMax + " concurrent)", fixedMax == 1);

        // The deterministic test proves exclusion. This one proves the lock is
        // being asked to guard the thing that actually breaks: List<T>.Add from
        // several threads drops entries and can corrupt the backing array.
        var cfg = Activator.CreateInstance(cfgType);
        var mappings = (System.Collections.IList)cfgType.GetProperty("Mappings").GetValue(cfg);
        var mappingType = asm.GetType("Jellyfin.Profiles.Configuration.ProfileMapping", true);

        const int Threads = 8, Each = 500;
        var threads = new List<Thread>();
        Exception blew = null;
        for (var t = 0; t < Threads; t++)
        {
            var th = new Thread(() =>
            {
                try
                {
                    for (var i = 0; i < Each; i++)
                        lock (theLock) mappings.Add(Activator.CreateInstance(mappingType));
                }
                catch (Exception ex) { Interlocked.CompareExchange(ref blew, ex, null); }
            });
            threads.Add(th); th.Start();
        }
        foreach (var th in threads) th.Join();

        Ok("no thread blew up mutating Mappings under ConfigLock", blew == null);
        Ok("every write survived (" + mappings.Count + " of " + (Threads * Each) + ")",
           mappings.Count == Threads * Each);
    }
}

Console.WriteLine();
Console.WriteLine("── Every lock in the plugin, one at a time ─────────────────────");

// A lock target is acceptable only if it is a static object whose lifetime does
// not depend on the configuration. Each entry says why it is allowed, so adding
// to this list is a decision somebody has to write down.
var allowed = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["ConfigLock"]   = "static readonly on ProfilesBaseController; survives a settings save",
    ["AuditLogLock"] = "static readonly; guards the audit_log.json path and file",
    ["JsCacheLock"]  = "static readonly; guards the one-time profiles.js cache",
    ["PatchLock"]    = "static readonly in ProfilesBootstrapTask; guards index.html patching",
    ["_cleanupLock"] = "static readonly in RateLimiter; guards the sweep",
    ["list"]         = "a List<DateTime> held in a ConcurrentDictionary in RateLimiter — the "
                     + "reference is stable for the key's lifetime and is never replaced",
};

var sources = Directory
    .GetFiles(RepoRoot(), "*.cs", SearchOption.AllDirectories)
    .Where(p => !p.Contains(Path.DirectorySeparatorChar + "tests" + Path.DirectorySeparatorChar)
             && !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
             && !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
             && !p.Contains(Path.DirectorySeparatorChar + "scratch" + Path.DirectorySeparatorChar))
    .OrderBy(p => p, StringComparer.Ordinal)
    .ToArray();

Ok("found the plugin sources to scan (" + sources.Length + " files)", sources.Length >= 3);

var rx = new Regex(@"(?<![A-Za-z0-9_])lock\s*\(\s*([^)]*?)\s*\)", RegexOptions.Compiled);
var sites = 0;
foreach (var path in sources)
{
    var lines = File.ReadAllLines(path);
    var rel = path.Substring(RepoRoot().Length).TrimStart(Path.DirectorySeparatorChar);
    for (var i = 0; i < lines.Length; i++)
    {
        var m = rx.Match(lines[i]);
        if (!m.Success) continue;
        sites++;
        var target = m.Groups[1].Value;
        var okHere = allowed.ContainsKey(target);
        Ok(rel + ":" + (i + 1) + "  lock (" + target + ")"
           + (okHere ? "  — " + allowed[target] : "  — NOT a known configuration-independent lock"),
           okHere);
    }
}

Ok("the scan actually found locks (" + sites + " sites) — a regex that matches nothing "
   + "would otherwise pass this whole section", sites >= 20);

Console.WriteLine();
Console.WriteLine("── Every save happens while the lock is held ───────────────────");

// Holding the lock for the mutation and then dropping it before persisting puts the
// read-modify-write and the write-to-disk in different critical sections: two requests
// can interleave so the configuration on disk is neither of the two states either
// request intended. Every SaveConfiguration() must therefore sit inside the ConfigLock
// block that made the change it is saving.
//
// This one was already true when it was written, so unlike the section above it never
// had a failing build to prove itself against. The self-check below is what stands in
// for that: it runs the same detector over a sample with one good call and one bad one
// and requires it to pick out exactly the bad one. A detector that silently stopped
// matching anything would pass the real scan and fail this.
var sample = new[]
{
    "void Good() {",
    "    lock (ConfigLock) {",
    "        Plugin.Instance?.SaveConfiguration();",
    "    }",
    "}",
    "void Bad() {",
    "    lock (ConfigLock) { config.Mappings.Add(m); }",
    "    Plugin.Instance?.SaveConfiguration();",
    "}",
};
var sampleUnguarded = FindUnguardedSaves(sample, rx);
Ok("the detector finds the unguarded save in a known-bad sample, and only that one "
   + "(flagged lines: " + (sampleUnguarded.Count == 0 ? "none" : string.Join(", ", sampleUnguarded)) + ")",
   sampleUnguarded.Count == 1 && sampleUnguarded[0] == 8);

var saveSites = 0;
foreach (var path in sources)
{
    var lines = File.ReadAllLines(path);
    var rel = path.Substring(RepoRoot().Length).TrimStart(Path.DirectorySeparatorChar);
    var unguarded = new HashSet<int>(FindUnguardedSaves(lines, rx));
    for (var i = 0; i < lines.Length; i++)
    {
        if (!lines[i].Contains("SaveConfiguration()")) continue;
        saveSites++;
        Ok(rel + ":" + (i + 1) + "  SaveConfiguration() is inside the ConfigLock block "
           + "that made the change", !unguarded.Contains(i + 1));
    }
}

Ok("the save scan actually found saves (" + saveSites + " sites)", saveSites >= 20);

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

// ── plumbing ────────────────────────────────────────────────────────────────

// Returns the 1-based line numbers of SaveConfiguration() calls that are not
// lexically inside a `lock (ConfigLock)` block. Brace counting, not a parser:
// enough for this codebase's formatting, and it reports lines rather than a
// total so a failure names the call rather than the fact that one exists.
static List<int> FindUnguardedSaves(IReadOnlyList<string> lines, Regex rx)
{
    var bad = new List<int>();
    var depth = 0;
    var openLocks = new List<(string Target, int Depth)>();
    (string Target, int Line)? pending = null;

    for (var i = 0; i < lines.Count; i++)
    {
        var line = lines[i];

        if (line.Contains("SaveConfiguration()") && !openLocks.Any(l => l.Target == "ConfigLock"))
            bad.Add(i + 1);

        var m = rx.Match(line);
        if (m.Success) pending = (m.Groups[1].Value, i + 1);

        var opens = line.Count(c => c == '{');
        var closes = line.Count(c => c == '}');
        if (pending != null && opens > 0)
        {
            openLocks.Add((pending.Value.Target, depth + 1));
            pending = null;
        }
        depth += opens - closes;
        while (openLocks.Count > 0 && depth < openLocks[^1].Depth)
            openLocks.RemoveAt(openLocks.Count - 1);
    }
    return bad;
}

// Runs the interleaving that breaks instance locking, and reports the greatest
// number of threads seen inside the critical section at the same time.
//
//   A: read the config, take the lock, announce, wait to be released
//   main: replace the configuration (what UpdateConfiguration does)
//   B: read the config *again* — now a different object — and take the lock
//
// With the configuration as the lock, B is locking something A never touched.
static int RunTwoThreads(Holder holder, Type cfgType, bool useInstanceAsLock, object lockObject)
{
    var inside = 0;
    var maxInside = 0;
    var aInside = new ManualResetEventSlim(false);
    var bInside = new ManualResetEventSlim(false);
    var release = new ManualResetEventSlim(false);

    void Enter()
    {
        var now = Interlocked.Increment(ref inside);
        int seen;
        do { seen = Volatile.Read(ref maxInside); }
        while (now > seen && Interlocked.CompareExchange(ref maxInside, now, seen) != seen);
    }

    var a = new Thread(() =>
    {
        var c = holder.Cfg;                                  // what a controller does
        lock (useInstanceAsLock ? c : lockObject)
        {
            Enter();
            aInside.Set();
            release.Wait(5000);
            Interlocked.Decrement(ref inside);
        }
    });
    a.Start();
    aInside.Wait(5000);

    holder.Cfg = Activator.CreateInstance(cfgType);          // the administrator saves

    var b = new Thread(() =>
    {
        var c = holder.Cfg;                                  // the next request
        lock (useInstanceAsLock ? c : lockObject)
        {
            Enter();
            bInside.Set();
            Interlocked.Decrement(ref inside);
        }
    });
    b.Start();

    // If exclusion holds, B is still blocked here and this wait times out — which
    // is the passing case. Long enough that a slow CI box cannot fake a pass.
    bInside.Wait(1500);

    release.Set();
    a.Join(5000);
    b.Join(5000);
    return Volatile.Read(ref maxInside);
}

sealed class Holder
{
    private volatile object _cfg;
    public object Cfg { get => _cfg; set => _cfg = value; }
}

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

// Never succeeds at reading, never writes anything. LoadConfiguration falls back
// to a fresh instance, which is exactly what we want, and SaveConfiguration
// becomes a no-op instead of touching the disk.
sealed class StubXml : MediaBrowser.Model.Serialization.IXmlSerializer
{
    public void SerializeToStream(object obj, Stream stream) { }
    public void SerializeToFile(object obj, string file) { }
    public object DeserializeFromFile(Type type, string file)
        => throw new FileNotFoundException("stub serializer", file);
    public object DeserializeFromStream(Type type, Stream stream)
        => throw new NotSupportedException();
    public object DeserializeFromBytes(Type type, byte[] buffer)
        => throw new NotSupportedException();
}
