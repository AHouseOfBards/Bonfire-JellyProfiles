using System;
using System.Reflection;

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

// Exercises the pure decision helpers against the compiled plugin:
//   - the cross-account (Bonfire) switch matrix         — issue #13
//   - switcher-preference resolution and migration      — issues #8 / #14

var asm = Assembly.LoadFrom(RepoPath("bin", "Release", "net9.0", "Jellyfin.Profiles.dll"));
var baseType = asm.GetType("Jellyfin.Profiles.Controllers.ProfilesBaseController", true);
var mappingType = asm.GetType("Jellyfin.Profiles.Configuration.ProfileMapping", true);
var locType = asm.GetType("Jellyfin.Profiles.Configuration.SwitcherLocations", true);

const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
var evaluate = baseType.GetMethod("EvaluateCrossAccountSwitch", Flags)
    ?? throw new Exception("EvaluateCrossAccountSwitch not found");
var canSkip = baseType.GetMethod("CanSkipPin", Flags)
    ?? throw new Exception("CanSkipPin not found");
var resolve = locType.GetMethod("Resolve", Flags)
    ?? throw new Exception("SwitcherLocations.Resolve not found");

object MakeMapping(bool hasPin, bool allowHousehold, bool ownLanBypass)
{
    var m = Activator.CreateInstance(mappingType);
    mappingType.GetProperty("PinHash").SetValue(m, hasPin ? "pbkdf2.sha256$150000$c2FsdA==$aGFzaA==" : null);
    mappingType.GetProperty("AllowHouseholdLanBypass").SetValue(m, allowHousehold);
    mappingType.GetProperty("BypassPinOnLocalNetwork").SetValue(m, ownLanBypass);
    return m;
}

object MakePrefMapping(string legacyMode, bool? ask, string location)
{
    var m = Activator.CreateInstance(mappingType);
    if (legacyMode != null) mappingType.GetProperty("SwitcherMode").SetValue(m, legacyMode);
    mappingType.GetProperty("AskOnStartup").SetValue(m, ask);
    mappingType.GetProperty("SwitcherLocation").SetValue(m, location);
    return m;
}

(bool bypass, bool blocked) Evaluate(object mapping, bool isLocal)
{
    var t = evaluate.Invoke(null, new object[] { mapping, isLocal });
    var ty = t.GetType();
    // ValueTuple exposes Item1/Item2 as fields, not properties.
    return ((bool)ty.GetField("Item1").GetValue(t), (bool)ty.GetField("Item2").GetValue(t));
}

// 1.3.4 added the server-wide defaults as optional parameters; reflection does not apply
// defaults for you, so they are passed explicitly.
(bool ask, string loc) Resolve(object mapping, bool defaultAsk = true, string defaultLoc = "button")
{
    var t = resolve.Invoke(null, new object[] { mapping, defaultAsk, defaultLoc });
    var ty = t.GetType();
    return ((bool)ty.GetField("Item1").GetValue(t), (string)ty.GetField("Item2").GetValue(t));
}

bool Skip(object mapping, bool isLocal, bool isCross, bool household)
    => (bool)canSkip.Invoke(null, new object[] { mapping, isLocal, isCross, household });

int pass = 0, fail = 0;
void Check(string name, object actual, object expected)
{
    if (Equals(actual, expected)) { pass++; Console.WriteLine($"  PASS  {name}"); }
    else { fail++; Console.WriteLine($"  FAIL  {name} — expected {expected}, got {actual}"); }
}

Console.WriteLine("Cross-account (Bonfire) switch matrix");
Console.WriteLine("-------------------------------------");

foreach (var optedIn in new[] { false, true })
foreach (var hasPin in new[] { false, true })
foreach (var isLocal in new[] { false, true })
{
    // ownLanBypass deliberately ON everywhere: it must never affect a cross-account switch.
    var m = MakeMapping(hasPin, optedIn, ownLanBypass: true);
    var (bypass, blocked) = Evaluate(m, isLocal);
    var label = $"optedIn={optedIn,-5} hasPin={hasPin,-5} local={isLocal,-5}";

    bool expectBypass = optedIn && isLocal;
    bool expectBlocked = !hasPin && !expectBypass;

    Check($"{label} -> bypass", bypass, expectBypass);
    Check($"{label} -> blocked", blocked, expectBlocked);

    if (hasPin)
        Check($"{label} -> skipPin", Skip(m, isLocal, isCross: true, household: bypass), expectBypass);
}

Console.WriteLine();
Console.WriteLine("Own-household switches (must be unchanged by that feature)");
Console.WriteLine("----------------------------------------------------------");

