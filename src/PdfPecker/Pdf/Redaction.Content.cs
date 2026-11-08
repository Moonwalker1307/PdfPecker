using PdfImage = PdfPecker.Images.PdfImage;
using PdfPecker.Rendering.Content;
using PdfPecker.Rendering.Fonts;
using PdfPecker.Rendering.Graphics;
using PdfPecker.Rendering.Images;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Pdf;

/// <summary>
/// Content streams: each operator is copied as it is unless it draws in an area. Text operators lose the glyphs in the
/// areas, with the space they took kept as TJ adjustments; paths lose the subpaths wholly inside one; images and forms
/// are dropped, or replaced by redacted copies.
/// </summary>
internal sealed partial class Redaction
{
    /// <summary>The graphics state the walk follows: the transform and the text state.</summary>
    private sealed class State
    {
        public Matrix Ctm;
        public PdfFont? Font;
        public string? FontName;
        public double FontSize, CharSpacing, WordSpacing, HScale = 1, Leading, Rise;

        public State Clone() => (State)MemberwiseClone();
    }

    /// <summary>
    /// Open marked content: its properties' place in the output when they carry texts to strip, with the name they have
    /// in the resources when they are named there, and its MCID.
    /// </summary>
    private sealed class Mark
    {
        public int Segment = -1;
        public string Tag = "";
        public PdfDict? Properties;
        public string? Named;
        public int Mcid = -1;
        public bool Removed;
    }

    /// <summary>The rewritten stream: copied and written bytes, with marked content properties held apart so they can be replaced.</summary>
    private sealed class Output
    {
        private readonly List<byte[]> _segments = new();
        private ByteBuffer _current = new(4096);
        private int _length;

        public void Copy(byte[] data, int start, int end)
        {
            if (end > start) _current.Bytes(data.AsSpan(start, end - start));
        }

        public ByteBuffer Text => _current;

        public int Hold(byte[] bytes)
        {
            Flush();
            _segments.Add(bytes);
            _length += bytes.Length;
            return _segments.Count - 1;
        }

        public void Replace(int segment, byte[] bytes)
        {
            _length += bytes.Length - _segments[segment].Length;
            _segments[segment] = bytes;
        }

        private void Flush()
        {
            if (_current.Length == 0) return;
            _segments.Add(_current.Span.ToArray());
            _length += _current.Length;
            _current = new ByteBuffer(4096);
        }

        public byte[] ToArray()
        {
            Flush();
            var result = new byte[_length];
            int at = 0;
            foreach (var s in _segments)
            {
                s.CopyTo(result, at);
                at += s.Length;
            }
            return result;
        }
    }

    /// <summary>A path being built: the bytes of its operators and the box of each subpath on the displayed page.</summary>
    private sealed class PathBuffer
    {
        public int Start = -1, End;
        public bool Clip;
        public readonly List<(int Start, int End, Box Box, bool Droppable)> Subpaths = new();
        private double _x0, _y0, _x1, _y1;

        public bool Pending => Start >= 0;

        public void Clear()
        {
            Start = -1;
            Clip = false;
            Subpaths.Clear();
        }

        public void BeginSubpath(int start, bool droppable)
        {
            Subpaths.Add((start, start, default, droppable));
            _x0 = _y0 = double.PositiveInfinity;
            _x1 = _y1 = double.NegativeInfinity;
        }

        public void Point(double x, double y)
        {
            _x0 = Math.Min(_x0, x);
            _y0 = Math.Min(_y0, y);
            _x1 = Math.Max(_x1, x);
            _y1 = Math.Max(_y1, y);
        }

        public void EndOperator(int end)
        {
            End = end;
            if (Subpaths.Count == 0) return;
            var s = Subpaths[^1];
            Subpaths[^1] = (s.Start, end, new Box(_x0, _y0, _x1, _y1), s.Droppable);
        }
    }

