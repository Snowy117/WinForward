#!/usr/bin/env python3
"""Guard the campaign analysis's fairness disclosures with assertions on its own output.

Four guards, each judged against the analysis *source*: ``analyze.py`` is imported, so a rule and
its rendering are compared rather than a rendering being grepped for a remembered string.

* ``#17`` every row the source declares incapable of carrying UDP prints ``not carried (UDP
  bypassed)`` in every UDP-bearing cell of the headline, latency, UDP-accuracy and DNS tables --
  never a number -- while a row that does carry UDP still prints numbers, so a renderer that marks
  everything fails too.
* ``#18`` every row that measured the port-53 arm prints the source's own carriage label for its
  UDP/53 path, and a row whose path is not the relayed one is marked not cross-product comparable.
* ``#19`` the CPU table discloses the scope of its numbers: user-mode process time only, with no
  kernel/DPC/ISR time.
* ``#11`` a target ledger reporting undecodable datagrams is disclosed as a target-side total inside
  the ledger section and nowhere else -- the count never reaches a row- or arm-level cell -- and a
  ledger reporting none prints no disclosure at all.

By default the script generates the fixture tree (``synthetic/make_tree.py``, which needs no
campaign and no network) twice, once clean and once with an undecodable count, and runs every guard
against both. ``--tables PATH`` skips generation and checks an already-written ``tables.md``
instead, which is how a mutated copy proves the guards can fail.

Usage:
    python3 scripts/check-fairness.py
    python3 scripts/check-fairness.py --workdir /tmp/fairness --undecodable 3
    python3 scripts/check-fairness.py --tables analysis/verification/synthetic-tables.md

Exit codes: ``0`` every guard held; ``1`` at least one guard failed; ``2`` the input could not be
produced or read.
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
import tempfile
from pathlib import Path

# The analysis package's __pycache__ is tracked in git, so importing analyze.py must not rewrite it.
sys.dont_write_bytecode = True

SECTION_MARK = re.compile(r"^## (\d+)\. ", re.MULTILINE)
TABLE_SEPARATOR = re.compile(r"^\|[\s:|-]+\|$")
UNDECODABLE = "undecodable"


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
            for line in detail.splitlines():
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


def resolve_analysis_dir(root, given):
    """Where ``analyze.py`` lives: the explicit argument, E4's home, or the newest campaign's."""
    if given is not None:
        candidates = [Path(given)]
    else:
        candidates = [root / "benchmarks" / "WinForward.E2E.Analysis"]
        candidates.extend(sorted((root / "benchmarks" / "results").glob("*/analysis")))
    for candidate in candidates:
        if (candidate / "analyze.py").is_file():
            return candidate
    unusable(
        "check-fairness.py: no analyze.py under any of %s; pass --analysis-dir"
        % ", ".join(str(candidate) for candidate in candidates)
    )


def load_analysis(analysis_dir):
    sys.path.insert(0, str(analysis_dir))
    import analyze  # noqa: PLC0415  (the module is the rule source, resolved at run time)

    return analyze


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


def designed_rows(analysis, sections):
    """The campaign's designed rows that section 1 says this tree holds, and the arms each ran.

    The rules live in the analysis source and the tree decides which of them can be observed: a
    one-row selftest tree holds none of the campaign's rows, and its report cannot be judged against
    a rule about them.
    """
    rows = {}
    for header, row in table_rows(sections.get("1", "")):
        if "arms the plan runs" not in header or row.get("row") not in analysis.ROW_PROFILES:
            continue
        rows[row["row"]] = row.get("arms the plan runs", "")
    return rows


def guard_udp_leakage(analysis, sections, report):
    rows = designed_rows(analysis, sections)
    incapable = sorted(analysis.UDP_INCAPABLE_ROWS)
    in_tree = [name for name in incapable if name in rows]
    cell = analysis.NOT_CARRIED_CELL
    report.check(
        "#17/rule: the analysis declares at least one UDP-incapable row",
        bool(incapable),
        "UDP_INCAPABLE_ROWS is empty, so no rendering could be wrong about it",
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

    carriers = [
        name for name in rows if analysis.ROW_PROFILES[name].udp != analysis.UDP_NOT_CARRIED
    ]
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


def guard_dns_paths(analysis, sections, report):
    rows = designed_rows(analysis, sections)
    wanted = {name for name, arms in rows.items() if "DNS" in arms}
    offenders = []
    seen = set()
    relayed = 0
    elsewhere = 0
    for _, row in table_rows(sections.get("9", "")):
        row_id = row.get("row")
        profile = analysis.ROW_PROFILES.get(row_id)
        if profile is None or not dns_measured(row):
            continue
        seen.add(row_id)
        expected = analysis.UDP53_LABEL[profile.udp53]
        if row.get("UDP/53 carriage") != expected:
            offenders.append("%s: %r != %r" % (row_id, row.get("UDP/53 carriage"), expected))
        if profile.udp53 == analysis.UDP53_RELAYED:
            relayed += 1
            continue
        elsewhere += 1
        note = row.get("comparability", "")
        if row.get("DNS answerRate") == analysis.NOT_CARRIED_CELL:
            if "UDP bypassed" not in note:
                offenders.append("%s comparability: %r lacks the exclusion note" % (row_id, note))
        elif "not cross-product comparable" not in note:
            offenders.append("%s comparability: %r lacks the warning" % (row_id, note))
    report.check(
        "#18/section 9: every measured row's UDP/53 path carries the source's own label",
        not offenders,
        "\n".join(offenders),
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
        if ("LOSS" in arms or "MIX" in arms) and analysis.ROW_PROFILES[name].udp != analysis.UDP_NOT_CARRIED
    }
    offenders = []
    seen8 = set()
    for _, row in table_rows(sections.get("8", "")):
        profile = analysis.ROW_PROFILES.get(row.get("row"))
        measured = not row.get("sent", "n/a").startswith("n/a")
        if profile is None or not measured or not row.get("UDP carriage", "").startswith("proxied"):
            continue
        seen8.add(row["row"])
        expected = analysis.UDP53_LABEL[profile.udp53]
        if row.get("UDP/53 carriage") != expected:
            offenders.append("%s: %r != %r" % (row.get("row"), row.get("UDP/53 carriage"), expected))
    report.check(
        "#18/section 8: the UDP-accuracy table repeats the carriage label of every row it holds (%d)"
        % len(seen8),
        not offenders and seen8 == wanted8,
        ("\n".join(offenders) + "\n" if offenders else "")
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


def guard_undecodable(sections, expected, report):
    """``expected`` is the ledger's own total, or ``None`` when only a text file was supplied."""
    present = sorted(number for number, body in sections.items() if UNDECODABLE in body.lower())
    if expected is None:
        if not present:
            report.note("#11: no undecodable disclosure in this file (a clean ledger prints none)")
            return
        report.check(
            "#11/arms: the undecodable token stays inside the ledger section",
            present == ["14"],
            "the token appears in section(s) %s" % ", ".join(present),
        )
        return

    if expected == 0:
        report.check(
            "#11/zero: a ledger with no undecodable datagram prints no disclosure",
            not present,
            "the token appears in section(s) %s" % ", ".join(present),
        )
        return

    report.check(
        "#11/arms: the undecodable token appears in the ledger section and nowhere else",
        present == ["14"],
        "the token appears in section(s) %s" % ", ".join(present),
    )
    disclosure_tables = 0
    totals = []
    last_row = None
    for _, header, rows in tables_of(sections.get("14", "")):
        columns = [column.lower() for column in header]
        if UNDECODABLE not in columns:
            continue
        disclosure_tables += 1
        for row in rows:
            try:
                totals.append(float(row[columns.index(UNDECODABLE)].replace(",", "")))
            except (ValueError, IndexError):
                continue
        if rows:
            try:
                last_row = float(rows[-1][columns.index(UNDECODABLE)].replace(",", ""))
            except (ValueError, IndexError):
                last_row = None
    report.check(
        "#11/table: the ledger section carries exactly one undecodable table",
        disclosure_tables == 1,
        "%d table(s) in section 14 carry an undecodable column, so the count is attributed twice"
        % disclosure_tables,
    )
    report.check(
        "#11/value: the disclosed total is the ledger's own total (%s)" % expected,
        bool(totals) and max(totals) == float(expected),
        "disclosed %s" % (totals if totals else "nothing"),
    )
    report.check(
        "#11/total-row: the table's last row is the total and equals the ledger's own total (%s)" % expected,
        last_row == float(expected),
        "the last row reads %s" % last_row,
    )


