using PdfPecker.Rendering.Fonts;
using PdfPecker.Rendering.Graphics;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering;

internal sealed partial class PageRenderer
{
    private const int MaxCachedGlyphPixels = 256; // glyphs larger than this (in device pixels) are drawn as paths
    private const int SubpixelSteps = 4;

    private Matrix _tm = Matrix.Identity, _tlm = Matrix.Identity;
    private PathData? _textClip; // device-space glyph outlines collected by clipping text render modes
    private readonly FlatPath _glyphFlat = new(), _rasterFlat = new();
    private readonly BitmapSink _bitmapSink = new();
    private readonly PathData _glyphPath = new();
    private readonly Dictionary<GlyphKey, GlyphBitmap> _localGlyphs = new();

    private void NextLine()
    {
        _textMoved = true;
        _tlm = Matrix.Translation(0, -_gs.Leading).Multiply(_tlm);
        _tm = _tlm;
    }

    private void ShowTextArray(PdfArray arr, PdfDict? res)
    {
        var font = _gs.Font;
        foreach (var item in arr.Items)
        {
            var o = _file.Resolve(item);
            if (o is PdfString s) ShowText(s.Bytes, res);
            else if (o is PdfNumber num)
            {
                double adj = -num.Value / 1000 * _gs.FontSize;
                if (font != null && font.Vertical) _tm = Matrix.Translation(0, adj).Multiply(_tm);
                else _tm = Matrix.Translation(adj * _gs.HScale, 0).Multiply(_tm);
            }
        }
    }

    private void ShowText(byte[] bytes, PdfDict? res)
    {
        var font = _gs.Font;
        if (font == null) return;
        var gs = _gs;
        int mode = gs.TextRender;
        bool fill = mode is 0 or 2 or 4 or 6, stroke = mode is 1 or 2 or 5 or 6, clip = mode >= 4 && _text == null;
        if (clip) _textClip ??= new PathData();
        double fs = gs.FontSize, th = gs.HScale;
        bool canFill = fill && _hidden == 0 && SetupPaint(false);
        bool vertical = font.Vertical;
        int pos = 0;
        var span = bytes.AsSpan();
        while (pos < span.Length)
        {
            int code = font.NextCode(span, ref pos, out bool isSpace);
            double w0 = font.Width(code);
            // Text rendering matrix without translation of the glyph origin: [fs*Th 0 0 fs 0 rise] × Tm × CTM.
            var trm = new Matrix(fs * th, 0, 0, fs, 0, gs.Rise).Multiply(_tm).Multiply(gs.Ctm);
            double vx = 0, vy = 0, w1 = 0;
            if (vertical)
            {
                (w1, vx, vy) = font.VerticalMetrics(code);
                trm = Matrix.Translation(-vx * fs * th, -vy * fs).Multiply(new Matrix(fs * th, 0, 0, fs, 0, gs.Rise)).Multiply(_tm).Multiply(gs.Ctm);
            }

            if (_text != null)
            {
                if (_hidden == 0) CollectGlyph(font, code, w0, w1, vertical);
            }
            else if (font.Kind == FontKind.Type3)
            {
                if (_hidden == 0) DrawType3Glyph(font, code, trm, res);
            }
            else if (font.Source != null && (fill || stroke || clip))
            {
                int gid = font.GlyphId(code);
                var gm = font.GlyphMatrix;
                float hs = font.HScale(code);
                if (hs != 1f) gm = gm.Multiply(Matrix.Scale(hs, 1));
                var full = gm.Multiply(trm);
                if (canFill && !stroke && !clip) DrawGlyphCached(font.Source, gid, full);
                else
                {
                    var glyph = font.Source.GetGlyph(gid);
                    if (glyph != null && !glyph.IsEmpty)
                    {
                        if (canFill) DrawGlyphCached(font.Source, gid, full);
                        if (stroke && _hidden == 0 && SetupPaint(true))
                        {
                            // Stroke in user space so the line width is transformed by the CTM only.
                            _glyphPath.Clear();
                            var glyphToUser = gm.Multiply(new Matrix(fs * th, 0, 0, fs, 0, gs.Rise)).Multiply(_tm);
                            if (vertical) glyphToUser = gm.Multiply(Matrix.Translation(-vx * fs * th, -vy * fs)).Multiply(new Matrix(fs * th, 0, 0, fs, 0, gs.Rise)).Multiply(_tm);
                            glyph.AppendTransformed(_glyphPath, glyphToUser);
                            StrokePathData(_glyphPath, gs.Ctm);
                            if (canFill) SetupPaint(false);
                        }
                        if (clip) glyph.AppendTransformed(_textClip!, full);
                    }
                }
            }

            // Advance.
            if (vertical)
            {
                double ty = w1 * fs + gs.CharSpacing + (isSpace ? gs.WordSpacing : 0);
                _tm = Matrix.Translation(0, ty).Multiply(_tm);
            }
            else
            {
                double tx = (w0 * fs + gs.CharSpacing + (isSpace ? gs.WordSpacing : 0)) * th;
                _tm = Matrix.Translation(tx, 0).Multiply(_tm);
            }
        }
    }

