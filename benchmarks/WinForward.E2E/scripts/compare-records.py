#!/usr/bin/env python3
"""Compare two selftest artifact trees record by record.

Scope (design-decisions D7, D14.6): the ``<arm>.jsonl`` groups (which include ``run.json``) and
``ledger.jsonl`` contribute canonical paths and values; ``target.out`` is compared as normalized
text and never enters the path set.

The report has three columns:

1. structural -- paths present on one side only, JSON kind changes, array arity changes, and
   changes in normalized string values.
2. conditional -- the subset of the missing/extra paths that the normalize config declares
   conditional (``UseTcp``/``UseUdp`` arms, histograms that are omitted when empty, target-side
   source lists). Run-to-run repetition is not required for these, so they are listed apart and
   never fail the comparison.
3. numeric -- per-path numeric deltas. With ``--band`` a difference is a finding only when it
   leaves the recorded per-key jitter band; without ``--band`` the measured bands are printed
   and can be frozen with ``--write-band``. There is deliberately no global tolerance: a global
   one would be simultaneously too loose for counters and too tight for CPU/memory samples.

Usage:
    compare-records.py RUN1 RUN2 --normalize record-normalize.json [--band jitter-band.json]
                                 [--write-band jitter-band.json] [--json-out findings.json]

``RUN1``/``RUN2`` are the per-run artifact directories (the ones holding ``out/``,
``ledger.jsonl`` and ``target.out``).
"""

from __future__ import annotations

import argparse
import fnmatch
import json
import math
import re
import sys
from collections.abc import Iterable, Sequence
from pathlib import Path
from typing import NamedTuple

sys.path.insert(0, str(Path(__file__).resolve().parent))

import jsonl_paths  # noqa: E402  (the shared flattener must be importable next to this script)

COLUMN_STRUCTURAL = "structural"
COLUMN_CONDITIONAL = "conditional"
COLUMN_NUMERIC = "numeric"

GROUP_RECORDS = "records"
GROUP_LEDGER = "ledger"
GROUP_TEXT = "text"

VOLATILE_PLACEHOLDER = "<volatile>"
PATH_PLACEHOLDER = "<path>"

TIMESTAMP = re.compile(r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})?")
ABSOLUTE_PATH = re.compile(r"(?:/[\w.@+-]+)+")
ENDPOINT_PORT = re.compile(r":\d+$")
EMBEDDED_DECIMAL = re.compile(r"\d+\.\d+")


class Finding(NamedTuple):
    column: str
    where: str
    path: str
    detail: str


class Side:
    """One run's flattened groups, keyed ``"<group>/<file>"``."""

    def __init__(self, root: Path) -> None:
        self.root = root
        self.groups: dict[str, dict[str, list[jsonl_paths.Observation]]] = {}
        self.text: str | None = None

        records_dir = root / "out" if (root / "out").is_dir() else root
        for file in sorted(records_dir.iterdir()):
            if file.suffix not in (".json", ".jsonl") or not file.is_file():
                continue
            self.groups[f"{GROUP_RECORDS}/{file.name}"] = jsonl_paths.flatten_file(file)

        ledger = root / "ledger.jsonl"
        if ledger.is_file():
            self.groups[f"{GROUP_LEDGER}/{ledger.name}"] = jsonl_paths.flatten_file(ledger)

        target = root / "target.out"
        if target.is_file():
            self.text = target.read_text(encoding="utf-8-sig", errors="replace")