def guard_text(analysis, text, expected, source):
    sections = sections_of(text)
    report = Report(source)
    guard_udp_leakage(analysis, sections, report)
    guard_dns_paths(analysis, sections, report)
    guard_cpu_scope(sections, report)
    guard_undecodable(sections, expected, report)
    return report


def run(command, what):
    result = subprocess.run(command, capture_output=True, text=True, check=False)
    if result.returncode != 0:
        unusable(
            "check-fairness.py: %s failed (rc=%d)\n%s\n%s"
            % (what, result.returncode, result.stdout[-2000:], result.stderr[-2000:])
        )
    return result


def ledger_undecodable_total(raw):
    """The fixture's own undecodable total, recomputed from its ledger files."""
    total = 0
    for path in sorted(raw.parent.glob("*ledger*.jsonl")):
        highest = 0
        for line in path.read_text(encoding="utf-8-sig").splitlines():
            if not line.strip():
                continue
            record = json.loads(line)
            value = record.get(UNDECODABLE)
            if value is None and record.get("type") == "targetSummary":
                value = (record.get("udp") or {}).get(UNDECODABLE)
            if isinstance(value, (int, float)):
                highest = max(highest, value)
        total += highest
    return total


def fixture_tables(analysis_dir, workdir, undecodable):
    tree = workdir / ("clean" if undecodable == 0 else "counted")
    raw = tree / "raw"
    run(
        [sys.executable, str(analysis_dir / "synthetic" / "make_tree.py"), "--undecodable", str(undecodable), str(raw)],
        "make_tree.py --undecodable %d" % undecodable,
    )
    run(
        [sys.executable, str(analysis_dir / "analyze.py"), "--raw", str(raw), "--out", str(tree / "out")],
        "analyze.py on the undecodable=%d fixture" % undecodable,
    )
    text = (tree / "out" / "tables.md").read_text(encoding="utf-8")
    total = ledger_undecodable_total(raw)
    if undecodable and not total:
        unusable(
            "check-fairness.py: the fixture was asked for %d undecodable datagram(s) but its ledger "
            "carries none, so the #11 guards would judge the zero case instead" % undecodable
        )
    # Section 2 echoes the tree's own path; fold it out so a work directory that spells a
    # counter's name cannot masquerade as the analysis printing that counter.
    return text.replace(str(workdir), "<workdir>"), total


