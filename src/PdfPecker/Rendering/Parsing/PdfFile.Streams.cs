namespace PdfPecker.Rendering.Parsing;

/// <summary>Stream objects: reading their data after the "stream" keyword, decrypting them, and decoding their filters within the page's budget.</summary>
internal sealed partial class PdfFile
{
    private const byte CarriageReturn = (byte)'\r';
    private const byte LineFeed = (byte)'\n';

    // The specification wants "endstream" after the data, but a damaged keyword that starts the same way is accepted.
    private static ReadOnlySpan<byte> EndstreamStart => "endst"u8;

    /// <summary>
    /// Reads the stream whose "stream" keyword ends at <paramref name="afterKeyword"/>. The data starts after the
    /// end-of-line marker and runs for /Length bytes when "endstream" follows them; otherwise the file is searched for
    /// the end, since a wrong /Length is the most common damage.
    /// </summary>
    private PdfStream ReadStreamBody(int afterKeyword, PdfDict dict, int num, int gen, bool decrypt)
    {
        var scan = new ByteScanner(Data, afterKeyword);
        scan.TryConsume(CarriageReturn);
        scan.TryConsume(LineFeed);
        int start = scan.Position;

        // A /Length that refers to the stream's own object cannot be resolved; it is read as missing.
        var lengthObject = dict["Length"] is PdfRef lengthRef ? (lengthRef.Num == num ? null : GetObject(lengthRef.Num)) : dict["Length"];
        int length = GetIntValue(lengthObject);
        // The length is compared with the data left rather than added to the start, since a damaged /Length near
        // int.MaxValue would overflow the sum and lose the stream instead of falling back to the search.
        bool valid = length >= 0 && length <= Data.Length - start && EndstreamFollows(start + length);
        int end = start + length;
        if (!valid)
        {
            end = FindEndstream(start);
            // Trim the end-of-line marker before "endstream".
            if (end > start && Data[end - 1] == LineFeed) end--;
            if (end > start && Data[end - 1] == CarriageReturn) end--;
        }
        var raw = Data.AsSpan(start, Math.Max(0, end - start)).ToArray();

        bool decrypting = decrypt && _decryptor != null && num != _encryptObjNum;
        if (decrypting && StreamIsEncrypted(dict)) raw = _decryptor!.DecryptStream(raw, num, gen);
        var stream = new PdfStream(dict.Entries, raw) { ObjectNumber = num };
        if (decrypting) DecryptStrings(stream, num, gen);
        return stream;
    }

    /// <summary>Whether "endstream" (or at least its start) follows the whitespace at <paramref name="position"/>.</summary>
    private bool EndstreamFollows(int position)
    {
        var scan = new ByteScanner(Data, position);
        scan.SkipWhile<Whitespace>();
        if (scan.AtEnd) return false;
        var rest = scan.Rest;
        return rest.Length >= EndstreamKeyword.Length && rest.StartsWith(EndstreamStart);
    }

    /// <summary>
    /// Whether a stream's data is encrypted: not a cross-reference stream, not the XMP metadata of a file encrypted with
    /// /EncryptMetadata false, and not a stream whose own /Crypt filter is the Identity filter (the default when the
    /// filter names none).
    /// </summary>
    private bool StreamIsEncrypted(PdfDict dict)
    {
        var type = GetName(dict, "Type");
        if (type == "XRef" || type == "Metadata" && !_decryptor!.EncryptMetadata) return false;
        var filter = dict["Filter"];
        var parms = dict["DecodeParms"];
        if (filter is PdfArray filters && filters.Count > 0)
        {
            filter = filters[0];
            parms = parms is PdfArray parmsArray && parmsArray.Count > 0 ? parmsArray[0] : null;
        }
        if (filter is not PdfName { Value: "Crypt" }) return true;
        return parms is PdfDict cryptParms && cryptParms["Name"] is PdfName { Value: var name } && name != "Identity";
    }

    /// <summary>Decrypts every string inside <paramref name="o"/> in place.</summary>
    private PdfObject DecryptStrings(PdfObject o, int num, int gen)
    {
        switch (o)
        {
            case PdfString s:
                s.Bytes = _decryptor!.DecryptString(s.Bytes, num, gen);
                return s;
            case PdfArray a:
                foreach (var item in a.Items) DecryptStrings(item, num, gen);
                return a;
            case PdfDict d:
                foreach (var item in d.Entries.Values) DecryptStrings(item, num, gen);
                return d;
            default:
                return o;
        }
    }

    // ------------------------------------------------------------------ decode budget

