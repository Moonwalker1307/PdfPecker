namespace PdfPecker.Rendering.Parsing;

/// <summary>The file header, the startxref pointer, and the chain of cross-reference tables and streams.</summary>
internal sealed partial class PdfFile
{
    /// <summary>The header marker is looked for in this many bytes at the start; a file with more junk before it is read as if it had none.</summary>
    private const int HeaderSearchLength = 1024;

    /// <summary>The "startxref" keyword is looked for in this many bytes at the end of the file.</summary>
    private const int StartxrefSearchLength = 2048;

    /// <summary>The widest field of a cross-reference stream row, in bytes.</summary>
    private const int MaxXrefFieldWidth = 8;

    private const int XrefTypeInFile = 1;
    private const int XrefTypeInObjectStream = 2;

    private static ReadOnlySpan<byte> HeaderMarker => "%PDF-"u8;

    private static ReadOnlySpan<byte> StartxrefKeyword => "startxref"u8;

    /// <summary>The widths of the three fields of a cross-reference stream row (/W).</summary>
    private readonly record struct XrefRowLayout(int TypeWidth, int SecondWidth, int ThirdWidth)
    {
        public int RowLength => TypeWidth + SecondWidth + ThirdWidth;

        public bool IsValid => TypeWidth >= 0 && SecondWidth >= 0 && ThirdWidth >= 0
            && TypeWidth <= MaxXrefFieldWidth && SecondWidth <= MaxXrefFieldWidth && ThirdWidth <= MaxXrefFieldWidth;
    }

    /// <summary>
    /// Where "%PDF-" starts, when it is within the first kilobyte of the file and something follows it; offsets in a file
    /// with junk before the header may be relative to it. Zero otherwise.
    /// </summary>
    private static int FindHeader(byte[] data)
    {
        int searchLength = Math.Clamp(data.Length - 1, 0, HeaderSearchLength + HeaderMarker.Length - 1);
        return Math.Max(0, data.AsSpan(0, searchLength).IndexOf(HeaderMarker));
    }

    /// <summary>The offset after the last "startxref" keyword near the end of the file, or -1.</summary>
    private long FindStartXref()
    {
        int tailStart = Math.Max(0, Data.Length - StartxrefSearchLength);
        int keyword = Data.AsSpan(tailStart).LastIndexOf(StartxrefKeyword);
        if (keyword < 0) return -1;
        var lexer = new PdfLexer(Data, tailStart + keyword + StartxrefKeyword.Length);
        return lexer.Next() == TokenKind.Integer ? lexer.Integer : -1;
    }

    /// <summary>
    /// Reads the newest cross-reference section and the older ones it points at through /Prev, so that the newest entry
    /// of each object wins. Returns false when a section of the chain is unreadable.
    /// </summary>
    private bool ReadXrefChain(long offset)
    {
        var visited = new HashSet<long>();
        PdfDict? newest = null;
        while (offset >= 0 && visited.Add(offset))
        {
            var trailer = ReadXrefSection(offset) ?? ReadXrefSection(offset + _headerOffset);
            if (trailer == null) return false;
            newest ??= trailer;
            // Hybrid file: the classic table's trailer points at an xref stream with the compressed objects.
            if (trailer["XRefStm"] is PdfNumber hybrid && visited.Add((long)hybrid.Value)) ReadXrefSection((long)hybrid.Value);
            if (trailer["Prev"] is not PdfNumber previous) break;
            offset = (long)previous.Value;
        }
        if (newest == null) return false;
        Trailer = newest;
        return true;
    }

    /// <summary>Reads one xref table or stream at <paramref name="offset"/>, adding entries not already known. Returns its trailer.</summary>
    private PdfDict? ReadXrefSection(long offset)
    {
        if (offset < 0 || offset >= Data.Length) return null;
        var lexer = new PdfLexer(Data, (int)offset);
        var kind = lexer.Next();
        if (kind == TokenKind.Keyword && lexer.KeywordIs("xref")) return ReadXrefTable(lexer);
        if (kind == TokenKind.Integer)
        {
            // Cross-reference stream: "n g obj << ... >> stream".
            lexer.Pos = (int)offset;
            var obj = ParseIndirectAt(lexer, out int num, out _, decrypt: false);
            if (obj is not PdfStream stream || GetName(stream, "Type") != "XRef") return null;
            if (!_cache.ContainsKey(num)) _cache[num] = stream;
            ReadXrefStream(stream);
            return stream;
        }
        return null;
    }

