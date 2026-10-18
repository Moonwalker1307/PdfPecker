using System.Numerics;

namespace PdfPecker.Rendering.Images;

// The inverse discrete wavelet transform (ISO/IEC 15444-1, annex F): the reversible 5/3 and irreversible 9/7 filters,
// by lifting.
internal sealed partial class JpxDecoder
{
    // The tile-component buffer holds the sub-bands in the usual layout: at each level the low-pass image sits at the
    // top left with HL to its right, LH below and HH diagonally. Each level is synthesized in place.
    private const int Strip = 32; // columns per vertical pass

    private static void InverseDwt(TileComponent tc)
    {
        var buf = tc.Buffer;
        int stride = tc.Width;
        bool rev = tc.Style.Reversible;
        for (int r = 1; r < tc.Resolutions.Length; r++)
        {
            var res = tc.Resolutions[r];
            int w = res.X1 - res.X0, h = res.Y1 - res.Y0;
            if (w <= 0 || h <= 0) continue;
            int lowW = CeilDivPow2(res.X1, 1) - CeilDivPow2(res.X0, 1), lowH = CeilDivPow2(res.Y1, 1) - CeilDivPow2(res.Y0, 1);

            // Rows: interleave low and high halves, then synthesize.
            int xLow = res.X0 & 1, x0Abs = res.X0;
            int minParallel = (long)w * h >= 1 << 18 ? 16 : int.MaxValue;
            ParallelRange(h, minParallel, () => new float[w], (y, row) =>
            {
                int o = y * stride;
                for (int i = 0, x = xLow; i < lowW; i++, x += 2) row[x] = buf[o + i];
                for (int i = lowW, x = 1 - xLow; i < w; i++, x += 2) row[x] = buf[o + i];
                SynthesizeRow(row.AsSpan(0, w), x0Abs, rev);
                Array.Copy(row, 0, buf, o, w);
            });

            // Columns, a strip at a time: gather rows interleaved, lift whole rows, scatter back.
            int yLow = res.Y0 & 1, y0Abs = res.Y0;
            ParallelRange((w + Strip - 1) / Strip, minParallel, () => new float[Strip * h], (k, strip) =>
            {
                int x0 = k * Strip;
                int sw = Math.Min(Strip, w - x0);
                for (int j = 0; j < h; j++)
                {
                    int dst = j < lowH ? yLow + 2 * j : 1 - yLow + 2 * (j - lowH);
                    Array.Copy(buf, j * stride + x0, strip, dst * sw, sw);
                }
                SynthesizeColumns(strip, sw, h, y0Abs, rev);
                for (int j = 0; j < h; j++) Array.Copy(strip, j * sw, buf, j * stride + x0, sw);
            });
        }
    }

    // The lifting steps and scaling of the 9/7 filter (table F.4).
    private const float Alpha = -1.586134342059924f, Beta = -0.052980118572961f, Gamma = 0.882911075530934f,
        Delta = 0.443506852043971f, K = 1.230174104914001f;

    // Lifting runs in place: with whole-sample symmetric extension every step's output stays symmetric, so a
    // neighbor past either end is its mirror image inside the signal.

    /// <summary>1-D synthesis of one row whose first sample has absolute index <paramref name="i0"/>.</summary>
    private static void SynthesizeRow(Span<float> x, int i0, bool reversible)
    {
        int n = x.Length;
        if (n == 1)
        {
            if ((i0 & 1) != 0) x[0] /= 2;
            return;
        }
        int even = i0 & 1, odd = 1 - even; // first local index of each parity
        if (reversible)
        {
            for (int j = even; j < n; j += 2) x[j] -= MathF.Floor((x[Mirror(j - 1, n)] + x[Mirror(j + 1, n)] + 2) / 4);
            for (int j = odd; j < n; j += 2) x[j] += MathF.Floor((x[Mirror(j - 1, n)] + x[Mirror(j + 1, n)]) / 2);
            return;
        }
        for (int j = even; j < n; j += 2) x[j] *= K;
        for (int j = odd; j < n; j += 2) x[j] *= 1 / K;
        Lift(x, even, Delta);
        Lift(x, odd, Gamma);
        Lift(x, even, Beta);
        Lift(x, odd, Alpha);
    }