def parse_args(argv):
    parser = argparse.ArgumentParser(add_help=True, usage=__doc__.split("Usage:", 1)[1].split("Exit codes")[0].strip())
    parser.add_argument("--tables", action="append", default=None, help="check this tables.md instead of generating one")
    parser.add_argument("--analysis-dir", default=None, help="the directory holding analyze.py and synthetic/make_tree.py")
    parser.add_argument("--workdir", default=None, help="where the fixture trees are written (default: a temporary directory)")
    parser.add_argument("--undecodable", type=int, default=7, help="undecodable datagrams the fixture's ledger reports")
    return parser.parse_args(argv)


def main(argv=None):
    args = parse_args(argv)
    if args.undecodable < 0:
        print("check-fairness.py: --undecodable must not be negative", file=sys.stderr)
        return 2
    root = repository_root(Path(__file__).resolve())
    analysis_dir = resolve_analysis_dir(root, args.analysis_dir)
    analysis = load_analysis(analysis_dir)
    print("check-fairness.py: analysis source %s" % (analysis_dir / "analyze.py"))

    if args.tables:
        exit_code = 0
        for name in args.tables:
            path = Path(name)
            if not path.is_file():
                print("check-fairness.py: no such tables.md: %s" % path, file=sys.stderr)
                return 2
            report = guard_text(analysis, path.read_text(encoding="utf-8-sig"), None, str(path))
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
        exit_code = 0
        for undecodable in (0, args.undecodable):
            text, total = fixture_tables(analysis_dir, workdir, undecodable)
            source = "fixture with %d undecodable datagram(s) (ledger total %d)" % (undecodable, total)
            exit_code |= guard_text(analysis, text, total, source).summary()
        return exit_code
    finally:
        if temporary is not None:
            temporary.cleanup()


if __name__ == "__main__":
    sys.exit(main())
