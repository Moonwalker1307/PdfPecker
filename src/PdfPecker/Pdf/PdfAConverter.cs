using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Pdf;

/// <summary>
/// Converts a parsed PDF to PDF/A-2 or PDF/A-3 (level B or U) in place, or with <c>fix</c> off checks one and reports
/// what breaks the rules. The rules are those of ISO 19005-2 and -3 as veraPDF checks them; each step below names the
/// clauses it covers. <see cref="PdfToPdfA"/> runs it once to fix and once more on the written result to report what is
/// left.
/// </summary>
/// <remarks>
/// The class is split by concern: this file walks the objects and handles the file structure, the catalog and the
/// actions; <c>PdfAConverter.Content.cs</c> reads the content streams for the fonts, colours and operators they use;
/// <c>PdfAConverter.Graphics.cs</c> handles colour, the output intent, graphics states and images;
/// <c>PdfAConverter.Fonts.cs</c> the fonts; <c>PdfAConverter.Annotations.cs</c> annotations, forms, optional content
/// and attachments; and <c>PdfAConverter.Metadata.cs</c> the XMP metadata.
/// </remarks>
internal sealed partial class PdfAConverter
{
    /// <summary>How deep the walks below follow nested objects before giving up on a branch.</summary>
    private const int MaxDepth = 64;

    private readonly PdfFile _file;
    private readonly PdfAReport _report;
    private readonly bool _fix;
    private readonly int _depth;
    private readonly int _part;
    private readonly bool _unicode;
    private readonly PdfConformance _conformance;

    public PdfAConverter(PdfFile file, PdfConformance conformance, PdfAReport report, bool fix, int depth)
    {
        _file = file;
        _conformance = conformance;
        _report = report;
        _fix = fix;
        _depth = depth;
        _part = conformance is PdfConformance.PdfA3b or PdfConformance.PdfA3u ? 3 : 2;
        _unicode = conformance is PdfConformance.PdfA2u or PdfConformance.PdfA3u;
    }

    /// <summary>Records something the conversion changed (fixing) or something that breaks the rules (checking).</summary>
    private void Found(string fixedTemplate, string problemTemplate, string? name = null)
        => _report.Add(_fix ? fixedTemplate : problemTemplate, name);

    /// <summary>Records something that breaks the rules and that the conversion cannot fix.</summary>
    private void Problem(string template, string? name = null)
    {
        if (!_fix) _report.Add(template, name);
    }

    public void Run()
    {
        if (_fix) Preload();
        // Reading every object can make a damaged file rebuild its cross-reference table, which can lose the catalog.
        if (_file.Catalog is not { } catalog) throw new InvalidDataException("The PDF is not readable: it has no catalog.");
        CatalogEntries(catalog);
        Actions();
        // The output intent decides which device colours need a profile, which the walks below record.
        OutputIntent(catalog);
        WalkObjects();
        Pages(catalog);
        Fonts();
        Colours();
        OptionalContent(catalog);
        Forms(catalog);
        Attachments(catalog);
        Metadata(catalog);
    }

    /// <summary>
    /// Loads every object the file uses before anything changes, so that a damaged cross-reference table is rebuilt now
    /// (which forgets the objects parsed so far) rather than while the result is written.
    /// </summary>
    private void Preload()
    {
        for (int pass = 0; pass < 2; pass++)
        {
            bool before = _file.Reconstructed;
            var seen = new HashSet<int>();
            var stack = new Stack<PdfObject>();
            foreach (var value in _file.Trailer.Entries.Values) stack.Push(value);
            int visited = 0;
            while (stack.Count > 0)
            {
                if ((++visited & 1023) == 0) PdfFile.CheckCancel();
                var o = stack.Pop();
                if (o is PdfRef r)
                {
                    if (!seen.Add(r.Num)) continue;
                    o = _file.GetObject(r.Num);
                }
                switch (o)
                {
                    case PdfArray a:
                        foreach (var item in a.Items) stack.Push(item);
                        break;
                    case PdfDict d:
                        foreach (var value in d.Entries.Values) stack.Push(value);
                        break;
                }
            }
            if (_file.Reconstructed == before) return;
        }
    }

    // ------------------------------------------------------------------ catalog (6.1.12, 6.4.2, 6.10, 6.11)

