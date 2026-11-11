using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

// ─────────────────────────────────────────────────────────────────────────────
// Email (1.7.2), through the real routes, with the SMTP sender swapped for a recorder.
//
// What it holds the plugin to:
//
//  - Off until an administrator saves settings AND a test goes through. Changing anything
//    the connection uses un-verifies it, and a change made while a test is out is not
//    verified by that test.
//  - Port 465 with STARTTLS is refused (SmtpClient cannot speak it and hangs).
//  - PIN reset by email needs email: its switch is refused while email is off, and if
//    email stops working later the feature reports unavailable even with its switch on.
//  - An address is stored only once its code comes back. Codes: wrong, expired (15
//    minutes), five tries, spent on use, forgotten when the send fails.
//  - Only the main account, never a profile or nobody, and with its PIN — except the
//    PIN-reset routes, which exist for an account that has forgotten it.
//  - A reset sets the PIN, lifts the lock a forgotten PIN ends in, and mails a note.
//  - Sending codes is limited per account and per server.
//  - Notices go to the main account, in its language, only when it asked, at most once
//    per 15 minutes per kind, and never while email is off.
//  - The mail's words are never sent to a browser.
//  - The settings survive Jellyfin's XmlSerializer; no code is ever written there.
//
// Against 29b14bb the first assertion fails: there is no email at all.
//
// Usage: dotnet run -c Release   (loads bin/Release/<tfm>/Jellyfin.Profiles.dll)
// ─────────────────────────────────────────────────────────────────────────────

int pass = 0;
var fails = new List<string>();
void Ok(string name, bool cond, string detail = null)
{
    if (cond) { pass++; Console.WriteLine("  PASS  " + name); }
    else
    {
        fails.Add(name + (detail is null ? "" : "  — " + detail));
        Console.WriteLine("  FAIL  " + name + (detail is null ? "" : "  — " + detail));
    }
}
void Section(string s) { Console.WriteLine(); Console.WriteLine("── " + s + " " + new string('─', Math.Max(4, 66 - s.Length))); }

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
var cfgType = asm.GetType("Jellyfin.Profiles.Configuration.PluginConfiguration", true);
var mapType = asm.GetType("Jellyfin.Profiles.Configuration.ProfileMapping", true);
var hasher = asm.GetType("Jellyfin.Profiles.Auth.PinHasher", true);
var mailerType = asm.GetType("Jellyfin.Profiles.Mail.Mailer");
var senderIface = asm.GetType("Jellyfin.Profiles.Mail.IMailSender");
var codesType = asm.GetType("Jellyfin.Profiles.Mail.EmailCodes");
var notesType = asm.GetType("Jellyfin.Profiles.Mail.Notifications");
var featuresType = asm.GetType("Jellyfin.Profiles.Configuration.Features");
var registry = asm.GetType("Jellyfin.Profiles.Auth.DeviceRegistry", true);

Ok("this build has email", mailerType != null && senderIface != null && codesType != null && notesType != null
   && featuresType != null && ctrlType.GetMethod("SetAccountEmail") != null && ctrlType.GetMethod("ResetPinByEmail") != null);
if (mailerType == null || senderIface == null || codesType == null || notesType == null || featuresType == null)
{
    Console.WriteLine();
    Console.WriteLine(pass + " passed, " + fails.Count + " failed");
    return 1;
}

// ── The recorder ─────────────────────────────────────────────────────────────
var sent = new List<Mail>();
Func<Mail, Exception> onSend = null;   // return an exception to fail the send
var proxy = DispatchProxy.Create(senderIface, typeof(Recorder));
((Recorder)proxy).OnSend = m =>
{
    var ex = onSend?.Invoke(m);
    if (ex != null) throw ex;
    lock (sent) sent.Add(m);
};
mailerType.GetField("Sender", Any).SetValue(null, proxy);

var tempDir = Path.Combine(Path.GetTempPath(), "bonfire-email-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
var plugin = Activator.CreateInstance(pluginType, new StubPaths(tempDir), new StubXml());
var config = pluginType.GetProperty("Configuration").GetValue(plugin);
// A server that already has Bonfire; a new install is tested in configbackup.
config.GetType().GetProperty("RequireMainPin")?.SetValue(config, false);
var mappings = (IList)cfgType.GetProperty("Mappings").GetValue(config);
object Email() => cfgType.GetProperty("Email").GetValue(config);
IList AccountEmails() => (IList)cfgType.GetProperty("AccountEmails").GetValue(config);
object AccountRow(Guid id) => AccountEmails().Cast<object>().FirstOrDefault(a => (Guid)a.GetType().GetProperty("UserId").GetValue(a) == id);
string StoredAddress(Guid id) => (string)AccountRow(id)?.GetType().GetProperty("Address").GetValue(AccountRow(id));

void Map(Guid profile, Guid master, string name, string pin = null)
{
    var m = Activator.CreateInstance(mapType);
    mapType.GetProperty("ProfileUserId").SetValue(m, profile);
    mapType.GetProperty("MasterUserId").SetValue(m, master);
    mapType.GetProperty("ProfileName").SetValue(m, name);
    if (pin != null) mapType.GetProperty("PinHash").SetValue(m, hasher.GetMethod("Hash").Invoke(null, new object[] { pin }));
    mappings.Add(m);
}
object Mapping(Guid id) => mappings.Cast<object>().First(m => (Guid)mapType.GetProperty("ProfileUserId").GetValue(m) == id);
bool PinIs(Guid id, string pin) =>
    hasher.GetMethod("Verify").Invoke(null, new object[] { pin, mapType.GetProperty("PinHash").GetValue(Mapping(id)) }).ToString() != "NoMatch";

var world = new World();
var users = World.Create(world);

(int status, object value) Call(string method, Guid caller, Dictionary<string, object> body = null, string pinHeader = null)
{
    var route = ctrlType.GetMethod(method);
    if (route == null) return (-404, "no route " + method);

    var ctor = ctrlType.GetConstructors().First();
    var args = ctor.GetParameters().Select(p =>
        p.ParameterType == typeof(IUserManager) ? users
        : p.ParameterType.Name.StartsWith("ILogger") ? Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(ctrlType))
        : (object)null).ToArray();
    var c = (ControllerBase)ctor.Invoke(args);
    var http = new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", caller.ToString()) }, "test")),
    };
    if (pinHeader != null) http.Request.Headers["X-Bonfire-Master-Pin"] = pinHeader;
    c.ControllerContext = new ControllerContext { HttpContext = http };

    var invokeArgs = route.GetParameters().Select(p =>
    {
        if (p.ParameterType.IsValueType) return Activator.CreateInstance(p.ParameterType);
        if (p.ParameterType == typeof(string)) return null;
        var req = Activator.CreateInstance(p.ParameterType);
        foreach (var kv in body ?? new Dictionary<string, object>())
        {
            var prop = p.ParameterType.GetProperty(kv.Key);
            if (prop == null) throw new Exception(p.ParameterType.Name + " has no " + kv.Key);
            prop.SetValue(req, kv.Value);
        }
        return req;
    }).ToArray();

    object result;
    try
    {
        result = route.Invoke(c, invokeArgs);
        if (result is Task t) { t.GetAwaiter().GetResult(); result = t.GetType().GetProperty("Result")?.GetValue(t); }
    }
    catch (TargetInvocationException ex) { return (-500, ex.InnerException?.GetType().Name + ": " + ex.InnerException?.Message); }
    catch (Exception ex) { return (-500, ex.GetType().Name + ": " + ex.Message); }

    if (result is IConvertToActionResult conv) result = conv.Convert();
    return result switch
    {
        ObjectResult o => (o.StatusCode ?? 200, o.Value),
        StatusCodeResult s => (s.StatusCode, null),
        _ => (-1, result?.GetType().Name),
    };
}

