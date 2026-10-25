# MVP 性能初测（E2E harness，THRU 臂）

日期：2026-10-21
目的：MVP 跑通后，用原仓库的 `benchmarks/WinForward.E2E` 取第一组性能数据，与 2026-10-18 CPU 分析的现状对比。

> **先看最后一节**：直连中继的初测（90.4%）与经真实 sing-box 的复测（81.3%）是两次不同的测量，与基线同形的是后者。

## 1. 方法

沿用原仓库 harness 的 THRU 臂，参数对齐 CPU 分析那一轮：

| 项 | 值 |
|---|---|
| 计划 | `{"name":"THRU","kind":"throughput","seconds":30,"streams":4,"targetBytesPerSecond":60000000}` |
| 帧 | 32 KiB |
| 目标速率 | 60 MB/s ≈ 480 Mbps |
| 采样 | `--sampler-process WinForward.Controller`，1 Hz 读 `Process.TotalProcessorTime` |
| 客户端 | `WinForward.E2E client --target 192.168.100.2 --tcp-port 19000` |

被测路径：客户端连接 → WFP `ALE_CONNECT_REDIRECT` 改写到本地监听端口 → 控制器 accept、读 redirect context → 直连原始目的地 → 双向中继。

## 2. 结果

| 指标 | 值 |
|---|---|
| goodput | **479.92 Mbps**（目标 480） |
| 帧 | 发出 54878、回显 54876、`corrupt=0`、`protocolErrors=0` |
| 传输字节 | 1,799,932,800 |
| 中继侧 | 4 条连接，`bytes_in=1,799,998,548` / `bytes_out=1,799,998,400` |
| **`WinForward.Controller` CPU** | **90.4%**（单核百分比；26.20 CPU-s / 28.98 s） |
| 负载生成器 CPU | 434.2%（harness 自身成本，不与产品比较） |

与 2026-10-18 CPU 分析的对照：

| 场景 | 单核 CPU |
|---|---:|
| 旧代理路径（NDIS + sing-box SOCKS5） | 154.0% |
| 旧纯透传（NDIS 抓包 + 回注，不过 sing-box） | 58.1% |
| sing-box 同轮 | 47.0% |
| **新架构（WFP 重定向 + 直连中继）** | **90.4%** |

## 3. 可比性限制（必须一起读）

1. **不是同一条路径**。新栈目前只做**直连中继**，没有 SOCKS5 上游。旧「154%」是经过 sing-box 的完整代理路径，旧「58.1%」是包级抓取 + 回注、没有用户态 TCP 中继。90.4% 介于两者之间是预期的——它替换掉的是"每包成本"，代价是新引入的"每连接一次的用户态中继"。
2. **目标端在 VM 内部**。宿主防火墙阻断了 VM → 宿主的所有 TCP（ICMP 通、TCP 全超时，且无免密 sudo 可开端口），所以 target 只能起在 VM 里（`192.168.100.2:19000`）。这意味着数据不过物理/vNIC 链路，网络栈成本低于基线那轮，**90.4% 可能低估了真实链路下的数字**。
3. **单次运行**，未取多轮中位数。基线那轮也是单轮，但样本量同样为 1。
4. 内核侧成本按设计接近 0（每连接一次 classify），实测该轮 `rules_replaced` 等计数与中继无关，CPU 全部落在用户态中继上。

## 4. 与 kill criteria 的关系

Phase 0 的判据是「480 Mbps THRU 代理路径的进程 CPU 不能降到 110% 以下，就停止完整重写」。

本轮 90.4% 已经低于 110%，落在当初对"用户态 relay"的预测区间（85–100%）内。但**这不能当作 kill criteria 的直接结论**——判据说的是代理路径，而本轮跑的是直连中继。要真正判定，需要在「真实链路 + SOCKS5 上游」下复测一次。

结论：**方向成立，数值在预期区间**；正式判定留给带 SOCKS5 的复测。

## 5. 环境事实（供后续复测参考）

