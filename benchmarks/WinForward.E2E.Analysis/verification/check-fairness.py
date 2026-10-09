#!/usr/bin/env python3
"""Guard the campaign report's fairness disclosures with assertions on the analysis's own output.

Four guards, each judged against the **C# analysis's** `tables.md` and against the rules in
`benchmarks/WinForward.E2E.Analysis/verification/row-profiles.json` -- the machine-readable row table
that says, per row, what its plan runs and what it does with port-53 and general UDP. The rules used to
be imported from the Python analyzer; they are external now, so this checker outlives it (E4-c).

* ``#17`` every row the rules declare incapable of carrying UDP prints ``not carried (UDP bypassed)``
  in every UDP-bearing cell of the row profile, headline, latency, UDP-accuracy and DNS tables --
  never a number -- while a row that does carry UDP still prints numbers, so a renderer that marks
  everything fails too.
* ``#18`` every row that measured the port-53 arm prints the rules' own carriage label for its
  UDP/53 path, and a row whose path is not the relayed one is marked not cross-product comparable.
* ``#19`` the CPU table discloses the scope of its numbers: user-mode process time only, with no
  kernel/DPC/ISR time.
* ``#11`` the ``undecodable`` token stays inside the ledger section, so a target-side total is never
  read as a row- or arm-level number.

By default the script generates the frozen campaign tree (``verification/synthetic/make_tree.py``,
which needs no campaign and no network) and runs the built analysis over it, then judges the produced
document. ``--tables PATH`` skips both and checks an already-written ``tables.md`` instead, which is
how a mutated copy proves the guards can fail.

Usage:
    python3 benchmarks/WinForward.E2E.Analysis/verification/check-fairness.py
    python3 benchmarks/WinForward.E2E.Analysis/verification/check-fairness.py --workdir /tmp/fairness
    python3 benchmarks/WinForward.E2E.Analysis/verification/check-fairness.py --tables /tmp/fairness/out/tables.md

Exit codes: ``0`` every guard held; ``1`` at least one guard failed; ``2`` the input could not be
produced or read.
"""

from __future__ import annotations

import argparse
import json
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

SECTION_MARK = re.compile(r"^## (\d+)\. ", re.MULTILINE)
TABLE_SEPARATOR = re.compile(r"^\|[\s:|-]+\|$")
UNDECODABLE = "undecodable"

# The hardcoded path both implementations are called with, and the one the frozen recipes are bound
# to: every `run.json` embeds its own `outDirectory`.
DEFAULT_TREE = Path("/tmp/wf-synth")


def udp_bearing_headline_column(header):
    """Whether a headline-matrix column reports a UDP measurement rather than a TCP one."""
    lowered = header.lower()
    return "udp" in lowered or lowered.startswith(("dns", "loss"))


# Per section: the identity columns, the latency classes in scope (None = the section's main
# table), and which of the remaining columns carry UDP numbers (None = every one of them).
UDP_TABLES = {
    "4": (("row",), None, udp_bearing_headline_column),
    "5": (("row", "arm"), ("udp-rtt", "dns-rtt"), None),
    "8": (("row", "arm", "passes"), None, None),
}
DNS_MEASUREMENT_CELLS = ("DNS answerRate", "DNS dns-rtt p50", "DNSALT answerRate", "DNSALT dns-rtt p50")
CPU_SCOPE_MARKERS = ("user-mode only",)
CPU_SCOPE_GAP = ("DPC", "ISR", "kernel")


class Report:
    def __init__(self, source):
        self.source = source
        self.failures = []
        self.checks = 0

    def check(self, name, ok, detail=""):
        self.checks += 1
        if ok:
            print("  PASS  %s" % name)
            return True
        self.failures.append(name)
        print("  FAIL  %s" % name)
        if detail:
            for line in str(detail).splitlines():
                print("        %s" % line)
        return False

    def note(self, text):
        print("  NOTE  %s" % text)

    def summary(self):
        if self.failures:
            print(
                "check-fairness.py: %s: %d of %d guard(s) failed: %s"
                % (self.source, len(self.failures), self.checks, "; ".join(self.failures))
            )
            return 1
        print("check-fairness.py: %s: all %d guard(s) held" % (self.source, self.checks))
        return 0


def unusable(message):
    """Exit 2: the input could not be produced or read, so no guard was judged."""
    print(message, file=sys.stderr)
    raise SystemExit(2)


