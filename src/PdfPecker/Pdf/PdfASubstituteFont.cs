using System.Buffers.Binary;
using System.Text;
using PdfPecker.Fonts;
using PdfPecker.Rendering.Fonts;

namespace PdfPecker.Pdf;

/// <summary>
/// Builds the TrueType program that stands in for a font a PDF uses without embedding it, from the glyphs of the face
/// PdfPecker's renderer draws it with. Each glyph is one the document shows, at the document's own width, so the
/// program agrees with the font dictionary as PDF/A requires (6.2.11.5) and looks as PdfPecker renders the original.
/// </summary>
internal static class PdfASubstituteFont
{
    /// <summary>
    /// One glyph of the program: <paramref name="SourceGid"/> in the face (or -1 for an empty glyph), its advance in the
    /// face's units and the horizontal scale its outline is drawn with.
    /// </summary>
    internal readonly record struct Glyph(int SourceGid, int Advance, double XScale);

    /// <summary>
    /// The program and its bounding box in the face's units. Glyph 0 is an empty .notdef that nothing shows; the glyphs
    /// given follow it. <paramref name="cmap"/> maps character codes to glyph ids, written as a (3,0) symbol subtable
    /// for a simple font (both the codes and the codes plus 0xF000, as viewers look up either) or a (3,1) Unicode
    /// subtable for a CID font.
    /// </summary>
    public static (byte[] Program, int XMin, int YMin, int XMax, int YMax) Build(PackFace face, IReadOnlyList<Glyph> glyphs, string postScriptName,
        IReadOnlyList<(int Code, int Gid)> cmap, bool symbol)
    {
        var all = new List<Glyph>(glyphs.Count + 1) { new(-1, 0, 1) };
        all.AddRange(glyphs);
        var glyf = new MemoryStream();
        var loca = new int[all.Count + 1];
        var hmtx = new byte[all.Count * 4];
        int xMin = int.MaxValue, yMin = int.MaxValue, xMax = int.MinValue, yMax = int.MinValue;
        int maxPoints = 0, maxContours = 0, advanceMax = 0, minLsb = int.MaxValue, minRsb = int.MaxValue, maxExtent = 0;
        var xs = new List<int>();
        var ys = new List<int>();
        var on = new List<bool>();
        var ends = new List<int>();
        for (int g = 0; g < all.Count; g++)
        {
            loca[g] = (int)glyf.Length;
            var glyph = all[g];
            int advance = Math.Clamp(glyph.Advance, 0, ushort.MaxValue);
            int lsb = 0;
            if (glyph.SourceGid >= 0 && face.Outline(glyph.SourceGid, xs, ys, on, ends) && ends.Count > 0)
            {
                for (int i = 0; i < xs.Count; i++) xs[i] = (int)Math.Round(xs[i] * glyph.XScale, MidpointRounding.AwayFromZero);
                var (gxMin, gyMin, gxMax, gyMax) = WriteGlyph(glyf, xs, ys, on, ends);
                lsb = gxMin;
                xMin = Math.Min(xMin, gxMin);
                yMin = Math.Min(yMin, gyMin);
                xMax = Math.Max(xMax, gxMax);
                yMax = Math.Max(yMax, gyMax);
                maxPoints = Math.Max(maxPoints, xs.Count);
                maxContours = Math.Max(maxContours, ends.Count);
                minLsb = Math.Min(minLsb, gxMin);
                minRsb = Math.Min(minRsb, advance - gxMax);
                maxExtent = Math.Max(maxExtent, gxMax);
            }
            advanceMax = Math.Max(advanceMax, advance);
            BinaryPrimitives.WriteUInt16BigEndian(hmtx.AsSpan(g * 4), (ushort)advance);
            BinaryPrimitives.WriteInt16BigEndian(hmtx.AsSpan(g * 4 + 2), (short)lsb);
        }
        loca[all.Count] = (int)glyf.Length;
        if (xMin > xMax) xMin = yMin = xMax = yMax = 0;
        if (minLsb == int.MaxValue) minLsb = minRsb = 0;

        var locaBytes = new byte[loca.Length * 4];
        for (int i = 0; i < loca.Length; i++) BinaryPrimitives.WriteInt32BigEndian(locaBytes.AsSpan(i * 4), loca[i]);

        var tables = new List<(uint Tag, byte[] Data)>
        {
            (Tag("head"), Head(face.UnitsPerEm, xMin, yMin, xMax, yMax)),
            (Tag("hhea"), Hhea(face, all.Count, advanceMax, minLsb, minRsb, maxExtent)),
            (Tag("maxp"), Maxp(all.Count, maxPoints, maxContours)),
            (Tag("hmtx"), hmtx),
            (Tag("loca"), locaBytes),
            (Tag("glyf"), glyf.Length == 0 ? new byte[4] : glyf.ToArray()),
            (Tag("cmap"), Cmap(cmap, symbol)),
            (Tag("name"), Name(postScriptName)),
            (Tag("post"), Post()),
        };
        return (SfntBuilder.Build(0x00010000, tables), xMin, yMin, xMax, yMax);
    }

