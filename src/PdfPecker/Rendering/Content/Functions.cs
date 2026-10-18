using System.Runtime.InteropServices;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering.Content;

/// <summary>A PDF function (types 0, 2, 3 and 4) mapping m inputs to n outputs.</summary>
internal abstract class PdfFunction
{
    // Stitching functions and arrays of functions contain further functions, and a document can nest them without end.
    private const int MaxNestingDepth = 8;

    /// <summary>Pairs of minimum and maximum, one pair per input.</summary>
    protected double[] Domain = { 0, 1 };

    /// <summary>Pairs of minimum and maximum, one pair per output, or null when the outputs are not clipped.</summary>
    protected double[]? Range;

    public int Inputs => Domain.Length / 2;
    public virtual int Outputs => Range != null ? Range.Length / 2 : 1;

    public abstract void Eval(ReadOnlySpan<double> input, Span<double> output);

    /// <summary>Clips one input to its domain; NaN becomes the minimum, and an input without a domain is kept.</summary>
    protected double ClipInput(double value, int input)
    {
        if (2 * input + 1 >= Domain.Length) return value;
        double min = Domain[2 * input], max = Domain[2 * input + 1];
        return value < min ? min : value > max ? max : double.IsNaN(value) ? min : value;
    }

    /// <summary>Clips the outputs to the range; a range whose maximum is below its minimum clips to the minimum.</summary>
    protected void ClipOutput(Span<double> output)
    {
        if (Range == null) return;
        for (int i = 0; i < output.Length && 2 * i + 1 < Range.Length; i++)
            output[i] = Math.Clamp(output[i], Range[2 * i], Math.Max(Range[2 * i], Range[2 * i + 1]));
    }

    /// <summary>
    /// Maps <paramref name="x"/> from the interval from <paramref name="xMin"/> to <paramref name="xMax"/> linearly onto
    /// the one from <paramref name="yMin"/> to <paramref name="yMax"/>, the Interpolate formula of ISO 32000-1, 7.10.2.
    /// An empty source interval maps everything to <paramref name="yMin"/>.
    /// </summary>
    protected static double Interpolate(double x, double xMin, double xMax, double yMin, double yMax)
        => xMax == xMin ? yMin : yMin + (x - xMin) * (yMax - yMin) / (xMax - xMin);

    /// <summary>Parses a function object; an array of 1-output functions is combined into one n-output function.</summary>
    public static PdfFunction? Create(PdfFile file, PdfObject? obj, int depth = 0)
    {
        if (depth > MaxNestingDepth) return null;
        return file.Resolve(obj) switch
        {
            PdfArray array => CreateCombined(file, array, depth),
            PdfDict dict => CreateFromDictionary(file, dict, depth),
            _ => null,
        };
    }

    private static CombinedFunction? CreateCombined(PdfFile file, PdfArray array, int depth)
    {
        var parts = new List<PdfFunction>();
        foreach (var item in array.Items)
        {
            var part = Create(file, item, depth + 1);
            if (part == null) return null;
            parts.Add(part);
        }
        return parts.Count == 0 ? null : new CombinedFunction(parts);
    }

    private static PdfFunction? CreateFromDictionary(PdfFile file, PdfDict dict, int depth)
    {
        PdfFunction? function = file.GetInt(dict, "FunctionType", -1) switch
        {
            0 when dict is PdfStream stream => SampledFunction.Create(file, stream),
            2 => new ExponentialFunction(file, dict),
            3 => StitchingFunction.Create(file, dict, depth),
            4 when dict is PdfStream stream => PostScriptFunction.Create(file, stream),
            _ => null,
        };
        if (function == null) return null;
        if (file.GetNumbers(dict, "Domain") is { Length: >= 2 } domain) function.Domain = domain;
        function.Range = file.GetNumbers(dict, "Range") is { Length: >= 2 } range ? range : null;
        return function;
    }

