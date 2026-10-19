# E2E benchmark 设计审查：汇总

这份文档回答一个问题：**`benchmarks/WinForward.E2E` 这套端到端对比基准，作为一件测量仪器和一次实验，结构上能支持什么结论、不能支持什么结论。**

四条轴各有一份证据文档，本文只做汇总与排序，不重复其中的证据：

| 轴 | 文档 |
|---|---|
| 统计与决策模型 | [design-stats-and-decisions.md](./design-stats-and-decisions.md) |
| 仪器有效性与归因 | [design-instrument-validity.md](./design-instrument-validity.md) |
| 实验设计与公平性 | [design-experiment-fairness.md](./design-experiment-fairness.md) |
| 可复现性与运行层 | [design-reproducibility.md](./design-reproducibility.md) |

实现级缺陷不在此重复，见 `.trellis/tasks/10-06-e2e-competitor-benchmark/research/harness-audit.md`。

**贯穿全文的约束**：整改建议只使用现有的两台机器（Linux 靶机宿主 + 一台 Windows VM）。全文不出现"加机器""换环境""用 netem 模拟真实网络"这类建议。

---

## 0. 判据

判断一条设计缺陷是否成立，用三条标准：

1. **它让哪个结论不可信**——不是"代码写得不好"，而是"照着这条设计做出来的数字支撑不了它被用来支撑的那句话"。
2. **它能不能被读者从结果里发现**——一个已经写在报告里的 caveat 是披露，不是缺陷；一个读者无从察觉、只能靠通读源码才能发现的偏差才是缺陷。
3. **它能不能用现有两台机器修**——修不了的不提，只记录它在当前条件下的边界。

---

## 1. 这套基准没有测量模型：它的设计就是它的表格

**证据。** 三份文档各管一段，没有一段管"要估计什么"：

| 文档 | 它规定的是 |
|---|---|
| `benchmarks/WinForward.E2E/README.md`（765 行） | 记录的形状、键的契约、gate 的含义 |
| `benchmarks/WinForward.E2E.Analysis/README.md`（503 行） | 表格怎么渲染、哪些格子不可比、八条"表格看不见的性质" |
| `benchmarks/research/2026-10-06-proxifier-comparison-design.md`（693 行） | 方法与 RFC 依据、抽样量、决策规则、风险表 |

三份加起来仍然没有一句话回答：**这次测量要估计的量（estimand）是什么，分析单位是什么，误差模型是什么，跑完之后要拿它做哪一个决定。** 设计文档 §4 有一张"Statistics plan"表，写了 design / repetitions / aggregation / decision rule，但它描述的是**统计方法**，不是**要回答的问题**：它没说"WinForward 比 Proxifier 好"与"WinForward 这一版比上一版好"是两个不同的问题、需要两套不同的设计。

**后果。** 产出是一张 21 指标 × 9 行（7 个产品行 + 2 个控制块）的矩阵；哪些格子能支撑哪句话，留给读者自己判断。`ROW_PROFILES`、carriage 标签、"not measured in this row"这些机制说明作者知道格子之间不等价，但**没有任何一处把"哪些格子合起来构成一个可发布的结论"写下来**。

最容易踩的两个坑：

* **把"配置对比"读成"产品对比"。** 9 个行里有 4 个是 WinForward 自己的构建（`wf-aot-opt`、`wf-fdd-opt`、`wf-aot-nativeudp`、`wf-aot-dnsrelay`），3 个是对手。行与行之间同时变了实现、构建方式、UDP 承载、配置文件和驱动栈（见实验设计轴）。跨行差分因此是"配置差分"，不是"产品差分"。
* **把"测不出来"读成"没有差别"。** 见下一条。

**归类：设计缺陷**（缺一份量化的测量模型/estimand 文档）。**在现有两台机器内可落实**：不需要重跑，只需要写一页纸——这次 campaign 要回答的 2–3 个问题、每个问题对应的指标、判定规则与所需轮数。这份东西是后面所有"该跑多少轮"决定的依据。

---

## 2. 默认轮数下，推断层退化成一个极差比较；而这套设计的决策规则建立在它上面

这是本次审查里最硬的一条，因为它不依赖任何实现 bug，只依赖 n 和这套分析器自己的算法。

**算法本身。** `Stats/BootstrapPair.cs`：对每一对行，取两行的**逐轮值**做配对，重采样**轮**（不是样本）10000 次，区间端点取重采样分布的中位数的 2.5 % / 97.5 % 分位（`Stats/DescriptiveStats.cs:27-48`，type-7 线性插值）。判定规则（`BootstrapPair.Decide`）：CI 完全越出阈值带 → `different`；CI 完全落在带内 → `same`；否则 `inconclusive`。

**在 n=4 时这个区间就是 [最小值, 最大值]。** 重采样 4 个值的中位数落在最小值上的概率是

```
P(median of 4 resampled passes == sample min) = C(4,3)·(1/4)³·(3/4) + (1/4)⁴ = 0.0508 > 0.025
```

也就是说重采样分布的第 2.5 百分位**就是最小的那个原子**：10000 次重采样里约有 508 次落在最小值上，而 2.5 % 分位取的是排序后第 250 个（position = 0.025 × 9999 = 249.975），上端同理。n=5 时这个概率是 0.0579，仍然退化；**n=6 时降到 0.0087，区间端点才开始向内移动**。模拟（30000 次试验 × 200 次重采样）复现了同一个形状：下端点等于样本最小值的频率在 n=3/4/5 时是 1.000 / 0.945 / 0.977，n=6 掉到 0.010——n=4 那 0.945 是模拟本身只用 200 次重采样造成的（重采样数少到第 0.025 分位偶尔越过最小值），在分析器真实的 10000 次重采样下端点就是最小值。

**于是 p 值只有两档。** `PValue = 2·min(P(resample ≤ 0), P(resample ≥ 0))`。四对配对差值同号时它是 **0**；只要有一对反号，它就跳到 0.5 以上——四对里 3 正 1 负时 `P(重采样中位数 ≤ 0) = 1 − (3/4)⁴ − 4·(1/4)·(3/4)³ = 0.2617`，p ≈ 0.52；两正两负时 p 被截到 1。Holm–Bonferroni 乘上家族大小（`loss.lossRate` 家族 10 对，`cpu.proxy.vcpuPct` 与 `mem.privateBytes.p50` 家族 21 对，取自冻结的 `verification/golden/py-verdict.json`）之后，0 仍然是 0，0.52 被截到 1 —— **Holm 在 n=4 下永远不改变判决**。分析器自己的冻结说明已经记录了这个现象："every `holm_p_value` leaf sits at 0 or 1 (41 zeros, 171 ones)"（`verification/FROZEN.md:203-213`）。

