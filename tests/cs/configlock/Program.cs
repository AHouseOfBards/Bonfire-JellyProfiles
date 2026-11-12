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
//   1. instantiates the real plugin and calls the real UpdateConfiguration, which
//      Jellyfin's base class implements by replacing the instance. The plugin now
//      overrides it to copy onto the instance in use, so this asserts the reference
//      survives the save (P2-25) — the root of the defect below, not just its symptom;
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

// Which build of the plugin to load. This harness's own output sits in
// bin/Release/<tfm>/, so its folder name IS the framework the csproj resolved — there is
// no second place to keep in step, and it cannot say net9.0 while the <Reference> that
// compiled it pointed at net10.0. tests/run.sh cs10 runs the whole set against net10.0.
var tfm = new System.IO.DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
    System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)).Name;
var dll = RepoPath("bin", "Release", tfm, "Jellyfin.Profiles.dll");
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
// A new install starts with Bonfire off (2.0), and off every gate stands down. This harness
// is about a server where it is on.
if (plugin != null)
{
    var startConfig = pluginType.GetProperty("Configuration").GetValue(plugin);
    startConfig.GetType().GetProperty("BonfireOn").SetValue(startConfig, true);
}

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

        // Exactly what Jellyfin's generic configuration save does.
        //
        // These two assertions used to prove the defect: Jellyfin's own UpdateConfiguration
        // assigns the new object, so everything that had read the configuration before
        // taking ConfigLock went on mutating an orphan (P2-25). The plugin now overrides it
        // to copy the saved values onto the instance in use, so the reference never
        // changes — and the question is whether the values arrived.
        var replacement = Activator.CreateInstance(cfgType);
        cfgType.GetProperty("MaxProfilesPerUser").SetValue(replacement, 13);
        // Two more things the override promises that nothing here read: that it persists
        // what it just copied, exactly once, and that it tells subscribers. Dropping
        // either stayed green, because the stub serializer and the event were never looked at.
        var changedCalls = 0;
        // In Jellyfin's BasePlugin it is a delegate property, not an event.
        var changedProp = pluginType.GetProperty("ConfigurationChanged", Any);
        Ok("the plugin exposes ConfigurationChanged", changedProp != null);
        if (changedProp != null)
        {
            var handler = (EventHandler<MediaBrowser.Model.Plugins.BasePluginConfiguration>)((s, c) => changedCalls++);
            var existing = (EventHandler<MediaBrowser.Model.Plugins.BasePluginConfiguration>)changedProp.GetValue(plugin);
            changedProp.SetValue(plugin, (EventHandler<MediaBrowser.Model.Plugins.BasePluginConfiguration>)Delegate.Combine(existing, handler));
        }
        var savesBefore = StubXml.Saves;
        update.Invoke(plugin, new[] { replacement });
        var after = configProp.GetValue(plugin);
        Ok("UpdateConfiguration writes the configuration to disk, once (" + (StubXml.Saves - savesBefore) + ")",
           StubXml.Saves - savesBefore == 1);
        Ok("and raises ConfigurationChanged, once (" + changedCalls + ")", changedCalls == 1);

        Ok("after UpdateConfiguration, Plugin.Instance.Configuration is the SAME object "
           + "(so a reference read before the lock is never an orphan)",
           ReferenceEquals(before, after));
        Ok("and the saved values were copied onto it",
           (int)cfgType.GetProperty("MaxProfilesPerUser").GetValue(after) == 13);

        // R-1128: a posted body that carries no list (null) copied null over the live one,
        // and every route that then read Mappings threw.
        var mappingsProp = cfgType.GetProperty("Mappings");
        var liveMappings = (System.Collections.IList)mappingsProp.GetValue(after);
        liveMappings.Add(Activator.CreateInstance(mappingsProp.PropertyType.GetGenericArguments()[0]));
        var withNulls = Activator.CreateInstance(cfgType);
        foreach (var p in cfgType.GetProperties())
            if (p.CanWrite && p.PropertyType.IsGenericType
                && p.PropertyType.GetGenericTypeDefinition() == typeof(List<>))
                p.SetValue(withNulls, null);
        update.Invoke(plugin, new[] { withNulls });
        var afterNulls = configProp.GetValue(plugin);
        var stillThere = mappingsProp.GetValue(afterNulls) as System.Collections.IList;
        Ok("a posted configuration with null lists leaves the live lists alone",
           stillThere != null && stillThere.Count == 1);
        Ok("every list on the live configuration is still a list",
           cfgType.GetProperties()
               .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(List<>))
               .All(p => p.GetValue(afterNulls) != null));

        // R-1290: one int showed the copy ran, not that it copies everything. Every
        // writable property gets its own value, different from the default, and each is
        // checked one at a time, so a property the copy skips is named. A list carries one
        // element. A property this harness cannot make a distinct value for fails by name
        // rather than being skipped: a new setting of a new type must be taught here.
        // Different from what the instance in use holds NOW, not from a fresh default:
        // the plugin's constructor turns RequireMainPin on, so "true" was no change.
        var fresh = configProp.GetValue(plugin);
        var full = Activator.CreateInstance(cfgType);
        var wanted = new List<PropertyInfo>();
        var noValue = new List<string>();
        foreach (var p in cfgType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!(p.CanRead && p.CanWrite) || p.GetIndexParameters().Length != 0) continue;
            var made = DistinctValue(p.PropertyType, p.GetValue(fresh), p.Name);
            if (made == null || (made.GetType().IsValueType || made is string) && Equals(made, p.GetValue(fresh)))
            { noValue.Add(p.Name + " (" + p.PropertyType.Name + ")"); continue; }
            p.SetValue(full, made);
            wanted.Add(p);
        }
        Ok("a distinct value could be made for every writable property"
           + (noValue.Count == 0 ? "" : "; not for: " + string.Join(", ", noValue)), noValue.Count == 0);
        Ok("there are properties to check (" + wanted.Count + ")", wanted.Count >= 30);

        update.Invoke(plugin, new[] { full });
        var live = configProp.GetValue(plugin);
        foreach (var p in wanted)
        {
            var sent = p.GetValue(full);
            var got = p.GetValue(live);
            bool same;
            if (sent is System.Collections.IList sentList)
            {
                var gotList = got as System.Collections.IList;
                same = gotList != null && gotList.Count == sentList.Count;
                for (var k = 0; same && k < sentList.Count; k++)
                    same = sentList[k] is string ? Equals(sentList[k], gotList[k]) : sentList[k] != null && gotList[k] != null;
            }
            else same = Equals(sent, got);
            Ok("UpdateConfiguration carries " + p.Name + " onto the instance in use", same);
        }

        // Turning Bonfire off or on reaches every reader through Plugin.IsOff at once, except
        // two caches: the viewing-limits index and the tokens the proxy switcher has put down
        // as not ours. admin/settings cleared both; Jellyfin's own configuration save, which
        // lands here, did not, so a switch made there took up to minutes to reach a device.
        var tracker = asm.GetType("Jellyfin.Profiles.Auth.ViewingTracker", true);
        var generation = tracker.GetField("_indexGeneration", Any);
        var notOurs = (System.Collections.IDictionary)asm.GetType("Jellyfin.Profiles.Auth.ProxySwitcher", true)
            .GetField("NotOurs", Any).GetValue(null);
        var onProp = cfgType.GetProperty("BonfireOn");
        var wasOn = (bool)onProp.GetValue(configProp.GetValue(plugin));
        var genBefore = (int)generation.GetValue(null);
        notOurs["harness-token"] = (DateTime.UtcNow.AddMinutes(5), 0);
        var flipped = Activator.CreateInstance(cfgType);
        onProp.SetValue(flipped, !wasOn);
        update.Invoke(plugin, new[] { flipped });
        Ok("a configuration save that turns Bonfire " + (wasOn ? "off" : "on") + " takes effect",
           (bool)onProp.GetValue(configProp.GetValue(plugin)) == !wasOn);
        Ok("and drops the viewing-limits index", (int)generation.GetValue(null) != genBefore);
        Ok("and the apps the proxy switcher put down as not ours", !notOurs.Contains("harness-token"));
        notOurs.Clear();

        // R-1290: the two-thread sections lock on an object this harness fetched, so they
        // prove what `lock` does, and could not go red if the plugin stopped taking it.
        // This runs the real UpdateConfiguration while the harness holds ConfigLock: it
        // must wait. If it locked anything else (the configuration, a private object) or
        // nothing, it walks straight through and this fails.
        var realLock = baseCtl.GetField("ConfigLock", Any).GetValue(null);
        var held = new ManualResetEventSlim(false);
        var letGo = new ManualResetEventSlim(false);
        var holderThread = new Thread(() => { lock (realLock) { held.Set(); letGo.Wait(10000); } });
        holderThread.Start();
        held.Wait(5000);
        var returned = new ManualResetEventSlim(false);
        var callerThread = new Thread(() =>
        {
            try { update.Invoke(plugin, new[] { Activator.CreateInstance(cfgType) }); } catch { }
            returned.Set();
        });
        callerThread.Start();
        var wentThrough = returned.Wait(1500);
        letGo.Set();
        var finishedAfter = returned.Wait(5000);
        holderThread.Join(5000); callerThread.Join(5000);
        Ok("the real UpdateConfiguration waits while ConfigLock is held elsewhere", !wentThrough);
        Ok("and completes once it is released", finishedAfter);
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
// One field, two legal spellings: bare inside ProfilesBaseController, qualified from
// anywhere else. Enumerated instead of prefix-stripped so a different type's ConfigLock
// is still a failure.
static bool IsConfigLock(string target) =>
    target == "ConfigLock" || target == "ProfilesBaseController.ConfigLock";