object Get(object o, string prop) => o?.GetType().GetProperty(prop)?.GetValue(o);
bool Bool(object o, string prop) => Get(o, prop) is bool b && b;
Dictionary<string, object> B(params (string k, object v)[] kv) => kv.ToDictionary(x => x.k, x => x.v);

bool Available(string feature) => (bool)featuresType.GetMethod("Available").Invoke(null, new[] { feature, config });
string MissingFor(string feature) => (string)featuresType.GetMethod("MissingFor").Invoke(null, new[] { feature, config });
bool InfoEmail() => Bool(Get(Call("GetInfo", Guid.Empty).value, "Features"), "Email");
bool InfoPinReset() => Bool(Get(Call("GetInfo", Guid.Empty).value, "Features"), "PinResetByEmail");
bool MePinReset(Guid who) => Bool(Call("GetMe", who).value, "PinResetByEmail");

void Drain()
{
    var q = (System.Collections.Concurrent.ConcurrentQueue<Task>)notesType.GetField("Pending", Any).GetValue(null);
    Task.WaitAll(q.ToArray(), 10000);
}
int Sent() { Drain(); lock (sent) return sent.Count; }
Mail Last() { Drain(); lock (sent) return sent.LastOrDefault(); }
string CodeIn(Mail m) => m == null ? null : Regex.Match(m.Body, @"\b\d{6}\b").Value;

var setNow = codesType.GetField("Now", Any);
void ClockAhead(TimeSpan by) { var at = DateTime.UtcNow + by; setNow.SetValue(null, (Func<DateTime>)(() => at)); }
void ClockReal() => setNow.SetValue(null, (Func<DateTime>)(() => DateTime.UtcNow));

// The harness sends more codes than an hour allows. Each section that needs sends to go
// through starts from clear limits; the Limits section is what holds the limits themselves.
var perAccount = ctrlType.GetField("EmailPerAccount", Any)?.GetValue(null);
var perServer = ctrlType.GetField("EmailPerServer", Any)?.GetValue(null);
void FreshLimits(params Guid[] ids)
{
    var reset = perAccount.GetType().GetMethod("Reset");
    foreach (var id in ids)
        foreach (var purpose in new[] { "confirm", "reset" })
            reset.Invoke(perAccount, new object[] { purpose + ":" + id.ToString("N") });
    reset.Invoke(perServer, new object[] { "server" });
    var resetServer = ctrlType.GetField("EmailResetPerServer", Any)?.GetValue(null);   // R-1063
    if (resetServer != null) reset.Invoke(resetServer, new object[] { "server" });
}

var ADMIN = Guid.NewGuid(); world.Add(ADMIN, "Admin", admin: true);
var MAIN = Guid.NewGuid(); world.Add(MAIN, "Parent");
var KID = Guid.NewGuid(); world.Add(KID, "Parent_kid");
var OTHER = Guid.NewGuid(); world.Add(OTHER, "Other");
var KID2 = Guid.NewGuid(); world.Add(KID2, "Other_kid");
Map(MAIN, MAIN, "Parent", pin: "1234");
Map(KID, MAIN, "Kid");
Map(OTHER, OTHER, "Other");
Map(KID2, OTHER, "Littlest");

(int status, object value) Admin(string method, Dictionary<string, object> body) => Call(method, ADMIN, body);

// ── Off by default ───────────────────────────────────────────────────────────
Section("Off by default");

Ok("email is not available", !Available("email") && !InfoEmail());
Ok("nor is PIN reset", !Available("pinResetByEmail") && !InfoPinReset() && !MePinReset(MAIN));
var r = Call("GetAccountEmail", OTHER);
Ok("an account sees email as unavailable", r.status == 200 && !Bool(r.value, "Available"), r.status + " " + r.value);
r = Call("SetAccountEmail", OTHER, B(("Address", "other@example.com")));
Ok("and cannot add an address", r.status == 400 && Sent() == 0, r.status + " " + r.value);

// ── The administrator's settings ─────────────────────────────────────────────
Section("Outgoing mail settings");

r = Call("UpdateEmailSettings", MAIN, B(("Host", "smtp.example.com")));
Ok("a non-administrator cannot change them", r.status == 401 || r.status == 403, r.status.ToString());
r = Call("GetEmailSettings", MAIN);
Ok("or read them", r.status == 401 || r.status == 403, r.status.ToString());

