using System.IO.Compression;
using System.Security.Cryptography;
using PdfPecker.Rendering.Fonts;

// Builds the compact built-in font pack (StandardFonts.bin) used to render the standard 14 PDF fonts and
// non-embedded fonts. Inputs: the Liberation 2.x TTFs (SIL OFL 1.1) and DejaVuSans.ttf (Bitstream Vera license).
// Usage: dotnet run --project tools/FontPackBuilder -- /usr/share/fonts/truetype/liberation /usr/share/fonts/truetype/dejavu/DejaVuSans.ttf [out]
//
// With --bundle it instead writes the Liberation TTFs, byte for byte, Brotli-compressed to src/PdfPecker/Fonts/Bundled,
// where they are embedded for PDF/A and PdfOptions.EmbedStandardFonts:
// dotnet run --project tools/FontPackBuilder -- --bundle /usr/share/fonts/truetype/liberation [outDir]
//
// The checked-in files were built from Liberation 2.1.5 and DejaVu Sans 2.37, as Ubuntu 24.04's fonts-liberation and
// fonts-dejavu-core packages install them. Every input is checked against the hashes below, so other versions of the
// fonts cannot change the output unnoticed; when updating the fonts on purpose, update the hashes, these versions and
// THIRD-PARTY-NOTICES.md in the same change.
var pinned = new Dictionary<string, string>
{
    ["LiberationMono-Bold.ttf"] = "626655e94dd82f3f42549daf995c921b0915fa8ab1f4b839559e8892ea41d240",
    ["LiberationMono-BoldItalic.ttf"] = "15eb161953e3ecc7fc05a3fec8a59e0f4e0a54a4e375736adf8e98d65981f813",
    ["LiberationMono-Italic.ttf"] = "a71b2c25c89da05cf0e7c4dbba8d473fdead0b181ae56165217747d2c1f39215",
    ["LiberationMono-Regular.ttf"] = "395fa5ab8d40c8eba390ced528744ea75a7f69aabf3e68b6f925ca0e39a27370",
    ["LiberationSans-Bold.ttf"] = "3973aa5054fb467dd5627245d3dc82e37bf16fe075756156a570455871351582",
    ["LiberationSans-BoldItalic.ttf"] = "c80fa7f2ffa0e01d4d8dcd6a6d1e43eda665222d1b4db597dde1174c456006cf",
    ["LiberationSans-Italic.ttf"] = "830c5fa600505fb4c1a271b4271c53c44bae43f492b2a240d0e98a3a7a380121",
    ["LiberationSans-Regular.ttf"] = "4659bc0c58c5028dd488ec928d41d9265db43d9b669fc14ca8b0832daca7b144",
    ["LiberationSerif-Bold.ttf"] = "9e66c25e20868756bf12eb24a520a4552727c9cd108f577f7ae33e3c0110e39c",
    ["LiberationSerif-BoldItalic.ttf"] = "379010e87421a883f7bbfa7936d23dfe0257a54ce850819b74556e9f2c615a8a",
    ["LiberationSerif-Italic.ttf"] = "eaeaf7f2b12544ecde64290c1fa7ec403ff70fd58b313637b265400b20249242",
    ["LiberationSerif-Regular.ttf"] = "705903ae1382f0150f115d6f10a30f5b2f3d6ade649c474afe6366c39174fb65",
    ["DejaVuSans.ttf"] = "ae7b7855e115a5966d8b1b3f80f254ccc117ec86f9965e202ee2940453837280",
};
void CheckPinned(string path)
{
    string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    if (!pinned.TryGetValue(Path.GetFileName(path), out var expected) || actual != expected)
        throw new InvalidOperationException($"{path} is not the pinned version (SHA-256 {actual}); see the comment at the top of Program.cs.");
}

