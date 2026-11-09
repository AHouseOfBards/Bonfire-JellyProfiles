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

Console.WriteLine();
Console.WriteLine($"{pass} passed, {fails.Count} failed");
Environment.Exit(fails.Count == 0 ? 0 : 1);
return 0;
