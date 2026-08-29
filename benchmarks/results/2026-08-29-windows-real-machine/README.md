# 2026-08-29 Windows 真机测试战报

一次性横跨两台机器的完整验证：Linux 开发机上的基准与浸泡，加上 Windows 真机上从驱动加载到真实代理流量的全链路测试。本文是原始数据的索引与分析结论。

## 环境

| 项 | Linux 端 | Windows 端 |
|---|---|---|
| 机器 | 开发机（AMD Ryzen 9 9955HX） | 192.168.100.2，远程 WinRM |
| 系统 | Ubuntu 24.04.4 LTS | Windows 11 IoT Enterprise LTSC 10.0.26100 x64 |
| 运行时 | .NET 10.0.10 | .NET 10.0.10（自包含发布） |
| 角色 | 稳定性浸泡 + SOCKS5 服务端（sing-box, 端口 30890, auth） | 被测机：驱动路径、真实代理、浸泡 |
| 驱动 | 不适用（managed-only） | ndisrd 服务运行中，ndisapi.dll x64 侧车加载正常 |

- 代码版本：`969a2ce`（benchmark 重写之后）。
- Windows 侧制品为 `dotnet publish -r win-x64 --self-contained /p:PublishAot=false /p:PublishSingleFile=true`（Linux 无法交叉编译 Native AOT，测试用自包含单文件替代，不影响结论）。
- WinRM 会话为提权会话（`LocalAccountTokenFilterPolicy=1`）。
- 测试网络上游存在一台 fake-IP 路由器（DNS 回答落在 198.18.0.0/15），DNS 内容异常源于它，与 WinForward 无关。

## 原始文件

| 文件 | 内容 |
|---|---|
| `stability-linux-full.jsonl` | Linux `--stability --scenario all --duration 60 --pps 25000` |
| `stability-windows-full.jsonl` | Windows 同参数完整浸泡 |
| `winforward-pass-smoke-run.log` | Windows 全 pass 30 秒真机运行日志（info 级） |
| `winforward-proxy-run-debug.log` | Windows 真实代理运行 debug 日志（42 事件，0 警告 0 错误） |

Windows `--quick`（15s/10000pps/64flows/16 并发）数字未单独存档，关键值收录于下表，以 full 运行为准。

## 功能验证（全部通过）

| 测试 | 结果 |
|---|---|
| `validate` 非法配置 | exit 1，逐字段诊断（`socks5Servers: Field is required.` 等） |
| `validate` 合法配置 | exit 0，`tcpFlowCapacity: 4096` |
| `adapters` | exit 0，枚举 2 块网卡（External/Internal），NDISAPI 侧车 DLL 加载正常 |
| 30s 全 pass 运行（无规则，fallback=pass） | 进程存活，捕获→pass→重注入闭环；ping 30/30 零丢包；日志 3 行生命周期记录干净；Stop-Process 硬杀后 2 秒内网络恢复（驱动自动回收过滤器） |
| 真实 TCP 代理（curl TLS → cloudflare） | exit 0，TLSv1.3，出口 IP 为 Linux 侧 IPv6（2406:da18:...）——流量确实经由 SOCKS5，Windows 本机无 IPv6 出口，构成铁证 |
| 真实 UDP 代理（nslookup → 8.8.8.8） | exit 0，UDP ASSOCIATE 路径全程走通，含 IPv6 目的（fd00::1:53）经代理往返 |
| 代理链路吞吐（10MB × 3，LAN 内对照） | 25–30 MB/s 稳定（首次 11 MB/s 冷启动）——中继路径无性能问题 |
| 代理期间系统健康 | debug 日志 42 事件 0 警告；WinRM 自身流量（5985）被 LAN pass 规则正确放行，会话全程存活 |

## 稳定性浸泡对比（同参数：60s / 25000pps / 256 flows / 64 并发）

| 指标 | Linux | Windows | 评注 |
|---|---|---|---|
| UDP 丢包率 | 0.19% | **2.47%** | 见问题 1 |
| UDP 实际 pps（目标 25000） | 22391（89.6%） | **6914（27.7%）** | 见问题 1 |
| UDP sendLoopOverflows | 18 | **1952**（约 1/3 tick 超时） | 见问题 1 |
| UDP 乱序 / 重复 | 0 / 0 | 0 / 0 | 两平台均无 |
| TCP 完成率 | 25.00%（70799/283428，精确命中 clean 权重） | 23.72%（5350/22555） | 均符合对抗混合预期 |
| TCP otherErrors | 0 | **8.2%（1849）** | 见问题 2 |
| TCP 单次传输均值 | 13.5 ms | 150.3 ms | Windows 循环 + 换联开销，量级差异主要来自 22555 vs 283428 的吞吐差 |
| 会话足迹（1000 会话） | 9.79 MB，0 次 gen0 GC | 10.39 MB，0 次 gen0 GC | 均无泄漏迹象，工作集零增长 |

Windows `--quick` 参考：UDP 丢包 0.33% @ 6144 pps（904 overflows）；TCP otherErrors 18%（1978，样本为空）；完成率 25.34%。

## Perf 摘要（Linux，BenchmarkDotNet ShortRun，详见根目录 BenchmarkDotNet.Artifacts/）

- 解析热路径：IPv4/UDP 解析 12–21 ns 零分配；SOCKS5 UDP span 编解码 20–45 ns 零分配。
- 流表：miss 43–117 ns、跨网卡 hit ~155 ns，全零分配，基数 0–65535 无退化。
- `NdisPacketBuffer` 复用 7–31 ns 零分配（对比 new+dispose 106–122 ns）。
- 捕获泵端到端：200k 包 ~64–69 ms（≈3M pps），分配恒定 1.86 MB。
- TCP 中继：8KB 块 16.7 ms/256KiB 量级，块 ≥1024 后吞吐平台。

## 待解决问题（后续任务素材）

1. **Windows UDP 发送路径吞吐与丢包**（最重要）：同参数下 Windows 实际发送速率仅为 Linux 的 31%（6914 vs 22391 pps），且 1/3 的 10ms tick 错过截止期（1952 overflows），已发数据报丢失 2.47%（Linux 0.19%）。怀疑方向：a) 发送循环定时器粒度（Windows 默认时钟 15.6ms vs 10ms tick）；b) 环路 socket 缓冲在落后时的压力放大；c) 发送路径本身在 Windows 上的每包成本。注意 README 约定：饱和前丢包 >0 即值得调查。
2. **TCP 高频换联撞端口池**：64 并发持续换联在 Windows 上产生 8.2% `WSAEADDRINUSE`（`SocketException: Only one usage of each socket address...`）。这是 Windows 动态端口池 + TIME_WAIT 的已知行为，README 的 `tcpFlowCapacity`（默认 4096）正是为此设计；压测器具自身也 consuming 端口，需分辨被测件与器具各自占比。
3. **未验证项**：真实控制台 Ctrl+C 优雅停机（本次仅硬杀，依赖驱动回收兜底）；小时级长浸泡；AOT 发布产物真机验证（本次为非 AOT 自包含）。

## 复现

```text
# Linux（开发机）
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario all --duration 60 --pps 25000 --output stability-linux-full.jsonl

# Windows（自包含单文件发布后，提权会话）
.\WinForward.Benchmarks.exe --stability --scenario all --duration 60 --pps 25000 \
  --output stability-windows-full.jsonl
```

功能冒烟流程：`validate` → `adapters` → 全 pass `run` 30 秒（ping 旁路监控）→ 代理 `run`（LAN pass + 其余走 sing-box 30890）→ curl TCP / nslookup UDP / 10MB 吞吐对照。
