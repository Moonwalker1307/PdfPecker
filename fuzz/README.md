# PdfPecker fuzzer

A dependency-free mutation fuzzer for the library's parsers of untrusted input. It is a console project in the solution
and has no package references. There is no coverage feedback (the .NET runtime offers none without a third-party
tool), so it relies on a seed corpus taken from the repository and on broad mutations. It is good at finding hangs,
huge allocations, deep recursion and unexpected exception types; it will not find deep logic bugs.

## Run it

```
dotnet build -c Release fuzz/PdfPecker.Fuzz
dotnet fuzz/PdfPecker.Fuzz/bin/Release/net10.0/PdfPecker.Fuzz.dll --target html --duration 2m --out fuzz-findings
```

| Option | Meaning |
| --- | --- |
| `--target <name\|all>` | What to fuzz; `--list` prints the names. `all` runs them one after another, `--duration` each. |
| `--duration 90s\|10m\|1h` | How long to run per target (default 1m). |
| `--seed <n>` | Seed of the random generator (default 1). The same seed gives the same inputs; timeouts still depend on the machine. |
| `--out <dir>` | Findings folder (default `fuzz-findings`); each target gets a sub-folder. |
| `--timeout <s>` | Wall-clock limit per input (default 5). The `svg` target raises it to at least 30, because large inputs of filtered shapes are slow but linear. |
| `--max-memory-mb <n>` | Heap ceiling per input (default 1536). |
| `--strict` | Also report the exceptions that `ImageLoader` swallows around the image decoders (see below). |
| `--no-minimize` | Save inputs as found. |
| `--repro <file> --target <name>` | Run one saved input once; prints the verdict and exits with 10 when it fails. |

The process exits with 1 when the findings folder has any finding, which is what fails the CI job.

Each target runs in a worker process, which the supervisor restarts when an input hangs beyond cancellation or
crashes the process. A restarted worker skips the seeds that were already run (`--skip-seeds`, passed by the
supervisor), so a seed that hangs is reported once instead of on every restart. Seeds never come from `bin`, `obj` or a
`fuzz-findings` folder: a saved finding read back as a seed would hang every worker on the same input.

Targets: `pdf-render`, `pdf-text`, `pdf-merge`, `pdf-form`, `pdf-verify`, `html`, `css` (as a `<style>` in a page), `svg`
(inline in a page and as a data-URI image), `markdown`, `png`, `jpeg`, `gif`, `webp`, `bmp`, `tiff`, `jpx`, `jbig2`,
`font-ttf`, `font-otf`, `font-woff`, `font-woff2` and `font-variable` (variable TrueType and CFF2 fonts). The font
targets register the font and then lay out and embed text in it, because most font bugs are in shaping and subsetting
rather than in loading.

## What counts as a finding

* **exception**: anything other than `FormatException`, `InvalidDataException`, `PdfLimitException`,
  `PdfPasswordException`, `OperationCanceledException` and `NotSupportedException`. The image decoders are internal and
  their caller swallows a few more types so that one damaged picture does not fail a document (`IOException`,
  `ArgumentException`, `OverflowException`, `IndexOutOfRangeException`); those are counted in the progress lines but
  not reported unless `--strict` is given. `pdf-form` also accepts the documented `InvalidOperationException` for
  flattening a signed PDF.
* **timeout**: an input that runs longer than `--timeout`. The token is cancelled first; a target without a token
  (`pdf-verify`, the image decoders) is waited for three more seconds, then its thread is abandoned and the process
  restarted.
* **memory**: the live heap grows by more than `--max-memory-mb` over what stays live between inputs, or the
  runtime's hard heap limit raises `OutOfMemoryException`. The heap belongs to the whole process, so the baseline is
  measured again before a memory finding is confirmed; heap that keeps growing between inputs is a leak in the library
  and is printed as a `LEAK` line rather than blamed on the input that happened to cross the ceiling; once the leak
  alone passes the ceiling, the worker is restarted.
  A timeout or memory finding is run a second time and only counts if it fails again, because a busy machine (or the
  state left by earlier inputs) can make an innocent input slow; the progress line counts those as "unconfirmed".
  Check a saved one with `--repro` before trusting it.
* **crash**: the process died, for example from a stack overflow. Each target runs in a child process, so a crash
  costs one restart. The input is written to `.current` before every run, which is how the supervisor finds it; a crash that
  does not happen again with that input alone is not saved.

Every finding is minimised (whole tokens first for text formats, then runs of bytes) and saved as
`<target>/<kind>-<hash>.<ext>` with a `<kind>-<hash>.txt` that has the signature, the sizes, the seed and the stack
trace. Findings with the same signature (exception type plus the top library frame) are reported once per run, and
signatures already in the folder are skipped.

## Seeds and mutations

Seeds are read in place from `tests/PdfPecker.Tests/Fixtures`, the `Golden` inputs and baselines, `samples` and the
README, so nothing is copied. A few are generated at start-up: signed PDFs, PDFs that carry JPEG, JPEG 2000, JBIG2 and
predicted Flate images, and PNG, JPEG and WebP images rendered by the library.

Mutations (one to four per input): bit flips, byte changes, inserted, deleted and duplicated chunks, interesting
integers (0, -1, 0x7fffffff, 0x80000000, 0xffff and so on in 1, 2 and 4 bytes, both byte orders), splicing between seeds,
truncation, and for the text formats token-level duplication, deep nesting, deletion, swaps, dictionary words and
interesting numbers. Inputs that run cleanly are sometimes kept and mutated further.

`--target selftest-crash` and `--target selftest-hang` (not listed, not part of `all`) fail on purpose, to check the
supervisor and the abandoned-thread handling.

## From finding to regression test

1. Reproduce: `dotnet ... PdfPecker.Fuzz.dll --repro fuzz-findings/html/exception-1234abcd.html --target html`.
2. Copy the minimised input into the test. It is usually small enough to inline as a string or a `byte[]` literal; for a
   binary input use `Convert.FromBase64String("...")` (`base64 -w0 file`).
3. Add a test to `tests/PdfPecker.Tests/HostileInputTests.cs` (class is in `HostileInputCollection`), bounded like the
   others so that a regression fails instead of hanging:

   ```csharp
   [Fact]
   public void Css_page_margins_that_leave_no_room_do_not_throw()
   {
       // Found by the fuzzer: target html, exception-8f292e15.
       AssertStructurallyValid(Bounded("<style>@page{margin:483"));
   }
   ```

   For a byte input use the matching API with a token, or `Bounded(() => ...)`, and assert either the documented
   exception (`Assert.Throws<InvalidDataException>`) or a valid result. It must fail, or time out, before the fix.
4. Fix the library, confirm the test passes and run the full suite. Do not commit the findings folder.
