using System.Security.Cryptography;

namespace PdfPecker.Images;

/// <summary>
/// Decoded images shared between conversions, found by the SHA-256 of their file bytes: a template's logo is decoded
/// once, not for every document made from it. Each document gets its own copy of the image object (the writer numbers
/// it), sharing the read-only pixel data. Small and least-recently-used, so a stream of different images costs no more
/// than the cap.
/// </summary>
internal static class DecodedImageCache
{
    private const int MaxEntries = 64;
    private const long MaxBytes = 16L << 20;

    private sealed class Entry
    {
        public required byte[] Hash;
        public required PdfImage Image;
        public required long Size;
    }

    private static readonly LinkedList<Entry> Entries = new(); // most recently used first
    private static long _bytes;

    /// <summary>Decodes JPEG, PNG, GIF or WebP data, or returns a copy of the same image decoded earlier.</summary>
    public static PdfImage? Decode(byte[] data)
    {
        var hash = SHA256.HashData(data);
        lock (Entries)
        {
            for (var node = Entries.First; node != null; node = node.Next)
            {
                if (!node.Value.Hash.AsSpan().SequenceEqual(hash)) continue;
                Entries.Remove(node);
                Entries.AddFirst(node);
                return node.Value.Image.Copy();
            }
        }
        var image = PdfImage.FromBytes(data);
        if (image == null) return null;
        long size = image.Data.Length + (image.SMask?.Data.Length ?? 0);
        if (size > MaxBytes / 4) return image;
        lock (Entries)
        {
            Entries.AddFirst(new Entry { Hash = hash, Image = image.Copy(), Size = size });
            _bytes += size;
            while (Entries.Count > MaxEntries || _bytes > MaxBytes)
            {
                _bytes -= Entries.Last!.Value.Size;
                Entries.RemoveLast();
            }
        }
        return image;
    }
}
