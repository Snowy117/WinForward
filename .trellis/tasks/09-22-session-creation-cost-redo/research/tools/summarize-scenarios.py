#!/usr/bin/env python3
"""Aggregate udp.churn / udp.sessionFootprint JSONL rows from the 09-22 redo campaign.

Usage:
  summarize-scenarios.py waves     <raw-dir>
  summarize-scenarios.py sustained <raw-dir>
  summarize-scenarios.py incheck   <raw-dir>
  summarize-scenarios.py footprint <raw-dir>
"""

import glob
import json
import os
import sys


def results(path):
    out = []
    with open(path, encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if line:
                row = json.loads(line)
                if row.get("type") == "result":
                    out.append(row)
    return out


def main():
    mode, raw = sys.argv[1], sys.argv[2]
    if mode == "waves":
        print(f"{'N':>4} {'D':>3} | {'run1':>10} {'run2':>10} {'run3':>10} | {'mean':>10} {'spread':>8} | acc/rej/resp | gen0/1/2 | retireMs | p50/p95")
        for n in (48, 128, 256):
            for d in (0, 5, 20):
                values, meta = [], None
                for run in (1, 2, 3):
                    path = os.path.join(raw, f"udpchurn-run{run}-N{n}-D{d}.jsonl")
                    rows = results(path)
                    if len(rows) != 1:
                        print(f"  !! {path} has {len(rows)} result rows")
                        continue
                    row = rows[0]
                    metrics = row["metrics"]
                    values.append(metrics["bytesPerSession"])
                    meta = metrics
                mean = sum(values) / len(values)
                spread = max(values) - min(values)
                print(f"{n:>4} {d:>3} | {values[0]:>10.0f} {values[1]:>10.0f} {values[2]:>10.0f} | {mean:>10.1f} {spread:>8.1f} | "
                      f"{meta['accepted']}/{meta['rejected']}/{meta['firstResponses']} | "
                      f"{meta['gen0Collections']}/{meta['gen1Collections']}/{meta['gen2Collections']} | "
                      f"{meta['retireMs']:>8.2f} | {meta['firstResponseMs']['p50']:>7.2f}/{meta['firstResponseMs']['p95']:>7.2f}")
    elif mode == "sustained":
        print(f"{'D':>3} {'run':>3} | {'waves':>7} {'sessions':>9} {'allocated':>12} {'B/session':>10} {'MB/s':>7} {'sess/s':>8} {'wallS':>7} | gen0/1/2 | MB/gen0")
        for d in (0, 5):
            for run in (1, 2, 3):
                path = os.path.join(raw, f"udpchurn-sustained-D{d}-run{run}.jsonl")
                for row in results(path):
                    m = row["metrics"]
                    per_gen0 = m["allocatedBytes"] / m["gen0Collections"] / 1024 / 1024 if m["gen0Collections"] else 0
                    print(f"{d:>3} {run:>3} | {m['waves']:>7} {m['sessions']:>9} {m['allocatedBytes']:>12} {m['bytesPerSession']:>10.1f} "
                          f"{m['bytesPerSecond'] / 1024 / 1024:>7.2f} {m['achievedSessionsPerSecond']:>8.1f} {m['wallSeconds']:>7.1f} | "
                          f"{m['gen0Collections']}/{m['gen1Collections']}/{m['gen2Collections']} | {per_gen0:>7.2f}")
    elif mode == "incheck":
        for row in results(os.path.join(raw, "udpchurn-incheck-N48-D0.jsonl")):
            m = row["metrics"]
            print(f"in-process N=48 D=0: bytesPerSession={m['bytesPerSession']:.1f} allocated={m['allocatedBytes']} "
                  f"accepted={m['accepted']}/{m['rejected']} gen0={m['gen0Collections']}")
    elif mode == "footprint":
        for run in (1, 2, 3):
            for row in results(os.path.join(raw, f"footprint-run{run}.jsonl")):
                m = row["metrics"]
                print(f"run{run} sessions={row['parameters'].get('sessions', '?'):>5} "
                      f"workingSetDelta={m.get('workingSetDeltaBytes', '?')} allocated={m['allocatedBytes']:>10} "
                      f"gen0={m['gen0Collections']}")


if __name__ == "__main__":
    main()
