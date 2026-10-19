# 仪器有效性与归因：WinForward.E2E 端到端对比基准的设计审查

审查对象：`benchmarks/WinForward.E2E`（harness + orchestrator）、`benchmarks/WinForward.E2E.Analysis`（报告）、
`.trellis/tasks/10-06-e2e-competitor-benchmark/design.md`（设计）、本机 runbook `benchmarks/WinForward.E2E/AGENTS.local.md`。

范围是**设计层面**：这套装置作为一台测量仪器，它结构上能回答什么问题、不能支撑什么结论、哪些结论一旦被引用就会误导决策。
实现级清单不在此重复（见 `research/harness-audit.md`）。本文的每一条都给出可核对的锚点；凡我查不到的，写明"未能证实"。
所有整改建议都限定在现有的两台机器内（Linux 宿主 + Windows VM），不新增机器、不更换环境、不改网络拓扑。

---

## 结论摘要

1. **客户端同时是测量仪器和负载发生器，而且两者都在同一批 16 个逻辑核上。** RTT 样本的两端（预定发送时刻与接收打点时刻）
   全部由客户端自己的 `Stopwatch` 给出，产品多吃 CPU 会把这个减法的两端都推后。harness 侧没有任何亲和、优先级、
   headroom 断言或客户端侧延迟分解；唯一的成本显式化是 §6 的 `generator %vCPU` / `headroom %` 两列，它们既不是 verdict 指标，
   也不触发任何 finding。
2. **被测路径比 harness 自己的文档承认的多一跳：VM 上的 sing-box（`127.0.0.1:1080`）。** 这一跳对七个被测行全都存在，
   却在整个 765 行的 harness README 里一个字都没有。设计里承诺用于分离这一跳的 `BASE-singbox` 行**不存在于任何提交的 plan**，
   而且以当前的客户端形态（"从不说 SOCKS5"）**结构上做不出来**。因此"相对直连控制的增量"里，产品开销与隧道开销不可分离。
3. **跨机器归因只有钟，没有身份，而钟的一致性没有任何检查。** TCP 侧本可以按 `connectionId` 对身份，
   分析器的表注甚至在向读者承诺这个连接（`Tables/TableTcp.cs:88`），但代码里一次都没有读它。UDP 侧连身份字段都没有。
   已有实测证据表明：VM 钟偏 2.5–3.2 s 会把 `ledger-connection-mismatch` 从 13 条推到 90 条。
4. **分辨率与效应量真正错配的只有一格：`rel.unexpectedEofRate`。** 它的声明阈值是 0.1 pp，而提交的 plan 给它的样本量是
   2400 次尝试 → 全零格的 rule-of-three 上界 `3/2400 = 0.125 pp` **比阈值还大**，量子 `1/2400 = 0.042 pp` 是阈值的 42 %。
   其余格子（UDP 损失、延迟直方图、CPU）的分辨率都远细于阈值——这一点必须说清楚，否则会得到"整套仪器分辨率都不够"的过度结论。
5. **分母由被测对象自己决定的 headline 指标有五个**（`tcp-connect` 直方图、`meanConnectMs`、`udp.lossRate`/`strictLossRate`、
   `DNS answerRate`、`THRU goodputMbps`）。其中 `DNS answerRate` 的分母缩减量（`unsent`）不在任何 gate 的覆盖名单里，
   也不在任何表格列里——这是我找到的唯一一条在所有 gate 与全部披露清单里都没出现的分母缺陷。
6. **没有任何独立于本套 codec 的外部参照。** self-test 能证明"两端对同一份协议一致"，不能证明"测到的 RTT 是真实的线上往返时间"；
   帧头里 8 字节的 `clientSendTicks` 写进去以后没有任何读取点，仓库里也没有一次抓包、没有一次 ping/iperf 对照。
   设计阶段想过 iperf3 交叉核对（`benchmarks/research/2026-10-06-proxifier-comparison-design.md:145`），没有落实。

---

## 发现

### 1. 客户端是唯一的时钟源，同时又是被测产品的 CPU 竞争者

**证据**

