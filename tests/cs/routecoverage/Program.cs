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

// ─────────────────────────────────────────────────────────────────────────────
// Routes no harness called (R-1288).
//
// Each of these was reachable only through a live server: the emergency disable (Panic,
// SetPanicCode, GetPanicState), the PIN check behind the web switcher (VerifyPin), and
// the administrator's SetProfileLimit, SetMaintenance, SignOutSession and CleanupOrphans.
// Here the real controller methods are called against a modelled Jellyfin and what they
// answer and change is read back.
//
// Import and export are left to R-1146, which covers them as a round trip.
//
// A coverage harness can't fail against old code that behaves the same, so each route
// group was shown to go red against a deliberate local break of the plugin, one per group,
// listed in the commit that added this file.
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
(int status, object value) Call(string method, Guid? caller, object body = null, string ip = "203.0.113.7", object[] raw = null)
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

    var right = Call("Panic", null, new { Code = "correct horse battery" }, ip: "198.51.100.4");
    Ok("the right code from anyone switches it off (" + Say(right) + ")", right.status == 200 && Prop(right.value, "Disabled") is true);
    Ok("and panic-state says so", StateSays());
    PanicOff();

    var clear = Call("SetPanicCode", ADMIN, new { Code = "" });
    Ok("an empty code clears it (" + Say(clear) + ")", clear.status == 200 && string.IsNullOrEmpty((string)Cfg("PanicCodeHash")));
    var afterClear = Call("Panic", null, new { Code = "correct horse battery" }, ip: "198.51.100.5");
    Ok("after which the old code does nothing", afterClear.status == 400 && !StateSays());
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
    var remove = Call("SetProfileLimit", ADMIN, new { UserId = PARENT, MaxProfiles = (int?)null });
    Ok("no value removes it", remove.status == 200 && Limit(PARENT) == null);
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

    var byParent = Call("CleanupOrphans", PARENT, raw: new object[] { true });
    Ok("a non-administrator can't clean up (" + Say(byParent) + ")", byParent.status == 401 && Mapped(GONE) && Mapped(LEFT));

    var preview = Call("CleanupOrphans", ADMIN, raw: new object[] { false });
    var orphans = (Prop(preview.value, "orphans") as IEnumerable)?.Cast<object>().Select(o => (Guid)Prop(o, "profileUserId")).ToList() ?? new();
    Ok("a preview lists both kinds of orphan (" + orphans.Count + ")", preview.status == 200 && orphans.Contains(GONE) && orphans.Contains(LEFT));
    Ok("and lists no live profile", !orphans.Contains(KID) && !orphans.Contains(PARENT) && !orphans.Contains(ADMIN));
    Ok("and removes nothing", Mapped(GONE) && Mapped(LEFT));

    var applied = Call("CleanupOrphans", ADMIN, raw: new object[] { true });
    Ok("applying removes them (" + Say(applied) + ")", applied.status == 200 && !Mapped(GONE) && !Mapped(LEFT));
    Ok("and keeps every live profile", Mapped(KID) && Mapped(OPEN_KID) && Mapped(PARENT) && Mapped(ADMIN) && Mapped(STRANGER));
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
