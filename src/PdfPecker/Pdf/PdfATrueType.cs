using System.Buffers.Binary;
using PdfPecker.Fonts;
using PdfPecker.Rendering.Fonts;

namespace PdfPecker.Pdf;

/// <summary>
/// Rewrites an embedded TrueType program so that it agrees with its font dictionary as PDF/A requires: every code the
/// text shows selects a glyph the program has (6.2.11.4.1, 6.2.11.8), at the width the dictionary gives it (6.2.11.5),
/// through a cmap whose lookup is unambiguous (6.2.11.6). The glyphs viewers drew stay the ones they draw: the codes
/// keep the glyphs PdfPecker's renderer resolves them to.
/// </summary>
internal static class PdfATrueType
{
    /// <summary>
    /// One code (a character code of a simple font, or a CID): the glyph it selects now (0 when the program has none for
    /// it) and the advance it should have in font units, or -1 when its width does not matter.
    /// </summary>
    internal readonly record struct Use(int Key, int Gid, int Advance);

    // Tables whose size or content follows the glyph count or the glyphs' order, which the rewrite changes, and the
    // signature, which no longer matches.
    private static readonly HashSet<string> DroppedTables = new(StringComparer.Ordinal) { "hdmx", "LTSH", "VDMX", "vhea", "vmtx", "DSIG", "post" };

    // Composite glyph flags: word arguments that are x and y offsets.
    private const ushort ArgsAreWords = 0x0001, ArgsAreXyValues = 0x0002;

    private const int MaxGlyphs = 65535;

