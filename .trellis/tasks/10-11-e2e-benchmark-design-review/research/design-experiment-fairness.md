# E2E 竞品基准的设计审查：实验设计与公平性、工作负载代表性

审查对象：`benchmarks/WinForward.E2E/`（harness）、`benchmarks/WinForward.E2E.Analysis/`（分析器与报告）、
`benchmarks/WinForward.E2E/scripts/orchestrator.ps1`（campaign 驱动）、
`.trellis/tasks/10-06-e2e-competitor-benchmark/design.md`（campaign 原始设计）、
`benchmarks/research/2026-10-06-proxifier-comparison-design.md`（同期、且更严格的兄弟设计）。

本文只做**设计层面**的批判：这套仪器声称能回答什么、结构上不能支持什么结论、哪些结论一旦被引用就会误导决策。
实现级缺陷（计数器算术、窗口、崩溃路径）不在此重复，见 `.trellis/tasks/10-06-e2e-competitor-benchmark/research/harness-audit.md`。

辅助证据：`/tmp/wf-bench/` 下的一次单轮验证 campaign（`val/pass1`，7 个产品行 + 1 个控制行，每行有自己的 `config*` 副本）；
`benchmarks/WinForward.E2E.Analysis/verification/golden/py-tables.md` 是**合成树**的金标准，本文只用它核对表格结构，其数字不是真实测量。

---

## 结论摘要

1. **七行是七个因子束，不是七个处理。** 行与行之间同时变了产品实现、构建方式（AOT/FDD）、UDP 承载（UoT/native/bypass）、
   驱动栈（ndisrd/ProxifierDrv/WinDivert）、进程模型（常驻进程/Windows 服务）、配置、plan 与 DNS 路径。
   除 `wf-aot-opt` 与 `wf-fdd-opt` 这一对外，任何行间差异都不能归因到单个因子；而报告的表结构（一行一个产品）会让读者把它读成产品排序。
2. **配置是处理的一部分，但配置选择政策从未被声明。** 三个竞品各只有一份由 WinForward 团队写的配置；
   WinForward 有四个行，头部行跑的是**非默认**的 opt-in UoT 模式（`UdpOverTcp` 默认 `false`），跑产品默认 UDP 承载的那一行只跑 3 个臂；
   而配置直接决定最大一处排除的 Proxifier 行，是全仓库唯一没有配置文件的行。
3. **MIX 臂按字节 81% 是一条恒定速率 bulk 流，UDP 只占 0.5%。** 相对团队自家兄弟设计所依据的带引用行为模型，
   video 与 realtime UDP 各被放大 4 倍，QUIC、TCP/53、CNAME、DNS 背景查询全部缺失，三条被标为 mandatory 的敏感性臂一条都没实现；
   而且**报告不打印 MIX 的组成参数**，"日常流量"这个名头撑不起来。
4. **时长上限决定了可观测现象的集合。** 最长的臂 120 s、整套 plan 870 s、没有 warmup、没有 soak；
   而产品自己的性能/稳定性计划的验收仪器是 1 小时 soak 与高 churn 端口池归因。连接表增长、端口耗尽、清扫与内存的长时行为在这套基准里结构上不可见。
5. **三种 UDP 承载被放进同一列比较，前提没有被声明；MIX 臂的 `dns-rtt` 还在多数行上是直连样本。**
   前者让"UDP 精度"的跨行排序不再是同一物理量的比较，后者把 §5 延迟表里的一格变成了路径混合的产物——两处都靠手写声明而非测量来区分。
6. **没有阳性对照，也没有盲法。** 仪器从未被证明能测出已知量级的退化（`--inject-*` 只覆盖 loss 臂的两个计数器，campaign 从不传、测试也不引用；
   设计里的 `BURST-udp` 标定臂没实现）；配置、跑批、分析、排除规则出自同一批人，`pre-declared threshold` 的可信度只等于一次无盲法的预注册。

---

## 发现

### 1. 七行是七个因子束，行间差异不可归因

**证据**

- 行定义在 `benchmarks/WinForward.E2E/scripts/orchestrator.ps1:78-110`：七个行各自绑定 `Exe`/`Args`/`ConfigSource`/`Plan`/`Dual`。
  把它和 `benchmarks/WinForward.E2E.Analysis/Model/RowProfiles.cs:151-199` 的 profile 表并排看，行间同时变化的是：
  产品实现（五个不同程序）、构建（AOT vs framework-dependent）、UDP 承载（`proxied-utcp` / `proxied-native` / `not-carried`）、
  驱动栈（`benchmarks/WinForward.E2E/AGENTS.local.md` 第 5 节：`ndisrd` / `ProxifierDrv` / `WinDivert`）、
  进程模型（常驻 CLI 进程 vs Windows 服务）、配置作者、plan（`full` / `udp only` / `dns only`）与 DNS 路径。
- orchestrator 自己的注释只对 WinForward 行做了归因声明："The matrix is arranged so that each WinForward row differs from the
  reference row by at most one feature, which is what makes a delta attributable"（`orchestrator.ps1:9-12`）。
  这句话不覆盖头部比较（WinForward 行 vs 竞品行），而头部比较正是报告的主表。
- 唯一被设计成单因子对比的是 `wf-aot-opt` 与 `wf-fdd-opt`：配置文件逐字节相同（`wf-aot-opt.json` 与 `wf-fdd-opt.json` 的 sha256 同为 `dc9972e4…`），
  plan 相同、驱动相同。