- 宿主 `192.168.100.4` 与 VM `192.168.100.2` 同网段，ICMP 通，但**所有 TCP 端口从 VM 侧均超时**（测过 19000 / 30053 / 30006 / 30010）。
- 因此跨机测量需要宿主放行端口（需 root），或把 target 放进 VM。
- 跨机那一版的失败现场保留在 findings 里：`relay error: OperationCanceledException`，`bytes_in=0`——中继出站连不上，客户端只看到协议错误。这本身就是"出站不可达时客户端无诊断信息"的一个实例。
---

# 附加：经真实 sing-box 的复测（与基线同形）

---

## 上游的来历

按 `WinForward` 仓库 `benchmarks/WinForward.E2E/AGENTS.local.md` §5 的指导，从源码构建：

- sing-box `v1.14.2` + `SagerNet/sing@87c33f17688f`，打补丁
  `.trellis/tasks/10-06-e2e-competitor-benchmark/research/sing-socks-pipelined-payload.patch`
  （SOCKS5 握手后流水线字节被 `bufio.Reader` 吞掉）
- 补丁自带的 `TestPipelinedRequestPayloadReachesHandler`：打前 FAIL、打后 PASS
- 另编了一个**撤销补丁的对照版**：同一流水线测试下，打过补丁的回显 41 字节完整帧（26 ms），
  未打补丁的读超时——证明补丁确实解决了这个问题
- 配置与仓库模板 `scripts/singbox/base-singbox-socks5.json` 逐字节相同

## 结果

| 项 | 值 |
|---|---|
| goodput | **479.88 Mbps**（目标 480） |
| 帧 | 发出 54874、回显 54874、`corrupt=0`、`protocolErrors=0` |
| 中继 | 4 条连接全部 `via=socks5:127.0.0.1:1080`，`bytes_in=1,799,998,548` |
| **`WinForward.Controller` CPU** | **81.3%**（23.55 CPU-s / 28.96 s） |
| **`sing-box` CPU** | **53.0%**（15.36 CPU-s / 28.95 s） |

与 2026-10-18 基线的对照：

| 项 | 旧（NDISAPI 包级 + sing-box） | 新（WFP 重定向 + sing-box） | 变化 |
|---|---:|---:|---:|
| 代理路径进程 | 154.0% | **81.3%** | **−47.2%** |
| sing-box | 47.0% | 53.0% | +12.8% |
| 两者合计 | 201.0% | 134.3% | −33.2% |

`sing-box` 这一行是环境校验：同一个二进制、同样的配置与目标，53.0% 对 47.0% 偏高 12.8%，
说明这台 VM 当前状态与当年并非完全一致（构建工具链是 nixpkgs 的 go1.26.7，官方 release 用的不是它），
因此 81.3% 也应带同样的折扣看，不宜当作精确复现值。

## 关于 kill criteria

Phase 0 的判据是「480 Mbps THRU 代理路径的进程 CPU 不能降到 110% 以下就停止完整重写」。

本轮**代理路径 = 81.3%**，与基线同形、同一 harness、同一计划、同一台 VM。判据满足，且留有约 26% 的余量。

## 可比性限制（这一轮的）

1. **sing-box 的构建链不同**（源码自建 vs 官方 release），其 CPU 偏高 12.8% 提示环境有差异。
2. **单次运行**，与基线同样只有一轮样本。
3. 目标在宿主机、客户端与控制器在 VM，跨 vNIC——这一点与基线一致。
4. 上游在 `127.0.0.1:1080`（loopback），与基线一致。

---

# 附加二：内核态 CPU、内存与延迟

日期：2026-10-21

## 1. 内核态 CPU 是否被计入

**是，而且占多数。**

harness 的 `Client/ProcessCounterSource.cs:153` 读的是 `process.TotalProcessorTime`，Windows 上它等于
`UserProcessorTime + PrivilegedProcessorTime`，即用户态与内核态之和。

