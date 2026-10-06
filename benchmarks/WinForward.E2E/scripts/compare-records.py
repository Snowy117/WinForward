#!/usr/bin/env python3
"""Compare two selftest artifact trees record by record, in the four classes of D15.

Scope (design-decisions D7, D14.6): the ``<arm>.jsonl`` groups (which include ``run.json``) and
``ledger.jsonl`` contribute canonical paths and values; ``target.out`` is compared as normalized
text and never enters the path set. ``jsonl_paths`` is the single alphabet, shared with the
contract inventory: two flatteners would make the inventory and the comparison disagree about what
a key is.

The D15 classes and their verdicts:

1. ``structural`` -- key set, JSON kind, array arity, normalized string values, ``target.out``
   text. A difference must be identical on both sides, or be declared conditional (``UseTcp`` /
   ``UseUdp`` / an omitted empty histogram) or declared in the rename table.
2. ``conditional`` -- the subset of the one-sided paths the normalize config declares conditional.
   Run-to-run repetition is not required for these, so they are listed apart and never fail.
3. ``identity`` -- the config's ``identityPathPatterns`` (``*/pid``, ``sources/port``, plan path
   and version/hash keys). Presence and JSON kind are checked, the value is never compared, so a
   new pid or ephemeral port is not a contract change.
4. ``contract`` -- everything numeric that is neither identity nor a declared reading, plus
   booleans. These are the counters, gates, declared parameters, deterministic ratios and
   thresholds: every one of them must land inside its *per-key* jitter band. There is deliberately
   no global tolerance (a global one would be simultaneously too loose for counters and too tight
   for CPU/memory samples), and ``allowedCountDelta`` defaults to **0** so a record-count drift has
   to be declared in the band file instead of being assumed. Booleans have no band: they must be
   equal. Without ``--band`` the contract values are measured and tabulated but not checked, which
   the header line and the summary both say; ``--write-band`` freezes such a measurement into a
   band file. A band file whose ``paths`` object is missing or empty is refused, because it would
   enforce nothing.
5. ``reading`` -- the config's ``readingPathPatterns``: clocks, CPU/memory/thread gauges,
   throughput, transfer volumes, latency-histogram readings. These are ``observed movement``:
   reported for information and **never a failure**, because a reading that did not move between
   two runs is the exception, not the contract. ``--strict`` lists every reading that moved; without
   it only the census is printed.

``gates/inFlightCeilingMs`` is the cross-class key D15 item 6 names: it is a ``gates.*`` counter by
shape and a millisecond measurement by meaning. It is classified by an explicit ``classOverrides``
entry, so the reading class comes from the config and not from a matching accident.

The reading declaration is exhaustive by construction: anything numeric that is not declared
identity or reading is a contract value (fail-closed), so a counter cannot be silenced by omission.
``--explain-classes`` prints which rule classified every observed numeric path.

``--rename-table`` applies D7 item 4's criterion on the same two path sets: every path present on
one side only must hit a ``renamed``/``added``/``removed`` entry, and every entry marked with the
batch named by ``--batch`` must be observed exactly once (its old spelling gone *and* its new
spelling present). That is the check B2 is judged by; entries of a batch that has not been executed
are reported as pending instead.

Usage:
    compare-records.py RUN1 RUN2 --normalize record-normalize.json [--band jitter-band.json]
                       [--write-band jitter-band.json] [--json-out findings.json]
                       [--rename-table contract-rename.json [--batch B2]]
                       [--strict] [--show-bands] [--explain-classes]

``RUN1``/``RUN2`` are the per-run artifact directories (the ones holding ``out/``,
``ledger.jsonl`` and ``target.out``); pointing at ``<run>/out`` works too and still finds the
ledger and the target log.
"""

from __future__ import annotations

import argparse
import fnmatch
import json
import math
import re
import sys
from collections import Counter
from collections.abc import Iterable, Sequence
from pathlib import Path
from typing import NamedTuple

sys.path.insert(0, str(Path(__file__).resolve().parent))

import jsonl_paths  # noqa: E402  (the shared flattener must be importable next to this script)

COLUMN_STRUCTURAL = "structural"
COLUMN_CONDITIONAL = "conditional"
COLUMN_IDENTITY = "identity"
COLUMN_CONTRACT = "contract"
COLUMN_READING = "reading"
COLUMN_DECLARED = "declared"
COLUMN_RENAME = "rename"