var allowed = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["ConfigLock"]   = "static readonly on ProfilesBaseController; survives a settings save",
    ["PassLock"]     = "static readonly in Auth.MasterUpdateListener; guards which main accounts are being passed on to their profiles, never held across a call out",
    // The same field, named from outside the class that declares it. Spelled out rather
    // than matched by stripping the qualifier, which would have admitted any type's
    // ConfigLock — and the whole point of this table is that one object is THE lock.
    ["ProfilesBaseController.ConfigLock"]
                     = "the same static readonly field, qualified from another class",
    ["AuditLogLock"] = "static readonly; guards the audit_log.json path and file",
    ["JsCacheLock"]  = "static readonly; guards the one-time profiles.js cache",
    ["PatchLock"]    = "static readonly in ProfilesBootstrapTask; guards index.html patching",
    ["_cleanupLock"] = "static readonly in RateLimiter; guards the sweep",
    ["SaveAndCopyLock"]
                     = "static readonly in Plugin; one configuration save and its .lastgood copy at a time",
    ["CodesLock"]    ="static readonly in Mail.EmailCodes; guards issuing, checking and spending a code",
    ["UsageLock"]    = "static readonly in Auth.ViewingTracker; guards viewing time, extra time and requests",
    ["PickerFileLock"] = "static readonly in Auth.PickerMarks; reading and writing proxy_picker.json, which TVs are in a child's profile",
    ["SaveLock"]     ="static readonly in Auth.ViewingTracker; one write of viewing_usage.json at a time, taken before UsageLock",
    ["BudgetLock"]   = "static readonly in Mail.Notifications; guards the per-account new-device notice budget (R-1056)",
    ["ViewingTracker.UsageLock"]
                     = "the same static readonly field, qualified from the timer and the gate",
    ["list"]         = "a List<DateTime> held in a ConcurrentDictionary in RateLimiter — the "
                     + "reference is stable for the key's lifetime and is never replaced",
};

