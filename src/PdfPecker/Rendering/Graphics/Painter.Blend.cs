namespace PdfPecker.Rendering.Graphics;

// The PDF blend functions of ISO 32000-1, section 11.3.5, on premultiplied pixels in memory order R, G, B, A.
internal sealed partial class Painter
{
    private const int RedShift = 0;
    private const int GreenShift = 8;
    private const int BlueShift = 16;
    private const int AlphaShift = 24;

    /// <summary>Composites the premultiplied source <paramref name="s"/> over the backdrop <paramref name="d"/> using a blend mode.</summary>
    private static uint BlendPixel(uint s, uint d, BlendMode mode)
    {
        float sourceAlpha = Channel(s, AlphaShift);
        if (sourceAlpha <= 0) return d;
        float backdropAlpha = Channel(d, AlphaShift);
        float sr = Channel(s, RedShift), sg = Channel(s, GreenShift), sb = Channel(s, BlueShift);
        float dr = Channel(d, RedShift), dg = Channel(d, GreenShift), db = Channel(d, BlueShift);
        var (resultR, resultG, resultB) = BlendColor(mode, sourceAlpha, sr, sg, sb, backdropAlpha, dr, dg, db);
        float resultAlpha = sourceAlpha + backdropAlpha - sourceAlpha * backdropAlpha;
        return Pack(resultR, resultG, resultB, resultAlpha);
    }

    /// <summary>
    /// Composites the premultiplied source <paramref name="s"/> with a blend mode onto <paramref name="p"/>, a pixel of
    /// the layer of a non-isolated group, which holds only what the group drew. The blend function sees what the group
    /// shows there so far, the layer over <paramref name="below"/>, which is what shows through the layer from its
    /// backdrop. The result is again only what the group adds: the backdrop's share is taken out as the group's result
    /// is when it ends (ISO 32000-2, 11.4.8), so that the group can be composited like an isolated one.
    /// </summary>
    private static uint BlendPixelNonIsolated(uint s, uint p, uint below, BlendMode mode)
    {
        float sourceAlpha = Channel(s, AlphaShift);
        if (sourceAlpha <= 0) return p;
        float layerAlpha = Channel(p, AlphaShift), belowAlpha = Channel(below, AlphaShift);
        float sr = Channel(s, RedShift), sg = Channel(s, GreenShift), sb = Channel(s, BlueShift);
        float br = Channel(below, RedShift), bg = Channel(below, GreenShift), bb = Channel(below, BlueShift);
        float showsThrough = 1 - layerAlpha;
        float shownAlpha = layerAlpha + belowAlpha * showsThrough;
        float shownR = Channel(p, RedShift) + br * showsThrough;
        float shownG = Channel(p, GreenShift) + bg * showsThrough;
        float shownB = Channel(p, BlueShift) + bb * showsThrough;
        var (resultR, resultG, resultB) = BlendColor(mode, sourceAlpha, sr, sg, sb, shownAlpha, shownR, shownG, shownB);
        // The group's own alpha, which leaves the backdrop out, and the part of the backdrop the result still shows.
        float groupAlpha = sourceAlpha + layerAlpha - sourceAlpha * layerAlpha;
        float backdropShare = 1 - groupAlpha;
        return Pack(Math.Clamp(resultR - br * backdropShare, 0, groupAlpha), Math.Clamp(resultG - bg * backdropShare, 0, groupAlpha),
            Math.Clamp(resultB - bb * backdropShare, 0, groupAlpha), groupAlpha);
    }

    /// <summary>
    /// The premultiplied color of a premultiplied source composited with a blend mode onto a premultiplied backdrop by
    /// the basic compositing formula, with channels and alphas from 0 to 1.
    /// </summary>
    private static (float R, float G, float B) BlendColor(BlendMode mode, float sourceAlpha, float sr, float sg, float sb,
        float backdropAlpha, float dr, float dg, float db)
    {
        // The blend functions work on straight colors.
        float csr = sr / sourceAlpha, csg = sg / sourceAlpha, csb = sb / sourceAlpha;
        float cbr = Unpremultiply(dr, backdropAlpha), cbg = Unpremultiply(dg, backdropAlpha), cbb = Unpremultiply(db, backdropAlpha);
        float br, bg, bb;
        if (mode >= BlendMode.Hue)
        {
            var blended = NonSeparable(mode, new Rgb(cbr, cbg, cbb), new Rgb(csr, csg, csb));
            (br, bg, bb) = (blended.R, blended.G, blended.B);
        }
        else
        {
            br = Separable(mode, cbr, csr);
            bg = Separable(mode, cbg, csg);
            bb = Separable(mode, cbb, csb);
        }
        return (sr * (1 - backdropAlpha) + dr * (1 - sourceAlpha) + sourceAlpha * backdropAlpha * br,
            sg * (1 - backdropAlpha) + dg * (1 - sourceAlpha) + sourceAlpha * backdropAlpha * bg,
            sb * (1 - backdropAlpha) + db * (1 - sourceAlpha) + sourceAlpha * backdropAlpha * bb);
    }

