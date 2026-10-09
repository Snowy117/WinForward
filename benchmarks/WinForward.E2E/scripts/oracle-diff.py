#!/usr/bin/env python3
"""Compare the frozen Python oracle with the C# analysis, one batch of slices at a time.

Both implementations write a *complete* `tables.md` and `verdict.json`; the progress of the C#
port is measured by comparing only the slices one batch owns, so a batch can be declared done while
the batches after it are still placeholders. The separation lives here and nowhere else:

    BATCH_SECTIONS = {batch: {"tables.md": [...], "verdict.json": [...]}}

Tables are sliced by their `## N.` heading (plus a `preamble` slice, everything before `## 0.`, which
holds the `--raw` value and the program-name line), the verdict by top-level key -- with `metrics`
split into `metrics/<key>` so its extractors can land in two batches. Every slice is owned by exactly
one batch; the startup check refuses a table that would leave one unowned or own one twice.

Two comparison modes:

    --mode semantic (default)   Compare *meaning*, because the C# rewrite exists for maintainability
                                rather than for byte-identical output (D21).

                                Asserted exactly, never tolerated (all of it reported as
                                `structure`):
                                  * `tables.md`: the 16 `## N.` headings and their titles, each
                                    exactly once, and the `preamble`;
                                  * `tables.md` per table: the header column names, the row identity
                                    columns, every row's cell count (which is what keeps the
                                    reference's malformed 11-cell row visible), and every cell's
                                    category -- number / `n/a` / `n/a (<reason>)` / empty / `< 3/n`
                                    bound;
                                  * `verdict.json`: the 14 top-level keys and the 21 `metrics`
                                    members, `bootstrap`'s member keys and value types
                                    (`resamples`/`seed`/`min_passes` are exact integers),
                                    `thresholds`' member keys and text, `generated_by`/`raw`/`flat_mode`,
                                    and `passes`/`rows` as ordered arrays;
                                  * the artifacts' form: UTF-8 without BOM, `\\n` line endings, exactly
                                    one trailing newline, and `plots/SKIPPED.md` byte-for-byte the
                                    frozen text.

                                Compared within a tolerance (all of it reported as `value`):
                                  * numbers by **one unit of the last printed digit** (the reference's
                                    printed precision is the contract; `--tolerance` scales that
                                    unit, and a printed value that lost the fraction the reference
                                    printed is not a formatting difference; a run of two dots or more
                                    -- an endpoint, a version -- is an identifier and is compared
                                    exactly);
                                  * composite cells (`median [p25-p75] unit (n=K)`, `< 3/n = x %`)
                                    split into their numbers, with brackets, the en dash and the
                                    spacing treated as typography;
                                  * JSON floats parsed into numbers before comparison;
                                  * strings compared by their decoded value (`\\u2013` and `-` are the
                                    same character) after folding whitespace and normalizing dashes
                                    and quotes, with the numbers inside them compared under the same
                                    tolerance and the wording itself required to match;
                                  * the object key order ignored, and table rows looked up by their
                                    identity key rather than by position (the row *sets* must still
                                    be equal).

                                One class of number gets a declared tolerance of its own, because it
                                is resampling noise rather than a published statistic: inside a
                                `metrics/<member>.pairs[i]` entry, the four p-value leaves
                                (`p_value`, `holm_p_value`, `p_equivalence`, `holm_p_equivalence`)
                                are compared within an absolute `5e-2` and the two interval edges
                                (`ci95[0]`, `ci95[1]`) within `max(1e-2, 1e-2 * abs(expected))`.
                                A p-value is a `--resamples`-draw estimate of a probability,
                                doubled, so two generator sequences estimate the same one with a
                                difference of 6.4e-3 on average and 3.5e-2 at worst -- wider than
                                the last-printed-digit rule allows and narrower than any real
                                change. `holm_p_value` and both `ci95` edges move nowhere on this
                                tree (3 passes leave the 2.5 % quantile on the smallest atom) and
                                stay in the set for the longer campaigns where they do. The bound is
                                the width of that noise, the path has to match exactly, and the
                                leaves are the only numbers the relaxation touches; the estimates
                                `estimate`/`median`/`iqr`, every verdict string, every table cell and
                                all key sets keep the rules above.

                                Markers (`FAIL`, `n/a (not comparable)`, `not carried (UDP bypassed)`,
                                ...) go through an explicit equivalence-class table; a marker is never
                                silently equal to arbitrary text.

    --mode byte                 Compare each slice the way it was compared before semantic mode
                                existed: `tables.md` by its text, `verdict.json` by the canonical
                                re-serialization of each parsed subtree -- so member order inside a
                                slice counts, the top level's order does not, and an escape and the
                                character it stands for are the same text. It is the structure-surface
                                regression mode (`--byte` is its short flag), not a batch criterion.

Exit codes (three states, never two):

    0   every slice of this batch exists on both sides and is equal
    1   every slice exists but at least one has a structural difference, or a value outside tolerance
    2   something that should exist does not -- a key the batch owns, a whole file, a table row, a
        `plots/SKIPPED.md`, a file that is not valid UTF-8 or not valid JSON, a tree that does not
        hold two ledgers, a batch table that does not add up -- "nothing to compare" is never a pass,
        and an infrastructure failure must not be reportable as "the content differs"

The script is the only caller of the two sides, and it calls both with the same hardcoded absolute
path (`/tmp/wf-synth/raw`): the raw path, the two ledger paths and §2's path columns are part of the
compared output, so a tree extracted anywhere else would compare unequal for the wrong reason.

Usage:
    python3 oracle-diff.py --batch 3
    python3 oracle-diff.py --batch 1a,1b,1c --cs-out DIR --golden DIR
    python3 oracle-diff.py --mode byte --batch 5

`--cs-out` compares an existing output directory instead of running the analyzer, and `--golden`
overrides the frozen reference; both exist for the mechanism's own self-checks (a skeleton must be
reported as missing slices, a deliberately changed number as a difference).
"""

from __future__ import annotations

