using PdfPecker.Css;
using PdfPecker.Fonts;
using PdfPecker.Html;

namespace PdfPecker.Layout;

/// <summary>
/// initial-letter: a ::first-letter that sinks into the lines of its paragraph (CSS Inline 3 §7), laid out as Chrome
/// does. The style gives the letter its font size; here its box is sized to the letter's ink, dropped so that its
/// baseline sits on the baseline of the line its size names, and placed at the start of the first line like a float,
/// which the lines beside it flow round.
/// </summary>
internal sealed partial class LayoutEngine
{
    /// <summary>Where an initial letter goes, from the top of its paragraph's first line.</summary>
    private sealed class InitialLetterPlace
    {
        public float Top;   // the top of its margin box
        public float Shift; // how far a raised letter moves the text of the first line down
    }

    private static bool IsInitialLetter(Element e)
        => e.Generated && e.Style is { InitialLetterSize: > 0, Floated: true } && e.Name == "::first-letter";

    /// <summary>
    /// Lays out an initial letter: its box holds the ink of its text, with the ink's top left at the content box's top
    /// left, as Chrome sizes it. A letter its author floats is then placed as an ordinary float. Null when the text has no
    /// ink, and the letter is an ordinary float.
    /// </summary>
    private PendingFloat? MeasureInitialLetter(Element e, float cbW)
    {
        var s = e.Style;
        var first = CaptureBox(e, cbW, shrinkToFit: true);
        if (!InkBox(first.Ops, out float inkL, out float inkT, out float inkR, out float inkB, out _)) return null;
        float left = s.PaddingLeft.Resolve(cbW) + s.BorderLeft, right = s.PaddingRight.Resolve(cbW) + s.BorderRight;
        float top = s.PaddingTop.Resolve(cbW) + s.BorderTop, bottom = s.PaddingBottom.Resolve(cbW) + s.BorderBottom;
        float ml = s.MarginLeft.IsAuto ? 0 : s.MarginLeft.Resolve(cbW), mt = s.MarginTop.IsAuto ? 0 : s.MarginTop.Resolve(cbW);
        float mb = s.MarginBottom.IsAuto ? 0 : s.MarginBottom.Resolve(cbW);
        // Chrome sizes the box to the ink whatever its width and height say.
        var c = CaptureBox(e, cbW, shrinkToFit: true, inkR - inkL + left + right, inkB - inkT + top + bottom);
        if (!InkBox(c.Ops, out inkL, out inkT, out _, out _, out float baseline)) return null;
        ShiftText(c.Ops, ml + left - inkL, mt + top - inkT);
        if (!s.InitialLetterSunk) return new PendingFloat { Element = e, CbW = cbW, Box = c };
        // The box's baseline, from its border box top.
        float ascent = baseline + top - inkT;

        var paragraph = StyleResolver.ParagraphStyle(e.Parent!, null, firstLine: true);
        float lineH = paragraph.LineHeight;
        TextMetrics(paragraph, out _, out float descent);
        int size = (int)MathF.Ceiling(s.InitialLetterSize);
        int sink = s.InitialLetterSink > 0 ? s.InitialLetterSink : size;
        var place = new InitialLetterPlace();
        if (size < sink)
        {
            // Sunk deeper than it is tall: the box stands on the bottom of its last line, as Chrome 141 places it.
            place.Top = lineH * sink - (c.Height - mt - mb);
        }
        else
        {
            // Its baseline on the baseline of the line its size names, counted from the text of the first line, which
            // a raised letter moves down.
            place.Shift = lineH * (size - sink);
            place.Top = lineH * s.InitialLetterSize - ascent - descent;
        }
        return new PendingFloat { Element = e, CbW = cbW, Box = c, Letter = place };
    }

