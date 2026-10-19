using System.Globalization;
using System.Text;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Pdf;

/// <summary>
/// Encrypts the strings and streams of one indirect object. The older handlers derive a key per object from its number
/// and generation; AES-256 ignores them.
/// </summary>
internal interface IPdfObjectEncryption
{
    byte[] EncryptString(byte[] data, int num, int gen);
    byte[] EncryptStream(byte[] data, int num, int gen);
}

/// <summary>
/// How a <see cref="PdfObjectWriter"/> spells objects. The writers that rewrite a whole file and the ones that append an
/// incremental update grew their own spellings; both are kept so that every writer's output stays what it was.
/// </summary>
internal sealed class PdfObjectSyntax
{
    /// <summary>Dictionaries as <c>&lt;&lt;/A 1 /B 2 &gt;&gt;</c> rather than <c>&lt;&lt; /A 1 /B 2 &gt;&gt;</c>.</summary>
    public bool CompactDictionaries { get; init; }

    /// <summary>A real with an integral value below 10^15 is written as an integer, which spells -0 as 0.</summary>
    public bool IntegralRealsAsIntegers { get; init; }

    /// <summary>Hex strings with the digits A-F in upper case.</summary>
    public bool UpperHexStrings { get; init; }

    /// <summary>Hex strings that were encrypted with the digits A-F in upper case.</summary>
    public bool UpperHexEncryptedStrings { get; init; }

    /// <summary>A stream met as a direct object is an error rather than written as null.</summary>
    public bool ThrowOnDirectStream { get; init; }

    /// <summary>
    /// The fewest bytes: no whitespace except between two tokens that would otherwise run together, unencrypted strings
    /// as literals, and reals without a leading zero.
    /// </summary>
    public bool Minimal { get; init; }

    /// <summary>Deeper nesting is written as null, so a pathological object cannot exhaust the stack.</summary>
    public int MaxDepth { get; init; } = int.MaxValue;

    /// <summary>The writers that copy parsed objects into a new file (merging, stamping, encrypting).</summary>
    public static readonly PdfObjectSyntax Rewrite = new() { MaxDepth = 256 };

    /// <summary>
    /// <see cref="Rewrite"/> with encrypted strings in upper-case hex, as the stamper writes them with the encryptor's
    /// own hex writer.
    /// </summary>
    public static readonly PdfObjectSyntax Stamp = new() { MaxDepth = 256, UpperHexEncryptedStrings = true };

    /// <summary>The compactor, which writes every object in as few bytes as it can.</summary>
    public static readonly PdfObjectSyntax Compact = new() { MaxDepth = 256, Minimal = true };

    /// <summary>The incremental updates (signatures, validation data, attachments, form filling).</summary>
    public static readonly PdfObjectSyntax Update = new()
    {
        CompactDictionaries = true, IntegralRealsAsIntegers = true, UpperHexStrings = true, UpperHexEncryptedStrings = true,
        ThrowOnDirectStream = true,
    };
}

/// <summary>
/// Writes parsed objects (<see cref="PdfObject"/>) back in PDF syntax, for every writer that copies or updates an
/// existing file, so escaping, number formatting and encryption are done in one place. Strings are written as hex,
/// encrypted with <see cref="Encryption"/> for the object <see cref="ObjectNumber"/> when it is set. Writers that renumber
/// objects override <see cref="WriteRef"/>, and ones that change entries as they copy override <see cref="WriteEntry"/>.
/// </summary>
internal class PdfObjectWriter
{
    protected readonly PdfObjectSyntax Syntax;

    /// <summary>The encryption of strings and streams, or null to write them as they are.</summary>
    public IPdfObjectEncryption? Encryption { get; set; }

    /// <summary>The indirect object being written, whose number and generation key its encryption.</summary>
    public int ObjectNumber { get; set; }

    /// <inheritdoc cref="ObjectNumber"/>
    public int Generation { get; set; }

