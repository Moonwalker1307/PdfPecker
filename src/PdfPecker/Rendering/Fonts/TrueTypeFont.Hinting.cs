using System.Text;
using PdfPecker.Rendering.Graphics;

namespace PdfPecker.Rendering.Fonts;

/// <summary>
/// The "tricky" fonts: older CJK fonts whose glyphs are only right after their TrueType instructions have run. Their
/// composite glyphs place shared strokes at nonsense offsets and leave it to the glyph program to move them, so
/// without the instructions the strokes land in the wrong places. FreeType keeps a list of them by name and always
/// hints them; this does the same, at one pixel per font unit, so the outlines keep their designed shapes.
/// </summary>
internal sealed partial class TrueTypeFont
{
    // The family names FreeType treats as tricky (tt_check_trickyness_family in ttobjs.c).
    private static readonly string[] TrickyNames =
    [
        "cpop", "DFGirl-W6-WIN-BF", "DFGothic-EB", "DFGyoSho-Lt", "DFHei", "DFHSGothic-W5", "DFHSMincho-W3",
        "DFHSMincho-W7", "DFKaiSho-SB", "DFKaiShu", "DFKai-SB", "DFMaruGothic", "DFMincho", "DFMinCho", "DLC",
        "HuaTianKaiTi?", "HuaTianSongTi?", "Ming(for ISO10646)", "MingLiU", "MingMedium", "PMingLiU", "MingLi43",
    ];

    private const int MaxpTwilightField = 16, MaxpStorageField = 18, MaxpFunctionDefsField = 20, MaxpStackField = 24, MaxpVersion1Length = 32;
    private const int CompositeHasInstructions = 0x100, RoundXYToGrid = 4;
    private const int Pixel = 64; // one font unit at one pixel per unit, in 26.6
    private const int MaxHintedPoints = 65_536;

    private bool _tricky;
    private TrueTypeInterpreter? _interpreter;
    private bool _interpreterReady;

    /// <summary>True when the font is one whose glyphs are drawn by running their instructions.</summary>
    public bool IsTricky => _tricky;

    /// <summary>
    /// Marks the font as tricky when <paramref name="name"/>, the PDF's name for it, is one of the tricky fonts: a
    /// subset can lose its name table, but the PDF still names the font.
    /// </summary>
    public void CheckTrickyName(string? name)
    {
        if (!_tricky && name != null && IsTrickyName(name)) _tricky = true;
    }

