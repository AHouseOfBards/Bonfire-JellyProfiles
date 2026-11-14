using System.Linq;
using System;
using System.Reflection;

// Find the repository root by walking up from this binary to the plugin's project file.
// A hardcoded path made these harnesses fail on the first CI run.
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

// Which build to load. This harness's output sits in bin/Release/<tfm>/, so its folder name
// is the framework the csproj resolved: no second place to keep in step. tests/run.sh cs10
// runs the whole set against net10.0.
var tfm = new System.IO.DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
    System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)).Name;

var asm = Assembly.LoadFrom(RepoPath("bin", "Release", tfm, "Jellyfin.Profiles.dll"));
var baseType = asm.GetType("Jellyfin.Profiles.Controllers.ProfilesBaseController", true);
var mappingType = asm.GetType("Jellyfin.Profiles.Configuration.ProfileMapping", true);
var locType = asm.GetType("Jellyfin.Profiles.Configuration.SwitcherLocations", true);

const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
var evaluate = baseType.GetMethod("EvaluateCrossHouseholdEntry", Flags)
    ?? throw new Exception("EvaluateCrossHouseholdEntry not found");
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
    mappingType.GetProperty("AskOnStartup").SetValue(m, ask);
    mappingType.GetProperty("SwitcherLocation").SetValue(m, location);
    // The old field is only read now, as a file or backup is loaded, and turned into the two
    // above where they were not chosen. Last here because this sets a null, which a file
    // that simply lacks the element never does.
    if (legacyMode != null) mappingType.GetProperty("LegacySwitcherMode").SetValue(m, legacyMode);
    return m;
}