CLASS_IDENTITY = "identity"
CLASS_CONTRACT = "contract"
CLASS_READING = "reading"

GROUP_RECORDS = "records"
GROUP_LEDGER = "ledger"
GROUP_TEXT = "text"

VOLATILE_PLACEHOLDER = "<volatile>"

TIMESTAMP = re.compile(r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})?")
ABSOLUTE_PATH = re.compile(r"(?:/[\w.@+-]+)+")
ENDPOINT_PORT = re.compile(r":\d+$")
EMBEDDED_DECIMAL = re.compile(r"\d+\.\d+")


class Finding(NamedTuple):
    column: str
    where: str
    path: str
    detail: str


class Movement(NamedTuple):
    """One reading that moved between the two runs; informational, never a failure."""

    where: str
    max_abs_delta: float
    band: float | None
    n_base: float
    n_after: float
    base_mean: float
    after_mean: float


def resolve_run(root: Path) -> tuple[Path, Path]:
    """``(run root holding ledger.jsonl/target.out, directory holding the .json/.jsonl records)``.

    The ledger and the target log live next to ``out/``, so a caller who points at ``<run>/out``
    still gets all three artifact kinds instead of a comparison that reads the records only and
    calls the missing ledger "no difference".
    """
    if (root / "out").is_dir():
        return root, root / "out"
    if root.name == "out" and (root.parent / "ledger.jsonl").is_file():
        return root.parent, root
    return root, root


class Side:
    """One run's flattened groups, keyed ``"<group>/<file>"``."""

    def __init__(self, root: Path) -> None:
        self.root = root
        self.groups: dict[str, dict[str, list[jsonl_paths.Observation]]] = {}
        self.text: str | None = None

        run_root, records_dir = resolve_run(root)
        if not records_dir.is_dir():
            raise SystemExit(f"{root}: not a directory")

        ledger = next(
            (
                candidate
                for candidate in (run_root / "ledger.jsonl", records_dir / "ledger.jsonl")
                if candidate.is_file()
            ),
            None,
        )
        for file in sorted(records_dir.iterdir()):
            if file.suffix not in (".json", ".jsonl") or not file.is_file():
                continue
            # The ledger is exactly one group and never also a record file: when the records
            # directory is the run root, both labels would carry the same paths into the path set.
            if ledger is not None and file.resolve() == ledger.resolve():
                continue
            self.groups[f"{GROUP_RECORDS}/{file.name}"] = jsonl_paths.flatten_file(file)

        if ledger is not None:
            self.groups[f"{GROUP_LEDGER}/{ledger.name}"] = jsonl_paths.flatten_file(ledger)

        for candidate in (run_root / "target.out", records_dir / "target.out"):
            if candidate.is_file():
                self.text = candidate.read_text(encoding="utf-8-sig", errors="replace")
                break

        if not self.groups:
            raise SystemExit(
                f"{root}: no .json/.jsonl records and no ledger.jsonl; point at a run directory "
                "(or its out/) -- target.out on its own compares no paths"
            )

    def paths(self) -> set[str]:
        found: set[str] = set()
        for group in self.groups.values():
            found.update(group)
        return found


