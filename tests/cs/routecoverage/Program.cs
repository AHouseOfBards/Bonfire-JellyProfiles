using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

// Routes no harness called (R-1288).
//
// Each was reachable only through a live server: the emergency disable (Panic, SetPanicCode,
// GetPanicState), the PIN check behind the web switcher (VerifyPin), and the administrator's
// SetProfileLimit, SetMaintenance, SignOutSession and CleanupOrphans. The real controller
// methods are called against a modelled Jellyfin and what they answer and change is read back.
// Import and export are left to R-1146, which covers them as a round trip.
//
// A coverage harness can't fail against old code that behaves the same, so each route group
// was shown red against a deliberate local break, one per group, listed in the commit that
// added this file.
//
// Usage: dotnet run -c Release   (loads bin/Release/<tfm>/Jellyfin.Profiles.dll)

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
        d = Path.GetDirectoryName(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    return d ?? throw new Exception("could not find the repository root");
}

var tfm = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Name;
var asm = Assembly.LoadFrom(Path.Combine(RepoRoot(), "bin", "Release", tfm, "Jellyfin.Profiles.dll"));

var ctrlType = asm.GetType("Jellyfin.Profiles.Controllers.ProfilesController", true);
var pluginType = asm.GetType("Jellyfin.Profiles.Plugin", true);
var cfgType = asm.GetType("Jellyfin.Profiles.Configuration.PluginConfiguration", true);
var mapType = asm.GetType("Jellyfin.Profiles.Configuration.ProfileMapping", true);
var hasher = asm.GetType("Jellyfin.Profiles.Auth.PinHasher", true);
var flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

var tempDir = Path.Combine(Path.GetTempPath(), "bonfire-routecoverage-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
var plugin = Activator.CreateInstance(pluginType, new StubPaths(tempDir), new StubXml());
var config = pluginType.GetProperty("Configuration").GetValue(plugin);
// A new install starts with Bonfire off (2.0) and every gate stands down, so a refusal
// would prove nothing. This harness is about a server where it is on.
config.GetType().GetProperty("BonfireOn").SetValue(config, true);
var mappings = (IList)cfgType.GetProperty("Mappings").GetValue(config);
object Cfg(string name) => cfgType.GetProperty(name).GetValue(config);

var world = new World();

object Map(Guid profile, Guid master, string pin = null)
{
    var m = Activator.CreateInstance(mapType);
    mapType.GetProperty("ProfileUserId").SetValue(m, profile);
    mapType.GetProperty("MasterUserId").SetValue(m, master);
    mapType.GetProperty("ProfileName").SetValue(m, "p" + profile.ToString("N").Substring(0, 4));
    if (pin != null) mapType.GetProperty("PinHash").SetValue(m, hasher.GetMethod("Hash").Invoke(null, new object[] { pin }));
    mappings.Add(m);
    return m;
}
bool Mapped(Guid id) => mappings.Cast<object>().Any(m => (Guid)mapType.GetProperty("ProfileUserId").GetValue(m) == id);

// One call into the real controller, as `caller` (null: nobody signed in) from `ip`.
(int status, object value) Call(string method, Guid? caller, object body = null, string ip = "203.0.113.7", object[] raw = null, string forwardedFor = null)
{
    var route = ctrlType.GetMethod(method);
    if (route == null) return (-404, "no route " + method);

    var ctor = ctrlType.GetConstructors().First();
    var args = ctor.GetParameters().Select(p =>
        p.ParameterType.IsInterface && !p.ParameterType.Name.StartsWith("ILogger")
            ? World.Create(p.ParameterType, world)
            : p.ParameterType.Name.StartsWith("ILogger") ? Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(ctrlType))
            : null).ToArray();
    var c = (ControllerBase)ctor.Invoke(args);
    var http = new DefaultHttpContext
    {
        User = caller == null
            ? new ClaimsPrincipal(new ClaimsIdentity())
            : new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", caller.Value.ToString()) }, "test")),
    };
    http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);
    if (forwardedFor != null)
    {
        // A proxy the server trusts, in front of the caller: what Jellyfin's own options say.
        var fwd = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions { ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor };
        fwd.KnownProxies.Add(System.Net.IPAddress.Parse(ip));
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, Microsoft.Extensions.Options.Options.Create(fwd));
        http.RequestServices = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        http.Request.Headers["X-Forwarded-For"] = forwardedFor;
    }
    c.ControllerContext = new ControllerContext { HttpContext = http };
    world.LastResponse = http.Response;

    object[] invokeArgs = raw;
    if (invokeArgs == null)
    {
        invokeArgs = route.GetParameters().Select(p =>
        {
            if (p.ParameterType.IsValueType) return Activator.CreateInstance(p.ParameterType);
            var req = Activator.CreateInstance(p.ParameterType);
            if (body != null)
                foreach (var prop in body.GetType().GetProperties())
                    p.ParameterType.GetProperty(prop.Name)?.SetValue(req, prop.GetValue(body));
            return req;
        }).ToArray();
    }

    object result;
    try { result = route.Invoke(c, invokeArgs); }
    catch (TargetInvocationException ex) { return (-500, ex.InnerException?.GetType().Name + ": " + ex.InnerException?.Message); }

    if (result is Task task)
    {
        try { task.GetAwaiter().GetResult(); }
        catch (Exception ex) { return (-500, ex.GetType().Name + ": " + ex.Message); }
        result = task.GetType().GetProperty("Result")?.GetValue(task);
    }
    var inner = result?.GetType().GetProperty("Result")?.GetValue(result) ?? result;
    if (inner == null && result != null) inner = result.GetType().GetProperty("Value")?.GetValue(result) is object v ? new OkObjectResult(v) : null;
    return inner switch
    {
        ObjectResult o => (o.StatusCode ?? 200, o.Value),
        StatusCodeResult s => (s.StatusCode, null),
        _ => (-1, inner?.GetType().Name),
    };
}
static object Prop(object o, string name) => o?.GetType().GetProperty(name)?.GetValue(o);
static string Say((int status, object value) r) => r.status + " " + (r.value as string ?? r.value?.GetType().Name);

