using System.Globalization;
using System.Text;
using PdfPecker.Fonts;
using PdfPecker.Rendering.Content;
using PdfPecker.Rendering.Parsing;
using Encodings = PdfPecker.Rendering.Fonts.Encodings;
using RenderFont = PdfPecker.Rendering.Fonts.PdfFont;

namespace PdfPecker.Pdf;

/// <summary>Appearance streams for filled fields, the fonts they draw with, and flattening.</summary>
internal sealed partial class PdfFormEditor
{
    private FallbackFont? _fallback;
    private byte[]? _fallbackData;

    // ------------------------------------------------------------------ fonts

    /// <summary>A font an appearance draws with: how wide text is and how to write it in a content stream.</summary>
    private abstract class AppearanceFont
    {
        public string ResourceName = "";
        public PdfObject Ref = PdfNull.Instance;
        public double Ascent = 0.8, Descent = -0.2; // per unit font size

        public abstract double Width(string s);

        /// <summary>Appends <paramref name="s"/> as a string operand.</summary>
        public abstract void Show(StringBuilder b, string s);
    }

    /// <summary>A simple font of the form's resources, used when its encoding has every character of the value.</summary>
    private sealed class SimpleFont : AppearanceFont
    {
        private readonly Dictionary<int, byte> _codes = new();
        private readonly RenderFont _metrics;

        private SimpleFont(RenderFont metrics) => _metrics = metrics;

        public static SimpleFont? Create(PdfFormEditor e, string name, PdfObject reference, PdfDict font)
        {
            var subtype = e.Nm(font, "Subtype");
            if (subtype is not ("Type1" or "TrueType" or "MMType1")) return null;
            string baseFont = e.Nm(font, "BaseFont") ?? "";
            var desc = e.D(font, "FontDescriptor");
            bool embedded = desc != null && (desc["FontFile"] != null || desc["FontFile2"] != null || desc["FontFile3"] != null);
            string?[]? baseEnc = null;
            var diffs = new string?[256];
            var enc = e.R(font["Encoding"]);
            if (enc is PdfName en) baseEnc = Encodings.ByName(en.Value);
            else if (enc is PdfDict ed)
            {
                baseEnc = Encodings.ByName(e.Nm(ed, "BaseEncoding"));
                if (e.A(ed, "Differences") is PdfArray da)
                {
                    int code = 0;
                    foreach (var item in da.Items)
                    {
                        var o = e.R(item);
                        if (o is PdfNumber n) code = n.IntValue;
                        else if (o is PdfName nm) { if (code is >= 0 and < 256) diffs[code] = nm.Value; code++; }
                    }
                }
            }
            // Symbol fonts have their own built-in encoding, which is not text.
            if (baseEnc == null && (baseFont.Contains("Symbol", StringComparison.Ordinal) || baseFont.Contains("Dingbats", StringComparison.Ordinal))) return null;
            baseEnc ??= Encodings.Standard;
            RenderFont metrics;
            try
            {
                metrics = RenderFont.Load(e._file, font);
            }
            catch (Exception ex) when (ex is PdfFormatException or InvalidDataException or NotSupportedException)
            {
                return null;
            }
            var f = new SimpleFont(metrics) { ResourceName = name, Ref = reference };
            for (int c = 0; c < 256; c++)
            {
                var glyph = diffs[c] ?? baseEnc[c];
                int u = Encodings.NameToUnicode(glyph);
                // An embedded font is often a subset: only codes with a glyph can be used.
                if (u > 0 && !f._codes.ContainsKey(u) && (!embedded || metrics.GlyphId(c) != 0)) f._codes[u] = (byte)c;
            }
            if (desc != null && e.R(desc["Ascent"]) is PdfNumber asc && asc.Value > 0) f.Ascent = asc.Value / 1000;
            if (desc != null && e.R(desc["Descent"]) is PdfNumber dsc && dsc.Value < 0) f.Descent = dsc.Value / 1000;
            return f;
        }

        public bool CanEncode(string s)
        {
            foreach (char c in s)
                if (!_codes.ContainsKey(c)) return false;
            return true;
        }

