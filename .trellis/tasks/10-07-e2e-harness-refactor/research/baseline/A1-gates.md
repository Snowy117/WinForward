# A1 证据：测试工程骨架 + 五条门禁

对象：`tests/WinForward.E2E.Tests`（新建，72 个用例）、`benchmarks/WinForward.E2E/Wire/FrameStreamReader.cs`
（DD D14.18 的 internal 读委托 ctor）、`WinForward.slnx`、`benchmarks/WinForward.E2E/WinForward.E2E.csproj`（IVT）。

## 1. 五条门禁（全部在最终树上实测）

| # | 命令 | 结果 | 关键输出 |
|---|---|---|---|
| 1 | `benchmarks/WinForward.E2E/scripts/publish.sh` | 通过 | 三份产物；`linux/WinForward.E2E.dll` sha256 `5abca740d6c1cf85c91b42556e6be1f549d7f08d3d47933e814b1449924be7a7`；重复发布哈希一致（确定性构建） |
| 2 | `dotnet build WinForward.slnx -c Release` | 通过 | `Build succeeded. 0 Warning(s) 0 Error(s)` |
| 3 | `dotnet test tests/WinForward.E2E.Tests -c Release` | 通过 | `Passed! - Failed: 0, Passed: 72, Skipped: 0, Total: 72` |
| 4 | `cd benchmarks/WinForward.E2E && scripts/selftest.sh scripts/plans/selftest-plan.json` | 通过 | 退出码 0，11 条 `result` 记录 |
| 5 | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | 通过 | 退出码 0，**0 行输出** |
| 6 | `jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode2.xml WinForward.slnx` | 通过 | `<Issues />` 为空（0 个 `<Issue>`）；耗时 2m33s |

`jb` 首次运行报出 2 处并已按其建议修好（不是抑制）：`FrameCodecTests.cs` 的冗余 `using System.Buffers.Binary;`；
`RepoPaths.Root` 实为私有可达（`internal` 改 `private`）。再跑为 0。

工程设置核对：`IsTestProject=true`、`IsPackable=false`；`obj/project.assets.json` 中**没有** 4 个中央分析器包
（Meziantou / VSTHRD / Roslynator / Sonar），而 `benchmarks/WinForward.E2E` 仍保有这 4 个。

## 2. A1 的等价性证据（记录形状）

`FrameStreamReader` 是生产代码的唯一改动。把改动后的 selftest 产物与 A0 的 run1 用 A0 冻结的抖动带比对
（完整输出见 `A1-vs-run1-band-enforced.txt`）：

- **结构性差异 = 0**，**条件键差异 = 0**：903 条数值路径之外的一切（键集、类型、数组长度、字符串值、
  `target.out` 文本）与基线逐字相同。
- 903 条数值路径中 598 条与 run1 完全相同；305 条移动，其中：
  - 43 条是时钟键（`*Ticks`/`*Utc`/`ticks`/`wallSeconds`/`elapsedSeconds`）——两次运行相隔约 9.5 分钟；
  - 204 条是采样读数与延迟分位（`*Bytes`/`cpuSeconds`/`threads`/`pid`/`*Us`）；
  - 其余 58 条是速率、天花板、吞吐字节量一类的派生读数；其中只有 3 条是计数类：
    `THRU.metrics.frames`/`framesEchoed` 4877↔4876（run1↔run2 的带宽就是 1，落在带内）、
    `ledger.received` 的聚合均值（min/max 相同）。
- 契约计数器与 run1 **逐字节相同**：`DNS/DNSALT.sent=402 answered=402`、`LOSS.sent=supplied=arrived=receivedDatagrams=2001`、
  `LAT.tcp.sentOk=322 udp.sentOk=161`、`LATLOAD.tcp/udp.sentOk=1601`、`clientSendLoss=0`。
- 用 band 强制的整轮比对会报 478 条超带项：全部落在上面第 2 类（延迟/CPU/内存读数）与时钟键上——
  这是"两次运行带宽"用于**第三次**运行时的固有极限，不是 A1 的行为差异；判定按 D7 第 3 条需要
  "超出抖动带 **且** 命中改名表或语义清单"才算差异，本批次既没有改名也没有语义改动。

## 3. 用例清单

按文件（每文件 ≤ 139 有效行，AC1 上限 400）：

| 文件 | 用例数 | 覆盖 |
|---|---|---|
| `Crc32CTests.cs` | 10 | 空串=0、`"123456789"`=0xE3069283（RFC 3720 锚点）；长度 0/1/4/8/9/16/17 与全部 256 个单字节值对独立位实现 |
| `FillerTests.cs` | 6 | 固定 `(0x11223344,42)` 与零种子 `(0,0)` 的 16 字节黄金向量（独立算出）、Fill/Matches 互逆、`(uint)sequence` 截断事实 |
| `FrameCodecTests.cs` | 8 | 往返；BadMagic / BadLength（>4MiB）/ 头部与帧体 Truncated / BadChecksum 各一条；尾部多余字节被接受 |
| `DnsWireTests.cs` | 15 | 查询往返、空名被拒、压缩指针 QNAME、超长标签/空标签/非 ASCII 被拒、容量不足被拒、响应回填与指针、5 种 queryType 的 answerCount |
| `TcpCommandTests.cs` | 7 | 回归护栏：名字数 == 成员数、已定义成员不落兜底字面量；三态往返；非法 mode 字节与错长度被拒 |
| `LedgerWriterTests.cs` | 2 | 键序 `utc,label,type,value`、`\n` 结尾、UTC 可回环解析、`WriteErrors==0`；多次写入 = 多条记录 |
| `FrameStreamReaderTests.cs` | 6 | 一帧拆三段（3 次 read）、一包两帧、坏校验和跳过后续帧、**EOF 落帧中间 → 现状 `EndOfStream`（注释标明 E3 D9 将改为 `Truncated`）** |
| `LogHistogramTests.cs` | 6 | 空直方图、单值自成一桶、粗桶的排他上界（100000ns→100.031µs）、百分位单调且被 min/max 夹住、`long.MaxValue` 饱和到 2³⁴−1、<1ns 夹到地板 |
| `UdpReliabilityTrackerTests.cs` | 8 | 分类恒等式（arrived+late+never+undetermined+corruptDatagrams==sent）、未过窗=undetermined、`MarkSendRefused` 退出统计、重排/重复、接收侧越界（2⁶³ / −1 / Max+1）不崩且计入 `OutOfRange`、bitmap 边界 |
| `PlanFileTests.cs` | 4 | 内置 plan（8 臂、默认值）、**11 份 shipped plan 全部通过**、给定路径覆盖内置 plan |
| `RepoPaths.cs` | — | 从输出目录上溯 `WinForward.slnx` 定位仓库（沿用 `WinForward.Configuration.Tests` 的既定手法） |

**本批次有意未写**（属 A2/下一轮）：未知 key 被拒、`#6` 的 W 判据、任何会红的 Tier 0 用例、`TryLoad("")`、
`MarkSent(MaxSequence+1, …)` 的 D7 边界（它今天会红，归 0.1 与守卫同 commit）。

## 4. 复现

```bash
dotnet build WinForward.slnx -c Release
dotnet test tests/WinForward.E2E.Tests -c Release
cd benchmarks/WinForward.E2E && scripts/publish.sh && scripts/selftest.sh scripts/plans/selftest-plan.json
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore
jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx   # 解析 XML，不看退出码
```