var ADMIN = Guid.NewGuid(); world.Add(ADMIN, "Admin", admin: true);
var PARENT = Guid.NewGuid(); world.Add(PARENT, "Parent");
var KID = Guid.NewGuid(); world.Add(KID, "Parent_kid");
var OPEN_KID = Guid.NewGuid(); world.Add(OPEN_KID, "Parent_open");
var STRANGER = Guid.NewGuid(); world.Add(STRANGER, "Stranger");
Map(ADMIN, ADMIN);
Map(PARENT, PARENT);
Map(KID, PARENT, pin: "1357");
Map(OPEN_KID, PARENT);
Map(STRANGER, STRANGER);

// ── Emergency disable ────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── Emergency disable: Panic, SetPanicCode, GetPanicState ───────");
{
    var panicField = pluginType.GetField("_panicDisabled", flags);
    void PanicOff() => panicField?.SetValue(null, false);
    bool StateSays() => (bool)(Prop(Call("GetPanicState", null).value, "Disabled") ?? false);
    PanicOff();

    var st = Call("GetPanicState", null);
    Ok("panic-state answers anyone: not disabled (" + Say(st) + ")", st.status == 200 && Prop(st.value, "Disabled") is false);
    Ok("and is never cached", world.LastResponse?.Headers["Cache-Control"].ToString() == "no-store");

    var unarmed = Call("Panic", null, new { Code = "anything at all" }, ip: "198.51.100.1");
    Ok("with no code set, any code is refused (" + Say(unarmed) + ")", unarmed.status == 400 && unarmed.value as string == "Incorrect code.");

    var byParent = Call("SetPanicCode", PARENT, new { Code = "correct horse battery" });
    Ok("only an administrator can set the code (" + Say(byParent) + ")",
       byParent.status == 401 && string.IsNullOrEmpty((string)Cfg("PanicCodeHash")));
    var byNobody = Call("SetPanicCode", null, new { Code = "correct horse battery" });
    Ok("nor anyone signed out", byNobody.status == 401 && string.IsNullOrEmpty((string)Cfg("PanicCodeHash")));

    var shortCode = Call("SetPanicCode", ADMIN, new { Code = "short" });
    Ok("a short code is refused (" + Say(shortCode) + ")", shortCode.status == 400 && string.IsNullOrEmpty((string)Cfg("PanicCodeHash")));
    var set = Call("SetPanicCode", ADMIN, new { Code = "  correct horse battery  " });
    var hash = (string)Cfg("PanicCodeHash");
    Ok("an administrator sets it (" + Say(set) + ")", set.status == 200 && !string.IsNullOrEmpty(hash));
    Ok("and it is stored hashed, not as typed", hash != null && !hash.Contains("correct horse"));

    var wrong = Call("Panic", null, new { Code = "wrong horse battery" }, ip: "198.51.100.2");
    Ok("a wrong code is refused exactly as on an unarmed server (" + Say(wrong) + ")",
       wrong.status == unarmed.status && Equals(wrong.value, unarmed.value) && !StateSays());

    for (int i = 0; i < 5; i++) Call("Panic", null, new { Code = "guess " + i + " xxxxxxxx" }, ip: "198.51.100.3");
    var limited = Call("Panic", null, new { Code = "correct horse battery" }, ip: "198.51.100.3");
    Ok("after five guesses from one address even the right code is refused (" + Say(limited) + ")", limited.status == 429);
    Ok("and the plugin stays on", !StateSays());

    // R-1165: the code is stored trimmed, so it is checked trimmed. An app that sends what was
    // typed, spaces and all, must not burn attempts on a code that was right.
    var padded = Call("Panic", null, new { Code = "  correct horse battery \n" }, ip: "198.51.100.6");
    Ok("a right code with spaces round it still works (" + Say(padded) + ")", padded.status == 200 && StateSays());
    PanicOff();

    // R-1166: behind a proxy the server trusts, every guess comes from the proxy's address.
    // Five from one stranger must not lock out the administrator on another.
    for (int i = 0; i < 5; i++) Call("Panic", null, new { Code = "guess " + i + " xxxxxxxx" }, ip: "10.9.9.9", forwardedFor: "203.0.113.50");
    var behindProxy = Call("Panic", null, new { Code = "correct horse battery" }, ip: "10.9.9.9", forwardedFor: "203.0.113.51");
    Ok("behind a trusted proxy the limit counts the client, not the proxy (" + Say(behindProxy) + ")", behindProxy.status == 200);
    PanicOff();

    var right = Call("Panic", null, new { Code = "correct horse battery" }, ip: "198.51.100.4");
    Ok("the right code from anyone switches it off (" + Say(right) + ")", right.status == 200 && Prop(right.value, "Disabled") is true);
    Ok("and panic-state says so", StateSays());
    PanicOff();

    var clear = Call("SetPanicCode", ADMIN, new { Code = "" });
    Ok("an empty code clears it (" + Say(clear) + ")", clear.status == 200 && string.IsNullOrEmpty((string)Cfg("PanicCodeHash")));
    var afterClear = Call("Panic", null, new { Code = "correct horse battery" }, ip: "198.51.100.5");
    Ok("after which the old code does nothing", afterClear.status == 400 && !StateSays());

    // R-1307. The limits above are per raw address; these are what they did not cover.
    var rlPanic = asm.GetType("Jellyfin.Profiles.Controllers.RateLimiter", true);
    void ClearPanicLimits()
    {
        foreach (var f in rlPanic.GetFields(flags).Where(f => f.IsStatic && f.FieldType == rlPanic && f.Name.StartsWith("Panic")))
        {
            var store = rlPanic.GetFields(flags | BindingFlags.Instance).FirstOrDefault(x => !x.IsStatic && typeof(System.Collections.IDictionary).IsAssignableFrom(x.FieldType));
            (store?.GetValue(f.GetValue(null)) as System.Collections.IDictionary)?.Clear();
        }
    }
    ClearPanicLimits();
    Call("SetPanicCode", ADMIN, new { Code = "correct horse battery" });

    // One subscriber owns a whole /64. Five guesses from five of its addresses are five guesses.
    for (int i = 1; i <= 5; i++) Call("Panic", null, new { Code = "guess " + i + " xxxxxxxx" }, ip: "2001:db8:1:2::" + i);
    var sameBlock = Call("Panic", null, new { Code = "correct horse battery" }, ip: "2001:db8:1:2:ffff::9");
    Ok("addresses in one IPv6 /64 share a limit (" + Say(sameBlock) + ")", sameBlock.status == 429 && !StateSays());
    var otherBlock = Call("Panic", null, new { Code = "correct horse battery" }, ip: "2001:db8:1:3::1");
    Ok("another /64 is not held by it (" + Say(otherBlock) + ")", otherBlock.status == 200);
    PanicOff();

    // Many addresses: a ceiling on everyone together.
    ClearPanicLimits();
    for (int i = 0; i < 200; i++) Call("Panic", null, new { Code = "guess xxxxxxxxxx" }, ip: "192.0.2." + (i + 1));
    var flood = Call("Panic", null, new { Code = "correct horse battery" }, ip: "198.51.100.77");
    Ok("once enough guesses have come from anywhere, a fresh address is refused too (" + Say(flood) + ")", flood.status == 429 && !StateSays());
    ClearPanicLimits();

    // An unarmed server answers as slowly as an armed one: a missing derivation is a tell.
    long Ms(string code, string ip)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Call("Panic", null, new { Code = code }, ip: ip);
        return sw.ElapsedMilliseconds;
    }
    Ms("warm up xxxxxxxx", "198.51.100.90");
    var armedMs = new[] { Ms("guess aaaaaaaa", "198.51.100.91"), Ms("guess bbbbbbbb", "198.51.100.92"), Ms("guess cccccccc", "198.51.100.93") }.Min();
    Call("SetPanicCode", ADMIN, new { Code = "" });
    var unarmedMs = new[] { Ms("guess aaaaaaaa", "198.51.100.94"), Ms("guess bbbbbbbb", "198.51.100.95"), Ms("guess cccccccc", "198.51.100.96") }.Min();
    Ok("an unarmed server takes about as long as an armed one (unarmed " + unarmedMs + " ms, armed " + armedMs + " ms)",
       armedMs >= 5 && unarmedMs * 2 >= armedMs);
    ClearPanicLimits();
}

