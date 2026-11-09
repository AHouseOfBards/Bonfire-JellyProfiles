using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

// The validator patterns accept exactly their shape and nothing after it. In .NET a
// pattern ending in `$` also matches before a final newline, so "dark\n" would pass.
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

var tfm = AppContext.BaseDirectory.Contains("net10.0") ? "net10.0" : "net9.0";
var asm = Assembly.LoadFrom(Path.Combine(RepoRoot(), "bin", "Release", tfm, "Jellyfin.Profiles.dll"));

Regex Field(string type, string name)
{
    var f = asm.GetType("Jellyfin.Profiles.Configuration." + type, true)
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
    return f == null ? null : (Regex)f.GetValue(null);
}

var hex32 = new string('a', 32);
void Check(string label, Regex r, string good, string tail)
{
    Ok(label + " exists", r != null);
    if (r == null) return;
    Ok(label + " accepts its shape", r.IsMatch(good));
    Ok(label + " refuses a trailing newline", !r.IsMatch(good + "\n"));
    Ok(label + " refuses a trailing carriage return", !r.IsMatch(good + "\r"));
    Ok(label + " refuses trailing text", !r.IsMatch(good + tail));
}

Check("theme id", Field("ThemeValidator", "IdRegex"), "dark-2", "!");
Check("theme colour", Field("ThemeValidator", "HexRegex"), "#aabbcc", "0");
Check("theme name key", Field("ThemeValidator", "KeyRegex"), "theme.dark", "!");
Check("logo file name", Field("ThemeLogo", "FileName"), hex32 + ".png", "x");
Check("font file name", Field("ThemeCatalog", "CustomFontName"), hex32 + ".woff2", "x");

Console.WriteLine();
Console.WriteLine($"{pass} passed, {fails.Count} failed");
return fails.Count == 0 ? 0 : 1;