* 样本的定义：`Client/Arms/LatencyTcpPolicy.cs:177` 与 `Client/Arms/UdpLatencyPolicy.cs:214` 都是
  `_rtt.Record(Clock.ToNanoseconds(receivedTicks - intended));`，`Client/Arms/MixUdpBook.cs:215` 同形。
  `intendedTicks` 来自 `Client/Pacer.cs:18` `IntendedTicks(index) => _startTicks + index * _ticksPerRequest`（纯算术，无测量）。
* 接收打点：`Client/Lanes/LaneEngine.cs:250` `_policy.OnReceive(received, destination.Span[..received.Length], Clock.Now);`
  ——时刻在 receive 线程的 `await` 返回之后才读，续体何时被调度进入这个数。
* 发送侧：`Client/Pacer.cs:34/37/40`，等待同一时刻用 `Thread.Sleep((int)ms - 6)`（>8 ms）、`Thread.Sleep(0)`（0.2–8 ms）、
  `Thread.SpinWait(32)`（<0.2 ms）。pacer 自己就是一个随速率增长的 CPU 消费者；`Client/Lanes/LaneEngine.cs:119-121`
  还刻意用阻塞版本，因为 `Task.Delay` 会把 15.6 ms 的 Windows 定时器 tick 带进来。
* 客户端侧延迟被明确计入样本：`benchmarks/WinForward.E2E/README.md:707`（"Latency is measured from each request's
  intended instant, never from the instant it was actually sent … the percentile includes the pacing lateness"）。
* 没有任何隔离：在 `benchmarks/WinForward.E2E` 全树搜索 `Affinity|ThreadPriority|Priority|SetProcessPriorityClass|IdealProcessor`
  **零命中**；`Client/DedicatedThread.cs:8-13` 只设了 `LongRunning | DenyChildAttach`，没有优先级。
* 唯一的成本显式化：`Client/ResourceSampler.cs:12`（1 Hz）、`:142`（每个 tick 写 `generatorCpuSeconds`，取自
  `Process.GetCurrentProcess().TotalProcessorTime`，即客户端整个进程）。分析侧 `Stats/CpuDetail.cs:49` 读它，
  `Tables/TableCpu.cs:34` 定义 `headroom % = 100 × (P×100 − generator %vCPU − proxy %vCPU) / (P×100)`，`:208` 计算。
* 但它不是断言：`Metrics/MetricCatalogue.cs` 里 CPU 家族只有一条 `cpu.proxy.vcpuPct`；在 `Analysis/Findings` 全目录搜索
  `generator|headroom` **零命中**。也就是说"生成负载把自己 CPU 烧光了"这件事在报告里既不打印判决，也不产生 finding。

**后果**

机制是这样的：被测产品多吃一个核的 CPU（用户态 + 它触发的 DPC/ISR）→ 客户端的 receive 线程续体排队更久 →
`receivedTicks` 更晚 → RTT 更大。方向恒为正，且随产品 CPU 用量单调。
要说清楚的一点是：**被拖慢的不是时钟**。`Stopwatch` 底层是 QPC/invariant TSC，频率不随负载变化，所以"产品的 CPU 把测量时钟拖慢了"
这个说法是错的；进入偏差的是**打点那两行代码何时被执行**。

由此读者会得到两个错误印象：

* "该产品在这一栏更慢"——其中一部分是仪器被同一台机器上的被测产品挤出来的，不是路径成本。
* "高 CPU 与高延迟互相印证"——CPU 列只统计被采样进程自己的时间（`Analysis/README.md:330`），内核里的 DPC/ISR 不计；
  两个列会以互相印证的姿态出现，而它们对同一个机制其实是"一个瞎、一个偏"。

**边界（这条能被怎么反驳）**：我**不**主张必然发生。harness-audit 第 19 条实测各产品占 5.22–25.82 % 单核，16 逻辑核上不存在整机饱和；
真正可疑的机制是线程级调度与 DPC/ISR 抢占，而这两者在本套计数器里都不可见。所以我的主张是：
**这套设计产生的数据无法排除这个偏差**，因为它既没有 headroom 断言、也没有客户端侧发送/唤醒延迟的分解、也没有同刻的 null-load 对照。
要反驳我，只需要指出这三者之一存在；它们都不存在。