import argparse
import codecs
import json
import re
import shutil
import subprocess
import sys
import tarfile
import unicodedata
from decimal import Decimal, InvalidOperation
from pathlib import Path
from typing import NoReturn

REPO_ROOT = Path(__file__).resolve().parents[3]
VERIFICATION = REPO_ROOT / "benchmarks" / "WinForward.E2E.Analysis" / "verification"
GOLDEN = VERIFICATION / "golden"
ARCHIVE = VERIFICATION / "synthetic-tree.tar.gz"
PLOTS = VERIFICATION / "plots-SKIPPED.md"
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
HEADING_PATTERN = re.compile(r"^(##) (\d+)\.\s*(.*)$", re.MULTILINE)

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

# The two slices whose member set and value types are asserted by their own rule rather than walked
# generically: they are flat blocks of published parameters, and a member that is not the promised
# kind is a structural difference rather than a value one.
PINNED_BLOCKS = frozenset({"bootstrap", "thresholds"})
BOOTSTRAP_INTEGERS = ("resamples", "seed", "min_passes")

ABSENT = "<absent>"

# The keys whose strings are *wording*: a reason or a finding quoted in prose, where the numbers the
# sentence carries are reprinted values and follow the numeric tolerance while the words must match.
WORDING_KEYS = frozenset({"detail", "status_reason", "notes", "reason"})


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


def check_coverage(golden: Path) -> None:
    """Refuse a batch table that would let a slice fall outside every batch, or into two.

    `metrics` is the one container two batches share: its members are owned one at a time, so the
    check compares the set of `metrics/<key>` slices with the members the frozen reference actually
    publishes rather than with the container's name.
    """
    covered_sections: dict[str, str] = {}
    covered_keys: dict[str, str] = {}
    for batch, slices in BATCH_SECTIONS.items():
        if not slices["tables.md"] and not slices["verdict.json"]:
            fail(f"batch {batch} owns no slice; a batch that compares nothing would pass vacuously")
        for section in slices["tables.md"]:
            if section in covered_sections:
                fail(f"tables.md:{section} is owned by both batch {covered_sections[section]} and {batch}")
            covered_sections[section] = batch
        for key in slices["verdict.json"]:
            if key in covered_keys:
                fail(f"verdict.json:{key} is owned by both batch {covered_keys[key]} and {batch}")
            covered_keys[key] = batch

    unowned_sections = sorted(set(ALL_SECTIONS) - set(covered_sections))
    unowned_keys = sorted(set(ALL_VERDICT_KEYS) - {key.split("/", 1)[0] for key in covered_keys})
    if unowned_sections or unowned_keys:
        fail(
            "BATCH_SECTIONS does not own every slice: "
            f"tables {unowned_sections}, verdict keys {unowned_keys}"
        )

    members = metrics_members(golden)
    if members is not None:
        unowned_members = sorted(members - {key.split("/", 1)[1] for key in covered_keys if "/" in key})
        if unowned_members:
            fail(f"BATCH_SECTIONS does not own every metrics member: {unowned_members}")


def metrics_members(golden: Path) -> set[str] | None:
    """The `metrics` members the frozen reference publishes, or None when the file is not there."""
    path = golden / GOLDEN_NAMES["verdict.json"]
    if not path.is_file():
        return None
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as error:
        fail(f"{path}: the frozen reference is not readable JSON: {error}")
    metrics = document.get("metrics") if isinstance(document, dict) else None
    return set(metrics) if isinstance(metrics, dict) else None


def extract_tree() -> None:
    """Unpack the frozen tree where both sides expect it, and assert its layout contract."""
    if TREE.exists():
        shutil.rmtree(TREE)
    TREE.mkdir(parents=True)
    try:
        with tarfile.open(ARCHIVE, "r:gz") as archive:
            archive.extractall(TREE, filter="data")
    except (OSError, tarfile.TarError) as error:
        fail(f"{ARCHIVE}: the frozen tree cannot be unpacked: {error}")

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


DASHES = str.maketrans({
    "\u2010": "-", "\u2011": "-", "\u2012": "-", "\u2013": "-", "\u2014": "-", "\u2015": "-",
    "\u2212": "-", "\u00ad": "-",
})
QUOTES = str.maketrans({
    "\u2018": "'", "\u2019": "'", "\u201a": "'", "\u201b": "'", "\u2032": "'",
    "\u201c": '"', "\u201d": '"', "\u201e": '"', "\u00ab": '"', "\u00bb": '"', "\u2033": '"',
})
NUMBER_BODY = re.compile(r"(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?")
NUMBER_TOKEN = re.compile(r"[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?")
PURE_INTEGER = re.compile(r"[-+]?\d+")
# Two dots or more is an identifier rather than a measurement: an endpoint, a version, a date.
DOTTED_TOKEN = re.compile(r"\d+(?:\.\d+){2,}")
WORD = re.compile(r"[A-Za-z]+")
NA_REASON_PATTERN = re.compile(r"n/a \((.*)\)")
BOUND_PATTERN = re.compile(r"<\s*\d+/")

# The words a *numeric* cell may still carry: units, the pass-count phrase and the "of N" tail.
# Anything else makes the cell text (a verdict, a reason, an endpoint, a hash), which is compared as
# wording rather than as a number.
UNIT_WORDS = frozenset({
    "us", "ms", "s", "pp", "n", "vcpu", "MiB", "Mbps", "min", "of", "across", "passes", "pass",
    "without", "a", "value", "unavailable", "es",
})

# Marker equivalence classes: spellings that mean the same thing on both sides. A cell that is a
# marker on one side and not on the other is always a difference -- markers are never silently equal
# to arbitrary text.
MARKERS = {
    "fail": "fail",
    "failed": "fail",
    "n/a (not comparable)": "not-comparable",
    "not carried (udp bypassed)": "udp-not-carried",
    "not measured in this row": "not-measured",
    "—": "no-value",
    "-": "no-value",
}


