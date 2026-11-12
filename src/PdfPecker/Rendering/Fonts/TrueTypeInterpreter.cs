namespace PdfPecker.Rendering.Fonts;

/// <summary>
/// A TrueType bytecode interpreter, used only for the fonts that cannot be drawn without their instructions: some
/// older CJK fonts (MingLiU, PMingLiU, DFKai-SB and other DynaLab fonts) build their characters from shared strokes
/// whose positions only the glyph programs set. It follows FreeType's classic (version 35) interpreter, including its
/// undocumented cases, and runs at a size of one pixel per font unit, so rounding to the grid rounds to whole units
/// and the outlines come out at their designed shapes. Coordinates are 26.6 fixed point and vectors 2.14, as in the
/// TrueType specification.
/// <para>
/// Every run is bounded: a budget of instructions (counting the points that loops over a contour or a zone touch),
/// a stack, call depth and storage sized from maxp but capped, and a failure stops the program and leaves the points
/// where they are, as FreeType does. A failure in the font or control value program turns hinting off for the font.
/// </para>
/// </summary>
internal sealed class TrueTypeInterpreter
{
    /// <summary>Instructions (and loop iterations over points) one program may run.</summary>
    private const long InstructionBudget = 2_000_000;
    private const int MaxCallDepth = 64;
    private const int MaxStack = 65_536, StackSlack = 32, MinStack = 256;
    private const int MaxStorage = 65_536, MaxTwilightPoints = 65_536, MaxFunctions = 65_536;
    private const int One = 64; // one pixel in 26.6
    private const int Unit = 0x4000; // 1.0 in 2.14

    private readonly int _ppem;
    private readonly int _stackSize;
    private readonly int[] _cvt;
    private readonly int[] _storage;
    private readonly int[] _twilightX, _twilightY, _twilightOrgX, _twilightOrgY;
    private readonly Dictionary<int, Function> _functions = new();
    private readonly Dictionary<int, Function> _instructionDefinitions = new();
    private readonly int _maxFunctions;
    private GraphicsState _defaultState = GraphicsState.Default;

    private readonly record struct Function(byte[] Code, int Start);

    /// <summary>The graphics state of the specification, with FreeType's defaults.</summary>
    private struct GraphicsState
    {
        public int ProjX, ProjY, FreeX, FreeY, DualX, DualY;
        public int Rp0, Rp1, Rp2;
        public int Gep0, Gep1, Gep2;
        public int Loop;
        public int MinimumDistance, ControlValueCutIn, SingleWidthCutIn, SingleWidthValue;
        public int DeltaBase, DeltaShift;
        public bool AutoFlip;
        public int RoundState; // 0 half grid, 1 grid, 2 double grid, 3 down, 4 up, 5 off, 6 super, 7 super 45
        public int Period, Phase, Threshold;
        public int InstructControl;

        public static GraphicsState Default => new()
        {
            ProjX = Unit, FreeX = Unit, DualX = Unit,
            Gep0 = 1, Gep1 = 1, Gep2 = 1,
            Loop = 1, MinimumDistance = One, ControlValueCutIn = 68,
            DeltaBase = 9, DeltaShift = 3, AutoFlip = true, RoundState = 1,
            Period = One, Threshold = 32,
        };
    }

    /// <summary>The points of one glyph (with its four phantom points last) or of the twilight zone.</summary>
    internal sealed class Zone
    {
        public const byte TouchX = 1, TouchY = 2, OnCurve = 4;
        public int[] CurX, CurY, OrgX, OrgY, OrusX, OrusY;
        public byte[] Flags;
        public int[] Contours;
        public int Count => CurX.Length;

        public Zone(int count, int[] contours)
        {
            CurX = new int[count];
            CurY = new int[count];
            OrgX = new int[count];
            OrgY = new int[count];
            OrusX = new int[count];
            OrusY = new int[count];
            Flags = new byte[count];
            Contours = contours;
        }
    }

    private TrueTypeInterpreter(int ppem, int maxStack, int cvtLength, int maxStorage, int maxTwilight, int maxFunctions)
    {
        _ppem = ppem;
        _stackSize = Math.Clamp(maxStack + StackSlack, MinStack, MaxStack);
        _cvt = new int[cvtLength];
        _storage = new int[Math.Clamp(maxStorage, 0, MaxStorage)];
        int twilight = Math.Clamp(maxTwilight, 0, MaxTwilightPoints);
        _twilightX = new int[twilight];
        _twilightY = new int[twilight];
        _twilightOrgX = new int[twilight];
        _twilightOrgY = new int[twilight];
        _maxFunctions = Math.Clamp(maxFunctions, 64, MaxFunctions);
    }

    /// <summary>
    /// Runs the font program and the control value program at one pixel per font unit. Null when either fails, so the
    /// font is drawn without instructions.
    /// </summary>
    public static TrueTypeInterpreter? Create(byte[]? fpgm, byte[]? prep, short[] cvt, int unitsPerEm, int maxStack, int maxStorage, int maxTwilight, int maxFunctions)
    {
        var interpreter = new TrueTypeInterpreter(unitsPerEm, maxStack, cvt.Length, maxStorage, maxTwilight, maxFunctions);
        // One pixel per font unit: a control value in font units is the same number of pixels.
        for (int i = 0; i < cvt.Length; i++) interpreter._cvt[i] = cvt[i] * One;
        var empty = new Zone(0, Array.Empty<int>());
        if (fpgm is { Length: > 0 })
        {
            var run = new Execution(interpreter, empty, CodeRange.Font, interpreter._cvt, interpreter._storage, GraphicsState.Default);
            if (!run.Execute(fpgm)) return null;
        }
        if (prep is { Length: > 0 })
        {
            var run = new Execution(interpreter, empty, CodeRange.ControlValue, interpreter._cvt, interpreter._storage, GraphicsState.Default);
            if (!run.Execute(prep)) return null;
            interpreter._defaultState = run.State;
        }
        return interpreter;
    }

    /// <summary>True when the control value program asked for glyph programs not to run.</summary>
    public bool GlyphProgramsDisabled => (_defaultState.InstructControl & 1) != 0;

    /// <summary>
    /// Runs a glyph program on <paramref name="zone"/>. Each run starts from the state the control value program
    /// left, with its own copy of the control values, storage and twilight points, so glyphs never affect each other
    /// and may be hinted on several threads at once.
    /// </summary>
    public void RunGlyph(byte[] program, Zone zone)
    {
        if (program.Length == 0 || GlyphProgramsDisabled) return;
        var state = (_defaultState.InstructControl & 2) != 0 ? GraphicsState.Default : _defaultState;
        // As FreeType: each glyph starts in the glyph zone with the vectors on the x axis.
        state.Gep0 = state.Gep1 = state.Gep2 = 1;
        state.ProjX = state.FreeX = state.DualX = Unit;
        state.ProjY = state.FreeY = state.DualY = 0;
        state.RoundState = 1;
        state.Loop = 1;
        var run = new Execution(this, zone, CodeRange.Glyph, (int[])_cvt.Clone(), (int[])_storage.Clone(), state);
        run.Execute(program);
    }