**后果** 报告里 "WinForward AOT 的 `LOSS lossRate` 比 ProxyBridge 低 3.3 pp" 这类句子会被读成产品结论，
但它实际是"（我们的配置 + UoT + ndisrd + 常驻进程）比（我们的配置 + native relay + WinDivert + 常驻进程）"。
这条缺陷让**所有跨产品行间差异的因果解释**不可信，具体地是"某个机制（内核重定向 / 用户态拷贝 / UDP 承载）导致了差异"这一类结论。
不看它的话，读者会把一个因子束的排序当成产品能力的排序。

**可被推翻的条件** 如果报告在每个跨产品结论旁都写明"这是整机方案对比、不可归因到单因子"，这条就只剩措辞问题；
今天的 `RowProfiles.Config` 只描述配置内容，不描述选择规则，也不限制结论的归因强度。

**归类** 设计缺陷。

**整改（两台机器内可落实）** 结论降级为"方案对比"，并在报告 §1 的 row 表里加一列"与哪一行构成最小对比"。
若要保住因果语言，只需再加一两个行（例如把 native-UDP 配置也跑 `full` plan），它们跑在同一台 VM 的同一个 pass 里，成本约每 pass 多 15 分钟。

### 2. "只改一个因子"在隔离行上被 workload 变化破坏

**证据**

- `wf-aot-nativeudp` 跑 `udp-plan.json`（`orchestrator.ps1:88-91`）：该 plan 的 `LAT`/`LATLOAD` 是 `"protocol": "udp"` 的单协议臂；
  参考行 `wf-aot-opt` 跑 `full-plan.json` 的 `"protocol": "tcp+udp"`。两行的 `LAT` 不是同一个实验条件。
- `wf-aot-dnsrelay` 只跑 `dns-plan.json`（`DNS` + `DNSALT`）。
- 这两行 `Dual = $false`（`orchestrator.ps1:90,95`）：没有直连车道证据，没有 `IDLE`/`MIX`/`PERSIST`/`REL`/`THRU`，
  内存与 CPU 主表里没有它们的格子。
- `RowProfiles.cs:55-63` 的 `PlanArms` 把这件事固化成设计：`udp only` 就是 `LAT/LATLOAD/LOSS`，`dns only` 就是 `DNS/DNSALT`；
  分析器据此把未跑的臂渲染成 `not measured in this row`（`Metrics/MetricCell.cs:55-67`）。

**后果** `wf-aot-nativeudp` 与 `wf-aot-opt` 之间唯一算得上"只改了承载"的格子是 `LOSS`（两个 plan 的 `LOSS` 形状一致：120 s / 500 pkt/s / 200 B）；
`LAT`/`LATLOAD` 的对比同时改了协议组成。而"UoT 与 native 在 CPU、内存、长连接上的差别"没有可比对的数据——
尽管这两行本来就是为了回答这个问题而存在的。不看它的话，读者会以为这四个 WinForward 行构成一张可以逐因子读的表。

**可被推翻的条件** 如果有人证明 `udp-plan` 的 UDP-only 臂与 `full-plan` 的 tcp+udp 臂在 UDP 类上的测量条件等价（同一时刻的其它流量不影响该类的 loss/latency），
这条只剩后者一半；本审查未找到这样的论证，且 `MIX`/`THRU` 的并发负载差异使等价性难以成立。

**归类** 设计缺陷。

**整改** 把两个隔离行的 plan 换成 `full`（至少补上 `IDLE`/`MIX`/`PERSIST`），代价是每 pass 多两行的时间；同一台 VM、同一套 plan 机制内的改动。

### 3. 配置是被测对象的一部分，但配置政策从未声明，而且我方头部行是非默认模式

**证据**

- 六份配置全部由本仓库提供（`benchmarks/WinForward.E2E/scripts/configs/`），对手行各只有一份。
  分析器的 row 表把配置写成一句话，例如 `proxifyre` = "SOCKS5 rule for the client, supportedProtocols TCP+UDP"、
  `proxifier` = "SOCKS5 profile, process rules (no UDP support in the product)"（`RowProfiles.cs:179-199`），
  但没有一句话说明"这份配置是怎么选出来的"。
- WinForward 头部行的 UDP 承载是 opt-in 的非默认模式：`src/WinForward.Configuration/ConfigurationModels.cs:119` 把 `UdpOverTcp` 称作
  "the opt-in UoT v2 connect-mode"，`:128` 的默认值是 `false`；`wf-aot-opt.json` 把它设为 `true`，
  行名里的 `-opt` 与 README 的措辞（"`wf-aot-opt` — the reference build"，`benchmarks/WinForward.E2E/README.md:314`）都表明这是被选中的那一份。
- 这份头部配置还含一条只对本基准有意义的 DNS 捷径：`LocalTargets[dns] = 192.168.100.4:53` 加一条 `udp + remotePort 53` 规则，
  使 port-53 UDP 完全绕过 SOCKS5（`scripts/configs/wf-aot-opt.json`）；分析器自己把它标为 `direct-local-target`（`RowProfiles.cs:85`）。
- Proxifier 那一行没有仓库配置：README 明说 `bench.ppx` "is a GUI export that exists only on the machine"（`README.md:327`）。
  只有本地 `/tmp/wf-bench/bench.ppx` 与验证 campaign 里的副本（`/tmp/wf-bench/val/pass1/proxifier/config.ppx`）——
  而这两份还不一样：当前副本多了一条直连车道的 `Direct` 规则。
- 没有任何文件声明"对手按厂商推荐配置跑"。可核对的是：ProxiFyre 的 `"bypassLan": false` 与厂商默认一致
  （wiresock/proxifyre README 记 v2.2.0 起该项默认 `false`，https://github.com/wiresock/proxifyre/ ）；
  Proxifier 的 `<Udp mode="mode_bypass"/>` 与该产品"不处理 UDP、UDP 直连"的公开行为一致
  （https://app.cyberyozh.com/guides/proxy-setup/Proxyclients/proxy-set-up-in-proxifier/ 、https://geonode.com/proxies-for/proxifier ）。
  除此之外**未能证实**任何一行是"厂商默认配置"；ProxiFyre 的 `app-config.json` 是否等于厂商样例也**未能证实**（仓库里没有厂商包）。
