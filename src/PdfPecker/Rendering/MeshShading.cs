using PdfPecker.Rendering.Content;
using PdfPecker.Rendering.Graphics;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering;

/// <summary>Free-form, lattice, Coons and tensor-product mesh shadings (types 4–7), painted as Gouraud triangles.</summary>
internal static class MeshShading
{
    private const int LutSize = 1024;

    private struct Vertex
    {
        public double X, Y; // device space
        public double C0, C1, C2; // t (with a function) or r, g, b in 0..255
    }

    private sealed class Ctx
    {
        public Canvas Layer = null!;
        public int CX0, CY0, CX1, CY1;
        public uint[]? Lut;
        public double T0, T1;
    }

    public static void Paint(PdfFile file, PdfDict shading, int type, ColorSpace cs, PdfFunction? fn, Matrix toDevice, Canvas layer, ClipRegion clip)
    {
        var ctx = new Ctx { Layer = layer, CX0 = clip.X0, CY0 = clip.Y0, CX1 = clip.X1, CY1 = clip.Y1 };
        int bpcoord = file.GetInt(shading, "BitsPerCoordinate", 16);
        int bpcomp = file.GetInt(shading, "BitsPerComponent", 8);
        int bpflag = file.GetInt(shading, "BitsPerFlag", 8);
        int nComps = fn != null ? 1 : cs.Components;
        var decode = file.GetNumbers(shading, "Decode");
        if (decode == null || decode.Length < 4 + 2 * nComps) return;
        if (fn != null)
        {
            ctx.T0 = decode[4];
            ctx.T1 = decode[5];
            ctx.Lut = ShadingColor.BuildLut(cs, fn, ctx.T0, ctx.T1, LutSize);
        }
        byte[] data = shading is PdfStream s ? file.DecodeStream(s) : Array.Empty<byte>();
        var br = new BitReader(data);
        var comps = new float[Math.Max(1, cs.Components)];

        Vertex ReadVertex(bool flag, out int f)
        {
            f = flag ? (int)br.Read(bpflag) : 0;
            var v = new Vertex();
            double x = Scale(br.Read(bpcoord), bpcoord, decode[0], decode[1]);
            double y = Scale(br.Read(bpcoord), bpcoord, decode[2], decode[3]);
            (v.X, v.Y) = toDevice.Apply(x, y);
            ReadColor(ref v);
            return v;
        }

        void ReadColor(ref Vertex v)
        {
            if (fn != null)
            {
                v.C0 = Scale(br.Read(bpcomp), bpcomp, decode[4], decode[5]);
                return;
            }
            for (int i = 0; i < nComps; i++) comps[i] = (float)Scale(br.Read(bpcomp), bpcomp, decode[4 + 2 * i], decode[5 + 2 * i]);
            uint rgb = cs.ToRgb(comps);
            v.C0 = rgb & 255;
            v.C1 = (rgb >> 8) & 255;
            v.C2 = (rgb >> 16) & 255;
        }

        switch (type)
        {
            case 4:
            {
                Vertex a = default, b = default, c = default;
                int count = 0;
                while (br.HasMore(bpflag + 2 * bpcoord + nComps * bpcomp))
                {
                    var v = ReadVertex(true, out int f);
                    br.Align();
                    if (f == 0 || count < 3)
                    {
                        if (f == 0 && count >= 3) count = 0;
                        if (count == 0) a = v; else if (count == 1) b = v; else c = v;
                        count++;
                        if (count == 3) Triangle(ctx, a, b, c);
                        continue;
                    }
                    if (f == 1) { a = b; b = c; c = v; }
                    else { b = c; c = v; }
                    Triangle(ctx, a, b, c);
                }
                break;
            }
            case 5:
            {
                int perRow = file.GetInt(shading, "VerticesPerRow", 2);
                if (perRow < 2) return;
                var prev = new Vertex[perRow];
                var cur = new Vertex[perRow];
                int row = 0;
                while (true)
                {
                    int i;
                    for (i = 0; i < perRow; i++)
                    {
                        if (!br.HasMore(2 * bpcoord + nComps * bpcomp)) break;
                        cur[i] = ReadVertex(false, out _);
                    }
                    if (i < perRow) break;
                    if (row > 0)
                        for (int k = 0; k + 1 < perRow; k++)
                        {
                            Triangle(ctx, prev[k], prev[k + 1], cur[k]);
                            Triangle(ctx, prev[k + 1], cur[k + 1], cur[k]);
                        }
                    (prev, cur) = (cur, prev);
                    row++;
                }
                break;
            }
            case 6:
            case 7:
            {
                int nPts = type == 6 ? 12 : 16;
                var pts = new (double X, double Y)[16];
                var cols = new Vertex[4];
                var prevPts = new (double X, double Y)[16];
                var prevCols = new Vertex[4];
                bool havePrev = false;
                while (br.HasMore(bpflag))
                {
                    int f = (int)br.Read(bpflag);
                    int first = 0, firstCol = 0;
                    if (f != 0)
                    {
                        if (!havePrev) break;
                        int[] edge = f switch { 1 => new[] { 3, 4, 5, 6 }, 2 => new[] { 6, 7, 8, 9 }, _ => new[] { 9, 10, 11, 0 } };
                        int[] edgeCol = f switch { 1 => new[] { 1, 2 }, 2 => new[] { 2, 3 }, _ => new[] { 3, 0 } };
                        for (int i = 0; i < 4; i++) pts[i] = prevPts[edge[i]];
                        cols[0] = prevCols[edgeCol[0]];
                        cols[1] = prevCols[edgeCol[1]];
                        first = 4;
                        firstCol = 2;
                    }
                    int need = (nPts - first) * 2 * bpcoord + (4 - firstCol) * nComps * bpcomp;
                    if (!br.HasMore(need)) break;
                    for (int i = first; i < nPts; i++)
                    {
                        double x = Scale(br.Read(bpcoord), bpcoord, decode[0], decode[1]);
                        double y = Scale(br.Read(bpcoord), bpcoord, decode[2], decode[3]);
                        pts[i] = toDevice.Apply(x, y);
                    }
                    for (int i = firstCol; i < 4; i++)
                    {
                        var v = new Vertex();
                        ReadColor(ref v);
                        cols[i] = v;
                    }
                    br.Align();
                    Patch(ctx, pts, cols, type == 7);
                    Array.Copy(pts, prevPts, 16);
                    Array.Copy(cols, prevCols, 4);
                    havePrev = true;
                }
                break;
            }
        }
    }

