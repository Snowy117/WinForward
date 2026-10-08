#!/usr/bin/env python3
"""Assert the boundary trees the oracle cannot cover, on the C# analysis alone.

The two-implementation oracle is defined on the frozen **clean** tree. Three counter flags move
values without moving the key set, so a tree built with one of them is a *boundary tree*: the Python
reference either has no answer there or would crash on it, which is why D20.1 sends those shapes to
pointed assertions against the produced `tables.md` / `verdict.json` **alone** — every guard here
reads the C# output and nothing else. The recipes are the ones `FROZEN.md` §4 freezes.

    --truncated-tcp 3   §14.7 discloses the TCP echo listener's 3 cut-off frames, per target and per
    --truncated-dns 2   pass, and the DNS listeners' 2 short reads in a table of their own; every
                        other section is byte-identical to the clean run's, and §14 differs from it
                        only by the appended subsection, which is what "no arm-level cell consumes
                        the counter" means.
    --zero-denominator  the shapes a zero denominator produces (D21.2 #2): a rate the harness wrote
                        as JSON null for every pass (an **empty** cell, never a zero), the null-pass
                        count and its reason, §14.2's datagram count of exactly zero (no band -> n/a),
                        and the MIX UDP-class fallback in both directions -- a null class field must
                        **not** fall back to the arm-level field, a missing one must.

Every guard is paired with a **negative control**: one edit on a copy of the produced document that
the guard must reject. A guard that stays green under its own control is not a guard.

The Python reference is deliberately not invoked (it is retired). On `--zero-denominator` it raised
`TypeError: '<=' not supported between instances of 'float' and 'NoneType'` at `analyze.py:5037`
(§7.3) and writes neither file, so it cannot judge these trees; running it here would also make this
checker depend on the file E4-c retires.

Usage:
    python3 check-boundary-trees.py [--tree-dir /tmp/wf-synth] [--workdir DIR] [--analyzer PATH]

Exit codes: ``0`` every guard held and every control failed it; ``1`` at least one guard or control
did not behave; ``2`` the input could not be produced or read.
"""

from __future__ import annotations

import argparse
import json
import re
import shutil
import subprocess
import sys
import tempfile
from collections import Counter
from pathlib import Path

SECTION_MARK = re.compile(r"^## (\d+)\. ", re.MULTILINE)
TABLE_SEPARATOR = re.compile(r"^\|[\s:|-]+\|$")

# The hardcoded path both implementations are called with, and the one the recipes in FROZEN.md are
# bound to: every `run.json` embeds its own `outDirectory`, so a tree generated anywhere else reads
# (and hashes) differently.
DEFAULT_TREE = Path("/tmp/wf-synth")

TRUNCATION_HEADING = "### 14.7 Truncated frames (target side, unattributable)"
NULL_RATE_REASON = "null rate: the harness wrote null because the denominator was zero"

# The truncation each recipe asks for, and the ledger that carries it.
TRUNCATED_TCP = 3
TRUNCATED_DNS = 2
MAIN_LEDGER = "ledger-main.jsonl"
PASSES = ["pass1", "pass2", "pass3"]

# The readings the zero-denominator guards name.
ZERO_PERSIST_ROW = "wf-fdd-opt"
ZERO_PERSIST_COLUMN = "PERSIST responseRate (pp)"
ZERO_MIX_ROW = "wf-aot-opt"
ZERO_LAT_ROW = "wf-aot-opt"
ZERO_LAT_PASS = "pass1"


def unusable(message):
    """Exit 2: the input could not be produced or read, so no guard was judged."""
    print(message, file=sys.stderr)
    raise SystemExit(2)


class Run:
    """One produced analysis: the two documents, parsed, plus the document's own sections."""

    def __init__(self, tables, verdict):
        self.tables = tables
        self.verdict = verdict
        self.sections = sections_of(tables)

    def copy(self):
        return Run(self.tables, json.loads(json.dumps(self.verdict)))

    def metric(self, key, row_id, where):
        entry = self.verdict.get("metrics", {}).get(key, {}).get("rows", {}).get(row_id)
        if entry is None:
            unusable("check-boundary-trees.py: %s: verdict.json holds no metrics.%s.rows.%s" % (where, key, row_id))
        return entry


