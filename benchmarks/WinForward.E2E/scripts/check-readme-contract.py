#!/usr/bin/env python3
"""Check that the harness README's contract table names keys the code actually publishes.

The README's "Which keys are contract" section is the document every reader trusts when a number
looks wrong, and it is the one section a rename can silently falsify: a stale spelling there reads
as a key the analysis reads, while the record no longer carries it and the report quietly renders an
empty cell. This script turns that risk into a gate.

It reads three things:

* ``benchmarks/WinForward.E2E/README.md`` -- its "Which keys are contract" section: the table under
  it and the paragraphs around it;
* ``benchmarks/WinForward.E2E.Contracts/ArmKeys.*.cs`` -- one ``const string`` per published key,
  nested so that the class chain spells the key's own path (``ArmKeys.Common.Gates.ClientSendLoss``
  is ``gates/clientSendLoss``);
* ``research/contract-rename.json`` -- the rename registry, whose ``old_path`` entries are spellings
  the README must no longer use.

Every key path the section names must then satisfy three checks, and so must the five root names the
table spells bare: a matching constant exists, that constant is referenced by a write site (the
harness or the ``Contracts`` writers, never a test and never a commented-out call), and the spelling
is not an ``old_path`` of the rename table. A single-segment name that is neither a path nor one of
those five is outside the check: run.json's own keys are named in the section but not read here.

Usage:
    python3 benchmarks/WinForward.E2E/scripts/check-readme-contract.py
    python3 benchmarks/WinForward.E2E/scripts/check-readme-contract.py --readme path/to/README.md

Exit codes: ``0`` every key resolved; ``1`` at least one key is stale, unwritten or unspellable;
``2`` an input could not be read, the declaration set is empty, or the section names no key.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from collections.abc import Iterator
from dataclasses import dataclass
from pathlib import Path
from typing import NoReturn

HARNESS_DIR = Path(__file__).resolve().parent.parent
REPO_DIR = HARNESS_DIR.parent.parent
CONTRACTS_DIR = HARNESS_DIR.parent / "WinForward.E2E.Contracts"
RENAME_TABLE = REPO_DIR / ".trellis/tasks/10-07-e2e-harness-refactor/research/contract-rename.json"

KEYS_GLOB = "ArmKeys.*.cs"

SECTION_HEADING = "## Which keys are contract"

WRITE_ROOTS = (
    HARNESS_DIR / "Cli",
    HARNESS_DIR / "Client",
    HARNESS_DIR / "Target",
    HARNESS_DIR / "Wire",
    HARNESS_DIR / "Program.cs",
    CONTRACTS_DIR,
)

#: The class chain of `ArmKeys` (below the outer class) mapped to the JSON path it declares. A path
#: ending in `<histogram>` is the shared leaf set of the four latency histograms. `None` marks a
#: class that only declares member names rather than a path of its own.
CLASS_PATHS: dict[str, str | None] = {
    "Common.Record": "",
    "Common.Gates": "gates",
    "Common.Parameters": "parameters",
    "Common.ArmSummary": "armSummary",
    "Common.ErrorRecord": "error",
    "Common.LatencyRecord": "latency",
    "Common.LatencyRecord.Histogram": "latency/<histogram>",
    "Latency": "metrics",
    "Loss": "metrics",
    "Reliability": "metrics",
    "Reliability.Outcomes": "metrics/outcomes",
    "Reliability.Expected": "metrics/expected",
    "Reliability.OutcomeNames": None,
    "Reliability.ModeNames": None,
    "Reliability.Mode": "metrics/byMode/<mode>",
    "Reliability.Attempt": "attempt",
    "Throughput": "metrics",
    "Dns": "metrics",
    "Mix": "metrics",
    "Mix.ClassNames": None,
    "Mix.PageClass": "metrics/classes/page",
    "Mix.BulkClass": "metrics/classes/bulk",
    "Mix.DnsClass": "metrics/classes/dns",
    "Mix.UdpClass": "metrics/classes/udp",
    "Mix.DesktopLane": "metrics/desktops",
    "Persistent": "metrics",
    "Idle": "metrics",
    "Control": "metrics",
    "Run": "",
    "Run.Arm": "arms",
    "Run.TargetObject": "target",
    "Sample": "",
    "Sample.Counters": "",
    "Sample.ProcessEntry": "processes",
    "Sample.SamplerError": "samplerError",
    "Ledger.Envelope": "",
    "Ledger.TcpRecord": "tcp",
    "Ledger.TcpSummary": "tcpSummary",
    "Ledger.UdpSummary": "udpSummary",
    "Ledger.UdpSummary.SourceEntry": "sources",
    "Ledger.DnsSummary": "dnsSummary",
    "Ledger.TargetSummary": "targetSummary",
    "Ledger.TargetSummary.TcpTotals": "targetSummary/tcp",
    "Ledger.TargetSummary.UdpTotals": "targetSummary/udp",
    "Ledger.TargetSummary.DnsTotals": "targetSummary/dns",
    "Ledger.ErrorRecord": "error",
    "Ledger.VerdictNames": None,
}

CLASS_RE = re.compile(r"\b(?:partial\s+)?class\s+(\w+)")
CONST_RE = re.compile(r'\bconst\s+string\s+(\w+)\s*=\s*"([^"]*)"')
BACKTICK_RE = re.compile(r"`([^`]+)`")
KEY_RE = re.compile(r"[A-Za-z][A-Za-z0-9_.\-/\[\]{}<>]*")
FILE_RE = re.compile(r"\.(py|sh|ps1|cs|json|jsonl|md|exe|dll|txt)$")

NON_KEY_TOKENS = frozenset({
    "[]", "…", "...", "n/a", "null", "true", "false", "JSON", "JSONL", "UTC",
})

#: The one spelling the table quotes on purpose: a legacy fallback the analysis still accepts, with
#: no current writer. Kept explicit so dropping it is a deliberate edit rather than a silent pass.
LEGACY_TOKENS = frozenset({"metrics/clientSendLoss"})

#: The two plan-key spellings the section documents as carrying nothing: no record publishes either,
#: and the analysis reads neither, so naming them is a statement about the plan's own keys rather than
#: about a path. Declared rather than inferred so dropping them is a deliberate edit.
DOCUMENTED_NON_KEYS = frozenset({"parameters/window", "parameters/loss.lossWindowMs"})


@dataclass(frozen=True)
class Constant:
    """One `const string` declaration: the path it publishes and the member that holds the value."""

    path: str
    reference: str


def fail(message: str) -> NoReturn:
    """Exit 2: the checker's own inputs are unreadable or the tree no longer matches its model."""
    print(message, file=sys.stderr)
    raise SystemExit(2)