**两台机器内可落实的整改**

1. 在 `OnSent`（`ILanePolicy` 已经能拿到 `intendedTicks`）处记录**实际发送 tick**，按臂发布一张发送延迟直方图（例如 `gates/sendDelay`）。
   这样"延迟格里的多少是 pacer 迟到、多少是路径"第一次可分离，且改动只在客户端。
2. 发布 `Settle(Clock.Now)` 相对 `receivedTicks` 的滞后分布，暴露 send 线程自身的拥塞。
3. 给客户端进程设 `AboveNormal`，把 lane 线程绑定到 CPU 子集，把剩下的核留给产品；再加一条 headroom gate：
   某臂的 `generator %vCPU` 超过声明值，就把该臂的延迟格标为 instrument-contaminated 而不是照常印数。
4. 加一个校准控制：客户端内建 `--inject-cpu-load <pct>`，用无产品的控制块跑一次"同机多烧 N % 一个核"的形状，
   直接量出"同机竞争者"折算成 RTT 是多少。这正是 `.trellis/spec/backend/benchmark-methodology.md` 里
   "A wall-clock maximum is not an acceptance figure when the host queueing dominates it" 与 `--sweep-window-control-ms`
   那类 calibration control 的做法，也呼应同文件 "The real-dial harness: the server must not share the measured process" 的同一类要求。

**归类：设计缺陷。**（部分症状已作为列披露，但没有任何隔离与断言）

---

### 2. 被测路径上有一跳报告里不存在：sing-box；没有一行能只测它

**证据（逐跳）**

1. 客户端进程（`C:\wfbench\e2e\WinForward.E2E.exe`，`orchestrator.ps1:36`）被产品规则抓取——例如
   `scripts/configs/wf-aot-opt.json:60-70` 的 `WinForward.E2E.exe → proxy → main`。
2. 产品的上游是 VM 本机的 SOCKS5：`scripts/configs/wf-aot-opt.json:8-14`（`Host 127.0.0.1` / `Port 1080`），
   `scripts/configs/proxifyre-app-config.json:9`（`socks5ProxyEndpoint: 127.0.0.1:1080`），
   `scripts/configs/proxybridge.pbprofile:6-11`（`socks5` / `127.0.0.1` / `1080`）。
   Proxifier 行**没有仓库配置文件**（README:327），其端点未能证实。
3. sing-box 由编排器在整轮 campaign 里常驻，不在任何一行前后重启：`orchestrator.ps1:167-173`（`Start-SingBox`）、
   `:414`（预检启动，失败即 abort）、`:466`（只有 teardown 才停）。每行的流量证据来自它的日志：`Get-ProxyTruth`（`:193-220`）。
4. 之后是 sing-box 的 `direct` 出站、跨 vSwitch 到 Linux 靶机：`orchestrator.ps1:26` `-TargetAddress = '192.168.100.4'`。
   **这里要修正任务书里的一个前提**：实际承载测试流量的是 **Internal** 网段 `192.168.100.0/24`（`AGENTS.local.md:11`、`:16`），
   不是 External 的 `192.168.77.0/24`（`:12`）。`design.md:4-18` 恰好相反：它声称靶机必须在 `192.168.77.4`，并把
   `192.168.100.0/24` 判为 "carries WinRM for the whole campaign and is never in any product's interception scope"。
   也就是说，"Linux 外网段"这一跳在现有证据下是 Internal vSwitch，而且设计文档与部署事实互相矛盾。
   我没有找到任何一处文档解释这个反转，**该矛盾未能证实其成因**。

**`BASE-singbox` 确实不存在**

* `design.md:82` 声明了 `BASE-singbox | control: explicit SOCKS5, no product | TCP+UDP | 60 s | attributable proxy increment`，
  `design.md:153` 的计划里也排了它。
* 提交的六个 plan（`scripts/plans/*.json`）里唯一的 base 臂叫 `BASE`（`scripts/plans/base-plan.json:4-5`），
  搜索 `singbox|sing-box` 在这些 plan 里零命中。编排器用 `base-plan.json` 跑的是**没有加载任何产品**的控制块
  （`orchestrator.ps1:385-388`）。
