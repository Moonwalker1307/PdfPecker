using PdfPecker.Rendering.Content;
using PdfPecker.Rendering.Graphics;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering;

/// <summary>
/// Interprets PDF content streams and paints them onto a <see cref="Canvas"/>: paths, text, images, shadings,
/// patterns, form XObjects, transparency groups, soft masks, optional content and annotation appearances.
/// </summary>
internal sealed partial class PageRenderer
{
    private const int MaxDepth = 24;
    /// <summary>
    /// Operators one page may run, counting every repetition inside form XObjects, patterns and glyphs. Nested forms
    /// multiply: a few hundred bytes that each draw the next form ten times would otherwise run for days.
    /// </summary>
    internal const long MaxOperations = 20_000_000;
    private const int FormCost = 16; // running a form XObject costs about as much as this many simple operators
    private const float Tolerance = 0.2f; // curve flattening tolerance in device pixels

    private readonly PdfFile _file;
    private readonly RenderCache _cache;
    private readonly CancellationToken _cancel;
    private Canvas _canvas;
    private readonly Rasterizer _raster = new();
    private readonly Painter _painter = new();
    private readonly Stroker _stroker = new();
    private readonly FlatPath _flat = new(), _strokePoly = new();
    private GraphicsState _gs;
    private readonly Stack<GraphicsState> _stack = new();
    private readonly PathData _path = new();
    private int _pendingClip; // 0 none, 1 non-zero, 2 even-odd
    private int _depth;
    private Matrix _patternBase; // the CTM that pattern matrices are relative to
    private bool _ignoreColor; // inside an uncolored Type 3 glyph (d1) or uncolored tiling pattern
    private int _hidden; // nesting level of hidden optional content
    private readonly Stack<bool> _marked = new();
    private OptionalContent? _oc;
    private long _ops;
    // Text extraction: when set, glyphs go to the collector and nothing is painted, clipped or decoded for pixels.
    private readonly TextCollector? _text;
    private string? _actualText; // the /ActualText of the marked content being run, which replaces its glyphs
    private bool _actualTextShown;
    private readonly Stack<(string? Text, bool Shown)> _actualStack = new();
    private bool _textMoved; // the text position was set, not advanced, since the last glyph

    public PageRenderer(PdfFile file, RenderCache cache, Canvas canvas, CancellationToken cancel)
    {
        _file = file;
        _cache = cache;
        _canvas = canvas;
        _cancel = cancel;
        _gs = new GraphicsState { Clip = new ClipRegion(0, 0, canvas.Width, canvas.Height) };
    }

    /// <summary>A renderer that runs pages for their text: the glyphs go to <paramref name="text"/>.</summary>
    public PageRenderer(PdfFile file, RenderCache cache, TextCollector text, CancellationToken cancel)
        : this(file, cache, new Canvas(1, 1, 0), cancel)
    {
        _text = text;
    }

    /// <summary>Renders a page's contents and (optionally) its annotation appearances with the given base transform.</summary>
    public void RenderPage(PdfDict page, PdfDict? resources, in Matrix baseCtm, bool annotations)
    {
        _oc = OptionalContent.Create(_file);
        _gs.Ctm = baseCtm;
        _patternBase = baseCtm;
        var contents = _file.Resolve(page["Contents"]);
        var data = ConcatContents(contents);
        try
        {
            if (data.Length > 0) RunContent(data, resources);
            if (annotations) RenderAnnotations(page, baseCtm);
        }
        catch (BudgetExceeded)
        {
            throw new InvalidDataException($"The page is too complex to render: it runs more than {MaxOperations:N0} drawing operations.");
        }
    }

    /// <summary>Thrown past <see cref="MaxOperations"/>; not caught by the per-operator error handling.</summary>
    private sealed class BudgetExceeded : Exception;

    private byte[] ConcatContents(PdfObject? contents)
    {
        if (contents is PdfStream s) return SafeDecode(s);
        if (contents is not PdfArray arr) return Array.Empty<byte>();
        var ms = new MemoryStream();
        foreach (var item in arr.Items)
        {
            if (_file.Resolve(item) is not PdfStream cs) continue;
            var d = SafeDecode(cs);
            ms.Write(d);
            ms.WriteByte((byte)'\n'); // content streams may split between tokens
        }
        return ms.ToArray();
    }

