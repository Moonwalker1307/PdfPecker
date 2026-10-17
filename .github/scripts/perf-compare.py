#!/usr/bin/env python3
"""Compares a perf.json run with a baseline and writes a Markdown table.

Usage: perf-compare.py current.json [baseline.json]

Exits 1 when a case allocates more than ALLOC_LIMIT more bytes, or is more than TIME_LIMIT slower once the runner's
own speed is taken out. Allocation per conversion is deterministic, so its limit is tight and needs no correction.

Time is not: GitHub's hosted runners come in several processor models and are shared, so the same commit measured
twice a few minutes apart came out 20 to 70 percent slower in every case. The median of the cases' time ratios is
taken as the runner's speed against the baseline's, and each case is judged by how far it strays from that median, so
one case that slows down is still caught while a slower runner is not. A broad slowdown of every case cannot be told
apart from a slower runner, so it only fails when both runs report the same processor model; otherwise it is shown.

Without a baseline the run only reports the numbers and passes. A case that the baseline does not have yet, because it
was added since, is reported as new and not compared.
"""
import json
import statistics
import sys

TIME_LIMIT = 0.25
ALLOC_LIMIT = 0.10
# On a different processor the cases do not all scale alike (a GC-heavy case leans more on memory than a decoder), so
# the limit for a case against the median is wider there.
OTHER_CPU_TIME_LIMIT = 0.40


def load(path):
    with open(path, encoding="utf-8") as f:
        data = json.load(f)
    return data.get("cpu"), {c["name"]: c for c in data["cases"]}


def change(new, old):
    return (new - old) / old if old else 0.0


def fmt(delta, new=False):
    if new:
        return "new"
    return "none" if delta is None else f"{delta:+.1%}"


def main():
    cpu, current = load(sys.argv[1])
    base_cpu, baseline = load(sys.argv[2]) if len(sys.argv) > 2 else (None, {})
    same_cpu = cpu is not None and cpu == base_cpu
    failures = []

    # How much slower this runner is than the baseline's, as the median over the cases both runs have.
    ratios = []
    for name, cur in current.items():
        base = baseline.get(name)
        if not base:
            continue
        if cur["perSecond"] is not None:
            if cur["perSecond"] and base["perSecond"]:
                ratios.append(base["perSecond"] / cur["perSecond"])
        elif base["meanMs"]:
            ratios.append(cur["meanMs"] / base["meanMs"])
    runner = statistics.median(ratios) if ratios else 1.0
    case_limit = TIME_LIMIT if same_cpu else OTHER_CPU_TIME_LIMIT

    print("| Case | Time | vs baseline | Alloc/op (KB) | vs baseline |")
    print("|---|---:|---:|---:|---:|")
    for name, cur in current.items():
        base = baseline.get(name)
        # Throughput is documents per second, where lower is worse; every other time is milliseconds, where higher is.
        if cur["perSecond"] is not None:
            shown = f"{cur['perSecond']:.0f} docs/s"
            time_delta = change(base["perSecond"], cur["perSecond"]) if base and base["perSecond"] else None
        else:
            shown = f"{cur['meanMs']:.3f} ms" if cur["meanMs"] < 1 else f"{cur['meanMs']:.2f} ms"
            time_delta = change(cur["meanMs"], base["meanMs"]) if base else None
        alloc = f"{cur['allocBytes'] / 1024:.0f}" if cur["allocBytes"] else "n/a"
        alloc_delta = change(cur["allocBytes"], base["allocBytes"]) if base and cur["allocBytes"] else None

        if time_delta is not None:
            against_runner = (1 + time_delta) / runner - 1
            if against_runner > case_limit:
                failures.append(f"{name}: {time_delta:+.0%} slower, {against_runner:+.0%} against the other cases (limit {case_limit:+.0%})")
        if alloc_delta is not None and alloc_delta > ALLOC_LIMIT:
            failures.append(f"{name}: {alloc_delta:+.0%} allocated bytes (limit {ALLOC_LIMIT:+.0%})")

        new = bool(baseline) and base is None
        print(f"| {name} | {shown} | {fmt(time_delta, new)} | {alloc} | {fmt(alloc_delta, new)} |")

    print()
    if baseline:
        machine = f"same processor ({cpu})" if same_cpu else f"{cpu or 'unknown processor'}, baseline on {base_cpu or 'an unrecorded processor'}"
        print(f"Runner speed against the baseline: {runner - 1:+.0%} median over the cases; {machine}.")
        print()
        if runner - 1 > TIME_LIMIT:
            if same_cpu:
                failures.append(f"every case: {runner - 1:+.0%} slower on the same processor (limit {TIME_LIMIT:+.0%})")
            else:
                print(f"Every case is {runner - 1:+.0%} slower, but on a different processor, so this is not counted as a regression.")
                print()
    if not baseline:
        print("No baseline from an earlier run, so nothing was compared.")
    elif failures:
        print("**Regression**")
        for f in failures:
            print(f"- {f}")
    else:
        print(f"Within limits: time {case_limit:.0%} against the other cases, allocation {ALLOC_LIMIT:.0%}.")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
