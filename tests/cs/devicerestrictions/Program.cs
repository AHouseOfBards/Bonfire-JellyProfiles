using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

// ─────────────────────────────────────────────────────────────────────────────
// A device restriction has two ways to be wrong, and only one of them is loud.
//
//   Too narrow: the restriction refuses a device it should have allowed. Taken to
//   its limit it refuses *every* device, and the profile cannot be reached from
//   anywhere — which is what "restricting sub-profiles to specific devices breaks
//   the gate" turned out to mean.
//
//   Too wide: the restriction quietly stops restricting. An empty AllowedDeviceIds
//   means "any device", so anything that can empty a whitelist switches the feature
//   off without saying so. tests/cs/devices covers the pruning route into this;
//   here it is the forget-a-device and merge-two-devices routes.
//
// Both directions are asserted here because a fix aimed at one of them is exactly
// how a project acquires the other.
//
// Point it at an older build to watch it fail:
//   BONFIRE_DLL=/path/to/old/Jellyfin.Profiles.dll dotnet run --project tests/cs/devicerestrictions -c Release
// ─────────────────────────────────────────────────────────────────────────────

static string RepoRoot()
{
    var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
    while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "Jellyfin.Profiles.csproj")))
        d = d.Parent;
    if (d == null) throw new InvalidOperationException("Could not find the repository root.");
    return d.FullName;
}

int pass = 0;
var fails = new List<string>();
void Ok(string name, bool cond, string detail = null)
{
    if (cond) { pass++; Console.WriteLine("  PASS  " + name); }
    else
    {
        fails.Add(name);
        Console.WriteLine("  FAIL  " + name);
        if (!string.IsNullOrEmpty(detail)) Console.WriteLine("        " + detail);
    }
}

var dll = Environment.GetEnvironmentVariable("BONFIRE_DLL");
if (string.IsNullOrEmpty(dll))
    dll = System.IO.Path.Combine(RepoRoot(), "bin", "Release", "net9.0", "Jellyfin.Profiles.dll");

Console.WriteLine("  against: " + dll);
var asm = Assembly.LoadFrom(dll);
var cfgType = asm.GetType("Jellyfin.Profiles.Configuration.PluginConfiguration", true);
var deviceType = asm.GetType("Jellyfin.Profiles.Configuration.KnownDevice", true);
var mappingType = asm.GetType("Jellyfin.Profiles.Configuration.ProfileMapping", true);
var baseCtl = asm.GetType("Jellyfin.Profiles.Controllers.ProfilesBaseController", true);

const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance
                       | BindingFlags.NonPublic | BindingFlags.Public;

// Every member this file drives. Missing ones are reported once, as a failure, rather
// than thrown one at a time — pointed at a build from before the fix, the useful output
// is "none of this exists yet", not a stack trace from the first line.
MethodInfo M(string name)
{
    var m = baseCtl.GetMethod(name, Any);
    if (m == null) { fails.Add("ProfilesBaseController." + name + " does not exist"); Console.WriteLine("  FAIL  ProfilesBaseController." + name + " does not exist"); }
    return m;
}

var parse = M("ParseAuthorizationParameter");
var evaluate = M("EvaluateDeviceRestriction");
var unrestricted = M("ProfilesLeftUnrestrictedBy");
var merge = M("MergeDeviceRecords");
var disambiguate = M("DisambiguateDeviceNames");
var decodeLegacy = M("DecodeLegacyDeviceName");

object Device(string id, string name, string client, Guid owner)
{
    var d = Activator.CreateInstance(deviceType);
    deviceType.GetProperty("DeviceId").SetValue(d, id);
    deviceType.GetProperty("DeviceName").SetValue(d, name);
    deviceType.GetProperty("Client").SetValue(d, client);
    deviceType.GetProperty("LastSeen").SetValue(d, DateTime.UtcNow);
    deviceType.GetProperty("MasterUserId").SetValue(d, owner);
    return d;
}

object Mapping(Guid profile, Guid master, params string[] allowed)
{
    var m = Activator.CreateInstance(mappingType);
    mappingType.GetProperty("ProfileUserId").SetValue(m, profile);
    mappingType.GetProperty("MasterUserId").SetValue(m, master);
    var list = (IList)mappingType.GetProperty("AllowedDeviceIds").GetValue(m);
    foreach (var a in allowed) list.Add(a);
    return m;
}