* 此时客户端直连靶机，因为 README:23 明确写着 "The harness never speaks SOCKS5 and never impersonates a proxy."
  客户端的旗标表（README:169-176）里也没有任何代理选项。**所以这一行不只是"没写"，而是当前客户端形态下做不出来。**

**后果**

* 行与行的成对比较里，隧道成本是常数项，会抵消——"谁更快"的排序结论不受影响，这一点应当承认。
* 但 "ratio-to-control" 这一族不是：它的分母是一条**没有 SOCKS5 跳、没有第二个进程**的路径。任何
  "X 产品相对直连增加 N %" 的说法，N 里都混着 sing-box 那一跳与它的同机 CPU。
* `design.md:226` 把这个风险的对策写成 "`BASE-singbox` makes the product-attributable increment explicit"。
  该行不存在，风险敞口是敞开的：报告读起来像"这是产品的成本"，实际是"产品 + 隧道的成本"。
* harness 自己的 765 行契约文档（README）从头到尾没有出现 "sing-box"（全树搜索零命中）。只读 harness README 的人
  会得到"透明代理重写路径"的单跳模型（README:23-24），而真实路径是两跳加一次本机 SOCKS5。

**两台机器内可落实的整改**：给客户端加 `--socks5 <host:port>`（先只做 CONNECT），控制块里跑一次 `BASE-singbox`。
sing-box 已经常驻、已经配好 `direct` 出站，不需要新机器、不需要改拓扑。UDP 侧要么实现 `UDP ASSOCIATE`，
要么诚实地把"UDP 隧道项未测、不可减"写进报告；后者是零成本的。

**归类：设计缺陷。**（`design.md` 已经识别了风险也开了对策，只是没有实现）

---

### 3. 控制组没有被证明是"地板"

**证据**

* `orchestrator.ps1:379-390` 的 `Invoke-ControlBlock` 只做四件事：`Assert-CleanSlate`、建目录、跑 `base-plan.json`、touch 心跳。
  对比 `Invoke-Row`（`orchestrator.ps1:344-372`）：它额外做 `Clear-SingBoxLog`（`:360`）、跑完写 `proxy-truth.json`（`:367-368`）。
* `Assert-CleanSlate`（`:153-165`）发现残留拦截器时只 `Write-Log` 一行 WARNING（`:163`），**从不加入 `$script:Failures`**
  （对比 `Test-ClientRun` 与 `Invoke-DualPhase` 都会 add）。
* 这行 WARNING 只走 stdout，而运行配方把它重定向到 `C:\wfbench\orch.log`（`AGENTS.local.md` 第 8 节），**不在 `-OutRoot` 里**；
  分析器读的是 `environment.json`、`order.txt` 与行目录，读不到它。
* `Stop-Product`（`:133-146`）自己的注释承认这个风险："A driver can outlive its user-mode client and keep filtering,
  which would contaminate the next row."

**后果**

控制行是 ratio-to-control 的分母，也是 `control-pre` / `control-post` 漂移检查（`Analysis/README.md:463` 一带、
`Analysis/Checks/ControlDrift.cs`）的输入。一个残留驱动会让**控制行变慢**，于是每个产品都显得更快——
而且结果树里没有任何一条记录能让读者发现这件事。读者会得到"控制组正常、产品差异可信"的印象，证据却只存在于一份
不随结果归档的 stdout 日志里。

**整改（零成本，两台机器内）**：clean-slate 失败进 `Failures` 并写进 `environment.json`；控制块也清 sing-box 日志、
也写一份 `proxy-truth.json`（期望 `tcp/udp/utcp/directLeak` 全 0）。这样"控制流量没经过代理"从一个假设变成一条可检查的事实。

**归类：设计缺陷。**

---

### 4. 跨机器归因只有钟，没有身份；而 TCP 的身份线已经存在、报告还在表注里承诺用了它

**证据**

