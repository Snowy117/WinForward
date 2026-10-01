"""Batch 4b step 5 — prove every InternalsVisibleTo grant to a test friend is load-bearing.

If the friend is not in the owner's reference closure the grant is dead and is removed.
Otherwise the declaration is removed, the friend rebuilt (forcing a full rebuild when the
incremental build passes), and the declaration restored only when the build breaks with an
access error.

Usage: python3 research/verify-ivt-necessity.py [--only PATH_SUBSTRING]
"""

import re
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path("/home/paff/Projects/WinForward")
CSPROJ_RE = re.compile(r'^(?P<indent>\s*)<InternalsVisibleTo\s+Include="(?P<name>[^"]+)"\s*/>\s*$')
CS_RE = re.compile(r'^(?P<indent>\s*)\[assembly:\s*InternalsVisibleTo\("(?P<name>[^"]+)"\)\]\s*$')
PROJECT_REF_RE = re.compile(r'<ProjectReference\s+Include="(?P<path>[^"]+)"')
ACCESS_ERRORS = ("CS0122", "CS0272", "CS1061", "CS0117", "CS7036", "CS1729")


def is_friend(name: str) -> bool:
    return name.endswith(".Tests") or name == "WinForward.TestSupport"


def project_for(friend: str) -> Path:
    return ROOT / "tests" / friend / f"{friend}.csproj"


def owner_project(path: Path) -> Path:
    for parent in path.parents:
        candidates = list(parent.glob("*.csproj"))
        if candidates:
            return candidates[0]
    raise AssertionError(f"no project owns {path}")


def reference_closure(project: Path) -> set[str]:
    closure, queue = set(), [project]
    while queue:
        current = queue.pop()
        if current.name in closure or not current.exists():
            continue
        closure.add(current.name)
        for match in PROJECT_REF_RE.finditer(current.read_text(encoding="utf-8")):
            queue.append((current.parent / match.group("path")).resolve())
    return closure


def scan(filter_substring: str | None):
    entries = []
    for base in ("src", "benchmarks"):
        for path in sorted((ROOT / base).rglob("*")):
            if path.suffix not in (".csproj", ".cs"):
                continue
            regex = CSPROJ_RE if path.suffix == ".csproj" else CS_RE
            for line in path.read_text(encoding="utf-8").split("\n"):
                match = regex.match(line)
                if match and is_friend(match.group("name")):
                    rel = path.relative_to(ROOT).as_posix()
                    if filter_substring and filter_substring not in rel:
                        continue
                    entries.append((path, line, match.group("name")))
    return entries


def remove_declaration(path: Path, line: str) -> int:
    lines = path.read_text(encoding="utf-8").split("\n")
    index = lines.index(line)
    del lines[index]
    path.write_text("\n".join(lines), encoding="utf-8")
    return index


def restore_declaration(path: Path, index: int, line: str):
    lines = path.read_text(encoding="utf-8").split("\n")
    lines.insert(index, line)
    path.write_text("\n".join(lines), encoding="utf-8")


def build(friend: str, force: bool = False):
    command = ["dotnet", "build", str(project_for(friend)), "-c", "Release", "--no-restore", "-nologo", "-v:q"]
    if force:
        command.append("--no-incremental")
    started = time.time()
    result = subprocess.run(command, cwd=ROOT, capture_output=True, text=True)
    errors = [line.strip() for line in (result.stdout + result.stderr).split("\n") if " error " in line]
    return result.returncode, errors, time.time() - started


def access_errors(errors: list[str]) -> list[str]:
    return [error for error in errors if any(tag in error for tag in ACCESS_ERRORS)]


def verify(path: Path, line: str, friend: str):
    index = remove_declaration(path, line)
    code, errors, seconds = build(friend)
    if code == 0:
        forced_code, forced_errors, forced_seconds = build(friend, force=True)
        seconds += forced_seconds
        if forced_code == 0:
            return "REMOVED", "clean build, incremental and forced", round(seconds, 1)
        errors, code = forced_errors, forced_code
    verdict = "NEEDED" if access_errors(errors) else "UNEXPECTED"
    detail = (access_errors(errors) or errors or [f"exit {code}"])[0]
    restore_declaration(path, index, line)
    return verdict, detail[:150], round(seconds, 1)


def main():
    only = sys.argv[2] if len(sys.argv) > 2 and sys.argv[1] == "--only" else None
    closures, rows = {}, []

    for path, line, friend in scan(only):
        owner = owner_project(path)
        rel = path.relative_to(ROOT).as_posix()
        if friend not in closures:
            closures[friend] = reference_closure(project_for(friend))
        if owner.name in closures[friend]:
            verdict, detail, seconds = verify(path, line, friend)
        else:
            remove_declaration(path, line)
            verdict, detail, seconds = "DEAD", f"{friend} does not reference {owner.name}", 0.0
        rows.append((verdict, rel, friend, seconds, detail))
        print(f"{verdict:11} {rel} -> {friend}  ({seconds:.1f}s)  {detail}", flush=True)

    counts = {verdict: sum(1 for row in rows if row[0] == verdict) for verdict in {row[0] for row in rows}}
    print(f"\nchecked={len(rows)} " + " ".join(f"{k.lower()}={v}" for k, v in sorted(counts.items())))

    report = ["# IVT necessity sweep (batch 4b step 5)", "",
              "Each grant to a test friend was removed, its friend rebuilt (forcing a full rebuild when the",
              "incremental build passed), and restored only when the build broke with an access error. Grants",
              "whose friend does not reference the owner were dead on arrival.", "",
              "| Verdict | Owning file | Friend | Detail |", "|---|---|---|---|"]
    for verdict, rel, friend, _seconds, detail in rows:
        report.append(f"| {verdict} | `{rel}` | `{friend}` | {detail} |")
    (ROOT / ".trellis/tasks/10-01-split-test-projects/research/ivt-necessity.md").write_text(
        "\n".join(report) + "\n", encoding="utf-8")


main()