**结论：默认 `-Passes 4` 时，"A 与 B 有差异"等价于"四轮的配对差值全部同号且全部越出阈值带"；"没有差异"等价于"四轮全部落在带内"。设计文档写下的三段式决策规则（CI 排除 1 + 效应超过阈值 + Holm 校正后 p < 0.05）实际只剩中间一段在起作用。**

**这一条已经在真实数据上核过。** `/tmp/wf-bench/e5b2-campaign/verdict.json`（2026-10-08 的 E5-b2 轮，3 个 pass）里取最干净的一对——`wf-aot-opt` 与 `wf-fdd-opt`，**同一份配置、只差构建方式**，也就是这套矩阵里最可信的一对：

| pass | `wf-aot-opt` | `wf-fdd-opt` | 比值 a/b |
|---|---|---|---|
| pass1 | 3376.127 µs | 3445.759 µs | 0.97979 |
| pass2 | 3919.871 µs | 4011.007 µs | 0.97728 |
| pass3 | 3408.895 µs | 3625.983 µs | **0.94013** |

分析器给出的是 `estimate = 0.97728`（三个比值的中位数）、`ci95 = [0.94012989, 0.97979197]`——**恰好是三个比值的最小值与最大值**，与上面的推导逐位吻合。判决是 `inconclusive`：区间跨过了 ±5 % 带的下沿 0.95。而同一对的 `p_value = 0.0`、`holm_p_value = 0.0`：**p 值说"高度显著"，判决说"说不清"**，两个数字并列在同一格证据里。

把整轮 campaign 的 212 个可比对全部过一遍：`raw_verdict` 与 `holm_verdict` **逐对完全相同**（different 102、same 51、inconclusive 49、no-threshold-declared 10）——**Holm–Bonferroni 在真实数据里一次判决也没有改变过**。为什么不变：`different` 那一侧 102 个对的 `p_value` 全是 0（差值同号），乘任何家族大小仍是 0，**校正对 `different` 在 K≤6 时结构上无效**；`same` 那一侧 51 个对的 `p_equivalence` 也全是 0，而它们之所以是 0，正是因为那些格子的逐轮值完全相同、区间宽度为零——**这 51 个 `same` 不是等价性检验的结论，是退化区间的产物**。统计轴的仿真给出另一半：当真出现跨轮波动时（K=6、σ=5%），Holm 会把 58.8 % 的 `same` 翻成 `inconclusive`——**校正打在的是与设计文档承诺相反的一侧**。
**比 CI 退化更要紧的是噪声本身。** 同一份 `verdict.json` 里每行的逐轮值可以直接算出轮间离散度 ((max−min)/min)，把它和该指标自己声明的阈值并排：

| 指标 | 声明阈值 | 各行轮间离散度（中位） | 最差行 |
|---|---|---|---|
| `lat.tcp_rtt.p50` | 5 % | **13.1 %** | 16.4 % |
| `lat.udp_rtt.p50` | 5 % | **15.1 %** | 21.9 % |
| `lat.tcp_rtt.p99` | 5 % | 6.5 % | **278.7 %** |
| `latload.tcp_rtt.p50` | 5 % | 2.5 % | 28.2 % |
| `cpu.proxy.vcpuPct` | 10 % | **10.4 %** | 74.2 % |
| `mem.privateBytes.p50` | 10 % | 1.3 % | 14.0 % |
| `thru.goodputMbps` | 无阈值 | 2.2 % | 9.3 % |

**同一行、同一 plan、同一台机器，三轮之间的 p50 差 13 %，而判定它的带子是 ±5 %。** 也就是说：延迟与 CPU 这两族的阈值落在噪声里，`different` 与 `same` 更多在描述哪几轮恰好跑得快，而不是产品。真正稳的是内存（1.3 %）与几个近零的率指标。（口径提醒：这组数字来自**压缩口径**的 E5-b2 轮，臂比 canonical 短；canonical 口径的轮间噪声**从未被测过**——见可复现性轴第 5、6 条。）

顺带说清同一枚硬币的另一面：51 个 `same` 里绝大多数是 `loss.lossRate`、`loss.corruptRate`、`persist.responseRate` 这类**全零或近零的率指标**。它们每一轮的值完全相同 → 重采样分布退化成单点 → 零宽区间 → 无条件落进带内。更直白地说：`BootstrapPair` 自己会给这种情形打上 `degenerate` 并附一句 `degenerate bootstrap CI (zero variance across passes)` 的注，**但判决仍然是 `same`**——一个方差为零的样本得到的是"等价"，不是"无法估计"。这是 harness-audit 第 10–12 条那类"结构性恒零"的计数器在**判决层**的复现：不是测出了零，而是这套推断无法表达"零"以外的东西。

**还有一层，比 CI 的退化更难修：那 4 个"独立重复"共享同一个 session 的一切。** pass 循环在同一个 pwsh 进程里跑，`Start-SingBox` 只在循环之前执行一次，`environment.json` 也只在循环前写一次（没有逐轮的环境记录），轮与轮之间只有产品的启停与 3 s 等待。于是 4 个 per-pass 值共享宿主负载、sing-box 的长寿命状态、VM 的内存碎片、防火墙与更新状态——**campaign 级因素的自由度是 1，不是 4**；CI 覆盖的只是短时抖动，"四轮彼此一致"不能当作可复现性的证据。另外，设计要求"丢弃第一轮作预热"（`prd.md:115`、`implement.md:86`），仓库脚本里**零处丢弃**：`publish-campaign.sh` 把 `C:\wfbench\results\*` 全量打包后直接分析——"三个测量轮"这个说法在实现里不存在，N 由 `--raw` 指到哪决定，而产物里读不出来（`environment.json` 只记 `passes = 跑了几轮`）。



**设计文档自己要求过这件事。** `benchmarks/research/2026-10-06-proxifier-comparison-design.md:480-483`：

> Repetitions: **n = 4 passes minimum, n = 5 preferred** … RFC 8219's ≥20 repetitions for latency is the ideal; this matrix cannot afford it — **document the deviation and the resulting CI width** rather than pretending.

