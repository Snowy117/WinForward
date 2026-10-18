# 统计与决策模型：pass 作为重复单元、判决规则的实际分辨力、以及阈值与顺序的来历

审查对象：`benchmarks/WinForward.E2E`（harness）、`benchmarks/WinForward.E2E.Analysis`（分析器）、
`benchmarks/WinForward.E2E/scripts/orchestrator.ps1`（campaign 驱动）与四份设计文档
（`benchmarks/WinForward.E2E/README.md`、`benchmarks/WinForward.E2E.Analysis/README.md`、
`benchmarks/research/2026-10-06-proxifier-comparison-design.md`、
`.trellis/tasks/10-06-e2e-competitor-benchmark/{design.md,prd.md,implement.md}`）。

**不重复父 agent 已独立核实的三件事，只在需要处引用**：
(a) n=4 时 percentile bootstrap 的 CI 退化成 `[min, max]`；
(b) p 值只有 0 与 ≥0.5 两档；
(c) Holm 在 E5-b2 真实数据里 0 次改变判决；
(d) E5-b2 的轮间离散度：`LAT p50` 中位 13.1%（对照 5% 阈值）。
本文凡以 (d) 为锚点的地方都写明"锚点"，并把它当 σ_log ≈ 0.131 的量级使用，不再重新推导。

本文出现的"仿真"指：按 `Stats/BootstrapPair.cs` 的算法逐句复现的一个模型实验（对数正态跨轮波动、每格 K 个 per-pass 值、10000 次重采样、按 pass 重采样、与代码相同的 CI 与判决规则、m=10 的 Holm 族）。它是模型，不是 campaign 数据；用途是回答"给定真实噪声水平，这套规则会怎么判"，可被任何真实数据推翻。

---

## 结论摘要

1. **"pass"不是独立重复，是同一次 session 内的时间切片。** 4 轮 pass 在同一个 orchestrator 进程里背靠背跑完，VM 不重启、sing-box 只在开跑前启动一次（`orchestrator.ps1` 的 `Start-SingBox` 在 pass 循环之前）、`environment.json` 只在开跑前写一次。所以那 4 个 per-pass 值共享了 campaign 级的一切（宿主负载、sing-box 长寿命状态、VM 内存碎片、防火墙状态、Windows 更新状态），`median [p25–p75] across passes` 度量的是"这一个下午这台机器上这个数字抖了多少"，**不是"这个数字可不可复现"**。
2. **分辨力与实测噪声差一个数量级。** 以 E5-b2 实测的 13.1% 轮间离散度为锚点（σ_log ≈ 0.131）仿真：K=4 下要判出 `different`，真实比值需要 **≈1.30**（82% 把握）、≈1.40（95%）；K=8 时 1.30 → 95%。**30% 以内的差异，这套实验基本读不出来**，而 5%/10%/0.5pp/0.1pp 这四条判定线都远在分辨力之下。
3. **"same"（等价声明）在这套条件下几乎不可达成。** 同样的锚点下，真实比值恰为 1.00（两个产品完全一样）时判成 `same` 的概率是 **0.8%（K=4）/ 0.3%（K=8）/ 2.0%（K=16）**，其余 96–99% 是 `inconclusive`。反向的假 `different` 在 K=4 时有 **3.5%**（K=8 降到 0.5%，K=16 为 0%）。也就是说 `same` 和 `different` 的出现频率主要由那 4 个数的整齐程度决定，不由产品差异决定。
4. **阈值里只有四个有文档、没有一个有推导，第五个连文档都没有。** `design.md:217` 列了 latency 5 % / CPU 10 % / memory 10 % / loss 0.5 pp；TCP 意外率的 0.1 pp **只存在于代码**（`Metrics/MetricSpec.cs:72`），任何设计文档里都没有。而四个有文档的也只作为断言出现，代码注释自己说它们是 "declared constants, not tuned after seeing a result"（`MetricSpec.cs:50-52`）——没有说它们从哪来。更严重的是 TCP 那条比这套基准自己援引的 RFC 9411 验证线（0.001 %）宽 **100 倍**。
5. **随机化：行序是对的，臂序没有；记录是有的，分析没有。** 每轮用 `Sort-Object { $random.Next() }` 真的重排了 7 个产品行（`orchestrator.ps1:441,450`），配合"pass 为区组 + 成对 bootstrap"构成一个自洽的随机化完全区组设计，这部分是加分项。但 `control-pre`/`control-post` 被钉死在首尾（`:453,459`），臂序完全固定（`Client/ClientRunner.cs:40-41` 按 plan 数组顺序），`order.txt` 只写产品行（`:451`，不含控制块，而 fixture `make_tree.py:1459` 含控制块），且没有任何统计使用它——唯一被设计来看顺序漂移的图明确不画（`Analysis/README.md:440` 对 `:430-444`）。
6. **能支持的结论**：量级 ≥30%、方向一致、工具侧 gate 全绿、且分析那 4 个数恰好整齐的差异，以及由此得到的粗粒度排序。**不能支持的结论**：任何 5–10% 量级的优劣、任何"两者相同"、任何"WinForward 整体不输"、任何"这个数字可复现"。前提分别在第 1、2、4 条发现里。

---

## 发现

### 1. pass 的语义：同一次 session 内连跑的时间切片，不是独立重复

**证据**

- pass 循环是同一个 PowerShell 进程里的一个 `for`：`orchestrator.ps1` 的 `for ($pass = 1; $pass -le $Passes; $pass++)`，循环体内按 `$order` 逐个 `Invoke-Row`，末尾两个 `Invoke-ControlBlock`。没有进程重启、没有 VM 复位、没有重新部署。
- sing-box 只在 pass 循环**之前**启动一次：`if (-not (Start-SingBox)) { … exit 1 }` 位于 `$passes` 循环之上；循环内只有 `Clear-SingBoxLog`（清日志，不重启进程）。所以 sing-box 的累计状态（UDP association 表、GC、句柄增长）跨 4 轮单调累积。
- `environment.json` 只在循环之前写一次（`Write-JsonNoBom -Path (Join-Path $OutRoot 'environment.json')`），内容是 `started / os / build / logicalCpus / totalRamMb / freeRamMb / passes / seed / firewallProfiles / singBox / target`。循环内**没有任何按轮的环境记录**。
- 每个产品行每轮都 `Start-Product` / `Stop-Product`，且 `Stop-Product` 末尾有 `Start-Sleep -Seconds 3`、`Invoke-Row` 在启动后有 `Start-Sleep -Seconds 5`。也就是说：轮与轮之间，机器状态是"上一个产品的驱动刚卸载 3 秒"。
- 计划里的 `-Passes 4` 对应 `AGENTS.local.md:155` 的用法，而设计文档说的是另一件事：`implement.md:86` "Four passes with randomized program order; **pass 1 discarded as warm-up**"、`prd.md:115` "At least three measured passes **after a discarded warm-up pass**"。仓库脚本里搜不到丢弃 `pass1` 的代码（`scripts/*.sh`、`scripts/*.py`、`orchestrator.ps1` 对 `pass1|pass 1|warm-up pass` 零命中），而 `publish-campaign.sh` 是 `Compress-Archive -Path C:\wfbench\results\*` 全量打包后直接 `analyze.sh --raw "$raw"`。

