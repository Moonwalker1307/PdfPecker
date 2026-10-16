using System.Globalization;
using System.IO.Compression;
using System.Text;
using PdfPecker.Fonts;
using PdfPecker.Rendering.Content;
using PdfPecker.Rendering.Parsing;
using PdfPecker.Signing;
using Encodings = PdfPecker.Rendering.Fonts.Encodings;
using RenderFont = PdfPecker.Rendering.Fonts.PdfFont;

namespace PdfPecker.Pdf;

/// <summary>
/// Reads, fills and flattens the AcroForm fields of an existing PDF. Changed and new objects are kept in an overlay over
/// the parsed file and written as an incremental update, which a full rewrite then compacts when the file is neither
/// signed nor encrypted.
/// </summary>
internal sealed partial class PdfFormEditor
{
    // Field flags (ISO 32000-1, tables 221, 226, 228 and 230).
    private const int FfReadOnly = 1, FfRequired = 1 << 1, FfMultiline = 1 << 12, FfPassword = 1 << 13, FfRadio = 1 << 15,
        FfPushButton = 1 << 16, FfCombo = 1 << 17, FfEdit = 1 << 18, FfMultiSelect = 1 << 21, FfComb = 1 << 24;

    /// <summary>The most fields and widgets read: a hostile file could otherwise make the walk take very long.</summary>
    private const int MaxFields = 100_000;
    private const int MaxDepth = 32;

    private sealed class Field
    {
        public string Name = "";
        public PdfRef Ref = null!;
        public readonly List<PdfRef> Widgets = new();
    }

    private readonly PdfFile _file;
    private readonly Dictionary<int, (int Gen, PdfObject Obj)> _changed = new();
    private int _next;
    private readonly PdfRef _rootRef;
    private readonly List<Field> _fields = new();
    private readonly List<PdfRef> _pages = new();
    private readonly Dictionary<int, int> _widgetPage = new(); // widget object number -> page index
    private readonly CancellationToken _cancel;

    private PdfFormEditor(PdfFile file, CancellationToken cancel)
    {
        _file = file;
        _cancel = cancel;
        _rootRef = file.Trailer["Root"] as PdfRef ?? throw new InvalidDataException("The PDF has no document catalog reference.");
        if (file.Resolve(_rootRef) is not PdfDict) throw new InvalidDataException("The PDF has no document catalog.");
        _next = Math.Max(file.GetInt(file.Trailer, "Size", 0), file.MaxObjectNumber + 1);
        CollectPages();
        CollectFields();
    }

    private static PdfFile Open(byte[] pdf, string? password)
    {
        try
        {
            return PdfFile.Open(pdf, password);
        }
        catch (PdfFormatException ex)
        {
            throw new InvalidDataException("The PDF is not readable: " + ex.Message, ex);
        }
    }

    // ------------------------------------------------------------------ public entry points

    public static IReadOnlyList<PdfFormField> GetFields(byte[] pdf, string? password)
    {
        using (PdfFile.BeginRender(CancellationToken.None))
        {
            var editor = new PdfFormEditor(Open(pdf, password), CancellationToken.None);
            return editor._fields.Select(editor.Describe).ToList();
        }
    }

    public static byte[] Fill(byte[] pdf, IDictionary<string, string> values, PdfFormFillOptions options, CancellationToken cancel)
    {
        using (PdfFile.BeginRender(cancel))
        {
            var file = Open(pdf, options.Password);
            var editor = new PdfFormEditor(file, cancel);
            bool signed = editor.IsSigned();
            if (options.Flatten && signed)
                throw new InvalidOperationException("The PDF is digitally signed, and flattening its form would invalidate the signatures.");
            editor.FillValues(values, options);
            if (options.Flatten) editor.FlattenFields();
            if (editor._changed.Count == 0) return (byte[])pdf.Clone();
            var updated = editor.WriteIncremental(pdf);
            if (signed || file.Decryptor != null) return updated;
            // An unsigned, unencrypted file is written out again so that earlier values do not stay in its old revision.
            var rewritten = PdfRewriter.Write(Open(updated, null), null);
            return rewritten.Span.ToArray();
        }
    }

    // ------------------------------------------------------------------ the overlay

    private PdfObject? R(PdfObject? o)
    {
        for (int i = 0; i < 32 && o is PdfRef r; i++)
        {
            if (_changed.TryGetValue(r.Num, out var c)) o = c.Obj;
            else return _file.Resolve(r);
        }
        return o is PdfRef ? null : o;
    }

