using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

// ─────────────────────────────────────────────────────────────────────────────
// R-1119: who the caller is.
//
// An API key from Dashboard -> API Keys carries a user id claim of all zeros, which is not
// a user. GetCurrentUserId returned it anyway, and an empty id elsewhere means "unset" or
// "every household", so a key on a route with no administrator check acted as a very large
// caller.
// ─────────────────────────────────────────────────────────────────────────────

static string RepoRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d != null && !File.Exists(Path.Combine(d.FullName, "Jellyfin.Profiles.csproj")))
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

var tfm = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Name;
var asm = Assembly.LoadFrom(Path.Combine(RepoRoot(), "bin", "Release", tfm, "Jellyfin.Profiles.dll"));
var ctrlType = asm.GetType("Jellyfin.Profiles.Controllers.ProfilesController", true);
var baseType = asm.GetType("Jellyfin.Profiles.Controllers.ProfilesBaseController", true);
var getId = baseType.GetMethod("GetCurrentUserId", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
Ok("GetCurrentUserId is where the harness expects", getId != null);

Guid? IdFor(string claimValue)
{
    var c = (ControllerBase)FormatterServices.GetUninitializedObject(ctrlType);
    baseType.GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(c, NullLogger.Instance);
    var ctx = new DefaultHttpContext();
    ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", claimValue) }, "test"));
    c.ControllerContext = new ControllerContext { HttpContext = ctx };
    return (Guid?)getId.Invoke(c, null);
}

var real = Guid.NewGuid();
Ok("a real user id is returned", IdFor(real.ToString()) == real);
Ok("an all-zero id (an API key) is not a user", IdFor(Guid.Empty.ToString()) == null);
Ok("an unparseable id is not a user", IdFor("not-a-guid") == null);

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
