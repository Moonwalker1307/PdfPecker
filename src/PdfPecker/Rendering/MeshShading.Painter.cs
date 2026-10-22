using PdfPecker.Rendering.Content;
using PdfPecker.Rendering.Graphics;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering;

internal static partial class MeshShading
{
    /// <summary>
    /// Fills Gouraud-shaded triangles into the layer within the clip bounds, and splits patches into such triangles.
    /// With a function, the vertex colours are the parameter t, looked up in a table sampled over the Decode range.
    /// </summary>
    private sealed class MeshPainter
    {
        private const int LutSize = 1024;

        // Patches are split into cells about this many device pixels wide, at most MaxPatchSteps along each side.
        private const double PixelsPerPatchStep = 3;
        private const int MaxPatchSteps = 64;

        // Triangles thinner than this are skipped, and a pixel centre this far outside an edge still counts as inside,
        // so that neighbouring triangles leave no gaps.
        private const double MinArea = 1e-12;
        private const double EdgeTolerance = -1e-7;

        private const uint OpaqueAlpha = 0xFF000000;

        // The work a mesh may cost, in triangles set up plus pixels tested: enough to cover the layer many times over
        // and to give each patch or vertex of the data hundreds of triangles beyond that, but bounded, so that a small
        // stream of huge overlapping patches or a vast stream of vertices cannot run for minutes. Cancellation is
        // checked each time CancelCheckInterval more of it is done.
        private const long BaseBudget = 1L << 20;
        private const long BudgetPerPixel = 64, BudgetPerDataByte = 16;
        private const long CancelCheckInterval = 1L << 16;

        // Stream order of the patch points as positions row * 4 + column of the 4 x 4 control grid:
        // p00 p01 p02 p03 p13 p23 p33 p32 p31 p30 p20 p10, then the inner p11 p12 p22 p21 of a tensor patch.
        private static ReadOnlySpan<byte> PatchPointGridPositions => new byte[] { 0, 1, 2, 3, 7, 11, 15, 14, 13, 12, 8, 4, 5, 6, 10, 9 };

        private readonly uint[] _pixels;
        private readonly int _width, _layerX, _layerY;
        private readonly int _clipX0, _clipY0, _clipX1, _clipY1;
        private readonly uint[]? _lut;
        private readonly double _t0, _tRange;
        private long _budget;
        private long _nextCancelCheck;

        /// <summary>
        /// A painter into <paramref name="layer"/>, whose top left pixel is device pixel (<paramref name="layerX"/>,
        /// <paramref name="layerY"/>); it paints only where the layer and the clip bounds overlap.
        /// </summary>
        public MeshPainter(Canvas layer, int layerX, int layerY, ClipRegion clip, long budget)
        {
            _pixels = layer.Pixels;
            _width = layer.Width;
            (_layerX, _layerY) = (layerX, layerY);
            (_clipX0, _clipY0) = (Math.Max(clip.X0, layerX), Math.Max(clip.Y0, layerY));
            (_clipX1, _clipY1) = (Math.Min(clip.X1, layerX + layer.Width), Math.Min(clip.Y1, layerY + layer.Height));
            _budget = budget;
            _nextCancelCheck = budget - CancelCheckInterval;
        }

        private MeshPainter(Canvas layer, int layerX, int layerY, ClipRegion clip, long budget, uint[] lut, double t0, double t1)
            : this(layer, layerX, layerY, clip, budget)
        {
            _lut = lut;
            _t0 = t0;
            _tRange = t1 - t0;
        }

        /// <summary>A painter whose vertex colours are parameters of <paramref name="fn"/> over the colour Decode range.</summary>
        public static MeshPainter WithFunction(Canvas layer, int layerX, int layerY, ClipRegion clip, long budget, ColorSpace cs, PdfFunction fn,
            ReadOnlySpan<double> decode)
        {
            double t0 = decode[MeshReader.ColorDecodeStart], t1 = decode[MeshReader.ColorDecodeStart + 1];
            return new MeshPainter(layer, layerX, layerY, clip, budget, ShadingColor.BuildLut(cs, fn, t0, t1, LutSize), t0, t1);
        }

        /// <summary>The work budget for a mesh of <paramref name="dataLength"/> bytes painted on a page of the given size.</summary>
        public static long WorkBudget(int pageWidth, int pageHeight, int dataLength) =>
            BaseBudget + BudgetPerPixel * pageWidth * (long)pageHeight + BudgetPerDataByte * dataLength;