    private PdfDict? D(PdfDict? d, string key) => d == null ? null : R(d[key]) as PdfDict;
    private PdfArray? A(PdfDict? d, string key) => d == null ? null : R(d[key]) as PdfArray;
    private string? Nm(PdfDict? d, string key) => d == null ? null : (R(d[key]) as PdfName)?.Value;
    private int I(PdfDict? d, string key, int fallback = 0) => d != null && R(d[key]) is PdfNumber n ? n.IntValue : fallback;
    private double[]? Nums(PdfDict? d, string key)
    {
        if (A(d, key) is not PdfArray a) return null;
        var r = new double[a.Count];
        for (int i = 0; i < a.Count; i++) r[i] = R(a[i]) is PdfNumber n ? n.Value : 0;
        return r;
    }

    private string? Str(PdfObject? o) => R(o) is PdfString s ? s.Text : null;

    private PdfDict Catalog => (PdfDict)R(_rootRef)!;

    /// <summary>The object behind <paramref name="r"/>, copied into the overlay the first time, to be changed in place.</summary>
    private PdfDict Edit(PdfRef r)
    {
        if (_changed.TryGetValue(r.Num, out var c) && c.Obj is PdfDict changed) return changed;
        var source = _file.Resolve(r) as PdfDict ?? new PdfDict();
        var copy = source is PdfStream s
            ? new PdfStream(new Dictionary<string, PdfObject>(s.Entries, StringComparer.Ordinal), s.RawData)
            : new PdfDict(new Dictionary<string, PdfObject>(source.Entries, StringComparer.Ordinal));
        _changed[r.Num] = (r.Gen, copy);
        return copy;
    }

    private PdfRef Add(PdfObject o)
    {
        var r = new PdfRef(_next++, 0);
        _changed[r.Num] = (0, o);
        return r;
    }

    private PdfRef Reserve() => new(_next++, 0);

    private void Put(PdfRef r, PdfObject o) => _changed[r.Num] = (r.Gen, o);

    private static PdfStream FlateStream(Dictionary<string, PdfObject> entries, ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        entries["Filter"] = PdfName.Get("FlateDecode");
        return new PdfStream(entries, ms.ToArray());
    }

    private static Dictionary<string, PdfObject> Entries() => new(StringComparer.Ordinal);

    // ------------------------------------------------------------------ reading the form

    private void CollectPages()
    {
        var seen = new HashSet<int>();
        void Walk(PdfObject? node, int depth)
        {
            if (depth > 64 || node is not PdfRef r || !seen.Add(r.Num) || R(r) is not PdfDict d || _pages.Count >= 1_000_000) return;
            var kids = A(d, "Kids");
            if (kids != null && Nm(d, "Type") != "Page")
            {
                foreach (var k in kids.Items) Walk(k, depth + 1);
                return;
            }
            int index = _pages.Count;
            _pages.Add(r);
            if (A(d, "Annots") is PdfArray annots)
                foreach (var a in annots.Items)
                    if (a is PdfRef ar) _widgetPage.TryAdd(ar.Num, index);
        }
        Walk(Catalog["Pages"], 0);
    }

    private PdfDict? AcroForm => D(Catalog, "AcroForm");

    private void CollectFields()
    {
        if (A(AcroForm, "Fields") is not PdfArray top) return;
        var seen = new HashSet<int>();
        int count = 0;
        void Walk(PdfObject item, string prefix, int depth)
        {
            if (depth > MaxDepth || ++count > MaxFields || item is not PdfRef r || !seen.Add(r.Num) || R(r) is not PdfDict d) return;
            string? t = Str(d["T"]);
            string name = t == null ? prefix : prefix.Length == 0 ? t : prefix + "." + t;
            var field = new Field { Name = name, Ref = r };
            bool hasFieldKids = false;
            if (A(d, "Kids") is PdfArray kids)
            {
                foreach (var k in kids.Items)
                {
                    if (R(k) is not PdfDict kd) continue;
                    // A kid with a name is a field of its own; one without is a widget of this field.
                    if (kd["T"] != null) { hasFieldKids = true; Walk(k, name, depth + 1); }
                    else if (k is PdfRef kr && seen.Add(kr.Num) && ++count <= MaxFields) field.Widgets.Add(kr);
                }
            }
            if (Nm(d, "Subtype") == "Widget") field.Widgets.Insert(0, r);
            if (field.Widgets.Count > 0 || !hasFieldKids) _fields.Add(field);
        }
        foreach (var f in top.Items) Walk(f, "", 0);
    }