r = Admin("UpdateEmailSettings", B(("Host", "smtp.example.com"), ("Port", (int?)465), ("Security", "starttls")));
Ok("465 with STARTTLS is refused", r.status == 400 && (string)Get(Email(), "Host") == "", r.status + " " + r.value);
r = Admin("UpdateEmailSettings", B(("Port", (int?)0)));
Ok("a port of 0 is refused", r.status == 400);
r = Admin("UpdateEmailSettings", B(("Security", "ssl")));
Ok("an unknown security mode is refused", r.status == 400);
r = Admin("UpdateEmailSettings", B(("FromAddress", "not an address")));
Ok("a bad From address is refused", r.status == 400);
// The name mail is sent under is not a setting: nothing a request or a file says can put a
// header or a line break there. (The sender itself needs a mail server to run; this is
// the constant it uses.)
Ok("there is no From name setting to put a line break in", asm.GetType("Jellyfin.Profiles.Configuration.EmailSettings", true).GetProperty("FromName") == null);
Ok("mail is sent as Bonfire", (string)asm.GetType("Jellyfin.Profiles.Mail.Mailer", true).GetField("FromDisplayName", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).GetRawConstantValue() == "Bonfire");

r = Admin("UpdateEmailSettings", B(("Host", " smtp.example.com "), ("Port", (int?)587), ("Security", "STARTTLS"),
    ("Username", "bonfire@example.com"), ("Password", "s3cret"), ("FromAddress", "bonfire@example.com")));
Ok("good settings are saved", r.status == 200 && (string)Get(Email(), "Host") == "smtp.example.com"
   && (string)Get(Email(), "Security") == "starttls", r.status + " " + r.value);
Ok("configured, not yet verified", Bool(r.value, "Configured") && !Bool(r.value, "Verified") && !Available("email"));
Ok("the password is never sent back", r.value?.GetType().GetProperty("Password") == null && Bool(r.value, "HasPassword"));

r = Admin("UpdateAdminSettings", B(("EnablePinResetByEmail", (bool?)true)));
Ok("PIN reset cannot be turned on before email works", r.status == 400 && !(bool)cfgType.GetProperty("EnablePinResetByEmail").GetValue(config),
   r.status + " " + r.value);

onSend = _ => new System.Net.Mail.SmtpException((System.Net.Mail.SmtpStatusCode)535, "5.7.8 Username and Password not accepted. Learn more at https://support.example.com/xyz");
r = Admin("TestEmailSettings", B(("To", "admin@example.com")));
Ok("a failed test says why, in plain words", r.status == 400 && (r.value as string ?? "").Contains("username or password"), r.status + " " + r.value);
Ok("and not the server's raw reply", !(r.value as string ?? "").Contains("5.7.8"));
Ok("and email stays off", !Available("email"));
onSend = null;

r = Admin("TestEmailSettings", B(("To", "admin@example.com")));
var test = Last();
Ok("a test that goes through turns email on", r.status == 200 && Bool(r.value, "Verified") && Available("email") && InfoEmail(),
   r.status + " " + r.value);
Ok("the test went to the address given, from the settings", test?.To == "admin@example.com" && test?.Subject == "Bonfire test email"
   && test?.Host == "smtp.example.com");

r = Admin("UpdateEmailSettings", B(("Password", "n3w")));
Ok("changing the password un-verifies it", r.status == 200 && !Bool(r.value, "Verified") && !Available("email"));
r = Admin("TestEmailSettings", B(("To", "admin@example.com")));
Ok("and a new test verifies it again", Available("email"));

r = Admin("UpdateEmailSettings", B(("Username", "bonfire@example.com")));
Ok("saving the same values keeps it verified", r.status == 200 && Available("email"));

// A change that lands while the test is out. The test went through with the old host,
// which says nothing about the new one.
onSend = m => { Email().GetType().GetProperty("Host").SetValue(Email(), "smtp.changed.example.com"); return null; };
r = Admin("TestEmailSettings", B(("To", "admin@example.com")));
onSend = null;
Ok("a change made during a test is not verified by it", r.status == 200 && !Available("email"), r.status + " verified=" + Available("email"));
Admin("UpdateEmailSettings", B(("Host", "smtp.example.com")));
Admin("TestEmailSettings", B(("To", "admin@example.com")));
Ok("(back to working)", Available("email"));

// ── What depends on what ─────────────────────────────────────────────────────
Section("PIN reset needs email");

r = Admin("UpdateAdminSettings", B(("EnablePinResetByEmail", (bool?)true)));
Ok("with email working, PIN reset can be turned on", r.status == 200 && Available("pinResetByEmail") && InfoPinReset(),
   r.status + " " + r.value);
Admin("UpdateEmailSettings", B(("Host", "smtp.broken.example.com")));
Ok("when email stops working, PIN reset is unavailable with its switch still on",
   (bool)cfgType.GetProperty("EnablePinResetByEmail").GetValue(config) && !Available("pinResetByEmail") && !InfoPinReset());
Ok("and what it is missing is named", MissingFor("pinResetByEmail") == "email");
// The dashboard sends every field on every save. A switch already on must not make an
// unrelated save fail just because email broke since.
r = Admin("UpdateAdminSettings", B(("EnablePinResetByEmail", (bool?)true), ("MaxProfilesPerUser", (int?)6)));
Ok("an unrelated save still goes through with the switch on", r.status == 200
   && (int)cfgType.GetProperty("MaxProfilesPerUser").GetValue(config) == 6, r.status + " " + r.value);
Ok("(and PIN reset is still unavailable)", !Available("pinResetByEmail"));
Admin("UpdateEmailSettings", B(("Host", "smtp.example.com")));
Admin("TestEmailSettings", B(("To", "admin@example.com")));
Ok("a feature with nothing missing names nothing", MissingFor("pinResetByEmail") == null && Available("pinResetByEmail"));

