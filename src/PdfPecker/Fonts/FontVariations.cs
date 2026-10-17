using System.Buffers.Binary;

namespace PdfPecker.Fonts;

/// <summary>
/// Makes static instances of variable fonts: the outlines, advance widths and kerning at one point of the design
/// space, as a plain font the rest of the library embeds and measures like any other. PDF has no variable fonts, so
/// this is what a browser's print output does too. TrueType outlines get their gvar deltas; CFF2 outlines become a
/// static CFF table (see <see cref="Cff2Instancer"/>).
/// </summary>
internal static partial class FontVariations
{
    public const uint Wght = 0x77676874, Wdth = 0x77647468, Ital = 0x6974616C, Slnt = 0x736C6E74, Opsz = 0x6F70737A;

    private const uint TrueTypeVersion = 0x00010000, OpenTypeVersion = 0x4F54544F; // 'OTTO'
    private const uint TagCff = 0x43464620, TagGlyf = 0x676C7966, TagLoca = 0x6C6F6361, TagHmtx = 0x686D7478;
    private const uint TagGpos = 0x47504F53, TagHead = 0x68656164, TagHhea = 0x68686561, TagOs2 = 0x4F532F32;

    // Tables that describe variations or hinting of the default instance: an instance has no use for them.
    private static readonly HashSet<uint> Dropped = new[]
    {
        "fvar", "gvar", "avar", "CFF2", "HVAR", "VVAR", "MVAR", "STAT", "cvar", "fpgm", "prep", "cvt ", "hdmx", "LTSH", "VDMX", "DSIG", "gasp",
    }.Select(FontProgram.Tag).ToHashSet();

    // head: the font's bounding box and the loca format; an instance's loca always has long offsets.
    private const int HeadMinimumLength = 54;
    private const int HeadXMinField = 36, HeadYMinField = 38, HeadXMaxField = 40, HeadYMaxField = 42, HeadIndexToLocFormatField = 50;
    private const short LongLocaFormat = 1;

    // hhea: the largest advance, the smallest left side bearing and the number of full metrics in hmtx.
    private const int HheaMinimumLength = 36;
    private const int HheaAdvanceWidthMaxField = 10, HheaMinLeftSideBearingField = 12, HheaNumberOfHMetricsField = 34;

    // OS/2: the weight class (1 to 1000) and the width class (1 to 9).
    private const int Os2MinimumLength = 6;
    private const int Os2WeightClassField = 4, Os2WidthClassField = 6;
    private const int MinWeightClass = 1, MaxWeightClass = 1000;

    // hmtx: an advance width and a left side bearing for every glyph.
    private const int MetricLength = 4, MetricBearingField = 2;

    /// <summary>True when the font's outlines can be instanced: glyf outlines with gvar variations, or CFF2 outlines.</summary>
    public static bool CanInstance(FontProgram p)
        => p.Table("fvar") != null
            && (p.Table("CFF2") != null || !p.IsCff && p.Table("gvar") != null && p.Table("glyf") != null && p.Table("loca") != null);

    /// <summary>The outlines of an instance: the tables that replace the variable font's, and each glyph's metrics.</summary>
    private sealed class InstanceOutlines(int[] advances, short[] lsbs, (short XMin, short YMin, short XMax, short YMax)?[] boxes)
    {
        public int[] Advances { get; } = advances;
        public short[] Lsbs { get; } = lsbs;

        /// <summary>The bounding box of each glyph; none for a glyph without contours.</summary>
        public (short XMin, short YMin, short XMax, short YMax)?[] Boxes { get; } = boxes;

        public Dictionary<uint, byte[]> Tables { get; } = new();
        public uint SfntVersion { get; init; } = TrueTypeVersion;
    }

    /// <summary>A static font at the normalized coordinates, as sfnt bytes.</summary>
    public static byte[] Instance(FontProgram p, float[] coords, IReadOnlyDictionary<uint, float> userValues, VariationAxis[] axes)
    {
        var outlines = p.Table("CFF2") != null ? InstanceCff2(p, coords) : InstanceGlyf(p, coords);
        var gdefStore = VariationStore.FromGdef(p, coords);
        var metrics = new FontMetrics(outlines, p.NumGlyphs);

        var tables = new List<(uint Tag, byte[] Data)>();
        foreach (var (tag, offset, length) in p.Tables)
        {
            if (Dropped.Contains(tag)) continue;
            var data = p.Data.AsSpan(offset, length).ToArray();
            if (outlines.Tables.Remove(tag, out var instanced)) data = instanced;
            switch (tag)
            {
                case TagHmtx:
                    data = metrics.Hmtx;
                    break;
                case TagGpos when gdefStore != null:
                    // The kerning and anchors of the instance.
                    data = GposVariations.Instance(data, gdefStore);
                    break;
                case TagHead when data.Length >= HeadMinimumLength:
                    PatchHead(data, metrics, outlines.SfntVersion);
                    break;
                case TagHhea when data.Length >= HheaMinimumLength:
                    PatchHhea(data, metrics, p.NumGlyphs);
                    break;
                case TagOs2 when data.Length >= Os2MinimumLength:
                    PatchOs2(data, userValues, axes);
                    break;
            }
            tables.Add((tag, data));
        }
        foreach (var (tag, data) in outlines.Tables) tables.Add((tag, data));
        return SfntBuilder.Build(outlines.SfntVersion, tables);
    }

