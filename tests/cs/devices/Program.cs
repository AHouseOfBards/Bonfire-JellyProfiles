using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

// ─────────────────────────────────────────────────────────────────────────────
// KnownDevices is one server-wide list that only ever grew. Every phone that
// ever touched the server stayed in it forever, and the device picker is a list
// an administrator has to read before deciding what a profile may be used on.
//
// Pruning it is only safe under one rule: a device some profile still names is
// kept however old it is. AllowedDeviceIds being empty means "any device", so
// removing the last entry from a whitelist does not tidy a list — it turns a
// restriction off. That is the assertion this file exists for; the rest is the
// boundary either side of it.
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
void Ok(string name, bool cond)
{
    if (cond) { pass++; Console.WriteLine("  PASS  " + name); }
    else { fails.Add(name); Console.WriteLine("  FAIL  " + name); }
}

var dll = System.IO.Path.Combine(RepoRoot(), "bin", "Release", "net9.0", "Jellyfin.Profiles.dll");
var asm = Assembly.LoadFrom(dll);
var cfgType = asm.GetType("Jellyfin.Profiles.Configuration.PluginConfiguration", true);
var deviceType = asm.GetType("Jellyfin.Profiles.Configuration.KnownDevice", true);
var mappingType = asm.GetType("Jellyfin.Profiles.Configuration.ProfileMapping", true);
var baseCtl = asm.GetType("Jellyfin.Profiles.Controllers.ProfilesBaseController", true);

const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance
                       | BindingFlags.NonPublic | BindingFlags.Public;

Console.WriteLine();
Console.WriteLine("── The intervals are what the comments claim ───────────────────");

TimeSpan Field(string name)
{
    var f = baseCtl.GetField(name, Any);
    return f == null ? TimeSpan.Zero : (TimeSpan)f.GetValue(null);
}

var writeEvery = Field("DeviceLastSeenWriteInterval");
var retention = Field("DeviceRetention");

Ok("LastSeen is written at most once an hour per device (" + writeEvery + ")",
   writeEvery == TimeSpan.FromHours(1));
Ok("devices are kept for 180 days after they were last seen (" + retention + ")",
   retention == TimeSpan.FromDays(180));
Ok("the write interval is far finer than the retention it feeds — otherwise the "
   + "persisted LastSeen could be stale enough to change the pruning answer",
   writeEvery > TimeSpan.Zero && retention > TimeSpan.FromDays(1)
   && writeEvery < TimeSpan.FromTicks(retention.Ticks / 100));

Ok("there is a record of when each device was last written, separate from LastSeen "
   + "(throttling against LastSeen would never fire for a device in daily use)",
   baseCtl.GetField("DevicePersistedAt", Any) != null);

Console.WriteLine();
Console.WriteLine("── Pruning keeps anything a profile still names ────────────────");

var prune = baseCtl.GetMethod("RemoveStaleDevices", Any);
Ok("the pruning is reachable", prune != null);

if (prune != null)
{
    var now = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);

    object Device(string id, int daysAgo)
    {
        var d = Activator.CreateInstance(deviceType);
        deviceType.GetProperty("DeviceId").SetValue(d, id);
        deviceType.GetProperty("DeviceName").SetValue(d, id + " name");
        deviceType.GetProperty("LastSeen").SetValue(d, now.AddDays(-daysAgo));
        return d;
    }

    // Named for what each one is testing, so a failure below reads as a sentence.
    var devices = new[]
    {
        (Id: "phone-seen-today",        Days: 0,   Whitelisted: false, Keep: true,
         Why: "in daily use"),
        (Id: "tablet-179-days",         Days: 179, Whitelisted: false, Keep: true,
         Why: "one day inside the window"),
        (Id: "laptop-exactly-180",      Days: 180, Whitelisted: false, Keep: true,
         Why: "exactly at the boundary — the cut is strictly older than"),
        (Id: "old-phone-181-days",      Days: 181, Whitelisted: false, Keep: false,
         Why: "one day past, nobody names it"),
        (Id: "ancient-tv-900-days",     Days: 900, Whitelisted: false, Keep: false,
         Why: "long gone, nobody names it"),
        (Id: "kids-tv-on-a-whitelist",  Days: 900, Whitelisted: true,  Keep: true,
         Why: "a profile still names it — removing it would silently allow every device"),
        (Id: "CASE-Mismatch-Device",    Days: 900, Whitelisted: true,  Keep: true,
         Why: "named with different casing; device ids are matched case-insensitively"),
    };

    var config = Activator.CreateInstance(cfgType);
    var known = (IList)cfgType.GetProperty("KnownDevices").GetValue(config);
    foreach (var d in devices) known.Add(Device(d.Id, d.Days));

    // Two profiles, so the whitelist that protects a device is not necessarily on the
    // profile that last used it. One of them has an empty list, which means "any device"
    // and must contribute nothing to the protected set.
    var mappings = (IList)cfgType.GetProperty("Mappings").GetValue(config);

    var withWhitelist = Activator.CreateInstance(mappingType);
    var allowed = (IList)mappingType.GetProperty("AllowedDeviceIds").GetValue(withWhitelist);
    allowed.Add("kids-tv-on-a-whitelist");
    allowed.Add("case-mismatch-device");          // lower case on purpose
    mappings.Add(withWhitelist);

    var unrestricted = Activator.CreateInstance(mappingType);
    mappings.Add(unrestricted);                   // empty AllowedDeviceIds == any device

    var removed = (int)prune.Invoke(null, new object[] { config, now });

    var survivors = new HashSet<string>(
        known.Cast<object>().Select(d => (string)deviceType.GetProperty("DeviceId").GetValue(d)),
        StringComparer.Ordinal);

    // Enumerated one device at a time. A count would say the wrong number survived
    // without saying which, and a wrongly-kept device and a wrongly-dropped one cancel out.
    foreach (var d in devices)
    {
        var kept = survivors.Contains(d.Id);
        Ok((d.Keep ? "keeps  " : "drops  ") + d.Id.PadRight(24)
           + " — " + d.Why, kept == d.Keep);
    }

    Ok("the reported count matches what actually went ("
       + removed + " reported, " + (devices.Length - survivors.Count) + " gone)",
       removed == devices.Length - survivors.Count);

    Console.WriteLine();
    Console.WriteLine("── An empty whitelist does not protect everything ──────────────");

    // If "no restriction" were folded into the protected set as a wildcard, nothing would
    // ever be pruned and this whole feature would be inert while looking like it worked.
    var onlyUnrestricted = Activator.CreateInstance(cfgType);
    var known2 = (IList)cfgType.GetProperty("KnownDevices").GetValue(onlyUnrestricted);
    known2.Add(Device("forgotten-phone", 900));
    ((IList)cfgType.GetProperty("Mappings").GetValue(onlyUnrestricted))
        .Add(Activator.CreateInstance(mappingType));

    var removed2 = (int)prune.Invoke(null, new object[] { onlyUnrestricted, now });
    Ok("a stale device is still dropped when every profile allows any device",
       removed2 == 1 && known2.Count == 0);

    Console.WriteLine();
    Console.WriteLine("── Nothing is dropped from a server with nothing stale ─────────");

    var fresh = Activator.CreateInstance(cfgType);
    var known3 = (IList)cfgType.GetProperty("KnownDevices").GetValue(fresh);
    known3.Add(Device("a", 1));
    known3.Add(Device("b", 30));
    var removed3 = (int)prune.Invoke(null, new object[] { fresh, now });
    Ok("pruning a healthy list is a no-op", removed3 == 0 && known3.Count == 2);
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
