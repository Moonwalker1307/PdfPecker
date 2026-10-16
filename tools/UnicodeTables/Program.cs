using System.Globalization;
using System.Text;

// Generates src/PdfPecker/Text/UnicodeTables.g.cs from the Unicode Character Database.
// Usage: dotnet run --project tools/UnicodeTables -- <ucd-folder> [out]
//
// The folder needs DerivedBidiClass.txt, BidiMirroring.txt, BidiBrackets.txt, ArabicShaping.txt, UnicodeData.txt,
// LineBreak.txt and EastAsianWidth.txt of one Unicode version, for example from
// https://www.unicode.org/Public/16.0.0/ucd/ (DerivedBidiClass.txt is in extracted/).
if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/UnicodeTables -- <ucd-folder> [out]");
    return 1;
}
string ucd = args[0];
string output = args.Length > 1 ? args[1] : Path.Combine("src", "PdfPecker", "Text", "UnicodeTables.g.cs");

const string UcdVersion = "16.0.0";
string[] bidi = ["L", "R", "AL", "EN", "ES", "ET", "AN", "CS", "NSM", "BN", "B", "S", "WS", "ON",
    "LRE", "LRO", "RLE", "RLO", "PDF", "LRI", "RLI", "FSI", "PDI"];
// Joining types as the shaper uses them: C (join causing) behaves as D; Syriac Alaph and Dalath/Rish get their own.
var joiningTypes = new Dictionary<string, int> { ["U"] = 0, ["L"] = 1, ["R"] = 2, ["D"] = 3, ["C"] = 3, ["T"] = 4 };
const int Alaph = 5, DalathRish = 6;
var formNames = new Dictionary<string, int> { ["<isolated>"] = 0, ["<final>"] = 1, ["<initial>"] = 2, ["<medial>"] = 3 };

// Bidi classes, as run-length ranges over all code points (unlisted ones are L).
var classes = new byte[0x110000];
foreach (var f in Lines("DerivedBidiClass.txt"))
{
    var (lo, hi) = CodeRange(f[0]);
    int v = Array.IndexOf(bidi, f[1]);
    if (v < 0) throw new InvalidDataException("Unknown bidi class " + f[1]);
    classes.AsSpan(lo, hi - lo + 1).Fill((byte)v);
}
var starts = new List<int>();
var values = new List<int>();
for (int cp = 0; cp < 0x110000; cp++)
{
    if (cp == 0 || classes[cp] != classes[cp - 1])
    {
        starts.Add(cp);
        values.Add(classes[cp]);
    }
}

var mirrors = Lines("BidiMirroring.txt").Select(f => (Hex(f[0]), Hex(f[1]))).Order().ToList();
var brackets = Lines("BidiBrackets.txt").Select(f => (Hex(f[0]), Hex(f[1]), f[2])).OrderBy(b => b, Comparer<(int, int, string)>.Create(
    (a, b) => a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : a.Item2 != b.Item2 ? a.Item2.CompareTo(b.Item2) : string.CompareOrdinal(a.Item3, b.Item3))).ToList();

var joining = new SortedDictionary<int, int>();
foreach (var f in Lines("ArabicShaping.txt"))
{
    int t = joiningTypes[f[2]];
    if (f[3] == "ALAPH") t = Alaph;
    else if (f[3] == "DALATH RISH") t = DalathRish;
    joining[Hex(f[0])] = t;
}
var joinRanges = Ranges(joining);

var forms = new List<(int, int)>();
var lamAlef = new List<(int, int)>();
var ccc = new SortedDictionary<int, int>();
var decomp = new List<(int, int, int)>();
foreach (var f in Lines("UnicodeData.txt"))
{
    int cp = Hex(f[0]);
    int combining = int.Parse(f[3], CultureInfo.InvariantCulture);
    if (combining != 0) ccc[cp] = combining;
    if (f[5].Length > 0 && !f[5].StartsWith('<') && (cp is >= 0x0590 and <= 0x08FF || cp is >= 0xFB1D and <= 0xFB4F))
    {
        var d = f[5].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Hex).ToArray();
        decomp.Add((cp, d[0], d.Length > 1 ? d[1] : 0));
    }
    var parts = f[5].Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0 || !formNames.TryGetValue(parts[0], out int form) || cp is < 0xFB50 or > 0xFEFF) continue;
    var chars = parts.Skip(1).Select(Hex).ToArray();
    if (chars.Length == 1) forms.Add((chars[0] << 2 | form, cp));
    else if (chars.Length == 2 && chars[0] == 0x0644) lamAlef.Add((chars[1] << 2 | form, cp));
}
forms.Sort();
lamAlef.Sort();
var cccRanges = Ranges(ccc);

// Line_Break classes, reduced to the ones the line breaker tells apart (see LineBreakClass in UnicodeTables.cs), with
// 0x80 set for East Asian wide and fullwidth characters. The breaker only looks for opportunities next to those (and
// Hangul jamo), so text in other scripts keeps breaking at spaces and hyphens alone.
string[] lineBreak = ["XX", "NU", "PR", "PO", "IS", "SY", "HY", "BA", "BB", "B2", "GL", "WJ", "ZW", "CM", "ZWJ", "QU",
    "EX", "IN", "OP", "CL", "CP", "NS", "CJ", "ID", "H2", "H3", "JL", "JV", "JT"];