    private static InstanceOutlines InstanceCff2(FontProgram p, float[] coords)
    {
        var cff = Cff2Instancer.Instance(p, coords, p.PostScriptName);
        var outlines = new InstanceOutlines(cff.Advances, cff.Lsbs, cff.Boxes) { SfntVersion = OpenTypeVersion };
        outlines.Tables[TagCff] = cff.Cff;
        return outlines;
    }

    /// <summary>The instance's hmtx table, with a full metric for every glyph, and the extremes that head and hhea record.</summary>
    private sealed class FontMetrics
    {
        public readonly byte[] Hmtx;
        public readonly int MaxAdvance;

        // Only glyphs with contours count towards the smallest bearing and the font's bounding box.
        public readonly short MinLsb = short.MaxValue;
        public readonly short XMin = short.MaxValue, YMin = short.MaxValue, XMax = short.MinValue, YMax = short.MinValue;

        public FontMetrics(InstanceOutlines outlines, int glyphCount)
        {
            Hmtx = new byte[glyphCount * MetricLength];
            for (int g = 0; g < glyphCount; g++)
            {
                int advance = outlines.Advances[g];
                short lsb = outlines.Lsbs[g];
                var metric = Hmtx.AsSpan(g * MetricLength);
                BinaryPrimitives.WriteUInt16BigEndian(metric, (ushort)Math.Clamp(advance, 0, ushort.MaxValue));
                BinaryPrimitives.WriteInt16BigEndian(metric[MetricBearingField..], lsb);
                MaxAdvance = Math.Max(MaxAdvance, advance);
                if (outlines.Boxes[g] is not { } box) continue;
                MinLsb = Math.Min(MinLsb, lsb);
                XMin = Math.Min(XMin, box.XMin);
                YMin = Math.Min(YMin, box.YMin);
                XMax = Math.Max(XMax, box.XMax);
                YMax = Math.Max(YMax, box.YMax);
            }
        }

        public bool HasBoundingBox => XMin <= XMax;
    }

    // A TrueType instance's loca has long offsets; the bounding box is the instance's own when any glyph has contours.
    private static void PatchHead(byte[] head, FontMetrics metrics, uint sfntVersion)
    {
        var span = head.AsSpan();
        if (sfntVersion != OpenTypeVersion) BinaryPrimitives.WriteInt16BigEndian(span[HeadIndexToLocFormatField..], LongLocaFormat);
        if (!metrics.HasBoundingBox) return;
        BinaryPrimitives.WriteInt16BigEndian(span[HeadXMinField..], metrics.XMin);
        BinaryPrimitives.WriteInt16BigEndian(span[HeadYMinField..], metrics.YMin);
        BinaryPrimitives.WriteInt16BigEndian(span[HeadXMaxField..], metrics.XMax);
        BinaryPrimitives.WriteInt16BigEndian(span[HeadYMaxField..], metrics.YMax);
    }

    // Every glyph of the instance has a metric of its own.
    private static void PatchHhea(byte[] hhea, FontMetrics metrics, int glyphCount)
    {
        var span = hhea.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span[HheaAdvanceWidthMaxField..], (ushort)Math.Clamp(metrics.MaxAdvance, 0, ushort.MaxValue));
        if (metrics.MinLsb != short.MaxValue) BinaryPrimitives.WriteInt16BigEndian(span[HheaMinLeftSideBearingField..], metrics.MinLsb);
        BinaryPrimitives.WriteUInt16BigEndian(span[HheaNumberOfHMetricsField..], (ushort)glyphCount);
    }

    // The instance's weight and width classes, when the font varies along those axes.
    private static void PatchOs2(byte[] os2, IReadOnlyDictionary<uint, float> userValues, VariationAxis[] axes)
    {
        var span = os2.AsSpan();
        if (userValues.TryGetValue(Wght, out float weight) && HasAxis(axes, Wght))
            BinaryPrimitives.WriteUInt16BigEndian(span[Os2WeightClassField..], (ushort)Math.Clamp((int)MathF.Round(weight), MinWeightClass, MaxWeightClass));
        if (userValues.TryGetValue(Wdth, out float width) && HasAxis(axes, Wdth))
            BinaryPrimitives.WriteUInt16BigEndian(span[Os2WidthClassField..], (ushort)WidthClass(width));
    }

    private static bool HasAxis(VariationAxis[] axes, uint tag)
    {
        foreach (var axis in axes)
            if (axis.Tag == tag) return true;
        return false;
    }

    // The OS/2 width classes, from ultra-condensed (50%) to ultra-expanded (200%), split halfway between their widths.
    private static int WidthClass(float percent) => percent switch
    {
        <= 56.25f => 1, <= 68.75f => 2, <= 81.25f => 3, <= 93.75f => 4, <= 106.25f => 5, <= 118.75f => 6, <= 137.5f => 7, <= 175f => 8, _ => 9,
    };
}