def normalize_text(text: str) -> str:
    """Decode-independent typography: NFC, ASCII dashes and quotes, whitespace folded."""
    text = unicodedata.normalize("NFC", text)
    text = text.translate(DASHES).translate(QUOTES)
    return " ".join(text.split())


def is_number_token(token: str) -> bool:
    """Whether a whole token is one printed number, sign included."""
    return NUMBER_TOKEN.fullmatch(token) is not None


def number_unit(token: str) -> Decimal:
    """One unit of the token's last printed digit (`0.0500` -> 1e-4, `1e-06` -> 1e-6)."""
    try:
        exponent = Decimal(token).as_tuple().exponent
    except InvalidOperation:  # pragma: no cover - NUMBER_TOKEN already rejected everything else
        return Decimal(0)
    return Decimal(1).scaleb(int(exponent))


def numbers_equivalent(expected: str, actual: str, tolerance: Decimal) -> bool:
    """Two printed numbers under the last-printed-digit rule.

    The reference's printed precision is the contract, so one unit of *its* last digit is the
    tolerance; the produced side may print more digits of the same number (that is a formatting
    difference) but not fewer of a value that has them -- a fraction the reference printed and the
    produced side dropped is a real loss, not typography.
    """
    left = Decimal(expected)
    right = Decimal(actual)
    if left == right:
        return True
    if PURE_INTEGER.fullmatch(expected):
        if PURE_INTEGER.fullmatch(actual) or right == right.to_integral_value():
            # Both sides printed a whole number and they differ: no rounding latitude at all.
            return False
        # The reference rounded a fractional value to no decimals; one of its units is the latitude.
        return abs(left - right) <= tolerance * number_unit(expected)
    if PURE_INTEGER.fullmatch(actual):
        return False
    return abs(left - right) <= tolerance * max(number_unit(expected), number_unit(actual))


# The statistical leaves of a comparison, matched on the path `compare_json` actually receives: the
# slice name, `metrics/`, the member (its own name is dotted, hence the `.+`), the pair index and the
# field. Nothing else may match -- in particular no table cell, no `estimate`/`median`/`iqr` and no
# verdict string.
STATISTICAL_PATH = re.compile(
    r"verdict\.json:metrics/.+\.pairs\[\d+\]\."
    r"(p_value|holm_p_value|p_equivalence|holm_p_equivalence|ci95\[[01]\])$"
)

# The four p-values are `count/10000` and the interval edges are on the metric's own comparison scale
# (a ratio, or a difference in the metric's units, spanning 0.09 to 0.8 on the frozen tree), so they
# need two rules.
#
# The p-value bound is not the print granularity (1e-4, which is what the field is stored in but not
# how far it moves): a p-value is a `--resamples`-draw Monte-Carlo estimate doubled, so two generator
# sequences estimate the same probability with a difference whose measured standard deviation is
# 6.4e-3 over the frozen tree's 192 p-values, worst case 3.5e-2 (the RNG swap) and 3.0e-2 (the same
# binary re-run with the next seed, measured). An absolute 5e-2 is eight of those deviations and 1.4x
# the worst movement, so two estimates of one probability pass while a real change in the underlying
# probability -- which moves a p-value by a tenth or more -- does not. A narrower 1e-2 bound was tried
# and left 27 of the 75 moved leaves unexplained.
STATISTICAL_P_ABSOLUTE = Decimal("0.05")
STATISTICAL_P_VALUES = frozenset({"p_value", "holm_p_value", "p_equivalence", "holm_p_equivalence"})

# The interval edges get a relative tolerance with the same order of absolute floor, because their
# scale is the metric's, not the p-value's.
STATISTICAL_EDGE_ABSOLUTE = Decimal("0.01")
STATISTICAL_EDGE_RELATIVE = Decimal("0.01")


def statistical_tolerance(path: str, expected: str, actual: str) -> bool | None:
    """Whether a statistical leaf is equal under its own rule; `None` when the path is not one.

    Called before the last-printed-digit rule, which the resampling noise exceeds; see the module
    docstring for why these six leaves are the only ones that get it.
    """
    field = STATISTICAL_PATH.fullmatch(path)
    if field is None:
        return None
    left = Decimal(expected)
    right = Decimal(actual)
    if field.group(1) in STATISTICAL_P_VALUES:
        return abs(left - right) <= STATISTICAL_P_ABSOLUTE
    return abs(left - right) <= max(STATISTICAL_EDGE_ABSOLUTE, STATISTICAL_EDGE_RELATIVE * abs(left))


def text_tokens(text: str) -> list[str]:
    """Words, printed numbers and single characters, with the whitespace dropped.

    Dropping the whitespace is what makes `5 %` and `5%`, or a broken line and a joined one, the same
    text; keeping words and punctuation as separate tokens is what stops `no product` from becoming
    `noproduct`.
    """
    tokens: list[str] = []
    index = 0
    while index < len(text):
        char = text[index]
        if char.isspace():
            index += 1
            continue

        dotted = DOTTED_TOKEN.match(text, index) if char.isdigit() else None
        if dotted is not None:
            tokens.append(dotted.group())
            index = dotted.end()
            continue

        if char.isdigit() or (char == "." and index + 1 < len(text) and text[index + 1].isdigit()):
            match = NUMBER_BODY.match(text, index)
            tokens.append(match.group())
            index = match.end()
            continue

        if char in "+-":
            following = NUMBER_BODY.match(text, index + 1)
            previous = tokens[-1] if tokens else ""
            if following is not None and not is_number_token(previous) and WORD.fullmatch(previous) is None:
                tokens.append(char + following.group())
                index = following.end() + 1
                continue

        word = WORD.match(text, index)
        if word is not None:
            tokens.append(word.group())
            index = word.end()
            continue

        tokens.append(char)
        index += 1
    return tokens


def text_identical(expected: str, actual: str) -> bool:
    """Whether two texts are the same wording, whitespace and typography aside.

    This is the rule for the strings that are published verbatim -- the thresholds, the generator
    name, a path -- where a changed digit is a changed value rather than a reprinted one.
    """
    return text_tokens(normalize_text(expected)) == text_tokens(normalize_text(actual))


