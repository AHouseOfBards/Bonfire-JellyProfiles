using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

// ─────────────────────────────────────────────────────────────────────────────
// Issue #27: switching into another person's MAIN account through a Bonfire link
// cleared that account's library access, permanently, on every switch.
//
// Bonfire is the route, not the fault. The grouping code correctly linked the two
// accounts and correctly authorised the switch. The damage was done afterwards, by
// the block that makes a sub-profile inherit its master's policy — it was gated
// only on "the target is not the caller's own master", which is true for a
// cross-account switch, and with no mapping the code took the target to be its own
// master and inherited the account onto itself.
//
// Two halves here, because the truth table alone would be a restatement of the
// guard:
//
//   1. the guard's answers, enumerated;
//   2. a replay of the legacy folder computation, to show what it produced for a
//      main account.
//
// Be clear about which of those bisects. The truth table cannot: against 1.5.7 the
// guard simply does not exist, and a missing method is a structural failure, not
// evidence. The replay cannot either — it is arithmetic over local values, so it
// passes in both builds; it is a demonstration of the consequence, kept because the
// numbers are the whole argument for the fix, not because it discriminates.
//
// The assertions that genuinely fail on content are the WIRING ones at the end.
// They read Controllers/ProfilesController.cs and require the call site to have
// changed, so they go red against the shipped source for the right reason. They also
// cover the failure mode that would otherwise look fine: a correct helper nobody
// calls, which reads as coverage while changing nothing.
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

var root = RepoRoot();
// Which build of the plugin to load. This harness's own output sits in
// bin/Release/<tfm>/, so its folder name IS the framework the csproj resolved — there is
// no second place to keep in step, and it cannot say net9.0 while the <Reference> that
// compiled it pointed at net10.0. tests/run.sh cs10 runs the whole set against net10.0.
var tfm = new System.IO.DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
    System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)).Name;
var dll = System.IO.Path.Combine(root, "bin", "Release", tfm, "Jellyfin.Profiles.dll");
var asm = Assembly.LoadFrom(dll);
var baseCtl = asm.GetType("Jellyfin.Profiles.Controllers.ProfilesBaseController", true);
var mappingType = asm.GetType("Jellyfin.Profiles.Configuration.ProfileMapping", true);

const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance
                       | BindingFlags.NonPublic | BindingFlags.Public;

Console.WriteLine();
Console.WriteLine("── The guard exists and is reachable ───────────────────────────");

var guard = baseCtl.GetMethod("ShouldInheritMasterPolicy", Any);
Ok("ProfilesBaseController.ShouldInheritMasterPolicy exists", guard != null);
if (guard == null)
{
    Console.WriteLine();
    Console.WriteLine("  This build predates the fix for #27 — the guard is absent, so the truth");
    Console.WriteLine("  table below is unrunnable. That is a missing method, not a bisect. The");
    Console.WriteLine("  wiring assertions at the end are the ones that fail on content here.");
}

object MakeMapping(Guid profileId, Guid masterId)
{
    var m = Activator.CreateInstance(mappingType);
    mappingType.GetProperty("ProfileUserId").SetValue(m, profileId);
    mappingType.GetProperty("MasterUserId").SetValue(m, masterId);
    return m;
}

bool Inherits(object mapping, Guid target, Guid callerMaster)
    => (bool)guard.Invoke(null, new object[] { mapping, target, callerMaster });

var userA = Guid.NewGuid();     // the caller's master account
var userB = Guid.NewGuid();     // somebody else's master account, linked by a Bonfire
var subOfA = Guid.NewGuid();    // a genuine sub-profile of A
var subOfB = Guid.NewGuid();

if (guard != null)
{
    Console.WriteLine();
    Console.WriteLine("── Who inherits, and who owns their own policy ─────────────────");

    // The case in the report. No mapping exists for a main account, so this is the
    // one that has to answer false.
    Ok("a main account reached through a Bonfire does NOT inherit",
        !Inherits(null, userB, userA));

    // A master may carry a self-mapping. It is still a real account that owns its
    // policy; treating it as its own master is precisely the defect.
    Ok("a master's own self-mapping does NOT inherit",
        !Inherits(MakeMapping(userB, userB), userB, userA));

    Ok("switching back to the caller's own master does NOT inherit",
        !Inherits(MakeMapping(userA, userA), userA, userA));

    // And the thing the block was written for still works.
    Ok("a genuine sub-profile of the caller DOES inherit",
        Inherits(MakeMapping(subOfA, userA), subOfA, userA));

    Ok("a sub-profile of a linked Bonfire account DOES inherit",
        Inherits(MakeMapping(subOfB, userB), subOfB, userA));

    Ok("a null mapping never inherits, whoever the caller is",
        !Inherits(null, userB, userB) && !Inherits(null, subOfB, userA));
}

Console.WriteLine();
Console.WriteLine("── What the shipped code computed for a main account ────────────");

