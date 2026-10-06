#!/usr/bin/env bash
# Four artifacts come out of one source tree, and the fourth is not a duplicate by accident: the
# products tell applications apart by image name, so sending one application around the proxy and
# another through it requires two different images.
set -euo pipefail

repo=$(cd "$(dirname "$0")/.." && pwd)
pub=${WF_PUB:-${TMPDIR:-/tmp}/wf-bench/pub}

cd "$repo"

echo "=== build and test ==="
dotnet build -c Release 2>&1 | tail -3

echo
echo "=== publish ==="
rm -rf "$pub/linux" "$pub/win" "$pub/win-direct"
dotnet publish -c Release -r linux-x64 --self-contained false -o "$pub/linux" >/dev/null
dotnet publish -c Release -r win-x64 --self-contained false -o "$pub/win" >/dev/null
dotnet publish -c Release -r win-x64 --self-contained false -p:AssemblyName=WinForward.E2E.Direct -o "$pub/win-direct" >/dev/null

for dir in linux win win-direct; do
    printf '%-12s %s\n' "$dir" "$(ls "$pub/$dir" | tr '\n' ' ')"
done

echo
echo "=== the two Windows clients must be distinguishable by image name ==="
ls -1 "$pub/win"/*.exe "$pub/win-direct"/*.exe
