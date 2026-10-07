#!/usr/bin/env python3
"""Compare the frozen Python oracle with the C# analysis, one batch of slices at a time.

Both implementations write a *complete* `tables.md` and `verdict.json`; the progress of the C#
port is measured by comparing only the slices one batch owns, so a batch can be declared done while
the batches after it are still placeholders. The separation lives here and nowhere else:

    BATCH_SECTIONS = {batch: {"tables.md": [...], "verdict.json": [...]}}

Tables are sliced by their `## N.` heading (plus a `preamble` slice, everything before `## 0.`, which
holds the `--raw` value and the program-name line), the verdict by top-level key -- with `metrics`
split into `metrics/<key>` so its extractors can land in two batches.

Exit codes (three states, never two):

    0   every slice of this batch exists on both sides and is equal
    1   every slice exists but at least one differs
    2   a slice that should exist does not (a key the batch owns is missing, a whole file is
        missing, the frozen tree could not be extracted, the ledger count is not what the tree
        guarantees), or this script's own tables are wrong -- "nothing to compare" is never a pass,
        and an infrastructure failure must not be reportable as "the content differs"

The script is the only caller of the two sides, and it calls both with the same hardcoded absolute
path (`/tmp/wf-synth/raw`): the raw path, the two ledger paths and §2's path columns are part of the
compared bytes, so a tree extracted anywhere else would compare unequal for the wrong reason.

Usage:
    python3 oracle-diff.py --batch 3
    python3 oracle-diff.py --batch 1a,1b,1c --cs-out DIR --golden DIR

`--cs-out` compares an existing output directory instead of running the analyzer, and `--golden`
overrides the frozen reference; both exist for the mechanism's own self-checks (a skeleton must be
reported as missing slices, a deliberately changed number as a difference).
"""

from __future__ import annotations

import argparse
import json
import re
import shutil
import subprocess
import sys
import tarfile
from pathlib import Path
from typing import NoReturn

REPO_ROOT = Path(__file__).resolve().parents[3]
VERIFICATION = REPO_ROOT / "benchmarks" / "WinForward.E2E.Analysis" / "verification"
GOLDEN = VERIFICATION / "golden"
ARCHIVE = VERIFICATION / "synthetic-tree.tar.gz"
ANALYZER = (
    REPO_ROOT / "benchmarks" / "WinForward.E2E.Analysis" / "bin" / "Release" / "net10.0"
    / "WinForward.E2E.Analysis"
)

# The one path both sides are called with; every byte of it is part of the compared output.
TREE = Path("/tmp/wf-synth")
RAW = TREE / "raw"
COMPARISON = Path("/tmp/wf-oracle")
CS_OUT = COMPARISON / "cs"

PREAMBLE = "preamble"
SECTION_PATTERN = re.compile(r"^## (\d+)\.", re.MULTILINE)

# The names the frozen reference stores its two files under; a produced directory uses the plain ones.
GOLDEN_NAMES = {"tables.md": "py-tables.md", "verdict.json": "py-verdict.json"}

BATCH_SECTIONS = {
    "1a": {
        "tables.md": [PREAMBLE, "15"],
        "verdict.json": ["generated_by", "raw", "flat_mode", "passes", "rows"],
    },
    "1b": {
        "tables.md": [],
        "verdict.json": ["bootstrap", "thresholds"],
    },
    "1c": {
        "tables.md": ["1", "2"],
        "verdict.json": ["row_profiles"],
    },
    "2": {
        "tables.md": ["0", "3"],
        "verdict.json": ["findings", "findings_by_severity"],
    },
    "3": {
        "tables.md": ["5", "8", "9"],
        "verdict.json": [
            "metrics/lat.tcp_rtt.p50",
            "metrics/lat.tcp_rtt.p99",
            "metrics/lat.udp_rtt.p50",
            "metrics/lat.udp_lossRate",
            "metrics/latload.tcp_rtt.p50",
            "metrics/latload.tcp_rtt.p99",
            "metrics/loss.lossRate",
            "metrics/loss.corruptRate",
            "metrics/loss.foreignConnection",
            "metrics/dns.answerRate",
            "metrics/dns.rtt.p50",
            "metrics/dnsalt.answerRate",
            "metrics/dnsalt.rtt.p50",
            "metrics/thru.goodputMbps",
            "metrics/mix.udp.lossRate",
        ],
    },
    "4": {
        "tables.md": ["4", "6", "7", "10", "11"],
        "verdict.json": [
            "metrics/rel.unexpectedEofRate",
            "metrics/rel.fidelityRate",
            "metrics/persist.responseRate",
            "metrics/persist.reconnects",
            "metrics/mem.privateBytes.p50",
            "metrics/cpu.proxy.vcpuPct",
        ],
    },
    "5": {
        "tables.md": ["12", "13", "14"],
        "verdict.json": ["control_blocks", "dual_phase", "ledger"],
    },
}

