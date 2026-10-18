using PdfPecker.Fonts.Cff;

namespace PdfPecker.Fonts;

/// <summary>Finding the subroutines that the kept glyphs call.</summary>
internal static partial class CffSubsetter
{
    // The Type 2 operators the walk acts on. The stem hints matter only for the size of the hint masks that follow
    // them; every other operator just consumes its operands.
    private const int HStemOperator = 1, VStemOperator = 3, HStemHmOperator = 18, VStemHmOperator = 23;
    private const int HintMaskOperator = 19, CntrMaskOperator = 20;
    private const int CallSubrOperator = 10, ReturnOperator = 11, EscapeOperator = 12, EndCharOperator = 14, CallGSubrOperator = 29;

    private const int OperandStackCapacity = 64;

    // The walk is bounded per glyph and per font, since a document may use thousands of glyphs. A font past either
    // bound is not subset but embedded whole, which keeps every subroutine.
    private const int FontCharstringBudget = 50_000_000;

    /// <summary>Finds the subroutines used by a set of Type 2 charstrings by walking their operators.</summary>
    private sealed class SubroutineUsage
    {
        private readonly byte[] _data;
        private readonly CffIndex _global;
        private readonly List<PrivateBlock> _privates;
        private readonly Type2Stack _stack = new(OperandStackCapacity);
        private CharstringBudget _budget = new(FontCharstringBudget);
        private int _stems;
        private bool _ended;

        public SubroutineUsage(byte[] data, CffIndex global, List<PrivateBlock> privates)
        {
            _data = data;
            _global = global;
            _privates = privates;
            GlobalUsed = new bool[global.Count];
            LocalUsed = new bool[privates.Count][];
            for (int i = 0; i < LocalUsed.Length; i++) LocalUsed[i] = new bool[privates[i].Subrs?.Count ?? 0];
        }

        public bool[] GlobalUsed { get; }

        /// <summary>The local subroutines used, for each Private DICT.</summary>
        public bool[][] LocalUsed { get; }

        /// <summary>Walks a glyph's charstring; a Font DICT number out of range counts as the first.</summary>
        public void MarkGlyph(ReadOnlySpan<byte> charString, int fontDict)
        {
            _stack.Clear();
            _stems = 0;
            _ended = false;
            _budget.StartGlyph();
            if ((uint)fontDict >= (uint)_privates.Count) fontDict = 0;
            Run(charString, fontDict, 0);
        }

        private void Run(ReadOnlySpan<byte> charString, int fontDict, int depth)
        {
            if (depth > Type2.MaxSubrDepth) return;
            int pos = 0;
            while (pos < charString.Length && !_ended)
            {
                if (!_budget.Spend()) throw new InvalidOperationException("CFF charstrings call too many subroutines.");
                int b = charString[pos++];
                if (Type2.IsNumber(b))
                {
                    _stack.PushNumber(charString, b, ref pos);
                    continue;
                }
                switch (b)
                {
                    case HStemOperator:
                    case VStemOperator:
                    case HStemHmOperator:
                    case VStemHmOperator:
                        CountStems();
                        break;
                    case HintMaskOperator:
                    case CntrMaskOperator:
                        // An implicit vstem may precede the mask.
                        CountStems();
                        pos += Type2.MaskBytes(_stems);
                        break;
                    case CallSubrOperator:
                        if (_privates[fontDict].Subrs is { } local) Call(local, LocalUsed[fontDict], fontDict, depth);
                        else _stack.Clear();
                        break;
                    case CallGSubrOperator:
                        Call(_global, GlobalUsed, fontDict, depth);
                        break;
                    case ReturnOperator:
                        return;
                    case EndCharOperator:
                        _ended = true;
                        return;
                    case EscapeOperator:
                        // The two-byte operators (flex and arithmetic) consume their operands.
                        pos++;
                        _stack.Clear();
                        break;
                    default:
                        _stack.Clear();
                        break;
                }
            }
        }

        private void CountStems()
        {
            _stems += _stack.Count / 2;
            _stack.Clear();
        }

        // Runs the subroutine whose biased number is on top of the stack; a number out of range calls nothing.
        private void Call(CffIndex subrs, bool[] used, int fontDict, int depth)
        {
            if (_stack.Count == 0) return;
            int number = (int)_stack.Pop() + Type2.SubrBias(subrs.Count);
            if ((uint)number >= (uint)subrs.Count) return;
            used[number] = true;
            Run(subrs.Item(_data, number), fontDict, depth + 1);
        }
    }
}