    /// <summary>
    /// Walks a content stream drawn with the transform <paramref name="ctm"/>, and returns it rewritten, or null when it
    /// draws nothing in the areas.
    /// </summary>
    private byte[]? Walk(byte[] data, Context context, Matrix ctm, int depth)
    {
        var output = new Output();
        var gs = new State { Ctm = ctm };
        var saved = new Stack<State>();
        var marks = new List<Mark>();
        var path = new PathBuffer();
        Matrix tm = Matrix.Identity, tlm = Matrix.Identity;
        bool changed = false;
        var p = new ContentParser(data);
        var full = Matrix.Identity;

        void Removed()
        {
            changed = true;
            foreach (var m in marks)
            {
                m.Removed = true;
                if (m.Mcid >= 0 && context.StructParents >= 0) _structHits.Add((context.StructParents, m.Mcid));
            }
            if (context.StructParents >= 0 && depth > 0) _structHits.Add((context.StructParents, -1));
        }

        while (p.Next())
        {
            PdfFile.CheckCancel();
            string op = p.Op;
            var o = p.Operands;
            if (path.Pending && !IsPathOperator(op))
            {
                // Operators a path does not allow between its parts: the path is kept as it was.
                output.Copy(data, path.Start, path.End);
                path.Clear();
            }
            switch (op)
            {
                case "q":
                    if (saved.Count < 4096) saved.Push(gs.Clone());
                    output.Copy(data, p.Start, p.End);
                    break;
                case "Q":
                    if (saved.Count > 0) gs = saved.Pop();
                    output.Copy(data, p.Start, p.End);
                    break;
                case "cm":
                    if (o.Count >= 6) gs.Ctm = new Matrix(N(o, 0), N(o, 1), N(o, 2), N(o, 3), N(o, 4), N(o, 5)).Multiply(gs.Ctm);
                    output.Copy(data, p.Start, p.End);
                    break;
                case "BT":
                    tm = tlm = Matrix.Identity;
                    output.Copy(data, p.Start, p.End);
                    break;
                case "Tf":
                    if (o.Count >= 2)
                    {
                        gs.FontName = o[0].Name;
                        gs.FontSize = N(o, 1);
                        gs.Font = LoadFont(context.Resources, gs.FontName);
                    }
                    output.Copy(data, p.Start, p.End);
                    break;
                case "Tc": gs.CharSpacing = N(o, 0); output.Copy(data, p.Start, p.End); break;
                case "Tw": gs.WordSpacing = N(o, 0); output.Copy(data, p.Start, p.End); break;
                case "Tz": gs.HScale = N(o, 0) / 100; output.Copy(data, p.Start, p.End); break;
                case "TL": gs.Leading = N(o, 0); output.Copy(data, p.Start, p.End); break;
                case "Ts": gs.Rise = N(o, 0); output.Copy(data, p.Start, p.End); break;
                case "Td":
                case "TD":
                    if (op == "TD") gs.Leading = -N(o, 1);
                    tlm = Matrix.Translation(N(o, 0), N(o, 1)).Multiply(tlm);
                    tm = tlm;
                    output.Copy(data, p.Start, p.End);
                    break;
                case "Tm":
                    if (o.Count >= 6) tm = tlm = new Matrix(N(o, 0), N(o, 1), N(o, 2), N(o, 3), N(o, 4), N(o, 5));
                    output.Copy(data, p.Start, p.End);
                    break;
                case "T*":
                    tlm = Matrix.Translation(0, -gs.Leading).Multiply(tlm);
                    tm = tlm;
                    output.Copy(data, p.Start, p.End);
                    break;
                case "Tj":
                case "'":
                case "\"":
                case "TJ":
                {
                    string prefix = "";
                    if (op == "\"" && o.Count >= 3)
                    {
                        gs.WordSpacing = N(o, 0);
                        gs.CharSpacing = N(o, 1);
                        prefix = F(gs.WordSpacing) + " Tw " + F(gs.CharSpacing) + " Tc T*\n";
                    }
                    if (op is "'" or "\"")
                    {
                        tlm = Matrix.Translation(0, -gs.Leading).Multiply(tlm);
                        tm = tlm;
                        if (prefix.Length == 0) prefix = "T*\n";
                    }
                    var items = new List<PdfObject>();
                    if (op == "TJ")
                    {
                        if (o.Count > 0 && o[^1].Array is { } array) items.AddRange(array.Items.Select(i => _file.Resolve(i) ?? PdfNull.Instance));
                    }
                    else if (o.Count > 0 && o[^1].Bytes is { } bytes) items.Add(new PdfString(bytes));
                    if (ShowText(items, gs, ref tm, output, prefix)) Removed();
                    else output.Copy(data, p.Start, p.End);
                    break;
                }
                case "Do":
                    if (DrawXObject(o.Count > 0 ? o[0].Name : null, gs.Ctm, context, output, depth)) Removed();
                    else output.Copy(data, p.Start, p.End);
                    break;
                case "BI":
                    if (p.InlineDict is { } inline && InlineImage(inline, p.InlineData ?? Array.Empty<byte>(), gs.Ctm, context, output)) Removed();
                    else output.Copy(data, p.Start, p.End);
                    break;
                case "m":
                case "re":
                case "l":
                case "c":
                case "v":
                case "y":
                case "h":
                    if (!path.Pending) path.Start = p.Start;
                    full = gs.Ctm.Multiply(_display);
                    if (op is "m" or "re") path.BeginSubpath(p.Start, droppable: true);
                    else if (path.Subpaths.Count == 0) path.BeginSubpath(p.Start, droppable: false);
                    if (op == "re" && o.Count >= 4)
                    {
                        double x = N(o, 0), y = N(o, 1), w = N(o, 2), h = N(o, 3);
                        AddPoint(path, full, x, y);
                        AddPoint(path, full, x + w, y);
                        AddPoint(path, full, x + w, y + h);
                        AddPoint(path, full, x, y + h);
                    }
                    else
                    {
                        for (int i = 0; i + 1 < o.Count; i += 2) AddPoint(path, full, N(o, i), N(o, i + 1));
                    }
                    path.EndOperator(p.End);
                    break;
                case "W":
                case "W*":
                    if (!path.Pending) path.Start = p.Start;
                    path.Clip = true;
                    path.End = p.End;
                    break;
                case "S":
                case "s":
                case "f":
                case "F":
                case "f*":
                case "B":
                case "B*":
                case "b":
                case "b*":
                case "n":
                    if (path.Pending && PaintPath(path, data, op, output)) Removed();
                    else output.Copy(data, path.Pending ? path.Start : p.Start, p.End);
                    path.Clear();
                    break;
                case "BMC":
                case "BDC":
                {
                    var mark = new Mark { Tag = o.Count > 0 ? o[0].Name ?? "Span" : "Span" };
                    var properties = op == "BDC" && o.Count >= 2
                        ? o[1].Dict ?? (o[1].Name is { } pn ? _file.GetDict(_file.GetDict(context.Resources, "Properties"), pn) : null)
                        : null;
                    if (properties != null)
                    {
                        mark.Mcid = _file.GetInt(properties, "MCID", -1);
                        if (properties["ActualText"] != null || properties["Alt"] != null || properties["E"] != null)
                        {
                            mark.Properties = properties;
                            mark.Named = o[1].Dict == null ? o[1].Name : null;
                            mark.Segment = output.Hold(data.AsSpan(p.Start, p.End - p.Start).ToArray());
                            if (marks.Count < 4096) marks.Add(mark);
                            break;
                        }
                    }
                    if (marks.Count < 4096) marks.Add(mark);
                    output.Copy(data, p.Start, p.End);
                    break;
                }
                case "EMC":
                    if (marks.Count > 0)
                    {
                        CloseMark(marks[^1], output, context);
                        marks.RemoveAt(marks.Count - 1);
                    }
                    output.Copy(data, p.Start, p.End);
                    break;
                default:
                    output.Copy(data, p.Start, p.End);
                    break;
            }
        }
        if (path.Pending) output.Copy(data, path.Start, path.End);
        foreach (var m in marks) CloseMark(m, output, context);
        return changed ? output.ToArray() : null;
    }