- 归档机制本身是可靠的：orchestrator 在每次运行后把该行的生效配置复制进结果目录（`orchestrator.ps1:370`），
  分析器对它做 sha256 并打印成 "Per-row effective configuration hashes"（`Tables/TableEnvironment.cs:62,255-270`）。

**后果** "WinForward 比 ProxiFyre 快"实际是"我们为自己的产品选的配置比我们为对手写的配置快"。
这条让任何**产品级**结论不可信。不看它的话，读者会默认各方都在各自的推荐配置下被测量。

**可被推翻的条件** 一份写明的配置选择规则（"这份是厂商样例 / 这份是文档推荐值 / 这份是我们为暴露某能力而写"）加每行的配置出处，
就能把这条降级为"披露不足"。当前仓库里不存在这份声明。

**归类** 设计缺陷（需要配置政策声明 + 关键对手配置可复核）；非默认能力的使用属于只能如实披露。

**整改** 在 README 的 "The configs and the plan sets" 里加一段配置选择规则，逐行写明来源；
把头部行所用配置的文件名与 sha256 印进报告 §1 的 row 表（分析器已在算这些 hash）；
把 `bench.ppx` 收进仓库——它是 XML 文本，不是第三方二进制。

### 4. MIX 不代表日常 Windows 端点流量，而且报告不打印它的组成

**证据**

- 实现的组成（`Client/Arms/MixPageLoop.cs:8-12`、`MixBulkLoop.cs:8`、`MixUdpLoop.cs:22`、`MixArm.cs:7-10`）：
  每 desktop 每 20 s 一次 page load，13 条连接、73 个请求、每个请求一个 38 000 B 的帧，
  发一帧等一次回显（`MixPageLoop.cs:94-98`，即 stop-and-wait）；bulk 5 Mbit/s；UDP 30 pkt/s × 120 B；DNS 每 page 4 次。
  按字节折算，每个 desktop 每秒：bulk 625 000 B、page 138 700 B、UDP 3 600 B——
  **bulk 占 81.5%，page 占 18.1%，UDP 占 0.47%，TCP:UDP ≈ 99.5:0.5**。
- 团队自己的兄弟设计给的模型是有出处的（`benchmarks/research/2026-10-06-proxifier-comparison-design.md:99-113`）：
  page 2.9 MB/页、77 请求、5.6 请求/连接、13 连接/页（[6][8]）；QUIC 占 21% origin（[1]）；
  video "1 stream per 4 desktops, 5 Mbps"（[14][15]）；realtime UDP "1 flow per 4 desktops, 30 pkt/s"（[16][17]）；
  DNS 4 lookups/页、类型混合 A 54 / AAAA 24 / HTTPS 20 / other 2、1/50 走 TCP/53、另有 0.2 qps 背景查询（[9][10]）；
  按 ×8 desktop 缩放（`:112`，合计约 20 Mbit/s）。同一文档写死三条
  "**Sensitivity arms (mandatory, because the mix is a design choice)**"：`MIX-h3hi`、`MIX-vidhi`、`MIX-dnslo`（`:130-133`）。
- 实现把这些"每 4 个 desktop"的常量当成了**每 desktop** 的常量：video 与 realtime UDP 各放大 4 倍（模型 156 250 B/s 与 900 B/s per desktop，
  实现 625 000 B/s 与 3 600 B/s）；QUIC 类不存在；DNS 类型是 A/AAAA/HTTPS/TXT（`MixPageLoop.cs:178-184`），
  没有 CNAME 追逐、没有 TCP/53、没有背景查询；`MIX-h3hi`/`MIX-vidhi`/`MIX-dnslo` 一个都没实现（`scripts/plans/` 下没有任何 MIX 变体）；
  desktop 数从 8 降到 4（`scripts/plans/full-plan.json:68`）。
  总报价负载倒是接近设计目标（4 × 767 KB/s ≈ 24.5 Mbit/s vs 设计 ~20 Mbit/s），但**组成被倒过来了**（模型里 video ≈ 52%、page ≈ 48%）。
- 原设计说 "The report states the mix explicitly, as RFC 9411 §7.1 requires"（`design.md:99`）。实测不成立：
  分析器只读 `MIX.parameters.desktops` 与 `parameters.udpPacketsPerSecondPerDesktop`；
  `pageIntervalSeconds`、`pageConnections`、`pageRequestsTotal`、`pageMessageBytes`、`bulkBitsPerSecondPerDesktop`、`dnsQueriesPerPage`、`udpPayloadBytes`
  在 `benchmarks/WinForward.E2E.Analysis/` 里**一次都没有被读取**（全目录 grep 0 命中），
  所以 `tables.md` 里没有 MIX 组成，读者必须去读 harness 源码才知道 "MIX" 是什么。

**后果** MIX 是唯一声称代表"日常流量"的臂，但它 81% 的字节是恒定速率 bulk 传输——正是 `LATLOAD`/`THRU` 已经在测的东西；
它的 UDP 类只以 120 pkt/s 的聚合速率（4 desktop × 30 pkt/s，120 s 共 14 400 个数据报）采样产品的新流建立路径。
据此不能得出任何"日常负载下的表现"结论，`MIX udp.lossRate` 也不能被当作代表性 UDP 精度。不看它的话，
读者会把这条曲线读成浏览场景下的产品行为。