    private static uint Tag(string tag) => (uint)(tag[0] << 24 | tag[1] << 16 | tag[2] << 8 | tag[3]);

    // Simple glyph flags.
    private const byte OnCurve = 1, XShort = 2, YShort = 4, XSameOrPositive = 16, YSameOrPositive = 32;

    /// <summary>Writes a simple glyph (no instructions), padded to an even length; returns its bounding box.</summary>
    private static (int XMin, int YMin, int XMax, int YMax) WriteGlyph(MemoryStream glyf, List<int> xs, List<int> ys, List<bool> on, List<int> ends)
    {
        int xMin = xs.Min(), yMin = ys.Min(), xMax = xs.Max(), yMax = ys.Max();
        var b = new List<byte>(16 + xs.Count * 5);
        void Short(int v)
        {
            b.Add((byte)(v >> 8));
            b.Add((byte)v);
        }
        Short(ends.Count);
        Short(xMin);
        Short(yMin);
        Short(xMax);
        Short(yMax);
        foreach (int end in ends) Short(end);
        Short(0); // no instructions
        var flags = new byte[xs.Count];
        int px = 0, py = 0;
        for (int i = 0; i < xs.Count; i++)
        {
            int dx = xs[i] - px, dy = ys[i] - py;
            byte f = on[i] ? OnCurve : (byte)0;
            if (dx == 0) f |= XSameOrPositive;
            else if (Math.Abs(dx) < 256) f |= (byte)(XShort | (dx > 0 ? XSameOrPositive : 0));
            if (dy == 0) f |= YSameOrPositive;
            else if (Math.Abs(dy) < 256) f |= (byte)(YShort | (dy > 0 ? YSameOrPositive : 0));
            flags[i] = f;
            px = xs[i];
            py = ys[i];
        }
        b.AddRange(flags);
        px = 0;
        for (int i = 0; i < xs.Count; i++)
        {
            int dx = xs[i] - px;
            px = xs[i];
            if ((flags[i] & XShort) != 0) b.Add((byte)Math.Abs(dx));
            else if (dx != 0) Short(dx);
        }
        py = 0;
        for (int i = 0; i < ys.Count; i++)
        {
            int dy = ys[i] - py;
            py = ys[i];
            if ((flags[i] & YShort) != 0) b.Add((byte)Math.Abs(dy));
            else if (dy != 0) Short(dy);
        }
        if ((b.Count & 1) != 0) b.Add(0);
        glyf.Write(b.ToArray());
        return (xMin, yMin, xMax, yMax);
    }