    private static bool IsPathOperator(string op) => op is "m" or "re" or "l" or "c" or "v" or "y" or "h" or "W" or "W*"
        or "S" or "s" or "f" or "F" or "f*" or "B" or "B*" or "b" or "b*" or "n";

    private static double N(List<Operand> o, int i) => i < o.Count && o[i].Kind == OperandKind.Number ? o[i].Num : 0;

    private static void AddPoint(PathBuffer path, in Matrix full, double x, double y)
    {
        var (dx, dy) = full.Apply(x, y);
        path.Point(dx, dy);
    }

    /// <summary>
    /// Marked content that held removed text has its properties written again without their texts. Properties named in
    /// the resources get a new name for the copy, so that marked content elsewhere that lost nothing keeps its own, and
    /// the original goes from the file once nothing uses it.
    /// </summary>
    private void CloseMark(Mark mark, Output output, Context context)
    {
        if (!mark.Removed || mark.Segment < 0 || mark.Properties == null) return;
        var properties = PdfDict.CopyOf(mark.Properties);
        properties.Entries.Remove("ActualText");
        properties.Entries.Remove("Alt");
        properties.Entries.Remove("E");
        var b = new ByteBuffer(256);
        b.Nl();
        WriteName(b, mark.Tag);
        b.Sp();
        if (mark.Named != null)
        {
            WriteName(b, context.AddProperties(this, properties));
            _replaced.Add(mark.Properties);
        }
        else WriteValue(b, properties, 0);
        b.Ascii(" BDC\n");
        output.Replace(mark.Segment, b.Span.ToArray());
    }

