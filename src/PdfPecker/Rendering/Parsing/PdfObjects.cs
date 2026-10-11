using System.Text;

namespace PdfPecker.Rendering.Parsing;

/// <summary>A parsed PDF object. Indirect references are resolved lazily through <see cref="PdfFile"/>.</summary>
internal abstract class PdfObject
{
    public virtual bool IsNull => false;
}

internal sealed class PdfNull : PdfObject
{
    public static readonly PdfNull Instance = new();
    private PdfNull() { }
    public override bool IsNull => true;
    public override string ToString() => "null";
}

internal sealed class PdfBool : PdfObject
{
    public static readonly PdfBool True = new(true), False = new(false);
    public readonly bool Value;
    private PdfBool(bool v) => Value = v;
    public override string ToString() => Value ? "true" : "false";
}

internal sealed class PdfNumber : PdfObject
{
    private static readonly PdfNumber[] Small = CreateSmall();
    public readonly double Value;
    public readonly bool IsInteger;

    private PdfNumber(double v, bool isInt) { Value = v; IsInteger = isInt; }

    private static PdfNumber[] CreateSmall()
    {
        var a = new PdfNumber[257];
        for (int i = 0; i < a.Length; i++) a[i] = new PdfNumber(i - 1, true);
        return a;
    }

    public static PdfNumber Int(long v) => v is >= -1 and <= 255 ? Small[v + 1] : new PdfNumber(v, true);
    public static PdfNumber Real(double v) => new(v, false);
    public int IntValue => Value >= int.MaxValue ? int.MaxValue : Value <= int.MinValue ? int.MinValue : (int)Value;
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class PdfString : PdfObject
{
    public byte[] Bytes;
    public readonly bool IsHex;
    public PdfString(byte[] bytes, bool hex = false) { Bytes = bytes; IsHex = hex; }

    /// <summary>Decodes a PDF text string (UTF-16BE with BOM, UTF-8 with BOM, or PDFDocEncoding approximated as Latin-1).</summary>
    public string Text
    {
        get
        {
            var b = Bytes;
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
            return Encoding.Latin1.GetString(b);
        }
    }

    public override string ToString() => "(" + Text + ")";
}

internal sealed class PdfName : PdfObject
{
    private static readonly Dictionary<string, PdfName> Interned = new(StringComparer.Ordinal);
    public readonly string Value;
    private PdfName(string v) => Value = v;

    public static PdfName Get(string v)
    {
        lock (Interned)
        {
            if (!Interned.TryGetValue(v, out var n))
            {
                n = new PdfName(v);
                if (Interned.Count < 100_000) Interned[v] = n;
            }
            return n;
        }
    }

    public override string ToString() => "/" + Value;
}

internal sealed class PdfArray : PdfObject
{
    public readonly List<PdfObject> Items;
    public PdfArray() => Items = new List<PdfObject>();
    public PdfArray(List<PdfObject> items) => Items = items;
    public int Count => Items.Count;
    public PdfObject this[int i] => Items[i];
    public override string ToString() => "[" + string.Join(" ", Items) + "]";
}

internal class PdfDict : PdfObject
{
    public readonly Dictionary<string, PdfObject> Entries;
    public PdfDict() => Entries = new Dictionary<string, PdfObject>(StringComparer.Ordinal);
    public PdfDict(Dictionary<string, PdfObject> entries) => Entries = entries;

    public PdfObject? this[string key] => Entries.TryGetValue(key, out var v) ? v : null;
    public bool ContainsKey(string key) => Entries.ContainsKey(key);
    public override string ToString() => "<<" + string.Join(" ", Entries.Select(e => "/" + e.Key + " " + e.Value)) + ">>";
}

/// <summary>A stream: its dictionary plus raw (still encoded, already decrypted) data.</summary>
internal sealed class PdfStream : PdfDict
{
    public byte[] RawData;
    public int ObjectNumber; // 0 for inline/unknown
    public PdfStream(Dictionary<string, PdfObject> entries, byte[] data) : base(entries) => RawData = data;
    public override string ToString() => "stream" + base.ToString();
}

internal sealed class PdfRef : PdfObject, IEquatable<PdfRef>
{
    public readonly int Num, Gen;
    public PdfRef(int num, int gen) { Num = num; Gen = gen; }
    public bool Equals(PdfRef? other) => other != null && other.Num == Num && other.Gen == Gen;
    public override bool Equals(object? obj) => obj is PdfRef r && Equals(r);
    public override int GetHashCode() => Num * 31 + Gen;
    public override string ToString() => Num + " " + Gen + " R";
}

/// <summary>Thrown for files that cannot be read at all.</summary>
internal sealed class PdfFormatException : Exception
{
    public PdfFormatException(string message) : base(message) { }
}