// The vendored Campground.Shared library (2.0). Its locks are on its own objects, and it
// cannot take Bonfire's configuration at all: it never names a Jellyfin.Profiles type, which
// is asserted below rather than assumed. Allowed by file AND target, not by name, because
// `_gate` here says nothing about a `_gate` Bonfire might add — and a new lock in a future
// copy of the library fails until someone writes down why it is safe.
var sharedAllowed = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["Campground.Shared/src/Contracts/EncoderLedger.cs|leases"]
        = "the registry's leases section, one dictionary for the life of the process",
    ["Campground.Shared/src/Injection/InjectionCoordinator.cs|state"]
        = "the registry's injection.state section; writes the owner and its version together",
    ["Campground.Shared/src/Registry/CampgroundRegistry.cs|AppDomain.CurrentDomain"]
        = "the one object every plugin's load context shares; guards creating the registry",
    ["Campground.Shared/src/Registry/CampgroundLifetime.cs|section"]
        = "the registry's lifetime section, one dictionary every copy agrees on; guards the generation record",
    ["Campground.Shared/src/Registry/CampgroundLifetime.cs|CopyGate"]
        = "static readonly object of this copy; guards its registered host lifetimes",
    ["Campground.Shared/src/Registry/CampgroundLifetime.cs|lifetime section"]
        = "the same lifetime section, named in a comment",
    ["Campground.Shared/src/Security/RateLimiter.cs|_gate"]
        = "readonly object per limiter instance, never replaced",
    ["Campground.Shared/src/Storage/AtomicJsonFile.cs|_gate"]
        = "readonly object per file instance; one read or write of that file at a time",
};