if (args.Length > 0 && args[0] == "--bundle")
{
    string from = args.Length > 1 ? args[1] : "/usr/share/fonts/truetype/liberation";
    string to = args.Length > 2 ? args[2] : Path.Combine("src", "PdfPecker", "Fonts", "Bundled");
    Directory.CreateDirectory(to);
    foreach (var family in new[] { "Sans", "Serif", "Mono" })
    foreach (var style in new[] { "Regular", "Bold", "Italic", "BoldItalic" })
    {
        string name = $"Liberation{family}-{style}.ttf";
        CheckPinned(Path.Combine(from, name));
        var data = File.ReadAllBytes(Path.Combine(from, name));
        var packed = new byte[BrotliEncoder.GetMaxCompressedLength(data.Length)];
        if (!BrotliEncoder.TryCompress(data, packed, out int written, quality: 11, window: 22)) throw new Exception("Brotli failed for " + name);
        File.WriteAllBytes(Path.Combine(to, name + ".br"), packed.AsSpan(0, written).ToArray());
        Console.WriteLine($"{name}: {data.Length} -> {written} bytes");
    }
    return;
}

string libDir = args.Length > 0 ? args[0] : "/usr/share/fonts/truetype/liberation";
string dejavu = args.Length > 1 ? args[1] : "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";
string output = args.Length > 2 ? args[2] : Path.Combine("src", "PdfPecker", "Rendering", "Fonts", "StandardFonts.bin");

var latinRanges = new (int, int)[]
{
    (0x20, 0x7E), (0xA0, 0x17F), (0x192, 0x192), (0x1FA, 0x1FF), (0x218, 0x21B), (0x237, 0x237), (0x2C6, 0x2DD),
    (0x384, 0x3CE), (0x1E80, 0x1E85), (0x1EF2, 0x1EF3), (0x2010, 0x2027), (0x2030, 0x203A), (0x203C, 0x203E), (0x2044, 0x2044),
    (0x20A0, 0x20BF), (0x2105, 0x2105), (0x2113, 0x2113), (0x2116, 0x2116), (0x2122, 0x2122), (0x2126, 0x2126),
    (0x212E, 0x212E), (0x215B, 0x215E), (0x2190, 0x2195), (0x2202, 0x2202), (0x2206, 0x2206), (0x220F, 0x220F),
    (0x2211, 0x2212), (0x2215, 0x2215), (0x2219, 0x221A), (0x221E, 0x221E), (0x222B, 0x222B), (0x2248, 0x2248),
    (0x2260, 0x2260), (0x2264, 0x2265), (0x25CA, 0x25CA), (0x25CF, 0x25CF), (0xFB00, 0xFB06),
};
var faces = new List<(string Name, string File, HashSet<int> Chars)>();
foreach (var (family, file) in new[] { ("Sans", "LiberationSans"), ("Serif", "LiberationSerif"), ("Mono", "LiberationMono") })
{
    foreach (var (style, suffix) in new[] { ("", "Regular"), ("-Bold", "Bold"), ("-Italic", "Italic"), ("-BoldItalic", "BoldItalic") })
    {
        var chars = new HashSet<int>();
        foreach (var (a, b) in latinRanges) for (int c = a; c <= b; c++) chars.Add(c);
        faces.Add((family + style, Path.Combine(libDir, file + "-" + suffix + ".ttf"), chars));
    }
}
foreach (var face in faces) CheckPinned(face.File);
CheckPinned(dejavu);
faces.Add(("Symbol", dejavu, new HashSet<int>(Encodings.SymbolUnicode.Where(u => u > 0))));
faces.Add(("Dingbats", dejavu, new HashSet<int>(Encodings.DingbatsUnicode.Where(u => u > 0))));

var ms = new MemoryStream();
var w = new Writer(ms);
ms.Write("PPF1"u8);

