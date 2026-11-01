using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Serialization;
using Jellyfin.Profiles.Configuration;

// ─────────────────────────────────────────────────────────────────────────────
// The server half of themes, against the compiled plugin.
//
//   - which theme an account sees, from its own row, the server default, and whether the
//     server lets profiles choose;
//   - what the create and update routes accept and store;
//   - that a configuration saved before themes existed loads with choosing ON and the
//     default Bonfire, so upgrading changes nobody's screen.
//
// The last one is the one that would be silent: Jellyfin reads PluginConfiguration.xml
// with XmlSerializer, and a missing element keeps the property initializer. Change an
// initializer and every existing server changes with it, with nothing in the log.
// ─────────────────────────────────────────────────────────────────────────────

int pass = 0;
var fails = new List<string>();
void Ok(string name, bool cond, string detail = null)
{
    if (cond) { pass++; Console.WriteLine("  PASS  " + name); }
    else { fails.Add(name); Console.WriteLine("  FAIL  " + name + (detail == null ? "" : "  — " + detail)); }
}

Console.WriteLine("── The theme list ───────────────────────────────────────────────");

var expected = new[] { "bonfire", "crimson", "starlight", "harbor", "nightfall", "glass" };
Ok("six themes, Bonfire first (" + string.Join(", ", Themes.All) + ")",
   Themes.All.SequenceEqual(expected));
Ok("every id is lowercase, which is what the stylesheet keys on",
   Themes.All.All(id => id == id.ToLowerInvariant()));

foreach (var id in expected)
{
    Ok(id + " is known", Themes.IsKnown(id));
    Ok(id.ToUpperInvariant() + " is known too, and normalises to " + id,
       Themes.IsKnown(id.ToUpperInvariant()) && Themes.Normalize(id.ToUpperInvariant()) == id);
}
foreach (var bad in new[] { null, "", "   ", "default", "neon", "bonfire-dark" })
{
    Ok("'" + (bad ?? "null") + "' is not a theme, and normalises to bonfire",
       !Themes.IsKnown(bad) && Themes.Normalize(bad) == Themes.Bonfire);
}

Console.WriteLine();
Console.WriteLine("── Which theme an account sees ─────────────────────────────────");

ProfileMapping Row(string theme) => new ProfileMapping { ProfileUserId = Guid.NewGuid(), Theme = theme };

Ok("its own choice, when choosing is allowed",
   Themes.Resolve(Row("glass"), "crimson", allowChoice: true) == "glass");
Ok("the server default, when it has not chosen",
   Themes.Resolve(Row(null), "crimson", allowChoice: true) == "crimson");
Ok("the server default, when choosing is turned off — whatever it chose",
   Themes.Resolve(Row("glass"), "crimson", allowChoice: false) == "crimson");
Ok("the server default for an account with no row at all",
   Themes.Resolve(null, "harbor", allowChoice: true) == "harbor");
Ok("a stored choice in another case still counts",
   Themes.Resolve(Row("Starlight"), "crimson", allowChoice: true) == "starlight");
Ok("a stored id that is no longer a theme falls back to the default, not to nothing",
   Themes.Resolve(Row("retired"), "nightfall", allowChoice: true) == "nightfall");
Ok("and a broken default falls back to Bonfire",
   Themes.Resolve(Row(null), "nonsense", allowChoice: true) == Themes.Bonfire);

Console.WriteLine();
Console.WriteLine("── What the create and update routes accept ────────────────────");

var ctrl = typeof(Themes).Assembly.GetType("Jellyfin.Profiles.Controllers.ProfilesController", true);
const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
var validate = ctrl.GetMethod("ValidateTheme", Flags);
var toStore = ctrl.GetMethod("ThemeToStore", Flags);
Ok("ValidateTheme is reachable", validate != null);
Ok("ThemeToStore is reachable", toStore != null);

if (validate != null && toStore != null)
{
    string V(string v) => (string)validate.Invoke(null, new object[] { v });
    string S(string v) => (string)toStore.Invoke(null, new object[] { v });

    foreach (var good in new[] { null, "", "glass", "Glass", "CRIMSON" })
        Ok("'" + (good ?? "null") + "' is accepted", V(good) == null);
    var refusal = V("neon");
    Ok("an unknown theme is refused, naming the ones there are (" + refusal + ")",
       refusal != null && expected.All(id => refusal.Contains(id)));

    Ok("\"\" is stored as null — follow the server default", S("") == null);
    Ok("null is stored as null", S(null) == null);
    Ok("a theme is stored lowercase", S("Glass") == "glass");
}

Console.WriteLine();
Console.WriteLine("── Upgrading changes nobody's screen ───────────────────────────");

var fresh = new PluginConfiguration();
Ok("a new server defaults to Bonfire", fresh.DefaultTheme == Themes.Bonfire);
Ok("and lets profiles choose", fresh.AllowProfileThemes);

