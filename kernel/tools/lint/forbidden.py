#!/usr/bin/env python3
"""禁止模式检查：把 `cpp-conventions.md` §5.1 的禁止清单变成会失败的自动检查。

规则来源是同一目录下的 `forbidden-rules.txt`：每行
`规则ID <TAB> glob <TAB> 正则 <TAB> 说明`，`#` 开头为注释。匹配前先剥离注释与
字符串，所以注释与字符串字面量里的字样不会误报。

正则里可以出现命名组 `hit`：一旦出现，只有它捕获到的匹配才算命中，别的分支用来
精确排除合法写法（例如 `<new>`、placement new、`operator new`、`= delete`）。

豁免只认原始行里的 `// WF-LINT-ALLOW(规则ID): 理由`，写在命中行或它的上一行。
理由为空、规则 ID 不存在都算无效豁免——无效豁免本身就会报错，不会把命中放过去。

退出码：0 没有发现；1 有发现；2 环境或参数问题（规则文件坏了、git 不可用、
显式列出的文件读不到）。
"""

from __future__ import annotations

import argparse
import bisect
import fnmatch
import re
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

SOURCE_SUFFIXES = frozenset(
    {".c", ".cc", ".cpp", ".cxx", ".h", ".hh", ".hpp", ".hxx", ".inl"}
)

ALLOW_MARK = "WF-LINT-ALLOW"
ALLOW_RE = re.compile(r"//\s*" + ALLOW_MARK + r"\(([^)]*)\)\s*:(.*)\Z")
RULE_ID_RE = re.compile(r"[a-z][a-z0-9_-]*")


class EnvironmentProblem(Exception):
    """退出码 2：规则文件、git、文件系统等环境问题。"""


@dataclass(frozen=True)
class Rule:
    rule_id: str
    globs: tuple[str, ...]
    regex: re.Pattern[str]
    description: str

    def applies_to(self, path: str) -> bool:
        return any(fnmatch.fnmatchcase(path, pattern) for pattern in self.globs)


@dataclass(frozen=True)
class Finding:
    path: str
    line: int
    rule_id: str
    message: str


def is_identifier_character(character: str) -> bool:
    return character.isalnum() or character == "_"


def continued_by_backslash(text: str, newline: int) -> bool:
    """行尾是不是反斜杠续行（`\\` 紧贴在换行前，中间允许一个 `\\r`）。"""
    position = newline - 1
    if position >= 0 and text[position] == "\r":
        position -= 1
    return position >= 0 and text[position] == "\\"


def raw_literal_start(text: str, quote: int) -> int | None:
    """`quote` 指向字面量的起始 '"'；是原始字符串时返回整段字面量的起点。

    原始字符串的前缀是 `R`、`u8R`、`uR`、`UR`、`LR`，并且 `R` 前面不能再贴着
    标识符字符——`modelR"(x)"` 里那个 `R` 只是标识符的尾巴，不是原始字符串。
    """
    if quote < 1 or text[quote - 1] != "R":
        return None
    start = quote - 1
    if start >= 2 and text[start - 1] == "8" and text[start - 2] == "u":
        start -= 2
    elif start >= 1 and text[start - 1] in "LuU":
        start -= 1
    if start >= 1 and is_identifier_character(text[start - 1]):
        return None
    return start


def raw_literal_end(text: str, quote: int, length: int) -> int:
    opening = text.find("(", quote + 1)
    if opening < 0:
        return length
    delimiter = text[quote + 1 : opening]
    terminator = ")" + delimiter + '"'
    end = text.find(terminator, opening + 1)
    return length if end < 0 else end + len(terminator)


def strip_comments_and_strings(text: str) -> tuple[str, list[tuple[int, int]]]:
    """把注释与字符串的内容替换成空格，保留换行与长度。

    返回 (掩码文本, 行注释区间列表)。区间只记录以 `//` 开头的注释，豁免语法就写在
    这种注释里；块注释与字符串都被抹掉，但仍保留换行，所以行号对得上。行尾反斜杠
    续行按 C++ 翻译阶段 2 处理：下一行也属于同一条注释。
    """
    characters = list(text)
    comment_spans: list[tuple[int, int]] = []
    length = len(text)

    def blank(start: int, end: int) -> None:
        for position in range(start, end):
            if characters[position] != "\n":
                characters[position] = " "

    index = 0
    while index < length:
        character = text[index]
        if character == "/" and index + 1 < length and text[index + 1] == "/":
            end = text.find("\n", index)
            while end >= 0 and continued_by_backslash(text, end):
                end = text.find("\n", end + 1)
            if end < 0:
                end = length
            comment_spans.append((index, end))
            blank(index, end)
            index = end
        elif character == "/" and index + 1 < length and text[index + 1] == "*":
            end = text.find("*/", index + 2)
            end = length if end < 0 else end + 2
            blank(index, end)
            index = end
        elif character in "\"'":
            quote = character
            if quote == '"':
                raw_start = raw_literal_start(text, index)
                if raw_start is not None:
                    end = raw_literal_end(text, index, length)
                    blank(raw_start, end)
                    index = end
                    continue
            cursor = index + 1
            closed = False
            while cursor < length:
                current = text[cursor]
                if current == "\\":
                    # 反斜杠 + 换行是行拼接：字面量在下一行继续，不能按行截断。
                    if (
                        cursor + 2 < length
                        and text[cursor + 1] == "\r"
                        and text[cursor + 2] == "\n"
                    ):
                        cursor += 3
                    else:
                        cursor += 2
                    continue
                if current == quote:
                    cursor += 1
                    closed = True
                    break
                if current == "\n":
                    break
                cursor += 1
            if quote == "'" and not closed:
                # 数字分隔符（1'000）不是字符字面量；别吞掉整行后面的代码。
                index += 1
                continue
            end = min(cursor, length)
            blank(index, end)
            index = end
        else:
            index += 1

    return "".join(characters), comment_spans