class Config:
    def __init__(self, document: dict[str, object]) -> None:
        self.volatile_key_names = set(document.get("volatileKeyNames", []))
        self.volatile_key_suffixes = tuple(document.get("volatileKeySuffixes", []))
        self.endpoint_key_names = set(document.get("endpointKeyNames", []))
        self.numeric_text_key_names = set(document.get("numericTextKeyNames", []))
        self.conditional_path_patterns = tuple(document.get("conditionalPathPatterns", []))
        self.volatile_arity_patterns = tuple(document.get("volatileArityPatterns", []))
        self.identity_path_patterns = tuple(document.get("identityPathPatterns", []))
        self.contract_path_patterns = tuple(document.get("contractPathPatterns", []))
        self.reading_path_patterns = tuple(document.get("readingPathPatterns", []))
        self.class_overrides = dict(document.get("classOverrides", {}))
        self.target_out_timestamp = bool(document.get("normalizeTargetOutTimestamps", True))
        self.target_out_paths = bool(document.get("normalizeTargetOutPaths", True))
        self._classes: dict[str, tuple[str, str]] = {}

    def is_volatile(self, key: str) -> bool:
        return key in self.volatile_key_names or key.endswith(self.volatile_key_suffixes)

    def is_conditional(self, path: str) -> bool:
        return any(fnmatch.fnmatch(path, pattern) for pattern in self.conditional_path_patterns)

    def has_volatile_arity(self, path: str) -> bool:
        return any(fnmatch.fnmatch(path, pattern) for pattern in self.volatile_arity_patterns)

    def classify(self, path: str) -> tuple[str, str]:
        """``(class, rule)`` for a numeric path: identity, contract or reading.

        The order is override, identity, contract, reading, contract-by-default. Identity and
        identity-vs-reading overlaps cannot happen silently: an exact override wins, and a path that
        matches both a contract and a reading pattern is a configuration error rather than a
        coin toss.
        """
        cached = self._classes.get(path)
        if cached is not None:
            return cached

        override = self.class_overrides.get(path)
        if override is not None:
            found = (str(override), f"classOverrides[{path}]")
        else:
            identity = match_pattern(path, self.identity_path_patterns)
            contract = match_pattern(path, self.contract_path_patterns)
            reading = match_pattern(path, self.reading_path_patterns)
            if contract is not None and reading is not None:
                raise SystemExit(
                    f"{path}: matches both contractPathPatterns ({contract}) and "
                    f"readingPathPatterns ({reading}); declare it in exactly one class"
                )
            if identity is not None:
                found = (CLASS_IDENTITY, f"identityPathPatterns:{identity}")
            elif contract is not None:
                found = (CLASS_CONTRACT, f"contractPathPatterns:{contract}")
            elif reading is not None:
                found = (CLASS_READING, f"readingPathPatterns:{reading}")
            else:
                found = (CLASS_CONTRACT, "default: numeric paths are contract unless declared a reading")

        self._classes[path] = found
        return found

    def normalize(self, path: str, observation: jsonl_paths.Observation) -> object:
        key = path.rsplit("/", 1)[-1]
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


def match_pattern(path: str, patterns: Sequence[str]) -> str | None:
    for pattern in patterns:
        if fnmatch.fnmatch(path, pattern):
            return pattern
    return None


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


def scalar_values(
    observations: Iterable[jsonl_paths.Observation], config: Config, path: str, kind: str
) -> set[object]:
    return {
        config.normalize(path, observation)
        for observation in observations
        if observation.kind == kind
    }


def numeric_values(observations: Iterable[jsonl_paths.Observation]) -> list[float]:
    return [float(o.value) for o in observations if o.kind == jsonl_paths.KIND_NUMBER]  # type: ignore[arg-type]