    private void CatalogEntries(PdfDict catalog)
    {
        if (_file.GetName(catalog, "Type") != "Catalog")
        {
            Found("Added the catalog's missing /Type entry.", "The catalog lacks its /Type /Catalog entry.");
            if (_fix) catalog.Entries["Type"] = PdfName.Get("Catalog");
        }
        if (_file.GetDict(catalog, "Pages") == null) Problem("The document has no page tree, so it has no pages.");
        // PDF/A-2 and -3 are based on PDF 1.7: a catalog that claims a later version is written as 1.7.
        if (_file.GetName(catalog, "Version") is { } version && string.CompareOrdinal(version, "1.7") > 0)
        {
            Found("Wrote the file as PDF 1.7, the version PDF/A-2 and PDF/A-3 are based on, instead of PDF {names}.",
                "The catalog claims PDF {names}, which PDF/A-2 and PDF/A-3 do not allow.", version);
            if (_fix) catalog.Entries.Remove("Version");
        }
        foreach (var key in new[] { "NeedsRendering", "Requirements" })
        {
            if (!catalog.ContainsKey(key)) continue;
            Found("Removed the catalog's {names} entry, which PDF/A forbids.", "The catalog has a {names} entry, which PDF/A forbids.", key);
            if (_fix) catalog.Entries.Remove(key);
        }
        if (_file.GetDict(catalog, "Names") is { } names && names.ContainsKey("AlternatePresentations"))
        {
            Found("Removed the alternate presentations (slide shows), which PDF/A forbids.", "The document has alternate presentations, which PDF/A forbids.");
            if (_fix) names.Entries.Remove("AlternatePresentations");
        }
    }

    // ------------------------------------------------------------------ actions (6.5, 6.4.1)

    // Actions PDF/A allows; every other type goes, and so do named actions other than page navigation.
    private static readonly HashSet<string> AllowedActions = new(StringComparer.Ordinal) { "GoTo", "GoToR", "GoToE", "Thread", "URI", "Named", "SubmitForm" };
    private static readonly HashSet<string> AllowedNamedActions = new(StringComparer.Ordinal) { "NextPage", "PrevPage", "FirstPage", "LastPage" };

    /// <summary>
    /// Removes the actions PDF/A forbids (6.5.1) and all additional actions (6.5.2, 6.4.1): first everything the
    /// sanitizer removes as active content (scripts, launching programs, opening other files, submitting forms,
    /// media), then the rest of what PDF/A forbids, such as hiding fields, resetting forms and named actions other
    /// than page navigation.
    /// </summary>
    private void Actions()
    {
        if (_fix)
        {
            int removed = ActiveContent.Process(_file, remove: true, attachments: false, CancellationToken.None);
            if (removed > 0)
                _report.Add(removed == 1
                    ? "Removed a script or action that runs on its own or reaches outside the document, which PDF/A forbids."
                    : $"Removed {removed} scripts and actions that run on their own or reach outside the document, which PDF/A forbids.");
        }
        VisitDicts((dict, isCatalog) =>
        {
            if (dict.ContainsKey("AA"))
            {
                Found("Removed {0} additional action{s} (actions run when a page, field or the document is opened, changed or closed), which PDF/A forbids.",
                    "The document has {0} additional action{s} (/AA), which PDF/A forbids.");
                if (_fix) dict.Entries.Remove("AA");
            }
            foreach (var key in ActionKeys)
            {
                // Structure elements use /A for attributes, which have no action type.
                if (_file.Resolve(dict[key]) is not PdfDict action || !IsAction(action) || IsAllowedAction(action)) continue;
                ForbiddenAction(action);
                if (_fix) dict.Entries.Remove(key);
            }
            if (_file.Resolve(dict["Next"]) is { } next && _file.GetName(dict, "S") != null)
            {
                if (next is PdfDict single && IsAction(single) && !IsAllowedAction(single))
                {
                    ForbiddenAction(single);
                    if (_fix) dict.Entries.Remove("Next");
                }
                else if (next is PdfArray chain)
                {
                    for (int i = chain.Count - 1; i >= 0; i--)
                    {
                        if (_file.Resolve(chain[i]) is not PdfDict a || !IsAction(a) || IsAllowedAction(a)) continue;
                        ForbiddenAction(a);
                        if (_fix) chain.Items.RemoveAt(i);
                    }
                }
            }
        });
    }

    private static readonly string[] ActionKeys = { "A", "OpenAction" };

    private bool IsAction(PdfDict dict) => _file.GetName(dict, "S") != null || _file.GetName(dict, "Type") == "Action";

    private bool IsAllowedAction(PdfDict action)
    {
        var type = _file.GetName(action, "S");
        if (type == null || !AllowedActions.Contains(type)) return false;
        return type != "Named" || _file.GetName(action, "N") is { } n && AllowedNamedActions.Contains(n);
    }