def load_rules(path: Path) -> list[Rule]:
    try:
        text = path.read_text(encoding="utf-8")
    except OSError as error:
        raise EnvironmentProblem(f"无法读取规则文件 {path}：{error}") from error

    rules: list[Rule] = []
    for number, raw in enumerate(text.split("\n"), start=1):
        stripped = raw.strip()
        if not stripped or stripped.startswith("#"):
            continue
        fields = raw.split("\t")
        if len(fields) != 4:
            raise EnvironmentProblem(
                f"规则文件 {path}:{number} 应有 4 个 TAB 分隔字段（规则ID、glob、正则、说明），"
                f"实际 {len(fields)} 个"
            )
        rule_id, glob_field, pattern_text, description = (field.strip() for field in fields)
        if not rule_id or not glob_field or not pattern_text or not description:
            raise EnvironmentProblem(f"规则文件 {path}:{number} 有空字段")
        if RULE_ID_RE.fullmatch(rule_id) is None:
            raise EnvironmentProblem(f"规则文件 {path}:{number} 的规则 ID 不合法：{rule_id}")
        globs = tuple(part.strip() for part in glob_field.split(",") if part.strip())
        if not globs:
            raise EnvironmentProblem(f"规则文件 {path}:{number} 的 glob 字段为空")
        try:
            regex = re.compile(pattern_text)
        except re.error as error:
            raise EnvironmentProblem(
                f"规则文件 {path}:{number} 的正则编译失败：{error}"
            ) from error
        rules.append(Rule(rule_id, globs, regex, description))

    if not rules:
        raise EnvironmentProblem(f"规则文件 {path} 里没有任何规则")
    return rules


def line_violates(regex: re.Pattern[str], masked_line: str) -> bool:
    """这一行有没有命中。带 `hit` 命名组的规则只看 `hit` 参与匹配的那些。"""
    uses_hit_group = "hit" in regex.groupindex
    for match in regex.finditer(masked_line):
        if uses_hit_group and match.group("hit") is None:
            continue
        return True
    return False


def check_source(physical: Path, display: str, rules: list[Rule]) -> list[Finding]:
    try:
        raw_text = physical.read_text(encoding="utf-8", errors="replace")
    except OSError as error:
        raise EnvironmentProblem(f"无法读取源文件 {display}：{error}") from error

    masked_text, comment_spans = strip_comments_and_strings(raw_text)
    masked_lines = masked_text.split("\n")

    line_starts = [0]
    for offset, character in enumerate(raw_text):
        if character == "\n":
            line_starts.append(offset + 1)

    known_ids = {rule.rule_id for rule in rules}
    exempt: dict[int, set[str]] = {}
    findings: list[Finding] = []

    for start, end in comment_spans:
        match = ALLOW_RE.match(raw_text[start:end])
        if match is None:
            continue
        line_number = bisect.bisect_right(line_starts, start)
        reason = match.group(2).strip()
        for rule_id in (item.strip() for item in match.group(1).split(",")):
            if not rule_id:
                findings.append(
                    Finding(display, line_number, ALLOW_MARK, "豁免没有写规则 ID")
                )
            elif rule_id not in known_ids:
                findings.append(
                    Finding(
                        display,
                        line_number,
                        ALLOW_MARK,
                        f"豁免引用了规则文件里没有的规则 ID：{rule_id}",
                    )
                )
            elif not reason:
                findings.append(
                    Finding(
                        display,
                        line_number,
                        ALLOW_MARK,
                        f"豁免理由为空，规则 {rule_id} 的豁免无效",
                    )
                )
            else:
                exempt.setdefault(line_number, set()).add(rule_id)

    for rule in rules:
        if not rule.applies_to(display):
            continue
        for index, masked_line in enumerate(masked_lines):
            if not line_violates(rule.regex, masked_line):
                continue
            line_number = index + 1
            if rule.rule_id in exempt.get(line_number, ()):
                continue
            if rule.rule_id in exempt.get(line_number - 1, ()):
                continue
            findings.append(Finding(display, line_number, rule.rule_id, rule.description))

    return findings


def default_rules_path() -> Path:
    return Path(__file__).resolve().parent / "forbidden-rules.txt"


