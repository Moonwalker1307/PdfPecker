using System.Runtime.CompilerServices;

namespace PdfPecker.Rendering.Graphics;

/// <summary>Receives rasterized coverage, one row at a time.</summary>
internal interface ICoverageSink
{
    /// <summary>Coverage for pixels [x0, x0 + cov.Length) of row y.</summary>
    void Row(int y, int x0, ReadOnlySpan<byte> cov);

    /// <summary>Constant coverage for pixels [x0, x1) of row y.</summary>
    void Span(int y, int x0, int x1, byte cov);
}

/// <summary>
/// Anti-aliased scanline polygon rasterizer using exact signed-area accumulation: every line segment deposits the area
/// it covers in each cell, and a running sum along each row turns that into per-pixel coverage (winding number).
/// Non-zero and even-odd fill rules are supported.
/// </summary>
internal sealed class Rasterizer
{
    private float[] _acc = new float[4096];
    private int[] _rowMin = new int[256], _rowMax = new int[256];
    private byte[] _cov = new byte[1024];
    private int _x0, _y0, _w, _h, _stride;
    private int _minRow, _maxRow;

    /// <summary>Starts a new path clipped to the device rectangle [x0, x1) × [y0, y1). Returns false if it is empty.</summary>
    public bool Begin(int x0, int y0, int x1, int y1)
    {
        if (x1 <= x0 || y1 <= y0) return false;
        _x0 = x0;
        _y0 = y0;
        _w = x1 - x0;
        _h = y1 - y0;
        _stride = _w + 2;
        long need = (long)_stride * _h;
        if (need > int.MaxValue / 2) return false;
        if (_acc.Length < need)
        {
            _acc = new float[Math.Max(need, _acc.Length * 2L > int.MaxValue / 2 ? need : _acc.Length * 2L)];
        }
        if (_rowMin.Length < _h)
        {
            _rowMin = new int[Math.Max(_h, _rowMin.Length * 2)];
            _rowMax = new int[_rowMin.Length];
        }
        for (int i = 0; i < _h; i++) { _rowMin[i] = int.MaxValue; _rowMax[i] = -1; }
        if (_cov.Length < _stride) _cov = new byte[_stride * 2];
        _minRow = int.MaxValue;
        _maxRow = -1;
        return true;
    }

    public void AddPath(FlatPath p)
    {
        var pts = p.Pts;
        for (int c = 0; c < p.ContourCount; c++)
        {
            int s = p.Starts[c], e = p.ContourEnd(c);
            if (e - s < 2) continue;
            float px = pts[(e - 1) * 2], py = pts[(e - 1) * 2 + 1]; // implicit close
            for (int i = s; i < e; i++)
            {
                float x = pts[i * 2], y = pts[i * 2 + 1];
                Line(px, py, x, y);
                px = x;
                py = y;
            }
        }
    }

    /// <summary>Adds a segment in device coordinates.</summary>
    public void Line(float ax, float ay, float bx, float by)
    {
        ax -= _x0; bx -= _x0;
        ay -= _y0; by -= _y0;
        if (ay == by || float.IsNaN(ax + ay + bx + by)) return;
        float dir = 1;
        if (ay > by)
        {
            (ax, bx) = (bx, ax);
            (ay, by) = (by, ay);
            dir = -1;
        }
        if (by <= 0 || ay >= _h) return;
        // Clip vertically.
        float dxdy = (bx - ax) / (by - ay);
        if (ay < 0) { ax -= ay * dxdy; ay = 0; }
        if (by > _h) { bx -= (by - _h) * dxdy; by = _h; }
        if (by <= ay) return;
        // Split at the left and right edges so every piece lies within [0, w]; pieces left of the area become vertical
        // at x = 0 (they still change the winding of everything to their right), pieces to the right are dropped.
        float w = _w;
        if ((ax < 0) != (bx < 0) && ax != bx)
        {
            float ym = ay + (0 - ax) / (bx - ax) * (by - ay);
            ym = Math.Clamp(ym, ay, by);
            Piece(ax, ay, 0, ym, dir);
            Piece(0, ym, bx, by, dir);
            return;
        }
        if ((ax > w) != (bx > w) && ax != bx)
        {
            float ym = ay + (w - ax) / (bx - ax) * (by - ay);
            ym = Math.Clamp(ym, ay, by);
            Piece(ax, ay, w, ym, dir);
            Piece(w, ym, bx, by, dir);
            return;
        }
        Piece(ax, ay, bx, by, dir);
    }