    private enum CodeRange { Font, ControlValue, Glyph }

    private sealed class TrueTypeError() : Exception("TrueType instruction error");

    /// <summary>One run of a program: its stack, graphics state, zones and budget.</summary>
    private sealed class Execution
    {
        private readonly TrueTypeInterpreter _font;
        private readonly CodeRange _range;
        private readonly int[] _cvt, _storage;
        private readonly int[] _stack;
        private int _top;
        private readonly Zone _glyph, _twilight;
        private Zone _zp0, _zp1, _zp2;
        private GraphicsState _gs;
        private int _fDotP;
        private long _budget = InstructionBudget;
        private byte[] _code = Array.Empty<byte>();
        private int _ip;
        private int _opcode;
        private readonly Stack<Frame> _calls = new();

        private readonly record struct Frame(byte[] CallerCode, int ReturnIp, Function Function, int Count);

        public GraphicsState State => _gs;

        public Execution(TrueTypeInterpreter font, Zone glyph, CodeRange range, int[] cvt, int[] storage, GraphicsState state)
        {
            _font = font;
            _range = range;
            _cvt = cvt;
            _storage = storage;
            _stack = new int[font._stackSize];
            _glyph = glyph;
            _twilight = new Zone(font._twilightX.Length, Array.Empty<int>());
            if (range == CodeRange.Glyph)
            {
                Array.Copy(font._twilightX, _twilight.CurX, _twilight.Count);
                Array.Copy(font._twilightY, _twilight.CurY, _twilight.Count);
                Array.Copy(font._twilightOrgX, _twilight.OrgX, _twilight.Count);
                Array.Copy(font._twilightOrgY, _twilight.OrgY, _twilight.Count);
            }
            _gs = state;
            _zp0 = ZoneOf(_gs.Gep0);
            _zp1 = ZoneOf(_gs.Gep1);
            _zp2 = ZoneOf(_gs.Gep2);
            ComputeFunctions();
        }

        /// <summary>Runs <paramref name="code"/>; false when it failed (the points keep what it did before that).</summary>
        public bool Execute(byte[] code)
        {
            _code = code;
            _ip = 0;
            try
            {
                Run();
                if (_range == CodeRange.ControlValue) SaveTwilight();
                return true;
            }
            catch (Exception ex) when (ex is TrueTypeError or IndexOutOfRangeException or OverflowException or DivideByZeroException)
            {
                SwallowedErrors.Record(ex);
                return false;
            }
        }

        // The twilight points the control value program set are where every glyph's twilight zone starts.
        private void SaveTwilight()
        {
            Array.Copy(_twilight.CurX, _font._twilightX, _twilight.Count);
            Array.Copy(_twilight.CurY, _font._twilightY, _twilight.Count);
            Array.Copy(_twilight.OrgX, _font._twilightOrgX, _twilight.Count);
            Array.Copy(_twilight.OrgY, _font._twilightOrgY, _twilight.Count);
        }

        private static TrueTypeError Fail() => new();

        private Zone ZoneOf(int number) => number switch
        {
            0 => _twilight,
            1 => _glyph,
            _ => throw Fail(),
        };

        // ------------------------------------------------------------------ stack

        private int Pop() => _top > 0 ? _stack[--_top] : throw Fail();

        private void Push(int value)
        {
            if (_top >= _stack.Length) throw Fail();
            _stack[_top++] = value;
        }

        // ------------------------------------------------------------------ fixed-point arithmetic, as FreeType

        private static int MulDiv(long a, long b, long c)
        {
            int sign = 1;
            if (a < 0) { a = -a; sign = -sign; }
            if (b < 0) { b = -b; sign = -sign; }
            if (c < 0) { c = -c; sign = -sign; }
            long d = c > 0 ? (a * b + (c >> 1)) / c : int.MaxValue;
            return (int)Math.Clamp(sign * d, int.MinValue, int.MaxValue);
        }

        private static int MulDivNoRound(long a, long b, long c)
        {
            int sign = 1;
            if (a < 0) { a = -a; sign = -sign; }
            if (b < 0) { b = -b; sign = -sign; }
            if (c < 0) { c = -c; sign = -sign; }
            long d = c > 0 ? a * b / c : int.MaxValue;
            return (int)Math.Clamp(sign * d, int.MinValue, int.MaxValue);
        }

        // a * b / 2^14, rounded as FreeType's TT_MulFix14 and TT_DotFix14 round.
        private static int MulFix14(long product)
        {
            product += 0x2000 + (product >> 63);
            return (int)Math.Clamp(product >> 14, int.MinValue, int.MaxValue);
        }

        private int Project(int dx, int dy) => MulFix14((long)dx * _gs.ProjX + (long)dy * _gs.ProjY);
        private int DualProject(int dx, int dy) => MulFix14((long)dx * _gs.DualX + (long)dy * _gs.DualY);

        private void ComputeFunctions()
        {
            if (_gs.FreeX == Unit) _fDotP = _gs.ProjX;
            else if (_gs.FreeY == Unit) _fDotP = _gs.ProjY;
            else _fDotP = (int)(((long)_gs.ProjX * _gs.FreeX + (long)_gs.ProjY * _gs.FreeY) >> 14);
            if (Math.Abs(_fDotP) < 0x400) _fDotP = Unit;
        }

        private void Move(Zone zone, int point, int distance)
        {
            if (_gs.FreeX != 0)
            {
                zone.CurX[point] += MulDiv(distance, _gs.FreeX, _fDotP);
                zone.Flags[point] |= Zone.TouchX;
            }
            if (_gs.FreeY != 0)
            {
                zone.CurY[point] += MulDiv(distance, _gs.FreeY, _fDotP);
                zone.Flags[point] |= Zone.TouchY;
            }
        }

        private void MoveOriginal(Zone zone, int point, int distance)
        {
            if (_gs.FreeX != 0) zone.OrgX[point] += MulDiv(distance, _gs.FreeX, _fDotP);
            if (_gs.FreeY != 0) zone.OrgY[point] += MulDiv(distance, _gs.FreeY, _fDotP);
        }