// ── A main account's address ─────────────────────────────────────────────────
Section("Adding an address");

r = Call("GetAccountEmail", KID);
Ok("a profile cannot see the main account's address", r.status == 401, r.status.ToString());
r = Call("SetAccountEmail", KID, B(("Address", "kid@example.com")));
Ok("or set one", r.status == 401);
r = Call("GetAccountEmail", Guid.Empty);
Ok("nor can a caller with no user", r.status == 401);
r = Call("GetAccountEmail", MAIN);
Ok("the main account needs its PIN", r.status == 400, r.status + " " + r.value);
r = Call("GetAccountEmail", MAIN, pinHeader: "9999");
Ok("the right one", r.status == 400 || r.status == 429, r.status + " " + r.value);
r = Call("GetAccountEmail", MAIN, pinHeader: "1234");
Ok("with it, the address is empty", r.status == 200 && (string)Get(r.value, "Address") == "" && Bool(r.value, "Available"));

var before = Sent();
r = Call("SetAccountEmail", MAIN, B(("Address", "parent@example"), ("MasterPin", "1234")));
Ok("an address with no domain is refused", r.status == 400 && Sent() == before);
r = Call("SetAccountEmail", MAIN, B(("Address", "parent@example.com, x@example.com"), ("MasterPin", "1234")));
Ok("so is a list", r.status == 400 && Sent() == before);
r = Call("SetAccountEmail", MAIN, B(("Address", "parent@example.com"), ("MasterPin", "1111")));
Ok("so is a wrong PIN", r.status == 400 && Sent() == before);

r = Call("SetAccountEmail", MAIN, B(("Address", " parent@example.com "), ("Language", "fr"), ("MasterPin", "1234")));
var confirm = Last();
Ok("a code is sent to the new address", r.status == 200 && Sent() == before + 1 && confirm.To == "parent@example.com", r.status + " " + r.value);
Ok("in the language asked for", confirm?.Subject == "Confirmez votre e-mail Bonfire", confirm?.Subject);
Ok("with a six-digit code and the account's name", CodeIn(confirm)?.Length == 6 && confirm.Body.Contains("Parent"));
Ok("no placeholder is left in it", confirm != null && !Regex.IsMatch(confirm.Subject + confirm.Body, @"\{[a-z]+\}"));
Ok("nothing is stored before the code comes back", StoredAddress(MAIN) == null);
Ok("the address waits as pending", (string)Get(r.value, "PendingAddress") == "parent@example.com");

var code = CodeIn(confirm);
var wrong = code == "000000" ? "111111" : "000000";
r = Call("ConfirmAccountEmail", MAIN, B(("Code", wrong), ("MasterPin", "1234")));
Ok("a wrong code is refused", r.status == 400 && StoredAddress(MAIN) == null);
r = Call("ConfirmAccountEmail", KID, B(("Code", code)));
Ok("a profile cannot confirm it", r.status == 401 && StoredAddress(MAIN) == null);
r = Call("ConfirmAccountEmail", MAIN, B(("Code", code), ("Language", "fr"), ("MasterPin", "1234")));
Ok("the right code stores the address", r.status == 200 && StoredAddress(MAIN) == "parent@example.com" && (string)Get(r.value, "Address") == "parent@example.com",
   r.status + " " + r.value);
Ok("with the language for later mail", (string)Get(AccountRow(MAIN), "Language") == "fr");
Ok("and notices on", Bool(AccountRow(MAIN), "NotifyLockout") && Bool(AccountRow(MAIN), "NotifyNewDevice"));
r = Call("ConfirmAccountEmail", MAIN, B(("Code", code), ("MasterPin", "1234")));
Ok("a code works once", r.status == 400);

Section("Codes");

FreshLimits(MAIN);
Call("SetAccountEmail", MAIN, B(("Address", "parent2@example.com"), ("MasterPin", "1234")));
code = CodeIn(Last());
ClockAhead(TimeSpan.FromMinutes(16));
r = Call("ConfirmAccountEmail", MAIN, B(("Code", code), ("MasterPin", "1234")));
ClockReal();
Ok("a code is good for 15 minutes, not 16", r.status == 400 && StoredAddress(MAIN) == "parent@example.com", r.status + " " + r.value);

Call("SetAccountEmail", MAIN, B(("Address", "parent2@example.com"), ("MasterPin", "1234")));
code = CodeIn(Last());
ClockAhead(TimeSpan.FromMinutes(14));
r = Call("ConfirmAccountEmail", MAIN, B(("Code", code.Substring(0, 3) + " " + code.Substring(3)), ("MasterPin", "1234")));
ClockReal();
Ok("at 14 minutes it works, typed with a space", r.status == 200 && StoredAddress(MAIN) == "parent2@example.com", r.status + " " + r.value);

Call("SetAccountEmail", MAIN, B(("Address", "parent@example.com"), ("MasterPin", "1234")));
code = CodeIn(Last());
wrong = code == "000000" ? "111111" : "000000";
for (var i = 0; i < 5; i++) Call("ConfirmAccountEmail", MAIN, B(("Code", wrong), ("MasterPin", "1234")));
r = Call("ConfirmAccountEmail", MAIN, B(("Code", code), ("MasterPin", "1234")));
Ok("five wrong tries spend the code", r.status == 400 && StoredAddress(MAIN) == "parent2@example.com", r.status + " " + r.value);

FreshLimits(MAIN);
before = Sent();
var s1 = Call("SetAccountEmail", MAIN, B(("Address", "parent@example.com"), ("MasterPin", "1234"))).status;
var first = CodeIn(Last());
var s2 = Call("SetAccountEmail", MAIN, B(("Address", "parent@example.com"), ("Language", "fr"), ("MasterPin", "1234"))).status;
code = CodeIn(Last());
r = first == code ? (400, null) : Call("ConfirmAccountEmail", MAIN, B(("Code", first), ("MasterPin", "1234")));
Ok("a new code replaces the one before", s1 == 200 && s2 == 200 && Sent() == before + 2 && r.status == 400, s1 + "," + s2);
r = Call("ConfirmAccountEmail", MAIN, B(("Code", code), ("Language", "fr"), ("MasterPin", "1234")));
Ok("(and the new one works)", r.status == 200 && StoredAddress(MAIN) == "parent@example.com");