def read_text(path: Path) -> str:
    """The file's text, or an exit 2 naming the path that could not be read."""
    try:
        return path.read_text(encoding="utf-8")
    except OSError as error:
        fail(f"cannot read {path}: {error}")


def contract_section(readme: Path) -> str:
    """The README's contract-table section, up to the next top-level heading."""
    text = read_text(readme)
    start = text.find(SECTION_HEADING)
    if start < 0:
        fail(f"{readme}: no '{SECTION_HEADING}' heading")
    rest = text[start + len(SECTION_HEADING):]
    end = rest.find("\n## ")
    return rest if end < 0 else rest[:end]

def declarations(source: str) -> Iterator[tuple[str, str, str]]:
    """Every `const string` in one shard, as `(dotted class chain, member name, value)`."""
    scopes: list[str | None] = []
    pending: str | None = None
    for line in source.splitlines():
        code = line.split("//", 1)[0]
        class_match = CLASS_RE.search(code)
        if class_match:
            pending = class_match.group(1)
        const_match = CONST_RE.search(code)
        if const_match:
            chain = ".".join(name for name in scopes if name and name != "ArmKeys")
            if chain:
                yield chain, const_match.group(1), const_match.group(2)
        for character in code:
            if character == "{":
                scopes.append(pending)
                pending = None
            elif character == "}" and scopes:
                scopes.pop()


def key_constants() -> dict[str, list[Constant]]:
    """Every declared key's canonical path mapped to the constants that declare it.

    A path can have several declaring constants when two arms publish the same spelling
    (`metrics/udp.sent` is the latency, dns and mix roll-up key), so the caller asks whether *any*
    of them has a write site rather than pinning one declaration.
    """
    constants: dict[str, list[Constant]] = {}
    for shard in sorted(CONTRACTS_DIR.glob(KEYS_GLOB)):
        for chain, member, value in declarations(read_text(shard)):
            if chain not in CLASS_PATHS:
                fail(f"{shard}: class chain '{chain}' has no declared path (add it to CLASS_PATHS)")
            prefix = CLASS_PATHS[chain]
            if prefix is None:
                continue
            path = value if prefix == "" else f"{prefix}/{value}"
            constants.setdefault(path, []).append(Constant(path, f"ArmKeys.{chain}.{member}"))
    return constants