class Config:
    def __init__(self, document: dict[str, object]) -> None:
        self.volatile_key_names = set(document.get("volatileKeyNames", []))
        self.volatile_key_suffixes = tuple(document.get("volatileKeySuffixes", []))
        self.endpoint_key_names = set(document.get("endpointKeyNames", []))
        self.numeric_text_key_names = set(document.get("numericTextKeyNames", []))
        self.path_key_names = set(document.get("pathKeyNames", []))
        self.conditional_path_patterns = tuple(document.get("conditionalPathPatterns", []))
        self.volatile_arity_patterns = tuple(document.get("volatileArityPatterns", []))
        self.target_out_timestamp = bool(document.get("normalizeTargetOutTimestamps", True))
        self.target_out_paths = bool(document.get("normalizeTargetOutPaths", True))

    def is_volatile(self, key: str) -> bool:
        return key in self.volatile_key_names or key.endswith(self.volatile_key_suffixes)

    def is_path_key(self, key: str) -> bool:
        return key in self.path_key_names

    def is_conditional(self, path: str) -> bool:
        return any(fnmatch.fnmatch(path, pattern) for pattern in self.conditional_path_patterns)

    def has_volatile_arity(self, path: str) -> bool:
        return any(fnmatch.fnmatch(path, pattern) for pattern in self.volatile_arity_patterns)

    def normalize(self, path: str, observation: jsonl_paths.Observation) -> object:
        key = path.rsplit("/", 1)[-1]
        if self.is_path_key(key):
            return PATH_PLACEHOLDER
        if key in self.endpoint_key_names and isinstance(observation.value, str):
            return ENDPOINT_PORT.sub(":<port>", observation.value)
        if key in self.numeric_text_key_names and isinstance(observation.value, str):
            return EMBEDDED_DECIMAL.sub("<n>", observation.value)
        if self.is_volatile(key):
            return VOLATILE_PLACEHOLDER
        return observation.value

    def normalize_text(self, text: str) -> str:
        if self.target_out_timestamp:
            text = TIMESTAMP.sub("<ts>", text)
        if self.target_out_paths:
            text = ABSOLUTE_PATH.sub("<path>", text)
        return text


def numeric_stats(values: Sequence[float]) -> dict[str, float]:
    return {
        "count": float(len(values)),
        "min": float(min(values)),
        "max": float(max(values)),
        "mean": math.fsum(values) / len(values),
    }


def kind_set(observations: Iterable[jsonl_paths.Observation]) -> set[str]:
    return {observation.kind for observation in observations}


def arity_set(observations: Iterable[jsonl_paths.Observation]) -> set[int]:
    return {
        int(observation.value)  # type: ignore[arg-type]
        for observation in observations
        if observation.kind == jsonl_paths.KIND_ARRAY
    }


def string_values(
    observations: Iterable[jsonl_paths.Observation], config: Config, path: str
) -> set[object]:
    return {
        config.normalize(path, observation)
        for observation in observations
        if observation.kind == jsonl_paths.KIND_STRING
    }


def numeric_values(observations: Iterable[jsonl_paths.Observation]) -> list[float]:
    return [float(o.value) for o in observations if o.kind == jsonl_paths.KIND_NUMBER]  # type: ignore[arg-type]


def compare_group(
    group: str,
    base: dict[str, list[jsonl_paths.Observation]],
    after: dict[str, list[jsonl_paths.Observation]],
    config: Config,
    band: dict[str, dict[str, object]],
    findings: list[Finding],
    measured: dict[str, dict[str, object]],
) -> None:
    for path in sorted(set(base) - set(after)):
        column = COLUMN_CONDITIONAL if config.is_conditional(path) else COLUMN_STRUCTURAL
        findings.append(Finding(column, group, path, "only in base"))
    for path in sorted(set(after) - set(base)):
        column = COLUMN_CONDITIONAL if config.is_conditional(path) else COLUMN_STRUCTURAL
        findings.append(Finding(column, group, path, "only in after"))

    for path in sorted(set(base) & set(after)):
        where = f"{group}::{path}"
        base_kinds = kind_set(base[path])
        after_kinds = kind_set(after[path])
        if base_kinds != after_kinds:
            findings.append(
                Finding(
                    COLUMN_STRUCTURAL,
                    group,
                    path,
                    f"kind changed: {sorted(base_kinds)} -> {sorted(after_kinds)}",
                )
            )
            continue

        if jsonl_paths.KIND_ARRAY in base_kinds:
            base_arity = arity_set(base[path])
            after_arity = arity_set(after[path])
            if base_arity != after_arity:
                column = COLUMN_CONDITIONAL if config.has_volatile_arity(path) else COLUMN_STRUCTURAL
                findings.append(
                    Finding(column, group, path, f"arity changed: {sorted(base_arity)} -> {sorted(after_arity)}")
                )

        if jsonl_paths.KIND_STRING in base_kinds:
            base_strings = string_values(base[path], config, path)
            after_strings = string_values(after[path], config, path)
            if base_strings != after_strings:
                findings.append(
                    Finding(
                        COLUMN_STRUCTURAL,
                        group,
                        path,
                        f"string values differ: only-base={sorted(map(str, base_strings - after_strings))} "
                        f"only-after={sorted(map(str, after_strings - base_strings))}",
                    )
                )

        if jsonl_paths.KIND_NUMBER in base_kinds:
            base_numbers = numeric_values(base[path])
            after_numbers = numeric_values(after[path])
            if not base_numbers or not after_numbers:
                continue
            record_numeric(where, group, path, base_numbers, after_numbers, band, findings, measured)