    private void ForbiddenAction(PdfDict action)
    {
        var type = _file.GetName(action, "S") ?? "unknown";
        if (type == "Named") type += " " + (_file.GetName(action, "N") ?? "");
        Found("Removed {0} action{s} that PDF/A forbids{names}.", "The document has {0} action{s} that PDF/A forbids{names}.", type);
    }

    // ------------------------------------------------------------------ the object walk

    /// <summary>Calls <paramref name="visit"/> once for every dictionary (and stream) the catalog reaches.</summary>
    private void VisitDicts(Action<PdfDict, bool> visit)
    {
        var catalog = _file.Catalog;
        var seen = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<PdfObject>();
        stack.Push(_file.Trailer["Root"]!);
        if (_file.Trailer["Info"] is { } info) stack.Push(info);
        int visited = 0;
        while (stack.Count > 0)
        {
            if ((++visited & 1023) == 0) PdfFile.CheckCancel();
            var o = _file.Resolve(stack.Pop());
            if (o == null || !seen.Add(o)) continue;
            if (o is PdfArray array)
            {
                foreach (var item in array.Items) stack.Push(item);
                continue;
            }
            if (o is not PdfDict dict) continue;
            visit(dict, ReferenceEquals(dict, catalog));
            foreach (var value in dict.Entries.Values) stack.Push(value);
        }
    }

    /// <summary>
    /// One pass over every object: stream filters and external data (6.1.7), metadata streams of objects other than the
    /// catalog, images (6.2.8), form and PostScript XObjects (6.2.9), resource dictionaries (graphics states, 6.2.5 and
    /// 6.2.10) and the implementation limits (6.1.13).
    /// </summary>
    private void WalkObjects()
    {
        var catalog = _file.Catalog;
        VisitDicts((dict, isCatalog) =>
        {
            foreach (var (key, value) in dict.Entries)
            {
                if (key.Length > MaxNameBytes) Problem("The document has {0} name{s} longer than 127 bytes, the PDF/A limit.");
                Limits(value);
                if (_file.Resolve(value) is PdfArray { Count: >= 2 } array) ColourSpaceArray(array, 0);
            }
            if (!isCatalog && dict.ContainsKey("Metadata") && _file.Resolve(dict["Metadata"]) is PdfStream)
            {
                // PDF/A holds every metadata stream to its XMP rules; those of pages, images and fonts are rarely kept
                // to them, and nothing needs them.
                if (_fix)
                {
                    _report.Add("Removed {0} metadata stream{s} of pages, images and other objects; the document's own metadata stays.");
                    dict.Entries.Remove("Metadata");
                }
            }
            if (dict is PdfStream stream) Stream(stream);
            if (_file.Resolve(dict["Resources"]) is PdfDict resources) Resources(resources);
            switch (_file.GetName(dict, "Subtype"))
            {
                case "Image" when dict is PdfStream image:
                    Image(image);
                    break;
                case "Form" when dict is PdfStream form:
                    Form(form);
                    break;
            }
        });
    }

    private const int MaxNameBytes = 127;
    private const int MaxStringBytes = 32767;

    // The implementation limits PDF/A-2 keeps from PDF 1.7 (6.1.13) that a writer cannot simply spell differently.
    private void Limits(PdfObject value)
    {
        switch (value)
        {
            case PdfString s when s.Bytes.Length > MaxStringBytes:
                Problem("The document has {0} string{s} longer than 32767 bytes, the PDF/A limit.");
                break;
            case PdfName n when n.Value.Length > MaxNameBytes:
                Problem("The document has {0} name{s} longer than 127 bytes, the PDF/A limit.");
                break;
            case PdfNumber { IsInteger: true } i when i.Value > int.MaxValue || i.Value < int.MinValue:
                Problem("The document has {0} integer{s} outside the 32-bit range, the PDF/A limit.");
                break;
            case PdfNumber r when Math.Abs(r.Value) > 3.403e38:
                Problem("The document has {0} number{s} beyond 3.403 × 10^38, the PDF/A limit.");
                break;
            case PdfArray a:
                foreach (var item in a.Items)
                    if (item is not (PdfArray or PdfDict)) Limits(item);
                break;
        }
    }

    // Filters PDF/A allows (6.1.7.2); LZW is re-encoded with Flate, and an identity Crypt filter is dropped by the writer.
    private static readonly HashSet<string> AllowedFilters = new(StringComparer.Ordinal)
    {
        "ASCIIHexDecode", "ASCII85Decode", "FlateDecode", "RunLengthDecode", "CCITTFaxDecode", "JBIG2Decode", "DCTDecode", "JPXDecode",
    };

