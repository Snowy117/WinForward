import difflib
import json
import pathlib
import re
import subprocess

BASELINE = "32b67f7"
BASE = "tests/WinForward.Core.Tests"
DECLARATION = re.compile(
    r"^\s*(?:using\s+(?:static\s+)?[\w.]+\s*;|namespace\s+[\w.]+\s*;)\s*$", re.M
)


def significant_lines(text):
    return [line for line in DECLARATION.sub("", text).split("\n") if line.strip()]


file_map = json.loads(
    pathlib.Path(".trellis/tasks/10-01-split-test-projects/research/file-map.json").read_text()
)
moves = {}
for project, files in file_map["projects"].items():
    for f in files:
        moves[f] = f"tests/{project}/{f}"
for f in file_map["testSupportSources"]:
    moves[f"TestHelpers/{f}"] = f"tests/WinForward.TestSupport/{f}"

drifted = []
for old_rel, new_path in sorted(moves.items()):
    old = subprocess.run(
        ["git", "show", f"{BASELINE}:{BASE}/{old_rel}"], capture_output=True, text=True
    )
    if old.returncode != 0:
        print(f"MISSING IN BASELINE: {old_rel}")
        continue
    before = significant_lines(old.stdout)
    after = significant_lines(pathlib.Path(new_path).read_text(encoding="utf-8"))
    if before != after:
        diff = [
            line
            for line in difflib.unified_diff(before, after, lineterm="", n=0)
            if line.startswith(("+", "-")) and not line.startswith(("+++", "---"))
        ]
        drifted.append((old_rel, new_path, diff))

print(f"{len(moves)} files compared against {BASELINE}")
print("(namespace/using declarations and blank lines ignored)\n")
for old_rel, new_path, diff in drifted:
    print(f"  DIFFERS {old_rel} -> {new_path}  ({len(diff)} significant lines)")
    for line in diff[:10]:
        print(f"      {line[:110]}")
print("\nAC4:", "PURE" if not drifted else f"{len(drifted)} file(s) differ")