    private byte[] SafeDecode(PdfStream s)
    {
        try { return _file.DecodeStream(s); }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or PdfFormatException or IndexOutOfRangeException) { return Array.Empty<byte>(); }
    }

    // ------------------------------------------------------------------ content streams

    private void RunContent(byte[] data, PdfDict? resources)
    {
        if (_depth >= MaxDepth) return;
        _depth++;
        int stackBase = _stack.Count;
        int markedBase = _marked.Count;
        int hiddenBase = _hidden;
        int actualBase = _actualStack.Count;
        var parser = new ContentParser(data);
        try
        {
            while (parser.Next())
            {
                if ((++_ops & 1023) == 0)
                {
                    _cancel.ThrowIfCancellationRequested();
                    if (_ops > MaxOperations) throw new BudgetExceeded();
                }
                try
                {
                    Execute(parser, resources, stackBase);
                }
                catch (Exception ex) when (ex is InvalidCastException or IndexOutOfRangeException or ArgumentException
                                               or NullReferenceException or NotSupportedException or InvalidDataException
                                               or PdfFormatException or OverflowException or KeyNotFoundException)
                {
                    // A bad operator does not stop the page.
                }
            }
        }
        finally
        {
            while (_stack.Count > stackBase) _gs = _stack.Pop();
            while (_marked.Count > markedBase) _marked.Pop();
            _hidden = hiddenBase;
            while (_actualStack.Count > actualBase) (_actualText, _actualTextShown) = _actualStack.Pop();
            _depth--;
        }
    }

    private double N(ContentParser p, int i) => i < p.Operands.Count && p.Operands[i].Kind == OperandKind.Number ? p.Operands[i].Num : 0;

    private void Execute(ContentParser p, PdfDict? res, int stackBase)
    {
        var ops = p.Operands;
        int n = ops.Count;
        switch (p.Op)
        {
            // ---------------------------------------------------------- graphics state
            case "q":
                if (_stack.Count < 512) _stack.Push(_gs.Clone());
                break;
            case "Q":
                if (_stack.Count > stackBase) _gs = _stack.Pop();
                break;
            case "cm":
                if (n >= 6) _gs.Ctm = new Matrix(N(p, 0), N(p, 1), N(p, 2), N(p, 3), N(p, 4), N(p, 5)).Multiply(_gs.Ctm);
                break;
            case "w": if (n >= 1) _gs.LineWidth = (float)N(p, 0); break;
            case "J": if (n >= 1) _gs.Cap = (LineCap)Math.Clamp((int)N(p, 0), 0, 2); break;
            case "j": if (n >= 1) _gs.Join = (LineJoin)Math.Clamp((int)N(p, 0), 0, 2); break;
            case "M": if (n >= 1) _gs.MiterLimit = (float)Math.Max(1, N(p, 0)); break;
            case "d":
                if (n >= 2 && ops[0].Array is PdfArray da)
                {
                    _gs.Dash = da.Count == 0 ? null : da.Items.Select(x => (float)_file.Num(x)).ToArray();
                    _gs.DashPhase = (float)N(p, 1);
                }
                break;
            case "gs":
                if (n >= 1 && ops[0].Name is string gsName) ApplyExtGState(gsName, res);
                break;
            case "ri": case "i": break;

            // ---------------------------------------------------------- paths
            case "m": if (n >= 2) _path.Move(N(p, 0), N(p, 1)); break;
            case "l": if (n >= 2) _path.Line(N(p, 0), N(p, 1)); break;
            case "c": if (n >= 6) _path.Cubic(N(p, 0), N(p, 1), N(p, 2), N(p, 3), N(p, 4), N(p, 5)); break;
            case "v":
                if (n >= 4) _path.Cubic(_path.CurrentX, _path.CurrentY, N(p, 0), N(p, 1), N(p, 2), N(p, 3));
                break;
            case "y": if (n >= 4) _path.Cubic(N(p, 0), N(p, 1), N(p, 2), N(p, 3), N(p, 2), N(p, 3)); break;
            case "h": _path.ClosePath(); break;
            case "re": if (n >= 4) _path.Rect(N(p, 0), N(p, 1), N(p, 2), N(p, 3)); break;
            case "S": Stroke(); EndPath(); break;
            case "s": _path.ClosePath(); Stroke(); EndPath(); break;
            case "f": case "F": Fill(false); EndPath(); break;
            case "f*": Fill(true); EndPath(); break;
            case "B": Fill(false); Stroke(); EndPath(); break;
            case "B*": Fill(true); Stroke(); EndPath(); break;
            case "b": _path.ClosePath(); Fill(false); Stroke(); EndPath(); break;
            case "b*": _path.ClosePath(); Fill(true); Stroke(); EndPath(); break;
            case "n": EndPath(); break;
            case "W": _pendingClip = 1; break;
            case "W*": _pendingClip = 2; break;

            // ---------------------------------------------------------- color
            case "g": SetColor(false, ColorSpace.Gray, p); break;
            case "G": SetColor(true, ColorSpace.Gray, p); break;
            case "rg": SetColor(false, ColorSpace.RgbSpace, p); break;
            case "RG": SetColor(true, ColorSpace.RgbSpace, p); break;
            case "k": SetColor(false, ColorSpace.Cmyk, p); break;
            case "K": SetColor(true, ColorSpace.Cmyk, p); break;
            case "cs": case "CS":
            {
                if (_ignoreColor || n < 1) break;
                bool stroke = p.Op == "CS";
                var cs = ops[0].Name is string csName ? ColorSpace.Parse(_file, PdfName.Get(csName), res) : null;
                cs ??= ColorSpace.Gray;
                var init = cs.InitialColor();
                if (stroke) { _gs.StrokeSpace = cs; _gs.StrokeComps = init; _gs.StrokePattern = null; _gs.StrokeRgb = cs is PatternSpace ? 0 : cs.ToRgb(init); }
                else { _gs.FillSpace = cs; _gs.FillComps = init; _gs.FillPattern = null; _gs.FillRgb = cs is PatternSpace ? 0 : cs.ToRgb(init); }
                break;
            }
            case "sc": case "scn": SetColorInSpace(false, p, res); break;
            case "SC": case "SCN": SetColorInSpace(true, p, res); break;

            // ---------------------------------------------------------- text
            case "BT":
                _tm = _tlm = Matrix.Identity;
                _textClip = null;
                _textMoved = true;
                break;
            case "ET":
                if (_textClip != null) { ApplyTextClip(); _textClip = null; }
                break;
            case "Tc": if (n >= 1) _gs.CharSpacing = N(p, 0); break;
            case "Tw": if (n >= 1) _gs.WordSpacing = N(p, 0); break;
            case "Tz": if (n >= 1) _gs.HScale = N(p, 0) / 100; break;
            case "TL": if (n >= 1) _gs.Leading = N(p, 0); break;
            case "Ts": if (n >= 1) _gs.Rise = N(p, 0); break;
            case "Tr": if (n >= 1) _gs.TextRender = Math.Clamp((int)N(p, 0), 0, 7); break;
            case "Tf":
                if (n >= 2)
                {
                    _gs.FontSize = N(p, 1);
                    if (ops[0].Name is string fontName) _gs.Font = LoadFont(fontName, res);
                }
                break;
            case "Td":
                if (n >= 2) { _tlm = Matrix.Translation(N(p, 0), N(p, 1)).Multiply(_tlm); _tm = _tlm; _textMoved = true; }
                break;
            case "TD":
                if (n >= 2)
                {
                    _gs.Leading = -N(p, 1);
                    _tlm = Matrix.Translation(N(p, 0), N(p, 1)).Multiply(_tlm);
                    _tm = _tlm;
                    _textMoved = true;
                }
                break;
            case "Tm":
                if (n >= 6) { _tlm = _tm = new Matrix(N(p, 0), N(p, 1), N(p, 2), N(p, 3), N(p, 4), N(p, 5)); _textMoved = true; }
                break;
            case "T*":
                NextLine();
                break;
            case "Tj":
                if (n >= 1 && ops[n - 1].Bytes is byte[] tj) ShowText(tj, res);
                break;
            case "'":
                NextLine();
                if (n >= 1 && ops[n - 1].Bytes is byte[] t1) ShowText(t1, res);
                break;
            case "\"":
                if (n >= 3)
                {
                    _gs.WordSpacing = N(p, 0);
                    _gs.CharSpacing = N(p, 1);
                    NextLine();
                    if (ops[2].Bytes is byte[] t2) ShowText(t2, res);
                }
                break;
            case "TJ":
                if (n >= 1 && ops[n - 1].Array is PdfArray tja) ShowTextArray(tja, res);
                break;
            case "d0":
                break;
            case "d1":
                _ignoreColor = true;
                break;

            // ---------------------------------------------------------- XObjects, images, shadings
            case "Do":
                if (n >= 1 && ops[0].Name is string xname) DoXObject(xname, res);
                break;
            case "BI":
                if (p.InlineDict != null && p.InlineData != null && _hidden == 0 && _text == null) DrawInlineImage(p.InlineDict, p.InlineData, res);
                break;
            case "sh":
                if (n >= 1 && ops[0].Name is string shName && _hidden == 0 && _text == null) PaintShadingOperator(shName, res);
                break;

            // ---------------------------------------------------------- marked content
            case "BMC":
                _marked.Push(false);
                if (_text != null) _actualStack.Push((_actualText, _actualTextShown));
                break;
            case "BDC":
            {
                bool hide = false;
                if (n >= 2 && ops[0].Name == "OC" && _oc != null)
                {
                    PdfObject? props = ops[1].Kind == OperandKind.Name
                        ? _file.GetDict(res, "Properties")?[ops[1].Name!] : ops[1].Dict;
                    hide = !_oc.IsVisible(_file.Resolve(props));
                }
                _marked.Push(hide);
                if (hide) _hidden++;
                if (_text != null) BeginActualText(p, res);
                break;
            }
            case "EMC":
                if (_marked.Count > 0 && _marked.Pop()) _hidden--;
                if (_text != null && _actualStack.Count > 0) (_actualText, _actualTextShown) = _actualStack.Pop();
                break;
        }
    }

    /// <summary>
    /// Starts marked content for text extraction: a span with /ActualText stands for its glyphs, unless it is inside
    /// another such span, whose text then covers both.
    /// </summary>
    private void BeginActualText(ContentParser p, PdfDict? res)
    {
        _actualStack.Push((_actualText, _actualTextShown));
        var ops = p.Operands;
        if (_actualText != null || ops.Count < 2) return;
        var props = ops[1].Kind == OperandKind.Name ? _file.Resolve(_file.GetDict(res, "Properties")?[ops[1].Name!]) as PdfDict : ops[1].Dict;
        if (_file.Resolve(props?["ActualText"]) is PdfString actual)
        {
            _actualText = actual.Text;
            _actualTextShown = false;
        }
    }

    private void EndPath()
    {
        if (_pendingClip != 0)
        {
            ApplyClip(_pendingClip == 2);
            _pendingClip = 0;
        }
        _path.Clear();
    }

    // ------------------------------------------------------------------ colors

    private void SetColor(bool stroke, ColorSpace cs, ContentParser p)
    {
        if (_ignoreColor) return;
        int n = cs.Components;
        if (p.Operands.Count < n) return;
        var comps = new float[n];
        for (int i = 0; i < n; i++) comps[i] = (float)N(p, i);
        uint rgb = cs.ToRgb(comps);
        if (stroke) { _gs.StrokeSpace = cs; _gs.StrokeComps = comps; _gs.StrokeRgb = rgb; _gs.StrokePattern = null; }
        else { _gs.FillSpace = cs; _gs.FillComps = comps; _gs.FillRgb = rgb; _gs.FillPattern = null; }
    }

    private void SetColorInSpace(bool stroke, ContentParser p, PdfDict? res)
    {
        if (_ignoreColor) return;
        var cs = stroke ? _gs.StrokeSpace : _gs.FillSpace;
        var ops = p.Operands;
        if (cs is PatternSpace ps)
        {
            PdfObject? pattern = null;
            if (ops.Count > 0 && ops[^1].Name is string patName) pattern = _file.GetDict(res, "Pattern")?[patName];
            pattern = _file.Resolve(pattern);
            int nComps = Math.Max(0, ops.Count - 1);
            var comps = new float[nComps];
            for (int i = 0; i < nComps; i++) comps[i] = (float)N(p, i);
            uint rgb = ps.Under != null && nComps >= ps.Under.Components ? ps.Under.ToRgb(comps) : 0;
            if (stroke) { _gs.StrokePattern = pattern; _gs.StrokeComps = comps; _gs.StrokeRgb = rgb; }
            else { _gs.FillPattern = pattern; _gs.FillComps = comps; _gs.FillRgb = rgb; }
            return;
        }
        int n = Math.Min(ops.Count, Math.Max(1, cs.Components));
        var c = new float[Math.Max(1, cs.Components)];
        for (int i = 0; i < n; i++) c[i] = (float)N(p, i);
        uint color = cs.ToRgb(c);
        if (stroke) { _gs.StrokeComps = c; _gs.StrokeRgb = color; _gs.StrokePattern = null; }
        else { _gs.FillComps = c; _gs.FillRgb = color; _gs.FillPattern = null; }
    }

    // ------------------------------------------------------------------ painting

    /// <summary>Sets up the painter for a fill or stroke. Returns false if nothing should be painted.</summary>
    private bool SetupPaint(bool stroke)
    {
        var gs = _gs;
        if (_text != null || _hidden > 0 || gs.Clip.IsEmpty) return false;
        float alpha = stroke ? gs.StrokeAlpha : gs.FillAlpha;
        if (alpha <= 0) return false;
        var pattern = stroke ? gs.StrokePattern : gs.FillPattern;
        var space = stroke ? gs.StrokeSpace : gs.FillSpace;
        IShader? shader = null;
        if (space is PatternSpace)
        {
            // Build the pattern first: rendering a tile reuses the painter.
            if (pattern == null) return false;
            shader = CreatePatternShader(pattern, stroke);
            if (shader == null) return false;
        }
        else if (space is SeparationSpace { IsNone: true }) return false;
        var pt = _painter;
        pt.Canvas = _canvas;
        pt.Clip = gs.Clip;
        pt.Alpha = (int)(Math.Clamp(alpha, 0, 1) * 255 + 0.5f);
        pt.Blend = gs.Blend;
        pt.SoftMask = gs.SoftMask;
        pt.Shader = shader;
        if (shader != null) return true;
        uint rgb = stroke ? gs.StrokeRgb : gs.FillRgb;
        pt.Color = rgb | 0xFF000000;
        return true;
    }

    private void Fill(bool evenOdd)
    {
        if (_path.IsEmpty || !SetupPaint(false)) return;
        var ctm = _gs.Ctm;
        if (_path.IsSingleRect && ctm.IsAxisAligned)
        {
            var (x0, y0) = ctm.Apply(_path.RectX, _path.RectY);
            var (x1, y1) = ctm.Apply(_path.RectX + _path.RectW, _path.RectY + _path.RectH);
            _painter.FillRect((float)x0, (float)y0, (float)x1, (float)y1);
            return;
        }
        _flat.Flatten(_path, ctm, Tolerance);
        FillFlat(_flat, evenOdd);
    }

    private void FillFlat(FlatPath dev, bool evenOdd)
    {
        if (dev.Count < 2) return;
        var clip = _gs.Clip;
        int x0 = Math.Max(clip.X0, (int)MathF.Floor(dev.MinX)), y0 = Math.Max(clip.Y0, (int)MathF.Floor(dev.MinY));
        int x1 = Math.Min(clip.X1, (int)MathF.Ceiling(dev.MaxX) + 1), y1 = Math.Min(clip.Y1, (int)MathF.Ceiling(dev.MaxY) + 1);
        if (!_raster.Begin(x0, y0, x1, y1)) return;
        _raster.AddPath(dev);
        _raster.Render(evenOdd, _painter);
    }

    private void Stroke()
    {
        if (_path.IsEmpty || !SetupPaint(true)) return;
        StrokePathData(_path, _gs.Ctm);
    }

    /// <summary>Strokes a user-space path with the current line style.</summary>
    private void StrokePathData(PathData path, in Matrix ctm)
    {
        var gs = _gs;
        var (maxScale, minScale) = ctm.ScaleRange();
        if (maxScale <= 0) return;
        double width = gs.LineWidth;
        // Lines thinner than one device pixel are drawn one pixel wide.
        double minWidth = 1.0 / Math.Max(minScale, 1e-9);
        if (minScale <= 1e-9) minWidth = 1.0 / maxScale;
        if (width * minScale < 1) width = Math.Max(width, Math.Min(minWidth, 1.0 / maxScale * 1.5));
        float tol = (float)(Tolerance / maxScale);
        // Stroke in user space, then transform the outline: correct for any (even non-uniform) transform.
        _flat.Flatten(path, Matrix.Identity, tol);
        _stroker.Stroke(_flat, _strokePoly, (float)width, gs.Cap, gs.Join, gs.MiterLimit, gs.Dash, gs.DashPhase, tol);
        _strokePoly.Transform(ctm);
        FillFlat(_strokePoly, false);
    }

    private void ApplyClip(bool evenOdd)
    {
        if (_text != null) return; // text is extracted whether or not a clip hides it
        var ctm = _gs.Ctm;
        if (_path.IsEmpty)
        {
            _gs.Clip = new ClipRegion(0, 0, 0, 0);
            return;
        }
        if (_path.IsSingleRect && ctm.IsAxisAligned)
        {
            var (x0, y0) = ctm.Apply(_path.RectX, _path.RectY);
            var (x1, y1) = ctm.Apply(_path.RectX + _path.RectW, _path.RectY + _path.RectH);
            if (x0 > x1) (x0, x1) = (x1, x0);
            if (y0 > y1) (y0, y1) = (y1, y0);
            _gs.Clip = _gs.Clip.IntersectRect((int)Math.Round(x0), (int)Math.Round(y0), (int)Math.Round(x1), (int)Math.Round(y1));
            return;
        }
        _flat.Flatten(_path, ctm, Tolerance);
        _gs.Clip = _gs.Clip.IntersectPath(_flat, evenOdd, _raster);
    }

    // ------------------------------------------------------------------ extended graphics state

    private void ApplyExtGState(string name, PdfDict? res)
    {
        var dict = _file.GetDict(_file.GetDict(res, "ExtGState"), name);
        if (dict == null) return;
        foreach (var (key, value) in dict.Entries)
        {
            var v = _file.Resolve(value);
            switch (key)
            {
                case "LW": _gs.LineWidth = (float)_file.Num(v, _gs.LineWidth); break;
                case "LC": _gs.Cap = (LineCap)Math.Clamp(_file.GetIntValue(v), 0, 2); break;
                case "LJ": _gs.Join = (LineJoin)Math.Clamp(_file.GetIntValue(v), 0, 2); break;
                case "ML": _gs.MiterLimit = (float)Math.Max(1, _file.Num(v, 10)); break;
                case "D":
                    if (v is PdfArray d && d.Count >= 2 && _file.Resolve(d[0]) is PdfArray dash)
                    {
                        _gs.Dash = dash.Count == 0 ? null : dash.Items.Select(x => (float)_file.Num(x)).ToArray();
                        _gs.DashPhase = (float)_file.Num(d[1]);
                    }
                    break;
                case "CA": _gs.StrokeAlpha = (float)Math.Clamp(_file.Num(v, 1), 0, 1); break;
                case "ca": _gs.FillAlpha = (float)Math.Clamp(_file.Num(v, 1), 0, 1); break;
                case "BM":
                {
                    var bm = v is PdfArray ba && ba.Count > 0 ? _file.Resolve(ba[0]) as PdfName : v as PdfName;
                    _gs.Blend = bm?.Value switch
                    {
                        "Multiply" => BlendMode.Multiply, "Screen" => BlendMode.Screen, "Overlay" => BlendMode.Overlay,
                        "Darken" => BlendMode.Darken, "Lighten" => BlendMode.Lighten, "ColorDodge" => BlendMode.ColorDodge,
                        "ColorBurn" => BlendMode.ColorBurn, "HardLight" => BlendMode.HardLight, "SoftLight" => BlendMode.SoftLight,
                        "Difference" => BlendMode.Difference, "Exclusion" => BlendMode.Exclusion, "Hue" => BlendMode.Hue,
                        "Saturation" => BlendMode.Saturation, "Color" => BlendMode.Color, "Luminosity" => BlendMode.Luminosity,
                        _ => BlendMode.Normal,
                    };
                    break;
                }
                case "SMask":
                    if (v is PdfName { Value: "None" }) _gs.SoftMask = null;
                    else if (v is PdfDict sm) _gs.SoftMask = BuildSoftMask(sm);
                    break;
                case "Font":
                    if (v is PdfArray fa && fa.Count >= 2 && _file.Resolve(fa[0]) is PdfDict fd)
                    {
                        _gs.Font = _cache.GetFont(_file, fd);
                        _gs.FontSize = _file.Num(fa[1], 1);
                    }
                    break;
            }
        }
    }

    // ------------------------------------------------------------------ XObjects

    private void DoXObject(string name, PdfDict? res)
    {
        var xobj = _file.GetStream(_file.GetDict(res, "XObject"), name);
        if (xobj == null) return;
        if (_oc != null && xobj["OC"] != null && !_oc.IsVisible(_file.Resolve(xobj["OC"]))) return;
        var subtype = _file.GetName(xobj, "Subtype");
        if (subtype == "Image")
        {
            if (_hidden > 0 || _text != null) return;
            DrawImageXObject(xobj, res);
        }
        else if (subtype == "Form" || xobj["BBox"] != null)
        {
            RunForm(xobj, res, null);
        }
    }

    /// <summary>Runs a form XObject: its matrix, bounding-box clip, resources, and transparency group if needed.</summary>
    private void RunForm(PdfStream form, PdfDict? parentRes, Matrix? extra)
    {
        if (_depth >= MaxDepth) return;
        if ((_ops += FormCost) > MaxOperations) throw new BudgetExceeded();
        var data = SafeDecode(form);
        if (data.Length == 0) return;
        var res = _file.GetDict(form, "Resources") ?? parentRes;
        var saved = _gs;
        _gs = _gs.Clone();
        var savedPath = _path.IsEmpty ? null : _path.Clone();
        _path.Clear();
        try
        {
            var m = Matrix.FromArray(_file.GetNumbers(form, "Matrix"));
            if (extra is Matrix em) m = m.Multiply(em);
            _gs.Ctm = m.Multiply(_gs.Ctm);
            var savedBase = _patternBase;
            if (_file.GetNumbers(form, "BBox") is { Length: >= 4 } bbox)
            {
                _path.Rect(Math.Min(bbox[0], bbox[2]), Math.Min(bbox[1], bbox[3]), Math.Abs(bbox[2] - bbox[0]), Math.Abs(bbox[3] - bbox[1]));
                ApplyClip(false);
                _path.Clear();
            }
            var group = _file.GetDict(form, "Group");
            bool isolatedGroup = group != null && _file.GetName(group, "S") == "Transparency";
            // An isolated group (/I true) is drawn on its own transparent canvas even when it is opaque, so that blend
            // modes inside it blend only with each other and not with the page below.
            if (isolatedGroup && (_gs.FillAlpha < 1 || _gs.SoftMask != null || _gs.Blend != BlendMode.Normal || _file.GetBool(group, "I", false))
                && _hidden == 0 && _text == null)
            {
                RenderGroup(data, res);
            }
            else
            {
                // Patterns inside a form are relative to the form's coordinate system.
                _patternBase = _gs.Ctm;
                RunContent(data, res);
            }
            _patternBase = savedBase;
        }
        finally
        {
            _gs = saved;
            _path.Clear();
            if (savedPath != null) savedPath.AppendTransformed(_path, Matrix.Identity);
        }
    }

    /// <summary>Renders a transparency group offscreen, then composites it with the group's alpha, blend mode and soft mask.</summary>
    private void RenderGroup(byte[] data, PdfDict? res)
    {
        var clip = _gs.Clip;
        if (clip.IsEmpty) return;
        float alpha = _gs.FillAlpha;
        var blend = _gs.Blend;
        var softMask = _gs.SoftMask;
        var layer = new Canvas(_canvas.Width, _canvas.Height, 0);
        var mainCanvas = _canvas;
        var savedBase = _patternBase;
        _canvas = layer;
        _gs.FillAlpha = _gs.StrokeAlpha = 1;
        _gs.Blend = BlendMode.Normal;
        _gs.SoftMask = null;
        _patternBase = _gs.Ctm;
        try
        {
            RunContent(data, res);
        }
        finally
        {
            _canvas = mainCanvas;
            _patternBase = savedBase;
        }
        var pt = _painter;
        pt.Canvas = _canvas;
        pt.Clip = clip;
        pt.Alpha = (int)(alpha * 255 + 0.5f);
        pt.Blend = blend;
        pt.SoftMask = softMask;
        pt.Shader = new CanvasShader(layer);
        pt.FillRect(clip.X0, clip.Y0, clip.X1, clip.Y1);
        pt.Shader = null;
    }

    /// <summary>Renders an ExtGState soft mask group to a per-pixel mask over the whole canvas.</summary>
    private byte[]? BuildSoftMask(PdfDict sm)
    {
        if (_text != null) return null;
        var g = _file.GetStream(sm, "G");
        if (g == null) return null;
        bool luminosity = _file.GetName(sm, "S") != "Alpha";
        uint backdrop = 0;
        if (luminosity)
        {
            // Backdrop color in the group's color space (default black).
            var groupCs = ColorSpace.Parse(_file, _file.GetDict(g, "Group")?["CS"], null) ?? ColorSpace.RgbSpace;
            var bc = _file.GetNumbers(sm, "BC");
            if (bc != null && bc.Length >= groupCs.Components)
                backdrop = groupCs.ToRgb(bc.Select(x => (float)x).ToArray());
            backdrop |= 0xFF000000;
        }
        var layer = new Canvas(_canvas.Width, _canvas.Height, backdrop);
        var mainCanvas = _canvas;
        var saved = _gs;
        var savedBase = _patternBase;
        _gs = _gs.Clone();
        _gs.Clip = new ClipRegion(0, 0, _canvas.Width, _canvas.Height);
        _gs.FillAlpha = _gs.StrokeAlpha = 1;
        _gs.Blend = BlendMode.Normal;
        _gs.SoftMask = null;
        _canvas = layer;
        try
        {
            RunForm(g, null, null);
        }
        finally
        {
            _canvas = mainCanvas;
            _gs = saved;
            _patternBase = savedBase;
        }
        var tr = _file.Resolve(sm["TR"]);
        PdfFunction? fn = tr is PdfName ? null : PdfFunction.Create(_file, tr);
        byte[]? lut = null;
        if (fn != null)
        {
            lut = new byte[256];
            Span<double> i1 = stackalloc double[1];
            Span<double> o1 = stackalloc double[1];
            for (int i = 0; i < 256; i++)
            {
                i1[0] = i / 255.0;
                fn.Eval(i1, o1);
                lut[i] = (byte)(Math.Clamp(o1[0], 0, 1) * 255 + 0.5);
            }
        }
        var px = layer.Pixels;
        var mask = new byte[px.Length];
        for (int i = 0; i < px.Length; i++)
        {
            uint c = px[i];
            int v;
            if (luminosity)
            {
                // The layer is opaque (backdrop), so premultiplied equals straight color.
                v = ((int)(c & 255) * 77 + (int)((c >> 8) & 255) * 151 + (int)((c >> 16) & 255) * 28) >> 8;
            }
            else v = (int)(c >> 24);
            mask[i] = lut != null ? lut[v] : (byte)v;
        }
        return mask;
    }

    // ------------------------------------------------------------------ annotations

    private void RenderAnnotations(PdfDict page, in Matrix baseCtm)
    {
        if (_file.GetArray(page, "Annots") is not PdfArray annots) return;
        foreach (var item in annots.Items)
        {
            if (_file.Resolve(item) is not PdfDict annot) continue;
            var subtype = _file.GetName(annot, "Subtype");
            if (subtype == "Popup") continue;
            int flags = _file.GetInt(annot, "F", 0);
            if ((flags & 2) != 0 || (flags & 32) != 0) continue; // hidden, no view
            if (_oc != null && annot["OC"] != null && !_oc.IsVisible(_file.Resolve(annot["OC"]))) continue;
            var ap = _file.GetDict(annot, "AP");
            var normal = _file.Resolve(ap?["N"]);
            PdfStream? form = normal as PdfStream;
            if (form == null && normal is PdfDict states)
            {
                var state = _file.GetName(annot, "AS");
                if (state != null) form = _file.Resolve(states[state]) as PdfStream;
            }
            if (form == null) continue;
            var rect = _file.GetNumbers(annot, "Rect");
            if (rect == null || rect.Length < 4) continue;
            double rx0 = Math.Min(rect[0], rect[2]), ry0 = Math.Min(rect[1], rect[3]);
            double rx1 = Math.Max(rect[0], rect[2]), ry1 = Math.Max(rect[1], rect[3]);
            // An appearance mapped onto an empty rectangle covers no pixels.
            if (!(rx1 - rx0 > 0 && ry1 - ry0 > 0)) continue;
            // A damaged /BBox with fewer than four numbers is treated as missing, as for other form XObjects.
            var bbox = _file.GetNumbers(form, "BBox") is { Length: >= 4 } box ? box : new double[] { 0, 0, rx1 - rx0, ry1 - ry0 };
            var fm = Matrix.FromArray(_file.GetNumbers(form, "Matrix"));
            // Transform the bounding box by the form matrix, then map that box onto the annotation rectangle.
            double bx0 = double.MaxValue, by0 = double.MaxValue, bx1 = double.MinValue, by1 = double.MinValue;
            foreach (var (x, y) in new[] { (bbox[0], bbox[1]), (bbox[2], bbox[1]), (bbox[0], bbox[3]), (bbox[2], bbox[3]) })
            {
                var (tx, ty) = fm.Apply(x, y);
                bx0 = Math.Min(bx0, tx); by0 = Math.Min(by0, ty); bx1 = Math.Max(bx1, tx); by1 = Math.Max(by1, ty);
            }
            double sx = bx1 - bx0 > 1e-9 ? (rx1 - rx0) / (bx1 - bx0) : 1, sy = by1 - by0 > 1e-9 ? (ry1 - ry0) / (by1 - by0) : 1;
            var a = new Matrix(sx, 0, 0, sy, rx0 - bx0 * sx, ry0 - by0 * sy);
            _gs = new GraphicsState { Clip = new ClipRegion(0, 0, _canvas.Width, _canvas.Height), Ctm = baseCtm };
            _patternBase = baseCtm;
            try
            {
                RunForm(form, null, a);
            }
            catch (Exception ex) when (ex is InvalidCastException or IndexOutOfRangeException or ArgumentException or NullReferenceException or PdfFormatException)
            {
            }
        }
    }

    private Fonts.PdfFont? LoadFont(string name, PdfDict? res)
    {
        var dict = _file.GetDict(_file.GetDict(res, "Font"), name);
        return dict == null ? null : _cache.GetFont(_file, dict);
    }
}