**可被推翻的条件** 如果 MIX 的实际字节构成不是 bulk 主导（例如 bulk 速率其实被实现为"每 4 个 desktop 共享"，或 page 的实际字节远高于 73 × 38 KB），
这条的数值部分就要重算；依据是 `MixArm.cs:52` 自己发布的参数名 `BulkBitsPerSecondPerDesktop` 与 `MixPageLoop.cs:94` 的逐请求发送，
两者都直接读自代码。

**归类** 设计缺陷（组成与敏感性臂都要改）+ 只能如实披露（不改就必须写明 MIX 是 bulk 主导的合成形状）。

**整改（两台机器内可落实）** 纯常量改动即可把 bulk 降到模型值（1.25 Mbit/s/desktop）并把 UDP 改成"1 flow per 4 desktops"；
再加一份 plan 变体做敏感性臂（loader 没有覆盖机制，但多一个文件即可）。
最低成本方案是不改臂、只改报告：把七项组成参数打进 `tables.md` 的 MIX 段落——数据已经在 `parameters` 里。

### 5. 时长上限决定了这套仪器看不到产品计划真正关心的那类行为

**证据**

- `scripts/plans/full-plan.json` 十个臂的时长：60/60/60/90/90/120/120/60/120/90 s，合计 870 s = 14.5 min；
  最长的单臂 120 s（`LOSS`、`REL`、`MIX`）；唯一的"长连接"臂 `PERSIST` 是 90 s、含 25 s 空闲（`full-plan.json:72-76`）。
  `REL` 的 churn 是 20 conn/s（`full-plan.json:54`），一轮 2 400 次尝试。
- 产品自己的性能/稳定性计划的验收仪器是长的：`08-30-windows-reality` 的交付项里有 "1h soak, port-pool attribution"
  （`.trellis/tasks/08-30-proxy-perf-stability/prd.md:31`）；`09-28-udp-association-reuse` 的验收是 "1 h acceptance recorded"（`:38`）；
  `10-01-udp-session-footprint` 的结论建立在 soak 的 resident-set 峰值与"第二次人口循环 overflow 增长 +0 vs +742"（`:45`）；
  同一 PRD 还把 "high-churn port-pool capacity"（TIME_WAIT / 端口范围）列成独立候选。
- 兄弟设计对 churn 维度说得更直接：`REL-tcp` 应为 **200 conn/s**，
  "ephemeral-port pressure, TIME_WAIT accumulation, and per-connection memory are where transparent proxies actually differ"
  （`2026-10-06-proxifier-comparison-design.md:142,210`）。实现是 20 conn/s，低一个数量级。
- 没有 warmup：plan schema 的键表里没有 warmup 键（`benchmarks/WinForward.E2E/README.md:238` 起），
  client 代码里也没有 warmup 概念（`Client/`、`Contracts/`、`scripts/` 下 grep `warmup` 0 命中）；
  分析器的 `--warmup-seconds` 只作用于**内存**格子（`WinForward.E2E.Analysis/README.md:47`，并在 `:391` 明确 CPU 不做 warmup）。
  兄弟设计要求的是 "120 s + 30 s warmup"（`:139`）。
- 分辨率可以直接算：`REL` 一轮 2 400 次尝试，零事件时 rule-of-three 上界 3/2400 = 0.125%，
  而分析器给 TCP 意外率的等价带是 0.1 pp（`Analysis/Metrics/MetricSpec.cs:72`）——
  **仪器自身的分辨率比它用来判定"相同"的带宽还宽**。按设计文档的 200 conn/s，上界是 0.0125%，才落进带内。

**后果** 连接表增长、端口池耗尽、清扫/定时器周期、内存长时漂移、以及"跑了十分钟以后才变慢"这类行为在这套基准里结构上不可能出现——
不是测到"没有"，而是根本没有观察到的时间窗。不看它的话，读者会把"4 轮 × 120 s 的 `REL` 全绿"读成"稳定性相当"。
同时冷启动（FDD 的 JIT、驱动缓冲池填充、sing-box 关联建立）被算进每个 latency 格子，因为没有任何 warmup 窗口把它排除。

**可被推翻的条件** 如果产品计划里那些 1 小时 soak 关心的机制在 120 s 内已经有同等强度的替代仪器（例如 `REL` 的 2 400 次连接已足够触发端口耗尽），
这条就只对"内存与清扫"成立。当前 campaign 里没有这样的论证，也没有对应的长时臂。

**归类** 设计缺陷（时长与 warmup 是设计参数）。

**整改** 同一台 VM 即可落实：把 `REL` 的 `connectionsPerSecond` 提到 100–200（loader 只要求 `ceil(rate × seconds) ≤ 262143`，200 × 300 s 远低于此）；
把 `LOSS`/`MIX`/`PERSIST` 拉长到 10–30 min 作为每 pass 的"长时行"；给 CPU 补上 warmup 窗口。代价是每 pass 变长，不需要新机器。

### 6. 三种 UDP 承载被放进同一列比较，前提没有被声明

**证据**

- 分析器为每行维护两个承载标签（承载名常量 `RowProfiles.cs:82-103`，标签字典 `:112-135`）：`Udp53`（port-53 路径）与 `Udp`（一般 UDP），
  取值包括 `proxied-utcp` / `proxied-native` / `not-carried`。
  UoT 行的数据报走"每条流一条 TCP 连接"的 `sp.v2.udp-over-tcp.arpa` 通道——orchestrator 专门把它计成 `utcp`（`orchestrator.ps1:207-208`）；
  native 行走 SOCKS5 `UDP ASSOCIATE`；Proxifier 行根本不过代理。
- UDP 精度表只按 `metrics.sent/arrived/late/never/...` 分列，没有一列说明这个数字来自哪种承载；
  跨行比较家族是同一个（UDP loss，阈值 0.5 pp，`MetricSpec.cs:69`）。