    /// <summary>An array of functions with one output each, evaluated on the same input as one function.</summary>
    private sealed class CombinedFunction : PdfFunction
    {
        // Each part writes into a scratch buffer of this size, of which only the first value is used.
        private const int PartOutputCapacity = 8;

        private readonly List<PdfFunction> _parts;

        public CombinedFunction(List<PdfFunction> parts)
        {
            _parts = parts;
            Domain = new double[] { double.MinValue, double.MaxValue };
        }

        public override int Outputs => _parts.Count;

        public override void Eval(ReadOnlySpan<double> input, Span<double> output)
        {
            // The scratch buffer is shared by the parts, so a part that writes nothing passes on the previous value.
            Span<double> partOutput = stackalloc double[PartOutputCapacity];
            for (int i = 0; i < _parts.Count && i < output.Length; i++)
            {
                _parts[i].Eval(input, partOutput);
                output[i] = partOutput[0];
            }
        }
    }
}

/// <summary>
/// A type 0 function: a table of samples on a grid over the inputs, interpolated linearly between the grid points.
/// </summary>
internal sealed class SampledFunction : PdfFunction
{
    private const int MaxInputs = 4;

    // Samples are decoded up front into doubles, so the table size is capped to bound the memory a document can claim.
    private const long MaxSamples = 16_000_000;

    private int[] _sizes = Array.Empty<int>();
    private double[] _encode = Array.Empty<double>(), _decode = Array.Empty<double>();

    /// <summary>The samples scaled to 0-1, with the outputs of one grid point next to each other and the first input varying fastest.</summary>
    private double[] _samples = Array.Empty<double>();

    private int _outputs;

    public override int Outputs => _outputs;

    public static SampledFunction? Create(PdfFile file, PdfStream stream)
    {
        var sizes = file.GetNumbers(stream, "Size");
        var range = file.GetNumbers(stream, "Range");
        var domain = file.GetNumbers(stream, "Domain");
        int bitsPerSample = file.GetInt(stream, "BitsPerSample", 8);
        if (sizes == null || range == null || domain == null || bitsPerSample is not (1 or 2 or 4 or 8 or 12 or 16 or 24 or 32))
            return null;
        int inputs = domain.Length / 2, outputs = range.Length / 2;
        if (inputs < 1 || inputs > MaxInputs || outputs < 1 || sizes.Length < inputs) return null;

        var function = new SampledFunction { _outputs = outputs, _sizes = new int[inputs] };
        long sampleCount = outputs;
        for (int i = 0; i < inputs; i++)
        {
            function._sizes[i] = Math.Max(1, (int)sizes[i]);
            sampleCount *= function._sizes[i];
            if (sampleCount > MaxSamples) return null;
        }
        function._encode = file.GetNumbers(stream, "Encode") ?? DefaultEncode(function._sizes);
        function._decode = file.GetNumbers(stream, "Decode") ?? range;
        if (function._encode.Length < 2 * inputs || function._decode.Length < 2 * outputs) return null;
        function._samples = ReadSamples(file.DecodeStream(stream), sampleCount, bitsPerSample);
        return function;
    }

    /// <summary>The default Encode array maps each input's domain onto the whole grid: 0 to size - 1.</summary>
    private static double[] DefaultEncode(int[] sizes)
    {
        var encode = new double[2 * sizes.Length];
        for (int i = 0; i < sizes.Length; i++) encode[2 * i + 1] = sizes[i] - 1;
        return encode;
    }

    /// <summary>Unpacks the samples and scales them to 0-1; samples missing from a short stream read as 0.</summary>
    private static double[] ReadSamples(byte[] data, long count, int bitsPerSample)
    {
        var samples = new double[count];
        double maxSample = bitsPerSample == 32 ? uint.MaxValue : (1L << bitsPerSample) - 1;
        var reader = new SampleReader(data);
        for (long i = 0; i < count; i++) samples[i] = reader.Read(bitsPerSample) / maxSample;
        return samples;
    }