**后果**

- **这叫"同一 session 内的重复"，不是"独立重复"。** 想让一个数字可复现，需要变化的是那些在真实使用中会变的东西：重启后的驱动加载顺序、冷启动的 sing-box、隔天的宿主负载、Windows 更新后的状态。这四轮 pass **一个都没变**——它们全都是恒定的。因此 CI 只覆盖了短时间尺度的抖动（热、缓存、临时状态），而所有 campaign 级因素是 n=1。
- 直接不可信：任何以"4 轮一致"为证据的稳定性/可复现性陈述，以及任何把 `[p25–p75]` 或 CI 当作"这个结论的不确定度"的引用。那个区间只是"这个下午的抖动"。
- 另一个具体后果：`environment.json` 的 `freeRamMb` 是开跑前的**一个**快照。在一台动态内存的 VM 上（`benchmarks/research/2026-10-06-proxifier-comparison-design.md` 风险 5 专门警告 "Dynamic-memory ballooning changes the measurable working set mid-matrix"，其缓解措施是 "log guest-visible RAM per sample; reject windows where it changed"），如果内存在第 2 轮被 ballooning 改掉，**数据里没有任何字段能发现它**：`records/*.jsonl` 里的 `envWorkingSetBytes` 是采样进程自己的 working set（`records/BASE.jsonl::envWorkingSetBytes` 那一条），不是 guest 可见内存。
- 错误印象：`(n=4)` 与 `passes: ["pass1","pass2","pass3","pass4"]` 读起来像"重复了 4 次实验"；实际是"一次实验被切成了 4 段"。这一条是所有其它统计结论的上游——如果读者接受"4 轮 = 4 次独立重复"，后面每一条区间都会被高估。

**归类**：设计缺陷（要真正提高可复现性声明，必须让轮与轮之间变化机器状态；这不需要新机器，但需要额外机器时间，属于用户要拍板的取舍）。

**被推翻的条件**：在 `orchestrator.ps1`、`deploy-campaign.sh` 或 `wf.sh` 里找到轮与轮之间的 VM 复位、sing-box 重启、产品驱动重装或机器状态重置。我只逐行读了 `orchestrator.ps1` 与 `publish-campaign.sh`；`wf.sh` 是 gitignored 的本地胶水，**未能证实**它里面没有额外的轮间复位步骤。

---

### 2. 每格的独立重复数 = pass 数（默认 4，判一个 pair 至少 3），而 pass 内几万个样本被压成一个分位数

**证据**

- `orchestrator.ps1:23` `[int]$Passes = 4,`；`WinForward.E2E/README.md:286` `| \`-Passes\` | 4 | passes to run |`。一轮里每个产品行跑一次自己的 plan。
- 轮内不重复：`Client/ClientRunner.cs:40-41` 是 `foreach (var arm in arms) { var summary = await RunArmAsync(…); }`，按 plan 数组顺序把每个臂各跑一次。
- 每轮只产出一个值：`Metrics/MetricCatalogue.cs` 的 `PerPassValues` 对每个 `passId` 调一次 `spec.Extract`，取回一个数（一个直方图分位数或一个比率）。聚合规则见 `WinForward.E2E.Analysis/README.md:137-141`。
- 判决门槛：`Cli/AnalysisOptions.cs:21` `DefaultMinPasses = 3`；`Stats/BootstrapPair.cs:209-215` 少于它直接返回 `inconclusive`；`WinForward.E2E.Analysis/README.md:463`。
- **`min_passes` 不可配置**：`AnalysisOptions` 唯一暴露的数值开关是 `--warmup-seconds`、`--resamples`、`--seed`（`Cli/AnalysisOptions.cs:23-30` 的 usage 串），`MinPasses` 只由常量赋值（`Loading/CampaignLoader.cs:69` `MinPasses = AnalysisOptions.DefaultMinPasses`）。分析器无法表达"这次 campaign 至少要有 8 轮才算数"。
- 冻结树实测：`verification/golden/py-verdict.json` 的 `passes` 是 `["pass1","pass2","pass3"]`、`bootstrap.min_passes` 是 3、`bootstrap.resampling_unit` 是 `"passes (never samples)"`。

**后果**

- 决策只用了 4 个数。campaign 的绝大部分机器时间花在把一个 pass 的 per-pass 值估得更准（几万到几百万个样本），而 pass 之间的变异——唯一影响判决的那个量——只被采样了 4 次。这是"精度花在了不决定结论的地方"。
- 3 轮是"允许出结论"的下限，不是"够用"。`README.md:463` 的措辞是 "Fewer than 3 passes is never enough"，读者容易读成"3 轮就够了"。
- 设计文档自己援引的标准不是 3–4 次：`benchmarks/research/2026-10-06-proxifier-comparison-design.md` 把 RFC 8219 列为 [28] 并写 "with the test repeated ≥20 times and the 1st and 99th percentiles of the per-repetition values reported as the measure of variation"；`prd.md:121` 明确要求交付物里写 "deviation from RFC 8219's ≥20 repetitions"。这条偏离在两份 README 里**一个字都没有**（`grep -niE "8219|repetition"` 在 `WinForward.E2E/README.md` 与 `WinForward.E2E.Analysis/README.md` 上零命中）。
- 错误印象：`(n=4)` 被读成"4 组、每组几万样本"；RFC 8219 的 ≥20 次偏离被当成不存在。

**归类**：只能如实披露（次数是否提高是用户的时间取舍）+ 一行文档修复（把 RFC 8219 的偏离写回交付 README）。

**被推翻的条件**：给出一次 `-Passes >= 20` 的真实 campaign 树，且分析器把 20 个 per-pass 值全部纳入。

---

### 3. 阈值（5 % / 10 % / 10 % / 0.5 pp / 0.1 pp）的来历与文档依据

**证据**（逐条查文档，不猜）

| 阈值 | 代码锚点 | 设计文档依据 | 推导 |
|---|---|---|---|
| latency 5 % | `Metrics/MetricSpec.cs:60` | `design.md:217` 列出 | 无 |
| CPU 10 % | `Metrics/MetricSpec.cs:63` | `design.md:217` 列出 | 无 |
| memory 10 % | `Metrics/MetricSpec.cs:66` | `design.md:217` 列出 | 无 |
| UDP loss 0.5 pp | `Metrics/MetricSpec.cs:69` | `design.md:217` 列出 | 无 |
| TCP unexpected 0.1 pp | `Metrics/MetricSpec.cs:72` | **任何设计文档里都没有** | 无 |