    /// <summary>External stream data (6.1.7.1) and filters (6.1.7.2).</summary>
    private void Stream(PdfStream s)
    {
        if (s.ContainsKey("F") || s.ContainsKey("FFilter") || s.ContainsKey("FDecodeParms"))
        {
            Found("Removed the references to external files of {0} stream{s}; their data in the file is used.",
                "{0} stream{s} refer to external files, which PDF/A forbids.");
            if (_fix)
            {
                s.Entries.Remove("F");
                s.Entries.Remove("FFilter");
                s.Entries.Remove("FDecodeParms");
            }
        }
        var filter = _file.Resolve(s["Filter"]);
        var names = filter switch
        {
            PdfName single => new List<string> { single.Value },
            PdfArray array => array.Items.Select(i => (_file.Resolve(i) as PdfName)?.Value ?? "").ToList(),
            _ => null,
        };
        if (names == null) return;
        bool lzw = false, unknown = false;
        foreach (var name in names)
        {
            var canonical = Filters.Canonical(name);
            if (canonical == "LZWDecode") lzw = true;
            else if (canonical == "Crypt") continue;
            else if (!AllowedFilters.Contains(name)) unknown = true;
        }
        if (unknown)
        {
            // Abbreviated names (Fl, AHx) belong to inline images; in a stream they are not standard.
            if (_fix && !lzw && Recompress(s)) _report.Add("Re-encoded {0} stream{s} that used filters PDF/A does not allow with Flate.");
            else Problem("{0} stream{s} use filters PDF/A does not allow.");
            return;
        }
        if (!lzw) return;
        if (!_fix)
        {
            Problem("{0} stream{s} use LZW compression, which PDF/A forbids.");
            return;
        }
        if (Recompress(s)) _report.Add("Re-encoded {0} stream{s} that used LZW compression, which PDF/A forbids, with Flate.");
        else _report.Add("{0} stream{s} use LZW compression, which PDF/A forbids, and could not be decoded to re-encode them.");
    }

    /// <summary>
    /// Decodes a stream up to its image codec, if any, and encodes the result with Flate in front of that codec. False
    /// when the stream cannot be decoded.
    /// </summary>
    private bool Recompress(PdfStream s)
    {
        byte[] data;
        string? imageFilter;
        PdfDict? imageParms;
        try
        {
            data = _file.DecodeStream(s, stopAtImageFilter: true, out imageFilter, out imageParms);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or PdfFormatException or IndexOutOfRangeException or ArgumentException)
        {
            SwallowedErrors.Record(ex);
            return false;
        }
        s.RawData = Deflate(data);
        s.Entries.Remove("DecodeParms");
        s.Entries.Remove("DP");
        if (imageFilter == null)
        {
            s.Entries["Filter"] = PdfName.Get("FlateDecode");
        }
        else
        {
            s.Entries["Filter"] = new PdfArray(new List<PdfObject> { PdfName.Get("FlateDecode"), PdfName.Get(imageFilter) });
            if (imageParms != null) s.Entries["DecodeParms"] = new PdfArray(new List<PdfObject> { PdfNull.Instance, imageParms });
        }
        return true;
    }

    internal static byte[] Deflate(byte[] data) => Images.PdfImage.Deflate(data, System.IO.Compression.CompressionLevel.Optimal);

    // ------------------------------------------------------------------ helpers

    private string FontName(PdfDict font)
    {
        var name = _file.GetName(font, "BaseFont");
        if (name == null && _file.GetArray(font, "DescendantFonts") is { Count: > 0 } d && _file.Resolve(d[0]) is PdfDict cid)
            name = _file.GetName(cid, "BaseFont");
        if (name == null) return _file.GetName(font, "Subtype") == "Type3" ? _file.GetName(font, "Name") ?? "Type 3" : "unnamed";
        // A subset tag such as ABCDEF+ says nothing to a reader.
        return name.Length > 7 && name[6] == '+' && name.AsSpan(0, 6).IndexOfAnyExceptInRange('A', 'Z') < 0 ? name[7..] : name;
    }

    private static PdfDict NewDict(params (string Key, PdfObject Value)[] entries)
    {
        var d = new PdfDict();
        foreach (var (key, value) in entries) d.Entries[key] = value;
        return d;
    }

    private static PdfArray NewArray(params PdfObject[] items) => new(items.ToList());
}