逐字对照：设计把 4–5 轮定为下限并且**预先知道自己偏离了 RFC 8219 的 ≥20 轮**，同时要求把"由此产生的 CI 宽度"写下来。这份文档不存在（`Analysis/README.md` 只说"少于 3 轮不足以判定一对"）。而且从上面的算式看，设计选的下限（4）和偏好值（5）都落在退化区里——**这个偏离比设计文档自己以为的更严重：它不是"CI 更宽"，而是"CI 退化成极差、p 值退化成 0/1"**。

需要同时说清的另一半：**每轮之内的样本量是充足的**。设计文档的抽样量表（p99 需 300–400、p99.9 需 2000–4000）由 `LATLOAD` 500 req/s × 60 s = 30000 个样本、`LOSS` 500 包/s × 120 s = 60000 个包满足；直方图相对分辨率约 0.05 %，远细于 5 % 的延迟阈值。**瓶颈不在轮内，在轮数。**

**归类：设计缺陷。**（算法与轮数是设计参数，不是 bug；分析器按"轮"重采样这件事本身是设计里做对了的部分——它没有把样本当独立重复。）

---

## 3. 成本与决策：一轮 canonical campaign ≈ 6.5 h 的机器时间，买回 4 个独立值

**证据。** 行与 plan（`scripts/plans/`）的对应来自 `RowProfiles`，时长是各行 plan 的 `seconds` 之和：

| 行 | plan | 名义臂时长 |
|---|---|---|
| 5 个 full 行：`wf-aot-opt`、`wf-fdd-opt`、`proxifyre`、`proxifier`、`proxybridge` | full（10 臂） | 870 s = 14.5 min |
| `wf-aot-nativeudp` | udp only（3 臂） | 240 s = 4.0 min |
| `wf-aot-dnsrelay` | dns only（2 臂） | 180 s = 3.0 min |
| `control-pre` / `control-post` | BASE（两个相位） | 2 × 120 s = 4.0 min |

**有实测，不必估算。** 2026-10-08 的 E5-b2 轮（压缩口径、3 个 pass、7 行 + 2 个控制块）从 `=== preflight ===` 到 `=== orchestrator complete ===` 是 **2 h 30 min 45 s**，单 pass 49–51 min；可复现性轴第 6 条给出逐行日志读数，并用"臂时长之和 × 1.033 + dual 相位 + 每行 8 s 生命周期"把同一轮复现到 0.8 % 以内。

**换成 canonical `plans/full-plan.json`**（每个 full 行多 549 s，5 个 full 行多约 46 min）之后：**一轮 pass ≈ 97 min**。于是 3 轮 ≈ 4 h 51 min、4 轮 ≈ 6 h 29 min、5 轮 ≈ 8 h 05 min 的独占机器时间，部署、回传、分析与 VM 重置另计。

**已经跑过的那一轮，是压缩口径的 3 个 pass、0 个丢弃的预热轮。** 设计要求的"丢弃一轮 warm-up 之后至少 3 个测量 pass"没有实现（`implement.md` 里没有丢弃逻辑），于是这轮恰好压在分析器的判定下限 `Cli/AnalysisOptions.cs:21`（`DefaultMinPasses = 3`）上——再少一个 pass，所有成对比较一律 `inconclusive`。
于是：**canonical 4 轮 ≈ 6.5 h 换 4 个独立值/每格，而 4 个独立值给出的区间是极差。** 边际成本很清楚——多一轮约 +1.6 h，把轮数从 4 提到 6（约 +3.2 h）区间才不再退化；这两个数字是同一台机器上的，不需要新设备。

设计文档 §4 明确写过哪根旋钮不能拧："**Cutting repetitions below 4 is the wrong knob**: with n=1 you cannot report variance at all"。但实现里被拧掉的恰恰是它给的**裁剪旋钮**（设计 §4："Trim knobs if that is too long: MIX 360→240 s, LOSS-udp 180→120 s, DNS-mix 120→90 s"）：提交的 `full-plan.json` 里 MIX 是 120 s、LOSS 是 120 s、DNS 是 90 s——三个旋钮全拧了，轮数却停在 4。

**归类：设计缺陷（成本分配方向反了）。** 在现有两台机器内可落实：把 `-Passes` 提到 6（约 +3.6 h），或先写第 1 条要求的测量模型、只对真正要做决定的那几个指标加轮数。

---

## 4. 设计文档承诺过、但没有落实的条目

设计文档（`benchmarks/research/2026-10-06-proxifier-comparison-design.md`）是一份有 RFC 依据的方法论文档，它的每一条承诺都对应一个具体风险。以下条目在实现里查不到，且**没有任何替代仪器**：

