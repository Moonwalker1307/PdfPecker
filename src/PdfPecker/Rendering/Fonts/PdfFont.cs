using PdfPecker.Rendering.Graphics;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering.Fonts;

internal enum FontKind : byte { Simple, Cid, Type3 }

/// <summary>
/// A font resource prepared for rendering: splits strings into codes and maps each code to a width and a glyph.
/// Embedded TrueType, CFF (Type1C/CIDFontType0C/OpenType) and Type 1 programs are used directly; other fonts are
/// substituted with the built-in font pack.
/// </summary>
internal sealed partial class PdfFont
{
    // The font descriptor's Flags.
    private const int FixedPitchFlag = 1, SerifFlag = 2, SymbolicFlag = 4, NonsymbolicFlag = 32, ItalicFlag = 64, ForceBoldFlag = 1 << 18;

    /// <summary>Glyph space of simple fonts and CID fonts is a thousandth of text space.</summary>
    private const double GlyphUnitsPerTextUnit = 1000;

    private const int SpaceCode = 32;

    // A BaseFont of a subset font starts with six capitals and a plus.
    private const int SubsetTagLength = 6;

    public FontKind Kind { get; private set; }
    public IGlyphSource? Source { get; private set; }
    public bool Vertical { get; private set; }
    public string BaseFont { get; private set; } = "";

    // Simple fonts: by code.
    private readonly int[] _codeToGid = new int[256];
    private readonly double[] _widths = new double[256];
    private readonly float[] _hScale = new float[256];

    // CID fonts: by CID.
    private CMap? _cmap;
    private int[]? _cidToGid;
    private readonly Dictionary<int, double> _cidWidths = new();
    private double _defaultWidth = 1000;
    private double _vDefaultY = 880, _vDefaultW1 = -1000;
    private readonly Dictionary<int, (double W1, double Vx, double Vy)> _vMetrics = new();
    private CffFont? _cidCff;
    private Func<int, int>? _cidFallbackGid;

    // Type 3.
    public Matrix FontMatrix { get; private set; } = Matrix.Scale(0.001, 0.001);
    public PdfStream?[]? Type3Procs { get; private set; }
    public PdfDict? Type3Resources { get; private set; }

    /// <summary>
    /// The height of the font's glyphs in text space units, for text extraction: 1 for most fonts, but a Type 3 font
    /// whose glyphs are drawn at another scale (such as a FontMatrix of [1 0 0 1 0 0] with glyphs 72 units high, used at
    /// 0.24 points) has its own, so its letters are not taken for words.
    /// </summary>
    public double TextSizeScale { get; private set; } = 1;

    public static PdfFont Load(PdfFile file, PdfDict dict)
    {
        var font = new PdfFont();
        var subtype = file.GetName(dict, "Subtype");
        font.BaseFont = StripSubset(file.GetName(dict, "BaseFont") ?? "");
        try
        {
            switch (subtype)
            {
                case "Type0": font.LoadType0(file, dict); break;
                case "Type3": font.LoadType3(file, dict); break;
                default: font.LoadSimple(file, dict, subtype); break;
            }
        }
        catch (Exception ex) when (ex is PdfFormatException or IndexOutOfRangeException or ArgumentException or NotSupportedException or InvalidDataException)
        {
            // Unusable font program: fall back to substitution.
            SwallowedErrors.Record(ex);
            if (font.Kind != FontKind.Type3)
            {
                font.Kind = FontKind.Simple;
                font.Substitute(file, dict, null, null);
            }
        }
        font.LoadUnicode(file, dict);
        return font;
    }

    private static string StripSubset(string name)
    {
        if (name.Length <= SubsetTagLength + 1 || name[SubsetTagLength] != '+') return name;
        for (int i = 0; i < SubsetTagLength; i++)
        {
            if (name[i] is < 'A' or > 'Z') return name;
        }
        return name.Substring(SubsetTagLength + 1);
    }

    /// <summary>The (code, name) pairs of a Differences array: each number restarts the code, each name takes the next.</summary>
    private static IEnumerable<(int Code, string Name)> Differences(PdfFile file, PdfArray differences)
    {
        int code = 0;
        foreach (var item in differences.Items)
        {
            var o = file.Resolve(item);
            if (o is PdfNumber n) code = n.IntValue;
            else if (o is PdfName name) yield return (code++, name.Value);
        }
    }