class Report:
    def __init__(self, source):
        self.source = source
        self.failures = []
        self.checks = 0

    def guard(self, name, outcome):
        """Record one `(ok, detail)` outcome; a guard that did not hold is a failure."""
        ok, detail = outcome
        self.checks += 1
        print("  %s  %s" % ("PASS" if ok else "FAIL", name))
        if not ok:
            self.failures.append(name)
            if detail:
                for line in str(detail).splitlines():
                    print("        %s" % line)
        return ok

    def summary(self):
        if self.failures:
            print(
                "check-boundary-trees.py: %s: %d of %d guard(s) failed: %s"
                % (self.source, len(self.failures), self.checks, "; ".join(self.failures))
            )
            return 1
        print("check-boundary-trees.py: %s: all %d guard(s) held" % (self.source, self.checks))
        return 0


def sections_of(text):
    """The document's slices, keyed by section number, plus `preamble`."""
    out = {}
    marks = list(SECTION_MARK.finditer(text))
    if not marks:
        return out
    out["preamble"] = text[: marks[0].start()]
    for index, mark in enumerate(marks):
        end = marks[index + 1].start() if index + 1 < len(marks) else len(text)
        out[mark.group(1)] = text[mark.start() : end]
    return out


def split_row(line):
    return [cell.strip() for cell in line.strip().strip("|").split("|")]


def tables_of(section):
    """Every table in a section as ``(heading, header, rows)``, the heading being the nearest above."""
    lines = section.splitlines()
    out = []
    heading = ""
    index = 0
    while index < len(lines):
        if lines[index].startswith("### "):
            heading = lines[index]
        if (
            lines[index].startswith("|")
            and index + 1 < len(lines)
            and TABLE_SEPARATOR.match(lines[index + 1].strip())
        ):
            header = split_row(lines[index])
            rows = []
            index += 2
            while index < len(lines) and lines[index].startswith("|"):
                rows.append(split_row(lines[index]))
                index += 1
            out.append((heading, header, rows))
            continue
        index += 1
    return out


def cell_row(run, first_header, column, row_id):
    """One row of the table whose first column is `first_header` and which holds `column`."""
    lines = run.tables.splitlines()
    for index, line in enumerate(lines):
        if not line.startswith("|"):
            continue
        header = split_row(line)
        if header[0] != first_header or column not in header:
            continue
        position = header.index(column)
        for candidate in range(index + 2, len(lines)):
            if not lines[candidate].startswith("|"):
                break
            row = split_row(lines[candidate])
            if row[0] == row_id:
                return dict(zip(header, row))
    unusable("check-boundary-trees.py: no %s row in the table holding %r" % (row_id, column))


def with_cell(run, first_header, column, row_id, value):
    """The same document with one table cell replaced, which is what a control edits."""
    lines = run.tables.splitlines()
    for index, line in enumerate(lines):
        if not line.startswith("|"):
            continue
        header = split_row(line)
        if header[0] != first_header or column not in header:
            continue
        position = header.index(column)
        for candidate in range(index + 2, len(lines)):
            if not lines[candidate].startswith("|"):
                break
            row = split_row(lines[candidate])
            if row[0] == row_id:
                row[position] = value
                lines[candidate] = "| " + " | ".join(row) + " |"
                run.tables = "\n".join(lines)
                run.sections = sections_of(run.tables)
                return
    unusable("check-boundary-trees.py: no %s row in the table holding %r" % (row_id, column))


def seven_tables(run):
    """The tables §14.7 prints, or an empty list when the subsection is absent."""
    return [table for table in tables_of(run.sections.get("14", "")) if table[0] == TRUNCATION_HEADING]