def repository_root(start):
    for candidate in (start, *start.parents):
        if (candidate / "benchmarks" / "WinForward.E2E").is_dir():
            return candidate
    unusable("check-fairness.py: no repository root above %s" % start)


def load_rules(root, given):
    """The externalized row table, and the labels and marker every guard judges against."""
    path = Path(given) if given else (
        root / "benchmarks" / "WinForward.E2E.Analysis" / "verification" / "row-profiles.json"
    )
    if not path.is_file():
        unusable("check-fairness.py: no rule source at %s" % path)
    try:
        rules = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as error:
        unusable("check-fairness.py: %s is not valid JSON: %s" % (path, error))
    for key in ("rows", "plan_arms", "carriage", "not_carried_cell"):
        if key not in rules:
            unusable("check-fairness.py: %s holds no %r" % (path, key))
    return path, rules


def sections_of(text):
    out = {}
    marks = list(SECTION_MARK.finditer(text))
    for index, mark in enumerate(marks):
        end = marks[index + 1].start() if index + 1 < len(marks) else len(text)
        out[mark.group(1)] = text[mark.start() : end]
    return out


def split_row(line):
    return [cell.strip() for cell in line.strip().strip("|").split("|")]


def tables_of(section):
    """Every table as ``(heading, header, rows)``: the heading is the nearest ``###`` above it."""
    lines = section.splitlines()
    out = []
    heading = ""
    index = 0
    while index < len(lines):
        if lines[index].startswith("### "):
            heading = lines[index]
        if lines[index].startswith("|") and index + 1 < len(lines) and TABLE_SEPARATOR.match(lines[index + 1].strip()):
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


def scoped_tables(section, classes):
    """The tables one guard reads: the classes' own tables, or the section's main table.

    Sections 5 and 9 are one table per latency class, so only the classes that report UDP are in
    scope; the other sections lead with the table being judged and follow it with auxiliary ones
    (pass counts, denominators) whose cells count the same records without rendering a result.
    """
    tables = tables_of(section)
    if classes is None:
        return tables[:1]
    return [table for table in tables if any(name in table[0] for name in classes)]


def table_rows(section, classes=None):
    for _, header, rows in scoped_tables(section, classes):
        for row in rows:
            # Rows that print the not-carried marker carry one trailing cell past the header (the
            # analysis has always rendered them that way); zip keeps the named columns, and a row
            # shorter than its header is dropped so a malformed table fails the guards instead.
            if len(row) >= len(header):
                yield header, dict(zip(header, row))


def designed_rows(rules, sections):
    """The campaign's designed rows that section 1 says this tree holds, and the arms each ran.

    The rules live in `row-profiles.json` and the tree decides which of them can be observed: a
    one-row selftest tree holds none of the campaign's rows, and its report cannot be judged against
    a rule about them.
    """
    rows = {}
    for header, row in table_rows(sections.get("1", "")):
        if "arms the plan runs" not in header or row.get("row") not in rules["rows"]:
            continue
        rows[row["row"]] = row.get("arms the plan runs", "")
    return rows


def guard_designed_rows(rules, sections, report):
    """A document that holds none of the declared rows cannot be judged, which is a failure."""
    rows = designed_rows(rules, sections)
    report.check(
        "#17/designed-rows: section 1 holds at least one declared row",
        bool(rows),
        "section 1 publishes none of the %d row(s) the rules declare, so no row could be judged"
        % len(rules["rows"]),
    )
    return rows