    private static double Scale(uint raw, int bits, double min, double max)
    {
        double maxRaw = bits >= 32 ? uint.MaxValue : (1L << bits) - 1;
        return min + raw * (max - min) / maxRaw;
    }

    // ------------------------------------------------------------------ patches

    private static void Patch(Ctx ctx, (double X, double Y)[] s, Vertex[] cols, bool tensor)
    {
        // Grid p[i, j]: stream order p00 p01 p02 p03 p13 p23 p33 p32 p31 p30 p20 p10 (+ p11 p12 p22 p21).
        var p = new (double X, double Y)[4, 4];
        p[0, 0] = s[0]; p[0, 1] = s[1]; p[0, 2] = s[2]; p[0, 3] = s[3];
        p[1, 3] = s[4]; p[2, 3] = s[5]; p[3, 3] = s[6];
        p[3, 2] = s[7]; p[3, 1] = s[8]; p[3, 0] = s[9];
        p[2, 0] = s[10]; p[1, 0] = s[11];
        if (tensor)
        {
            p[1, 1] = s[12]; p[1, 2] = s[13]; p[2, 2] = s[14]; p[2, 1] = s[15];
        }
        else
        {
            p[1, 1] = Inner(p[0, 0], p[0, 1], p[1, 0], p[0, 3], p[3, 0], p[3, 1], p[1, 3], p[3, 3]);
            p[1, 2] = Inner(p[0, 3], p[0, 2], p[1, 3], p[0, 0], p[3, 3], p[3, 2], p[1, 0], p[3, 0]);
            p[2, 1] = Inner(p[3, 0], p[3, 1], p[2, 0], p[3, 3], p[0, 0], p[0, 1], p[2, 3], p[0, 3]);
            p[2, 2] = Inner(p[3, 3], p[3, 2], p[2, 3], p[3, 0], p[0, 3], p[0, 2], p[2, 0], p[0, 0]);
        }
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var q in p)
        {
            minX = Math.Min(minX, q.X); maxX = Math.Max(maxX, q.X);
            minY = Math.Min(minY, q.Y); maxY = Math.Max(maxY, q.Y);
        }
        if (maxX < ctx.CX0 || minX > ctx.CX1 || maxY < ctx.CY0 || minY > ctx.CY1) return;
        int n = (int)Math.Clamp(Math.Ceiling(Math.Max(maxX - minX, maxY - minY) / 3), 1, 64);
        // Corner colors: c00 (u=0,v=0), c03 (u=0,v=1), c33 (u=1,v=1), c30 (u=1,v=0); u follows i, v follows j.
        var grid = new Vertex[(n + 1) * (n + 1)];
        Span<double> bu = stackalloc double[4];
        Span<double> bv = stackalloc double[4];
        for (int iu = 0; iu <= n; iu++)
        {
            double u = (double)iu / n;
            Bernstein(u, bu);
            for (int iv = 0; iv <= n; iv++)
            {
                double v = (double)iv / n;
                Bernstein(v, bv);
                double x = 0, y = 0;
                for (int i = 0; i < 4; i++)
                    for (int j = 0; j < 4; j++)
                    {
                        double w = bu[i] * bv[j];
                        x += p[i, j].X * w;
                        y += p[i, j].Y * w;
                    }
                var vx = new Vertex { X = x, Y = y };
                double w00 = (1 - u) * (1 - v), w01 = (1 - u) * v, w11 = u * v, w10 = u * (1 - v);
                vx.C0 = cols[0].C0 * w00 + cols[1].C0 * w01 + cols[2].C0 * w11 + cols[3].C0 * w10;
                vx.C1 = cols[0].C1 * w00 + cols[1].C1 * w01 + cols[2].C1 * w11 + cols[3].C1 * w10;
                vx.C2 = cols[0].C2 * w00 + cols[1].C2 * w01 + cols[2].C2 * w11 + cols[3].C2 * w10;
                grid[iu * (n + 1) + iv] = vx;
            }
        }
        for (int iu = 0; iu < n; iu++)
            for (int iv = 0; iv < n; iv++)
            {
                var a = grid[iu * (n + 1) + iv];
                var b = grid[(iu + 1) * (n + 1) + iv];
                var c = grid[(iu + 1) * (n + 1) + iv + 1];
                var d = grid[iu * (n + 1) + iv + 1];
                Triangle(ctx, a, b, c);
                Triangle(ctx, a, c, d);
            }
    }

    private static (double X, double Y) Inner((double X, double Y) corner, (double X, double Y) n1, (double X, double Y) n2,
        (double X, double Y) far1, (double X, double Y) far2, (double X, double Y) o1, (double X, double Y) o2, (double X, double Y) opp)
    {
        // p11 = (-4 p00 + 6 (p01 + p10) - 2 (p03 + p30) + 3 (p31 + p13) - p33) / 9
        return ((-4 * corner.X + 6 * (n1.X + n2.X) - 2 * (far1.X + far2.X) + 3 * (o1.X + o2.X) - opp.X) / 9,
                (-4 * corner.Y + 6 * (n1.Y + n2.Y) - 2 * (far1.Y + far2.Y) + 3 * (o1.Y + o2.Y) - opp.Y) / 9);
    }

    private static void Bernstein(double t, Span<double> b)
    {
        double it = 1 - t;
        b[0] = it * it * it;
        b[1] = 3 * t * it * it;
        b[2] = 3 * t * t * it;
        b[3] = t * t * t;
    }

    // ------------------------------------------------------------------ triangles

    private static void Triangle(Ctx ctx, in Vertex a, in Vertex b, in Vertex c)
    {
        double area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        if (Math.Abs(area) < 1e-12) return;
        int x0 = Math.Max(ctx.CX0, (int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X))));
        int x1 = Math.Min(ctx.CX1 - 1, (int)Math.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))));
        int y0 = Math.Max(ctx.CY0, (int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y))));
        int y1 = Math.Min(ctx.CY1 - 1, (int)Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))));
        if (x1 < x0 || y1 < y0) return;
        double inv = 1 / area;
        var px = ctx.Layer.Pixels;
        int width = ctx.Layer.Width;
        const double Eps = -1e-7;
        for (int y = y0; y <= y1; y++)
        {
            double py = y + 0.5;
            for (int x = x0; x <= x1; x++)
            {
                double pxc = x + 0.5;
                double w0 = ((b.X - pxc) * (c.Y - py) - (b.Y - py) * (c.X - pxc)) * inv;
                double w1 = ((c.X - pxc) * (a.Y - py) - (c.Y - py) * (a.X - pxc)) * inv;
                double w2 = 1 - w0 - w1;
                if (w0 < Eps || w1 < Eps || w2 < Eps) continue;
                uint color;
                if (ctx.Lut != null)
                {
                    double t = a.C0 * w0 + b.C0 * w1 + c.C0 * w2;
                    double range = ctx.T1 - ctx.T0;
                    int idx = range == 0 ? 0 : (int)Math.Clamp((t - ctx.T0) / range * (LutSize - 1) + 0.5, 0, LutSize - 1);
                    color = ctx.Lut[idx];
                }
                else
                {
                    int r = (int)Math.Clamp(a.C0 * w0 + b.C0 * w1 + c.C0 * w2 + 0.5, 0, 255);
                    int g = (int)Math.Clamp(a.C1 * w0 + b.C1 * w1 + c.C1 * w2 + 0.5, 0, 255);
                    int bl = (int)Math.Clamp(a.C2 * w0 + b.C2 * w1 + c.C2 * w2 + 0.5, 0, 255);
                    color = (uint)(r | g << 8 | bl << 16) | 0xFF000000;
                }
                px[y * width + x] = color;
            }
        }
    }

    private sealed class BitReader
    {
        private readonly byte[] _d;
        private long _bit;

        public BitReader(byte[] d) => _d = d;

        public bool HasMore(int bits) => _bit + bits <= (long)_d.Length * 8;

        public void Align() => _bit = (_bit + 7) & ~7L;

        public uint Read(int bits)
        {
            uint v = 0;
            for (int i = 0; i < bits; i++)
            {
                long b = _bit++;
                if (b >> 3 >= _d.Length) return v << (bits - i - 1);
                v = v << 1 | (uint)((_d[b >> 3] >> (7 - (int)(b & 7))) & 1);
            }
            return v;
        }
    }
}