// Emoji bases break like ideographs, and emoji modifiers stay with their base like combining marks.
var lineBreakAliases = new Dictionary<string, string> { ["EB"] = "ID", ["EM"] = "CM" };
var lb = new byte[0x110000];
foreach (var f in Lines("LineBreak.txt"))
{
    var (lo, hi) = CodeRange(f[0]);
    int v = Array.IndexOf(lineBreak, lineBreakAliases.GetValueOrDefault(f[1], f[1]));
    lb.AsSpan(lo, hi - lo + 1).Fill((byte)Math.Max(v, 0)); // the other classes (AL, SA, SP, ...) are XX here
}
foreach (var f in Lines("EastAsianWidth.txt"))
{
    if (f[1] is not ("W" or "F")) continue;
    var (lo, hi) = CodeRange(f[0]);
    for (int cp = lo; cp <= hi; cp++) lb[cp] |= 0x80;
}
var lbStarts = new List<int>();
var lbValues = new List<int>();
for (int cp = 0; cp < 0x110000; cp++)
{
    if (cp == 0 || lb[cp] != lb[cp - 1])
    {
        lbStarts.Add(cp);
        lbValues.Add(lb[cp]);
    }
}

var o = new StringBuilder();
o.Append($$"""
    // <auto-generated>
    // Generated by tools/UnicodeTables from the Unicode Character Database {{UcdVersion}}
    // (DerivedBidiClass, BidiMirroring, BidiBrackets, ArabicShaping, UnicodeData, LineBreak, EastAsianWidth).
    // Do not edit by hand.
    // </auto-generated>
    namespace PdfPecker.Text;

    internal static partial class UnicodeTables
    {
        internal const string Version = "{{UcdVersion}}";

        // Bidi_Class (BidiClass values) of every code point: BidiClassValues[k] from BidiClassStarts[k] up to the next start.

    """.ReplaceLineEndings("\n"));
Ints("BidiClassStarts", starts);
Ints("BidiClassValues", values, 32, "byte");
o.Append("\n    // Bidi_Mirroring_Glyph pairs: code point, mirrored code point, sorted by the first.\n");
Ints("MirrorPairs", mirrors.SelectMany(p => new[] { p.Item1, p.Item2 }));
o.Append("\n    // Bidi_Paired_Bracket: code point, its pair, 1 for an opening bracket and 0 for a closing one.\n");
Ints("BracketPairs", brackets.SelectMany(b => new[] { b.Item1, b.Item2, b.Item3 == "o" ? 1 : 0 }), 15);
o.Append("\n    // Joining_Type of the characters ArabicShaping lists: first, last, type (see JoiningType).\n");
Ints("JoiningRanges", joinRanges.SelectMany(r => r), 15);
o.Append("\n    // Canonical_Combining_Class of the characters that have one: first, last, class.\n");
Ints("CombiningClassRanges", cccRanges.SelectMany(r => r), 15);
o.Append("\n    // Canonical decompositions of Hebrew and Arabic letters: code point, first, second (0 for a singleton).\n");
Ints("Decompositions", decomp.SelectMany(d => new[] { d.Item1, d.Item2, d.Item3 }), 15);
o.Append("\n    // Arabic presentation forms: (letter << 2 | form) and the presentation form, sorted; form 0 isolated,\n");
o.Append("    // 1 final, 2 initial, 3 medial.\n");
Ints("PresentationForms", forms.SelectMany(p => new[] { p.Item1, p.Item2 }));
o.Append("\n    // Lam-alef ligatures: (alef << 2 | form) and the ligature, for lam followed by that alef.\n");
Ints("LamAlefForms", lamAlef.SelectMany(p => new[] { p.Item1, p.Item2 }));
o.Append("\n    // Line_Break (LineBreakClass values, 0x80 for East Asian wide and fullwidth) of every code point:\n");
o.Append("    // LineBreakValues[k] from LineBreakStarts[k] up to the next start.\n");
Ints("LineBreakStarts", lbStarts);
Ints("LineBreakValues", lbValues, 32, "byte");
o.Append("}\n");
File.WriteAllText(output, o.ToString(), new UTF8Encoding(false));
Console.WriteLine($"{starts.Count} bidi ranges, {mirrors.Count} mirror pairs, {brackets.Count} brackets, " +
    $"{joinRanges.Count} joining ranges, {cccRanges.Count} ccc ranges, {forms.Count} forms, {lamAlef.Count} lam-alef, {lbStarts.Count} line break ranges");
return 0;

// The data lines of a UCD file, without comments, split into trimmed fields.
IEnumerable<string[]> Lines(string name)
{
    foreach (var raw in File.ReadLines(Path.Combine(ucd, name), Encoding.UTF8))
    {
        int hash = raw.IndexOf('#');
        var line = (hash >= 0 ? raw[..hash] : raw).Trim();
        if (line.Length > 0) yield return line.Split(';').Select(p => p.Trim()).ToArray();
    }
}

static int Hex(string s) => int.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

static (int, int) CodeRange(string s)
{
    int dots = s.IndexOf("..", StringComparison.Ordinal);
    return dots >= 0 ? (Hex(s[..dots]), Hex(s[(dots + 2)..])) : (Hex(s), Hex(s));
}

// Merges consecutive code points with the same value into first, last, value ranges.
static List<int[]> Ranges(SortedDictionary<int, int> map)
{
    var ranges = new List<int[]>();
    foreach (var (cp, v) in map)
    {
        if (ranges.Count > 0 && ranges[^1][1] == cp - 1 && ranges[^1][2] == v) ranges[^1][1] = cp;
        else ranges.Add([cp, cp, v]);
    }
    return ranges;
}

void Ints(string name, IEnumerable<int> data, int perLine = 16, string type = "int")
{
    o.Append($"    internal static ReadOnlySpan<{type}> {name} => [\n");
    foreach (var chunk in data.Chunk(perLine))
        o.Append("        ").AppendJoin(", ", chunk.Select(v => $"0x{v:X}")).Append(",\n");
    o.Append("    ];\n");
}