def text_equivalent(expected: str, actual: str, tolerance: Decimal) -> bool:
    """Whether two texts say the same thing: same words, same numbers within the tolerance."""
    left = text_tokens(normalize_text(expected))
    right = text_tokens(normalize_text(actual))
    if len(left) != len(right):
        return False
    for here, there in zip(left, right):
        if is_number_token(here) and is_number_token(there):
            if not numbers_equivalent(here, there, tolerance):
                return False
        elif here != there:
            return False
    return True


def marker_class(text: str) -> str | None:
    """The equivalence class of a marker cell, or None when the cell is not a marker."""
    return MARKERS.get(normalize_text(text).casefold())


EMPTY = "empty"
NA = "n/a"
NA_REASON = "n/a (reason)"
BOUND = "bound"
NUMBER = "number"
TEXT = "text"


def is_numeric_cell(text: str) -> bool:
    """A number, optionally with a unit, an interquartile range and a pass count."""
    if NUMBER_TOKEN.search(text) is None or DOTTED_TOKEN.search(text) is not None:
        return False
    residual = NUMBER_TOKEN.sub("", text)
    if "." in residual or ":" in residual:
        return False
    return all(word in UNIT_WORDS for word in WORD.findall(residual))


def cell_category(text: str) -> str:
    """Which kind of cell this is; two cells of different kinds are a structural difference."""
    stripped = text.strip()
    if not stripped:
        return EMPTY
    if stripped == "n/a":
        return NA
    if NA_REASON_PATTERN.fullmatch(stripped):
        return NA_REASON
    if BOUND_PATTERN.match(stripped):
        return BOUND
    if is_numeric_cell(stripped):
        return NUMBER
    return TEXT


def cells_equivalent(expected: str, actual: str, tolerance: Decimal) -> bool:
    """Whether two cells of the same category say the same thing."""
    left = expected.strip()
    right = actual.strip()
    here = marker_class(left)
    if here is not None or marker_class(right) is not None:
        return here is not None and here == marker_class(right)
    return text_equivalent(left, right, tolerance)


class Table:
    """One markdown table of `tables.md`: the sub-heading that names it, its header and its rows."""

    __slots__ = ("name", "header", "rows", "separator")

    def __init__(self, name: str, header: list[str], rows: list[list[str]], separator: bool) -> None:
        self.name = name
        self.header = header
        self.rows = rows
        self.separator = separator

    def render_header(self) -> str:
        return "| " + " | ".join(self.header) + " |"


def cells_of(line: str) -> list[str]:
    """The cells of one markdown table line, without the outer pipes."""
    parts = line.split("|")
    if parts and parts[0].strip() == "":
        parts = parts[1:]
    if parts and parts[-1].strip() == "":
        parts = parts[:-1]
    return [part.strip() for part in parts]


def is_separator(line: str) -> bool:
    """The `|---|---|` line under a table header."""
    stripped = line.strip()
    return stripped.startswith("|") and "-" in stripped and re.fullmatch(r"\|[\s:|-]*\|", stripped) is not None


def section_blocks(text: str) -> list[tuple[str, object]]:
    """A section cut into heading/paragraph/table blocks, with adjacent paragraphs merged.

    The `## N.` line itself is left out: the headings are asserted once for the whole file, so a
    changed title is reported once rather than once per slice.
    """
    blocks: list[tuple[str, object]] = []
    paragraphs: list[str] = []
    table_lines: list[str] = []
    table_name = ""
    tables_seen = 0

    def flush_text() -> None:
        if paragraphs:
            blocks.append(("text", " ".join(paragraphs)))
            paragraphs.clear()

    def flush_table() -> None:
        nonlocal tables_seen
        if table_lines:
            tables_seen += 1
            name = table_name if table_name else f"table{tables_seen}"
            blocks.append(("table", build_table(table_lines, name)))
            table_lines.clear()

    for line in text.split("\n"):
        if re.match(r"^#{3,6}\s", line):
            flush_text()
            flush_table()
            table_name = normalize_text(re.sub(r"^#+\s*", "", line))
            blocks.append(("heading", normalize_text(line)))
        elif line.startswith("|"):
            flush_text()
            table_lines.append(line)
        elif re.match(r"^##\s", line):
            flush_text()
            flush_table()
        elif line.strip():
            flush_table()
            paragraphs.append(line.strip())
        else:
            flush_table()

    flush_text()
    flush_table()

    merged: list[tuple[str, object]] = []
    for kind, body in blocks:
        if kind == "text" and merged and merged[-1][0] == "text":
            merged[-1] = ("text", f"{merged[-1][1]} {body}")
            continue
        merged.append((kind, body))
    return merged


def build_table(lines: list[str], name: str) -> Table:
    """One run of `|` lines as a table, with its separator row removed when it is there."""
    header = cells_of(lines[0])
    body = lines[1:]
    separator = bool(body) and is_separator(body[0])
    if separator:
        body = body[1:]
    return Table(name=name, header=header, rows=[cells_of(line) for line in body], separator=separator)


DIMENSION_COLUMNS = frozenset({
    "pass", "row", "arm", "run", "kind", "scope", "item", "metric", "metric (base arm)", "file",
    "label", "counter", "assertion", "dns port", "target clean",
})


def row_key_width(header: list[str], rows: list[list[str]]) -> int:
    """How many leading columns identify a row.

    The shortest leading run that is unique wins; it is capped by the leading run of dimension
    columns, so a table whose rows repeat (the reference does write the same row twice) is still
    keyed by its identity rather than by its whole text.
    """
    unique: int | None = None
    for width in range(1, len(header) + 1):
        keys = [tuple(row[:width]) for row in rows if len(row) >= width]
        if len(keys) == len(rows) and len(set(keys)) == len(keys):
            unique = width
            break

    dimensions = 0
    for index, name in enumerate(header):
        if normalize_text(name).casefold() in DIMENSION_COLUMNS:
            dimensions = index + 1
        else:
            break

    if unique is None:
        return dimensions or len(header)
    return min(unique, dimensions) if dimensions else unique


