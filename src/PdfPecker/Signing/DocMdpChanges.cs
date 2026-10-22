using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Signing;

/// <summary>
/// Finds the changes made after a certification signature that its DocMDP level does not allow. The certified revision
/// is a prefix of the file, so it is opened on its own and every object a later update wrote or freed is compared with
/// its certified version, by what the object was in that revision: the catalog, the form, a field, a page, an
/// annotation, the validation data.
/// </summary>
/// <remarks>
/// Validation data (the DSS and its catalog extension), document timestamps, the document information dictionary and
/// the XMP metadata may change at every level, as PAdES and the common validators allow. Level 2 adds filling in fields
/// and adding or filling signature fields; level 3 adds annotations. Everything else (page content, resources, the page
/// tree, other fields) is a disallowed change. New objects are only checked where a changed object points at them, so
/// an unreferenced object an update adds is harmless.
/// </remarks>
internal sealed class DocMdpChanges
{
    private const int MaxReported = 20;
    private const int MaxChangedObjects = 200_000;
    private const int MaxWalked = 200_000;
    private const int MaxDepth = 32;

    private enum Role : byte
    {
        Other,
        Catalog,
        AcroForm,
        FieldList,
        Field,
        SignatureField,
        FieldAppearance,
        Page,
        PageAnnotations,
        Annotation,
        Free,
    }

    private readonly PdfFile _old;
    private readonly PdfFile _new;
    private readonly int _level;
    private readonly Dictionary<int, Role> _roles = new();
    private readonly Dictionary<int, int> _pageNumbers = new();
    private readonly Dictionary<int, string> _fieldNames = new();
    private readonly List<string> _found = new();

    private DocMdpChanges(PdfFile old, PdfFile current, int level)
    {
        _old = old;
        _new = current;
        _level = level;
    }

    /// <summary>
    /// The disallowed changes in <paramref name="file"/> after the certified revision that ends at
    /// <paramref name="revisionEnd"/>, in plain words; empty when every change is one <paramref name="level"/> allows.
    /// </summary>
    public static List<string> Find(PdfFile file, long revisionEnd, int level, string? password)
    {
        PdfFile old;
        try
        {
            old = PdfFile.Open(file.Data.AsSpan(0, (int)revisionEnd).ToArray(), password);
        }
        catch (Exception ex) when (ex is PdfFormatException or PdfPasswordException or InvalidDataException or ArgumentException
            or InvalidOperationException or OverflowException or NotSupportedException)
        {
            return new List<string> { "The certified revision of the document cannot be read, so the changes made after it cannot be checked." };
        }
        var changes = new DocMdpChanges(old, file, level);
        changes.Check();
        return changes._found;
    }

    private void Check()
    {
        if (_old.Trailer["Root"] is not PdfRef oldRoot || _new.Trailer["Root"] is not PdfRef newRoot || oldRoot.Num != newRoot.Num)
        {
            Report("The document catalog was replaced after the certification");
            return;
        }
        Classify(oldRoot);
        var changed = _new.ObjectsChangedAfter(_old.Data.Length);
        if (changed.Count > MaxChangedObjects)
        {
            Report("The updates after the certification change too many objects to check");
            return;
        }
        changed.Sort();
        foreach (int num in changed)
        {
            if (_found.Count >= MaxReported) break;
            var before = _old.GetObject(num);
            var after = _new.GetObject(num);
            if (before == null || before.IsNull || Same(before, after, 0)) continue; // new objects count where they are used
            CheckObject(num, before, after);
        }
    }

    // ------------------------------------------------------------------ what each object was in the certified revision

    private void Classify(PdfRef root)
    {
        _roles[root.Num] = Role.Catalog;
        var catalog = _old.Resolve(root) as PdfDict;
        if (catalog?["AcroForm"] is PdfRef acroRef) _roles[acroRef.Num] = Role.AcroForm;
        var acro = _old.GetDict(catalog, "AcroForm");
        if (acro?["Fields"] is PdfRef fieldsRef) _roles[fieldsRef.Num] = Role.FieldList;
        if (_old.GetArray(acro, "Fields") is { } fields) ClassifyFields(fields, "", null, 0);
        if (catalog?["Pages"] is PdfRef pagesRef) ClassifyPages(pagesRef);
        // The document information, the XMP metadata and the validation data may change at every level.
        if (_old.Trailer["Info"] is PdfRef info) _roles.TryAdd(info.Num, Role.Free);
        if (catalog?["Metadata"] is PdfRef metadata) _roles.TryAdd(metadata.Num, Role.Free);
        if (catalog?["DSS"] is { } dss) ClassifyFree(dss, 0);
    }

