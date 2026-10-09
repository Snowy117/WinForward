#!/usr/bin/env python3
"""Acceptance checks for the .trellis/spec library (task 10-09-spec-revision).

Checks, in the order of the task's acceptance criteria:

  AC2  size          every doc <= 400 lines; print the table
  AC3  structure     every .md is listed in its layer's index.md, and in every family the
                     children are reachable from the hub and each links back to it
  AC4  links         every relative markdown link resolves
  AC4  identifiers   every backticked identifier that looks like a code symbol exists in the tree
  AC6  language      CJK prose, exempting only the lines that name a real artifact

Run from the repo root:

    python3 .trellis/tasks/10-09-spec-revision/research/verify-specs.py
    python3 .trellis/tasks/10-09-spec-revision/research/verify-specs.py --doc backend/udp-relay.md

Exit code is 0 only when every check passes. `--doc` narrows the identifier and language
checks to one document (size/link/structure checks always cover the whole library).
"""

from __future__ import annotations

import argparse
import pathlib
import re
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[4]
SPEC = ROOT / ".trellis" / "spec"

MAX_DOC_LINES = 400

# Identifiers that are legitimately absent from this repo's source: BCL and framework types,
# analyzer/editorconfig rule names, Roslyn diagnostics, native ABI members, and English words
# that merely look like symbols. Every entry needs a reason; keep this list alphabetical and
# short enough to audit. A symbol that belongs to *this* codebase never goes here.
NON_REPO_IDENTIFIERS = {
    # .NET / BCL
    "ActivityPropagationInterval", "AsyncLocal", "ColorBehavior", "ConsoleLogger",
    "IConfigureOptions", "IPostConfigureOptions", "IncludeScopes", "LogError", "LogFormat",
    "LogValues", "LogWarning", "LoggerFilterOptions", "MemorySends", "NonBlocking",
    "NoInlining", "OverflowException", "SynchronizationContext",
    # Roslyn diagnostics and analyzer/editorconfig rules
    "ArrangeObjectCreationWhenTypeNotEvident", "ArrangeRedundantParentheses", "CS0117",
    "CS0122", "CS1061", "CS7036", "CSharpErrors", "EnforceExtendedAnalyzerRules", "InvertIf",
    "NamingStyleCodeFixProvider", "NotAccessedPositionalProperty", "SYSLIB1015", "SYSLIB1025",
    "UnusedAutoPropertyAccessor",
    # native ABI members
    "th_dport", "th_sport",
    # historical or illustrative names (each is marked as historical in the doc text)
    "Socks5Client.cs", "database-guidelines.md", "TcpRelayFaultObserver.cs", "analyze.py",
    "ReinjectExistingSynAsync", "TcpRedirectLogging", "UdpProxyLogging", "TcpRelayEndResetTests",
    "UdpAssociationCapabilitySampler", "UdpAssociationEvidence", "UdpAssociationLease",
    "UdpControlAssociation", "UdpServerCapability",
    # artifacts the analyzer writes at run time — real, but never present in the tree
    "tables.md", "verdict.json", "plots/SKIPPED.md",
}

CJK_LINE_EXEMPTIONS: set[tuple[str, str]] = set()

HUB_CHILDREN: dict[str, list[str]] = {
    "hot-path": [
        "warm-path-dispatch", "packet-shape-and-flow-identity", "native-lease-and-pool-lifetime",
        "allocation-gates", "allocation-gate-host-lumps", "udp-datagram-path",
        "relay-pump-and-checksums", "benchmark-methodology",
    ],
    "udp-relay": [
        "udp-response-reinjection", "udp-relay-transport", "udp-flow-setup",
        "udp-session-lifecycle", "udp-association-ownership", "udp-over-tcp",
    ],
    "tcp-local-redirect": [
        "tcp-redirect-transform", "tcp-client-close-injection", "tcp-syn-setup-admission",
        "tcp-redirect-teardown-grace", "tcp-relay-lifecycle",
    ],
    "windows-ndisapi": ["ndis-capture-refresh", "ndis-batched-capture", "ndis-batched-send"],
    "measurement-harness": [
        "measurement-record-contract", "measurement-run-lifecycle", "measurement-lane-seam",
        "measurement-judgement", "measurement-udp-census", "measurement-tooling",
    ],
}


def sh(*args: str) -> str:
    return subprocess.run(args, cwd=ROOT, capture_output=True, text=True, check=False).stdout


def docs() -> list[pathlib.Path]:
    return sorted(SPEC.rglob("*.md"))


def check_size(failures: list[str]) -> None:
    print("== AC2 size ==")
    print(f"{'lines':>5} {'eff':>5}  doc")
    for path in docs():
        text = path.read_text(encoding="utf-8").splitlines()
        eff = sum(1 for line in text if line.strip() and not line.strip().startswith("#"))
        mark = "  <-- OVER" if len(text) > MAX_DOC_LINES else ""
        print(f"{len(text):5d} {eff:5d}  {path.relative_to(SPEC)}{mark}")
        if len(text) > MAX_DOC_LINES:
            failures.append(f"{path.relative_to(ROOT)}: {len(text)} lines > {MAX_DOC_LINES}")


