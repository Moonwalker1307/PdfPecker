namespace PdfPecker.Text;

/// <summary>
/// The Unicode Bidirectional Algorithm (UAX #9, rules P2 to I2 and L1 to L2): the embedding level of every character of
/// a paragraph, and the visual order of a line. Even levels run left to right, odd ones right to left.
/// </summary>
internal static class Bidi
{
    private const int MaxDepth = 125;

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
        var p = new Paragraph(types, codePoints, paragraphLevel < 0 ? FirstStrongLevel(types) : paragraphLevel);
        p.Run();
        return p.Levels;
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
            var t = originalTypes[i];
            if (t is BidiClass.S or BidiClass.B)
            {
                levels[i] = (byte)paragraphLevel;
                trailing = true;
            }
            else if (t is BidiClass.WS or BidiClass.LRI or BidiClass.RLI or BidiClass.FSI or BidiClass.PDI || IsRemovedByX9(t))
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
        int max = 0, minOdd = int.MaxValue;
        foreach (byte l in levels)
        {
            if (l > max) max = l;
            if ((l & 1) != 0 && l < minOdd) minOdd = l;
        }
        for (int level = max; level >= minOdd && level > 0; level--)
        {
            int i = 0;
            while (i < levels.Length)
            {
                if (levels[order[i]] < level) { i++; continue; }
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
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = s.Length - 1; i >= 0; i--)
        {
            if (char.IsLowSurrogate(s[i]) && i > 0 && char.IsHighSurrogate(s[i - 1]))
            {
                sb.Append(s[i - 1]).Append(s[i]);
                i--;
                continue;
            }
            int m = UnicodeTables.Mirror(s[i]);
            sb.Append(m < 0x10000 ? (char)m : s[i]);
        }
        return sb.ToString();
    }

    internal static bool IsRemovedByX9(BidiClass t) => t is BidiClass.LRE or BidiClass.RLE or BidiClass.LRO or BidiClass.RLO or BidiClass.PDF or BidiClass.BN;

    private static bool IsIsolateInitiator(BidiClass t) => t is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI;

    private sealed class Paragraph
    {
        private readonly BidiClass[] _initial;
        private readonly int[] _cps;
        private readonly int _level;
        private readonly BidiClass[] _types; // resolved types, changed by the rules
        public readonly byte[] Levels;
        private readonly int[] _matchingPdi, _matchingInitiator; // -1: none; Length when the PDI is missing

        public Paragraph(BidiClass[] types, int[] cps, int level)
        {
            _initial = types;
            _cps = cps;
            _level = level;
            _types = (BidiClass[])types.Clone();
            Levels = new byte[types.Length];
            _matchingPdi = new int[types.Length];
            _matchingInitiator = new int[types.Length];
        }

        public void Run()
        {
            int n = _types.Length;
            MatchIsolates();
            ExplicitLevels();
            foreach (var seq in IsolatingRunSequences()) seq.Resolve();
            // Characters removed by X9 take the level of the character before them (or the paragraph's).
            for (int i = 0; i < n; i++)
                if (IsRemovedByX9(_initial[i])) Levels[i] = i == 0 ? (byte)_level : Levels[i - 1];
            for (int i = 0; i < n; i++)
                if (_initial[i] == BidiClass.B) Levels[i] = (byte)_level;
        }

        // BD9: the PDI that closes each isolate initiator, and the reverse.
        private void MatchIsolates()
        {
            int n = _types.Length;
            Array.Fill(_matchingPdi, -1);
            Array.Fill(_matchingInitiator, -1);
            var stack = new Stack<int>();
            for (int i = 0; i < n; i++)
            {
                var t = _types[i];
                if (IsIsolateInitiator(t)) stack.Push(i);
                else if (t == BidiClass.PDI && stack.Count > 0)
                {
                    int open = stack.Pop();
                    _matchingPdi[open] = i;
                    _matchingInitiator[i] = open;
                }
                else if (t == BidiClass.B)
                {
                    foreach (int open in stack) _matchingPdi[open] = n;
                    stack.Clear();
                }
            }
            foreach (int open in stack) _matchingPdi[open] = n;
        }

        private struct Status
        {
            public byte Level;
            public BidiClass Override; // ON for none, else L or R
            public bool Isolate;
        }

