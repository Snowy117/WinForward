# Implementation plan — adapter list refresh

Ordered checklist; each step ends compiling + tests green (rollback point = prior step's commit
state). Validation baseline after every step:

```
dotnet build WinForward.slnx && dotnet test WinForward.slnx
```

## Steps

- [x] S1 ABI + driver surface（2026-09-07 完成；单方法零值释放，报告见会话）
- [x] S2 Watcher seam（Linux 可测 wait core + fake source；15 测试）
- [x] S3 UDP target source（快照换血 + fail-closed 语义保留；10 测试）
- [x] S4 Scope resolver refresh overload（共享匹配核心 + fatal 参数化；9 测试）
- [x] S5 Generation runner（18 测试锁 AC1-AC5；no-op 判定在停代之前）
- [x] S6 CLI wiring（Program.cs 295 有效行 + DurableCaptureBundle.cs 142；555/555 绿）
- [x] S7 Hardware smoke（2026-09-08 完成，证据见 smoke-evidence.md；Win11 开发机经 WinRM）
      External 禁用/启用：87 降级 → 77 ms 后 adapter.refresh removed(present=false) → 重新启用
      后 added（非受限收养），进程存活、WinRM 穿笼连接跨刷新不断，NCSI 探测被重新拦截（恢复
      实证）。LAC* 8 隐藏网卡：零事件零降级（成员变化不重建绑定列表）。R-2 auto-reset 驱动
      语义 VERIFIED；R-1 刷新期代际停机两次硬件验证（终局 Ctrl+C 无法在无头远程会话注入，
      记录为环境限制）。收尾强杀 + Internal 清洗，滤波状态干净恢复。
- [x] S8 Wrap-up — spec 部分（windows-ndisapi.md 新增 "Adapter list change refresh" 契约章节）；
      trellis-check 已过（5 维度 Pass + 3 处顺手修复，门禁 0 警告 / 555 测试绿 / format 触碰
      文件零违规）；S7 证据已落盘 → commit 就绪。

## Review gates

- After S5: design-conformance review (layer boundaries, storm guard, no-op skip) before CLI
  rewiring.
- After S6: full-scope `trellis-check` (spec compliance, lint, typecheck, cross-layer flow).
- S7 evidence recorded before commit (S8).

## Rollback

- Each step is an independent commit candidate; revert to the last green step.
- Feature has no persisted state; runtime behavior before first list change is identical to
  today's code path except the watcher registration.
