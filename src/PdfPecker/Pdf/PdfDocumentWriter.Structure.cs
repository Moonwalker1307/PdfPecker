using System.IO.Compression;
using PdfPecker.Css;
using PdfPecker.Fonts;
using PdfPecker.Images;
using PdfPecker.Layout;
using PdfPecker.Svg;

namespace PdfPecker.Pdf;

/// <summary>The structure tree of a tagged PDF.</summary>
internal sealed partial class PdfDocumentWriter
{
    /// <summary>
    /// Writes the structure tree of a tagged PDF: its root, the elements, and the parent tree that maps each page's
    /// marked content and each link annotation back to its element. Returns the next free object number.
    /// </summary>
    private int WriteStructure(int next, List<StructElem> elements, int rootId, int nextKey, int[] pageIds, McStream[] pageStreams,
        Dictionary<int, int> annotKeys)
    {
        var tree = Structure!;
        int parentTreeId = next++;

        // PDF 2.0 files put every element in the PDF 2.0 standard structure namespace; the PDF 1.7 types that PDF 2.0
        // dropped stay in the PDF 1.7 namespace, role mapped to their nearest PDF 2.0 type.
        int ns2Id = 0, ns17Id = 0;
        if (Pdf20)
        {
            ns2Id = next++;
            if (elements.Any(e => Pdf17Only(e.Type) != null)) ns17Id = next++;
        }

        var b = BeginStructure(rootId);
        b.Ascii("<< /Type /StructTreeRoot /K ").Int(tree.Root.Id).Ascii(" 0 R /ParentTree ").Int(parentTreeId)
            .Ascii(" 0 R /ParentTreeNextKey ").Int(nextKey);
        // Elements with identifiers (footnote Notes) are listed in the ID tree, in the byte order of their identifiers.
        var identified = elements.Where(e => e.ElementId != null).OrderBy(e => e.ElementId, StringComparer.Ordinal).ToList();
        if (identified.Count > 0)
        {
            b.Ascii(" /IDTree << /Names [");
            foreach (var e in identified)
            {
                WriteAsciiString(b, e.ElementId!);
                b.Sp().Int(e.Id).Ascii(" 0 R ");
            }
            b.Ascii("] >>");
        }
        if (ns2Id != 0)
        {
            b.Ascii(" /Namespaces [").Int(ns2Id).Ascii(" 0 R");
            if (ns17Id != 0) b.Ascii(" ").Int(ns17Id).Ascii(" 0 R");
            b.Ascii("]");
        }
        b.Ascii(" >>");
        EndStructure();
        if (ns2Id != 0)
        {
            // Through WriteTextString, so an encrypted file encrypts the namespace names like every other string.
            b = BeginStructure(ns2Id);
            b.Ascii("<< /Type /Namespace /NS ");
            WriteTextString(b, "http://iso.org/pdf2/ssn");
            b.Ascii(" >>");
            EndStructure();
        }
        if (ns17Id != 0)
        {
            b = BeginStructure(ns17Id);
            b.Ascii("<< /Type /Namespace /NS ");
            WriteTextString(b, "http://iso.org/pdf/ssn");
            b.Ascii(" /RoleMapNS <<");
            foreach (var type in elements.Select(e => e.Type).Where(t => Pdf17Only(t) != null).Distinct())
                b.Ascii(" /").Ascii(type).Ascii(" [/").Ascii(Pdf17Only(type)!).Sp().Int(ns2Id).Ascii(" 0 R]");
            b.Ascii(" >> >>");
            EndStructure();
        }

        // The parent tree: a number tree with one flat /Nums array, in key order.
        var entries = new List<(int Key, McStream? Stream, StructElem? Link)>();
        foreach (var page in pageStreams)
        {
            if (page.Elements.Count > 0) entries.Add((page.Page, page, null));
            foreach (var (id, link) in page.Annotations) entries.Add((annotKeys[id], null, link));
        }
        foreach (var form in _formStreams)
            if (form is { Key: >= 0 }) entries.Add((form.Key, form, null));
        entries.Sort((x, y) => x.Key.CompareTo(y.Key));
        b = BeginStructure(parentTreeId);
        b.Ascii("<< /Nums [");
        foreach (var (key, stream, link) in entries)
        {
            b.Ascii("\n").Int(key).Sp();
            if (link != null) { b.Int(link.Id).Ascii(" 0 R"); continue; }
            b.Ascii("[");
            foreach (var e in stream!.Elements) b.Int(e.Id).Ascii(" 0 R ");
            b.Ascii("]");
        }
        b.Ascii("] >>");
        EndStructure();

        foreach (var e in elements)
        {
            b = BeginStructure(e.Id);
            b.Ascii("<< /Type /StructElem /S /").Ascii(e.Type).Ascii(" /P ").Int(e.Parent?.Id ?? rootId).Ascii(" 0 R");
            if (ns2Id != 0) b.Ascii(" /NS ").Int(Pdf17Only(e.Type) != null ? ns17Id : ns2Id).Ascii(" 0 R");
            if (e.Lang != null) { b.Ascii(" /Lang "); WriteTextString(b, e.Lang); }
            if (e.Alt != null) { b.Ascii(" /Alt "); WriteTextString(b, e.Alt); }
            if (e.ElementId != null) { b.Ascii(" /ID "); WriteAsciiString(b, e.ElementId); }
            if (e.Scope != null || e.RowSpan > 1 || e.ColSpan > 1)
            {
                b.Ascii(" /A << /O /Table");
                if (e.Scope != null) b.Ascii(" /Scope /").Ascii(e.Scope);
                if (e.RowSpan > 1) b.Ascii(" /RowSpan ").Int(e.RowSpan);
                if (e.ColSpan > 1) b.Ascii(" /ColSpan ").Int(e.ColSpan);
                b.Ascii(" >>");
            }
            if (e.ListNumbering != null)
            {
                // PDF 1.7 has no Ordered and Unordered numbering, so its nearest values stand in for them.
                string numbering = Pdf20 ? e.ListNumbering : e.ListNumbering switch { "Ordered" => "Decimal", "Unordered" => "Disc", var n => n };
                b.Ascii(" /A << /O /List /ListNumbering /").Ascii(numbering).Ascii(" >>");
            }
            b.Ascii(" /K [");
            foreach (var k in e.Kids)
            {
                switch (k)
                {
                    case StructElem c:
                        b.Int(c.Id).Ascii(" 0 R ");
                        break;
                    case ContentSlot slot:
                        foreach (var (stream, mcid) in slot.Refs)
                        {
                            b.Ascii("<< /Type /MCR /Pg ").Int(pageIds[stream.Page]).Ascii(" 0 R /MCID ").Int(mcid);
                            if (stream.FormId != 0) b.Ascii(" /Stm ").Int(stream.FormId).Ascii(" 0 R");
                            b.Ascii(" >> ");
                        }
                        break;
                    case AnnotRef annot:
                        b.Ascii("<< /Type /OBJR /Pg ").Int(pageIds[annot.Page]).Ascii(" 0 R /Obj ").Int(annot.Id).Ascii(" 0 R >> ");
                        break;
                }
            }
            b.Ascii("] >>");
            EndStructure();
        }
        return next;
    }