    /// <summary>
    /// Records a glyph for text extraction. Invisible text (render mode 3) is kept: it is how scanned pages carry their
    /// recognised text.
    /// </summary>
    private void CollectGlyph(PdfFont font, int code, double w0, double w1, bool vertical)
    {
        var gs = _gs;
        double fs = gs.FontSize, th = gs.HScale;
        var trm = new Matrix(fs * th, 0, 0, fs, 0, gs.Rise).Multiply(_tm).Multiply(gs.Ctm);
        var (x, y) = trm.Apply(0, 0);
        // Character spacing belongs to the glyph so letter-spaced text does not read as separate words; word spacing
        // does not, so a widened space still reads as a word gap.
        double advance = Math.Abs(fs) > 1e-9 ? Math.Max(0, w0 + gs.CharSpacing / fs) : w0;
        var (ex, ey) = vertical ? trm.Apply(0, w1) : trm.Apply(advance, 0);
        double xLen = Math.Sqrt(trm.A * trm.A + trm.B * trm.B), yLen = Math.Sqrt(trm.C * trm.C + trm.D * trm.D);
        // Vertical text runs down the text space's y axis, so its lines follow that axis.
        double dx = vertical ? -trm.C : trm.A, dy = vertical ? -trm.D : trm.B, size = yLen * font.TextSizeScale;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (!(len > 1e-9) || !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(ex) || !double.IsFinite(ey)) return;
        string? text;
        if (_actualText != null)
        {
            if (_actualTextShown)
            {
                _text!.ExtendLast(ex, ey);
                return;
            }
            _actualTextShown = true;
            text = _actualText;
        }
        else text = font.Unicode(code);
        if (string.IsNullOrEmpty(text)) return;
        bool moved = _textMoved;
        _textMoved = false;
        _text!.Add(new TextGlyph
        {
            Text = text, Moved = moved, X = x, Y = y, EndX = ex, EndY = ey, DirX = dx / len, DirY = dy / len, Size = size,
            SpaceWidth = vertical ? 0 : font.SpaceWidth * xLen, Font = font.BaseFont,
        });
    }

    private void ApplyTextClip()
    {
        var clipPath = _textClip!;
        if (clipPath.IsEmpty)
        {
            _gs.Clip = new ClipRegion(0, 0, 0, 0);
            return;
        }
        _flat.Flatten(clipPath, Matrix.Identity, Tolerance);
        _gs.Clip = _gs.Clip.IntersectPath(_flat, false, _raster);
    }

    /// <summary>Fills a glyph with the current painter setup, through the glyph bitmap cache when it is small enough.</summary>
    private void DrawGlyphCached(IGlyphSource source, int gid, in Matrix m)
    {
        var clip = _gs.Clip;
        double ex = m.E, ey = m.F;
        if (double.IsNaN(ex) || double.IsNaN(ey) || ex < clip.X0 - 4096 || ex > clip.X1 + 4096 || ey < clip.Y0 - 4096 || ey > clip.Y1 + 4096) return;
        if (EmPixels(source, m) > MaxCachedGlyphPixels)
        {
            var g = source.GetGlyph(gid);
            if (g == null || g.IsEmpty) return;
            _glyphFlat.Flatten(g, m, Tolerance);
            FillFlat(_glyphFlat, false);
            return;
        }
        int ix = (int)Math.Floor(ex), iy = (int)Math.Floor(ey);
        byte fx = (byte)Math.Min(SubpixelSteps - 1, (int)((ex - ix) * SubpixelSteps));
        byte fy = (byte)Math.Min(SubpixelSteps - 1, (int)((ey - iy) * SubpixelSteps));
        var key = new GlyphKey(source, gid, Q(m.A), Q(m.B), Q(m.C), Q(m.D), fx, fy);
        // A per-page dictionary in front of the shared cache avoids its locks for repeated glyphs.
        if (!_localGlyphs.TryGetValue(key, out var bmp))
        {
            if (!_cache.TryGetGlyph(key, out bmp))
            {
                bmp = RasterizeGlyph(source, gid, new Matrix(m.A, m.B, m.C, m.D, (double)fx / SubpixelSteps, (double)fy / SubpixelSteps));
                _cache.AddGlyph(key, bmp);
            }
            if (_localGlyphs.Count < 20_000) _localGlyphs[key] = bmp;
        }
        if (bmp.Width == 0) return;
        int bx = ix + bmp.X0, by = iy + bmp.Y0;
        int y0 = Math.Max(by, clip.Y0), y1 = Math.Min(by + bmp.Height, clip.Y1);
        int x0 = Math.Max(bx, clip.X0), x1 = Math.Min(bx + bmp.Width, clip.X1);
        if (x1 <= x0 || y1 <= y0) return;
        var cov = bmp.Coverage;
        for (int y = y0; y < y1; y++)
        {
            var row = cov.AsSpan((y - by) * bmp.Width + (x0 - bx), x1 - x0);
            _painter.Row(y, x0, row);
        }
    }