为了不靠推断，本轮用独立采样器（250 ms 一次，231 个样本，59.77 s）把两者分开读：

```
total      26.84 s  (44.9 %)
  user      6.89 s  (11.5 %)
  privileged 19.95 s (33.4 %)
校验 total - (user + privileged) = 0.0000 s
```

**控制器的 CPU 有 74.3 % 落在内核态**。这是中继的必然结果：一次 `recv`/`send` 的协议栈处理在同一线程上下文里执行，成本记在该进程的 privileged 时间里。

### 但有一个盲区必须说明

`TotalProcessorTime` 只覆盖「本进程线程上下文里」的内核时间。有两类成本不计入：

1. **驱动替别的进程干活**。WFP callout 的 classify 发生在发起 `connect()` 的那个客户端线程上，成本记在**客户端**进程，不是 `WinForward.Controller`。
2. **DPC / 中断**。网络栈的延迟过程调用记在系统账上，不属于任何进程。

本轮的 THRU 臂只建立 4 条连接、LAT 臂 31 次 connect，逐连接的内核成本在构造上就接近 0，所以这个盲区对吞吐数字没有影响。**若要验证「每连接成本确实被新架构压低了」，需要跑 REL 臂（20 conn/s）并配合全机测量（ETW 或 `\Processor(_Total)`），只看产品进程会漏掉驱动那一份。** 这一项本轮没有做。

## 2. 内存

LAT + THRU 两臂，231 个样本：

| 指标 | p50 | min | max |
|---|---:|---:|---:|
| privateBytes | **13.31 MB** | 7.75 MB | 16.35 MB |
| workingSet | 35.42 MB | — | 37.95 MB |
| handles | 334 | — | 353 |

## 3. 延迟（LAT 臂，30 s，20 req/s，120 B）

| 指标 | 值 |
|---|---:|
| `tcp-rtt` p50 | **4056 µs** |
| `tcp-rtt` p90 | 9589 µs |
| `tcp-rtt` p99 | 10973 µs |
| `tcp-connect` p50 | 2664 µs |
| 平均建连 | 1.527 ms |

## 4. 与 Proxifier 的对比

Proxifier **没有装在 VM 上**（当初就要求用户通过 RDP 手动安装），因此无法用同一把尺子现场测。只能引用
`benchmarks/results/2026-10-19-release/README.md` 已发布的、相对参考行 `wf-aot-nativeudp`（旧产品 AOT 构建）的比值。
由 `Metrics/MetricCatalogue.cs` 的定义反推绝对值：

| 指标 | 旧产品（参考行） | Proxifier（比值 × 参考） | 本轮新架构 |
|---|---:|---:|---:|
| `mem.privateBytes.p50` | 37.307 MB | **166.5 MB**（×4.4630） | **13.31 MB** |
| `lat.tcp_rtt.p50` | 3919 µs | **3641 µs**（×0.929） | **4056 µs** |
| `cpu.proxy.vcpuPct` | 23.582 % | **61.95 %**（×2.627） | 见下 |

读法：

- **内存我们小一个数量级**。相对 Proxifier 约 1/12.5，相对旧产品约 1/2.8。这是 .NET 托管进程与
  大型商业代理在常驻集上的差距，不是这一轮的优化成果，但值得记一笔。
- **延迟我们比 Proxifier 慢 11.4 %**（4056 µs 对 3641 µs），比旧产品慢 3.5 %。这一点如实记下：
  新架构在延迟上没有优势，WFP 重定向 + 用户态中继多了一次地址改写与一次进程边界穿越。
- **CPU 不可比**。`cpu.proxy.vcpuPct` 的定义是「整轮负载臂（排除 IDLE）的平均」，覆盖 LAT/LATLOAD/DNS/
  LOSS/REL/THRU/MIX/PERSIST 全部臂；而本轮 81.3 % 只覆盖 THRU 单臂，44.9 % 只覆盖 LAT+THRU。
  窗口不同，直接摆在一起会误导。**要真比，得在同一份完整计划上跑，而我们 v1 只承载 TCP，DNS/UDP 臂跑不了。**