def row_label(header: list[str], width: int, row: list[str], index: int) -> str:
    """`[row=wf-aot-opt&arm=LAT]` for a keyed table, `[#12]` when a row has no usable key."""
    if width <= 0 or width > 4 or width >= len(header):
        return f"#{index}"
    parts = [f"{normalize_text(header[i])}={row[i]}" for i in range(width) if i < len(row)]
    label = "&".join(parts)
    return label if len(label) <= 120 else f"#{index}"


def row_differences(expected: list[str], actual: list[str], tolerance: Decimal) -> int:
    """How many cells of two rows disagree, for choosing the best partner when duplicates exist."""
    score = abs(len(expected) - len(actual))
    for here, there in zip(expected, actual):
        if cell_category(here) != cell_category(there) or not cells_equivalent(here, there, tolerance):
            score += 1
    return score


class VerdictNumber:
    """A JSON number that still knows how it was printed, because the tolerance is per print."""

    __slots__ = ("raw", "value")

    def __init__(self, raw: str) -> None:
        self.raw = raw
        self.value = Decimal(raw)

    def __repr__(self) -> str:  # pragma: no cover - diagnostics only
        return f"VerdictNumber({self.raw})"


def parse_document(text: str) -> object:
    """`verdict.json` parsed with its numbers kept as printed."""
    return json.loads(text, parse_float=VerdictNumber, parse_int=VerdictNumber)


def verdict_value_slices(document: dict) -> dict[str, object]:
    """`verdict.json` cut at its top-level keys, `metrics` one level further.

    A slice is the parsed subtree, so the comparison is over the keys and values themselves and the
    object key order inside a slice does not count (the semantic mode ignores it by construction; the
    byte mode re-serializes the slice, where it does).
    """
    slices: dict[str, object] = {}
    for key, value in document.items():
        if key == "metrics" and isinstance(value, dict):
            for metric, entry in value.items():
                slices[f"metrics/{metric}"] = {metric: entry}
            continue
        slices[key] = {key: value}
    return slices


def verdict_text_slices(text: str) -> dict[str, str]:
    """The byte mode's slices: the canonical re-serialization of each parsed subtree."""
    document = json.loads(text)
    slices: dict[str, str] = {}
    for key, value in document.items():
        if key == "metrics" and isinstance(value, dict):
            for metric, entry in value.items():
                slices[f"metrics/{metric}"] = json.dumps({metric: entry}, indent=2, ensure_ascii=False)
            continue
        slices[key] = json.dumps({key: value}, indent=2, ensure_ascii=False)
    return slices


def kind_of(value: object) -> str:
    """The JSON kind of a parsed value, with the printed number as its own kind."""
    if isinstance(value, VerdictNumber):
        return "number"
    if isinstance(value, dict):
        return "object"
    if isinstance(value, list):
        return "array"
    if isinstance(value, bool):
        return "bool"
    if value is None:
        return "null"
    return "string"


def type_name(value: object) -> str:
    """How a value is named in a difference report."""
    if isinstance(value, VerdictNumber):
        return value.raw
    if isinstance(value, dict):
        return f"{{{len(value)} key(s)}}"
    if isinstance(value, list):
        return f"[{len(value)} element(s)]"
    if value is None:
        return "null"
    if isinstance(value, bool):
        return "true" if value else "false"
    return str(value)


class Report:
    """The differences found, in the order they were found, with their categories."""

    STRUCTURE = "structure"
    VALUE = "value"
    MISSING = "missing"

    def __init__(self) -> None:
        self.entries: list[tuple[str, str, str, str]] = []

    def __len__(self) -> int:
        return len(self.entries)

    def structure(self, path: str, expected: object, actual: object) -> None:
        self.entries.append((self.STRUCTURE, path, str(expected), str(actual)))

    def value(self, path: str, expected: object, actual: object) -> None:
        self.entries.append((self.VALUE, path, str(expected), str(actual)))

    def missing(self, path: str, expected: object, actual: object = ABSENT) -> None:
        self.entries.append((self.MISSING, path, str(expected), str(actual)))

    def count(self, category: str) -> int:
        return sum(1 for entry in self.entries if entry[0] == category)

    def counts(self) -> str:
        return (
            f"{self.count(self.STRUCTURE)} structure, {self.count(self.VALUE)} value, "
            f"{self.count(self.MISSING)} missing"
        )

    def since(self, mark: int) -> list[tuple[str, str, str, str]]:
        return self.entries[mark:]

    def render(self, entries: list[tuple[str, str, str, str]]) -> None:
        for category, path, expected, actual in entries:
            print(f"   [{category}] {path}: expected {show(expected)} vs actual {show(actual)}")


def show(value: str) -> str:
    """One side of a difference, quoted, with a long value cut short and its length kept."""
    if value == ABSENT:
        return value
    text = value if len(value) <= 160 else f"{value[:157]}…"
    suffix = "" if text == value else f" ({len(value)} chars)"
    return f"{text!r}{suffix}"