- 两者的物理含义不同：UoT 把 client→proxy 这一跳的丢包换成 TCP 重传，应用层看到的是延迟与队头阻塞；
  native 行会真的在这一跳丢数据报。而分类门限 W 对所有行是同一个 200 ms（每臂 `lossWindowMs: 200`，
  `full-plan.json`；分析器把它当声明值打印，`Analysis/README.md:448-453`）。W 相同、承载不同，同一个 `lossRate` 不在测同一个量。
- 兄弟设计给的前提是"每条被代理的流都必须穿过 `127.0.0.1:1080`"，并据此定义 `proxiedFraction`（`:229-241`）；
  实现把 UDP 侧的门简化成存在性检查 `udpGate = (proxy-truth.udp + proxy-truth.utcp) > 0 required`（`Analysis/README.md:163`），
  分析器自己在 `:173` 写明 "The UDP gate is a presence check"。

**后果** "UDP 精度"这一栏的排序（以及跨产品 0.5 pp 的等价判定）不是同一物理量的比较：
一个用 UoT 的行可以在这一栏天然占优或天然吃亏，而报告不会显示这一点。
不看它的话，读者会得出"WinForward 的 UDP 比 ProxyBridge 可靠"这类结论——它至少部分是承载方式的差异。

**可被推翻的条件** 如果分析器已经在 UDP 表里打印了每行的承载标签（当前只在 DNS 表与 gate 表里打印），
或者等价判定是按承载分组的，这条就不成立。grep 表明 UDP 精度表的列定义里没有承载列。

**归类** 设计缺陷。

**整改** 分析器已经有 `RowProfiles.Udp` 字段，只需在 UDP 表里加一列承载标签、并在等价比较里按承载分组
（`MetricCell.cs:70-74` 已经在用同一个字段做 `not-carried` 判定）。数据不需要重跑。

### 7. MIX 臂的 `dns-rtt` 在多数行上是直连样本，却被当成可比数字打印

**证据**

- MIX 的 page loop 用 `context.DnsEndPoint`（= 靶机地址 + 本次运行的 `--dns-port`，即 53）发 UDP 查询
  （`Client/ArmContext.cs:26`、`MixPageLoop.cs:132-133`）。
- 在 `wf-aot-opt`/`wf-fdd-opt` 上，port-53 UDP 被自己的规则送进 `LocalTargets[dns]`，直连转发到靶机、不过 SOCKS5
  （`scripts/configs/wf-aot-opt.json`；分析器把这个格子标为 `direct-local-target`）；
  在 `proxifyre` 上是产品的 port-53 硬编码放行（`RowProfiles.cs:179-185` 的 `Udp53Hardcoded`）；只有 `proxybridge` 是 relayed。
  也就是说跑 MIX 的 5 个行里，3 个是直连样本、1 个是 not-carried、1 个是代理样本。
- §5 延迟表按 latency class 渲染，`dns-rtt` 一类的解析刻意不带 port-53 检查：
  `var udpPath = latencyClass is "udp-rtt" or "dns-rtt" ? "udp" : null; var (status, reason) = MetricStatus.Resolve(campaign, armName, udpPath, dns53: false, rowId);`
  （`Tables/TableLatency.cs:88-89`）；port-53 承载只在 `dns53: true` 时生效（`Metrics/MetricCell.cs:76-78`），
  即只对 `DNS`/`DNSALT` 两个臂生效（`MetricCatalogue.cs:90-101`）。冻结金标准里这一段确实逐行打印了 MIX 的 `dns-rtt`（`verification/golden/py-tables.md:669`）。
- 分析器文档明确声称这一格跨行可比："The MIX arm measures a third `dns-rtt` … it is polled rather than awaited,
  so it is comparable between rows but not with either DNS arm's cell."（`Analysis/README.md:131-133`）——
  它只讨论了轮询开销，没有讨论路径。

**后果** §5 的 `dns-rtt` 表把"到靶机的直连 DNS"与"经 SOCKS5 的 DNS"并列且无标注，
而同一份报告在别处（§9 与 `Analysis/README.md:127-129`）对 DNS 臂做了完全正确的处理（只用 `DNSALT` 跨产品比）。
不看它的话，读者会把 MIX 的 DNS 延迟读成产品的 DNS 转发延迟。可被反驳的反面意见是"这一格不是 verdict 指标"，
但它就是一张延迟表里的一格数字。

**可被推翻的条件** 如果 MIX 的 DNS 查询实际发往 `--dns-port` 之外（例如某个不被 port 规则匹配的端口），
或者 WF 的 port-53 规则带进程限定因而对客户端不生效，这条就要重写。配置里该规则没有进程限定，且 `--dns-port` 由 orchestrator 传 53（`orchestrator.ps1:247`）。

**归类** 设计缺陷（一个 `dns53` 参数或一行标注就能修）。

**整改** 把 MIX 臂的 `dns-rtt` 也按 port-53 承载打标签，或在 §5 的表头为 MIX 行加注实际路径。数据不用重跑。

### 8. 没有阳性对照，也没有灵敏度标定

**证据**

- 只有空控制：`control-pre`/`control-post` 跑 `BASE`（无产品，`orchestrator.ps1:379-390`），
  双车道的 direct 车道是第二个客户端镜像走直连（`orchestrator.ps1:305-322`）。这些都是"没有产品"的下限，不是"已知会退化"的正控制。