// Glyph name -> Unicode table from the fonts' post tables (AGL-style names), excluding names that decode by rule.
var names = new SortedDictionary<string, int>(StringComparer.Ordinal);
foreach (var file in faces.Select(f => f.File).Distinct().Append(Path.Combine(libDir, "LiberationSans-Regular.ttf")))
{
    var font = TrueTypeFont.Parse(File.ReadAllBytes(file)) ?? throw new Exception("Cannot parse " + file);
    var cmap = font.GetCmap(3, 1) ?? throw new Exception("No Unicode cmap in " + file);
    var byGid = new Dictionary<int, int>();
    foreach (var (u, g) in cmap.OrderBy(kv => kv.Key)) byGid.TryAdd(g, u);
    foreach (var (name, gid) in font.PostNames ?? new())
    {
        if (!byGid.TryGetValue(gid, out int u) || u > 0xFFFF) continue;
        if (name.StartsWith("uni", StringComparison.Ordinal) || (name.Length >= 5 && name[0] == 'u' && name.Skip(1).All(Uri.IsHexDigit))) continue;
        if (name.StartsWith(".", StringComparison.Ordinal) || name.Contains('.')) continue;
        names.TryAdd(name, u);
    }
}
// AGL names that the fonts spell differently.
foreach (var (n, u) in new (string, int)[]
{
    ("Euro", 0x20AC), ("nbspace", 0xA0), ("sfthyphen", 0xAD), ("hyphen", 0x2D), ("minus", 0x2212), ("mu", 0xB5), ("Omega", 0x2126),
    ("Delta", 0x2206), ("periodcentered", 0xB7), ("middot", 0xB7), ("dotlessi", 0x131), ("fi", 0xFB01), ("fl", 0xFB02),
    ("ff", 0xFB00), ("ffi", 0xFB03), ("ffl", 0xFB04), ("quotesingle", 0x27), ("grave", 0x60), ("macron", 0xAF), ("overscore", 0xAF),
    ("Ifraktur", 0x2111), ("Rfraktur", 0x211C), ("aleph", 0x2135), ("weierstrass", 0x2118), ("universal", 0x2200),
    ("existential", 0x2203), ("suchthat", 0x220B), ("asteriskmath", 0x2217), ("congruent", 0x2245), ("therefore", 0x2234),
    ("perpendicular", 0x22A5), ("radicalex", 0x203E), ("theta1", 0x3D1), ("sigma1", 0x3C2), ("phi1", 0x3D5), ("omega1", 0x3D6),
    ("Upsilon1", 0x3D2), ("similar", 0x223C), ("minute", 0x2032), ("second", 0x2033), ("proportional", 0x221D),
    ("equivalence", 0x2261), ("approxequal", 0x2248), ("arrowvertex", 0x23D0), ("arrowhorizex", 0x23AF), ("carriagereturn", 0x21B5),
    ("circlemultiply", 0x2297), ("circleplus", 0x2295), ("emptyset", 0x2205), ("intersection", 0x2229), ("union", 0x222A),
    ("propersuperset", 0x2283), ("reflexsuperset", 0x2287), ("notsubset", 0x2284), ("propersubset", 0x2282),
    ("reflexsubset", 0x2286), ("element", 0x2208), ("notelement", 0x2209), ("angle", 0x2220), ("gradient", 0x2207),
    ("registerserif", 0xAE), ("copyrightserif", 0xA9), ("trademarkserif", 0x2122), ("registersans", 0xAE), ("copyrightsans", 0xA9),
    ("trademarksans", 0x2122), ("product", 0x220F), ("radical", 0x221A), ("dotmath", 0x22C5), ("logicaland", 0x2227),
    ("logicalor", 0x2228), ("arrowdblboth", 0x21D4), ("arrowdblleft", 0x21D0), ("arrowdblup", 0x21D1), ("arrowdblright", 0x21D2),
    ("arrowdbldown", 0x21D3), ("angleleft", 0x2329), ("angleright", 0x232A), ("summation", 0x2211), ("integral", 0x222B),
    ("integraltp", 0x2320), ("integralbt", 0x2321), ("integralex", 0x23AE), ("parenlefttp", 0x239B), ("parenleftex", 0x239C),
    ("parenleftbt", 0x239D), ("parenrighttp", 0x239E), ("parenrightex", 0x239F), ("parenrightbt", 0x23A0),
    ("bracketlefttp", 0x23A1), ("bracketleftex", 0x23A2), ("bracketleftbt", 0x23A3), ("bracketrighttp", 0x23A4),
    ("bracketrightex", 0x23A5), ("bracketrightbt", 0x23A6), ("bracelefttp", 0x23A7), ("braceleftmid", 0x23A8),
    ("braceleftbt", 0x23A9), ("braceex", 0x23AA), ("bracerighttp", 0x23AB), ("bracerightmid", 0x23AC), ("bracerightbt", 0x23AD),
    ("club", 0x2663), ("diamond", 0x2666), ("heart", 0x2665), ("spade", 0x2660), ("lozenge", 0x25CA), ("apple", 0xF8FF),
    ("partialdiff", 0x2202), ("infinity", 0x221E), ("notequal", 0x2260), ("lessequal", 0x2264), ("greaterequal", 0x2265),
    ("florin", 0x192), ("fraction", 0x2044), ("bullet", 0x2022), ("ellipsis", 0x2026), ("arrowleft", 0x2190), ("arrowup", 0x2191),
    ("arrowright", 0x2192), ("arrowdown", 0x2193), ("arrowboth", 0x2194), ("degree", 0xB0), ("Tcommaaccent", 0x162),
    ("tcommaaccent", 0x163), ("Scommaaccent", 0x218), ("scommaaccent", 0x219), ("commaaccent", 0xF6C3),
})
    names[n] = u;
