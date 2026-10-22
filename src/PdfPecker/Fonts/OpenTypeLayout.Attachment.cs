namespace PdfPecker.Fonts;

// The GPOS lookup types that attach one glyph to another at anchor points: cursive, mark-to-base, mark-to-ligature and
// mark-to-mark positioning, and the pass that turns the attachments into offsets once every lookup has run.
internal sealed partial class OpenTypeLayout
{
    // An anchor: its format, then the x and y coordinates; the contour point and device tables of formats 2 and 3 are
    // not applied.
    private const int AnchorXField = 2, AnchorYField = 4;

    // Cursive attachment (format 1 only): an entry and an exit anchor per coverage index.
    private const int EntryExitCountField = 4, EntryExitRecordsField = 6, EntryExitRecordLength = 4;
    private const int EntryAnchorField = 0, ExitAnchorField = 2;

    // Mark attachment (format 1 only): the mark and base coverages, the mark class count, and the mark and base arrays.
    // The base array of mark-to-mark positioning holds the preceding marks, and that of mark-to-ligature positioning
    // holds an attachment per ligature, with a row of anchors per component.
    private const int MarkCoverageField = 2, BaseCoverageField = 4, MarkClassCountField = 6, MarkArrayField = 8, BaseArrayField = 10;
    private const int MarkRecordsField = 2, MarkRecordLength = 4, MarkClassField = 0, MarkAnchorField = 2;
    private const int BaseRecordsField = 2, ComponentRecordsField = 2;

    // Attachment chains are followed this deep when the positions are finished.
    private const int MaxAttachmentNesting = 64;

    private (int X, int Y) Anchor(int anchor) => (_reader.S16(anchor + AnchorXField), _reader.S16(anchor + AnchorYField));

    // Joins the glyph to the one before it that the lookup does not skip: the previous glyph's exit anchor meets this
    // glyph's entry anchor.
    private bool CursivePos(LookupState c, int sub, ref int i)
    {
        var buf = c.Buf;
        if (_reader.U16(sub + FormatField) != OnlyFormat) return false;
        int coverage = sub + _reader.U16(sub + CoverageField);
        int entry = CursiveAnchor(sub, coverage, buf[i].Gid, EntryAnchorField);
        if (entry == 0) return false;
        int prev = PreviousMatch(c, i);
        if (prev < 0) return false;
        int exit = CursiveAnchor(sub, coverage, buf[prev].Gid, ExitAnchorField);
        if (exit == 0) return false;
        var (exitX, exitY) = Anchor(sub + exit);
        var (entryX, entryY) = Anchor(sub + entry);
        var gi = buf[prev];
        var gj = buf[i];
        if (!c.Rtl)
        {
            gi.XAdvance = exitX + gi.XOffset;
            int d = entryX + gj.XOffset;
            gj.XAdvance -= d;
            gj.XOffset -= d;
        }
        else
        {
            int d = exitX + gi.XOffset;
            gi.XAdvance -= d;
            gi.XOffset -= d;
            gj.XAdvance = entryX + gj.XOffset;
        }
        buf[prev] = gi;
        buf[i] = gj;
        AttachCursively(c, prev, i, entryY - exitY);
        i++;
        return true;
    }

    // The offset of a glyph's entry or exit anchor from the subtable, or 0 when it has none or is not covered.
    private int CursiveAnchor(int sub, int coverage, int gid, int anchorField)
    {
        int index = CoverageIndex(coverage, gid);
        if (index < 0 || index >= _reader.U16(sub + EntryExitCountField)) return 0;
        return _reader.U16(sub + EntryExitRecordsField + index * EntryExitRecordLength + anchorField);
    }

    // Attaches the glyphs of a cursive pair, the earlier one to the later one unless the lookup is right to left, with
    // the later glyph's height relative to the earlier one's.
    private static void AttachCursively(LookupState c, int earlier, int later, int yOffset)
    {
        var buf = c.Buf;
        int child = earlier, parent = later;
        if ((c.LookupFlag & RightToLeft) == 0)
        {
            (child, parent) = (parent, child);
            yOffset = -yOffset;
        }
        ReverseCursiveMinorOffset(buf, child, parent);
        var ch = buf[child];
        ch.AttachType = AttachCursive;
        ch.AttachChain = parent - child;
        ch.YOffset = yOffset;
        buf[child] = ch;
        if (buf[parent].AttachChain == -ch.AttachChain)
        {
            var pa = buf[parent];
            pa.AttachChain = 0;
            buf[parent] = pa;
        }
    }

