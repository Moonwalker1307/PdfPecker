using System.Globalization;
using PdfPecker.Html;

namespace PdfPecker.Svg;

// Primitive subregions, feTurbulence, feTile, feConvolveMatrix, feDisplacementMap and the lighting primitives.
internal sealed partial class SvgFilter
{
    // ------------------------------------------------------------------ parsing

    /// <summary>
    /// Reads a primitive's x, y, width and height. Missing values come from the filter region; with none given, the
    /// subregion is left to the inputs.
    /// </summary>
    private static void Subregion(Element e, Primitive p, Matrix ctm, bool primitiveBox, (float X, float Y, float W, float H) box,
        (float X, float Y, float W, float H) region, (float W, float H) viewport)
    {
        float Value(string name, bool horizontal, bool size)
        {
            var text = e.GetAttribute(name)?.Trim();
            if (string.IsNullOrEmpty(text)) return float.NaN;
            bool percent = text.EndsWith('%');
            if (percent) text = text[..^1];
            else if (text.EndsWith("px", StringComparison.OrdinalIgnoreCase)) text = text[..^2];
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) || !float.IsFinite(v)) return float.NaN;
            if (primitiveBox)
            {
                if (percent) v /= 100;
                return horizontal ? (size ? 0 : box.X) + v * box.W : (size ? 0 : box.Y) + v * box.H;
            }
            return percent ? v / 100 * (horizontal ? viewport.W : viewport.H) : v;
        }
        float x = Value("x", true, false), y = Value("y", false, false), w = Value("width", true, true), h = Value("height", false, true);
        (p.UX, p.UY, p.UW, p.UH) = region;
        if (float.IsNaN(x) && float.IsNaN(y) && float.IsNaN(w) && float.IsNaN(h)) return;
        if (!float.IsNaN(x)) p.UX = x;
        if (!float.IsNaN(y)) p.UY = y;
        if (!float.IsNaN(w)) p.UW = Math.Max(0, w);
        if (!float.IsNaN(h)) p.UH = Math.Max(0, h);
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var (cx, cy) in new[] { (p.UX, p.UY), (p.UX + p.UW, p.UY), (p.UX, p.UY + p.UH), (p.UX + p.UW, p.UY + p.UH) })
        {
            float rx = cx, ry = cy;
            ctm.Apply(ref rx, ref ry);
            minX = Math.Min(minX, rx); maxX = Math.Max(maxX, rx);
            minY = Math.Min(minY, ry); maxY = Math.Max(maxY, ry);
        }
        p.HasRegion = true;
        (p.RX, p.RY, p.RW, p.RH) = (minX, minY, maxX - minX, maxY - minY);
    }

    private static void ParseConvolve(Element e, Primitive p)
    {
        var order = SvgImage.ParseNumbers(e.GetAttribute("order"));
        p.OrderX = order.Count > 0 ? (int)order[0] : 3;
        p.OrderY = order.Count > 1 ? (int)order[1] : p.OrderX;
        var kernel = SvgImage.ParseNumbers(e.GetAttribute("kernelmatrix"));
        // An invalid matrix makes the result transparent black, as in browsers.
        if (p.OrderX is < 1 or > MaxKernelOrder || p.OrderY is < 1 or > MaxKernelOrder || kernel.Count != p.OrderX * p.OrderY) return;
        p.Kernel = kernel.ToArray();
        float sum = kernel.Sum();
        p.Divisor = Number(e, "divisor", sum == 0 ? 1 : sum);
        if (p.Divisor == 0) p.Divisor = 1;
        p.Bias = Number(e, "bias", 0);
        p.TargetX = (int)Number(e, "targetx", p.OrderX / 2);
        p.TargetY = (int)Number(e, "targety", p.OrderY / 2);
        if (p.TargetX < 0 || p.TargetX >= p.OrderX || p.TargetY < 0 || p.TargetY >= p.OrderY) { p.Kernel = null; return; }
        p.EdgeMode = Keyword(e, "edgemode", "duplicate") switch { "wrap" => EdgeMode.Wrap, "none" => EdgeMode.None, _ => EdgeMode.Duplicate };
        p.PreserveAlpha = string.Equals(e.GetAttribute("preservealpha")?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    private static ColorChannel ParseChannel(string? selector) => selector?.Trim().ToUpperInvariant() switch
    {
        "R" => ColorChannel.R,
        "G" => ColorChannel.G,
        "B" => ColorChannel.B,
        _ => ColorChannel.A,
    };

    private static void ParseLighting(Element e, Primitive p, Matrix ctm, bool primitiveBox, (float X, float Y, float W, float H) box)
    {
        p.SurfaceScale = Number(e, "surfacescale", 1);
        bool diffuse = p.Kind == Kind.DiffuseLighting;
        p.Constant = Math.Max(0, Number(e, diffuse ? "diffuseconstant" : "specularconstant", 1));
        p.SpecularExponent = Math.Clamp(Number(e, "specularexponent", 1), MinSpecularExponent, MaxSpecularExponent);
        p.Color = e.Style?.SvgLightingColor ?? 0xFFFFFFFFu;
        var source = e.Children.OfType<Element>().FirstOrDefault(c => c.Name is "fedistantlight" or "fepointlight" or "fespotlight");
        if (source == null) return; // no light: the result is black (diffuse) or transparent (specular)
        var light = new Light();
        float diagonal = MathF.Sqrt((box.W * box.W + box.H * box.H) / 2);
        (float X, float Y) Point(string nx, string ny)
        {
            float x = Number(source, nx, 0), y = Number(source, ny, 0);
            if (primitiveBox) { x = box.X + x * box.W; y = box.Y + y * box.H; }
            ctm.Apply(ref x, ref y);
            return (x, y);
        }
        float Depth(string name) => Number(source, name, 0) * (primitiveBox ? diagonal : 1) * ctm.ScaleFactor;
        switch (source.Name)
        {
            case "fedistantlight":
                light.Type = LightType.Distant;
                light.Azimuth = Number(source, "azimuth", 0) * MathF.PI / 180;
                light.Elevation = Number(source, "elevation", 0) * MathF.PI / 180;
                break;
            case "fepointlight":
                light.Type = LightType.Point;
                (light.X, light.Y) = Point("x", "y");
                light.Z = Depth("z");
                break;
            default:
                light.Type = LightType.Spot;
                (light.X, light.Y) = Point("x", "y");
                light.Z = Depth("z");
                (light.PointsAtX, light.PointsAtY) = Point("pointsatx", "pointsaty");
                light.PointsAtZ = Depth("pointsatz");
                light.SpotExponent = Number(source, "specularexponent", 1);
                if (source.GetAttribute("limitingconeangle") != null)
                    light.ConeCos = MathF.Cos(Math.Min(90, Math.Abs(Number(source, "limitingconeangle", 90))) * MathF.PI / 180);
                break;
        }
        p.Light = light;
    }

    // ------------------------------------------------------------------ feTurbulence

    /// <summary>
    /// Perlin turbulence as the SVG specification's reference code computes it, sampled at the pixel centres'
    /// coordinates in the filtered element's user space.
    /// </summary>
    private float[] Turbulence(Primitive p, int w, int h, float scaleX, float scaleY, int x0, int y0, int x1, int y1, CancellationToken cancel)
    {
        var dst = new float[w * h * 4];
        if (x1 <= x0 || y1 <= y0) return dst;
        var noise = new PerlinNoise(p.Seed);
        float freqX = p.FreqX, freqY = p.FreqY;
        float tileX = p.UX, tileY = p.UY, tileW = p.UW, tileH = p.UH;
        // Stitching adjusts the frequencies so that a whole number of periods fits the tile.
        if (p.Stitch && freqX > 0 && freqY > 0)
        {
            float lo = MathF.Floor(tileW * freqX) / tileW, hi = MathF.Ceiling(tileW * freqX) / tileW;
            freqX = lo > 0 && freqX / lo < hi / freqX ? lo : hi;
            lo = MathF.Floor(tileH * freqY) / tileH;
            hi = MathF.Ceiling(tileH * freqY) / tileH;
            freqY = lo > 0 && freqY / lo < hi / freqY ? lo : hi;
        }
        var toUser = ToUser;
        float originX = X, originY = Y;
        System.Threading.Tasks.Parallel.For(y0, y1, Lines((long)w * (y1 - y0), cancel), y =>
        {
            Span<float> point = stackalloc float[2];
            Span<float> c = stackalloc float[4];
            for (int x = x0; x < x1; x++)
            {
                float ux = originX + (x + 0.5f) / scaleX, uy = originY + (y + 0.5f) / scaleY;
                toUser.Apply(ref ux, ref uy);
                int i = (y * w + x) * 4;
                for (int channel = 0; channel < 4; channel++)
                {
                    point[0] = ux;
                    point[1] = uy;
                    float v = noise.Turbulence(channel, point, freqX, freqY, p.Octaves, p.Fractal, p.Stitch, tileX, tileY, tileW, tileH);
                    v = p.Fractal ? (v + 1) / 2 : v;
                    c[channel] = Math.Clamp(v, 0, 1);
                }
                float a = c[3];
                dst[i] = c[0] * a;
                dst[i + 1] = c[1] * a;
                dst[i + 2] = c[2] * a;
                dst[i + 3] = a;
            }
        });
        return dst;
    }

    /// <summary>The noise of the SVG 1.1 specification's feTurbulence reference implementation.</summary>
    private sealed class PerlinNoise
    {
        private const int BSize = 0x100, BM = 0xff, PerlinN = 0x1000;
        private const int RandM = 2147483647, RandA = 16807, RandQ = 127773, RandR = 2836;
        private readonly int[] _lattice = new int[BSize + BSize + 2];
        private readonly float[,,] _gradient = new float[4, BSize + BSize + 2, 2];

        public PerlinNoise(int seed)
        {
            long s = seed;
            if (s <= 0) s = -(s % (RandM - 1)) + 1;
            if (s > RandM - 1) s = RandM - 1;
            long Random()
            {
                long r = RandA * (s % RandQ) - RandR * (s / RandQ);
                if (r <= 0) r += RandM;
                return s = r;
            }
            for (int k = 0; k < 4; k++)
            {
                for (int i = 0; i < BSize; i++)
                {
                    _lattice[i] = i;
                    for (int j = 0; j < 2; j++) _gradient[k, i, j] = (float)((Random() % (BSize + BSize)) - BSize) / BSize;
                    double len = Math.Sqrt(_gradient[k, i, 0] * _gradient[k, i, 0] + _gradient[k, i, 1] * _gradient[k, i, 1]);
                    if (len > 0)
                    {
                        _gradient[k, i, 0] = (float)(_gradient[k, i, 0] / len);
                        _gradient[k, i, 1] = (float)(_gradient[k, i, 1] / len);
                    }
                }
            }
            for (int i = BSize - 1; i > 0; i--)
            {
                int k = _lattice[i];
                int j = (int)(Random() % BSize);
                _lattice[i] = _lattice[j];
                _lattice[j] = k;
            }
            for (int i = 0; i < BSize + 2; i++)
            {
                _lattice[BSize + i] = _lattice[i];
                for (int k = 0; k < 4; k++)
                    for (int j = 0; j < 2; j++) _gradient[k, BSize + i, j] = _gradient[k, i, j];
            }
        }

        private static float SCurve(float t) => t * t * (3 - 2 * t);
        private static float Lerp(float t, float a, float b) => a + t * (b - a);

        private float Noise2(int channel, float vx, float vy, bool stitch, int wrapX, int wrapY, int width, int height)
        {
            float t = vx + PerlinN;
            int bx0 = (int)t & BM, bx1 = (bx0 + 1) & BM;
            float rx0 = t - (int)t, rx1 = rx0 - 1;
            t = vy + PerlinN;
            int by0 = (int)t & BM, by1 = (by0 + 1) & BM;
            float ry0 = t - (int)t, ry1 = ry0 - 1;
            if (stitch)
            {
                if (bx0 >= wrapX) bx0 -= width;
                if (bx1 >= wrapX) bx1 -= width;
                if (by0 >= wrapY) by0 -= height;
                if (by1 >= wrapY) by1 -= height;
            }
            bx0 &= BM; bx1 &= BM; by0 &= BM; by1 &= BM;
            int i = _lattice[bx0], j = _lattice[bx1];
            int b00 = _lattice[i + by0], b10 = _lattice[j + by0], b01 = _lattice[i + by1], b11 = _lattice[j + by1];
            float sx = SCurve(rx0), sy = SCurve(ry0);
            float u = rx0 * _gradient[channel, b00, 0] + ry0 * _gradient[channel, b00, 1];
            float v = rx1 * _gradient[channel, b10, 0] + ry0 * _gradient[channel, b10, 1];
            float a = Lerp(sx, u, v);
            u = rx0 * _gradient[channel, b01, 0] + ry1 * _gradient[channel, b01, 1];
            v = rx1 * _gradient[channel, b11, 0] + ry1 * _gradient[channel, b11, 1];
            float b = Lerp(sx, u, v);
            return Lerp(sy, a, b);
        }

        public float Turbulence(int channel, Span<float> point, float freqX, float freqY, int octaves, bool fractal, bool stitch,
            float tileX, float tileY, float tileW, float tileH)
        {
            int width = 0, height = 0, wrapX = 0, wrapY = 0;
            if (stitch)
            {
                width = (int)(tileW * freqX + 0.5f);
                wrapX = (int)(tileX * freqX + PerlinN + width);
                height = (int)(tileH * freqY + 0.5f);
                wrapY = (int)(tileY * freqY + PerlinN + height);
            }
            float sum = 0, ratio = 1;
            float vx = point[0] * freqX, vy = point[1] * freqY;
            for (int n = 0; n < octaves; n++)
            {
                float noise = Noise2(channel, vx, vy, stitch, wrapX, wrapY, width, height);
                sum += fractal ? noise / ratio : MathF.Abs(noise) / ratio;
                vx *= 2;
                vy *= 2;
                ratio *= 2;
                if (stitch)
                {
                    // The tile doubles with the frequency.
                    width *= 2;
                    wrapX = 2 * wrapX - PerlinN;
                    height *= 2;
                    wrapY = 2 * wrapY - PerlinN;
                }
            }
            return sum;
        }
    }

    // ------------------------------------------------------------------ feTile

    /// <summary>The input's subregion repeated across the whole image (the caller clips it to the tile's subregion).</summary>
    private static float[] Tile(Image input, int w, int x0, int y0, int x1, int y1)
    {
        var src = input.P;
        var dst = new float[src.Length];
        int tw = input.X1 - input.X0, th = input.Y1 - input.Y0;
        if (tw <= 0 || th <= 0) return dst;
        for (int y = y0; y < y1; y++)
        {
            int sy = input.Y0 + Mod(y - input.Y0, th);
            for (int x = x0; x < x1; x++)
            {
                int sx = input.X0 + Mod(x - input.X0, tw);
                Array.Copy(src, (sy * w + sx) * 4, dst, (y * w + x) * 4, 4);
            }
        }
        return dst;
    }

    private static int Mod(int a, int n) => ((a % n) + n) % n;

    // ------------------------------------------------------------------ feConvolveMatrix

    private static float[] Convolve(float[] src, int w, int h, Primitive p, CancellationToken cancel)
    {
        var dst = new float[src.Length];
        if (p.Kernel is not { } kernel) return dst;
        int ox = p.OrderX, oy = p.OrderY;
        var edge = p.EdgeMode;
        System.Threading.Tasks.Parallel.For(0, h, Lines((long)w * h, cancel), y =>
        {
            Span<float> sum = stackalloc float[4];
            for (int x = 0; x < w; x++)
            {
                sum.Clear();
                for (int i = 0; i < oy; i++)
                {
                    int sy = y - p.TargetY + i;
                    for (int j = 0; j < ox; j++)
                    {
                        int sx = x - p.TargetX + j;
                        int px = sx, py = sy;
                        if (px < 0 || px >= w || py < 0 || py >= h)
                        {
                            if (edge == EdgeMode.None) continue; // transparent black outside
                            if (edge == EdgeMode.Wrap) { px = Mod(px, w); py = Mod(py, h); }
                            else { px = Math.Clamp(px, 0, w - 1); py = Math.Clamp(py, 0, h - 1); }
                        }
                        // The kernel is applied rotated by 180 degrees, as the specification's formula says.
                        float k = kernel[(oy - 1 - i) * ox + (ox - 1 - j)];
                        if (k == 0) continue;
                        int s = (py * w + px) * 4;
                        float a = src[s + 3];
                        if (p.PreserveAlpha)
                        {
                            if (a <= 0) continue;
                            sum[0] += src[s] / a * k; sum[1] += src[s + 1] / a * k; sum[2] += src[s + 2] / a * k;
                        }
                        else
                        {
                            sum[0] += src[s] * k; sum[1] += src[s + 1] * k; sum[2] += src[s + 2] * k; sum[3] += a * k;
                        }
                    }
                }
                int o = (y * w + x) * 4;
                if (p.PreserveAlpha)
                {
                    float a = src[o + 3];
                    for (int c = 0; c < 3; c++) dst[o + c] = Math.Clamp(sum[c] / p.Divisor + p.Bias, 0, 1) * a;
                    dst[o + 3] = a;
                }
                else
                {
                    float a = Math.Clamp(sum[3] / p.Divisor + p.Bias, 0, 1);
                    for (int c = 0; c < 3; c++) dst[o + c] = Math.Clamp(sum[c] / p.Divisor + p.Bias * a, 0, a);
                    dst[o + 3] = a;
                }
            }
        });
        return dst;
    }

    // ------------------------------------------------------------------ feDisplacementMap

    /// <summary>
    /// Moves each pixel of <paramref name="input"/> by the map's selected channels: by (value − 0.5) × scale, scale in
    /// pixels. The map's colour channels are read without premultiplication.
    /// </summary>
    private static Image Displace(Image input, Image map, int w, int h, float scaleX, float scaleY, Primitive p)
    {
        var src = input.P;
        var m = map.P;
        var dst = new float[src.Length];
        float Value(int i, ColorChannel channel)
        {
            float a = m[i + 3];
            if (channel == ColorChannel.A) return a;
            return a > 0 ? Math.Clamp(m[i + (int)channel] / a, 0, 1) : 0;
        }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                int sx = (int)MathF.Floor(x + 0.5f + scaleX * (Value(i, p.XChannel) - 0.5f));
                int sy = (int)MathF.Floor(y + 0.5f + scaleY * (Value(i, p.YChannel) - 0.5f));
                if (sx < 0 || sx >= w || sy < 0 || sy >= h) continue;
                Array.Copy(src, (sy * w + sx) * 4, dst, i, 4);
            }
        return new Image(dst, input.Linear);
    }

    // ------------------------------------------------------------------ lighting

    /// <summary>
    /// feDiffuseLighting or feSpecularLighting on the input's alpha as a height map, computed in the filtered
    /// element's user units so that the result does not depend on the filter's resolution.
    /// </summary>
    private float[] Lighting(float[] src, int w, int h, float scaleX, float scaleY, Primitive p, CancellationToken cancel)
    {
        var dst = new float[src.Length];
        bool diffuse = p.Kind == Kind.DiffuseLighting;
        var (lr, lg, lb) = Rgb(p.Color);
        if (p.Linear) { lr = Lookup(ToLinearTable, lr); lg = Lookup(ToLinearTable, lg); lb = Lookup(ToLinearTable, lb); }
        var light = p.Light;
        if (light == null)
        {
            if (diffuse) for (int i = 3; i < dst.Length; i += 4) dst[i] = 1;
            return dst;
        }
        float k = UserScale * scaleX; // pixels per user unit
        if (!(k > 0)) return dst;
        float ss = p.SurfaceScale;
        // The light's position and target in user units, measured from the filter region's corner.
        float lx = (light.X - X) * scaleX / k, ly = (light.Y - Y) * scaleY / k, lz = light.Z / UserScale;
        float sx = light.PointsAtX - light.X, sy = light.PointsAtY - light.Y, sz = light.PointsAtZ - light.Z;
        float sLen = MathF.Sqrt(sx * sx + sy * sy + sz * sz);
        if (sLen > 0) { sx /= sLen; sy /= sLen; sz /= sLen; }
        float dx = MathF.Cos(light.Azimuth) * MathF.Cos(light.Elevation), dy = MathF.Sin(light.Azimuth) * MathF.Cos(light.Elevation), dz = MathF.Sin(light.Elevation);
        float A(int x, int y) => src[(Math.Clamp(y, 0, h - 1) * w + Math.Clamp(x, 0, w - 1)) * 4 + 3];
        System.Threading.Tasks.Parallel.For(0, h, Lines((long)w * h, cancel), y =>
        {
            for (int x = 0; x < w; x++)
            {
                // The surface normal from Sobel gradients of the alpha, per user unit.
                float gx = A(x + 1, y - 1) + 2 * A(x + 1, y) + A(x + 1, y + 1) - A(x - 1, y - 1) - 2 * A(x - 1, y) - A(x - 1, y + 1);
                float gy = A(x - 1, y + 1) + 2 * A(x, y + 1) + A(x + 1, y + 1) - A(x - 1, y - 1) - 2 * A(x, y - 1) - A(x + 1, y - 1);
                float nx = -ss * k * gx / 4, ny = -ss * k * gy / 4, nz = 1;
                float nLen = MathF.Sqrt(nx * nx + ny * ny + 1);
                nx /= nLen; ny /= nLen; nz /= nLen;
                float surfaceZ = ss * A(x, y);
                float vx, vy, vz;
                if (light.Type == LightType.Distant) (vx, vy, vz) = (dx, dy, dz);
                else
                {
                    vx = lx - (x + 0.5f) / k;
                    vy = ly - (y + 0.5f) / k;
                    vz = lz - surfaceZ;
                    float len = MathF.Sqrt(vx * vx + vy * vy + vz * vz);
                    if (len > 0) { vx /= len; vy /= len; vz /= len; }
                }
                float cr = lr, cg = lg, cb = lb;
                if (light.Type == LightType.Spot)
                {
                    float minusLDotS = -(vx * sx + vy * sy + vz * sz);
                    float spot = minusLDotS <= 0 || (!float.IsNaN(light.ConeCos) && minusLDotS < light.ConeCos) ? 0 : MathF.Pow(minusLDotS, light.SpotExponent);
                    cr *= spot; cg *= spot; cb *= spot;
                }
                int o = (y * w + x) * 4;
                if (diffuse)
                {
                    float f = p.Constant * Math.Max(0, nx * vx + ny * vy + nz * vz);
                    dst[o] = Math.Clamp(f * cr, 0, 1);
                    dst[o + 1] = Math.Clamp(f * cg, 0, 1);
                    dst[o + 2] = Math.Clamp(f * cb, 0, 1);
                    dst[o + 3] = 1;
                }
                else
                {
                    float hx = vx, hy = vy, hz = vz + 1;
                    float hLen = MathF.Sqrt(hx * hx + hy * hy + hz * hz);
                    float nDotH = hLen > 0 ? (nx * hx + ny * hy + nz * hz) / hLen : 0;
                    float f = p.Constant * MathF.Pow(Math.Max(0, nDotH), p.SpecularExponent);
                    float r = Math.Clamp(f * cr, 0, 1), g = Math.Clamp(f * cg, 0, 1), b = Math.Clamp(f * cb, 0, 1);
                    // The alpha is the brightest channel; the colour is taken as already premultiplied by it.
                    dst[o] = r; dst[o + 1] = g; dst[o + 2] = b;
                    dst[o + 3] = Math.Max(r, Math.Max(g, b));
                }
            }
        });
        return dst;
    }
}
