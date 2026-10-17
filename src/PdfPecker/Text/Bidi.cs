using System.Text;

namespace PdfPecker.Text;

/// <summary>
/// The Unicode Bidirectional Algorithm (UAX #9, rules P2 to I2 and L1 to L2): the embedding level of every character of
/// a paragraph, and the visual order of a line. Even levels run left to right, odd ones right to left.
/// </summary>
internal static class Bidi
{
    /// <summary>The deepest embedding level (BD2); deeper embeddings and isolates are ignored (rules X5a to X5c).</summary>
    private const int MaxDepth = 125;

    /// <summary>The most opening brackets the pairing of rule N0 keeps on its stack (BD16); more stops the search.</summary>
    private const int MaxOpenBrackets = 63;

    /// <summary>The paragraph level the first strong character gives (rules P2 and P3): 1 for R or AL, else 0.</summary>
    public static int FirstStrongLevel(ReadOnlySpan<BidiClass> types, int start = 0, int end = -1)
    {
        if (end < 0) end = types.Length;
        int isolates = 0;
        for (int i = start; i < end; i++)
        {
            switch (types[i])
            {
                case BidiClass.L: if (isolates == 0) return 0; break;
                case BidiClass.R:
                case BidiClass.AL: if (isolates == 0) return 1; break;
                case BidiClass.LRI:
                case BidiClass.RLI:
                case BidiClass.FSI: isolates++; break;
                case BidiClass.PDI: if (isolates > 0) isolates--; break;
                case BidiClass.B: return 0;
            }
        }
        return 0;
    }

    /// <summary>
    /// Resolves the embedding levels of one paragraph. <paramref name="types"/> are the characters' bidi classes and
    /// <paramref name="codePoints"/> the characters themselves (for bracket pairs; 0 for an object that is not a
    /// character). <paramref name="paragraphLevel"/> is 0 or 1, or -1 to take it from the first strong character.
    /// Characters that rule X9 removes get the level of the character before them.
    /// </summary>
    public static byte[] ResolveLevels(BidiClass[] types, int[] codePoints, int paragraphLevel)
    {
        var paragraph = new Paragraph(types, codePoints, paragraphLevel < 0 ? FirstStrongLevel(types) : paragraphLevel);
        paragraph.Resolve();
        return paragraph.Levels;
    }

    /// <summary>
    /// Rule L1 for one line of a paragraph: segment and paragraph separators, and whitespace and isolate controls
    /// before them or at the end of the line, get the paragraph level.
    /// </summary>
    public static void ResetWhitespace(ReadOnlySpan<BidiClass> originalTypes, Span<byte> levels, int paragraphLevel)
    {
        bool trailing = true;
        for (int i = levels.Length - 1; i >= 0; i--)
        {
            var type = originalTypes[i];
            if (type is BidiClass.S or BidiClass.B)
            {
                levels[i] = (byte)paragraphLevel;
                trailing = true;
            }
            else if (type == BidiClass.WS || IsIsolateControl(type) || IsRemovedByX9(type))
            {
                if (trailing) levels[i] = (byte)paragraphLevel;
            }
            else trailing = false;
        }
    }

    /// <summary>
    /// Rule L2: the visual order of a line, as indices into <paramref name="levels"/>: from the highest level down to the
    /// lowest odd one, every run at that level or above is reversed.
    /// </summary>
    public static int[] VisualOrder(ReadOnlySpan<byte> levels)
    {
        var order = new int[levels.Length];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        int highest = 0, lowestOdd = int.MaxValue;
        foreach (byte level in levels)
        {
            if (level > highest) highest = level;
            if ((level & 1) != 0 && level < lowestOdd) lowestOdd = level;
        }
        for (int level = highest; level >= lowestOdd && level > 0; level--)
        {
            int i = 0;
            while (i < levels.Length)
            {
                if (levels[order[i]] < level)
                {
                    i++;
                    continue;
                }
                int start = i;
                while (i < levels.Length && levels[order[i]] >= level) i++;
                Array.Reverse(order, start, i - start);
            }
        }
        return order;
    }

    /// <summary>Text drawn right to left as characters in left-to-right order: reversed, with mirrored brackets.</summary>
    public static string VisualText(string s)
    {
        var visual = new StringBuilder(s.Length);
        for (int i = s.Length - 1; i >= 0; i--)
        {
            if (char.IsLowSurrogate(s[i]) && i > 0 && char.IsHighSurrogate(s[i - 1]))
            {
                visual.Append(s[i - 1]).Append(s[i]);
                i--;
                continue;
            }
            int mirrored = UnicodeTables.Mirror(s[i]);
            visual.Append(mirrored < 0x10000 ? (char)mirrored : s[i]);
        }
        return visual.ToString();
    }