    // ------------------------------------------------------------------ tables

    /// <summary>Reads the subsections after the "xref" keyword and then the trailer dictionary, or returns null for a damaged table.</summary>
    private PdfDict? ReadXrefTable(PdfLexer lexer)
    {
        while (true)
        {
            int subsectionStart = lexer.Pos;
            var kind = lexer.Next();
            if (kind == TokenKind.Keyword && lexer.KeywordIs("trailer")) break;
            if (kind != TokenKind.Integer)
            {
                // Something else than a subsection or the trailer keyword: the table ends and that is the trailer.
                lexer.Pos = subsectionStart;
                break;
            }
            long firstNumber = lexer.Integer;
            if (lexer.Next() != TokenKind.Integer) return null;
            long count = lexer.Integer;
            if (!ReadXrefSubsection(lexer, firstNumber, count)) return null;
        }
        return lexer.ReadObject() as PdfDict;
    }

    /// <summary>Reads <paramref name="count"/> entries of "offset generation n|f", returning false when one is malformed.</summary>
    private bool ReadXrefSubsection(PdfLexer lexer, long firstNumber, long count)
    {
        for (long i = 0; i < count; i++)
        {
            if (lexer.Next() != TokenKind.Integer) return false;
            long offset = lexer.Integer;
            if (lexer.Next() != TokenKind.Integer) return false;
            long generation = lexer.Integer;
            if (lexer.Next() != TokenKind.Keyword) return false;
            bool inUse = lexer.KeywordIs("n");
            int num = (int)(firstNumber + i);
            if (_xref.ContainsKey(num)) continue;
            _xref[num] = inUse && offset > 0 ? XrefEntry.AtOffset(offset, (int)generation) : XrefEntry.Free;
        }
        return true;
    }

    // ------------------------------------------------------------------ streams

    /// <summary>Adds the entries of a cross-reference stream: fixed-width big-endian rows, grouped by /Index.</summary>
    private void ReadXrefStream(PdfStream stream)
    {
        if (Resolve(stream["W"]) is not PdfArray widths || widths.Count < 3) return;
        var layout = new XrefRowLayout(GetIntValue(widths[0]), GetIntValue(widths[1]), GetIntValue(widths[2]));
        if (!layout.IsValid) return;
        int size = GetInt(stream, "Size", 0);
        var subsections = new List<(int Start, int Count)>();
        if (Resolve(stream["Index"]) is PdfArray index)
        {
            for (int i = 0; i + 1 < index.Count; i += 2) subsections.Add((GetIntValue(index[i]), GetIntValue(index[i + 1])));
        }
        else
        {
            subsections.Add((0, size));
        }
        var rows = DecodeStream(stream);
        int rowLength = layout.RowLength;
        if (rowLength == 0) return;
        int position = 0;
        foreach (var (start, count) in subsections)
        {
            for (int i = 0; i < count && position + rowLength <= rows.Length; i++, position += rowLength)
            {
                int num = start + i;
                var entry = ReadXrefRow(rows.AsSpan(position, rowLength), layout);
                if (!_xref.ContainsKey(num)) _xref[num] = entry;
            }
        }
    }

    /// <summary>Reads one row. A missing type field means an object in the file, as the specification says.</summary>
    private static XrefEntry ReadXrefRow(ReadOnlySpan<byte> row, XrefRowLayout layout)
    {
        long type = layout.TypeWidth == 0 ? XrefTypeInFile : ReadBigEndian(row.Slice(0, layout.TypeWidth));
        long second = ReadBigEndian(row.Slice(layout.TypeWidth, layout.SecondWidth));
        long third = ReadBigEndian(row.Slice(layout.TypeWidth + layout.SecondWidth, layout.ThirdWidth));
        return type switch
        {
            XrefTypeInFile => XrefEntry.AtOffset(second, (int)third),
            XrefTypeInObjectStream => XrefEntry.InStream(second, (int)third),
            _ => XrefEntry.Free,
        };
    }

    /// <summary>Reads a big-endian unsigned integer of up to eight bytes.</summary>
    private static long ReadBigEndian(ReadOnlySpan<byte> field)
    {
        long value = 0;
        foreach (byte b in field) value = (value << 8) | b;
        return value;
    }
}
