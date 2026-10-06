#!/usr/bin/env python3
"""Flatten JSON / JSONL records into canonical paths.

Path alphabet (design-decisions D7 item 4, D14.6): a canonical path is ``"/".join(segments)``
where each segment is the literal member name, so a dot inside a key is *never* split --
``metrics/tcp.sentOk`` names the member ``tcp.sentOk`` of ``metrics``, not a nested ``tcp``.
An array contributes its own path exactly once and its elements are flattened under that same
path (no ``[i]`` segments); how many elements there were is recorded as the array's arity.

This module is the single flattener shared by the contract inventory and by
``compare-records.py``: two alphabets would make the inventory and the comparison disagree
about what a key is.

Usage:
    python3 jsonl_paths.py FILE [FILE ...]      # sorted unique paths, one per line
    python3 jsonl_paths.py --json FILE [...]    # {"FILE": [path, ...]}
"""

from __future__ import annotations

import argparse
import json
import sys
from collections.abc import Iterable, Iterator, Mapping, Sequence
from pathlib import Path
from typing import NamedTuple

KIND_OBJECT = "object"
KIND_ARRAY = "array"
KIND_NULL = "null"
KIND_STRING = "string"
KIND_NUMBER = "number"
KIND_BOOL = "boolean"


class Observation(NamedTuple):
    """One occurrence of a path in one record.

    ``value`` is the scalar for scalar kinds, the element count for ``array`` and ``None`` for
    ``object`` and ``null``. The same path can be observed many times in one group (one per
    record, or one per array element), which is why flattening returns a list per path.
    """

    kind: str
    value: object


def require_python3() -> None:
    """Refuse to run on an interpreter older than the one the evidence was produced with.

    The baseline recipe is deliberately dependency-free: it runs with the ``python3`` already on
    PATH (direnv provides it) and must never silently fall back to ``nix-shell``, which would
    make the evidence depend on a toolchain the reader cannot reproduce.
    """
    if sys.version_info >= (3, 10):
        return

    sys.stderr.write(
        f"jsonl_paths: python 3.10 or newer is required, this interpreter is {sys.version.split()[0]} "
        f"({sys.executable}); the baseline recipe does not fall back to nix-shell\n"
    )
    raise SystemExit(2)


def canonical_path(segments: Iterable[str]) -> str:
    """Join literal member names with ``/``; keys keep their dots."""
    return "/".join(segments)


def kind_of(value: object) -> str:
    """JSON kind of a scalar, with bool checked before int (bool is an int subclass)."""
    if value is None:
        return KIND_NULL
    if isinstance(value, bool):
        return KIND_BOOL
    if isinstance(value, (int, float)):
        return KIND_NUMBER
    if isinstance(value, str):
        return KIND_STRING
    if isinstance(value, Mapping):
        return KIND_OBJECT
    if isinstance(value, Sequence):
        return KIND_ARRAY
    raise TypeError(f"not a JSON value: {type(value).__name__}")


def flatten(document: object) -> dict[str, list[Observation]]:
    """Flatten one JSON document into ``{canonical_path: [Observation, ...]}`` in document order."""
    observations: dict[str, list[Observation]] = {}
    _walk(document, (), observations)
    return observations


def _walk(node: object, segments: tuple[str, ...], observations: dict[str, list[Observation]]) -> None:
    if isinstance(node, Mapping):
        _record(observations, segments, KIND_OBJECT, None)
        for key, value in node.items():
            _walk(value, (*segments, str(key)), observations)
        return

    if isinstance(node, Sequence) and not isinstance(node, (str, bytes)):
        _record(observations, segments, KIND_ARRAY, len(node))
        for item in node:
            _walk(item, segments, observations)
        return

    _record(observations, segments, kind_of(node), node)


def _record(
    observations: dict[str, list[Observation]],
    segments: tuple[str, ...],
    kind: str,
    value: object,
) -> None:
    # The document root is not a member of anything, so it has no path of its own.
    if not segments:
        return

    observations.setdefault(canonical_path(segments), []).append(Observation(kind, value))


def paths(document: object) -> set[str]:
    return set(flatten(document))


def read_jsonl(path: Path) -> list[object]:
    """Parse a JSONL file, skipping blank lines and rejecting malformed ones.

    A malformed line is an error, never a skip: silently dropping it would hide exactly the
    half-written record a comparison is supposed to notice.
    """
    records: list[object] = []
    with path.open(encoding="utf-8-sig") as handle:
        for number, line in enumerate(handle, start=1):
            if not line.strip():
                continue
            try:
                records.append(json.loads(line))
            except json.JSONDecodeError as error:
                raise ValueError(f"{path}:{number}: not valid JSON: {error}") from error
    return records


def read_document(path: Path) -> object:
    """Parse a whole-file JSON document (``run.json``) or a JSONL stream, by suffix."""
    if path.suffix == ".jsonl":
        return read_jsonl(path)

    with path.open(encoding="utf-8-sig") as handle:
        return json.load(handle)


def merge(observations: dict[str, list[Observation]], document: object) -> None:
    for path, found in flatten(document).items():
        observations.setdefault(path, []).extend(found)


def flatten_file(path: Path) -> dict[str, list[Observation]]:
    """Flatten one file: a JSON document (``.json``) or every record of a JSONL stream."""
    observations: dict[str, list[Observation]] = {}
    if path.suffix == ".jsonl":
        for record in read_jsonl(path):
            merge(observations, record)
    else:
        merge(observations, read_document(path))
    return observations


def iter_paths(path: Path) -> Iterator[tuple[str, Observation]]:
    for canonical, found in flatten_file(path).items():
        for observation in found:
            yield canonical, observation


def main(argv: list[str]) -> int:
    require_python3()
    parser = argparse.ArgumentParser(description="Flatten JSON/JSONL records into canonical paths.")
    parser.add_argument("files", nargs="+", type=Path)
    parser.add_argument("--json", action="store_true", help="emit {file: [path, ...]} instead of plain lines")
    args = parser.parse_args(argv[1:])

    if args.json:
        print(json.dumps({str(file): sorted(flatten_file(file)) for file in args.files}, indent=2))
        return 0

    for file in args.files:
        for canonical in sorted(flatten_file(file)):
            print(canonical)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
