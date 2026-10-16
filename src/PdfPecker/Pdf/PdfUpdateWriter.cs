using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Pdf;

/// <summary>
/// An incremental update: the original file unchanged, followed by new and changed objects (strings and streams
/// encrypted with the file's own encryption) and a cross-reference section chained to the previous one. Signing,
/// adding validation data or attachments and filling forms all append to a file this way, so earlier signatures stay
/// valid.
/// </summary>
internal sealed class PdfUpdateWriter
{
    private readonly PdfFile _file;
    private readonly SortedDictionary<int, (long Offset, int Gen)> _offsets = new();

    /// <summary>The file so far; signers patch their placeholders in its buffer.</summary>
    public ByteBuffer Output { get; }

    /// <summary>Writes objects with the file's numbering, encrypting their strings with the file's encryption.</summary>
    public PdfObjectWriter Objects { get; }

    /// <summary>Starts the update of <paramref name="file"/>, parsed from <paramref name="pdf"/>, with room for <paramref name="extra"/> more bytes.</summary>
    public PdfUpdateWriter(PdfFile file, byte[] pdf, int extra)
    {
        _file = file;
        Objects = new PdfObjectWriter(PdfObjectSyntax.Update, file.Decryptor);
        Output = new ByteBuffer(pdf.Length + extra);
        Output.Bytes(pdf);
        if (pdf.Length == 0 || pdf[^1] != '\n') Output.Nl();
    }

    /// <summary>Starts object <paramref name="num"/>; its strings are then encrypted for it.</summary>
    public ByteBuffer Begin(int num, int gen)
    {
        _offsets[num] = (Output.Length, gen);
        Objects.ObjectNumber = num;
        Objects.Generation = gen;
        return Output.Int(num).Sp().Int(gen).Ascii(" obj\n");
    }

    /// <summary>Writes a whole object (not a stream).</summary>
    public void WriteObject(int num, int gen, PdfObject obj)
    {
        Begin(num, gen);
        Objects.WriteObject(Output, obj);
        Output.Ascii("\nendobj\n");
    }

    /// <summary>
    /// Writes a stream object: <paramref name="data"/> encrypted for it, with <paramref name="entries"/>' /Length set to
    /// the encrypted length (in its place when it has one, else last).
    /// </summary>
    public void WriteStream(int num, int gen, Dictionary<string, PdfObject> entries, byte[] data)
    {
        if (Objects.Encryption != null) data = Objects.Encryption.EncryptStream(data, num, gen);
        entries["Length"] = PdfNumber.Int(data.Length);
        Begin(num, gen);
        Objects.WriteObject(Output, new PdfDict(entries));
        Output.Ascii("\nstream\n").Bytes(data).Ascii("\nendstream\nendobj\n");
    }

    /// <summary>
    /// Writes the update's cross-reference section, a table or a stream as the file uses, and the end of the file;
    /// <paramref name="next"/> is the next free object number.
    /// </summary>
    public void Finish(PdfRef rootRef, int next)
    {
        var trailer = _file.Trailer;
        long prev = _file.LastXrefOffset;
        bool stream = prev >= 0 && !StartsWithXrefKeyword(_file.Data, prev);
        var output = Output;
        // The trailer and a cross-reference stream are never encrypted.
        var plain = new PdfObjectWriter(PdfObjectSyntax.Update);

        var t = new PdfDict();
        t.Entries["Root"] = rootRef;
        if (trailer["Info"] is PdfRef info) t.Entries["Info"] = info;
        if (trailer["Encrypt"] is { } enc && !enc.IsNull) t.Entries["Encrypt"] = enc;
        if (_file.Resolve(trailer["ID"]) is PdfArray { Count: 2 } id) t.Entries["ID"] = id;
        else
        {
            var fresh = new PdfString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16), hex: true);
            t.Entries["ID"] = new PdfArray(new List<PdfObject> { fresh, fresh });
        }
        if (prev >= 0) t.Entries["Prev"] = PdfNumber.Int(prev);

        // Runs of consecutive object numbers.
        var runs = new List<(int Start, List<(long Offset, int Gen)> Entries)>();
        void Collect()
        {
            runs.Clear();
            foreach (var (num, entry) in _offsets)
            {
                if (runs.Count > 0 && runs[^1].Start + runs[^1].Entries.Count == num) runs[^1].Entries.Add(entry);
                else runs.Add((num, new List<(long, int)> { entry }));
            }
        }

        long xrefPos = output.Length;
        if (!stream)
        {
            Collect();
            output.Ascii("xref\n");
            foreach (var (start, entries) in runs)
            {
                output.Int(start).Sp().Int(entries.Count).Nl();
                foreach (var (off, gen) in entries)
                    PdfXref.Padded(PdfXref.Padded(output, off, 10).Sp(), gen, 5).Ascii(" n\r\n");
            }
            t.Entries["Size"] = PdfNumber.Int(next);
            output.Ascii("trailer\n");
            plain.WriteObject(output, t);
        }
        else
        {
            int xrefNum = next++;
            _offsets[xrefNum] = (xrefPos, 0);
            Collect();
            var data = new List<byte>();
            var index = new List<PdfObject>();
            foreach (var (start, entries) in runs)
            {
                index.Add(PdfNumber.Int(start));
                index.Add(PdfNumber.Int(entries.Count));
                foreach (var (off, gen) in entries)
                {
                    data.Add(1);
                    for (int s = 32; s >= 0; s -= 8) data.Add((byte)(off >> s));
                    data.Add((byte)(gen >> 8));
                    data.Add((byte)gen);
                }
            }
            t.Entries["Type"] = PdfName.Get("XRef");
            t.Entries["Size"] = PdfNumber.Int(next);
            t.Entries["W"] = new PdfArray(new List<PdfObject> { PdfNumber.Int(1), PdfNumber.Int(5), PdfNumber.Int(2) });
            t.Entries["Index"] = new PdfArray(index);
            t.Entries["Length"] = PdfNumber.Int(data.Count);
            output.Int(xrefNum).Ascii(" 0 obj\n");
            plain.WriteObject(output, t);
            output.Ascii("\nstream\n").Bytes(data.ToArray()).Ascii("\nendstream\nendobj");
        }
        output.Ascii("\nstartxref\n").Int(xrefPos).Ascii("\n%%EOF\n");
    }

    private static bool StartsWithXrefKeyword(byte[] data, long offset)
    {
        long p = offset;
        while (p < data.Length && (data[p] == ' ' || data[p] == '\r' || data[p] == '\n' || data[p] == '\t')) p++;
        return p + 4 <= data.Length && data[p] == 'x' && data[p + 1] == 'r' && data[p + 2] == 'e' && data[p + 3] == 'f';
    }
}