    /// <summary>An inheritable field attribute: from the field or the nearest ancestor that has it.</summary>
    private PdfObject? Inherited(PdfRef field, string key)
    {
        var d = R(field) as PdfDict;
        for (int i = 0; d != null && i < MaxDepth; i++)
        {
            if (d[key] is { } v && R(v) is { IsNull: false } value) return value;
            d = D(d, "Parent");
        }
        return null;
    }

    private string? FieldType(Field f) => (Inherited(f.Ref, "FT") as PdfName)?.Value;
    private int Flags(Field f) => Inherited(f.Ref, "Ff") is PdfNumber n ? n.IntValue : 0;

    private PdfFormFieldType Kind(Field f)
    {
        int ff = Flags(f);
        return FieldType(f) switch
        {
            "Btn" => (ff & FfPushButton) != 0 ? PdfFormFieldType.PushButton : (ff & FfRadio) != 0 ? PdfFormFieldType.RadioButton : PdfFormFieldType.CheckBox,
            "Ch" => (ff & FfCombo) != 0 ? PdfFormFieldType.ComboBox : PdfFormFieldType.ListBox,
            "Sig" => PdfFormFieldType.Signature,
            _ => PdfFormFieldType.Text,
        };
    }

    /// <summary>The choices of a choice field: (export value, shown text).</summary>
    private List<(string Value, string Text)> ChoiceOptions(Field f)
    {
        var list = new List<(string, string)>();
        if (Inherited(f.Ref, "Opt") is not PdfArray opt) return list;
        foreach (var item in opt.Items.Take(MaxFields))
        {
            var o = R(item);
            if (o is PdfString s) list.Add((s.Text, s.Text));
            else if (o is PdfArray pair && pair.Count >= 2 && Str(pair[0]) is string e) list.Add((e, Str(pair[1]) ?? e));
            else if (o is PdfArray single && single.Count == 1 && Str(single[0]) is string e1) list.Add((e1, e1));
        }
        return list;
    }

    /// <summary>The on states of a button field's widgets, in widget order, with their export text from /Opt when it has one.</summary>
    private List<(string Value, string Text)> ButtonOptions(Field f)
    {
        var opt = Inherited(f.Ref, "Opt") as PdfArray;
        var list = new List<(string, string)>();
        for (int i = 0; i < f.Widgets.Count; i++)
        {
            var state = OnState(f.Widgets[i]);
            if (state == null) continue;
            string text = opt != null && i < opt.Count && Str(opt[i]) is string t ? t : state;
            if (!list.Any(x => x.Item1 == state)) list.Add((state, text));
        }
        return list;
    }

    /// <summary>A button widget's on state: the name of its normal appearance that is not Off.</summary>
    private string? OnState(PdfRef widget)
    {
        if (D(D(R(widget) as PdfDict, "AP"), "N") is PdfDict states)
            foreach (var key in states.Entries.Keys)
                if (key != "Off") return key;
        return null;
    }

    private PdfFormField Describe(Field f)
    {
        var kind = Kind(f);
        int ff = Flags(f);
        var values = new List<string>();
        var v = Inherited(f.Ref, "V");
        switch (kind)
        {
            case PdfFormFieldType.CheckBox:
            case PdfFormFieldType.RadioButton:
                if (v is PdfName n) values.Add(n.Value);
                else if (f.Widgets.Select(w => Nm(R(w) as PdfDict, "AS")).FirstOrDefault(s => s != null && s != "Off") is string on) values.Add(on);
                else values.Add("Off");
                break;
            case PdfFormFieldType.ListBox:
            case PdfFormFieldType.ComboBox:
                if (v is PdfArray arr) values.AddRange(arr.Items.Select(Str).OfType<string>());
                else if (Str(v) is string s) values.Add(s);
                break;
            case PdfFormFieldType.Text:
                if (Str(v) is string text) values.Add(text);
                break;
        }
        var options = kind switch
        {
            PdfFormFieldType.ComboBox or PdfFormFieldType.ListBox => ChoiceOptions(f),
            PdfFormFieldType.CheckBox or PdfFormFieldType.RadioButton => ButtonOptions(f),
            _ => new List<(string Value, string Text)>(),
        };
        int page = f.Widgets.Select(w => _widgetPage.TryGetValue(w.Num, out int p) ? p + 1 : 0).FirstOrDefault(p => p > 0);
        if (page == 0)
            foreach (var w in f.Widgets)
                if (R(w) is PdfDict wd && wd["P"] is PdfRef pr && _pages.FindIndex(p => p.Num == pr.Num) is int pi and >= 0) { page = pi + 1; break; }
        return new PdfFormField(f.Name, kind, values, options.Select(o => new PdfFormFieldOption(o.Value, o.Text)).ToList(),
            (ff & FfReadOnly) != 0, (ff & FfRequired) != 0, (ff & FfMultiline) != 0, (ff & FfPassword) != 0,
            (ff & FfMultiSelect) != 0, (ff & FfEdit) != 0, Inherited(f.Ref, "MaxLen") is PdfNumber ml ? ml.IntValue : 0,
            Str(R(f.Ref) is PdfDict fd ? fd["TU"] : null), page);
    }

