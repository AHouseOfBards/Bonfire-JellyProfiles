using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
// ─────────────────────────────────────────────────────────────────────────────
// Bonfire's background work stops when the server does.
//
// Jellyfin restarts inside the same process (Program.cs loops StartServer while
// _restartOnShutdown; Campground-Coordination #33). The new host loads a new copy of the
// plugin, and the old copy's background work carried on beside it: the picker's sweep
// twenty seconds after start waited on StartAsync's token, the session listener's second
// policy pass on nothing at all, so both woke after a restart and ran against services the
// old host had disposed.
//
// Checked here:
//   - BackgroundWork exists, cancels its token on stop, waits for running work, and starts
//     nothing after a stop;
//   - its Stopper is the first hosted service registered, so the host stops it last;
//   - every Task.Run in the plugin's own source is inside BackgroundWork.cs, named one at a
//     time;
//   - every Task.Delay in the plugin's own source takes a token, named one at a time.
//
// Against 99598a2: BackgroundWork is missing (the behaviour section stops there), the
// registration check fails, six Task.Run sites fail and one Task.Delay fails.
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
void Section(string s) { Console.WriteLine(); Console.WriteLine("── " + s + " " + new string('─', Math.Max(4, 66 - s.Length))); }

static string RepoRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d != null && !File.Exists(Path.Combine(d.FullName, "Jellyfin.Profiles.csproj")))
        d = d.Parent;
    if (d == null) throw new InvalidOperationException("Could not find the repository root.");
    return d.FullName;
}

var asm = typeof(Jellyfin.Profiles.PluginServiceRegistrator).Assembly;

// ── Behaviour ───────────────────────────────────────────────────────────────
Section("BackgroundWork");
var bw = asm.GetType("Jellyfin.Profiles.Configuration.BackgroundWork");
Ok("this build has BackgroundWork", bw != null);
if (bw != null)
{
    var flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    var run = bw.GetMethods(flags).First(m => m.Name == "Run"
        && m.GetParameters()[0].ParameterType == typeof(Func<CancellationToken, Task>));
    var stop = bw.GetMethod("StopAsync", flags);
    Task Run(Func<CancellationToken, Task> work) => (Task)run.Invoke(null, new object[] { work });
    Task Stop() => (Task)stop.Invoke(null, new object[] { CancellationToken.None });

    // Work that waits on the token, as the picker sweep and the second pass do.
    var sawCancel = false;
    var waiting = Run(async t =>
    {
        try { await Task.Delay(TimeSpan.FromMinutes(10), t); }
        catch (OperationCanceledException) { sawCancel = true; }
    });

    // Work that ignores the token and takes a moment, as an audit log write does.
    var slowDone = false;
    var slow = Run(async _ => { await Task.Delay(400); slowDone = true; });

    var sw = Stopwatch.StartNew();
    Stop().Wait();
    sw.Stop();

    Ok("a stop cancels work waiting on the token", sawCancel && waiting.IsCompleted);
    Ok("a stop waits for work that is still running", slowDone && slow.IsCompleted);
    Ok("and does not wait out a long delay", sw.Elapsed < TimeSpan.FromSeconds(5), sw.Elapsed.ToString());

    var ranLate = false;
    Run(_ => { ranLate = true; return Task.CompletedTask; }).Wait();
    Thread.Sleep(100);
    Ok("nothing is started after a stop", !ranLate);
}

// ── Registration ────────────────────────────────────────────────────────────
Section("Registration");
var services = new ServiceCollection();
new Jellyfin.Profiles.PluginServiceRegistrator().RegisterServices(services, null);
var hosted = services.Where(d => d.ServiceType == typeof(IHostedService)).ToList();
var first = hosted.FirstOrDefault()?.ImplementationType;
Ok("the stopper is the first hosted service, so the host stops it last",
    first != null && first.Name == "Stopper" && first.DeclaringType?.Name == "BackgroundWork",
    "first is " + (first?.FullName ?? "(none)") + " of " + hosted.Count);

// ── Source ──────────────────────────────────────────────────────────────────
var root = RepoRoot();
var sources = Directory
    .GetFiles(root, "*.cs", SearchOption.AllDirectories)
    .Where(p =>
    {
        var rel = Path.GetRelativePath(root, p).Replace('\\', '/');
        return !rel.StartsWith("tests/") && !rel.StartsWith("obj/") && !rel.StartsWith("bin/")
            && !rel.StartsWith(".claude/") && !rel.StartsWith("scratch/")
            // Vendored; shared code is checked in its own repository.
            && !rel.StartsWith("Campground.Shared/");
    })
    .OrderBy(p => p, StringComparer.Ordinal)
    .ToArray();

Section("Task.Run outside BackgroundWork");
Ok("found the plugin sources (" + sources.Length + " files)", sources.Length >= 20);
var runSites = 0;
foreach (var file in sources)
{
    var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
    var lines = File.ReadAllLines(file);
    for (var i = 0; i < lines.Length; i++)
    {
        var code = lines[i].Split("//")[0];
        if (!Regex.IsMatch(code, @"\bTask\.Run\s*\(")) continue;
        runSites++;
        Ok(rel + ":" + (i + 1) + " goes through BackgroundWork", rel == "Configuration/BackgroundWork.cs", lines[i].Trim());
    }
}
Ok("the scan saw Task.Run at all", runSites >= 1);

Section("Task.Delay without a token");
var delaySites = 0;
foreach (var file in sources)
{
    var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
    var lines = File.ReadAllLines(file);
    for (var i = 0; i < lines.Length; i++)
    {
        var code = lines[i].Split("//")[0];
        var m = Regex.Match(code, @"\bTask\.Delay\s*\(([^;]*)\)");
        if (!m.Success) continue;
        delaySites++;
        // A token is a second argument at the top level of the call.
        var callArgs = m.Groups[1].Value;
        int depth = 0, commas = 0;
        foreach (var c in callArgs)
        {
            if (c == '(') depth++;
            else if (c == ')') { if (depth == 0) break; depth--; }
            else if (c == ',' && depth == 0) commas++;
        }
        Ok(rel + ":" + (i + 1) + " can be cancelled", commas >= 1, lines[i].Trim());
    }
}
Ok("the scan saw Task.Delay at all", delaySites >= 1);

Console.WriteLine();
if (fails.Count > 0)
{
    foreach (var f in fails) Console.WriteLine("   - " + f);
    Console.WriteLine(pass + " passed, " + fails.Count + " failed");
    return 1;
}
Console.WriteLine(pass + " passed, 0 failed");
return 0;
