#!/usr/bin/env python3
"""List every cross-document citation that names a section, so the integration pass can retarget them.

A split moves sections between files: a link whose *label* says "§3.9" or "Allocation-gate stability"
keeps resolving as a path but starts pointing at the wrong place, and a code comment that cites a
section by number or title breaks silently because nothing compiles against documentation.

Prints, grouped by citing file:
  - spec-document links whose label or fragment names a section
  - `§`-style citations inside spec prose
  - spec-document citations in code comments (excluding the frozen benchmarks/results records)

Usage, from the repo root:
    python3 .trellis/tasks/10-09-spec-revision/research/list-citations.py
    python3 .trellis/tasks/10-09-spec-revision/research/list-citations.py --code-only
"""

from __future__ import annotations

import argparse
import pathlib
import re
import subprocess

ROOT = pathlib.Path(__file__).resolve().parents[4]
SPEC = ROOT / ".trellis" / "spec"
DOCS = sorted(p.name for p in SPEC.rglob("*.md") if p.name != "index.md")
DOC_PATTERN = "|".join(re.escape(name) for name in DOCS)

SECTION_LABEL = re.compile(r"§|section|Section")
LINK = re.compile(r"\[([^\]]*)\]\(([^)\s]+)\)")


def spec_citations() -> None:
    print("== spec prose: links whose label names a section ==")
    for path in sorted(SPEC.rglob("*.md")):
        for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
            for label, target in LINK.findall(line):
                if target.startswith(("http", "mailto")):
                    continue
                if SECTION_LABEL.search(label) or "#" in target:
                    print(f"  {path.relative_to(ROOT)}:{number}: [{label}]({target})")
    print("\n== spec prose: '§' citations ==")
    for path in sorted(SPEC.rglob("*.md")):
        for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
            if "§" in line:
                print(f"  {path.relative_to(ROOT)}:{number}: {line.strip()[:150]}")


def code_citations() -> None:
    print("\n== code comments citing a spec document (benchmarks/results excluded) ==")
    out = subprocess.run(
        ["rg", "-n", "--no-heading", "-e", DOC_PATTERN, "-g", "!benchmarks/results/**",
         "-g", "!.trellis/**", "src", "tests", "benchmarks", "analyzers"],
        cwd=ROOT, capture_output=True, text=True, check=False).stdout
    for line in sorted(out.splitlines()):
        print(f"  {line.strip()[:170]}")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--code-only", action="store_true")
    args = parser.parse_args()
    if not args.code_only:
        spec_citations()
    code_citations()


if __name__ == "__main__":
    main()