    private PdfFont? LoadFont(PdfDict? resources, string? name)
    {
        if (name == null || _file.GetDict(_file.GetDict(resources, "Font"), name) is not { } dict) return null;
        if (_fonts.TryGetValue(dict, out var font)) return font;
        try
        {
            font = PdfFont.Load(_file, dict);
        }
        catch (Exception ex) when (ex is PdfFormatException or InvalidDataException or NotSupportedException or IndexOutOfRangeException or ArgumentException)
        {
            SwallowedErrors.Record(ex);
            font = null;
        }
        _fonts[dict] = font;
        return font;
    }

    // ------------------------------------------------------------------ text

    /// <summary>
    /// Shows text as the renderer does, moving <paramref name="tm"/>. When glyphs fall in an area, writes the operator
    /// again as a TJ without them, the space they took kept as adjustments, and returns true; otherwise writes nothing.
    /// </summary>
    private bool ShowText(List<PdfObject> items, State gs, ref Matrix tm, Output output, string prefix)
    {
        var font = gs.Font;
        if (font == null) return ShowUnmeasured(items, gs, tm, output, prefix);
        double fs = gs.FontSize, th = gs.HScale;
        bool vertical = font.Vertical;
        double top = 0.9 * font.TextSizeScale, bottom = -0.25 * font.TextSizeScale;
        var parts = new List<(byte[]? Bytes, double Adjust)>();
        var kept = new List<byte>();
        bool any = false;
        double total = 0; // the advance of the whole operator, in text space units at size 1 times the font size

        void Keep()
        {
            if (kept.Count == 0) return;
            parts.Add((kept.ToArray(), 0));
            kept.Clear();
        }

        void Adjust(double value)
        {
            Keep();
            if (parts.Count > 0 && parts[^1].Bytes == null) parts[^1] = (null, parts[^1].Adjust + value);
            else parts.Add((null, value));
        }

        foreach (var item in items)
        {
            if (item is PdfNumber num)
            {
                double adj = -num.Value / 1000 * fs;
                tm = vertical ? Matrix.Translation(0, adj).Multiply(tm) : Matrix.Translation(adj * th, 0).Multiply(tm);
                total += adj;
                Adjust(num.Value);
                continue;
            }
            if (item is not PdfString s) continue;
            var span = s.Bytes.AsSpan();
            int pos = 0;
            while (pos < span.Length)
            {
                int start = pos;
                int code = font.NextCode(span, ref pos, out bool isSpace);
                if (pos <= start) pos = start + 1;
                double w0 = font.Width(code);
                var trm = new Matrix(fs * th, 0, 0, fs, 0, gs.Rise).Multiply(tm).Multiply(gs.Ctm);
                double advance;
                if (vertical)
                {
                    var (w1, vx, vy) = font.VerticalMetrics(code);
                    trm = Matrix.Translation(-vx * fs * th, -vy * fs).Multiply(new Matrix(fs * th, 0, 0, fs, 0, gs.Rise)).Multiply(tm).Multiply(gs.Ctm);
                    advance = w1 * fs + gs.CharSpacing + (isSpace ? gs.WordSpacing : 0);
                }
                else advance = w0 * fs + gs.CharSpacing + (isSpace ? gs.WordSpacing : 0);

                if (Covered(BoxOf(0, bottom, Math.Max(0, w0), top, trm), GlyphCover))
                {
                    any = true;
                    if (fs != 0) Adjust(-advance * 1000 / fs);
                }
                else kept.AddRange(span.Slice(start, pos - start).ToArray());

                tm = vertical ? Matrix.Translation(0, advance).Multiply(tm) : Matrix.Translation(advance * th, 0).Multiply(tm);
                total += advance;
            }
        }
        if (!any) return false;
        Keep();

        var b = output.Text;
        b.Nl().Ascii(prefix);
        if (fs == 0)
        {
            // Glyphs of size 0 cannot be replaced by adjustments, which scale with the size; they draw nothing, so the
            // operator becomes a move by its whole advance at size 1.
            if (gs.FontName == null) return true;
            WriteName(b, gs.FontName);
            b.Ascii(" 1 Tf [").Ascii(F(-total * 1000)).Ascii("] TJ ");
            WriteName(b, gs.FontName);
            b.Ascii(" 0 Tf\n");
            return true;
        }
        b.Byte((byte)'[');
        foreach (var (bytes, value) in parts)
        {
            if (bytes != null) WriteHex(b, bytes);
            else b.Ascii(F(value));
            b.Sp();
        }
        b.Ascii("] TJ\n");
        return true;
    }