def guard_udp_leakage(rules, sections, report):
    rows = designed_rows(rules, sections)
    incapable = sorted(name for name, profile in rules["rows"].items() if profile["udp"] == "not-carried")
    in_tree = [name for name in incapable if name in rows]
    cell = rules["not_carried_cell"]
    report.check(
        "#17/rule: the rules declare at least one UDP-incapable row",
        bool(incapable),
        "no row in row-profiles.json declares udp == not-carried, so no rendering could be wrong about it",
    )
    if not in_tree:
        report.note(
            "#17: this tree holds none of the UDP-incapable rows (%s), so the marker cannot be observed"
            % ", ".join(incapable)
        )
        return

    published = {}
    for header, row in table_rows(sections.get("1", "")):
        if "row" in header and "what it does with general UDP" in header:
            published[row["row"]] = row["what it does with general UDP"]
    missing = ["%s -> %r" % (name, published.get(name)) for name in in_tree if published.get(name) != cell]
    report.check(
        "#17/profile: section 1 publishes every UDP-incapable row as %r" % cell,
        not missing,
        "\n".join(missing),
    )

    for number, (identity, classes, picker) in sorted(UDP_TABLES.items()):
        offenders = []
        checked = 0
        for _, row in table_rows(sections.get(number, ""), classes):
            if row.get("row") not in in_tree:
                continue
            for column, value in row.items():
                if column in identity or (picker is not None and not picker(column)):
                    continue
                checked += 1
                if value != cell:
                    offenders.append("%s %s: %r" % (row["row"], column, value))
        report.check(
            "#17/section %s: every UDP cell of a UDP-incapable row reads %r (%d cell(s))" % (number, cell, checked),
            not offenders and checked > 0,
            "\n".join(offenders) if offenders else "no UDP cell of an UDP-incapable row was found",
        )

    offenders = []
    checked = 0
    for _, row in table_rows(sections.get("9", "")):
        if row.get("row") not in in_tree:
            continue
        for column in DNS_MEASUREMENT_CELLS:
            if column not in row:
                continue
            checked += 1
            if row[column] != cell:
                offenders.append("%s %s: %r" % (row["row"], column, row[column]))
        if "UDP bypassed" not in row.get("comparability", ""):
            offenders.append("%s comparability: %r" % (row["row"], row.get("comparability")))
    report.check(
        "#17/section 9: the UDP-incapable row's DNS numbers are withheld (%d cell(s))" % checked,
        not offenders and checked > 0,
        "\n".join(offenders) if offenders else "no DNS cell of an UDP-incapable row was found",
    )

    carriers = [name for name in rows if rules["rows"][name]["udp"] != "not-carried"]
    carrying = 0
    for _, row in table_rows(sections.get("8", "")):
        if not row.get("UDP carriage", "").startswith("proxied"):
            continue
        if any(not value.startswith("n/a") and value != cell for value in row.values()) and row.get("row") in carriers:
            carrying += 1
    report.check(
        "#17/counterweight: a row that does carry UDP still prints numbers",
        carrying > 0 or not carriers,
        "section 8 marks every row as not carried, so the marker no longer distinguishes anything",
    )


def dns_measured(row):
    """Whether a row of the DNS table measured the port-53 arm at all, rather than never running it."""
    columns = ("DNS arm port", "DNSALT arm port", *DNS_MEASUREMENT_CELLS)
    return any(not row.get(column, "n/a").startswith("n/a") for column in columns)


def guard_dns_paths(rules, sections, report):
    rows = designed_rows(rules, sections)
    wanted = {name for name, arms in rows.items() if "DNS" in arms}
    offences = []
    seen = set()
    relayed = 0
    elsewhere = 0
    for _, row in table_rows(sections.get("9", "")):
        row_id = row.get("row")
        profile = rules["rows"].get(row_id)
        if profile is None or not dns_measured(row):
            continue
        seen.add(row_id)
        expected = rules["carriage"]["udp53"][profile["udp53"]]
        if row.get("UDP/53 carriage") != expected:
            offences.append("%s: %r != %r" % (row_id, row.get("UDP/53 carriage"), expected))
        if profile["udp53"] == "relayed":
            relayed += 1
            continue
        elsewhere += 1
        note = row.get("comparability", "")
        if row.get("DNS answerRate") == rules["not_carried_cell"]:
            if "UDP bypassed" not in note:
                offences.append("%s comparability: %r lacks the exclusion note" % (row_id, note))
        elif "not cross-product comparable" not in note:
            offences.append("%s comparability: %r lacks the warning" % (row_id, note))
    report.check(
        "#18/section 9: every measured row's UDP/53 path carries the rules' own label",
        not offences,
        "\n".join(offences),
    )
    report.check(
        "#18/coverage: the DNS table holds every designed row whose plan runs a DNS arm (%d)" % len(wanted),
        seen == wanted,
        "labelled %s, wanted %s" % (sorted(seen) or "nothing", sorted(wanted) or "nothing"),
    )
    if relayed and elsewhere:
        report.note("#18: the table carries %d relayed and %d non-relayed port-53 row(s)" % (relayed, elsewhere))
    elif seen:
        report.note(
            "#18: every DNS row in this tree is on the same port-53 path, so the labels cannot be told apart here"
        )

    wanted8 = {
        name
        for name, arms in rows.items()
        if ("LOSS" in arms or "MIX" in arms) and rules["rows"][name]["udp"] != "not-carried"
    }
    offences = []
    seen8 = set()
    for _, row in table_rows(sections.get("8", "")):
        profile = rules["rows"].get(row.get("row"))
        measured = not row.get("sent", "n/a").startswith("n/a")
        if profile is None or not measured or not row.get("UDP carriage", "").startswith("proxied"):
            continue
        seen8.add(row["row"])
        expected = rules["carriage"]["udp53"][profile["udp53"]]
        if row.get("UDP/53 carriage") != expected:
            offences.append("%s: %r != %r" % (row.get("row"), row.get("UDP/53 carriage"), expected))
    report.check(
        "#18/section 8: the UDP-accuracy table repeats the carriage label of every row it holds (%d)"
        % len(seen8),
        not offences and seen8 == wanted8,
        ("\n".join(offences) + "\n" if offences else "")
        + "labelled %s, wanted %s" % (sorted(seen8) or "nothing", sorted(wanted8) or "nothing"),
    )


