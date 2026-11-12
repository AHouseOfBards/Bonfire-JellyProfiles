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
    Console.WriteLine("── A whitelist entry naming an alias protects the record ───────");

    {
        // A merge keeps the old id as an alias of the surviving record; a profile's list
        // may still name the old id. Dropping that record would widen the profile.
        var aliasCfg = Activator.CreateInstance(cfgType);
        var aliasKnown = (IList)cfgType.GetProperty("KnownDevices").GetValue(aliasCfg);
        var merged = Device("new-id", 900);
        ((IList<string>)deviceType.GetProperty("Aliases").GetValue(merged)).Add("Old-Id");
        aliasKnown.Add(merged);
        aliasKnown.Add(Device("unrelated", 900));
        var aliasMap = Activator.CreateInstance(mappingType);
        ((IList)mappingType.GetProperty("AllowedDeviceIds").GetValue(aliasMap)).Add("old-id");
        ((IList)cfgType.GetProperty("Mappings").GetValue(aliasCfg)).Add(aliasMap);

        prune.Invoke(null, new object[] { aliasCfg, now });
        var ids = aliasKnown.Cast<object>().Select(d => (string)deviceType.GetProperty("DeviceId").GetValue(d)).ToList();
        Ok("keeps a record whose alias a profile names", ids.Contains("new-id"));
        Ok("still drops the unrelated old record", !ids.Contains("unrelated"));
    }

    // r2-09-02: every other reader trims a list's ids and tolerates a null list.
    {
        var padCfg = Activator.CreateInstance(cfgType);
        var padKnown = (IList)cfgType.GetProperty("KnownDevices").GetValue(padCfg);
        padKnown.Add(Device("padded-tv", 900));
        padKnown.Add(Device("unrelated-2", 900));
        var padMaps = (IList)cfgType.GetProperty("Mappings").GetValue(padCfg);
        var padded = Activator.CreateInstance(mappingType);
        ((IList)mappingType.GetProperty("AllowedDeviceIds").GetValue(padded)).Add("  padded-tv ");
        padMaps.Add(padded);
        var nullList = Activator.CreateInstance(mappingType);
        mappingType.GetProperty("AllowedDeviceIds").SetValue(nullList, null);
        padMaps.Add(nullList);

        object threw = null;
        try { prune.Invoke(null, new object[] { padCfg, now }); } catch (Exception ex) { threw = ex.GetBaseException().Message; }
        var padIds = padKnown.Cast<object>().Select(d => (string)deviceType.GetProperty("DeviceId").GetValue(d)).ToList();
        Ok("a profile with a null device list does not stop the prune (" + (threw ?? "ok") + ")", threw == null);
        Ok("keeps a record a profile names with stray spaces around the id", padIds.Contains("padded-tv"));
        Ok("and still drops the unrelated one", !padIds.Contains("unrelated-2"));
    }

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
        var ids = new[] { "fb030d0001", "392e4e0002", "bdf0880003", "51efea0004" }
            .Select(s => Sha1(s)).ToArray();
        for (var i = 0; i < ids.Length; i++)
        {
            See(h.Cfg, ids[i], "Family Room TV", TV, h.Master, t0.AddDays(i * 7));
            See(h.Cfg, ids[i], "Family Room TV", TV, h.Master, t0.AddDays(i * 7 + 2));
        }
        Ok("two days after the newest id appeared it is still its own row: the old one may be in use ("
           + h.List.Count + ")", h.List.Count >= 2);
        // A week on, with nothing seen on the old ids since: the next sighting tidies.
        See(h.Cfg, ids[3], "Family Room TV", TV, h.Master, t0.AddDays(30));
        Ok("four ids that followed one another on one television are one row (" + h.List.Count + ")",
           h.List.Count == 1);
        Ok("the row kept is the newest id", h.List.Count == 1 && Ids(h.List)[0] == ids[3]);
        // R2-02-01: a guess that two ids are one device leaves no alias, or allowing the
        // newer row later would also allow whatever device the old id belonged to.
        Ok("a merge by time keeps no alias for an old id", !ids.Take(3).Any(id => Answers(h.List, id)));
    }

    // 1b. Owner decision, 2026-11-12: a device on an allow list is never joined by the time
    //     rule. An unproven merge of two different devices hands the allowed one's
    //     permission to the other: a kid's iPad renamed "Family Room TV", the real TV unused
    //     for a week. Only proof (one id derived from the other) may merge it.
    {
        var h = Household(1);
        var allow = (IList)mappingType.GetProperty("AllowedDeviceIds")
            .GetValue(((IList)cfgType.GetProperty("Mappings").GetValue(h.Cfg))[0]);
        See(h.Cfg, "real-tv-0001", "Family Room TV", TV, h.Master, t0);
        allow.Add("real-tv-0001");
        See(h.Cfg, "ipad-newcomer-2", "Family Room TV", TV, h.Master, t0.AddDays(2));
        var merged = Merge(h.Cfg, h.Master, t0.AddDays(10));
        Ok("an allow-listed television and a same-named newcomer after 8 days are two rows (" + h.List.Count + ")",
           h.List.Count == 2 && merged == 0);
        Ok("and the newcomer is not on the allow list", !allow.Cast<string>().Contains("ipad-newcomer-2")
           && allow.Cast<string>().Contains("real-tv-0001"));

        // The same two, with the newcomer the one on the allow list.
        var h2 = Household(1);
        var allow2 = (IList)mappingType.GetProperty("AllowedDeviceIds")
            .GetValue(((IList)cfgType.GetProperty("Mappings").GetValue(h2.Cfg))[0]);
        See(h2.Cfg, "old-tv-0001", "Family Room TV", TV, h2.Master, t0);
        See(h2.Cfg, "new-tv-0002", "Family Room TV", TV, h2.Master, t0.AddDays(2));
        allow2.Add("new-tv-0002");
        Merge(h2.Cfg, h2.Master, t0.AddDays(10));
        Ok("an allow-listed newer row is not folded into either (" + h2.List.Count + ")", h2.List.Count == 2);

        // An alias counts: the list may name an id this row only answers to.
        var h3 = Household(1);
        var allow3 = (IList)mappingType.GetProperty("AllowedDeviceIds")
            .GetValue(((IList)cfgType.GetProperty("Mappings").GetValue(h3.Cfg))[0]);
        See(h3.Cfg, "tv-a-000001", "Family Room TV", TV, h3.Master, t0);
        See(h3.Cfg, "tv-b-000002", "Family Room TV", TV, h3.Master, t0.AddDays(2));
        ((IList)deviceType.GetProperty("Aliases").GetValue(h3.List[0])).Add("tv-a-alias");
        allow3.Add("tv-a-alias");
        Merge(h3.Cfg, h3.Master, t0.AddDays(10));
        Ok("a row the allow list names only by an alias is not joined by time (" + h3.List.Count + ")", h3.List.Count == 2);

        // Unrestricted rows keep the time rule.
        var h4 = Household(1);
        See(h4.Cfg, "plain-old-0001", "Family Room TV", TV, h4.Master, t0);
        See(h4.Cfg, "plain-new-0002", "Family Room TV", TV, h4.Master, t0.AddDays(2));
        Merge(h4.Cfg, h4.Master, t0.AddDays(10));
        Ok("unrestricted rows are still joined by the time rule (" + h4.List.Count + ")", h4.List.Count == 1);
        Ok("and the older id is not an alias, so allowing the survivor later does not allow it (R2-02-01)",
           h4.List.Count == 1 && !Answers(h4.List, "plain-old-0001") && Answers(h4.List, "plain-new-0002"));

        // Proof still merges an allow-listed row, and the allowance moves with it.
        if (forgetScreens != null) forgetScreens.Invoke(null, null);
        var h5 = Household(1);
        var allow5 = (IList)mappingType.GetProperty("AllowedDeviceIds")
            .GetValue(((IList)cfgType.GetProperty("Mappings").GetValue(h5.Cfg))[0]);
        const string bare5 = "5c1d7e90ab34f2d6";
        var accts = new[] { h5.Master, h5.Profiles[0] };
        foreach (var a in accts) See(h5.Cfg, Sha1(bare5 + "+" + a), "Family Room TV", TV, h5.Master, t0.AddHours(Array.IndexOf(accts, a)));
        allow5.Add(Sha1(bare5 + "+" + accts[0]));
        noteScreen.Invoke(null, new object[] { bare5 });
        Merge(h5.Cfg, h5.Master, t0.AddDays(1));
        Ok("ids derived from one another still merge when one is allow-listed (" + h5.List.Count + ")", h5.List.Count == 1);
        Ok("and the allowance is on the surviving id",
           allow5.Count > 0 && allow5.Cast<string>().All(id => Answers(h5.List, id)));
        if (forgetScreens != null) forgetScreens.Invoke(null, null);
    }

    // 1c. R2-02-02: a household at its row cap gives up its oldest unlisted row for a new
    //     device instead of refusing it, and an allow-listed row is never the one to go.
    {
        var cap = (int)registry.GetField("MaxDevicesPerHousehold", Any).GetRawConstantValue();
        var h = Household(1);
        var allow = (IList)mappingType.GetProperty("AllowedDeviceIds")
            .GetValue(((IList)cfgType.GetProperty("Mappings").GetValue(h.Cfg))[0]);
        for (var i = 0; i < cap; i++) See(h.Cfg, "minted-" + i.ToString("D4") + "-xxxxxxxx", "Box " + i, TV, h.Master, t0.AddMinutes(i));
        allow.Add("minted-0000-xxxxxxxx");
        Ok("the household is at its cap (" + h.List.Count + ")", h.List.Count == cap);
        See(h.Cfg, "real-device-yyyyyyyy", "Real TV", TV, h.Master, t0.AddDays(1));
        Ok("a new device is still recorded at the cap (" + h.List.Count + ")",
           Ids(h.List).Contains("real-device-yyyyyyyy") && h.List.Count == cap);
        Ok("the oldest row on a device list stays", Ids(h.List).Contains("minted-0000-xxxxxxxx"));
        Ok("the oldest row nothing depends on went", !Ids(h.List).Contains("minted-0001-xxxxxxxx"));
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

    // 3b. The set's own id folded in as an alias is still what a sign-in by name is traced to.
    {
        if (forgetScreens != null) forgetScreens.Invoke(null, null);
        var h = Household(1);
        const string bare = "abcd1234abcd1234";
        See(h.Cfg, bare, "Family Room TV", TV, h.Master, t0);
        See(h.Cfg, Sha1(bare + "+" + h.Master), "Family Room TV", TV, h.Master, t0.AddMinutes(1));
        Merge(h.Cfg, h.Master, t0.AddMinutes(2));
        var resolve = registry.GetMethod("ResolveSignInDevice", Any);
        var traced = (string)resolve.Invoke(null, new object[] { h.Cfg, Sha1(bare + "+Kid 0"), "Kid 0" });
        Ok("a sign-in by name is traced to the set's own id after it became an alias (" + traced + ")",
           h.List.Count == 1 && traced == bare);
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
Console.WriteLine("── A service that signs in as a user is not a device ────────────");
{
    var registry = asm.GetType("Jellyfin.Profiles.Auth.DeviceRegistry", true);
    var sight = registry.GetMethod("RecordSighting", Any);
    var notePlayer = registry.GetMethod("NotePlayer", Any);
    var reports = registry.GetMethod("ReportsPlayback", Any);
    var scope = baseCtl.GetMethod("ScopeDevicesToHousehold", Any);
    var canPlayProp = deviceType.GetProperty("CanPlay");
    var master = Guid.NewGuid();
    var t0 = new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    Ok("a device records whether it can play, and the registry can be told", canPlayProp != null && notePlayer != null && reports != null);
    Ok("the device list is scoped by a rule that can see it", scope != null);

    if (canPlayProp != null && notePlayer != null && reports != null && scope != null)
    {
        var cfg = Activator.CreateInstance(cfgType);
        var known = (IList)cfgType.GetProperty("KnownDevices").GetValue(cfg);
        void See(string id, string name, string client)
            => sight.Invoke(null, new object[] { cfg, id, name, client, master, t0, null, true });
        bool Note(string id, bool plays) => (bool)notePlayer.Invoke(null, new object[] { cfg, id, plays });
        bool? CanPlay(string id)
            => (bool?)canPlayProp.GetValue(known.Cast<object>().First(d => (string)deviceType.GetProperty("DeviceId").GetValue(d) == id));
        List<string> Listed(params string[] whitelisted)
        {
            var set = new HashSet<string>(whitelisted, StringComparer.OrdinalIgnoreCase);
            var rows = (IEnumerable)scope.Invoke(null, new object[] { known, master, set });
            return rows.Cast<object>().Select(d => (string)deviceType.GetProperty("DeviceId").GetValue(d)).ToList();
        }
        bool Reports(int types, bool control) => (bool)reports.Invoke(null, new object[] { types, control });

        // jfa-go: Client and Device "jfa-go" for the service, Device and id "auth" for the
        // connection that signs a user in. Neither ever posts capabilities.
        See("jfa-go", "jfa-go", "jfa-go");
        See("auth", "auth", "jfa-go");
        See("tv-1", "Living Room", "Jellyfin Android TV");
        See("late-1", "Phone", "Streamyfin");
        See("kept-1", "Old script", "jfa-go");

        Ok("a device nothing is known about is listed", Listed().Contains("jfa-go") && CanPlay("jfa-go") == null);

        Note("tv-1", Reports(1, false));
        Note("jfa-go", Reports(0, false));
        Note("auth", Reports(0, false));
        Note("kept-1", Reports(0, false));
        Ok("capabilities with playable media types, or media control, mean a player",
           Reports(1, false) && Reports(0, true) && !Reports(0, false));

        var shown = Listed("kept-1");
        Ok("jfa-go's service connection is off the list", !shown.Contains("jfa-go"));
        Ok("jfa-go's account-page sign-in is off the list", !shown.Contains("auth"));
        Ok("a real player stays on it", shown.Contains("tv-1"));
        Ok("a device nothing is known about yet stays on it", shown.Contains("late-1"));
        Ok("a service row a profile is restricted to stays on it", shown.Contains("kept-1"));
        Ok("and without the whitelist it goes", !Listed().Contains("kept-1"));

        // Capabilities that arrive late, and a true that nothing takes back.
        Note("late-1", false);
        Ok("a player judged too early is off the list", !Listed().Contains("late-1"));
        Ok("then it posts capabilities and is on it again", Note("late-1", true) && Listed().Contains("late-1") && CanPlay("late-1") == true);
        Ok("a later quiet minute does not demote it", !Note("late-1", false) && CanPlay("late-1") == true);
        Ok("noting a device nobody recorded changes nothing", !Note("never-seen", true) && known.Count == 5);
    }

    var listener = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "Auth", "BonfireSessionListener.cs"));
    Ok("the listener judges a session after the grace, hears late capabilities, and counts a playback",
       listener.Contains("JudgeAfterGrace(session)") && listener.Contains("ShowsPlayback(session)")
       && listener.Contains("PlaybackStart += OnPlaybackStart"));
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