(bool bypass, bool blocked) Evaluate(object mapping, bool isLocal)
{
    // The rule /switch really calls: the PIN is the target's own, the consent the owner's.
    // Here one row is both, a master entered directly.
    var pinHash = mapping == null ? null : (string)mappingType.GetProperty("PinHash").GetValue(mapping);
    var t = evaluate.Invoke(null, new object[] { pinHash, mapping, isLocal });
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
    => (bool)canSkip.Invoke(null, new object[] { mapping, isLocal, isCross, household, false });

// Source is matched with every run of whitespace read as one space, on both sides. A needle
// that must match character for character goes blind to a reformat, and a negative check
// ("this text is gone") then passes on code that merely moved a space (R-1292).
static bool SrcHas(string src, string needle)
{
    string N(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ");
    return N(src).Contains(N(needle), StringComparison.Ordinal);
}

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
    // A brand-new account: the historical behavior, untouched.
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

    // The old SwitcherMode defaulted to "gate" on every mapping whether or not anyone chose.
    // Treating it as an explicit choice would stop the administrator's default reaching anybody.
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

    // A nonsense default must normalize rather than propagate.
    Check("junk admin default -> button", Resolve(fresh, true, "sideways").loc, "button");
    Check("null admin default -> button", Resolve(fresh, true, null).loc, "button");
}

Console.WriteLine();
Console.WriteLine("Image format gate (DecodeImageDataUrl)");
Console.WriteLine("--------------------------------------");
{
    // Non-public instance method, and the controller cannot be constructed here, so it is
    // invoked against an uninitialized instance — it touches only its arguments and _logger.
    var ctrlType = asm.GetType("Jellyfin.Profiles.Controllers.ProfilesController", true);
    var instance = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(ctrlType);

    // The reject paths log, and _logger is null on an uninitialized object — a readonly
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

    // Each format's real first bytes: the format decision also checks them (R-1111).
    string Url(string mime, params byte[] head) => "data:" + mime + ";base64," + Convert.ToBase64String(head);
    Check("png accepted", ExtensionFor(Url("image/png", 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)), ".png");
    Check("jpeg accepted", ExtensionFor(Url("image/jpeg", 0xFF, 0xD8, 0xFF, 0xE0)), ".jpg");
    Check("webp accepted", ExtensionFor(Url("image/webp", 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50)), ".webp");
    Check("gif accepted", ExtensionFor(Url("image/gif", 0x47, 0x49, 0x46, 0x38, 0x39, 0x61)), ".gif");
    Check("png bytes under a jpeg label REJECTED", ExtensionFor(Url("image/jpeg", 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)), null);

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
// Every 429 used to end "please try again in 15 minutes". Fifteen is the window's width, not
// the wait: the limiter slides, freeing a slot when its oldest counted attempt ages out, so
// somebody who mistyped a PIN five times in a quarter hour is usually about a minute from
// another go. The old number was not a safe over-estimate either; it was a different
// quantity printed where a wait belonged.
Console.WriteLine();
Console.WriteLine("Rate limit: the wait quoted is the wait enforced");
{
    var rlType = asm.GetType("Jellyfin.Profiles.Controllers.RateLimiter", true);
    const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance
                           | BindingFlags.NonPublic | BindingFlags.Public;

    var ctor = rlType.GetConstructor(Any, null, new[] { typeof(int), typeof(int) }, null);
    var rlBeginAttempt = rlType.GetMethod("TryBegin", Any, null, new[] { typeof(string) }, null);
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

        Check("nothing to wait for before any attempt", (TimeSpan)retryAfter.Invoke(limiter, new object[] { "ip" }), TimeSpan.Zero);

        var allowed = 0;
        for (var i = 0; i < 3; i++) if ((bool)rlBeginAttempt.Invoke(limiter, new object[] { "ip" })) allowed++;
        Check("the first three attempts are allowed", allowed, 3);

        Check("limited at the threshold", (bool)rlBeginAttempt.Invoke(limiter, new object[] { "ip" }), false);

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
// PluginConfiguration initializes every list it owns to an empty list, so a body that fails
// to bind arrives as a pristine default, not null, and does not throw. An import that wrote
// it field by field would replace every profile, device and Bonfire with nothing and report
// success. A camelCase file, a truncated download or an object from another tool would do it.
//
// The endpoint therefore reads properties off the JSON explicitly and case-insensitively, so
// "the file did not contain this" differs from "the file contained an empty list". These
// assertions are on that difference.
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
Console.WriteLine("── Across a Bonfire, profiles have the same protection as accounts ─");

// A link runs both ways, and a sub-profile in another household used to open with or
// without a PIN, from anywhere. Joining somebody's Bonfire — or being joined — let every
// member into your PIN-less profiles. /switch, /verify-pin and /list now ask one function,
// so they cannot disagree about it either.
var evalSwitch = baseType.GetMethod("EvaluateSwitch", Flags);
Check("there is one rule for /switch, /verify-pin and /list", evalSwitch != null, true);

if (evalSwitch != null)
{
    var mine = Guid.NewGuid();
    var theirs = Guid.NewGuid();
    var stranger = Guid.NewGuid();
    var myKid = Guid.NewGuid();
    var theirKidNoPin = Guid.NewGuid();
    var theirKidPin = Guid.NewGuid();
    var strangersKid = Guid.NewGuid();

    object Row(Guid profile, Guid master, bool pin)
    {
        var m = Activator.CreateInstance(mappingType);
        mappingType.GetProperty("ProfileUserId").SetValue(m, profile);
        mappingType.GetProperty("MasterUserId").SetValue(m, master);
        mappingType.GetProperty("PinHash").SetValue(m, pin ? "pbkdf2.sha256$150000$c2FsdA==$aGFzaA==" : null);
        return m;
    }

    var listType = typeof(System.Collections.Generic.List<>).MakeGenericType(mappingType);
    System.Collections.IList Rows(bool ownerConsents, bool ownerHides, bool iHide)
    {
        var rows = (System.Collections.IList)Activator.CreateInstance(listType);
        var myRow = Row(mine, mine, false);
        mappingType.GetProperty("HideOthersSubProfilesFromMe").SetValue(myRow, iHide);
        var theirRow = Row(theirs, theirs, true);
        mappingType.GetProperty("AllowHouseholdLanBypass").SetValue(theirRow, ownerConsents);
        mappingType.GetProperty("HideMySubProfilesFromOthers").SetValue(theirRow, ownerHides);
        rows.Add(myRow);
        rows.Add(theirRow);
        rows.Add(Row(myKid, mine, false));
        rows.Add(Row(theirKidNoPin, theirs, false));
        rows.Add(Row(theirKidPin, theirs, true));
        rows.Add(Row(strangersKid, stranger, false));
        return rows;
    }

    var linked = new System.Collections.Generic.HashSet<Guid> { mine, theirs };

    string Outcome(Guid target, bool isLocal, bool consent = false, bool ownerHides = false, bool iHide = false)
    {
        // No accounts added to this household: those rules are tests/cs/accountlinks.
        var r = evalSwitch.Invoke(null, new object[] { Rows(consent, ownerHides, iHide), mine, target, linked, isLocal, null });
        var ty = r.GetType();
        if ((bool)ty.GetProperty("Unauthorized").GetValue(r)) return "unauthorized";
        if (ty.GetProperty("Refusal").GetValue(r) != null) return "refused";
        var cross = (bool)ty.GetProperty("CrossHousehold").GetValue(r);
        var bypass = (bool)ty.GetProperty("HouseholdLanBypass").GetValue(r);
        return cross ? (bypass ? "cross, no PIN" : "cross, PIN") : "own";
    }

    Check("my own profile, no PIN", Outcome(myKid, false), "own");
    Check("my own account", Outcome(mine, false), "own");
    Check("their profile with a PIN", Outcome(theirKidPin, false), "cross, PIN");
    Check("their profile with no PIN, from away", Outcome(theirKidNoPin, false), "refused");
    Check("their profile with no PIN, at home, owner has not consented", Outcome(theirKidNoPin, true), "refused");
    Check("their profile with no PIN, at home, owner consented", Outcome(theirKidNoPin, true, consent: true), "cross, no PIN");
    Check("their profile with no PIN, away, owner consented", Outcome(theirKidNoPin, false, consent: true), "refused");
    Check("their profile, owner hides their profiles", Outcome(theirKidPin, true, ownerHides: true), "unauthorized");
    Check("their profile, I hide other people's profiles", Outcome(theirKidPin, true, iHide: true), "unauthorized");
    Check("their master is not hidden by either", Outcome(theirs, false, ownerHides: true, iHide: true), "cross, PIN");
    Check("a household I am not linked to", Outcome(strangersKid, true, consent: true), "unauthorized");
}

var controllerSrc = System.IO.File.ReadAllText(RepoPath("Controllers", "ProfilesController.cs"))
    .Replace("\r\n", "\n");
Check("/switch uses it",
    SrcHas(controllerSrc, "var rules = EvaluateSwitch(config.Mappings, callerMasterUserId, request.ProfileId, linkedMasterIds, isLocal,\n                LinkedAccountsFor(callerMasterUserId, config));\n            if (rules.Unauthorized)\n            {\n                return Unauthorized(\"Unauthorized profile switch attempt.\");"), true);
Check("/verify-pin uses it",
    SrcHas(controllerSrc, "return Unauthorized(\"Unauthorized profile PIN verification.\");")
    && System.Text.RegularExpressions.Regex.Matches(controllerSrc, @"EvaluateSwitch\(").Count >= 3, true);
Check("/list drops what /switch would refuse",
    SrcHas(controllerSrc, ".Where(x => !x.Rules.Unauthorized && x.Rules.Refusal == null)"), true);
Check("/list sends a profile's restrictions to its own master only",
    SrcHas(controllerSrc, "AllowedDeviceIds = ownerView ? (m.AllowedDeviceIds ?? new List<string>()) : new List<string>(),"), true);

Console.WriteLine();
Console.WriteLine("── A kick lasts ────────────────────────────────────────────────");

// The kicked member still had the code, and could type it again straight away.
var kickStart = controllerSrc.IndexOf("KickBonfireMember([FromBody]", StringComparison.Ordinal);
var kickEnd = kickStart < 0 ? -1 : controllerSrc.IndexOf("[Http", kickStart, StringComparison.Ordinal);
var kickBody = kickStart < 0 || kickEnd < 0 ? "" : controllerSrc.Substring(kickStart, kickEnd - kickStart);
Check("kicking a member replaces the code", kickBody.Contains("group.BonfireCode = GenerateSecureCode();"), true);
// The owner replaces it on demand through POST bonfire/generate (regenerate-code is gone).
Check("and the owner can replace it on demand", SrcHas(controllerSrc, "[HttpPost(\"bonfire/generate\")]")
    && !SrcHas(controllerSrc, "bonfire/regenerate-code"), true);


Console.WriteLine();
Console.WriteLine("── A limit holds under parallel requests ───────────────────────");

// Checking and then recording left the whole PIN verification in between, so a burst of
// parallel requests all passed the check before any failure was counted.
var limiterType = asm.GetType("Jellyfin.Profiles.Controllers.RateLimiter", true);
var tryBegin = limiterType.GetMethod("TryBegin", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
Check("attempts can be counted and checked in one step", tryBegin != null, true);
if (tryBegin != null)
{
    var ctor = limiterType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).First();
    var limiter = ctor.Invoke(new object[] { 5, 15 });
    var admitted = 0;
    System.Threading.Tasks.Parallel.For(0, 64, _ =>
    {
        if ((bool)tryBegin.Invoke(limiter, new object[] { "one-key" })) System.Threading.Interlocked.Increment(ref admitted);
    });
    Check("64 parallel attempts against a limit of five admit five", admitted, 5);
}

Console.WriteLine();
Console.WriteLine("── What a profile is called ────────────────────────────────────");

// Nothing checked the name: null threw a 500, empty created an account called
// "master_", and a character Jellyfin refuses in a username failed as a 500 with no text.
var validateName = baseType.GetMethod("ValidateProfileName", Flags);
Check("names are validated", validateName != null, true);
if (validateName != null)
{
    bool Accepts(string n) => validateName.Invoke(null, new object[] { n }) == null;
    Check("an ordinary name", Accepts("Kids"), true);
    Check("with a space and an apostrophe", Accepts("Mum's TV"), true);
    Check("in another script", Accepts("Élodie"), true);
    Check("no name", Accepts(null), false);
    Check("only spaces", Accepts("   "), false);
    Check("markup", Accepts("<b>kids</b>"), false);
    Check("a slash", Accepts("kids/tv"), false);
    Check("thirty-three characters", Accepts(new string('a', 33)), false);
}

// A rating below zero was stored as asked when the main account has no limit (R-1141), and a
// device list was stored as sent: untrimmed, with blanks and repeats, and with no cap (R-1155).
var checkRating = baseType.GetMethod("ValidateRatingScore", Flags);
Check("a rating is validated in one place", checkRating != null, true);
if (checkRating != null)
{
    bool RatingOk(string v) => checkRating.Invoke(null, new object[] { v }) == null;
    Check("no rating is no limit", RatingOk(""), true);
    Check("a score", RatingOk("10"), true);
    Check("zero", RatingOk("0"), true);
    Check("minus one", RatingOk("-1"), false);
    Check("not a number", RatingOk("PG"), false);
}

var normIds = baseType.GetMethod("NormalizeAllowedDeviceIds", Flags);
Check("device lists are normalised in one place", normIds != null, true);
if (normIds != null)
{
    (System.Collections.Generic.List<string> Ids, string Error) Norm(System.Collections.Generic.IEnumerable<string> l)
    {
        var r = normIds.Invoke(null, new object[] { l });
        return ((System.Collections.Generic.List<string>)r.GetType().GetField("Item1").GetValue(r), (string)r.GetType().GetField("Item2").GetValue(r));
    }
    var trimmed = Norm(new[] { " tv-1 ", "tv-1", "", "  ", "phone" });
    Check("ids are trimmed and repeats and blanks dropped", string.Join("|", trimmed.Ids ?? new System.Collections.Generic.List<string>()), "tv-1|phone");
    Check("no list stays none", Norm(null).Ids == null, true);
    Check("an id over 128 characters is refused", Norm(new[] { new string('a', 129) }).Error != null, true);
    Check("200 devices are taken", Norm(Enumerable.Range(0, 200).Select(i => "d" + i)).Error == null, true);
    Check("201 devices are refused", Norm(Enumerable.Range(0, 201).Select(i => "d" + i)).Error != null, true);
}
// r2-22-01: tag lists are capped, a fixed limit.
var tagErr = baseType.GetMethod("TagListError", Flags);
Check("tag lists are checked in one place", tagErr != null, true);
if (tagErr != null)
{
    string TagErr(System.Collections.Generic.IEnumerable<string> l) => (string)tagErr.Invoke(null, new object[] { l });
    Check("no list is fine", TagErr(null) == null, true);
    Check("100 tags are taken", TagErr(Enumerable.Range(0, 100).Select(i => "t" + i)) == null, true);
    Check("101 tags are refused", TagErr(Enumerable.Range(0, 101).Select(i => "t" + i)) != null, true);
    Check("repeats are counted once", TagErr(Enumerable.Range(0, 500).Select(i => "t" + (i % 5))) == null, true);
    Check("a tag of 100 characters is taken", TagErr(new[] { new string('a', 100) }) == null, true);
    Check("a tag of 101 characters is refused", TagErr(new[] { new string('a', 101) }) != null, true);
}
Check("create and update both check the tag lists",
    System.Text.RegularExpressions.Regex.Matches(controllerSrc, @"TagListError\(request\.BlockedTags\) \?\? TagListError\(request\.AllowedTags\)").Count, 2);
Check("create checks the rating and the list",SrcHas(controllerSrc, "var ratingError = ValidateRatingScore(request.MaxParentalRating);"), true);
Check("create stores the cleaned list", SrcHas(controllerSrc, "AllowedDeviceIds = newDeviceIds ?? new List<string>(),"), true);
Check("update stores the cleaned list", SrcHas(controllerSrc, "mappingEntry.AllowedDeviceIds = newDeviceIds!;"), true);
Check("the raw list is stored nowhere", SrcHas(controllerSrc, "AllowedDeviceIds = request.AllowedDeviceIds"), false);

var validPin = baseType.GetMethod("IsValidPin", Flags);
Check("PINs are validated in one place", validPin != null, true);
if (validPin != null)
{
    Check("four ASCII digits", (bool)validPin.Invoke(null, new object[] { "0123" }), true);
    // char.IsDigit says yes to these, and no television remote can type them.
    Check("Arabic-Indic digits", (bool)validPin.Invoke(null, new object[] { "٠١٢٣" }), false);
    Check("nine digits", (bool)validPin.Invoke(null, new object[] { "123456789" }), false);
}

Console.WriteLine();
Console.WriteLine("── Pictures from outside the server ────────────────────────────");

// A picture may be any http(s) address, drawn straight from there by every switcher that
// shows it — so a linked household could collect everyone's address from its own server.
var ctlType = asm.GetType("Jellyfin.Profiles.Controllers.ProfilesController", true);
var forViewer = ctlType.GetMethod("ImageForViewer", Flags);
Check("pictures are filtered for who is looking", forViewer != null, true);
if (forViewer != null)
{
    string View(string img, bool own) => (string)forViewer.Invoke(null, new object[] { img, own });
    Check("an outside picture is shown to its own household", View("https://example.com/a.png", true), "https://example.com/a.png");
    Check("and not to another one", View("https://example.com/a.png", false), null);
    Check("a picture stored here is shown to both", View("/plugins/profiles/image/x", false), "/plugins/profiles/image/x");
}

Console.WriteLine();
Console.WriteLine("── Smaller things ──────────────────────────────────────────────");

Check("the sessions page survives a duplicated mapping",
    SrcHas(controllerSrc, ".GroupBy(m => m.ProfileUserId)\n                .ToDictionary(g => g.Key, g => g.First());"), true);
Check("profile creation is one at a time, so the limit holds",
    SrcHas(controllerSrc, "await CreateGate.WaitAsync().ConfigureAwait(false);"), true);
Check("every master-PIN check on an edit is limited",
    !SrcHas(controllerSrc, "VerifyPinAndUpgrade(request.MasterPin"), true);


Console.WriteLine();
Console.WriteLine("── Picture quality ─────────────────────────────────────────────");

// The byte limit a saved picture must fit in follows the administrator's setting, and the
// avatar list tells the browser what size to render at. Standard is what every picture was
// before the setting existed, so an upgrade changes nothing until somebody chooses.
var iqType = asm.GetType("Jellyfin.Profiles.Configuration.ImageQualities");
Check("there are picture quality tiers", iqType != null, true);
if (iqType != null)
{
    var forTier = iqType.GetMethod("For", Flags);
    var normalizeTier = iqType.GetMethod("Normalize", Flags);
    int Bytes(string q) { var s = forTier.Invoke(null, new object[] { q }); return (int)s.GetType().GetProperty("MaxBytes").GetValue(s); }
    int Size(string q) { var s = forTier.Invoke(null, new object[] { q }); return (int)s.GetType().GetProperty("MasterSize").GetValue(s); }

    Check("standard is the 512-pixel, 2 MB picture of every earlier release", (Size("standard"), Bytes("standard")), (512, 2 * 1024 * 1024));
    Check("high is 1024 pixels, 4 MB", (Size("high"), Bytes("high")), (1024, 4 * 1024 * 1024));
    Check("maximum is 2048 pixels, 8 MB", (Size("maximum"), Bytes("maximum")), (2048, 8 * 1024 * 1024));
    // The stored ids are values in saved configs and are NOT the words on screen (Low, Medium,
    // High, Maximum): "high" is still 1024 px and "maximum" still 2048 px, whatever they are
    // called. These four lines are what stops a rename from changing a saved server's setting.
    Check("ultra is 4096 pixels, 16 MB", (Size("ultra"), Bytes("ultra")), (4096, 16 * 1024 * 1024));
    var isKnown = iqType.GetMethod("IsKnown", Flags);
    Check("ultra is a known level", isKnown.Invoke(null, new object[] { "ultra" }), true);
    Check("ULTRA, in any case, is too", normalizeTier.Invoke(null, new object[] { "ULTRA" }), "ultra");
    Check("the largest picture any level takes is ultra's, so a saved one stays readable",
        iqType.GetProperty("LargestMaxBytes", Flags).GetValue(null), 16 * 1024 * 1024);
    // Was { "ultra" }: it is a real level now, so the unknown name here is another one.
    Check("an unknown name is standard", normalizeTier.Invoke(null, new object[] { "enormous" }), "standard");
    Check("and is not known", isKnown.Invoke(null, new object[] { "enormous" }), false);
    Check("so is none", normalizeTier.Invoke(null, new object[] { null }), "standard");

    var cfgType2 = asm.GetType("Jellyfin.Profiles.Configuration.PluginConfiguration", true);
    var cfg2 = Activator.CreateInstance(cfgType2);
    // Was "starts on standard". The default is now maximum (a fresh install); a saved level
    // is kept, which tests/cs/goldenconfig checks against a real file.
    Check("a fresh install starts on maximum", cfgType2.GetProperty("ProfileImageQuality").GetValue(cfg2), "maximum");
}

var baseSrc = System.IO.File.ReadAllText(RepoPath("Controllers", "ProfilesBaseController.cs"));
Check("the save limit is the setting's, not a constant",
    SrcHas(baseSrc, "=> ImageQualities.For(Plugin.Instance?.Configuration?.ProfileImageQuality).MaxBytes;"), true);
Check("the avatar list tells the browser what to render", SrcHas(controllerSrc, "Image = DescribeImageSpec(config),"), true);
Check("an unknown tier is refused when an administrator saves it",
    SrcHas(controllerSrc, "if (request.ProfileImageQuality != null && !ImageQualities.IsKnown(request.ProfileImageQuality))"), true);
Check("and an import carries it", SrcHas(controllerSrc, "config.ProfileImageQuality = ImageQualities.Normalize(incoming.ProfileImageQuality);"), true);

Console.WriteLine();
Console.WriteLine($"{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;