    private void ClassifyFields(PdfArray kids, string prefix, string? inheritedType, int depth)
    {
        if (depth > MaxDepth) return;
        foreach (var kid in kids.Items)
        {
            if (kid is not PdfRef r || _roles.Count >= MaxWalked || _roles.ContainsKey(r.Num) || _old.Resolve(r) is not PdfDict field) continue;
            string name = _old.Resolve(field["T"]) is PdfString t ? (prefix.Length == 0 ? t.Text : prefix + "." + t.Text) : prefix;
            string? type = _old.GetName(field, "FT") ?? inheritedType;
            _roles[r.Num] = type == "Sig" ? Role.SignatureField : Role.Field;
            _fieldNames[r.Num] = name;
            ClassifyAppearance(field);
            if (_old.GetArray(field, "Kids") is { } sub) ClassifyFields(sub, name, type, depth + 1);
        }
    }

    /// <summary>A field's appearance dictionary and streams, which filling it in may write again in place.</summary>
    private void ClassifyAppearance(PdfDict annotation)
    {
        var ap = annotation["AP"];
        if (ap is PdfRef apRef) _roles.TryAdd(apRef.Num, Role.FieldAppearance);
        if (_old.Resolve(ap) is not PdfDict apDict) return;
        foreach (var state in apDict.Entries.Values)
        {
            if (state is PdfRef s) _roles.TryAdd(s.Num, Role.FieldAppearance);
            if (_old.Resolve(state) is PdfDict states and not PdfStream)
                foreach (var v in states.Entries.Values)
                    if (v is PdfRef vr) _roles.TryAdd(vr.Num, Role.FieldAppearance);
        }
    }

    private void ClassifyPages(PdfRef root)
    {
        var stack = new Stack<(PdfRef Node, int Depth)>();
        stack.Push((root, 0));
        var seen = new HashSet<int>();
        while (stack.Count > 0 && seen.Count < MaxWalked)
        {
            var (node, depth) = stack.Pop();
            if (depth > 64 || !seen.Add(node.Num) || _old.Resolve(node) is not PdfDict d) continue;
            var kids = _old.GetArray(d, "Kids");
            if (kids != null && _old.GetName(d, "Type") != "Page")
            {
                for (int i = kids.Count - 1; i >= 0; i--)
                    if (kids[i] is PdfRef k) stack.Push((k, depth + 1));
                continue;
            }
            if (_roles.ContainsKey(node.Num)) continue;
            _roles[node.Num] = Role.Page;
            _pageNumbers[node.Num] = _pageNumbers.Count + 1;
            if (d["Annots"] is PdfRef annotsRef)
            {
                _roles.TryAdd(annotsRef.Num, Role.PageAnnotations);
                _pageNumbers.TryAdd(annotsRef.Num, _pageNumbers[node.Num]);
            }
            if (_old.GetArray(d, "Annots") is not { } annots) continue;
            foreach (var a in annots.Items)
            {
                if (a is not PdfRef ar || _roles.ContainsKey(ar.Num) || _old.Resolve(ar) is not PdfDict annot) continue;
                if (_old.GetName(annot, "Subtype") == "Widget") continue; // a field the form does not list: not fillable
                _roles[ar.Num] = Role.Annotation;
                if (annot["Popup"] is PdfRef popup) _roles.TryAdd(popup.Num, Role.Annotation);
                if (annot["AP"] is PdfRef apRef) _roles.TryAdd(apRef.Num, Role.Annotation);
                if (_old.Resolve(annot["AP"]) is PdfDict apDict)
                    foreach (var state in apDict.Entries.Values)
                        if (state is PdfRef s) _roles.TryAdd(s.Num, Role.Annotation);
            }
        }
    }

