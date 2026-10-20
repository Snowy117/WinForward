#!/usr/bin/env bash
# 把驱动与测试程序源码同步到 WinLTSC 的固定工作目录。只上传源码，不上传构建产物。
set -euo pipefail

session="${1:?用法: tools/vm/sync.sh <windows-vm 会话名> [远程根目录]}"
remote_root="${2:-C:\wfbench\wfprobe}"
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
staging="$(mktemp -d)"
trap 'rm -rf "$staging"' EXIT

mkdir -p "$staging/src" "$staging/tools"
cp -r "$repo_root/src/driver" "$repo_root/src/common" "$repo_root/src/userland" "$staging/src/"
cp -r "$repo_root/tools/wfprobe" "$repo_root/tools/vm" "$staging/tools/"
rm -rf "$staging/src/driver/x64" "$staging/src/driver/obj" \
       "$staging/tools/wfprobe/bin" "$staging/tools/wfprobe/obj"
# src/userland 的构建产物不跨机器复制：bin/obj 里是宿主的绝对路径。
find "$staging/src/userland" -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +

(cd "$staging" && zip -qr "$staging/wfprobe-src.zip" src tools)

windows-vm "$session" run "Remove-Item -Recurse -Force '$remote_root' -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force '$remote_root' | Out-Null" >/dev/null
windows-vm "$session" up "$staging/wfprobe-src.zip" 'C:\wfbench\wfprobe-src.zip' >/dev/null
windows-vm "$session" run "Expand-Archive -Force 'C:\wfbench\wfprobe-src.zip' '$remote_root'" >/dev/null
# 解压后显式确认关键文件都在：构建读到还没展开完的目录树会报 MSB1009，且看起来像源码缺失。
probe="$(windows-vm "$session" run "if ((Test-Path '$remote_root\src\driver\probe_ioctl.h') -and (Test-Path '$remote_root\src\common\redirect_context.h') -and (Test-Path '$remote_root\tools\wfprobe\wfprobe.csproj') -and (Test-Path '$remote_root\src\userland\WinForward.Userland.slnx') -and (Test-Path '$remote_root\tools\vm\userland-e2e.ps1')) { 'SYNC-OK' } else { 'SYNC-MISSING' }")"
case "$probe" in
    *SYNC-OK*) ;;
    *) echo "同步不完整：$probe" >&2; exit 1 ;;
esac
echo "synced $repo_root -> $remote_root"
