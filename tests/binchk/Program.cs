// binchk: does every Jellyfin/MediaBrowser member the built plugin references exist in the
// Jellyfin it will run on?
//
// The csproj compiles against 10.11.5 for both frameworks, so a green build says nothing about
// 12.0, or about a 10.11 patch that moved something (Jellyfin changed RenameUser in one). A
// missing member does not fail at load: it throws MissingMethodException on the first call, in
// front of a user. This asks the runtime to resolve every member reference in the plugin's
// metadata against the other server's assemblies, which is exactly what that first call does.
//
//   binchk <plugin.dll> <folder-of-jellyfin-dlls>...   exit 1 if any reference fails to resolve
//
// Calls made by reflection (RenameUserCompat, ChangePasswordCompat) have no member reference
// in the metadata, so they are not checked here and cannot fail it: they are deliberate, they
// look the member up by name and cope with its absence themselves.
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
var dirs = args.Skip(1).Select(Path.GetFullPath).ToArray();

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
foreach (var h in md.MemberReferences)
{
    var mr = md.GetMemberReference(h);
    if (!Judged(mr)) { unjudged++; continue; }
    try { module.ResolveMember(MetadataTokens.GetToken(h)); ok++; }
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
Console.WriteLine($"  {ok} resolved, {missing} missing, {unjudged} not judged");
return missing == 0 ? 0 : 1;
