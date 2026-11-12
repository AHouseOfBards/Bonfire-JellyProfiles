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

// Which build of the plugin to load. This harness's own output sits in
// bin/Release/<tfm>/, so its folder name IS the framework the csproj resolved — there is
// no second place to keep in step, and it cannot say net9.0 while the <Reference> that
// compiled it pointed at net10.0. tests/run.sh cs10 runs the whole set against net10.0.
var tfm = new System.IO.DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
    System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)).Name;
var dll = System.IO.Path.Combine(RepoRoot(), "bin", "Release", tfm, "Jellyfin.Profiles.dll");
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

Ok("there is a record of when LastSeen was last written, separate from LastSeen "
   + "(throttling against LastSeen would never fire for a device in daily use)",
   baseCtl.GetField("LastSeenPersistedAt", Any) != null);
Ok("and it is one stamp, not a dictionary that grows with every device ever seen",
   baseCtl.GetField("DevicePersistedAt", Any) == null
   && baseCtl.GetField("LastSeenPersistedAt", Any)?.FieldType == typeof(DateTime));

Console.WriteLine();
Console.WriteLine("── One save an interval, however many devices ──────────────────");
{
    var registry = asm.GetType("Jellyfin.Profiles.Auth.DeviceRegistry", true);
    var sight = registry.GetMethod("RecordSighting", Any);
    var stamp = baseCtl.GetField("LastSeenPersistedAt", Any);
    var owner = Guid.NewGuid();
    var t0 = new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    var cfg = Activator.CreateInstance(cfgType);
    var list = (IList)cfgType.GetProperty("KnownDevices").GetValue(cfg);
    const int N = 20;
    for (var i = 0; i < N; i++)
    {
        var d = Activator.CreateInstance(deviceType);
        deviceType.GetProperty("DeviceId").SetValue(d, "dev-" + i);
        deviceType.GetProperty("DeviceName").SetValue(d, "Device " + i);
        deviceType.GetProperty("LastSeen").SetValue(d, t0.AddDays(-1));
        deviceType.GetProperty("MasterUserId").SetValue(d, owner);
        list.Add(d);
    }

    int Saves(DateTime at)
    {
        var n = 0;
        for (var i = 0; i < N; i++)
            if ((bool)sight.Invoke(null, new object[] { cfg, "dev-" + i, "Device " + i, "Web", owner, at, null, false })) n++;
        return n;
    }

    stamp?.SetValue(null, DateTime.MinValue);
    var first = Saves(t0);
    Ok(N + " devices seen in the same minute cause one save, not " + N + " (" + first + ")", first == 1);
    Ok("and the rest of the hour causes none (" + Saves(t0.AddMinutes(30)) + ")", Saves(t0.AddMinutes(30)) == 0);
    var next = Saves(t0.AddMinutes(61));
    Ok("an hour on, one more (" + next + ")", next == 1);
    Ok("every device's LastSeen is what the sighting set, so the one save carries all of them",
       list.Cast<object>().All(d => (DateTime)deviceType.GetProperty("LastSeen").GetValue(d) == t0.AddMinutes(61)));
}

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
Console.WriteLine("── One device that got a new id is one row ─────────────────────");
{
    // The owner's screenshot: one household, four "Family Room TV" rows from Jellyfin
    // Android TV, and Tidy up duplicates doing nothing, because it only groups ids that
    // share a base64 user agent. Two causes, both drawn here: a television that minted a
    // new id (reinstall, reset, cleared data) and never used the old one again, and one
    // television signed in as four accounts, which Android TV turns into four ids
    // (sha1(id + "+" + account)) used side by side.
    var registry = asm.GetType("Jellyfin.Profiles.Auth.DeviceRegistry", true);
    var sight = registry.GetMethod("RecordSighting", Any);
    var mergeSame = registry.GetMethod("MergeSameDevices", Any);
    var noteScreen = registry.GetMethod("NoteSignInScreen", Any);
    var forgetScreens = registry.GetMethod("ForgetSignInScreens", Any);
    var stamp = baseCtl.GetField("LastSeenPersistedAt", Any);
    var t0 = new DateTime(2030, 3, 1, 12, 0, 0, DateTimeKind.Utc);
    const string TV = "Jellyfin Android TV";

    string Sha1(string s) => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
        System.Text.Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    (object Cfg, IList List, Guid Master, Guid[] Profiles) Household(int profiles)
    {
        var cfg = Activator.CreateInstance(cfgType);
        var master = Guid.NewGuid();
        var ids = new Guid[profiles];
        var maps = (IList)cfgType.GetProperty("Mappings").GetValue(cfg);
        for (var i = 0; i < profiles; i++)
        {
            ids[i] = Guid.NewGuid();
            var m = Activator.CreateInstance(mappingType);
            mappingType.GetProperty("MasterUserId").SetValue(m, master);
            mappingType.GetProperty("ProfileUserId").SetValue(m, ids[i]);
            mappingType.GetProperty("ProfileName").SetValue(m, "Kid " + i);
            maps.Add(m);
        }
        return (cfg, (IList)cfgType.GetProperty("KnownDevices").GetValue(cfg), master, ids);
    }

    bool See(object cfg, string id, string name, string client, Guid owner, DateTime at)
    {
        stamp?.SetValue(null, DateTime.MinValue);
        return (bool)sight.Invoke(null, new object[] { cfg, id, name, client, owner, at, null, true });
    }

    int Merge(object cfg, Guid household, DateTime at)
        => mergeSame == null ? -1 : (int)mergeSame.Invoke(null, new object[] { cfg, household, at });

    List<string> Ids(IList list) => list.Cast<object>()
        .Select(d => (string)deviceType.GetProperty("DeviceId").GetValue(d)).ToList();

    bool Answers(IList list, string id) => list.Cast<object>()
        .Count(d => (bool)deviceType.GetMethod("Answers").Invoke(d, new object[] { id })) == 1;

    // 1. Four ids one after another: each used, then never again once the next appeared.
    {
        var h = Household(1);
        var allowFirst = (IList)mappingType.GetProperty("AllowedDeviceIds")
            .GetValue(((IList)cfgType.GetProperty("Mappings").GetValue(h.Cfg))[0]);
        var ids = new[] { "fb030d0001", "392e4e0002", "bdf0880003", "51efea0004" }
            .Select(s => Sha1(s)).ToArray();
        for (var i = 0; i < ids.Length; i++)
        {
            See(h.Cfg, ids[i], "Family Room TV", TV, h.Master, t0.AddDays(i * 7));
            See(h.Cfg, ids[i], "Family Room TV", TV, h.Master, t0.AddDays(i * 7 + 2));
            if (i == 0) allowFirst.Add(ids[0]);
        }
        Ok("two days after the newest id appeared it is still its own row: the old one may be in use ("
           + h.List.Count + ")", h.List.Count >= 2);
        // A week on, with nothing seen on the old ids since: the next sighting tidies.
        See(h.Cfg, ids[3], "Family Room TV", TV, h.Master, t0.AddDays(30));
        Ok("four ids that followed one another on one television are one row (" + h.List.Count + ")",
           h.List.Count == 1);
        Ok("the row kept is the newest id", h.List.Count == 1 && Ids(h.List)[0] == ids[3]);
        Ok("every old id still answers as that row", ids.All(id => Answers(h.List, id)));
        Ok("a profile allowed on the first id is allowed on the merged one",
           allowFirst.Cast<string>().Contains(ids[3]) && !allowFirst.Cast<string>().Contains(ids[0]));
    }

    // 2. One television, four accounts: four derived ids in use side by side. The time rule
    //    cannot join these; the derivation can, once the set's own id has been seen.
    {
        if (forgetScreens != null) forgetScreens.Invoke(null, null);
        var h = Household(3);
        const string bare = "9a6dae35cc29c74f";
        var accounts = new[] { h.Master }.Concat(h.Profiles).ToArray();
        for (var round = 0; round < 3; round++)
            foreach (var a in accounts)
                See(h.Cfg, Sha1(bare + "+" + a), "Family Room TV", TV, h.Master, t0.AddHours(round * 5 + Array.IndexOf(accounts, a)));
        Ok("four accounts on one television, its own id not yet seen, stay four rows (" + h.List.Count + ")",
           h.List.Count == 4);
        noteScreen.Invoke(null, new object[] { bare });
        var merged = Merge(h.Cfg, h.Master, t0.AddDays(1));
        Ok("once the sign-in screen has shown the set's own id, they are one row (" + h.List.Count + ")",
           h.List.Count == 1 && merged == 3);
        Ok("every account's id answers as that row", accounts.All(a => Answers(h.List, Sha1(bare + "+" + a))));
        Ok("merging again changes nothing (" + Merge(h.Cfg, h.Master, t0.AddDays(2)) + ")",
           Merge(h.Cfg, h.Master, t0.AddDays(2)) == 0 && h.List.Count == 1);
        if (forgetScreens != null) forgetScreens.Invoke(null, null);
    }

    // 3. The second screenshot's Pixel 8: Jellyfin for Android's own id, and the same id with
    //    the account appended, used side by side.
    {
        var h = Household(1);
        const string bare = "6044d6840404e4e8";
        See(h.Cfg, bare, "Pixel 8", "Jellyfin Android", h.Master, t0);
        See(h.Cfg, bare + h.Master, "Pixel 8", "Jellyfin Android", h.Master, t0.AddMinutes(1));
        See(h.Cfg, bare, "Pixel 8", "Jellyfin Android", h.Master, t0.AddMinutes(2));
        Merge(h.Cfg, h.Master, t0.AddMinutes(3));
        Ok("a phone's own id and its id with the account appended are one row (" + h.List.Count + ")",
           h.List.Count == 1 && Answers(h.List, bare) && Answers(h.List, bare + h.Master));
    }

    // 4. Two tablets, one renamed to match the other, both in use: never one row.
    {
        var h = Household(1);
        See(h.Cfg, "tablet-a-id-0001", "Kids Tablet", "Jellyfin Android", h.Master, t0);
        See(h.Cfg, "tablet-b-id-0002", "Kids Tablet", "Jellyfin Android", h.Master, t0.AddDays(1));
        See(h.Cfg, "tablet-a-id-0001", "Kids Tablet", "Jellyfin Android", h.Master, t0.AddDays(2));
        See(h.Cfg, "tablet-b-id-0002", "Kids Tablet", "Jellyfin Android", h.Master, t0.AddDays(3));
        Merge(h.Cfg, h.Master, t0.AddDays(4));
        Merge(h.Cfg, h.Master, t0.AddDays(40));
        Ok("two tablets with one name, used in the same weeks, stay two rows (" + h.List.Count + ")",
           h.List.Count == 2);
    }

    // 5. The rule is per household, per client, per name.
    {
        var h = Household(1);
        var other = Guid.NewGuid();
        See(h.Cfg, "home-tv-0000001", "Family Room TV", TV, h.Master, t0);
        See(h.Cfg, "other-home-tv-02", "Family Room TV", TV, other, t0.AddDays(5));
        See(h.Cfg, "home-tv-chrome-3", "Family Room TV", "Jellyfin Web", h.Master, t0.AddDays(10));
        See(h.Cfg, "new-name-tv-0004", "Den TV", TV, h.Master, t0.AddDays(15));
        Merge(h.Cfg, h.Master, t0.AddDays(20));
        Ok("another household's set, another app, another name: none merged (" + h.List.Count + ")",
           h.List.Count == 4);
    }
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