    public PdfObjectWriter(PdfObjectSyntax syntax, IPdfObjectEncryption? encryption = null)
    {
        Syntax = syntax;
        Encryption = encryption;
    }

    /// <summary>Writes any object; <paramref name="depth"/> is its nesting within the indirect object.</summary>
    public void WriteObject(ByteBuffer b, PdfObject? o, int depth = 0)
    {
        if (Syntax.Minimal && o is not (PdfString or PdfName or PdfArray or PdfDict)) Separate(b);
        if (depth > Syntax.MaxDepth) { b.Ascii("null"); return; }
        switch (o)
        {
            case PdfBool x:
                b.Ascii(x.Value ? "true" : "false");
                break;
            case PdfNumber n:
                WriteNumber(b, n);
                break;
            case PdfString s:
                WriteString(b, s.Bytes);
                break;
            case PdfName nm:
                WriteName(b, nm.Value);
                break;
            case PdfRef r:
                WriteRef(b, r);
                break;
            case PdfArray a:
                b.Byte((byte)'[');
                for (int i = 0; i < a.Count; i++)
                {
                    if (i > 0 && !Syntax.Minimal) b.Sp();
                    WriteObject(b, a[i], depth + 1);
                }
                b.Byte((byte)']');
                break;
            case PdfStream:
                // A stream can only be an indirect object; a direct one cannot occur in parsed data.
                if (Syntax.ThrowOnDirectStream) throw new PdfSignatureException("A stream cannot be written inline.");
                b.Ascii("null");
                break;
            case PdfDict d:
                WriteDict(b, d, depth);
                break;
            default:
                b.Ascii("null");
                break;
        }
    }

    public void WriteDict(ByteBuffer b, PdfDict d, int depth)
    {
        b.Ascii("<<");
        foreach (var (key, value) in d.Entries) WriteEntry(b, d, key, value, depth);
        b.Ascii(Syntax.CompactDictionaries || Syntax.Minimal ? ">>" : " >>");
    }

    /// <summary>
    /// Writes a space when the last byte written is a regular character, which the token that follows (a number, a
    /// keyword or a reference) would otherwise run into.
    /// </summary>
    protected static void Separate(ByteBuffer b)
    {
        if (b.Length > 0 && IsRegular(b.Span[b.Length - 1])) b.Sp();
    }

