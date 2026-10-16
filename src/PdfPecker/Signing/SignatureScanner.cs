using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Signing;

/// <summary>A signature found in a PDF: its field, its dictionary and the raw bytes its /ByteRange points at.</summary>
internal sealed class FoundSignature
{
    public string FieldName = "";
    public PdfDict Dict = null!;
    public long[] ByteRange = Array.Empty<long>();

    /// <summary>Whether the byte range is well formed: it starts at 0, leaves exactly one hex string out, and stays in the file.</summary>
    public bool ByteRangeValid;

    /// <summary>The decoded hex string in the gap of the byte range (the /Contents value), or null.</summary>
    public byte[]? Contents;

    public string? SubFilter;
    public bool IsDocumentTimestamp => SubFilter == "ETSI.RFC3161";
}

/// <summary>Finds the signed signature fields of a PDF and locates each signature's bytes.</summary>
internal static class SignatureScanner
{
    private const int MaxSignatures = 1000;
    private const int MaxFields = 100_000;

    // A PDF signature is a few kilobytes, with a timestamp and a long chain perhaps a hundred; anything far larger is
    // either not a signature or an attempt to make the verifier allocate.
    private const long MaxContentsHexLength = 16L * 1024 * 1024;

    public static List<FoundSignature> Find(PdfFile file)
    {
        var result = new List<FoundSignature>();
        var fields = file.GetArray(file.GetDict(file.Catalog, "AcroForm"), "Fields");
        if (fields == null) return result;
        var seen = new HashSet<PdfDict>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(PdfObject Field, string Prefix, string? InheritedType, int Depth)>();
        for (int i = fields.Count - 1; i >= 0; i--) stack.Push((fields[i], "", null, 0));
        while (stack.Count > 0 && seen.Count < MaxFields && result.Count < MaxSignatures)
        {
            var (o, prefix, inherited, depth) = stack.Pop();
            if (depth > 32 || file.Resolve(o) is not PdfDict field || !seen.Add(field)) continue;
            string name = prefix;
            if (file.Resolve(field["T"]) is PdfString t) name = prefix.Length == 0 ? t.Text : prefix + "." + t.Text;
            var type = file.GetName(field, "FT") ?? inherited;
            if (type == "Sig" && file.Resolve(field["V"]) is PdfDict sig && file.GetArray(sig, "ByteRange") != null)
            {
                result.Add(Locate(file, sig, name));
                continue;
            }
            if (file.GetArray(field, "Kids") is { } kids)
                for (int i = kids.Count - 1; i >= 0; i--) stack.Push((kids[i], name, type, depth + 1));
        }
        // Oldest first: the order in which the signatures were applied.
        result.Sort((a, b) => End(a).CompareTo(End(b)));
        return result;
    }

    private static long End(FoundSignature s) => s.ByteRange.Length == 4 ? s.ByteRange[2] + s.ByteRange[3] : long.MaxValue;

    private static FoundSignature Locate(PdfFile file, PdfDict sig, string name)
    {
        var found = new FoundSignature { FieldName = name, Dict = sig, SubFilter = file.GetName(sig, "SubFilter") };
        var range = file.GetArray(sig, "ByteRange");
        if (range == null || range.Count != 4) return found;
        var r = new long[4];
        for (int i = 0; i < 4; i++)
        {
            if (file.Resolve(range[i]) is not PdfNumber { IsInteger: true } n || n.Value < 0 || n.Value > long.MaxValue / 4) return found;
            r[i] = (long)n.Value;
        }
        found.ByteRange = r;
        var data = file.Data;
        long gapStart = r[0] + r[1], gapEnd = r[2];
        if (r[0] != 0 || r[1] < 1 || gapEnd <= gapStart + 1 || r[2] + r[3] > data.Length || gapEnd - gapStart > MaxContentsHexLength) return found;
        if (data[gapStart] != '<' || data[gapEnd - 1] != '>') return found;
        var hex = ReadHex(data, gapStart + 1, gapEnd - 1);
        if (hex == null) return found;
        found.Contents = hex;

        // The gap must be this dictionary's own /Contents. A byte range that skips some other string would let a signature
        // from elsewhere vouch for this one. Encrypted files store /Contents unencrypted, which the parser cannot
        // compare, so there the gap only has to be a hex string.
        if (file.Decryptor == null && (file.Resolve(sig["Contents"]) is not PdfString c || !c.Bytes.AsSpan().SequenceEqual(hex))) return found;
        found.ByteRangeValid = true;
        return found;
    }

    /// <summary>Decodes hex digits between <paramref name="start"/> and <paramref name="end"/>, allowing white space; null on any other byte.</summary>
    private static byte[]? ReadHex(byte[] data, long start, long end)
    {
        var bytes = new byte[(end - start + 1) / 2];
        int count = 0, high = -1;
        for (long i = start; i < end; i++)
        {
            byte b = data[i];
            int v = b >= '0' && b <= '9' ? b - '0' : b >= 'A' && b <= 'F' ? b - 'A' + 10 : b >= 'a' && b <= 'f' ? b - 'a' + 10 : -1;
            if (v < 0)
            {
                if (b is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t' or (byte)'\f' or 0) continue;
                return null;
            }
            if (high < 0) high = v;
            else
            {
                bytes[count++] = (byte)(high << 4 | v);
                high = -1;
            }
        }
        if (high >= 0) bytes[count++] = (byte)(high << 4);
        Array.Resize(ref bytes, count);
        return bytes;
    }

    /// <summary>Hashes the two signed ranges of the file.</summary>
    public static byte[] HashRanges(byte[] data, long[] r, System.Security.Cryptography.HashAlgorithmName hash)
    {
        using var h = System.Security.Cryptography.IncrementalHash.CreateHash(hash);
        h.AppendData(data, (int)r[0], (int)r[1]);
        h.AppendData(data, (int)r[2], (int)r[3]);
        return h.GetHashAndReset();
    }

    /// <summary>
    /// Counts the incremental updates after a signed revision that ends at <paramref name="offset"/>: every update ends
    /// with its own end-of-file marker.
    /// </summary>
    public static int UpdatesAfter(byte[] data, long offset)
    {
        int count = 0;
        var marker = "%%EOF"u8;
        var span = data.AsSpan((int)Math.Min(offset, data.Length));
        int pos;
        while ((pos = span.IndexOf(marker)) >= 0)
        {
            count++;
            span = span[(pos + marker.Length)..];
        }
        return count;
    }
}
