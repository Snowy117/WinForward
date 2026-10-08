# A0 baseline (recorded before any script authoring)

recorded-at-utc: 2026-10-06T17:34:42Z

## git rev-parse HEAD
d90707ebe7a9560a497eaa7f5ea499d7ba00bb91

## git status --porcelain (state BEFORE writing any A0/A1 file)
 M .trellis/tasks/10-07-tcp-close-drain/check.jsonl
 M .trellis/tasks/10-07-tcp-close-drain/implement.jsonl
 M .trellis/tasks/10-07-tcp-close-drain/prd.md
 M .trellis/tasks/10-07-tcp-close-drain/task.json
?? .trellis/tasks/10-06-e2e-competitor-benchmark/research/arms-code-quality-audit.md
?? .trellis/tasks/10-06-e2e-competitor-benchmark/research/wire-target-code-quality-audit.md
?? .trellis/tasks/10-07-e2e-e1-contracts/
?? .trellis/tasks/10-07-e2e-e2-structure/
?? .trellis/tasks/10-07-e2e-e3-semantics/
?? .trellis/tasks/10-07-e2e-e4-analyzer/
?? .trellis/tasks/10-07-e2e-e5-docs/
?? .trellis/tasks/10-07-e2e-harness-refactor/
?? .trellis/tasks/10-07-tcp-close-drain/design.md

## published binary sha256
35802188398c3a0074f3ae16078a1f8f3473f12e32622bdca765429e38da893d  /tmp/wf-bench/pub/linux/WinForward.E2E

## publish log (tail)
    0 Error(s)

Time Elapsed 00:00:01.65

=== publish ===
linux        WinForward.E2E WinForward.E2E.deps.json WinForward.E2E.dll WinForward.E2E.pdb WinForward.E2E.runtimeconfig.json 
win          WinForward.E2E.deps.json WinForward.E2E.dll WinForward.E2E.exe WinForward.E2E.pdb WinForward.E2E.runtimeconfig.json 
win-direct   WinForward.E2E.Direct.deps.json WinForward.E2E.Direct.dll WinForward.E2E.Direct.exe WinForward.E2E.Direct.pdb WinForward.E2E.Direct.runtimeconfig.json 

=== the two Windows clients must be distinguishable by image name ===
/tmp/wf-bench/pub/win-direct/WinForward.E2E.Direct.exe
/tmp/wf-bench/pub/win/WinForward.E2E.exe

## Correction (2026-10-07, after A1): which hash identifies the code

`publish.sh` 输出的 `WinForward.E2E` 是 108 KB 的原生 apphost，哈希**不随代码变化**：A0 发布与
A1 改动后重新发布得到同一个 `35802188…893d`。受管代码在 `WinForward.E2E.dll`：

| 树 | `WinForward.E2E.dll` sha256 |
|---|---|
| A0 基线（HEAD `d90707eb`，独立 worktree 重建验证） | `2eb371625f7f055c594d1de3f77069a5be545cdc5ec9d7cdfaeade810fa51a58` |
| A1 之后（`FrameStreamReader` 的 internal 读委托 ctor + IVT） | `5abca740d6c1cf85c91b42556e6be1f549d7f08d3d47933e814b1449924be7a7` |

验证方法：`git worktree add --detach /tmp/wf-base-wt HEAD` + `dotnet publish -c Release -r linux-x64`，
两次发布的 apphost 哈希相同而 DLL 哈希不同，确认 apphost 不是代码标识。
