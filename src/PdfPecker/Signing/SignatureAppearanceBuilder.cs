using PdfPecker.Pdf;
using PdfPecker.Rendering.Fonts;
using PdfPecker.Rendering.Graphics;

namespace PdfPecker.Signing;

/// <summary>
/// Draws the visible signature box. Text is drawn as glyph outlines from the built-in font pack rather than with a font,
/// so the appearance needs no font resources and is valid in PDF/A files, where every font must be embedded.
/// </summary>
internal static class SignatureAppearanceBuilder
{
    private const float Padding = 4, LineSpacing = 1.25f, MaxFontSize = 11;

    public static byte[] Content(float width, float height, string[] lines, bool border)
    {
        var b = new ByteBuffer(4096);
        if (border && width > 1 && height > 1)
            b.Ascii("0.35 0.35 0.35 RG 0.75 w 0.375 0.375 ").Num(width - 0.75f).Sp().Num(height - 0.75f).Ascii(" re S\n");

        var face = StandardFontPack.Face("Sans");
        if (face == null || lines.Length == 0) return b.Span.ToArray();
        double em = face.UnitsPerEm;

        // Widths at 1pt.
        var widths = new double[lines.Length];
        for (int i = 0; i < lines.Length; i++)
            foreach (int cp in CodePoints(lines[i])) widths[i] += Math.Max(0, face.Advance(Glyph(face, cp))) / em;

        float size = Math.Min(MaxFontSize, (height - 2 * Padding) / (lines.Length * LineSpacing));
        foreach (var lw in widths)
            if (lw > 0) size = (float)Math.Min(size, (width - 2 * Padding) / lw);
        if (size <= 0.5f) return b.Span.ToArray();

        double scale = size / em;
        float lineHeight = size * LineSpacing;
        float blockHeight = lineHeight * lines.Length;
        double baseline = height - (height - blockHeight) / 2 - size * 0.95;
        b.Ascii("0.1 0.1 0.1 rg\n");
        foreach (var line in lines)
        {
            double pen = Padding;
            bool any = false;
            foreach (int cp in CodePoints(line))
            {
                int gid = Glyph(face, cp);
                var path = face.GetGlyph(gid);
                if (path != null && !path.IsEmpty)
                {
                    AppendPath(b, path, pen, baseline, scale);
                    any = true;
                }
                pen += Math.Max(0, face.Advance(gid)) * scale;
            }
            if (any) b.Ascii("f\n");
            baseline -= lineHeight;
        }
        return b.Span.ToArray();
    }

    private static int Glyph(PackFace face, int cp)
    {
        if (cp == '\t') cp = ' ';
        int g = face.Lookup(cp);
        return g != 0 || cp == ' ' ? g : face.Lookup('?');
    }

    private static IEnumerable<int> CodePoints(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                yield return char.ConvertToUtf32(s[i], s[i + 1]);
                i++;
            }
            else yield return s[i];
        }
    }

    private static void AppendPath(ByteBuffer b, PathData path, double dx, double dy, double scale)
    {
        int p = 0;
        var pts = path.Points;
        void Pt(int i) => b.Num((float)(dx + pts[i] * scale)).Sp().Num((float)(dy + pts[i + 1] * scale)).Sp();
        foreach (var verb in path.Verbs)
        {
            switch (verb)
            {
                case PathData.MoveTo: Pt(p); b.Ascii("m\n"); p += 2; break;
                case PathData.LineTo: Pt(p); b.Ascii("l\n"); p += 2; break;
                case PathData.CubicTo: Pt(p); Pt(p + 2); Pt(p + 4); b.Ascii("c\n"); p += 6; break;
                case PathData.Close: b.Ascii("h\n"); break;
            }
        }
    }
}
