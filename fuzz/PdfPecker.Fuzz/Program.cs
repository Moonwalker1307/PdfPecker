using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfPecker.Fuzz;

internal sealed class Options
{
    public string Target = "all";
    public TimeSpan Duration = TimeSpan.FromMinutes(1);
    public string Out = "fuzz-findings";
    public long Seed = 1;
    public TimeSpan Timeout = TimeSpan.FromSeconds(5);
    public long MaxMemoryMb = 1536;
    public string? Repo;
    public bool Strict;
    public bool NoMinimize;
    public bool Worker;
    public string? Repro;
    public bool List;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("Missing value for " + args[i]);
            switch (args[i])
            {
                case "--target": o.Target = Value(); break;
                case "--duration": o.Duration = ParseDuration(Value()); break;
                case "--out": o.Out = Value(); break;
                case "--seed": o.Seed = long.Parse(Value()); break;
                case "--timeout": o.Timeout = TimeSpan.FromSeconds(double.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture)); break;
                case "--max-memory-mb": o.MaxMemoryMb = long.Parse(Value()); break;
                case "--repo": o.Repo = Value(); break;
                case "--strict": o.Strict = true; break;
                case "--no-minimize": o.NoMinimize = true; break;
                case "--worker": o.Worker = true; break;
                case "--repro": o.Repro = Value(); break;
                case "--list": o.List = true; break;
                default: throw new ArgumentException("Unknown option " + args[i]);
            }
        }
        return o;
    }

    /// <summary>Seconds, or a number with an s, m or h suffix.</summary>
    public static TimeSpan ParseDuration(string s)
    {
        var m = Regex.Match(s.Trim(), @"^(\d+(?:\.\d+)?)([smh]?)$");
        if (!m.Success) throw new ArgumentException("Bad duration " + s);
        double n = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        return m.Groups[2].Value switch { "m" => TimeSpan.FromMinutes(n), "h" => TimeSpan.FromHours(n), _ => TimeSpan.FromSeconds(n) };
    }

    public string[] ToWorkerArgs(string target, TimeSpan duration, long seed, bool worker)
    {
        var a = new List<string>
        {
            "--target", target, "--duration", ((int)Math.Max(1, duration.TotalSeconds)).ToString() + "s", "--out", Out, "--seed", seed.ToString(),
            "--timeout", Timeout.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "--max-memory-mb", MaxMemoryMb.ToString(),
        };
        if (Repo != null) { a.Add("--repo"); a.Add(Repo); }
        if (Strict) a.Add("--strict");
        if (NoMinimize) a.Add("--no-minimize");
        if (worker) a.Add("--worker");
        return a.ToArray();
    }
}

/// <summary>Exit codes shared by the supervisor, its worker processes and --repro runs.</summary>
internal static class Exit
{
    public const int Done = 0;
    public const int Findings = 1;
    public const int Usage = 2;
    public const int Restart = 3; // the worker had to abandon a thread that ignored cancellation
    public const int ReproFails = 10;
}