- `design.md:217` 的原文是 "`verdict.json` — the decision per metric, with the pre-declared practical thresholds (latency 5 %, CPU 10 %, memory 10 %, loss 0.5 percentage points) and Holm–Bonferroni correction."——四个数字作为断言出现，句子里没有"为什么是 5 %"。
- 代码自己承认它们是声明的：`Metrics/MetricSpec.cs:50-52` "The thresholds are declared constants, not tuned after seeing a result: the interval is computed first and judged against the band afterwards, and the band is published in `verdict.json`'s `thresholds` so a reader can see which one judged a pair."——"不是看了结果才调的"是对的、也是好设计，但它回答的是"没有 p-hacking"，不是"这个数值是怎么来的"。
- TCP 那条是唯一连"声明"都没有的：`Metrics/MetricSpec.cs:72` `TcpUnexpected = new(Kind: "diff", Value: 0.1, Text: "0.1 pp TCP unexpected rate")`；`verdict.json` 的 `thresholds` 段（`Verdict/VerdictSections.cs:72-79`）发布它，`WinForward.E2E.Analysis/README.md:304` 也发布它，但**设计文档、prd、research design doc 都找不到 0.1 这个数**。
- 而设计文档援引过一条更严的线，没有落实：`benchmarks/research/2026-10-06-proxifier-comparison-design.md:206-207` — "RFC 9411 gives you a published sanity threshold for the validation criteria: failed application transactions **< 0.001 %** and 'terminated TCP connections due to unexpected TCP RST sent by the DUT/SUT **< 0.001 %**' of initiated connections … **Adopt these as pass/fail gates and report your observed rates against them.**"（同文 `:434-436` 重复一次）。交付的分析器里搜不到这条：`grep -rn "0.001" benchmarks/WinForward.E2E.Analysis --include=*.cs` 零命中。换算：RFC 9411 的 0.001 % = 0.001 pp；交付的带子是 0.1 pp，**相差 100×**。
- 设计文档还要求过族的预声明，也没落实：同文 `:579`（风险 15）"Multiplicity: 5 programs × ~25 metrics | **Pre-declare the primary family**; Holm–Bonferroni within each metric family; publish the full secondary table as exploratory"。交付物里 21 个指标全部按 headline 处理，没有主次之分（`Metrics/MetricCatalogue.cs:96-117` 的 `s_specs` 21 条；`WinForward.E2E.Analysis/README.md:289` "Twenty-one headline metrics"）。

**后果**

- "差异 X% 算不算回归"今天完全由五个常数回答，其中四个有一句断言、一个什么都没有。以第 4 条发现的锚点看，这四个"判定线"全部落在仪器的分辨力之下，所以它们事实上不是判定线，只是**报告阈值**：决定一个结论会不会被印上 `different`/`same` 的字样。
- TCP 那条最容易被误用：判 `same` 与"该产品按 RFC 9411 的验证准则已不合格 100 倍"完全相容。**"TCP 意外率无差异"不能被引用来支持"可靠性达标"**。
- 错误印象：`verdict.json` 的 `thresholds` 段整齐地列出五条，读者会以为它们是"经过论证的可接受差异上限"；实际上没有一条有推导，其中一条没有文档，而本项目援引过的更严的那条被丢了。

**归类**：只能如实披露（改常数是一行，但要重跑分析器；把"这是报告阈值、不是验证准则；RFC 9411 的线是 0.001 %、本项目未采用"写清楚，零成本）。

**被推翻的条件**：找到 5 %/10 %/0.5 pp 中任何一条的推导记录（例如来自某个内部分级标准或历史数据）；或找到 RFC 9411 的 0.001 % 在别处被采用为 gate 的证据。目前两份 README、四份设计文档里都没有。

---

### 4. 判决规则在实测噪声下的分辨力："30 % 以内读不出来，等价声明几乎不可达成"

**证据（仿真，锚点 = E5-b2 实测的 `LAT p50` 轮间离散度中位 13.1 %，按 σ_log ≈ 0.131 使用）**

P(判成 `different`)，判决规则同 `Stats/BootstrapPair.cs:240`：

| K | 真 1.10 | 真 1.20 | 真 1.30 | 真 1.40 | 真 1.50 |
|---|---|---|---|---|---|
| 4 | 15 % | 50 % | **82 %** | 95 % | 99 % |
| 8 | 15 % | 69 % | **95 %** | 100 % | 100 % |
| 16 | 24 % | 92 % | 100 % | 100 % | 100 % |

真比值恰为 1.00（两个产品完全一样）时的判决分布：

| K | `same` | `inconclusive` | `different` |
|---|---|---|---|
| 4 | **0.8 %** | 95.8 % | 3.5 % |
| 8 | **0.3 %** | 99.3 % | 0.5 % |
| 16 | **2.0 %** | 98.0 % | 0.0 % |

对照：同一个仿真的低噪声档（σ=0.02）下，真平局有 92.3 % 判 `same`、K=4 时真 1.08 有 71 % 判 `different`。所以上表不是规则的固有性质，而是**规则 × 这台机器的噪声**的结果。

**后果**

- **最小可辨效应 ≈ 1.30（K=4，82 % 把握）**。这就是"这套仪器能回答什么"的硬边界：30 % 以内的差异，`different` 与 `inconclusive` 的区分几乎不携带关于产品的信息。报告里绝大多数"持平/无差异"的表述都落在这个区间内。
- **`same` 实际上是不可达成的**：真平局也只有 0.8 %（K=4）会被判成等价，96 % 落在 `inconclusive`。而且提高 K 的收益很小（K=16 只有 2.0 %），因为 CI 要整体落进 ±5 % 的同时还要通过 `same` 侧的 Holm（第 5 条发现）。**结论：`same` 的出现频率不携带"产品是否相近"的信息；它只在 4 个数恰好整齐时出现。**把 `same` 读成"证明了两者相同"是本轴最危险的误读。
- **反向也有一个真实的小概率**：真平局被判 `different` 的概率在 K=4 时是 **3.5 %**（K=8 为 0.5 %，K=16 为 0 %）。这与父 agent 在 E5-b2 上核到的"Holm 0 次改变判决"合起来意味着：**这 3.5 % 的假差异不会被任何校正拦下来**。
- 由此不可信：任何形如"WinForward 的 p50 比 X 低 8 %，所以更快"的陈述（真 1.10 只有 15 % 会被判 `different`，且判出来时无法与 3.5 % 的假阳性区分）；以及所有以 `same` 为依据的并列结论。
- 错误印象：报告里 `inconclusive` 的占比会非常高，而它读起来像"差别不大"；正确解读是"这次实验没有分辨力"。

