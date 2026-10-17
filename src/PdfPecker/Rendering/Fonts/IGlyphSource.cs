using PdfPecker.Rendering.Graphics;

namespace PdfPecker.Rendering.Fonts;

/// <summary>A source of glyph outlines in glyph space (y up).</summary>
internal interface IGlyphSource
{
    /// <summary>Scale from glyph units to text space (1/unitsPerEm for TrueType, the FontMatrix for CFF/Type 1).</summary>
    Matrix FontMatrix { get; }

    int GlyphCount { get; }

    PathData? GetGlyph(int gid);

    /// <summary>Advance width in glyph units, or -1 if unknown.</summary>
    double Advance(int gid);
}