| # | 设计里的承诺（行号） | 现状 | 被打开的风险 |
|---|---|---|---|
| 1 | `BASE-singbox` 控制行："run `BASE-direct` and `BASE-singbox` so the proxy-attributable delta is `proxy − base`"（:310），并在报告里给"ratio to the `BASE-singbox` baseline"（:517） | 不存在。六个提交的 plan 里唯一的 base 臂是 `BASE`（无产品、直连）；客户端按设计"从不说 SOCKS5"（harness README:23），**当前形态下做不出来** | "产品相对直连增加 N %"里的 N 混着 sing-box 那一跳 |
| 2 | `env.*`："NIC offloads, dynamic-memory settings, sing-box version, product version+config hash, VM snapshot id — **constant; must be identical across all rows**"（:474） | `environment.json` 只有 os/build/logicalCpus/总内存/空闲内存/passes/seed/三个 firewall 档/sing-box 版本/端口。**没有任何 offload 字段、没有动态内存设置、没有 snapshot id**；orchestrator 里搜 `offload|RSC|LSO|snapshot` 零命中 | 风险 9（offload 漂移改变每包开销）与风险 5（动态内存 ballooning）没有记录，也没有前置复核 |
| 3 | 风险 14："Keep the VM utilization below ~50 %, report `cpu.vmTotal` and headroom"（:597） | `Analysis/README.md:455` 明写"Machine-wide CPU is not sampled"，headroom 是相对逻辑核数算出来的；`Findings` 目录里搜 `generator|headroom` 零命中 | CPU 饥饿这个混杂因子有列、无断言、无 finding |
| 4 | 风险 4 的四桶瀑布与"mandatory `kernelSendDrop`/`RcvbufErrors` gate"（:594） | 未实现。LOSS 的 `clientSendLoss` 是四个**客户端侧**项（`abandonedAtTeardown + sendFailures + windowOverflow + sentOutOfRangeSequences`），没有内核侧目击者 | 历史"幻影丢包"陷阱的原始防线缺席（现在靠 in-flight 窗口与恒等式兜底） |
| 5 | `BURST-udp`（30 s，用于标定 harness 自己的突发天花板）（:203, :425） | 未实现（11 个臂里没有它） | harness 自身饱和点未知 |
| 6 | 故障模式表里的 `stall`（2 s 停顿）（:142） | 靶机实现了（`Target/TcpConnectionProtocol.cs:70`），但提交的 plan 里 `modeMix` 是 `clean=25,resetAfterN=25,partialFin=25,halfClose=25`——**campaign 里 stall 永不发生** | 最像真实网络抖动的那条路径（超时/保活）从未被测 |
| 7 | 风险 18："assert by injecting an artificial 1 s stall and verifying the reported p99.9 moves to ≈1 s"（:601） | 从未执行；`--inject-corrupt-every`/`--inject-rewrite-every` 只作用于 LOSS 与 BASE 的丢包相位，且前者在靶机侧被静默丢弃 | 延迟仪器的灵敏度从未被证明（违背 `.trellis/spec/backend/benchmark-methodology.md` 的"Before a gate is trusted, prove it can fail"） |
| 8 | QUIC/HTTP3 臂与 `quic.*` 指标（:140-150, :470），IPv4/IPv6 比例须记录（风险 12） | harness 全树搜 `quic|http3` 零命中 | 设计自己的流量证据说 HTTP/3 占 21 % 请求、QUIC 正在取代 TCP；这块流量恰好是 UDP 承载差异最吃紧的地方 |
| 9 | "One discarded pass for the whole matrix; within each run, ≥30 s / ≥60 s warmup"（:479） | 没有丢弃轮；预热是分析侧的 5 s，且只作用于内存格 | 首轮效应与臂内瞬态 |
| 10 | `cpu.proxy.perMB`（:459） | 未实现（缺 sing-box 字节数） | 跨行归一少一个口径 |
| 11 | "randomize arm order within each program"（:478） | 未实现，臂序恒为 plan 顺序。这正是内存斜率按 `ArmRecords.LoadOrder` 而非真实运行序拼接的根因（harness README 已披露该项） | 漂移检查失去意义；但 `run.json` 已记录每臂 tick 窗口，修它不需要新数据 |
| 12 | 报告要"Provenance block above every table"（:513）与 run-order drift 图（:531） | plots 全部 SKIP；控制块只覆盖 BASE 的路径质量，不覆盖每个产品行的逐轮漂移 | 漂移只有数字在 `verdict.json` 里，没有任何自动检查 |
| 13 | "assert `proxiedFraction == 1` on a warm-up probe"（`design.md` §4） | 改为跑完之后的 `tcpGate ≥ 0.95` 事后门（`Analysis/README.md` 的 flow gate 定义）；`Assert-CleanSlate` 失败只写 stdout 日志、不进 `Failures`、不进结果树（见仪器轴发现 3） | 一个没抓流量的产品要烧掉整行 18 min 才被发现；残留驱动污染控制组时读者无从察觉 |

**归类：前 7 条是设计缺陷**（有风险、有对策、没实现，且无替代仪器）；**后 6 条是设计缺陷或只能如实披露**，逐条见各轴文档。

**这批落差本身也是一个设计问题**：设计文档与实现之间没有任何机械的对应关系。它没有测试、没有 gate、没有 CI 检查哪一条承诺落到哪个文件；harness README 与 analysis README 甚至从不引用设计文档。结果是十三个已知风险敞口只能靠人重读 693 行方法论文档才能发现。

---

## 5. 其余三条轴与统计轴的要点

### 5.1 仪器有效性与归因（7 条，见 [design-instrument-validity.md](./design-instrument-validity.md)）

1. **路径上有一跳报告里不存在：VM 本机的 sing-box。** 三份提交配置的上游都是 `127.0.0.1:1080`，它由编排器整轮常驻、不在任何一行前后重启，每行的"流量真的过了代理"这个证据也来自它的日志——而 765 行的 harness README 里 "sing-box" 零命中。设计里用来把这一跳从产品开销里分出来的 `BASE-singbox` 行不在任何提交的 plan 里，并且以"客户端从不说 SOCKS5"这条设计前提，**当前形态下做不出来**。受影响的是 ratio-to-control 那一族结论。
2. **跨机器归因只有钟、没有身份，而报告的表注承诺了一条不存在的身份连接。** 靶机的 `tcp` 账本和 REL 的 `attempt` 记录都带 `connectionId`，`Tables/TableTcp.cs:86-88` 的表注写着"按同一个 `connectionId` 把客户端观察与靶机判决连起来"，而分析器里没有任何代码读这个字段。时钟一致性没有检查，也不体现在产物里：钟差只产生 `ledger-*-mismatch` 这类 `measurement-caveat`，**不进任何延迟/丢包/CPU 数字**，所以一次 3 秒钟差的 campaign 的 headline 表照常渲染。
3. **分辨率与效应量真正错配的只有一格。** `rel.unexpectedEofRate` 的声明阈值是 0.1 pp，而 full-plan 给它的 2400 次尝试的全零上界是 `3/2400 = 0.125 pp`——**阈值比测量下限还小**（压缩口径的 480 次更差：0.625 pp）。其余格子都够：UDP 损失 0.005 pp vs 0.5 pp、直方图相对分辨率约 0.05 %、CPU 量子约 0.027 %vCPU。这一条必须和"其余够用"一起说，否则会得出"整套仪器分辨率都不够"的过度结论。
4. 另有一处只在本文档出现过的分母缺陷：`DNS.answerRate = answered/sent`，而 `sent` 会被 `unsent`（在途窗口满时被跳过的配速槽位）缩小；DNS **不在** `clientSendLoss` 的 gate 名单里，表里也没有它的列，分析器还不读臂的 `notes`。

### 5.2 实验设计与公平性（12 条，见 [design-experiment-fairness.md](./design-experiment-fairness.md)）

