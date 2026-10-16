#!/usr/bin/env python3
"""Compares a perf.json run with a baseline and writes a Markdown table.

Usage: perf-compare.py current.json [baseline.json]

Exits 1 when a case is more than TIME_LIMIT slower or allocates more than ALLOC_LIMIT more bytes. Allocation per
conversion is deterministic, so its limit is tight; time is noisy on shared runners, so its limit is wide.
Without a baseline the run only reports the numbers and passes. A case that the baseline does not have yet, because it
was added since, is reported as new and not compared.
"""
import json
import sys

TIME_LIMIT = 0.25
ALLOC_LIMIT = 0.10


def load(path):
    with open(path, encoding="utf-8") as f:
        return {c["name"]: c for c in json.load(f)["cases"]}


def change(new, old):
    return (new - old) / old if old else 0.0


def fmt(delta, new=False):
    if new:
        return "new"
    return "none" if delta is None else f"{delta:+.1%}"


def main():
    current = load(sys.argv[1])
    baseline = load(sys.argv[2]) if len(sys.argv) > 2 else {}
    failures = []

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

        if time_delta is not None and time_delta > TIME_LIMIT:
            failures.append(f"{name}: {time_delta:+.0%} slower (limit {TIME_LIMIT:+.0%})")
        if alloc_delta is not None and alloc_delta > ALLOC_LIMIT:
            failures.append(f"{name}: {alloc_delta:+.0%} allocated bytes (limit {ALLOC_LIMIT:+.0%})")

        new = bool(baseline) and base is None
        print(f"| {name} | {shown} | {fmt(time_delta, new)} | {alloc} | {fmt(alloc_delta, new)} |")

    print()
    if not baseline:
        print("No baseline from an earlier run, so nothing was compared.")
    elif failures:
        print("**Regression**")
        for f in failures:
            print(f"- {f}")
    else:
        print(f"Within limits: time {TIME_LIMIT:.0%}, allocation {ALLOC_LIMIT:.0%}.")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
