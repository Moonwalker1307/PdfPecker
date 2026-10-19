using System.Text;

namespace PdfPecker.Pdf;

/// <summary>The document information dictionary's entries: the standard ones and custom ones.</summary>
internal sealed class DocumentProperties
{
    /// <summary>Keys the information dictionary defines itself, which custom properties cannot use.</summary>
    internal static readonly HashSet<string> StandardKeys = new(StringComparer.Ordinal)
    {
        "Title", "Author", "Subject", "Keywords", "Creator", "Producer", "CreationDate", "ModDate", "Trapped",
    };

    public string? Title { get; init; }
    public string? Author { get; init; }
    public string? Subject { get; init; }
    public string? Keywords { get; init; }
    public string? Creator { get; init; }

    /// <summary>The producing application. Null or empty means "PdfPecker".</summary>
    public string? Producer { get; init; }

    /// <summary>Custom entries, in order, with keys already checked by <see cref="Validate"/>.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Custom { get; init; } = Array.Empty<KeyValuePair<string, string>>();

    public string ProducerOrDefault => string.IsNullOrEmpty(Producer) ? "PdfPecker" : Producer!;

    /// <summary>Checks custom property keys and copies them into a list.</summary>
    /// <exception cref="ArgumentException">A key is empty or one of the standard keys.</exception>
    public static IReadOnlyList<KeyValuePair<string, string>> Validate(IEnumerable<KeyValuePair<string, string>>? custom)
    {
        if (custom == null) return Array.Empty<KeyValuePair<string, string>>();
        var list = new List<KeyValuePair<string, string>>();
        foreach (var kv in custom)
        {
            if (string.IsNullOrEmpty(kv.Key)) throw new ArgumentException("A custom property needs a name.", nameof(custom));
            if (StandardKeys.Contains(kv.Key))
                throw new ArgumentException($"\"{kv.Key}\" is a standard property; set it with its own option instead.", nameof(custom));
            if (kv.Value == null) continue;
            list.Add(new KeyValuePair<string, string>(kv.Key, kv.Value));
        }
        return list;
    }

    public DocumentProperties WithStandard(string? title, string? author, string? subject) => new()
    {
        Title = title, Author = author, Subject = subject,
        Keywords = Keywords, Creator = Creator, Producer = Producer, Custom = Custom,
    };

    /// <summary>A copy without the control characters XML cannot carry.</summary>
    public DocumentProperties Cleaned() => new()
    {
        Title = Clean(Title), Author = Clean(Author), Subject = Clean(Subject),
        Keywords = Clean(Keywords), Creator = Clean(Creator), Producer = Clean(Producer),
        Custom = Custom.Select(kv => new KeyValuePair<string, string>(kv.Key, Clean(kv.Value)!)).ToList(),
    };

    private static string? Clean(string? s)
    {
        if (s == null) return null;
        foreach (char ch in s)
            if (ch < 0x20 && ch is not ('\t' or '\n' or '\r'))
                return new string(s.Where(x => x >= 0x20 || x is '\t' or '\n' or '\r').ToArray());
        return s;
    }

    /// <summary>Writes the information dictionary, <c>&lt;&lt; ... &gt;&gt;</c>.</summary>
    public void WriteInfo(ByteBuffer b, DateTime now, bool modDate, Action<ByteBuffer, string> writeText)
    {
        string date = "D:" + now.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) + "Z";
        b.Ascii("<< /Producer ");
        writeText(b, ProducerOrDefault);
        void Entry(string key, string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            b.Sp();
            PdfSyntax.Name(b, key);
            b.Sp();
            writeText(b, value!);
        }
        Entry("Title", Title);
        Entry("Author", Author);
        Entry("Subject", Subject);
        Entry("Keywords", Keywords);
        Entry("Creator", Creator);
        foreach (var kv in Custom) Entry(kv.Key, kv.Value);
        b.Ascii(" /CreationDate ");
        writeText(b, date);
        if (modDate)
        {
            b.Ascii(" /ModDate ");
            writeText(b, date);
        }
        b.Ascii(" >>");
    }
}

/// <summary>Low-level PDF syntax shared by the writers: names and strings.</summary>
internal static class PdfSyntax
{
    private static ReadOnlySpan<byte> UpperDigits => "0123456789ABCDEF"u8;
    private static ReadOnlySpan<byte> LowerDigits => "0123456789abcdef"u8;

    /// <summary>Writes a name object: <c>/</c> plus the UTF-8 bytes, with delimiters, whitespace, '#' and non-ASCII as #xx.</summary>
    public static void Name(ByteBuffer b, string name) => Name(b, Encoding.UTF8.GetBytes(name));

    /// <summary>Writes a name object from its bytes.</summary>
    public static void Name(ByteBuffer b, ReadOnlySpan<byte> name)
    {
        b.Byte((byte)'/');
        foreach (var c in name) NameByte(b, c);
    }

    /// <summary>Writes a name object from its raw bytes (as a parsed name holds them, one char per byte).</summary>
    public static void RawName(ByteBuffer b, string name)
    {
        b.Byte((byte)'/');
        foreach (char ch in name) NameByte(b, (byte)ch);
    }

    private static void NameByte(ByteBuffer b, byte c)
    {
        if (c < 0x21 || c > 0x7E || c is (byte)'#' or (byte)'/' or (byte)'%' or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>'
            or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}')
            b.Byte((byte)'#').Byte(UpperDigits[c >> 4]).Byte(UpperDigits[c & 15]);
        else b.Byte(c);
    }

    /// <summary>Writes an unencrypted text string: a literal for printable ASCII, else UTF-16BE hex with a byte order mark.</summary>
    public static void TextString(ByteBuffer b, string s)
    {
        if (s.All(c => c is >= ' ' and <= '~'))
        {
            b.Byte((byte)'(');
            foreach (char c in s)
            {
                if (c is '(' or ')' or '\\') b.Byte((byte)'\\');
                b.Byte((byte)c);
            }
            b.Byte((byte)')');
            return;
        }
        var raw = new byte[2 + s.Length * 2];
        raw[0] = 0xFE;
        raw[1] = 0xFF;
        for (int i = 0; i < s.Length; i++)
        {
            raw[2 + i * 2] = (byte)(s[i] >> 8);
            raw[3 + i * 2] = (byte)s[i];
        }
        HexString(b, raw);
    }

    /// <summary>
    /// Writes a string object as a literal: the bytes as they are, with parentheses and backslashes escaped, and carriage
    /// returns too, which a reader would otherwise turn into line feeds. It is never longer than the hex form.
    /// </summary>
    public static void LiteralString(ByteBuffer b, ReadOnlySpan<byte> bytes)
    {
        b.Byte((byte)'(');
        foreach (var c in bytes)
        {
            if (c == (byte)'\r') b.Ascii("\\r");
            else if (c is (byte)'(' or (byte)')' or (byte)'\\') b.Byte((byte)'\\').Byte(c);
            else b.Byte(c);
        }
        b.Byte((byte)')');
    }

    /// <summary>Writes a string object as hex, in lower case.</summary>
    public static void HexString(ByteBuffer b, ReadOnlySpan<byte> bytes) => Hex(b, bytes, upper: false);

    /// <summary>Writes a string object as hex: <c>&lt;...&gt;</c> with two digits per byte.</summary>
    public static void Hex(ByteBuffer b, ReadOnlySpan<byte> bytes, bool upper)
    {
        var digits = upper ? UpperDigits : LowerDigits;
        b.Byte((byte)'<');
        foreach (var x in bytes) b.Byte(digits[x >> 4]).Byte(digits[x & 15]);
        b.Byte((byte)'>');
    }
}
