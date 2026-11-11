using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

// ─────────────────────────────────────────────────────────────────────────────
// The audit log file (audit_log.json).
//
// R-1120: RecordAuditLog copied the list under the lock and wrote the copy later, on a
//   background task. Two switches close together meant two writes, and whichever took
//   the lock last won, which could be the older copy. The file then lacked the newest
//   entry until the next switch.
// R-1121: a file that would not parse was read as empty, and the next switch wrote a
//   one-entry log over it. A damaged log is evidence; it is moved aside, not replaced.
//
// Both drive the real methods against a real file.
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

var tfm = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Name;
var asm = Assembly.LoadFrom(Path.Combine(RepoRoot(), "bin", "Release", tfm, "Jellyfin.Profiles.dll"));
var ctrlType = asm.GetType("Jellyfin.Profiles.Controllers.ProfilesController", true);
var baseType = asm.GetType("Jellyfin.Profiles.Controllers.ProfilesBaseController", true);

const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance
                       | BindingFlags.NonPublic | BindingFlags.Public;

var pathField = baseType.GetField("AuditLogPath", Any);
var cacheField = baseType.GetField("_auditCache", Any);
var record = baseType.GetMethod("RecordAuditLog", Any);
var read = baseType.GetMethod("ReadAuditLogs", Any);
Ok("the audit log members are where the harness expects",
   pathField != null && cacheField != null && record != null && read != null);
var lockObj = baseType.GetField("AuditLogLock", Any).GetValue(null);

ControllerBase NewController()
{
    var c = (ControllerBase)FormatterServices.GetUninitializedObject(ctrlType);
    baseType.GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(c, NullLogger.Instance);
    c.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
    return c;
}

int Count(string path)
{
    try
    {
        var entries = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(File.ReadAllText(path));
        return entries == null ? -1 : entries.Count;
    }
    catch { return -1; }
}

var dir = Path.Combine(Path.GetTempPath(), "bonfire-audit-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
try
{
    Console.WriteLine();
    Console.WriteLine("── The file ends up holding the newest entry (R-1120) ──────────");

    // Two entries recorded while the lock is held, so both background writes queue behind
    // it and are released together: which runs first is up to the scheduler. The old code
    // wrote each one's own copy, so about one run in two left the one-entry copy on disk.
    var bad = 0;
    for (var i = 0; i < 80 && bad < 3; i++)
    {
        var path = Path.Combine(dir, "log" + i + ".json");
        pathField.SetValue(null, path);
        cacheField.SetValue(null, null);
        var c = NewController();
        lock (lockObj)
        {
            record.Invoke(c, new object[] { "main", "first" });
            record.Invoke(c, new object[] { "main", "second" });
        }
        for (var w = 0; w < 40 && Count(path) != 2; w++) Thread.Sleep(10);
        Thread.Sleep(30); // a write still in flight lands here
        if (Count(path) != 2) bad++;
    }
    Ok("every run leaves both entries on disk (" + bad + " runs lost one)", bad == 0);

    Console.WriteLine();
    Console.WriteLine("── A log that will not parse is kept (R-1121) ──────────────────");

    var damaged = Path.Combine(dir, "damaged.json");
    File.WriteAllText(damaged, "[{\"Timestamp\": \"2026-01-01T00:00:00Z\", \"MasterUser");
    pathField.SetValue(null, damaged);
    cacheField.SetValue(null, null);
    var rc = NewController();
    var got = (System.Collections.IList)read.Invoke(rc, null);
    Ok("it reads as empty", got != null && got.Count == 0);
    Ok("the damaged file is kept as .bad", File.Exists(damaged + ".bad")
        && File.ReadAllText(damaged + ".bad").Contains("MasterUser"));
    Ok("and is no longer where the next write would replace it", !File.Exists(damaged));

    var good = Path.Combine(dir, "good.json");
    File.WriteAllText(good, "[]");
    pathField.SetValue(null, good);
    read.Invoke(rc, null);
    Ok("a log that parses is left alone", File.Exists(good) && !File.Exists(good + ".bad"));

    Console.WriteLine();
    Console.WriteLine("── Entries age out, and an administrator can clear the log (R-1313) ──");

    // The log names accounts, devices and addresses. Newest-1000 alone kept a quiet
    // server's entries for years, so each also expires by age. Every case here starts
    // from a fresh cache.
    var snapshot = baseType.GetMethod("GetAuditLogSnapshot", Any);
    var clear = baseType.GetMethod("ClearAuditLog", Any);
    var maxAgeField = baseType.GetField("AuditLogMaxAge", Any);
    Ok("the age limit and the clear method are where the harness expects",
       snapshot != null && clear != null && maxAgeField != null);
    var maxAge = maxAgeField != null ? (TimeSpan)maxAgeField.GetValue(null) : TimeSpan.FromDays(90);
    Ok("the age limit is a quarter, not forever", maxAge >= TimeSpan.FromDays(30) && maxAge <= TimeSpan.FromDays(366));

    string Entry(string who, DateTime at) =>
        "{\"Timestamp\":\"" + at.ToString("o") + "\",\"MasterUsername\":\"" + who
        + "\",\"TargetUsername\":\"kid\",\"DeviceName\":\"TV\",\"Client\":\"Web\",\"IpAddress\":\"192.0.2.1\"}";
    int Shown(ControllerBase c) =>
        snapshot == null ? -1 : ((System.Collections.IEnumerable)snapshot.Invoke(c, null)).Cast<object>().Count();

    var aged = Path.Combine(dir, "aged.json");
    File.WriteAllText(aged, "[" + Entry("stale", DateTime.UtcNow - maxAge - TimeSpan.FromDays(10)) + ","
        + Entry("fresh", DateTime.UtcNow - TimeSpan.FromDays(1)) + "]");
    pathField.SetValue(null, aged);
    cacheField.SetValue(null, null);
    var ac = NewController();
    Ok("an entry past the age limit is not shown", Shown(ac) == 1);

    // Recording prunes too, so the file itself shrinks, not just the view.
    record.Invoke(ac, new object[] { "main", "next" });
    for (var w = 0; w < 100 && Count(aged) != 2; w++) Thread.Sleep(10);
    Ok("and the file loses it the next time it is written", Count(aged) == 2);

    File.WriteAllText(aged, "[" + Entry("a", DateTime.UtcNow) + "," + Entry("b", DateTime.UtcNow) + "]");
    cacheField.SetValue(null, null);
    Shown(ac);
    if (clear != null) clear.Invoke(ac, null);
    Ok("clearing empties what the dashboard sees", clear != null && Shown(ac) == 0);
    Ok("and the file, at once", clear != null && Count(aged) == 0);

    // With no plugin instance there is no data folder. The log used to go to a file in the
    // system temp folder, a copy of usernames and addresses nobody would think to look for.
    var fallback = Path.Combine(Path.GetTempPath(), "bonfire_audit_log_fallback.json");
    try { File.Delete(fallback); } catch { }
    pathField.SetValue(null, null);
    cacheField.SetValue(null, null);
    record.Invoke(NewController(), new object[] { "main", "nowhere" });
    Thread.Sleep(300);
    Ok("with no data folder nothing is written to the temp folder", !File.Exists(fallback));
}
finally
{
    pathField.SetValue(null, null);
    Thread.Sleep(100);
    try { Directory.Delete(dir, true); } catch { }
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
