#!/usr/bin/env python3
"""Validates the PDFs written by tools/ConformanceSamples with veraPDF and compares the failures with known-failures.txt.

Usage: check.py SAMPLES_DIR VERAPDF_JARS_DIR [--allowlist FILE]

Every profile folder in SAMPLES_DIR is checked against the veraPDF flavours listed for it in profiles.tsv. The exit
status is 1 when a rule fails that known-failures.txt does not list for that profile and flavour, or when veraPDF could
not process a file. Rules that are listed but no longer fail are reported, so the list shrinks as the library improves.
"""
import argparse
import collections
import glob
import json
import os
import subprocess
import sys

MAIN_CLASS = "org.verapdf.apps.GreenfieldCliWrapper"


def load_allowlist(path):
    """Lines of 'profile flavour clause testNumber', with # comments. Returns a set of tuples."""
    allowed = set()
    if not path or not os.path.exists(path):
        return allowed
    with open(path, encoding="utf-8") as handle:
        for line in handle:
            line = line.split("#", 1)[0].strip()
            if not line:
                continue
            parts = line.split()
            if len(parts) != 4:
                sys.exit(f"{path}: expected 'profile flavour clause testNumber', got: {line}")
            allowed.add(tuple(parts))
    return allowed


def validate(jars, flavour, files):
    command = ["java", "-cp", os.path.join(jars, "*"), MAIN_CLASS, "-f", flavour, "--format", "json",
               "--maxfailuresdisplayed", "1"] + files
    result = subprocess.run(command, capture_output=True, text=True)
    try:
        return json.loads(result.stdout)["report"]
    except (ValueError, KeyError):
        sys.exit(f"veraPDF produced no report for flavour {flavour} (exit {result.returncode}):\n{result.stderr[-2000:]}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("samples")
    parser.add_argument("jars")
    parser.add_argument("--allowlist", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "known-failures.txt"))
    args = parser.parse_args()

    allowed = load_allowlist(args.allowlist)
    seen_allowed = set()
    new_failures = []
    broken = []
    totals = collections.OrderedDict()

    with open(os.path.join(args.samples, "profiles.tsv"), encoding="utf-8") as handle:
        profiles = [line.rstrip("\n").split("\t") for line in handle if line.strip()]

    for profile, flavour_list in profiles:
        flavours = [f for f in flavour_list.split(",") if f]
        files = sorted(glob.glob(os.path.join(args.samples, profile, "*.pdf")))
        if not flavours:
            print(f"{profile}: {len(files)} files generated, no veraPDF profile for this level")
            continue
        for flavour in flavours:
            report = validate(args.jars, flavour, files)
            summary = report["batchSummary"]
            if summary.get("failedParsingJobs") or summary.get("veraExceptions") or summary.get("outOfMemory"):
                broken.append(f"{profile} {flavour}: veraPDF could not process every file ({summary})")
            rules = collections.OrderedDict()  # (clause, test) -> (description, [(file, failed checks)])
            compliant = 0
            for job in report["jobs"]:
                name = os.path.basename(job["itemDetails"]["name"])
                results = job.get("validationResult") or []
                if not results:
                    broken.append(f"{profile} {flavour} {name}: no validation result")
                    continue
                for result in results:
                    if result.get("compliant"):
                        compliant += 1
                    for rule in result["details"]["ruleSummaries"]:
                        key = (rule["clause"], str(rule["testNumber"]))
                        entry = rules.setdefault(key, (rule["description"], []))
                        entry[1].append((name, rule["failedChecks"]))
            totals[(profile, flavour)] = (compliant, len(files))
            print(f"{profile} [{flavour}]: {compliant}/{len(files)} files compliant")
            for (clause, test), (description, hits) in rules.items():
                known = (profile, flavour, clause, test) in allowed
                if known:
                    seen_allowed.add((profile, flavour, clause, test))
                else:
                    new_failures.append((profile, flavour, clause, test))
                count = sum(c for _, c in hits)
                label = "known" if known else "NEW"
                print(f"    {label}: {clause} test {test}, {count} failed checks in {len(hits)} files "
                      f"({', '.join(n for n, _ in hits[:4])}{', ...' if len(hits) > 4 else ''}): {description}")

    stale = sorted(allowed - seen_allowed)
    for entry in stale:
        print("note: known failure no longer occurs, remove it from known-failures.txt: " + " ".join(entry))
    for message in broken:
        print("ERROR: " + message)
    if new_failures:
        print(f"\n{len(new_failures)} rule(s) fail that known-failures.txt does not list.")
    return 1 if (new_failures or broken) else 0


if __name__ == "__main__":
    sys.exit(main())