internal static class Program
{
    private static int Main(string[] args)
    {
        Options o;
        try { o = Options.Parse(args); }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Usage: PdfPecker.Fuzz --target <name|all> [--duration 2m] [--out dir] [--seed 1] [--timeout 5] [--max-memory-mb 1536] [--strict] [--no-minimize] [--repo path] | --list | --repro <file> --target <name>");
            return Exit.Usage;
        }
        if (o.List)
        {
            foreach (var t in Targets.All) Console.WriteLine(t.Name);
            return Exit.Done;
        }
        if (o.Repro != null) return Repro(o);
        if (o.Worker) return new Worker(o).Run();
        return Supervise(o);
    }

    private static List<Target> Select(Options o)
    {
        if (o.Target == "all") return Targets.All.ToList();
        var t = Targets.Find(o.Target);
        if (t == null)
        {
            Console.Error.WriteLine("Unknown target " + o.Target + ". Targets: " + string.Join(", ", Targets.All.Select(x => x.Name)));
            return new List<Target>();
        }
        return new List<Target> { t };
    }

    /// <summary>Runs one file through a target once, for reproducing and for minimising crashes from outside the process.</summary>
    private static int Repro(Options o)
    {
        var target = Targets.Find(o.Target);
        if (target == null) return Exit.Usage;
        var executor = new Executor(o.Timeout, o.MaxMemoryMb << 20);
        var verdict = executor.Run(target, File.ReadAllBytes(o.Repro!), o.Strict, out _);
        if (verdict != null) Console.WriteLine($"{verdict.Kind}: {verdict.Signature}\n{verdict.Detail}");
        return executor.AbandonedThreads > 0 ? Exit.Restart : verdict != null ? Exit.ReproFails : Exit.Done;
    }

    /// <summary>
    /// Runs each target in a child process. A crash the process cannot survive (stack overflow, fatal error) kills only
    /// the child; the supervisor saves the input the child had written down and starts a new child for the rest of the time.
    /// </summary>
    private static int Supervise(Options o)
    {
        var targets = Select(o);
        if (targets.Count == 0) return Exit.Usage;
        int total = 0;
        foreach (var target in targets)
        {
            var store = new FindingStore(o.Out, target.Name);
            var end = DateTime.UtcNow + o.Duration;
            int restarts = 0;
            while (DateTime.UtcNow < end)
            {
                var remaining = end - DateTime.UtcNow;
                var (code, stderr) = RunChild(o.ToWorkerArgs(target.Name, remaining, o.Seed + restarts * 1_000_003L, worker: true));
                if (code == Exit.Done) break;
                restarts++;
                if (code == Exit.Restart) continue;
                if (code == Exit.Usage) return Exit.Usage;
                CrashFound(o, target, store, code, stderr);
            }
            if (File.Exists(store.CurrentInputPath)) File.Delete(store.CurrentInputPath);
            int count = store.Count;
            Console.WriteLine($"[{target.Name}] done: {count} finding(s) in {store.Directory}");
            total += count;
        }
        return total > 0 ? Exit.Findings : Exit.Done;
    }

    private static (int Code, string Stderr) RunChild(string[] args)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet") psi.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = new StringBuilder();
        int lines = 0;
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null || lines > 200) return;
            lines++;
            lock (err) err.AppendLine(e.Data);
        };
        p.BeginErrorReadLine();
        p.WaitForExit();
        p.WaitForExit(); // flushes the asynchronous error handler
        lock (err) return (p.ExitCode, err.ToString());
    }

    private static void CrashFound(Options o, Target target, FindingStore store, int code, string stderr)
    {
        var frame = Regex.Match(stderr, @"at (PdfPecker\.(?!Fuzz)[^\r\n(]*)");
        var signature = "crash|exit " + code + "|" + (frame.Success ? frame.Groups[1].Value : "no-library-frame");
        if (store.Repeat(signature)) { Console.WriteLine($"[{target.Name}] crash (exit {code}) repeated"); return; }
        if (!File.Exists(store.CurrentInputPath))
        {
            Console.WriteLine($"[{target.Name}] the worker died with exit code {code} before running an input:\n{stderr}");
            return;
        }
        var input = File.ReadAllBytes(store.CurrentInputPath);
        string[] ReproArgs(string file) => new[] { "--repro", file, "--target", target.Name, "--timeout", o.Timeout.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "--max-memory-mb", o.MaxMemoryMb.ToString() }
            .Concat(o.Strict ? new[] { "--strict" } : Array.Empty<string>()).ToArray();
        // A crash that does not happen again with the input alone came from state built up over many inputs, such as a
        // heap that filled up; there is nothing to minimise or to put in a regression test.
        if (RunChild(ReproArgs(store.CurrentInputPath)).Code != code)
        {
            Console.WriteLine($"[{target.Name}] crash (exit {code}) did not reproduce with the input alone; not saved");
            return;
        }
        var minimised = input;
        if (!o.NoMinimize)
        {
            var scratch = Path.Combine(store.Directory, ".trial");
            minimised = Minimizer.Minimize(input, target.Text, candidate =>
            {
                File.WriteAllBytes(scratch, candidate);
                var (trialCode, _) = RunChild(ReproArgs(scratch));
                return trialCode == code;
            }, TimeSpan.FromSeconds(120), maxTrials: 150);
            File.Delete(scratch);
        }
        var detail = $"The process died with exit code {code} (a stack overflow or another fatal error that cannot be caught).\nStandard error:\n{stderr}";
        var name = store.Save(target, new Verdict("crash", signature, detail), minimised, input.Length, o.Seed, 0);
        Console.WriteLine($"[{target.Name}] FINDING crash exit {code}: {name} ({minimised.Length} bytes)");
    }
}

/// <summary>The in-process fuzzing loop of one target: seeds first, then mutants until the time is up.</summary>
internal sealed class Worker
{
    private readonly Options _o;

    public Worker(Options o) => _o = o;

