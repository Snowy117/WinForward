#!/usr/bin/env bash
# Regenerate the frozen synthetic tree and its tarball, reproducibly.
#
# The tarball's root is the *contents* of the raw directory's parent: `raw/`, both ledgers and the
# three plan files. The archive is built with sorted names, one fixed mtime and zeroed ownership, so
# regenerating it from the same make_tree.py produces the same sha256 on any machine.
#
# Run from anywhere; the tree lands in /tmp/wf-synth, the path both implementations are called with.
# Regenerating the tarball is only half of a re-freeze: the golden `tables.md`/`verdict.json` are
# produced from the same tree (see FROZEN.md), and any change to make_tree.py invalidates both.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TREE=/tmp/wf-synth
ARCHIVE="$HERE/synthetic-tree.tar.gz"

rm -rf "$TREE"
mkdir -p "$TREE"
python3 "$HERE/synthetic/make_tree.py" "$TREE/raw"

cd "$TREE"
tar --sort=name \
    --mtime='2026-10-01 00:00:00 UTC' \
    --owner=0 --group=0 --numeric-owner \
    --format=gnu \
    -czf "$ARCHIVE" \
    raw ledger-main.jsonl ledger-direct.jsonl plan-*.json

echo "wrote $ARCHIVE"
sha256sum "$ARCHIVE" "$TREE/raw/environment.json" | sed 's|/tmp/wf-synth/|  |'
