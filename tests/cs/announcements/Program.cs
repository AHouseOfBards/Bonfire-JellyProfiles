using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Xml.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

// ─────────────────────────────────────────────────────────────────────────────
// Announcements: what is valid, what is showing, and that they survive being saved.
//
//  - Validation refuses an empty or long message, an unknown severity, and an end that
//    is not after the start.
//  - "Showing" is start-inclusive and end-exclusive, so an announcement ending at 2:00
//    is gone at 2:00.
//  - Most important first; newest first within a severity.
//  - Saving replaces by id, drops long-expired entries, and never edits the list it was
//    given (the configuration object is shared with every reader).
//  - The configuration is XML on disk. A date must come back as the same instant, and
//    still UTC: a Local date compared with UtcNow is off by the server's offset.
//  - Every route refuses a caller with no token.
//
// Against 1.7.1.2 the first assertion fails: there are no announcements.
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

static string RepoRoot()
{
    var d = AppContext.BaseDirectory;
    while (d != null && !File.Exists(Path.Combine(d, "Jellyfin.Profiles.csproj")))
        d = Path.GetDirectoryName(d);
    return d ?? throw new Exception("could not find the repository root");
}

var root = RepoRoot();
var tfm = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Name;
var asm = Assembly.LoadFrom(Path.Combine(root, "bin", "Release", tfm, "Jellyfin.Profiles.dll"));

var annType = asm.GetType("Jellyfin.Profiles.Configuration.Announcement");
var rules = asm.GetType("Jellyfin.Profiles.Configuration.Announcements");
Ok("this build has announcements", annType != null && rules != null);
if (annType == null || rules == null) return Finish();

var t0 = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

object Make(string message, string severity = "info", DateTime? starts = null, DateTime? ends = null,
            Guid? id = null, DateTime? created = null)
{
    var a = Activator.CreateInstance(annType);
    annType.GetProperty("Id").SetValue(a, id ?? Guid.NewGuid());
    annType.GetProperty("Message").SetValue(a, message);
    annType.GetProperty("Severity").SetValue(a, severity);
    annType.GetProperty("StartsUtc").SetValue(a, starts);
    annType.GetProperty("EndsUtc").SetValue(a, ends);
    annType.GetProperty("CreatedUtc").SetValue(a, created ?? t0);
    return a;
}
T Get<T>(object a, string name) => (T)annType.GetProperty(name).GetValue(a);
IList NewList(params object[] items)
{
    var l = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(annType));
    foreach (var i in items) l.Add(i);
    return l;
}

Console.WriteLine();
Console.WriteLine("── Validation ──────────────────────────────────────────────────");

string Validate(object a) => (string)rules.GetMethod("Validate").Invoke(null, new[] { a });
Ok("a plain message is accepted", Validate(Make("Restarting at 2am.")) == null);
Ok("280 characters is accepted", Validate(Make(new string('x', 280))) == null);
Ok("281 characters is refused", Validate(Make(new string('x', 281))) != null);
Ok("an empty message is refused", Validate(Make("")) != null);
Ok("a blank message is refused", Validate(Make("   ")) != null);
Ok("each severity is accepted",
   new[] { "info", "warning", "critical" }.All(s => Validate(Make("hi", s)) == null));
Ok("an unknown severity is refused", Validate(Make("hi", "red")) != null);
Ok("a severity in capitals is refused (the route lower-cases first)", Validate(Make("hi", "Warning")) != null);
Ok("an end equal to the start is refused", Validate(Make("hi", "info", t0, t0)) != null);
Ok("an end before the start is refused", Validate(Make("hi", "info", t0, t0.AddMinutes(-1))) != null);
Ok("an end with no start is accepted", Validate(Make("hi", "info", null, t0)) == null);

Console.WriteLine();
Console.WriteLine("── Showing ─────────────────────────────────────────────────────");

bool IsActive(object a, DateTime now) => (bool)rules.GetMethod("IsActive").Invoke(null, new[] { a, (object)now });
Ok("no dates: showing", IsActive(Make("a"), t0));
Ok("starts now: showing", IsActive(Make("a", "info", t0), t0));
Ok("starts in a minute: not yet", !IsActive(Make("a", "info", t0.AddMinutes(1)), t0));
Ok("ends now: gone", !IsActive(Make("a", "info", null, t0), t0));
Ok("ends in a minute: showing", IsActive(Make("a", "info", null, t0.AddMinutes(1)), t0));