        private int Round(int distance)
        {
            int value;
            switch (_gs.RoundState)
            {
                case 0: // half grid
                    if (distance >= 0) { value = (distance & -One) + 32; if (value < 0) value = 32; }
                    else { value = -((-distance & -One) + 32); if (value > 0) value = -32; }
                    return value;
                case 1: // grid
                    if (distance >= 0) { value = (distance + 32) & -One; if (value < 0) value = 0; }
                    else { value = -((-distance + 32) & -One); if (value > 0) value = 0; }
                    return value;
                case 2: // double grid
                    if (distance >= 0) { value = (distance + 16) & -32; if (value < 0) value = 0; }
                    else { value = -((-distance + 16) & -32); if (value > 0) value = 0; }
                    return value;
                case 3: // down to grid
                    if (distance >= 0) { value = distance & -One; if (value < 0) value = 0; }
                    else { value = -(-distance & -One); if (value > 0) value = 0; }
                    return value;
                case 4: // up to grid
                    if (distance >= 0) { value = (distance + 63) & -One; if (value < 0) value = 0; }
                    else { value = -((-distance + 63) & -One); if (value > 0) value = 0; }
                    return value;
                case 6: // super
                    if (_gs.Period == 0) return distance;
                    if (distance >= 0)
                    {
                        value = ((distance + (_gs.Threshold - _gs.Phase)) & -_gs.Period) + _gs.Phase;
                        if (value < 0) value = _gs.Phase;
                    }
                    else
                    {
                        value = -(((_gs.Threshold - _gs.Phase) - distance) & -_gs.Period) - _gs.Phase;
                        if (value > 0) value = -_gs.Phase;
                    }
                    return value;
                case 7: // super 45 degrees
                    if (_gs.Period == 0) return distance;
                    if (distance >= 0)
                    {
                        value = (distance + (_gs.Threshold - _gs.Phase)) / _gs.Period * _gs.Period + _gs.Phase;
                        if (value < 0) value = _gs.Phase;
                    }
                    else
                    {
                        value = -(((_gs.Threshold - _gs.Phase) - distance) / _gs.Period * _gs.Period) - _gs.Phase;
                        if (value > 0) value = -_gs.Phase;
                    }
                    return value;
                default:
                    return distance;
            }
        }

        private void SetSuperRound(int gridPeriod, int selector)
        {
            int period = (selector & 0xC0) switch
            {
                0 => gridPeriod / 2,
                0x80 => gridPeriod * 2,
                _ => gridPeriod,
            };
            int phase = (selector & 0x30) switch
            {
                0 => 0,
                0x10 => period / 4,
                0x20 => period / 2,
                _ => period * 3 / 4,
            };
            int threshold = (selector & 0x0F) == 0 ? period - 1 : ((selector & 0x0F) - 4) * period / 8;
            _gs.Period = period >> 8;
            _gs.Phase = phase >> 8;
            _gs.Threshold = threshold >> 8;
        }

        private static void Normalize(int x, int y, out int vx, out int vy)
        {
            double length = Math.Sqrt((double)x * x + (double)y * y);
            if (length == 0)
            {
                vx = Unit;
                vy = 0;
                return;
            }
            vx = (int)Math.Round(x * Unit / length, MidpointRounding.AwayFromZero);
            vy = (int)Math.Round(y * Unit / length, MidpointRounding.AwayFromZero);
        }

        private static bool OutOf(int point, Zone zone) => (uint)point >= (uint)zone.Count;

        private int ReadCvt(int index) => (uint)index < (uint)_cvt.Length ? _cvt[index] : 0;

        // ------------------------------------------------------------------ the instruction loop

        private void Spend(long cost)
        {
            _budget -= cost;
            if (_budget < 0) throw Fail();
        }

        private void Run()
        {
            while (true)
            {
                if (_ip >= _code.Length)
                {
                    if (_calls.Count > 0) throw Fail(); // a function without ENDF
                    return;
                }
                Spend(1);
                _opcode = _code[_ip];
                int next = _ip + InstructionLength(_code, _ip);
                if (Step()) _ip = next;
            }
        }

        // The length of the instruction at ip with its inline data.
        private static int InstructionLength(byte[] code, int ip)
        {
            int op = code[ip];
            if (op == 0x40) return ip + 1 < code.Length ? 2 + code[ip + 1] : throw Fail();
            if (op == 0x41) return ip + 1 < code.Length ? 2 + 2 * code[ip + 1] : throw Fail();
            if (op is >= 0xB0 and <= 0xB7) return 1 + (op - 0xAF);
            if (op is >= 0xB8 and <= 0xBF) return 1 + 2 * (op - 0xB7);
            return 1;
        }

