using System.Globalization;

namespace PdfPecker.Rendering.Parsing;

/// <summary>
/// Recovery of damaged files: rebuilding the object table from the "num gen obj" headers in the file, and finding
/// the end of a stream whose /Length is wrong.
/// </summary>
internal sealed partial class PdfFile
{
    // An object number with more digits than this, or a generation with more than six, is damage rather than a header.
    private const int MaxObjectNumberDigits = 10;
    private const int MaxGenerationDigits = 6;

    private static ReadOnlySpan<byte> ObjKeyword => "obj"u8;

    private static ReadOnlySpan<byte> EndobjKeyword => "endobj"u8;

    private static ReadOnlySpan<byte> EndstreamKeyword => "endstream"u8;

    private static ReadOnlySpan<byte> TrailerKeyword => "trailer"u8;

    private readonly struct Whitespace : IBytePredicate
    {
        public static bool Matches(byte value) => PdfLexer.IsWhite(value);
    }

    private readonly struct Digit : IBytePredicate
    {
        public static bool Matches(byte value) => value is >= (byte)'0' and <= (byte)'9';
    }

    /// <summary>
    /// A read position that moves towards the start of the data, for reading the "num gen" that precedes an "obj"
    /// keyword. <see cref="Position"/> is -1 once it has moved before the first byte.
    /// </summary>
    private ref struct BackwardScanner
    {
        private readonly ReadOnlySpan<byte> data;

        public BackwardScanner(ReadOnlySpan<byte> data, int position)
        {
            this.data = data;
            Position = position;
        }

        public int Position { get; private set; }

        public readonly bool AtStart => Position < 0;

        /// <summary>The byte under the cursor. The caller has checked <see cref="AtStart"/>.</summary>
        public readonly byte Current => data[Position];

        /// <summary>Moves back over the bytes that <typeparamref name="TPredicate"/> accepts and returns how many there were.</summary>
        public int SkipWhile<TPredicate>() where TPredicate : IBytePredicate
        {
            int start = Position;
            while (Position >= 0 && TPredicate.Matches(data[Position])) Position--;
            return start - Position;
        }
    }

    /// <summary>An object header "num gen obj" found by scanning the file.</summary>
    private readonly record struct ObjectHeader(int Start, int Number, int Generation);

    /// <summary>Rebuilds the object table by scanning the file for "n g obj" headers.</summary>
    /// <param name="opened">
    /// True when the file already opened through its own cross-reference table and only one entry was found wrong
    /// later. A scan that then finds no catalog keeps that table and trailer instead of failing: the file was readable
    /// a moment ago, and this runs deep inside a caller that does not expect the reader's exception.
    /// </param>
    private void Reconstruct(bool opened = false)
    {
        lock (_lock)
        {
            if (_reconstructed) return;
            _reconstructed = true;
            var previous = _xref;
            _xref = ScanObjectHeaders();
            _cache.Clear();
            _objStreams.Clear();
            var trailer = ScanTrailers();

            // Objects inside object streams, and trailer information from xref streams.
            foreach (var (num, _) in _xref.ToArray())
            {
                PdfObject? o;
                try
                {
                    o = LoadObject(num);
                }
                catch (Exception ex) when (ex is PdfFormatException or IndexOutOfRangeException or ArgumentException or InvalidOperationException)
                {
                    continue;
                }
                if (o is not PdfStream stream) continue;
                var type = GetName(stream, "Type");
                if (type == "XRef" && stream["Root"] != null) trailer ??= stream;
                else if (type == "ObjStm") AddObjectStreamEntries(num);
            }

            if (trailer == null || Resolve(trailer["Root"]) is not PdfDict) trailer = TrailerFromCatalog(trailer);
            if (trailer == null && opened)
            {
                _xref = previous;
                _cache.Clear();
                _objStreams.Clear();
                return;
            }
            if (trailer == null) throw new PdfFormatException("No document catalog found; the file is not a readable PDF.");
            Trailer = trailer;
        }
    }