def run_analysis(analyzer, tree_dir, out_dir):
    out_dir.mkdir(parents=True, exist_ok=True)
    result = subprocess.run(
        [str(analyzer), "--raw", str(tree_dir / "raw"), "--out", str(out_dir)],
        capture_output=True,
        text=True,
        check=False,
    )
    if result.returncode != 0:
        unusable(
            "check-boundary-trees.py: the analysis failed on %s (rc=%d)\n%s\n%s"
            % (tree_dir, result.returncode, result.stdout[-2000:], result.stderr[-2000:])
        )
    return Run(
        (out_dir / "tables.md").read_text(encoding="utf-8"),
        json.loads((out_dir / "verdict.json").read_text(encoding="utf-8")),
    )


def generate_tree(tree_dir, make_tree, flags):
    if tree_dir.exists():
        shutil.rmtree(tree_dir)
    tree_dir.mkdir(parents=True)
    result = subprocess.run(
        [sys.executable, str(make_tree), *flags, str(tree_dir / "raw")],
        capture_output=True,
        text=True,
        check=False,
    )
    if result.returncode != 0:
        unusable(
            "check-boundary-trees.py: make_tree.py %s failed (rc=%d)\n%s\n%s"
            % (" ".join(flags), result.returncode, result.stdout[-2000:], result.stderr[-2000:])
        )


def arm_metric(raw, pass_id, row_id, arm, path):
    """One number out of the tree's own arm file, so a guard can name the fallback's source."""
    arm_file = raw / pass_id / row_id / ("%s.jsonl" % arm)
    if not arm_file.is_file():
        return None
    for line in arm_file.read_text(encoding="utf-8-sig").splitlines():
        if not line.strip():
            continue
        record = json.loads(line)
        if record.get("type") != "result":
            continue
        value = record
        for step in path.split("/"):
            value = value.get(step) if isinstance(value, dict) else None
        return float(value) if isinstance(value, (int, float)) else None
    return None


def guard_clean_has_no_caveat(clean):
    """The clean tree is what makes the §14.7 condition observable at all."""
    offenders = [number for number, body in clean.sections.items() if "truncated frame" in body.lower()]
    return not offenders, "a truncation caveat appears in section(s) %s" % ", ".join(offenders)


def guard_other_sections_unmoved(clean, boundary):
    """No arm-level cell consumes the counter: only §14 may move, and only by addition."""
    moved = sorted(
        number
        for number, body in clean.sections.items()
        if number != "14" and boundary.sections.get(number) != body
    )
    appended = boundary.sections.get("14", "").startswith(clean.sections.get("14", ""))
    return (not moved and appended, "section(s) %s moved; §14 is a prefix: %s" % (", ".join(moved) or "none", appended))


def guard_truncation_table(boundary, mechanism, frames):
    """The disclosed numbers: one mechanism's own table, one row per pass of the target that cut."""
    tables = seven_tables(boundary)
    if len(tables) != 1:
        return False, "%d table(s) under the §14.7 heading" % len(tables)
    _, header, rows = tables[0]
    tcp = mechanism == "TCP"
    expected_header = ["pass", "ledger", "truncated frames"] if tcp else ["pass", "ledger", "dns port", "truncated frames"]
    if header != expected_header:
        return False, "header %s, wanted %s" % (header, expected_header)

    carrying = [row for row in rows if row[1].endswith(MAIN_LEDGER)]
    others = [row[1] for row in rows if not row[1].endswith(MAIN_LEDGER)]
    # The echo listener keeps one count, so it is one row per pass; each DNS listener keeps its own
    # count, so a pass has one row per port.
    passes = [row[0] for row in carrying]
    ports = Counter(row[2] for row in carrying)
    ok = (
        passes == (PASSES if tcp else [pass_id for pass_id in PASSES for _ in range(2)])
        and (tcp or ports == {"53": len(PASSES), "40053": len(PASSES)})
        and all(row[-1] == str(frames) for row in carrying)
        and not others
        and all(len(row) == len(header) for row in rows)
    )
    return ok, "passes %s, ports %s, cells %s, other ledgers %s" % (
        passes,
        dict(ports) if not tcp else "n/a",
        [row[-1] for row in carrying],
        others or "none",
    )