1. **七行是七个因子束，不是七个处理。** 除 `wf-aot-opt` / `wf-fdd-opt` 这一对（同配置、只差构建）外，行间差异同时包含产品实现、构建方式、UDP 承载、驱动栈（ndisrd / ProxifierDrv / WinDivert）、进程模型（常驻进程 / Windows 服务）、配置、plan 与 DNS 路径。表结构是"一行一个产品"，读者会把它读成产品排序。
2. **配置政策从未声明，而我方头部行跑的是非默认模式，而这条路径还依赖一个本地打过补丁的 sing-box。** `UdpOverTcp` 在产品侧默认 `false`（`ConfigurationModels.cs:119,128`，注释称其为 "the opt-in UoT v2 connect-mode"），`wf-aot-opt` 把它设为 `true`；跑默认承载的那一行 `wf-aot-nativeudp` 只跑 3 个臂。三份对手配置全部由本团队手写，Proxifier 那份是唯一没有仓库文件的（只在机器上，且现在的副本与验证轮的副本已经不同）。另外 VM 上用的是一个本地补丁版的 sing-box（`1.14.2-singfix`，补的就是 SOCKS5 流水线吞首包那个 bug），而 UoT 行依赖这个补丁——对手在不同行上面对的其实是两个不同的代理实现。
3. **MIX 撑不起"日常流量"这个名字**：按字节 81 % 是一条恒定速率 bulk 流，UDP 只占 0.5 %；QUIC、TCP/53、CNAME、背景查询全部缺失，而**报告不打印 MIX 的组成参数**。
4. **没有阳性对照，也没有盲法**：仪器从未被证明能测出已知量级的退化（`--inject-*` 只覆盖 loss 相位的两个计数器，orchestrator 从不传；设计里的 `BURST-udp` 标定臂没实现）。这直接违背仓库自己的规范：`.trellis/spec/backend/benchmark-methodology.md` 的第一条纪律是 "Before a gate is trusted, prove it can fail"。另外，产品的二进制版本与哈希不在任何记录里（`deploy-campaign.sh` 不部署产品，版本只硬编码在 `RowProfiles.cs` 的标签里）。

### 5.3 可复现性与运行层（10 条，见 [design-reproducibility.md](./design-reproducibility.md)）

1. **驱动链路从来没有进过版本控制。** 5 个 gitignored 文件（`wf.sh`、`deploy-campaign.sh`、`start-targets.sh`、`publish-campaign.sh`、`AGENTS.local.md`）加上 `watchdog.ps1`、`bench.ppx` 与宿主侧的两条 iptables/sysctl 前提，`git log --all` 对这些路径的提交数**全部是 0**——不是删过，是从来没有过。除"构建"与 `orchestrator.ps1` 本身，跑一轮的每一步都不在仓库里。
2. **部署的是工作区，不是某个 commit。** 当前工作区里四份 WinForward 配置相对 HEAD 是未提交改动（`221+/197−`，且是配置 schema 换代），sha256 与任何一轮已记录运行都不同；README 里那张"repo file → row → deployed path"映射表的 `15e49373…` 指纹对今天的工作区不成立。照 README 重跑 = 测另一套配置，而行名、列名、阈值完全一样。
3. **plan 口径混用，且报告里无法解码。** E5-b2 里 5 个 full 行跑的是压缩形状（`plans-windows/full-shape-plan.json`，321 s），而两个控制块与 `wf-aot-nativeudp` 跑的是全长 plan；`run.json` 记的是 `planPath = …\full-plan.json` + 一个 16 位 hash，要读者自己去 hash 三个仓库文件才能发现这件事。分析器只检查臂名，从不检查时长或速率。
4. **原始数据不在仓库里，唯一副本在 `/tmp`**（`/tmp/wf-bench/val`、`/tmp/wf-bench/e5b2-campaign`；靶机账本也在 `/tmp`）。仓库里没有任何一份 campaign 报告。另外：产品起不来时行目录是空的，`LooksLikeRow` 不认它，37 个 finding code 里没有一条覆盖"某个声明的行整个不见了"。

### 5.4 统计与决策模型（12 条，见 [design-stats-and-decisions.md](./design-stats-and-decisions.md)）

第 2 条已经写了这套推断在默认轮数下的退化；这一轴补的是**其余**部分：