class Comparer:
    """The semantic comparison: one report, one tolerance, no state between slices."""

    def __init__(self, report: Report, tolerance: Decimal) -> None:
        self.report = report
        self.tolerance = tolerance

    # -- tables.md ---------------------------------------------------------

    def compare_sections(self, section: str, expected: str, actual: str) -> None:
        """One `## N.` slice: its blocks, and inside a table its rows by identity."""
        left = section_blocks(expected)
        right = section_blocks(actual)
        if len(left) != len(right):
            self.report.structure(
                f"tables.md:{section}.blocks",
                f"{len(left)} block(s)",
                f"{len(right)} block(s)",
            )
        for index in range(min(len(left), len(right))):
            self.compare_blocks(section, index, left[index], right[index])

    def compare_blocks(
        self,
        section: str,
        index: int,
        expected: tuple[str, object],
        actual: tuple[str, object],
    ) -> None:
        kind, body = expected
        other_kind, other = actual
        if kind != other_kind:
            self.report.structure(f"tables.md:{section}.block[{index}]", kind, other_kind)
            return
        if kind == "heading":
            if normalize_text(str(body)) != normalize_text(str(other)):
                self.report.structure(f"tables.md:{section}.heading[{index}]", body, other)
            return
        if kind == "text":
            if not text_equivalent(str(body), str(other), self.tolerance):
                self.report.value(f"tables.md:{section}.prose[{index}]", body, other)
            return
        self.compare_tables(section, body, other)  # type: ignore[arg-type]

    def compare_tables(self, section: str, expected: Table, actual: Table) -> None:
        where = f"tables.md:{section}.{expected.name}"
        if [normalize_text(cell) for cell in expected.header] != [
            normalize_text(cell) for cell in actual.header
        ]:
            self.report.structure(f"{where}.header", expected.render_header(), actual.render_header())
        if not actual.separator:
            self.report.structure(f"{where}.separator", "| --- |", ABSENT)

        width = row_key_width(expected.header, expected.rows)
        groups: dict[tuple[str, ...], list[int]] = {}
        for index, row in enumerate(actual.rows):
            groups.setdefault(tuple(row[:width]), []).append(index)

        pairs: list[tuple[int, int]] = []
        missing: list[int] = []
        for index, row in enumerate(expected.rows):
            candidates = groups.get(tuple(row[:width]), [])
            if not candidates:
                missing.append(index)
                continue
            best = min(candidates, key=lambda other: row_differences(row, actual.rows[other], self.tolerance))
            pairs.append((index, best))
            groups[tuple(row[:width])].remove(best)

        for index, other in pairs:
            self.compare_rows(where, expected, actual.rows[other], index)
        for index in missing:
            label = row_label(expected.header, width, expected.rows[index], index)
            self.report.missing(
                f"{where}[{label}]",
                " | ".join(expected.rows[index])[:160],
            )
        for leftover in sorted(index for group in groups.values() for index in group):
            label = row_label(expected.header, width, actual.rows[leftover], leftover)
            self.report.structure(
                f"{where}[{label}]",
                ABSENT,
                " | ".join(actual.rows[leftover])[:160],
            )

    def compare_rows(self, where: str, table: Table, actual: list[str], index: int) -> None:
        expected = table.rows[index]
        label = row_label(table.header, row_key_width(table.header, table.rows), expected, index)
        if len(expected) != len(actual):
            self.report.structure(
                f"{where}[{label}].cells",
                f"{len(expected)} cell(s)",
                f"{len(actual)} cell(s)",
            )
        for column in range(min(len(expected), len(actual))):
            name = normalize_text(table.header[column]) if column < len(table.header) else f"#{column}"
            here = expected[column]
            there = actual[column]
            if cell_category(here) != cell_category(there):
                self.report.structure(
                    f"{where}[{label}].{name}",
                    f"{cell_category(here)}: {here}",
                    f"{cell_category(there)}: {there}",
                )
            elif not cells_equivalent(here, there, self.tolerance):
                self.report.value(f"{where}[{label}].{name}", here, there)

    # -- verdict.json ------------------------------------------------------

    def compare_pinned(self, slice_name: str, expected: object, actual: object) -> None:
        """The two flat parameter blocks: member set, value types and text, compared their own way."""
        path = f"verdict.json:{slice_name}"
        if not isinstance(expected, dict) or not isinstance(actual, dict):
            self.report.structure(path, kind_of(expected), kind_of(actual))
            return
        key = next(iter(expected))
        if key not in actual:
            self.report.missing(path, type_name(expected[key]))
            return
        if slice_name == "thresholds":
            self.compare_members(path, expected[key], actual[key], strings_only=True)
            return
        self.compare_bootstrap(path, expected[key], actual[key])

    def compare_bootstrap(self, path: str, expected: object, actual: object) -> None:
        """`bootstrap`: the same member keys, integers where integers are promised."""
        if not isinstance(expected, dict) or not isinstance(actual, dict):
            self.report.structure(path, kind_of(expected), kind_of(actual))
            return
        self.compare_members(path, expected, actual, strings_only=False, skip=BOOTSTRAP_INTEGERS)
        for member in BOOTSTRAP_INTEGERS:
            here = expected.get(member)
            there = actual.get(member)
            if not isinstance(here, VerdictNumber) or not isinstance(there, VerdictNumber):
                continue
            if PURE_INTEGER.fullmatch(there.raw) is None:
                self.report.structure(f"{path}.{member}", "integer", f"float {there.raw}")
            elif here.raw != there.raw:
                self.report.value(f"{path}.{member}", here.raw, there.raw)

    def compare_members(
        self,
        path: str,
        expected: dict,
        actual: dict,
        strings_only: bool,
        skip: tuple[str, ...] = (),
    ) -> None:
        """A pinned block's member set and, for the members that are strings, their text."""
        for member in expected:
            if member not in actual:
                self.report.missing(f"{path}.{member}", type_name(expected[member]))
                continue
            if member in skip:
                continue
            here = expected[member]
            there = actual[member]
            member_path = f"{path}.{member}"
            if isinstance(here, str) and isinstance(there, str):
                same = text_identical(here, there) if strings_only else text_equivalent(here, there, self.tolerance)
                if not same:
                    self.report.value(member_path, here, there)
            elif isinstance(here, VerdictNumber) and isinstance(there, VerdictNumber):
                if not numbers_equivalent(here.raw, there.raw, self.tolerance):
                    self.report.value(member_path, here.raw, there.raw)
            elif not isinstance(there, type(here)):
                self.report.structure(member_path, kind_of(here), kind_of(there))
        for member in actual:
            if member not in expected:
                self.report.structure(f"{path}.{member}", ABSENT, type_name(actual[member]))

    def compare_json(self, path: str, expected: object, actual: object, wording: bool = False) -> None:
        """One verdict slice, by structure: key order ignored, arrays in order, numbers tolerant."""
        here = kind_of(expected)
        there = kind_of(actual)
        if here != there:
            self.report.structure(path, here, there)
            return
        if here == "number":
            assert isinstance(expected, VerdictNumber) and isinstance(actual, VerdictNumber)
            statistical = statistical_tolerance(path, expected.raw, actual.raw)
            equal = (
                statistical
                if statistical is not None
                else numbers_equivalent(expected.raw, actual.raw, self.tolerance)
            )
            if not equal:
                self.report.value(path, expected.raw, actual.raw)
            return
        if here == "string":
            same = (
                text_equivalent(str(expected), str(actual), self.tolerance)
                if wording
                else text_identical(str(expected), str(actual))
            )
            if not same:
                self.report.value(path, expected, actual)
            return
        if here in ("bool", "null"):
            if expected != actual:
                self.report.value(path, type_name(expected), type_name(actual))
            return
        if here == "array":
            assert isinstance(expected, list) and isinstance(actual, list)
            if len(expected) != len(actual):
                self.report.structure(path, f"{len(expected)} element(s)", f"{len(actual)} element(s)")
            for index in range(min(len(expected), len(actual))):
                self.compare_json(f"{path}[{index}]", expected[index], actual[index], wording)
            return
        assert isinstance(expected, dict) and isinstance(actual, dict)
        for member in expected:
            if member not in actual:
                self.report.missing(f"{path}.{member}", type_name(expected[member]))
                continue
            self.compare_json(
                f"{path}.{member}", expected[member], actual[member], wording or member in WORDING_KEYS
            )
        for member in actual:
            if member not in expected:
                self.report.structure(f"{path}.{member}", ABSENT, type_name(actual[member]))

    def compare_slice(self, name: str, slice_name: str, expected: object, actual: object) -> None:
        """Compare one owned slice in semantic mode."""
        if name == "tables.md":
            self.compare_sections(slice_name, str(expected), str(actual))
            return
        if slice_name in PINNED_BLOCKS:
            self.compare_pinned(slice_name, expected, actual)
            return
        key = next(iter(expected)) if isinstance(expected, dict) else None
        if key is not None and isinstance(actual, dict) and key in actual:
            expected, actual = expected[key], actual[key]
        self.compare_json(f"verdict.json:{slice_name}", expected, actual)


