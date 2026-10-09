# 注释元数据清单（精确普查）

普查时间：2026-10-09（本任务开始前的工作树状态）
方法：Python 逐行扫描 `src/`、`tests/`、`benchmarks/` 下的 `.cs`，按注释行分类并匹配元数据模式
（任务名、`MM-DD-slug` 目录名、`YYYY-MM-DD` 日期、`design §`、`PRD`、`ACn`、条目号 `R1-B`/`B11`/`P3` 等）。
脚本 `/tmp/comment_scan.py`。

## 总量

| 维度 | 数量 |
| --- | ---: |
| 注释行总计 | 21,154 |
| 其中 XML 文档注释（`///`） | 15,784 |
| 其中普通注释（`//`） | 5,370 |
| 命中元数据模式的注释行 | 609 |
| 命中元数据模式的文件 | 234 |
| 元数据命中分布 | XML 374 行 / 普通注释 235 行 |

即：真正带任务档案引用的注释约占全部注释的 2.9%。其余 97% 是普通技术注释，其中一部分
存在与实现重复、过期失配的问题，但需要逐条判断而非机械匹配。

## 元数据最密集的文件（前 25）

| 命中数 | 该文件注释行数 | 文件 |
| ---: | ---: | --- |
| 32 | 129 | `benchmarks/WinForward.Benchmarks/Perf/SessionSetupDecompositionBenchmarks.cs` |
| 16 | 192 | `src/WinForward.Runtime/TcpRedirect/TcpProxyCoordinator.cs` |
| 15 | 85 | `src/WinForward.Runtime/TcpRedirect/TcpProxyRelay.cs` |
| 14 | 109 | `src/WinForward.Runtime/Capture/LayeredCaptureRunner.cs` |
| 13 | 83 | `benchmarks/WinForward.E2E/Client/Arms/UdpLatencyPolicy.cs` |
| 12 | 130 | `src/WinForward.Cli/DurableCaptureBundle.cs` |
| 12 | 174 | `src/WinForward.Runtime/Socks5/Socks5UdpTransport.cs` |
| 10 | 187 | `src/WinForward.Runtime/Socks5/Socks5ControlConnection.cs` |
| 9 | 62 | `src/WinForward.Cli/Program.cs` |
| 9 | 116 | `src/WinForward.Runtime/TcpRedirect/TcpRedirectSessionStore.cs` |
| 8 | 158 | `src/WinForward.Runtime/Socks5/Socks5UotTransport.cs` |
| 8 | 78 | `tests/WinForward.Runtime.UdpProxy.Tests/UdpUotFlowLifecycleTests.cs` |
| 8 | 54 | `benchmarks/WinForward.E2E/Client/Arms/LatencyTcpPolicy.cs` |
| 7 | 99 | `benchmarks/WinForward.E2E/Client/Arms/MixUdpBook.cs` |
| 7 | 93 | `benchmarks/WinForward.E2E/Client/Lanes/LaneEngine.cs` |
| 6 | 34 | `tests/WinForward.E2E.Tests/PlanFileValidationTests.cs` |
| 6 | 71 | `benchmarks/WinForward.E2E/Client/Lanes/ILanePolicy.cs` |
| 6 | 239 | `benchmarks/WinForward.E2E.Contracts/ArmKeys.Ledger.cs` |
| 5 | 74 | `src/WinForward.Runtime/Capture/NdisCaptureGeneration.cs` |
| 5 | 184 | `src/WinForward.Runtime/Capture/NdisPacketActionExecutor.cs` |
| 5 | 60 | `src/WinForward.Runtime/RuntimeHeartbeat.cs` |
| 5 | 153 | `src/WinForward.Runtime/UdpProxy/LocalUdpTransport.cs` |
| 5 | 52 | `tests/WinForward.E2E.Tests/Lanes/LatencyPolicyTests.cs` |
| 5 | 80 | `tests/WinForward.Performance.Tests/HotPathAllocationGateTests.cs` |
| 5 | 51 | `tests/WinForward.Runtime.Socks5.Tests/Socks5ProtocolTests.cs` |

## 典型病灶样本

任务名 + 条目号：

```
src/WinForward.Cli/DurableCaptureBundle.cs:17
  /// The durable capture layer (task 09-07-adapter-list-refresh, design §2): built once per run and
src/WinForward.Cli/DurableCaptureBundle.cs:127
  /// <paramref name="healthSignal"/> (task 09-17 R1-B) receives the interception-path failure
src/WinForward.Cli/Program.cs:245
  // (its forced-refresh trigger) — task 09-17 R1-B.
```

裸条目号（没有任务名，维护者更无从查起）：

```
src/WinForward.Cli/TcpRedirectComposer.cs:14
  /// The bundle-created collaborators the durable TCP coordinator is wired from (P3): the redirect
src/WinForward.Cli/DurableCaptureBundle.cs:149
  // One native pool backs retained SYNs and association reset templates (B1/B2); the
src/WinForward.Configuration/ConfigurationLimits.cs:116
  /// Normalizes the optional UdpSessionCapacity budget (R4): the bound on concurrent UDP
src/WinForward.Cli/Program.cs:333
  /// error 87 into its refresh channel (R3); transient-read retries ride the shared log gate.
```

设计文档章节号：

```
src/WinForward.Cli/Program.cs:230
  // repeating this resolution fail-closed for generation 0 (design §3.5/§3.6).
src/WinForward.Cli/DurableCaptureBundle.cs:147
  // session gate and the redirect table's bounded capacity (design §4).
src/WinForward.Runtime/Capture/NdisCapture.cs:363
  /// handler blocks this dedicated pump thread (documented deviation from design §3.1 — see the
```

日期与测量产物路径：

```
tests/WinForward.Runtime.TcpRedirect.Tests/SetupExecutorTests.cs:136
  // attempts (measured 2026-09-30: the host aborted on every run without the worker guard,
src/WinForward.Performance.Tests/SweepAllocationGateTests.cs:41
  /// <c>benchmarks/results/2026-09-30-expiry-sweep-bounded-pause/README.md</c>.
```

## 处理判据

- **删除**：任务名 / 条目号 / 日期 / `design §` / `PRD` / 测量产物路径等"档案指针"。
- **留下约束**：若同一句还承载当前代码的约束（容量下界、分配预算、超时值），保留约束本身，
  只去掉指针。例：`// One native pool backs retained SYNs … (B1/B2)` → `// One native pool backs
  retained SYNs …`。
- **不得**用同长度的新句子替换被删除的元数据——改写后应更短。

## 复核命令

```bash
rg -n '/[/]?.*(task [0-9]|20[0-9]{2}-[0-9]{2}-[0-9]{2}|design §|PRD)' --glob '*.cs' src tests benchmarks
rg -n '/[/]?.*\b[A-Z][0-9]{1,2}\b' --glob '*.cs' src tests benchmarks   # 需人工排除假阳性
```