class Comparison:
    """Accumulates the four-class report for one pair of runs."""

    def __init__(
        self,
        config: Config,
        band: dict[str, dict[str, object]],
        declared_base: set[str] | None = None,
        declared_after: set[str] | None = None,
    ) -> None:
        self.config = config
        self.band = band
        self.declared_base = declared_base or set()
        self.declared_after = declared_after or set()
        self.findings: list[Finding] = []
        self.movements: list[Movement] = []
        self.measured: dict[str, dict[str, object]] = {}
        self.census: Counter[str] = Counter()
        self.reading_paths = 0
        self.compared = 0
        self.class_rules: dict[str, str] = {}
        self.numeric_paths: set[str] = set()

    def compare_group(
        self,
        group: str,
        base: dict[str, list[jsonl_paths.Observation]],
        after: dict[str, list[jsonl_paths.Observation]],
    ) -> None:
        config = self.config
        for path in sorted(set(base) - set(after)):
            self.findings.append(Finding(self.one_sided_column(path, self.declared_base), group, path, "only in base"))
        for path in sorted(set(after) - set(base)):
            self.findings.append(Finding(self.one_sided_column(path, self.declared_after), group, path, "only in after"))

        for path in sorted(set(base) & set(after)):
            self.compare_path(group, path, base[path], after[path])

    def one_sided_column(self, path: str, declared: set[str]) -> str:
        """A one-sided path is structural unless the rename table declares it (D15)."""
        if path in declared:
            return COLUMN_DECLARED
        return COLUMN_CONDITIONAL if self.config.is_conditional(path) else COLUMN_STRUCTURAL

    def compare_path(
        self,
        group: str,
        path: str,
        base_observations: list[jsonl_paths.Observation],
        after_observations: list[jsonl_paths.Observation],
    ) -> None:
        config = self.config
        self.compared += 1
        where = f"{group}::{path}"
        base_kinds = kind_set(base_observations)
        after_kinds = kind_set(after_observations)
        kind_changed = base_kinds != after_kinds
        identity_class, rule = config.classify(path)
        self.class_rules[path] = rule
        column = COLUMN_IDENTITY if identity_class == CLASS_IDENTITY else COLUMN_STRUCTURAL
        if kind_changed:
            self.findings.append(
                Finding(column, group, path, f"kind changed: {sorted(base_kinds)} -> {sorted(after_kinds)}")
            )
            return

        if jsonl_paths.KIND_ARRAY in base_kinds:
            base_arity = arity_set(base_observations)
            after_arity = arity_set(after_observations)
            if base_arity != after_arity:
                arity_column = COLUMN_CONDITIONAL if config.has_volatile_arity(path) else COLUMN_STRUCTURAL
                self.findings.append(
                    Finding(arity_column, group, path, f"arity changed: {sorted(base_arity)} -> {sorted(after_arity)}")
                )

        # An identity value is a different pid, port, plan path or version on every run by design.
        if identity_class == CLASS_IDENTITY:
            if jsonl_paths.KIND_NUMBER in base_kinds:
                self.numeric_paths.add(path)
                self.census[CLASS_IDENTITY] += 1
            return

        if jsonl_paths.KIND_STRING in base_kinds:
            base_strings = scalar_values(base_observations, config, path, jsonl_paths.KIND_STRING)
            after_strings = scalar_values(after_observations, config, path, jsonl_paths.KIND_STRING)
            if base_strings != after_strings:
                self.findings.append(
                    Finding(
                        COLUMN_STRUCTURAL,
                        group,
                        path,
                        f"string values differ: only-base={sorted(map(str, base_strings - after_strings))} "
                        f"only-after={sorted(map(str, after_strings - base_strings))}",
                    )
                )

        # A boolean has no band to fall into: a gate or a flag is equal or it is a contract change.
        if jsonl_paths.KIND_BOOL in base_kinds:
            base_bools = scalar_values(base_observations, config, path, jsonl_paths.KIND_BOOL)
            after_bools = scalar_values(after_observations, config, path, jsonl_paths.KIND_BOOL)
            if base_bools != after_bools:
                self.findings.append(
                    Finding(
                        COLUMN_CONTRACT,
                        group,
                        path,
                        f"boolean values differ: only-base={sorted(base_bools)} only-after={sorted(after_bools)}",
                    )
                )

        if jsonl_paths.KIND_NUMBER in base_kinds:
            self.numeric_paths.add(path)
            self.compare_numbers(where, group, path, base_observations, after_observations)

    def compare_numbers(
        self,
        where: str,
        group: str,
        path: str,
        base_observations: list[jsonl_paths.Observation],
        after_observations: list[jsonl_paths.Observation],
    ) -> None:
        reading_class, rule = self.config.classify(path)
        base_numbers = numeric_values(base_observations)
        after_numbers = numeric_values(after_observations)
        if not base_numbers or not after_numbers:
            return

        base_stats = numeric_stats(base_numbers)
        after_stats = numeric_stats(after_numbers)
        deltas = {stat: after_stats[stat] - base_stats[stat] for stat in base_stats}
        max_abs_delta = max(abs(delta) for delta in deltas.values())
        recorded = self.band.get(where)
        band_value = None if recorded is None else float(recorded.get("maxAbsDelta", 0.0))  # type: ignore[arg-type]
        self.measured[where] = {
            "class": reading_class,
            "rule": rule,
            "maxAbsDelta": max_abs_delta,
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
            "band": band_value,
            "allowedCountDelta": None if recorded is None else float(recorded.get("allowedCountDelta", 0.0)),  # type: ignore[arg-type]
        }
        self.census[reading_class] += 1

        if reading_class == CLASS_READING:
            self.reading_paths += 1
            if max_abs_delta != 0.0 or deltas["count"] != 0.0:
                self.movements.append(
                    Movement(
                        where,
                        max_abs_delta,
                        band_value,
                        base_stats["count"],
                        after_stats["count"],
                        base_stats["mean"],
                        after_stats["mean"],
                    )
                )
            return

        if reading_class != CLASS_CONTRACT:
            return

        if recorded is None:
            if self.band:
                self.findings.append(
                    Finding(COLUMN_CONTRACT, group, path, "no recorded jitter band for this path")
                )
            return

        allowed = float(recorded.get("maxAbsDelta", 0.0))  # type: ignore[arg-type]
        # A record-count drift is a contract change unless the band file declares a tolerance for it.
        allowed_count = float(recorded.get("allowedCountDelta", 0.0))  # type: ignore[arg-type]
        for stat, delta in deltas.items():
            tolerance = allowed_count if stat == "count" else allowed
            if abs(delta) > tolerance + 1e-9:
                self.findings.append(
                    Finding(
                        COLUMN_CONTRACT,
                        group,
                        path,
                        f"{stat} delta {delta:g} exceeds jitter band {tolerance:g} "
                        f"(base {base_stats[stat]:g}, after {after_stats[stat]:g})",
                    )
                )

    def compare_text(self, base: Side, after: Side) -> None:
        if base.text is None and after.text is None:
            return

        base_lines = self.config.normalize_text(base.text or "").splitlines()
        after_lines = self.config.normalize_text(after.text or "").splitlines()
        base_set = set(base_lines)
        after_set = set(after_lines)
        for line in sorted(base_set - after_set):
            self.findings.append(
                Finding(COLUMN_STRUCTURAL, GROUP_TEXT, "target.out", f"line only in base: {line!r}")
            )
        for line in sorted(after_set - base_set):
            self.findings.append(
                Finding(COLUMN_STRUCTURAL, GROUP_TEXT, "target.out", f"line only in after: {line!r}")
            )
        if base_set == after_set and base_lines != after_lines:
            self.findings.append(
                Finding(COLUMN_STRUCTURAL, GROUP_TEXT, "target.out", "normalized line order differs")
            )


