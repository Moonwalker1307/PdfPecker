using System.Buffers.Binary;

namespace PdfPecker.Fonts;

/// <summary>
/// Glyphs of one font program used by a document. New glyph ids are handed out in order of first use; they are the
/// CIDs written in content streams and, for TrueType, the glyph ids of the subset font.
/// </summary>
internal sealed class GlyphSubset
{
    public readonly FontProgram Program;
    public readonly List<int> Glyphs = new() { 0 };   // new id -> original glyph id (0 = .notdef stays 0)
    public readonly List<int> Unicode = new() { 0 };  // new id -> first code point that produced it (0 = none)
    private readonly Dictionary<int, int> _byCodePoint = new();
    private readonly Dictionary<int, int> _byGlyph = new() { [0] = 0 };
    private Dictionary<int, string>? _texts;          // new id -> the characters of a shaped glyph ("" for none)

    public GlyphSubset(FontProgram program) => Program = program;

    /// <summary>The CID for a code point. CFF fonts keep their glyph ids when subset, so their CIDs are the original glyph ids.</summary>
    public int Cid(int codePoint)
    {
        if (_byCodePoint.TryGetValue(codePoint, out int id)) return id;
        int gid = Program.GlyphId(codePoint);
        if (!_byGlyph.TryGetValue(gid, out id))
        {
            id = Program.IsCff ? gid : Glyphs.Count;
            _byGlyph[gid] = id;
            Glyphs.Add(gid);
            Unicode.Add(codePoint);
        }
        else if (gid != 0 && !HasText(id))
        {
            // The glyph was first drawn as an extra glyph of a shaped cluster, which carries no characters of its own;
            // its own character is the better mapping.
            int index = Index(id);
            Unicode[index] = codePoint;
            _texts?.Remove(index);
        }
        _byCodePoint[codePoint] = id;
        return id;
    }

    /// <summary>
    /// The CID for glyph k of shaped text: a ligature glyph maps back to all of its characters. Its neighbours tell
    /// whether shaping split its cluster into several glyphs.
    /// </summary>
    public int Cid(List<ShapedGlyph> glyphs, int k, string text)
    {
        var g = glyphs[k];
        // A glyph for one character that is the character's own glyph (not a contextual form or a mirror) is mapped by
        // the character.
        if (g.Length == 1 && Program.GlyphId(text[g.Start]) == g.Gid) return Cid(text[g.Start]);
        if (g.Length == 2 && char.IsHighSurrogate(text[g.Start]) && char.ConvertToUtf32(text[g.Start], text[g.Start + 1]) is int cp
            && Program.GlyphId(cp) == g.Gid)
            return Cid(cp);
        // PDF/A level U and PDF/UA require every drawn glyph to map to Unicode, shaped glyphs included, so a glyph maps
        // to the characters of its cluster. When shaping split a cluster into several glyphs, the extra glyphs (Length 0)
        // map to nothing, because the first glyph already carries the cluster's text and repeating it would double the
        // characters in extracted text. A split character with a canonical decomposition, such as alef with hamza drawn
        // as alef and a separate hamza, maps part by part instead: the alef glyph is shared with plain alef, and a CMap
        // maps each code once, so the whole character on it would turn every plain alef into alef with hamza.
        string mapped;
        bool split = (k > 0 && glyphs[k - 1].Start == g.Start && glyphs[k - 1].Length == 0)
            || (k + 1 < glyphs.Count && glyphs[k + 1].Start == g.Start && glyphs[k + 1].Length == 0);
        if (g.Length == 0)
            mapped = g.Start < text.Length && Text.UnicodeTables.Decompose(text[g.Start], out _, out int second) && second != 0
                ? char.ConvertFromUtf32(second) : "";
        else if (split && g.Length == 1 && Text.UnicodeTables.Decompose(text[g.Start], out int first, out int rest) && rest != 0)
            mapped = char.ConvertFromUtf32(first);
        else mapped = text.Substring(g.Start, g.Length);
        // A glyph id reused for other characters keeps its first mapping.
        if (_byGlyph.TryGetValue(g.Gid, out int id))
        {
            if (id != 0 && mapped.Length > 0 && !HasText(id)) (_texts ??= new())[Index(id)] = mapped;
            return id;
        }
        id = Program.IsCff ? g.Gid : Glyphs.Count;
        _byGlyph[g.Gid] = id;
        Glyphs.Add(g.Gid);
        Unicode.Add(0);
        (_texts ??= new())[Glyphs.Count - 1] = mapped;
        return id;
    }

    // The index into Glyphs and Unicode of a CID: CFF subsets keep the original glyph ids as CIDs.
    private int Index(int id) => Program.IsCff ? Glyphs.IndexOf(id) : id;