- 两个注入开关只被 loss 臂读取（`Client/Arms/LossArm.cs:126,132`）；campaign 从不传（`Invoke-Client` 的参数表里没有它们，`orchestrator.ps1:228-247`）；
  测试项目里也没有任何测试引用（`tests/WinForward.E2E.Tests/` 全目录 grep `inject` 只命中 `JsonlSinkTests` 的写入失败注入）。
  它们覆盖的是"客户端把该记的丢包记成丢包"这一件事，覆盖不到延迟、CPU、内存、连接失败、DNS 或分析管线的灵敏度。
- 兄弟设计要求的标定臂 `BURST-udp`（ramp 200 → 20 000 pkt/s over 30 s，"**calibration arm** — this is where client send-buffer overflow appears"，`:144`；
  并在风险表里被列为"harness loss 伪装成 path loss"的对策，`:568`）在实现里不存在：
  六个 plan 与九种 arm kind 都没有它（`scripts/plans/`、`Client/Arms/` 下 grep `burst` 只命中一个不相关的注释）。
- 设计文档里"旧 harness 把 abort 注入产品代码路径"的正控制被正确移除了（`design.md:105-106` 说明了原因），但没有任何东西替代它。

**后果** 这套仪器从未被证明能测出量级为 M 的退化。于是所有 `same`（等价）判定都不可解释：
它可能是"没有差异"，也可能是"仪器测不出来"。不看它的话，读者会把 `verdict.json` 里的 `same` 当成等价性的证据。
这是本轮审查里对决策影响最大的漏洞之一，因为报告的主要用途就是"我们要不要改"。

**可被推翻的条件** 如果存在一份记录在案的实验，让某个臂经过一个已知量级的劣化环节并观察到分析器报出 `different`，
这条就只剩"没有把该实验纳入 campaign"；本审查未在仓库或 task 研究目录里找到这样的实验。

**归类** 设计缺陷。

**整改（两台机器内可落实）** 不需要新产品：让被代理车道的流量经过一个**受控劣化**的本地环节
（最省事的做法是把 sing-box 的 upstream 换成一个会注入固定延迟或固定丢包率的本地转发器，跑一次"已知退化 50 ms / 1% 丢包"的行），
看分析器是否报 `different`。再退一步：用 `--inject-rewrite-every` 配一份专用 `LOSS` plan 并把结果写进报告，
至少证明 corrupt 通道是活的。

### 9. 没有盲法

**证据**

- 行 id 就是产品名（`orchestrator.ps1:78-110`），配置由本仓库提供（`scripts/configs/`），campaign 由同一个团队在 VM 上跑；
  分析器里每行的产品名、配置、plan 以及**排除规则**都是手写常量（`RowProfiles.cs:151-199`）。
  benchmark 目录下 grep `blind` 0 命中。
- "哪一行不参与哪个比较"也编码在同一张表里：`UdpIncapableRows` 由 `Udp == not-carried` 推导（`RowProfiles.cs:134-138`）；
  port-53 不可比由 `Udp53` 推导（`MetricCell.cs:76-78`）；plan 决定哪些臂渲染成 `not measured in this row`（`MetricCell.cs:55-67`）。
- 值得肯定的部分：阈值预先声明在源码里（`MetricSpec.cs:57-75`：latency 5%、CPU 10%、memory 10%、UDP loss 0.5 pp、TCP unexpected 0.1 pp），
  分析是确定性程序，`verdict.json` 同时给出原始与 Holm 校正后的判定（`Analysis/README.md:287-313`）。
  这替代了一部分盲法的功能（防止事后挑选），但替代不了**排除规则与结论口径出自同一个未盲作者**这件事。

**后果** 读者应把 "pre-declared threshold" 理解为"一位知道自己想要什么结果的作者在不同的时间写下的规则"，而不是"第三方预注册"。
具体地，"Proxifier 不参与 UDP 比较"、"port-53 DNS 臂不可比"、"partial row 不参与未跑的臂"三条排除本身都合理，
但它们的合理性由作者自证；报告里没有任何一处记录"谁决定了这些排除、有没有第二个人复核"。
不看它的话，读者会赋予这套判定超出其证据地位的可信度。

**可被推翻的条件** 如果存在一份由第二个人签署的排除规则清单或分析计划（pre-registration），这条就只剩形式问题。仓库里没有这样的文件。

**归类** 只能如实披露（两台机器、同一个人的现实约束下，真正的盲法不现实）。

**整改** 成本最低的是"可核查性"：在报告里增一节列出每条排除的规则、出处与依据（例如 "Proxifier 的 UDP 不参与比较：因为 `bench.ppx` 里 `<Udp mode="mode_bypass"/>`，配置原文见附录"），
并把每行的配置原文附进报告（分析器已经算了 hash，贴内容更彻底）。没有盲法也能让读者自行复核。

### 10. "流量真的过了代理"靠人工声明保证，不靠测量

**证据**

- TCP 侧的门是流量计数比：`tcpGate = proxy-truth.tcp / tcpAttempts >= 0.95`（`Analysis/README.md:159`）；
  UDP 侧只有存在性检查（`:163`），分析器自己写明它是 presence check（`:173`）。
- 唯一能发现 UDP 泄漏的探针是 sing-box 日志的行计数（`orchestrator.ps1:193-208` 的 `Get-ProxyTruth`），
  它数的是**流**不是**包**。
- 真正把 Proxifier 的 UDP 从表里去掉的不是测量，而是一行手写声明：`RowProfiles.cs:187-192` 的 `UdpNotCarried`。
  分析器只能对**已经被人写进这张表**的泄漏做处理；如果将来某一行静默泄漏 UDP 而没有人更新 `RowProfiles`，
  UDP 表会照常打印一个"完美"的数字。