        /// <summary>Whether work is left in the budget; once it is spent, nothing more is painted.</summary>
        public bool HasBudget => _budget > 0;

        /// <summary>Spends work from the budget, and checks for cancellation each time another interval of it is done.</summary>
        private void Spend(long work)
        {
            _budget -= work;
            if (_budget > _nextCancelCheck) return;
            PdfFile.CheckCancel();
            _nextCancelCheck = _budget - CancelCheckInterval;
        }

        public void FillTriangle(in Vertex a, in Vertex b, in Vertex c)
        {
            Spend(1);
            if (!HasBudget) return;
            double area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            if (Math.Abs(area) < MinArea) return;
            int x0 = Math.Max(_clipX0, (int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X))));
            int x1 = Math.Min(_clipX1 - 1, (int)Math.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))));
            int y0 = Math.Max(_clipY0, (int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y))));
            int y1 = Math.Min(_clipY1 - 1, (int)Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))));
            if (x1 < x0 || y1 < y0) return;
            Spend((long)(x1 - x0 + 1) * (y1 - y0 + 1));
            double inverseArea = 1 / area;
            for (int y = y0; y <= y1; y++)
            {
                double py = y + 0.5;
                int row = (y - _layerY) * _width - _layerX;
                for (int x = x0; x <= x1; x++)
                {
                    double px = x + 0.5;
                    double w0 = ((b.X - px) * (c.Y - py) - (b.Y - py) * (c.X - px)) * inverseArea;
                    double w1 = ((c.X - px) * (a.Y - py) - (c.Y - py) * (a.X - px)) * inverseArea;
                    double w2 = 1 - w0 - w1;
                    if (w0 < EdgeTolerance || w1 < EdgeTolerance || w2 < EdgeTolerance) continue;
                    _pixels[row + x] = Blend(a.Color, b.Color, c.Color, w0, w1, w2);
                }
            }
        }

        private uint Blend(in VertexColor a, in VertexColor b, in VertexColor c, double w0, double w1, double w2)
        {
            if (_lut != null)
            {
                double t = a.C0 * w0 + b.C0 * w1 + c.C0 * w2;
                int index = _tRange == 0 ? 0 : (int)Math.Clamp((t - _t0) / _tRange * (LutSize - 1) + 0.5, 0, LutSize - 1);
                return _lut[index];
            }
            int red = (int)Math.Clamp(a.C0 * w0 + b.C0 * w1 + c.C0 * w2 + 0.5, 0, 255);
            int green = (int)Math.Clamp(a.C1 * w0 + b.C1 * w1 + c.C1 * w2 + 0.5, 0, 255);
            int blue = (int)Math.Clamp(a.C2 * w0 + b.C2 * w1 + c.C2 * w2 + 0.5, 0, 255);
            return (uint)(red | green << 8 | blue << 16) | OpaqueAlpha;
        }

        /// <summary>
        /// Evaluates the patch surface on a grid of about three pixels per cell and fills each cell as two triangles.
        /// Positions follow the bicubic surface and colours blend the corners bilinearly; u follows the first index of
        /// the control grid p[i, j] and v the second.
        /// </summary>
        public void FillPatch(ReadOnlySpan<(double X, double Y)> points, ReadOnlySpan<VertexColor> corners, bool tensor)
        {
            Spend(1);
            if (!HasBudget) return;
            Span<(double X, double Y)> grid = stackalloc (double X, double Y)[16];
            int streamPoints = tensor ? TensorPoints : CoonsPoints;
            for (int i = 0; i < streamPoints; i++) grid[PatchPointGridPositions[i]] = points[i];
            if (!tensor) AddCoonsInnerPoints(grid);

            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var (x, y) in grid)
            {
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
            if (maxX < _clipX0 || minX > _clipX1 || maxY < _clipY0 || minY > _clipY1) return;
            int steps = (int)Math.Clamp(Math.Ceiling(Math.Max(maxX - minX, maxY - minY) / PixelsPerPatchStep), 1, MaxPatchSteps);
            // A NaN extent converts to 0 or int.MinValue, depending on the runtime; either way there are no cells.
            if (steps < 1) return;

            // The cells between two neighbouring grid rows are filled before the next row is evaluated, so only two
            // rows are kept.
            Span<Vertex> lower = stackalloc Vertex[MaxPatchSteps + 1];
            Span<Vertex> upper = stackalloc Vertex[MaxPatchSteps + 1];
            lower = lower[..(steps + 1)];
            upper = upper[..(steps + 1)];
            EvaluatePatchRow(grid, corners, 0, steps, lower);
            for (int iu = 0; iu < steps && HasBudget; iu++)
            {
                EvaluatePatchRow(grid, corners, iu + 1, steps, upper);
                for (int iv = 0; iv < steps; iv++)
                {
                    FillTriangle(lower[iv], upper[iv], upper[iv + 1]);
                    FillTriangle(lower[iv], upper[iv + 1], lower[iv + 1]);
                }
                var filled = lower;
                lower = upper;
                upper = filled;
            }
        }

        /// <summary>Evaluates the surface and the corner blend at u = iu / steps for each v = iv / steps.</summary>
        private static void EvaluatePatchRow(ReadOnlySpan<(double X, double Y)> grid, ReadOnlySpan<VertexColor> corners, int iu, int steps, Span<Vertex> row)
        {
            // Corner colours: c00 (u = 0, v = 0), c03 (u = 0, v = 1), c33 (u = 1, v = 1), c30 (u = 1, v = 0).
            ref readonly var c00 = ref corners[0];
            ref readonly var c03 = ref corners[1];
            ref readonly var c33 = ref corners[2];
            ref readonly var c30 = ref corners[3];
            Span<double> bu = stackalloc double[4];
            Span<double> bv = stackalloc double[4];
            double u = (double)iu / steps;
            Bernstein(u, bu);
            for (int iv = 0; iv <= steps; iv++)
            {
                double v = (double)iv / steps;
                Bernstein(v, bv);
                double x = 0, y = 0;
                for (int i = 0; i < 4; i++)
                    for (int j = 0; j < 4; j++)
                    {
                        double w = bu[i] * bv[j];
                        x += grid[i * 4 + j].X * w;
                        y += grid[i * 4 + j].Y * w;
                    }
                double w00 = (1 - u) * (1 - v), w01 = (1 - u) * v, w11 = u * v, w10 = u * (1 - v);
                row[iv] = new Vertex(x, y, new VertexColor(
                    c00.C0 * w00 + c03.C0 * w01 + c33.C0 * w11 + c30.C0 * w10,
                    c00.C1 * w00 + c03.C1 * w01 + c33.C1 * w11 + c30.C1 * w10,
                    c00.C2 * w00 + c03.C2 * w01 + c33.C2 * w11 + c30.C2 * w10));
            }
        }

        /// <summary>
        /// A Coons patch has no inner control points; these are the ones that make the tensor-product surface equal to
        /// the Coons surface (PDF 32000-1, 8.7.4.5.8).
        /// </summary>
        private static void AddCoonsInnerPoints(Span<(double X, double Y)> p)
        {
            p[5] = Inner(p[0], p[1], p[4], p[3], p[12], p[13], p[7], p[15]);
            p[6] = Inner(p[3], p[2], p[7], p[0], p[15], p[14], p[4], p[12]);
            p[9] = Inner(p[12], p[13], p[8], p[15], p[0], p[1], p[11], p[3]);
            p[10] = Inner(p[15], p[14], p[11], p[12], p[3], p[2], p[8], p[0]);
        }

        // For p11: (-4 p00 + 6 (p01 + p10) - 2 (p03 + p30) + 3 (p31 + p13) - p33) / 9, and the same around the other
        // three corners.
        private static (double X, double Y) Inner((double X, double Y) corner, (double X, double Y) n1, (double X, double Y) n2,
            (double X, double Y) far1, (double X, double Y) far2, (double X, double Y) o1, (double X, double Y) o2, (double X, double Y) opp)
        {
            return ((-4 * corner.X + 6 * (n1.X + n2.X) - 2 * (far1.X + far2.X) + 3 * (o1.X + o2.X) - opp.X) / 9,
                    (-4 * corner.Y + 6 * (n1.Y + n2.Y) - 2 * (far1.Y + far2.Y) + 3 * (o1.Y + o2.Y) - opp.Y) / 9);
        }

        /// <summary>The four cubic Bernstein polynomials at <paramref name="t"/>.</summary>
        private static void Bernstein(double t, Span<double> b)
        {
            double it = 1 - t;
            b[0] = it * it * it;
            b[1] = 3 * t * it * it;
            b[2] = 3 * t * t * it;
            b[3] = t * t * t;
        }
    }
}