    /// <summary>
    /// Places an initial letter at the start of the first line, whose top is <paramref name="y"/>, after the line's
    /// <paramref name="indent"/>. The lines beside it flow round its margin box, from the first line's top down.
    /// </summary>
    private void PlaceInitialLetter(PendingFloat f, float y, float cbX, float cbW, float indent)
    {
        var place = f.Letter!;
        var c = f.Box;
        bool right = f.Element.Style.Float == FloatSide.Right;
        var (l, r) = _floats.Band(y, Eps, cbX, cbX + cbW);
        // Chrome moves the letter by the indent twice: the line it starts already begins after the indent.
        float x = right ? r - 2 * indent - c.Width : l + 2 * indent;
        int start = _ops.Count;
        Place(c, x, y + place.Top);
        WrapLayer(start, LayerOp.FloatOrder, stackingContext: false);
        _floats.Add(new FloatBox
        {
            X0 = x, X1 = x + c.Width, Y0 = y + Math.Min(place.Top, 0), Y1 = y + place.Top + c.Height, Right = right, OpsIndex = start,
            InitialLetter = true,
        });
    }

    /// <summary>
    /// The box round the ink of the text in <paramref name="ops"/>, and the baseline of the first text; false when the
    /// text has no ink.
    /// </summary>
    private bool InkBox(List<DrawOp> ops, out float left, out float top, out float right, out float bottom, out float baseline)
    {
        var ink = new InkBounds { Left = float.MaxValue, Top = float.MaxValue, Right = float.MinValue, Bottom = float.MinValue, Baseline = float.NaN };
        AddInk(ops, ref ink);
        (left, top, right, bottom, baseline) = (ink.Left, ink.Top, ink.Right, ink.Bottom, ink.Baseline);
        return left <= right && top <= bottom;
    }

    private void AddInk(List<DrawOp> ops, ref InkBounds ink)
    {
        foreach (var op in ops)
        {
            if (op is LayerOp layer) AddInk(layer.Ops, ref ink);
            else if (op is TextOp { Size: > 0 } text)
            {
                if (float.IsNaN(ink.Baseline)) ink.Baseline = text.Baseline;
                ink.Op = text;
                VisitGlyphs(text, ref ink);
            }
        }
    }

    /// <summary>Moves the text in <paramref name="ops"/>, but not the box it is in.</summary>
    private static void ShiftText(List<DrawOp> ops, float dx, float dy)
    {
        foreach (var op in ops)
        {
            if (op is LayerOp layer) ShiftText(layer.Ops, dx, dy);
            else if (op is TextOp text) text.Offset(dx, dy);
        }
    }

    /// <summary>
    /// Grows a box, in page coordinates, round the ink of each glyph. Each glyph's box is first rounded out to whole
    /// pixels from its origin, as the glyph bounds Chrome measures initial letters with are.
    /// </summary>
    private struct InkBounds : IGlyphVisitor
    {
        public float Left, Top, Right, Bottom, Baseline;
        public TextOp Op;

        public void Glyph(FontProgram program, int gid, string text, int start, float x, float dy, float skew)
        {
            if (GlyphInk.Bounds(program, gid, skew, out float l, out float b, out float r, out float t)) Add(x, dy, l, b, r, t);
        }

        public void Glyph(StandardFont font, char c, string text, int start, float x)
        {
            if (GlyphInk.Bounds(font, c, out float l, out float b, out float r, out float t)) Add(x, 0, l, b, r, t);
        }

        private void Add(float x, float dy, float l, float b, float r, float t)
        {
            float size = Op.Size, y = Op.Baseline - dy;
            Left = Math.Min(Left, x + MathF.Floor(Pixels(l * size)) * CssPixel);
            Right = Math.Max(Right, x + MathF.Ceiling(Pixels(r * size)) * CssPixel);
            Top = Math.Min(Top, y - MathF.Ceiling(Pixels(t * size)) * CssPixel);
            Bottom = Math.Max(Bottom, y - MathF.Floor(Pixels(b * size)) * CssPixel);
        }

        // FreeType scales outlines to 1/64 pixel before it measures them.
        private static float Pixels(float points) => MathF.Round(points / CssPixel * 64) / 64;
    }
}