- 靶机侧的 `udpSummary.sources[]` 逐区间源端点普查是现成的、可以承担"这个包从哪来"的仪器
  （`README.md` 的 ledger 契约；`Analysis/README.md:265-268` 与 `:271-274`），但分析器只把它用于 endpoint 重叠检查，不用来判断某个臂的数据报是否经过了代理。

**后果** 这套基准无法自行区分"产品转发了"与"产品放行了"。
不看它的话，读者会以为 `proxy-truth.json` 是数据报级的证据（它是流级的），
并且会以为 UDP 表的每一格都经过了泄漏检查（它是靠声明）。

**可被推翻的条件** 如果 `udpSummary.sources` 的源端点能与客户端本地端点对齐并已用于逐臂归属，这条就不成立；
`Analysis/README.md:271-274` 描述的只有 endpoint 重叠检查，没有逐臂来源判定。

**归类** 设计缺陷（根因与 harness-audit 第 17 条同源：那一条解决了 Proxifier 这个**实例**，机制仍是声明式的）。

**整改（两台机器内可落实）** 靶机已按源端点分区间统计；只要让客户端在 UDP 帧里带上本进程的本地端点
（帧头已有 `connId` 与 `seq`，加字段是协议改动，但两端都是我们的代码），分析器就能把每个臂的数据报按源端点归属，
从而**测出**泄漏而不是**声明**泄漏。作为过渡，应在每张 UDP 表下打印该行的 `Udp` 声明及其出处。

### 11. 被测产品的二进制版本不在记录里

**证据**

- `run.json` 记录的是 harness 自己的版本、OS、CPU 数、plan hash 与端口（`Client/RunFileWriter.cs:66-91`）；
  `environment.json` 记录 OS/build/内存/防火墙/sing-box 版本/靶机端口（`orchestrator.ps1:421-437`）。
  两者都没有被测产品的版本或二进制 hash，README 的契约表里也没有这样的字段。
- campaign 也不部署产品：`deploy-campaign.sh` 只上传两个客户端镜像、六份配置、五份 plan 与 orchestrator（`deploy-campaign.sh:32-70`）；
  `C:\wfbench\wf-aot\WinForward.exe`、ProxiFyre、Proxifier、ProxyBridge、sing-box 都是 VM 上的**前置条件**（`AGENTS.local.md` 第 5 节）。
- 报告里的产品版本是分析器源码里的常量字符串：`"ProxiFyre 2.6.1"`、`"Proxifier 4.14"`、`"ProxyBridge 4.0.0"`（`RowProfiles.cs:179-199`），
  四个 WinForward 行连版本号都没有（只有 `"WinForward AOT"` 与 `"WinForward framework-dependent"`）。
- 中继用的 sing-box 是本地打过补丁的 `1.14.2-singfix`（`AGENTS.local.md` 第 5 节；补丁与 issue 报告在
  `.trellis/tasks/10-06-e2e-competitor-benchmark/research/`）。`environment.json` 会把它作为字符串打印
  （`Tables/TableEnvironment.cs:106-115`），但报告不解释 `-singfix` 是什么，也不说明 `wf-aot-opt`/`wf-fdd-opt` 的 UoT 臂
  **只有在这个补丁存在的服务器上才成立**——`research/uot-singbox-interop.md` 记录了未打补丁时"每一条 UDP 流都失败、`arrived=0`"。

**后果** 这份报告无法回答"测的是哪个版本"，因此不能被当作可引用的产品结论；
而且 UoT 行的结果依赖于一个不在被测对象清单里的代理补丁。
不看它的话，读者会把它当成某个发行版的可比数字。

**可被推翻的条件** 如果每行的产品二进制 hash 已经出现在 artifact tree 的某个文件里（本审查在 `run.json`/`environment.json`/`config*` 里都没有找到），这条就不成立。

**归类** 设计缺陷（补一个版本采集即可）+ 只能如实披露（补丁的存在必须在结论旁说明）。

**整改** orchestrator 在每行启动后取一次二进制的版本信息与文件 hash，写进 `environment.json` 或行目录；
分析器把它打进 §1 的 row 表。纯采集，成本几分钟。

### 12. 设计文档承诺过、但没有落实（或落实得不一样）的条目

**证据**

