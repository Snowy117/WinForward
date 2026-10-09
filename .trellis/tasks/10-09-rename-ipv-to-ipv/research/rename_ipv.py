#!/usr/bin/env python3

import argparse
import pathlib
import sys

OLD = "Ipv"
NEW = "IPv"


def load_targets(list_path: pathlib.Path) -> list[pathlib.Path]:
    targets = []
    for raw in list_path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        p = pathlib.Path(line)
        if not p.is_file():
            sys.exit(f"FATAL: target does not exist: {line}")
        targets.append(p)
    if not targets:
        sys.exit("FATAL: empty target list")
    return targets


def main() -> int:
    ap = argparse.ArgumentParser(
        description="Rewrite Ipv4/Ipv6 to IPv4/IPv6 in the .cs files named by LIST, "
                    "one repo-relative path per line.")
    mode = ap.add_mutually_exclusive_group(required=True)
    mode.add_argument("--plan", action="store_true",
                      help="report the hit count per file and write nothing")
    mode.add_argument("--apply", action="store_true",
                      help="rewrite the files in place")
    ap.add_argument("list", type=pathlib.Path,
                    help="file holding the target paths")
    args = ap.parse_args()

    targets = load_targets(args.list)
    touched = 0
    total_hits = 0
    residual = []

    for p in targets:
        text = p.read_text(encoding="utf-8")
        hits = text.count(OLD)
        if hits == 0:
            continue
        if args.plan:
            print(f"{hits:5}  {p}")
            touched += 1
            total_hits += hits
            continue
        new_text = text.replace(OLD, NEW)
        p.write_text(new_text, encoding="utf-8")
        after = new_text.count(OLD)
        if after:
            residual.append((p, after))
        touched += 1
        total_hits += hits
        print(f"{hits:5}  {p}")

    verb = "would rewrite" if args.plan else "rewrote"
    print(f"\n{verb} {total_hits} occurrences in {touched} files")
    if residual:
        print("FATAL: residual 'Ipv' after rewrite:")
        for p, n in residual:
            print(f"  {n:5}  {p}")
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