var sources = Directory
    .GetFiles(RepoRoot(), "*.cs", SearchOption.AllDirectories)
    .Where(p => !p.Contains(Path.DirectorySeparatorChar + "tests" + Path.DirectorySeparatorChar)
             && !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
             && !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
             && !p.Contains(Path.DirectorySeparatorChar + "scratch" + Path.DirectorySeparatorChar)
             // Other checkouts of the repo nested under this one, half-edited. Relative to
             // the root, so a run from inside such a checkout still reads its own source.
             && !Path.GetRelativePath(RepoRoot(), p).StartsWith(".claude" + Path.DirectorySeparatorChar))
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
        var relSlash = rel.Replace('\\', '/');
        string why = null;
        if (relSlash.StartsWith("Campground.Shared/", StringComparison.Ordinal))
            sharedAllowed.TryGetValue(relSlash + "|" + target, out why);
        else
            allowed.TryGetValue(target, out why);
        var okHere = why != null;
        Ok(rel + ":" + (i + 1) + "  lock (" + target + ")"
           + (okHere ? "  — " + why : "  — NOT a known configuration-independent lock"),
           okHere);
    }
}

Ok("the scan actually found locks (" + sites + " sites) — a regex that matches nothing "
   + "would otherwise pass this whole section", sites >= 20);

// `lock (x)` is one spelling of taking a monitor. Monitor.Enter(Configuration) and a
// Synchronized method are others, and the scan above never saw them, so the orphaned-
// monitor defect could come back in a form this section could not read.
var otherMonitors = new List<string>();
var monitorRx = new Regex(@"\bMonitor\s*\.\s*(Try)?Enter\b|MethodImplOptions\s*\.\s*Synchronized", RegexOptions.Compiled);
foreach (var path in sources)
{
    var lines = File.ReadAllLines(path);
    for (var i = 0; i < lines.Length; i++)
    {
        if (lines[i].TrimStart().StartsWith("//")) continue;
        if (monitorRx.IsMatch(lines[i]))
            otherMonitors.Add(path.Substring(RepoRoot().Length).TrimStart(Path.DirectorySeparatorChar) + ":" + (i + 1));
    }
}
Ok("nothing in the plugin takes a monitor any way but lock (...), where this section can see it"
   + (otherMonitors.Count == 0 ? "" : ": " + string.Join(", ", otherMonitors)),
   otherMonitors.Count == 0);

// What makes the shared allowances above safe. If a copy of the library ever reaches into
// Bonfire, its locks have to be judged like Bonfire's own.
var sharedFiles = sources.Where(p => Path.GetRelativePath(RepoRoot(), p).Replace('\\', '/')
    .StartsWith("Campground.Shared/", StringComparison.Ordinal)).ToArray();
var reaching = sharedFiles.Where(p => Regex.IsMatch(File.ReadAllText(p),
    @"Jellyfin\.Profiles|PluginConfiguration|Plugin\.Instance|ConfigLock")).ToArray();
Ok("the vendored Campground.Shared never names Bonfire's plugin or configuration ("
   + sharedFiles.Length + " files" + (reaching.Length == 0 ? ")" : "; does: "
   + string.Join(", ", reaching.Select(p => Path.GetFileName(p))) + ")"),
   sharedFiles.Length > 0 && reaching.Length == 0);

