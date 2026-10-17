#!/usr/bin/env python3
"""Compares a perf.json run with a baseline and writes a Markdown table.

Usage: perf-compare.py current.json [baseline.json]

Exits 1 when a case allocates more than ALLOC_LIMIT more bytes, or is more than TIME_LIMIT slower once the runner's
own speed is taken out. Allocation per conversion is deterministic, so its limit is tight and needs no correction.

Time is not: GitHub's hosted runners come in several processor models and are shared, so the same commit measured
twice a few minutes apart came out 20 to 90 percent slower, and not by the same factor in every case. Time is
therefore only judged against a baseline from the same processor model, which the workflow picks from recent runs.
There, the median of the cases' time ratios is taken as the runner's speed against the baseline's, and a case fails
when it strays more than TIME_LIMIT from that median, or when the median itself is more than TIME_LIMIT slower. On a
different processor the times are shown but not judged.

Without a baseline the run only reports the numbers and passes. A case that the baseline does not have yet, because it
was added since, is reported as new and not compared.
"""
import json
import statistics
import sys

TIME_LIMIT = 0.25
ALLOC_LIMIT = 0.10


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

        if time_delta is not None and same_cpu:
            against_runner = (1 + time_delta) / runner - 1
            if against_runner > TIME_LIMIT:
                failures.append(f"{name}: {time_delta:+.0%} slower, {against_runner:+.0%} against the other cases (limit {TIME_LIMIT:+.0%})")
        if alloc_delta is not None and alloc_delta > ALLOC_LIMIT:
            failures.append(f"{name}: {alloc_delta:+.0%} allocated bytes (limit {ALLOC_LIMIT:+.0%})")

        new = bool(baseline) and base is None
        print(f"| {name} | {shown} | {fmt(time_delta, new)} | {alloc} | {fmt(alloc_delta, new)} |")

    print()
    if baseline:
        machine = f"same processor ({cpu})" if same_cpu else f"{cpu or 'unknown processor'}, baseline on {base_cpu or 'an unrecorded processor'}"
        print(f"Runner speed against the baseline: {runner - 1:+.0%} median over the cases; {machine}.")
        print()
        if not same_cpu:
            print("No earlier run on this processor model, so times are shown but not judged; allocation still is.")
            print()
        elif runner - 1 > TIME_LIMIT:
            failures.append(f"every case: {runner - 1:+.0%} slower on the same processor (limit {TIME_LIMIT:+.0%})")
    if not baseline:
        print("No baseline from an earlier run, so nothing was compared.")
    elif failures:
        print("**Regression**")
        for f in failures:
            print(f"- {f}")
    else:
        judged = f"time {TIME_LIMIT:.0%}, " if same_cpu else ""
        print(f"Within limits: {judged}allocation {ALLOC_LIMIT:.0%}.")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