def check_rename_table(
    base: Side,
    after: Side,
    table: list[dict[str, object]],
    batch: str | None,
    findings: list[Finding],
) -> list[str]:
    """Apply D7 item 4's criterion to the two path sets and return the report lines."""
    old_paths = base.paths()
    new_paths = after.paths()
    renamed_by_old: dict[str, dict[str, object]] = {}
    renamed_by_new: dict[str, dict[str, object]] = {}
    added_by_new: dict[str, dict[str, object]] = {}
    removed_by_old: dict[str, dict[str, object]] = {}
    counts: Counter[str] = Counter()
    for row in table:
        kind = str(row.get("kind", ""))
        counts[kind] += 1
        if kind == "renamed":
            old, new = str(row["old_path"]), str(row["new_path"])
            if old in renamed_by_old:
                raise SystemExit(f"rename table declares {old} twice")
            renamed_by_old[old] = row
            # Two spellings of one statistic converge on the same name, so several rows may share a
            # new_path; only the old side has to be unique.
            renamed_by_new.setdefault(new, row)
        elif kind == "added":
            added_by_new[str(row["new_path"])] = row
        elif kind == "removed":
            removed_by_old[str(row["old_path"])] = row
        elif kind != "identical":
            raise SystemExit(f"rename table has an unknown kind: {kind!r}")

    old_only = sorted(old_paths - new_paths)
    new_only = sorted(new_paths - old_paths)

    hit_old = 0
    for path in old_only:
        if path in renamed_by_old or path in removed_by_old:
            hit_old += 1
        else:
            findings.append(
                Finding(COLUMN_RENAME, "rename-table", path, "only in base and not declared by any entry")
            )
    hit_new = 0
    for path in new_only:
        if path in renamed_by_new or path in added_by_new:
            hit_new += 1
        else:
            findings.append(
                Finding(COLUMN_RENAME, "rename-table", path, "only in after and not declared by any entry")
            )

    landed: list[str] = []
    pending: list[str] = []
    vanished: list[str] = []
    pending_with_target: list[str] = []
    required = 0
    unsatisfied: list[str] = []
    for old, row in sorted(renamed_by_old.items()):
        new = str(row["new_path"])
        if old in new_paths:
            # The target spelling may already exist in the baseline for another arm (two spellings
            # converge on one name), so its presence is recorded, not read as the rename landing.
            state = "pending"
            pending.append(old)
            if new in new_paths:
                pending_with_target.append(old)
        elif new in new_paths:
            state = "landed"
            landed.append(old)
        else:
            state = "vanished"
            vanished.append(old)
            findings.append(
                Finding(
                    COLUMN_RENAME,
                    "rename-table",
                    old,
                    f"neither {old} nor {new} is published: the key disappeared without its replacement",
                )
            )

        for group in sorted(set(base.groups) & set(after.groups)):
            if old in after.groups[group] and new in after.groups[group]:
                findings.append(
                    Finding(
                        COLUMN_RENAME,
                        "rename-table",
                        old,
                        f"{group} publishes both spellings ({old} and {new}); one writer must emit one name",
                    )
                )

        if batch is not None and row.get("batch") == batch:
            required += 1
            if state != "landed":
                unsatisfied.append(old)
                findings.append(
                    Finding(
                        COLUMN_RENAME,
                        "rename-table",
                        old,
                        f"declared executed in batch {batch} but the rename is {state}: "
                        f"expected {old} gone and {new} present",
                    )
                )

    added_observed = sorted(path for path in added_by_new if path in new_paths)
    added_absent = sorted(path for path in added_by_new if path not in new_paths)
    if batch is not None:
        for path, row in sorted(added_by_new.items()):
            if row.get("batch") == batch and path not in new_paths:
                findings.append(
                    Finding(COLUMN_RENAME, "rename-table", path, f"declared added in batch {batch} but not observed")
                )
        for path, row in sorted(removed_by_old.items()):
            if row.get("batch") == batch and path in new_paths:
                findings.append(
                    Finding(COLUMN_RENAME, "rename-table", path, f"declared removed in batch {batch} but still published")
                )

    lines = [
        f"  table: {len(table)} entries = "
        + ", ".join(f"{kind} {count}" for kind, count in sorted(counts.items())),
        f"  path sets: base {len(old_paths)}, after {len(new_paths)}; "
        f"only in base {len(old_only)} (hit {hit_old}), only in after {len(new_only)} (hit {hit_new})",
        f"  renamed observed: landed {len(landed)}, pending {len(pending)} "
        f"({len(pending_with_target)} of them already publishing the target spelling), vanished {len(vanished)}",
        f"  added observed: {len(added_observed)}, not observed {len(added_absent)}",
    ]
    if batch is None:
        lines.append("  executed batch: (none declared; pass --batch NAME to require a batch to have landed)")
    else:
        lines.append(
            f"  executed batch: {batch}; {required} entr{'y' if required == 1 else 'ies'} required, "
            f"{required - len(unsatisfied)} satisfied, {len(unsatisfied)} not observed"
        )
    if pending:
        lines.append("  pending: " + ", ".join(f"{path} -> {renamed_by_old[path]['new_path']}" for path in pending))
    if vanished:
        lines.append("  vanished on both sides: " + ", ".join(vanished))
    if added_absent:
        lines.append("  declared additions not observed: " + ", ".join(added_absent))
    return lines