def form_problems(name: str, data: bytes, report: Report) -> None:
    """The product's own form: BOM, line endings, and exactly one trailing newline."""
    if data.startswith(codecs.BOM_UTF8):
        report.structure(f"{name}:form", "UTF-8 without BOM", "UTF-8 with BOM")
    if b"\r" in data:
        report.structure(f"{name}:form", "'\\n' line endings", "'\\r' present")
    trailing = len(data) - len(data.rstrip(b"\n"))
    if trailing != 1:
        report.structure(f"{name}:form", "exactly one trailing '\\n'", f"{trailing} trailing '\\n'")


def read_artifact(path: Path, name: str, report: Report, check_form: bool) -> str | None:
    """The artifact's text, or None when the file is not there.

    The form is the *produced* side's obligation -- the frozen reference is the contract's own
    bytes -- so only that side is checked for its BOM, its line endings and its trailing newline.
    """
    if not path.is_file():
        return None
    data = path.read_bytes()
    if check_form:
        form_problems(name, data, report)
    try:
        return data.decode("utf-8-sig")
    except UnicodeDecodeError as error:
        fail(f"{path}: not valid UTF-8: {error}")


def check_headings(expected: str, actual: str, report: Report) -> None:
    """The 16 `## N.` headings: the same numbers, in the same order, with the same titles."""
    left = [(int(match.group(2)), normalize_text(match.group(3))) for match in HEADING_PATTERN.finditer(expected)]
    right = [(int(match.group(2)), normalize_text(match.group(3))) for match in HEADING_PATTERN.finditer(actual)]
    if len(left) != len(right) or [number for number, _ in left] != [number for number, _ in right]:
        report.structure(
            "tables.md:headings",
            " ".join(str(number) for number, _ in left),
            " ".join(str(number) for number, _ in right),
        )
    titles = {number: title for number, title in left}
    for number, title in right:
        if number in titles and titles[number] != title:
            report.structure(f"tables.md:{number}.title", f"## {number}. {titles[number]}", f"## {number}. {title}")
        elif number not in titles:
            report.structure(f"tables.md:{number}.title", ABSENT, f"## {number}. {title}")


def check_plots(produced: Path, report: Report) -> None:
    """`plots/SKIPPED.md` is written unconditionally and is the frozen text, byte for byte."""
    path = produced / "plots" / "SKIPPED.md"
    if not PLOTS.is_file():
        fail(f"{PLOTS}: the frozen plots text is missing; the produced plots/SKIPPED.md cannot be judged")
    actual = read_artifact(path, "plots/SKIPPED.md", report, check_form=True)
    if actual is None:
        report.missing("plots/SKIPPED.md", "the frozen text")
        return
    try:
        expected = PLOTS.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError) as error:
        fail(f"{PLOTS}: the frozen plots text is not readable UTF-8: {error}")
    if actual != expected:
        report.structure("plots/SKIPPED.md", expected, actual)


class Loaded:
    """One artifact: its raw text and, per mode, its slices."""

    __slots__ = ("text", "slices")

    def __init__(self, text: str, slices: dict[str, object]) -> None:
        self.text = text
        self.slices = slices


def load_artifact(directory: Path, name: str, reference: bool, mode: str, report: Report) -> Loaded | None:
    """The slices of one output file, or None when the file itself is missing.

    The frozen reference keeps its two files under the `py-` names (`py-tables.md`,
    `py-verdict.json`) so the directory itself says which implementation produced them; a produced
    directory holds the plain names both implementations write.
    """
    path = directory / (GOLDEN_NAMES[name] if reference else name)
    text = read_artifact(path, name, report, check_form=not reference)
    if text is None:
        return None
    if name == "tables.md":
        return Loaded(text, table_slices(text))
    try:
        if mode == "byte":
            return Loaded(text, verdict_text_slices(text))
        document = parse_document(text)
    except json.JSONDecodeError as error:
        fail(f"{path}: not valid JSON: {error}")
    if not isinstance(document, dict):
        fail(f"{path}: the top level is {type(document).__name__}, not an object")
    return Loaded(text, verdict_value_slices(document))


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