**归类**：设计缺陷（提高 K、或改判决规则、或把可辨效应量写进报告并据此约束结论用词；三者都在两台机器内可做，前两者只需重跑分析器＋重跑 campaign）。

**被推翻的条件**：用真实 per-pass 值算出各指标的跨轮离散度，若某些指标（例如样本量大的 `LATLOAD` p50）实测离散度 ≤2 %，则那些指标的分辨力远好于上表，本条要按指标拆开。父 agent 给的 13.1 % 是 `LAT p50` 的中位数，**不是所有指标的统一值**——这是本条最大的不确定性，我按"最坏情形"使用它。

---

### 5. Holm 族与校正方向：族不是要回答的假设集，而校正在 K≤6 时对 `different` 是空操作

**证据**

- 族定义：`Metrics/MetricComparisons.cs:87` `("holm_family_size", VerbatimJson.Integer(candidates.Count(pair => pair.Comparable)))`，`:209` `var family = candidates.Count(pair => pair.Comparable);`，`:230-233` **每个指标各自**调一次 `DescriptiveStats.HolmAdjust`（`Stats/DescriptiveStats.cs:169-190`）。成员是矩阵里全部可比行对（`Candidates` 的 `rowIds` 双重循环）。
- 冻结树实测的族大小与可比对数（`verification/golden/py-verdict.json`）：`lat.tcp_rtt.p50/p99`、`latload.*`、`lat.udp_rtt.p50`、`loss.*`、`dnsalt.*`、`rel.*`、`persist.*`、`thru.goodputMbps`、`loss.foreignConnection` 等 **10 对**（`not_comparable_pairs` 26，总 36）；`cpu.proxy.vcpuPct`、`mem.privateBytes.p50` **21 对**（不可比 15）；`mix.udp.lossRate` **6 对**；`dns.answerRate`、`dns.rtt.p50` **2 对**。
- 族里有多少"没人要问的比较"（WinForward 自己的构建之间）：`cpu`/`memory` 族 21 对里 6 对（4 个 WinForward 行的 C(4,2)）；`lat.udp_rtt.p50` 族 10 对里 3 对；`lat.tcp_rtt.p50` 族 10 对里 1 对（只有 `wf-aot-opt` 与 `wf-fdd-opt` 跑 full plan 且可比）；`dns.*` 族 2 对里 0 对。**族的规模在 2–21 之间随指标漂移，且与"要回答的问题"无关。**
- 跨指标零校正：21 个指标各自成族，`README.md:289` "Twenty-one headline metrics"，没有 primary/secondary 之分；而设计文档 `benchmarks/research/2026-10-06-proxifier-comparison-design.md:579` 要求 "Pre-declare the primary family … publish the full secondary table as exploratory"。
- 校正方向（机制，与父 agent 的 E5-b2 实测互补）：`different` 用的 p 值检验的是"比值 = 1"（`Stats/BootstrapPair.cs:80-81`、`:105`），而判决要求的是"CI 越过 ±band"（`:240`）——**零假设与判决规则不是同一个**。父 agent 已核实该 p 值在实测中只有 0 与 ≥0.5 两档、Holm 在 E5-b2 里 0 次改变判决；机制上一旦 CI 整体越出 ±5 %，`P(θ* ≤ 1)` 必然 ≤2.5 %，p ≤ 0.05，而在 K≤6 时它实际取 0，乘 m 仍是 0。**校正真正生效的地方在另一侧**：`same` 用的是 TOST 等价 p 值（`:96-98` 的 `Math.Max(belowEdge, aboveEdge)`），而 `Metrics/MetricComparisons.cs:272` 把 `same` 的 claim 交给同一个 `HolmAdjust`。TOST 是 intersection-union 检验，"两尾取最大"的 p 值只在交集零假设下超均匀，按 Holm 的 step-down 处理其适用前提不成立。

**后果**

- **"做了 Holm–Bonferroni 校正"这句话在当前 K 下对差异结论是空的**，而它真正削弱的是等价结论——方向与文档承诺相反。`WinForward.E2E.Analysis/README.md:306-311` 说校正的目的是 "the adjusted verdict requires the adjusted p-value of the claim actually made to be below 0.05"，读者会理解为"防止假差异"；实测与机制都指向"消灭真等价"。
- 族不是假设集：族里既有没人要问的 WinForward 内部对（在 `cpu`/`memory` 族里占 6/21），又缺少跨指标的联合控制。**"WinForward 在 21 个指标里有 N 个优于对手"这个整体主张没有任何对应的族。**
- 错误印象：`holm_family_size: 10` / `holm_p_value` 这些字段读起来像"多重比较已经处理完毕"。而 `holm_p_value` 在 `different` 那一侧基本恒为 0。

**归类**：设计缺陷（族定义是数据，改它只需重跑分析器；`different` 的零假设要改成与 band 一致；`same` 侧的族控制要么换方法要么如实声明不校正）。

**被推翻的条件**：在一份真实 `verdict.json` 里找到至少一个 `raw_verdict: different` 且 `holm_verdict: inconclusive`、`holm_p_value` 非 0 的例子（父 agent 在 E5-b2 上已核到 0 例，所以需要的是一份不同的 campaign）；或者找到一段文档说明 Holm 族按"参考行 vs 各对手"定义。

---

### 6. `family = none` 的三个指标在判决模型里**没有决策规则**，而不是"允许判无差异"

**证据**

- `Metrics/MetricCatalogue.cs:45` `NoFamily = "none"`；`:79` `loss.foreignConnection`、`:102` `thru.goodputMbps`、`:111` `persist.reconnects` 三条走它。
- `Metrics/MetricSpec.cs:75` `None = new(Kind: "ratio", Value: null, Text: "no pre-declared threshold")`；`:80-88` 的 `For("none")` 落到它。
- `Stats/BootstrapPair.cs:217-220`：`if (threshold is null) { return ("no-threshold-declared", "no pre-declared practical threshold covers this metric"); }`——在 band 逻辑**之前**返回，所以既不可能 `different` 也不可能 `same`。
- 冻结树实测：这三个指标的 10/10/10 个可比对全部是 `no-threshold-declared`。
- 一个附带的口径不一致：这三条的 `Kind` 是 `"ratio"`（CI 与 estimate 按 a/b 报），而 `Unit` 是 `"count"`（`MetricCatalogue.cs:79,111`）——`persist.reconnects` 会以"比值的 95 % CI"配上 `count` 单位出现。

**后果**