        // Runs the instruction at _ip; false when it moved _ip itself.
        private bool Step()
        {
            int op = _opcode;
            switch (op)
            {
                case 0x00: case 0x01: // SVTCA
                case 0x02: case 0x03: // SPVTCA
                case 0x04: case 0x05: // SFVTCA
                {
                    int x = (op & 1) != 0 ? Unit : 0, y = (op & 1) != 0 ? 0 : Unit;
                    if (op < 4) { _gs.ProjX = _gs.DualX = x; _gs.ProjY = _gs.DualY = y; }
                    if (op is < 2 or >= 4) { _gs.FreeX = x; _gs.FreeY = y; }
                    ComputeFunctions();
                    return true;
                }
                case 0x06: case 0x07: // SPVTL
                case 0x08: case 0x09: // SFVTL
                {
                    int p2 = Pop(), p1 = Pop();
                    if (OutOf(p1, _zp1) || OutOf(p2, _zp2)) return true;
                    int a = _zp1.CurX[p1] - _zp2.CurX[p2], b = _zp1.CurY[p1] - _zp2.CurY[p2];
                    bool rotate = (op & 1) != 0;
                    if (a == 0 && b == 0) { a = Unit; rotate = false; }
                    if (rotate) (a, b) = (-b, a);
                    Normalize(a, b, out int vx, out int vy);
                    if (op < 8) { _gs.ProjX = _gs.DualX = vx; _gs.ProjY = _gs.DualY = vy; }
                    else { _gs.FreeX = vx; _gs.FreeY = vy; }
                    ComputeFunctions();
                    return true;
                }
                case 0x0A: case 0x0B: // SPVFS, SFVFS
                {
                    int y = (short)Pop(), x = (short)Pop();
                    Normalize(x, y, out int vx, out int vy);
                    if (op == 0x0A) { _gs.ProjX = _gs.DualX = vx; _gs.ProjY = _gs.DualY = vy; }
                    else { _gs.FreeX = vx; _gs.FreeY = vy; }
                    ComputeFunctions();
                    return true;
                }
                case 0x0C: Push(_gs.ProjX); Push(_gs.ProjY); return true; // GPV
                case 0x0D: Push(_gs.FreeX); Push(_gs.FreeY); return true; // GFV
                case 0x0E: _gs.FreeX = _gs.ProjX; _gs.FreeY = _gs.ProjY; ComputeFunctions(); return true; // SFVTPV
                case 0x0F: Intersect(); return true; // ISECT
                case 0x10: _gs.Rp0 = Pop(); return true;
                case 0x11: _gs.Rp1 = Pop(); return true;
                case 0x12: _gs.Rp2 = Pop(); return true;
                case 0x13: _gs.Gep0 = Pop(); _zp0 = ZoneOf(_gs.Gep0); return true; // SZP0
                case 0x14: _gs.Gep1 = Pop(); _zp1 = ZoneOf(_gs.Gep1); return true;
                case 0x15: _gs.Gep2 = Pop(); _zp2 = ZoneOf(_gs.Gep2); return true;
                case 0x16: // SZPS
                {
                    int zone = Pop();
                    _zp0 = _zp1 = _zp2 = ZoneOf(zone);
                    _gs.Gep0 = _gs.Gep1 = _gs.Gep2 = zone;
                    return true;
                }
                case 0x17: // SLOOP
                {
                    int count = Pop();
                    if (count < 0) throw Fail();
                    _gs.Loop = Math.Min(count, 0xFFFF);
                    return true;
                }
                case 0x18: _gs.RoundState = 1; return true; // RTG
                case 0x19: _gs.RoundState = 0; return true; // RTHG
                case 0x1A: _gs.MinimumDistance = Pop(); return true; // SMD
                case 0x1B: SkipBranch(elseEnds: false); return false; // ELSE: skip to EIF
                case 0x1C: return Jump(Pop()); // JMPR
                case 0x1D: _gs.ControlValueCutIn = Pop(); return true; // SCVTCI
                case 0x1E: _gs.SingleWidthCutIn = Pop(); return true; // SSWCI
                case 0x1F: _gs.SingleWidthValue = (int)Math.Clamp((long)Pop() * One, int.MinValue, int.MaxValue); return true; // SSW
                case 0x20: { int v = Pop(); Push(v); Push(v); return true; } // DUP
                case 0x21: Pop(); return true; // POP
                case 0x22: _top = 0; return true; // CLEAR
                case 0x23: { int b = Pop(), a = Pop(); Push(b); Push(a); return true; } // SWAP
                case 0x24: Push(_top); return true; // DEPTH
                case 0x25: // CINDEX
                {
                    int k = Pop();
                    if (k <= 0 || k > _top) throw Fail();
                    Push(_stack[_top - k]);
                    return true;
                }
                case 0x26: // MINDEX
                {
                    int k = Pop();
                    if (k <= 0 || k > _top) throw Fail();
                    int value = _stack[_top - k];
                    Array.Copy(_stack, _top - k + 1, _stack, _top - k, k - 1);
                    _stack[_top - 1] = value;
                    return true;
                }
                case 0x27: // ALIGNPTS
                {
                    int p2 = Pop(), p1 = Pop();
                    if (OutOf(p1, _zp1) || OutOf(p2, _zp0)) return true;
                    int distance = Project(_zp0.CurX[p2] - _zp1.CurX[p1], _zp0.CurY[p2] - _zp1.CurY[p1]) / 2;
                    Move(_zp1, p1, distance);
                    Move(_zp0, p2, -distance);
                    return true;
                }
                case 0x29: // UTP
                {
                    int point = Pop();
                    if (OutOf(point, _zp0)) return true;
                    byte mask = 0xFF;
                    if (_gs.FreeX != 0) mask &= unchecked((byte)~Zone.TouchX);
                    if (_gs.FreeY != 0) mask &= unchecked((byte)~Zone.TouchY);
                    _zp0.Flags[point] &= mask;
                    return true;
                }
                case 0x2A: // LOOPCALL
                {
                    int number = Pop(), count = Pop();
                    if (count <= 0) return true;
                    return Call(number, count);
                }
                case 0x2B: return Call(Pop(), 1); // CALL
                case 0x2C: DefineFunction(_font._functions); return false; // FDEF
                case 0x2D: return EndFunction(); // ENDF
                case 0x2E: case 0x2F: // MDAP
                {
                    int point = Pop();
                    if (OutOf(point, _zp0)) return true;
                    int distance = 0;
                    if ((op & 1) != 0)
                    {
                        int current = Project(_zp0.CurX[point], _zp0.CurY[point]);
                        distance = Round(current) - current;
                    }
                    Move(_zp0, point, distance);
                    _gs.Rp0 = _gs.Rp1 = point;
                    return true;
                }
                case 0x30: case 0x31: InterpolateUntouched((op & 1) != 0); return true; // IUP
                case 0x32: case 0x33: ShiftPoints(); return true; // SHP
                case 0x34: case 0x35: ShiftContour(); return true; // SHC
                case 0x36: case 0x37: ShiftZone(); return true; // SHZ
                case 0x38: ShiftPixels(); return true; // SHPIX
                case 0x39: InterpolatePoints(); return true; // IP
                case 0x3A: case 0x3B: // MSIRP
                {
                    int distance = Pop(), point = Pop();
                    if (OutOf(point, _zp1) || OutOf(_gs.Rp0, _zp0)) return true;
                    if (_gs.Gep1 == 0)
                    {
                        _zp1.OrgX[point] = _zp0.OrgX[_gs.Rp0];
                        _zp1.OrgY[point] = _zp0.OrgY[_gs.Rp0];
                        MoveOriginal(_zp1, point, distance);
                        _zp1.CurX[point] = _zp1.OrgX[point];
                        _zp1.CurY[point] = _zp1.OrgY[point];
                    }
                    int current = Project(_zp1.CurX[point] - _zp0.CurX[_gs.Rp0], _zp1.CurY[point] - _zp0.CurY[_gs.Rp0]);
                    Move(_zp1, point, distance - current);
                    _gs.Rp1 = _gs.Rp0;
                    _gs.Rp2 = point;
                    if ((op & 1) != 0) _gs.Rp0 = point;
                    return true;
                }
                case 0x3C: // ALIGNRP
                    for (; _gs.Loop > 0; _gs.Loop--)
                    {
                        Spend(1);
                        int point = Pop();
                        if (OutOf(point, _zp1) || OutOf(_gs.Rp0, _zp0)) continue;
                        int distance = Project(_zp1.CurX[point] - _zp0.CurX[_gs.Rp0], _zp1.CurY[point] - _zp0.CurY[_gs.Rp0]);
                        Move(_zp1, point, -distance);
                    }
                    _gs.Loop = 1;
                    return true;
                case 0x3D: _gs.RoundState = 2; return true; // RTDG
                case 0x3E: case 0x3F: // MIAP
                {
                    int entry = Pop(), point = Pop();
                    if (OutOf(point, _zp0)) return true;
                    int distance = ReadCvt(entry);
                    if (_gs.Gep0 == 0)
                    {
                        _zp0.OrgX[point] = MulFix14((long)distance * _gs.FreeX);
                        _zp0.OrgY[point] = MulFix14((long)distance * _gs.FreeY);
                        _zp0.CurX[point] = _zp0.OrgX[point];
                        _zp0.CurY[point] = _zp0.OrgY[point];
                    }
                    int original = Project(_zp0.CurX[point], _zp0.CurY[point]);
                    if ((op & 1) != 0)
                    {
                        if (Math.Abs(distance - original) > _gs.ControlValueCutIn) distance = original;
                        distance = Round(distance);
                    }
                    Move(_zp0, point, distance - original);
                    _gs.Rp0 = _gs.Rp1 = point;
                    return true;
                }
                case 0x40: case 0x41: case >= 0xB0 and <= 0xBF: PushInline(op); return true;
                case 0x42: // WS
                {
                    int value = Pop(), index = Pop();
                    if ((uint)index < (uint)_storage.Length) _storage[index] = value;
                    return true;
                }
                case 0x43: { int index = Pop(); Push((uint)index < (uint)_storage.Length ? _storage[index] : 0); return true; } // RS
                case 0x44: { int value = Pop(), index = Pop(); if ((uint)index < (uint)_cvt.Length) _cvt[index] = value; return true; } // WCVTP
                case 0x45: Push(ReadCvt(Pop())); return true; // RCVT
                case 0x46: case 0x47: // GC
                {
                    int point = Pop();
                    if (OutOf(point, _zp2)) { Push(0); return true; }
                    Push((op & 1) != 0 ? DualProject(_zp2.OrgX[point], _zp2.OrgY[point]) : Project(_zp2.CurX[point], _zp2.CurY[point]));
                    return true;
                }
                case 0x48: // SCFS
                {
                    int value = Pop(), point = Pop();
                    if (OutOf(point, _zp2)) return true;
                    int current = Project(_zp2.CurX[point], _zp2.CurY[point]);
                    Move(_zp2, point, value - current);
                    if (_gs.Gep2 == 0)
                    {
                        _zp2.OrgX[point] = _zp2.CurX[point];
                        _zp2.OrgY[point] = _zp2.CurY[point];
                    }
                    return true;
                }
                case 0x49: case 0x4A: // MD
                {
                    int k = Pop(), l = Pop();
                    if (OutOf(l, _zp0) || OutOf(k, _zp1)) { Push(0); return true; }
                    int distance;
                    if ((op & 1) != 0) distance = Project(_zp0.CurX[l] - _zp1.CurX[k], _zp0.CurY[l] - _zp1.CurY[k]);
                    else if (_gs.Gep0 == 0 || _gs.Gep1 == 0) distance = DualProject(_zp0.OrgX[l] - _zp1.OrgX[k], _zp0.OrgY[l] - _zp1.OrgY[k]);
                    else distance = DualProject(_zp0.OrusX[l] - _zp1.OrusX[k], _zp0.OrusY[l] - _zp1.OrusY[k]) * One;
                    Push(distance);
                    return true;
                }
                case 0x4B: case 0x4C: Push(_font._ppem); return true; // MPPEM, MPS
                case 0x4D: _gs.AutoFlip = true; return true;
                case 0x4E: _gs.AutoFlip = false; return true;
                case 0x4F: Pop(); return true; // DEBUG
                case 0x50: { int b = Pop(), a = Pop(); Push(a < b ? 1 : 0); return true; }
                case 0x51: { int b = Pop(), a = Pop(); Push(a <= b ? 1 : 0); return true; }
                case 0x52: { int b = Pop(), a = Pop(); Push(a > b ? 1 : 0); return true; }
                case 0x53: { int b = Pop(), a = Pop(); Push(a >= b ? 1 : 0); return true; }
                case 0x54: { int b = Pop(), a = Pop(); Push(a == b ? 1 : 0); return true; }
                case 0x55: { int b = Pop(), a = Pop(); Push(a != b ? 1 : 0); return true; }
                case 0x56: Push((Round(Pop()) & 127) == 64 ? 1 : 0); return true; // ODD
                case 0x57: Push((Round(Pop()) & 127) == 0 ? 1 : 0); return true; // EVEN
                case 0x58: // IF
                    if (Pop() != 0) return true;
                    SkipBranch(elseEnds: true);
                    return false;
                case 0x59: return true; // EIF
                case 0x5A: { int b = Pop(), a = Pop(); Push(a != 0 && b != 0 ? 1 : 0); return true; }
                case 0x5B: { int b = Pop(), a = Pop(); Push(a != 0 || b != 0 ? 1 : 0); return true; }
                case 0x5C: Push(Pop() == 0 ? 1 : 0); return true;
                case 0x5D: case 0x71: case 0x72: DeltaPoints(op == 0x5D ? 0 : op == 0x71 ? 16 : 32); return true;
                case 0x5E: _gs.DeltaBase = Pop(); return true; // SDB
                case 0x5F: _gs.DeltaShift = Pop(); if ((uint)_gs.DeltaShift > 6) throw Fail(); return true; // SDS
                case 0x60: { int b = Pop(), a = Pop(); Push(unchecked(a + b)); return true; }
                case 0x61: { int b = Pop(), a = Pop(); Push(unchecked(a - b)); return true; }
                case 0x62: { int b = Pop(), a = Pop(); if (b == 0) throw Fail(); Push(MulDivNoRound(a, One, b)); return true; }
                case 0x63: { int b = Pop(), a = Pop(); Push(MulDiv(a, b, One)); return true; }
                case 0x64: { int a = Pop(); Push(a == int.MinValue ? int.MaxValue : Math.Abs(a)); return true; }
                case 0x65: Push(unchecked(-Pop())); return true;
                case 0x66: Push(Pop() & -One); return true; // FLOOR
                case 0x67: Push(unchecked(Pop() + 63) & -One); return true; // CEILING
                case >= 0x68 and <= 0x6B: Push(Round(Pop())); return true; // ROUND
                case >= 0x6C and <= 0x6F: Pop(); Push(_stack[_top]); return true; // NROUND: no engine compensation
                case 0x70: // WCVTF
                {
                    int value = Pop(), index = Pop();
                    if ((uint)index < (uint)_cvt.Length) _cvt[index] = (int)Math.Clamp((long)value * One, int.MinValue, int.MaxValue);
                    return true;
                }
                case 0x73: case 0x74: case 0x75: DeltaControlValues(op == 0x73 ? 0 : op == 0x74 ? 16 : 32); return true;
                case 0x76: SetSuperRound(Unit, Pop()); _gs.RoundState = 6; return true; // SROUND
                case 0x77: SetSuperRound(0x2D41, Pop()); _gs.RoundState = 7; return true; // S45ROUND
                case 0x78: { int condition = Pop(), offset = Pop(); return condition == 0 || Jump(offset); } // JROT
                case 0x79: { int condition = Pop(), offset = Pop(); return condition != 0 || Jump(offset); } // JROF
                case 0x7A: _gs.RoundState = 5; return true; // ROFF
                case 0x7C: _gs.RoundState = 4; return true; // RUTG
                case 0x7D: _gs.RoundState = 3; return true; // RDTG
                case 0x7E: case 0x7F: Pop(); return true; // SANGW, AA
                case 0x80: // FLIPPT
                    for (; _gs.Loop > 0; _gs.Loop--)
                    {
                        Spend(1);
                        int point = Pop();
                        if (!OutOf(point, _glyph)) _glyph.Flags[point] ^= Zone.OnCurve;
                    }
                    _gs.Loop = 1;
                    return true;
                case 0x81: case 0x82: // FLIPRGON, FLIPRGOFF
                {
                    int high = Pop(), low = Pop();
                    if (OutOf(high, _glyph) || OutOf(low, _glyph)) return true;
                    Spend(Math.Max(0, high - low));
                    for (int i = low; i <= high; i++)
                        _glyph.Flags[i] = op == 0x81 ? (byte)(_glyph.Flags[i] | Zone.OnCurve) : (byte)(_glyph.Flags[i] & ~Zone.OnCurve);
                    return true;
                }
                case 0x85: Pop(); return true; // SCANCTRL
                case 0x86: case 0x87: SetDualProjectionToLine(); return true; // SDPVTL
                case 0x88: // GETINFO: version 35, grayscale rendering
                {
                    int selector = Pop(), info = 0;
                    if ((selector & 1) != 0) info = 35;
                    if ((selector & 32) != 0) info |= 1 << 12;
                    Push(info);
                    return true;
                }
                case 0x89: DefineFunction(_font._instructionDefinitions); return false; // IDEF
                case 0x8A: { int a = Pop(), b = Pop(), c = Pop(); Push(b); Push(a); Push(c); return true; } // ROLL
                case 0x8B: { int b = Pop(), a = Pop(); Push(Math.Max(a, b)); return true; }
                case 0x8C: { int b = Pop(), a = Pop(); Push(Math.Min(a, b)); return true; }
                case 0x8D: Pop(); return true; // SCANTYPE
                case 0x8E: // INSTCTRL, only in the control value program
                {
                    int selector = Pop(), value = Pop();
                    if (_range != CodeRange.ControlValue || selector < 1 || selector > 3) return true;
                    int flag = 1 << (selector - 1);
                    _gs.InstructControl = value != 0 ? _gs.InstructControl | flag : _gs.InstructControl & ~flag;
                    return true;
                }
                case >= 0xC0 and <= 0xDF: MoveDirectRelative(); return true; // MDRP
                case >= 0xE0: MoveIndirectRelative(); return true; // MIRP
                default:
                    if (_font._instructionDefinitions.TryGetValue(op, out var definition)) return Enter(definition, 1);
                    throw Fail();
            }
        }

