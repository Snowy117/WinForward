#!/usr/bin/env python3
"""Inventory the record contract, and derive the rename table the migration is judged against.

Two subcommands, one alphabet: both import ``jsonl_paths``, the flattener the comparison script
uses, so "what a key is" is answered in exactly one place (design-decisions D7 item 4 / D14.6).

``inventory`` reads one run's artifacts (``out/*.jsonl``, ``out/run.json`` and ``ledger.jsonl``;
``target.out`` is text and never enters the key set) and writes every canonical path per file plus
the union to ``contract-inventory.json``.

``rename`` reads the frozen baseline's artifacts, applies the convergence rules below, and writes the
full ``{kind, old_path, new_path, reason}`` table -- ``identical`` rows included, because the table
is the complete statement of what the contract's paths are, not just of what moved. It also checks
the fresh run against that table: a path the fresh run publishes and the table does not declare is an
error (the contract grew without being registered), while a declared path the fresh run did not
publish is reported as unobserved, which is what a conditional key looks like in a green run.

Usage:
    python3 contract-inventory.py inventory --run DIR --out FILE
    python3 contract-inventory.py rename --baseline DIR --run DIR --out-json FILE --out-md FILE
"""

from __future__ import annotations

import argparse
import json
import sys
from collections import Counter
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import jsonl_paths  # noqa: E402  (the shared flattener must be importable next to this script)

GROUP_RECORDS = "records"
GROUP_LEDGER = "ledger"

# The four families of design-decisions D5 / design.md section 2.2.1. Only the last segment of a path
# is rewritten: the grouping prefixes (`tcp.` / `udp.` / `classes.*` / BASE's `latency` and `loss`
# phases) are structure, not spelling, and a dot inside a key is part of that key.
#
# A segment that is already dotted keeps its prefix and only its leaf is respelled (`tcp.sentOk` ->
# `tcp.sent`); a segment with no dot carries its grouping itself and is respelled whole (`udpSent` ->
# `udp.sent`, `udpLossRate` -> `udp.lossRate`). An unrelated name that merely ends in one of these
# words -- `clientSendLossRate`, `strictLossRate` -- is a different statistic and is left alone.
SEGMENT_RENAMES = {
    "udpSent": "udp.sent",
    "udpLossRate": "udp.lossRate",
    "udpArrived": "udp.arrived",
    "udpForeignConnection": "udp.foreignConnection",
}
DOTTED_LEAF_RENAMES = {
    "sentOk": "sent",
}

# Paths the frozen baseline cannot contain because they arrived after it: a green run writes no
# error record, run.json's planSource was added with the plan handling (D14.23, D14.12), the
# three sentOutOfRangeSequences paths are D7's send-side half of outOfRangeSequences (E3-b1/D19.3 A),
# the four truncatedFrames paths are the two servers' truncation counters (E3-c/D19.3 B/C), and the
# five acceptErrors/udpReceivers paths are E3-d's listener accounting (E3-d/D19.3 A). The ledger's
# error record declares no path of its own here: `detail` is the spelling its family publishes, and
# the client's arm files already publish that path.
# An addition carries no batch: only a rename has an old spelling that has to disappear, and this
# tool emits `batch` for the renamed rows alone, so the comparison licenses an addition by its kind.
ADDITIONS = {
    "planSource": "run.json top-level key: 'builtin' when no plan file was given (D14.23)",
    "error": "error record field: the exception family name (D14.12)",
    "detail": "error record field: the innermost exception type name (D14.12)",
    "metrics/sentOutOfRangeSequences": "loss metrics: offered slots the tracker refused to send as outside its bounded sequence space (D7)",
    "metrics/loss/sentOutOfRangeSequences": "the control's loss phase publishes the same loss record one level down (D7)",
    "metrics/classes/udp/sentOutOfRangeSequences": "mix UDP class: the same counter, folded from the per-desktop trackers (D7)",
    "truncatedFrames": "tcpSummary and dnsSummary roots: connections the peer's close cut off inside a frame, and DNS stream messages a peer stopped writing mid-message -- two mechanisms under one spelling (D19.3 C)",
    "tcp/truncatedFrames": "the tcp truncation counter one level down, under targetSummary/tcp (D19.3 B)",
    "dns/truncatedFrames": "the first DNS listener's truncation counter one level down, under targetSummary/dns (D19.3 B)",
    "dnsAlt/truncatedFrames": "the second DNS listener's truncation counter one level down, under targetSummary/dnsAlt; present only when the target was started with a second DNS port (D19.3 B)",
    "acceptErrors": "tcpSummary and dnsSummary roots: accepts a listener's accept loop refused and then retried -- the same mechanism on two listeners (E3-d)",
    "tcp/acceptErrors": "the tcp listener's refused accepts one level down, under targetSummary/tcp (E3-d)",
    "dns/acceptErrors": "the first DNS listener's refused accepts one level down, under targetSummary/dns (E3-d)",
    "dnsAlt/acceptErrors": "the second DNS listener's refused accepts one level down, under targetSummary/dnsAlt; present only when the target was started with a second DNS port (E3-d)",
    "udp/udpReceivers": "the UDP echo listener's receive loops that actually started, under targetSummary/udp (E3-d)",
}