def guard_empty_cell(run):
    """A rate whose denominator was zero is printed as an empty cell, never as a zero."""
    cell = cell_row(run, "row", ZERO_PERSIST_COLUMN, ZERO_PERSIST_ROW)[ZERO_PERSIST_COLUMN]
    return cell == "", "%s's %s cell reads %r" % (ZERO_PERSIST_ROW, ZERO_PERSIST_COLUMN, cell)


def guard_null_passes(run):
    """Every pass is counted as a null pass, with the reason the empty cell rests on."""
    entry = run.metric("persist.responseRate", ZERO_PERSIST_ROW, "empty cell")
    reasons = entry["unavailable_passes"]
    ok = (
        entry["passes"] == 0
        and entry["per_pass"] == {}
        and entry["null_passes"] == 3
        and entry["median"] is None
        and sorted(reasons) == PASSES
        and all(reason == NULL_RATE_REASON for reason in reasons.values())
    )
    return ok, json.dumps(
        {key: entry[key] for key in ("passes", "per_pass", "null_passes", "median", "unavailable_passes")}
    )


def guard_mix_gate(run, raw):
    """A null class rate makes no claim on the arm-level field; a missing one does."""
    entry = run.metric("mix.udp.lossRate", ZERO_MIX_ROW, "MIX fallback")
    fallback = arm_metric(raw, "pass2", ZERO_MIX_ROW, "MIX", "metrics/udp.lossRate")
    ok = (
        entry["null_passes"] == 1
        and entry["unavailable_passes"].get("pass1") == NULL_RATE_REASON
        and sorted(entry["per_pass"]) == ["pass2", "pass3"]
        and fallback is not None
        and abs(entry["per_pass"]["pass2"] - (fallback * 100.0)) < 1e-9
    )
    return ok, "null_passes %s, per_pass %s, reasons %s, arm-level %s" % (
        entry["null_passes"],
        entry["per_pass"],
        entry["unavailable_passes"],
        fallback,
    )


def guard_datagram_band(run):
    """A client datagram count of exactly zero has no band, so the check is stated, not faked."""
    row = None
    for _, header, rows in tables_of(run.sections.get("14", "")):
        if header[:3] != ["pass", "run", "arm"]:
            continue
        for cells in rows:
            if len(cells) == len(header) and cells[:3] == [ZERO_LAT_PASS, ZERO_LAT_ROW, "LAT"]:
                row = dict(zip(header, cells))
    if row is None:
        return False, "§14.2 holds no %s / %s / LAT row" % (ZERO_LAT_PASS, ZERO_LAT_ROW)
    ok = row["client datagrams"] == "0" and row["datagram check"] == "n/a"
    return ok, "client datagrams %r, ledger datagrams %r, check %r" % (
        row["client datagrams"],
        row["ledger datagrams"],
        row["datagram check"],
    )