    private bool IsSigned()
    {
        if (D(Catalog, "Perms") is { Entries.Count: > 0 }) return true;
        return _fields.Any(f => FieldType(f) == "Sig" && Inherited(f.Ref, "V") is PdfDict);
    }

    // ------------------------------------------------------------------ filling

    private void FillValues(IDictionary<string, string> values, PdfFormFillOptions options)
    {
        var byName = new Dictionary<string, Field>(StringComparer.Ordinal);
        foreach (var f in _fields) byName.TryAdd(f.Name, f);
        var toDraw = new List<(Field Field, string? Shown)>();
        foreach (var (name, value) in values)
        {
            _cancel.ThrowIfCancellationRequested();
            if (name == null) throw new ArgumentException("A field name is null.", nameof(values));
            if (!byName.TryGetValue(name, out var f))
            {
                if (options.IgnoreUnknownFields) continue;
                throw new ArgumentException($"The PDF has no form field named \"{name}\".", nameof(values));
            }
            string text = value ?? "";
            switch (Kind(f))
            {
                case PdfFormFieldType.Text:
                {
                    int max = Inherited(f.Ref, "MaxLen") is PdfNumber ml ? ml.IntValue : 0;
                    if (max > 0 && text.Length > max)
                        throw new ArgumentException($"The value of \"{name}\" has {text.Length} characters; the field takes at most {max}.", nameof(values));
                    var d = Edit(f.Ref);
                    d.Entries["V"] = SignatureWriter.Text(text);
                    d.Entries.Remove("RV"); // rich text would show the old value
                    toDraw.Add((f, text));
                    break;
                }
                case PdfFormFieldType.CheckBox:
                case PdfFormFieldType.RadioButton:
                    SetButton(f, text, name);
                    break;
                case PdfFormFieldType.ComboBox:
                case PdfFormFieldType.ListBox:
                    toDraw.Add((f, SetChoice(f, text, name)));
                    break;
                default:
                    throw new ArgumentException($"The field \"{name}\" is a {Kind(f)} field, which has no value to fill.", nameof(values));
            }
        }

        // Fields a viewer was asked to draw get their appearances now, since NeedAppearances is cleared; so do fields
        // without an appearance that are about to be flattened.
        var acro = AcroForm;
        bool needAll = acro != null && R(acro["NeedAppearances"]) is PdfBool { Value: true };
        var drawn = new HashSet<Field>(toDraw.Select(t => t.Field));
        foreach (var f in _fields)
        {
            if (drawn.Contains(f) || Kind(f) is not (PdfFormFieldType.Text or PdfFormFieldType.ComboBox or PdfFormFieldType.ListBox)) continue;
            if (needAll || options.Flatten && f.Widgets.Any(w => R(D(R(w) as PdfDict, "AP")?["N"]) is not PdfStream))
                toDraw.Add((f, null));
        }

        _fallback = null;
        _fallbackData = options.Font;
        foreach (var (f, shown) in toDraw)
        {
            _cancel.ThrowIfCancellationRequested();
            DrawField(f, shown);
        }
        _fallback?.Finish(this);

        if (toDraw.Count > 0 || needAll || values.Count > 0)
        {
            var acroForm = EditAcroForm();
            if (acroForm != null)
            {
                acroForm.Entries.Remove("NeedAppearances");
                // XFA data would be shown instead of the filled fields by viewers that read it.
                acroForm.Entries.Remove("XFA");
            }
        }
    }

