using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Jellyfin.Profiles.Configuration;

// ─────────────────────────────────────────────────────────────────────────────
// Tiles the server draws in a theme's colours (1.7.2): the Moonfin picker's folders and
// a profile with no picture. Shapes only, in a PNG Bonfire writes itself.
//
// Each PNG is taken apart here by a decoder written for this harness: the signature,
// every chunk's CRC, the header, and the pixels inflated back out. Then the pixels are
// read: the glyph's centre is the theme's accent, a corner is the theme's background,
// and a gradient runs the way CSS draws it. A file that only looks like a PNG, or a
// picture drawn in the wrong colours, fails.
//
// Against ae2dd66 this does not compile: TileImage does not exist.
//
// Usage: dotnet run -c Release
// ─────────────────────────────────────────────────────────────────────────────

int pass = 0;
var fails = new List<string>();
void Ok(string name, bool cond, string detail = null)
{
    if (cond) { pass++; Console.WriteLine("  PASS  " + name); }
    else { fails.Add(name + (detail == null ? "" : "  — " + detail)); Console.WriteLine("  FAIL  " + name + (detail == null ? "" : "  — " + detail)); }
}

uint Crc(byte[] data)
{
    var c = 0xFFFFFFFFu;
    foreach (var b in data)
    {
        c ^= b;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
    }
    return c ^ 0xFFFFFFFFu;
}
uint U32(byte[] b, int at) => ((uint)b[at] << 24) | ((uint)b[at + 1] << 16) | ((uint)b[at + 2] << 8) | b[at + 3];

