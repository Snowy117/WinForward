#!/usr/bin/env python3
"""检查源文件的有效行数（去掉空行与整行注释）是否超过上限。

有效行的定义与 .trellis/spec/backend/cpp-conventions.md §4 一致：
去掉空行、以及整行都是注释的行之后剩下的行数。行尾注释不计入额外行。

只识别行注释（C# 与 C++ 的 //、PowerShell 的 #）。本仓库不使用块注释写文件头，
真要用的时候请在本脚本里补上 /* */ 的处理，否则块注释会被算成有效行。

用法：
    python3 src/userland/scripts/check-effective-lines.py [路径 ...]

不传路径时检查本批新增的全部源文件。有超限文件时退出码为 1。
"""

import pathlib
import sys

MAX_EFFECTIVE_LINES = 500

COMMENT_PREFIXES = {
    ".cs": ("//",),
    ".h": ("//",),
    ".cpp": ("//",),
    ".ps1": ("#",),
}

DEFAULT_TARGETS = (
    "src/userland",
    "tests/host/differential_fixture.h",
    "tests/host/differential_test.cpp",
)

SKIPPED_DIRECTORIES = {"bin", "obj", ".git"}


def effective_line_count(path: pathlib.Path, prefixes: tuple[str, ...]) -> int:
    count = 0
    for line in path.read_text(encoding="utf-8").splitlines():
        stripped = line.strip()
        if not stripped or stripped.startswith(prefixes):
            continue
        count += 1
    return count


def collect_sources(targets: list[str]) -> list[pathlib.Path]:
    sources: list[pathlib.Path] = []
    for target in targets:
        path = pathlib.Path(target)
        if path.is_file():
            sources.append(path)
            continue
        if not path.is_dir():
            raise SystemExit(f"路径不存在：{target}")
        for candidate in sorted(path.rglob("*")):
            if not candidate.is_file() or candidate.suffix not in COMMENT_PREFIXES:
                continue
            if SKIPPED_DIRECTORIES.intersection(candidate.parts):
                continue
            sources.append(candidate)
    return sources


def main(argv: list[str]) -> int:
    targets = argv if argv else list(DEFAULT_TARGETS)
    sources = collect_sources(targets)
    failures = 0
    for path in sources:
        count = effective_line_count(path, COMMENT_PREFIXES[path.suffix])
        if count > MAX_EFFECTIVE_LINES:
            failures += 1
            print(f"超限：{path} 有 {count} 行有效行，上限 {MAX_EFFECTIVE_LINES}")
        else:
            print(f"{count:4d}  {path}")

    if failures:
        print(f"失败：{failures} 个文件超过 {MAX_EFFECTIVE_LINES} 行")
        return 1

    print(f"通过：{len(sources)} 个文件均未超过 {MAX_EFFECTIVE_LINES} 行")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