// ── VerifyPin ────────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── VerifyPin ───────────────────────────────────────────────────");
{
    var rl = asm.GetType("Jellyfin.Profiles.Controllers.RateLimiter", true);
    void Forget()
    {
        foreach (var f in rl.GetFields(flags).Where(f => f.FieldType == rl))
        {
            var lim = f.GetValue(null);
            foreach (var key in new[] { "203.0.113.7_" + KID, "203.0.113.8_" + KID, KID.ToString("N") })
                rl.GetMethod("Reset")?.Invoke(lim, new object[] { key });
        }
    }
    Forget();

    var nobody = Call("VerifyPin", null, new { ProfileId = KID, Pin = "1357" });
    Ok("nobody signed in gets 401 (" + Say(nobody) + ")", nobody.status == 401);

    var wrong = Call("VerifyPin", PARENT, new { ProfileId = KID, Pin = "0000" });
    Ok("a wrong PIN is refused (" + Say(wrong) + ")", wrong.status == 400 && wrong.value as string == "Invalid PIN.");
    var right = Call("VerifyPin", PARENT, new { ProfileId = KID, Pin = "1357" });
    Ok("the right PIN is accepted (" + Say(right) + ")", right.status == 200);
    var noPin = Call("VerifyPin", PARENT, new { ProfileId = OPEN_KID, Pin = "" });
    Ok("a profile with no PIN needs none (" + Say(noPin) + ")", noPin.status == 200);

    var foreign = Call("VerifyPin", STRANGER, new { ProfileId = KID, Pin = "1357" });
    Ok("another household can't check this household's PIN (" + Say(foreign) + ")", foreign.status == 401 || foreign.status == 400);

    Forget();
    for (int i = 0; i < 5; i++) Call("VerifyPin", PARENT, new { ProfileId = KID, Pin = "000" + i });
    var locked = Call("VerifyPin", PARENT, new { ProfileId = KID, Pin = "1357" });
    Ok("after five wrong, even the right PIN is refused (" + Say(locked) + ")", locked.status == 429);
    var otherAddress = Call("VerifyPin", PARENT, new { ProfileId = KID, Pin = "1357" }, ip: "203.0.113.8");
    Ok("from another address too: the allowance is the profile's (" + Say(otherAddress) + ")", otherAddress.status == 429);
    Forget();

    // r2-11-01: opening with no PIN asked (none set, a trusted address, LAN bypass) proves
    // nothing about the PIN, so it must not hand a guesser a fresh allowance.
    {
        var budget = asm.GetType("Jellyfin.Profiles.Controllers.PinBudget", true);
        var begin = budget.GetMethod("TryBegin", flags);
        bool Spend() => (bool)begin.Invoke(null, new object[] { OPEN_KID });
        for (int i = 0; i < 12 && Spend(); i++) { }
        Ok("setup: the guesser has used the quarter hour", !Spend());
        var free = Call("VerifyPin", PARENT, new { ProfileId = OPEN_KID, Pin = "" });
        Ok("a PIN-free open is allowed (" + Say(free) + ")", free.status == 200);
        Ok("but it does not give the guesser a fresh allowance", !Spend());
        Forget();
        budget.GetMethod("End", flags).Invoke(null, new object[] { OPEN_KID });
    }
}