before = Sent();
onSend = _ => new System.Net.Mail.SmtpException("Service not available");
r = Call("SetAccountEmail", MAIN, B(("Address", "parent3@example.com"), ("MasterPin", "1234")));
onSend = null;
Ok("a send that fails says so", r.status == 400 && Sent() == before, r.status + " " + r.value);
Ok("in words for the account, not the server's", (r.value as string ?? "").Contains("administrator") && !(r.value as string ?? "").Contains("Service not available"));
r = Call("GetAccountEmail", MAIN, pinHeader: "1234");
Ok("and leaves no code waiting", (string)Get(r.value, "PendingAddress") == "");

r = Call("SetAccountEmailNotifications", MAIN, B(("NotifyNewDevice", (bool?)false), ("MasterPin", "1234")));
Ok("notices can be turned off one at a time", r.status == 200 && !Bool(AccountRow(MAIN), "NotifyNewDevice") && Bool(AccountRow(MAIN), "NotifyLockout"));
r = Call("SetAccountEmailNotifications", KID, B(("NotifyLockout", (bool?)false)));
Ok("but not by a profile", r.status == 401 && Bool(AccountRow(MAIN), "NotifyLockout"));

// OTHER has no PIN, so needs none; it writes in English.
Call("SetAccountEmail", OTHER, B(("Address", "other@example.com")));
var otherMail = Last();
Call("ConfirmAccountEmail", OTHER, B(("Code", CodeIn(otherMail))));
Ok("an account without a PIN adds one too, in English by default",
   StoredAddress(OTHER) == "other@example.com" && otherMail.Subject == "Confirm your Bonfire email");

// ── Notices ──────────────────────────────────────────────────────────────────
Section("Notices");

void Device(string id, string name, Guid owner) =>
    registry.GetMethod("RecordAndSave").Invoke(null, new object[] { id, name, "Jellyfin Web", owner, null });

before = Sent();
Device("dev-living-room", "Living room TV", MAIN);
Ok("no new-device mail when the account turned it off", Sent() == before);
Call("SetAccountEmailNotifications", MAIN, B(("NotifyNewDevice", (bool?)true), ("MasterPin", "1234")));
Device("dev-kitchen", "Kitchen tablet", MAIN);
var note = Last();
Ok("a new device is mailed when it is on", Sent() == before + 1 && note.To == "parent@example.com" && note.Body.Contains("Kitchen tablet"),
   Sent() - before + " " + note?.Body);
Ok("in the account's language", note?.Subject == "Nouvel appareil connecté à Bonfire", note?.Subject);
Device("dev-kitchen", "Kitchen tablet", MAIN);
Ok("the same device again is not new", Sent() == before + 1);
Device("dev-bedroom", "Bedroom TV", MAIN);
Ok("a second, different new device right after is mailed too (R-1056)", Sent() == before + 2 && Last().Body.Contains("Bedroom TV"), (Sent() - before) + "");
Device("dev-other-1", "Phone", OTHER);
Ok("another household's device goes to that household", Sent() == before + 3 && Last().To == "other@example.com");
Device("dev-nobody", "Phone", Guid.NewGuid());
Ok("an account without an address gets nothing", Sent() == before + 3);

// One client must not be able to mail the household without limit by making up device ids.
var beforeFlood = Sent();
for (var i = 0; i < 20; i++) Device("dev-flood-" + i, "Flood " + i, MAIN);
Ok("a flood of made-up devices is capped per account (R-1056)", Sent() - beforeFlood <= 3, (Sent() - beforeFlood) + " mails");

// A device name is set by the client. It is cut and stripped before it goes in a mail.
before = Sent();
Device("dev-evil", "Phone\r\n\r\nYour account is locked, sign in at http://evil.example/login to fix it " + new string('x', 300), OTHER);
note = Last();
Ok("a long, multi-line device name is mailed", Sent() == before + 1 && note.To == "other@example.com", (Sent() - before) + "");
Ok("with its line breaks removed (R-1059)", note != null && !Regex.IsMatch(note.Body, @"Phone\s*[\r\n]+\s*[\r\n]*Your account"), note?.Body);
Ok("and cut short, so the rest never arrives (R-1059)", note != null && !note.Body.Contains("evil.example") && !note.Body.Contains("xxxxx"), note?.Body);

// A profile's PIN locked: the notice is the main account's.
before = Sent();
notesType.GetMethod("PinLocked", Any).Invoke(null, new object[] { KID2, "Littlest", null });
note = Last();
Ok("a profile's locked PIN is mailed to its main account", Sent() == before + 1 && note.To == "other@example.com"
   && note.Body.Contains("Littlest"), note?.To + " " + note?.Body);

// Two profiles locked in the same quarter-hour are two notices (R-1057).
var KID3 = Guid.NewGuid(); world.Add(KID3, "Third_kid"); Map(KID3, OTHER, "Third");
before = Sent();
notesType.GetMethod("PinLocked", Any).Invoke(null, new object[] { KID3, "Third", null });
note = Last();
Ok("a second profile locked soon after the first is mailed too (R-1057)", Sent() == before + 1 && note.Body.Contains("Third"), (Sent() - before) + "");
notesType.GetMethod("PinLocked", Any).Invoke(null, new object[] { KID3, "Third", null });
Ok("the same profile again is not (R-1057)", Sent() == before + 1);

// A send that fails must not use up the quiet period (R-1058).
var KID4 = Guid.NewGuid(); world.Add(KID4, "Fourth_kid"); Map(KID4, OTHER, "Fourth");
before = Sent();
onSend = m => new Exception("mail server down");
notesType.GetMethod("PinLocked", Any).Invoke(null, new object[] { KID4, "Fourth", null });
Drain();
onSend = null;
notesType.GetMethod("PinLocked", Any).Invoke(null, new object[] { KID4, "Fourth", null });
Ok("the next event is mailed after a failed send (R-1058)", Sent() == before + 1 && Last().Body.Contains("Fourth"), (Sent() - before) + "");