        // Rules X1 to X8.
        private void ExplicitLevels()
        {
            int n = _types.Length;
            var stack = new Status[MaxDepth + 2];
            int depth = 0;
            stack[0] = new Status { Level = (byte)_level, Override = BidiClass.ON };
            int overflowIsolates = 0, overflowEmbeddings = 0, validIsolates = 0;
            for (int i = 0; i < n; i++)
            {
                var t = _types[i];
                switch (t)
                {
                    case BidiClass.RLE:
                    case BidiClass.LRE:
                    case BidiClass.RLO:
                    case BidiClass.LRO:
                    case BidiClass.RLI:
                    case BidiClass.LRI:
                    case BidiClass.FSI:
                    {
                        bool isolate = IsIsolateInitiator(t);
                        bool rtl = t is BidiClass.RLE or BidiClass.RLO or BidiClass.RLI;
                        if (t == BidiClass.FSI)
                        {
                            int end = _matchingPdi[i] < 0 ? n : _matchingPdi[i];
                            rtl = FirstStrongLevel(_types, i + 1, end) == 1;
                        }
                        if (isolate)
                        {
                            Levels[i] = stack[depth].Level;
                            if (stack[depth].Override != BidiClass.ON) _types[i] = stack[depth].Override;
                        }
                        int cur = stack[depth].Level;
                        int newLevel = rtl ? (cur + 1) | 1 : (cur + 2) & ~1;
                        if (newLevel <= MaxDepth && overflowIsolates == 0 && overflowEmbeddings == 0)
                        {
                            if (isolate) validIsolates++;
                            stack[++depth] = new Status
                            {
                                Level = (byte)newLevel,
                                Override = t switch { BidiClass.LRO => BidiClass.L, BidiClass.RLO => BidiClass.R, _ => BidiClass.ON },
                                Isolate = isolate,
                            };
                            // Embedding and override controls are removed by X9; they keep the new level here.
                            if (!isolate) Levels[i] = (byte)newLevel;
                        }
                        else
                        {
                            if (isolate) overflowIsolates++;
                            else
                            {
                                if (overflowIsolates == 0) overflowEmbeddings++;
                                Levels[i] = stack[depth].Level;
                            }
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
                        if (stack[depth].Override != BidiClass.ON) _types[i] = stack[depth].Override;
                        break;
                    case BidiClass.PDF:
                        Levels[i] = stack[depth].Level;
                        if (overflowIsolates > 0) { }
                        else if (overflowEmbeddings > 0) overflowEmbeddings--;
                        else if (!stack[depth].Isolate && depth >= 1) depth--;
                        break;
                    case BidiClass.B:
                        Levels[i] = (byte)_level;
                        break;
                    case BidiClass.BN:
                        Levels[i] = stack[depth].Level;
                        break;
                    default:
                        Levels[i] = stack[depth].Level;
                        if (stack[depth].Override != BidiClass.ON) _types[i] = stack[depth].Override;
                        break;
                }
            }
        }

        // BD13 and X10: the level runs (of characters X9 keeps), chained across isolates.
        private List<RunSequence> IsolatingRunSequences()
        {
            int n = _types.Length;
            var runs = new List<List<int>>();
            List<int>? current = null;
            int currentLevel = -1;
            for (int i = 0; i < n; i++)
            {
                if (IsRemovedByX9(_initial[i])) continue;
                if (current == null || Levels[i] != currentLevel)
                {
                    current = new List<int>();
                    runs.Add(current);
                    currentLevel = Levels[i];
                }
                current.Add(i);
            }
            var runOf = new int[n];
            for (int r = 0; r < runs.Count; r++)
                foreach (int i in runs[r]) runOf[i] = r;

            var sequences = new List<RunSequence>();
            foreach (var run in runs)
            {
                int first = run[0];
                // A run that starts with the PDI of a matched initiator continues the initiator's sequence.
                if (_initial[first] == BidiClass.PDI && _matchingInitiator[first] >= 0) continue;
                var indices = new List<int>();
                var r = run;
                while (true)
                {
                    indices.AddRange(r);
                    int last = r[^1];
                    if (IsIsolateInitiator(_initial[last]) && _matchingPdi[last] is int pdi && pdi >= 0 && pdi < n)
                        r = runs[runOf[pdi]];
                    else break;
                }
                sequences.Add(new RunSequence(this, indices.ToArray()));
            }
            return sequences;
        }

        private sealed class RunSequence
        {
            private readonly Paragraph _p;
            private readonly int[] _idx;
            private readonly BidiClass[] _t;
            private readonly int _level;
            private readonly BidiClass _sos, _eos;

            public RunSequence(Paragraph p, int[] indices)
            {
                _p = p;
                _idx = indices;
                _t = new BidiClass[indices.Length];
                for (int k = 0; k < indices.Length; k++) _t[k] = p._types[indices[k]];
                _level = p.Levels[indices[0]];

                int first = indices[0], last = indices[^1];
                int prevLevel = p._level;
                for (int i = first - 1; i >= 0; i--)
                    if (!IsRemovedByX9(p._initial[i])) { prevLevel = p.Levels[i]; break; }
                int succLevel = p._level;
                if (!(IsIsolateInitiator(p._initial[last]) && p._matchingPdi[last] >= 0))
                    for (int i = last + 1; i < p._types.Length; i++)
                        if (!IsRemovedByX9(p._initial[i])) { succLevel = p.Levels[i]; break; }
                _sos = (Math.Max(prevLevel, _level) & 1) != 0 ? BidiClass.R : BidiClass.L;
                _eos = (Math.Max(succLevel, _level) & 1) != 0 ? BidiClass.R : BidiClass.L;
            }

            public void Resolve()
            {
                WeakTypes();
                PairedBrackets();
                NeutralTypes();
                ImplicitLevels();
            }

            private void WeakTypes()
            {
                int n = _t.Length;
                // W1: NSM takes the type of the character before it (ON after an isolate control).
                var prev = _sos;
                for (int k = 0; k < n; k++)
                {
                    if (_t[k] == BidiClass.NSM) _t[k] = prev;
                    else prev = _t[k] is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI or BidiClass.PDI ? BidiClass.ON : _t[k];
                    if (_t[k] is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI or BidiClass.PDI) prev = BidiClass.ON;
                }
                // W2: EN after AL becomes AN. W3: AL becomes R.
                var strong = _sos;
                for (int k = 0; k < n; k++)
                {
                    var t = _t[k];
                    if (t is BidiClass.L or BidiClass.R or BidiClass.AL) strong = t;
                    else if (t == BidiClass.EN && strong == BidiClass.AL) _t[k] = BidiClass.AN;
                }
                for (int k = 0; k < n; k++)
                    if (_t[k] == BidiClass.AL) _t[k] = BidiClass.R;
                // W4: a single ES between ENs is EN; a single CS between two ENs or two ANs takes their type.
                for (int k = 1; k < n - 1; k++)
                {
                    if (_t[k] == BidiClass.ES && _t[k - 1] == BidiClass.EN && _t[k + 1] == BidiClass.EN) _t[k] = BidiClass.EN;
                    else if (_t[k] == BidiClass.CS && _t[k - 1] is BidiClass.EN or BidiClass.AN && _t[k + 1] == _t[k - 1]) _t[k] = _t[k - 1];
                }
                // W5: ETs next to EN become EN.
                for (int k = 0; k < n; k++)
                {
                    if (_t[k] != BidiClass.ET) continue;
                    int s = k;
                    while (k < n && _t[k] == BidiClass.ET) k++;
                    bool en = s > 0 && _t[s - 1] == BidiClass.EN || k < n && _t[k] == BidiClass.EN;
                    if (en) for (int j = s; j < k; j++) _t[j] = BidiClass.EN;
                    k--;
                }
                // W6: other separators and terminators become ON.
                for (int k = 0; k < n; k++)
                    if (_t[k] is BidiClass.ES or BidiClass.ET or BidiClass.CS) _t[k] = BidiClass.ON;
                // W7: EN after L (or sos L) becomes L.
                strong = _sos;
                for (int k = 0; k < n; k++)
                {
                    var t = _t[k];
                    if (t is BidiClass.L or BidiClass.R) strong = t;
                    else if (t == BidiClass.EN && strong == BidiClass.L) _t[k] = BidiClass.L;
                }
            }

            // N0: bracket pairs take the embedding direction, or the direction of the context.
            private void PairedBrackets()
            {
                var cps = _p._cps;
                var pairs = new List<(int Open, int Close)>();
                var stack = new List<(int Bracket, int Pos)>();
                for (int k = 0; k < _t.Length; k++)
                {
                    if (_t[k] != BidiClass.ON) continue;
                    int cp = cps[_idx[k]];
                    int pair = UnicodeTables.Bracket(cp, out bool opening);
                    if (pair == 0) continue;
                    if (opening)
                    {
                        if (stack.Count == 63) break; // BD16: overflow stops the search
                        stack.Add((UnicodeTables.CanonicalBracket(pair), k));
                    }
                    else
                    {
                        int canon = UnicodeTables.CanonicalBracket(cp);
                        for (int s = stack.Count - 1; s >= 0; s--)
                        {
                            if (stack[s].Bracket != canon) continue;
                            pairs.Add((stack[s].Pos, k));
                            stack.RemoveRange(s, stack.Count - s);
                            break;
                        }
                    }
                }
                pairs.Sort((a, b) => a.Open.CompareTo(b.Open));
                var embedding = (_level & 1) != 0 ? BidiClass.R : BidiClass.L;
                foreach (var (open, close) in pairs)
                {
                    bool foundEmbedding = false, foundOpposite = false;
                    for (int k = open + 1; k < close; k++)
                    {
                        var d = StrongOf(_t[k]);
                        if (d == BidiClass.ON) continue;
                        if (d == embedding) { foundEmbedding = true; break; }
                        foundOpposite = true;
                    }
                    BidiClass dir;
                    if (foundEmbedding) dir = embedding;
                    else if (foundOpposite)
                    {
                        // The context before the opening bracket decides.
                        var before = _sos;
                        for (int k = open - 1; k >= 0; k--)
                        {
                            var d = StrongOf(_t[k]);
                            if (d != BidiClass.ON) { before = d; break; }
                        }
                        dir = before != embedding ? before : embedding;
                    }
                    else continue;
                    SetBracket(open, dir);
                    SetBracket(close, dir);
                }
            }

            // A bracket's new type, which NSMs after it (originally) follow.
            private void SetBracket(int k, BidiClass dir)
            {
                _t[k] = dir;
                for (int j = k + 1; j < _t.Length && _p._initial[_idx[j]] == BidiClass.NSM; j++) _t[j] = dir;
            }

            private static BidiClass StrongOf(BidiClass t) => t switch
            {
                BidiClass.L => BidiClass.L,
                BidiClass.R or BidiClass.AL or BidiClass.EN or BidiClass.AN => BidiClass.R,
                _ => BidiClass.ON,
            };

            // N1 and N2: runs of neutrals take the direction around them when both sides agree, else the embedding's.
            private void NeutralTypes()
            {
                int n = _t.Length;
                var embedding = (_level & 1) != 0 ? BidiClass.R : BidiClass.L;
                for (int k = 0; k < n; k++)
                {
                    if (!IsNeutral(_t[k])) continue;
                    int s = k;
                    while (k < n && IsNeutral(_t[k])) k++;
                    var before = s == 0 ? _sos : StrongOf(_t[s - 1]);
                    var after = k == n ? _eos : StrongOf(_t[k]);
                    var dir = before == after ? before : embedding;
                    for (int j = s; j < k; j++) _t[j] = dir;
                    k--;
                }
            }

            private static bool IsNeutral(BidiClass t)
                => t is BidiClass.B or BidiClass.S or BidiClass.WS or BidiClass.ON or BidiClass.LRI or BidiClass.RLI or BidiClass.FSI or BidiClass.PDI;

            // I1 and I2.
            private void ImplicitLevels()
            {
                for (int k = 0; k < _t.Length; k++)
                {
                    int i = _idx[k];
                    int level = _p.Levels[i];
                    var t = _t[k];
                    if ((level & 1) == 0)
                    {
                        if (t == BidiClass.R) level += 1;
                        else if (t is BidiClass.AN or BidiClass.EN) level += 2;
                    }
                    else if (t is BidiClass.L or BidiClass.EN or BidiClass.AN) level += 1;
                    _p.Levels[i] = (byte)level;
                }
            }
        }
    }
}