    // True when the CID maps to at least one character.
    private bool HasText(int id)
    {
        int index = Index(id);
        return Unicode[index] != 0 || (_texts != null && _texts.TryGetValue(index, out var t) && t.Length > 0);
    }

    /// <summary>
    /// (CID, original glyph id, code point, characters of a shaped glyph) for every glyph used, in CID order. The code
    /// point is 0 for a shaped glyph, whose characters are empty when it is an extra glyph of its cluster.
    /// </summary>
    public IEnumerable<(int Cid, int Gid, int CodePoint, string? Text)> Used()
    {
        var list = new List<(int, int, int, string?)>(Glyphs.Count);
        for (int i = 0; i < Glyphs.Count; i++)
            list.Add((Program.IsCff ? Glyphs[i] : i, Glyphs[i], Unicode[i], Unicode[i] == 0 && _texts != null && _texts.TryGetValue(i, out var t) ? t : null));
        list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return list;
    }

    /// <summary>A six-letter subset tag derived from the glyph set, so that identical input gives identical output.</summary>
    public string Tag()
    {
        uint h = 2166136261;
        foreach (int g in Glyphs) h = (h ^ (uint)g) * 16777619;
        foreach (char c in Program.PostScriptName) h = (h ^ c) * 16777619;
        var tag = new char[6];
        for (int i = 0; i < 6; i++) { tag[i] = (char)('A' + h % 26); h /= 26; }
        return new string(tag);
    }
}

/// <summary>Builds the font file embedded in the PDF: a TrueType subset with only the used glyphs, or a whole CFF-based OpenType font.</summary>
internal static class FontSubsetter
{
    private static readonly string[] CopiedTrueTypeTables = { "cvt ", "fpgm", "prep", "OS/2" };
    private static readonly string[] CopiedCffTables = { "OS/2", "head", "hhea", "hmtx", "maxp", "VORG" };

    public static byte[] Build(GlyphSubset subset)
        => subset.Program.IsCff ? BuildOpenType(subset) : BuildTrueType(subset);

    /// <summary>
    /// An OpenType file with PostScript outlines: the CFF table reduced to the used glyphs (glyph ids unchanged) and
    /// the tables a PDF reader needs. CFF2 (variable) fonts are embedded whole.
    /// </summary>
    private static byte[] BuildOpenType(GlyphSubset subset)
    {
        var font = subset.Program;
        if (font.Table("CFF ") is not { } cff)
        {
            if (font.Data.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(font.Data) == 0x4F54544F) return font.Data;
            var all = font.Tables.Select(t => (t.Tag, font.Data.AsSpan(t.Offset, t.Length).ToArray())).ToList();
            return SfntBuilder.Build(0x4F54544F, all);
        }
        var cffData = font.Data.AsSpan(cff.Offset, cff.Length).ToArray();
        var tables = new List<(uint, byte[])> { (FontProgram.Tag("CFF "), CffSubsetter.Subset(cffData, subset.Glyphs) ?? cffData) };
        foreach (var tag in CopiedCffTables)
            if (font.Table(tag) is { } t) tables.Add((FontProgram.Tag(tag), font.Data.AsSpan(t.Offset, t.Length).ToArray()));
        var post = new byte[32];
        if (font.Table("post") is { Length: >= 32 } p) Buffer.BlockCopy(font.Data, p.Offset, post, 0, 32);
        BinaryPrimitives.WriteUInt32BigEndian(post, 0x00030000);
        tables.Add((FontProgram.Tag("post"), post));
        return SfntBuilder.Build(0x4F54544F, tables);
    }