// The main PIN locked through a real route: wrong PINs until it refuses.
before = Sent();
var locked = false;
for (var i = 0; i < 15 && !locked; i++) locked = Call("GetAccountEmail", MAIN, pinHeader: "9999").status == 429;
note = Last();
Ok("guessing the main PIN until it locks", locked);
Ok("mails the main account, once", Sent() == before + 1 && note.To == "parent@example.com" && note.Subject == "Trop de codes PIN erronés sur Bonfire",
   (Sent() - before) + " " + note?.Subject);
Call("GetAccountEmail", MAIN, pinHeader: "9999");
Ok("and not again for the next refusal", Sent() == before + 1);
Ok("the right PIN is locked out too", Call("GetAccountEmail", MAIN, pinHeader: "1234").status == 429);

// ── Forgotten PIN ────────────────────────────────────────────────────────────
Section("Forgotten PIN");

Ok("/me offers it to a main account with an address", MePinReset(MAIN) && MePinReset(OTHER));
Ok("never to a profile", !MePinReset(KID) && !MePinReset(KID2));
var NOADDR = Guid.NewGuid(); world.Add(NOADDR, "NoAddress"); Map(NOADDR, NOADDR, "NoAddress", pin: "5555");
Ok("nor to an account with no address", !MePinReset(NOADDR));

r = Call("SendPinReset", KID, B(("Language", "en")));
Ok("a profile cannot ask for a reset code", r.status == 401);
r = Call("SendPinReset", NOADDR, B(("Language", "en")));
Ok("an account with no address cannot", r.status == 400);

before = Sent();
r = Call("SendPinReset", MAIN, B(("Language", "fr")));
var reset = Last();
Ok("the locked main account can, without its PIN", r.status == 200 && Sent() == before + 1, r.status + " " + r.value);
Ok("the code goes to the confirmed address only", reset?.To == "parent@example.com");
Ok("and the reply shows it masked", (string)Get(r.value, "SentTo") == "p•••@example.com", Get(r.value, "SentTo") as string);
Ok("in French", reset?.Subject == "Réinitialisez votre code PIN Bonfire", reset?.Subject);
code = CodeIn(reset);
wrong = code == "000000" ? "111111" : "000000";

r = Call("ResetPinByEmail", MAIN, B(("Code", code), ("NewPin", "12")));
Ok("a malformed new PIN is refused", r.status == 400 && PinIs(MAIN, "1234"));
r = Call("ResetPinByEmail", MAIN, B(("Code", wrong), ("NewPin", "2468")));
Ok("a wrong code is refused", r.status == 400 && PinIs(MAIN, "1234"));
r = Call("ResetPinByEmail", KID, B(("Code", code), ("NewPin", "2468")));
Ok("a profile cannot use the code", r.status == 401 && PinIs(MAIN, "1234"));

before = Sent();
r = Call("ResetPinByEmail", MAIN, B(("Code", code), ("NewPin", "2468")));
Ok("the code and a new PIN set it (a malformed PIN did not spend the code)", r.status == 200 && PinIs(MAIN, "2468") && !PinIs(MAIN, "1234"),
   r.status + " " + r.value);
note = Last();
Ok("a note goes to the address", Sent() == before + 1 && note.To == "parent@example.com" && note.Subject == "Votre code PIN Bonfire a été changé",
   note?.Subject);
Ok("the lock is lifted", Call("GetAccountEmail", MAIN, pinHeader: "2468").status == 200);
Ok("the old PIN no longer works", Call("GetAccountEmail", MAIN, pinHeader: "1234").status == 400);
r = Call("ResetPinByEmail", MAIN, B(("Code", code), ("NewPin", "1357")));
Ok("the code works once", r.status == 400 && PinIs(MAIN, "2468"));

Admin("UpdateAdminSettings", B(("EnablePinResetByEmail", (bool?)false)));
r = Call("SendPinReset", MAIN, B(("Language", "en")));
Ok("with the switch off, no code is sent", r.status == 400 && !MePinReset(MAIN));
Admin("UpdateAdminSettings", B(("EnablePinResetByEmail", (bool?)true)));

// ── When email stops working ─────────────────────────────────────────────────
Section("Email off again");

Admin("UpdateEmailSettings", B(("Port", (int?)2525)));
before = Sent();
Device("dev-other-2", "Laptop", OTHER);
Ok("no notices", Sent() == before);
Ok("no PIN reset", !MePinReset(MAIN) && Call("SendPinReset", MAIN, B(("Language", "en"))).status == 400);
Ok("no new addresses", Call("SetAccountEmail", OTHER, B(("Address", "o2@example.com"))).status == 400);
Ok("but addresses already confirmed are kept", StoredAddress(MAIN) == "parent@example.com" && StoredAddress(OTHER) == "other@example.com");
Admin("UpdateEmailSettings", B(("Port", (int?)587)));
Ok("putting the setting back is working again without a test (same fingerprint)", Available("email"));

// ── Limits ───────────────────────────────────────────────────────────────────
Section("Limits");

var LIMITED = Guid.NewGuid(); world.Add(LIMITED, "Limited"); Map(LIMITED, LIMITED, "Limited");
FreshLimits();
before = Sent();
var codes = Enumerable.Range(0, 6).Select(_ => Call("SetAccountEmail", LIMITED, B(("Address", "l@example.com"))).status).ToList();
Ok("five codes an hour per account", codes.Take(5).All(s => s == 200) && codes[5] == 429 && Sent() == before + 5, string.Join(",", codes));

var serverRefused = false; var tries = 0;
while (!serverRefused && tries < 80)
{
    tries++;
    var u = Guid.NewGuid(); world.Add(u, "u" + tries); Map(u, u, "u" + tries);
    var s = Call("SetAccountEmail", u, B(("Address", "u" + tries + "@example.com")));
    serverRefused = s.status == 429;
}
// 60 an hour for the server; LIMITED's five count toward it, so the 56th account is refused.
Ok("and a cap for the whole server, reached by accounts that each sent one", serverRefused && tries == 56, "tries=" + tries);