def render(comparison: Comparison, banded: bool, show_bands: bool, strict: bool, explain: bool) -> str:
    lines: list[str] = []
    columns = (
        (COLUMN_STRUCTURAL, "1. structural (key set / kind / arity / string values)"),
        (COLUMN_CONDITIONAL, "2. conditional keys (UseTcp / UseUdp / non-empty histogram)"),
        (COLUMN_IDENTITY, "3. identity (presence + JSON kind; the value is never compared)"),
        (COLUMN_CONTRACT, "4. contract (counters / gates / parameters / booleans; per-key jitter band)"),
    )
    for column, title in columns:
        rows = [finding for finding in comparison.findings if finding.column == column]
        lines.append(f"== {title} ==")
        if rows:
            lines.extend(f"  {row.where} {row.path}: {row.detail}" for row in rows)
        else:
            lines.append("  (none)")
        lines.append("")

    declared_rows = [finding for finding in comparison.findings if finding.column == COLUMN_DECLARED]
    if declared_rows:
        lines.append("== 5. declared key-set changes (licensed by the rename table; not a failure) ==")
        lines.extend(f"  {row.where} {row.path}: {row.detail}" for row in declared_rows)
        lines.append("")

    rename_rows = [finding for finding in comparison.findings if finding.column == COLUMN_RENAME]
    if rename_rows:
        lines.append("== 6. rename table (D7 item 4) ==")
        lines.extend(f"  {row.path}: {row.detail}" for row in rename_rows)
        lines.append("")

    readings = sorted(comparison.movements, key=lambda movement: movement.max_abs_delta, reverse=True)
    lines.append("== 7. readings (informational observed movement; never a failure) ==")
    if comparison.reading_paths == 0:
        lines.append("  (no reading paths)")
    else:
        lines.append(
            f"  {len(readings)} of {comparison.reading_paths} measured reading paths moved between the runs"
        )
        if not readings:
            lines.append("  (no reading moved at all)")
        elif strict:
            lines.append("  every movement, largest first:")
            for movement in readings:
                band = "no band" if movement.band is None else f"band {movement.band:g}"
                lines.append(
                    f"    {movement.where}: mean {movement.base_mean:g} -> {movement.after_mean:g}, "
                    f"max |delta| {movement.max_abs_delta:g} ({band}), n {movement.n_base:g}/{movement.n_after:g}"
                )
        else:
            lines.append("  (pass --strict to list every observed movement)")
    lines.append("")

    if explain:
        lines.append("== 8. path classification (the config rule that chose it) ==")
        for path in sorted(set(comparison.class_rules) & comparison.numeric_paths):
            reading_class, rule = comparison.config.classify(path)
            lines.append(f"  {reading_class:<8} {path:<55} {rule}")
        lines.append("")

    if banded:
        return "\n".join(lines)

    lines.append("== 3b. measured per-key jitter bands (freeze with --write-band) ==")
    if not comparison.measured:
        lines.append("  (no numeric paths)")
        lines.append("")
        return "\n".join(lines)

    exact = sum(1 for entry in comparison.measured.values() if float(entry["maxAbsDelta"]) == 0.0)  # type: ignore[arg-type]
    lines.append(
        f"  {len(comparison.measured)} numeric paths measured: {exact} identical in both runs, "
        f"{len(comparison.measured) - exact} moved between them (per-key bands, no global tolerance)"
    )
    ordered = sorted(
        comparison.measured.items(), key=lambda item: float(item[1]["maxAbsDelta"]), reverse=True  # type: ignore[arg-type]
    )
    shown = ordered if show_bands else ordered[:10]
    lines.append(
        f"  {'path':<58} {'class':<9} {'n':>7} {'base range':>26} {'after range':>26} {'maxAbsDelta':>12}"
    )
    for where, entry in shown:
        n = f"{entry['nBase']:g}/{entry['nAfter']:g}"
        base_range = f"[{entry['baseRange'][0]:g}, {entry['baseRange'][1]:g}]"  # type: ignore[index]
        after_range = f"[{entry['afterRange'][0]:g}, {entry['afterRange'][1]:g}]"  # type: ignore[index]
        lines.append(
            f"  {where:<58} {str(entry['class']):<9} {n:>7} {base_range:>26} {after_range:>26} "
            f"{entry['maxAbsDelta']:>12g}"
        )
    if not show_bands and len(ordered) > len(shown):
        lines.append(f"  ... {len(ordered) - len(shown)} more (rerun with --show-bands for the full table)")
    lines.append("")
    return "\n".join(lines)