        // ------------------------------------------------------------------ flow

        private void PushInline(int op)
        {
            int count, at = _ip + 1;
            bool words;
            if (op == 0x40 || op == 0x41)
            {
                count = _code[at++];
                words = op == 0x41;
            }
            else
            {
                count = op <= 0xB7 ? op - 0xAF : op - 0xB7;
                words = op >= 0xB8;
            }
            if (at + count * (words ? 2 : 1) > _code.Length) throw Fail();
            for (int i = 0; i < count; i++)
            {
                if (words)
                {
                    Push((short)(_code[at] << 8 | _code[at + 1]));
                    at += 2;
                }
                else Push(_code[at++]);
            }
        }

        private bool Jump(int offset)
        {
            long target = (long)_ip + offset;
            if (target < 0 || target > _code.Length) throw Fail();
            _ip = (int)target;
            return false;
        }

        // From IF (elseEnds) or ELSE: moves past the matching ELSE or EIF, skipping nested IFs.
        private void SkipBranch(bool elseEnds)
        {
            int depth = 1, ip = _ip + InstructionLength(_code, _ip);
            while (ip < _code.Length)
            {
                Spend(1);
                int op = _code[ip];
                if (op == 0x58) depth++;
                else if (op == 0x59 && --depth == 0)
                {
                    _ip = ip + 1;
                    return;
                }
                else if (op == 0x1B && depth == 1 && elseEnds)
                {
                    _ip = ip + 1;
                    return;
                }
                ip += InstructionLength(_code, ip);
            }
            throw Fail();
        }