foreach (var ownBypass in new[] { false, true })
foreach (var isLocal in new[] { false, true })
{
    var m = MakeMapping(hasPin: true, allowHousehold: true, ownLanBypass: ownBypass);
    var label = $"ownBypass={ownBypass,-5} local={isLocal,-5}";
    Check($"{label} -> skipPin", Skip(m, isLocal, isCross: false, household: false), ownBypass && isLocal);
}

Console.WriteLine();
Console.WriteLine("Edge cases");
Console.WriteLine("----------");
{
    var (bypass, blocked) = Evaluate(null, true);
    Check("null mapping, local -> bypass", bypass, false);
    Check("null mapping, local -> blocked", blocked, true);
    Check("null mapping -> skipPin(cross)", Skip(null, true, true, false), false);
    Check("null mapping -> skipPin(own)", Skip(null, true, false, false), false);

    var notOpted = MakeMapping(hasPin: true, allowHousehold: false, ownLanBypass: true);
    var (b2, _) = Evaluate(notOpted, true);
    Check("opted-out account, local -> bypass", b2, false);
    Check("opted-out account -> skipPin uses evaluated value", Skip(notOpted, true, true, b2), false);
}

Console.WriteLine();
Console.WriteLine("Switcher preference resolution");
Console.WriteLine("------------------------------");
{
    // A brand-new account: the historical behaviour, untouched.
    Check("no mapping -> ask", Resolve(null).ask, true);
    Check("no mapping -> location", Resolve(null).loc, "button");

    var fresh = MakePrefMapping(null, null, null);
    Check("default mapping -> ask", Resolve(fresh).ask, true);
    Check("default mapping -> location", Resolve(fresh).loc, "button");

    // Migration from the 1.3.1-beta single setting.
    var legacyGate = MakePrefMapping("gate", null, null);
    Check("legacy gate -> ask", Resolve(legacyGate).ask, true);
    Check("legacy gate -> location", Resolve(legacyGate).loc, "button");

    var legacyNative = MakePrefMapping("native", null, null);
    Check("legacy native -> ask", Resolve(legacyNative).ask, false);
    Check("legacy native -> location", Resolve(legacyNative).loc, "menu");

    // Explicit values must win over the legacy field, including when they contradict it.
    var overridden = MakePrefMapping("native", true, "button");
    Check("explicit beats legacy -> ask", Resolve(overridden).ask, true);
    Check("explicit beats legacy -> location", Resolve(overridden).loc, "button");

    // The combination issue #14 asked for, unreachable under the old single setting.
    var askPlusMenu = MakePrefMapping("gate", true, "menu");
    Check("ask + menu -> ask", Resolve(askPlusMenu).ask, true);
    Check("ask + menu -> location", Resolve(askPlusMenu).loc, "menu");

    // Partial writes: one field set, the other still deriving from the legacy value.
    var halfSet = MakePrefMapping("native", null, "button");
    Check("location only, legacy native -> ask still false", Resolve(halfSet).ask, false);
    Check("location only, legacy native -> location honoured", Resolve(halfSet).loc, "button");

    // Garbage must degrade, never throw.
    var junk = MakePrefMapping("wat", null, "sideways");
    Check("unknown legacy mode -> ask", Resolve(junk).ask, true);
    Check("unknown location -> button", Resolve(junk).loc, "button");
}

Console.WriteLine();
Console.WriteLine("Administrator defaults (issue #14)");
Console.WriteLine("----------------------------------");
{
    // An account that has never chosen inherits whatever the administrator set.
    Check("no mapping -> admin ask", Resolve(null, false, "menu").ask, false);
    Check("no mapping -> admin location", Resolve(null, false, "menu").loc, "menu");

    var fresh = MakePrefMapping(null, null, null);
    Check("unchosen mapping -> admin ask", Resolve(fresh, false, "menu").ask, false);
    Check("unchosen mapping -> admin location", Resolve(fresh, false, "menu").loc, "menu");

    // SwitcherMode is non-nullable and has always defaulted to "gate", so every mapping
    // carries it whether or not anyone chose. Treating it as an explicit choice would
    // stop the administrator's default reaching anybody.
    var legacyGate = MakePrefMapping("gate", null, null);
    Check("legacy gate does not block the default (ask)", Resolve(legacyGate, false, "menu").ask, false);
    Check("legacy gate does not block the default (location)", Resolve(legacyGate, false, "menu").loc, "menu");

    // "native" is a real 1.3.1 choice and still outranks the default.
    var legacyNative = MakePrefMapping("native", null, null);
    Check("legacy native beats the admin default (ask)", Resolve(legacyNative, true, "button").ask, false);
    Check("legacy native beats the admin default (location)", Resolve(legacyNative, true, "button").loc, "menu");

    // An account that has chosen keeps its choice, whatever the administrator says.
    var chosen = MakePrefMapping("gate", true, "button");
    Check("own choice beats the admin default (ask)", Resolve(chosen, false, "menu").ask, true);
    Check("own choice beats the admin default (location)", Resolve(chosen, false, "menu").loc, "button");

    // Half-chosen: the set field holds, the unset one takes the default.
    var half = MakePrefMapping("gate", null, "button");
    Check("unset field takes the default", Resolve(half, false, "menu").ask, false);
    Check("set field is kept", Resolve(half, false, "menu").loc, "button");

    // A nonsense default must normalise rather than propagate.
    Check("junk admin default -> button", Resolve(fresh, true, "sideways").loc, "button");
    Check("null admin default -> button", Resolve(fresh, true, null).loc, "button");
}