    /// <summary>
    /// Shows text whose font cannot be read. Nothing here knows where its glyphs are, but viewers draw it with a
    /// substitute font and tools extract it, so each code is taken to be a full em wide, which few substitute glyphs
    /// exceed, and the whole operator goes when that reach touches an area, as which of its glyphs lie there cannot be
    /// told. It is not replaced by a move, as the renderer and text extraction do not move for it either.
    /// </summary>
    private bool ShowUnmeasured(List<PdfObject> items, State gs, Matrix tm, Output output, string prefix)
    {
        double fs = gs.FontSize, x = 0, x0 = double.PositiveInfinity, x1 = double.NegativeInfinity;
        foreach (var item in items)
        {
            if (item is PdfNumber num) x -= num.Value / 1000 * fs;
            else if (item is PdfString s)
                foreach (byte c in s.Bytes)
                {
                    x0 = Math.Min(x0, Math.Min(x, x + fs));
                    x1 = Math.Max(x1, Math.Max(x, x + fs));
                    x += fs + gs.CharSpacing + (c == (byte)' ' ? gs.WordSpacing : 0);
                }
        }
        if (x0 > x1) return false;
        var trm = new Matrix(gs.HScale, 0, 0, 1, 0, gs.Rise).Multiply(tm).Multiply(gs.Ctm);
        if (!Overlaps(BoxOf(x0, Math.Min(fs, -0.3 * fs), x1, Math.Max(fs, -0.3 * fs), trm))) return false;
        output.Text.Nl().Ascii(prefix);
        return true;
    }

    // ------------------------------------------------------------------ paths

    /// <summary>
    /// Paints a path without its subpaths that lie wholly inside an area; returns false, writing nothing, when it keeps
    /// them all. Clipping paths are kept whole, as removing part of one would show what it hides.
    /// </summary>
    private bool PaintPath(PathBuffer path, byte[] data, string op, Output output)
    {
        if (path.Clip || path.Subpaths.Count == 0) return false;
        var drop = path.Subpaths.Select(s => s.Droppable && Inside(s.Box)).ToList();
        if (!drop.Contains(true)) return false;
        var b = output.Text;
        if (drop.Contains(false))
        {
            b.Nl();
            for (int i = 0; i < drop.Count; i++)
                if (!drop[i]) b.Bytes(data.AsSpan(path.Subpaths[i].Start, path.Subpaths[i].End - path.Subpaths[i].Start)).Nl();
            b.Ascii(op).Nl();
        }
        return true;
    }

    // ------------------------------------------------------------------ images and forms