// A configuration from before themes: no DefaultTheme, no AllowProfileThemes, and a
// mapping with no Theme. Written by serialising a current one and deleting the elements,
// so the rest of the document is exactly what the serializer expects.
var serializer = new XmlSerializer(typeof(PluginConfiguration));
var before = new PluginConfiguration();
before.Mappings.Add(new ProfileMapping { ProfileUserId = Guid.NewGuid(), MasterUserId = Guid.NewGuid(), ProfileName = "Kids" });
string xml;
using (var w = new StringWriter()) { serializer.Serialize(w, before); xml = w.ToString(); }
var old = System.Xml.Linq.XDocument.Parse(xml);
old.Descendants().Where(e => e.Name.LocalName is "DefaultTheme" or "AllowProfileThemes" or "Theme").ToList()
   .ForEach(e => e.Remove());
Ok("the old document really has no theme elements",
   !old.Descendants().Any(e => e.Name.LocalName.Contains("Theme")));

PluginConfiguration loaded;
using (var r = new StringReader(old.ToString())) loaded = (PluginConfiguration)serializer.Deserialize(r);
Ok("it loads with the default Bonfire", loaded.DefaultTheme == Themes.Bonfire);
Ok("with choosing on", loaded.AllowProfileThemes);
Ok("and its profile following the default", loaded.Mappings.Count == 1 && loaded.Mappings[0].Theme == null);

// And a choice survives the round trip, including through the copy every read takes.
var chosen = new PluginConfiguration { DefaultTheme = Themes.Harbor, AllowProfileThemes = false };
chosen.Mappings.Add(new ProfileMapping { ProfileUserId = Guid.NewGuid(), Theme = Themes.Glass });
using (var w = new StringWriter()) { serializer.Serialize(w, chosen); xml = w.ToString(); }
using (var r = new StringReader(xml)) loaded = (PluginConfiguration)serializer.Deserialize(r);
Ok("the default and the switch are saved", loaded.DefaultTheme == Themes.Harbor && !loaded.AllowProfileThemes);
Ok("and so is a profile's choice", loaded.Mappings[0].Theme == Themes.Glass);
Ok("which the locked read copy keeps", loaded.Mappings[0].CopyForReading().Theme == Themes.Glass);

Console.WriteLine();
Console.WriteLine("── Settings → Theme sets one profile, and only the owner's ────");

// The route behind the Settings page. It exists because update rewrites every field from
// what it is sent; this one must touch the theme and nothing else, and only inside the
// caller's own household.
Guid kidsLibrary = Guid.NewGuid();
Guid master = Guid.NewGuid(), child = Guid.NewGuid(), otherMaster = Guid.NewGuid(), otherChild = Guid.NewGuid();
PluginConfiguration House() => new PluginConfiguration
{
    AllowProfileThemes = true,
    Mappings = new List<ProfileMapping>
    {
        new ProfileMapping { ProfileUserId = master, MasterUserId = master, Theme = null },
        new ProfileMapping { ProfileUserId = child, MasterUserId = master, Theme = "crimson", PinHash = "hash", EnabledFolders = new List<Guid> { kidsLibrary } },
        new ProfileMapping { ProfileUserId = otherMaster, MasterUserId = otherMaster },
        new ProfileMapping { ProfileUserId = otherChild, MasterUserId = otherMaster, Theme = "glass" }
    }
};

var h = House();
Ok("a master sets its own theme", Themes.SetFor(h, master, master, "Nightfall") == ThemeChange.Changed
   && h.Mappings[0].Theme == "nightfall");
Ok("and one of its profiles'", Themes.SetFor(h, master, child, "harbor") == ThemeChange.Changed
   && h.Mappings[1].Theme == "harbor");
Ok("and nothing else on that profile moves", h.Mappings[1].PinHash == "hash"
   && h.Mappings[1].EnabledFolders.SequenceEqual(new[] { kidsLibrary }));
Ok("an empty theme goes back to following the server default",
   Themes.SetFor(h, master, child, "") == ThemeChange.Changed && h.Mappings[1].Theme == null);

h = House();
Ok("another household's profile is refused", Themes.SetFor(h, master, otherChild, "harbor") == ThemeChange.NotInHousehold);
Ok("and left as it was", h.Mappings[3].Theme == "glass");
Ok("another household's master too", Themes.SetFor(h, master, otherMaster, "harbor") == ThemeChange.NotInHousehold
   && h.Mappings[2].Theme == null);
Ok("an account with no row is refused", Themes.SetFor(h, master, Guid.NewGuid(), "harbor") == ThemeChange.NotInHousehold);
Ok("an unknown theme is refused, and stores nothing",
   Themes.SetFor(h, master, child, "neon") == ThemeChange.UnknownTheme && h.Mappings[1].Theme == "crimson");

h = House();
h.AllowProfileThemes = false;
Ok("nothing is set while the server does not let profiles choose",
   Themes.SetFor(h, master, child, "harbor") == ThemeChange.NotAllowed && h.Mappings[1].Theme == "crimson");

Console.WriteLine();
if (fails.Count > 0)
{
    foreach (var f in fails) Console.WriteLine("   - " + f);
    Console.WriteLine(pass + " passed, " + fails.Count + " failed");
    return 1;
}
Console.WriteLine(pass + " passed, 0 failed");
return 0;