    public override void Eval(ReadOnlySpan<double> input, Span<double> output)
    {
        int inputs = _sizes.Length;
        // Multilinear interpolation over the 2^m corners of the grid cell that holds the input.
        Span<int> cellStart = stackalloc int[inputs];
        Span<double> cellFraction = stackalloc double[inputs];
        for (int i = 0; i < inputs; i++) LocateInCell(input, i, out cellStart[i], out cellFraction[i]);

        int corners = 1 << inputs;
        Span<double> cornerWeight = stackalloc double[corners];
        Span<long> cornerOffset = stackalloc long[corners];
        for (int corner = 0; corner < corners; corner++)
            (cornerWeight[corner], cornerOffset[corner]) = Corner(corner, cellStart, cellFraction);

        int count = Math.Min(_outputs, output.Length);
        for (int j = 0; j < count; j++)
        {
            double sum = 0;
            for (int corner = 0; corner < corners; corner++)
            {
                if (cornerWeight[corner] != 0) sum += cornerWeight[corner] * _samples[cornerOffset[corner] + j];
            }
            double decodeMin = _decode[2 * j], decodeMax = _decode[2 * j + 1];
            output[j] = decodeMin + sum * (decodeMax - decodeMin);
        }
        ClipOutput(output.Slice(0, count));
    }

    /// <summary>Encodes one input onto the grid and splits it into the cell's first grid index and the fraction past it.</summary>
    private void LocateInCell(ReadOnlySpan<double> input, int i, out int start, out double fraction)
    {
        double x = ClipInput(i < input.Length ? input[i] : 0, i);
        double e = Interpolate(x, Domain[2 * i], Domain[2 * i + 1], _encode[2 * i], _encode[2 * i + 1]);
        int lastIndex = _sizes[i] - 1;
        e = Math.Clamp(e, 0, lastIndex);
        // The last grid point starts no cell of its own, so an input there interpolates in the cell before it.
        start = Math.Max(0, Math.Min((int)e, lastIndex - 1));
        fraction = _sizes[i] == 1 ? 0 : e - start;
    }

    /// <summary>
    /// The weight of one corner of the cell and the offset of its samples. Bit i of <paramref name="corner"/> picks the
    /// upper grid point for input i.
    /// </summary>
    private (double Weight, long Offset) Corner(int corner, ReadOnlySpan<int> cellStart, ReadOnlySpan<double> cellFraction)
    {
        double weight = 1;
        long offset = 0, stride = _outputs;
        for (int i = 0; i < cellStart.Length; i++)
        {
            bool upper = ((corner >> i) & 1) == 1;
            int index = Math.Min(cellStart[i] + (upper ? 1 : 0), _sizes[i] - 1);
            weight *= upper ? cellFraction[i] : 1 - cellFraction[i];
            offset += index * stride;
            stride *= _sizes[i];
        }
        return (weight, offset);
    }

    /// <summary>Reads samples of 1 to 32 bits, most significant bit first; bits past the end of the data read as 0.</summary>
    private ref struct SampleReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private long _bitPosition;

        public SampleReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _bitPosition = 0;
        }

        public uint Read(int bits)
        {
            long firstByte = _bitPosition >> 3;
            int skippedBits = (int)(_bitPosition & 7);
            int byteCount = (skippedBits + bits + 7) >> 3;
            ulong window = 0;
            for (int i = 0; i < byteCount; i++) window = window << 8 | ByteAt(firstByte + i);
            _bitPosition += bits;
            int trailingBits = 8 * byteCount - skippedBits - bits;
            return (uint)((window >> trailingBits) & ((1UL << bits) - 1));
        }

        private readonly byte ByteAt(long index) => index < _data.Length ? _data[(int)index] : (byte)0;
    }
}

/// <summary>A type 2 function: C0 + x^N * (C1 - C0) for each output.</summary>
internal sealed class ExponentialFunction : PdfFunction
{
    private readonly double[] _c0, _c1;
    private readonly double _exponent;

