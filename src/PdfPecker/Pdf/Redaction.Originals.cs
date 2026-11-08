using PdfPecker.Rendering;
using PdfPecker.Rendering.Content;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Pdf;

/// <summary>
/// The originals of what redaction replaced or dropped: images, forms and marked content properties. Pages and forms
/// draw the copies, but the originals are still named in resource dictionaries, which other pages may share or inherit
/// from the page tree, and still referenced from places such as the structure tree, so the rewriter would write them out
/// with what was redacted. Here they lose every name that nothing draws, and those that nothing can draw are emptied.
/// </summary>
internal sealed partial class Redaction
{
    /// <summary>How many content streams the search for what still draws an original may read before it gives up.</summary>
    private const int MaxScans = 4 * MaxFormWalks;

    private int _scans;

    /// <summary>The resource names a content stream uses: XObjects it draws and marked content properties it names.</summary>
    private sealed class Used
    {
        public readonly HashSet<string> XObjects = new(StringComparer.Ordinal);
        public readonly HashSet<string> Properties = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Removes the originals from the file once every page is redacted. A resource dictionary loses the names of
    /// originals that the content using it does not draw; an original that is still drawn somewhere stays, as it shows
    /// outside the areas there. Content that cannot be read whole keeps its names.
    /// </summary>
    public void RemoveOriginals(RenderDocument doc)
    {
        if (_replaced.Count == 0) return;
        _scans = 0;
        var forms = new HashSet<PdfDict>(ReferenceEqualityComparer.Instance);
        foreach (var info in doc.Pages)
        {
            PdfFile.CheckCancel();
            var page = info.Dict;
            var own = _file.GetDict(page, "Resources");
            var resources = own ?? info.Resources;
            if (resources != null)
            {
                var used = new Used();
                bool whole = Scan(PageContent(page), resources, used, forms, 0);
                // Pages that inherit a dictionary naming an original get it, or its pruned copy, as their own, so that
                // the page tree's copy can lose the name below without taking it from a page that draws it.
                if (!Prune(page, resources, used, whole) && own == null && NamesReplaced(resources))
                    page.Entries["Resources"] = resources;
            }
            if (_file.GetArray(page, "Annots") is { } annots)
                foreach (var item in annots.Items)
                    if (_file.GetDict(_file.Resolve(item) as PdfDict, "AP") is { } ap)
                        foreach (var appearance in ap.Entries.Values)
                            switch (_file.Resolve(appearance))
                            {
                                case PdfStream s: VisitForm(s, forms, 0); break;
                                case PdfDict states:
                                    foreach (var state in states.Entries.Values)
                                        if (_file.Resolve(state) is PdfStream ss) VisitForm(ss, forms, 0);
                                    break;
                            }
        }
        PageTree.Walk(_file, _file.Catalog?["Pages"], static (_, _, _) => true, node: (_, node) =>
        {
            if (_file.GetDict(node, "Resources") is { } inherited) Prune(node, inherited, new Used(), whole: true);
        });
        EmptyUndrawn();
    }

    /// <summary>
    /// Collects the names a content stream drawn with <paramref name="resources"/> uses, with those of the forms it draws
    /// that have no resources of their own; forms with their own are pruned in turn. Returns false when the stream or such
    /// a form could not be read whole.
    /// </summary>
    private bool Scan(byte[]? data, PdfDict resources, Used used, HashSet<PdfDict> forms, int depth)
    {
        if (data == null) return true;
        if (++_scans > MaxScans || depth > MaxDepth) return false;
        bool whole = true;
        var xObjects = _file.GetDict(resources, "XObject");
        var p = new ContentParser(data);
        while (p.Next())
        {
            PdfFile.CheckCancel();
            var o = p.Operands;
            switch (p.Op)
            {
                case "Do" when o.Count > 0 && o[0].Name is { } name:
                    used.XObjects.Add(name);
                    if (_file.GetDict(xObjects, name) is PdfStream form && _file.GetName(form, "Subtype") == "Form")
                    {
                        if (_file.GetDict(form, "Resources") != null) VisitForm(form, forms, depth + 1);
                        else if (Decode(form) is { } inner) whole &= Scan(inner, resources, used, forms, depth + 1);
                    }
                    break;
                case "BDC" or "DP" when o.Count > 1 && o[1].Name is { } properties:
                    used.Properties.Add(properties);
                    break;
            }
        }
        // A Type 3 font without resources of its own draws its glyphs with these, from content not read here.
        if (_file.GetDict(resources, "Font") is { } fonts)
            foreach (var font in fonts.Entries.Values)
                if (_file.Resolve(font) is PdfDict f && _file.GetName(f, "Subtype") == "Type3" && _file.GetDict(f, "Resources") == null)
                    whole = false;
        return whole;
    }