* 唯一的桥：`Client/ClientRunner.cs:35-36`（`var runStartTicks = Clock.Now; var startedUtc = DateTimeOffset.UtcNow;` 相邻两行）、
  README:390（"`run.json`'s `startedUtc` and `startedTicks` are written next to each other: that pair is the only bridge"）、
  `Analysis/Model/RunClocks.cs:80-93`（`ArmUtcWindow = startedUtc + (armTicks − runStartTicks) / frequency`）、
  `Analysis/README.md:381-382`。
* 一致性没有任何检查：`AGENTS.local.md:188` 自己写着"**两台机器的 UTC 时钟一致**（这条脚本查不了）"；
  在 `WinForward.E2E.Analysis` 全树搜索 `clock|skew|offset|drift`，只命中类名与变量名，没有任何偏移检查。
* 灵敏度是实测过的：`AGENTS.local.md:191-193`——VM 快 2.5–3.2 s 时 `ledger-connection-mismatch` 90 条；
  把账本整体平移 +3 s 重跑，掉到 13 条。即"这条交叉校验的结果"是"一个未经验证的钟"的函数。
* 身份线存在但没人用：靶机的 `tcp` 账本记录带 `connectionId`（`WinForward.E2E.Contracts/ArmKeys.Ledger.cs:83`），
  REL 臂的 `attempt` 记录也带（`ArmKeys.Reliability.cs:214`）。而 **`Analysis/Tables/TableTcp.cs:86-88` 的表注写着**
  "one `type: \"attempt\"` line per disconfirming attempt, joining a client observation to the target ledger's verdict
  through the same `connectionId`"。在 `WinForward.E2E.Analysis` 全树搜索 `connectionId|ConnectionId` 的 `.cs` 命中**只有这一句表注**。
  没有任何代码读它。**报告在向读者承诺一个不存在的身份连接。**
* UDP 侧连身份字段都没有：`udpSummary` 是 1 Hz 聚合、无逐包记录（README:360、`Analysis/README.md:383-388`），
  只有 `sources[{address,port,datagrams}]` 的端点普查——而在代理车道上，那个源端口是 sing-box 的出口端口，
  不是客户端的，所以它也不能当作客户端的身份。

**后果**

VM 钟偏几秒 → 每个臂的窗口整体错位几秒 → 窗口边缘落进相邻臂的流量 → 计数系统性偏离。错误不是随机的，
而是系统性的，并且在结果里长得像"产品差异"。读者会以为"TCP 的客户端观察与靶机判决是逐连接对上的"（表注就这么写），
实际它们只按时间窗口对齐——表注与实现不一致这件事，甚至让"去看一眼源码确认"这条常规怀疑路径失效。

**关于 `w32tm /resync` 的定性**：手工对时本身是**可接受的工程折中**（两台机器、无第三方 NTP 基础设施）。
缺陷不在"靠对时"，而在于：它既没有被断言、也没有被记录，分析器也无法在窗口错位时报警（它只会报 mismatch 计数，
读者必须自己想到去看钟）。所以这条的归类是：**对时=只能如实披露（但必须升级为前置断言）**，
**缺少身份连接=设计缺陷**。

**两台机器内可落实的整改**

1. 把 TCP 的 `connectionId` 连接真正实现——两侧数据齐备，只差代码。
2. UDP：让靶机按帧头里的 `connId` 做逐 connId 计数（`Wire/FrameCodec.cs:51-65`，u32 由客户端自选，靶机为了校 CRC
   本来就要解头），客户端把自己的 connId 写进结果。臂归属从此不再需要钟。
3. 每行重启两个靶机实例、用行标签写账本（`--label`，README 的 target 旗标表），至少把 **run 归属**从钟里拿出来。
4. 在跑批前后各记一次两机的 `Get-Date` / `date -u` 差值，写进 `environment.json`，让钟偏成为可读数字而不是口头约定。

**归类：设计缺陷。**

---

### 5. 分辨率与效应量：只有一格真的错配，而且错在阈值比测量下限还小

**证据**

* 阈值：`Metrics/MetricSpec.cs:72` `TcpUnexpected = new(Kind: "diff", Value: 0.1, Text: "0.1 pp TCP unexpected rate")`；
  家族含 `rel.unexpectedEofRate`、`rel.fidelityRate`、`persist.responseRate`（`Metrics/MetricCatalogue.cs:82-107`），
  前者分母 `metrics/connectAttempts`。