    /// <summary>
    /// The rewritten program and the glyph each key selects in it, or null when the program cannot be rewritten (it has
    /// no glyf table, or would need more glyphs than TrueType allows). With <paramref name="symbolCmap"/> the program
    /// gets a (3,0) cmap instead of its own that maps each key and each key plus 0xF000, as a symbolic simple font needs;
    /// a CID font maps through its CIDToGIDMap instead and keeps its cmap.
    /// </summary>
    public static (byte[] Program, Dictionary<int, int> Gids)? Rewrite(TrueTypeFont font, IReadOnlyList<Use> uses, bool symbolCmap)
    {
        if (!font.HasGlyf || font.TableBytes("head").Length < 54 || font.TableBytes("hhea").Length < 36 || font.TableBytes("maxp").Length < 6) return null;
        int count = font.GlyphCount;
        var advances = new List<int>(count + 8);
        var lsbs = new List<int>(count + 8);
        for (int g = 0; g < count; g++)
        {
            advances.Add(Math.Max(0, (int)font.Advance(g)));
            lsbs.Add(font.LeftSideBearing(g));
        }
        var added = new List<(int Source, int Advance)>(); // Source -1: an empty glyph
        var gids = new Dictionary<int, int>();
        var widthOf = new Dictionary<int, int>(); // the advance given to each original glyph so far
        var duplicates = new Dictionary<(int Gid, int Advance), int>();
        foreach (var (key, gid, advance) in uses)
        {
            if (gids.ContainsKey(key)) continue;
            int target;
            if (gid <= 0 || gid >= count)
            {
                // A code with no glyph gets an empty one: viewers drew the .notdef glyph, usually nothing or a box.
                int wanted = Math.Max(0, advance);
                if (!duplicates.TryGetValue((-1, wanted), out target))
                {
                    target = count + added.Count;
                    added.Add((-1, wanted));
                    duplicates[(-1, wanted)] = target;
                }
            }
            else if (advance < 0 || advance == advances[gid] || widthOf.TryGetValue(gid, out int given) && given == advance)
            {
                target = gid;
            }
            else if (!widthOf.ContainsKey(gid))
            {
                widthOf[gid] = advance;
                advances[gid] = advance;
                target = gid;
            }
            else if (!duplicates.TryGetValue((gid, advance), out target))
            {
                // The same glyph at another width: a composite glyph that draws it, with its own advance.
                target = count + added.Count;
                added.Add((gid, advance));
                duplicates[(gid, advance)] = target;
            }
            if (advance >= 0 && target < count) widthOf.TryAdd(target, advance);
            gids[key] = target;
        }
        int total = count + added.Count;
        if (total > MaxGlyphs) return null;

        // glyf and loca: the original glyphs as they are, then the added ones.
        var glyf = new MemoryStream();
        var loca = new byte[(total + 1) * 4];
        int maxComponents = 0;
        for (int g = 0; g < total; g++)
        {
            BinaryPrimitives.WriteInt32BigEndian(loca.AsSpan(g * 4), (int)glyf.Length);
            if (g < count)
            {
                glyf.Write(font.GlyphBytes(g));
            }
            else
            {
                var (source, advance) = added[g - count];
                advances.Add(advance);
                var bytes = source >= 0 ? font.GlyphBytes(source) : ReadOnlySpan<byte>.Empty;
                lsbs.Add(source >= 0 ? lsbs[source] : 0);
                if (bytes.Length >= 10)
                {
                    var composite = new byte[18];
                    BinaryPrimitives.WriteInt16BigEndian(composite, -1);
                    bytes.Slice(2, 8).CopyTo(composite.AsSpan(2)); // the bounding box
                    BinaryPrimitives.WriteUInt16BigEndian(composite.AsSpan(10), ArgsAreWords | ArgsAreXyValues);
                    BinaryPrimitives.WriteUInt16BigEndian(composite.AsSpan(12), (ushort)source);
                    glyf.Write(composite);
                    maxComponents = 1;
                }
            }
            while ((glyf.Length & 3) != 0) glyf.WriteByte(0);
        }
        BinaryPrimitives.WriteInt32BigEndian(loca.AsSpan(total * 4), (int)glyf.Length);

        var hmtx = new byte[total * 4];
        for (int g = 0; g < total; g++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(hmtx.AsSpan(g * 4), (ushort)Math.Clamp(advances[g], 0, ushort.MaxValue));
            BinaryPrimitives.WriteInt16BigEndian(hmtx.AsSpan(g * 4 + 2), (short)lsbs[g]);
        }

        var head = font.TableBytes("head").ToArray();
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), 1); // long loca offsets
        var hhea = font.TableBytes("hhea").ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(hhea.AsSpan(10), (ushort)Math.Clamp(advances.Max(), 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16BigEndian(hhea.AsSpan(34), (ushort)total);
        var maxp = font.TableBytes("maxp").ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(4), (ushort)total);
        if (maxp.Length >= 32 && maxComponents > 0)
        {
            // maxComponentElements and maxComponentDepth must allow the one-component glyphs added.
            if (BinaryPrimitives.ReadUInt16BigEndian(maxp.AsSpan(28)) < 1) BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(28), 1);
            if (BinaryPrimitives.ReadUInt16BigEndian(maxp.AsSpan(30)) < 1) BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(30), 1);
        }

        var tables = new List<(uint Tag, byte[] Data)>
        {
            (Tag("head"), head),
            (Tag("hhea"), hhea),
            (Tag("maxp"), maxp),
            (Tag("hmtx"), hmtx),
            (Tag("loca"), loca),
            (Tag("glyf"), glyf.Length == 0 ? new byte[4] : glyf.ToArray()),
            (Tag("post"), Post(font.TableBytes("post"))),
        };
        if (symbolCmap) tables.Add((Tag("cmap"), PdfASubstituteFont.Cmap(gids.Where(g => g.Key is >= 0 and <= 255).Select(g => (g.Key, g.Value)).ToList(), symbol: true)));
        foreach (var tag in font.TableTags)
        {
            if (tag.Length != 4 || DroppedTables.Contains(tag) || tag == "cmap" && symbolCmap || tables.Any(t => t.Tag == Tag(tag))) continue;
            tables.Add((Tag(tag), font.TableBytes(tag).ToArray()));
        }
        return (SfntBuilder.Build(0x00010000, tables), gids);
    }

    private static uint Tag(string tag) => (uint)(tag[0] << 24 | tag[1] << 16 | tag[2] << 8 | tag[3]);

    /// <summary>A version 3 post table (no glyph names, which would no longer match the glyphs) with the original's metrics.</summary>
    private static byte[] Post(ReadOnlySpan<byte> original)
    {
        var t = new byte[32];
        if (original.Length >= 16) original[..16].CopyTo(t);
        BinaryPrimitives.WriteUInt32BigEndian(t, 0x00030000);
        return t;
    }
}