def record_numeric(
    where: str,
    group: str,
    path: str,
    base_numbers: list[float],
    after_numbers: list[float],
    band: dict[str, dict[str, object]],
    findings: list[Finding],
    measured: dict[str, dict[str, object]],
) -> None:
    base_stats = numeric_stats(base_numbers)
    after_stats = numeric_stats(after_numbers)
    deltas = {stat: after_stats[stat] - base_stats[stat] for stat in base_stats}
    recorded = band.get(where)

    measurements = {
        "maxAbsDelta": max(abs(delta) for delta in deltas.values()),
        "maxRelDelta": max(
            abs(delta) / max(abs(base_stats[stat]), 1e-12) for stat, delta in deltas.items()
        ),
        "countDelta": deltas["count"],
        "nBase": len(base_numbers),
        "nAfter": len(after_numbers),
        "baseRange": [base_stats["min"], base_stats["max"]],
        "afterRange": [after_stats["min"], after_stats["max"]],
        "baseMean": base_stats["mean"],
        "afterMean": after_stats["mean"],
    }
    measured[where] = measurements

    if recorded is None:
        if band:
            findings.append(Finding(COLUMN_NUMERIC, group, path, "no recorded jitter band for this path"))
        return

    allowed = float(recorded.get("maxAbsDelta", 0.0))  # type: ignore[arg-type]
    allowed_count = float(recorded.get("allowedCountDelta", 1.0))  # type: ignore[arg-type]
    for stat, delta in deltas.items():
        tolerance = allowed_count if stat == "count" else allowed
        if abs(delta) > tolerance + 1e-9:
            findings.append(
                Finding(
                    COLUMN_NUMERIC,
                    group,
                    path,
                    f"{stat} delta {delta:g} exceeds jitter band {tolerance:g} "
                    f"(base {base_stats[stat]:g}, after {after_stats[stat]:g})",
                )
            )


def compare_text(base: Side, after: Side, config: Config, findings: list[Finding]) -> None:
    if base.text is None and after.text is None:
        return

    base_lines = config.normalize_text(base.text or "").splitlines()
    after_lines = config.normalize_text(after.text or "").splitlines()
    base_set = set(base_lines)
    after_set = set(after_lines)
    for line in sorted(base_set - after_set):
        findings.append(Finding(COLUMN_STRUCTURAL, GROUP_TEXT, "target.out", f"line only in base: {line!r}"))
    for line in sorted(after_set - base_set):
        findings.append(Finding(COLUMN_STRUCTURAL, GROUP_TEXT, "target.out", f"line only in after: {line!r}"))
    if base_set == after_set and base_lines != after_lines:
        findings.append(Finding(COLUMN_STRUCTURAL, GROUP_TEXT, "target.out", "normalized line order differs"))


def render(
    findings: list[Finding],
    measured: dict[str, dict[str, object]],
    banded: bool,
    show_bands: bool,
) -> str:
    lines: list[str] = []
    columns = (
        (COLUMN_STRUCTURAL, "1. structural (key set / kind / arity / string values)"),
        (COLUMN_CONDITIONAL, "2. conditional keys (UseTcp / UseUdp / non-empty histogram)"),
        (COLUMN_NUMERIC, "3. numeric (per-key jitter band)"),
    )
    for column, title in columns:
        rows = [finding for finding in findings if finding.column == column]
        lines.append(f"== {title} ==")
        if rows:
            lines.extend(f"  {row.where} {row.path}: {row.detail}" for row in rows)
        else:
            lines.append("  (none)")
        lines.append("")

    if banded:
        return "\n".join(lines)

    lines.append("== 3b. measured per-key jitter bands (freeze with --write-band) ==")
    if not measured:
        lines.append("  (no numeric paths)")
        lines.append("")
        return "\n".join(lines)

    exact = sum(1 for entry in measured.values() if float(entry["maxAbsDelta"]) == 0.0)  # type: ignore[arg-type]
    lines.append(
        f"  {len(measured)} numeric paths measured: {exact} identical in both runs, "
        f"{len(measured) - exact} moved between them (per-key bands, no global tolerance)"
    )
    ordered = sorted(measured.items(), key=lambda item: float(item[1]["maxAbsDelta"]), reverse=True)  # type: ignore[arg-type]
    shown = ordered if show_bands else ordered[:10]
    lines.append(f"  {'path':<70} {'n':>7} {'base range':>26} {'after range':>26} {'maxAbsDelta':>12}")
    for where, entry in shown:
        n = f"{entry['nBase']:g}/{entry['nAfter']:g}"
        base_range = f"[{entry['baseRange'][0]:g}, {entry['baseRange'][1]:g}]"  # type: ignore[index]
        after_range = f"[{entry['afterRange'][0]:g}, {entry['afterRange'][1]:g}]"  # type: ignore[index]
        lines.append(
            f"  {where:<70} {n:>7} {base_range:>26} {after_range:>26} {entry['maxAbsDelta']:>12g}"
        )
    if not show_bands and len(ordered) > len(shown):
        lines.append(f"  ... {len(ordered) - len(shown)} more (rerun with --show-bands for the full table)")
    lines.append("")
    return "\n".join(lines)