def control(report, name, run, mutate, guard):
    """One negative control: the mutation must make the guard fail."""
    broken = run.copy()
    mutate(broken)
    outcome = guard(broken)
    return report.guard(
        "control/%s: the guard rejects the mutation" % name,
        (not outcome[0], outcome[1]),
    )


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("Usage:", 1)[0])
    parser.add_argument("--tree-dir", type=Path, default=DEFAULT_TREE, help="where each tree is generated (the recipes' own path)")
    parser.add_argument("--workdir", type=Path, default=None, help="where the produced documents are written")
    parser.add_argument("--analyzer", type=Path, default=None, help="the built WinForward.E2E.Analysis binary")
    args = parser.parse_args(argv)

    root = Path(__file__).resolve().parents[3]
    make_tree = root / "benchmarks" / "WinForward.E2E.Analysis" / "verification" / "synthetic" / "make_tree.py"
    analyzer = args.analyzer or (
        root / "benchmarks" / "WinForward.E2E.Analysis" / "bin" / "Release" / "net10.0" / "WinForward.E2E.Analysis"
    )
    if not make_tree.is_file():
        unusable("check-boundary-trees.py: no make_tree.py at %s" % make_tree)
    if not analyzer.is_file():
        unusable("check-boundary-trees.py: no analysis at %s; build it first" % analyzer)

    temporary = None
    if args.workdir:
        workdir = args.workdir
        workdir.mkdir(parents=True, exist_ok=True)
    else:
        temporary = tempfile.TemporaryDirectory(prefix="wf-boundary-")
        workdir = Path(temporary.name)

    try:
        print("analyzer:  %s" % analyzer)
        print("tree:      %s" % args.tree_dir)
        generate_tree(args.tree_dir, make_tree, [])
        clean = run_analysis(analyzer, args.tree_dir, workdir / "clean")
        print("clean:     %d byte(s) of tables.md" % len(clean.tables))

        exit_code = 0
        for label, mechanism, frames in (("tcp", "TCP", TRUNCATED_TCP), ("dns", "DNS", TRUNCATED_DNS)):
            flags = ["--truncated-%s" % label, str(frames)]
            generate_tree(args.tree_dir, make_tree, flags)
            boundary = run_analysis(analyzer, args.tree_dir, workdir / label)
            report = Report("--truncated-%s %d" % (label, frames))
            report.guard("#14.7/present: the caveat is printed inside §14", (TRUNCATION_HEADING in boundary.sections.get("14", ""), "no §14.7 heading"))
            report.guard("#14.7/%s: the %s table carries the target's own count" % (label, mechanism), guard_truncation_table(boundary, mechanism, frames))
            report.guard("#14.7/%s: no arm-level cell consumes the counter" % label, guard_other_sections_unmoved(clean, boundary))
            report.guard("#14.7/clean: the clean tree prints no caveat", guard_clean_has_no_caveat(clean))
            control(
                report,
                "truncated-%s-arm-cell" % label,
                boundary,
                lambda run: with_cell(run, "row", "LAT tcp-rtt p50 (us)", "wf-aot-opt", str(frames)),
                lambda run: guard_other_sections_unmoved(clean, run),
            )
            exit_code |= report.summary()

        generate_tree(args.tree_dir, make_tree, ["--zero-denominator"])
        zero = run_analysis(analyzer, args.tree_dir, workdir / "zero")
        raw = args.tree_dir / "raw"
        report = Report("--zero-denominator")
        report.guard("#zero-denominator/empty-cell: a zero-denominator rate is an empty cell", guard_empty_cell(zero))
        report.guard("#zero-denominator/null-passes: the reason is the harness's own null", guard_null_passes(zero))
        report.guard("#zero-denominator/mix-gate: a null class rate does not fall back", guard_mix_gate(zero, raw))
        report.guard("#zero-denominator/datagram-band: a zero client count has no band", guard_datagram_band(zero))
        control(
            report,
            "empty-cell-to-zero",
            zero,
            lambda run: with_cell(run, "row", ZERO_PERSIST_COLUMN, ZERO_PERSIST_ROW, "0"),
            guard_empty_cell,
        )
        control(
            report,
            "null-pass-to-number",
            zero,
            lambda run: run.verdict["metrics"]["persist.responseRate"]["rows"][ZERO_PERSIST_ROW].update({"null_passes": 2}),
            guard_null_passes,
        )
        control(
            report,
            "fallback-through-a-null",
            zero,
            lambda run: run.verdict["metrics"]["mix.udp.lossRate"]["rows"][ZERO_MIX_ROW]["unavailable_passes"].update(
                {"pass1": "MIX metrics.classes.udp.lossRate missing"}
            ),
            lambda run: guard_mix_gate(run, raw),
        )
        exit_code |= report.summary()
        return exit_code
    finally:
        if temporary is not None:
            temporary.cleanup()


if __name__ == "__main__":
    sys.exit(main())