    private static void Lift(Span<float> x, int start, float c)
    {
        int n = x.Length;
        int j = start;
        if (j == 0)
        {
            x[0] -= c * 2 * x[1];
            j = 2;
        }
        for (; j < n - 1; j += 2) x[j] -= c * (x[j - 1] + x[j + 1]);
        if (j == n - 1) x[j] -= c * 2 * x[n - 2];
    }

    private static int Mirror(int i, int n) => i < 0 ? -i : i >= n ? 2 * (n - 1) - i : i;

    /// <summary>Vertical synthesis, processing whole rows at a time.</summary>
    private static void SynthesizeColumns(float[] a, int w, int h, int i0, bool reversible)
    {
        if (h == 1)
        {
            if ((i0 & 1) != 0) for (int x = 0; x < w; x++) a[x] /= 2;
            return;
        }
        int even = i0 & 1, odd = 1 - even;
        if (reversible)
        {
            for (int j = even; j < h; j += 2) RowLift53(a, w, j, Mirror(j - 1, h), Mirror(j + 1, h), true);
            for (int j = odd; j < h; j += 2) RowLift53(a, w, j, Mirror(j - 1, h), Mirror(j + 1, h), false);
            return;
        }
        for (int j = 0; j < h; j++) RowScale(a.AsSpan(j * w, w), ((j - even) & 1) == 0 ? K : 1 / K);
        LiftRows(a, w, h, even, Delta);
        LiftRows(a, w, h, odd, Gamma);
        LiftRows(a, w, h, even, Beta);
        LiftRows(a, w, h, odd, Alpha);
    }

    /// <summary>One vertical 9/7 lifting step over the rows from <paramref name="start"/>, every other one.</summary>
    private static void LiftRows(float[] a, int w, int h, int start, float c)
    {
        for (int j = start; j < h; j += 2) RowLift97(a, w, j, Mirror(j - 1, h), Mirror(j + 1, h), c);
    }

    private static void RowScale(Span<float> row, float s)
    {
        int x = 0;
        if (Vector.IsHardwareAccelerated)
        {
            int vn = Vector<float>.Count;
            var vs = new Vector<float>(s);
            for (; x + vn <= row.Length; x += vn)
                (new Vector<float>(row.Slice(x, vn)) * vs).CopyTo(row.Slice(x, vn));
        }
        for (; x < row.Length; x++) row[x] *= s;
    }

    private static void RowLift97(float[] a, int w, int j, int ja, int jb, float c)
    {
        var dst = a.AsSpan(j * w, w);
        var pa = a.AsSpan(ja * w, w);
        var pb = a.AsSpan(jb * w, w);
        int x = 0;
        if (Vector.IsHardwareAccelerated)
        {
            int vn = Vector<float>.Count;
            var vc = new Vector<float>(c);
            for (; x + vn <= w; x += vn)
            {
                var d = new Vector<float>(dst.Slice(x, vn));
                var s = new Vector<float>(pa.Slice(x, vn)) + new Vector<float>(pb.Slice(x, vn));
                (d - vc * s).CopyTo(dst.Slice(x, vn));
            }
        }
        for (; x < w; x++) dst[x] -= c * (pa[x] + pb[x]);
    }

    private static void RowLift53(float[] a, int w, int j, int ja, int jb, bool lowStep)
    {
        var dst = a.AsSpan(j * w, w);
        var pa = a.AsSpan(ja * w, w);
        var pb = a.AsSpan(jb * w, w);
        int x = 0;
        if (Vector.IsHardwareAccelerated)
        {
            int vn = Vector<float>.Count;
            var two = new Vector<float>(2f);
            var quarter = new Vector<float>(0.25f);
            var half = new Vector<float>(0.5f);
            for (; x + vn <= w; x += vn)
            {
                var d = new Vector<float>(dst.Slice(x, vn));
                var s = new Vector<float>(pa.Slice(x, vn)) + new Vector<float>(pb.Slice(x, vn));
                var r = lowStep ? d - Vector.Floor((s + two) * quarter) : d + Vector.Floor(s * half);
                r.CopyTo(dst.Slice(x, vn));
            }
        }
        for (; x < w; x++)
            dst[x] = lowStep ? dst[x] - MathF.Floor((pa[x] + pb[x] + 2) / 4) : dst[x] + MathF.Floor((pa[x] + pb[x]) / 2);
    }

}
