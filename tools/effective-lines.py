#!/usr/bin/env python3
"""Report the C# files whose *effective* line count is above the repository's file-length limit.

The rule (``.trellis/spec/backend/directory-structure.md``) is 400 effective lines per ``.cs`` file,
where an effective line is a line that carries code: blank lines do not count, a line that is only a
``//`` comment does not count, and neither does a line inside a ``/* ... */`` block. A line that
carries code *and* a trailing comment does count, because the code on it is real; so does every line
of a multi-line string literal, because those lines are part of a statement.

The scanner reads comments and string literals the way the compiler does rather than by prefix: a
``//`` inside a string is a string, and a ``/*`` inside one does not open a block. That is what keeps
the count stable when a line of prose happens to spell a comment marker; the counts this script
reports for the tree are the counts the rule has always meant.

A path may be a directory (walked recursively for ``*.cs``) or one file. Build outputs under ``bin``
and ``obj`` are never counted: they are generated copies, not sources. The exit status is 1 when at
least one file is over the limit, so the script can stand as the gate itself.

Usage, from the repository root:
    python3 tools/effective-lines.py benchmarks/WinForward.E2E tests/WinForward.E2E.Tests
    python3 tools/effective-lines.py --all benchmarks/WinForward.E2E
    python3 tools/effective-lines.py --limit 200 benchmarks/WinForward.E2E/Client
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

DEFAULT_LIMIT = 400
SKIPPED_DIRECTORIES = frozenset({"bin", "obj"})


def is_string_start(source: str, index: int) -> tuple[int, bool, int] | None:
    """Describe the string literal at ``index``, or return ``None`` when there is none.

    The answer is ``(end, verbatim, quotes)``: where the literal ends, whether an ``@`` prefix makes
    it verbatim (a doubled quote is content, not the end), and how many quotes open a raw literal
    (zero for a regular or verbatim one).
    """
    position = index
    while position < len(source) and source[position] in "$@":
        position += 1

    if not source.startswith('"', position):
        return None

    quotes = 0
    while position + quotes < len(source) and source[position + quotes] == '"':
        quotes += 1

    verbatim = "@" in source[index:position]
    if quotes >= 3:
        return raw_string_end(source, position, quotes), verbatim, quotes
    if verbatim:
        return verbatim_string_end(source, position), True, 0

    return regular_string_end(source, position), False, 0


def regular_string_end(source: str, open_quote: int) -> int:
    """The index just past a regular (or interpolated) literal, ``\\`` escapes included."""
    position = open_quote + 1
    while position < len(source):
        if source[position] == "\\":
            position += 2
            continue

        if source[position] == '"':
            return position + 1

        position += 1

    return len(source)


def verbatim_string_end(source: str, open_quote: int) -> int:
    """The index just past a verbatim literal, where ``""`` is one content quote."""
    position = open_quote + 1
    while position < len(source):
        if source[position] != '"':
            position += 1
            continue

        if position + 1 < len(source) and source[position + 1] == '"':
            position += 2
            continue

        return position + 1

    return len(source)


def raw_string_end(source: str, open_quote: int, quotes: int) -> int:
    """The index just past a raw literal, which closes on a run of at least ``quotes`` quotes."""
    position = open_quote + quotes
    while position < len(source):
        if source[position] != '"':
            position += 1
            continue

        run = 0
        while position + run < len(source) and source[position + run] == '"':
            run += 1

        if run >= quotes:
            return position + run

        position += run

    return len(source)


def character_end(source: str, open_quote: int) -> int:
    """The index just past a character literal, ``'\\''`` included."""
    position = open_quote + 1
    while position < len(source):
        if source[position] == "\\":
            position += 2
            continue

        if source[position] == "'":
            return position + 1

        position += 1

    return len(source)


def blank_comments(source: str) -> str:
    """Return ``source`` with every comment replaced by spaces, newlines and literals untouched.

    Blanking rather than deleting is what keeps the line count and the columns honest: a comment that
    covers whole lines leaves those lines blank, and a line of code with a trailing comment keeps its
    code. String literals are copied through whole, so a line of a multi-line raw literal is still a
    line a statement occupies and counts as effective exactly as its own text reads.
    """
    out: list[str] = []
    position = 0
    while position < len(source):
        character = source[position]
        if source.startswith("//", position):
            newline = source.find("\n", position)
            end = len(source) if newline < 0 else newline
            out.append(" " * (end - position))
            position = end
            continue

        if source.startswith("/*", position):
            close = source.find("*/", position + 2)
            end = len(source) if close < 0 else close + 2
            out.append("".join(character if character == "\n" else " " for character in source[position:end]))
            position = end
            continue

        string = is_string_start(source, position)
        if string is not None:
            end, _, _ = string
            out.append(source[position:end])
            position = end
            continue

        if character == "'":
            end = character_end(source, position)
            out.append(source[position:end])
            position = end
            continue

        out.append(character)
        position += 1

    return "".join(out)


def effective_line_count(path: Path) -> int:
    """Count the lines of ``path`` that carry code."""
    return sum(1 for line in blank_comments(path.read_text(encoding="utf-8")).splitlines() if line.strip())


def source_files(path: Path) -> list[Path]:
    """Every ``.cs`` file under ``path``, a directory walked recursively and build outputs skipped."""
    if path.is_file():
        return [path]

    return sorted(
        candidate
        for candidate in path.rglob("*.cs")
        if not SKIPPED_DIRECTORIES.intersection(candidate.parts)
    )


def display(path: Path) -> str:
    """The path as the shell named it: relative to the working directory when it is below it."""
    try:
        return str(path.relative_to(Path.cwd()))
    except ValueError:
        return str(path)


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("paths", nargs="+", type=Path, help="a directory to walk or one .cs file")
    parser.add_argument("--limit", type=int, default=DEFAULT_LIMIT, help=f"effective-line limit (default {DEFAULT_LIMIT})")
    parser.add_argument("--all", action="store_true", help="print every file, not only the ones over the limit")
    args = parser.parse_args(argv[1:])

    counts: list[tuple[int, Path]] = []
    missing = False
    for path in args.paths:
        if not path.exists():
            print(f"effective-lines: '{path}' does not exist", file=sys.stderr)
            missing = True
            continue

        counts.extend((effective_line_count(file), file) for file in source_files(path))

    if missing:
        return 2

    over = [(count, file) for count, file in counts if count > args.limit]
    shown = counts if args.all else over
    for count, file in sorted(shown, key=lambda entry: (-entry[0], str(entry[1]))):
        print(f"{count:5d}  {display(file)}")

    return 1 if over else 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
