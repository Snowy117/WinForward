#!/usr/bin/env python3
"""Check that every path pattern in a normalize config is a shape the run actually publishes.

``record-normalize.json`` classifies numeric paths by pattern (D15), so a pattern that matches no
observed path declares a shape the artifacts never carry: a reading that is in fact being compared as
a contract value, or an array arity that cannot vary. Dead patterns are therefore reported and fail
the check (D16.2); the fix is to write the real spelling or to delete the pattern, never to leave it
in place "just in case".

The two classes are checked against the domains they classify in:

* ``readingPathPatterns`` against every observed path -- a reading pattern that matches nothing is
  dead wherever it is used.
* ``volatileArityPatterns`` against the observed **array** paths only, which is the only place
  compare-records.py consults them.

``identityPathPatterns``, ``contractPathPatterns`` and ``conditionalPathPatterns`` are printed for
information and never fail: a conditional pattern legitimately matches nothing when the plan does
not run the arm that publishes it (``metrics/loss/*`` in a run without a base arm).

Usage:
    normalize-pattern-hits.py --normalize FILE [--run DIR ...]

At least one ``--run`` is required; the run directory is the one holding ``out/`` (or the ``out/``
directory itself), the same argument compare-records.py takes.
"""

from __future__ import annotations

import argparse
import fnmatch
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import jsonl_paths  # noqa: E402  (the shared alphabet must be importable next to this script)

FAILING_CLASSES = ("readingPathPatterns", "volatileArityPatterns")
INFORMATIONAL_CLASSES = ("identityPathPatterns", "contractPathPatterns", "conditionalPathPatterns")


def observed(run: Path) -> tuple[set[str], set[str]]:
    """``(every path, the paths whose observations include an array)`` for one run directory.

    The scope is compare-records.py's: the ``.json``/``.jsonl`` files of the record directory plus
    ``ledger.jsonl``, which lives next to ``out/``. ``target.out`` is text and never enters the path
    set, so a directory passed as ``<run>/out`` still finds the ledger beside it.
    """
    records = run / "out" if (run / "out").is_dir() else run
    if not records.is_dir():
        raise SystemExit(f"{run}: not a directory")

    files = [file for file in sorted(records.rglob("*")) if file.suffix in (".json", ".jsonl") and file.is_file()]
    ledger = next(
        (candidate for candidate in (run / "ledger.jsonl", records / "ledger.jsonl") if candidate.is_file()),
        None,
    )
    if ledger is not None and all(file.resolve() != ledger.resolve() for file in files):
        files.append(ledger)

    paths: set[str] = set()
    arrays: set[str] = set()
    for file in files:
        for path, observations in jsonl_paths.flatten_file(file).items():
            paths.add(path)
            if any(observation.kind == jsonl_paths.KIND_ARRAY for observation in observations):
                arrays.add(path)
    return paths, arrays


def hits(pattern: str, paths: set[str]) -> list[str]:
    return sorted(path for path in paths if fnmatch.fnmatch(path, pattern))


def main(argv: list[str]) -> int:
    jsonl_paths.require_python3()
    parser = argparse.ArgumentParser(description="Report normalize patterns that match nothing.")
    parser.add_argument("--normalize", required=True, type=Path, help="normalization config JSON")
    parser.add_argument("--run", required=True, action="append", type=Path, help="run directory (repeatable)")
    args = parser.parse_args(argv[1:])

    config = json.loads(args.normalize.read_text(encoding="utf-8"))
    dead = 0
    for run in args.run:
        paths, arrays = observed(run)
        print(f"{run}: {len(paths)} observed path(s), {len(arrays)} of them arrays")
        for key in FAILING_CLASSES:
            domain = arrays if key == "volatileArityPatterns" else paths
            for pattern in config.get(key, []):
                found = hits(pattern, domain)
                if not found:
                    print(f"  DEAD {key}: {pattern!r} matches no observed {'array' if domain is arrays else 'path'}")
                    dead += 1
        for key in INFORMATIONAL_CLASSES:
            for pattern in config.get(key, []):
                if not hits(pattern, paths):
                    print(f"  note {key}: {pattern!r} matches nothing in this run (informational)")

    print(f"dead patterns: {dead}")
    return 1 if dead else 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