    /// <summary>Whether a byte is neither whitespace nor a delimiter, so that it continues the token before it.</summary>
    private static bool IsRegular(byte c)
        => c is not ((byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t' or (byte)'\f' or 0 or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>'
            or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%');

    /// <summary>
    /// Writes one entry of a dictionary at <paramref name="depth"/>; <paramref name="owner"/> is the dictionary, or null for
    /// a stream's dictionary.
    /// </summary>
    protected virtual void WriteEntry(ByteBuffer b, PdfDict? owner, string key, PdfObject value, int depth)
    {
        if (Syntax.Minimal)
        {
            WriteName(b, key);
            WriteObject(b, value, depth + 1);
        }
        else if (Syntax.CompactDictionaries)
        {
            WriteName(b, key);
            b.Sp();
            WriteObject(b, value, depth + 1);
            b.Sp();
        }
        else
        {
            b.Sp();
            WriteName(b, key);
            b.Sp();
            WriteObject(b, value, depth + 1);
        }
    }

    /// <summary>Writes a reference; by default unchanged, as an incremental update keeps the file's numbering.</summary>
    protected virtual void WriteRef(ByteBuffer b, PdfRef r) => b.Int(r.Num).Sp().Int(r.Gen).Ascii(" R");

    public void WriteNumber(ByteBuffer b, PdfNumber n)
    {
        double v = n.Value;
        if (n.IsInteger || (Syntax.IntegralRealsAsIntegers && v == Math.Floor(v) && Math.Abs(v) < 1e15)) b.Int((long)v);
        else if (!double.IsFinite(v)) b.Byte((byte)'0');
        else if (Syntax.Minimal) b.Ascii(MinimalReal(v));
        else b.Ascii(v.ToString("0.##########", CultureInfo.InvariantCulture));
    }

    /// <summary>A real as the shortest spelling a reader accepts: 0.5 as .5 and -0.25 as -.25.</summary>
    private static string MinimalReal(double v)
    {
        var s = v.ToString("0.##########", CultureInfo.InvariantCulture);
        if (s.StartsWith("0.", StringComparison.Ordinal)) return s.Substring(1);
        if (s.StartsWith("-0.", StringComparison.Ordinal)) return string.Concat("-", s.AsSpan(2));
        return s;
    }

    /// <summary>
    /// Writes a string as hex (as a literal with <see cref="PdfObjectSyntax.Minimal"/>), encrypted for the current object
    /// when there is an encryption.
    /// </summary>
    public void WriteString(ByteBuffer b, byte[] bytes)
    {
        if (Encryption != null) PdfSyntax.Hex(b, Encryption.EncryptString(bytes, ObjectNumber, Generation), Syntax.UpperHexEncryptedStrings);
        else if (Syntax.Minimal) PdfSyntax.LiteralString(b, bytes);
        else PdfSyntax.Hex(b, bytes, Syntax.UpperHexStrings);
    }

    public static void WriteName(ByteBuffer b, string name)
    {
        // The parser keeps one char per byte, so a parsed name (all chars up to U+00FF) is written back as the same bytes,
        // while a name made in code with other characters is UTF-8 rather than cut to one byte per character.
        if (!IsLatin1(name)) PdfSyntax.Name(b, Encoding.UTF8.GetBytes(name));
        else PdfSyntax.RawName(b, name);
    }

    /// <summary>
    /// Writes a parsed stream as a copy: without its /Crypt filter (the parser has already decrypted it), encrypted again
    /// for the current object when there is an encryption, with /Length last.
    /// </summary>
    public void WriteStream(ByteBuffer b, PdfFile file, PdfStream s)
    {
        var entries = new Dictionary<string, PdfObject>(s.Entries, StringComparer.Ordinal);
        entries.Remove("Length");
        RemoveCryptFilter(file, entries);
        var data = Encryption != null ? Encryption.EncryptStream(s.RawData, ObjectNumber, Generation) : s.RawData;
        b.Ascii("<<");
        foreach (var (key, value) in entries) WriteEntry(b, null, key, value, 0);
        b.Ascii(" /Length ").Int(data.Length).Ascii(" >>\nstream\n").Bytes(data).Ascii("\nendstream");
    }

    /// <summary>The input's decryption is already undone, so a /Crypt filter in the chain must go too.</summary>
    public static void RemoveCryptFilter(PdfFile file, Dictionary<string, PdfObject> entries)
    {
        var filter = file.Resolve(entries.GetValueOrDefault("Filter"));
        if (filter is PdfName { Value: "Crypt" })
        {
            entries.Remove("Filter");
            entries.Remove("DecodeParms");
        }
        else if (filter is PdfArray fa && fa.Items.Any(f => file.Resolve(f) is PdfName { Value: "Crypt" }))
        {
            var parms = file.Resolve(entries.GetValueOrDefault("DecodeParms")) as PdfArray;
            var nf = new List<PdfObject>();
            var np = new List<PdfObject>();
            for (int i = 0; i < fa.Count; i++)
            {
                if (file.Resolve(fa[i]) is PdfName { Value: "Crypt" }) continue;
                nf.Add(fa[i]);
                np.Add(parms != null && i < parms.Count ? parms[i] : PdfNull.Instance);
            }
            entries["Filter"] = new PdfArray(nf);
            if (parms != null) entries["DecodeParms"] = new PdfArray(np);
        }
    }

    private static bool IsLatin1(string s)
    {
        foreach (char ch in s)
            if (ch > 0xFF) return false;
        return true;
    }
}