// ── SetProfileLimit ──────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── SetProfileLimit ─────────────────────────────────────────────");
{
    var overrides = (IList)Cfg("UserProfileLimitOverrides");
    int? Limit(Guid u)
    {
        var o = overrides.Cast<object>().FirstOrDefault(x => (Guid)Prop(x, "UserId") == u);
        return o == null ? null : (int)Prop(o, "MaxProfiles");
    }

    var byParent = Call("SetProfileLimit", PARENT, new { UserId = PARENT, MaxProfiles = (int?)9 });
    Ok("a non-administrator can't raise their own limit (" + Say(byParent) + ")", byParent.status == 401 && Limit(PARENT) == null);
    var set = Call("SetProfileLimit", ADMIN, new { UserId = PARENT, MaxProfiles = (int?)3 });
    Ok("an administrator sets one (" + Say(set) + ")", set.status == 200 && Limit(PARENT) == 3);
    var again = Call("SetProfileLimit", ADMIN, new { UserId = PARENT, MaxProfiles = (int?)4 });
    Ok("setting it again replaces it, not adds a second", again.status == 200 && Limit(PARENT) == 4
       && overrides.Cast<object>().Count(x => (Guid)Prop(x, "UserId") == PARENT) == 1);
    var zero = Call("SetProfileLimit", ADMIN, new { UserId = PARENT, MaxProfiles = (int?)0 });
    var huge = Call("SetProfileLimit", ADMIN, new { UserId = PARENT, MaxProfiles = (int?)2_000_000_000 });
    Ok("out-of-range values are refused (" + Say(zero) + ", " + Say(huge) + ")", zero.status == 400 && huge.status == 400 && Limit(PARENT) == 4);
    // R-1168: a limit is for an account that exists, and a missing body is a 400, not a 500.
    var nobody = Call("SetProfileLimit", ADMIN, new { UserId = Guid.NewGuid(), MaxProfiles = (int?)3 });
    var noBody = Call("SetProfileLimit", ADMIN, null, raw: new object[] { null });
    Ok("an unknown account or no body is refused (" + Say(nobody) + ", " + Say(noBody) + ")", nobody.status == 400 && noBody.status == 400);
    var remove = Call("SetProfileLimit", ADMIN, new { UserId = PARENT, MaxProfiles = (int?)null });
    Ok("no value removes it", remove.status == 200 && Limit(PARENT) == null);
}