# `--batch 1` is the whole first batch; the sub-batches exist so the port can be landed in halves.
BATCH_ALIASES = {"1": ["1a", "1b", "1c"]}

# Every table heading the analysis writes, and every top-level verdict key, so the coverage check
# below can refuse a BATCH_SECTIONS table that silently drops one.
ALL_SECTIONS = [PREAMBLE] + [str(number) for number in range(16)]
ALL_VERDICT_KEYS = [
    "generated_by", "raw", "flat_mode", "passes", "rows", "row_profiles", "bootstrap", "thresholds",
    "findings", "findings_by_severity", "control_blocks", "dual_phase", "ledger",
]


def fail(message: str) -> NoReturn:
    """Report that there is nothing to compare, and exit with the code that says so (rc=2)."""
    print(f"rc=2: {message}", file=sys.stderr)
    raise SystemExit(2)


def batches_of(names: list[str]) -> list[str]:
    """Expand the requested batch names, rejecting an unknown one."""
    expanded: list[str] = []
    for name in names:
        for candidate in BATCH_ALIASES.get(name, [name]):
            if candidate not in BATCH_SECTIONS:
                fail(f"unknown batch '{name}'; known: " + ", ".join(BATCH_SECTIONS))
            if candidate not in expanded:
                expanded.append(candidate)
    return expanded


def check_coverage() -> None:
    """Refuse a batch table that would let a slice fall outside every batch."""
    covered_sections: set[str] = set()
    covered_keys: set[str] = set()
    for batch, slices in BATCH_SECTIONS.items():
        if not slices["tables.md"] and not slices["verdict.json"]:
            fail(f"batch {batch} owns no slice; a batch that compares nothing would pass vacuously")
        covered_sections.update(slices["tables.md"])
        covered_keys.update(key.split("/", 1)[0] for key in slices["verdict.json"])

    unowned_sections = sorted(set(ALL_SECTIONS) - covered_sections)
    unowned_keys = sorted(set(ALL_VERDICT_KEYS) - covered_keys)
    if unowned_sections or unowned_keys:
        fail(
            "BATCH_SECTIONS does not own every slice: "
            f"tables {unowned_sections}, verdict keys {unowned_keys}"
        )


def extract_tree() -> None:
    """Unpack the frozen tree where both sides expect it, and assert its layout contract."""
    if TREE.exists():
        shutil.rmtree(TREE)
    TREE.mkdir(parents=True)
    with tarfile.open(ARCHIVE, "r:gz") as archive:
        archive.extractall(TREE, filter="data")

    ledgers = sorted(TREE.glob("*ledger*.jsonl"))
    if len(ledgers) < 2:
        fail(
            f"{ARCHIVE}: expected the tree beside the raw directory to hold at least two *ledger*.jsonl, "
            f"found {len(ledgers)}"
        )
    if not RAW.is_dir():
        fail(f"{ARCHIVE}: no raw/ directory inside the tarball")


def run_analyzer() -> None:
    """Run the C# analysis on the frozen tree, at the same hardcoded --raw path the golden used."""
    if not ANALYZER.is_file():
        fail(f"{ANALYZER}: the analyzer is not built; run `dotnet build WinForward.slnx -c Release`")
    if CS_OUT.exists():
        shutil.rmtree(CS_OUT)
    CS_OUT.mkdir(parents=True)
    completed = subprocess.run(
        [str(ANALYZER), "--raw", str(RAW), "--out", str(CS_OUT)],
        cwd=str(REPO_ROOT),
        capture_output=True,
        text=True,
        check=False,
    )
    if completed.stdout:
        sys.stdout.write(completed.stdout)
    if completed.returncode != 0:
        sys.stderr.write(completed.stderr)
        fail(f"the analyzer exited {completed.returncode}; there is nothing to compare")


def table_slices(text: str) -> dict[str, str]:
    """`tables.md` cut at its `## N.` headings, plus the preamble before the first one."""
    marks = [(match.start(), match.group(1)) for match in SECTION_PATTERN.finditer(text)]
    if not marks:
        return {}
    slices = {PREAMBLE: text[: marks[0][0]]}
    for index, (start, number) in enumerate(marks):
        end = marks[index + 1][0] if index + 1 < len(marks) else len(text)
        slices[number] = text[start:end]
    return slices


