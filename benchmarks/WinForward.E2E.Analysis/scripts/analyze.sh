#!/usr/bin/env bash
# Build the analysis and run it: the thin wrapper a campaign calls instead of an interpreter.
#
# It builds first, then `exec`s the produced binary, so the process a caller sees is the analysis
# itself: its exit code is the analysis's, its stdin/stdout/stderr are its own, and no shell stays in
# between to mangle them. Every argument is passed through unchanged, a `--` separator included; the
# script parses nothing and defaults nothing.
#
# The working directory is never changed: relative paths in the arguments mean what they mean for the
# caller, exactly as they do for a direct invocation. Nothing is printed by this wrapper itself, so a
# caller that compares the analysis's output is not reading this script's.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$HERE/WinForward.E2E.Analysis.csproj"
BINARY="$HERE/bin/Release/net10.0/WinForward.E2E.Analysis"

dotnet build "$PROJECT" -c Release --no-restore --nologo -v quiet >/dev/null

exec "$BINARY" "$@"