    /// <summary>Marks the validation data and everything it holds as free to change, unless the object is something else too.</summary>
    private void ClassifyFree(PdfObject o, int depth)
    {
        if (depth > 4 || _roles.Count >= MaxWalked) return;
        if (o is PdfRef r)
        {
            if (!_roles.TryAdd(r.Num, Role.Free)) return;
            o = _old.Resolve(r) ?? PdfNull.Instance;
        }
        if (o is PdfStream) return;
        if (o is PdfArray a) foreach (var item in a.Items) ClassifyFree(item, depth + 1);
        else if (o is PdfDict d) foreach (var v in d.Entries.Values) ClassifyFree(v, depth + 1);
    }

    // ------------------------------------------------------------------ checking a changed object

    private void CheckObject(int num, PdfObject before, PdfObject? after)
    {
        var role = _roles.TryGetValue(num, out var known) ? known : Role.Other;
        if (role == Role.Free || before is PdfStream { } s && _old.GetName(s, "Type") is "ObjStm" or "XRef") return;
        switch (role)
        {
            case Role.Catalog when before is PdfDict b && after is PdfDict a:
                CheckCatalog(b, a);
                return;
            case Role.AcroForm when before is PdfDict b && after is PdfDict a:
                CheckAcroForm(b, a);
                return;
            case Role.FieldList:
                CheckFieldList(before as PdfArray, _new.Resolve(after) as PdfArray);
                return;
            case Role.Field or Role.SignatureField when before is PdfDict b && after is PdfDict a:
                CheckField(num, b, a, role == Role.SignatureField);
                return;
            case Role.FieldAppearance:
                if (_level < 2) Report($"The appearance of a form field was changed after the certification, {DocMdp.Allows(_level)}");
                return;
            case Role.Page when before is PdfDict b && after is PdfDict a:
                CheckPage(num, b, a);
                return;
            case Role.PageAnnotations:
                CheckAnnotations(_pageNumbers.GetValueOrDefault(num), before as PdfArray, _new.Resolve(after) as PdfArray);
                return;
            case Role.Annotation:
                if (_level < 3) Report($"An annotation was changed or deleted after the certification, {DocMdp.Allows(_level)}");
                return;
            default:
                Report(after == null || after.IsNull
                    ? $"Object {num} was deleted after the certification, {DocMdp.Allows(_level)}"
                    : $"{Describe(num, role)} was changed after the certification, {DocMdp.Allows(_level)}");
                return;
        }
    }

    private string Describe(int num, Role role) => role switch
    {
        Role.Catalog => "The document catalog",
        Role.AcroForm => "The form",
        Role.Page => $"Page {_pageNumbers.GetValueOrDefault(num)}",
        Role.Field or Role.SignatureField => $"The field \"{_fieldNames.GetValueOrDefault(num)}\"",
        _ => $"Object {num}",
    };

    private void CheckCatalog(PdfDict before, PdfDict after)
    {
        foreach (var key in ChangedKeys(before, after))
        {
            switch (key)
            {
                case "DSS" or "Extensions" or "Metadata":
                    break;
                case "AcroForm" when _old.Resolve(before["AcroForm"]) is PdfDict b && _new.Resolve(after["AcroForm"]) is PdfDict a:
                    CheckAcroForm(b, a);
                    break;
                default:
                    Report($"The document catalog's /{key} was changed after the certification, {DocMdp.Allows(_level)}");
                    break;
            }
        }
    }

    private void CheckAcroForm(PdfDict before, PdfDict after)
    {
        foreach (var key in ChangedKeys(before, after))
        {
            switch (key)
            {
                case "Fields":
                    CheckFieldList(_old.Resolve(before["Fields"]) as PdfArray, _new.Resolve(after["Fields"]) as PdfArray);
                    break;
                case "SigFlags":
                    break;
                case "DR" or "DA" or "NeedAppearances" when _level >= 2:
                    break;
                default:
                    Report($"The form's /{key} was changed after the certification, {DocMdp.Allows(_level)}");
                    break;
            }
        }
    }

    private void CheckFieldList(PdfArray? before, PdfArray? after)
    {
        var (removed, added) = Difference(before, after);
        if (removed.Count > 0) Report($"A form field was removed after the certification, {DocMdp.Allows(_level)}");
        foreach (var item in added)
            if (NewFieldProblem(_new.Resolve(item) as PdfDict) is { } problem) Report(problem);
    }