    public ExponentialFunction(PdfFile file, PdfDict dict)
    {
        _c0 = file.GetNumbers(dict, "C0") ?? new double[] { 0 };
        _c1 = file.GetNumbers(dict, "C1") ?? new double[] { 1 };
        _exponent = file.GetNumber(dict, "N", 1);
    }

    public override int Outputs => Math.Min(_c0.Length, _c1.Length);

    public override void Eval(ReadOnlySpan<double> input, Span<double> output)
    {
        double x = ClipInput(input.Length > 0 ? input[0] : 0, 0);
        double xn = _exponent == 1 ? x : Math.Pow(x, _exponent);
        // A negative x with a fractional exponent has no real power; the function then gives C0.
        if (double.IsNaN(xn)) xn = 0;
        int count = Math.Min(Outputs, output.Length);
        for (int i = 0; i < count; i++) output[i] = _c0[i] + xn * (_c1[i] - _c0[i]);
        ClipOutput(output.Slice(0, count));
    }
}

/// <summary>A type 3 function: the domain is split at the bounds, and each part is mapped onto its own function.</summary>
internal sealed class StitchingFunction : PdfFunction
{
    private PdfFunction[] _functions = Array.Empty<PdfFunction>();
    private double[] _bounds = Array.Empty<double>(), _encode = Array.Empty<double>();

    public override int Outputs => Range != null ? Range.Length / 2 : _functions.Length > 0 ? _functions[0].Outputs : 1;

    public static StitchingFunction? Create(PdfFile file, PdfDict dict, int depth)
    {
        if (file.GetArray(dict, "Functions") is not PdfArray array || array.Count == 0) return null;
        var functions = new PdfFunction[array.Count];
        for (int i = 0; i < functions.Length; i++)
        {
            var function = PdfFunction.Create(file, array[i], depth + 1);
            if (function == null) return null;
            functions[i] = function;
        }
        var bounds = file.GetNumbers(dict, "Bounds") ?? Array.Empty<double>();
        var encode = file.GetNumbers(dict, "Encode") ?? DefaultEncode(functions.Length);
        if (encode.Length < 2 * functions.Length || bounds.Length < functions.Length - 1) return null;
        return new StitchingFunction { _functions = functions, _bounds = bounds, _encode = encode };
    }

    /// <summary>The default Encode array maps every part onto 0 to 1.</summary>
    private static double[] DefaultEncode(int functions)
    {
        var encode = new double[2 * functions];
        for (int i = 0; i < functions; i++) encode[2 * i + 1] = 1;
        return encode;
    }

    public override void Eval(ReadOnlySpan<double> input, Span<double> output)
    {
        double x = ClipInput(input.Length > 0 ? input[0] : 0, 0);
        int k = FindPart(x);
        double lower = k == 0 ? Domain[0] : _bounds[k - 1];
        double upper = k == _functions.Length - 1 ? Domain[1] : _bounds[k];
        Span<double> partInput = stackalloc double[1];
        partInput[0] = Interpolate(x, lower, upper, _encode[2 * k], _encode[2 * k + 1]);
        _functions[k].Eval(partInput, output);
        ClipOutput(output);
    }

    /// <summary>The part whose subdomain holds x: a bound belongs to the part above it.</summary>
    private int FindPart(double x)
    {
        int k = 0;
        while (k < _functions.Length - 1 && x >= _bounds[k]) k++;
        return k;
    }
}

/// <summary>A type 4 function: a PostScript calculator program, compiled to a flat instruction list.</summary>
internal sealed class PostScriptFunction : PdfFunction
{
    // The operand stack is a fixed buffer. Execution stops once the stack comes within StackHeadroom entries of its end,
    // so that every instruction can push at least one value without a check of its own.
    private const int StackSize = 128;
    private const int StackHeadroom = 4;

    // There are no loops, but a long program is still cut off after this many instructions.
    private const int MaxSteps = 100_000;

    // Each { } nests one call of Compile, so the nesting is limited to keep the recursion off the end of the stack.
    private const int MaxBlockDepth = 50;

