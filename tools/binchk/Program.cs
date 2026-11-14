// binchk: does every Jellyfin/MediaBrowser member the built plugin references exist in the
// Jellyfin it will run on? (Bonfire's tests/binchk, moved into the kit.)
//
// A plugin ships one DLL built against its lowest supported package. A member that moved in a
// later release doesn't fail at load: it throws MissingMethodException on the first call, in
// front of a user, and a broad catch can hide it for good (IUserManager.Users, gone in 12.0).
// This resolves every member reference in the DLL's metadata against another release's
// assemblies, which is what that first call does.
//
//   binchk <plugin.dll> <folder-of-jellyfin-dlls>...   exit 1 if any reference fails to resolve
//
// Calls made by reflection have no member reference in the metadata, so they aren't checked;
// code that looks a member up by name has to cope with its absence itself.
//
// Only members whose declaring type lives in a Jellyfin/MediaBrowser assembly are judged;
// the .NET and ASP.NET ones are the runtime's own business.

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;

if (args.Length < 2) { Console.Error.WriteLine("usage: binchk <plugin.dll> <dll-folder>..."); return 2; }
var plugin = Path.GetFullPath(args[0]);
// The plugin's own folder last, so a DLL shipped beside it (a vendored library) loads, and Jellyfin's copies win.
var dirs = args.Skip(1).Select(Path.GetFullPath).Append(Path.GetDirectoryName(plugin)!).ToArray();

var alc = new AssemblyLoadContext("binchk");
alc.Resolving += (ctx, name) =>
{
    foreach (var d in dirs)
    {
        var p = Path.Combine(d, name.Name + ".dll");
        if (File.Exists(p)) return ctx.LoadFromAssemblyPath(p);
    }
    return null;
};
var module = alc.LoadFromAssemblyPath(plugin).ManifestModule;

using var fs = File.OpenRead(plugin);
using var pe = new PEReader(fs);
var md = pe.GetMetadataReader();

static bool Ours(string ns) => ns.StartsWith("MediaBrowser", StringComparison.Ordinal) || ns.StartsWith("Jellyfin", StringComparison.Ordinal);

string Describe(MemberReference mr)
{
    string parent = "?";
    if (mr.Parent.Kind == HandleKind.TypeReference)
    {
        var tr = md.GetTypeReference((TypeReferenceHandle)mr.Parent);
        parent = md.GetString(tr.Namespace) + "." + md.GetString(tr.Name);
    }
    else if (mr.Parent.Kind == HandleKind.TypeSpecification) parent = "(generic instance)";
    return parent + "::" + md.GetString(mr.Name);
}

bool Judged(MemberReference mr)
{
    if (mr.Parent.Kind == HandleKind.TypeReference)
    {
        var tr = md.GetTypeReference((TypeReferenceHandle)mr.Parent);
        return Ours(md.GetString(tr.Namespace));
    }
    return mr.Parent.Kind == HandleKind.TypeSpecification;   // judged by what resolution says
}

int ok = 0, missing = 0, unjudged = 0;
var unloadable = new SortedSet<string>(StringComparer.Ordinal);
foreach (var h in md.MemberReferences)
{
    var mr = md.GetMemberReference(h);
    if (!Judged(mr)) { unjudged++; continue; }
    try
    {
        // A generic instance resolves whoever owns it; only Jellyfin's members count (List<int>::.ctor isn't one).
        var resolved = module.ResolveMember(MetadataTokens.GetToken(h));
        if (Ours(resolved?.DeclaringType?.Namespace ?? "")) ok++; else unjudged++;
    }
    catch (Exception ex) when (ex is FileNotFoundException or FileLoadException)
    {
        // An assembly that won't load leaves every member that mentions it unchecked, so it fails the run.
        unloadable.Add(ex.Message);
    }
    catch (Exception ex) when (ex is MissingMemberException or TypeLoadException or MissingMethodException or MissingFieldException)
    {
        // A generic instance of a type we don't own (List<T>) can fail only because the
        // runtime hasn't the right context; count a failure only when the name is Jellyfin's.
        var d = Describe(mr);
        if (d.StartsWith("(generic", StringComparison.Ordinal) && !ex.Message.Contains("MediaBrowser") && !ex.Message.Contains("Jellyfin")) { unjudged++; continue; }
        missing++;
        Console.WriteLine("  MISSING  " + d + "   [" + ex.GetType().Name + ": " + ex.Message + "]");
    }
    catch (Exception) { unjudged++; }
}
foreach (var u in unloadable) Console.WriteLine("  UNLOADABLE  " + u);
Console.WriteLine($"  {ok} resolved, {missing} missing, {unjudged} not judged, {unloadable.Count} assemblies unloadable");

// Every plugin calls at least BasePlugin's constructor. Nothing judged means the wrong DLL, not a pass.
if (ok + missing == 0) { Console.WriteLine("  FAIL  no Jellyfin member references found in " + plugin); return 1; }
return missing == 0 && unloadable.Count == 0 ? 0 : 1;
