using System.Runtime.CompilerServices;

namespace PdfPecker.Rendering.Graphics;

internal sealed partial class Painter
{
    /// <summary>Composites a run of coverage values onto the canvas row <paramref name="y"/>, starting at pixel <paramref name="x0"/>.</summary>
    private void Composite(int y, int x0, ReadOnlySpan<byte> coverage)
    {
        int count = coverage.Length;
        var pixels = Canvas.Pixels.AsSpan(y * Canvas.Width + x0, count);
        var clip = Clip;
        ReadOnlySpan<byte> clipMask = clip.Mask != null
            ? clip.Mask.AsSpan((y - clip.MaskY0) * clip.MaskStride + (x0 - clip.MaskX0), count)
            : default;
        ReadOnlySpan<byte> softMask = SoftMask != null ? SoftMask.AsSpan(y * Canvas.Width + x0, count) : default;

        Span<uint> shaded = default;
        if (Shader != null)
        {
            if (_colors.Length < count) _colors = new uint[Math.Max(count, _colors.Length * 2)];
            shaded = _colors.AsSpan(0, count);
            Shader.Shade(y, x0, shaded);
        }

        if (Blend != BlendMode.Normal) CompositeBlended(pixels, coverage, clipMask, softMask, shaded);
        else if (clipMask.IsEmpty && softMask.IsEmpty && Alpha == FullCoverage)
        {
            if (shaded.IsEmpty && Color >> 24 == 255) CompositeOpaqueSolid(pixels, coverage);
            else CompositeUnmasked(pixels, coverage, shaded);
        }
        else CompositeMasked(pixels, coverage, clipMask, softMask, shaded);
    }

    /// <summary>The coverage of pixel <paramref name="i"/> after the clip mask, the soft mask and the constant alpha.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int EffectiveCoverage(ReadOnlySpan<byte> coverage, ReadOnlySpan<byte> clipMask, ReadOnlySpan<byte> softMask, int alpha, int i)
    {
        int c = coverage[i];
        if (!clipMask.IsEmpty) c = Multiply(c, clipMask[i]);
        if (!softMask.IsEmpty) c = Multiply(c, softMask[i]);
        if (alpha != FullCoverage) c = Multiply(c, alpha);
        return c;
    }

    /// <summary>The product of two values of 0 to 255, divided by 255 with a multiply and a shift (the quotient is rounded down).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Multiply(int a, int b) => a * b * 257 >> 16;

    /// <summary>A coverage of 0 to 255 as a factor of 0 to 256, so that full coverage leaves a color unchanged.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ToScaleFactor(int coverage) => (uint)(coverage + (coverage >> 7));

    private void CompositeBlended(Span<uint> pixels, ReadOnlySpan<byte> coverage, ReadOnlySpan<byte> clipMask, ReadOnlySpan<byte> softMask, ReadOnlySpan<uint> shaded)
    {
        uint solid = Color;
        int alpha = Alpha;
        bool hasShader = !shaded.IsEmpty;
        var mode = Blend;
        for (int i = 0; i < pixels.Length; i++)
        {
            int c = EffectiveCoverage(coverage, clipMask, softMask, alpha, i);
            if (c == 0) continue;
            uint source = hasShader ? shaded[i] : solid;
            pixels[i] = BlendPixel(Canvas.Scale(source, ToScaleFactor(c)), pixels[i], mode);
        }
    }

    /// <summary>The common case of opaque solid paint with nothing but the shape's own coverage.</summary>
    private void CompositeOpaqueSolid(Span<uint> pixels, ReadOnlySpan<byte> coverage)
    {
        uint solid = Color;
        for (int i = 0; i < pixels.Length; i++)
        {
            int c = coverage[i];
            if (c == 0) continue;
            if (c == FullCoverage) { pixels[i] = solid; continue; }
            pixels[i] = Canvas.Mul255(solid, (uint)c) + Canvas.Mul255(pixels[i], FullCoverage - (uint)c);
        }
    }

    private void CompositeUnmasked(Span<uint> pixels, ReadOnlySpan<byte> coverage, ReadOnlySpan<uint> shaded)
    {
        uint solid = Color;
        bool hasShader = !shaded.IsEmpty;
        for (int i = 0; i < pixels.Length; i++)
        {
            int c = coverage[i];
            if (c == 0) continue;
            uint source = hasShader ? shaded[i] : solid;
            if (c != FullCoverage) source = Canvas.Scale(source, ToScaleFactor(c));
            pixels[i] = Canvas.Over(source, pixels[i]);
        }
    }

    private void CompositeMasked(Span<uint> pixels, ReadOnlySpan<byte> coverage, ReadOnlySpan<byte> clipMask, ReadOnlySpan<byte> softMask, ReadOnlySpan<uint> shaded)
    {
        uint solid = Color;
        int alpha = Alpha;
        bool hasShader = !shaded.IsEmpty;
        for (int i = 0; i < pixels.Length; i++)
        {
            int c = EffectiveCoverage(coverage, clipMask, softMask, alpha, i);
            if (c == 0) continue;
            uint source = hasShader ? shaded[i] : solid;
            if (c != FullCoverage) source = Canvas.Scale(source, ToScaleFactor(c));
            pixels[i] = Canvas.Over(source, pixels[i]);
        }
    }
}
