namespace PdfPecker.Rendering.Parsing;

/// <summary>Typed accessors that resolve references and tolerate missing or mistyped entries.</summary>
internal sealed partial class PdfFile
{
    public PdfDict? Catalog => Resolve(Trailer["Root"]) as PdfDict;

    public PdfDict? GetDict(PdfDict? d, string key) => d == null ? null : Resolve(d[key]) as PdfDict;
    public PdfArray? GetArray(PdfDict? d, string key) => d == null ? null : Resolve(d[key]) as PdfArray;
    public PdfStream? GetStream(PdfDict? d, string key) => d == null ? null : Resolve(d[key]) as PdfStream;

    public string? GetName(PdfDict? d, string key) => d == null ? null : (Resolve(d[key]) as PdfName)?.Value;

    public int GetInt(PdfDict? d, string key, int fallback)
    {
        if (d == null) return fallback;
        return Resolve(d[key]) is PdfNumber n ? n.IntValue : fallback;
    }

    public double GetNumber(PdfDict? d, string key, double fallback)
    {
        if (d == null) return fallback;
        return Resolve(d[key]) is PdfNumber n ? n.Value : fallback;
    }

    public bool GetBool(PdfDict? d, string key, bool fallback)
    {
        if (d == null) return fallback;
        return Resolve(d[key]) is PdfBool b ? b.Value : fallback;
    }

    public byte[]? GetBytes(PdfDict? d, string key) => d == null ? null : (Resolve(d[key]) as PdfString)?.Bytes;

    public int GetIntValue(PdfObject? o) => Resolve(o) is PdfNumber n ? n.IntValue : 0;

    public double Num(PdfObject? o, double fallback = 0) => Resolve(o) is PdfNumber n ? n.Value : fallback;

    /// <summary>Reads a numeric array, e.g. a matrix or rectangle.</summary>
    public double[]? GetNumbers(PdfDict? d, string key) => ToNumbers(d == null ? null : Resolve(d[key]) as PdfArray);

    public double[]? ToNumbers(PdfArray? a)
    {
        if (a == null) return null;
        var r = new double[a.Count];
        for (int i = 0; i < r.Length; i++) r[i] = Num(a[i]);
        return r;
    }
}