- 回答"有没有一条结论是设计上允许得出 A 与 B 无差异的？"——**有，但只在五个有阈值的族里**（latency / udp-loss / tcp-unexpected / memory / cpu）。`none` 族既不允许不同也不允许相同，只有 CI。**设计上没有任何一条规则能对 THRU 吞吐得出"相同"的结论。**
- 这直接影响最容易被引用的一栏：`thru.goodputMbps` 属 `none` 族，而它同时被自己的 plan 速率封顶（`WinForward.E2E/README.md:683` 的 THRU 条；`WinForward.E2E.Analysis/README.md` 第 8 项）。所以"四行吞吐相同"这个印象**既没有决策规则支持、也没有区分度**。
- `loss.foreignConnection` 与 `persist.reconnects` 没有这个问题：它们靠 findings 走（`foreignConnection > 0` → `correctness-failure`；`reconnects > 0` → §10 的 verdict 列读 `BROKE THE IDLE CONNECTION`）。三条里只有 THRU 是真的没有规则。
- 错误印象：读者把 `no-threshold-declared` 当中立，然后自己拿 CI 目测——这正是设计想避免的 "guessing"。

**归类**：设计缺陷（给 THRU 声明"上限/非劣"语义，或明确写"该列不可用于任何比较结论"）。与 harness-audit 第 20 条互补：那条说"上限之上无区分度"，本轴说"它连决策规则都没有"。

**被推翻的条件**：找到一处依据 THRU 的 CI 得出名次或等价结论的报告文字，或找到 `thru` 的 family 被改成有阈值的记录。

---

### 7. 行序随机化的实现质量：真的随机了，但只随机器产品行；且排列序列由种子完全决定

**证据**

- `orchestrator.ps1:441` `$random = [System.Random]::new($Seed)`，`:42` `[int]$Seed = 20261006`，`:450` `$order = @($products | Sort-Object { $random.Next() })`。`Sort-Object` 对每个输入对象求一次键，所以这是一次真实的随机排列（不是排序一个已经排好的表）。
- 控制块不参与：`:451` 写完 `order.txt` 之后，`:453` `Invoke-ControlBlock … -Tag 'pre'`、`:459` `… -Tag 'post'`——`control-pre` 永远在产品块之前、`control-post` 永远之后。这一条是**有意的**且必须如此：`Checks/ControlDrift.cs` 的 `Ordering` 就是靠 `startedUtc` 校验"控制块夹住了产品块"。
- 种子固定且只创建一次：`$random` 在 pass 循环**之外**创建，循环内不重播种；`environment.json` 记录 `seed = $Seed`。所以 4 轮 pass 的 4 个排列是 `-Seed` 的确定性函数——两次用同一 seed 的 campaign 会看到**完全相同**的 4 个排列序列。
- 设计上是自洽的：轮内随机 + pass 作区组 + `Stats/BootstrapPair.cs:63` 的**成对** bootstrap（`paired` 要求两侧跑过同样的 pass 集合）三者互相配套，构成一个标准的随机化完全区组设计。这部分是本套实验设计里做得对的地方。

**后果**

- 行序随机化完成了它该做的事：没有哪个产品被系统性地安排在最热或最冷的时段，产品效应与时段效应在期望上解耦。**"哪个产品排在前面"不是这套数据的主要威胁。**
- 但它是**块内**随机，不是完全随机：`control-pre` 的位置固定意味着"产品块的第一行"永远紧跟在一次 60 秒的 BASE 运行之后（机器已经被 BASE 预热过），这一条对所有产品一致，无害；可它同时意味着 `control-pre` **永远不跟在任何产品后面**——harness-audit 第 16 条已经指出这一点（"唯一能发现上一轮产品留下污染的那一行，永远不跟在产品后面"），本轴只补一句：`control-post` 也确实永远跟在最后一个产品后面，所以"残留污染"这件事有且只有一个观测点，位于固定位置。
- 固定种子 + 固定排列的后果是双向的：好在可复现（同一 seed 的 campaign 可比），坏在**没有把"排列"当成一个随机因素来采样**——4 轮只有 4 个排列，而且永远是同 4 个。如果某个产品恰好总排在某类行之后，两次同 seed 的 campaign 会重复同一个偏差。要评价"排列效应"，需要的是不同 seed 的 campaign，而不是同一 seed 的更多轮。
- 错误印象：`design.md:149` 写的是 "per pass randomized program order"，`benchmarks/research/2026-10-06-proxifier-comparison-design.md:577`（风险 13）写的是 "Randomized program order per pass; **run-order regression check**"。读者会以为整轮 pass 的**程序**顺序都随机了、并且有一个顺序漂移的检查。实际上：只有 7 个产品行的先后被随机，臂的先后没有（第 8 条发现），"run-order regression check" 不存在（第 9 条发现）。

**归类**：只能如实披露（"随机化到哪一层"需要在 README 里写清楚；`-Seed` 的语义要在文档里说明）+ 设计缺陷（"run-order regression check" 要么落实要么从设计文档里删掉）。

**被推翻的条件**：在 `wf.sh`/`deploy-campaign.sh` 里找到每轮换 seed 或额外打乱的动作（**未能证实**：这两个文件我没能逐行读完，`wf.sh` 是 gitignored 的本地胶水）；或者在 `orchestrator.ps1` 里找到对 `$products` 之外的东西也参与 `$order` 的证据。

---

### 8. 臂序完全不随机：位置与身份在每一行、每一轮完全共线

**证据**

- `Client/ClientRunner.cs:40-41` 按 plan 的数组顺序 `foreach (var arm in arms)` 逐个 `await RunArmAsync`，无打乱、无重复、无交错。
- plan 的数组顺序固定：`scripts/plans/full-plan.json` 的 10 个臂依次是 `IDLE, LAT, LATLOAD, DNS, DNSALT, LOSS, REL, THRU, MIX, PERSIST`；`scripts/plans-windows/full-shape-plan.json` 的 README 说明称其 "arm order, arm names and key sets are identical to `scripts/plans/full-plan.json`"。
- 分析侧也按固定顺序遍历臂：`Model/ArmRecords.cs:39-40` `LoadOrder = ["IDLE","LAT","LATLOAD","DNS","DNSALT","LOSS","REL","THRU","MIX","PERSIST","BASE"]`。
- 双车道阶段固定在主 plan **之后**、属于同一行：`orchestrator.ps1` 的 `Invoke-Row` 里 `if ($Product.Dual) { Invoke-DualPhase -Product $Product -RowDir $productDir }`；`Dual = $true` 的是 5 个 full-plan 行，`wf-aot-nativeudp` / `wf-aot-dnsrelay` 是 `$false`。`Invoke-DualPhase` 用 `Start-Process` 同时拉起 proxied 与 direct 两个客户端（`dual-plan.json` 是 `LAT 40 s + MIX 40 s + LOSS 40 s`），也就是**约 2 分钟的双客户端并发负载**，被算在这一行的占用里。

**后果**