def guard_cpu_scope(sections, report):
    body = sections.get("6", "")
    lowered = body.lower()
    report.check(
        "#19/scope: the CPU table discloses 'user-mode only'",
        any(marker in lowered for marker in CPU_SCOPE_MARKERS),
        "section 6 carries no user-mode scope marker",
    )
    report.check(
        "#19/gap: the disclosure names the kernel/DPC/ISR time it leaves out",
        any(token.lower() in lowered for token in CPU_SCOPE_GAP),
        "section 6 does not say that kernel, DPC or ISR time is outside the number",
    )


def guard_undecodable(sections, report):
    present = sorted(number for number, body in sections.items() if UNDECODABLE in body.lower())
    report.check(
        "#11/arms: the undecodable token stays inside the ledger section",
        present in ([], ["14"]),
        "the token appears in section(s) %s" % ", ".join(present),
    )
    if not present:
        report.note("#11: this tree's targets decoded every datagram, so no disclosure is printed")


def guard_text(rules, text, source):
    sections = sections_of(text)
    report = Report(source)
    guard_designed_rows(rules, sections, report)
    guard_udp_leakage(rules, sections, report)
    guard_dns_paths(rules, sections, report)
    guard_cpu_scope(sections, report)
    guard_undecodable(sections, report)
    return report


def run(command, what):
    result = subprocess.run(command, capture_output=True, text=True, check=False)
    if result.returncode != 0:
        unusable(
            "check-fairness.py: %s failed (rc=%d)\n%s\n%s"
            % (what, result.returncode, result.stdout[-2000:], result.stderr[-2000:])
        )
    return result


def produced_tables(root, tree_dir, workdir):
    """The frozen tree, analyzed by the built C# analysis: the document the guards judge."""
    make_tree = root / "benchmarks" / "WinForward.E2E.Analysis" / "verification" / "synthetic" / "make_tree.py"
    analyzer = root / "benchmarks" / "WinForward.E2E.Analysis" / "bin" / "Release" / "net10.0" / "WinForward.E2E.Analysis"
    if not make_tree.is_file():
        unusable("check-fairness.py: no make_tree.py at %s" % make_tree)
    if not analyzer.is_file():
        unusable("check-fairness.py: no built analysis at %s; build it first" % analyzer)
    if tree_dir.exists():
        shutil.rmtree(tree_dir)
    tree_dir.mkdir(parents=True)
    run([sys.executable, str(make_tree), str(tree_dir / "raw")], "make_tree.py on the frozen tree")
    run([str(analyzer), "--raw", str(tree_dir / "raw"), "--out", str(workdir / "out")], "the analysis on the frozen tree")
    return (workdir / "out" / "tables.md").read_text(encoding="utf-8")


