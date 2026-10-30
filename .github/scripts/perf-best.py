#!/usr/bin/env python3
"""Keeps the better time of two benchmark runs of the same build for each case.

Usage: perf-best.py first.json second.json

Writes the result over first.json. A hosted runner can be slow for a minute while a neighbour is busy, and one such
spell during one case failed the nightly job for that case alone (report-30, +34% against the other cases, with the
same speed as the release before it when measured again). A real slowdown shows in both runs, so the better of the two
is what the comparison judges. Allocation is the same in both runs and is kept from the first.
"""
import json
import sys


def main():
    with open(sys.argv[1], encoding="utf-8") as f:
        first = json.load(f)
    with open(sys.argv[2], encoding="utf-8") as f:
        second = {c["name"]: c for c in json.load(f)["cases"]}
    for case in first["cases"]:
        other = second.get(case["name"])
        if not other:
            continue
        if case.get("perSecond") is not None and other.get("perSecond") is not None:
            case["perSecond"] = max(case["perSecond"], other["perSecond"])
        elif case.get("meanMs") and other.get("meanMs"):
            case["meanMs"] = min(case["meanMs"], other["meanMs"])
    with open(sys.argv[1], "w", encoding="utf-8") as f:
        json.dump(first, f, indent=2)
        f.write("\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
