using System.Runtime.CompilerServices;
using PdfPecker.Fonts;

namespace PdfPecker.Rendering.Fonts;

/// <summary>
/// The glyph outlines of a font that HTML to PDF uses, read with the renderer's font readers. Each font gets one reader,
/// which keeps every outline it reads, so the ink of text decorations and the drawing of colour glyphs read the font's
/// tables and outlines once between them, and conversions running in parallel share them.
/// </summary>
internal static class FontProgramOutlines
{
    private static readonly ConditionalWeakTable<FontProgram, StrongBox<IGlyphSource?>> Sources = new();

    /// <summary>The font's outline reader, or null when the renderer cannot read the font.</summary>
    public static IGlyphSource? Source(FontProgram program) => Sources.GetValue(program, p => new StrongBox<IGlyphSource?>(Read(p))).Value;

    private static IGlyphSource? Read(FontProgram program)
    {
        try
        {
            var tables = program.Tables.Select(t => (TagName(t.Tag), t.Offset, t.Length));
            var trueType = TrueTypeFont.Parse(program.Data, tables);
            return trueType?.CffTable is { } cff ? CffFont.Parse(cff) : trueType;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException or InvalidOperationException)
        {
            SwallowedErrors.Record(ex);
            return null;
        }
    }

    private static string TagName(uint tag) => new([(char)(tag >> 24), (char)(tag >> 16 & 0xFF), (char)(tag >> 8 & 0xFF), (char)(tag & 0xFF)]);
}