    public int Run()
    {
        var target = Targets.Find(_o.Target);
        if (target == null) { Console.Error.WriteLine("Unknown target " + _o.Target); return Exit.Usage; }
        var root = SeedContext.FindRoot(_o.Repo);
        var seeds = target.Seeds(new SeedContext(root)).Where(s => s.Data.Length <= target.MaxSize).ToList();
        if (seeds.Count == 0) { Console.Error.WriteLine($"[{target.Name}] no seed inputs found under {root}"); return Exit.Usage; }

        var store = new FindingStore(_o.Out, target.Name);
        var executor = new Executor(_o.Timeout, _o.MaxMemoryMb << 20);
        long startRetained = executor.Retained;
        bool leakReported = false, shed = false;
        var rng = new Random(unchecked((int)_o.Seed));
        var mutator = new Mutator(rng, target.MaxSize, target.Dictionary, target.Nesters);
        var corpus = seeds.Select(s => s.Data).ToList();
        var survivors = new List<byte[]>();
        var tolerated = new SortedDictionary<string, long>();
        long executions = 0, duplicates = 0, flaky = 0;
        var clock = Stopwatch.StartNew();
        var nextReport = TimeSpan.FromSeconds(20);
        Console.WriteLine($"[{target.Name}] {seeds.Count} seeds, seed {_o.Seed}, {_o.Duration.TotalSeconds:0}s");

        // Returns true when the input ran without any exception, which makes the mutant worth mutating further.
        bool Execute(byte[] input, string? label)
        {
            File.WriteAllBytes(store.CurrentInputPath, input); // survives a crash that takes the process down
            executions++;
            var verdict = executor.Run(target, input, _o.Strict, out var tol);
            if (tol != null) { tolerated[tol] = tolerated.GetValueOrDefault(tol) + 1; return false; }
            if (verdict == null) return true;
            if (store.Repeat(verdict.Signature)) { duplicates++; return false; }

            // A slow or memory-hungry input is often just a victim of a busy machine or of state left by earlier inputs, so
            // it only counts when it fails again on its own.
            if (verdict.Kind is "timeout" or "memory" && executor.AbandonedThreads == 0)
            {
                // The retry measures the input against the heap as it is now, so a cache that earlier inputs filled
                // does not count; heap that keeps growing between inputs is a leak, reported on its own.
                long now = executor.MeasureRetained();
                if (now - startRetained > (_o.MaxMemoryMb << 20) / 4 && !leakReported)
                {
                    leakReported = true;
                    Console.WriteLine($"[{target.Name}] LEAK: the heap kept between inputs grew from {startRetained >> 20} MB to {now >> 20} MB");
                }
                // Once the leak alone is past the ceiling, the hard heap limit is close and every input would fail;
                // a fresh worker sheds it.
                if (now - startRetained > _o.MaxMemoryMb << 20)
                {
                    flaky++;
                    shed = true;
                    return false;
                }
                if (executor.Run(target, input, _o.Strict, out _) is not { Kind: var repeatKind } || repeatKind != verdict.Kind)
                {
                    flaky++;
                    return false;
                }
            }

            var minimised = input;
            if (!_o.NoMinimize && executor.AbandonedThreads == 0)
            {
                bool Same(Verdict v) => v.Kind == verdict.Kind && (v.Kind != "exception" || v.Signature == verdict.Signature);
                minimised = Minimizer.Minimize(input, target.Text,
                    candidate => executor.AbandonedThreads == 0 && executor.Run(target, candidate, _o.Strict, out _) is { } v && Same(v),
                    TimeSpan.FromSeconds(90));
            }
            var final = verdict;
            if (minimised.Length != input.Length && executor.AbandonedThreads == 0 && executor.Run(target, minimised, _o.Strict, out _) is { } again && again.Kind == verdict.Kind)
                final = again with { Signature = verdict.Signature };
            var name = store.Save(target, final, minimised, input.Length, _o.Seed, executions);
            Console.WriteLine($"[{target.Name}] FINDING {verdict.Kind}: {name} ({minimised.Length} bytes{(label != null ? ", from seed " + label : "")}) {verdict.Signature}");
            return false;
        }

        foreach (var (name, data) in seeds)
        {
            if (clock.Elapsed > _o.Duration) break;
            Execute(data, name);
            if (executor.AbandonedThreads > 0 || shed) return Exit.Restart;
        }
        while (clock.Elapsed < _o.Duration)
        {
            var basis = survivors.Count > 0 && rng.Next(5) == 0 ? survivors[rng.Next(survivors.Count)] : corpus[rng.Next(corpus.Count)];
            var input = mutator.Mutate(basis, corpus, target.Text);
            if (Execute(input, null) && rng.Next(10) == 0)
            {
                if (survivors.Count < 200) survivors.Add(input);
                else survivors[rng.Next(survivors.Count)] = input;
            }
            if (executor.AbandonedThreads > 0 || shed) return Exit.Restart;
            if (clock.Elapsed > nextReport)
            {
                nextReport += TimeSpan.FromSeconds(20);
                Console.WriteLine($"[{target.Name}] {executions} executions ({executions / clock.Elapsed.TotalSeconds:0.#}/s), {store.Count} finding(s), {duplicates} duplicate(s), {flaky} unconfirmed, "
                    + $"tolerated: {(tolerated.Count == 0 ? "none" : string.Join(", ", tolerated.Select(kv => kv.Key + "=" + kv.Value)))}");
            }
        }
        Console.WriteLine($"[{target.Name}] finished: {executions} executions, {store.Count} finding(s), {duplicates} duplicate(s), {flaky} unconfirmed slow or large input(s)");
        File.Delete(store.CurrentInputPath);
        return Exit.Done;
    }
}
