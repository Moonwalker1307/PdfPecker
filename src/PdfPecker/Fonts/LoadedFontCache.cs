namespace PdfPecker.Fonts;

/// <summary>
/// Fonts of @font-face rules shared between conversions, found by their file bytes: a template's web fonts are decoded
/// (WOFF2 above all) and given character maps once, not for every document made from it. Each document gets copies
/// (<see cref="FontProgram.Copy"/>) that share those tables but make their own variable-font instances and feature
/// variants, so a document's output is the same as when it loads the font itself. Each entry keeps a copy of the file,
/// because comparing the bytes costs less than hashing them, and the fonts read that copy, because the caller's array
/// may change after the conversion. Small and least-recently-used, so a stream of different fonts costs no more than
/// the cap.
/// </summary>
internal static class LoadedFontCache
{
    private const int MaxEntries = 32;
    private const long MaxBytes = 64L << 20;
    // The character map and the widths by BMP code point that every font builds.
    private const int TableBytes = 65536 * 2 * 2;

    private sealed class Entry
    {
        public required byte[] File;
        public required List<FontProgram> Fonts;
        public required long Size;
    }

    private static readonly LinkedList<Entry> Entries = new(); // most recently used first
    private static long _bytes;

    /// <summary>The fonts of a font file, as <see cref="FontProgram.Load"/> reads them, loaded now or earlier.</summary>
    public static List<FontProgram> Load(byte[] data)
    {
        lock (Entries)
        {
            for (var node = Entries.First; node != null; node = node.Next)
            {
                if (!node.Value.File.AsSpan().SequenceEqual(data)) continue;
                Entries.Remove(node);
                Entries.AddFirst(node);
                return Copies(node.Value.Fonts);
            }
        }
        var file = data.ToArray();
        var fonts = FontProgram.Load(file);
        long size = file.Length;
        foreach (var font in fonts)
        {
            font.BuildSharedTables();
            size += (font.Data == file ? 0 : font.Data.Length) + TableBytes;
        }
        if (fonts.Count == 0 || size > MaxBytes / 4) return fonts;
        lock (Entries)
        {
            Entries.AddFirst(new Entry { File = file, Fonts = fonts, Size = size });
            _bytes += size;
            while (Entries.Count > MaxEntries || _bytes > MaxBytes)
            {
                _bytes -= Entries.Last!.Value.Size;
                Entries.RemoveLast();
            }
        }
        return Copies(fonts);
    }

    private static List<FontProgram> Copies(List<FontProgram> fonts)
    {
        var copies = new List<FontProgram>(fonts.Count);
        foreach (var font in fonts) copies.Add(font.Copy());
        return copies;
    }
}