    // ------------------------------------------------------------------ per-code access

    /// <summary>Reads the next code. <paramref name="isSpace"/> is set for the single-byte code 32 (for word spacing).</summary>
    public int NextCode(ReadOnlySpan<byte> s, ref int pos, out bool isSpace)
    {
        if (Kind == FontKind.Cid)
        {
            uint code = _cmap!.NextCode(s, ref pos, out int length);
            isSpace = length == 1 && code == SpaceCode;
            return (int)code;
        }
        int c = s[pos++];
        isSpace = c == SpaceCode;
        return c;
    }

    /// <summary>Horizontal advance in text space units for a font size of 1.</summary>
    public double Width(int code)
    {
        if (Kind == FontKind.Cid)
        {
            int cid = _cmap!.ToCid((uint)code);
            return (_cidWidths.TryGetValue(cid, out double w) ? w : _defaultWidth) / GlyphUnitsPerTextUnit;
        }
        if (Kind == FontKind.Type3) return _widths[code & 255] * FontMatrix.A;
        return _widths[code & 255] / GlyphUnitsPerTextUnit;
    }

    /// <summary>Vertical-mode metrics (text space, size 1): advance w1 and the position vector v.</summary>
    public (double W1, double Vx, double Vy) VerticalMetrics(int code)
    {
        int cid = Kind == FontKind.Cid ? _cmap!.ToCid((uint)code) : code;
        if (_vMetrics.TryGetValue(cid, out var m)) return (m.W1 / GlyphUnitsPerTextUnit, m.Vx / GlyphUnitsPerTextUnit, m.Vy / GlyphUnitsPerTextUnit);
        // Without metrics the glyph hangs from the top of the em, centred on its horizontal advance.
        return (_vDefaultW1 / GlyphUnitsPerTextUnit, Width(code) / 2, _vDefaultY / GlyphUnitsPerTextUnit);
    }

    public int GlyphId(int code)
    {
        if (Kind == FontKind.Cid)
        {
            int cid = _cmap!.ToCid((uint)code);
            if (_cidFallbackGid != null) return _cidFallbackGid(code);
            if (_cidCff != null) return _cidCff.GidForCid(cid);
            if (_cidToGid != null) return cid < _cidToGid.Length ? _cidToGid[cid] : 0;
            return cid;
        }
        return _codeToGid[code & 255];
    }

    /// <summary>Horizontal glyph stretch applied to substituted fonts so they keep the document's widths.</summary>
    public float HScale(int code) => Kind == FontKind.Simple ? _hScale[code & 255] : 1f;

    public Matrix GlyphMatrix => Source?.FontMatrix ?? FontMatrix;

    // ------------------------------------------------------------------ Type 3

    private void LoadType3(PdfFile file, PdfDict dict)
    {
        Kind = FontKind.Type3;
        FontMatrix = Matrix.FromArray(file.GetNumbers(dict, "FontMatrix") ?? new double[] { 0.001, 0, 0, 0.001, 0, 0 });
        Type3Resources = file.GetDict(dict, "Resources");
        // The nominal size holds for glyphs about one text unit high; only a font far off that uses its bounding box.
        if (file.GetNumbers(dict, "FontBBox") is { Length: 4 } bbox)
        {
            double height = Math.Abs(bbox[3] - bbox[1]) * Math.Sqrt(FontMatrix.C * FontMatrix.C + FontMatrix.D * FontMatrix.D);
            if (double.IsFinite(height) && height > 0 && (height < 0.5 || height > 2)) TextSizeScale = height;
        }
        int first = file.GetInt(dict, "FirstChar", 0);
        var widths = file.GetNumbers(dict, "Widths");
        for (int c = 0; c < 256; c++)
        {
            int i = c - first;
            _widths[c] = widths != null && i >= 0 && i < widths.Length ? widths[i] : 0;
        }
        var procs = file.GetDict(dict, "CharProcs");
        Type3Procs = new PdfStream?[256];
        var encoding = file.GetDict(dict, "Encoding");
        if (procs == null || encoding == null || file.GetArray(encoding, "Differences") is not { } differences) return;
        foreach (var (code, name) in Differences(file, differences))
        {
            if (code < 0 || code >= 256) continue;
            Type3Procs[code] = file.Resolve(procs[name]) as PdfStream;
            _encNames[code] = name;
        }
    }
}