def write_sites() -> str:
    """Every `.cs` file of the write roots, concatenated, with declarations and comments left out."""
    parts: list[str] = []
    for root in WRITE_ROOTS:
        files = sorted(root.rglob("*.cs")) if root.is_dir() else [root]
        for path in files:
            if "bin" in path.parts or "obj" in path.parts or path.name.startswith("ArmKeys."):
                continue
            source = read_text(path)
            parts.append("\n".join(line.split("//", 1)[0] for line in source.splitlines()))
    return "\n".join(parts)


def section_tokens(section: str) -> list[str]:
    """The key-shaped tokens the section names, in file order, tables and prose alike."""
    tokens: list[str] = []
    for token in BACKTICK_RE.findall(section):
        token = token.strip()
        if token in NON_KEY_TOKENS or token.endswith(("...", "…")) or FILE_RE.search(token):
            continue
        if "/" not in token and token not in {"type", "arm", "kind", "label", "notes"}:
            continue
        if KEY_RE.fullmatch(token) is None:
            continue
        tokens.append(token.removesuffix("[]"))
    return tokens


def resolve(token: str, constants: dict[str, list[Constant]]) -> list[Constant] | None:
    """The constants a table token names, or `None` when nothing declares it.

    Three shapes are accepted beyond a direct hit. A ``<name>`` placeholder is a container
    (``metrics/outcomes/<name>``), a trailing slash included. A ``metrics/latency/…``,
    ``metrics/loss/…``, ``parameters/latency/…`` or ``parameters/loss/…`` token is one phase of a
    ``BASE`` record, whose keys are the phase arm's own one level down. Anything else is a container
    path only if some declared key sits under it.
    """
    if token in constants:
        return constants[token]
    for container in ("metrics", "parameters"):
        for phase in ("latency", "loss"):
            prefix = f"{container}/{phase}/"
            if token.startswith(prefix) and f"{container}/{token[len(prefix):]}" in constants:
                return constants[f"{container}/{token[len(prefix):]}"]
    if "<" in token:
        holder = token.split("<", 1)[0].rstrip("/")
        if holder in constants:
            return constants[holder]
        candidates = [path for path in constants if path.startswith(f"{holder}/")]
        return constants[candidates[0]] if candidates else None
    return None


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--readme", type=Path, default=HARNESS_DIR / "README.md",
                        help="the README whose contract table is checked")
    arguments = parser.parse_args()

    constants = key_constants()
    if not constants:
        fail(f"{CONTRACTS_DIR}: no '{KEYS_GLOB}' declaration found")
    written = write_sites()
    rename = json.loads(read_text(RENAME_TABLE))
    old_paths = {entry["old_path"]: entry["new_path"] for entry in rename
                 if entry.get("old_path") and entry["old_path"] != entry["new_path"]}

    failures: list[str] = []
    tokens = section_tokens(contract_section(arguments.readme))
    if not tokens:
        fail(f"{arguments.readme}: the '{SECTION_HEADING}' section names no key")
    for token in tokens:
        if token in LEGACY_TOKENS:
            print(f"note: {token} is a declared legacy fallback, not a current writer's key")
            continue
        if token in DOCUMENTED_NON_KEYS:
            print(f"note: {token} is a documented non-key; the record carries a readable spelling instead")
            continue
        if token in old_paths:
            failures.append(f"{token}: renamed to {old_paths[token]}")
            continue
        declared = resolve(token, constants)
        if declared is None:
            if not any(path.startswith(f"{token}/") for path in constants):
                failures.append(f"{token}: no ArmKeys constant declares that path")
        elif not any(constant.reference in written for constant in declared):
            references = ", ".join(constant.reference for constant in declared)
            failures.append(f"{token}: {references} declared but no write site references any of them")

    for failure in failures:
        print(f"FAIL: {failure}", file=sys.stderr)
    print(f"{len(tokens)} key(s) checked against {len(constants)} declared constant path(s): "
          f"{'ok' if not failures else f'{len(failures)} failure(s)'}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