        // FDEF and IDEF: records where the body starts and moves past its ENDF.
        private void DefineFunction(Dictionary<int, Function> table)
        {
            if (_range == CodeRange.Glyph) throw Fail();
            int number = Pop();
            if (!table.ContainsKey(number) && table.Count >= _font._maxFunctions) throw Fail();
            int start = _ip + 1, ip = start;
            while (ip < _code.Length)
            {
                Spend(1);
                int op = _code[ip];
                if (op is 0x2C or 0x89) throw Fail();
                if (op == 0x2D)
                {
                    table[number] = new Function(_code, start);
                    _ip = ip + 1;
                    return;
                }
                ip += InstructionLength(_code, ip);
            }
            throw Fail();
        }

        private bool Call(int number, int count)
            => _font._functions.TryGetValue(number, out var function) ? Enter(function, count) : throw Fail();

        private bool Enter(Function function, int count)
        {
            if (_calls.Count >= MaxCallDepth) throw Fail();
            _calls.Push(new Frame(_code, _ip + 1, function, count));
            _code = function.Code;
            _ip = function.Start;
            return false;
        }

        private bool EndFunction()
        {
            if (_calls.Count == 0) throw Fail();
            var frame = _calls.Pop();
            if (frame.Count > 1)
            {
                _calls.Push(frame with { Count = frame.Count - 1 });
                _ip = frame.Function.Start;
                return false;
            }
            _code = frame.CallerCode;
            _ip = frame.ReturnIp;
            return false;
        }

        // ------------------------------------------------------------------ points

        private void Intersect()
        {
            int b1 = Pop(), b0 = Pop(), a1 = Pop(), a0 = Pop(), point = Pop();
            if (OutOf(b0, _zp0) || OutOf(b1, _zp0) || OutOf(a0, _zp1) || OutOf(a1, _zp1) || OutOf(point, _zp2)) return;
            long dbx = _zp0.CurX[b1] - _zp0.CurX[b0], dby = _zp0.CurY[b1] - _zp0.CurY[b0];
            long dax = _zp1.CurX[a1] - _zp1.CurX[a0], day = _zp1.CurY[a1] - _zp1.CurY[a0];
            long dx = _zp0.CurX[b0] - _zp1.CurX[a0], dy = _zp0.CurY[b0] - _zp1.CurY[a0];
            long discriminant = (long)MulDiv(dax, -dby, One) + MulDiv(day, dbx, One);
            long dot = (long)MulDiv(dax, dbx, One) + MulDiv(day, dby, One);
            if (19 * Math.Abs(discriminant) > Math.Abs(dot))
            {
                long value = (long)MulDiv(dx, -dby, One) + MulDiv(dy, dbx, One);
                _zp2.CurX[point] = _zp1.CurX[a0] + MulDiv(value, dax, discriminant);
                _zp2.CurY[point] = _zp1.CurY[a0] + MulDiv(value, day, discriminant);
            }
            else
            {
                _zp2.CurX[point] = (int)(((long)_zp1.CurX[a0] + _zp1.CurX[a1] + _zp0.CurX[b0] + _zp0.CurX[b1]) / 4);
                _zp2.CurY[point] = (int)(((long)_zp1.CurY[a0] + _zp1.CurY[a1] + _zp0.CurY[b0] + _zp0.CurY[b1]) / 4);
            }
            _zp2.Flags[point] |= Zone.TouchX | Zone.TouchY;
        }

