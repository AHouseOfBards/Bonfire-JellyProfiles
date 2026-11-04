using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;

// ─────────────────────────────────────────────────────────────────────────────
// Machine-readable errors and GET /info, the two things a native app needs before it can
// be written against Bonfire without matching on English text.
//
//  - Every error from a Bonfire route carries X-Bonfire-Error.
//  - A refusal to a signed-in caller is 403, so "your token is dead" (401) means one thing.
//  - Every literal error message in the controllers is checked, one by one, against the
//    codes: none may fall through to the code for a different status, and the ones an
//    app acts on must have their own.
//  - GET /info answers without a token and says only server-wide things.
//
// Against 1.7.1.1 the first assertion fails: there is no ErrorCodes and no header.
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

var codes = asm.GetType("Jellyfin.Profiles.Controllers.ErrorCodes");
var filterType = asm.GetType("Jellyfin.Profiles.Controllers.BonfireErrorsAttribute");
Ok("there is one place that gives errors a code", codes != null && filterType != null, "no ErrorCodes in this build");
if (codes == null || filterType == null) return Finish();

var forMethod = codes.GetMethod("For");
string For(int status, string message) => (string)forMethod.Invoke(null, new object[] { status, message });

Console.WriteLine();
Console.WriteLine("── The filter, on real results ─────────────────────────────────");

(int status, string code) Run(IActionResult result, string preset = null)
{
    var http = new DefaultHttpContext();
    if (preset != null) http.Response.Headers["X-Bonfire-Error"] = preset;
    var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
    var ctx = new ResultExecutingContext(action, new List<IFilterMetadata>(), result, controller: null);
    var filter = (IResultFilter)Activator.CreateInstance(filterType);
    filter.OnResultExecuting(ctx);
    var status = ctx.Result switch
    {
        ObjectResult o => o.StatusCode ?? 0,
        StatusCodeResult s => s.StatusCode,
        _ => 0,
    };
    return (status, http.Response.Headers["X-Bonfire-Error"].ToString());
}

var dead = Run(new UnauthorizedResult());
Ok("a rejected token stays 401, coded unauthenticated", dead == (401, "unauthenticated"), dead.ToString());

var notMain = Run(new UnauthorizedObjectResult("Only the master profile can manage profiles."));
Ok("a signed-in caller who isn't allowed gets 403, not 401", notMain.status == 403, notMain.ToString());
Ok("coded not_main_account", notMain.code == "not_main_account", notMain.code);

var pin = Run(new BadRequestObjectResult("Invalid PIN code."));
Ok("a wrong PIN is 400 pin_wrong", pin == (400, "pin_wrong"), pin.ToString());

var missing = Run(new NotFoundResult());
Ok("a bare 404 is not_found", missing == (404, "not_found"), missing.ToString());

var limited = Run(new ObjectResult("Too many failed PIN attempts.") { StatusCode = 429 });
Ok("a 429 is rate_limited", limited == (429, "rate_limited"), limited.ToString());

// Maintenance sets its own: the message is the administrator's words, with nothing to match.
var own = Run(new ObjectResult("Back at 3.") { StatusCode = 503 }, preset: "maintenance");
Ok("a code the route set is kept", own == (503, "maintenance"), own.ToString());

var fine = Run(new OkObjectResult("done"));
Ok("a success carries no error code", fine.code == "" && fine.status == 200, fine.ToString());

Console.WriteLine();
Console.WriteLine("── Every message in the controllers ────────────────────────────");

// Read the source, not a list: a message added tomorrow is checked tomorrow.
var calls = new Regex(@"\b(BadRequest|Unauthorized|NotFound|TooManyAttempts|StatusCode)\((.*?)\)\s*;", RegexOptions.Singleline);
var literal = new Regex(@"\$?""((?:[^""\\]|\\.)*)""");
var seen = new List<(string kind, string text)>();
foreach (var file in Directory.GetFiles(Path.Combine(root, "Controllers"), "*.cs"))
{
    foreach (Match m in calls.Matches(File.ReadAllText(file)))
    {
        var text = string.Concat(literal.Matches(m.Groups[2].Value).Select(l => l.Groups[1].Value));
        if (text.Length > 0) seen.Add((m.Groups[1].Value, text));
    }
}

Ok("the controllers were read", seen.Count > 60, seen.Count + " messages");

// A message refusing a signed-in caller must never be coded as a dead token.
var badForbidden = seen.Where(s => s.kind == "Unauthorized" && For(401, s.text) == "unauthenticated").ToList();
Ok("no refusal with a message is coded unauthenticated", badForbidden.Count == 0, string.Join(" | ", badForbidden.Select(b => b.text)));

// The ones an app branches on, each with its own code.
var wanted = new Dictionary<string, string>
{
    ["Only the master profile can"] = "not_main_account",
    ["Only administrators can"] = "admin_only",
    ["Unauthorized profile"] = "not_your_profile",
    ["Invalid PIN"] = "pin_wrong",
    ["PIN code must be"] = "pin_format",
    ["Maximum profile limit"] = "profile_limit",
    ["already exists"] = "name_taken",
    ["account is turned off"] = "profile_disabled",
    ["Invalid Bonfire Code"] = "bonfire_code_wrong",
    ["does not let profiles choose a theme"] = "themes_locked",
    ["No such announcement"] = "announcement_not_found",
    ["Delete an announcement first"] = "announcement_limit",
    ["maintenance message can be at most"] = "maintenance_invalid",
    ["time maintenance ends must be"] = "maintenance_invalid",
};
foreach (var (contains, code) in wanted)
{
    var matches = seen.Where(s => s.text.Contains(contains)).ToList();
    var wrong = matches.Where(s => For(s.kind == "Unauthorized" ? 403 : s.kind == "NotFound" ? 404 : 400, s.text) != code).ToList();
    Ok("\"" + contains + "…\" is " + code, matches.Count > 0 && wrong.Count == 0,
       matches.Count == 0 ? "no such message any more" : string.Join(" | ", wrong.Select(w => w.text)));
}