        public override double Width(string s)
        {
            double w = 0;
            foreach (char c in s) if (_codes.TryGetValue(c, out byte code)) w += _metrics.Width(code);
            return w;
        }

        public override void Show(StringBuilder b, string s)
        {
            b.Append('<');
            foreach (char c in s) if (_codes.TryGetValue(c, out byte code)) b.Append(code.ToString("X2", CultureInfo.InvariantCulture));
            b.Append('>');
        }
    }

    /// <summary>
    /// A subset of a TrueType or OpenType font embedded for the values the form's own fonts cannot show: one per fill,
    /// shared by all fields, written once every appearance has added its glyphs.
    /// </summary>
    private sealed class FallbackFont : AppearanceFont
    {
        private readonly GlyphSubset _subset;
        private readonly FontProgram _program;
        private readonly PdfRef _fontRef;

        public FallbackFont(FontProgram program, PdfRef fontRef)
        {
            _program = program;
            _subset = new GlyphSubset(program);
            _fontRef = fontRef;
            Ref = fontRef;
            ResourceName = "PdfPeckerFill";
            Ascent = (double)program.Ascender / program.UnitsPerEm;
            Descent = -Math.Abs((double)program.Descender) / program.UnitsPerEm;
        }

        private static IEnumerable<int> CodePoints(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { yield return char.ConvertToUtf32(s[i], s[i + 1]); i++; }
                else yield return s[i];
            }
        }

        public override double Width(string s)
        {
            double w = 0;
            foreach (int cp in CodePoints(s)) w += (double)_program.Advance(_program.GlyphId(cp)) / _program.UnitsPerEm;
            return w;
        }

        public override void Show(StringBuilder b, string s)
        {
            b.Append('<');
            foreach (int cp in CodePoints(s)) b.Append(_subset.Cid(cp).ToString("X4", CultureInfo.InvariantCulture));
            b.Append('>');
        }

