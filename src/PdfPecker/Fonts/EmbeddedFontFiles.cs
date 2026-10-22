using System.IO.Compression;
using System.Runtime.InteropServices;

namespace PdfPecker.Fonts;

/// <summary>
/// Compressed font files of the latest subsets of each font, kept with the font and so shared between the conversions
/// that use it. Documents made from one template draw the same glyphs of the same fonts, so the subset is built and
/// compressed once instead of for every document, which was most of the cost of embedding fonts. The file depends only
/// on the font data, the glyph ids in subset order and the compression level, so those are the key and the output is
/// the same either way. A font keeps a few entries, so a stream of different documents costs no more than the cap.
/// </summary>
internal static class EmbeddedFontFiles
{
    private const int EntriesPerFont = 4;
    // A subset this large (thousands of CJK glyphs) is built every time rather than kept with its font.
    private const int MaxKeptLength = 2 << 20;
    // The writer stores a stream this short as it is: compressing it would not make it shorter.
    private const int MinCompressedLength = 65;

    /// <summary>A font file as the writer embeds it: compressed with zlib (Deflated) or, when tiny, as it is.</summary>
    internal readonly record struct File(byte[] Body, bool Deflated, int Length);

    internal sealed class Entry(int[] glyphs, CompressionLevel level, File file)
    {
        public readonly int[] Glyphs = glyphs;
        public readonly CompressionLevel Level = level;
        public readonly File File = file;
    }

    /// <summary>The font file of a subset, compressed at the level, built now or earlier for the same glyphs.</summary>
    public static File Get(GlyphSubset subset, CompressionLevel level)
    {
        var recent = subset.Program.EmbeddedFiles;
        var glyphs = CollectionsMarshal.AsSpan(subset.Glyphs);
        lock (recent)
        {
            for (var node = recent.First; node != null; node = node.Next)
            {
                var entry = node.Value;
                if (entry.Level != level || !entry.Glyphs.AsSpan().SequenceEqual(glyphs)) continue;
                recent.Remove(node);
                recent.AddFirst(node);
                return entry.File;
            }
        }

        var data = FontSubsetter.Build(subset);
        var file = data.Length < MinCompressedLength ? new File(data, false, data.Length) : new File(Deflate(data, level), true, data.Length);
        if (file.Body.Length <= MaxKeptLength)
        {
            lock (recent)
            {
                recent.AddFirst(new Entry(glyphs.ToArray(), level, file));
                while (recent.Count > EntriesPerFont) recent.RemoveLast();
            }
        }
        return file;
    }

    private static byte[] Deflate(byte[] data, CompressionLevel level)
    {
        var ms = new MemoryStream(data.Length * 3 / 4 + 64);
        using (var z = new ZLibStream(ms, level, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }
}