/// <summary>Visibility of optional content groups under the document's default configuration.</summary>
internal sealed class OptionalContent
{
    private readonly HashSet<PdfDict> _off = new(ReferenceEqualityComparer.Instance);
    private readonly PdfFile _file;
    private readonly bool _baseOff;
    private readonly HashSet<PdfDict> _on = new(ReferenceEqualityComparer.Instance);

    private OptionalContent(PdfFile file, bool baseOff)
    {
        _file = file;
        _baseOff = baseOff;
    }

    public static OptionalContent? Create(PdfFile file)
    {
        var props = file.GetDict(file.Catalog, "OCProperties");
        var d = file.GetDict(props, "D");
        if (d == null) return null;
        var oc = new OptionalContent(file, file.GetName(d, "BaseState") == "OFF");
        if (file.GetArray(d, "OFF") is PdfArray off)
            foreach (var o in off.Items) if (file.Resolve(o) is PdfDict g) oc._off.Add(g);
        if (file.GetArray(d, "ON") is PdfArray on)
            foreach (var o in on.Items) if (file.Resolve(o) is PdfDict g) oc._on.Add(g);
        return oc;
    }

    private bool GroupOn(PdfDict g) => _baseOff ? _on.Contains(g) : !_off.Contains(g);

    public bool IsVisible(PdfObject? obj)
    {
        if (obj is not PdfDict d) return true;
        var type = _file.GetName(d, "Type");
        if (type == "OCMD")
        {
            var ocgs = _file.Resolve(d["OCGs"]);
            var groups = new List<PdfDict>();
            if (ocgs is PdfDict single) groups.Add(single);
            else if (ocgs is PdfArray arr) foreach (var o in arr.Items) if (_file.Resolve(o) is PdfDict g) groups.Add(g);
            if (groups.Count == 0) return true;
            return _file.GetName(d, "P") switch
            {
                "AllOn" => groups.All(GroupOn),
                "AnyOff" => groups.Any(g => !GroupOn(g)),
                "AllOff" => groups.All(g => !GroupOn(g)),
                _ => groups.Any(GroupOn),
            };
        }
        return GroupOn(d);
    }
}

/// <summary>Reads pixels from another canvas (for compositing transparency groups).</summary>
internal sealed class CanvasShader : IShader
{
    private readonly Canvas _src;
    public CanvasShader(Canvas src) => _src = src;

    public void Shade(int y, int x0, Span<uint> colors)
        => _src.Pixels.AsSpan(y * _src.Width + x0, colors.Length).CopyTo(colors);
}