w.VarInt(names.Count);
foreach (var (n, u) in names)
{
    w.Str(n);
    w.VarInt(u);
}

w.VarInt(faces.Count);
long total = 0;
foreach (var (name, file, chars) in faces)
{
    var font = TrueTypeFont.Parse(File.ReadAllBytes(file)) ?? throw new Exception("Cannot parse " + file);
    var cmap = font.GetCmap(3, 1)!;
    // Glyph 0 (.notdef) first, then the glyphs for the selected characters (and the components they reference).
    var gids = new List<int> { 0 };
    var newIndex = new Dictionary<int, int> { [0] = 0 };
    var map = new SortedDictionary<int, int>();
    foreach (int c in chars.OrderBy(c => c))
    {
        if (!cmap.TryGetValue(c, out int g) || g == 0) continue;
        if (!newIndex.TryGetValue(g, out int ni))
        {
            ni = gids.Count;
            newIndex[g] = ni;
            gids.Add(g);
        }
        map[c] = ni;
    }
    w.Str(name);
    w.VarInt(font.UnitsPerEm);
    w.ZigZag(font.Ascender);
    w.ZigZag(font.Descender);
    w.VarInt(gids.Count);
    w.VarInt(map.Count);
    int prev = 0;
    foreach (var (u, g) in map)
    {
        w.VarInt(u - prev);
        w.VarInt(g);
        prev = u;
    }
    foreach (int g in gids)
    {
        double adv = font.Advance(g);
        w.VarInt((int)Math.Max(0, adv));
        var contours = new List<List<(int X, int Y, bool On)>>();
        font.TryGetRawContours(g, contours);
        contours.RemoveAll(c => c.Count < 2);
        w.VarInt(contours.Count);
        int x = 0, y = 0;
        foreach (var c in contours)
        {
            w.VarInt(c.Count);
            var flags = new byte[(c.Count + 7) / 8];
            for (int k = 0; k < c.Count; k++) if (c[k].On) flags[k / 8] |= (byte)(1 << (k % 8));
            ms.Write(flags);
            foreach (var p in c)
            {
                w.ZigZag(p.X - x);
                w.ZigZag(p.Y - y);
                x = p.X;
                y = p.Y;
            }
        }
    }
    Console.WriteLine($"{name,-16} {map.Count,4} chars {gids.Count,4} glyphs  {ms.Length - total,7} bytes");
    total = ms.Length;
}

var raw = ms.ToArray();
using (var outFile = File.Create(output))
using (var z = new DeflateStream(outFile, CompressionLevel.SmallestSize))
    z.Write(raw);
Console.WriteLine($"{names.Count} glyph names; {raw.Length} bytes raw, {new FileInfo(output).Length} bytes compressed -> {output}");

sealed class Writer
{
    private readonly Stream _s;
    public Writer(Stream s) => _s = s;

    public void VarInt(int v)
    {
        uint u = (uint)v;
        while (u >= 0x80)
        {
            _s.WriteByte((byte)(u | 0x80));
            u >>= 7;
        }
        _s.WriteByte((byte)u);
    }

    public void ZigZag(int v) => VarInt((v << 1) ^ (v >> 31));

    public void Str(string s)
    {
        var b = System.Text.Encoding.Latin1.GetBytes(s);
        _s.WriteByte((byte)b.Length);
        _s.Write(b);
    }
}