    private void CheckField(int num, PdfDict before, PdfDict after, bool signature)
    {
        // Signing an empty signature field fills /V, its appearance and the locked flag; at level 1 only a document
        // timestamp may do so. A widget kid of the field has no /V of its own; its field's counts.
        bool signing = signature && Value(_old, before) is null && Value(_new, after) is PdfDict sig
            && (_level >= 2 || IsDocumentTimestamp(sig));
        foreach (var key in ChangedKeys(before, after))
        {
            bool allowed = key switch
            {
                "V" or "AP" or "F" when signature => signing,
                "V" or "AP" or "AS" or "RV" => _level >= 2 && !signature,
                _ => false,
            };
            if (!allowed)
                Report(signature && key == "V"
                    ? $"The signature in field \"{_fieldNames.GetValueOrDefault(num)}\" was changed after the certification, {DocMdp.Allows(_level)}"
                    : $"The field \"{_fieldNames.GetValueOrDefault(num)}\" (/{key}) was changed after the certification, {DocMdp.Allows(_level)}");
        }
    }

    private void CheckPage(int num, PdfDict before, PdfDict after)
    {
        foreach (var key in ChangedKeys(before, after))
        {
            if (key == "Annots") CheckAnnotations(_pageNumbers.GetValueOrDefault(num), _old.Resolve(before["Annots"]) as PdfArray, _new.Resolve(after["Annots"]) as PdfArray);
            else Report($"Page {_pageNumbers.GetValueOrDefault(num)} (/{key}) was changed after the certification, {DocMdp.Allows(_level)}");
        }
    }

    private void CheckAnnotations(int page, PdfArray? before, PdfArray? after)
    {
        var (removed, added) = Difference(before, after);
        foreach (var item in removed)
            if (_level < 3 || _old.Resolve(item) is not PdfDict a || _old.GetName(a, "Subtype") == "Widget")
                Report($"An annotation or form field was removed from page {page} after the certification, {DocMdp.Allows(_level)}");
        foreach (var item in added)
        {
            if (_new.Resolve(item) is not PdfDict annot) continue;
            if (_new.GetName(annot, "Subtype") == "Widget")
            {
                if (NewFieldProblem(annot) is { } problem) Report(problem);
            }
            else if (_level < 3) Report($"An annotation was added to page {page} after the certification, {DocMdp.Allows(_level)}");
        }
    }

    /// <summary>
    /// Why a field or widget added after the certification is not allowed, or null when it is a signature field the level
    /// allows: at level 1 only an invisible one that holds a document timestamp, at level 2 and up any invisible one. A
    /// new visible field changes a page, so pyHanko and Acrobat accept only the fields the document had when certified.
    /// </summary>
    private string? NewFieldProblem(PdfDict? field)
    {
        if (field == null) return null;
        if (FieldType(field) != "Sig") return $"A form field was added after the certification, {DocMdp.Allows(_level)}";
        if (IsVisible(field)) return "A visible signature field was added after the certification, which allows only invisible new signatures and the signature fields it was certified with";
        if (_level >= 2) return null;
        PdfDict? f = field;
        for (int i = 0; f != null && i < MaxDepth; i++, f = _new.GetDict(f, "Parent"))
            if (_new.Resolve(f["V"]) is PdfDict sig)
                return IsDocumentTimestamp(sig) ? null : $"A signature was added after the certification, {DocMdp.Allows(_level)}";
        return $"A signature field was added after the certification, {DocMdp.Allows(_level)}";
    }

    /// <summary>Whether a widget, or any widget of a field, has an area on the page.</summary>
    private bool IsVisible(PdfDict field)
    {
        if (HasArea(field)) return true;
        if (_new.GetArray(field, "Kids") is { } kids)
            for (int i = 0; i < kids.Count && i < 1000; i++)
                if (_new.Resolve(kids[i]) is PdfDict kid && HasArea(kid)) return true;
        return false;
    }

    private bool HasArea(PdfDict annotation)
        => _new.GetNumbers(annotation, "Rect") is { Length: 4 } r && Math.Abs(r[2] - r[0]) * Math.Abs(r[3] - r[1]) > 0;