    /// <summary>The objects of the file by their headers; a later definition of a number replaces an earlier one.</summary>
    private Dictionary<int, XrefEntry> ScanObjectHeaders()
    {
        var table = new Dictionary<int, XrefEntry>();
        var data = Data.AsSpan();
        foreach (int keyword in KeywordPositions(data, ObjKeyword))
        {
            // The keyword must be followed by something that is not part of it, so "object" and the end of the file do not count.
            if (keyword + ObjKeyword.Length >= data.Length || PdfLexer.IsRegular(data[keyword + ObjKeyword.Length])) continue;
            if (TryReadObjectHeader(data, keyword, out var header)) table[header.Number] = XrefEntry.AtOffset(header.Start, header.Generation);
        }
        return table;
    }

    /// <summary>
    /// Reads the "num gen" before the "obj" keyword at <paramref name="keyword"/>. The number has at most ten digits
    /// and the generation at most six, nothing but whitespace separates them, and a regular character before the
    /// number means it is the tail of a longer token.
    /// </summary>
    private static bool TryReadObjectHeader(ReadOnlySpan<byte> data, int keyword, out ObjectHeader header)
    {
        header = default;
        var scan = new BackwardScanner(data, keyword - 1);
        scan.SkipWhile<Whitespace>();
        int generationDigits = scan.SkipWhile<Digit>();
        if (generationDigits == 0 || generationDigits > MaxGenerationDigits) return false;
        int generationStart = scan.Position + 1;
        scan.SkipWhile<Whitespace>();
        int numberDigits = scan.SkipWhile<Digit>();
        if (numberDigits == 0 || numberDigits > MaxObjectNumberDigits) return false;
        int numberStart = scan.Position + 1;
        if (!scan.AtStart && PdfLexer.IsRegular(scan.Current)) return false;
        // Ten digits can still be more than an int holds; such a header is damage, not an object.
        if (!int.TryParse(data.Slice(numberStart, numberDigits), NumberStyles.None, null, out int number)) return false;
        int generation = int.Parse(data.Slice(generationStart, generationDigits), NumberStyles.None, null);
        header = new ObjectHeader(numberStart, number, generation);
        return true;
    }

    /// <summary>The trailer dictionaries with a /Root in the file, merged so that a later one's entries win.</summary>
    private PdfDict? ScanTrailers()
    {
        PdfDict? trailer = null;
        foreach (int keyword in KeywordPositions(Data, TrailerKeyword))
        {
            int dictionary = keyword + TrailerKeyword.Length;
            if (dictionary >= Data.Length) continue;
            try
            {
                if (new PdfLexer(Data, dictionary).ReadObject() is PdfDict found && found["Root"] != null) trailer = Merge(trailer, found);
            }
            catch (PdfFormatException)
            {
                // A trailer too damaged to parse is skipped.
            }
        }
        return trailer;
    }

    /// <summary>Adds the objects of object stream <paramref name="streamNum"/> that no header in the file defines.</summary>
    private void AddObjectStreamEntries(int streamNum)
    {
        try
        {
            var numbers = GetObjectStream(streamNum).Numbers;
            for (int index = 0; index < numbers.Length; index++)
            {
                if (!_xref.ContainsKey(numbers[index])) _xref[numbers[index]] = XrefEntry.InStream(streamNum, index);
            }
        }
        catch (Exception ex) when (ex is PdfFormatException or IndexOutOfRangeException or ArgumentException or NotSupportedException or InvalidDataException)
        {
            // An unreadable object stream contributes nothing.
        }
    }