Console.WriteLine();
Console.WriteLine("Image format gate (DecodeImageDataUrl)");
Console.WriteLine("--------------------------------------");
{
    // Non-public instance method, and the controller cannot be constructed here, so it is
    // invoked against an uninitialised instance — it touches only its arguments and _logger.
    var ctrlType = asm.GetType("Jellyfin.Profiles.Controllers.ProfilesController", true);
    var instance = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(ctrlType);

    // The reject paths log, and _logger is null on an uninitialised object — a readonly
    // field, so it has to be set through reflection.
    baseType.GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(instance, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

    var decode = baseType.GetMethod("DecodeImageDataUrl",
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    string? ExtensionFor(string dataUrl)
    {
        var r = decode.Invoke(instance, new object[] { dataUrl, "test" });
        if (r == null) return null;
        var ty = r.GetType();
        // Nullable<ValueTuple>: unwrap, then read Item2.
        var inner = ty.GetProperty("Value")?.GetValue(r) ?? r;
        return (string)inner.GetType().GetField("Item2").GetValue(inner);
    }

    // "AA==" decodes to one byte, enough to exercise the format decision.
    Check("png accepted", ExtensionFor("data:image/png;base64,AA=="), ".png");
    Check("jpeg accepted", ExtensionFor("data:image/jpeg;base64,AA=="), ".jpg");
    Check("webp accepted", ExtensionFor("data:image/webp;base64,AA=="), ".webp");
    Check("gif accepted", ExtensionFor("data:image/gif;base64,AA=="), ".gif");

    // The point of the gate: an unknown type must be refused, not silently stored as .jpg
    // and then served back as image/jpeg from our own origin.
    Check("svg REJECTED", ExtensionFor("data:image/svg+xml;base64,AA=="), null);
    Check("svg (unencoded) REJECTED", ExtensionFor("data:image/svg+xml,<svg onload=alert(1)>"), null);
    Check("unknown type REJECTED", ExtensionFor("data:image/x-icon;base64,AA=="), null);
    Check("not a data url REJECTED", ExtensionFor("https://example.com/a.png"), null);
    Check("bad base64 REJECTED", ExtensionFor("data:image/png;base64,!!!not-base64!!!"), null);
}

// ── The rate limit says how long the wait actually is ───────────────────────
//
// Every 429 used to end "please try again in 15 minutes". Fifteen is the width of the
// window, not the wait: the limiter slides, freeing a slot when its oldest counted
// attempt ages out, so somebody who mistyped a PIN five times over a quarter of an hour
// is usually about a minute from another go. The old number was not a safe over-estimate
// either — it was a different quantity that happened to be printed where a wait belonged.
Console.WriteLine();
Console.WriteLine("Rate limit: the wait quoted is the wait enforced");
{
    var rlType = asm.GetType("Jellyfin.Profiles.Controllers.RateLimiter", true);
    const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance
                           | BindingFlags.NonPublic | BindingFlags.Public;

    var ctor = rlType.GetConstructor(Any, null, new[] { typeof(int), typeof(int) }, null);
    var isLimited = rlType.GetMethod("IsRateLimited", Any);
    var record = rlType.GetMethod("RecordFailure", Any);
    var retryAfter = rlType.GetMethod("RetryAfter", Any);
    var describe = rlType.GetMethod("DescribeWait", Any);

    if (ctor == null || retryAfter == null || describe == null)
    {
        fail++;
        Console.WriteLine("  FAIL  RateLimiter.RetryAfter/DescribeWait do not exist");
    }
    else
    {
        // 3 attempts per 15 minutes, so the third failure is the one that locks.
        var limiter = ctor.Invoke(new object[] { 3, 15 });

        Check("not limited before the threshold", (bool)isLimited.Invoke(limiter, new object[] { "ip" }), false);
        Check("and nothing to wait for", (TimeSpan)retryAfter.Invoke(limiter, new object[] { "ip" }), TimeSpan.Zero);

        for (var i = 0; i < 3; i++) record.Invoke(limiter, new object[] { "ip" });

        Check("limited at the threshold", (bool)isLimited.Invoke(limiter, new object[] { "ip" }), true);

        var wait = (TimeSpan)retryAfter.Invoke(limiter, new object[] { "ip" });
        // All three attempts were a moment ago, so the wait is the full window minus
        // that moment — just under fifteen minutes, and never more.
        Check("the wait is inside the window", wait > TimeSpan.FromMinutes(14) && wait <= TimeSpan.FromMinutes(15), true);

        // The point of the whole change: the number is derived, not the constant 15.
        Check("the wait is not simply the window", wait != TimeSpan.FromMinutes(15), true);

        Check("an unknown caller waits for nothing",
            (TimeSpan)retryAfter.Invoke(limiter, new object[] { "someone-else" }), TimeSpan.Zero);

        // How it reads. "in 0 minutes" was the failure mode worth naming.
        Check("under a minute reads in seconds",
            (string)describe.Invoke(null, new object[] { TimeSpan.FromSeconds(40) }), "in 40 seconds");
        Check("a whole minute reads as one",
            (string)describe.Invoke(null, new object[] { TimeSpan.FromSeconds(60) }), "in a minute");
        Check("minutes round up rather than down",
            (string)describe.Invoke(null, new object[] { TimeSpan.FromSeconds(61) }), "in 2 minutes");
        Check("nothing left to wait is never \"0 minutes\"",
            (string)describe.Invoke(null, new object[] { TimeSpan.Zero }), "in a moment");
    }
}

// ── A configuration import must not be able to wipe the server ──────────────
//
// PluginConfiguration initialises every list it owns to an empty list. So a body that
// fails to bind does not arrive as null and does not throw: it arrives as a pristine
// default, and an import that writes it field by field would replace every profile,
// device and Bonfire with nothing while reporting success. A camelCase file, a
// truncated download, or an object from any other tool would all do it.
//
// The endpoint therefore reads properties off the JSON explicitly and case-insensitively,
// so "the file did not contain this" is distinguishable from "the file contained an
// empty list". These assertions are on that distinction.
Console.WriteLine();
Console.WriteLine("Configuration import: absence is not emptiness");
{
    const BindingFlags Any = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    var tryProp = baseType.GetMethod("TryProperty", Any);
    var read = baseType.GetMethod("Read", Any);

    if (tryProp == null || read == null)
    {
        fail++;
        Console.WriteLine("  FAIL  TryProperty/Read do not exist — an import can still wipe the configuration");
    }
    else
    {
        object[] Args(string json, string name)
        {
            var doc = System.Text.Json.JsonDocument.Parse(json);
            return new object[] { doc.RootElement, name, null };
        }

        bool Has(string json, string name)
        {
            var args = Args(json, name);
            return (bool)tryProp.Invoke(null, args);
        }

        Check("a present property is found",
            Has("{\"Mappings\":[]}", "Mappings"), true);
        Check("an absent property is not invented",
            Has("{\"Mappings\":[]}", "KnownDevices"), false);

        // An export that round-trips through another tool arrives camelCased, and binding
        // by exact case would call it an empty configuration.
        Check("camelCase is matched",
            Has("{\"mappings\":[]}", "Mappings"), true);
        Check("SCREAMING case is matched",
            Has("{\"MAPPINGS\":[]}", "Mappings"), true);

        // An empty list is present. That is a real instruction — "this household has no
        // profiles" — and must not be confused with the file not mentioning them.
        Check("an empty list still counts as present",
            Has("{\"Mappings\":[]}", "Mappings"), true);

        Check("a non-object body has no properties",
            Has("[1,2,3]", "Mappings"), false);
        Check("a string body has no properties",
            Has("\"nonsense\"", "Mappings"), false);

        // Read<T> must swallow malformed input rather than throwing out of the endpoint.
        var cfgType = asm.GetType("Jellyfin.Profiles.Configuration.PluginConfiguration", true);
        var readCfg = read.MakeGenericMethod(cfgType);
        var good = System.Text.Json.JsonDocument.Parse("{\"MaxProfilesPerUser\":9}").RootElement;
        var parsed = readCfg.Invoke(null, new object[] { good });
        Check("a well-formed configuration reads back",
            parsed == null ? -1 : (int)cfgType.GetProperty("MaxProfilesPerUser").GetValue(parsed), 9);

        var wrong = System.Text.Json.JsonDocument.Parse("{\"MaxProfilesPerUser\":\"not a number\"}").RootElement;
        object bad = "threw";
        try { bad = readCfg.Invoke(null, new object[] { wrong }); }
        catch (TargetInvocationException) { bad = "threw"; }
        Check("a malformed value returns null instead of throwing", bad, null);
    }
}

Console.WriteLine();
Console.WriteLine($"{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;
