# B2b 期间 WinLTSC 崩溃证据（2026-10-21/22）

## 结论

- 屏幕是 `0xc000021a`（启动失败 / Automatic Repair），该错误不产生内核 dump。
- 可分析的内核 dump 是 `0x11D EVENT_TRACING_FATAL_ERROR`，Arg1=7（Trace buffer corruption）；
  栈为 `nt!EtwpValidateBuffer` ← `EtwpDequeueFreeBuffer` ← `EtwpReserveTraceBuffer` ←
  `EtwpLogKernelEvent` ← `PerfLogImageLoad` ← `IopInitializeSystemDrivers` ← `IoInitSystem`，
  发生在启动早期加载驱动映像时。
- 崩溃内核的已加载模块列表中没有 `wfprobe.sys`；服务不是 boot-start；WER 模块列表也没有
  wfprobe。SrtTrail 的 root causes = 0。
- 事件日志显示该 VM 此前多次 41/6008 非正常关机，属于既有不稳定环境。**目前证据不支持把
  本次崩溃归因到 B2b 驱动。**
- B2b 的功能验证在崩溃前已完成；崩溃后实现者拉取了证据并卸载了驱动。

## 证据位置

本机：`/home/paff/wfprobe-crash-2026-10-21/`
- `analysis.md`、`minidump.dmp`、`summary.txt`、`system-events.txt`、`wer-events.txt`、
  `SrtTrail.txt`、`Report.wer` 等。

VM：`C:\Windows\Minidump\102126-6390-01.dmp`、`C:\Windows\MEMORY.DMP`、
`C:\workspace\crash-evidence\`。

## 对 B3 的影响

- 不要依赖反复重启来取干净池基线；先用 VM checkpoint，或在同一启动内用零基线 + 差值。
- B3 的最终 gate 必须在稳定、可复现的 VM 状态上跑；如果再次出现 0x11D/0xc000021a，先按
  环境问题隔离（卸载本项目驱动/回退快照）再判定是否与本驱动相关。