    internal static bool IsRemovedByX9(BidiClass type)
        => type is BidiClass.LRE or BidiClass.RLE or BidiClass.LRO or BidiClass.RLO or BidiClass.PDF or BidiClass.BN;

    private static bool IsIsolateInitiator(BidiClass type) => type is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI;

    private static bool IsIsolateControl(BidiClass type) => type is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI or BidiClass.PDI;

    // The strong direction a resolved type counts as in rules N0 to N2: numbers count as R; neutrals have none (ON).
    private static BidiClass StrongDirection(BidiClass type) => type switch
    {
        BidiClass.L => BidiClass.L,
        BidiClass.R or BidiClass.AL or BidiClass.EN or BidiClass.AN => BidiClass.R,
        _ => BidiClass.ON,
    };

    private static BidiClass EmbeddingDirection(int level) => (level & 1) != 0 ? BidiClass.R : BidiClass.L;

    /// <summary>One entry of the directional status stack of rules X1 to X8.</summary>
    private struct DirectionalStatus
    {
        public byte Level;
        public BidiClass Override; // ON for none, else L or R
        public bool Isolate;
    }

    /// <summary>A paragraph being resolved: the explicit levels of rules X1 to X10, then each isolating run sequence.</summary>
    private sealed class Paragraph
    {
        private readonly BidiClass[] _originalTypes;
        private readonly int[] _codePoints;
        private readonly int _paragraphLevel;
        private readonly BidiClass[] _types; // resolved types, changed by the rules
        public readonly byte[] Levels;
        private readonly int[] _matchingPdi, _matchingInitiator; // -1: none; Length when the PDI is missing

        public Paragraph(BidiClass[] types, int[] codePoints, int paragraphLevel)
        {
            _originalTypes = types;
            _codePoints = codePoints;
            _paragraphLevel = paragraphLevel;
            _types = (BidiClass[])types.Clone();
            Levels = new byte[types.Length];
            _matchingPdi = new int[types.Length];
            _matchingInitiator = new int[types.Length];
        }

        public int Length => _types.Length;
        public int ParagraphLevel => _paragraphLevel;
        public BidiClass OriginalType(int i) => _originalTypes[i];
        public BidiClass Type(int i) => _types[i];
        public int CodePoint(int i) => _codePoints[i];
        public bool HasMatchingPdi(int initiator) => _matchingPdi[initiator] >= 0;

        public void Resolve()
        {
            MatchIsolates();
            ResolveExplicitLevels();
            foreach (var sequence in IsolatingRunSequences()) sequence.Resolve();
            // Characters removed by X9 take the level of the character before them (or the paragraph's).
            for (int i = 0; i < Length; i++)
                if (IsRemovedByX9(_originalTypes[i])) Levels[i] = i == 0 ? (byte)_paragraphLevel : Levels[i - 1];
            for (int i = 0; i < Length; i++)
                if (_originalTypes[i] == BidiClass.B) Levels[i] = (byte)_paragraphLevel;
        }

        // BD9: the PDI that closes each isolate initiator, and the reverse.
        private void MatchIsolates()
        {
            Array.Fill(_matchingPdi, -1);
            Array.Fill(_matchingInitiator, -1);
            var open = new Stack<int>();
            for (int i = 0; i < Length; i++)
            {
                var type = _types[i];
                if (IsIsolateInitiator(type)) open.Push(i);
                else if (type == BidiClass.PDI && open.Count > 0)
                {
                    int initiator = open.Pop();
                    _matchingPdi[initiator] = i;
                    _matchingInitiator[i] = initiator;
                }
                else if (type == BidiClass.B)
                {
                    foreach (int initiator in open) _matchingPdi[initiator] = Length;
                    open.Clear();
                }
            }
            foreach (int initiator in open) _matchingPdi[initiator] = Length;
        }