Console.WriteLine();
Console.WriteLine("── Reads of the mapping list wait for the lock (r2-11-02) ──────");
{
    // The routes that add and remove mappings do it under ConfigLock, so a reader that
    // enumerates without it can meet "Collection was modified", and the profile-limit
    // count can be torn. Each reader is run while the harness holds the real lock: it must
    // wait. One that walks the list unlocked returns at once.
    var realLock = baseCtl.GetField("ConfigLock", Any).GetValue(null);
    var cfg = Activator.CreateInstance(cfgType);
    var mapT = asm.GetType("Jellyfin.Profiles.Configuration.ProfileMapping", true);
    var list = (System.Collections.IList)cfgType.GetProperty("Mappings").GetValue(cfg);
    var someone = Guid.NewGuid();
    var row = Activator.CreateInstance(mapT);
    mapT.GetProperty("ProfileUserId").SetValue(row, someone);
    mapT.GetProperty("MasterUserId").SetValue(row, someone);
    list.Add(row);

    void WaitsForLock(string name, string method, object[] args)
    {
        var m = baseCtl.GetMethod(method, Any);
        if (m == null) { Ok(name + " (no such method " + method + ")", false); return; }
        var held = new ManualResetEventSlim(false);
        var letGo = new ManualResetEventSlim(false);
        var holder = new Thread(() => { lock (realLock) { held.Set(); letGo.Wait(10000); } });
        holder.Start(); held.Wait(5000);
        var returned = new ManualResetEventSlim(false);
        var caller = new Thread(() => { try { m.Invoke(null, args); } catch { } returned.Set(); });
        caller.Start();
        var through = returned.Wait(1000);
        letGo.Set();
        var after = returned.Wait(5000);
        holder.Join(5000); caller.Join(5000);
        Ok(name + " waits while ConfigLock is held elsewhere", !through && after);
    }

    WaitsForLock("MainHasPin", "MainHasPin", new object[] { someone, cfg });
    WaitsForLock("MappingIn (the main-PIN check's lookup)", "MappingIn", new object[] { cfg, someone });
    WaitsForLock("ProfileCountOf (the profile-limit count)", "ProfileCountOf", new object[] { cfg, someone });
}

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

// R-1290: the detector counted braces per line, strings and comments included. A "{" in
// a string kept a closed lock open (an unguarded save read as guarded); a "}" in one
// closed an open lock early (a guarded save read as unguarded). Each is pinned by a
// sample the old counter gets wrong.
var braceInString = new[]
{
    "void A() {",
    "    lock (ConfigLock) { var s = \"{\"; }",
    "    Plugin.Instance?.SaveConfiguration();",
    "}",
};
var flaggedOpen = FindUnguardedSaves(braceInString, rx);
Ok("a \"{\" inside a string does not keep a finished lock open (flagged: "
   + string.Join(", ", flaggedOpen) + ")", flaggedOpen.Count == 1 && flaggedOpen[0] == 3);

var closeInString = new[]
{
    "void B() {",
    "    lock (ConfigLock) {",
    "        var s = \"}\"; // } and { here are not code",
    "        Plugin.Instance?.SaveConfiguration();",
    "    }",
    "}",
};
var flaggedClose = FindUnguardedSaves(closeInString, rx);
Ok("a \"}\" inside a string or comment does not end the lock early (flagged: "
   + (flaggedClose.Count == 0 ? "none" : string.Join(", ", flaggedClose)) + ")", flaggedClose.Count == 0);