// ── Bonfire group routes from a profile, and the avatar library's size ──────
Console.WriteLine();
Console.WriteLine("── Bonfire leave/delete from a profile, library cap ────────────");
{
    // R-1162: only a main account is ever in a group; a profile is refused, not told "none".
    var leave = Call("LeaveBonfire", KID);
    var del = Call("DeleteBonfireGroup", KID);
    Ok("a profile can't leave or delete a Bonfire group (" + Say(leave) + ", " + Say(del) + ")", leave.status == 401 && del.status == 401);

    // R-1171: a library holds a fixed number of pictures.
    var library = (IList)Cfg("AvatarLibrary");
    var itemType = library.GetType().GetGenericArguments()[0];
    while (library.Count < 200) { var it = Activator.CreateInstance(itemType); itemType.GetProperty("Id").SetValue(it, "fill" + library.Count); library.Add(it); }
    var full = Call("AddLibraryAvatar", ADMIN, new { Image = "data:image/png;base64,AAAA" });
    Ok("a full avatar library refuses another (" + Say(full) + ")", full.status == 400 && library.Count == 200);
    var listed = Call("GetFormOptions", PARENT);
    Ok("and is still listed (" + Say(listed) + ")", listed.status == 200);
    library.Clear();
}

// ── SetMaintenance ───────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── SetMaintenance ──────────────────────────────────────────────");
{
    bool On() => (bool)(Prop(Cfg("Maintenance"), "Enabled") ?? false);

    var byParent = Call("SetMaintenance", PARENT, new { Enabled = true, Message = "down" });
    Ok("a non-administrator can't close the server (" + Say(byParent) + ")", byParent.status == 401 && !On());
    var past = Call("SetMaintenance", ADMIN, new { Enabled = true, Message = "x", UntilUtc = (DateTime?)DateTime.UtcNow.AddHours(-1) });
    Ok("an end time in the past is refused (" + Say(past) + ")", past.status == 400 && !On());
    var longMsg = Call("SetMaintenance", ADMIN, new { Enabled = true, Message = new string('x', 5000) });
    Ok("an over-long message is refused (" + Say(longMsg) + ")", longMsg.status == 400 && !On());
    var close = Call("SetMaintenance", ADMIN, new { Enabled = true, Message = "  Back at two  ", UntilUtc = (DateTime?)DateTime.UtcNow.AddHours(2) });
    Ok("an administrator closes it (" + Say(close) + ")", close.status == 200 && On());
    Ok("with the message trimmed and a start time", (string)Prop(Cfg("Maintenance"), "Message") == "Back at two"
       && Prop(Cfg("Maintenance"), "StartedUtc") != null);
    Ok("without the tick it is not set to reopen by itself", Prop(Cfg("Maintenance"), "ReopenAutomatically") is bool off && !off);
    var open = Call("SetMaintenance", ADMIN, new { Enabled = false });
    Ok("and reopens it", open.status == 200 && !On() && Prop(Cfg("Maintenance"), "UntilUtc") == null);

    // Reopening by itself needs a time to reopen at. Refused, not ignored: a tick that did
    // nothing would leave a server closed that its administrator thought would reopen.
    var noTime = Call("SetMaintenance", ADMIN, new { Enabled = true, Message = "x", ReopenAutomatically = true });
    Ok("reopening by itself with no time is refused (" + Say(noTime) + ")", noTime.status == 400 && !On());
    var timed = Call("SetMaintenance", ADMIN, new { Enabled = true, Message = "x", ReopenAutomatically = true, UntilUtc = (DateTime?)DateTime.UtcNow.AddHours(2) });
    Ok("with a time it is kept (" + Say(timed) + ")", timed.status == 200 && On()
       && Prop(Cfg("Maintenance"), "ReopenAutomatically") is bool on && on);
    Ok("and the route says so", Prop(timed.value, "ReopenAutomatically") is bool echoed && echoed);
    var reopened = Call("SetMaintenance", ADMIN, new { Enabled = false, ReopenAutomatically = true });
    Ok("reopening by hand clears it", reopened.status == 200 && !On() && Prop(Cfg("Maintenance"), "ReopenAutomatically") is bool cleared && !cleared);
}