* 样本量：`scripts/plans/full-plan.json` 的 REL 是 `seconds: 120` × `connectionsPerSecond: 20` = **2400** 次尝试。
* 全零格的上界：`Tables/TableTcp.cs:114-115` 用**每轮 attempts 的中位数**做 rule-of-three 的分母；
  `Stats/DescriptiveStats.cs:76-87` 把它印成 `< 3/n = x %`。于是 `3/2400 = 0.125 pp`。
* 量子：`1/2400 = 0.042 pp`，是阈值的 42 %。验证形态更差：`scripts/plans-windows/full-shape-plan.json` 的 REL 是 24 s → 480 次 → `3/480 = 0.625 pp`。

**必须同时说清楚"哪些格子是够的"**，否则会得到过度结论：

* UDP 损失：LOSS 500 包/s × 120 s = 60000 → 量子 0.0017 pp、上界 0.005 pp，阈值 0.5 pp，余量两个数量级
  （`Tables/TableUdp.cs:53-67` 的字段表、`:158` 的中位数分母）。
* 延迟：`Client/LogHistogram.cs:59` 的对数直方图是每倍频程 2048 个子桶，且百分位打印的是桶的**排他上界**（`:56`，永不低估），
  相对分辨率约 0.05 %，远细于 5 % 的延迟阈值。
* CPU：1 Hz 采样下，`Process.TotalProcessorTime` 的 15.625 ms 更新粒度折算到一格是
  `100 × 0.015625 / 59 ≈ 0.027 %vCPU`，比实测跨度（5.22–25.82 %vCPU，harness-audit 第 19 条）低两个数量级。
  **所以"1 Hz 采样分辨不出 CPU 差异"这个担心不成立**；CPU 栏真正的问题是口径（内核态不计，harness-audit 第 19 条），不是分辨率。
* LAT 的窗口：提交的 plan 里 LAT 与 LATLOAD 都是 `"window": 4096`（`full-plan.json`），**不是文档默认的 64**
  （README:249 那一带的默认表）。按 `gates/inFlightCeilingMs = window × lanes / achieved rate`（README:519），
  LATLOAD 500 rps 时上限 8.19 s，低于直方图 17.18 s 的饱和点；`windowOverflow > 0` 会把该臂延迟格渲染成 `n/a`
  （README:519）。这一格是设计好了的。
* 直方图 17.18 s 饱和（README:667-669）：只影响尾部；判决用的是 30000 样本上的 p50/p99，中位数不受影响。
  归"只能如实披露"，且已被覆盖。
* MIX 的 `dns-rtt` 用 `Socket.Available` + `Task.Delay(1)` 轮询（`Client/Arms/MixPageLoop.cs:229-235`），
  给一个亚毫秒的 DNS RTT 加上最多一个 15.6 ms 的定时器 tick。这一格的分辨率比它要测的效应粗 4 个数量级，
  但它不是 verdict 指标（`MetricCatalogue` 里 MIX 只有 `mix.udp.lossRate`），只出现在延迟表里，且 `Analysis/README.md:366-370` 已如实披露。

**后果**（对 `rel.unexpectedEofRate`）

* 两行都是 0 时，数据只把真实率约束到 0.125 pp 以内，而报告会拿 0.1 pp 判 "same"——**结论的精度超过了数据的精度**。
* 反过来，要判 "different"，需要一行出现 ≥3 个事件。
* 读者会得到"这几行的 TCP 意外率没有差别"的印象；这句话在统计上不成立。

**整改**：把 REL 的 `seconds` / `connectionsPerSecond` 提到能支撑 0.1 pp（`n ≥ 3000`，例如 20/s × 180 s = 3600 → `3/n = 0.083 pp`），
或者把该家族的声明阈值改成计划能分辨的值（0.25 pp）并在 verdict 里带上 `3/n`。前者只改一个 plan 数字。

**归类：设计缺陷。**

---

### 6. 分母由被测对象自己决定的 headline 指标清单