    private static byte[] Head(int unitsPerEm, int xMin, int yMin, int xMax, int yMax)
    {
        var t = new byte[54];
        var s = t.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(s, 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(s[4..], 0x00010000); // font revision 1.0
        BinaryPrimitives.WriteUInt32BigEndian(s[12..], 0x5F0F3CF5);
        BinaryPrimitives.WriteUInt16BigEndian(s[16..], 3); // baseline at y 0, left side bearing at x 0
        BinaryPrimitives.WriteUInt16BigEndian(s[18..], (ushort)unitsPerEm);
        // The created and modified dates stay zero so that the same input gives the same file.
        BinaryPrimitives.WriteInt16BigEndian(s[36..], (short)xMin);
        BinaryPrimitives.WriteInt16BigEndian(s[38..], (short)yMin);
        BinaryPrimitives.WriteInt16BigEndian(s[40..], (short)xMax);
        BinaryPrimitives.WriteInt16BigEndian(s[42..], (short)yMax);
        BinaryPrimitives.WriteUInt16BigEndian(s[46..], 8); // lowest readable size
        BinaryPrimitives.WriteInt16BigEndian(s[48..], 2); // font direction hint
        BinaryPrimitives.WriteInt16BigEndian(s[50..], 1); // long loca offsets
        return t;
    }

    private static byte[] Hhea(PackFace face, int glyphs, int advanceMax, int minLsb, int minRsb, int maxExtent)
    {
        var t = new byte[36];
        var s = t.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(s, 0x00010000);
        BinaryPrimitives.WriteInt16BigEndian(s[4..], (short)face.Ascent);
        BinaryPrimitives.WriteInt16BigEndian(s[6..], (short)face.Descent);
        BinaryPrimitives.WriteUInt16BigEndian(s[10..], (ushort)advanceMax);
        BinaryPrimitives.WriteInt16BigEndian(s[12..], (short)minLsb);
        BinaryPrimitives.WriteInt16BigEndian(s[14..], (short)minRsb);
        BinaryPrimitives.WriteInt16BigEndian(s[16..], (short)maxExtent);
        BinaryPrimitives.WriteInt16BigEndian(s[18..], 1); // caret slope: vertical
        BinaryPrimitives.WriteUInt16BigEndian(s[34..], (ushort)glyphs);
        return t;
    }

    private static byte[] Maxp(int glyphs, int maxPoints, int maxContours)
    {
        var t = new byte[32];
        var s = t.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(s, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(s[4..], (ushort)glyphs);
        BinaryPrimitives.WriteUInt16BigEndian(s[6..], (ushort)maxPoints);
        BinaryPrimitives.WriteUInt16BigEndian(s[8..], (ushort)maxContours);
        BinaryPrimitives.WriteUInt16BigEndian(s[14..], 2); // zones
        return t;
    }

    /// <summary>
    /// A cmap table with one format 4 subtable: (3,0) with each code and the code plus 0xF000 for a symbolic font, else
    /// (3,1) with each Unicode character in the Basic Multilingual Plane.
    /// </summary>
    internal static byte[] Cmap(IReadOnlyList<(int Code, int Gid)> map, bool symbol)
    {
        var pairs = new SortedDictionary<int, int>();
        foreach (var (code, gid) in map)
        {
            if (symbol)
            {
                pairs[code] = gid;
                pairs[0xF000 + code] = gid;
            }
            else if (code is > 0 and < 0xFFFF) pairs[code] = gid;
        }
        // Runs of consecutive characters with consecutive glyphs share a segment.
        var segments = new List<(int Start, int End, int Delta)>();
        foreach (var (c, g) in pairs)
        {
            if (segments.Count > 0 && segments[^1].End == c - 1 && segments[^1].Delta == g - c)
                segments[^1] = (segments[^1].Start, c, segments[^1].Delta);
            else segments.Add((c, c, g - c));
        }
        segments.Add((0xFFFF, 0xFFFF, 1));
        int n = segments.Count;
        int length = 16 + 8 * n;
        var t = new byte[12 + length];
        var s = t.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(s[2..], 1); // one subtable
        BinaryPrimitives.WriteUInt16BigEndian(s[4..], 3);
        BinaryPrimitives.WriteUInt16BigEndian(s[6..], (ushort)(symbol ? 0 : 1));
        BinaryPrimitives.WriteUInt32BigEndian(s[8..], 12);
        var f = s[12..];
        BinaryPrimitives.WriteUInt16BigEndian(f, 4);
        BinaryPrimitives.WriteUInt16BigEndian(f[2..], (ushort)length);
        BinaryPrimitives.WriteUInt16BigEndian(f[6..], (ushort)(2 * n));
        int power = 1, log = 0;
        while (power * 2 <= n)
        {
            power *= 2;
            log++;
        }
        BinaryPrimitives.WriteUInt16BigEndian(f[8..], (ushort)(2 * power));
        BinaryPrimitives.WriteUInt16BigEndian(f[10..], (ushort)log);
        BinaryPrimitives.WriteUInt16BigEndian(f[12..], (ushort)(2 * n - 2 * power));
        int ends = 14, starts = 16 + 2 * n, deltas = 16 + 4 * n, offsets = 16 + 6 * n;
        for (int i = 0; i < n; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(f[(ends + 2 * i)..], (ushort)segments[i].End);
            BinaryPrimitives.WriteUInt16BigEndian(f[(starts + 2 * i)..], (ushort)segments[i].Start);
            BinaryPrimitives.WriteUInt16BigEndian(f[(deltas + 2 * i)..], (ushort)(segments[i].Delta & 0xFFFF));
            BinaryPrimitives.WriteUInt16BigEndian(f[(offsets + 2 * i)..], 0);
        }
        return t;
    }

    /// <summary>A name table with the family, style, full and PostScript names (Windows, US English).</summary>
    private static byte[] Name(string postScriptName)
    {
        var records = new (int Id, string Text)[] { (1, postScriptName), (2, "Regular"), (4, postScriptName), (6, postScriptName) };
        var strings = new MemoryStream();
        var t = new MemoryStream();
        void Short(int v)
        {
            t.WriteByte((byte)(v >> 8));
            t.WriteByte((byte)v);
        }
        Short(0);
        Short(records.Length);
        Short(6 + 12 * records.Length);
        foreach (var (id, text) in records)
        {
            var bytes = Encoding.BigEndianUnicode.GetBytes(text);
            Short(3);
            Short(1);
            Short(0x409);
            Short(id);
            Short(bytes.Length);
            Short((int)strings.Length);
            strings.Write(bytes);
        }
        strings.WriteTo(t);
        return t.ToArray();
    }

    /// <summary>A version 3 post table: no glyph names, which TrueType fonts in PDF do not need.</summary>
    private static byte[] Post()
    {
        var t = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(t, 0x00030000);
        return t;
    }
}