1. **名义 95% CI 的实际覆盖率不到 95%。** 按 `BootstrapPair.cs` 逐句复现的仿真给出：K=4 时覆盖率 86.0–89.3%，K=3 时 71.3–75.7%。也就是说 `(n=4)` 印出来的那个区间，本身就比它自称的置信水平窄。
2. **`same` 在真实噪声下几乎不可达成，而假 `different` 无人拦。** 以实测的 13.1 % 轮间波动作仿真：真比值 1.00 时只有 **0.8 %**（K=4）会被判 `same`，96–99 % 落进 `inconclusive`；同时有 **3.5 %**（K=4）被判成 `different`。需要 **1.30** 的比值才有 82 % 把握判出差异，1.40 才到 95 %。也就是说 `same` 的出现频率不携带"两者相近"的信息——它只在 4 个数恰好整齐时出现。换到低噪声指标（σ=2 %）上，同一套规则的假平局判对率是 92.3 %、真 1.08 的差异有 71 % 判出——**这说明上表不是规则的固有性质，而是规则 × 这台机器噪声的结果，也说明噪声底本身就是设计参数**。
3. **阈值是声明而非推导，其中一条连设计文档里都没有，而且基准自己援引的准则线比它严 100 倍。** 5 % / 10 % / 10 % / 0.5 pp 写在 `design.md:217` 与 `MetricSpec.cs:50-52`（后者自认 "declared constants"），没有推导；**TCP 意外率的 0.1 pp 在任何设计文档里都不存在**，只活在 `Metrics/MetricSpec.cs:72`。兄弟设计 `2026-10-06-proxifier-comparison-design.md:206-207` 明确要求把 RFC 9411 的 0.001 %（失败事务、DUT 发出的意外 RST）"Adopt these as pass/fail gates"，而交付的分析器里搜 `0.001` 零命中——于是"TCP 意外率没有差异"与"按 RFC 9411 已不合格 100 倍"两句话可以同时为真。另外设计文档要求 "Pre-declare the primary family"（`:579`），交付里 21 个指标全是 headline，没有主次。
4. **噪声底从未被测量；唯一一份"同一个二进制跑两次"的实测不在 campaign 里。** 它是 harness 自测的归档 jitter band（同机 loopback、8–10 秒臂、n=2），在那里同一统计量两次相差：`LAT tcp-rtt p50` 7.5 %、`LAT udp-rtt p50` 6.7 %、`LAT tcp-connect p50` 21.4 %、`MIX tcp-rtt p50` 34.7 %、`LAT tcp-rtt p99` 31.7 %、`udp-rtt p99` 134.4 %——**而 campaign 的判定带是 latency 5 %**。（这与第 2 条里从 E5-b2 实测到的 13 % 相互印证，只是来源不同：一个是自测的 n=2 复跑，一个是真实 campaign 的三轮离散。）
5. **随机化只做了一半，而且做对的那一半带着两个折扣。** 行序确实每轮打乱（`orchestrator.ps1:441,450` 的 `Sort-Object { $random.Next() }`），配合"pass 作区组 + 成对 bootstrap"，这是一个自洽的随机化完全区组设计——这部分应当表扬。但：① `$random` 在循环外创建、seed 固定为 `20261006`，4 轮的 4 个排列是种子的确定函数，同一个 seed 的两次 campaign 会看到完全相同的排列序列（可复现，但没有把"排列"当作随机因素采样）；② **臂序完全不随机**：客户端按 plan 数组顺序 `foreach`，plan 顺序固定，分析器的 `LoadOrder` 也固定——在一个 12 分钟的 plan 内，"臂 A 比 B 慢"与"臂 A 比 B 先跑"在每一行、每一轮里完全共线，而行序随机化对此毫无帮助；③ 双车道相位固定在主 plan 之后，且是约 2 分钟的**两个客户端并发**负载（5 个 full 行各一次），使行与行之间被占用的时间长度不等。
6. **顺序被记录、被打印、没人用，而且描述是错的。** `order.txt` 只含 7 个产品 id、**不含控制块**，而 `benchmarks/WinForward.E2E/README.md:367` 把它说成 "the row ids in the order this pass ran them"，分析器的合成 fixture 与冻结金标准里还带着 `control-pre/post`——真实产物与文档、金标准三者不一致。分析器里读 `Campaign.Order` 的消费者只有"打印"和 `ControlDrift` 的夹逼校验（后者其实用 `startedUtc`）；Metrics / Stats / Verdict 一个都不读。唯一被设计来看顺序漂移的图（`lat-p99-by-pass.png`，"so run-order drift is visible"）明确不画，设计文档承诺的 "run-order regression check" 从未存在。
7. **Holm 的族既不是假设集，也不是问题集，而且打在了错误的一侧。** 族规模随指标漂移 2–21（cpu/mem 21 对、lat./loss./dnsalt. 10 对、`mix.udp.lossRate` 6 对、`dns.*` 2 对），跨 21 个指标之间没有任何联合控制；族里混着 6 个 WinForward 内部对（cpu/mem），而它们要回答的问题与"我方 vs 对手"不是同一个。机制上：`different` 一侧的 p 检验零假设是"比值 = 1"，判决条件却是"CI 越过 ±band"，两者不一致，所以 p 恒为 0、校正乘任何数都无效；校正真正生效的是 `same` 一侧，而那里的 TOST 是交集-并集检验，其 p 值只在交集零假设下超均匀，直接交给 Holm step-down 前提不成立——**方向与文档承诺相反**。
8. **三个 `family = none` 的指标在判决模型里没有规则，而不是"允许得出无差异"。** `thru.goodputMbps`、`persist.reconnects`、`loss.foreignConnection` 在 band 逻辑之前就返回 `no-threshold-declared`，既不能判 `different` 也不能判 `same`，只输出 CI。所以"设计上允许得出无差异"只在五个有阈值的族里成立；吞吐这一栏叠加 harness-audit 第 20 条的封顶之后，**既没有区分度、也没有判定语义**。附带一处口径不一致：这三条 `Kind = ratio` 而 `Unit = count`，`persist.reconnects` 会以"比值的 CI"配 count 单位印出来。
9. **gate 失败不作废任何数字，而且同一格在两张表里待遇不同。** `MetricStatus.Resolve` 全文只读 `RowProfiles`（plan 与承载方式），不看任何 gate；唯一真正会屏蔽取值的数据门是 UDP 会计恒等式，而它只强制每个臂的**第一个前缀**（`Checks/IdentityChecks.cs:107` 取 `prefixes[0]`），MIX 声明的 4 个前缀只查 1 个，且 `MixUdpLossRate` 会回退到未屏蔽的 `metrics/udp.lossRate`。其余 gate（`clientSendLoss`、`backlogDrops`、`scheduleTruncated`、`laneShortfall`、`samplerError`、BASE floor、控制块脏）只出现在 §3.2 与 findings 里，`verdict.json` 的 `metrics` 段独立计算、不读 findings。最尖锐的一处：`windowOverflow > 0` 会让 §5 把该臂的延迟格渲染成 `n/a`，而 `verdict.json` 仍然拿同一个臂的分位数做显著性检验。
10. **没有"预先声明作废规则"这回事，也没有 reference row。** 设计承诺的两条作废规则（`proxiedFraction = 1.0` 才纳入该行、丢弃第一轮作预热）在实现里都是零条——交付的是 `tcpGate ≥ 0.95` 的流计数比与 UDP 的存在性检查，且没有任何规则会因为 gate 而把一行排除出比较。4 个 WinForward 行没有任何 reference row 的声明（全仓搜 `reference_row`/`is_reference` 零命中，唯一的说明在 `orchestrator.ps1:9-11` 的注释里）；判决模型是对称遍历所有对，所以它不会"挑最好的一行"，但也没有任何东西阻止报告作者去挑。



---

## 6. 这套基准现在能支持什么、不能支持什么

### 能支持（附前提）

| 结论类型 | 前提 |
|---|---|
| 正确性与能力边界：UDP 到底有没有被承载、直连有没有泄漏、半关闭 / RST / 部分 FIN 的语义、数据报有没有被串流 | 这些是计数型断言与身份检查（`directLeak`、`foreignConnection`、`identity`），不依赖 CI，也不依赖轮数 |
| "某个方案在这个合成负载形状下、在这台机器上，两轮之间没有出现超过 X 的差异" | 必须把 X 与该指标实测的轮间离散度一起引用（第 2 条那张表），并且限定在**同一口径的 plan** 内 |
| 同一轮 campaign、同一 plan、同一承载类别内的**大差异**排序（例如"某承载根本不通"、"某行慢了 2 倍"） | 隧道成本是共模项，会抵消；差异必须显著大于第 2 条里的噪声 |
| 产品自身的回归检测（同一行、同一 plan、两轮对照） | 用 `compare-records.py` 的四类比较 + 一条**实测的**噪声底；这比跨产品对比更贴合这套装置的实际能力 |
| "本轮测量没有接线错误"：声明臂齐全、恒等式成立、车道都有见证、控制块前后一致 | 这些 gate 是机械的，且已在实现里 |