var otherLiterals = new[]
{
    "void C() {",
    "    lock (ConfigLock) {",
    "        var c = '}'; var v = @\"a \"\"}\"\" b\"; var i = $\"{x}}}\";",
    "        /* } */ Plugin.Instance?.SaveConfiguration();",
    "    }",
    "}",
};
var flaggedOther = FindUnguardedSaves(otherLiterals, rx);
Ok("nor do a char literal, a verbatim or interpolated string, or a block comment (flagged: "
   + (flaggedOther.Count == 0 ? "none" : string.Join(", ", flaggedOther)) + ")", flaggedOther.Count == 0);

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

    var inBlockComment = false;
    for (var i = 0; i < lines.Count; i++)
    {
        // The save is looked for in the raw line (a mention in a comment is still reported:
        // the stricter reading), but braces and the lock keyword are read from code only.
        var line = StripNonCode(lines[i], ref inBlockComment);

        if (lines[i].Contains("SaveConfiguration()") && !openLocks.Any(l => IsConfigLock(l.Target)))
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

// A value of this type different from the default, or null when none can be made.
static object DistinctValue(Type t, object def, string name)
{
    var u = Nullable.GetUnderlyingType(t);
    if (u != null) t = u;
    if (t == typeof(bool)) return !(def is bool b && b);
    if (t == typeof(string)) return "saved-" + name;
    if (t == typeof(int)) return (def is int i ? i : 0) + 7;
    if (t == typeof(long)) return (def is long l ? l : 0L) + 7;
    if (t == typeof(double)) return (def is double d ? d : 0d) + 7.5;
    if (t == typeof(Guid)) return Guid.NewGuid();
    if (t == typeof(DateTime)) return new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    if (t.IsEnum)
    {
        foreach (var v in Enum.GetValues(t)) if (!Equals(v, def)) return v;
        return null;
    }
    if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
    {
        var elem = t.GetGenericArguments()[0];
        var list = (System.Collections.IList)Activator.CreateInstance(t);
        var item = elem == typeof(string) ? "saved-" + name
                 : elem == typeof(Guid) ? Guid.NewGuid()
                 : elem == typeof(int) ? 7
                 : elem.GetConstructor(Type.EmptyTypes) != null ? Activator.CreateInstance(elem) : null;
        if (item == null) return null;
        // One more than the live list holds, so a copy that is skipped shows in the count.
        var n = (def as System.Collections.IList)?.Count ?? 0;
        list.Add(item);
        for (var k = 0; k < n; k++)
            list.Add(elem == typeof(string) ? "saved-" + name + k
                   : elem == typeof(Guid) ? Guid.NewGuid()
                   : elem == typeof(int) ? 8 + k
                   : Activator.CreateInstance(elem));
        return list;
    }
    // A nested settings object: a new instance is a different reference from the one the
    // live configuration holds, which is all "it was carried over" needs to tell.
    if (t.IsClass && t.GetConstructor(Type.EmptyTypes) != null) return Activator.CreateInstance(t);
    return null;
}

// A line with its strings (regular, verbatim, interpolated), char literals and comments
// blanked, so that braces and keywords left are code. Strings that run over several lines
// are not handled (this codebase has none a brace could hide in); block comments are.
static string StripNonCode(string line, ref bool inBlockComment)
{
    var sb = new System.Text.StringBuilder(line.Length);
    for (var i = 0; i < line.Length; i++)
    {
        var c = line[i];
        var next = i + 1 < line.Length ? line[i + 1] : '\0';
        if (inBlockComment)
        {
            if (c == '*' && next == '/') { inBlockComment = false; i++; }
            continue;
        }
        if (c == '/' && next == '/') break;
        if (c == '/' && next == '*') { inBlockComment = true; i++; continue; }
        if (c == '"' || ((c == '@' || c == '$') && (next == '"' || ((next == '@' || next == '$') && i + 2 < line.Length && line[i + 2] == '"'))))
        {
            // Opening: any of  "  @"  $"  @$"  $@"
            var verbatim = false;
            while (line[i] != '"') { if (line[i] == '@') verbatim = true; i++; }
            for (i++; i < line.Length; i++)
            {
                if (verbatim) { if (line[i] == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { i++; continue; } break; } }
                else if (line[i] == '\\') i++;
                else if (line[i] == '"') break;
            }
            sb.Append("\"\"");
            continue;
        }
        if (c == '\'')
        {
            for (i++; i < line.Length && line[i] != '\''; i++) if (line[i] == '\\') i++;
            sb.Append("''");
            continue;
        }
        sb.Append(c);
    }
    return sb.ToString();
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
    public static int Saves;
    public void SerializeToFile(object obj, string file) { System.Threading.Interlocked.Increment(ref Saves); }
    public object DeserializeFromFile(Type type, string file)
        => throw new FileNotFoundException("stub serializer", file);
    public object DeserializeFromStream(Type type, Stream stream)
        => throw new NotSupportedException();
    public object DeserializeFromBytes(Type type, byte[] buffer)
        => throw new NotSupportedException();
}