    // The longest operator names, "bitshift" and "truncate", have eight letters.
    private const int MaxOperatorLength = 8;

    /// <summary>
    /// The instructions. The operators that replace the top entry (Abs to Not) and those that replace the top two with
    /// one (Add to Le) are kept in two consecutive blocks, so that execution can tell them apart by range.
    /// </summary>
    private enum Op : byte
    {
        Push, True, False, Jump, JumpIfFalse,
        Copy, Dup, Exch, Index, Pop, Roll,
        Abs, Ceiling, Cos, Cvi, Cvr, Floor, Ln, Log, Neg, Round, Sin, Sqrt, Truncate, Not,
        Add, Sub, Mul, Div, Idiv, Mod, Exp, Atan, And, Or, Xor, Bitshift, Eq, Ne, Gt, Ge, Lt, Le,
    }

    /// <summary>One instruction: the operand is the value of a Push and the target index of a jump.</summary>
    private readonly record struct Instruction(Op Op, double Operand = 0)
    {
        public int Target => (int)Operand;
    }

    private readonly List<Instruction> _code = new();

    public static PostScriptFunction? Create(PdfFile file, PdfStream stream)
    {
        var lexer = new PdfLexer(file.DecodeStream(stream));
        if (lexer.Next() != TokenKind.BraceOpen) return null;
        var function = new PostScriptFunction();
        try
        {
            CompileBlock(lexer, function._code, 0);
        }
        catch (FormatException)
        {
            return null;
        }
        return function;
    }

    /// <summary>
    /// Compiles the tokens up to the closing brace of the current block, or to the end of the program when the braces
    /// are not closed. Throws <see cref="FormatException"/> on anything the calculator does not allow.
    /// </summary>
    private static void CompileBlock(PdfLexer lexer, List<Instruction> code, int depth)
    {
        if (depth > MaxBlockDepth) throw new FormatException();
        while (true)
        {
            switch (lexer.Next())
            {
                case TokenKind.Eof:
                case TokenKind.BraceClose:
                    return;
                case TokenKind.Integer:
                case TokenKind.Real:
                    code.Add(new Instruction(Op.Push, lexer.Number));
                    break;
                case TokenKind.BraceOpen:
                    CompileConditional(lexer, code, depth);
                    break;
                case TokenKind.Keyword:
                    code.Add(new Instruction(ParseOperator(lexer)));
                    break;
                default:
                    throw new FormatException();
            }
        }
    }

    /// <summary>
    /// Compiles "{ proc } if" or "{ proc1 } { proc2 } ifelse", after the first opening brace. The condition is popped by
    /// a jump past the first block when it is false; after an ifelse's first block, a jump skips the second.
    /// </summary>
    private static void CompileConditional(PdfLexer lexer, List<Instruction> code, int depth)
    {
        int jumpIfFalse = code.Count;
        code.Add(new Instruction(Op.JumpIfFalse));
        CompileBlock(lexer, code, depth + 1);
        var next = lexer.Next();
        if (next == TokenKind.BraceOpen)
        {
            int jump = code.Count;
            code.Add(new Instruction(Op.Jump));
            code[jumpIfFalse] = new Instruction(Op.JumpIfFalse, code.Count);
            CompileBlock(lexer, code, depth + 1);
            code[jump] = new Instruction(Op.Jump, code.Count);
            if (lexer.Next() != TokenKind.Keyword || !lexer.KeywordIs("ifelse")) throw new FormatException();
        }
        else
        {
            if (next != TokenKind.Keyword || !lexer.KeywordIs("if")) throw new FormatException();
            code[jumpIfFalse] = new Instruction(Op.JumpIfFalse, code.Count);
        }
    }

