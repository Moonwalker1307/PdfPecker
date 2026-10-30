using System.Text;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering.Fonts;

/// <summary>
/// Text extraction: what each code stands for. ToUnicode wins; then the glyph names of the encoding; then the
/// characters a substituted face was looked up by; then the embedded program's own cmap or glyph names.
/// </summary>
internal sealed partial class PdfFont
{
    private CMap? _toUnicode;
    private readonly string?[] _encNames = new string?[256];
    private int[]? _substUnicode;
    private bool _unicodeCodes; // a predefined Uni...-UCS2 or -UTF16 CMap: the codes are UTF-16 code units
    private Lazy<Dictionary<int, int>?>? _gidToUnicode;

    private void LoadUnicode(PdfFile file, PdfDict dict)
    {
        try
        {
            if (file.GetStream(dict, "ToUnicode") is { } stream) _toUnicode = CMap.Parse(file.DecodeStream(stream));
        }
        catch (Exception ex) when (ex is PdfFormatException or InvalidDataException or NotSupportedException or IndexOutOfRangeException)
        {
            // A damaged ToUnicode map only loses the text, not the drawing; the other sources still apply.
            SwallowedErrors.Record(ex);
        }
        var source = Source;
        _gidToUnicode = new Lazy<Dictionary<int, int>?>(() => ReverseCmap(source), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The glyph-to-character map of an embedded TrueType program, from its Unicode cmap.</summary>
    private static Dictionary<int, int>? ReverseCmap(IGlyphSource? source)
    {
        if (source is not TrueTypeFont trueType) return null;
        var map = trueType.GetCmap(3, 10) ?? trueType.GetCmap(3, 1) ?? trueType.GetCmap(0, 4) ?? trueType.GetCmap(0, 3);
        if (map == null) return null;
        var reverse = new Dictionary<int, int>();
        foreach (var (u, gid) in map)
        {
            // Prefer the lowest code point for a glyph that several characters share (such as a space and a no-break space).
            if (gid > 0 && (!reverse.TryGetValue(gid, out int have) || u < have)) reverse[gid] = u;
        }
        return reverse;
    }

    /// <summary>The text a character code stands for, or null when the font does not say.</summary>
    public string? Unicode(int code)
    {
        // An empty mapping is deliberate: the extra glyphs of a shaped cluster stand for no characters of their own, and
        // falling back to the font's cmap would repeat a character the cluster's first glyph already gives.
        if (_toUnicode?.ToUnicode((uint)code) is { } mapped) return mapped;
        if (Kind == FontKind.Cid)
        {
            if (_unicodeCodes) return code is > 0 and <= 0xFFFF && !char.IsSurrogate((char)code) ? ((char)code).ToString() : null;
            if (Source is CffFont { IsCid: false } cff && cff.GlyphName(GlyphId(code)) is { } cidName) return NameToText(cidName);
            return GlyphText(GlyphId(code));
        }
        int c = code & 255;
        if (_encNames[c] is { } name && NameToText(name) is { } named) return named;
        if (_substUnicode != null && _substUnicode[c] > 0) return FromCodePoint(_substUnicode[c]);
        if (Kind == FontKind.Simple) return GlyphText(_codeToGid[c]);
        // Type 3 glyphs are often named freely ("a12", "tri"); their codes, like TeX's bitmap fonts, are then usually
        // the ASCII letters themselves, the guess other extractors make too.
        return c is > 0x20 and < 0x7F ? ((char)c).ToString() : null;
    }

    private string? GlyphText(int gid)
        => gid > 0 && _gidToUnicode?.Value is { } map && map.TryGetValue(gid, out int u) ? FromCodePoint(u) : null;

    /// <summary>The text of a glyph name: an AGL or uniXXXX name, or a ligature name such as "f_f_i" (suffixes such as ".sc" ignored).</summary>
    internal static string? NameToText(string name)
    {
        int u = Encodings.NameToUnicode(name);
        if (u >= 0) return FromCodePoint(u);
        int dot = name.IndexOf('.');
        var core = dot > 0 ? name.Substring(0, dot) : name;
        if (core.IndexOf('_') <= 0) return null;
        var text = new StringBuilder();
        foreach (var part in core.Split('_'))
        {
            int partCodePoint = Encodings.NameToUnicode(part);
            if (partCodePoint < 0 || FromCodePoint(partCodePoint) is not { } partText) return null;
            text.Append(partText);
        }
        return text.ToString();
    }

    private static string? FromCodePoint(int u)
        => u is > 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF) ? char.ConvertFromUtf32(u) : null;

    /// <summary>The advance of the space character for word gap detection, in text space for size 1, or 0 when unknown.</summary>
    public double SpaceWidth => Kind != FontKind.Cid && _widths[SpaceCode] > 0 ? Width(SpaceCode) : 0;
}