// The pixels as [y, x] → (r, g, b), or a reason it is not a PNG this harness accepts.
(byte[,,] Pixels, string Error) Decode(byte[] png)
{
    if (!png.Take(8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return (null, "signature");
    var at = 8;
    int w = 0, h = 0;
    var idat = new MemoryStream();
    var sawEnd = false;
    while (at + 12 <= png.Length)
    {
        var len = (int)U32(png, at);
        var type = Encoding.ASCII.GetString(png, at + 4, 4);
        var body = png.Skip(at + 4).Take(4 + len).ToArray();
        if (U32(png, at + 8 + len) != Crc(body)) return (null, "bad CRC on " + type);
        var data = body.Skip(4).ToArray();
        if (type == "IHDR")
        {
            w = (int)U32(data, 0); h = (int)U32(data, 4);
            if (data[8] != 8 || data[9] != 2) return (null, "not 8-bit RGB");
        }
        else if (type == "IDAT") idat.Write(data);
        else if (type == "IEND") { sawEnd = true; break; }
        at += 12 + len;
    }
    if (!sawEnd) return (null, "no IEND");
    var raw = new MemoryStream();
    using (var z = new ZLibStream(new MemoryStream(idat.ToArray()), CompressionMode.Decompress)) z.CopyTo(raw);
    var bytes = raw.ToArray();
    if (bytes.Length != h * (1 + (w * 3))) return (null, "pixel data is " + bytes.Length + " bytes");
    var px = new byte[h, w, 3];
    for (var y = 0; y < h; y++)
    {
        var row = y * (1 + (w * 3));
        if (bytes[row] != 0) return (null, "filter " + bytes[row]);
        for (var x = 0; x < w; x++)
            for (var c = 0; c < 3; c++) px[y, x, c] = bytes[row + 1 + (x * 3) + c];
    }
    return (px, null);
}

string Hex(byte[,,] px, double u, double v)
{
    var x = (int)(u * TileImage.Size); var y = (int)(v * TileImage.Size);
    return "#" + px[y, x, 0].ToString("x2") + px[y, x, 1].ToString("x2") + px[y, x, 2].ToString("x2");
}

JsonObject Flat(string fill, string accent) => new JsonObject
{
    ["id"] = "flat",
    ["background"] = new JsonObject { ["fill"] = fill },
    ["colors"] = new JsonObject { ["accent"] = accent },
};

Console.WriteLine("── A flat theme, each glyph ─────────────────────────────────────");
var flat = Flat("#102030", "#ffcc00");
// Points well inside each glyph, and one well outside all of them.
var inside = new Dictionary<TileGlyph, (double, double)>
{
    [TileGlyph.Person] = (0.5, 0.38),
    [TileGlyph.Switch] = (0.45, 0.41),
    [TileGlyph.Profiles] = (0.39, 0.39),
};
foreach (var glyph in new[] { TileGlyph.Person, TileGlyph.Switch, TileGlyph.Profiles })
{
    var png = TileImage.Png(flat, glyph);
    var (px, err) = Decode(png);
    Ok(glyph + ": a valid 256×256 RGB PNG, every CRC right", px != null && px.GetLength(0) == 256 && px.GetLength(1) == 256, err);
    if (px == null) continue;
    var (gx, gy) = inside[glyph];
    Ok(glyph + ": the glyph is the accent", Hex(px, gx, gy) == "#ffcc00", Hex(px, gx, gy));
    Ok(glyph + ": the corners are the background", Hex(px, 0.02, 0.02) == "#102030" && Hex(px, 0.97, 0.97) == "#102030");
    var edge = 0;
    for (var x = 0; x < 256; x++)
    {
        var c = px[(int)(gy * 256), x, 0];
        if (c != 0x10 && c != 0xff) edge++;
    }
    Ok(glyph + ": its edges are smoothed, not stepped (" + edge + " blended pixels on a row)", edge >= 2);
}
Ok("each glyph is its own picture",
   !TileImage.Png(flat, TileGlyph.Person).SequenceEqual(TileImage.Png(flat, TileGlyph.Switch))
   && !TileImage.Png(flat, TileGlyph.Switch).SequenceEqual(TileImage.Png(flat, TileGlyph.Profiles)));

Console.WriteLine();
Console.WriteLine("── Backgrounds drawn as CSS draws them ──────────────────────────");
var linear = Flat("#000000", "#ff0000");
linear["background"]!["layers"] = new JsonArray(new JsonObject
{
    ["type"] = "linear", ["angle"] = 180,
    ["stops"] = new JsonArray(new JsonObject { ["color"] = "#000000", ["at"] = 0 }, new JsonObject { ["color"] = "#ffffff", ["at"] = 100 }),
});
var (lin, linErr) = Decode(TileImage.Png(linear, TileGlyph.Profiles));
Ok("180deg runs top to bottom: dark at the top, light at the bottom, grey between",
   lin != null && lin[1, 4, 0] < 8 && lin[254, 4, 0] > 247 && Math.Abs(lin[128, 4, 0] - 128) <= 3, linErr ?? lin[1, 4, 0] + " " + lin[128, 4, 0] + " " + lin[254, 4, 0]);
linear["background"]!["layers"]![0]!["angle"] = 90;
var (side, _) = Decode(TileImage.Png(linear, TileGlyph.Profiles));
Ok("90deg runs left to right", side[4, 1, 0] < 8 && side[4, 254, 0] > 247);

var see = Flat("#000000", "#ff0000");
see["background"]!["layers"] = new JsonArray(new JsonObject
{
    ["type"] = "linear", ["angle"] = 180,
    ["stops"] = new JsonArray(new JsonObject { ["color"] = "#ffffff80", ["at"] = 0 }, new JsonObject { ["color"] = "#ffffff80", ["at"] = 100 }),
});
var (half, _) = Decode(TileImage.Png(see, TileGlyph.Profiles));
Ok("a see-through layer is mixed onto the fill, not drawn opaque", Math.Abs(half[4, 4, 0] - 128) <= 2, half[4, 4, 0].ToString());

var stacked = Flat("#000000", "#ff0000");
JsonObject Solid(string colour) => new JsonObject
{
    ["type"] = "linear",
    ["stops"] = new JsonArray(new JsonObject { ["color"] = colour, ["at"] = 0 }, new JsonObject { ["color"] = colour, ["at"] = 100 }),
};
stacked["background"]!["layers"] = new JsonArray(Solid("#ffffff"), Solid("#0000ff"));
var (top, _) = Decode(TileImage.Png(stacked, TileGlyph.Profiles));
Ok("the first layer is on top, as in CSS", Hex(top, 0.02, 0.02) == "#ffffff", Hex(top, 0.02, 0.02));

var radial = Flat("#000000", "#ff0000");
radial["background"]!["layers"] = new JsonArray(new JsonObject
{
    ["type"] = "radial", ["x"] = 50, ["y"] = 50,
    ["stops"] = new JsonArray(new JsonObject { ["color"] = "#ffffff", ["at"] = 0 }, new JsonObject { ["color"] = "#000000", ["at"] = 100 }),
});
var (rad, _) = Decode(TileImage.Png(radial, TileGlyph.Person));
Ok("a radial layer is brightest at its centre and dark at the corners",
   rad[200, 128, 0] > rad[250, 128, 0] && rad[4, 4, 0] < 20, rad[200, 128, 0] + " " + rad[250, 128, 0] + " " + rad[4, 4, 0]);

Console.WriteLine();
Console.WriteLine("── Every built-in ───────────────────────────────────────────────");
foreach (var theme in ThemeCatalog.BuiltIn)
{
    var id = (string)theme["id"]!;
    var (px, err) = Decode(TileImage.Png(theme, TileGlyph.Person));
    var accent = ((string)theme["colors"]!["accent"]!).ToLowerInvariant();
    Ok(id + ": a valid PNG with the accent at the glyph (" + accent + ")", px != null && Hex(px, 0.5, 0.38) == accent.Substring(0, 7), err ?? Hex(px, 0.5, 0.38));
}

Console.WriteLine();
Console.WriteLine("── Tags ─────────────────────────────────────────────────────────");
var bonfire = ThemeCatalog.BuiltIn.First(t => (string)t["id"]! == "bonfire");
var tag = TileImage.Tag(bonfire, TileGlyph.Person);
Ok("the same theme and glyph, the same tag", TileImage.Tag((JsonObject)bonfire.DeepClone(), TileGlyph.Person) == tag);
Ok("another glyph, another tag", TileImage.Tag(bonfire, TileGlyph.Switch) != tag);
var recoloured = (JsonObject)bonfire.DeepClone();
recoloured["colors"]!["accent"] = "#00ff00";
Ok("a new accent, a new tag", TileImage.Tag(recoloured, TileGlyph.Person) != tag);
var refilled = (JsonObject)bonfire.DeepClone();
refilled["background"]!["fill"] = "#000000";
Ok("a new background, a new tag", TileImage.Tag(refilled, TileGlyph.Person) != tag);
var renamed = (JsonObject)bonfire.DeepClone();
renamed["name"] = new JsonObject { ["text"] = "Other" };
Ok("a change the picture does not show keeps the tag, so nothing is fetched again", TileImage.Tag(renamed, TileGlyph.Person) == tag);

Console.WriteLine();
Console.WriteLine("── Bad values draw something, never throw ───────────────────────");
var broken = new JsonObject
{
    ["background"] = new JsonObject { ["fill"] = "red; x", ["layers"] = new JsonArray(new JsonObject { ["type"] = "radial", ["stops"] = "no" }, 7) },
    ["colors"] = new JsonObject { ["accent"] = 12 },
};
string threw = null;
try { Decode(TileImage.Png(broken, TileGlyph.Person)); } catch (Exception e) { threw = e.GetType().Name; }
Ok("a theme with nonsense in it still draws, in Bonfire's colours", threw == null && Decode(TileImage.Png(broken, TileGlyph.Person)).Pixels != null, threw);

Console.WriteLine();
if (fails.Count > 0)
{
    foreach (var f in fails) Console.WriteLine("   - " + f);
    Console.WriteLine(pass + " passed, " + fails.Count + " failed");
    return 1;
}
Console.WriteLine(pass + " passed, 0 failed");
return 0;
