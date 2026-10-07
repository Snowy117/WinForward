#!/usr/bin/env python3
"""Record what the harness CLI shows a user: the exit code, stdout and stderr of a fixed command list.

The two verbs of ``WinForward.E2E`` are driven by one argument parser (E2-d). That rewrite may not
change a single character of what an operator sees, with one registered exception, so the check is
mechanical: run every help and error path through the published binary before the rewrite and again
after it, then diff the two trees byte for byte. A tree that differs anywhere outside
``INTENTIONAL.md``'s entry is a failed batch, and the diff names the case.

Usage:
    cli-snapshots.py <harness-binary> <out-directory>

Each case writes ``<nn>-<name>.exit``, ``<nn>-<name>.stdout`` and ``<nn>-<name>.stderr`` -- separate
files so trailing whitespace and a missing final newline survive the round trip, which a single
annotated log would lose -- and ``index.json`` repeats every case's argv so a reader (including the
xunit snapshot test, which replays the list through ``Program.Main``) never has to guess how a shell
would have quoted a command. The tree deliberately records nothing about which binary produced it:
the two trees are then identical by construction except for the behaviour under test.

The command list is the CLI's whole user-visible surface: the two helps, the three role forms, and
every rejection the two verbs can print. The names, not the positions, are the contract. Plan paths
are repository-relative names of the test suite's Tier 0 fixtures, so a recorded argv means the same
file on any machine and a renamed fixture shows up as changed error text rather than as "file not
found". Children run with the repository as their working directory, which is what lets a replay
resolve those same relative paths from a test host that is not sitting in the repository.

Every case is a rejection or a help, so none of them creates the client's output directory: the
parser and the plan reader both run before anything is created. The collector asserts that the
directory named by ``OUT`` stayed absent, which keeps the list replayable inside the test suite.
"""

import hashlib
import json
import os
import subprocess
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", ".."))
OUT = ".cli-snapshot-out"
PLANS = "tests/WinForward.E2E.Tests/Fixtures/plans"

CASES = [
    ("client-help", ["client", "--help"]),
    ("no-arguments", []),
    ("role-help", ["--help"]),
    ("unknown-role", ["bogus"]),
    ("target-help", ["target", "--help"]),
    ("target-unknown-option", ["target", "--nope"]),
    ("target-missing-value", ["target", "--tcp-port"]),
    ("target-port-not-a-number", ["target", "--tcp-port", "abc"]),
    ("target-port-out-of-range", ["target", "--tcp-port", "65536", "--dns-port", "53"]),
    ("target-dns-collides-with-tcp", ["target", "--tcp-port", "40010", "--dns-port", "40010"]),
    ("target-dns-collides-with-udp", ["target", "--udp-port", "40010", "--dns-port", "40010"]),
    ("target-dns-alt-collides", ["target", "--dns-alt-port", "30010"]),
    ("target-bind-not-an-ip", ["target", "--bind", "localhost"]),
    ("client-unknown-option", ["client", "--target", "127.0.0.1", "--out", OUT, "--nope"]),
    ("client-missing-value", ["client", "--target", "127.0.0.1", "--out", OUT, "--plan"]),
    ("client-empty-plan-inline", ["client", "--target", "127.0.0.1", "--out", OUT, "--plan="]),
    ("client-empty-plan-separate", ["client", "--target", "127.0.0.1", "--out", OUT, "--plan", ""]),
    ("client-empty-sampler-process", ["client", "--target", "127.0.0.1", "--out", OUT, "--sampler-process="]),
    ("client-port-not-a-number", ["client", "--target", "127.0.0.1", "--out", OUT, "--tcp-port", "abc"]),
    ("client-port-out-of-range", ["client", "--target", "127.0.0.1", "--out", OUT, "--tcp-port", "65536"]),
    ("client-leading-dash-value", ["client", "--target", "127.0.0.1", "--out", OUT, "--label", "--out", "x"]),
    ("client-target-required", ["client", "--out", OUT]),
    ("client-out-required", ["client", "--target", "127.0.0.1"]),
    ("client-plan-out-of-range", ["client", "--target", "127.0.0.1", "--out", OUT, "--plan", f"{PLANS}/beyond-int-window.json"]),
    ("client-plan-unknown-key", ["client", "--target", "127.0.0.1", "--out", OUT, "--plan", f"{PLANS}/unknown-key.json"]),
    ("client-plan-fractional-value", ["client", "--target", "127.0.0.1", "--out", OUT, "--plan", f"{PLANS}/fractional-window.json"]),
    ("client-plan-colliding-names", ["client", "--target", "127.0.0.1", "--out", OUT, "--plan", f"{PLANS}/colliding-file-names.json"]),
    ("client-plan-arm-name-too-long", ["client", "--target", "127.0.0.1", "--out", OUT, "--plan", f"{PLANS}/arm-name-too-long.json"]),
]


def main() -> int:
    if len(sys.argv) != 3:
        print(__doc__.strip(), file=sys.stderr)
        return 2

    binary, out_directory = sys.argv[1], sys.argv[2]
    if not os.path.isfile(binary):
        print(f"no harness binary at {binary}", file=sys.stderr)
        return 2

    with open(binary, "rb") as handle:
        digest = hashlib.sha256(handle.read()).hexdigest()

    print(f"binary  {os.path.abspath(binary)}")
    print(f"sha256  {digest}")

    os.makedirs(out_directory, exist_ok=True)
    probe = os.path.join(ROOT, OUT)
    if os.path.exists(probe):
        print(f"{probe} already exists; every case must be side-effect free", file=sys.stderr)
        return 2

    index = []
    for position, (name, argv) in enumerate(CASES):
        stem = f"{position:02d}-{name}"
        completed = subprocess.run([os.path.abspath(binary), *argv], cwd=ROOT, capture_output=True, check=False)
        with open(os.path.join(out_directory, f"{stem}.exit"), "w", encoding="ascii") as handle:
            handle.write(f"{completed.returncode}\n")
        with open(os.path.join(out_directory, f"{stem}.stdout"), "wb") as handle:
            handle.write(completed.stdout)
        with open(os.path.join(out_directory, f"{stem}.stderr"), "wb") as handle:
            handle.write(completed.stderr)
        index.append({"name": name, "stem": stem, "argv": argv})
        print(f"{stem:<34} exit {completed.returncode}")

    with open(os.path.join(out_directory, "index.json"), "w", encoding="utf-8") as handle:
        json.dump(index, handle, indent=2)
        handle.write("\n")

    if os.path.exists(probe):
        print(f"a case created {probe}; the snapshot list is no longer side-effect free", file=sys.stderr)
        return 2

    print(f"{len(CASES)} cases -> {out_directory}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