def main(argv: list[str]) -> int:
    jsonl_paths.require_python3()
    parser = argparse.ArgumentParser(description="Compare two selftest artifact trees.")
    parser.add_argument("base_dir", type=Path)
    parser.add_argument("after_dir", type=Path)
    parser.add_argument("--normalize", required=True, type=Path, help="normalization config JSON")
    parser.add_argument("--band", type=Path, help="recorded per-key jitter band to check against")
    parser.add_argument("--write-band", type=Path, help="write the jitter band measured from this pair")
    parser.add_argument("--json-out", type=Path, help="write the findings as JSON")
    parser.add_argument("--show-bands", action="store_true", help="print every measured per-key band")
    args = parser.parse_args(argv[1:])

    config = Config(json.loads(args.normalize.read_text(encoding="utf-8")))
    band: dict[str, dict[str, object]] = {}
    if args.band is not None:
        band = json.loads(args.band.read_text(encoding="utf-8")).get("paths", {})

    base = Side(args.base_dir)
    after = Side(args.after_dir)
    findings: list[Finding] = []
    measured: dict[str, dict[str, object]] = {}

    for group in sorted(set(base.groups) | set(after.groups)):
        compare_group(
            group,
            base.groups.get(group, {}),
            after.groups.get(group, {}),
            config,
            band,
            findings,
            measured,
        )

    compare_text(base, after, config, findings)

    if args.write_band is not None:
        paths: dict[str, dict[str, object]] = {}
        for where, entry in measured.items():
            paths[where] = {
                **entry,
                # Sample counts drift by one on a loaded host; the band for the count statistic is
                # the observed drift widened to one so a single extra/missing sample does not read
                # as a contract change, while any larger drift still does.
                "allowedCountDelta": max(abs(float(entry["countDelta"])), 1.0),  # type: ignore[arg-type]
            }
        args.write_band.write_text(
            json.dumps(
                {
                    "version": 1,
                    "stats": ["count", "min", "max", "mean"],
                    "scope": "per <group>::<canonical path>",
                    "base": str(args.base_dir),
                    "after": str(args.after_dir),
                    "paths": paths,
                },
                indent=2,
                sort_keys=True,
            )
            + "\n",
            encoding="utf-8",
        )

    print(f"base:  {args.base_dir}")
    print(f"after: {args.after_dir}")
    print(f"band:  {args.band if args.band is not None else '(measured only, not enforced)'}")
    print()
    print(render(findings, measured, banded=args.band is not None, show_bands=args.show_bands))

    structural = [f for f in findings if f.column == COLUMN_STRUCTURAL]
    conditional = [f for f in findings if f.column == COLUMN_CONDITIONAL]
    numeric = [f for f in findings if f.column == COLUMN_NUMERIC]
    print(
        f"summary: structural={len(structural)} conditional={len(conditional)} "
        f"numeric={len(numeric)} measured={len(measured)}"
    )

    if args.json_out is not None:
        args.json_out.write_text(
            json.dumps(
                {
                    "findings": [finding._asdict() for finding in findings],
                    "measured": measured,
                },
                indent=2,
                sort_keys=True,
            )
            + "\n",
            encoding="utf-8",
        )

    return 1 if structural or numeric else 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
