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
var dll = System.IO.Path.Combine(root, "bin", "Release", "net9.0", "Jellyfin.Profiles.dll");
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

Console.WriteLine();
Console.WriteLine("  " + pass + " passed, " + fails.Count + " failed");
if (fails.Count > 0)
{
    Console.WriteLine();
    foreach (var f in fails) Console.WriteLine("   FAILED: " + f);
    Environment.Exit(1);
}