FAMILY_OF = {
    "sentOk": "sentOk / sent / udpSent -> sent",
    "udpSent": "sentOk / sent / udpSent -> sent",
    "udpLossRate": "lossRate / udpLossRate -> lossRate",
    "udpArrived": "arrived / udpArrived -> arrived",
    "udpForeignConnection": "foreignConnection / udpForeignConnection -> foreignConnection",
}

# The batch that executes the four convergence families. A rename row carries it so the comparison
# can require the rows of the batch under test to have landed exactly (compare-records.py --batch).
CONVERGENCE_BATCH = "B2"


def groups_of(run: Path) -> dict[str, list[str]]:
    """``{"<group>/<file>": [canonical path, ...]}`` for one run directory.

    The labels match ``compare-records.py``: ``records/<file>`` for the per-arm files and
    ``records/run.json``, ``ledger/ledger.jsonl`` for the ledger.
    """
    records_dir = run / "out" if (run / "out").is_dir() else run
    groups: dict[str, list[str]] = {}
    for file in sorted(records_dir.iterdir()):
        if file.suffix not in (".json", ".jsonl") or not file.is_file():
            continue
        groups[f"{GROUP_RECORDS}/{file.name}"] = sorted(jsonl_paths.flatten_file(file))

    ledger = run / "ledger.jsonl"
    if ledger.is_file():
        groups[f"{GROUP_LEDGER}/{ledger.name}"] = sorted(jsonl_paths.flatten_file(ledger))

    if not groups:
        raise SystemExit(f"{run}: no .json/.jsonl records and no ledger.jsonl; point --run at a run directory")

    return groups


def union(groups: dict[str, list[str]]) -> list[str]:
    paths: set[str] = set()
    for found in groups.values():
        paths.update(found)
    return sorted(paths)


def renamed_path(path: str) -> tuple[str, str] | None:
    """The path this one becomes and the family that moves it, or None when no rule does."""
    head, _, segment = path.rpartition("/")
    moved = renamed_segment(segment)
    if moved is None:
        return None
    replacement, family = moved
    return (f"{head}/{replacement}" if head else replacement), family


def renamed_segment(segment: str) -> tuple[str, str] | None:
    """The respelled segment and its family, or None when the segment is not in a family."""
    if segment in SEGMENT_RENAMES:
        return SEGMENT_RENAMES[segment], FAMILY_OF[segment]
    prefix, dot, leaf = segment.rpartition(".")
    if dot and leaf in DOTTED_LEAF_RENAMES:
        return f"{prefix}.{DOTTED_LEAF_RENAMES[leaf]}", FAMILY_OF[leaf]
    return None


def where(groups: dict[str, list[str]], path: str) -> str:
    """The groups a path occurs in, for the table's reason column."""
    return ", ".join(name for name, found in groups.items() if path in found)


def reason_for(path: str, family: str, groups: dict[str, list[str]]) -> str:
    return f"{family}; seen in {where(groups, path)}"