    // Work limits for the page being rendered on this thread (see BeginRender): content streams, forms, fonts and images
    // drawn many times or decompressing to huge sizes add up, and one page must not decode without bound.
    [ThreadStatic] private static long t_decodeBudget;
    [ThreadStatic] private static bool t_budgeted;
    [ThreadStatic] private static CancellationToken t_cancel;

    /// <summary>The most decoded stream bytes one page render may produce, counting repeated decodes.</summary>
    public const long MaxDecodedPerPage = 512L * 1024 * 1024;

    /// <summary>What the page being rendered on this thread may still decode.</summary>
    internal static long DecodeLimit => t_budgeted ? Math.Max(0, t_decodeBudget) : long.MaxValue;

    /// <summary>
    /// Applies a decode budget (by default the per-page one) and <paramref name="cancel"/> to decoding on this thread
    /// until disposed.
    /// </summary>
    public static RenderScope BeginRender(CancellationToken cancel, long budget = MaxDecodedPerPage)
    {
        t_budgeted = true;
        t_decodeBudget = budget;
        t_cancel = cancel;
        return default;
    }

    public readonly struct RenderScope : IDisposable
    {
        public void Dispose()
        {
            t_budgeted = false;
            t_cancel = default;
        }
    }

    /// <summary>Charges memory an image codec allocates against the page's decode budget, and checks for cancellation.</summary>
    internal static void ChargeDecoded(long bytes)
    {
        t_cancel.ThrowIfCancellationRequested();
        if (t_budgeted && (t_decodeBudget -= bytes) < 0) throw BudgetExceeded();
    }

    /// <summary>True while a <see cref="BeginRender"/> scope is open on this thread.</summary>
    internal static bool InRenderScope => t_budgeted;

    /// <summary>The cancellation token of the render running on this thread.</summary>
    internal static CancellationToken CurrentCancellation => t_cancel;

    /// <summary>Throws if the render running on this thread was cancelled.</summary>
    internal static void CheckCancel() => t_cancel.ThrowIfCancellationRequested();

    private static InvalidDataException BudgetExceeded() => new("The page decodes to more data than allowed.");

    // ------------------------------------------------------------------ decoding

    /// <summary>Decodes a stream through all its filters.</summary>
    public byte[] DecodeStream(PdfStream s) => DecodeStream(s, stopAtImageFilter: false, out _, out _);

    /// <summary>
    /// Decodes a stream. With <paramref name="stopAtImageFilter"/>, decoding stops before an image codec (DCT, JPX,
    /// CCITT, JBIG2), which is returned in <paramref name="imageFilter"/> with its parameters.
    /// </summary>
    public byte[] DecodeStream(PdfStream s, bool stopAtImageFilter, out string? imageFilter, out PdfDict? imageParms)
    {
        imageFilter = null;
        imageParms = null;
        var filterObject = Resolve(s["Filter"] ?? s["F"]);
        var parmsObject = Resolve(s["DecodeParms"] ?? s["DP"]);
        var data = s.RawData;
        if (filterObject == null || filterObject.IsNull) return data;
        foreach (var (name, parms) in FilterChain(filterObject, parmsObject))
        {
            if (Filters.IsImageFilter(name))
            {
                if (!stopAtImageFilter) throw new NotSupportedException("Image filter " + name + " in a non-image stream.");
                imageFilter = name;
                imageParms = parms;
                return data;
            }
            t_cancel.ThrowIfCancellationRequested();
            if (t_budgeted && t_decodeBudget <= 0) throw BudgetExceeded();
            data = Filters.Decode(name, data, parms, this);
            ChargeDecoded(data.Length);
        }
        return data;
    }

    /// <summary>
    /// The filters of a stream in decoding order, each with its parameter dictionary: /Filter is one name or an array
    /// of names, /DecodeParms one dictionary or an array parallel to the names.
    /// </summary>
    private List<(string Name, PdfDict? Parms)> FilterChain(PdfObject filterObject, PdfObject? parmsObject)
    {
        var chain = new List<(string, PdfDict?)>();
        if (filterObject is PdfName single)
        {
            var parms = parmsObject as PdfDict ?? (parmsObject is PdfArray parmsArray && parmsArray.Count > 0 ? Resolve(parmsArray[0]) as PdfDict : null);
            chain.Add((Filters.Canonical(single.Value), parms));
        }
        else if (filterObject is PdfArray filters)
        {
            for (int i = 0; i < filters.Count; i++)
            {
                if (Resolve(filters[i]) is not PdfName name) continue;
                var parms = parmsObject is PdfArray parmsArray
                    ? (i < parmsArray.Count ? Resolve(parmsArray[i]) as PdfDict : null)
                    : i == 0 ? parmsObject as PdfDict : null;
                chain.Add((Filters.Canonical(name.Value), parms));
            }
        }
        return chain;
    }
}
