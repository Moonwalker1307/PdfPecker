using PdfPecker.Rendering;
using PdfPecker.Rendering.Graphics;
using PdfPecker.Rendering.Images;

namespace PdfPecker.Tests;

/// <summary>The bilinear sampling of an upright image, whose rows run four pixels at a time where vectors are fast.</summary>
public class ImageShaderTests
{
    [Theory]
    [InlineData(7, 5, 13.0, 9.0, 0.0, 0.0)]
    [InlineData(40, 30, 40.0, 30.0, 0.3, 0.7)]
    [InlineData(40, 30, 123.4, 77.7, 2.25, 1.5)]
    [InlineData(3, 2, 101.0, 3.0, 0.0, 0.0)]
    public void Each_pixel_is_the_bilinear_blend_of_its_four_samples(int w, int h, double deviceWidth, double deviceHeight, double x, double y)
    {
        var random = new Random(w * 7 + h);
        var pixels = new uint[w * h];
        for (int i = 0; i < pixels.Length; i++)
        {
            // Premultiplied pixels, some equal to their neighbours, as flat areas of a scan are.
            uint a = (uint)random.Next(4) == 0 ? (uint)random.Next(256) : 255;
            uint r = (uint)random.Next((int)a + 1), g = (uint)random.Next((int)a + 1), b = (uint)random.Next((int)a + 1);
            pixels[i] = i > 0 && random.Next(3) == 0 ? pixels[i - 1] : a << 24 | b << 16 | g << 8 | r;
        }
        var image = new DecodedImage { Width = w, Height = h, Pixels = pixels };
        // The unit square onto the device rectangle, upright, as an image is drawn.
        var ctm = new Matrix(deviceWidth, 0, 0, -deviceHeight, x, y + deviceHeight);
        Assert.True(ctm.TryInvert(out var inverse));
        var shader = new ImageShader(image, inverse, nearest: false, stencil: false, color: 0, inner: null);

        int width = (int)Math.Ceiling(deviceWidth + x);
        for (int row = 0; row < (int)Math.Ceiling(deviceHeight + y); row++)
        {
            foreach (int start in new[] { 0, 1 })
            {
                var colors = new uint[width - start];
                shader.Shade(row, start, colors);
                // The image coordinates step along the row as the shader steps them, so they round the same way.
                double fx = start + 0.5, fy = row + 0.5;
                double u = inverse.A * w * fx + inverse.C * w * fy + inverse.E * w, v = -inverse.B * h * fx + -inverse.D * h * fy + (1 - inverse.F) * h;
                for (int i = 0; i < colors.Length; i++, u += inverse.A * w)
                    Assert.Equal(Expected(pixels, w, h, u, v), colors[i]);
            }
        }
    }

    /// <summary>The bilinear sample at image coordinates (<paramref name="u"/>, <paramref name="v"/>), channel by channel.</summary>
    private static uint Expected(uint[] px, int w, int h, double u, double v)
    {
        double su = u - 0.5, sv = v - 0.5;
        int iu = (int)Math.Floor(su), iv = (int)Math.Floor(sv);
        int wu = (int)((su - iu) * 256), wv = (int)((sv - iv) * 256);
        int u0 = Math.Clamp(iu, 0, w - 1), u1 = Math.Clamp(iu + 1, 0, w - 1), v0 = Math.Clamp(iv, 0, h - 1), v1 = Math.Clamp(iv + 1, 0, h - 1);
        uint result = 0;
        for (int shift = 0; shift < 32; shift += 8)
        {
            int Channel(uint p) => (int)(p >> shift) & 255;
            int upper = (Channel(px[v0 * w + u0]) * (256 - wu) + Channel(px[v0 * w + u1]) * wu) >> 8;
            int lower = (Channel(px[v1 * w + u0]) * (256 - wu) + Channel(px[v1 * w + u1]) * wu) >> 8;
            result |= (uint)((upper * (256 - wv) + lower * wv) >> 8) << shift;
        }
        return result;
    }
}