    // Turns the cursive chain the glyph heads round, so that the new parent can become its parent.
    private static void ReverseCursiveMinorOffset(GlyphRun buf, int i, int newParent)
    {
        var g = buf[i];
        int chain = g.AttachChain;
        if (chain == 0 || (g.AttachType & AttachCursive) == 0) return;
        g.AttachChain = 0;
        buf[i] = g;
        int j = i + chain;
        if (j == newParent || (uint)j >= (uint)buf.Count) return;
        ReverseCursiveMinorOffset(buf, j, newParent);
        var gj = buf[j];
        gj.YOffset = -buf[i].YOffset;
        gj.AttachChain = -chain;
        gj.AttachType = g.AttachType;
        buf[j] = gj;
    }

    // Attaches a mark to the base, ligature component or mark before it, at the anchors of the mark's class.
    private bool MarkPos(LookupState c, int type, int sub, ref int i)
    {
        var buf = c.Buf;
        if (_reader.U16(sub + FormatField) != OnlyFormat) return false;
        int markIndex = CoverageIndex(sub + _reader.U16(sub + MarkCoverageField), buf[i].Gid);
        if (markIndex < 0) return false;
        int classCount = _reader.U16(sub + MarkClassCountField);
        int markArray = sub + _reader.U16(sub + MarkArrayField), baseArray = sub + _reader.U16(sub + BaseArrayField);
        if (markIndex >= _reader.U16(markArray)) return false;
        int markRecord = markArray + MarkRecordsField + markIndex * MarkRecordLength;
        int markClass = _reader.U16(markRecord + MarkClassField);
        int markAnchor = markArray + _reader.U16(markRecord + MarkAnchorField);
        if (markClass >= classCount) return false;

        int baseCoverage = sub + _reader.U16(sub + BaseCoverageField);
        int j = type switch
        {
            MarkToMarkPositioning => PreviousMarkToAttachTo(c, i),
            MarkToLigaturePositioning => PreviousBase(c, i, -1),
            _ => PreviousBase(c, i, baseCoverage),
        };
        if (j < 0) return false;
        int baseIndex = CoverageIndex(baseCoverage, buf[j].Gid);
        if (baseIndex < 0 || baseIndex >= _reader.U16(baseArray)) return false;
        int baseAnchor = type == MarkToLigaturePositioning
            ? LigatureAnchor(baseArray, baseIndex, classCount, markClass, buf[i], buf[j])
            : BaseAnchor(baseArray, baseIndex, classCount, markClass);
        if (baseAnchor < 0) return false;

        var (bx, by) = Anchor(baseAnchor);
        var (mx, my) = Anchor(markAnchor);
        var g = buf[i];
        g.XOffset = bx - mx;
        g.YOffset = by - my;
        g.AttachType = AttachMark;
        g.AttachChain = j - i;
        buf[i] = g;
        i++;
        return true;
    }

    // The base (or ligature) a mark attaches to for mark-to-base and mark-to-ligature positioning: the glyph before it
    // that is not a mark or an invisible control, whatever the lookup's flags. Of the glyphs a multiple substitution
    // made, a mark attaches to the first, unless the subtable covers a later one (HarfBuzz's MarkBasePos accept); with
    // no base coverage, as for mark-to-ligature positioning, to the nearest.
    //
    // A long run of marks after one base would make searching back from each mark quadratic, so the search stops where
    // the previous mark's search of the same lookup started and takes over its answer, as HarfBuzz's last_base does.
    private int PreviousBase(LookupState c, int i, int baseCoverage)
    {
        int stop = 0, found = -1;
        if (c.BaseCoverage == baseCoverage && c.BaseSearchedBelow <= i)
        {
            stop = c.BaseSearchedBelow;
            found = c.LastBase;
        }
        for (int k = i - 1; k >= stop; k--)
            if (IsBase(c, k, baseCoverage))
            {
                found = k;
                break;
            }
        c.BaseCoverage = baseCoverage;
        c.BaseSearchedBelow = i;
        c.LastBase = found;
        return found;
    }

    private bool IsBase(LookupState c, int k, int baseCoverage)
    {
        var buf = c.Buf;
        var g = buf[k];
        if ((g.Props & Mark) != 0 || MaySkip(c, g, context: false)) return false;
        if (baseCoverage >= 0 && k > 0 && (g.Substitutions & Multiplied) != 0 && g.LigComp != 0)
        {
            var before = buf[k - 1];
            bool laterComponent = (before.Props & Mark) == 0 && (before.Substitutions & Multiplied) != 0
                && before.LigId == g.LigId && g.LigComp == before.LigComp + 1;
            if (laterComponent && CoverageIndex(baseCoverage, g.Gid) < 0) return false;
        }
        return true;
    }