    internal static bool IsTrickyName(string name)
    {
        int plus = name.IndexOf('+');
        // A subset prefix is six capital letters and a plus sign.
        if (plus == 6) name = name[7..];
        foreach (string tricky in TrickyNames)
        {
            string prefix = tricky.TrimEnd('?');
            if (name.StartsWith(prefix, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    // Looks at the family, full and PostScript names of the name table.
    private void DetectTricky()
    {
        if (Table("name") is not { Length: >= 6 } name) return;
        int count = _font.U16(name.Offset + 2), strings = name.Offset + _font.U16(name.Offset + 4);
        for (int i = 0; i < count && i < 512; i++)
        {
            int record = name.Offset + 6 + i * 12;
            if (record + 12 > name.Offset + name.Length) break;
            int platform = _font.U16(record), nameId = _font.U16(record + 6);
            if (nameId is not (1 or 4 or 6)) continue;
            int length = _font.U16(record + 8), offset = strings + _font.U16(record + 10);
            if (offset < 0 || length > 256 || offset + length > Data.Length) continue;
            string text = platform is 0 or 3
                ? Encoding.BigEndianUnicode.GetString(Data, offset, length & ~1)
                : Encoding.Latin1.GetString(Data, offset, length);
            if (IsTrickyName(text))
            {
                _tricky = true;
                return;
            }
        }
    }

    private TrueTypeInterpreter? Interpreter()
    {
        lock (_lock)
        {
            if (_interpreterReady) return _interpreter;
            _interpreterReady = true;
            try { _interpreter = CreateInterpreter(); }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException)
            {
                SwallowedErrors.Record(ex);
                _interpreter = null;
            }
            return _interpreter;
        }
    }

    private TrueTypeInterpreter? CreateInterpreter()
    {
        byte[]? fpgm = Table("fpgm") is { } f ? Data.AsSpan(f.Offset, f.Length).ToArray() : null;
        byte[]? prep = Table("prep") is { } p ? Data.AsSpan(p.Offset, p.Length).ToArray() : null;
        var cvt = Array.Empty<short>();
        if (Table("cvt ") is { } c)
        {
            cvt = new short[c.Length / 2];
            for (int i = 0; i < cvt.Length; i++) cvt[i] = _font.S16(c.Offset + i * 2);
        }
        int twilight = 0, storage = 0, functions = 0, stack = 0;
        if (Table("maxp") is { Length: >= MaxpVersion1Length } maxp)
        {
            twilight = _font.U16(maxp.Offset + MaxpTwilightField);
            storage = _font.U16(maxp.Offset + MaxpStorageField);
            functions = _font.U16(maxp.Offset + MaxpFunctionDefsField);
            stack = _font.U16(maxp.Offset + MaxpStackField);
        }
        return TrueTypeInterpreter.Create(fpgm, prep, cvt, UnitsPerEm, stack, storage, twilight, functions);
    }

    /// <summary>The points of a glyph after its instructions ran, with its four phantom points last.</summary>
    private sealed class HintedGlyph
    {
        public readonly List<int> X = new(), Y = new(), OrusX = new(), OrusY = new(), Ends = new();
        public readonly List<bool> On = new();
        public int Count => X.Count;
    }

    // Draws the glyph from its hinted points; false when it cannot be hinted, so it is drawn as it is.
    private bool TryAppendHinted(PathData path, int gid)
    {
        if (Interpreter() is not { } interpreter) return false;
        int budget = ComponentBudgetPerGlyph;
        if (LoadHinted(interpreter, gid, 0, ref budget) is not { } glyph) return false;
        int realPoints = glyph.Count - 4;
        int originX = glyph.X[realPoints]; // the hinted left side bearing point is the glyph's origin
        var xs = new int[realPoints];
        var ys = new int[realPoints];
        var on = new bool[realPoints];
        for (int i = 0; i < realPoints; i++)
        {
            xs[i] = glyph.X[i] - originX;
            ys[i] = glyph.Y[i];
            on[i] = glyph.On[i];
        }
        var scale = Matrix.Scale(1.0 / Pixel, 1.0 / Pixel);
        int first = 0;
        foreach (int end in glyph.Ends)
        {
            int last = Math.Min(end, realPoints - 1);
            if (last < first) continue;
            AddContour(path, xs, ys, on, first, last, scale);
            first = last + 1;
        }
        return true;
    }

    private HintedGlyph? LoadHinted(TrueTypeInterpreter interpreter, int gid, int depth, ref int budget)
    {
        if (depth > MaxCompositeDepth || --budget < 0 || gid < 0 || gid + 1 >= _loca.Length) return null;
        var glyph = new HintedGlyph();
        if (!TryGetGlyphData(gid, out int at))
        {
            AddPhantoms(glyph, gid, 0);
            return glyph;
        }
        int contourCount = _font.S16(at);
        int xMin = _font.S16(at + 2);
        byte[] program;
        if (contourCount >= 0)
        {
            var simple = ReadSimpleGlyph(at, contourCount, out int instructions, out int length);
            if (simple.Xs.Length > MaxHintedPoints) return null;
            for (int i = 0; i < simple.Xs.Length; i++)
            {
                glyph.X.Add(simple.Xs[i] * Pixel);
                glyph.Y.Add(simple.Ys[i] * Pixel);
                glyph.OrusX.Add(simple.Xs[i]);
                glyph.OrusY.Add(simple.Ys[i]);
                glyph.On.Add(simple.OnCurve[i]);
            }
            for (int contour = 0; contour < simple.ContourCount; contour++) glyph.Ends.Add(simple.LastPoint(contour));
            program = instructions + length <= Data.Length ? Data.AsSpan(instructions, length).ToArray() : [];
        }
        else
        {
            var components = ReadComponents(at, out int end);
            foreach (var component in components)
            {
                if (LoadHinted(interpreter, component.Gid, depth + 1, ref budget) is not { } child) return null;
                if (!AddComponent(glyph, child, component)) return null;
            }
            program = [];
            if (components.Count > 0 && (components[^1].Flags & CompositeHasInstructions) != 0)
            {
                int length = _font.U16(end);
                if (end + 2 + length <= Data.Length) program = Data.AsSpan(end + 2, length).ToArray();
            }
        }
        AddPhantoms(glyph, gid, xMin);
        if (program.Length > 0) Hint(interpreter, glyph, program);
        return glyph;
    }

    // Appends a loaded component, transformed and offset, without its phantom points.
    private static bool AddComponent(HintedGlyph glyph, HintedGlyph child, Component component)
    {
        int count = child.Count - 4;
        int basePoints = glyph.Count;
        if (basePoints + count > MaxHintedPoints) return false;
        var m = component.Transform;
        bool transformed = m.A != 1 || m.B != 0 || m.C != 0 || m.D != 1;
        for (int i = 0; i < count; i++)
        {
            int x = child.X[i], y = child.Y[i];
            if (transformed)
            {
                double tx = m.A * x + m.C * y, ty = m.B * x + m.D * y;
                x = (int)Math.Round(tx);
                y = (int)Math.Round(ty);
            }
            glyph.X.Add(x);
            glyph.Y.Add(y);
            // As in FreeType, the original outline of a component keeps its own coordinates.
            glyph.OrusX.Add(child.OrusX[i]);
            glyph.OrusY.Add(child.OrusY[i]);
            glyph.On.Add(child.On[i]);
        }
        int dx, dy;
        if ((component.Flags & ArgsAreXYValues) != 0)
        {
            dx = component.Arg1 * Pixel;
            dy = component.Arg2 * Pixel;
            if ((component.Flags & RoundXYToGrid) != 0)
            {
                dx = (dx + 32) & -Pixel;
                dy = (dy + 32) & -Pixel;
            }
        }
        else
        {
            // Point matching: the component's point arg2 lands on the glyph's point arg1.
            int k = component.Arg1, l = basePoints + component.Arg2;
            if (k >= basePoints || l >= glyph.Count) return false;
            dx = glyph.X[k] - glyph.X[l];
            dy = glyph.Y[k] - glyph.Y[l];
        }
        for (int i = basePoints; i < glyph.Count; i++)
        {
            glyph.X[i] += dx;
            glyph.Y[i] += dy;
        }
        foreach (int end in child.Ends) glyph.Ends.Add(Math.Min(end, count - 1) + basePoints);
        return true;
    }

    // The phantom points: the origin and advance width horizontally, the top and the advance height vertically.
    private void AddPhantoms(HintedGlyph glyph, int gid, int xMin)
    {
        int advance = (int)Math.Max(0, Advance(gid));
        int origin = (xMin - LeftSideBearing(gid)) * Pixel;
        (int X, int Y)[] phantoms = [(origin, 0), (origin + advance * Pixel, 0), (0, Ascender * Pixel), (0, Descender * Pixel)];
        foreach (var (x, y) in phantoms)
        {
            glyph.X.Add(x);
            glyph.Y.Add(y);
            glyph.OrusX.Add(x / Pixel);
            glyph.OrusY.Add(y / Pixel);
            glyph.On.Add(true);
        }
    }

    private static void Hint(TrueTypeInterpreter interpreter, HintedGlyph glyph, byte[] program)
    {
        var zone = new TrueTypeInterpreter.Zone(glyph.Count, glyph.Ends.ToArray());
        for (int i = 0; i < glyph.Count; i++)
        {
            zone.CurX[i] = zone.OrgX[i] = glyph.X[i];
            zone.CurY[i] = zone.OrgY[i] = glyph.Y[i];
            zone.OrusX[i] = glyph.OrusX[i];
            zone.OrusY[i] = glyph.OrusY[i];
            zone.Flags[i] = glyph.On[i] ? TrueTypeInterpreter.Zone.OnCurve : (byte)0;
        }
        interpreter.RunGlyph(program, zone);
        for (int i = 0; i < glyph.Count; i++)
        {
            glyph.X[i] = zone.CurX[i];
            glyph.Y[i] = zone.CurY[i];
            glyph.On[i] = (zone.Flags[i] & TrueTypeInterpreter.Zone.OnCurve) != 0;
        }
    }
}
