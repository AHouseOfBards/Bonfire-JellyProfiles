// Reads what a packaged Jellyfin.Profiles.dll carries, for release.yml: the version it reports
// and its embedded scripts and stylesheet, written out so inject.test.js can run the script
// users will get (1.5.2 shipped one that threw on startup past a syntax check).
//
//   dotnet run --project tests/shipped -- <Jellyfin.Profiles.dll> <out-folder>
//
// Prints "info <InformationalVersion>", then one "res <name>" per file written.
using System;
using System.IO;
using System.Linq;
using System.Reflection;

if (args.Length != 2) { Console.Error.WriteLine("usage: shipped <plugin.dll> <out-folder>"); return 2; }
var asm = Assembly.LoadFile(Path.GetFullPath(args[0]));
Console.WriteLine("info " + asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
Directory.CreateDirectory(args[1]);
var names = asm.GetManifestResourceNames().Where(n => n.EndsWith(".js") || n.EndsWith(".css")).ToList();
foreach (var n in names)
{
    using var s = asm.GetManifestResourceStream(n)!;
    using var f = File.Create(Path.Combine(args[1], n));
    s.CopyTo(f);
    Console.WriteLine("res " + n);
}
return names.Count > 0 ? 0 : 1;
