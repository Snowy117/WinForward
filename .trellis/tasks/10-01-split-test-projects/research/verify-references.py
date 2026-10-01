import pathlib
import re

PRODUCTS = [
    ("WinForward.Core", "WinForward.Core"),
    ("WinForward.Configuration", "WinForward.Configuration"),
    ("WinForward.Protocols", "WinForward.Protocols"),
    ("WinForward.NdisApi", "WinForward.NdisApi"),
    ("WinForward.Windows", "WinForward.Windows"),
    ("WinForward.Runtime", "WinForward.Runtime"),
    ("WinForward.Cli", "WinForward.Cli"),
    ("WinForward.Benchmarks", "WinForward.Benchmarks"),
]
USING_RE = re.compile(r"^using\s+(?:static\s+)?(WinForward[\w.]*)\s*;", re.M)


def owner(ns):
    for prefix, project in PRODUCTS:
        if ns == prefix or ns.startswith(prefix + "."):
            return project
    return None


def implicitly_visible_projects(project_name):
    parts = project_name.split(".")
    prefixes = {".".join(parts[:i]) for i in range(len(parts) - 1, 0, -1)}
    return {project for _, project in PRODUCTS} & prefixes


def audit():
    mismatches = 0
    for projdir in sorted(pathlib.Path("tests").iterdir()):
        if not projdir.is_dir():
            continue
        name = projdir.name
        csproj = projdir / f"{name}.csproj"
        if not csproj.exists() or name == "WinForward.Analyzers.Tests":
            continue
        refs = {
            pathlib.Path(m.group(1)).stem
            for m in re.finditer(r'ProjectReference Include="([^"]+)"', csproj.read_text())
        } - {"WinForward.TestSupport"}
        usings = set()
        for f in projdir.glob("*.cs"):
            for u in USING_RE.findall(f.read_text(encoding="utf-8")):
                o = owner(u)
                if o:
                    usings.add(o)
        expected = usings | implicitly_visible_projects(name)
        extra, missing = refs - expected, expected - refs
        ok = not extra and not missing
        mismatches += 0 if ok else 1
        detail = ""
        if extra:
            detail += f" redundant-ref={sorted(extra)}"
        if missing:
            detail += f" MISSING-ref={sorted(missing)}"
        print(f"{name:<38} {'OK' if ok else 'MISMATCH':<9}{detail}")
    print("\nAC6:", "PASS" if mismatches == 0 else f"{mismatches} project(s) mismatched")


if __name__ == "__main__":
    audit()
