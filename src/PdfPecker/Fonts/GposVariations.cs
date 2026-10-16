namespace PdfPecker.Fonts;

/// <summary>
/// Writes an instance's GPOS values into a copy of the table. A variable font keeps the default instance's kerning
/// and anchor values in GPOS and their variations in GDEF's ItemVariationStore, referenced by VariationIndex device
/// tables; adding the deltas in place lets every reader of GPOS (kerning, mark positioning) see the instance without
/// knowing about variations. Only numbers change, so the table keeps its layout.
/// </summary>
internal static class GposVariations
{
    public static byte[] Instance(byte[] gpos, VariationStore store)
    {
        var d = (byte[])gpos.Clone();
        if (d.Length < 10 || U16(d, 0) != 1) return gpos;
        var done = new HashSet<int>(); // values already moved: subtables and anchors may be shared
        int lookups = U16(d, 8);
        for (int i = 0, n = U16(d, lookups); i < n; i++)
        {
            int lookup = lookups + U16(d, lookups + 2 + i * 2);
            int type = U16(d, lookup);
            for (int s = 0, m = U16(d, lookup + 4); s < m; s++)
            {
                int sub = lookup + U16(d, lookup + 6 + s * 2);
                int t = type;
                if (type == 9)
                {
                    t = U16(d, sub + 2);
                    sub += (int)U32(d, sub + 4);
                }
                Subtable(d, t, sub, store, done);
            }
        }
        return d;
    }

    private static int U16(byte[] d, int o) => d[o] << 8 | d[o + 1];
    private static short S16(byte[] d, int o) => (short)(d[o] << 8 | d[o + 1]);
    private static uint U32(byte[] d, int o) => (uint)(d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3]);

    private static int ValueSize(int format) => 2 * System.Numerics.BitOperations.PopCount((uint)(format & 0xFF));

    private static void Subtable(byte[] d, int type, int sub, VariationStore store, HashSet<int> done)
    {
        int format = U16(d, sub);
        switch (type)
        {
            case 1 when format == 1:
                Value(d, sub, sub + 6, U16(d, sub + 4), store, done);
                break;
            case 1 when format == 2:
            {
                int vf = U16(d, sub + 4), size = ValueSize(vf);
                for (int k = 0, n = U16(d, sub + 6); k < n; k++) Value(d, sub, sub + 8 + k * size, vf, store, done);
                break;
            }
            case 2 when format == 1:
            {
                int vf1 = U16(d, sub + 4), vf2 = U16(d, sub + 6), s1 = ValueSize(vf1), s2 = ValueSize(vf2);
                if (((vf1 | vf2) & 0xF0) == 0) break; // no device tables, so nothing varies
                for (int k = 0, n = U16(d, sub + 8); k < n; k++)
                {
                    int set = sub + U16(d, sub + 10 + k * 2);
                    if (!done.Add(-set)) continue;
                    for (int r = 0, c = U16(d, set); r < c; r++)
                    {
                        int rec = set + 2 + r * (2 + s1 + s2);
                        Value(d, sub, rec + 2, vf1, store, done);
                        Value(d, sub, rec + 2 + s1, vf2, store, done);
                    }
                }
                break;
            }
            case 2 when format == 2:
            {
                int vf1 = U16(d, sub + 4), vf2 = U16(d, sub + 6), s1 = ValueSize(vf1), s2 = ValueSize(vf2);
                if (((vf1 | vf2) & 0xF0) == 0) break;
                int c1 = U16(d, sub + 12), c2 = U16(d, sub + 14);
                for (int i = 0; i < c1 * c2; i++)
                {
                    int rec = sub + 16 + i * (s1 + s2);
                    Value(d, sub, rec, vf1, store, done);
                    Value(d, sub, rec + s1, vf2, store, done);
                }
                break;
            }
            case 3 when format == 1:
                for (int k = 0, n = U16(d, sub + 4) * 2; k < n; k++)
                    if (U16(d, sub + 6 + k * 2) is int off and > 0) Anchor(d, sub + off, store, done);
                break;
            case 4 or 5 or 6 when format == 1:
            {
                int classes = U16(d, sub + 6);
                int marks = sub + U16(d, sub + 8);
                for (int k = 0, n = U16(d, marks); k < n; k++)
                    if (U16(d, marks + 4 + k * 4) is int off and > 0) Anchor(d, marks + off, store, done);
                int array = sub + U16(d, sub + 10);
                if (type == 5)
                {
                    for (int l = 0, n = U16(d, array); l < n; l++)
                    {
                        int attach = array + U16(d, array + 2 + l * 2);
                        for (int k = 0, c = U16(d, attach) * classes; k < c; k++)
                            if (U16(d, attach + 2 + k * 2) is int off and > 0) Anchor(d, attach + off, store, done);
                    }
                }
                else
                {
                    for (int k = 0, c = U16(d, array) * classes; k < c; k++)
                        if (U16(d, array + 2 + k * 2) is int off and > 0) Anchor(d, array + off, store, done);
                }
                break;
            }
        }
    }

    /// <summary>A ValueRecord: placement and advance fields, then the offsets of their device tables.</summary>
    private static void Value(byte[] d, int parent, int record, int format, VariationStore store, HashSet<int> done)
    {
        if ((format & 0xF0) == 0) return;
        Span<int> fields = stackalloc int[4];
        int pos = record;
        for (int bit = 0; bit < 4; bit++)
        {
            fields[bit] = -1;
            if ((format & 1 << bit) != 0) { fields[bit] = pos; pos += 2; }
        }
        for (int bit = 4; bit < 8; bit++)
        {
            if ((format & 1 << bit) == 0) continue;
            int device = U16(d, pos);
            pos += 2;
            if (device != 0 && fields[bit - 4] >= 0) Vary(d, fields[bit - 4], parent + device, store, done);
        }
    }

    /// <summary>A format 3 anchor: x and y, with a device table for each.</summary>
    private static void Anchor(byte[] d, int anchor, VariationStore store, HashSet<int> done)
    {
        if (U16(d, anchor) != 3) return;
        if (U16(d, anchor + 6) is int x and > 0) Vary(d, anchor + 2, anchor + x, store, done);
        if (U16(d, anchor + 8) is int y and > 0) Vary(d, anchor + 4, anchor + y, store, done);
    }

    private static void Vary(byte[] d, int field, int device, VariationStore store, HashSet<int> done)
    {
        if (U16(d, device + 4) != 0x8000 || !done.Add(field)) return; // other device formats are hinting adjustments
        float delta = store.Delta(U16(d, device), U16(d, device + 2));
        if (delta == 0) return;
        int v = Math.Clamp((int)MathF.Round(S16(d, field) + delta), short.MinValue, short.MaxValue);
        d[field] = (byte)(v >> 8);
        d[field + 1] = (byte)v;
    }
}
