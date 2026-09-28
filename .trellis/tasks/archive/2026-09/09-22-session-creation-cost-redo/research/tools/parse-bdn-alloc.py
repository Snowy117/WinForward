#!/usr/bin/env python3
"""Extract per-case Allocated bytes from BenchmarkDotNet markdown logs and aggregate them
with the conventions of the 09-21-session-creation-cost research docs.

Conventions (see archive/2026-09/09-21-session-creation-cost/research/framework-decomposition.md):
- BDN's "KB" is 1024 B; the summary table's Allocated column is the per-invocation total.
- Ladder per-session value = Allocated(Sessions=1000) / 1000 (mean and max-min across runs).
- Probe marginal = (Allocated(Sessions=1000) - Allocated(Sessions=1)) / 999.

Usage:
  parse-bdn-alloc.py ladder <log> [<log> ...]
  parse-bdn-alloc.py probe  <log> [<log> ...]
  parse-bdn-alloc.py raw    <log> [<log> ...]
"""

import re
import sys

ROW = re.compile(r"^\|\s*([A-Za-z0-9_]+)\s*\|\s*(\d+)\s*\|.*\|\s*([\d.]+)\s*(B|KB|MB|GB)\s*\|\s*$")
UNITS = {"B": 1, "KB": 1024, "MB": 1024 ** 2, "GB": 1024 ** 3}


def parse(path):
    cases = {}
    with open(path, encoding="utf-8", errors="replace") as handle:
        for line in handle:
            match = ROW.match(line.rstrip("\n"))
            if match:
                method, sessions, value, unit = match.groups()
                cases[(method, int(sessions))] = int(float(value) * UNITS[unit])
    return cases


def main():
    mode, paths = sys.argv[1], sys.argv[2:]
    per_run = [parse(path) for path in paths]

    if mode == "raw":
        for path, cases in zip(paths, per_run):
            print(f"== {path}")
            for (method, sessions), allocated in sorted(cases.items()):
                print(f"   {method:38s} N={sessions:<5d} {allocated:>12,d}")
        return

    cases_before = set(per_run[0])
    for case in sorted(cases_before):
        if len(per_run) > 1 and any(case not in run for run in per_run):
            continue
        method, sessions = case
        if mode == "ladder":
            if sessions != 1000:
                continue
            values = [run[case] / 1000 for run in per_run]
            print(f"{method:38s} per-session={sum(values) / len(values):>10.1f}  spread={max(values) - min(values):>8.1f}  runs={[round(v, 1) for v in values]}")
        elif mode == "probe":
            if sessions != 1:
                continue
            if (method, 1000) not in per_run[0]:
                continue
            values = [(run[(method, 1000)] - run[case]) / 999 for run in per_run]
            print(f"{method:38s} marginal={sum(values) / len(values):>10.1f}  spread={max(values) - min(values):>8.1f}  runs={[round(v, 1) for v in values]}")


if __name__ == "__main__":
    main()
