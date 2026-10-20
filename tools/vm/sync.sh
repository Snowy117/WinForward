#!/usr/bin/env bash
# 把驱动与测试程序源码同步到 WinLTSC 的固定工作目录。只上传源码，不上传构建产物。
set -euo pipefail

session="${1:?用法: tools/vm/sync.sh <windows-vm 会话名> [远程根目录]}"
remote_root="${2:-C:\wfbench\wfprobe}"
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
staging="$(mktemp -d)"
trap 'rm -rf "$staging"' EXIT

mkdir -p "$staging/src" "$staging/tools"
cp -r "$repo_root/src/driver" "$repo_root/src/common" "$staging/src/"
cp -r "$repo_root/tools/wfprobe" "$repo_root/tools/vm" "$staging/tools/"
rm -rf "$staging/src/driver/x64" "$staging/src/driver/obj" \
       "$staging/tools/wfprobe/bin" "$staging/tools/wfprobe/obj"
(cd "$staging" && zip -qr "$staging/wfprobe-src.zip" src tools)

windows-vm "$session" run "Remove-Item -Recurse -Force '$remote_root' -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force '$remote_root' | Out-Null" >/dev/null
windows-vm "$session" up "$staging/wfprobe-src.zip" 'C:\wfbench\wfprobe-src.zip' >/dev/null
windows-vm "$session" run "Expand-Archive -Force 'C:\wfbench\wfprobe-src.zip' '$remote_root'" >/dev/null
echo "synced $repo_root -> $remote_root"