        // Rules X1 to X8.
        private void ResolveExplicitLevels()
        {
            var stack = new DirectionalStatus[MaxDepth + 2];
            int depth = 0;
            stack[0] = new DirectionalStatus { Level = (byte)_paragraphLevel, Override = BidiClass.ON };
            int overflowIsolates = 0, overflowEmbeddings = 0, validIsolates = 0;
            for (int i = 0; i < Length; i++)
            {
                var type = _types[i];
                switch (type)
                {
                    case BidiClass.RLE:
                    case BidiClass.LRE:
                    case BidiClass.RLO:
                    case BidiClass.LRO:
                    case BidiClass.RLI:
                    case BidiClass.LRI:
                    case BidiClass.FSI:
                    {
                        bool isolate = IsIsolateInitiator(type);
                        bool rtl = type is BidiClass.RLE or BidiClass.RLO or BidiClass.RLI;
                        if (type == BidiClass.FSI)
                        {
                            int end = _matchingPdi[i] < 0 ? Length : _matchingPdi[i];
                            rtl = FirstStrongLevel(_types, i + 1, end) == 1;
                        }
                        if (isolate)
                        {
                            Levels[i] = stack[depth].Level;
                            ApplyOverride(stack[depth], i);
                        }
                        int current = stack[depth].Level;
                        int newLevel = rtl ? (current + 1) | 1 : (current + 2) & ~1;
                        if (newLevel <= MaxDepth && overflowIsolates == 0 && overflowEmbeddings == 0)
                        {
                            if (isolate) validIsolates++;
                            stack[++depth] = new DirectionalStatus
                            {
                                Level = (byte)newLevel,
                                Override = type switch { BidiClass.LRO => BidiClass.L, BidiClass.RLO => BidiClass.R, _ => BidiClass.ON },
                                Isolate = isolate,
                            };
                            // Embedding and override controls are removed by X9; they keep the new level here.
                            if (!isolate) Levels[i] = (byte)newLevel;
                        }
                        else if (isolate) overflowIsolates++;
                        else
                        {
                            if (overflowIsolates == 0) overflowEmbeddings++;
                            Levels[i] = stack[depth].Level;
                        }
                        break;
                    }
                    case BidiClass.PDI:
                        if (overflowIsolates > 0) overflowIsolates--;
                        else if (validIsolates > 0)
                        {
                            overflowEmbeddings = 0;
                            while (!stack[depth].Isolate) depth--;
                            depth--;
                            validIsolates--;
                        }
                        Levels[i] = stack[depth].Level;
                        ApplyOverride(stack[depth], i);
                        break;
                    case BidiClass.PDF:
                        Levels[i] = stack[depth].Level;
                        if (overflowIsolates > 0) { }
                        else if (overflowEmbeddings > 0) overflowEmbeddings--;
                        else if (!stack[depth].Isolate && depth >= 1) depth--;
                        break;
                    case BidiClass.B:
                        Levels[i] = (byte)_paragraphLevel;
                        break;
                    case BidiClass.BN:
                        Levels[i] = stack[depth].Level;
                        break;
                    default:
                        Levels[i] = stack[depth].Level;
                        ApplyOverride(stack[depth], i);
                        break;
                }
            }
        }

        // Rule X6: under a directional override, a character takes the override's type.
        private void ApplyOverride(DirectionalStatus status, int i)
        {
            if (status.Override != BidiClass.ON) _types[i] = status.Override;
        }

        // BD13 and X10: the level runs (of characters X9 keeps), chained across isolates, in text order.
        private List<IsolatingRunSequence> IsolatingRunSequences()
        {
            // The kept characters, in order, with the level runs as ranges over them.
            var kept = new int[Length];
            int keptCount = 0;
            var runStarts = new List<int>();
            var runOf = new int[Length];
            int currentLevel = -1;
            for (int i = 0; i < Length; i++)
            {
                if (IsRemovedByX9(_originalTypes[i])) continue;
                if (runStarts.Count == 0 || Levels[i] != currentLevel)
                {
                    runStarts.Add(keptCount);
                    currentLevel = Levels[i];
                }
                runOf[i] = runStarts.Count - 1;
                kept[keptCount++] = i;
            }
            runStarts.Add(keptCount);
            int RunLength(int run) => runStarts[run + 1] - runStarts[run];
            int RunLast(int run) => kept[runStarts[run + 1] - 1];
            // The run after an isolate initiator's run in its sequence: the run of its matching PDI, if any.
            int NextRun(int run)
            {
                int last = RunLast(run);
                return IsIsolateInitiator(_originalTypes[last]) && _matchingPdi[last] is int pdi && pdi >= 0 && pdi < Length ? runOf[pdi] : -1;
            }

            var sequences = new List<IsolatingRunSequence>();
            for (int run = 0; run < runStarts.Count - 1; run++)
            {
                // A run that starts with the PDI of a matched initiator continues the initiator's sequence.
                int first = kept[runStarts[run]];
                if (_originalTypes[first] == BidiClass.PDI && _matchingInitiator[first] >= 0) continue;
                int total = 0;
                for (int r = run; r >= 0; r = NextRun(r)) total += RunLength(r);
                var indices = new int[total];
                int at = 0;
                for (int r = run; r >= 0; r = NextRun(r))
                {
                    Array.Copy(kept, runStarts[r], indices, at, RunLength(r));
                    at += RunLength(r);
                }
                sequences.Add(new IsolatingRunSequence(this, indices));
            }
            return sequences;
        }
    }

