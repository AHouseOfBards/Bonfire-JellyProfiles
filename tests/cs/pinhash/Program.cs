using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

// A stored PIN hash is checked before it is trusted. A hash whose parts are too short or
// too costly must be refused as malformed, never compared.
// Usage: dotnet run -c Release   (loads bin/Release/<tfm>/Jellyfin.Profiles.dll)

int pass = 0;
var fails = new List<string>();
void Ok(string name, bool cond)
{
    if (cond) { pass++; Console.WriteLine("  PASS  " + name); }
    else { fails.Add(name); Console.WriteLine("  FAIL  " + name); }
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
var hasher = asm.GetType("Jellyfin.Profiles.Auth.PinHasher", true);
string Verify(string pin, string stored) => hasher.GetMethod("Verify").Invoke(null, new object[] { pin, stored }).ToString();
string Hash(string pin) => (string)hasher.GetMethod("Hash").Invoke(null, new object[] { pin });

string B(int n) => Convert.ToBase64String(new byte[n]);
var good = Hash("1234");
Ok("a real hash matches", Verify("1234", good) == "Match");
Ok("a real hash rejects a wrong PIN", Verify("9999", good) == "NoMatch");
Ok("an empty hash part is malformed", Verify("1234", "pbkdf2.sha256$1000$" + B(16) + "$") == "MalformedHash");
Ok("an empty salt part is malformed", Verify("1234", "pbkdf2.sha256$1000$$" + B(32)) == "MalformedHash");
Ok("a short hash part is malformed", Verify("1234", "pbkdf2.sha256$1000$" + B(16) + "$" + B(8)) == "MalformedHash");
Ok("a short salt part is malformed", Verify("1234", "pbkdf2.sha256$1000$" + B(4) + "$" + B(32)) == "MalformedHash");
// Unfixed code would grind on this for minutes, so it runs on a thread that is abandoned
// after a few seconds; the process exits explicitly below.
var slow = System.Threading.Tasks.Task.Run(() => Verify("1234", "pbkdf2.sha256$2000000000$" + B(16) + "$" + B(32)));
Ok("an absurd iteration count is refused at once", slow.Wait(5000) && slow.Result == "MalformedHash");
Ok("zero iterations is malformed", Verify("1234", "pbkdf2.sha256$0$" + B(16) + "$" + B(32)) == "MalformedHash");

// ── Added by the test audit: each of these was a break that stayed green ──────────────
string Sha(string pin) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
    System.Text.Encoding.UTF8.GetBytes(pin))).ToLowerInvariant();
bool IsLegacy(string h) => (bool)hasher.GetMethod("IsLegacy").Invoke(null, new object[] { h });
string Derive(string pin, int rounds, System.Security.Cryptography.HashAlgorithmName alg) =>
    Convert.ToBase64String(System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
        System.Text.Encoding.UTF8.GetBytes(pin), new byte[16], rounds, alg, 32));

// Legacy (pre-PBKDF2) hashes are still accepted, so they are still a way in: the compare
// has to be a compare. Dropping the stored value from it, or answering Match for every
// mismatch, would open every profile that had not been re-hashed yet.
var legacy = Sha("1234");
Ok("a legacy hash matches its own PIN", Verify("1234", legacy) == "Match");
Ok("a legacy hash rejects a wrong PIN", Verify("9999", legacy) == "NoMatch");
Ok("a legacy hash is compared with the stored value, not with itself", Verify("1234", Sha("5678")) == "NoMatch");
Ok("an empty PIN does not match the legacy hash of the empty string", Verify("", Sha("")) == "NoMatch");
Ok("a PIN against an empty stored hash is NoMatch, not an exception", Verify("1234", "") == "NoMatch");
Ok("and against a missing one", hasher.GetMethod("Verify").Invoke(null, new object[] { "1234", null }).ToString() == "NoMatch");
Ok("a legacy hash is recognised as legacy", IsLegacy(legacy));
Ok("a PBKDF2 hash is not", !IsLegacy(good));
Ok("an empty hash is not", !IsLegacy("") && !IsLegacy(null));

// A stored value that is not the format is malformed, never a mismatch and never a match.
Ok("a hash with an extra part is malformed", Verify("1234", good + "$extra") == "MalformedHash");
Ok("a hash with a missing part is malformed", Verify("1234", "pbkdf2.sha256$1000$" + B(16)) == "MalformedHash");
Ok("a hash that is not base64 is malformed, not a plain mismatch",
   Verify("1234", "pbkdf2.sha256$1000$!!!notbase64!!!$" + B(32)) == "MalformedHash");

// What Hash writes, and what Verify reads back from a hash it did not write.
var h5678 = Hash("5678");
Ok("a hash is made from the PIN it was given", Verify("5678", h5678) == "Match");
Ok("and does not open another", Verify("1234", h5678) == "NoMatch");
Ok("two hashes of one PIN differ, because the salt is random", Hash("1234") != Hash("1234"));
Ok("hashing nothing writes nothing", Hash("") == "" && Hash(null) == "");
var rounds = int.Parse(good.Split('$')[1]);
Ok("a new hash costs enough to slow a brute force of four digits (" + rounds + " rounds)", rounds >= 100000);
var sha256 = System.Security.Cryptography.HashAlgorithmName.SHA256;
Ok("a hash from outside is read with the round count and salt it states",
   Verify("1234", "pbkdf2.sha256$1000$" + B(16) + "$" + Derive("1234", 1000, sha256)) == "Match");
Ok("not with a different round count",
   Verify("1234", "pbkdf2.sha256$2000$" + B(16) + "$" + Derive("1234", 1000, sha256)) == "NoMatch");
Ok("nor a different digest",
   Verify("1234", "pbkdf2.sha256$1000$" + B(16) + "$" + Derive("1234", 1000, System.Security.Cryptography.HashAlgorithmName.SHA1)) == "NoMatch");

Console.WriteLine();
Console.WriteLine($"{pass} passed, {fails.Count} failed");
Environment.Exit(fails.Count == 0 ? 0 : 1);
return 0;