    private void Piece(float ax, float ay, float bx, float by, float dir)
    {
        if (by <= ay) return;
        if (ax >= _w && bx >= _w) return;
        if (ax <= 0 && bx <= 0) { ax = bx = 0; }
        else if ((ax < 0) != (bx < 0))
        {
            // Splitting may leave a tiny negative from rounding.
            ax = Math.Max(0, ax);
            bx = Math.Max(0, bx);
        }
        ax = Math.Min(ax, _w);
        bx = Math.Min(bx, _w);
        Accumulate(ax, ay, bx, by, dir);
    }

    private void Accumulate(float ax, float ay, float bx, float by, float dir)
    {
        var a = _acc;
        int stride = _stride;
        float dxdy = (bx - ax) / (by - ay);
        float x = ax;
        int yStart = (int)ay;
        int yEnd = Math.Min(_h, (int)MathF.Ceiling(by));
        if (yStart < _minRow) _minRow = yStart;
        if (yEnd - 1 > _maxRow) _maxRow = yEnd - 1;
        for (int y = yStart; y < yEnd; y++)
        {
            int line = y * stride;
            float dy = MathF.Min(y + 1, by) - MathF.Max(y, ay);
            float xnext = x + dxdy * dy;
            float d = dy * dir;
            float x0 = x, x1 = xnext;
            if (x0 > x1) (x0, x1) = (x1, x0);
            float x0f = MathF.Floor(x0);
            int x0i = (int)x0f;
            float x1c = MathF.Ceiling(x1);
            int x1i = (int)x1c;
            if (x0i < _rowMin[y]) _rowMin[y] = x0i;
            int touchMax = Math.Max(x1i, x0i + 1);
            if (touchMax > _rowMax[y]) _rowMax[y] = touchMax;
            if (x1i <= x0i + 1)
            {
                float xmf = 0.5f * (x + xnext) - x0f;
                a[line + x0i] += d - d * xmf;
                a[line + x0i + 1] += d * xmf;
            }
            else
            {
                float s = 1 / (x1 - x0);
                float fx0 = x0 - x0f;
                float a0 = 0.5f * s * (1 - fx0) * (1 - fx0);
                float fx1 = x1 - x1c + 1;
                float am = 0.5f * s * fx1 * fx1;
                a[line + x0i] += d * a0;
                if (x1i == x0i + 2)
                {
                    a[line + x0i + 1] += d * (1 - a0 - am);
                }
                else
                {
                    float a1 = s * (1.5f - fx0);
                    a[line + x0i + 1] += d * (a1 - a0);
                    float ds = d * s;
                    for (int xi = x0i + 2; xi < x1i - 1; xi++) a[line + xi] += ds;
                    float a2 = a1 + (x1i - x0i - 3) * s;
                    a[line + x1i - 1] += d * (1 - a2 - am);
                }
                a[line + x1i] += d * am;
            }
            x = xnext;
        }
    }

    /// <summary>Converts the accumulated areas to coverage, feeds it to <paramref name="sink"/> and clears the buffer.</summary>
    public void Render(bool evenOdd, ICoverageSink sink)
    {
        var a = _acc;
        var cov = _cov;
        int stride = _stride, w = _w;
        int r0 = Math.Max(0, _minRow), r1 = Math.Min(_h - 1, _maxRow);
        for (int y = r0; y <= r1; y++)
        {
            int min = _rowMin[y];
            if (min == int.MaxValue) continue;
            int max = Math.Min(_rowMax[y], w + 1);
            min = Math.Max(0, min);
            int line = y * stride;
            float acc = 0;
            int n = 0;
            int lastX = Math.Min(max, w);
            for (int x = min; x < lastX; x++)
            {
                acc += a[line + x];
                a[line + x] = 0;
                cov[n++] = ToCoverage(acc, evenOdd);
            }
            // Clear the remaining touched cells; their sum determines the coverage of the rest of the row.
            for (int x = lastX; x <= max && x < stride; x++)
            {
                if (x < w) acc += a[line + x];
                a[line + x] = 0;
            }
            if (n > 0) sink.Row(y + _y0, min + _x0, cov.AsSpan(0, n));
            if (lastX < w)
            {
                byte c = ToCoverage(acc, evenOdd);
                if (c != 0) sink.Span(y + _y0, lastX + _x0, w + _x0, c);
            }
            _rowMin[y] = int.MaxValue;
            _rowMax[y] = -1;
        }
        _minRow = int.MaxValue;
        _maxRow = -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ToCoverage(float acc, bool evenOdd)
    {
        float v = MathF.Abs(acc);
        if (evenOdd)
        {
            v %= 2;
            if (v > 1) v = 2 - v;
        }
        else if (v > 1) v = 1;
        return (byte)(v * 255 + 0.5f);
    }
}