    /// <summary>
    /// An isolating run sequence (BD13): the characters the weak, neutral and implicit rules (W1 to I2) see as one
    /// unit, with the types of its start and end taken from the levels around it (X10).
    /// </summary>
    private sealed class IsolatingRunSequence
    {
        private readonly Paragraph _paragraph;
        private readonly int[] _indices;   // positions in the paragraph
        private readonly BidiClass[] _types; // resolved types, changed by the rules
        private readonly int _level;
        private readonly BidiClass _sos, _eos;

        public IsolatingRunSequence(Paragraph paragraph, int[] indices)
        {
            _paragraph = paragraph;
            _indices = indices;
            _types = new BidiClass[indices.Length];
            for (int k = 0; k < indices.Length; k++) _types[k] = paragraph.Type(indices[k]);
            _level = paragraph.Levels[indices[0]];

            int first = indices[0], last = indices[^1];
            int precedingLevel = paragraph.ParagraphLevel;
            for (int i = first - 1; i >= 0; i--)
                if (!IsRemovedByX9(paragraph.OriginalType(i)))
                {
                    precedingLevel = paragraph.Levels[i];
                    break;
                }
            // A sequence that ends in a matched isolate initiator ends at the paragraph level, as if at its end.
            int followingLevel = paragraph.ParagraphLevel;
            if (!(IsIsolateInitiator(paragraph.OriginalType(last)) && paragraph.HasMatchingPdi(last)))
                for (int i = last + 1; i < paragraph.Length; i++)
                    if (!IsRemovedByX9(paragraph.OriginalType(i)))
                    {
                        followingLevel = paragraph.Levels[i];
                        break;
                    }
            _sos = EmbeddingDirection(Math.Max(precedingLevel, _level));
            _eos = EmbeddingDirection(Math.Max(followingLevel, _level));
        }

        public void Resolve()
        {
            ResolveWeakTypes();
            ResolvePairedBrackets();
            ResolveNeutralTypes();
            ResolveImplicitLevels();
        }

        private void ResolveWeakTypes()
        {
            int n = _types.Length;
            // W1: NSM takes the type of the character before it (ON after an isolate control).
            var previous = _sos;
            for (int k = 0; k < n; k++)
            {
                if (_types[k] == BidiClass.NSM) _types[k] = previous;
                else previous = IsIsolateControl(_types[k]) ? BidiClass.ON : _types[k];
            }
            // W2: EN after AL becomes AN. W3: AL becomes R.
            var strong = _sos;
            for (int k = 0; k < n; k++)
            {
                var type = _types[k];
                if (type is BidiClass.L or BidiClass.R or BidiClass.AL) strong = type;
                else if (type == BidiClass.EN && strong == BidiClass.AL) _types[k] = BidiClass.AN;
            }
            for (int k = 0; k < n; k++)
                if (_types[k] == BidiClass.AL) _types[k] = BidiClass.R;
            // W4: a single ES between ENs is EN; a single CS between two ENs or two ANs takes their type.
            for (int k = 1; k < n - 1; k++)
            {
                if (_types[k] == BidiClass.ES && _types[k - 1] == BidiClass.EN && _types[k + 1] == BidiClass.EN) _types[k] = BidiClass.EN;
                else if (_types[k] == BidiClass.CS && _types[k - 1] is BidiClass.EN or BidiClass.AN && _types[k + 1] == _types[k - 1]) _types[k] = _types[k - 1];
            }
            // W5: ETs next to EN become EN.
            for (int k = 0; k < n; k++)
            {
                if (_types[k] != BidiClass.ET) continue;
                int start = k;
                while (k < n && _types[k] == BidiClass.ET) k++;
                bool nextToNumber = start > 0 && _types[start - 1] == BidiClass.EN || k < n && _types[k] == BidiClass.EN;
                if (nextToNumber) Array.Fill(_types, BidiClass.EN, start, k - start);
                k--;
            }
            // W6: other separators and terminators become ON.
            for (int k = 0; k < n; k++)
                if (_types[k] is BidiClass.ES or BidiClass.ET or BidiClass.CS) _types[k] = BidiClass.ON;
            // W7: EN after L (or sos L) becomes L.
            strong = _sos;
            for (int k = 0; k < n; k++)
            {
                var type = _types[k];
                if (type is BidiClass.L or BidiClass.R) strong = type;
                else if (type == BidiClass.EN && strong == BidiClass.L) _types[k] = BidiClass.L;
            }
        }