// A replay of the legacy branch as it stands in SwitchProfile, with a main account's
// real policy shape. This is arithmetic, not a call into the plugin, so it runs
// identically against any build — which is what makes the next assertion a bisect
// rather than a restatement.
//
// The shape that matters: Jellyfin stores "all libraries" as EnableAllFolders = true
// with an EMPTY EnabledFolders. The list is not a list of everything; it is empty.
bool mainEnableAllFolders = true;
Guid[] mainEnabledFolders = Array.Empty<Guid>();
Guid[] mainBlockedFolders = Array.Empty<Guid>();

var library = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };   // three libraries

// targetMasterUserId falls back to the target itself when mapping is null, so the
// "master" policy below IS the target's own policy.
var masterEnabledFolders = mainEnabledFolders;

List<Guid> legacyEnabled = mainEnableAllFolders
    ? masterEnabledFolders.ToList()
    : mainEnabledFolders.ToList();

if (legacyEnabled.Count == 0 && mainBlockedFolders.Length > 0)
    legacyEnabled = library.Where(f => !mainBlockedFolders.Contains(f)).ToList();

var authority = legacyEnabled;      // then intersected with the master's own list

Ok("the legacy branch resolved a main account's libraries to NOTHING",
    authority.Count == 0);

// Which is then written back as a closed list plus a full block list.
var writtenEnableAll = false;
var writtenEnabled = authority.ToArray();
var writtenBlocked = library.Where(f => !authority.Contains(f)).ToArray();

Ok("it would have written EnableAllFolders = false", writtenEnableAll == false);
Ok("with an empty EnabledFolders", writtenEnabled.Length == 0);
Ok("and every library blocked", writtenBlocked.Length == library.Length);

Console.WriteLine("        -> a user with " + library.Length + " libraries keeps "
    + writtenEnabled.Length + " of them, and is told to ask an administrator.");

// The demotion nobody reported, in the same block.
Console.WriteLine();
Console.WriteLine("── The two the report did not mention ──────────────────────────");
var src = System.IO.File.ReadAllText(
    System.IO.Path.Combine(root, "Controllers", "ProfilesController.cs"));

var blockStart = src.IndexOf("Inherit/synchronize streaming policies", StringComparison.Ordinal);
Ok("the inheritance block is still findable in the source", blockStart > 0);
if (blockStart > 0)
{
    var block = src.Substring(blockStart, Math.Min(4000, src.Length - blockStart));
    // These are correct for a sub-profile and wrong for an account that owns itself.
    Ok("it still demotes the target (correct only for a sub-profile)",
        block.Contains("IsAdministrator = false"));
    Ok("and hides it (same)", block.Contains("IsHidden = true"));
    Console.WriteLine("        -> both are why a switch into an admin's account demoted them;");
    Console.WriteLine("           the guard is what stops the block running there at all.");
}

Console.WriteLine();
Console.WriteLine("── The guard is actually wired into the switch path ─────────────");

// A helper nobody calls reads as coverage while changing nothing.
Ok("SwitchProfile calls ShouldInheritMasterPolicy",
    src.Contains("ShouldInheritMasterPolicy(mapping, targetUser.Id, callerMasterUserId)"));

// And the old condition must not still be guarding the block on its own.
var oldGuard = "if (masterUser != null && targetUser.Id != callerMasterUserId)";
Ok("the old unguarded condition is gone", !src.Contains(oldGuard));

// ─────────────────────────────────────────────────────────────────────────────
// Issue #30: entering a profile reverted library access granted in Dashboard →
// Users. The stored list was applied unconditionally, so a real, persisted grant
// was undone the next time somebody used the feature.
//
// The rule cannot be "the account's list wins when they differ", because that is
// also what a reset policy looks like, and healing those is why the stored copy
// exists. The separator is DIRECTION: a reset can only lose grants, never invent
// one, so additions are adopted and removals are not.
//
// Unlike the #27 half above, these call the real code. Reconcile is pure, so the
// harness can drive every case directly. The wiring assertions at the end are what
// bisect — a correct helper nobody calls reads as coverage while changing nothing.
// ─────────────────────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── Issue #30: a grant in Dashboard -> Users is not a stale value ");

var reconcilerType = asm.GetType("Jellyfin.Profiles.Auth.LibraryAccessReconciler", false);
Ok("there is a reconciler for a profile's library list", reconcilerType != null);

var reconcile = reconcilerType?.GetMethod("Reconcile", BindingFlags.Public | BindingFlags.Static);
Ok("and it can be asked about one profile", reconcile != null);

