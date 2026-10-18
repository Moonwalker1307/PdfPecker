namespace PdfPecker.Pdf;

/// <summary>
/// The document-level objects: the file header, catalog, page tree, document information, XMP metadata, output
/// intent, attachments, encryption dictionary and the cross-reference table with the trailer.
/// </summary>
internal sealed partial class PdfDocumentWriter
{
    /// <summary>
    /// Writes the header: the version (PDF 1.7 for what PDF 1.4 lacks, PDF 2.0 for PDF/A-4 and PDF/UA-2) and a comment
    /// of bytes above 127, which marks the file as binary for tools that guess.
    /// </summary>
    private void WriteHeader()
        => _out.Ascii(Pdf20 ? "%PDF-2.0\n%" : IsPdfA || _crypt != null || Tagged ? "%PDF-1.7\n%" : "%PDF-1.4\n%").Bytes(new byte[] { 0xE2, 0xE3, 0xCF, 0xD3 }).Nl();

    private void WriteCatalog(WriteState doc)
    {
        Begin(doc.CatalogId).Ascii("<< /Type /Catalog /Pages ").Int(doc.PagesId).Ascii(" 0 R");
        if (doc.OutlinesId != 0) _out.Ascii(" /Outlines ").Int(doc.OutlinesId).Ascii(" 0 R");
        if (doc.MetadataId != 0) _out.Ascii(" /Metadata ").Int(doc.MetadataId).Ascii(" 0 R");
        if (IsPdfA) _out.Ascii(" /OutputIntents [").Int(doc.OutputIntentId).Ascii(" 0 R]");
        if (Tagged)
        {
            _out.Ascii(" /MarkInfo << /Marked true >> /StructTreeRoot ").Int(doc.StructRootId).Ascii(" 0 R");
            if (!string.IsNullOrEmpty(Language)) { _out.Ascii(" /Lang "); WriteTextString(_out, Language!); }
        }
        // PDF/UA: viewers show the title, not the file name. A right-to-left document's pages progress from right to
        // left, which a viewer showing two pages side by side needs to know to put them in the right order.
        bool displayTitle = Tagged && !string.IsNullOrEmpty(doc.Title);
        if (displayTitle || RightToLeft)
            _out.Ascii(" /ViewerPreferences <<").Ascii(displayTitle ? " /DisplayDocTitle true" : "").Ascii(RightToLeft ? " /Direction /R2L" : "").Ascii(" >>");
        if (doc.FileSpecIds.Length > 0) WriteEmbeddedFilesEntries(doc.FileSpecIds);
        WriteAcroForm();
        // AES-256 (revision 6) is the Adobe extension level 8 to PDF 1.7, standardised in PDF 2.0.
        if (_crypt != null && !Pdf20) _out.Ascii(" /Extensions << /ADBE << /BaseVersion /1.7 /ExtensionLevel 8 >> >>");
        _out.Ascii(" >>");
        End();
    }

    /// <summary>
    /// The catalog's entries for the attachments: they are listed in the name tree viewers show and are associated
    /// files (/AF), as PDF/A-3 needs.
    /// </summary>
    private void WriteEmbeddedFilesEntries(int[] fileSpecIds)
    {
        _out.Ascii(" /Names << /EmbeddedFiles << /Names [");
        for (int i = 0; i < fileSpecIds.Length; i++)
        {
            if (i > 0) _out.Sp();
            WriteTextString(_out, Attachments[i].FileName);
            _out.Sp().Int(fileSpecIds[i]).Ascii(" 0 R");
        }
        _out.Ascii("] >> >> /AF [");
        for (int i = 0; i < fileSpecIds.Length; i++) _out.Ascii(i > 0 ? " " : "").Int(fileSpecIds[i]).Ascii(" 0 R");
        _out.Ascii("]");
    }

    private void WritePageTree(WriteState doc)
    {
        Begin(doc.PagesId).Ascii("<< /Type /Pages /Count ").Int(doc.PageCount).Ascii(" /Kids [");
        for (int i = 0; i < doc.PageCount; i++) _out.Int(doc.PageIds[i]).Ascii(" 0 R ");
        _out.Ascii("] >>");
        End();
    }