### 不能支持（除非先补对应缺陷）

| 结论 | 挡住它的发现 |
|---|---|
| "产品 A 比产品 B 快 / 省 X %" | 七行是七个因子束、配置政策未声明、无盲法（公平性轴）；阈值落在轮间噪声里（第 2 条） |
| "产品相对直连只增加 N %" | 隧道未分离、`BASE-singbox` 缺席（仪器轴） |
| "在默认 UDP 承载下的表现" | 头部行用的是非默认的 opt-in UoT；跑产品默认承载的那一行只有 3 个臂（公平性轴） |
| "日常 Windows 端点负载下的表现" | MIX 按字节 81 % 是一条恒定速率 bulk 流、UDP 只占 0.5 %，且报告不打印组成（公平性轴） |
| "长时间稳定 / 无泄漏 / 无端口耗尽" | 最长臂 120 s、整套 870 s、无 soak、无预热丢弃；内存斜率按固定臂序拼接（公平性轴；第 4 节 9/11） |
| "UDP 精度更高"的跨承载排序 | 三种承载同列比较的前提未声明；§5 的 `dns-rtt` 表在多数行上是直连样本（公平性轴） |
| "没有差异 = 等价" | 零事件格的退化区间；`rel.unexpectedEofRate` 的阈值比测量下限还小（第 2 条；仪器轴） |
| 绝对延迟数字 | 无独立外部对照、隧道未分离、客户端既是仪器又是竞争者（仪器轴） |
| "该版本的产品如此" | 产品二进制的版本与哈希不在任何记录里（公平性轴、可复现性轴） |
| 任何将这套数字外推到真实网络的结论 | 路径是 loopback + 一个 vSwitch 跳，没有 WAN 特征 |


---

## 7. 建议

分组按"要不要重跑"划分，因为这是唯一真正昂贵的资源。

### A 类：不重跑就能做（文档、归档、对账）

| # | 做什么 | 成本 | 消除 |
|---|---|---|---|
| A1 | 写一页**测量模型**：要估计什么、要回答的 2–3 个问题、每个问题对应的指标与判定规则、所需轮数、明确的可外推边界 | 0.2 人日 | 第 1 条 |
| A2 | 把已经跑过的两轮原始数据从 `/tmp/wf-bench/` 归档到固定位置（`~/bench-archive/<date>-<campaign>/`），附 `SHA256SUMS`；不提交 git，但位置与校验和固定 | 0.2 人日 | 可复现性 8 |
| A3 | 五个驱动脚本参数化 + 一份 tracked 的 `campaign.env`（地址/端口/路径/凭据变量名）；`deploy-campaign.sh` 记录 `git rev-parse HEAD` + dirty 摘要，dirty 时要求显式 `--allow-dirty` | 0.5–1 人日 | 可复现性 1、2 |
| A4 | 报告里把口径显式化：`planHash` → 仓库文件映射、"canonical / 压缩"分表、跨口径标注不可比 | 0.3 人日 | 可复现性 5 |
| A5 | 修掉那处表注与实现不一致：`connectionId` 那条要么实现、要么改成"按 UTC 窗口对齐"的如实描述 | 0.1 人日 | 仪器 4 |
| A6 | 在报告的限制一节写死五个前提：隧道未分离、绝对延迟无外部对照、环境未固定、时钟靠人工、轮数与实测噪声 | 0.2 人日 | 第 1、2 条；仪器、可复现性 |
| A7 | 给每个指标打印**这次的实测轮间离散度**，并在阈值旁写明"本 campaign 在这个指标上的最小可判差异"——数据已经全在 `verdict.json` 的逐轮值里，不需要重跑 | 0.2 人日 | 统计 2、3 |
| A8 | 把控制块 pre/post 的 CI（`ControlDrift` 已经在算 5 个指标）提为**这次 campaign 的分辨力上限**印在报告开头；把两处偏离写进 README（RFC 8219 的 ≥20 次重复、RFC 9411 的 0.001 % 验证线）；threshold 段的措辞从"判定线"改成"报告阈值"；修准 `order.txt` 的描述 | 0.3 人日 | 统计 3、4、6 |

### B 类：改代码，下一轮 campaign 生效

| # | 做什么 | 成本 | 消除 |
|---|---|---|---|
| B1 | clean-slate 失败进 `Failures` 并写进结果树；控制块也清 sing-box 日志、也写一份 `proxy-truth.json`（期望全 0） | 0.2 人日 | 仪器 3 |
| B2 | `environment.json` 补齐设计点名的量（NIC offload、动态内存序列、产品版本+哈希、电源计划、驱动版本、snapshot id），改为**先读后改**，并把改动本身记成一条 `environmentChange` | 0.5 人日 | 可复现性 3、4；实验 11 |
| B3 | preflight 做一次两机对时（靶机上一个回声端点的三段式往返），偏移写进 `environment.json`，超过 0.2 s 直接 `exit 1`；分析器把超过阈值的钟差升级成会污染 headline 的 finding | 0.2 人日 | 可复现性 9 |
| B4 | 起不来的行写一个 `run.json` 骨架（`failed: true` + 原因），`failures.json` 进产物树，新增一条"声明的行在所有 pass 里都缺席"的 finding | 0.3 人日 | 可复现性 7 |
| B5 | 客户端在 `OnSent` 处记录实际发送时刻并按臂发布直方图；加一条 headroom gate：`generator %vCPU` 超阈值就把该臂的延迟格标成 instrument-contaminated | 0.5 人日 | 仪器 1；第 2 条的归因 |
| B6 | 客户端加 `--socks5 <host:port>`（先只做 CONNECT），控制块里跑一次 `BASE-singbox` | 0.5–1 人日 | 仪器 2；第 4 节第 1 条 |
| B7 | TCP 的 `connectionId` 连接真正实现；UDP 让靶机按帧头 `connId` 逐流计数；每行重启两个靶机实例并用行标签写账本 | 1–2 人日 | 仪器 4 |
| B8 | 靶机加一个确定性注入（`--inject-delay-ms`），跑一条 sensitivity 无产品行；顺手把已有的 `stall`（2 s）放进 `modeMix` | 0.3 人日 | 第 4 节 6/7；实验 8 |
| B9 | 内存斜率按真实运行序拼接（`run.json` 已记每臂 tick 窗口）；把逐轮 drift 做成一条检查而不是靠读者看表 | 0.3 人日 | 第 4 节 11/12 |