// ── Administrator and account removal ────────────────────────────────────────
Section("Removing an address");

r = Admin("GetEmailSettings", null);
var listed = ((IEnumerable)Get(r.value, "Accounts")).Cast<object>().Select(a => (string)Get(a, "Address")).ToList();
Ok("the administrator sees confirmed addresses only", listed.Contains("parent@example.com") && listed.Contains("other@example.com")
   && !listed.Contains("l@example.com"), string.Join(",", listed));
r = Call("RemoveAccountEmailAsAdmin", MAIN, B(("UserId", OTHER)));
Ok("a non-administrator cannot remove one", (r.status == 401 || r.status == 403) && StoredAddress(OTHER) != null);
r = Admin("RemoveAccountEmailAsAdmin", B(("UserId", OTHER)));
Ok("the administrator can", r.status == 200 && StoredAddress(OTHER) == null && !MePinReset(OTHER));
r = Call("RemoveAccountEmail", MAIN, B(("MasterPin", "0000")));
Ok("an account needs its PIN to remove its own", r.status == 400 && StoredAddress(MAIN) != null);
r = Call("RemoveAccountEmail", MAIN, B(("MasterPin", "2468")));
Ok("and with it, it is gone", r.status == 200 && StoredAddress(MAIN) == null && !MePinReset(MAIN));

// ── An address that changes ──────────────────────────────────────────────────
Section("An address that changes, a send that fails, a full server");

// R-1060 / R-1061: a reset code sent to the old address must not outlive the address,
// and the old address is told.
FreshLimits(MAIN);
Call("SetAccountEmail", MAIN, B(("Address", "old@example.com"), ("MasterPin", "2468")));
Call("ConfirmAccountEmail", MAIN, B(("Code", CodeIn(Last())), ("MasterPin", "2468")));
Ok("(set up: the old address is confirmed)", StoredAddress(MAIN) == "old@example.com", StoredAddress(MAIN));
Call("SendPinReset", MAIN, B(("Language", "en")));
var oldReset = CodeIn(Last());
Call("SetAccountEmail", MAIN, B(("Address", "new@example.com"), ("MasterPin", "2468")));
Call("ConfirmAccountEmail", MAIN, B(("Code", CodeIn(Last())), ("MasterPin", "2468")));
Ok("(set up: the address changed)", StoredAddress(MAIN) == "new@example.com", StoredAddress(MAIN));
r = Call("ResetPinByEmail", MAIN, B(("Code", oldReset), ("NewPin", "9753")));
Ok("a reset code sent to the old address does not work after the address changes",
   r.status == 400 && PinIs(MAIN, "2468"), r.status + " " + r.value);
Sent();
Mail told;
lock (sent) told = sent.LastOrDefault(m => m.To == "old@example.com" && m.Subject.Contains("address"));
Ok("the old address is told it was replaced", told != null && told.Body.Contains("n•••@example.com") && !Regex.IsMatch(told.Body, @"\b\d{6}\b"),
   told == null ? "no mail" : told.Body);

// R-1062: a send that fails must not wipe the code already in the inbox.
FreshLimits(MAIN);
Call("SetAccountEmail", MAIN, B(("Address", "keep@example.com"), ("MasterPin", "2468")));
var kept = CodeIn(Last());
onSend = _ => new System.Net.Mail.SmtpException("Service not available");
r = Call("SetAccountEmail", MAIN, B(("Address", "keep@example.com"), ("MasterPin", "2468")));
onSend = null;
Ok("(a resend that fails says so)", r.status == 400, r.status.ToString());
r = Call("ConfirmAccountEmail", MAIN, B(("Code", kept), ("MasterPin", "2468")));
Ok("a failed resend leaves the code already received working", r.status == 200 && StoredAddress(MAIN) == "keep@example.com", r.status + " " + r.value);

// R-1063: confirmation mail to strangers must not use up the server's cap on reset mail.
FreshLimits(MAIN);
var flooded = false; var flood = 0;
while (!flooded && flood < 80)
{
    flood++;
    var f = Guid.NewGuid(); world.Add(f, "f" + flood); Map(f, f, "f" + flood);
    flooded = Call("SetAccountEmail", f, B(("Address", "f" + flood + "@example.com"))).status == 429;
}
Ok("(the server's cap for confirmation mail is reached)", flooded, "flood=" + flood);
r = Call("SendPinReset", MAIN, B(("Language", "en")));
Ok("and a PIN-reset code still goes out", r.status == 200, r.status + " " + r.value);
FreshLimits(MAIN);

// R-1065: what a person is told.
object Desc(Exception e) => mailerType.GetMethod("Describe", Any).Invoke(null, new object[] { e });
var starttls = (string)Desc(new System.Net.Mail.SmtpException("Server does not support secure connections."));
Ok("a server without STARTTLS is not 'refused the message'", starttls.Contains("STARTTLS") && !starttls.Contains("refused the message"), starttls);
var cancelled = (string)Desc(new OperationCanceledException());
Ok("a cancelled request is not 'didn't answer in time'", !cancelled.Contains("didn't answer"), cancelled);
Ok("a real timeout still is", ((string)Desc(new TimeoutException())).Contains("didn't answer in time"));

// R-1066: a made-up language tag must not stay in memory.
var emailTextType = asm.GetType("Jellyfin.Profiles.Mail.EmailText");
var loaded = (System.Collections.IDictionary)emailTextType.GetField("Loaded", Any).GetValue(null);
var loadedBefore = loaded.Count;
var textGet = emailTextType.GetMethod("Get", Any);
for (var i = 0; i < 40; i++) textGet.Invoke(null, new object[] { "email.test.subject", "zq" + i, null });
Ok("40 languages that don't exist leave nothing cached", loaded.Count == loadedBefore, loadedBefore + " -> " + loaded.Count);