    /// <summary>The operator of the lexer's current keyword token.</summary>
    private static Op ParseOperator(PdfLexer lexer)
    {
        if (lexer.TokLen > MaxOperatorLength) throw new FormatException();
        // Keyword bytes are Latin-1, so each byte is the character of the same value.
        Span<char> name = stackalloc char[lexer.TokLen];
        for (int i = 0; i < name.Length; i++) name[i] = (char)lexer.Data[lexer.TokStart + i];
        return name switch
        {
            "abs" => Op.Abs, "add" => Op.Add, "atan" => Op.Atan, "ceiling" => Op.Ceiling, "cos" => Op.Cos,
            "cvi" => Op.Cvi, "cvr" => Op.Cvr, "div" => Op.Div, "exp" => Op.Exp, "floor" => Op.Floor,
            "idiv" => Op.Idiv, "ln" => Op.Ln, "log" => Op.Log, "mod" => Op.Mod, "mul" => Op.Mul, "neg" => Op.Neg,
            "round" => Op.Round, "sin" => Op.Sin, "sqrt" => Op.Sqrt, "sub" => Op.Sub, "truncate" => Op.Truncate,
            "and" => Op.And, "or" => Op.Or, "not" => Op.Not, "xor" => Op.Xor, "bitshift" => Op.Bitshift,
            "eq" => Op.Eq, "ne" => Op.Ne, "gt" => Op.Gt, "ge" => Op.Ge, "lt" => Op.Lt, "le" => Op.Le,
            "true" => Op.True, "false" => Op.False, "copy" => Op.Copy, "dup" => Op.Dup, "exch" => Op.Exch,
            "index" => Op.Index, "pop" => Op.Pop, "roll" => Op.Roll,
            _ => throw new FormatException(),
        };
    }

    public override void Eval(ReadOnlySpan<double> input, Span<double> output)
    {
        Span<double> stack = stackalloc double[StackSize];
        int depth = 0;
        for (int i = 0; i < Inputs && depth < stack.Length; i++) stack[depth++] = ClipInput(i < input.Length ? input[i] : 0, i);
        depth = Execute(stack, depth);

        // The outputs are the top entries of the stack, the last one on top; missing and non-finite ones are 0.
        int count = Math.Min(Outputs, output.Length);
        for (int i = 0; i < count; i++)
        {
            int source = depth - count + i;
            double value = source >= 0 ? stack[source] : 0;
            output[i] = double.IsFinite(value) ? value : 0;
        }
        ClipOutput(output.Slice(0, count));
    }

    /// <summary>
    /// Runs the program on the stack, which holds <paramref name="depth"/> entries, and returns the final depth.
    /// An operator that finds too few operands, or invalid ones for copy or index, stops the program where it is.
    /// </summary>
    private int Execute(Span<double> stack, int depth)
    {
        var code = CollectionsMarshal.AsSpan(_code);
        int steps = 0;
        for (int pc = 0; pc < code.Length; pc++)
        {
            if (++steps > MaxSteps || depth >= stack.Length - StackHeadroom) break;
            var instruction = code[pc];
            switch (instruction.Op)
            {
                case Op.Push: stack[depth++] = instruction.Operand; break;
                case Op.True: stack[depth++] = 1; break;
                case Op.False: stack[depth++] = 0; break;
                // The loop's increment then moves on to the target.
                case Op.Jump: pc = instruction.Target - 1; break;
                case Op.JumpIfFalse:
                    if (depth < 1) return depth;
                    if (stack[--depth] == 0) pc = instruction.Target - 1;
                    break;
                case Op.Dup:
                    if (depth < 1) return depth;
                    stack[depth] = stack[depth - 1];
                    depth++;
                    break;
                case Op.Pop:
                    if (depth > 0) depth--;
                    break;
                case Op.Exch:
                    if (depth < 2) return depth;
                    (stack[depth - 1], stack[depth - 2]) = (stack[depth - 2], stack[depth - 1]);
                    break;
                case Op.Copy:
                    if (!Copy(stack, ref depth)) return depth;
                    break;
                case Op.Index:
                    if (!Index(stack, depth)) return depth;
                    break;
                case Op.Roll:
                    if (depth < 2) return depth;
                    Roll(stack, ref depth);
                    break;
                case >= Op.Abs and <= Op.Not:
                    if (depth < 1) return depth;
                    stack[depth - 1] = Unary(instruction.Op, stack[depth - 1]);
                    break;
                default:
                    if (depth < 2) return depth;
                    double y = stack[--depth];
                    stack[depth - 1] = Binary(instruction.Op, stack[depth - 1], y);
                    break;
            }
        }
        return depth;
    }

