using System.Net;
using System.Reflection;
using System.Text;
using Jellyfin.Extensions.Json;
using Jellyfin.Profiles.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// ─────────────────────────────────────────────────────────────────────────────
// Issue #33: "Can't create new profiles."
//
//   {"errors":{"":["The supplied value is invalid."],"request":["The request field is required."]}}
//
// That body is ASP.NET model binding giving up on the request as a whole. EnabledFolders
// was List<Guid>, and Jellyfin's JsonGuidConverter turns a string that is not a GUID into
// a FormatException rather than a JsonException — so instead of an error against
// "$.enabledFolders[0]" the formatter reports one against no field at all, and the action
// receives null. One library the form listed without an id gave its checkbox the value
// "undefined", and ticking it was enough.
//
// Only real binding reproduces this: Jellyfin's own JSON settings, the plugin's own
// request types, a real [ApiController] action, over HTTP. Deserializing by hand would
// throw where MVC returns 400, and would not show the shape the report showed.
//
// Point it at an older build by building that checkout and running this from it.
// ─────────────────────────────────────────────────────────────────────────────

int pass = 0;
var fails = new List<string>();
void Ok(string name, bool cond, string detail = null)
{
    if (cond) { pass++; Console.WriteLine("  PASS  " + name); }
    else { fails.Add(name); Console.WriteLine("  FAIL  " + name + (detail == null ? "" : "  — " + detail)); }
}

var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddControllers()
    .AddApplicationPart(typeof(BindingController).Assembly)
    // Exactly Jellyfin.Server's ApiServiceCollectionExtensions.AddJellyfinApi.
    .AddJsonOptions(options =>
    {
        var jsonOptions = JsonDefaults.PascalCaseOptions;
        options.JsonSerializerOptions.ReadCommentHandling = jsonOptions.ReadCommentHandling;
        options.JsonSerializerOptions.WriteIndented = jsonOptions.WriteIndented;
        options.JsonSerializerOptions.DefaultIgnoreCondition = jsonOptions.DefaultIgnoreCondition;
        options.JsonSerializerOptions.NumberHandling = jsonOptions.NumberHandling;
        options.JsonSerializerOptions.Converters.Clear();
        foreach (var converter in jsonOptions.Converters) options.JsonSerializerOptions.Converters.Add(converter);
        options.JsonSerializerOptions.PropertyNamingPolicy = jsonOptions.PropertyNamingPolicy;
    });

var app = builder.Build();
app.MapControllers();
await app.StartAsync();
var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses.First();
var http = new HttpClient { BaseAddress = new Uri(address) };

// What the create form sends, near enough; only enabledFolders varies.
string Body(string folders) =>
    "{\"profileName\":\"Kids\",\"pin\":\"\",\"avatarColor\":\"#00A4DC\",\"maxParentalRating\":null,"
    + "\"blockedTags\":[],\"allowedTags\":[],\"masterPin\":null,\"lockoutMinutes\":5,"
    + "\"bypassPinOnLocalNetwork\":false,\"allowedDeviceIds\":[],\"enabledFolders\":" + folders + "}";

async Task<(HttpStatusCode Status, string Text)> Post(string route, string body)
{
    var res = await http.PostAsync(route, new StringContent(body, Encoding.UTF8, "application/json"));
    return (res.StatusCode, await res.Content.ReadAsStringAsync());
}

const string Good = "f137a2dd21bbc1b99aa5c0f6bf02a805";

foreach (var route in new[] { "create", "update" })
{
    Console.WriteLine();
    Console.WriteLine($"── POST {route}: what binds ─────────────────────────────────────");

    var baseline = await Post(route, Body("null"));
    Ok("no libraries ticked", baseline.Status == HttpStatusCode.OK, baseline.Text);

    var good = await Post(route, Body($"[\"{Good}\"]"));
    Ok("a library id as the server writes it", good.Status == HttpStatusCode.OK, good.Text);

    var undefinedId = await Post(route, Body($"[\"{Good}\",\"undefined\"]"));
    Ok("a checkbox whose library had no id does not fail the request (the report)",
        undefinedId.Status == HttpStatusCode.OK, undefinedId.Text);

    var empty = await Post(route, Body("[\"\"]"));
    Ok("nor does an empty one", empty.Status == HttpStatusCode.OK, empty.Text);
}

await app.StopAsync();

Console.WriteLine();
Console.WriteLine("── What the controller makes of them ───────────────────────────");

var tfm = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Name;
var baseCtl = typeof(CreateProfileRequest).Assembly.GetType("Jellyfin.Profiles.Controllers.ProfilesBaseController", true);
var parse = baseCtl.GetMethod("ParseFolderIds", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
Ok("library ids are parsed in one place", parse != null);
if (parse != null)
{
    var ids = (List<Guid>)parse.Invoke(null, new object[] { new List<string> { Good, "undefined", "", Good } });
    Ok("the real id is kept, once", ids.Count == 1 && ids[0] == Guid.Parse(Good));
    Ok("and null still means \"inherit\", not \"none\"", parse.Invoke(null, new object[] { null }) == null);
}

static string RepoRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d != null && !File.Exists(Path.Combine(d.FullName, "Jellyfin.Profiles.csproj"))) d = d.Parent;
    return d?.FullName ?? throw new InvalidOperationException("no repository root");
}

var src = File.ReadAllText(Path.Combine(RepoRoot(), "Controllers", "ProfilesController.cs"));
Ok("/libraries no longer lists a library with no id",
    src.Contains(".Where(f => Guid.TryParse(f.ItemId, out var parsed) && parsed != Guid.Empty)"));

Console.WriteLine();
Console.WriteLine("  " + pass + " passed, " + fails.Count + " failed");
return fails.Count == 0 ? 0 : 1;

[ApiController]
[Route("")]
public class BindingController : ControllerBase
{
    [HttpPost("create")]
    public ActionResult Create([FromBody] CreateProfileRequest request) => Ok(request.ProfileName);

    [HttpPost("update")]
    public ActionResult Update([FromBody] UpdateProfileRequest request) => Ok(request.ProfileName);
}