| 设计文档 | 实现 | 影响 |
|---|---|---|
| `BASE-singbox` 控制（"explicit SOCKS5, no product … attributable proxy increment"，`design.md:82,153,226`） | 不存在：plan 里只有 `BASE`（无产品，`scripts/plans/base-plan.json`），控制块也只跑它（`orchestrator.ps1:379-390`） | 无法把"产品引入的开销"与"裸 SOCKS5 那一跳的开销"分开；所有绝对值都含 sing-box 的环回跳，只能声明"每个产品都同样付" |
| 每行 `proxiedFraction == 1` 作为进入对应表的**前置条件**（`design.md:152,159`；兄弟设计 `:240`） | 降级为 `tcpGate ≥ 0.95` 的流计数比与 UDP 的存在性检查（`Analysis/README.md:159,163`） | 见发现 10 |
| `tcp.fidelityDiscrepancyRate`（客户端判决 vs 靶机判决；兄弟设计称之为 "the strongest evidence you can produce without touching the products' code"，`:200,467`；`design.md:117,184`） | 只实现了客户端自己的 `fidelityMismatch`（观察 vs 请求模式的期望，`Client/Arms/ReliabilityArm.cs:70`） | 一个"均匀误判所有连接"的产品不会被这个指标发现；靶机账本的判决只作为逐臂披露出现 |
| `REL` 的 `stall`（2 s）故障模式（`design.md:91`；兄弟设计 `:142`） | wire 层有 `stall`（`README.md` 的 mode 表），但 `full-plan.json` 的 `modeMix` 只有 `clean/resetAfterN/partialFin/halfClose` | "超时 / keepalive 处理"这一维没有被测 |
| preflight 记录 `checksum/RSC/LSO` 状态（`design.md:146`），"Freeze and record all advanced properties; re-verify before each pass"（兄弟设计 `:573`） | 全仓没有任何 offload 采集（`scripts/` 与 `environment.json` 字段里 grep `Offload/RSC/LSO` 0 命中） | 一个会显著改变每包 CPU 与 NDIS 可见包形的因素既没被冻结也没被记录 |
| 动态内存风险的对策："per-sample guest-visible memory recorded, and any window whose visible memory moves is rejected"（`design.md:225`） | sampler 只读进程计数器；`environment.json` 只在 campaign 开始时记一次 RAM（`orchestrator.ps1:425`） | 在动态内存 VM 上，"内存不涨"可能是被 balloon/trim 压平的，报告无法分辨 |
| 每条记录都带环境块（产品与版本、配置 hash、sing-box 版本、日志级别、NIC offload、防火墙、pass index、程序顺序，`design.md:167-168`） | 只有 `run.json` 与 `environment.json` 带；产品版本见发现 11；日志级别没有任何地方被记录 | 逐行可复现性不成立 |
| 分析器输出 plots，含 "a run-order drift check"（`design.md:208-216`） | `plots/SKIPPED.md` 无条件写出（`Analysis/README.md:430-443`）；全仓 `drift` 只命中 control-pre/post 的比较（`Checks/ControlDrift.cs`） | "跑批过程中机器是否漂移"这个诊断不存在 |
| 每 pass 约 65 分钟、每程序约 12 分钟（`design.md:92`） | `full-plan` 十臂合计 870 s = 14.5 min/行，7 行 + 2 个控制块 + dual 相位与启停等待 | pass 长度与设计不符，读者无法从文档预估 campaign 规模 |
| `scripts/configs/wf-aot-opt.json` 的 sha256 被 README 记为 `15e49373…`（`README.md:321`） | 该 hash 对应的是 `/tmp/wf-bench/deploy/configs/` 里的**部署副本**；仓库里当前文件是 `dc9972e4…`（四个 WF 配置在 2026-10-10 有过一次未提交的 schema 迁移，`git status` 显示 ` M`） | "仓库配置 == 跑批配置"在今天的 checkout 上不成立；唯一权威是该行的 `config*` 副本 |

**后果** 这些是同一类问题：报告读起来像是按设计文档实现的，但若干条被引用时会改变结论含义的承诺并不成立
（尤其是 `BASE-singbox` 与 `proxiedFraction`）。
不看它的话，读者会把设计文档当成实现的说明书，并据此认为"产品增量"与"流量确实过了代理"都已经有证据。

**可被推翻的条件** 逐条独立：任何一条只要在实现或报告里找到对应物（例如某处确实采集并打印了 offload 状态），该条即被推翻。
本审查对每一条都给出了 grep 或 file:line 依据。

**归类** 设计缺陷（文档与实现漂移本身就是设计缺陷的证据）。

---

## 与 harness-audit 的关系

`research/harness-audit.md` 是**实现级**审查：逐条检查计数器的算术、窗口、边界与崩溃路径（22 条 + 披露清单），
并已用真实数据证实了 UDP 泄漏实例（第 17 条）、DNS local-target 绕过（第 18 条）与 CPU 只覆盖用户态（第 19 条）。
本文不重复那些条目，只在它们构成设计前提时引用：

- 发现 10 与 harness-audit 第 17 条同源：那一条解决了 Proxifier 这个**实例**（分析器现在把它标成 `not carried`），
  但"泄漏靠声明、不靠测量"这个**机制**仍在。
- 发现 4 与 harness-audit 第 2 条（`MIX` 观察窗口短于 W）相邻但不同：那条修的是观察地平线，本文谈的是**负载组成**。
- 发现 5 与 harness-audit 第 14、22 条（资源采样的进程身份与求和）相邻但不同：那些是采样缺陷，本文谈的是**没有足够长的时间窗**。
- 发现 8、9、11 在 harness-audit 里没有对应条目（它没有审查阳性对照、盲法与产品版本采集）。
- harness-audit 第六节列的四个待决问题中，前两个（DNS local target、Proxifier UDP）已在分析器侧落实为"声明 + 排除"；
  第三个（THRU 上限）在 `full-plan.json` 里仍是 60 MB/s，本文不重复。

---

## 附：本轴能支持与不能支持的决定

**能支持（附前提）**

- "在同一台 VM、同一套环境、同一份配置下的整机方案排序"——前提是结论按方案而非按产品表述，并附上每行的配置与版本。
- "某个方案在这个合成负载形状下没有出现可观测的差异"——前提是把 `same` 的判定与仪器分辨率一起引用（含发现 5 的 rule-of-three 计算）。
- "某行在某个臂上发生了接线错误 / 泄漏 / 数据错乱"——门与身份检查是机械的，且带证据（identity、`foreignConnection`、`directLeak`）。
- "本基准确认了某个产品的某条路径确实被走到"——前提是该行的 `Udp`/`Udp53` 声明本身经过了复核（见发现 10）。

**不能支持（除非先补对应缺陷）**

- 任何形如"产品 A 比产品 B 快/稳 X%"的**产品级**因果结论（发现 1、3）。
- "WinForward 在默认 UDP 承载下的表现"（发现 2、3：默认承载那一行只有 3 个臂）。
- "日常 Windows 端点负载下的表现"（发现 4）。
- "长时间运行稳定 / 无泄漏 / 无端口耗尽"（发现 5）。
- "UDP 精度更高"的跨承载排序（发现 6、7）。
- "没有差异 = 等价"（发现 8）。
- "该版本的产品如此"（发现 11）。
