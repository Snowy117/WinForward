#!/usr/bin/env python3
"""Check the synthetic tree's key set against the harness contract, in both directions.

`make_tree.py` fabricates the campaign tree the analysis oracle is frozen on. The tree is only
useful as an oracle input if its **key set** is the harness's own: a fixture that spells a key the
harness never writes, or that omits one it does, would let both implementations agree with each
other while neither agrees with a real campaign. This script is the mechanical guard for that; the
fixture's *values* are out of scope, because every value in the tree is invented.

Two authorities, composed:

* `contract-inventory.json` (`.trellis/tasks/10-07-e2e-harness-refactor/research/`) is the frozen
  pre-migration inventory: every canonical path one real run published, flattened with the shared
  alphabet (`benchmarks/WinForward.E2E/scripts/jsonl_paths.py`);
* `contract-rename.json` is the registered delta E1-E3 landed on top of it -- one row per path, of
  kind `identical`, `renamed` (the new spelling) or `added` (a path the migration introduced). Its
  `new_path` column is therefore the current contract *as a real run publishes it*.

The check is not a plain two-set difference, because the fixture deliberately covers shapes a green
campaign never produces, and those keys exist in the contract without ever being observed in one
run. They are declared in `UNOBSERVED_KEYS` below, each with the `ArmKeys` constant that publishes
it, and they must be *present* in the fixture: a declaration that no longer occurs is itself drift.

Usage:
    python3 check-fixture-drift.py [--tree DIR] [--root DIR]

`--tree` defaults to `/tmp/wf-synth`, the tree the oracle extracts; `--root` defaults to the
repository root three levels above this file. Exit code 0 means both differences are empty.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[3]
SCRIPTS = REPO_ROOT / "benchmarks" / "WinForward.E2E" / "scripts"
RESEARCH = REPO_ROOT / ".trellis" / "tasks" / "10-07-e2e-harness-refactor" / "research"

sys.path.insert(0, str(SCRIPTS))

import jsonl_paths  # noqa: E402  (the shared flattener must be the one the inventory used)

# Paths the fixture publishes that no *green* run observes, with the contract constant that
# declares each. They are shapes the campaign can only produce under a failure or under a plan the
# baseline did not run, so the inventory could not have seen them.
UNOBSERVED_KEYS = {
    "readError": "ArmKeys.Sample.ReadError: a sample whose counters could not be read",
    "message": "ArmKeys.Sample.SamplerError.Message / ArmKeys.Common.ErrorRecord.Message: the failure text",
    "detail": "ArmKeys.Ledger.ErrorRecord.Detail: the innermost cause the ledger's error record names",
}

# Files that are part of the tree but not of the record contract: the orchestrator's environment
# block, the within-run proxy truth the campaign scaffolding writes, and the plan files `run.json`
# points at (an input the campaign ships, not a record the harness publishes). The inventory covers
# `out/*.json`, `out/*.jsonl` and the ledgers, so these are excluded from both sides.
NON_CONTRACT_NAMES = {"environment.json", "proxy-truth.json"}
NON_CONTRACT_PATTERNS = ("plan-*.json",)


def contract_paths(root: Path) -> set[str]:
    """The paths the current contract declares, as a real run publishes them."""
    inventory = json.loads((root / "contract-inventory.json").read_text(encoding="utf-8"))
    rename = json.loads((root / "contract-rename.json").read_text(encoding="utf-8"))
    declared = {row["new_path"] or row["old_path"] for row in rename}
    known = set(inventory["paths"])
    unknown = {row["old_path"] for row in rename if row["old_path"] and row["old_path"] not in known}
    if unknown:
        raise SystemExit(
            "the rename table names baseline paths the inventory does not have: "
            + ", ".join(sorted(unknown))
        )

    return declared


def fixture_paths(tree: Path) -> set[str]:
    """Every canonical path the fixture publishes, in the contract's own alphabet."""
    files = [
        candidate
        for candidate in sorted(tree.rglob("*"))
        if candidate.is_file()
        and candidate.suffix in (".json", ".jsonl")
        and candidate.name not in NON_CONTRACT_NAMES
        and not any(candidate.match(pattern) for pattern in NON_CONTRACT_PATTERNS)
    ]
    if not files:
        raise SystemExit(f"{tree}: no .json/.jsonl records below the tree; point --tree at the raw directory")

    observations: dict[str, list] = {}
    for file in files:
        if file.suffix == ".jsonl":
            for record in jsonl_paths.read_jsonl(file):
                jsonl_paths.merge(observations, record)
        else:
            jsonl_paths.merge(observations, jsonl_paths.read_document(file))

    return set(observations)


def main(argv: list[str]) -> int:
    jsonl_paths.require_python3()
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--tree", type=Path, default=Path("/tmp/wf-synth"),
                        help="the generated tree (its parent holds the ledgers)")
    parser.add_argument("--root", type=Path, default=RESEARCH, help="directory holding the two contract tables")
    args = parser.parse_args(argv[1:])

    contract = contract_paths(args.root)
    observed_fixture = fixture_paths(args.tree)
    declared = set(UNOBSERVED_KEYS) | contract

    missing = sorted(contract - observed_fixture)
    extra = sorted(observed_fixture - declared)
    unexercised = sorted(set(UNOBSERVED_KEYS) - observed_fixture)

    print(f"contract: {len(contract)} path(s) from {args.root.name}")
    print(f"fixture:  {len(observed_fixture)} path(s) under {args.tree}")
    print(f"declared unobserved shapes: {len(UNOBSERVED_KEYS)}")
    for path, why in sorted(UNOBSERVED_KEYS.items()):
        print(f"    {path}  ({why})")

    if not missing and not extra and not unexercised:
        print("fixture drift: none (both directions empty)")
        return 0

    for label, paths in (("declared but not published by the fixture", missing),
                         ("published by the fixture but not declared", extra),
                         ("declared as an unobserved shape but absent from the fixture", unexercised)):
        if paths:
            print(f"{label}: {len(paths)}", file=sys.stderr)
            for path in paths:
                print(f"    {path}", file=sys.stderr)

    return 1


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