- **一个 12 分钟的 plan 内部，"臂 A 比臂 B 慢"与"臂 A 比臂 B 先跑"完全共线**，且这种共线在每一行、每一轮里一模一样。热/降频、前一个臂留下的 socket 与 TIME_WAIT、内存增长、甚至 CPU 调频状态，都随位置单调变化，无法与臂身份分离。行序随机化对此**零帮助**——它打乱的是行，不是臂。
- 具体不可信的结论：任何**跨臂**的比较（例如"LAT 的 p50 与 MIX 的 TCP p50 哪个更接近直连"），以及把某个臂的绝对值当作该产品"一般性能"的说法。排在最前面的 `IDLE` 与最后面的 `PERSIST` 之间的机器状态差异最大；`MIX`（全 plan 最重的臂）紧邻 `THRU` 与 `PERSIST`。
- 还有一个跨行的顺序效应：行与行之间的占用**不等长**（full-plan 行 = 10 个臂 + 双车道，partial 行 = 3 或 2 个臂），所以每一行的"前驱"是什么随轮变化。这本身是被随机化覆盖的（好事），代价是它把额外方差注入了 pass（由第 2、4 条发现的 K=4 来买单）。
- 错误印象：设计文档与 README 的叙事让读者以为"顺序已经被处理了"。真实情况是顺序只被处理到行这一层。

**归类**：设计缺陷（打乱臂顺序或至少轮换臂顺序，是纯代码改动；代价是每个结果记录需要多一个"臂在 plan 中的位置"字段，且 `ArmRecords.LoadOrder` 的假设要一起改）。

**被推翻的条件**：在 `ClientRunner.cs` 或 `ArmDispatch.cs` 里找到对 `arms` 的排序/打乱；或者找到证据表明实际使用的 plan 里臂序逐轮不同。

---

### 9. 顺序被记录、被打印，但没有任何统计使用它；唯一的顺序漂移图明确不画；fixture 与真实产物不一致

**证据**

- 记录：`orchestrator.ps1:451` `Write-JsonNoBom -Path (Join-Path $passDir 'order.txt') -Value (($order | ForEach-Object { $_.Id }) -join ',')`。`Write-JsonNoBom` 对 string 原样写出（`:73-77`），所以文件是一个裸的逗号连接串。读回：`Loading/CampaignLoader.cs:29` `OrderFile = "order.txt"`、`:164-185` `ReadOrder` 按 `[' ', '\t', '\r', '\n', ',']` 切分。
- 打印：`Tables/TableEnvironment.cs:102` 放进 §2 的 `{passId} run order (order.txt)` 行，拼成 `a -> b -> c`。
- **没有任何统计使用它**：分析器里读 `Campaign.Order` 的消费者只有 `TableEnvironment`（打印）与 `ControlDrift`（用 `run.json` 的 `startedUtc` 校验夹逼，不读 `order.txt`）。`Metrics/*`、`Stats/*`、`Verdict/*` 都不读它。
- 唯一被设计用来看顺序漂移的图不画：`WinForward.E2E.Analysis/README.md:440` 第 6 项 `lat-p99-by-pass.png` — "`LAT` p99 against pass index, so run-order drift is visible"；`:430-444` 明写 "None of them is drawn: the analysis writes `plots/SKIPPED.md` unconditionally instead"。设计文档也要求过这张图（`design.md:214` 的 `plots/` 一行，`:169` 的 "pass index, program order"）。
- 最像"顺序检查"的那条序列还用错了顺序：`WinForward.E2E.Analysis/README.md:401-408` 第 7 项——内存泄漏斜率的时间序列是"按分析器固定的 `ArmRecords.LoadOrder` 把各臂的样本首尾相接"，不是这一轮真实跑的顺序（"the curve jumps between arms and back, so the slope and the leak verdict describe the shape of the concatenation rather than a product's memory over wall-clock time"）。而 `orchestrator.ps1:450` 每轮都换排列。
- 记录的内容也不完整：`order.txt` 在 `:451` 写，那时**还没有**跑控制块，写进去的是 7 个产品 id；而 fixture `verification/synthetic/make_tree.py:1459` 写的是 `", ".join(order)`，其 `order` 含控制块，所以 `verification/golden/py-tables.md:152` 显示 `control-pre -> wf-aot-opt -> … -> control-post`。真实 campaign 的 §2 那一行**不会**出现控制块。
- 文档与这一事实不符：`WinForward.E2E/README.md:367` 把 `order.txt` 描述为 "the row ids in the order this pass ran them"。按这个描述，真实产物是不完整的。
- 没有任何按轮的环境记录（第 1 条发现）：`environment.json` 只在循环前写一次，「轮间是否发生了状态变化」在数据里无处可查。

**后果**

- 三个已知的系统性漂移源（热/降频、上一行残留的驱动过滤、内存增长）在这套数据里**没有任何回归量**。行序随机化只保证"在期望上无偏"，不提供任何"这一轮漂没漂"的读数。读者拿不到"第 4 轮是不是整体比第 1 轮慢"这个问题的答案——而 E5-b2 的 13.1 % 轮间离散度说明这个问题是真实存在的。
- 具体不可信：跨轮趋势类说法（"改进后第 3 轮更快"）；以及"控制块真的夹住了产品块"这条在**数据表面**上的可信度——它实际是被 `ControlDrift.Ordering` 用 `startedUtc` 校验的，但 §2 打印的 `order.txt` 给读者的印象（"完整顺序"）与事实不符。
- 错误印象：§2 有一行叫 "run order"，读者以为顺序被"检查过了"；实际是"记录并打印了，然后没有下文"。而设计文档承诺的 "run-order regression check"（`benchmarks/research/2026-10-06-proxifier-comparison-design.md:577`）从未存在。

**归类**：设计缺陷（顺序回归检查完全可以在这两台机器内落地，零额外机器时间：数据已经在每轮的 `order.txt` 与 per-pass 值里，只要在分析器里加一个"per-pass 值对运行位置的秩相关"并把它印在 §2）。

**被推翻的条件**：在分析器里找到任何一处把 `order.txt` 或运行位置当自变量的计算（目前只有两处消费者：打印与夹逼校验）；或者找到一份真实 `order.txt` 里含控制块的产物（那说明 `orchestrator.ps1` 的写入位置与我读到的不一致）。

---

### 10. gate 失败不作废任何数字；唯一会作废单元格的**实测**门是 UDP 恒等式，且只覆盖每臂的第一个前缀

**证据**

- 指标状态**只**由 plan 与承载方式决定：`Metrics/MetricCell.cs` 的 `MetricStatus.Resolve` 全文只读 `RowProfiles.Planned(rowId)`、`campaign.RunsOf(rowId)` 里的臂存在性、`profile.Udp`、`profile.Udp53`，返回 `Ok / not-in-plan / declared-absent / not-carried / dns-carriage`。**没有一种是"gate 失败"**。
- 取值路径里唯一的数据门是 UDP 会计恒等式：`Model/ArmAccess.cs:48-53`
  `var (blocked, detail) = IdentityChecks.BlockedPrefix(run, armName);`
  `if (blocked is not null && path.StartsWith(blocked, StringComparison.Ordinal)) { return new(Value: null, Reason: $"harness error: UDP identity violated ({detail}); excluded rather than averaged over"); }`