    /// <summary>n copy: pops n and duplicates the top n entries. Returns false when n does not fit, with n popped.</summary>
    private static bool Copy(Span<double> stack, ref int depth)
    {
        if (depth < 1) return false;
        int n = (int)stack[--depth];
        if (n < 0 || n > depth || depth + n >= stack.Length) return false;
        stack.Slice(depth - n, n).CopyTo(stack.Slice(depth, n));
        depth += n;
        return true;
    }

    /// <summary>n index: replaces n with a copy of the entry n below it. Returns false when there is no such entry.</summary>
    private static bool Index(Span<double> stack, int depth)
    {
        if (depth < 1) return false;
        int n = (int)stack[depth - 1];
        if (n < 0 || n >= depth - 1) return false;
        stack[depth - 1] = stack[depth - 2 - n];
        return true;
    }

    /// <summary>n j roll: pops j and n and rotates the top n entries up by j; an n that does not fit is ignored.</summary>
    private static void Roll(Span<double> stack, ref int depth)
    {
        int j = (int)stack[--depth], n = (int)stack[--depth];
        if (n <= 0 || n > depth) return;
        j = ((j % n) + n) % n;
        if (j == 0) return;
        // Rotating up by j moves the top j entries to the bottom of the window: reverse the window, then each part.
        var window = stack.Slice(depth - n, n);
        window.Reverse();
        window.Slice(0, j).Reverse();
        window.Slice(j).Reverse();
    }

    private static double Unary(Op op, double a) => op switch
    {
        Op.Abs => Math.Abs(a),
        Op.Ceiling => Math.Ceiling(a),
        Op.Cos => Math.Cos(a * Math.PI / 180),
        Op.Cvi => Math.Truncate(a),
        Op.Cvr => a,
        Op.Floor => Math.Floor(a),
        Op.Ln => Math.Log(a),
        Op.Log => Math.Log10(a),
        Op.Neg => -a,
        Op.Round => Math.Floor(a + 0.5),
        Op.Sin => Math.Sin(a * Math.PI / 180),
        Op.Sqrt => Math.Sqrt(a),
        Op.Truncate => Math.Truncate(a),
        // Booleans are 1 and 0; any other value is an integer whose bits are inverted.
        Op.Not => a == 0 ? 1 : a == 1 ? 0 : ~(long)a,
        _ => a,
    };

    private static double Binary(Op op, double x, double y) => op switch
    {
        Op.Add => x + y,
        Op.Sub => x - y,
        Op.Mul => x * y,
        Op.Div => y == 0 ? 0 : x / y,
        Op.Idiv => (long)y == 0 ? 0 : (long)x / (long)y,
        Op.Mod => (long)y == 0 ? 0 : (long)x % (long)y,
        Op.Exp => Math.Pow(x, y),
        Op.Atan => (Math.Atan2(x, y) * 180 / Math.PI + 360) % 360,
        Op.And => (long)x & (long)y,
        Op.Or => (long)x | (long)y,
        Op.Xor => (long)x ^ (long)y,
        Op.Bitshift => y >= 0 ? (long)x << (int)Math.Min(63, y) : (long)x >> (int)Math.Min(63, -y),
        Op.Eq => x == y ? 1 : 0,
        Op.Ne => x != y ? 1 : 0,
        Op.Gt => x > y ? 1 : 0,
        Op.Ge => x >= y ? 1 : 0,
        Op.Lt => x < y ? 1 : 0,
        Op.Le => x <= y ? 1 : 0,
        _ => x,
    };
}