    private static uint Pack(float r, float g, float b, float alpha)
        => (uint)(ToByte(r) << RedShift | ToByte(g) << GreenShift | ToByte(b) << BlueShift | ToByte(alpha) << AlphaShift);

    private static float Channel(uint pixel, int shift) => ((pixel >> shift) & 255) / 255f;

    private static float Unpremultiply(float channel, float alpha) => alpha > 0 ? channel / alpha : 0;

    private static int ToByte(float value) => (int)(Math.Clamp(value, 0, 1) * 255 + 0.5f);

    private static float Separable(BlendMode mode, float cb, float cs) => mode switch
    {
        BlendMode.Multiply => cb * cs,
        BlendMode.Screen => Screen(cb, cs),
        BlendMode.Overlay => HardLight(cs, cb),
        BlendMode.Darken => MathF.Min(cb, cs),
        BlendMode.Lighten => MathF.Max(cb, cs),
        BlendMode.ColorDodge => ColorDodge(cb, cs),
        BlendMode.ColorBurn => ColorBurn(cb, cs),
        BlendMode.HardLight => HardLight(cb, cs),
        BlendMode.SoftLight => SoftLight(cb, cs),
        BlendMode.Difference => MathF.Abs(cb - cs),
        BlendMode.Exclusion => cb + cs - 2 * cb * cs,
        _ => cs,
    };

    private static float Screen(float cb, float cs) => cb + cs - cb * cs;

    private static float ColorDodge(float cb, float cs) => cb == 0 ? 0 : cs >= 1 ? 1 : MathF.Min(1, cb / (1 - cs));

    private static float ColorBurn(float cb, float cs) => cb >= 1 ? 1 : cs <= 0 ? 0 : 1 - MathF.Min(1, (1 - cb) / cs);

    private static float HardLight(float cb, float cs) => cs <= 0.5f ? cb * 2 * cs : Screen(cb, 2 * cs - 1);

    private static float SoftLight(float cb, float cs)
    {
        if (cs <= 0.5f) return cb - (1 - 2 * cs) * cb * (1 - cb);
        float d = cb <= 0.25f ? ((16 * cb - 12) * cb + 4) * cb : MathF.Sqrt(cb);
        return cb + (2 * cs - 1) * (d - cb);
    }

    // ---- Non-separable modes, which blend the hue, saturation and luminosity of whole colors ------------------------

    private readonly record struct Rgb(float R, float G, float B);

    private static Rgb NonSeparable(BlendMode mode, Rgb backdrop, Rgb source)
    {
        switch (mode)
        {
            case BlendMode.Hue:
                return SetLuminosity(SetSaturation(source, Saturation(backdrop)), Luminosity(backdrop));
            case BlendMode.Saturation:
                return SetLuminosity(SetSaturation(backdrop, Saturation(source)), Luminosity(backdrop));
            case BlendMode.Color:
                return SetLuminosity(source, Luminosity(backdrop));
            default:
                return SetLuminosity(backdrop, Luminosity(source));
        }
    }

    private static float Luminosity(Rgb c) => 0.3f * c.R + 0.59f * c.G + 0.11f * c.B;

    private static float Saturation(Rgb c) => MathF.Max(c.R, MathF.Max(c.G, c.B)) - MathF.Min(c.R, MathF.Min(c.G, c.B));

    /// <summary>Moves a color to the given luminosity, then pulls it back into the 0 to 1 range keeping that luminosity.</summary>
    private static Rgb SetLuminosity(Rgb c, float luminosity)
    {
        float delta = luminosity - Luminosity(c);
        return ClipColor(new Rgb(c.R + delta, c.G + delta, c.B + delta));
    }

    private static Rgb ClipColor(Rgb c)
    {
        float r = c.R, g = c.G, b = c.B;
        float l = Luminosity(c), min = MathF.Min(r, MathF.Min(g, b)), max = MathF.Max(r, MathF.Max(g, b));
        if (min < 0)
        {
            r = l + (r - l) * l / (l - min);
            g = l + (g - l) * l / (l - min);
            b = l + (b - l) * l / (l - min);
        }
        if (max > 1)
        {
            r = l + (r - l) * (1 - l) / (max - l);
            g = l + (g - l) * (1 - l) / (max - l);
            b = l + (b - l) * (1 - l) / (max - l);
        }
        return new Rgb(r, g, b);
    }

    /// <summary>Scales the spread between the smallest and largest channel to <paramref name="saturation"/>; a gray stays black.</summary>
    private static Rgb SetSaturation(Rgb c, float saturation)
    {
        float max = MathF.Max(c.R, MathF.Max(c.G, c.B)), min = MathF.Min(c.R, MathF.Min(c.G, c.B));
        if (max <= min) return new Rgb(0, 0, 0);
        return new Rgb(Stretch(c.R), Stretch(c.G), Stretch(c.B));

        float Stretch(float channel) => (channel - min) * saturation / (max - min);
    }
}