- 这个门只覆盖每臂的**第一个**前缀：`Checks/IdentityChecks.cs:36-41` 的 `s_blockedPrefixes` 里 `["MIX"] = ["metrics/classes/udp/", "metrics/udp.sent", "metrics/udp.lossRate", "metrics/clientSendLoss"]`（4 个），而 `:107` `return violated is null ? (null, null) : (prefixes[0], violated.Detail);` **只返回 `prefixes[0]`**。LOSS 与 BASE 的第一个前缀恰好是全覆盖的（`metrics/`、`metrics/loss/`），MIX 不是。
- 于是 MIX 有一条可追的漏出路径：`Metrics/MetricCatalogue.cs` 的 `MixUdpLossRate` 先读 `metrics/classes/udp/lossRate`（被屏蔽 → null），再用 `!string.Equals(reading.Reason, ArmAccess.NullRateReason)` 判断并**回退**到 `metrics/udp.lossRate`——该路径不在 `prefixes[0]` 之下，`StartsWith` 为假，照样返回数字。而 `mix.udp.lossRate` 是 21 个 headline 指标之一。
- 其余全部 gate（`clientSendLoss`、`backlogDrops`、`scheduleTruncated`、`laneShortfall`、`windowOverflow`、`idleLanes`、lane witnesses、`samplerError`、被拒样本、BASE floor、控制块脏）只进 §3.2 与 findings：`Tables/GateValidity.cs` 把它们写成 `GateCheck(Pass/Fail/Warn)`（`:101,119,148,176,235,262`），`Findings/FindingsCollector.Structure.cs` 产出 `Finding`；而 `verdict.json` 的 `metrics` 段由 `MetricComparisons.Render` 独立计算，**不读 findings**。
- 一处尖锐的内部不一致：`windowOverflow`。`Tables/TableLatency.cs:35` 定义 `WindowOverflowCell = "n/a (windowOverflow > 0)"`，`:108` 是 `WindowOverflow.Reached` 在**全分析器里的唯一调用点**；而 `Metrics/MetricCatalogue.cs` 的 `lat.tcp_rtt.p50/p99`、`lat.udp_rtt.p50` 走 `ArmAccess.Latency(...)`，不查 `WindowOverflow`。同一个臂：§5 拒绝打印它的分位数，`verdict.json` 照常用这些分位数做中位数与成对检验。
- 机制本身是活的：冻结树里恒等式屏蔽真正生效了 3 次（`verification/golden/py-verdict.json:4773,5397,6027` 的 `"pass1": "harness error: UDP identity violated (arrived(6001) + late(0) + never(0) + abandonedAtTeardown(0) + corruptDatagrams(0) = 6001 != sent(6000)); excluded rather than averaged over"`，由 `make_tree.py:1101` 有意注入）。所以不是"没实现"，而是"覆盖面比文档小"。

**后果**

- **"gate 全绿"不是数字可用的前提。** 一个 `clientSendLoss > 0`（客户端自己丢了样本）、`scheduleTruncated > 0`（一部分调度从未被提供）、`laneShortfall > 0`（有条车道根本没跑）、有 `samplerError` 的轮次，照样整轮进入该指标的中位数、IQR 与 bootstrap；它只出现在 §3.2 与 `findings` 里，而 `metrics` 段（最像"结论"的那一段）不会提到它。
- 具体不可信：任何"某个指标上的名次/等价"结论，只要至少一方的那一轮有 gate 失败。§3.2 用 `warn` 渲染 `windowOverflow` 意味着"到达上限"是披露而非失败，于是"延迟表里这一行是 n/a、verdict 里它照样参与比较"这种组合会静默发生。
- 错误印象：读者会以为"这一轮没测好"会被自动排除。只有会计恒等式不成立的那几个 UDP 字段会被排除。

**归类**：设计缺陷（作废规则必须写成数据、并与单元格取值同源）；MIX 的 `prefixes[0]` 与 `MixUdpLossRate` 回退是实现级缺陷，但与"预先声明作废规则"直接相关，修它是一行。

**被推翻的条件**：在 `MetricCell.cs`、`MetricCatalogue.cs` 或 `MetricComparisons.cs` 里找到任何一处按 gate 值决定单元格状态的分支。`MetricStatus.Resolve` 全文可读，没有。

---

### 11. 没有"预先声明作废规则"这回事；4 个 WinForward 行既没有 reference row，也没有 best-of-N 防护

**证据**

- 设计文档承诺的作废规则一：`benchmarks/research/2026-10-06-proxifier-comparison-design.md:240` — "Define `proxiedFraction = flowsSeenBySingBox / flowsOfferedByClient` per class, and **treat `proxiedFraction = 1.0` as a precondition for including a product's row in the corresponding metric table.**"；`prd.md:110` 的验收标准同义（"a `proxiedFraction` check that must equal 1 for the measured client"）。
  交付的是：TCP flow gate 用 **≥0.95**（`WinForward.E2E.Analysis/README.md:150-170` 的 `tcpGate = proxy-truth.tcp / tcpAttempts >= 0.95 required`），UDP 侧只是存在性检查（`udpGate = (proxy-truth.udp + proxy-truth.utcp) > 0 required`），而且**没有任何一条规则会因为 gate 不满足而把行从指标表里排除**。`proxifier` 与两个控制块的豁免是写死在 `ROW_PROFILES` 里的**声明**（`Model/RowProfiles.cs` 的 `UdpIncapableRows`、`Profiles`），不是实测结果。
- 设计文档承诺的作废规则二：`prd.md:115` "At least three measured passes **after a discarded warm-up pass**"；`implement.md:86` "Four passes with randomized program order; **pass 1 discarded as warm-up**"。交付的是：`orchestrator.ps1:23` 的 `-Passes` 就是写进 `-OutRoot` 的轮数，没有任何丢弃机制；`publish-campaign.sh` 全量打包 `C:\wfbench\results\*`、解压到 `raw/`、直接 `analyze.sh --raw "$raw"`；仓库脚本里搜不到删除/跳过 `pass1` 的地方（见第 1 条发现的证据）。**"3 个测量轮"这个实验设计参数在实现里不存在**，`--raw` 指向哪个目录就有几轮。（"between-run spread reported"那一半落实了：`README.md:139-141` 的 `median [p25–p75] across passes (n=K)`。）
- 4 个 WinForward 行的地位：全仓 `grep -rniE "reference_row|is_reference|reference row" benchmarks/WinForward.E2E.Analysis` **零命中**。唯一的"参考行"声明在 `orchestrator.ps1:9-11` 的注释里（"wf-aot-opt is the reference, wf-fdd-opt changes only the build, wf-aot-nativeudp changes only the UDP carriage, wf-aot-dnsrelay changes only the DNS path"）——只在注释里，不进数据、不进 `verdict.json`、不进分析器。`MetricComparisons` 的 `Candidates` 对称遍历所有可比行对，9 行一视同仁。
- 设计文档还要求过族的预声明（见第 3、5 条发现）：`benchmarks/research/2026-10-06-proxifier-comparison-design.md:579` "**Pre-declare the primary family**; Holm–Bonferroni within each metric family; publish the full secondary table as exploratory"。