    /// <summary>
    /// Draws an XObject: an image or form that touches an area is dropped or replaced by a redacted copy, and true is
    /// returned; otherwise nothing is written.
    /// </summary>
    private bool DrawXObject(string? name, Matrix ctm, Context context, Output output, int depth)
    {
        if (name == null || _file.GetDict(_file.GetDict(context.Resources, "XObject"), name) is not PdfStream xObject) return false;
        switch (_file.GetName(xObject, "Subtype"))
        {
            case "Image":
            {
                var box = BoxOf(0, 0, 1, 1, ctm);
                if (!Overlaps(box)) return false;
                if (!Inside(box) && RedactImage(xObject, null, context.Resources, ctm) is { } copy) WriteDo(output, context.AddXObject(this, copy));
                _replaced.Add(xObject);
                return true;
            }
            case "Form":
            {
                var m = Matrix.FromArray(_file.GetNumbers(xObject, "Matrix")).Multiply(ctm);
                byte[]? content;
                if (_file.GetNumbers(xObject, "BBox") is { Length: >= 4 } bbox && !Overlaps(BoxOf(bbox[0], bbox[1], bbox[2], bbox[3], m)))
                {
                    // What the form draws outside its box is clipped away, but text there still extracts, so the form is
                    // walked too, on a budget of its own; past that it is kept, as nothing of it shows in an area.
                    if (depth >= MaxDepth || ++_hiddenWalks > MaxFormWalks || Decode(xObject) is not { } hidden) return false;
                    content = hidden;
                }
                else if (depth >= MaxDepth || ++_formWalks > MaxFormWalks || Decode(xObject) is not { } shown)
                {
                    _replaced.Add(xObject);
                    return true;
                }
                else content = shown;
                var own = _file.GetDict(xObject, "Resources");
                var inner = new Context(PdfDict.CopyOf(own ?? context.Resources), _file.GetInt(xObject, "StructParents", -1));
                if (Walk(content, inner, m, depth + 1) is not { } redacted) return false;
                var entries = new Dictionary<string, PdfObject>(xObject.Entries, StringComparer.Ordinal);
                entries.Remove("Length");
                entries.Remove("DecodeParms");
                // The producing application's private data can hold its own copy of what the form draws.
                entries.Remove("PieceInfo");
                entries["Filter"] = PdfName.Get("FlateDecode");
                entries["Resources"] = inner.Resources;
                var form = new PdfStream(entries, PdfImage.Deflate(redacted, System.IO.Compression.CompressionLevel.Optimal));
                WriteDo(output, context.AddXObject(this, _file.AddObject(form)));
                _replaced.Add(xObject);
                return true;
            }
            default:
                return false;
        }
    }

    /// <summary>An inline image that touches an area is dropped, or replaced by a redacted image XObject.</summary>
    private bool InlineImage(PdfDict dict, byte[] data, Matrix ctm, Context context, Output output)
    {
        var box = BoxOf(0, 0, 1, 1, ctm);
        if (!Overlaps(box)) return false;
        if (!Inside(box) && RedactImage(dict, data, context.Resources, ctm) is { } copy) WriteDo(output, context.AddXObject(this, copy));
        return true;
    }

    private static void WriteDo(Output output, string name)
    {
        var b = output.Text;
        b.Nl();
        WriteName(b, name);
        b.Ascii(" Do\n");
    }