共同结构是：**分母 = 客户端成功完成的那部分工作，而"成功"由被测对象决定。** 两个分母不同的行并列在同一张表里时，
那个比值既不是"每单位工作的成本"，也不是"每单位尝试的成本"。

| # | 指标 | 分母 | 谁在决定它 | 有没有被拦住 | 归类 |
|---|---|---|---|---|---|
| 1 | `latency.tcp-connect` 直方图 | 成功连上的探测 | 连接失败/挂起的产品贡献 0 个样本 | `metrics.tcp.connectFailures` 没有任何 gate 读、没有表打印 | 已被 harness-audit 覆盖；`Analysis/README.md:356-364` 已披露 |
| 2 | `metrics.meanConnectMs` | 连上的尝试 | 同上 | 同上；`Analysis/README.md:365-372` 说明不能与第 1 项并列引用 | 已被覆盖 |
| 3 | `LOSS.lossRate = (late + never)/sent`、`strictLossRate` | `sent`（socket 接受的发送数） | 窗口溢出、发送失败、teardown 残留、超界序列都不进 `sent`，而是进 `clientSendLoss`（`Client/Arms/LossArm.cs:76`；`:111-112` 是两条率的定义） | gate 只查 LOSS/LAT/LATLOAD/BASE（`Analysis/Tables/GateValidity.cs:80`）；**MIX 的真实 `metrics/clientSendLoss` 不在名单里** | 部分设计缺陷（gate 覆盖），部分如实披露 |
| 4 | `DNS.answerRate = answered/sent`（`Client/Arms/DnsArm.cs:109`） | `sent` | `gates/clientSendLoss = Unsent`（`DnsArm.cs:58`），而 `unsent` 的定义是"在途窗口满而被跳过的配速槽位，那些查询根本没到 socket"（`DnsArm.cs:65-66`） | DNS **不在** gate 名单里（`GateValidity.cs:80`）；`Tables/TableGates.cs:56-58` 的列只有 LOSS/LAT/LATLOAD；分析器不读臂的 `notes`（全树搜 `/notes` 只命中表头与合成树） | **设计缺陷（本文新增）** |
| 5 | `THRU.goodputMbps` | 由 `targetBytesPerSecond` 封顶的字节预算 | 是 harness 自己（`full-plan.json` 是 60 000 000，README:249 的文档默认是 25 000 000） | `Analysis/README.md:408-413` 已披露"上限之上无区分度"；它无阈值家族，只有 CI | 已被覆盖；但"60 MB/s 现在是否仍高于所有行"**未能证实**（campaign 数字不在树里） |
| 6 | rule-of-three 的分母本身 | 每轮分母的**中位数**（`TableUdp.cs:158`、`TableTcp.cs:114`） | 各轮分母差异大时上界偏乐观 | 无 | 如实披露 |

**后果**：第 4 条最具体——一个把 DNS 拖到窗口满的产品，其 `answerRate` 的分母被自己的慢缩小了，
而报告里既没有 `unsent` 这一列、也没有这一行的 gate 失败，读起来像"应答率还行"。
第 3 条的现状比 harness-audit 当时描述的要好：`Client/Arms/LossArm.cs:55` 的注释表明槽位现在在 `intended + W` 释放、
不再等一个永不来的应答，所以正常情况下 `sent == supplied`；这条缺陷从"必然失真"降级为"只在 gate 失效时才失真"。

**整改**：给每个率在表里印上它的分母（不是只印在规则的说明里）；把 DNS 加进 `clientSendLoss` 的 gate 名单。

---

### 7. 没有任何独立于本套 codec 的外部参照：self-test 只能证明自洽

**证据**

* harness 自己写明了边界：README:15-18（"A green self-test means the harness works. It says nothing about any product."）。
* 帧头偏移 16 的 `clientSendTicks`（`Wire/FrameCodec.cs:51`、`:65` 写入）**没有任何读取点**：靶机侧全树搜索
  `ClientSendTicks|SendTicks` 只命中写侧与两个同名局部变量；客户端的接收路径也不读它
  （`LatencyTcpPolicy.OnReceive` 明确忽略 payload 与头）。所以 RTT 完全是一个进程用自己的钟做的减法，
  没有任何端到端时间戳校验，也没有第二个观察者。