**后果**

- **"这一轮算不算数"在跑之前没有定义、在跑之后也没有规则去执行。** 存在的是三层：一个 plan 级别的"这一行跑不跑这个臂"（设计声明）、一个会计恒等式（实测、但只覆盖 UDP 的几个字段）、一组只进 findings 的检查。读者要自己翻 §3.2 找 FAIL。
- 具体不可信：任何在 gate 有失败的那一轮上得出的结论（第 10 条发现）；以及**整个实验的 N**。按 `implement.md` 的意图手工删掉 `pass1/` 是 N=3，不删（脚本默认）是 N=4；两次"4 passes"的 campaign 可以有不同 N，而产物里读不出来——`environment.json` 只记 `passes = $Passes`（"跑了几轮"），不是"分析了几轮"。
- 4 行 WinForward 的选择效应：判决模型不挑最好的一行，但**没有任何东西阻止报告作者挑**。按指标取 4 行里的最优再与 3 个对手比，等价于一次 best-of-4，而 4 次抽样高度相关（同一台机器、同一个下午、同一份 sing-box）。`verdict.json` 里既没有 `reference_row` 字段，也没有任何"这一行是参考行"的标注，所以读者无法从产物判断一个结论用的是哪一行。同时（第 5 条发现）这 4 行的内部对比还占掉了 `cpu`/`memory` 族 21 对里的 6 对。
- 错误印象有两个方向：把 `wf-aot-opt` 当唯一代表的人会低估自家最好的那一行；把 4 行里的最优当成"WinForward"的人会得到一次未被校正的选择效应，而产物里没有任何东西提示这件事。

**归类**：文档漂移 → 设计缺陷（R6 要求单独成节，这一条是其中最重要的：承诺的两条作废规则落地为零条）。`proxiedFraction` 这条与 harness-audit 第 17、18 条相邻但不同：审计说的是"缺少数据报级的转发证据"，本轴说的是"即使有证据，也没有实现那条'不满足就不纳入'的规则"。

**被推翻的条件**：在 `wf.sh`、`deploy-campaign.sh` 里找到丢弃/重命名 `pass1` 或按 gate 排除行的代码（**未能证实**：这两个我未能逐行读完）；或者找到一份产物里 `verdict.json` 的 `reference_row` 之类的字段。

---

## 与 harness-audit 的关系

harness-audit 的 21 条里与统计轴相邻的有四条，本文只做定位与增量，不重复叙述：

- **第 16 条（BASE 控制组跑两倍时长、只覆盖两个臂、参数不来自 plan、总排第一）**：本文把它变成一个正向资产。`Checks/ControlDrift.cs:268-277` 已经在每个 campaign 里对 5 个指标（`BASE loss lossRate`、`BASE tcp-rtt p50/p99`、`BASE udp-rtt p50`、`BASE udp lossRate`）跑了 pre vs post 的 bootstrap 比较，用的是**同一套阈值**。这是这套仪器里唯一一份随 campaign 一起产出、且是"同一条路径重复测量"的数据。审计说它没被利用是对的，本轴补的是怎么利用：把它的 CI 宽度当作该 campaign 在该指标上的**分辨力上限**，而不是一条 `measurement-caveat`。它是第 4 条发现那个 13.1 % 之外、成本为零的第二个噪声锚点。
- **第 20 条（THRU 被自己的目标速率卡住、四行字节级完全相同、verdict 家族是 `none`）**：与本文第 6 条发现合起来才是完整的一句话——那一列**既没有区分度，也没有判定语义**（`BootstrapPair.cs:217-220` 在 band 逻辑之前返回）。
- **第 10、11 条（结构性恒零的计数器仍然打印统计上界）**：统计轴上的对应物是本文第 4 条发现的一个细节——**零方差会被判成等价**。`Stats/BootstrapPair.cs:108` 的 `estimates[0].Equals(estimates[^1])` 会把"所有 pass 完全相同"标成 `degenerate` 并加一句 note（"degenerate bootstrap CI (zero variance across passes)"），但 `holm_verdict` 仍然是 `same`。也就是说一个方差为零的样本得到的是"等价"而不是"无法估计"。`Stats/DescriptiveStats.cs:76-88` 的 rule of three（`< 3/n`）是唯一一处把"零事件"和"零差异"分开的地方，而它只作用于表格单元格，不作用于 `verdict.json`。
- **第 4、8 条（窗口溢出会停发 / 会删样本）**：那两条说的是"测不到某段区间"，本文第 10 条补的是它在判决层的后果：`windowOverflow > 0` 让 §5 渲染 `n/a`，但 `verdict.json` 里同一个臂的分位数仍然参与成对检验（`Tables/TableLatency.cs:108` 是唯一调用点）。

不与原清单重叠的是第 1、2、3、4、5、6、7、8、9、11 条：它们不是某个计数器算错，而是**重复单元的语义、重复数、阈值来历、效应量分辨力、假设族、判决规则的零假设、随机化层次、顺序分析、作废规则**这九件设计层面的选择。没有一件能靠修 harness 的代码解决，也没有一件需要第三台机器。

本文中**未能证实**的两点，如实列出：

1. `scripts/wf.sh` 与 `scripts/deploy-campaign.sh` 的完整内容（前者是 gitignored 的本地胶水）。我未能逐行确认它们中没有任何"丢弃预热轮"、"轮间复位机器状态"、"按 gate 排除行"的动作。已确认的是 `publish-campaign.sh` 全量打包 `C:\wfbench\results\*` 并直接分析，以及 `orchestrator.ps1` 全文（476 行）里没有这些动作。
2. 真实 campaign 的 `passes` 数组长度与 `environment.json` 的 `passes` 字段。仓库里唯一的多轮树是 `verification/synthetic/make_tree.py` 生成的 3 轮构造数据；`benchmarks/results/2026-10-06-e2e-competitors/` 已随分析器移植被删除。因此本文的定量结论（第 4 条发现）以"仿真 + 父子两方核实的噪声锚点"给出，可以被任何一份真实多轮数据推翻或确认。