// Messages that are built elsewhere and handed in as a variable.
foreach (var (text, code) in new[]
{
    ("This profile is not allowed on this device.", "device_not_allowed"),
    ("This device did not identify itself, so it cannot be checked against the profile's device list.", "device_not_allowed"),
    ("This profile isn't allowed on this device.", "device_not_allowed"),
    ("This profile has too many active sessions.", "too_many_sessions"),
    ("Enter a name for the profile.", "name_invalid"),
    ("This profile has no PIN, so it cannot be opened from a shared Bonfire. Its owner must…", "pin_needed_for_bonfire"),
})
{
    Ok("\"" + text[..Math.Min(40, text.Length)] + "…\" is " + code, For(400, text) == code, For(400, text));
}

// Announcement refusals come from Announcements.Validate, handed in as a variable. Ask the
// validator for each one rather than copying its wording here, so a reworded message is
// checked as it now reads.
var annType = asm.GetType("Jellyfin.Profiles.Configuration.Announcement");
var annRules = asm.GetType("Jellyfin.Profiles.Configuration.Announcements");
Ok("announcements exist in this build", annType != null && annRules != null);
if (annType != null && annRules != null)
{
    var validate = annRules.GetMethod("Validate");
    object Make(string message, string severity, DateTime? starts, DateTime? ends)
    {
        var a = Activator.CreateInstance(annType);
        annType.GetProperty("Message").SetValue(a, message);
        annType.GetProperty("Severity").SetValue(a, severity);
        annType.GetProperty("StartsUtc").SetValue(a, starts);
        annType.GetProperty("EndsUtc").SetValue(a, ends);
        return a;
    }
    var t0 = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
    foreach (var (label, bad) in new[]
    {
        ("an empty message", Make("  ", "info", null, null)),
        ("a long message", Make(new string('x', 281), "info", null, null)),
        ("an unknown severity", Make("hi", "red", null, null)),
        ("an end before the start", Make("hi", "info", t0, t0.AddHours(-1))),
    })
    {
        var msg = (string)validate.Invoke(null, new[] { bad });
        Ok(label + " is announcement_invalid", msg != null && For(400, msg) == "announcement_invalid", msg + " → " + (msg == null ? "" : For(400, msg)));
    }
}

Console.WriteLine();
Console.WriteLine("── GET /info ───────────────────────────────────────────────────");

var ctrlType = asm.GetType("Jellyfin.Profiles.Controllers.ProfilesController", true);
var info = ctrlType.GetMethod("GetInfo");
Ok("the route exists", info != null);
if (info != null)
{
    var get = info.GetCustomAttributes().FirstOrDefault(a => a.GetType().Name == "HttpGetAttribute");
    Ok("at /plugins/profiles/info", (string)get?.GetType().GetProperty("Template")?.GetValue(get) == "info");

    var ctor = ctrlType.GetConstructors().First();
    var ctorArgs = ctor.GetParameters().Select(p => p.ParameterType.Name.StartsWith("ILogger")
        ? Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(ctrlType)) : null).ToArray();
    var controller = (ControllerBase)ctor.Invoke(ctorArgs);
    controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

    var result = info.Invoke(controller, null);
    var value = (result.GetType().GetProperty("Result")?.GetValue(result) as ObjectResult)?.Value;
    var json = JsonSerializer.Serialize(value);
    Ok("answers without a signed-in caller", value != null, json);
    Ok("names itself and its version", json.Contains("\"Name\":\"Bonfire\"") && json.Contains("\"Version\":\""), json);
    Ok("carries an API version", json.Contains("\"ApiVersion\":1"), json);
    // Matched key by key: a substring test caught "SkipQuickConnectOnKnownDevices" as a
    // device list, and "ClientPinLogin" as a PIN.
    var keys = new List<string>();
    void Walk(JsonElement e, string at)
    {
        if (e.ValueKind != JsonValueKind.Object) return;
        foreach (var p in e.EnumerateObject())
        {
            keys.Add(at + p.Name);
            Walk(p.Value, at + p.Name + ".");
        }
    }
    Walk(JsonDocument.Parse(json).RootElement, "");
    var allowed = new[]
    {
        "Name", "Version", "ApiVersion", "Disabled", "Features",
        "Features.ClientPinLogin", "Features.ClientProfileList", "Features.SkipQuickConnectOnKnownDevices",
        "Features.ProfileThemes", "Features.ProxyClients",
        // A server-wide setting, so a client knows to ask for a main PIN to be set.
        "Features.RequireMainPin",
        // Null unless closed for maintenance, which this harness never is.
        "Maintenance",
    };
    Ok("and nothing about any household: exactly the documented fields",
       keys.OrderBy(k => k).SequenceEqual(allowed.OrderBy(k => k)), string.Join(", ", keys));
    Ok("is not cached", controller.Response.Headers["Cache-Control"].ToString() == "no-store");
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