var info1 = Make("info old", "info", created: t0.AddHours(-3));
var info2 = Make("info new", "info", created: t0.AddHours(-1));
var warn = Make("warning", "warning", created: t0.AddHours(-5));
var crit = Make("critical", "critical", created: t0.AddHours(-9));
var future = Make("later", "critical", t0.AddHours(1));
var active = (IEnumerable)rules.GetMethod("Active").Invoke(null, new object[] { NewList(info1, warn, info2, future, crit), t0 });
var order = active.Cast<object>().Select(a => Get<string>(a, "Message")).ToList();
Ok("most important first, newest first within each, future ones left out",
   order.SequenceEqual(new[] { "critical", "warning", "info new", "info old" }), string.Join(", ", order));

var startedLater = Make("started later", "info", t0.AddHours(-1), created: t0.AddHours(-10));
var createdLater = Make("created later", "info", created: t0.AddHours(-2));
active = (IEnumerable)rules.GetMethod("Active").Invoke(null, new object[] { NewList(createdLater, startedLater), t0 });
order = active.Cast<object>().Select(a => Get<string>(a, "Message")).ToList();
Ok("a scheduled one counts as new from when it starts",
   order.SequenceEqual(new[] { "started later", "created later" }), string.Join(", ", order));

var none = (IEnumerable)rules.GetMethod("Active").Invoke(null, new object[] { null, t0 });
Ok("no list is no announcements", !none.Cast<object>().Any());

Console.WriteLine();
Console.WriteLine("── Saving ──────────────────────────────────────────────────────");

var with = rules.GetMethod("With");
var keepId = Guid.NewGuid();
var kept = Make("kept", id: keepId);
var recent = Make("ended 29 days ago", "info", null, t0.AddDays(-29));
var stale = Make("ended 31 days ago", "info", null, t0.AddDays(-31));
var original = NewList(kept, recent, stale);
var replacement = Make("replaced", id: keepId);
var next = ((IEnumerable)with.Invoke(null, new[] { original, replacement, (object)t0 })).Cast<object>().ToList();
var messages = next.Select(a => Get<string>(a, "Message")).OrderBy(m => m).ToList();
Ok("replaces by id, keeps a recent expiry, drops an old one",
   messages.SequenceEqual(new[] { "ended 29 days ago", "replaced" }), string.Join(", ", messages));
Ok("the list it was given is untouched",
   original.Count == 3 && Get<string>(original[0], "Message") == "kept");
Ok("returns a new list, not the same object", !ReferenceEquals(next, original));

var fromNothing = ((IEnumerable)with.Invoke(null, new[] { null, Make("first"), (object)t0 })).Cast<object>().ToList();
Ok("saving into no list starts one", fromNothing.Count == 1);

DateTime? ToUtc(DateTime? d) => (DateTime?)rules.GetMethod("ToUtc").Invoke(null, new object[] { d });
var unspecified = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Unspecified);
Ok("a date with no zone is read as UTC",
   ToUtc(unspecified) is DateTime u && u.Kind == DateTimeKind.Utc && u.Ticks == unspecified.Ticks);
var local = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Local);
Ok("a local date is converted", ToUtc(local) == local.ToUniversalTime());
Ok("no date stays none", ToUtc(null) == null);

Console.WriteLine();
Console.WriteLine("── Stored as XML ───────────────────────────────────────────────");