        private void SetDualProjectionToLine()
        {
            int p1 = Pop(), p2 = Pop();
            if (OutOf(p2, _zp1) || OutOf(p1, _zp2)) return;
            bool rotate = (_opcode & 1) != 0;
            int a = _zp1.OrgX[p2] - _zp2.OrgX[p1], b = _zp1.OrgY[p2] - _zp2.OrgY[p1];
            bool r = rotate;
            if (a == 0 && b == 0) { a = Unit; b = 0; r = false; }
            if (r) (a, b) = (-b, a);
            Normalize(a, b, out _gs.DualX, out _gs.DualY);
            a = _zp1.CurX[p2] - _zp2.CurX[p1];
            b = _zp1.CurY[p2] - _zp2.CurY[p1];
            r = rotate;
            if (a == 0 && b == 0) { a = Unit; b = 0; r = false; }
            if (r) (a, b) = (-b, a);
            Normalize(a, b, out _gs.ProjX, out _gs.ProjY);
            ComputeFunctions();
        }

        // How far the reference point of SHP, SHC and SHZ has moved, along the freedom vector.
        private bool PointDisplacement(out int dx, out int dy, out Zone zone, out int reference)
        {
            if ((_opcode & 1) != 0) { zone = _zp0; reference = _gs.Rp1; }
            else { zone = _zp1; reference = _gs.Rp2; }
            dx = dy = 0;
            if (OutOf(reference, zone)) return false;
            int d = Project(zone.CurX[reference] - zone.OrgX[reference], zone.CurY[reference] - zone.OrgY[reference]);
            dx = MulDiv(d, _gs.FreeX, _fDotP);
            dy = MulDiv(d, _gs.FreeY, _fDotP);
            return true;
        }

        private void MoveZp2(int point, int dx, int dy, bool touch)
        {
            if (_gs.FreeX != 0)
            {
                _zp2.CurX[point] += dx;
                if (touch) _zp2.Flags[point] |= Zone.TouchX;
            }
            if (_gs.FreeY != 0)
            {
                _zp2.CurY[point] += dy;
                if (touch) _zp2.Flags[point] |= Zone.TouchY;
            }
        }

        private void ShiftPoints()
        {
            bool valid = PointDisplacement(out int dx, out int dy, out _, out _);
            for (; _gs.Loop > 0; _gs.Loop--)
            {
                Spend(1);
                int point = Pop();
                if (valid && !OutOf(point, _zp2)) MoveZp2(point, dx, dy, touch: true);
            }
            _gs.Loop = 1;
        }

        private void ShiftContour()
        {
            int contour = Pop();
            if (!PointDisplacement(out int dx, out int dy, out var zone, out int reference)) return;
            if ((uint)contour >= (uint)_zp2.Contours.Length) return;
            int start = contour == 0 ? 0 : _zp2.Contours[contour - 1] + 1;
            int limit = Math.Min(_zp2.Contours[contour], _zp2.Count - 1);
            Spend(Math.Max(0, limit - start + 1));
            for (int i = start; i <= limit; i++)
                if (zone != _zp2 || i != reference) MoveZp2(i, dx, dy, touch: true);
        }

        private void ShiftZone()
        {
            var shifted = ZoneOf(Pop());
            if (!PointDisplacement(out int dx, out int dy, out var zone, out int reference)) return;
            // The phantom points stay where they are: the glyph zone shifts up to the end of its last contour.
            int limit = shifted == _twilight ? shifted.Count : shifted.Contours.Length > 0 ? Math.Min(shifted.Contours[^1] + 1, shifted.Count) : 0;
            Spend(limit);
            var saved = _zp2;
            _zp2 = shifted;
            for (int i = 0; i < limit; i++)
                if (zone != shifted || i != reference) MoveZp2(i, dx, dy, touch: false);
            _zp2 = saved;
        }

        private void ShiftPixels()
        {
            int distance = Pop();
            int dx = MulFix14((long)distance * _gs.FreeX), dy = MulFix14((long)distance * _gs.FreeY);
            for (; _gs.Loop > 0; _gs.Loop--)
            {
                Spend(1);
                int point = Pop();
                if (!OutOf(point, _zp2)) MoveZp2(point, dx, dy, touch: true);
            }
            _gs.Loop = 1;
        }

        private void InterpolatePoints()
        {
            bool twilight = _gs.Gep0 == 0 || _gs.Gep1 == 0 || _gs.Gep2 == 0;
            int rp1 = _gs.Rp1, rp2 = _gs.Rp2;
            long oldRange = 0, currentRange = 0;
            bool valid = !OutOf(rp1, _zp0);
            int baseX = 0, baseY = 0, curBaseX = 0, curBaseY = 0;
            if (valid)
            {
                baseX = twilight ? _zp0.OrgX[rp1] : _zp0.OrusX[rp1];
                baseY = twilight ? _zp0.OrgY[rp1] : _zp0.OrusY[rp1];
                curBaseX = _zp0.CurX[rp1];
                curBaseY = _zp0.CurY[rp1];
                if (!OutOf(rp2, _zp1))
                {
                    oldRange = twilight
                        ? DualProject(_zp1.OrgX[rp2] - baseX, _zp1.OrgY[rp2] - baseY)
                        : DualProject(_zp1.OrusX[rp2] - baseX, _zp1.OrusY[rp2] - baseY);
                    currentRange = Project(_zp1.CurX[rp2] - curBaseX, _zp1.CurY[rp2] - curBaseY);
                }
            }
            for (; _gs.Loop > 0; _gs.Loop--)
            {
                Spend(1);
                int point = Pop();
                if (!valid || OutOf(point, _zp2)) continue;
                int original = twilight
                    ? DualProject(_zp2.OrgX[point] - baseX, _zp2.OrgY[point] - baseY)
                    : DualProject(_zp2.OrusX[point] - baseX, _zp2.OrusY[point] - baseY);
                int current = Project(_zp2.CurX[point] - curBaseX, _zp2.CurY[point] - curBaseY);
                int wanted = original == 0 ? 0 : oldRange != 0 ? MulDiv(original, currentRange, oldRange) : original;
                Move(_zp2, point, wanted - current);
            }
            _gs.Loop = 1;
        }

        private void MoveDirectRelative()
        {
            int point = Pop();
            int rp0 = _gs.Rp0;
            if (OutOf(point, _zp1) || OutOf(rp0, _zp0)) return;
            int original = _gs.Gep0 == 0 || _gs.Gep1 == 0
                ? DualProject(_zp1.OrgX[point] - _zp0.OrgX[rp0], _zp1.OrgY[point] - _zp0.OrgY[rp0])
                : DualProject(_zp1.OrusX[point] - _zp0.OrusX[rp0], _zp1.OrusY[point] - _zp0.OrusY[rp0]) * One;
            if (_gs.SingleWidthCutIn > 0 && original < _gs.SingleWidthValue + _gs.SingleWidthCutIn && original > _gs.SingleWidthValue - _gs.SingleWidthCutIn)
                original = original >= 0 ? _gs.SingleWidthValue : -_gs.SingleWidthValue;
            int distance = (_opcode & 4) != 0 ? Round(original) : RoundNone(original);
            if ((_opcode & 8) != 0) distance = KeepMinimum(distance, original);
            int current = Project(_zp1.CurX[point] - _zp0.CurX[rp0], _zp1.CurY[point] - _zp0.CurY[rp0]);
            Move(_zp1, point, distance - current);
            _gs.Rp1 = rp0;
            _gs.Rp2 = point;
            if ((_opcode & 16) != 0) _gs.Rp0 = point;
        }