def check_full_key_sets(expected: Loaded, actual: Loaded, report: Report) -> None:
    """With every batch requested, the whole key surface is structural, not only the owned slices."""
    expected_document = parse_document(expected.text)
    actual_document = parse_document(actual.text)
    if not isinstance(expected_document, dict) or not isinstance(actual_document, dict):
        return
    for key in expected_document:
        if key not in actual_document:
            report.missing(f"verdict.json:{key}", type_name(expected_document[key]))
    for key in actual_document:
        if key not in expected_document:
            report.structure(f"verdict.json:{key}", ABSENT, type_name(actual_document[key]))
    for container in ("metrics",):
        here = expected_document.get(container)
        there = actual_document.get(container)
        if not isinstance(here, dict) or not isinstance(there, dict):
            continue
        for member in here:
            if member not in there:
                report.missing(f"verdict.json:{container}/{member}", type_name(here[member]))
        for member in there:
            if member not in here:
                report.structure(f"verdict.json:{container}/{member}", ABSENT, type_name(there[member]))


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--batch", action="append", default=None,
                        help="batch to compare (repeatable, comma separated; default: all)")
    parser.add_argument("--cs-out", type=Path, default=None,
                        help="compare this output directory instead of running the analyzer")
    parser.add_argument("--golden", type=Path, default=GOLDEN, help="the frozen reference directory")
    parser.add_argument("--mode", choices=("semantic", "byte"), default="semantic",
                        help="semantic compares meaning (default); byte compares slice text exactly")
    parser.add_argument("--byte", action="store_const", const="byte", dest="mode",
                        help="the short flag for --mode byte")
    parser.add_argument("--tolerance", type=Decimal, default=Decimal(1),
                        help="how many last-printed-digit units a number may differ by (default 1)")
    args = parser.parse_args(argv[1:])

    check_coverage(args.golden)
    requested = batches_of(args.batch[0].split(",") if args.batch else list(BATCH_SECTIONS))
    if args.cs_out is None:
        extract_tree()
        run_analyzer()
        produced = CS_OUT
    else:
        produced = args.cs_out

    report = Report()
    print(f"reference: {args.golden}")
    print(f"produced:  {produced}")
    print(f"mode:      {args.mode} (tolerance {args.tolerance} last-digit unit(s))")
    print(f"batches:   {', '.join(requested)}")

    reference_files = {
        name: load_artifact(args.golden, name, True, args.mode, report)
        for name in ("tables.md", "verdict.json")
    }
    produced_files = {
        name: load_artifact(produced, name, False, args.mode, report)
        for name in ("tables.md", "verdict.json")
    }
    check_plots(produced, report)
    if reference_files["tables.md"] and produced_files["tables.md"]:
        check_headings(reference_files["tables.md"].text, produced_files["tables.md"].text, report)
    if (
        len(requested) == len(BATCH_SECTIONS)
        and reference_files["verdict.json"] and produced_files["verdict.json"]
    ):
        check_full_key_sets(reference_files["verdict.json"], produced_files["verdict.json"], report)

    comparer = Comparer(report, args.tolerance)
    missing: list[str] = []
    differing: list[str] = []
    compared = 0
    for batch in requested:
        print(f"-- batch {batch}")
        for name in ("tables.md", "verdict.json"):
            wanted = BATCH_SECTIONS[batch][name]
            for slice_name in wanted:
                compared += 1
                if reference_files[name] is None:
                    missing.append(f"{name}:{slice_name}: the reference file is missing")
                    print(f"   {name}:{slice_name}: MISSING (reference file absent)")
                    continue
                if produced_files[name] is None:
                    missing.append(f"{name}:{slice_name}: the produced file is missing")
                    print(f"   {name}:{slice_name}: MISSING (produced file absent)")
                    continue
                reference = reference_files[name].slices.get(slice_name)
                actual = produced_files[name].slices.get(slice_name)
                if reference is None or actual is None:
                    missing.append(f"{name}:{slice_name}")
                    print(f"   {name}:{slice_name}: MISSING ({'reference' if reference is None else 'produced'} side)")
                    continue
                mark = len(report)
                if args.mode == "byte":
                    if reference == actual:
                        print(f"   {name}:{slice_name}: equal ({len(str(reference).splitlines())} line(s))")
                        continue
                    report.value(f"{name}:{slice_name}", reference, actual)
                else:
                    comparer.compare_slice(name, slice_name, reference, actual)
                found = report.since(mark)
                if not found:
                    print(f"   {name}:{slice_name}: equal")
                    continue
                differing.append(f"{name}:{slice_name}")
                if args.mode == "byte":
                    print(f"   {name}:{slice_name}: DIFFERS -- {first_difference(str(reference), str(actual))}")
                else:
                    print(f"   {name}:{slice_name}: DIFFERS ({len(found)} difference(s))")

    if report.entries:
        print(f"-- differences ({len(report.entries)})")
        report.render(report.entries)
    print(f"compared {compared} slice(s): {len(differing)} differ(ent), {len(missing)} missing")
    print(f"differences: {report.counts()}")
    if missing or report.count(Report.MISSING):
        print(f"rc=2: something that should exist does not ({len(missing)} slice(s), "
              f"{report.count(Report.MISSING)} difference(s))")
        return 2
    if differing or report.count(Report.STRUCTURE) or report.count(Report.VALUE):
        if differing:
            print(f"rc=1: every slice exists but {len(differing)} differ(s)")
        else:
            print("rc=1: every slice is equal but the file's own structure differs")
        return 1
    print("rc=0: every slice of this batch is equal")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