// Jellyfin writes plugin configuration with XmlSerializer. Round-trip a real
// PluginConfiguration the same way.
var cfgType = asm.GetType("Jellyfin.Profiles.Configuration.PluginConfiguration", true);
var cfg = Activator.CreateInstance(cfgType);
var listProp = cfgType.GetProperty("Announcements");
Ok("the configuration has an announcement list", listProp != null);
if (listProp != null)
{
    Ok("it starts empty, not missing", listProp.GetValue(cfg) is IList l0 && l0.Count == 0);
    var stored = Make("Restarting at 2am.", "warning", t0, t0.AddHours(2));
    listProp.SetValue(cfg, NewList(stored));

    var xs = new XmlSerializer(cfgType);
    var sw = new StringWriter();
    xs.Serialize(sw, cfg);
    var back = xs.Deserialize(new StringReader(sw.ToString()));
    var backList = (IList)listProp.GetValue(back);
    Ok("one announcement comes back", backList.Count == 1, backList.Count.ToString());
    if (backList.Count == 1)
    {
        var a = backList[0];
        Ok("its id, message and severity survive",
           Get<Guid>(a, "Id") == Get<Guid>(stored, "Id") && Get<string>(a, "Message") == "Restarting at 2am."
           && Get<string>(a, "Severity") == "warning");
        var s = Get<DateTime?>(a, "StartsUtc");
        var e = Get<DateTime?>(a, "EndsUtc");
        Ok("its start is the same instant", s.HasValue && s.Value.ToUniversalTime() == t0, s?.ToString("o"));
        Ok("and is still UTC, so it compares with UtcNow correctly",
           s.HasValue && s.Value.Kind == DateTimeKind.Utc && s.Value.Ticks == t0.Ticks, s?.ToString("o") + " " + s?.Kind);
        Ok("its end is the same instant and UTC",
           e.HasValue && e.Value.Kind == DateTimeKind.Utc && e.Value.Ticks == t0.AddHours(2).Ticks, e?.ToString("o"));
        Ok("so it is showing at the same moments",
           IsActive(a, t0.AddHours(1)) && !IsActive(a, t0.AddHours(2)) && !IsActive(a, t0.AddMinutes(-1)));
    }
}

Console.WriteLine();
Console.WriteLine("── Routes ──────────────────────────────────────────────────────");

var ctrlType = asm.GetType("Jellyfin.Profiles.Controllers.ProfilesController", true);
var ctor = ctrlType.GetConstructors().First();
var ctorArgs = ctor.GetParameters().Select(p => p.ParameterType.Name.StartsWith("ILogger")
    ? Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(ctrlType)) : null).ToArray();
ControllerBase NewController()
{
    var c = (ControllerBase)ctor.Invoke(ctorArgs);
    c.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
    return c;
}
int StatusOf(object result)
{
    var r = result.GetType().GetProperty("Result")?.GetValue(result) ?? result;
    return r switch
    {
        ObjectResult o => o.StatusCode ?? 200,
        StatusCodeResult sc => sc.StatusCode,
        _ => -1,
    };
}

var saveReq = asm.GetType("Jellyfin.Profiles.Models.SaveAnnouncementRequest");
var delReq = asm.GetType("Jellyfin.Profiles.Models.DeleteAnnouncementRequest");
foreach (var (name, template, verb, arg) in new (string, string, string, Func<object>)[]
{
    ("GetAllAnnouncements", "admin/announcements", "HttpGetAttribute", null),
    ("SaveAnnouncement", "admin/announcements", "HttpPostAttribute", () => saveReq == null ? null : Activator.CreateInstance(saveReq)),
    ("DeleteAnnouncement", "admin/announcements/delete", "HttpPostAttribute", () => delReq == null ? null : Activator.CreateInstance(delReq)),
})
{
    var m = ctrlType.GetMethod(name);
    Ok(name + " exists", m != null);
    if (m == null) continue;
    var attr = m.GetCustomAttributes().FirstOrDefault(a => a.GetType().Name == verb);
    Ok("  at " + verb.Replace("Attribute", "").Replace("Http", "").ToUpperInvariant() + " /" + template,
       (string)attr?.GetType().GetProperty("Template")?.GetValue(attr) == template);
    var result = m.Invoke(NewController(), arg == null ? null : new[] { arg() });
    Ok("  refuses a caller with no token", StatusOf(result) == 401, StatusOf(result).ToString());
}

return Finish();

int Finish()
{
    Console.WriteLine();
    if (fails.Count > 0)
    {
        foreach (var f in fails) Console.WriteLine("   - " + f);
        Console.WriteLine(pass + " passed, " + fails.Count + " failed");
        return 1;
    }
    Console.WriteLine(pass + " passed, 0 failed");
    return 0;
}