    // The mark a mark attaches to for mark-to-mark positioning: the previous glyph, skipping only what the lookup's mark
    // filter excludes (HarfBuzz's MarkMarkPos), when it is a mark of the same base or ligature component; or -1.
    private int PreviousMarkToAttachTo(LookupState c, int i)
    {
        var buf = c.Buf;
        int saved = c.LookupFlag;
        c.LookupFlag &= ~IgnoreFlags;
        int j = PreviousMatch(c, i);
        c.LookupFlag = saved;
        if (j < 0 || (buf[j].Props & Mark) == 0) return -1;
        var a = buf[i];
        var b = buf[j];
        // Both marks must belong to the same base or ligature component, unless one of them is a mark ligature.
        bool good = a.LigId == b.LigId
            ? a.LigId == 0 || a.LigComp == b.LigComp
            : a.LigId > 0 && a.LigComp == 0 || b.LigId > 0 && b.LigComp == 0;
        return good ? j : -1;
    }

    // The anchor of the mark class on a base (or on the preceding mark), or -1 when the base has none for it.
    private int BaseAnchor(int baseArray, int baseIndex, int classCount, int markClass)
    {
        int anchor = _reader.U16(baseArray + BaseRecordsField + (baseIndex * classCount + markClass) * 2);
        return anchor != 0 ? baseArray + anchor : -1;
    }

    // The anchor of the mark class on the ligature component the mark belongs to (the last component when the mark
    // was not part of the ligature), or -1 when the component has none for it.
    private int LigatureAnchor(int baseArray, int baseIndex, int classCount, int markClass, in GlyphInfo mark, in GlyphInfo ligature)
    {
        int attach = baseArray + _reader.U16(baseArray + BaseRecordsField + baseIndex * 2);
        int components = _reader.U16(attach);
        if (components == 0) return -1;
        int component = ligature.LigId != 0 && ligature.LigId == mark.LigId && mark.LigComp > 0
            ? Math.Min(components, mark.LigComp) - 1
            : components - 1;
        int anchor = _reader.U16(attach + ComponentRecordsField + (component * classCount + markClass) * 2);
        return anchor != 0 ? attach + anchor : -1;
    }

    /// <summary>
    /// Adds the offsets of the glyphs that marks and cursive glyphs are attached to, as HarfBuzz finishes positioning;
    /// positions stay in logical order.
    /// </summary>
    public static void PropagateAttachments(GlyphRun buf, bool rtl)
    {
        Advances? advances = null;
        for (int i = 0; i < buf.Count; i++) Propagate(buf, i, rtl, MaxAttachmentNesting, ref advances);
    }

    private static void Propagate(GlyphRun buf, int i, bool rtl, int nesting, ref Advances? advances)
    {
        var g = buf[i];
        int chain = g.AttachChain;
        if (chain == 0 || nesting <= 0) return;
        g.AttachChain = 0;
        buf[i] = g;
        int j = i + chain;
        if ((uint)j >= (uint)buf.Count) return;
        Propagate(buf, j, rtl, nesting - 1, ref advances);
        g = buf[i];
        var p = buf[j];
        if ((g.AttachType & AttachCursive) != 0) g.YOffset += p.YOffset;
        else
        {
            g.XOffset += p.XOffset;
            g.YOffset += p.YOffset;
            if (j < i)
            {
                // A mark attached to an earlier glyph is drawn after the advances between them, so it moves back over
                // them (left to right) or forward (right to left). Many marks on one distant base would make summing
                // the advances for each of them quadratic, so the sums come from running totals, which stay valid
                // because this pass changes only offsets.
                advances ??= new Advances(buf);
                var (x, y) = rtl ? advances.Between(j + 1, i + 1) : advances.Between(j, i);
                if (rtl) { g.XOffset += x; g.YOffset += y; }
                else { g.XOffset -= x; g.YOffset -= y; }
            }
        }
        buf[i] = g;
    }

    // Running totals of the glyph advances. The sums wrap as the int additions they replace would.
    private sealed class Advances
    {
        private readonly long[] _x, _y;

        public Advances(GlyphRun buf)
        {
            _x = new long[buf.Count + 1];
            _y = new long[buf.Count + 1];
            for (int k = 0; k < buf.Count; k++)
            {
                _x[k + 1] = _x[k] + buf[k].XAdvance;
                _y[k + 1] = _y[k] + buf[k].YAdvance;
            }
        }

        // The advances of the glyphs from index start up to, but not including, end.
        public (int X, int Y) Between(int start, int end) => (unchecked((int)(_x[end] - _x[start])), unchecked((int)(_y[end] - _y[start])));
    }
}