        private void MoveIndirectRelative()
        {
            int entry = Pop() + 1, point = Pop();
            int rp0 = _gs.Rp0;
            if (OutOf(point, _zp1) || (uint)entry > (uint)_cvt.Length || OutOf(rp0, _zp0)) return;
            int cvt = entry == 0 ? 0 : _cvt[entry - 1];
            if (Math.Abs(cvt - _gs.SingleWidthValue) < _gs.SingleWidthCutIn) cvt = cvt >= 0 ? _gs.SingleWidthValue : -_gs.SingleWidthValue;
            if (_gs.Gep1 == 0)
            {
                _zp1.OrgX[point] = _zp0.OrgX[rp0] + MulFix14((long)cvt * _gs.FreeX);
                _zp1.OrgY[point] = _zp0.OrgY[rp0] + MulFix14((long)cvt * _gs.FreeY);
                _zp1.CurX[point] = _zp1.OrgX[point];
                _zp1.CurY[point] = _zp1.OrgY[point];
            }
            int original = DualProject(_zp1.OrgX[point] - _zp0.OrgX[rp0], _zp1.OrgY[point] - _zp0.OrgY[rp0]);
            int current = Project(_zp1.CurX[point] - _zp0.CurX[rp0], _zp1.CurY[point] - _zp0.CurY[rp0]);
            if (_gs.AutoFlip && (original ^ cvt) < 0) cvt = -cvt;
            int distance;
            if ((_opcode & 4) != 0)
            {
                if (_gs.Gep0 == _gs.Gep1 && Math.Abs(cvt - original) > _gs.ControlValueCutIn) cvt = original;
                distance = Round(cvt);
            }
            else distance = RoundNone(cvt);
            if ((_opcode & 8) != 0) distance = KeepMinimum(distance, original);
            Move(_zp1, point, distance - current);
            _gs.Rp1 = rp0;
            if ((_opcode & 16) != 0) _gs.Rp0 = point;
            _gs.Rp2 = point;
        }

        private static int RoundNone(int distance) => distance;

        private int KeepMinimum(int distance, int original)
        {
            if (original >= 0) return distance < _gs.MinimumDistance ? _gs.MinimumDistance : distance;
            return distance > -_gs.MinimumDistance ? -_gs.MinimumDistance : distance;
        }

        private void DeltaPoints(int offset)
        {
            int count = Pop();
            for (int k = 0; k < count; k++)
            {
                Spend(1);
                int point = Pop(), argument = Pop();
                if (OutOf(point, _zp0)) continue;
                int ppem = ((argument & 0xF0) >> 4) + offset + _gs.DeltaBase;
                if (ppem != _font._ppem) continue;
                int steps = (argument & 0xF) - 8;
                if (steps >= 0) steps++;
                Move(_zp0, point, steps * (1 << (6 - _gs.DeltaShift)));
            }
        }

        private void DeltaControlValues(int offset)
        {
            int count = Pop();
            for (int k = 0; k < count; k++)
            {
                Spend(1);
                int index = Pop(), argument = Pop();
                if ((uint)index >= (uint)_cvt.Length) continue;
                int ppem = ((argument & 0xF0) >> 4) + offset + _gs.DeltaBase;
                if (ppem != _font._ppem) continue;
                int steps = (argument & 0xF) - 8;
                if (steps >= 0) steps++;
                _cvt[index] += steps * (1 << (6 - _gs.DeltaShift));
            }
        }

        // IUP: moves the untouched points of each contour with the touched points on either side of them.
        private void InterpolateUntouched(bool xAxis)
        {
            var zone = _glyph;
            if (zone.Contours.Length == 0) return;
            byte mask = xAxis ? Zone.TouchX : Zone.TouchY;
            int[] cur = xAxis ? zone.CurX : zone.CurY, org = xAxis ? zone.OrgX : zone.OrgY, orus = xAxis ? zone.OrusX : zone.OrusY;
            int realPoints = zone.Count;
            Spend(realPoints);
            int point = 0;
            for (int contour = 0; contour < zone.Contours.Length; contour++)
            {
                int end = zone.Contours[contour];
                int first = point;
                if (end >= realPoints) end = realPoints - 1;
                if (end < first) continue;
                while (point <= end && (zone.Flags[point] & mask) == 0) point++;
                if (point <= end)
                {
                    int firstTouched = point, touched = point;
                    point++;
                    while (point <= end)
                    {
                        if ((zone.Flags[point] & mask) != 0)
                        {
                            Interpolate(cur, org, orus, touched + 1, point - 1, touched, point);
                            touched = point;
                        }
                        point++;
                    }
                    if (touched == firstTouched)
                    {
                        int shift = cur[touched] - org[touched];
                        for (int i = first; i <= end; i++)
                            if (i != touched) cur[i] += shift;
                    }
                    else
                    {
                        Interpolate(cur, org, orus, touched + 1, end, touched, firstTouched);
                        if (firstTouched > 0) Interpolate(cur, org, orus, first, firstTouched - 1, touched, firstTouched);
                    }
                }
                point = end + 1;
            }
        }

        private static void Interpolate(int[] cur, int[] org, int[] orus, int p1, int p2, int ref1, int ref2)
        {
            if (p1 > p2) return;
            int org1 = org[ref1], org2 = org[ref2], orus1 = orus[ref1], orus2 = orus[ref2];
            if (orus1 > orus2)
            {
                (org1, org2) = (org2, org1);
                (orus1, orus2) = (orus2, orus1);
                (ref1, ref2) = (ref2, ref1);
            }
            int cur1 = cur[ref1], cur2 = cur[ref2];
            int delta1 = cur1 - org1, delta2 = cur2 - org2;
            if (cur1 == cur2 || orus1 == orus2)
            {
                for (int i = p1; i <= p2; i++)
                {
                    int x = org[i];
                    x = x <= org1 ? x + delta1 : x >= org2 ? x + delta2 : cur1;
                    cur[i] = x;
                }
                return;
            }
            long scale = ((long)(cur2 - cur1) << 16) / (orus2 - orus1);
            for (int i = p1; i <= p2; i++)
            {
                int x = org[i];
                if (x <= org1) x += delta1;
                else if (x >= org2) x += delta2;
                else x = cur1 + MulFix(orus[i] - orus1, scale);
                cur[i] = x;
            }
        }

        private static int MulFix(long a, long b)
        {
            int sign = 1;
            if (a < 0) { a = -a; sign = -sign; }
            if (b < 0) { b = -b; sign = -sign; }
            return (int)Math.Clamp(sign * ((a * b + 0x8000) >> 16), int.MinValue, int.MaxValue);
        }
    }
}