    /// <summary>Writes the document information dictionary (not in a PDF 2.0 file) and the XMP metadata (PDF/A and PDF/UA).</summary>
    private void WriteInfoAndMetadata(WriteState doc)
    {
        var props = Properties.WithStandard(doc.Title, doc.Author, doc.Subject);
        if (doc.MetadataId != 0) props = props.Cleaned(); // Info and XMP must agree, and XML cannot carry control characters.
        if (doc.InfoId != 0)
        {
            Begin(doc.InfoId);
            props.WriteInfo(_out, doc.Now, modDate: doc.MetadataId != 0, WriteTextString);
            End();
        }
        if (doc.MetadataId != 0)
        {
            var (part, level, rev) = PdfA.Identification(Conformance);
            var xmp = PdfA.Xmp(part, level, props, doc.Now, EInvoice, PdfUAPart, rev);
            WriteStream(doc.MetadataId, xmp, compress: false, "/Type /Metadata /Subtype /XML");
        }
    }

    /// <summary>Writes PDF/A's sRGB output intent and its ICC profile.</summary>
    private void WriteOutputIntent(WriteState doc)
    {
        Begin(doc.OutputIntentId).Ascii("<< /Type /OutputIntent /S /GTS_PDFA1 /OutputConditionIdentifier ");
        WriteTextString(_out, PdfA.OutputCondition);
        _out.Ascii(" /Info ");
        WriteTextString(_out, PdfA.OutputCondition);
        _out.Ascii(" /DestOutputProfile ").Int(doc.IccId).Ascii(" 0 R >>");
        End();
        WriteStream(doc.IccId, PdfA.SrgbProfile, _compress, "/N 3");
    }

    /// <summary>
    /// Writes an attachment's file specification (with the /Desc PDF/UA asks for and PDF/A-3's /AFRelationship) at
    /// <paramref name="specId"/> and its embedded file stream at the next number.
    /// </summary>
    private void WriteAttachment(PdfAttachment a, int specId, DateTime now)
    {
        int fileId = specId + 1;
        Begin(specId).Ascii("<< /Type /Filespec /F ");
        WriteTextString(_out, a.FileName);
        _out.Ascii(" /UF ");
        WriteTextString(_out, a.FileName);
        _out.Ascii(" /Desc ");
        WriteTextString(_out, string.IsNullOrEmpty(a.Description) ? a.FileName : a.Description);
        _out.Ascii(" /AFRelationship /").Ascii(a.Relationship.ToString())
            .Ascii(" /EF << /F ").Int(fileId).Ascii(" 0 R /UF ").Int(fileId).Ascii(" 0 R >> >>");
        End();
        var dict = new ByteBuffer(160);
        dict.Ascii("/Type /EmbeddedFile /Subtype ");
        PdfSyntax.Name(dict, a.MimeTypeOrGuess);
        dict.Ascii(" /Params << /Size ").Int(a.Data.Length);
        if (a.CreationDate is { } created)
        {
            dict.Ascii(" /CreationDate ");
            WriteTextString(dict, EmbeddedFiles.PdfDate(created));
        }
        dict.Ascii(" /ModDate ");
        WriteTextString(dict, a.ModDate is { } mod ? EmbeddedFiles.PdfDate(mod) : EmbeddedFiles.PdfDate(now));
        if (!a.OmitChecksum)
        {
            dict.Ascii(" /CheckSum ");
            var md5 = System.Security.Cryptography.MD5.HashData(a.Data);
            PdfSyntax.Hex(dict, _crypt != null ? _crypt.Encrypt(md5) : md5, upper: true);
        }
        dict.Ascii(" >>");
        WriteStream(fileId, a.Data, _compress, System.Text.Encoding.ASCII.GetString(dict.Span));
    }

    private void WriteEncryption(WriteState doc)
    {
        Begin(doc.EncryptId);
        _crypt!.WriteDictionary(_out);
        End();
    }

    /// <summary>
    /// Writes the cross-reference table and the trailer, with a new random file identifier, or with one hashed from the
    /// file's bytes when the date is fixed, so that the same input gives the same file.
    /// </summary>
    private void WriteTrailer(WriteState doc)
    {
        long xref = _out.Length;
        int count = _xref.Count;
        // This writer numbers its objects up front and must write every one of them.
        System.Diagnostics.Debug.Assert(_xref.FirstMissing(count) < 0, "An object id was handed out but never written.");
        _xref.WriteTable(_out, count);
        var fileId = FixedDate != null ? ReproducibleDate.FileId(_out.Span) : System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        PdfXref.WriteTrailer(_out, count, doc.CatalogId, doc.InfoId, doc.EncryptId, fileId, fileId, upperHexId: true, xref);
    }
}