    /// <summary>
    /// Starts a structure object: in an object stream when the file packs its structure tree, which is most of a long
    /// tagged file and compresses to a fraction of its size, or directly in the file.
    /// </summary>
    private ByteBuffer BeginStructure(int id) => _packed != null ? _packed.Begin(id) : Begin(id);

    private void EndStructure()
    {
        if (_packed != null) _packed.End();
        else End();
    }

    /// <summary>Leaves out the structure elements without content and numbers the others, in tree order.</summary>
    private List<StructElem> NumberStructure(ref int next)
    {
        Structure!.Prune();
        var elements = new List<StructElem>();
        int id = next;
        void Number(StructElem e)
        {
            e.Id = id++;
            elements.Add(e);
            foreach (var k in e.Kids)
                if (k is StructElem c) Number(c);
        }
        Number(Structure.Root);
        next = id;
        return elements;
    }

    /// <summary>For a PDF 1.7 structure type that PDF 2.0 dropped, the PDF 2.0 type it is role mapped to; else null.</summary>
    private static string? Pdf17Only(string type) => type switch
    {
        "BlockQuote" => "Div",
        "Quote" or "Code" => "Span",
        "Art" => "Sect",
        "Note" => "FENote",
        "TOC" or "TOCI" => "Div",
        "Index" => "Sect",
        _ => null,
    };
}
