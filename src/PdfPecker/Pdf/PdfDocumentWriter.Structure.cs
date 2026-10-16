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

        Begin(rootId).Ascii("<< /Type /StructTreeRoot /K ").Int(tree.Root.Id).Ascii(" 0 R /ParentTree ").Int(parentTreeId)
            .Ascii(" 0 R /ParentTreeNextKey ").Int(nextKey);
        // Elements with identifiers (footnote Notes) are listed in the ID tree, in the byte order of their identifiers.
        var identified = elements.Where(e => e.ElementId != null).OrderBy(e => e.ElementId, StringComparer.Ordinal).ToList();
        if (identified.Count > 0)
        {
            _out.Ascii(" /IDTree << /Names [");
            foreach (var e in identified)
            {
                WriteAsciiString(_out, e.ElementId!);
                _out.Sp().Int(e.Id).Ascii(" 0 R ");
            }
            _out.Ascii("] >>");
        }
        if (ns2Id != 0)
        {
            _out.Ascii(" /Namespaces [").Int(ns2Id).Ascii(" 0 R");
            if (ns17Id != 0) _out.Ascii(" ").Int(ns17Id).Ascii(" 0 R");
            _out.Ascii("]");
        }
        _out.Ascii(" >>");
        End();
        if (ns2Id != 0)
        {
            // Through WriteTextString, so an encrypted file encrypts the namespace names like every other string.
            Begin(ns2Id).Ascii("<< /Type /Namespace /NS ");
            WriteTextString(_out, "http://iso.org/pdf2/ssn");
            _out.Ascii(" >>");
            End();
        }
        if (ns17Id != 0)
        {
            Begin(ns17Id).Ascii("<< /Type /Namespace /NS ");
            WriteTextString(_out, "http://iso.org/pdf/ssn");
            _out.Ascii(" /RoleMapNS <<");
            foreach (var type in elements.Select(e => e.Type).Where(t => Pdf17Only(t) != null).Distinct())
                _out.Ascii(" /").Ascii(type).Ascii(" [/").Ascii(Pdf17Only(type)!).Sp().Int(ns2Id).Ascii(" 0 R]");
            _out.Ascii(" >> >>");
            End();
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
        Begin(parentTreeId).Ascii("<< /Nums [");
        foreach (var (key, stream, link) in entries)
        {
            _out.Ascii("\n").Int(key).Sp();
            if (link != null) { _out.Int(link.Id).Ascii(" 0 R"); continue; }
            _out.Ascii("[");
            foreach (var e in stream!.Elements) _out.Int(e.Id).Ascii(" 0 R ");
            _out.Ascii("]");
        }
        _out.Ascii("] >>");
        End();

        foreach (var e in elements)
        {
            Begin(e.Id).Ascii("<< /Type /StructElem /S /").Ascii(e.Type).Ascii(" /P ").Int(e.Parent?.Id ?? rootId).Ascii(" 0 R");
            if (ns2Id != 0) _out.Ascii(" /NS ").Int(Pdf17Only(e.Type) != null ? ns17Id : ns2Id).Ascii(" 0 R");
            if (e.Lang != null) { _out.Ascii(" /Lang "); WriteTextString(_out, e.Lang); }
            if (e.Alt != null) { _out.Ascii(" /Alt "); WriteTextString(_out, e.Alt); }
            if (e.ElementId != null) { _out.Ascii(" /ID "); WriteAsciiString(_out, e.ElementId); }
            if (e.Scope != null || e.RowSpan > 1 || e.ColSpan > 1)
            {
                _out.Ascii(" /A << /O /Table");
                if (e.Scope != null) _out.Ascii(" /Scope /").Ascii(e.Scope);
                if (e.RowSpan > 1) _out.Ascii(" /RowSpan ").Int(e.RowSpan);
                if (e.ColSpan > 1) _out.Ascii(" /ColSpan ").Int(e.ColSpan);
                _out.Ascii(" >>");
            }
            if (e.ListNumbering != null) _out.Ascii(" /A << /O /List /ListNumbering /").Ascii(e.ListNumbering).Ascii(" >>");
            _out.Ascii(" /K [");
            foreach (var k in e.Kids)
            {
                switch (k)
                {
                    case StructElem c:
                        _out.Int(c.Id).Ascii(" 0 R ");
                        break;
                    case ContentSlot slot:
                        foreach (var (stream, mcid) in slot.Refs)
                        {
                            _out.Ascii("<< /Type /MCR /Pg ").Int(pageIds[stream.Page]).Ascii(" 0 R /MCID ").Int(mcid);
                            if (stream.FormId != 0) _out.Ascii(" /Stm ").Int(stream.FormId).Ascii(" 0 R");
                            _out.Ascii(" >> ");
                        }
                        break;
                    case AnnotRef annot:
                        _out.Ascii("<< /Type /OBJR /Pg ").Int(pageIds[annot.Page]).Ascii(" 0 R /Obj ").Int(annot.Id).Ascii(" 0 R >> ");
                        break;
                }
            }
            _out.Ascii("] >>");
            End();
        }
        return next;
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
        _ => null,
    };
}