        /// <summary>Writes the Type 0 font, its CID font, descriptor, font file and ToUnicode map, as the HTML writer does.</summary>
        public void Finish(PdfFormEditor e)
        {
            var used = _subset.Used().ToList();
            string baseFont = _subset.Tag() + "+" + _program.PostScriptName;
            var cidRef = e.Reserve();
            var descRef = e.Reserve();
            var fileData = FontSubsetter.Build(_subset);
            var fileEntries = Entries();
            if (_program.IsCff) fileEntries["Subtype"] = PdfName.Get("OpenType");
            else fileEntries["Length1"] = PdfNumber.Int(fileData.Length);
            var fileRef = e.Add(FlateStream(fileEntries, fileData));

            var cmap = new StringBuilder("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n")
                .Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
            var mapped = used.Where(u => u.CodePoint > 0).ToList();
            for (int i = 0; i < mapped.Count; i += 100)
            {
                int n = Math.Min(100, mapped.Count - i);
                cmap.Append(n).Append(" beginbfchar\n");
                for (int k = i; k < i + n; k++)
                {
                    cmap.Append('<').Append(mapped[k].Cid.ToString("X4", CultureInfo.InvariantCulture)).Append("> <");
                    foreach (char c in char.ConvertFromUtf32(mapped[k].CodePoint)) cmap.Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    cmap.Append(">\n");
                }
                cmap.Append("endbfchar\n");
            }
            cmap.Append("endcmap\nCMapName currentdict /CMapResource defineresource pop\nend\nend\n");
            var toUnicode = e.Add(FlateStream(Entries(), Encoding.ASCII.GetBytes(cmap.ToString())));

            var type0 = new PdfDict(Entries());
            type0.Entries["Type"] = PdfName.Get("Font");
            type0.Entries["Subtype"] = PdfName.Get("Type0");
            type0.Entries["BaseFont"] = PdfName.Get(baseFont);
            type0.Entries["Encoding"] = PdfName.Get("Identity-H");
            type0.Entries["DescendantFonts"] = new PdfArray(new List<PdfObject> { cidRef });
            type0.Entries["ToUnicode"] = toUnicode;
            e.Put(_fontRef, type0);

            var widths = new List<PdfObject>();
            foreach (var u in used)
            {
                widths.Add(PdfNumber.Int(u.Cid));
                widths.Add(new PdfArray(new List<PdfObject> { PdfNumber.Int(_program.Advance1000(u.Gid)) }));
            }
            var info = new PdfDict(Entries());
            info.Entries["Registry"] = new PdfString(Encoding.ASCII.GetBytes("Adobe"));
            info.Entries["Ordering"] = new PdfString(Encoding.ASCII.GetBytes("Identity"));
            info.Entries["Supplement"] = PdfNumber.Int(0);
            var cid = new PdfDict(Entries());
            cid.Entries["Type"] = PdfName.Get("Font");
            cid.Entries["Subtype"] = PdfName.Get(_program.IsCff ? "CIDFontType0" : "CIDFontType2");
            cid.Entries["BaseFont"] = PdfName.Get(baseFont);
            cid.Entries["CIDSystemInfo"] = info;
            cid.Entries["FontDescriptor"] = descRef;
            cid.Entries["DW"] = PdfNumber.Int(1000);
            cid.Entries["W"] = new PdfArray(widths);
            if (!_program.IsCff) cid.Entries["CIDToGIDMap"] = PdfName.Get("Identity");
            e.Put(cidRef, cid);

            double scale = 1000.0 / _program.UnitsPerEm;
            var desc = new PdfDict(Entries());
            desc.Entries["Type"] = PdfName.Get("FontDescriptor");
            desc.Entries["FontName"] = PdfName.Get(baseFont);
            desc.Entries["Flags"] = PdfNumber.Int(4 | (_program.FixedPitch ? 1 : 0) | (_program.IsItalic || _program.ItalicAngle != 0 ? 64 : 0));
            desc.Entries["FontBBox"] = new PdfArray(new List<PdfObject>
            {
                PdfNumber.Int((int)(_program.XMin * scale)), PdfNumber.Int((int)(_program.YMin * scale)),
                PdfNumber.Int((int)Math.Ceiling(_program.XMax * scale)), PdfNumber.Int((int)Math.Ceiling(_program.YMax * scale)),
            });
            desc.Entries["ItalicAngle"] = PdfNumber.Real(_program.ItalicAngle);
            desc.Entries["Ascent"] = PdfNumber.Int((int)Math.Round(_program.Ascender * scale));
            desc.Entries["Descent"] = PdfNumber.Int((int)Math.Round(-Math.Abs(_program.Descender) * scale));
            desc.Entries["CapHeight"] = PdfNumber.Int((int)Math.Round(_program.CapHeight * scale));
            desc.Entries["StemV"] = PdfNumber.Int(50 + (int)(_program.WeightClass / 65f * (_program.WeightClass / 65f)));
            desc.Entries[_program.IsCff ? "FontFile3" : "FontFile2"] = fileRef;
            e.Put(descRef, desc);
        }
    }

    /// <summary>The font for values a field's own font cannot show, created on first use.</summary>
    private FallbackFont Fallback(string daFont)
    {
        if (_fallback != null) return _fallback;
        FontProgram program;
        if (_fallbackData != null)
        {
            try
            {
                program = FontProgram.Load(_fallbackData)[0];
            }
            catch (Exception ex) when (ex is FormatException or InvalidDataException or IndexOutOfRangeException or ArgumentException)
            {
                SwallowedErrors.Record(ex);
                throw new ArgumentException("PdfFormFillOptions.Font is not a TrueType or OpenType font: " + ex.Message, "options", ex);
            }
        }
        else
        {
            // The Liberation face closest to the field's own font, so the filled value looks like the rest of the form.
            string lower = daFont.ToLowerInvariant();
            var family = lower.StartsWith("cour", StringComparison.Ordinal) || lower.Contains("mono") ? FontFamily.Courier
                : lower.StartsWith("tiro", StringComparison.Ordinal) || lower.Contains("times") || lower.Contains("serif") ? FontFamily.Times
                : FontFamily.Helvetica;
            bool bold = lower.Contains("bold") || lower is "hebo" or "tibo" or "cobo";
            program = BundledFonts.Get(StandardFont.Get(family, bold, false))?.Program
                ?? throw new InvalidOperationException("The bundled Liberation fonts are missing from the library.");
        }
        return _fallback = new FallbackFont(program, Reserve());
    }

    // ------------------------------------------------------------------ appearances

    /// <summary>The parts of a default appearance string: font resource name, size and the colour operators.</summary>
    private static (string Font, double Size, string Color) ParseDa(string da)
    {
        string font = "Helv", color = "";
        double size = 0;
        var parser = new ContentParser(Encoding.Latin1.GetBytes(da));
        var colors = new StringBuilder();
        try
        {
            while (parser.Next())
            {
                var ops = parser.Operands;
                switch (parser.Op)
                {
                    case "Tf":
                        if (ops.Count >= 2 && ops[0].Name is string name) { font = name; size = ops[1].Kind == OperandKind.Number ? ops[1].Num : 0; }
                        break;
                    case "g" or "rg" or "k":
                        colors.Clear();
                        foreach (var o in ops) if (o.Kind == OperandKind.Number) colors.Append(N(o.Num)).Append(' ');
                        colors.Append(parser.Op);
                        break;
                }
            }
        }
        catch (PdfFormatException)
        {
        }
        color = colors.Length > 0 ? colors.ToString() : "0 g";
        return (font, double.IsFinite(size) && size > 0 && size < 1000 ? size : 0, color);
    }

    private void DrawField(Field f, string? shown)
    {
        var kind = Kind(f);
        int ff = Flags(f);
        var acro = AcroForm;
        string da = Str(Inherited(f.Ref, "DA")) ?? Str(acro?["DA"]) ?? "/Helv 0 Tf 0 g";
        int q = Inherited(f.Ref, "Q") is PdfNumber qn ? qn.IntValue : I(acro, "Q");
        var (fontName, size, color) = ParseDa(da);

        // What the widgets show: one line of text, several lines, or a list with chosen entries.
        var lines = new List<string>();
        var selected = new HashSet<int>();
        int topIndex = 0;
        bool list = kind == PdfFormFieldType.ListBox;
        if (list)
        {
            var options = ChoiceOptions(f);
            lines.AddRange(options.Select(o => o.Text));
            if (Inherited(f.Ref, "I") is PdfArray idx) foreach (var i in idx.Items) if (R(i) is PdfNumber n) selected.Add(n.IntValue);
            if (selected.Count == 0)
            {
                var v = Inherited(f.Ref, "V");
                var values = v is PdfArray va ? va.Items.Select(Str).OfType<string>().ToHashSet(StringComparer.Ordinal) : Str(v) is string sv ? new HashSet<string>(StringComparer.Ordinal) { sv } : new HashSet<string>();
                for (int i = 0; i < options.Count; i++) if (values.Contains(options[i].Value)) selected.Add(i);
            }
            topIndex = Math.Max(0, I(R(f.Ref) as PdfDict, "TI"));
        }
        else
        {
            string text = shown ?? (kind == PdfFormFieldType.ComboBox ? ComboText(f) : Str(Inherited(f.Ref, "V")) ?? "");
            if ((ff & FfPassword) != 0) text = new string('*', text.Length);
            lines.Add(text);
        }

        // The field's own font when it can show every character, else the embedded fallback.
        var dr = Inherited(f.Ref, "DR") as PdfDict ?? D(acro, "DR");
        var fontRes = D(dr, "Font");
        AppearanceFont font;
        var simple = fontRes?[fontName] is { } fref && R(fref) is PdfDict fd ? SimpleFont.Create(this, fontName, fref, fd) : null;
        if (simple != null && lines.All(l => simple.CanEncode(l.Replace("\r", "").Replace("\n", "")))) font = simple;
        else font = Fallback(fontName);

        foreach (var w in f.Widgets)
            DrawWidget(w, font, size, color, q, lines, kind, ff, selected, topIndex, f);
    }

    private string ComboText(Field f)
    {
        string? v = Str(Inherited(f.Ref, "V"));
        if (v == null) return "";
        var match = ChoiceOptions(f).FirstOrDefault(o => o.Value == v);
        return match.Text ?? v;
    }

    private void DrawWidget(PdfRef widgetRef, AppearanceFont font, double daSize, string color, int q, List<string> lines,
        PdfFormFieldType kind, int ff, HashSet<int> selected, int topIndex, Field f)
    {
        var wd = Edit(widgetRef);
        var (rw, rh) = RectSize(wd);
        if (rw <= 0 || rh <= 0) return;
        var mk = D(wd, "MK");
        int rot = ((I(mk, "R") % 360) + 360) % 360 / 90 * 90;
        var old = R(D(wd, "AP")?["N"]) as PdfStream;

        double[] bbox;
        PdfObject? matrix = null;
        if (old != null && Nums(old, "BBox") is { Length: >= 4 } ob && Math.Abs(ob[2] - ob[0]) > 0 && Math.Abs(ob[3] - ob[1]) > 0)
        {
            bbox = new[] { Math.Min(ob[0], ob[2]), Math.Min(ob[1], ob[3]), Math.Max(ob[0], ob[2]), Math.Max(ob[1], ob[3]) };
            matrix = R(old["Matrix"]);
        }
        else
        {
            bbox = rot is 90 or 270 ? new[] { 0, 0, rh, rw } : new[] { 0, 0, rw, rh };
            double[]? m = rot switch
            {
                90 => new[] { 0, 1, -1, 0, rw, 0 },
                180 => new[] { -1, 0, 0, -1, rw, rh },
                270 => new[] { 0, -1, 1, 0, 0, rh },
                _ => null,
            };
            if (m != null) matrix = new PdfArray(m.Select(x => (PdfObject)PdfNumber.Int((long)x)).ToList());
        }
        double x0 = bbox[0], y0 = bbox[1], bw = bbox[2] - bbox[0], bh = bbox[3] - bbox[1];

        double border = 1;
        if (D(wd, "BS") is PdfDict bs && R(bs["W"]) is PdfNumber bsw) border = bsw.Value;
        else if (A(wd, "Border") is { Count: >= 3 } ba && R(ba[2]) is PdfNumber bn) border = bn.Value;
        border = Math.Clamp(border, 0, Math.Min(bw, bh) / 4);
        double pad = Math.Max(1, border) + 1;

        var sb = new StringBuilder();
        PdfDict? oldResources = null;
        byte[]? background = old != null ? WithoutText(Decode(old)) : null;
        if (background != null)
        {
            sb.Append("q\n").Append(Encoding.Latin1.GetString(background)).Append("Q\n");
            oldResources = R(old!["Resources"]) as PdfDict;
        }
        else
        {
            if (Color(A(mk, "BG"), false) is string bg) sb.Append("q ").Append(bg).Append(' ').Append(N(x0)).Append(' ').Append(N(y0)).Append(' ')
                .Append(N(bw)).Append(' ').Append(N(bh)).Append(" re f Q\n");
            if (border > 0 && Color(A(mk, "BC"), true) is string bc) sb.Append("q ").Append(bc).Append(' ').Append(N(border)).Append(" w ")
                .Append(N(x0 + border / 2)).Append(' ').Append(N(y0 + border / 2)).Append(' ').Append(N(bw - border)).Append(' ').Append(N(bh - border)).Append(" re S Q\n");
        }

        double availW = Math.Max(1, bw - 2 * pad), availH = Math.Max(1, bh - 2 * pad);
        double lineFactor = 1.15;
        sb.Append("/Tx BMC\nq\n").Append(N(x0 + pad)).Append(' ').Append(N(y0 + pad)).Append(' ').Append(N(availW)).Append(' ').Append(N(availH)).Append(" re W n\n");
        bool multiline = kind == PdfFormFieldType.Text && (ff & FfMultiline) != 0;
        int maxLen = Inherited(f.Ref, "MaxLen") is PdfNumber ml ? ml.IntValue : 0;
        bool comb = kind == PdfFormFieldType.Text && (ff & FfComb) != 0 && (ff & (FfMultiline | FfPassword)) == 0 && maxLen > 0;

        if (kind == PdfFormFieldType.ListBox)
        {
            // A tiny font size would otherwise draw every option, however many, in every widget of the list.
            double size = daSize > 0 ? Math.Max(daSize, 1) : 12;
            double lineH = size * lineFactor;
            double top = y0 + bh - pad;
            for (int i = topIndex, row = 0; i < lines.Count && row < MaxListRows; i++, row++)
            {
                double lineTop = top - row * lineH;
                if (lineTop < y0) break;
                if (selected.Contains(i))
                    sb.Append("0.6 0.75 0.86 rg ").Append(N(x0 + pad)).Append(' ').Append(N(lineTop - lineH)).Append(' ').Append(N(availW)).Append(' ').Append(N(lineH)).Append(" re f\n");
                AppendLine(sb, font, size, color, lines[i], Align(q, font.Width(lines[i]) * size, x0, bw, pad), lineTop - (lineH - (font.Ascent - font.Descent) * size) / 2 - font.Ascent * size);
            }
        }
        else if (multiline)
        {
            double size = daSize;
            List<string> wrapped;
            if (size > 0) wrapped = Wrap(font, lines[0], size, availW);
            else
            {
                // Automatic size: the largest size up to 12 points at which the wrapped text fits the box.
                size = 12;
                wrapped = Wrap(font, lines[0], size, availW);
                while (size > 4 && wrapped.Count * size * lineFactor > availH)
                {
                    size -= 0.5;
                    wrapped = Wrap(font, lines[0], size, availW);
                }
            }
            double baseline = y0 + bh - pad - font.Ascent * size;
            foreach (var line in wrapped)
            {
                AppendLine(sb, font, size, color, line, Align(q, font.Width(line) * size, x0, bw, pad), baseline);
                baseline -= size * lineFactor;
                if (baseline < y0 - size) break;
            }
        }
        else
        {
            string text = lines[0].Replace("\r", " ").Replace("\n", " ");
            double size = daSize;
            if (size <= 0)
            {
                // Automatic size: as large as the box's height allows, then small enough for the text's width.
                size = availH / (font.Ascent - font.Descent);
                double width = comb ? 0 : font.Width(text);
                if (width > 0) size = Math.Min(size, availW / width);
                size = Math.Clamp(size, 2, 72);
            }
            double baseline = y0 + (bh - (font.Ascent - font.Descent) * size) / 2 - font.Descent * size;
            if (comb)
            {
                double cell = bw / maxLen;
                var elements = StringInfo.GetTextElementEnumerator(text);
                for (int i = 0; elements.MoveNext() && i < maxLen; i++)
                {
                    string ch = elements.GetTextElement();
                    AppendLine(sb, font, size, color, ch, x0 + i * cell + (cell - font.Width(ch) * size) / 2, baseline);
                }
            }
            else AppendLine(sb, font, size, color, text, Align(q, font.Width(text) * size, x0, bw, pad), baseline);
        }
        sb.Append("Q\nEMC\n");

        // Resources: those of the old appearance, with the font added.
        var res = new PdfDict(oldResources == null ? Entries() : new Dictionary<string, PdfObject>(oldResources.Entries, StringComparer.Ordinal));
        var fonts = new PdfDict(D(res, "Font") is PdfDict of ? new Dictionary<string, PdfObject>(of.Entries, StringComparer.Ordinal) : Entries());
        fonts.Entries[font.ResourceName] = font.Ref;
        res.Entries["Font"] = fonts;

        var entries = FormEntries(bw, bh);
        entries["BBox"] = new PdfArray(bbox.Select(v => (PdfObject)PdfNumber.Real(Math.Round(v, 3))).ToList());
        if (matrix is PdfArray) entries["Matrix"] = matrix;
        entries["Resources"] = res;
        var apRef = Add(FlateStream(entries, Encoding.Latin1.GetBytes(sb.ToString())));
        var ap = new PdfDict(Entries());
        ap.Entries["N"] = apRef;
        wd.Entries["AP"] = ap;
    }

    private static double Align(int q, double textWidth, double x0, double bw, double pad)
        => q == 1 ? x0 + (bw - textWidth) / 2 : q == 2 ? x0 + bw - pad - textWidth : x0 + pad;

    private static void AppendLine(StringBuilder sb, AppearanceFont font, double size, string color, string text, double x, double y)
    {
        if (text.Length == 0) return;
        sb.Append("BT /").Append(font.ResourceName).Append(' ').Append(N(size)).Append(" Tf ").Append(color).Append(" 1 0 0 1 ")
            .Append(N(x)).Append(' ').Append(N(y)).Append(" Tm ");
        font.Show(sb, text);
        sb.Append(" Tj ET\n");
    }

    // More rows than a list box can usefully show; the rest of its options are scrolled out of view anyway.
    private const int MaxListRows = 1_000;

    // More lines than any field can show; a longer value is cut off rather than wrapped to the end.
    private const int MaxWrappedLines = 10_000;

    /// <summary>
    /// Breaks text into lines that fit <paramref name="width"/>: at its line feeds, between words, and inside words too
    /// long for a line. Widths add up glyph by glyph, so the line's width is kept as a running sum instead of measuring
    /// the whole line again for every word, which made long values quadratic.
    /// </summary>
    private static List<string> Wrap(AppearanceFont font, string text, double size, double width)
    {
        var result = new List<string>();
        double space = font.Width(" ") * size;
        foreach (var paragraph in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = new StringBuilder();
            double lineWidth = 0;
            foreach (var word in paragraph.Split(' '))
            {
                double wordWidth = font.Width(word) * size;
                double candidate = line.Length == 0 ? wordWidth : lineWidth + space + wordWidth;
                if (candidate <= width || line.Length == 0 && wordWidth <= width)
                {
                    if (line.Length > 0) line.Append(' ');
                    line.Append(word);
                    lineWidth = candidate;
                    continue;
                }
                if (line.Length > 0) { result.Add(line.ToString()); line.Clear(); lineWidth = 0; }
                // A word wider than the line is split between characters, never inside a surrogate pair.
                foreach (var rune in word.EnumerateRunes())
                {
                    string r = rune.ToString();
                    double w = font.Width(r) * size;
                    if (line.Length > 0 && lineWidth + w > width)
                    {
                        result.Add(line.ToString());
                        if (result.Count >= MaxWrappedLines) return result;
                        line.Clear();
                        lineWidth = 0;
                    }
                    line.Append(r);
                    lineWidth += w;
                }
                if (result.Count >= MaxWrappedLines) return result;
            }
            result.Add(line.ToString());
            if (result.Count >= MaxWrappedLines) break;
        }
        return result;
    }

    /// <summary>A colour from an /MK array as a fill or stroke operator, or null for none (an empty array is transparent).</summary>
    private string? Color(PdfArray? a, bool stroke)
    {
        if (a == null || a.Count == 0) return null;
        var c = a.Items.Select(x => R(x) is PdfNumber n ? N(Math.Clamp(n.Value, 0, 1)) : "0").ToList();
        return a.Count switch
        {
            1 => c[0] + (stroke ? " G" : " g"),
            3 => string.Join(' ', c) + (stroke ? " RG" : " rg"),
            4 => string.Join(' ', c) + (stroke ? " K" : " k"),
            _ => null,
        };
    }

    // ------------------------------------------------------------------ flattening

    private void FlattenFields()
    {
        int counter = 0;
        for (int p = 0; p < _pages.Count; p++)
        {
            _cancel.ThrowIfCancellationRequested();
            var pageRef = _pages[p];
            var page = R(pageRef) as PdfDict;
            if (A(page, "Annots") is not PdfArray annots) continue;
            var keep = new List<PdfObject>();
            var ops = new StringBuilder();
            var forms = new List<(string Name, PdfRef Ref)>();
            var existing = D(InheritedResources(page!), "XObject");
            foreach (var item in annots.Items)
            {
                if (R(item) is not PdfDict a || Nm(a, "Subtype") != "Widget") { keep.Add(item); continue; }
                int flags = I(a, "F");
                if ((flags & (2 | 32)) != 0) continue; // hidden: removed without a trace, as a viewer shows it
                var normal = D(a, "AP")?["N"];
                if (R(normal) is PdfDict states and not PdfStream)
                    normal = Nm(a, "AS") is string state ? states[state] : null;
                if (normal is not PdfRef formRef || R(formRef) is not PdfStream form) continue;
                var rect = Nums(a, "Rect");
                if (rect == null || rect.Length < 4) continue;
                double rx0 = Math.Min(rect[0], rect[2]), ry0 = Math.Min(rect[1], rect[3]), rx1 = Math.Max(rect[0], rect[2]), ry1 = Math.Max(rect[1], rect[3]);
                var bbox = Nums(form, "BBox") ?? new[] { 0, 0, rx1 - rx0, ry1 - ry0 };
                if (bbox.Length < 4) continue;
                var fm = Nums(form, "Matrix") is { Length: 6 } m ? m : new double[] { 1, 0, 0, 1, 0, 0 };
                // As a viewer draws an annotation: the form's box, transformed by its matrix, is fitted to the rectangle.
                double bx0 = double.MaxValue, by0 = double.MaxValue, bx1 = double.MinValue, by1 = double.MinValue;
                foreach (var (x, y) in new[] { (bbox[0], bbox[1]), (bbox[2], bbox[1]), (bbox[0], bbox[3]), (bbox[2], bbox[3]) })
                {
                    double tx = fm[0] * x + fm[2] * y + fm[4], ty = fm[1] * x + fm[3] * y + fm[5];
                    bx0 = Math.Min(bx0, tx); by0 = Math.Min(by0, ty); bx1 = Math.Max(bx1, tx); by1 = Math.Max(by1, ty);
                }
                double sx = bx1 - bx0 > 1e-9 ? (rx1 - rx0) / (bx1 - bx0) : 1, sy = by1 - by0 > 1e-9 ? (ry1 - ry0) / (by1 - by0) : 1;
                string name;
                do name = "PdfPeckerField" + (++counter).ToString(CultureInfo.InvariantCulture);
                while (existing?[name] != null);
                forms.Add((name, formRef));
                ops.Append("q ").Append(N(sx)).Append(" 0 0 ").Append(N(sy)).Append(' ').Append(N(rx0 - bx0 * sx)).Append(' ').Append(N(ry0 - by0 * sy))
                    .Append(" cm /").Append(name).Append(" Do Q\n");
            }
            if (keep.Count == annots.Count) continue;

            var edited = Edit(pageRef);
            if (keep.Count > 0) edited.Entries["Annots"] = new PdfArray(keep);
            else edited.Entries.Remove("Annots");
            if (forms.Count == 0) continue;

            var res = InheritedResources(page!);
            var newRes = new PdfDict(res == null ? Entries() : new Dictionary<string, PdfObject>(res.Entries, StringComparer.Ordinal));
            var xobjects = new PdfDict(existing == null ? Entries() : new Dictionary<string, PdfObject>(existing.Entries, StringComparer.Ordinal));
            foreach (var (name, r) in forms) xobjects.Entries[name] = r;
            newRes.Entries["XObject"] = xobjects;
            edited.Entries["Resources"] = newRes;

            // The page's own content is wrapped in q and Q, so whatever state it leaves does not move the fields.
            var contents = new List<PdfObject> { Add(FlateStream(Entries(), "q\n"u8)) };
            var old = edited["Contents"];
            if (R(old) is PdfArray arr) contents.AddRange(arr.Items);
            else if (old is PdfRef) contents.Add(old);
            contents.Add(Add(FlateStream(Entries(), Encoding.ASCII.GetBytes("Q\n" + ops))));
            edited.Entries["Contents"] = new PdfArray(contents);
        }
        if (Catalog["AcroForm"] != null) Edit(_rootRef).Entries.Remove("AcroForm");
    }

    /// <summary>A page's resources, inherited from the page tree when the page has none of its own.</summary>
    private PdfDict? InheritedResources(PdfDict page)
    {
        var d = page;
        for (int i = 0; d != null && i < 64; i++)
        {
            if (D(d, "Resources") is PdfDict res) return res;
            d = D(d, "Parent");
        }
        return null;
    }
}