// ── SignOutSession ───────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── SignOutSession ──────────────────────────────────────────────");
{
    world.Devices.Add((KID, "tv-1"));
    world.Devices.Add((PARENT, "tv-1"));
    world.Devices.Add((KID, "phone-1"));

    var byParent = Call("SignOutSession", PARENT, new { UserId = KID, DeviceId = "tv-1" });
    Ok("a non-administrator can't sign anyone out (" + Say(byParent) + ")", byParent.status == 401 && world.LoggedOut.Count == 0);
    var missing = Call("SignOutSession", ADMIN, new { UserId = Guid.Empty, DeviceId = "tv-1" });
    Ok("both the account and the device are required (" + Say(missing) + ")", missing.status == 400 && world.LoggedOut.Count == 0);
    var none = Call("SignOutSession", ADMIN, new { UserId = KID, DeviceId = "nowhere" });
    Ok("an unknown session is refused (" + Say(none) + ")", none.status == 400 && world.LoggedOut.Count == 0);
    var one = Call("SignOutSession", ADMIN, new { UserId = KID, DeviceId = "tv-1" });
    Ok("an administrator signs one session out (" + Say(one) + ")", one.status == 200 && world.LoggedOut.Contains((KID, "tv-1")));
    Ok("and only that one: not the other account on the same television, nor the profile's phone",
       world.LoggedOut.Count == 1, string.Join(" ", world.LoggedOut));
}

// ── CleanupOrphans ───────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── CleanupOrphans ──────────────────────────────────────────────");
{
    var GONE = Guid.NewGuid();               // a profile whose Jellyfin user was deleted
    var GONE_MASTER = Guid.NewGuid();        // a main account that was deleted
    var LEFT = Guid.NewGuid(); world.Add(LEFT, "Left_behind");
    Map(GONE, PARENT);
    Map(LEFT, GONE_MASTER);
    // Viewing time kept for them, which went on sitting in viewing_usage.json (R-1023).
    var viewingTracker = asm.GetType("Jellyfin.Profiles.Auth.ViewingTracker", true);
    const BindingFlags AnyMember = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    foreach (var id in new[] { GONE, LEFT, KID })
        viewingTracker.GetMethod("AddSeconds", AnyMember).Invoke(null, new object[] { id, "2026-11-01", 60.0 });
    bool WatchTimeKept(Guid id) => ((IDictionary)viewingTracker.GetField("Entries", AnyMember).GetValue(null)).Contains(id);

    var byParent = Call("CleanupOrphans", PARENT, raw: new object[] { true });
    Ok("a non-administrator can't clean up (" + Say(byParent) + ")", byParent.status == 401 && Mapped(GONE) && Mapped(LEFT));

    var preview = Call("CleanupOrphans", ADMIN, raw: new object[] { false });
    var orphans = (Prop(preview.value, "orphans") as IEnumerable)?.Cast<object>().Select(o => (Guid)Prop(o, "profileUserId")).ToList() ?? new();
    Ok("a preview lists both kinds of orphan (" + orphans.Count + ")", preview.status == 200 && orphans.Contains(GONE) && orphans.Contains(LEFT));
    Ok("and lists no live profile", !orphans.Contains(KID) && !orphans.Contains(PARENT) && !orphans.Contains(ADMIN));
    Ok("and removes nothing", Mapped(GONE) && Mapped(LEFT));

    // First-time setup remembers who has seen it, one row per main account (R-1075).
    var onboarding = cfgType.GetProperty("Onboarding").GetValue(config);
    var seenMark = onboarding.GetType().GetMethod("MarkSeen");
    seenMark.Invoke(onboarding, new object[] { GONE_MASTER });
    seenMark.Invoke(onboarding, new object[] { PARENT });
    bool SeenKept(Guid id) => ((IEnumerable)onboarding.GetType().GetProperty("Seen").GetValue(onboarding))
        .Cast<object>().Any(r => (Guid)Prop(r, "UserId") == id);

    // A confirmed address and a profile-limit override are kept per account, and an account
    // deleted in Jellyfin left both behind (R-1311).
    object AddRow(string listName, Type rowType, Guid uid)
    {
        var row = Activator.CreateInstance(rowType);
        rowType.GetProperty("UserId").SetValue(row, uid);
        ((IList)cfgType.GetProperty(listName).GetValue(config)).Add(row);
        return row;
    }
    var emailType = asm.GetType("Jellyfin.Profiles.Configuration.AccountEmail", true);
    var overrideType = asm.GetType("Jellyfin.Profiles.Configuration.UserProfileLimitOverride", true);
    foreach (var uid in new[] { GONE_MASTER, PARENT })
    {
        AddRow("AccountEmails", emailType, uid);
        AddRow("UserProfileLimitOverrides", overrideType, uid);
    }
    bool RowKept(string listName, Guid id) => ((IEnumerable)cfgType.GetProperty(listName).GetValue(config))
        .Cast<object>().Any(r => (Guid)Prop(r, "UserId") == id);

    var applied = Call("CleanupOrphans", ADMIN, raw: new object[] { true });
    Ok("a deleted account's saved address and profile-limit override go, a live one's stay",
        !RowKept("AccountEmails", GONE_MASTER) && !RowKept("UserProfileLimitOverrides", GONE_MASTER)
        && RowKept("AccountEmails", PARENT) && RowKept("UserProfileLimitOverrides", PARENT));
    Ok("a deleted account's first-time setup record goes, a live one's stays", !SeenKept(GONE_MASTER) && SeenKept(PARENT));
    Ok("applying removes them (" + Say(applied) + ")", applied.status == 200 && !Mapped(GONE) && !Mapped(LEFT));
    Ok("and keeps every live profile", Mapped(KID) && Mapped(OPEN_KID) && Mapped(PARENT) && Mapped(ADMIN) && Mapped(STRANGER));
    Ok("their viewing time is forgotten, and a live profile's is not", !WatchTimeKept(GONE) && !WatchTimeKept(LEFT) && WatchTimeKept(KID));
}