def load_rename_table(path: Path) -> list[dict[str, object]]:
    document = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(document, list):
        raise SystemExit(f"{path}: the rename table must be a JSON array")
    return document


def main(argv: list[str]) -> int:
    jsonl_paths.require_python3()
    parser = argparse.ArgumentParser(description="Compare two selftest artifact trees.")
    parser.add_argument("base_dir", type=Path)
    parser.add_argument("after_dir", type=Path)
    parser.add_argument("--normalize", required=True, type=Path, help="normalization config JSON")
    parser.add_argument("--band", type=Path, help="recorded per-key jitter band to check against")
    parser.add_argument("--write-band", type=Path, help="write the jitter band measured from this pair")
    parser.add_argument("--json-out", type=Path, help="write the findings as JSON")
    parser.add_argument("--rename-table", type=Path, help="contract-rename.json, checked against the two path sets")
    parser.add_argument("--batch", help="the rename batch that has been executed; its entries must be observed exactly")
    parser.add_argument("--strict", action="store_true", help="list every reading that moved")
    parser.add_argument("--show-bands", action="store_true", help="print every measured per-key band")
    parser.add_argument("--explain-classes", action="store_true", help="print the rule that classified every numeric path")
    args = parser.parse_args(argv[1:])

    if args.batch is not None and args.rename_table is None:
        parser.error("--batch only means something together with --rename-table")

    config = Config(json.loads(args.normalize.read_text(encoding="utf-8")))
    band: dict[str, dict[str, object]] = {}
    if args.band is not None:
        document = json.loads(args.band.read_text(encoding="utf-8"))
        recorded_paths = document.get("paths") if isinstance(document, dict) else None
        # An empty band enforces nothing, so a mistyped or empty one is refused rather than honoured.
        if not isinstance(recorded_paths, dict) or not recorded_paths:
            raise SystemExit(
                f"{args.band}: not a non-empty per-key jitter band (its 'paths' object is missing or "
                "empty); freeze one with --write-band or drop --band"
            )
        band = recorded_paths

    base = Side(args.base_dir)
    after = Side(args.after_dir)

    table = load_rename_table(args.rename_table) if args.rename_table is not None else []
    declared_base = {str(row["old_path"]) for row in table if row.get("kind") in ("renamed", "removed")}
    declared_after = {str(row["new_path"]) for row in table if row.get("kind") in ("renamed", "added")}
    comparison = Comparison(config, band, declared_base, declared_after)

    for group in sorted(set(base.groups) | set(after.groups)):
        comparison.compare_group(group, base.groups.get(group, {}), after.groups.get(group, {}))

    comparison.compare_text(base, after)

    rename_lines: list[str] = []
    if args.rename_table is not None:
        rename_lines = check_rename_table(base, after, table, args.batch, comparison.findings)

    if args.write_band is not None:
        paths: dict[str, dict[str, object]] = {}
        for where, entry in comparison.measured.items():
            if entry["class"] != CLASS_CONTRACT:
                continue
            paths[where] = {
                **{key: value for key, value in entry.items() if key not in ("class", "rule", "band", "allowedCountDelta")},
                # Sample counts drift by one on a loaded host, so the observed drift is widened to
                # one and declared here rather than assumed by the reader.
                "allowedCountDelta": max(abs(float(entry["countDelta"])), 1.0),  # type: ignore[arg-type]
            }
        args.write_band.write_text(
            json.dumps(
                {
                    "version": 2,
                    "stats": ["count", "min", "max", "mean"],
                    "scope": "per <group>::<canonical path>, contract class only",
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
    print(render(comparison, banded=args.band is not None, show_bands=args.show_bands, strict=args.strict, explain=args.explain_classes))

    if rename_lines:
        print("== rename table check ==")
        print("\n".join(rename_lines))
        print()

    structural = [f for f in comparison.findings if f.column == COLUMN_STRUCTURAL]
    declared = [f for f in comparison.findings if f.column == COLUMN_DECLARED]
    conditional = [f for f in comparison.findings if f.column == COLUMN_CONDITIONAL]
    identity = [f for f in comparison.findings if f.column == COLUMN_IDENTITY]
    contract = [f for f in comparison.findings if f.column == COLUMN_CONTRACT]
    rename = [f for f in comparison.findings if f.column == COLUMN_RENAME]
    contract_column = f"contract={len(contract)}" + ("" if comparison.band else " (not checked: no --band)")
    print(
        f"summary: structural={len(structural)} conditional={len(conditional)} identity={len(identity)} "
        f"declared={len(declared)} "
        f"{contract_column} rename={len(rename)} readings={len(comparison.movements)}"
        f"/{comparison.reading_paths} compared={comparison.compared} measured={len(comparison.measured)} "
        f"classes(contract={comparison.census[CLASS_CONTRACT]} reading={comparison.census[CLASS_READING]} "
        f"identity={comparison.census[CLASS_IDENTITY]})"
    )

    if args.json_out is not None:
        args.json_out.write_text(
            json.dumps(
                {
                    "findings": [finding._asdict() for finding in comparison.findings],
                    "measurements": comparison.measured,
                    "readings": [movement._asdict() for movement in comparison.movements],
                    "census": dict(comparison.census),
                },
                indent=2,
                sort_keys=True,
            )
            + "\n",
            encoding="utf-8",
        )

    failing = bool(structural or identity or contract or rename)
    return 1 if failing else 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
