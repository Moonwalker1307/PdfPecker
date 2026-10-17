using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering.Fonts;

/// <summary>Fonts without a usable embedded program: a built-in face chosen from the name and the descriptor flags.</summary>
internal sealed partial class PdfFont
{
    private const int BoldWeightThreshold = 600;
    private const double ItalicAngleThreshold = 1;

    // How far a substituted glyph may be stretched to match the document's width.
    private const double MinHScale = 0.6, MaxHScale = 1.4;

    /// <summary>Which built-in face stands in for a font, and whether the standard 14 metrics apply to it.</summary>
    private readonly record struct Substitute14(string FaceName, int[]? BuiltinUnicode, bool Standard14);

    /// <summary>Uses a built-in face for a font without a usable embedded program.</summary>
    private void Substitute(PdfFile file, PdfDict dict, string?[]? baseEncoding, string?[]? differences)
    {
        Kind = FontKind.Simple;
        Array.Fill(_hScale, 1f);
        var descriptor = file.GetDict(dict, "FontDescriptor");
        var (faceName, builtin, standard14) = ChooseFace(file, descriptor, BaseFont);
        var face = StandardFontPack.Face(faceName) ?? StandardFontPack.Face("Sans");
        Source = face;
        if (face == null) return;
        _substUnicode = new int[256];

        var encoding = baseEncoding ?? (builtin != null ? null : Encodings.Standard);
        bool haveWidths = file.GetArray(dict, "Widths") != null;
        double toThousand = GlyphUnitsPerTextUnit / face.UnitsPerEm;
        for (int c = 0; c < 256; c++)
        {
            int u = CharacterOf(c, differences?[c] ?? encoding?[c], differences?[c] != null, encoding == null, builtin);
            int gid = u >= 0 ? face.Lookup(u) : 0;
            _substUnicode[c] = u;
            _codeToGid[c] = gid;
            double advance = face.Advance(gid) * toThousand;
            if (!haveWidths)
            {
                // Standard 14 metrics: the Adobe AFM widths where the character is in WinAnsi, else the face's own advance.
                double width = standard14 && builtin == null && u >= 0 ? AfmWidth(faceName, u) : -1;
                _widths[c] = width >= 0 ? width : Math.Max(0, advance);
            }
            else if (!standard14 && advance > 0 && _widths[c] > 0)
            {
                _hScale[c] = (float)Math.Clamp(_widths[c] / advance, MinHScale, MaxHScale);
            }
        }
    }

    // The character a code stands for: its glyph name (the Dingbats names through that face's own table), else the
    // symbol face's built-in encoding, else the code itself when there is no encoding at all.
    private static int CharacterOf(int code, string? glyphName, bool fromDifferences, bool noEncoding, int[]? builtin)
    {
        int u = -1;
        if (glyphName != null && builtin == Encodings.DingbatsUnicode && Encodings.DingbatsNames.TryGetValue(glyphName, out int dingbat)) u = builtin[dingbat];
        else if (glyphName != null) u = Encodings.NameToUnicode(glyphName);
        if (u < 0 && builtin != null && !fromDifferences) u = builtin[code];
        if (u < 0 && glyphName == null && noEncoding) u = code;
        return u;
    }

    private static Substitute14 ChooseFace(PdfFile file, PdfDict? descriptor, string name)
    {
        int flags = file.GetInt(descriptor, "Flags", 0);
        string lower = name.ToLowerInvariant();
        if (lower.Contains("symbol") && !lower.Contains("wingding")) return new Substitute14("Symbol", Encodings.SymbolUnicode, lower == "symbol");
        if (lower.Contains("dingbat")) return new Substitute14("Dingbats", Encodings.DingbatsUnicode, true);

        bool mono = lower.Contains("courier") || lower.Contains("mono") || lower.Contains("consol") || (flags & FixedPitchFlag) != 0;
        bool serif = !mono && (lower.Contains("times") || lower.Contains("serif") && !lower.Contains("sans") || lower.Contains("roman") ||
            lower.Contains("georgia") || lower.Contains("garamond") || lower.Contains("cambria") || lower.Contains("book") ||
            lower.Contains("palatino") || lower.Contains("minion") || lower.Contains("century") ||
            ((flags & SerifFlag) != 0 && !lower.Contains("arial") && !lower.Contains("helvetica") && !lower.Contains("sans")));
        bool bold = lower.Contains("bold") || lower.Contains("black") || lower.Contains("heavy") || lower.Contains("semibold") ||
            lower.Contains("demi") || (flags & ForceBoldFlag) != 0 || file.GetNumber(descriptor, "FontWeight", 400) >= BoldWeightThreshold;
        bool italic = lower.Contains("italic") || lower.Contains("oblique") || (flags & ItalicFlag) != 0 || Math.Abs(file.GetNumber(descriptor, "ItalicAngle", 0)) > ItalicAngleThreshold;
        bool standard14 = lower.StartsWith("helvetica", StringComparison.Ordinal) || lower.StartsWith("times", StringComparison.Ordinal) ||
            lower.StartsWith("courier", StringComparison.Ordinal) || lower.StartsWith("arial", StringComparison.Ordinal) ||
            lower.StartsWith("liberation", StringComparison.Ordinal);
        return new Substitute14(FaceName(mono, serif, bold, italic), null, standard14);
    }

    private static string FaceName(bool mono, bool serif, bool bold, bool italic)
        => (mono ? "Mono" : serif ? "Serif" : "Sans") + (bold && italic ? "-BoldItalic" : bold ? "-Bold" : italic ? "-Italic" : "");

    // The Adobe AFM width of a standard 14 character, or -1 when it is outside WinAnsi.
    private static double AfmWidth(string faceName, int unicode)
    {
        if (unicode > 0xFFFF) return -1;
        byte code = PdfPecker.Fonts.WinAnsi.Encode((char)unicode);
        if (code == (byte)'?' && unicode != '?') return -1;
        var family = faceName.StartsWith("Mono", StringComparison.Ordinal) ? PdfPecker.Fonts.FontFamily.Courier
            : faceName.StartsWith("Serif", StringComparison.Ordinal) ? PdfPecker.Fonts.FontFamily.Times : PdfPecker.Fonts.FontFamily.Helvetica;
        bool bold = faceName.Contains("Bold"), italic = faceName.Contains("Italic");
        var font = PdfPecker.Fonts.StandardFont.Get(family, bold, italic);
        return font.CharWidth((char)unicode, 1000);
    }
}