    /// <summary>
    /// When no trailer names a readable catalog, finds the catalog by its type and makes a trailer pointing at it,
    /// keeping whatever else <paramref name="trailer"/> had. Null when the file has no catalog either.
    /// </summary>
    private PdfDict? TrailerFromCatalog(PdfDict? trailer)
    {
        foreach (var num in _xref.Keys.ToArray())
        {
            if (GetObject(num) is not PdfDict candidate || candidate is PdfStream || GetName(candidate, "Type") != "Catalog") continue;
            var result = new PdfDict();
            if (trailer != null) foreach (var kv in trailer.Entries) result.Entries[kv.Key] = kv.Value;
            result.Entries["Root"] = new PdfRef(num, 0);
            return result;
        }
        return trailer;
    }

    private static PdfDict Merge(PdfDict? older, PdfDict newer)
    {
        if (older == null) return newer;
        var merged = new PdfDict();
        foreach (var kv in older.Entries) merged.Entries[kv.Key] = kv.Value;
        foreach (var kv in newer.Entries) merged.Entries[kv.Key] = kv.Value;
        return merged;
    }

    /// <summary>Every position where <paramref name="keyword"/> occurs, in order.</summary>
    private static int[] KeywordPositions(ReadOnlySpan<byte> data, ReadOnlySpan<byte> keyword)
    {
        var positions = new List<int>();
        for (int from = 0; ;)
        {
            int found = data.Slice(from).IndexOf(keyword);
            if (found < 0) break;
            positions.Add(from + found);
            from += found + keyword.Length;
        }
        return positions.ToArray();
    }

    // ------------------------------------------------------------------ stream ends

    private KeywordIndex? _keywords;

    /// <summary>
    /// The positions of the keywords that can end a stream, found in one pass and binary searched: scanning ahead
    /// from every stream with a wrong /Length would be quadratic on a file made of such streams.
    /// </summary>
    private sealed class KeywordIndex
    {
        private readonly int[] endstreams;
        private readonly int[] endobjs;
        private readonly int[] headers;

        public KeywordIndex(byte[] data)
        {
            endstreams = KeywordPositions(data, EndstreamKeyword);
            endobjs = KeywordPositions(data, EndobjKeyword);
            headers = ObjectHeaderStarts(data);
        }

        /// <summary>
        /// Where the stream whose data starts at <paramref name="from"/> ends: at "endstream"; when that is missing, at
        /// "endobj" or at the next object header, never further: a file of unterminated streams must not give each
        /// one a copy of the rest of the file. The length of the data when nothing follows.
        /// </summary>
        public int StreamEnd(int from, int dataLength)
        {
            int end = NextAtOrAfter(endstreams, from);
            int endobj = NextAtOrAfter(endobjs, from);
            int header = NextAtOrAfter(headers, from);
            if (end < 0 || endobj >= 0 && endobj < end) end = endobj;
            if (end < 0 || header >= 0 && header < end) end = header;
            return end < 0 ? dataLength : end;
        }

        private static int NextAtOrAfter(int[] sorted, int from)
        {
            int index = Array.BinarySearch(sorted, from);
            if (index < 0) index = ~index;
            return index < sorted.Length ? sorted[index] : -1;
        }

        /// <summary>
        /// The starts of the "num gen obj" headers. Unlike the scan that rebuilds the table, this one asks only for
        /// whitespace between the three parts and accepts a number of any length, so it finds a few more.
        /// </summary>
        private static int[] ObjectHeaderStarts(ReadOnlySpan<byte> data)
        {
            var starts = new List<int>();
            foreach (int keyword in KeywordPositions(data, ObjKeyword))
            {
                var scan = new BackwardScanner(data, keyword - 1);
                if (scan.SkipWhile<Whitespace>() == 0 || scan.SkipWhile<Digit>() == 0) continue;
                if (scan.SkipWhile<Whitespace>() == 0 || scan.SkipWhile<Digit>() == 0) continue;
                starts.Add(scan.Position + 1);
            }
            return starts.ToArray();
        }
    }

    private int FindEndstream(int from)
    {
        KeywordIndex keywords;
        lock (_lock)
        {
            keywords = _keywords ??= new KeywordIndex(Data);
        }
        return keywords.StreamEnd(from, Data.Length);
    }
}