def self_check(rules, text, report):
    """One edit per guard, on the document under judgement: every guard must reject its own control.

    The controls are the mutations the guards exist for, so a guard that survives its control is not
    a guard -- it is a restatement of whatever the analysis happened to print.
    """
    controls = [
        (
            "not-carried-marker-to-number",
            lambda body: rewrite_profile_cell(body, rules, "not-carried", rules["not_carried_cell"], "0"),
            "#17/profile",
        ),
        (
            "not-carried-udp-cell-to-zero",
            lambda body: rewrite_first_cell_in(body, "4", rules["not_carried_cell"], "0"),
            "#17/section 4",
        ),
        (
            "udp53-carriage-label-shortened",
            lambda body: rewrite_first_cell_in(body, "9", rules["carriage"]["udp53"]["direct-local-target"], "direct"),
            "#18/section 9",
        ),
        (
            "cpu-scope-marker-dropped",
            lambda body: body.replace("user-mode only", "process time", 1),
            "#19/scope",
        ),
        (
            "undecodable-moved-out-of-14",
            lambda body: body.replace("## 8. UDP accuracy detail", "## 8. UDP accuracy detail\n\n%s" % UNDECODABLE, 1),
            "#11/arms",
        ),
        (
            "designed-rows-renamed",
            lambda body: rename_designed_rows(body, rules),
            "#17/designed-rows",
        ),
    ]
    for name, mutate, guard in controls:
        mutated = mutate(text)
        if mutated == text:
            report.check("control/%s: the control edits the document" % name, False, "the mutation was a no-op")
            continue
        outcome = guard_text(rules, mutated, name)
        report.check(
            "control/%s: %s rejects the mutation" % (name, guard),
            any(failure.startswith(guard) for failure in outcome.failures),
            "failures: %s" % (outcome.failures or "none"),
        )


def rewrite_profile_cell(body, rules, udp_carriage, old, new):
    """The section-1 row of the first row whose rules declare `udp_carriage`, with one cell edited."""
    row_id = next(name for name, profile in rules["rows"].items() if profile["udp"] == udp_carriage)
    for line in body.splitlines():
        if line.startswith("| %s |" % row_id) and old in line:
            return body.replace(line, line.rsplit("|", 2)[0] + "| %s |" % new, 1)
    return body


def rewrite_first_cell_in(body, section_number, old, new):
    """The first table cell of `section_number` that carries `old`, rewritten."""
    section = sections_of(body).get(section_number, "")
    for line in section.splitlines():
        if line.startswith("|") and old in line:
            return body.replace(line, line.replace(old, new, 1), 1)
    return body


def rename_designed_rows(body, rules):
    """Every declared row id in the document, renamed, so section 1 holds none of them."""
    return re.sub(
        r"\| (%s) \|" % "|".join(re.escape(name) for name in rules["rows"]),
        lambda match: "| %s-renamed |" % match.group(1),
        body,
    )


def parse_args(argv):
    parser = argparse.ArgumentParser(add_help=True, usage=__doc__.split("Usage:", 1)[1].split("Exit codes")[0].strip())
    parser.add_argument("--tables", action="append", default=None, help="check this tables.md instead of producing one")
    parser.add_argument("--rules", default=None, help="the row-profile rule source (default: the analysis's own)")
    parser.add_argument("--tree-dir", type=Path, default=DEFAULT_TREE, help="where the frozen tree is generated (the recipes' own path)")
    parser.add_argument("--workdir", default=None, help="where the fixture tree is written (default: a temporary directory)")
    parser.add_argument("--self-check", action="store_true", help="also mutate the document once per guard and require the guard to reject it")
    return parser.parse_args(argv)


def main(argv=None):
    args = parse_args(argv)
    root = repository_root(Path(__file__).resolve())
    rules_path, rules = load_rules(root, args.rules)
    print("check-fairness.py: rules %s (%d row(s))" % (rules_path, len(rules["rows"])))

    if args.tables:
        exit_code = 0
        for name in args.tables:
            path = Path(name)
            if not path.is_file():
                print("check-fairness.py: no such tables.md: %s" % path, file=sys.stderr)
                return 2
            report = guard_text(rules, path.read_text(encoding="utf-8-sig"), str(path))
            exit_code |= report.summary()
        return exit_code

    temporary = None
    if args.workdir:
        workdir = Path(args.workdir)
        workdir.mkdir(parents=True, exist_ok=True)
    else:
        temporary = tempfile.TemporaryDirectory(prefix="wf-fairness-")
        workdir = Path(temporary.name)
    try:
        text = produced_tables(root, args.tree_dir, workdir)
        source = "the frozen tree, analyzed by the C# analysis"
        if args.self_check:
            report = Report(source)
            report.check(
                "#self-check: the document is the analysis's own tables.md",
                "# End-to-end transparent-proxy campaign" in text,
                "the document does not look like a produced report",
            )
            self_check(rules, text, report)
            return report.summary()
        return guard_text(rules, text, source).summary()
    finally:
        if temporary is not None:
            temporary.cleanup()


if __name__ == "__main__":
    sys.exit(main())