def run_inventory(args: argparse.Namespace) -> int:
    groups = groups_of(args.run)
    paths = union(groups)
    document = {
        "generatedBy": "benchmarks/WinForward.E2E/scripts/contract-inventory.py inventory",
        "alphabet": "jsonl_paths.canonical_path: '/'.join(member names); a dot inside a key never splits",
        "run": str(args.run),
        "groups": {name: {"count": len(found), "paths": found} for name, found in groups.items()},
        "paths": paths,
        "counts": {"groups": len(groups), "paths": len(paths)},
    }
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(document, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"{args.out}: {len(groups)} group(s), {len(paths)} distinct path(s)")
    for name, found in groups.items():
        print(f"  {len(found):5d}  {name}")
    return 0


def run_rename(args: argparse.Namespace) -> int:
    baseline = groups_of(args.baseline)
    fresh = groups_of(args.run)
    old_paths = union(baseline)
    fresh_paths = set(union(fresh))

    rows: list[dict[str, str | None]] = []
    new_paths: set[str] = set()
    pending: dict[str, str] = {}
    families: dict[str, list[dict[str, str | None]]] = {}
    for path in old_paths:
        moved = renamed_path(path)
        if moved is None:
            rows.append({"kind": "identical", "old_path": path, "new_path": path, "reason": "unchanged; " + reason_for(path, "structure kept", baseline)})
            new_paths.add(path)
            continue

        replacement, family = moved
        # A rename is registered here and executed by the batch that migrates its kind: the fresh run
        # legitimately still publishes the old spelling for every kind that has not migrated yet, so
        # an old path under a registered rename is pending, not unexpected.
        row = {
            "kind": "renamed",
            "old_path": path,
            "new_path": replacement,
            "batch": CONVERGENCE_BATCH,
            "reason": reason_for(path, family, baseline),
        }
        rows.append(row)
        families.setdefault(family, []).append(row)
        new_paths.add(replacement)
        pending[path] = replacement

    for path, reason in ADDITIONS.items():
        if path in new_paths:
            raise SystemExit(f"{path}: registered as an addition but the baseline already publishes it")
        rows.append({"kind": "added", "old_path": None, "new_path": path, "reason": reason})
        new_paths.add(path)

    rows.sort(key=lambda row: (row["kind"], row["old_path"] or row["new_path"] or ""))

    # Three states for a registered rename, read off the fresh run: still old (pending), already new
    # (landed), or neither spelling observed because the keys are conditional.
    published_old = sorted(path for path in pending if path in fresh_paths)
    landed = sorted(path for path in pending if path not in fresh_paths and pending[path] in fresh_paths)
    absent = sorted(path for path in pending if path not in fresh_paths and pending[path] not in fresh_paths)
    pending_new = {pending[path] for path in published_old}
    unexpected = sorted(fresh_paths - new_paths - set(published_old))
    unobserved = sorted(new_paths - fresh_paths - pending_new)
    if unexpected:
        print("the fresh run publishes paths the table does not declare:", file=sys.stderr)
        for path in unexpected:
            print(f"  {where(fresh, path)}: {path}", file=sys.stderr)
        return 1

    print(f"renames landed in this run: {len(landed)}; still published under the old spelling: {len(published_old)}; not observed at all: {len(absent)}")

    args.out_json.parent.mkdir(parents=True, exist_ok=True)
    args.out_json.write_text(json.dumps(rows, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    counts = Counter(row["kind"] for row in rows)

    lines = [
        "# Contract rename table (E1-B1)",
        "",
        "Generated by `benchmarks/WinForward.E2E/scripts/contract-inventory.py rename`; the machine-readable",
        "form is [`contract-rename.json`](./contract-rename.json). Paths are canonical paths",
        "(`jsonl_paths.py`): member names joined with `/`, a dot inside a member name kept whole.",
        "",
        f"- baseline: `{args.baseline}`",
        f"- fresh run: `{args.run}`",
        f"- rows: **{len(rows)}** = identical {counts.get('identical', 0)} + renamed {counts.get('renamed', 0)}"
        f" + added {counts.get('added', 0)} + removed {counts.get('removed', 0)}",
        f"- batch that executes the renames: `{CONVERGENCE_BATCH}`",
        f"- renames landed in the fresh run: **{len(landed)}**; registered but still published under the old"
        f" spelling: **{len(published_old)}** (listed below); not observed at all: **{len(absent)}**",
        f"- declared paths not observed in the fresh run: **{len(unobserved)}** (conditional keys; listed below)",
        "",
        "## Renamed",
        "",
    ]
    if families:
        for family, found in sorted(families.items()):
            lines += [f"### {family}", "", "| old path | new path | batch | reason |", "|---|---|---|---|"]
            lines += [
                f"| `{row['old_path']}` | `{row['new_path']}` | {row['batch']} | {row['reason']} |" for row in found
            ]
            lines.append("")
    else:
        lines += ["(none)", ""]

    lines += ["## Added", "", "| new path | reason |", "|---|---|"]
    added = [row for row in rows if row["kind"] == "added"]
    lines += [f"| `{row['new_path']}` | {row['reason']} |" for row in added] or ["| — | (none) |"]
    lines += ["", "## Removed", ""]
    removed = [row for row in rows if row["kind"] == "removed"]
    lines += [f"| `{row['old_path']}` | {row['reason']} |" for row in removed] or ["(none)"]
    lines += ["", "## Registered but still published under the old spelling", ""]
    lines += [f"- `{path}` (becomes `{pending[path]}`)" for path in published_old] or ["(none)"]
    lines += ["", "## Registered renames neither spelling of which the fresh run observed", ""]
    lines += [f"- `{path}` (becomes `{pending[path]}`)" for path in absent] or ["(none)"]
    lines += ["", "## Declared but not observed in the fresh run", ""]
    lines += [f"- `{path}`" for path in unobserved] or ["(none)"]
    lines += [
        "",
        "## Notes",
        "",
        "- Only the kinds migrated in the batch that owns a rename execute it; the others keep publishing the",
        "  old spelling until their batch lands, which is what the section above records. The batch column is",
        "  what `compare-records.py --rename-table ... --batch " + CONVERGENCE_BATCH + "` requires to have landed.",
        "- Four families are converged: `sentOk`/`sent`/`udpSent`, `lossRate`/`udpLossRate`,",
        "  `arrived`/`udpArrived`, `foreignConnection`/`udpForeignConnection`. Everything else is `identical`.",
        "- Names that merely resemble a family member are deliberately left alone: `clientSendLossRate` and",
        "  `strictLossRate` are different statistics, and `metrics/desktops/udpNever` has no second spelling",
        "  declared, so it stays as it is. A fifth family would have to be declared first.",
        "",
    ]
    args.out_md.write_text("\n".join(lines), encoding="utf-8")
    print(f"{args.out_json}: {len(rows)} row(s) = " + ", ".join(f"{kind} {count}" for kind, count in sorted(counts.items())))
    print(f"{args.out_md}: renamed {counts.get('renamed', 0)}, added {counts.get('added', 0)}, removed {counts.get('removed', 0)}")
    print(f"declared but not observed in the fresh run: {len(unobserved)}")
    return 0


def main(argv: list[str]) -> int:
    jsonl_paths.require_python3()
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)

    inventory = commands.add_parser("inventory", help="write every canonical path of one run")
    inventory.add_argument("--run", required=True, type=Path, help="run directory (out/, ledger.jsonl)")
    inventory.add_argument("--out", required=True, type=Path)
    inventory.set_defaults(handler=run_inventory)

    rename = commands.add_parser("rename", help="write the full rename table and its report")
    rename.add_argument("--baseline", required=True, type=Path, help="frozen baseline run directory")
    rename.add_argument("--run", required=True, type=Path, help="fresh run directory, checked against the table")
    rename.add_argument("--out-json", required=True, type=Path)
    rename.add_argument("--out-md", required=True, type=Path)
    rename.set_defaults(handler=run_rename)

    args = parser.parse_args(argv[1:])
    return int(args.handler(args))


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