// R-1067: 465 speaks TLS from the first byte whatever the security setting says.
r = Admin("UpdateEmailSettings", B(("Host", "smtp.example.com"), ("Port", (int?)465), ("Security", "none")));
Ok("port 465 is refused with security none too", r.status == 400 && (int)Get(Email(), "Port") != 465, r.status + " " + Get(Email(), "Port"));

// Back to where the next section expects MAIN: no confirmed address.
Call("RemoveAccountEmail", MAIN, B(("MasterPin", "2468")));

// ── Stored and served ────────────────────────────────────────────────────────
Section("Stored and served");

FreshLimits(MAIN, OTHER);
Call("SetAccountEmail", OTHER, B(("Address", "other@example.com"), ("Language", "pl")));
var pendingCode = CodeIn(Last());
Call("ConfirmAccountEmail", OTHER, B(("Code", pendingCode), ("Language", "pl")));
Call("SetAccountEmail", MAIN, B(("Address", "parent@example.com"), ("MasterPin", "2468")));
var unconfirmedCode = CodeIn(Last());

var xs = new XmlSerializer(cfgType);
string xml;
using (var w = new StringWriter()) { xs.Serialize(w, config); xml = w.ToString(); }
object back;
using (var rd = new StringReader(xml)) back = xs.Deserialize(rd);
var backEmail = cfgType.GetProperty("Email").GetValue(back);
var backRows = ((IList)cfgType.GetProperty("AccountEmails").GetValue(back)).Cast<object>().ToList();
Ok("the settings survive a save, still verified", (string)Get(backEmail, "Host") == "smtp.example.com" && Bool(backEmail, "IsVerified"));
Ok("the confirmed addresses survive, once each", backRows.Count == AccountEmails().Count && backRows.Count == 1
   && (string)Get(backRows[0], "Address") == "other@example.com" && (string)Get(backRows[0], "Language") == "pl", backRows.Count.ToString());
Ok("the PIN-reset switch survives", (bool)cfgType.GetProperty("EnablePinResetByEmail").GetValue(back));
Ok("an unconfirmed address is not written", !xml.Contains("parent@example.com"));
Ok("no code is written", !xml.Contains(unconfirmedCode));

var snap = cfgType.GetMethod("SnapshotForReading").Invoke(config, null);
Get(snap, "Email").GetType().GetProperty("Host").SetValue(Get(snap, "Email"), "changed");
((IList)cfgType.GetProperty("AccountEmails").GetValue(snap)).Clear();
Ok("a read snapshot has its own copies", (string)Get(Email(), "Host") == "smtp.example.com" && AccountEmails().Count == 1);

var part = (MethodInfo)ctrlType.GetMethod("ReadLocalePart", Any);
var appFr = (string)part.Invoke(null, new object[] { "fr", false, null });
var adminFr = (string)part.Invoke(null, new object[] { "fr", true, null });
var whole = (string)ctrlType.GetMethod("ReadLocaleJson", Any).Invoke(null, new object[] { "fr", null });
Ok("the French file has the mail's words", whole != null && whole.Contains("\"email.confirm.subject\""));
Ok("and neither half sent to a browser does", appFr != null && adminFr != null && !appFr.Contains("\"email.") && !adminFr.Contains("\"email."));

try { Directory.Delete(tempDir, true); } catch { }
Console.WriteLine();
if (fails.Count > 0)
{
    foreach (var f in fails) Console.WriteLine("   - " + f);
    Console.WriteLine(pass + " passed, " + fails.Count + " failed");
    return 1;
}
Console.WriteLine(pass + " passed, 0 failed");
return 0;

// ── The recorder ─────────────────────────────────────────────────────────────

public class Mail
{
    public string Host, To, Subject, Body;
}

public class Recorder : DispatchProxy
{
    public Action<Mail> OnSend;

    protected override object Invoke(MethodInfo m, object[] args)
    {
        if (m.Name != "SendAsync") throw new NotSupportedException(m.Name);
        var mail = new Mail
        {
            Host = (string)args[0]?.GetType().GetProperty("Host")?.GetValue(args[0]),
            To = (string)args[1],
            Subject = (string)args[2],
            Body = (string)args[3],
        };
        try { OnSend(mail); }
        catch (Exception ex) { return Task.FromException(ex); }
        return Task.CompletedTask;
    }
}

// ── A modelled Jellyfin ──────────────────────────────────────────────────────

public class World
{
    public Dictionary<Guid, (User user, bool admin)> Users = new();

    public void Add(Guid id, string name, bool admin = false)
    {
        var u = (User)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(User));
        typeof(User).GetProperty("Username").SetValue(u, name);
        typeof(User).GetProperty("Id").SetValue(u, id);
        Users[id] = (u, admin);
    }

    public static IUserManager Create(World w)
    {
        var p = DispatchProxy.Create<IUserManager, WorldProxy>();
        ((WorldProxy)(object)p).W = w;
        return p;
    }
}

public class WorldProxy : DispatchProxy
{
    public World W;

    protected override object Invoke(MethodInfo m, object[] args)
    {
        switch (m.Name)
        {
            case "GetUserById":
                return W.Users.TryGetValue((Guid)args[0], out var row) ? row.user : null;
            case "GetUserDto":
                var user = (User)args[0];
                var admin = W.Users.TryGetValue(user.Id, out var r2) && r2.admin;
                return new UserDto { Id = user.Id, Name = user.Username, Policy = new UserPolicy { IsAdministrator = admin }, Configuration = new UserConfiguration() };
            case "get_Users":
            case "GetUsers":
                return W.Users.Values.Select(v => v.user).ToList();
        }
        throw new NotSupportedException("IUserManager." + m.Name + " is not modelled");
    }
}

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
    public object DeserializeFromFile(Type type, string file)
        => throw new FileNotFoundException("stub serializer", file);
    public object DeserializeFromStream(Type type, Stream stream)
        => throw new NotSupportedException();
    public object DeserializeFromBytes(Type type, byte[] buffer)
        => throw new NotSupportedException();
}
