using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering.Images;

// The decoded components as an image: through the palette and channel definitions of the JP2 header, in the colour
// space it names, with sYCC converted to RGB.
internal sealed partial class JpxDecoder
{
    // Enumerated colour spaces of the colr box (ISO/IEC 15444-2, table M.25).
    private const int EnumeratedCmyk = 12, EnumeratedSrgb = 16, EnumeratedGray = 17, EnumeratedSycc = 18, EnumeratedRommRgb = 21;

    // Channel types and associations of the cdef box: an association of 0 is the whole image, and 65535 none.
    private const int ChannelColour = 0, ChannelOpacity = 1, ChannelPremultipliedOpacity = 2;
    private const int AssociationImage = 0, AssociationNone = 65535;
    private const int UnassociatedColourOrder = 1000; // unassociated colour channels follow the associated ones
    private const int PaletteMapping = 1;

    private JpxImage? Compose(Jp2Header? header)
    {
        // When every component is subsampled alike, the image is the components' own size (as other readers show it).
        bool uniform = true;
        for (int c = 1; c < _components.Length; c++)
            uniform &= _components[c].SubsamplingX == _components[0].SubsamplingX && _components[c].SubsamplingY == _components[0].SubsamplingY;
        int width = uniform ? _planes[0].Width : _imageX1 - _imageX0, height = uniform ? _planes[0].Height : _imageY1 - _imageY0;
        if (width <= 0 || height <= 0) return null;

        var channels = Channels(header, width, height);
        if (channels == null) return null;
        var (color, alpha) = ColourAndAlpha(header, channels);
        if (header?.EnumeratedColourSpace == EnumeratedSycc && color.Count == 3) YccToRgb(color[0], color[1], color[2]);

        int n = color.Count;
        var data = new byte[(long)width * height * n];
        for (int c = 0; c < n; c++)
        {
            var src = color[c];
            for (int i = 0, o = c; i < src.Length; i++, o += n) data[o] = src[i];
        }
        var kind = n switch { 1 => JpxColor.Gray, 3 => JpxColor.Rgb, 4 => JpxColor.Cmyk, _ => JpxColor.Unknown };
        return new JpxImage(width, height, n, data, alpha, kind);
    }

    /// <summary>
    /// The image's channels at full size: the components, or what the component mapping makes of them with the palette.
    /// Returns null when the mapping names a component the codestream does not have.
    /// </summary>
    private List<byte[]>? Channels(Jp2Header? header, int width, int height)
    {
        var channels = new List<byte[]>();
        if (header?.Palette is { } palette && header.ComponentMap is { } map)
        {
            foreach (var (component, type, column) in map)
            {
                if (component >= _components.Length) return null;
                var full = Upsample(component, width, height);
                channels.Add(type == PaletteMapping && column < palette.ColumnBits.Length ? MapThroughPalette(full, palette, column, _components[component].Precision) : full);
            }
        }
        else
        {
            for (int c = 0; c < _components.Length; c++) channels.Add(Upsample(c, width, height));
        }
        return channels;
    }

    private static byte[] MapThroughPalette(byte[] indexes, Jp2Palette palette, int column, int precision)
    {
        var entries = palette.Entries;
        int bits = palette.ColumnBits[column];
        var mapped = new byte[indexes.Length];
        for (int i = 0; i < indexes.Length; i++)
        {
            // Planes hold 8-bit values; recover the index at its original precision, in floating point because the
            // product overflows an int from 24 bits on.
            int index = precision == 8 ? indexes[i] : (int)Math.Round(indexes[i] * (double)((1 << precision) - 1) / 255.0);
            long v = index < entries.Length ? entries[index][column] : 0;
            // In 64 bits, because a column may be up to 38 bits deep.
            mapped[i] = bits == 8 ? (byte)v : (byte)(v * 255 / ((1L << bits) - 1));
        }
        return mapped;
    }

    /// <summary>
    /// Splits the channels into colour and opacity, by the channel definitions or else by how many colour components the
    /// colour space has.
    /// </summary>
    private static (List<byte[]> Color, byte[]? Alpha) ColourAndAlpha(Jp2Header? header, List<byte[]> channels)
    {
        byte[]? alpha = null;
        var color = new List<byte[]>();
        if (header?.ChannelDefinitions is { Length: > 0 } definitions)
        {
            // Colour channels in the order of the colours they stand for, and the first opacity channel.
            var ordered = new SortedDictionary<int, byte[]>();
            foreach (var (channel, type, association) in definitions)
            {
                if (channel >= channels.Count) continue;
                if (type is ChannelOpacity or ChannelPremultipliedOpacity) alpha ??= channels[channel];
                else if (type == ChannelColour)
                    ordered[association is AssociationImage or AssociationNone ? UnassociatedColourOrder + channel : association] = channels[channel];
            }
            color.AddRange(ordered.Values);
            if (color.Count == 0 && channels.Count > 0) color.Add(channels[0]);
        }
        else color.AddRange(channels);

        int expected = header?.EnumeratedColourSpace switch
        {
            EnumeratedSrgb or EnumeratedSycc or EnumeratedRommRgb => 3,
            EnumeratedGray => 1,
            EnumeratedCmyk => 4,
            _ => header?.IccComponents ?? 0,
        };
        if (expected == 0) expected = color.Count >= 4 ? 4 : color.Count >= 3 ? 3 : 1;
        if (color.Count > expected)
        {
            // Without channel definitions, one channel more than the colour space has is its opacity.
            if (alpha == null && header?.ChannelDefinitions == null && color.Count == expected + 1) alpha = color[expected];
            color.RemoveRange(expected, color.Count - expected);
        }
        if (color.Count == 2)
        {
            alpha ??= color[1];
            color.RemoveAt(1);
        }
        return (color, alpha);
    }

    /// <summary>A component at full image resolution (subsampled components are repeated).</summary>
    private byte[] Upsample(int c, int width, int height)
    {
        var component = _components[c];
        var plane = _planes[c];
        if (plane.Width == width && plane.Height == height) return plane.Samples;
        PdfFile.ChargeDecoded((long)width * height);
        var o = new byte[width * height];
        int pw = plane.Width, ph = plane.Height;
        if (pw <= 0 || ph <= 0) return o;
        var samples = plane.Samples;
        var xs = new int[width];
        for (int x = 0; x < width; x++) xs[x] = Math.Clamp((_imageX0 + x) / component.SubsamplingX - plane.X0, 0, pw - 1);
        for (int y = 0; y < height; y++)
        {
            int sy = Math.Clamp((_imageY0 + y) / component.SubsamplingY - plane.Y0, 0, ph - 1);
            int srow = sy * pw, orow = y * width;
            for (int x = 0; x < width; x++) o[orow + x] = samples[srow + xs[x]];
        }
        return o;
    }

    private static void YccToRgb(byte[] y, byte[] cb, byte[] cr)
    {
        for (int i = 0; i < y.Length; i++)
        {
            float yy = y[i], u = cb[i] - 128, v = cr[i] - 128;
            y[i] = Clamp8(yy + 1.402f * v);
            cb[i] = Clamp8(yy - 0.344136f * u - 0.714136f * v);
            cr[i] = Clamp8(yy + 1.772f * u);
        }
    }

    private static byte Clamp8(float v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5f);
}