    private static byte[] BuildTrueType(GlyphSubset subset)
    {
        var font = subset.Program;
        var d = font.Data;
        var head = font.Table("head")!.Value;
        var loca = font.Table("loca")!.Value;
        var glyf = font.Table("glyf")!.Value;
        bool longLoca = font.S16(head.Offset + 50) != 0;
        int locaCount = longLoca ? loca.Length / 4 : loca.Length / 2;

        (int Start, int Length) GlyphRange(int gid)
        {
            if (gid < 0 || gid + 1 >= locaCount) return (0, 0);
            int a, b;
            if (longLoca) { a = (int)font.U32(loca.Offset + gid * 4); b = (int)font.U32(loca.Offset + gid * 4 + 4); }
            else { a = font.U16(loca.Offset + gid * 2) * 2; b = font.U16(loca.Offset + gid * 2 + 2) * 2; }
            if (a < 0 || b <= a || b > glyf.Length) return (0, 0);
            return (glyf.Offset + a, b - a);
        }

        // Composite glyphs pull in their components; they get new ids after the glyphs used directly.
        var glyphs = new List<int>(subset.Glyphs);
        var newId = new Dictionary<int, int>(glyphs.Count * 2);
        for (int i = 0; i < glyphs.Count; i++) newId.TryAdd(glyphs[i], i);
        for (int i = 0; i < glyphs.Count; i++)
        {
            var (start, len) = GlyphRange(glyphs[i]);
            if (len < 10 || font.S16(start) >= 0) continue;
            foreach (int pos in ComponentIndexOffsets(d, start, len))
            {
                int component = font.U16(pos);
                if (component >= font.NumGlyphs || newId.ContainsKey(component)) continue;
                newId[component] = glyphs.Count;
                glyphs.Add(component);
            }
        }

        // glyf and loca (long format).
        int total = 0;
        foreach (int g in glyphs) total += (GlyphRange(g).Length + 3) & ~3;
        var newGlyf = new byte[Math.Max(total, 4)];
        var newLoca = new byte[(glyphs.Count + 1) * 4];
        int at = 0;
        for (int i = 0; i < glyphs.Count; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(i * 4), (uint)at);
            var (start, len) = GlyphRange(glyphs[i]);
            if (len == 0) continue;
            Buffer.BlockCopy(d, start, newGlyf, at, len);
            if (len >= 10 && font.S16(start) < 0)
            {
                foreach (int pos in ComponentIndexOffsets(d, start, len))
                {
                    int component = font.U16(pos);
                    int mapped = newId.TryGetValue(component, out int m) ? m : 0;
                    BinaryPrimitives.WriteUInt16BigEndian(newGlyf.AsSpan(at + pos - start), (ushort)mapped);
                }
            }
            at += (len + 3) & ~3;
        }
        BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(glyphs.Count * 4), (uint)at);

        // hmtx with one full metric per glyph.
        var hmtx = new byte[glyphs.Count * 4];
        for (int i = 0; i < glyphs.Count; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(hmtx.AsSpan(i * 4), (ushort)font.Advance(glyphs[i]));
            BinaryPrimitives.WriteInt16BigEndian(hmtx.AsSpan(i * 4 + 2), font.LeftSideBearing(glyphs[i]));
        }

        var newHead = Copy(font, "head");
        BinaryPrimitives.WriteInt16BigEndian(newHead.AsSpan(50), 1);
        var hhea = Copy(font, "hhea");
        BinaryPrimitives.WriteUInt16BigEndian(hhea.AsSpan(34), (ushort)glyphs.Count);
        var maxp = Copy(font, "maxp");
        BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(4), (ushort)glyphs.Count);
        var post = new byte[32];
        if (font.Table("post") is { Length: >= 32 } p) Buffer.BlockCopy(d, p.Offset, post, 0, 32);
        BinaryPrimitives.WriteUInt32BigEndian(post, 0x00030000); // no glyph names

        var tables = new List<(uint, byte[])>
        {
            (FontProgram.Tag("head"), newHead), (FontProgram.Tag("hhea"), hhea), (FontProgram.Tag("maxp"), maxp),
            (FontProgram.Tag("hmtx"), hmtx), (FontProgram.Tag("loca"), newLoca), (FontProgram.Tag("glyf"), newGlyf),
            (FontProgram.Tag("post"), post),
        };
        foreach (var tag in CopiedTrueTypeTables)
            if (font.Table(tag) is { } t) tables.Add((FontProgram.Tag(tag), d.AsSpan(t.Offset, t.Length).ToArray()));
        return SfntBuilder.Build(0x00010000, tables);
    }

    private static byte[] Copy(FontProgram font, string tag)
    {
        var t = font.Table(tag)!.Value;
        return font.Data.AsSpan(t.Offset, t.Length).ToArray();
    }

    /// <summary>Absolute offsets of the glyph index of every component of a composite glyph.</summary>
    private static List<int> ComponentIndexOffsets(byte[] d, int start, int length)
    {
        var result = new List<int>(4);
        int p = start + 10, end = start + length;
        while (p + 4 <= end)
        {
            int flags = d[p] << 8 | d[p + 1];
            result.Add(p + 2);
            p += 4;
            p += (flags & 0x0001) != 0 ? 4 : 2;     // ARG_1_AND_2_ARE_WORDS
            if ((flags & 0x0008) != 0) p += 2;       // WE_HAVE_A_SCALE
            else if ((flags & 0x0040) != 0) p += 4;  // WE_HAVE_AN_X_AND_Y_SCALE
            else if ((flags & 0x0080) != 0) p += 8;  // WE_HAVE_A_TWO_BY_TWO
            if ((flags & 0x0020) == 0) break;        // MORE_COMPONENTS
        }
        return result;
    }
}