* 仓库里的 "frozen oracle" 是**分析器**的参照，不是测量的参照：`Analysis/verification/oracle-diff.py` 比对 C# 输出与冻结的
  Python 参考，`verification/synthetic/make_tree.py` 造一棵 "follows the harness's schema" 的合成树。
  两者都以同一份 schema 为准——校验的是读的人，不是写的人。
* 没有任何抓包：在这些材料范围内搜索 `tshark|wireshark|pcap|tcpdump|dumpcap|iperf|nping|ping`，只命中别的任务的文档
  与一条无关注释。
* 设计阶段想过：`benchmarks/research/2026-10-06-proxifier-comparison-design.md:145` 的 `THRU-tcp` 行写着
  "harness (iperf3 only as a coarse cross-check)"。没有落实。

**后果**

self-test 能证明的是：两端对同一份 codec/协议一致、plan 能加载、记录形状与 gate 能在环回路径上跑绿、
RTT 是个有限的数。**不能**证明的是：这个数等于真实的线上往返时间。而在真实 campaign 里，两者的差正是
"产品 + 隧道"要背的那部分。读者会得到"RTT 是实测的"印象；它实际是"一个进程内两次读钟之差"，
其正确性依赖 codec、pacer、接收线程打点三处的正确性，而这三处没有外部对照。

**缺口（两台机器内可落实）**：Win11 自带 `pktmon`（无需安装），Linux 靶机侧有 tcpdump。
抓一次 LAT 臂，就能把线上的相邻帧间隔与直方图对照，并用帧头里的 `seq`/`connId` 对齐到具体样本。
`ping` 只能校准主机到主机的网络跳（ICMP 不经重定向），不能验证产品路径，但可以把"固定网络项"从 RTT 里界定出来。
在这一次对照做出来之前，所有**绝对**延迟数字都应当归入"只能如实披露"。

**归类：设计缺陷（缺一个可落实的对照）；在对照做出之前，绝对延迟值归"只能如实披露"。**

---

## 与 harness-audit 的关系

* 那份是**实现级**清单：三条已修 bug、十三条跑批前必修、三条公平性、若干披露，关心的是"某个计数器会不会算错"。
  本文关心的是"这台仪器结构上能支撑什么结论"。两者只有四处重叠，且都在本文里一句话带过：
  `tcp-connect` 的样本总体（audit 第四节第一条）、MIX `dns-rtt` 的轮询、直方图 17.18 s 饱和、CPU 只算用户态（audit 第 19 条）。
* **本文新增、audit 未覆盖**：客户端作为竞争者的自我强化偏差与缺少 headroom 断言（发现 1）；sing-box 那一跳与
  `BASE-singbox` 的缺席，以及 harness README 从未提到它（发现 2）；控制块没有被证明是地板（发现 3）；
  `connectionId` 表注与实现的脱节、钟一致性无检查（发现 4）；`rel.unexpectedEofRate` 的阈值-分辨率错配（发现 5）；
  DNS `unsent` 分母不在任何 gate 与任何披露清单里（发现 6）；帧头 `clientSendTicks` 无人读、无外部参照（发现 7）。
* **一处修正 audit 的说法**：audit 第 4 条说"报告的 `sent` 在不同行之间差 7 倍"。那是修复前的代码形态；
  当前 `Client/Arms/LossArm.cs:55` 与 `:76` 表明槽位在 `intended + W` 释放，`sent` 不再被产品的丢包直接压缩。
  这条从"必然失真"降级为"只在 `clientSendLoss > 0`（即 gate 已经失败）时才失真"。
* **两处修正任务书里的前提**：LAT 的 `window` 在提交的 plan 里是 4096，不是 64（64 只是 loader 默认值）；
  承载测试流量的是 Internal 网段 `192.168.100.0/24`，不是 External 的 `192.168.77.0/24`
  （`AGENTS.local.md:11/16` 与 `orchestrator.ps1:26` 对 `design.md:4-18` 的反转，成因未能证实）。
