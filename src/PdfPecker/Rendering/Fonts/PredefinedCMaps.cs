using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;

namespace PdfPecker.Rendering.Fonts;

/// <summary>
/// The predefined CMaps of the Adobe Chinese, Japanese and Korean orderings (ISO 32000-2 table 116), which fonts name
/// by their /Encoding instead of embedding a CMap, and each ordering's CID-to-Unicode map for text extraction. They are
/// Adobe's own files, generated into the library by tools/CMaps/build.cs; an ordering's files are read the first time
/// one of them is used, and each CMap is parsed once per process.
/// </summary>
internal static class PredefinedCMaps
{
    private static readonly string[] Orderings = { "Japan1", "GB1", "CNS1", "Korea1" };

    // The prefixes of the CMap names of each ordering, so a name finds its file without reading the others.
    private static readonly (string Prefix, string Ordering)[] Prefixes =
    {
        ("UniJIS", "Japan1"), ("90ms", "Japan1"), ("90msp", "Japan1"), ("90pv", "Japan1"), ("83pv", "Japan1"), ("Add-", "Japan1"),
        ("EUC-", "Japan1"), ("Ext-", "Japan1"), ("H", "Japan1"), ("V", "Japan1"), ("Adobe-Japan1", "Japan1"),
        ("UniGB", "GB1"), ("GB", "GB1"), ("Adobe-GB1", "GB1"),
        ("UniCNS", "CNS1"), ("B5", "CNS1"), ("HKscs", "CNS1"), ("ETen", "CNS1"), ("CNS", "CNS1"), ("Adobe-CNS1", "CNS1"),
        ("UniKS", "Korea1"), ("KSC", "Korea1"), ("Adobe-Korea1", "Korea1"),
    };

    private static readonly ConcurrentDictionary<string, Dictionary<string, byte[]>> Files = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Lazy<CMap?>> Parsed = new(StringComparer.Ordinal);

    /// <summary>The predefined CMap of that name, or null when the library does not have it.</summary>
    public static CMap? Get(string name)
    {
        // Only the names the library has are kept: the names come from the documents, and keeping every one would let
        // documents of many made-up names grow the process's memory without end.
        if (Parsed.TryGetValue(name, out var parsed)) return parsed.Value;
        if (OrderingOf(name) == null) return null;
        return Parsed.GetOrAdd(name, n => new Lazy<CMap?>(() => Load(n))).Value;
    }

    /// <summary>How many CMaps the process keeps, for tests.</summary>
    internal static int KeptCount => Parsed.Count;

    /// <summary>
    /// The CID-to-Unicode map of an Adobe ordering ("Japan1", "GB1", "CNS1" or "Korea1"): CIDs as two-byte codes mapped
    /// to their characters. Null for other orderings.
    /// </summary>
    public static CMap? CidToUnicode(string? ordering)
        => ordering != null && Array.IndexOf(Orderings, ordering) >= 0 ? Get("Adobe-" + ordering + "-UCS2") : null;

    /// <summary>The ordering a predefined CMap belongs to, or null when the library does not have it.</summary>
    public static string? OrderingOf(string name)
    {
        var ordering = OrderingByPrefix(name);
        return ordering != null && FilesOf(ordering).ContainsKey(name) ? ordering : null;
    }

    /// <summary>The CMap of that name, built on the CMap it names with usecmap; Adobe's files name one level at most.</summary>
    private static CMap? Load(string name)
    {
        if (OrderingByPrefix(name) is not { } ordering || !FilesOf(ordering).TryGetValue(name, out var data)) return null;
        return CMap.Parse(data, parent => parent != name ? Get(parent) : null);
    }

    private static string? OrderingByPrefix(string name)
    {
        string? best = null;
        int bestLength = 0;
        foreach (var (prefix, ordering) in Prefixes)
        {
            if (prefix.Length > bestLength && name.StartsWith(prefix, StringComparison.Ordinal))
            {
                best = ordering;
                bestLength = prefix.Length;
            }
        }
        return best;
    }

    private static Dictionary<string, byte[]> FilesOf(string ordering) => Files.GetOrAdd(ordering, Read);

    // An ordering's archive: for each CMap a line with its name, a line with its length, and its bytes.
    private static Dictionary<string, byte[]> Read(string ordering)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var stream = typeof(PredefinedCMaps).Assembly.GetManifestResourceStream("PdfPecker.Rendering.Fonts.CMaps." + ordering + ".cmaps.br");
        if (stream == null) return files;
        using var brotli = new BrotliStream(stream, CompressionMode.Decompress);
        using var all = new MemoryStream();
        brotli.CopyTo(all);
        var bytes = all.GetBuffer().AsSpan(0, (int)all.Length);
        int pos = 0;
        while (pos < bytes.Length)
        {
            int nameEnd = bytes[pos..].IndexOf((byte)'\n');
            if (nameEnd < 0) break;
            string name = Encoding.ASCII.GetString(bytes.Slice(pos, nameEnd));
            pos += nameEnd + 1;
            int lengthEnd = bytes[pos..].IndexOf((byte)'\n');
            if (lengthEnd < 0 || !Invariant.TryParseInt(Encoding.ASCII.GetString(bytes.Slice(pos, lengthEnd)), out int length)) break;
            pos += lengthEnd + 1;
            if (length < 0 || length > bytes.Length - pos) break;
            files[name] = bytes.Slice(pos, length).ToArray();
            pos += length;
        }
        return files;
    }
}