    /// <summary>The signature dictionary in a signature field's /V, or in its parent's for a widget kid; null when unsigned.</summary>
    private static PdfDict? Value(PdfFile file, PdfDict field)
    {
        PdfDict? f = field;
        for (int i = 0; f != null && i < MaxDepth; i++, f = file.GetDict(f, "Parent"))
            if (file.Resolve(f["V"]) is { IsNull: false } v) return v as PdfDict;
        return null;
    }

    private bool IsDocumentTimestamp(PdfDict sig) => _new.GetName(sig, "SubFilter") == "ETSI.RFC3161";

    private string? FieldType(PdfDict field)
    {
        PdfDict? f = field;
        for (int i = 0; f != null && i < MaxDepth; i++, f = _new.GetDict(f, "Parent"))
            if (_new.GetName(f, "FT") is { } ft) return ft;
        return null;
    }

    private void Report(string message)
    {
        message += ".";
        if (_found.Count < MaxReported && !_found.Contains(message)) _found.Add(message);
    }

    // ------------------------------------------------------------------ comparing objects

    private static IEnumerable<string> ChangedKeys(PdfDict before, PdfDict after)
    {
        foreach (var (key, value) in before.Entries)
            if (!Same(value, after[key], 0)) yield return key;
        foreach (var (key, value) in after.Entries)
            if (!before.ContainsKey(key) && !value.IsNull) yield return key;
    }

    /// <summary>
    /// The items of <paramref name="before"/> missing from <paramref name="after"/>, and the ones added. Annotations and
    /// fields are references, which a set compares in linear time however long the arrays are; a direct object, which
    /// does not belong there, counts as removed and added.
    /// </summary>
    private static (List<PdfObject> Removed, List<PdfObject> Added) Difference(PdfArray? before, PdfArray? after)
    {
        var removed = new List<PdfObject>();
        var added = new List<PdfObject>();
        var old = new HashSet<PdfRef>();
        var current = new HashSet<PdfRef>();
        foreach (var o in before?.Items ?? new List<PdfObject>())
            if (o is PdfRef r) old.Add(r);
            else removed.Add(o);
        foreach (var c in after?.Items ?? new List<PdfObject>())
            if (c is PdfRef r) current.Add(r);
            else added.Add(c);
        foreach (var r in old)
            if (!current.Contains(r)) removed.Add(r);
        foreach (var r in current)
            if (!old.Contains(r)) added.Add(r);
        return (removed, added);
    }

    /// <summary>Whether two direct objects from the two revisions are equal; references are equal when they name the same object.</summary>
    private static bool Same(PdfObject? a, PdfObject? b, int depth)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || a.IsNull) return b == null || b.IsNull;
        if (b == null || depth > 64) return false;
        switch (a)
        {
            case PdfRef ra:
                return b is PdfRef rb && ra.Equals(rb);
            case PdfNumber na:
                return b is PdfNumber nb && na.Value.Equals(nb.Value);
            case PdfString sa:
                return b is PdfString sb && sa.Bytes.AsSpan().SequenceEqual(sb.Bytes);
            case PdfName ma:
                return b is PdfName mb && string.Equals(ma.Value, mb.Value, StringComparison.Ordinal);
            case PdfBool ba:
                return b is PdfBool bb && ba.Value == bb.Value;
            case PdfArray aa:
                if (b is not PdfArray ab || aa.Count != ab.Count) return false;
                for (int i = 0; i < aa.Count; i++)
                    if (!Same(aa[i], ab[i], depth + 1)) return false;
                return true;
            case PdfStream ta:
                return b is PdfStream tb && ta.RawData.AsSpan().SequenceEqual(tb.RawData) && SameEntries(ta, tb, depth);
            case PdfDict da:
                return b is PdfDict db and not PdfStream && SameEntries(da, db, depth);
            default:
                return false;
        }
    }

    private static bool SameEntries(PdfDict a, PdfDict b, int depth)
    {
        foreach (var (key, value) in a.Entries)
            if (!Same(value, b[key], depth + 1)) return false;
        foreach (var (key, value) in b.Entries)
            if (!a.ContainsKey(key) && !value.IsNull) return false;
        return true;
    }
}
