using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

// The emergency code's two limits (r2-14-01): one address can spend its own five tries,
// and must not be able to spend the server-wide allowance by sending more. A request the
// per-address limit refuses costs the shared ceiling nothing, so the real administrator,
// at another address, still gets an answer after one address floods.

int pass = 0;
var fails = new List<string>();
void Ok(string name, bool cond, string detail = null)
{
    if (cond) { pass++; Console.WriteLine("  PASS  " + name); }
    else { fails.Add(name); Console.WriteLine("  FAIL  " + name + (detail is null ? "" : "  - " + detail)); }
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
const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
var ctrlType = asm.GetType("Jellyfin.Profiles.Controllers.ProfilesController", true);
var pluginType = asm.GetType("Jellyfin.Profiles.Plugin", true);
var rateLimiter = asm.GetType("Jellyfin.Profiles.Controllers.RateLimiter", true);

var tempDir = Path.Combine(Path.GetTempPath(), "bonfire-panic-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
var plugin = Activator.CreateInstance(pluginType, new StubPaths(tempDir), new StubXml());

void ResetLimits()
{
    foreach (var f in rateLimiter.GetFields(Any).Where(f => f.IsStatic && f.FieldType == rateLimiter))
    {
        var limiter = f.GetValue(null);
        var store = rateLimiter.GetFields(Any).FirstOrDefault(x => !x.IsStatic && typeof(IDictionary).IsAssignableFrom(x.FieldType));
        (store?.GetValue(limiter) as IDictionary)?.Clear();
    }
}

int Panic(string ip, string code)
{
    var ctor = ctrlType.GetConstructors().First();
    var args = ctor.GetParameters().Select(p =>
        p.ParameterType.Name.StartsWith("ILogger")
            ? Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(ctrlType))
            : (object)null).ToArray();
    var c = (ControllerBase)ctor.Invoke(args);
    var http = new DefaultHttpContext();
    http.Connection.RemoteIpAddress = IPAddress.Parse(ip);
    c.ControllerContext = new ControllerContext { HttpContext = http };
    var m = ctrlType.GetMethod("Panic");
    var req = Activator.CreateInstance(m.GetParameters()[0].ParameterType);
    req.GetType().GetProperty("Code").SetValue(req, code);
    var result = m.Invoke(c, new[] { req });
    var action = (ActionResult)result.GetType().GetProperty("Result").GetValue(result);
    return (action as IStatusCodeActionResult)?.StatusCode ?? -1;
}

ResetLimits();
var flood = new List<int>();
for (int i = 0; i < 260; i++) flood.Add(Panic("203.0.113.9", "wrong-" + i));
Ok("the flooding address gets 400 for five tries, then 429", flood.Take(5).All(s => s == 400) && flood.Skip(5).All(s => s == 429),
    string.Join(",", flood.Take(8)));
var other = Panic("198.51.100.4", "wrong");
Ok("another address is still answered, not told the server is shut", other == 400, "got " + other);

ResetLimits();
// Many addresses, one try each: this is what the server-wide ceiling is for, and it must still hold.
var spread = new List<int>();
for (int i = 0; i < 230; i++) spread.Add(Panic("10.1." + (i / 250) + "." + (i % 250 + 1), "wrong"));
Ok("the server-wide ceiling still stops 200 addresses' worth", spread.Take(200).All(s => s == 400) && spread.Skip(200).All(s => s == 429),
    spread.Count(s => s == 429) + " refused");

Console.WriteLine(pass + " passed, " + fails.Count + " failed");
return fails.Count == 0 ? 0 : 1;

sealed class StubPaths : MediaBrowser.Common.Configuration.IApplicationPaths
{
    private readonly string _root;
    public StubPaths(string root) { _root = root; }
    public string ProgramDataPath => _root;
    public string WebPath => _root;
    public string ProgramSystemPath => _root;
    public string DataPath => _root;
    public string ImageCachePath => _root;
    public string PluginsPath => _root;
    public string PluginConfigurationsPath => _root;
    public string LogDirectoryPath => _root;
    public string ConfigurationDirectoryPath => _root;
    public string SystemConfigurationFilePath => Path.Combine(_root, "system.xml");
    public string CachePath { get => _root; set { } }
    public string TempDirectory => _root;
    public string VirtualDataPath => _root;
    public string TrickplayPath => _root;
    public string BackupPath => _root;
    public void MakeSanityCheckOrThrow() { }
    public void CreateAndCheckMarker(string path, string markerName, bool recursive = false) { }
}

sealed class StubXml : MediaBrowser.Model.Serialization.IXmlSerializer
{
    public void SerializeToStream(object obj, Stream stream) { }
    public void SerializeToFile(object obj, string file) { }
    public object DeserializeFromFile(Type type, string file) => throw new FileNotFoundException("stub serializer", file);
    public object DeserializeFromStream(Type type, Stream stream) => throw new NotSupportedException();
    public object DeserializeFromBytes(Type type, byte[] buffer) => throw new NotSupportedException();
}