        // N0: bracket pairs take the embedding direction when they enclose it, else the direction of the context.
        private void ResolvePairedBrackets()
        {
            var pairs = FindBracketPairs();
            var embedding = EmbeddingDirection(_level);
            foreach (var (open, close) in pairs)
            {
                bool foundEmbedding = false, foundOpposite = false;
                for (int k = open + 1; k < close; k++)
                {
                    var direction = StrongDirection(_types[k]);
                    if (direction == BidiClass.ON) continue;
                    if (direction == embedding)
                    {
                        foundEmbedding = true;
                        break;
                    }
                    foundOpposite = true;
                }
                BidiClass resolved;
                if (foundEmbedding) resolved = embedding;
                else if (foundOpposite) resolved = ContextBefore(open);
                else continue;
                SetBracketType(open, resolved);
                SetBracketType(close, resolved);
            }
        }

        // BD16: the pairs of brackets that are still neutral, matched by their canonical forms, in order of the
        // opening bracket. Only the first MaxOpenBrackets unmatched openings are tracked.
        private List<(int Open, int Close)> FindBracketPairs()
        {
            var pairs = new List<(int Open, int Close)>();
            var openings = new List<(int Bracket, int Position)>();
            for (int k = 0; k < _types.Length; k++)
            {
                if (_types[k] != BidiClass.ON) continue;
                int codePoint = _paragraph.CodePoint(_indices[k]);
                int pair = UnicodeTables.Bracket(codePoint, out bool opening);
                if (pair == 0) continue;
                if (opening)
                {
                    if (openings.Count == MaxOpenBrackets) break;
                    openings.Add((UnicodeTables.CanonicalBracket(pair), k));
                }
                else
                {
                    int canonical = UnicodeTables.CanonicalBracket(codePoint);
                    for (int s = openings.Count - 1; s >= 0; s--)
                    {
                        if (openings[s].Bracket != canonical) continue;
                        pairs.Add((openings[s].Position, k));
                        openings.RemoveRange(s, openings.Count - s);
                        break;
                    }
                }
            }
            pairs.Sort((a, b) => a.Open.CompareTo(b.Open));
            return pairs;
        }

        // The strong direction before a position, back to the start of the sequence.
        private BidiClass ContextBefore(int position)
        {
            for (int k = position - 1; k >= 0; k--)
            {
                var direction = StrongDirection(_types[k]);
                if (direction != BidiClass.ON) return direction;
            }
            return _sos;
        }

        // A bracket's new type, which NSMs after it (originally) follow.
        private void SetBracketType(int k, BidiClass type)
        {
            _types[k] = type;
            for (int j = k + 1; j < _types.Length && _paragraph.OriginalType(_indices[j]) == BidiClass.NSM; j++) _types[j] = type;
        }

        // N1 and N2: runs of neutrals take the direction around them when both sides agree, else the embedding's.
        private void ResolveNeutralTypes()
        {
            int n = _types.Length;
            var embedding = EmbeddingDirection(_level);
            for (int k = 0; k < n; k++)
            {
                if (!IsNeutral(_types[k])) continue;
                int start = k;
                while (k < n && IsNeutral(_types[k])) k++;
                var before = start == 0 ? _sos : StrongDirection(_types[start - 1]);
                var after = k == n ? _eos : StrongDirection(_types[k]);
                Array.Fill(_types, before == after ? before : embedding, start, k - start);
                k--;
            }
        }

        private static bool IsNeutral(BidiClass type)
            => type is BidiClass.B or BidiClass.S or BidiClass.WS or BidiClass.ON || IsIsolateControl(type);

        // I1 and I2.
        private void ResolveImplicitLevels()
        {
            for (int k = 0; k < _types.Length; k++)
            {
                int i = _indices[k];
                int level = _paragraph.Levels[i];
                var type = _types[k];
                if ((level & 1) == 0)
                {
                    if (type == BidiClass.R) level += 1;
                    else if (type is BidiClass.AN or BidiClass.EN) level += 2;
                }
                else if (type is BidiClass.L or BidiClass.EN or BidiClass.AN) level += 1;
                _paragraph.Levels[i] = (byte)level;
            }
        }
    }
}