### C 类：设计参数，必须重跑才有意义

| # | 决定 | 代价 | 消除 |
|---|---|---|---|
| C1 | **定发布口径**：canonical 还是压缩口径。若定 canonical，先按 C4 降单价 | 0 | 可复现性 5、6 |
| C2 | **轮数 4（或 3）→ 6**：这是让 CI 不再是极差的最低要求（+约 3.2 h/campaign） | 一夜机器时间 | 第 2 条 |
| C3 | `REL` 的样本量提到 `20/s × 180 s = 3600`（全零上界 `3/3600 = 0.083 pp` < 阈值 0.1 pp），或把该家族的阈值改成计划能分辨的值（0.25 pp） | 60 s/轮 | 仪器 5 |
| C4 | 把 DNS（以及 MIX 的真实 `clientSendLoss`）加进 gate 名单；每个率在表里印上自己的分母 | 0.2 人日 | 仪器 6 |
| C5 | 写明**配置政策**并逐行标注来源；把 `bench.ppx` 收进仓库（它是 XML 文本，不是第三方二进制） | 0.3 人日 | 实验 3 |
| C6 | **测一次噪声底**：同一行、同一二进制、同一 session 连跑两遍（约 20–40 min），把逐指标差值作为这次 session 的噪声底写进报告——这是"差异算不算回归"这个问题的实测答案，现在它由五个手写常数回答 | 半小时机器时间 | 统计 3 |
| C7 | 把 `same` 的判定与 Holm 的关系写清：要么对等价性声明改用不校正的 TOST 并声明，要么如实写出"K=4 时校正会把 58.8 % 的 `same` 翻成 `inconclusive`"；同时把 K 的实际覆盖率（86–89 %）印进 `verdict.json` 的 thresholds 段 | 0.2 人日 | 统计 1、4 |
| C8 | 把"预先声明的作废规则"写成数据：`proxiedFraction = 1` 才纳入该行、第一轮作预热丢弃；并让 gate 失败真的作废单元格（现在只有 UDP 恒等式会，且只查第一个前缀） | 0.5 人日 | 统计 5、6 |
| C9 | 重做 Holm 的族定义：改成"参考行 vs 各对手"，或把 WinForward 内部对声明为 exploratory；让 `different` 的零假设与 band 对齐（或以 band 为界的非劣/等价检验）；`same` 一侧不校正或如实声明不校正；`min_passes` 改成命令行参数 | 0.3 人日（只重跑分析器） | 统计 7 |

### D 类：明确不修，只在报告里声明

| # | 缺口 | 为什么现在不修 |
|---|---|---|
| D1 | QUIC / HTTP3 完全没有覆盖 | 这套 harness 没有 QUIC 客户端；实现一个真 QUIC 负载是另一个量级的工程，且当前 MIX 的 UDP 类不能代替 |
| D2 | IPv4/IPv6 比例 | IPv6 流量对这三个产品是另一条代码路径，本轮的结论只对 IPv4 成立，写明即可 |
| D3 | 真正的盲法 | 两台机器、同一个人的现实约束下，盲法不是"加个脚本"能解决的 |
| D4 | 内核态 CPU（DPC/ISR） | 需要 ETW/WPR + ADK，属于环境依赖；改为在报告里把 CPU 的**口径**印在表头（已经做了），并禁止把它读成效率排名 |

---

## 8. 与 harness-audit 的关系

那份是**实现级**清单（21 条 + 披露项），关心"某个计数器会不会算错"；本文关心"这台仪器结构上能支撑什么结论"。四处需要对齐：

| 事项 | 更新 |
|---|---|
| audit 第 4 条：LOSS 的在途窗口永不释放，`sent` 被产品的丢包压缩 | **已过时**：`Client/Arms/LossArm.cs:55,76` 表明槽位现在在 `intended + W` 释放，正常情况下 `sent == supplied`。该缺陷降级为"只在 `clientSendLoss > 0`（即 gate 已失败）时才失真" |
| audit 第 8 条：LAT 的窗口 64、可测上限 128 ms | **前提有误**：提交的 plan 里 LAT/LATLOAD 都是 `window: 4096`，`inFlightCeilingMs` 在 500 rps 下是 8.19 s。64 只是 loader 的默认值 |
| audit 第 21 条：防火墙被关了但没记录 | **半过时**：`orchestrator.ps1:399-402` 现在会写 `firewallProfiles`，但它是在 `Set-NetFirewallProfile -Enabled False` **之后**读的，因此恒为 `false`——记的是脚本自己制造的状态 |
| audit 里的拓扑描述 | **需修正**：承载测试流量的是 **Internal** 网段 `192.168.100.0/24`，不是 `design.md` 说的 External `192.168.77.0/24`；`design.md:4-18` 与部署事实相反，成因未证实 |

除此之外，四份轴文档共提出 **41 条**新发现（统计 12、仪器 7、实验设计与公平性 12、可复现性 10），每条都在自己的文档里带证据锚点、后果与"被推翻的条件"。归类以"设计缺陷"为主；若干条属于"只能如实披露"（无盲法、时钟靠人工、直方图 17.18 s 饱和、MIX 的 `dns-rtt` 轮询开销）；1 条标注为"设计上存在但未能证实"（看门狗超时后 orchestrator 会继续跑，而防火墙已被它恢复）。

---

## 9. 需要你决定的事

1. **发布口径与轮数。** 是接受"这套装置只能分辨大于噪声的差异"、把阈值改到噪声之上并把结论写成区间，还是把轮数提到 6（+3.2 h/campaign）？前者改文档，后者改机器时间，两者不能都要。
2. **要不要补那三个仪器缺口。** `BASE-singbox`（B6）、阳性对照（B8）、连接身份（B7）——各半天到两天，全部在现有两台机器内，都不需要重跑已经跑过的轮次才能开工。它们分别解锁"产品相对直连"、"仪器能不能测出退化"、"账本交叉校验"这三类结论。
3. **对外措辞的边界。** 现有数字继续以"产品对比"的名义发布，还是改成"整机方案对比 + 限制清单"？第 6 节的两张表就是这个问题的答案草稿。
4. **哪些发现转成整改任务。** 我可以按 A/B/C 三组各建一个子任务，把上表的编号与验收条件写进各自的 `prd.md`。