## 5. 这一轮遗留的测量缺口

1. 逐连接的内核成本没有单独测量（需要 REL 臂 + 全机计数）。
2. 与 Proxifier 的 CPU 对比缺同一窗口的数据；Proxifier 未安装，本轮无法补。
3. 延迟与内存均为单轮。
日期：2026-10-21（同一天，初测之后）

初测是直连中继，与基线的「WinForward → sing-box」不同形。这一轮把上游补齐，路径与 2026-10-18 那轮一致：

```
WinForward.E2E client (VM) → WFP 重定向 → WinForward.Controller (VM) → 127.0.0.1:1080
  sing-box 1.14.2-singfix (direct outbound) → 192.168.100.4:40000 target (宿主)
```

---

# 附加三：延迟分解——那 11% 到底是什么

日期：2026-10-21

上一节把「比 Proxifier 慢 11.4%」当成结论记了下来。这一节做三段式分解，结论要修正。

## 做法

同一个 LAT 臂（30 s、20 req/s、120 B、601 次请求），三种拓扑各跑一遍：

| 标记 | 拓扑 |
|---|---|
| A-direct | 不装驱动，客户端直连宿主 target |
| B-relay | 驱动重定向 → 控制器 → **直连**中继 → target（无 SOCKS5） |
| C-socks5 | 驱动重定向 → 控制器 → **SOCKS5** → sing-box → target |

## 结果

| 拓扑 | `tcp-rtt` p50 | p90 | `tcp-connect` p50 |
|---|---:|---:|---:|
| A-direct（无代理） | **3813 µs** | 8671 µs | 1579 µs |
| B-relay（无 SOCKS5） | 3512 µs | 9220 µs | **1001 µs** |
| C-socks5（全链路） | **4422 µs** | 9511 µs | 1549 µs |

## 读法

1. **测量本身的底噪是 3.8 毫秒**。完全不装代理的直连，RTT p50 已经是 3813 µs。这 3.8 ms 由 target 的处理时间、Hyper-V vNIC 的一次往返、以及 harness 在 20 req/s 下的调度共同构成，与代理无关。

2. **WFP 重定向 + 用户态中继增加的开销在噪声量级**。B 的 3512 µs 比 A 的 3813 µs 还低 300 µs——方向反了，说明这个差值是噪声，不是信号。可靠的读数只有 `tcp-connect`：B 的 1001 µs 明显低于 A 的 1579 µs，因为重定向把 TCP 握手搬到了 loopback，这一段确实变快了。

3. **真实开销来自 SOCKS5 那一跳**：C − B = **+910 µs**。这是 sing-box 作为独立进程处理每条请求的代价，任何经过上游代理的架构都要付，与 WFP 无关。

4. **因此上一节「慢 11.4%」的结论应当撤回。** 其真实含义是：在 3.8 ms 的底噪之上，旧产品（3919 µs）与 Proxifier（3641 µs）的差值本身就小于基线噪声。Proxifier 给出的 0.929 倍甚至低于我们的**无代理基线**，这在物理上不成立，只能说明跨 campaign 比较被环境差异主导。跨轮次比较延迟，在这个靶场设置下没有分辨力。

## 要有分辨力，得这么测

- **靶机放到本机**（loopback target），把 vNIC 往返从底噪里去掉；
- 或者**同一次会话内测差**：A 作为基线，报 B − A、C − A，而不是拿 C 去比别人 campaign 的 C；
- 每条拓扑多轮取中位数，现在的单轮样本在同一拓扑内的波动就有 300 µs 量级。

## 对「换掉 WFP 重定向能不能改善延迟」的结论

**不能，因为重定向不是瓶颈。** 它把握手搬到 loopback（connect 反而更快），数据面多加一次进程边界，但在 3.8 ms 底噪下测不出。真正贵的是 SOCKS5 上游那一跳，那是架构选择，不是重定向机制的代价。