    /// <summary>Prunes the resources of a form that has its own, once.</summary>
    private void VisitForm(PdfStream form, HashSet<PdfDict> forms, int depth)
    {
        if (!forms.Add(form) || _file.GetDict(form, "Resources") is not { } resources) return;
        var used = new Used();
        bool whole = Scan(Decode(form), resources, used, forms, depth);
        Prune(form, resources, used, whole);
    }

    /// <summary>
    /// Gives <paramref name="owner"/> a copy of its resources without the originals that <paramref name="used"/> does not
    /// name, when any are named and the content was read whole; returns whether it did. The copy leaves other users of
    /// a shared dictionary their own.
    /// </summary>
    private bool Prune(PdfDict owner, PdfDict resources, Used used, bool whole)
    {
        if (!whole) return false;
        PdfDict? copy = null;
        foreach (var (category, names) in new[] { ("XObject", used.XObjects), ("Properties", used.Properties) })
        {
            if (_file.GetDict(resources, category) is not { } dict) continue;
            PdfDict? pruned = null;
            foreach (var (name, value) in dict.Entries)
            {
                if (names.Contains(name) || _file.Resolve(value) is not PdfDict target || !_replaced.Contains(target)) continue;
                pruned ??= PdfDict.CopyOf(dict);
                pruned.Entries.Remove(name);
            }
            if (pruned == null) continue;
            copy ??= PdfDict.CopyOf(resources);
            copy.Entries[category] = pruned;
        }
        if (copy == null) return false;
        owner.Entries["Resources"] = copy;
        return true;
    }

    private bool NamesReplaced(PdfDict resources)
    {
        foreach (var category in new[] { "XObject", "Properties" })
            if (_file.GetDict(resources, category) is { } dict)
                foreach (var value in dict.Entries.Values)
                    if (_file.Resolve(value) is PdfDict target && _replaced.Contains(target)) return true;
        return false;
    }

    /// <summary>
    /// Empties the originals the file still reaches only from places that do not draw them, such as a marked content
    /// reference in the structure tree. Those that a resource dictionary still names, or that an appearance, a soft mask
    /// or an image uses, stay as they are.
    /// </summary>
    private void EmptyUndrawn()
    {
        var reached = new HashSet<PdfDict>(ReferenceEqualityComparer.Instance);
        var drawn = new HashSet<PdfDict>(ReferenceEqualityComparer.Instance);
        var seen = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<PdfObject>();
        stack.Push(_file.Trailer);

        void Draws(PdfObject? value)
        {
            if (_file.Resolve(value) is PdfDict target && _replaced.Contains(target)) drawn.Add(target);
        }

        while (stack.Count > 0)
        {
            PdfFile.CheckCancel();
            var o = stack.Pop();
            if (o is PdfRef) o = _file.Resolve(o)!;
            if (o == null || !seen.Add(o)) continue;
            if (o is PdfArray array)
            {
                foreach (var item in array.Items) stack.Push(item);
                continue;
            }
            if (o is not PdfDict dict) continue;
            if (_replaced.Contains(dict)) reached.Add(dict);
            foreach (var (key, value) in dict.Entries)
            {
                switch (key)
                {
                    case "XObject" or "Properties" or "AP" when _file.Resolve(value) is PdfDict names:
                        foreach (var entry in names.Entries.Values)
                        {
                            Draws(entry);
                            if (key == "AP" && _file.Resolve(entry) is PdfDict states and not PdfStream)
                                foreach (var state in states.Entries.Values) Draws(state);
                        }
                        break;
                    case "G" or "SMask" or "Mask" or "Image":
                        Draws(value);
                        break;
                }
                stack.Push(value);
            }
        }

        foreach (var original in reached)
        {
            if (drawn.Contains(original)) continue;
            if (original is PdfStream stream) Empty(stream);
            else
            {
                original.Entries.Remove("ActualText");
                original.Entries.Remove("Alt");
                original.Entries.Remove("E");
            }
        }
    }

    /// <summary>Makes an image a single white pixel and a form an empty one, keeping only what says what they are.</summary>
    private void Empty(PdfStream stream)
    {
        bool image = _file.GetName(stream, "Subtype") == "Image";
        var keep = image ? new[] { "Type", "Subtype" } : new[] { "Type", "Subtype", "BBox", "Matrix" };
        foreach (var key in stream.Entries.Keys.Where(k => Array.IndexOf(keep, k) < 0).ToList()) stream.Entries.Remove(key);
        if (image)
        {
            stream.Entries["Width"] = PdfNumber.Int(1);
            stream.Entries["Height"] = PdfNumber.Int(1);
            stream.Entries["ColorSpace"] = PdfName.Get("DeviceGray");
            stream.Entries["BitsPerComponent"] = PdfNumber.Int(8);
            stream.RawData = new byte[] { 255 };
        }
        else stream.RawData = Array.Empty<byte>();
    }
}