// ── GetDevices (R-1299) ──────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── GetDevices ──────────────────────────────────────────────────");
{
    // The household's device ids are what a sibling's device whitelist and the known-device
    // sign-in are matched against, so a profile that only holds its own PIN must not be handed them.
    var knownType = asm.GetType("Jellyfin.Profiles.Configuration.KnownDevice", true);
    var known = (IList)cfgType.GetProperty("KnownDevices").GetValue(config);
    var d = Activator.CreateInstance(knownType);
    knownType.GetProperty("DeviceId").SetValue(d, "lounge-tv");
    knownType.GetProperty("DeviceName").SetValue(d, "Lounge");
    knownType.GetProperty("MasterUserId").SetValue(d, PARENT);
    knownType.GetProperty("LastSeen").SetValue(d, DateTime.UtcNow);
    known.Add(d);
    var requireProp = cfgType.GetProperty("RequireMainPin");
    int Count((int status, object value) r) => r.value is IEnumerable e ? e.Cast<object>().Count() : -1;

    requireProp.SetValue(config, false);
    var byMain = Call("GetDevices", PARENT);
    Ok("the main account gets its household's devices (" + Say(byMain) + ")", byMain.status == 200 && Count(byMain) == 1);
    var byOpenKid = Call("GetDevices", OPEN_KID);
    Ok("a profile whose main account is open still gets them (" + Say(byOpenKid) + ")", byOpenKid.status == 200 && Count(byOpenKid) == 1);

    // A main account with a PIN: the profile has to bring it, as it must to manage anything else.
    var LOCKED = Guid.NewGuid(); world.Add(LOCKED, "Locked");
    var LOCKED_KID = Guid.NewGuid(); world.Add(LOCKED_KID, "Locked_kid");
    Map(LOCKED, LOCKED, pin: "2468");
    Map(LOCKED_KID, LOCKED);
    var d2 = Activator.CreateInstance(knownType);
    knownType.GetProperty("DeviceId").SetValue(d2, "locked-tv");
    knownType.GetProperty("MasterUserId").SetValue(d2, LOCKED);
    knownType.GetProperty("LastSeen").SetValue(d2, DateTime.UtcNow);
    known.Add(d2);
    var kidNoPin = Call("GetDevices", LOCKED_KID);
    Ok("a profile without the main PIN gets an empty list, not an error (" + Say(kidNoPin) + ")", kidNoPin.status == 200 && Count(kidNoPin) == 0);
    var mainNoPin = Call("GetDevices", LOCKED);
    Ok("the main account is not asked for its PIN (" + Say(mainNoPin) + ")", mainNoPin.status == 200 && Count(mainNoPin) == 1);

    // With "require main PINs" on and no PIN set, a profile cannot manage anything either.
    requireProp.SetValue(config, true);
    var kidRequired = Call("GetDevices", OPEN_KID);
    Ok("a profile of a main account that must set a PIN first gets an empty list (" + Say(kidRequired) + ")", kidRequired.status == 200 && Count(kidRequired) == 0);
    requireProp.SetValue(config, false);

    var nobody = Call("GetDevices", null);
    Ok("nobody signed in is refused (" + Say(nobody) + ")", nobody.status == 401);
}

