#!/usr/bin/env python3
"""Compares a perf.json run with a baseline and writes a Markdown table.

Usage: perf-compare.py current.json [baseline.json [time-baseline.json]]

Exits 1 when a case allocates more than ALLOC_LIMIT more bytes, or is more than TIME_LIMIT slower once the runner's
own speed is taken out. Allocation per conversion is deterministic, so its limit is tight and needs no correction.

Time is not: GitHub's hosted runners come in several processor models and are shared, so the same commit measured
twice a few minutes apart came out 20 to 90 percent slower, and not by the same factor in every case. Time is
therefore only judged against a baseline from the same processor model, which the workflow picks from recent runs and
passes as time-baseline.json when it is not the run that baseline.json comes from. There, the median of the cases' time ratios is taken as the runner's speed against the baseline's, and a case fails
when it strays more than TIME_LIMIT from that median, or when the median itself is more than TIME_LIMIT slower. On a
different processor the times are shown but not judged.

Without a baseline the run only reports the numbers and passes. A case that the baseline does not have yet, because it
was added since, is reported as new and not compared, and so is a case whose input files changed (the "inputs"
fingerprint), such as one that runs on every test font when a test adds a font.
"""
import json
import statistics
import sys

TIME_LIMIT = 0.25
ALLOC_LIMIT = 0.10


def load(path):
    with open(path, encoding="utf-8") as f:
        data = json.load(f)
    return data.get("cpu"), data.get("allocation"), {c["name"]: c for c in data["cases"]}


def change(new, old):
    return (new - old) / old if old else 0.0


def fmt(delta):
    return "none" if delta is None else f"{delta:+.1%}"


def comparable(cur, base):
    """The baseline's figures for a case, or None when it lacks the case or ran it on other input files."""
    if not base or cur.get("inputs") != base.get("inputs", cur.get("inputs")):
        return None
    return base


def main():
    cpu, counted, current = load(sys.argv[1])
    base_cpu, base_counted, baseline = load(sys.argv[2]) if len(sys.argv) > 2 else (None, None, {})
    # Allocation is judged against the newest run, and time against the newest run on the same processor model.
    time_cpu, _, time_baseline = load(sys.argv[3]) if len(sys.argv) > 3 else (base_cpu, None, baseline)
    same_cpu = cpu is not None and cpu == time_cpu
    # Runs from before allocation was counted on every thread have figures of their own that are not comparable.
    same_count = counted == base_counted
    failures = []

    # How much slower this runner is than the time baseline's, as the median over the cases both runs have.
    ratios = []
    for name, cur in current.items():
        base = comparable(cur, time_baseline.get(name))
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
        base = comparable(cur, time_baseline.get(name))
        alloc_base = comparable(cur, baseline.get(name))
        # Throughput is documents per second, where lower is worse; every other time is milliseconds, where higher is.
        if cur["perSecond"] is not None:
            shown = f"{cur['perSecond']:.0f} docs/s"
            time_delta = change(base["perSecond"], cur["perSecond"]) if base and base["perSecond"] else None
        else:
            shown = f"{cur['meanMs']:.3f} ms" if cur["meanMs"] < 1 else f"{cur['meanMs']:.2f} ms"
            time_delta = change(cur["meanMs"], base["meanMs"]) if base else None
        alloc = f"{cur['allocBytes'] / 1024:.0f}" if cur["allocBytes"] else "n/a"
        alloc_delta = change(cur["allocBytes"], alloc_base["allocBytes"]) if alloc_base and same_count and cur["allocBytes"] else None

        if time_delta is not None and same_cpu:
            against_runner = (1 + time_delta) / runner - 1
            if against_runner > TIME_LIMIT:
                failures.append(f"{name}: {time_delta:+.0%} slower, {against_runner:+.0%} against the other cases (limit {TIME_LIMIT:+.0%})")
        if alloc_delta is not None and alloc_delta > ALLOC_LIMIT:
            failures.append(f"{name}: {alloc_delta:+.0%} allocated bytes (limit {ALLOC_LIMIT:+.0%})")

        # A case is new when the baseline lacks it, and not compared when its input files changed since.
        mark = None
        if baseline and name not in baseline:
            mark = "new"
        elif baseline and alloc_base is None:
            mark = "inputs changed"
        time_shown = mark if mark and base is None else fmt(time_delta)
        alloc_shown = mark if mark else fmt(alloc_delta)
        print(f"| {name} | {shown} | {time_shown} | {alloc} | {alloc_shown} |")

    print()
    if baseline:
        machine = f"same processor ({cpu})" if same_cpu else f"{cpu or 'unknown processor'}, baseline on {time_cpu or 'an unrecorded processor'}"
        print(f"Runner speed against the time baseline: {runner - 1:+.0%} median over the cases; {machine}.")
        print()
        if not same_cpu:
            print("No earlier run on this processor model, so times are shown but not judged; allocation still is.")
            print()
        if not same_count:
            print("The baseline counted allocation in another way, so allocation is shown but not judged.")
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
        judged = [f"time {TIME_LIMIT:.0%}"] if same_cpu else []
        judged += [f"allocation {ALLOC_LIMIT:.0%}"] if same_count else []
        print(f"Within limits: {', '.join(judged) or 'nothing judged'}.")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