def check_structure(failures: list[str]) -> None:
    print("\n== AC3 structure: layer index lists every doc ==")
    for layer in sorted(p for p in SPEC.iterdir() if p.is_dir()):
        index = layer / "index.md"
        if not index.exists():
            failures.append(f"{layer.relative_to(ROOT)}: no index.md")
            continue
        text = index.read_text(encoding="utf-8")
        for path in sorted(layer.rglob("*.md")):
            if path.name == "index.md":
                continue
            if path.name not in text:
                print(f"  MISSING from {index.relative_to(SPEC)}: {path.name}")
                failures.append(f"{index.relative_to(ROOT)} does not list {path.name}")
        print(f"  {index.relative_to(SPEC)}: ok")


def check_families(failures: list[str]) -> None:
    print("\n== AC3 structure: hubs and their children ==")
    for hub, children in HUB_CHILDREN.items():
        hub_path = SPEC / "backend" / f"{hub}.md"
        if not hub_path.exists():
            failures.append(f"{hub}.md is missing")
            continue
        hub_text = hub_path.read_text(encoding="utf-8")
        for child in children:
            child_path = SPEC / "backend" / f"{child}.md"
            if not child_path.exists():
                print(f"  MISSING child {child}.md (linked from {hub}.md)")
                failures.append(f"{hub}.md links {child}.md, which does not exist")
                continue
            child_text = child_path.read_text(encoding="utf-8")
            if not any(f"({prefix}{hub}.md)" in child_text for prefix in ("./", "../", "")):
                print(f"  {child}.md does not link back to its hub")
                failures.append(f"{child}.md does not link its hub {hub}.md")
            if not any(f"({prefix}{child}.md)" in hub_text for prefix in ("./", "../", "")):
                print(f"  {hub}.md does not link {child}.md")
                failures.append(f"{hub}.md does not link its child {child}.md")
    print(f"  {len(HUB_CHILDREN)} families checked")


def check_links(failures: list[str]) -> None:
    print("\n== AC4 links ==")
    bad = 0
    for path in docs():
        text = path.read_text(encoding="utf-8")
        for match in re.finditer(r"\[([^\]]*)\]\(([^)\s]+)\)", text):
            target = match.group(2)
            if target.startswith(("http", "mailto", "#")):
                continue
            target = target.split("#")[0]
            if not target:
                continue
            if not (path.parent / target).resolve().exists():
                line = text[: match.start()].count("\n") + 1
                print(f"  BROKEN {path.relative_to(SPEC)}:{line} -> {match.group(2)}")
                failures.append(f"{path.relative_to(ROOT)}:{line}: broken link {match.group(2)}")
                bad += 1
    print(f"  {bad} broken link(s)")


def build_corpus() -> set[str]:
    files = sh(
        "rg", "--files",
        "-g", "!**/bin/**", "-g", "!**/obj/**", "-g", "!**/node_modules/**",
        "-g", "!**/.git/**", "-g", "!.trellis/**",
        "-g", "*.cs", "-g", "*.json", "-g", "*.props", "-g", "*.slnx", "-g", "*.py",
        "-g", "*.yml", "-g", "*.yaml", "-g", "*.md",
    ).splitlines()
    chunks = []
    for rel in files:
        try:
            chunks.append((ROOT / rel).read_text(encoding="utf-8", errors="ignore"))
        except OSError:
            pass
    return set(re.findall(r"[A-Za-z_][A-Za-z0-9_]*", "\n".join(chunks)))


def check_identifiers(failures: list[str], only: str | None) -> None:
    print("\n== AC4 identifiers ==")
    corpus = build_corpus()
    unresolved = 0
    for path in docs():
        rel = str(path.relative_to(SPEC))
        if only and only not in rel:
            continue
        text = path.read_text(encoding="utf-8")
        missing: set[str] = set()
        for match in re.finditer(r"`([^`\n]+)`", text):
            token = match.group(1).strip()
            head = token.split("<")[0]
            if re.fullmatch(r"[A-Z][A-Za-z0-9]*", head):
                missing.add(head)
            elif re.fullmatch(r"[a-z_][A-Za-z0-9_]*", token) and ("_" in token or len(token) > 12):
                missing.add(token)
        missing -= NON_REPO_IDENTIFIERS | corpus
        unresolved += len(missing)
        for token in sorted(missing):
            print(f"  ? {rel}: `{token}`")
        if missing:
            failures.append(f"{rel}: {len(missing)} unresolved identifiers, e.g. {sorted(missing)[:5]}")
    print(f"  {unresolved} unresolved identifier(s) outside the allowlist")


def check_language(failures: list[str], only: str | None) -> None:
    print("\n== AC6 language ==")
    hits = 0
    for path in docs():
        rel = str(path.relative_to(SPEC))
        if only and only not in rel:
            continue
        for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
            if not re.search(r"[\u3400-\u9fff]", line):
                continue
            if any(rel.endswith(suffix) and token in line for suffix, token in CJK_LINE_EXEMPTIONS):
                continue
            print(f"  CJK {rel}:{number}: {line.strip()[:110]}")
            hits += 1
    print(f"  {hits} unexplained CJK line(s)")
    if hits:
        failures.append(f"{hits} lines of unexplained CJK text")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--doc", help="substring of a doc path, to narrow identifier/language checks")
    args = parser.parse_args()

    failures: list[str] = []
    check_size(failures)
    check_structure(failures)
    check_families(failures)
    check_links(failures)
    check_identifiers(failures, args.doc)
    check_language(failures, args.doc)

    print("\n== summary ==")
    if failures:
        for failure in failures:
            print(f"  FAIL {failure}")
        return 1
    print("  all checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