    /// <summary>
    /// A copy of an image with its pixels under the areas painted in the fill colour (or made transparent without one),
    /// as an image XObject; null when the image cannot be decoded, which leaves it out.
    /// </summary>
    private PdfRef? RedactImage(PdfDict dict, byte[]? inlineData, PdfDict? resources, Matrix ctm)
    {
        DecodedImage? image;
        try
        {
            image = PdfImageDecoder.Decode(_file, dict, inlineData ?? Array.Empty<byte>(), alreadyDecoded: false, resources, inline: inlineData != null);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or PdfFormatException or IndexOutOfRangeException or ArgumentException
            or OverflowException)
        {
            SwallowedErrors.Record(ex);
            return null;
        }
        if (image == null || !ctm.Multiply(_display).TryInvert(out var toImage)) return null;
        int w = image.Width, h = image.Height;
        var covered = new bool[(long)w * h];
        foreach (var a in _areas)
        {
            // The area's corners in image space, where the image fills the unit square with its first row at the top.
            var (ux0, uy0) = toImage.Apply(a.X0, a.Y0);
            var (ux1, uy1) = toImage.Apply(a.X1, a.Y0);
            var (ux2, uy2) = toImage.Apply(a.X1, a.Y1);
            var (ux3, uy3) = toImage.Apply(a.X0, a.Y1);
            int x0 = Clamp(Math.Floor(Min(ux0, ux1, ux2, ux3) * w), w), x1 = Clamp(Math.Ceiling(Max(ux0, ux1, ux2, ux3) * w), w);
            int y0 = Clamp(Math.Floor((1 - Max(uy0, uy1, uy2, uy3)) * h), h), y1 = Clamp(Math.Ceiling((1 - Min(uy0, uy1, uy2, uy3)) * h), h);
            for (int y = y0; y < y1; y++)
                Array.Fill(covered, true, y * w + x0, x1 - x0);
        }

        var entries = new Dictionary<string, PdfObject>(StringComparer.Ordinal)
        {
            ["Type"] = PdfName.Get("XObject"),
            ["Subtype"] = PdfName.Get("Image"),
            ["Width"] = PdfNumber.Int(w),
            ["Height"] = PdfNumber.Int(h),
            ["Filter"] = PdfName.Get("FlateDecode"),
        };
        if (image.Interpolate) entries["Interpolate"] = PdfBool.True;
        if (inlineData == null && dict["OC"] is { } oc) entries["OC"] = oc;

        if (image.Stencil is { } stencil)
        {
            // A stencil mask paints where its samples are 0.
            int stride = (w + 7) / 8;
            var bits = new byte[(long)stride * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (covered[y * w + x] || stencil[y * w + x] < 128) bits[y * stride + (x >> 3)] |= (byte)(0x80 >> (x & 7));
            entries["ImageMask"] = PdfBool.True;
            entries["BitsPerComponent"] = PdfNumber.Int(1);
            return _file.AddObject(new PdfStream(entries, PdfImage.Deflate(bits, System.IO.Compression.CompressionLevel.Optimal)));
        }
        if (image.Pixels is not { } pixels) return null;

        uint fill = _fill ?? 0;
        var rgb = new byte[(long)w * h * 3];
        var alpha = new byte[(long)w * h];
        bool translucent = false;
        for (int i = 0; i < pixels.Length; i++)
        {
            uint r, g, b, a;
            if (covered[i])
            {
                // Pixels are 0xAABBGGRR; the fill colour is 0xAARRGGBB.
                r = (fill >> 16) & 255;
                g = (fill >> 8) & 255;
                b = fill & 255;
                a = _fill != null ? 255u : 0u;
            }
            else
            {
                uint px = pixels[i];
                a = px >> 24;
                r = Straight(px & 255, a);
                g = Straight((px >> 8) & 255, a);
                b = Straight((px >> 16) & 255, a);
            }
            rgb[i * 3] = (byte)r;
            rgb[i * 3 + 1] = (byte)g;
            rgb[i * 3 + 2] = (byte)b;
            alpha[i] = (byte)a;
            translucent |= a != 255;
        }
        entries["ColorSpace"] = PdfName.Get("DeviceRGB");
        entries["BitsPerComponent"] = PdfNumber.Int(8);
        if (translucent)
        {
            var mask = new Dictionary<string, PdfObject>(StringComparer.Ordinal)
            {
                ["Type"] = PdfName.Get("XObject"),
                ["Subtype"] = PdfName.Get("Image"),
                ["Width"] = PdfNumber.Int(w),
                ["Height"] = PdfNumber.Int(h),
                ["ColorSpace"] = PdfName.Get("DeviceGray"),
                ["BitsPerComponent"] = PdfNumber.Int(8),
                ["Filter"] = PdfName.Get("FlateDecode"),
            };
            entries["SMask"] = _file.AddObject(new PdfStream(mask, PdfImage.Deflate(alpha, System.IO.Compression.CompressionLevel.Optimal)));
        }
        return _file.AddObject(new PdfStream(entries, PdfImage.Deflate(rgb, System.IO.Compression.CompressionLevel.Optimal)));
    }

    private static int Clamp(double v, int max) => double.IsNaN(v) ? 0 : (int)Math.Clamp(v, 0, max);

    private static uint Straight(uint channel, uint alpha) => alpha == 0 ? 0 : Math.Min(255, (channel * 255 + alpha / 2) / alpha);
}