    /// <summary>The AcroForm dictionary, made editable: in the overlay itself when indirect, else within the catalog.</summary>
    private PdfDict? EditAcroForm()
    {
        var entry = Catalog["AcroForm"];
        if (entry is PdfRef r) return R(r) is PdfDict ? Edit(r) : null;
        if (entry is not PdfDict direct) return null;
        var copy = new PdfDict(new Dictionary<string, PdfObject>(direct.Entries, StringComparer.Ordinal));
        Edit(_rootRef).Entries["AcroForm"] = copy;
        return copy;
    }

    private void SetButton(Field f, string value, string name)
    {
        var options = ButtonOptions(f);
        string? state;
        string v = value.Trim();
        if (v.Length == 0 || v.Equals("Off", StringComparison.OrdinalIgnoreCase) || v.Equals("false", StringComparison.OrdinalIgnoreCase)
            || v.Equals("no", StringComparison.OrdinalIgnoreCase) || v == "0")
            state = null;
        else if (options.FirstOrDefault(o => o.Value == value).Value is string exact) state = exact;
        else if (options.FirstOrDefault(o => o.Text == value).Value is string byText) state = byText;
        else if (Kind(f) == PdfFormFieldType.CheckBox && (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase)
                 || v.Equals("on", StringComparison.OrdinalIgnoreCase) || v == "1"))
            state = options.Count > 0 ? options[0].Value : "Yes";
        else
            throw new ArgumentException($"\"{value}\" is not a state of the field \"{name}\"; it has {string.Join(", ", options.Select(o => "\"" + o.Value + "\"").Append("\"Off\""))}.", "values");

        var on = PdfName.Get(state ?? "Off");
        Edit(f.Ref).Entries["V"] = on;
        foreach (var w in f.Widgets)
        {
            var wd = Edit(w);
            string? widgetOn = OnState(w);
            if (widgetOn == null && state != null && Kind(f) == PdfFormFieldType.CheckBox) widgetOn = AddCheckAppearance(wd, state);
            wd.Entries["AS"] = PdfName.Get(state != null && widgetOn == state ? state : "Off");
        }
    }

    /// <summary>Gives a check box widget without appearances a check mark and an empty off state; returns the on state.</summary>
    private string AddCheckAppearance(PdfDict widget, string state)
    {
        var (w, h) = RectSize(widget);
        double size = Math.Max(1, Math.Min(w, h) * 0.8);
        var content = new StringBuilder();
        content.Append("q 0 g BT /ZaDb ").Append(N(size)).Append(" Tf ").Append(N((w - size * 0.75) / 2)).Append(' ')
            .Append(N((h - size * 0.7) / 2)).Append(" Td (4) Tj ET Q\n");
        var zadb = new PdfDict(Entries());
        zadb.Entries["Type"] = PdfName.Get("Font");
        zadb.Entries["Subtype"] = PdfName.Get("Type1");
        zadb.Entries["BaseFont"] = PdfName.Get("ZapfDingbats");
        var fonts = new PdfDict(Entries());
        fonts.Entries["ZaDb"] = zadb;
        var res = new PdfDict(Entries());
        res.Entries["Font"] = fonts;
        var onEntries = FormEntries(w, h);
        onEntries["Resources"] = res;
        var onRef = Add(FlateStream(onEntries, Encoding.ASCII.GetBytes(content.ToString())));
        var offRef = Add(FlateStream(FormEntries(w, h), ReadOnlySpan<byte>.Empty));
        var n = new PdfDict(Entries());
        n.Entries[state] = onRef;
        n.Entries["Off"] = offRef;
        var ap = new PdfDict(Entries());
        ap.Entries["N"] = n;
        widget.Entries["AP"] = ap;
        return state;
    }

    /// <summary>Sets a choice field's value and selection; returns the text to show for a combo box.</summary>
    private string? SetChoice(Field f, string value, string name)
    {
        var options = ChoiceOptions(f);
        int ff = Flags(f);
        bool combo = (ff & FfCombo) != 0, multi = !combo && (ff & FfMultiSelect) != 0;
        var parts = multi ? value.Replace("\r\n", "\n").Split('\n').Where(p => p.Length > 0).ToArray() : value.Length == 0 ? Array.Empty<string>() : new[] { value };
        if (!combo && !multi && parts.Length > 1) throw new ArgumentException($"The list box \"{name}\" takes one choice.", "values");
        var indices = new List<int>();
        string? shown = null;
        var exports = new List<string>();
        foreach (var part in parts)
        {
            int i = options.FindIndex(o => o.Value == part);
            if (i < 0) i = options.FindIndex(o => o.Text == part);
            if (i >= 0)
            {
                if (!indices.Contains(i)) { indices.Add(i); exports.Add(options[i].Value); }
                shown ??= options[i].Text;
            }
            else if (combo && (ff & FfEdit) != 0)
            {
                exports.Add(part);
                shown = part;
            }
            else
                throw new ArgumentException($"\"{part}\" is not a choice of the field \"{name}\".", "values");
        }
        var d = Edit(f.Ref);
        if (exports.Count == 0) d.Entries.Remove("V");
        else if (exports.Count == 1) d.Entries["V"] = SignatureWriter.Text(exports[0]);
        else d.Entries["V"] = new PdfArray(exports.Select(e => (PdfObject)SignatureWriter.Text(e)).ToList());
        indices.Sort();
        if (indices.Count == 0) d.Entries.Remove("I");
        else d.Entries["I"] = new PdfArray(indices.Select(i => (PdfObject)PdfNumber.Int(i)).ToList());
        return shown ?? "";
    }

    // ------------------------------------------------------------------ writing

    private byte[] WriteIncremental(byte[] original)
    {
        var crypt = _file.Decryptor;
        var output = new ByteBuffer(original.Length + 65536);
        output.Bytes(original);
        if (original.Length == 0 || original[^1] != '\n') output.Nl();
        var offsets = new SortedDictionary<int, (long Offset, int Gen)>();
        foreach (var (num, (gen, obj)) in _changed.OrderBy(kv => kv.Key))
        {
            _cancel.ThrowIfCancellationRequested();
            offsets[num] = (output.Length, gen);
            output.Int(num).Sp().Int(gen).Ascii(" obj\n");
            Func<byte[], byte[]> encrypt = s => crypt == null ? s : crypt.EncryptString(s, num, gen);
            if (obj is PdfStream s)
            {
                var data = crypt == null ? s.RawData : crypt.EncryptStream(s.RawData, num, gen);
                var entries = new Dictionary<string, PdfObject>(s.Entries, StringComparer.Ordinal) { ["Length"] = PdfNumber.Int(data.Length) };
                SignatureWriter.Serialize(output, new PdfDict(entries), encrypt);
                output.Ascii("\nstream\n").Bytes(data).Ascii("\nendstream");
            }
            else SignatureWriter.Serialize(output, obj, encrypt);
            output.Ascii("\nendobj\n");
        }
        SignatureWriter.WriteXref(_file, output, offsets, _rootRef, _next);
        return output.Span.ToArray();
    }

    // ------------------------------------------------------------------ helpers

    private static string N(double v) => double.IsFinite(v) ? Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture) : "0";

    private (double W, double H) RectSize(PdfDict widget)
    {
        var r = Nums(widget, "Rect");
        if (r == null || r.Length < 4) return (0, 0);
        return (Math.Abs(r[2] - r[0]), Math.Abs(r[3] - r[1]));
    }

    private static Dictionary<string, PdfObject> FormEntries(double w, double h)
    {
        var e = Entries();
        e["Type"] = PdfName.Get("XObject");
        e["Subtype"] = PdfName.Get("Form");
        e["BBox"] = new PdfArray(new List<PdfObject> { PdfNumber.Int(0), PdfNumber.Int(0), PdfNumber.Real(Math.Round(w, 3)), PdfNumber.Real(Math.Round(h, 3)) });
        return e;
    }

    private byte[] Decode(PdfStream s)
    {
        try { return _file.DecodeStream(s); }
        catch (Exception ex) when (ex is PdfFormatException or InvalidDataException or NotSupportedException or IndexOutOfRangeException) { return Array.Empty<byte>(); }
    }

    /// <summary>
    /// The content of an old appearance without its text (BT to ET), so its box, border and background stay under the
    /// new value; null when it cannot be parsed.
    /// </summary>
    private static byte[]? WithoutText(byte[] content)
    {
        var parser = new ContentParser(content);
        var output = new MemoryStream(content.Length);
        int depth = 0;
        try
        {
            while (parser.Next())
            {
                if (parser.Op == "BT") { depth++; continue; }
                if (parser.Op == "ET") { if (depth > 0) depth--; continue; }
                if (depth > 0) continue;
                output.Write(content, parser.Start, parser.End - parser.Start);
            }
        }
        catch (PdfFormatException)
        {
            return null;
        }
        output.WriteByte((byte)'\n');
        return output.ToArray();
    }
}