def verdict_slices(text: str) -> dict[str, str]:
    """`verdict.json` cut at its top-level keys, `metrics` one level further.

    A slice is the canonical re-serialization of the parsed subtree, so the comparison is over the
    keys and values themselves while key order inside the slice still counts (the file is written
    with `sort_keys=False`, so order is part of the output).
    """
    document = json.loads(text)
    slices = {}
    for key, value in document.items():
        if key == "metrics" and isinstance(value, dict):
            for metric, entry in value.items():
                slices[f"metrics/{metric}"] = json.dumps({metric: entry}, indent=2, ensure_ascii=False)
            continue
        slices[key] = json.dumps({key: value}, indent=2, ensure_ascii=False)
    return slices


def load_slices(directory: Path, name: str, reference: bool) -> dict[str, str] | None:
    """The slices of one output file, or None when the file itself is missing.

    The frozen reference keeps its two files under the `py-` names (`py-tables.md`,
    `py-verdict.json`) so the directory itself says which implementation produced them; a produced
    directory holds the plain names both implementations write.
    """
    path = directory / (GOLDEN_NAMES[name] if reference else name)
    if not path.is_file():
        return None
    text = path.read_text(encoding="utf-8")
    if name == "tables.md":
        return table_slices(text)
    try:
        return verdict_slices(text)
    except json.JSONDecodeError as error:
        fail(f"{path}: not valid JSON: {error}")


def first_difference(left: str, right: str) -> str:
    """A one-line description of where two slice texts first part company."""
    left_lines = left.split("\n")
    right_lines = right.split("\n")
    for index in range(max(len(left_lines), len(right_lines))):
        here = left_lines[index] if index < len(left_lines) else "<missing line>"
        there = right_lines[index] if index < len(right_lines) else "<missing line>"
        if here != there:
            return f"line {index + 1}: reference {here!r} != produced {there!r}"
    return "the texts differ after their last line"


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--batch", action="append", default=None,
                        help="batch to compare (repeatable, comma separated; default: all)")
    parser.add_argument("--cs-out", type=Path, default=None,
                        help="compare this output directory instead of running the analyzer")
    parser.add_argument("--golden", type=Path, default=GOLDEN, help="the frozen reference directory")
    args = parser.parse_args(argv[1:])

    check_coverage()
    requested = batches_of(args.batch[0].split(",") if args.batch else list(BATCH_SECTIONS))
    if args.cs_out is None:
        extract_tree()
        run_analyzer()
        produced = CS_OUT
    else:
        produced = args.cs_out

    print(f"reference: {args.golden}")
    print(f"produced:  {produced}")
    print(f"batches:   {', '.join(requested)}")

    missing: list[str] = []
    differing: list[str] = []
    compared = 0
    for batch in requested:
        print(f"-- batch {batch}")
        for name in ("tables.md", "verdict.json"):
            wanted = BATCH_SECTIONS[batch][name]
            reference_slices = load_slices(args.golden, name, reference=True)
            produced_slices = load_slices(produced, name, reference=False)
            for slice_name in wanted:
                compared += 1
                if reference_slices is None:
                    missing.append(f"{name}:{slice_name}: the reference file is missing")
                    print(f"   {name}:{slice_name}: MISSING (reference file absent)")
                    continue
                if produced_slices is None:
                    missing.append(f"{name}:{slice_name}: the produced file is missing")
                    print(f"   {name}:{slice_name}: MISSING (produced file absent)")
                    continue
                reference = reference_slices.get(slice_name)
                actual = produced_slices.get(slice_name)
                if reference is None or actual is None:
                    missing.append(f"{name}:{slice_name}")
                    print(f"   {name}:{slice_name}: MISSING ({'reference' if reference is None else 'produced'} side)")
                    continue
                if reference == actual:
                    print(f"   {name}:{slice_name}: equal ({len(reference.splitlines())} line(s))")
                    continue
                differing.append(f"{name}:{slice_name}")
                print(f"   {name}:{slice_name}: DIFFERS -- {first_difference(reference, actual)}")

    print(f"compared {compared} slice(s): {len(differing)} differ(ent), {len(missing)} missing")
    if missing:
        print(f"rc=2: a slice that should exist does not ({len(missing)} of {compared})")
        return 2
    if differing:
        print(f"rc=1: every slice exists but {len(differing)} differ(s)")
        return 1
    print("rc=0: every slice of this batch is equal")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