    private static double EmPixels(IGlyphSource source, in Matrix m)
    {
        var fm = source.FontMatrix;
        double unitsPerEm = Math.Abs(fm.A) > 1e-12 ? 1 / Math.Abs(fm.A) : 1000;
        // m maps glyph units to device: one em is unitsPerEm glyph units.
        return Math.Max(Math.Abs(m.A) + Math.Abs(m.C), Math.Abs(m.B) + Math.Abs(m.D)) * unitsPerEm;
    }

    private static int Q(double v) => (int)Math.Round(v * (1 << 24));

    private GlyphBitmap RasterizeGlyph(IGlyphSource source, int gid, in Matrix m)
    {
        var bmp = new GlyphBitmap();
        var g = source.GetGlyph(gid);
        if (g == null || g.IsEmpty) return bmp;
        var flat = _rasterFlat;
        flat.Flatten(g, m, Tolerance);
        if (flat.Count < 2) return bmp;
        int x0 = (int)MathF.Floor(flat.MinX), y0 = (int)MathF.Floor(flat.MinY);
        int x1 = (int)MathF.Ceiling(flat.MaxX) + 1, y1 = (int)MathF.Ceiling(flat.MaxY) + 1;
        if (x1 - x0 > 4 * MaxCachedGlyphPixels || y1 - y0 > 4 * MaxCachedGlyphPixels) return bmp;
        if (!_raster.Begin(x0, y0, x1, y1)) return bmp;
        _raster.AddPath(flat);
        bmp.X0 = x0;
        bmp.Y0 = y0;
        bmp.Width = x1 - x0;
        bmp.Height = y1 - y0;
        bmp.Coverage = new byte[bmp.Width * bmp.Height];
        _bitmapSink.Target = bmp;
        _raster.Render(false, _bitmapSink);
        _bitmapSink.Target = null!;
        return bmp;
    }

    private void DrawType3Glyph(PdfFont font, int code, in Matrix textToDevice, PdfDict? res)
    {
        var procs = font.Type3Procs;
        if (procs == null || _depth >= MaxDepth) return;
        var proc = procs[code & 255];
        if (proc == null) return;
        var data = SafeDecode(proc);
        if (data.Length == 0) return;
        var saved = _gs;
        var savedPath = _path.IsEmpty ? null : _path.Clone();
        var savedTm = _tm;
        var savedTlm = _tlm;
        var savedClipText = _textClip;
        bool savedIgnore = _ignoreColor;
        _gs = _gs.Clone();
        _gs.Ctm = font.FontMatrix.Multiply(textToDevice);
        _gs.TextRender = 0;
        _path.Clear();
        _textClip = null;
        try
        {
            RunContent(data, font.Type3Resources ?? res);
        }
        finally
        {
            _gs = saved;
            _tm = savedTm;
            _tlm = savedTlm;
            _textClip = savedClipText;
            _ignoreColor = savedIgnore;
            _path.Clear();
            savedPath?.AppendTransformed(_path, Matrix.Identity);
            // Restore the paint setup for the next glyph of the string.
            SetupPaint(false);
        }
    }

    /// <summary>Writes rasterizer coverage into a glyph bitmap.</summary>
    private sealed class BitmapSink : ICoverageSink
    {
        public GlyphBitmap Target = null!;

        public void Row(int y, int x0, ReadOnlySpan<byte> cov)
        {
            var t = Target;
            int ry = y - t.Y0, rx = x0 - t.X0;
            if ((uint)ry >= (uint)t.Height) return;
            int start = Math.Max(0, rx), end = Math.Min(t.Width, rx + cov.Length);
            if (end <= start) return;
            cov.Slice(start - rx, end - start).CopyTo(t.Coverage.AsSpan(ry * t.Width + start));
        }

        public void Span(int y, int x0, int x1, byte cov)
        {
            var t = Target;
            int ry = y - t.Y0;
            if ((uint)ry >= (uint)t.Height) return;
            int start = Math.Max(0, x0 - t.X0), end = Math.Min(t.Width, x1 - t.X0);
            if (end > start) t.Coverage.AsSpan(ry * t.Width + start, end - start).Fill(cov);
        }
    }
}