if (reconcile != null)
{
    var MOVIES = Guid.NewGuid();
    var SHOWS = Guid.NewGuid();
    var MUSIC = Guid.NewGuid();   // the library added later, in Dashboard -> Users

    object Run(List<Guid> stored, List<Guid> account, bool enableAll) =>
        reconcile.Invoke(null, new object[] { stored, account, enableAll });

    List<Guid> Authority(object r) =>
        (List<Guid>)r.GetType().GetProperty("Authority").GetValue(r);
    List<Guid> Granted(object r) =>
        (List<Guid>)r.GetType().GetProperty("Granted").GetValue(r);
    List<Guid> Missing(object r) =>
        (List<Guid>)r.GetType().GetProperty("Missing").GetValue(r);
    bool Changed(object r) =>
        (bool)r.GetType().GetProperty("StoredListChanged").GetValue(r);

    // The reported case: a library granted on the account, absent from the profile's
    // stored copy. Before the fix the profile's copy was returned verbatim and the
    // grant was written away.
    var grant = Run(new List<Guid> { MOVIES, SHOWS }, new List<Guid> { MOVIES, SHOWS, MUSIC }, false);
    Ok("a library granted outside the plugin survives entering the profile",
        Authority(grant).Contains(MUSIC));
    Ok("and the profile keeps everything it already had",
        Authority(grant).Contains(MOVIES) && Authority(grant).Contains(SHOWS));
    Ok("the grant is reported, so there is a trail where there was none",
        Granted(grant).Count == 1 && Granted(grant)[0] == MUSIC);
    Ok("and the profile's stored list is updated rather than left to revert again",
        Changed(grant));

    // The other direction, which must NOT change: this is what a reset looks like,
    // and healing it is the whole reason a stored copy exists.
    var reset = Run(new List<Guid> { MOVIES, SHOWS }, new List<Guid>(), false);
    Ok("a profile whose account lost its libraries is still healed",
        Authority(reset).Count == 2
        && Authority(reset).Contains(MOVIES) && Authority(reset).Contains(SHOWS));
    Ok("nothing is adopted from an emptied account", !Changed(reset));
    Ok("but the mismatch is reported, because it reads as a failed save",
        Missing(reset).Count == 2);

    // Jellyfin stores "all libraries" as EnableAllFolders = true with an EMPTY
    // EnabledFolders — the same shape as a reset, and no per-library grant to read.
    var all = Run(new List<Guid> { MOVIES }, new List<Guid>(), true);
    Ok("an all-libraries account adopts nothing", !Changed(all));
    Ok("and the profile's own list is what gets applied",
        all is not null && Authority(all).Count == 1 && Authority(all)[0] == MOVIES);

    // Agreement is the common case and must be silent in both directions.
    var same = Run(new List<Guid> { MOVIES, SHOWS }, new List<Guid> { SHOWS, MOVIES }, false);
    Ok("two lists that agree report no grant", !Changed(same));
    Ok("and report nothing missing", Missing(same).Count == 0);

    // Degenerate inputs. Guid.Empty is what an unparseable folder id becomes
    // elsewhere in the plugin, so adopting it would grant nothing under a real name.
    var empty = Run(new List<Guid> { MOVIES }, new List<Guid> { MOVIES, Guid.Empty }, false);
    Ok("an empty guid is never adopted as a library", !Granted(empty).Contains(Guid.Empty));
    Ok("and does not appear in the applied list", !Authority(empty).Contains(Guid.Empty));

    var dupes = Run(new List<Guid> { MOVIES, MOVIES }, new List<Guid> { MOVIES, MUSIC, MUSIC }, false);
    Ok("a duplicated id is applied once", dupes is not null
        && Authority(dupes).Count(f => f == MOVIES) == 1
        && Authority(dupes).Count(f => f == MUSIC) == 1);

    var nulls = reconcile.Invoke(null, new object[] { null, null, false });
    Ok("null lists on both sides resolve to nothing rather than throwing",
        Authority(nulls).Count == 0 && !Changed(nulls));

    // A first grant onto a profile stored with an empty list. Adopting here is right:
    // an empty stored list plus a populated account list is still an addition.
    var first = Run(new List<Guid>(), new List<Guid> { MOVIES }, false);
    Ok("a profile stored with no libraries still adopts a grant",
        Authority(first).Count == 1 && Changed(first));
}

Console.WriteLine();
Console.WriteLine("── The reconciler is wired into the switch path ─────────────────");

Ok("SwitchProfile calls the reconciler",
    src.Contains("Auth.LibraryAccessReconciler.Reconcile("));

// The stored list must no longer be handed straight through. This is the line the
// issue is about, and its absence is what makes this assertion a bisect.
var oldApply = "authorityFolders = mapping.EnabledFolders;";
Ok("the stored list is no longer applied unconditionally", !src.Contains(oldApply));

// The adopted list has to reach the mapping, or it reverts again on the next entry.
Ok("an adopted grant is persisted to the mapping",
    src.Contains("liveRow.EnabledFolders = merged;"));

// Re-resolved inside the lock: Jellyfin replaces the whole configuration object when
// an administrator saves plugin settings, so the row captured earlier can be orphaned.
Ok("the mapping row is re-resolved inside the config lock",
    src.Contains("var liveRow = Plugin.Instance?.Configuration?.Mappings?"));

// A sub-profile still cannot exceed its master, whatever was adopted.
Ok("the result is still intersected with the master's accessible folders",
    src.Contains("authorityFolders = authorityFolders.Where(id => masterAccessible.Contains(id)).ToList();"));

Console.WriteLine();
Console.WriteLine("  " + pass + " passed, " + fails.Count + " failed");
if (fails.Count > 0)
{
    Console.WriteLine();
    foreach (var f in fails) Console.WriteLine("   FAILED: " + f);
    Environment.Exit(1);
}