try { Directory.Delete(tempDir, true); } catch { }
Console.WriteLine();
if (fails.Count > 0)
{
    foreach (var f in fails) Console.WriteLine("   - " + f);
    Console.WriteLine(pass + " passed, " + fails.Count + " failed");
    return 1;
}
Console.WriteLine(pass + " passed, 0 failed");
return 0;

// ── A modelled Jellyfin ──────────────────────────────────────────────────────

public class World
{
    public Dictionary<Guid, User> Users = new();
    public HashSet<Guid> Admins = new();
    public List<(Guid user, string device)> Devices = new();
    public List<(Guid user, string device)> LoggedOut = new();
    public HttpResponse LastResponse;

    public void Add(Guid id, string name, bool admin = false)
    {
        var u = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty("Username").SetValue(u, name);
        typeof(User).GetProperty("Id").SetValue(u, id);
        typeof(User).GetProperty("AuthenticationProviderId").SetValue(u, "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider");
        Users[id] = u;
        if (admin) Admins.Add(id);
    }

    public static object Create(Type t, World w)
    {
        var p = typeof(DispatchProxy).GetMethods().First(m => m.Name == "Create" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 2)
            .MakeGenericMethod(t, typeof(WorldProxy)).Invoke(null, null);
        ((WorldProxy)p).W = w;
        return p;
    }
}

public class WorldProxy : DispatchProxy
{
    public World W;

    protected override object Invoke(MethodInfo m, object[] args)
    {
        switch (m.Name)
        {
            case "GetVirtualFolders":
                return new List<MediaBrowser.Model.Entities.VirtualFolderInfo>();
            case "GetUserById":
                return W.Users.TryGetValue((Guid)args[0], out var u) ? u : null;
            case "GetUserDto":
                var user = (User)args[0];
                return new UserDto
                {
                    Id = user.Id,
                    Name = user.Username,
                    Policy = new UserPolicy { IsAdministrator = W.Admins.Contains(user.Id), AuthenticationProviderId = user.AuthenticationProviderId, PasswordResetProviderId = "x" },
                    Configuration = new UserConfiguration(),
                };
            case "get_Users":
                return W.Users.Values.ToList();
            case "get_Sessions":
                return Activator.CreateInstance(typeof(List<>).MakeGenericType(m.ReturnType.GetGenericArguments()[0]));
            case "GetDevices":
            {
                // Filtered the way Jellyfin filters: by every field the query sets.
                var q = args[0];
                var wantDevice = (string)q.GetType().GetProperty("DeviceId")?.GetValue(q);
                var wantUser = q.GetType().GetProperty("UserId")?.GetValue(q) as Guid?;
                var rt = m.ReturnType;
                var elem = rt.GetGenericArguments()[0];
                var items = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elem));
                foreach (var (du, dd) in W.Devices)
                {
                    if (wantDevice != null && wantDevice != dd) continue;
                    if (wantUser.HasValue && wantUser.Value != Guid.Empty && wantUser.Value != du) continue;
                    var d = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(elem);
                    elem.GetProperty("UserId")?.SetValue(d, du);
                    elem.GetProperty("DeviceId")?.SetValue(d, dd);
                    items.Add(d);
                }
                var result = Activator.CreateInstance(rt);
                var itemsProp = rt.GetProperty("Items");
                var arr = Array.CreateInstance(elem, items.Count);
                items.CopyTo(arr, 0);
                itemsProp.SetValue(result, itemsProp.PropertyType.IsAssignableFrom(items.GetType()) ? items : arr);
                return result;
            }
            case "Logout":
                if (args.Length == 1 && args[0] != null && args[0].GetType().GetProperty("DeviceId") != null)
                {
                    var d = args[0];
                    W.LoggedOut.Add(((Guid)d.GetType().GetProperty("UserId").GetValue(d), (string)d.GetType().GetProperty("DeviceId").GetValue(d)));
                }
                return Task.CompletedTask;
        }

        var t = m.ReturnType;
        if (t == typeof(void)) return null;
        if (t == typeof(Task)) return Task.CompletedTask;
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var inner = t.GetGenericArguments()[0];
            var value = inner.IsValueType ? Activator.CreateInstance(inner) : null;
            return typeof(Task).GetMethod("FromResult").MakeGenericMethod(inner).Invoke(null, new[] { value });
        }
        return t.IsValueType ? Activator.CreateInstance(t) : null;
    }
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