def locate_root(explicit: str | None) -> Path:
    if explicit:
        root = Path(explicit).resolve()
        if not root.is_dir():
            raise EnvironmentProblem(f"--root 不是目录：{explicit}")
        return root
    try:
        completed = subprocess.run(
            ["git", "rev-parse", "--show-toplevel"],
            capture_output=True,
            text=True,
            check=False,
        )
    except OSError as error:
        raise EnvironmentProblem(f"无法调用 git：{error}") from error
    if completed.returncode != 0:
        detail = completed.stderr.strip() or "不在 git 仓库里"
        raise EnvironmentProblem(f"无法定位仓库根目录：{detail}")
    return Path(completed.stdout.strip()).resolve()


def worktree_files(root: Path) -> list[str]:
    """工作区里被 git 跟踪或「未跟踪但未被忽略」的文件。

    只列 `git ls-files`（已跟踪）会漏掉本批新增、还没 `git add` 的源文件，让门禁假绿；
    `--exclude-standard` 继续尊重 .gitignore，build/、bin/obj/ 不会混进来。
    """
    try:
        completed = subprocess.run(
            ["git", "-C", str(root), "ls-files", "--cached", "--others", "--exclude-standard", "-z"],
            capture_output=True,
            check=False,
        )
    except OSError as error:
        raise EnvironmentProblem(f"无法调用 git：{error}") from error
    if completed.returncode != 0:
        detail = completed.stderr.decode("utf-8", "replace").strip() or "git ls-files 失败"
        raise EnvironmentProblem(f"无法列出仓库文件：{detail}")
    return [item.decode("utf-8", "replace") for item in completed.stdout.split(b"\0") if item]


def listed_files(path: Path) -> list[str]:
    try:
        data = path.read_bytes()
    except OSError as error:
        raise EnvironmentProblem(f"无法读取文件列表 {path}：{error}") from error
    return [item.decode("utf-8", "replace") for item in data.split(b"\0") if item]


def select_files(
    candidates: list[str],
    rules: list[Rule],
    root: Path,
    prefix: Path,
    allow_missing: bool,
) -> list[str]:
    selected: list[str] = []
    seen: set[str] = set()
    for display in candidates:
        if display in seen:
            continue
        seen.add(display)
        if Path(display).suffix.lower() not in SOURCE_SUFFIXES:
            continue
        if not any(rule.applies_to(display) for rule in rules):
            continue
        if not (prefix / display).is_file():
            if allow_missing and prefix == root:
                # git ls-files 会列出「工作区已删除、索引里还在」的文件，跳过。
                continue
            raise EnvironmentProblem(f"文件不存在：{display}")
        selected.append(display)
    return selected


def parse_arguments(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        prog="forbidden.py",
        description="按 forbidden-rules.txt 检查 C++ 源码里的禁用写法。",
    )
    parser.add_argument("files", nargs="*", help="仓库相对路径；省略时用工作区里的跟踪文件 + 未忽略的新文件")
    parser.add_argument(
        "--root", help="仓库根目录（默认取 git rev-parse --show-toplevel）"
    )
    parser.add_argument(
        "--prefix", help="文件实际所在目录；--staged 时指向暂存区索引镜像树"
    )
    parser.add_argument("--rules", help="规则文件路径（默认同目录的 forbidden-rules.txt）")
    parser.add_argument("--files-from", help="从该文件读 NUL 分隔的仓库相对路径列表")
    return parser.parse_args(argv)


def main(argv: list[str]) -> int:
    arguments = parse_arguments(argv)
    try:
        if arguments.files and arguments.files_from:
            raise EnvironmentProblem("位置参数与 --files-from 只能选一个")
        root = locate_root(arguments.root)
        rules = load_rules(
            Path(arguments.rules) if arguments.rules else default_rules_path()
        )
        explicit = bool(arguments.files or arguments.files_from)
        if arguments.files:
            candidates = list(arguments.files)
        elif arguments.files_from:
            candidates = listed_files(Path(arguments.files_from))
        else:
            candidates = worktree_files(root)
        prefix = Path(arguments.prefix).resolve() if arguments.prefix else root
        if not prefix.is_dir():
            raise EnvironmentProblem(f"--prefix 不是目录：{arguments.prefix}")
        selected = select_files(
            candidates, rules, root, prefix, allow_missing=not explicit
        )
        findings: list[Finding] = []
        for display in selected:
            findings.extend(check_source(prefix / display, display, rules))
    except EnvironmentProblem as problem:
        print(f"forbidden: {problem}", file=sys.stderr)
        return 2

    if not selected:
        print(
            "forbidden: 警告：没有任何文件命中规则的 glob，这次检查没有覆盖到文件——"
            "先看看规则文件里的 glob 是不是写错了。",
            file=sys.stderr,
        )

    findings.sort(key=lambda finding: (finding.path, finding.line, finding.rule_id))
    for finding in findings:
        print(f"{finding.path}:{finding.line}: [{finding.rule_id}] {finding.message}")
    print(f"forbidden: 扫描 {len(selected)} 个文件，命中 {len(findings)} 处")
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