// EvaluateDeviceRestriction takes IEnumerable<KnownDevice>, so the list has to be the
// real generic type — an ArrayList is rejected by the reflection binder.
IList KnownList(params object[] devices)
{
    var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(deviceType));
    foreach (var d in devices) list.Add(d);
    return list;
}

string Access(object mapping, string deviceId, IEnumerable known)
    => evaluate.Invoke(null, new object[] { mapping, deviceId, known }).ToString();

var master = Guid.NewGuid();
var child = Guid.NewGuid();

// ── The header the DeviceId is read out of ──────────────────────────────────
//
// This is where the severe half of the bug lived. The restriction is checked against
// GetAuthorizationParameter("DeviceId"), and a header that fails to parse yields null,
// which is indistinguishable from a device that is not on the list. Every one of these
// is a header a real Jellyfin client sends.
if (parse != null)
{
    Console.WriteLine();
    Console.WriteLine("── The authorization header parses the way Jellyfin parses it ──");

    string P(string auth, string emby, string name)
        => (string)parse.Invoke(null, new object[] { auth, emby, name });

    var plain = "MediaBrowser Client=\"Jellyfin Web\", Device=\"Chrome\", DeviceId=\"abc123\", Version=\"10.11.5\", Token=\"t\"";
    Ok("an ordinary header still reads", P(plain, null, "DeviceId") == "abc123");
    Ok("the scheme prefix is not mistaken for a value", P(plain, null, "Client") == "Jellyfin Web");

    // Jellyfin's own AuthorizationContext falls back to this header; a number of clients
    // still send only it. Reading Authorization alone returned null for every parameter,
    // so those clients were refused by a device check they were never measured against.
    var emby = "MediaBrowser Client=\"Jellyfin Android\", Device=\"Pixel\", DeviceId=\"xyz789\", Version=\"2.6\"";
    Ok("X-Emby-Authorization is read when Authorization is absent", P(null, emby, "DeviceId") == "xyz789");
    Ok("X-Emby-Authorization is read when Authorization is empty", P("", emby, "DeviceId") == "xyz789");
    Ok("Authorization wins when both are present",
       P(plain, emby, "DeviceId") == "abc123");

    // The device name is user-settable and travels unencoded, so a comma in it used to
    // shift every parameter after it out of alignment — including DeviceId.
    var comma = "MediaBrowser Client=\"Jellyfin Web\", Device=\"Living Room, TV\", DeviceId=\"tv-1\", Version=\"10.11.5\"";
    Ok("a comma inside a quoted value does not split it", P(comma, null, "Device") == "Living Room, TV");
    Ok("a comma in the device name does not lose the DeviceId", P(comma, null, "DeviceId") == "tv-1");

    // Jellyfin URL-decodes every value. Not decoding recorded one device under two
    // spellings and displayed "Xbox%20One" in the picker.
    var encoded = "MediaBrowser Client=\"Jellyfin\", Device=\"Xbox%20One\", DeviceId=\"box\", Version=\"1\"";
    Ok("values are URL-decoded", P(encoded, null, "Device") == "Xbox One");

    Ok("an absent parameter is null", P(plain, null, "Nonsense") == null);
    Ok("no header at all is null", P(null, null, "DeviceId") == null);
    Ok("the name is matched case-insensitively", P(plain, null, "deviceid") == "abc123");

    // ── The defect itself, pinned ───────────────────────────────────────────
    //
    // Everything above passes trivially against a build that never had the bug, and a
    // build that still has it fails only because the method is missing — which proves
    // nothing about the old behaviour. So the shipped rule is transcribed here and run
    // against the same headers, pairwise. Each pair says: this input, under the rule
    // 1.5.9 shipped, produced this wrong answer, and produces the right one now.
    //
    // Verbatim from ProfilesBaseController.GetAuthorizationParameter as of 046b88b:
    // read Authorization only, Split(','), StartsWith(name + "="), Trim('"', ' ').
    string Shipped(string authHeader, string name)
    {
        if (string.IsNullOrEmpty(authHeader)) return null;
        const string scheme = "MediaBrowser ";
        if (authHeader.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            authHeader = authHeader.Substring(scheme.Length);
        foreach (var part in authHeader.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return trimmed.Substring(name.Length + 1).Trim('"', ' ');
        }
        return null;
    }

    // The severe one. A client that sends only X-Emby-Authorization had no DeviceId as
    // far as the plugin was concerned, and a null DeviceId fails the device check — so a
    // profile restricted to a device was unreachable from every such client.
    Ok("PINNED: the shipped rule found no DeviceId in an X-Emby-Authorization request",
       Shipped(null, "DeviceId") == null && P(null, emby, "DeviceId") == "xyz789");

    // The quiet one. Splitting on every comma breaks a value that legitimately contains
    // one, and the device name is free text the user types.
    Ok("PINNED: the shipped rule truncated a device name at its comma",
       Shipped(comma, "Device") == "Living Room" && P(comma, null, "Device") == "Living Room, TV");

    Ok("PINNED: the shipped rule left values URL-encoded",
       Shipped(encoded, "Device") == "Xbox%20One" && P(encoded, null, "Device") == "Xbox One");

    // Not every difference is a fix: the ordinary case has to be unchanged, or the new
    // parser is a rewrite rather than a repair.
    Ok("the ordinary header reads identically under both rules",
       Shipped(plain, "DeviceId") == P(plain, null, "DeviceId")
       && Shipped(plain, "Client") == P(plain, null, "Client"));
}

// ── Too narrow: a restriction must not refuse everything by accident ────────
if (evaluate != null)
{
    Console.WriteLine();
    Console.WriteLine("── A restriction refuses the right devices, and only those ─────");

    var known = KnownList(Device("laptop", "Chrome", "Jellyfin Web", master));

    Ok("a device on the list is allowed",
       Access(Mapping(child, master, "laptop"), "laptop", known) == "Allowed");
    Ok("the match is case-insensitive",
       Access(Mapping(child, master, "LAPTOP"), "laptop", known) == "Allowed");
    Ok("surrounding space does not make it a different device",
       Access(Mapping(child, master, " laptop "), "laptop", known) == "Allowed");

    Ok("an empty list means any device",
       Access(Mapping(child, master), "anything", known) == "NotRestricted");
    Ok("a list of blank strings is not a restriction",
       Access(Mapping(child, master, "", "   "), "anything", known) == "NotRestricted");

    // A master is not a sub-profile. Restricting one would be a way to lock an owner out
    // of their own household with no route back in.
    Ok("a master account is never device-restricted",
       Access(Mapping(master, master, "laptop"), "phone", known) == "NotRestricted");
    Ok("no mapping at all is not a restriction",
       Access(null, "phone", known) == "NotRestricted");

    Ok("a device that is not on a live list is refused",
       Access(Mapping(child, master, "laptop"), "phone", known) == "DeniedNotOnList");

    // The two refusals below were one message. They have different causes and different
    // answers, and telling somebody "not allowed on this device" when the profile is
    // allowed on no device at all is how the report read as "it breaks the gate".
    Ok("a request with no DeviceId is refused as its own case",
       Access(Mapping(child, master, "laptop"), null, known) == "DeniedNoDeviceId");
    Ok("a blank DeviceId is refused as its own case",
       Access(Mapping(child, master, "laptop"), "   ", known) == "DeniedNoDeviceId");

    var goneKnown = KnownList(Device("laptop", "Chrome", "Jellyfin Web", master));
    Ok("a list naming only devices the server has forgotten is reported as stale",
       Access(Mapping(child, master, "old-tv"), "laptop", goneKnown) == "DeniedListIsStale");
    Ok("a stale list is still a refusal, never a silent widening",
       Access(Mapping(child, master, "old-tv"), "laptop", goneKnown) != "Allowed"
       && Access(Mapping(child, master, "old-tv"), "laptop", goneKnown) != "NotRestricted");
    Ok("one surviving device is enough to not be stale",
       Access(Mapping(child, master, "old-tv", "laptop"), "phone", goneKnown) == "DeniedNotOnList");
    Ok("without a device list the stale case is simply an ordinary refusal",
       Access(Mapping(child, master, "old-tv"), "laptop", null) == "DeniedNotOnList");
}

// ── Too wide: nothing may empty a whitelist as a side effect ────────────────
if (unrestricted != null)
{
    Console.WriteLine();
    Console.WriteLine("── Forgetting a device cannot switch a restriction off ─────────");

    var cfg = Activator.CreateInstance(cfgType);
    var maps = (IList)cfgType.GetProperty("Mappings").GetValue(cfg);
    maps.Add(Mapping(child, master, "only-tv"));

    var affected = (IList)unrestricted.Invoke(null, new object[] { cfg, new[] { "only-tv" } });
    Ok("forgetting a profile's only allowed device is reported", affected.Count == 1);

    var none = (IList)unrestricted.Invoke(null, new object[] { cfg, new[] { "some-other-device" } });
    Ok("forgetting an unrelated device is not reported", none.Count == 0);

    var twoCfg = Activator.CreateInstance(cfgType);
    ((IList)cfgType.GetProperty("Mappings").GetValue(twoCfg)).Add(Mapping(child, master, "tv", "laptop"));
    var one = (IList)unrestricted.Invoke(null, new object[] { twoCfg, new[] { "tv" } });
    Ok("forgetting one of two allowed devices is fine", one.Count == 0);
    var both = (IList)unrestricted.Invoke(null, new object[] { twoCfg, new[] { "tv", "laptop" } });
    Ok("forgetting both of two allowed devices is reported", both.Count == 1);

    var openCfg = Activator.CreateInstance(cfgType);
    ((IList)cfgType.GetProperty("Mappings").GetValue(openCfg)).Add(Mapping(child, master));
    var openNone = (IList)unrestricted.Invoke(null, new object[] { openCfg, new[] { "tv" } });
    Ok("a profile that was never restricted cannot be widened", openNone.Count == 0);
}

// ── Merging is the answer to duplicates, and must not widen either ──────────
if (merge != null)
{
    Console.WriteLine();
    Console.WriteLine("── Merging two records for one machine keeps the restriction ───");

    var cfg = Activator.CreateInstance(cfgType);
    var known = (IList)cfgType.GetProperty("KnownDevices").GetValue(cfg);
    var maps = (IList)cfgType.GetProperty("Mappings").GetValue(cfg);

    known.Add(Device("lan-id", "Chrome", "Jellyfin Web", master));
    known.Add(Device("wan-id", "Chrome", "Jellyfin Web", master));
    var map = Mapping(child, master, "lan-id");
    maps.Add(map);

    var ok = (bool)merge.Invoke(null, new object[] { cfg, "lan-id", "wan-id" });
    var allowed = ((IList)mappingType.GetProperty("AllowedDeviceIds").GetValue(map)).Cast<string>().ToList();

    Ok("the merge reports success", ok);
    Ok("the duplicate record is gone", known.Count == 1);
    Ok("the surviving record is the one merged into",
       (string)deviceType.GetProperty("DeviceId").GetValue(known[0]) == "wan-id");
    Ok("the whitelist follows the merge rather than emptying",
       allowed.Count == 1 && allowed[0] == "wan-id");
    Ok("the profile is still restricted after the merge",
       Access(map, "some-other-device", known) == "DeniedNotOnList");
    Ok("and the merged-into device may now be used",
       Access(map, "wan-id", known) == "Allowed");

    // Merging a device into one the profile already allows must not leave a duplicate
    // entry behind, and must not drop the restriction on the way through.
    var cfg2 = Activator.CreateInstance(cfgType);
    var known2 = (IList)cfgType.GetProperty("KnownDevices").GetValue(cfg2);
    known2.Add(Device("a", "Chrome", "Web", master));
    known2.Add(Device("b", "Chrome", "Web", master));
    var map2 = Mapping(child, master, "a", "b");
    ((IList)cfgType.GetProperty("Mappings").GetValue(cfg2)).Add(map2);
    merge.Invoke(null, new object[] { cfg2, "a", "b" });
    var allowed2 = ((IList)mappingType.GetProperty("AllowedDeviceIds").GetValue(map2)).Cast<string>().ToList();
    Ok("merging two devices a profile both allowed leaves one entry",
       allowed2.Count == 1 && allowed2[0] == "b");

    Ok("a device cannot be merged into itself",
       !(bool)merge.Invoke(null, new object[] { cfg2, "b", "b" }));
    Ok("merging an unknown device is refused",
       !(bool)merge.Invoke(null, new object[] { cfg2, "ghost", "b" }));
}

// ── The picker has to be readable, or the restriction cannot be set at all ──
if (disambiguate != null)
{
    Console.WriteLine();
    Console.WriteLine("── No two rows in the picker read the same ─────────────────────");

    List<string> Labels(params object[] devices)
    {
        var result = (IList)disambiguate.Invoke(null, new object[] { KnownList(devices) });
        return result.Cast<object>()
            .Select(d => (string)deviceType.GetProperty("DeviceName").GetValue(d))
            .ToList();
    }

    var nameless = Labels(
        Device("aaaaaa11", "", "Jellyfin Web", master),
        Device("bbbbbb22", "", "Jellyfin Web", master));
    Ok("two nameless devices do not render as the same row",
       nameless[0] != nameless[1]);
    Ok("a nameless device falls back to its client before its id",
       nameless[0].StartsWith("Jellyfin Web"));

    var placeholder = Labels(Device("cccccc33", "Unknown Device", "Jellyfin Android", master));
    Ok("the stored \"Unknown Device\" placeholder is replaced, not shown",
       placeholder[0] == "Jellyfin Android");

    var same = Labels(
        Device("dddddd44", "Chrome", "Jellyfin Web", master),
        Device("eeeeee55", "Chrome", "Jellyfin Web", master));
    Ok("one machine reached at two origins gives two tellable-apart rows",
       same[0] != same[1] && same[0].StartsWith("Chrome") && same[1].StartsWith("Chrome"));

    var distinct = Labels(
        Device("ffffff66", "Living room TV", "Jellyfin Android", master),
        Device("gggggg77", "Chrome", "Jellyfin Web", master));
    Ok("devices that already read differently are left alone",
       distinct[0] == "Living room TV" && distinct[1] == "Chrome");

    var blank = Labels(Device("hhhhhh88", "", "", master));
    Ok("a device with neither name nor client still says something",
       !string.IsNullOrWhiteSpace(blank[0]));

    // The shape a jellyfin-web device id actually has. generateDeviceId() is
    // btoa(navigator.userAgent + '|' + Date.now()), so every id one browser ever mints
    // begins with the same forty-odd characters -- the base64 of its user agent -- and
    // the timestamps end in zeros, so the tails collide too. Taking a fragment from
    // either end produced four rows all reading "Chrome (TW96aW)", which is base64 for
    // "Mozil". Reported from a real device picker, with a screenshot.
    const string UaPrefix = "TW96aWxsYS81LjAgKFdpbmRvd3MgTlQgMTAuMCkg";
    var realistic = Labels(
        Device(UaPrefix + "MTc1NjYwMDAwMDAwMA", "Chrome", "Jellyfin Web", master),
        Device(UaPrefix + "MTc1NzIwMDAwMDAwMA", "Chrome", "Jellyfin Web", master),
        Device(UaPrefix + "MTc1ODgwMDAwMDAwMA", "Chrome", "Jellyfin Web", master));

    Ok("ids sharing a long prefix still produce three different rows",
       realistic.Distinct().Count() == 3,
       string.Join(" | ", realistic));
    Ok("and the fragment is taken from where they differ, not from the front",
       realistic.All(n => !n.Contains("TW96aW")),
       string.Join(" | ", realistic));

    // The picker is read by a person, so the fragment has to be short enough to scan.
    Ok("the fragment stays short",
       realistic.All(n => n.Length <= "Chrome".Length + 12),
       string.Join(" | ", realistic));
}

// ── Names recorded before the parser was fixed ──────────────────────────────
if (decodeLegacy != null)
{
    Console.WriteLine();
    Console.WriteLine("-- Stored names repair themselves ------------------------------");

    string D(string name) => (string)decodeLegacy.Invoke(null, new object[] { name });

    // "Pixel+8" is a URL-encoded space, stored when the header parser returned values
    // verbatim. It stays wrong until that phone next contacts the server, which for a
    // whitelisted device can be months.
    Ok("a plus that was a space is decoded", D("Pixel+8") == "Pixel 8");
    Ok("a percent escape is decoded", D("Xbox%20One") == "Xbox One");

    // And a name somebody actually typed must survive intact.
    Ok("a real plus in a real name is left alone", D("Pixel 8 Pro+") == "Pixel 8 Pro+");
    Ok("an ordinary name is untouched", D("Living room TV") == "Living room TV");
    Ok("an empty name stays empty", D("") == "");
    Ok("null is handled", D(null) == "");
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
