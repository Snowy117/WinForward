# 可复现性与运行层设计审查（design-reproducibility）

本文件是 `10-11-e2e-benchmark-design-review` 的一条轴：**这套基准能不能被第二个人复算、跑一轮要多少机器时间、失败与重跑有没有规则、原始数据还在不在、运行前提是脚本强制还是人记得**。

审查对象是**已经发生过的那几次运行**（2026-10-06 的 validation 轮、2026-10-08 的 E5-b/E5-b2 轮）与当前仓库状态，不是构想。所有数字都在本机可复核：源码、`git` 历史、`/tmp/wf-bench/` 里留存的真实产物、以及 Windows VM 上那一轮 campaign 的 zip。

---

## 结论摘要

1. **「结论可以复算」目前只对同一台机器上的同一个人成立。** 一轮 campaign 的驱动链路是 5 个 gitignored 文件 + 两份 `AGENTS.local.md` + Proxifier 的 `bench.ppx` + VM 上的 `watchdog.ps1`，`git log --all` 对这几个路径的提交数全部是 **0**。README 自己承认了（`benchmarks/WinForward.E2E/README.md:95`），但没有任何一处把它当成要在设计上修的缺陷。

2. **环境记录只覆盖设计要求的约一半，而记录了的那一半没有任何消费者。** `environment.json` 在分析器里被逐字段原样 dump 成一张 info 表（`TableEnvironment.cs:106-111`），没有一条 gate、没有一条 finding 读它；设计点名的 NIC offload、dynamic-memory 状态、产品版本、VM snapshot id 一条都没记（对照 `2026-10-06-proxifier-comparison-design.md:473,518-519`、`design.md:167`）。

3. **「哪套 plan 的数字是发布口径」在仓库里没有定义，而 artifact 里记录的 `planPath` 指向一个内容与同名仓库文件不同的 plan。** E5-b2 轮 `wf-aot-opt` 的 `planPath = C:\wfbench\e2e-win2\full-plan.json`、`planHash = ed0a3477b74dfa97`；这个 hash 等于 `scripts/plans-windows/full-shape-plan.json`，**不等于** `scripts/plans/full-plan.json`（`ae766f4882f0a4ba`）。同一个 pass 里控制块和 `wf-aot-nativeudp` 跑的却是全长 plan——一轮 campaign 内部就混着两种时长口径。

4. **时长成本与「跑几轮才有统计意义」不匹配。** 用实测校准：canonical `plans/full-plan.json` 下一轮 pass ≈ **97 分钟**；AC 要求的「丢弃一轮 warm-up 之后至少三个测量 pass」（`prd.md:115`）≈ **6.5 小时**独占机器时间，设计自己要求的 n=4+1（`2026-10-06-proxifier-comparison-design.md:487`）≈ 8 小时。而实际跑过的 E5-b2 是 3 个 pass、**0 个 warm-up 丢弃**、2 小时 31 分，恰好压在这套分析自己的判定下限 `DefaultMinPasses = 3`（`AnalysisOptions.cs:21`）上。

5. **没有成文的重跑规则，而且失败的行可以静默消失。** `implement.md:90` 只有一行 "a pass can be re-run independently"；编排脚本无重试，失败只进 stdout，stdout 不在产物里。产品起不来时行目录是空的，分析器不把它当一行（`CampaignLoader.cs:251-253`），**一整行竞品可以无声消失**——源码里可 grep 到的 37 个 finding code 没有一条覆盖这种情况。

6. **原始数据不在仓库里，唯一的副本在 `/tmp`。** campaign 产物树从来没进过 git（该路径历史上只装过 `analysis/`，10 个文件），`benchmarks/results/` 里没有任何 e2e campaign 条目；靶机账本也写在 `/tmp/wf-bench/ledger-{main,direct}.jsonl`。再加上 VM 时钟、VM 重置、看门狗、防火墙恢复四件事全靠人记得做——这台仪器今天的可信度依赖操作者的纪律，不依赖脚本。

---

## 发现

### 1. 一轮 campaign 的驱动链路没有版本控制，「可复算」退化为「同一个人在同一台机器上再跑一次」

**证据**

`benchmarks/WinForward.E2E/README.md:84-95` 自己列出了五个 gitignored、fresh checkout 里不存在的本地胶水，并写明：

> A campaign is not reproducible from a fresh checkout: those five files are how it is driven, and the Proxifier row's `bench.ppx` is machine-local too. （`README.md:95-96`）

核实结果：

- `git ls-files benchmarks/WinForward.E2E` 里没有 `scripts/wf.sh`、`scripts/deploy-campaign.sh`、`scripts/start-targets.sh`、`scripts/publish-campaign.sh`、`AGENTS.local.md`；
- `git check-ignore -v` 三个来源：`benchmarks/WinForward.E2E/.gitignore:4-7`（四个脚本）、根 `.gitignore:18`（`AGENTS.local.md`）；
- `git log --all -- <每个路径>` 对这五个路径的提交数**全部为 0**——它们不是「以前跟踪过后来删了」，是**从来没有进过历史**；
- 还有三件不在那五条清单里的：`C:\wfbench\watchdog.ps1`（宿主上只在 `/tmp/wf-bench/deploy/watchdog.ps1` 找到一份副本，仓库、`.gitignore` 清单里都没有它）、`/tmp/wf-bench/bench.ppx`（Proxifier 的 GUI 导出，`README.md:327-328`）、以及宿主侧的两条环境前提：iptables 放行规则与 `net.ipv4.ip_unprivileged_port_start = 53`（只写在 `AGENTS.local.md` §2 里）。

一条 campaign 的步骤与它们的载体：

| 步骤 | 载体 | 进版本控制？ |
|---|---|---|
| 构建 + publish 三份产物 | `scripts/publish.sh` | 是 |
| 在 VM 上执行任意命令（所有远程操作的唯一通道） | `scripts/wf.sh` | **否** |
| 把 client / 配置 / plan / orchestrator 传到 VM | `scripts/deploy-campaign.sh` | **否** |
| 在 Linux 侧起两个靶机（含 `--label target:<port>`） | `scripts/start-targets.sh` | **否** |
| 用 pwsh7 起 orchestrator 的那段 `Start-Process` 配方 | `AGENTS.local.md` §8 | **否** |
| 启用看门狗计划任务 `wfbench-watchdog` | `AGENTS.local.md` §6 | **否** |
| 打包下载结果 + 跑分析 | `scripts/publish-campaign.sh` | **否** |
| 靶机与产品拓扑、端口、凭据、安装状态 | 两份 `AGENTS.local.md` | **否** |

也就是说：**除「构建」和 `orchestrator.ps1` 本身之外，每一步都没有版本控制**。`deploy-campaign.sh` 还独占了一条关键知识：哪个仓库文件被部署到哪个路径（`plans-windows/full-shape-plan.json` → `C:\wfbench\e2e\full-plan.json` 这类改名），`README.md:310-316` 只能事后描述它。

**后果**

- 任何一句「照 `benchmarks/WinForward.E2E/README.md` 做就能复现」都是假的：fresh checkout 里连怎么连上 VM 都不知道（凭据在 `$NEKO_PASS`，由被 gitignore 的 `.envrc` 注入）。
- 结论的可信度边界不是「测量方法是否成立」，而是「那台机器还在不在、那五份文件还在不在」。这五份文件被误删、被改动一次，之前所有结论的**复算路径**就断了，而且断得无声无息——没有任何校验和、没有 deploy manifest。
- 对「WinForward 比 ProxiFyre 快」这类对外结论，第三方能做的只有「相信那份报告」，不能做「自己跑一遍看对不对」。

**被推翻的条件**：如果这五份文件（或等价的一份带参数的 tracked 驱动脚本 + 一份 deploy manifest）存在于别的分支、发布物或归档里，并且 fresh checkout 能据此重建一轮 campaign。

**归类**：**设计缺陷**。修法不需要新机器：把 `wf.sh` / `deploy-campaign.sh` / `start-targets.sh` / `publish-campaign.sh` 改造成「参数化 + 一份 tracked 的 `campaign.env`（地址、端口、路径、凭据**变量名**）」，`AGENTS.local.md` 只保留真正机器专有的值（IP、密码）。工作量约 0.5–1 人日，不需要重跑 campaign（它只影响下一轮的可复算性）。

---

### 2. 部署的是工作区，不是某个 commit——今天照文档重跑，部署出来的配置与任何一轮已记录的运行都不同

**证据**

`deploy-campaign.sh` 的 `stage` 读的是仓库工作区路径（`"$repo/scripts/configs/wf-aot-opt.json"` 等），它不做 `git` 校验、不记录 commit。而当前工作区里四份 WinForward 配置相对 `HEAD` 是**未提交的改动**：

```
$ git diff --stat benchmarks/WinForward.E2E/scripts/configs/
 wf-aot-dnsrelay.json   |  82 ++++++------
 wf-aot-nativeudp.json  | 112 +++++++-------
 wf-aot-opt.json        | 112 +++++++-------
 wf-fdd-opt.json        | 112 +++++++-------
 4 files changed, 221 insertions(+), 197 deletions(-)
```

改动不是格式，是 **schema 换了一代**：`HEAD` 是扁平结构（顶层 `socks5Servers` / `host`），工作区是分区结构（顶层 `Logging` / `WinForward.Socks5Servers`）。我核对了两边的 sha256：

| 文件 | `HEAD`（sha256[:16]） | 工作区（sha256[:16]） | E5-b2 campaign 里实测 |
|---|---|---|---|
| `wf-aot-opt.json` | `15e49373e44d1d82` | `dc9972e4b46a0822` | `15e49373e44d1d82` |
| `wf-fdd-opt.json` | `15e49373e44d1d82` | `dc9972e4b46a0822` | `15e49373e44d1d82` |

（campaign 侧的 hash 是我从 `/tmp/wf-bench/e5b2-campaign/campaign-e5b2.zip` 里 `pass{1,2,3}\wf-aot-opt\config.json` 三个文件算出来的，三份一致。）

`README.md:321-323` 拿 `15e49373…` 这个指纹当作「`wf-fdd-opt` 与 `wf-aot-opt` byte-identical，所以 fdd-vs-aot 的 delta 只归因于 build」这一整条论断的**前提**。今天这个前提在工作区已经不成立：两份仍然互为 byte-identical（`dc9972e4…`），但已经不是任何一轮测过的那一份。

**后果**

- 照文档执行 `git clone` → `deploy-campaign.sh` → 跑一轮，得到的是**另一套产品配置**的数字，而行名、列名、单位、阈值全部一样。读者会以为自己在复核 E5-b2，实际在测另一个配置。
- 那个 schema 变更是否等价于旧配置，**未能证实**——需要产品端确认字段映射；但从基准设计的角度，这不重要：无论等不等价，它都意味着「配置 → 结论」这条链断开了一次。
- 反过来，`README.md:310-316` 那张「repo file → row → deployed path」表标注着 "Measured 2026-10-09"，它对**今天的工作区**是错的。

**被推翻的条件**：如果这四份改动随后被提交、并且 campaign 记录里带上 commit id（或 `deploy-campaign.sh` 拒绝在 dirty tree 上部署）。

**归类**：**设计缺陷**。修法：`deploy-campaign.sh` 在开始时记录 `git rev-parse HEAD` + `git status --porcelain` 的摘要，写进 `environment.json` 旁边的一个 `deploy.json`，并在 dirty 时要求显式 `--allow-dirty`。约 0.2 人日，不需要重跑 campaign。

---

### 3. 环境记录不足以判定「两次 campaign 是否可比」，设计点名要求的量有一半没测

**证据**

`orchestrator.ps1:419-437` 是环境块的全部内容：

```powershell
$os = Get-CimInstance Win32_OperatingSystem
$environment = [ordered]@{
    started = ...; os = $os.Caption; build = $os.BuildNumber
    logicalCpus = [int]$env:NUMBER_OF_PROCESSORS
    totalRamMb = ...; freeRamMb = ...
    passes = $Passes; seed = $Seed
    firewallProfiles = $firewall
    singBox = (& $SingBoxExe version 2>&1 | Select-Object -First 1)
    target = [ordered]@{ host; tcpPort; udpPort; dnsPort; dnsPortAlt; tcpPortDirect; dnsPortDirect }
}
```

真实产物（E5-b2，`environment.json`，703 字节）就是这个字段集合，没有别的键。

对照设计承诺：

| 设计要求的量 | 出处 | 现状 |
|---|---|---|
| NIC offload 状态（RSC / LSO / checksum / interrupt moderation） | `design.md:146`（"checksum/RSC/LSO state recorded"）、`design.md:167`、`2026-10-06-proxifier-comparison-design.md:473,519`、"Freeze and record all advanced properties; re-verify before each pass"（`:573`） | **未测**。orchestrator 全文没有 `Get-NetAdapter*`（grep 结果为 0） |
| dynamic-memory 状态 / guest-visible RAM 序列 | `design.md:225`、`comparison-design.md:569`（"log guest-visible RAM per sample; reject windows where it changed"） | **只有 preflight 一个标量**。E5-b2 记 `totalRamMb=2358 / freeRamMb=628`；2026-10-06 的 validation 轮记 `totalRamMb=3820`。同一台 VM 两次 campaign 可见 RAM 差 **1.46 GB**，而样本里没有任何机器级内存序列，"窗口内可见内存移动就作废"这条缓解措施无从执行 |
| 产品版本 | `design.md:167`、`comparison-design.md:473`、prd 要求「被测二进制是 `a247658` 的 CI 产物，不是本地构建」（`prd.md:89`） | **未测**。环境块只有 sing-box 的版本；`C:\wfbench\wf-aot\WinForward.exe`、ProxiFyre / Proxifier / ProxyBridge 的二进制版本与哈希一个都没记 |
| VM snapshot id | `comparison-design.md:473,518`（provenance block） | **未测**。只有 `os` + `build` |
| 电源计划 / turbo 状态 | `comparison-design.md:577`（risk 13，thermal/boost drift） | **未测、未设**。grep `powercfg` = 0 |
| Windows Update / Defender / 搜索索引状态 | `comparison-design.md:496,574`（"disable for the run window, document it"） | **未测、未关**。grep `Set-MpPreference` / `wuauserv` / Defender = 0 |
| 驱动版本（`ndisrd` / `ProxifierDrv` / `WinDivert` / `ndisapi.dll`） | `design.md:192`（"each one is asserted by the orchestrator rather than assumed"）、`comparison-design.md:565`（`driverquery` / `fltmc` / service list） | **未测**。grep `driverquery` / `fltmc` / `Get-Service` = 0 |
| CPU 频率 / 机器级进程数 | — | **未测** |
| 配置哈希 | `design.md:167` | **已记录**，且是这条清单里唯一达标的：分析器打了每行 config 的 `sha256[:12]`（`TableEnvironment.cs` "Per-row effective configuration hashes"） |

**后果**

- 报告无法回答「两次 campaign 之间到底变了什么」。VM 从 3820 MB 可见内存变成 2358 MB 这种量级的变化，在产物里只体现为两个孤立的标量，没有任何一处把它和内存指标联系起来。
- 「CPU 指标可比」这条尤其脆：`comparison-design.md:496` 要求的电源计划固定、Windows 后台活动关闭一条都没做，而这些正是 `cpu.proxy.v1` 这类小数字（单 vCPU 的百分比，量级 5–25 %）最容易被外部负载带偏的地方。
- 对外结论「WinForward 的 CPU 开销是 Proxifier 的 N 倍」在生产机器上会被复现失败，而报告里没有任何字段能解释为什么。

**被推翻的条件**：如果这些量其实记录在别处（例如 `orch.log` 里、或 VM 上某份随结果归档的 `driverquery` 输出）——我在 E5-b2 的 campaign zip 里没有找到，且 `publish-campaign.sh` 只打包 `C:\wfbench\results\*`。

**归类**：**设计缺陷**（可在这两台机器内补齐，见第 10 条的整改）。**部分与 harness-audit 第 21 条重叠**——那条讲防火墙没有记录，这条讲其余环境量没有记录、以及记录了的部分覆盖不全。区分见「与 harness-audit 的关系」。

---

### 4. `environment.json` 在分析里只是一段原样 dump，没有任何 gate/finding 读它；而`firewallProfiles` 恒为 `false`

**证据**

分析器对环境的全部处理是 `TableEnvironment.cs:106-111`：

```csharp
if (campaign.Environment is { ValueKind: JsonValueKind.Object } environment)
{
    foreach (var member in environment.EnumerateObject().OrderBy(m => m.Name, StringComparer.Ordinal))
        AddRow(info, $"environment.json: {member.Name}", JsonText.Of(member.Value));
}
else
    AddRow(info, "environment.json", "n/a (not found beside the pass directories)");
```

逐字段 dump，不解释、不比较、不判定。全项目没有任何 gate 读 `environment.json` 的任何键（`tables.md` §3.1/§3.2 的 gate 表、`verdict.json` 的 findings 都不含环境量）。

更要紧的是 `firewallProfiles` 这个字段本身：

```powershell
Get-NetFirewallProfile | ForEach-Object { Set-NetFirewallProfile -Name $_.Name -Enabled False }   # orchestrator.ps1:399
$firewall = @(Get-NetFirewallProfile | ForEach-Object { @{ name = $_.Name; enabled = [bool]$_.Enabled } })  # :402
```

**先强制关闭、再读**。因此这个字段在任何一轮 campaign 里都恒为 `Domain=False Private=False Public=False`，它记录的是脚本刚刚制造的状态，不是环境本来的状态。一个读者看到三行 `false`，无法区分「本来就关着」和「编排脚本关的」。

**后果**

- 「报告第一节会披露防火墙状态」这句是对的，但披露的内容是**恒真的常量**。它给读者的印象是「我们检查过环境」，实际提供的信息量是 0。这和 harness-audit 第 10–12 条那一类「结构性恒零的计数器」是同一种病，只是发生在环境块而不是指标上。
- 更广地说：**记录了但没有消费者的字段，等于没记**。`freeRamMb=628`、`totalRamMb=2358` 这些数字进了表，但没有任何阈值、没有任何 finding 会因为它们可疑而亮灯。

**被推翻的条件**：如果分析器对 `firewallProfiles` 里 `enabled != false` 的档位会报 finding，或者该字段是**在 `Set-NetFirewallProfile` 之前**读的。（两者都不是。）

**归类**：**设计缺陷**（把「环境」从 dump 提升为「有判定规则的输入」：先读后改、把改动本身记成一条 `environmentChange` 记录，并对关键量定义阈值/发现），但**修完之后只能如实披露，不能变成 gate**——因为这两台机器上你没法保证 Windows Update 一定关着，只能记录它当时的状态。

**与 harness-audit 的关系**：harness-audit 第 21 条说「`environment.json` 里没有该字段，所以 `tables.md` 第一节永远不会说这件事」。那条对**当时的数据**成立（`/tmp/wf-bench/val/environment.json` 确实只有 9 个键，没有 `firewallProfiles`），对**当前代码**不成立（`orchestrator.ps1:399-402` 已经写了）。这条发现是它的后半段：字段加上了，但没有消费者，而且读的时机让它退化成一个常量。

---

### 5. 「哪套 plan 是发布口径」没有定义；artifact 里记录的 `planPath` 指向一个内容与同名仓库文件不同的 plan

**证据**

`README.md:337-339` 把三套 plan 的关系写得很好（`plans/` 是 canonical，`plans-short/` 是「短时长**且更低速率**」，`plans-windows/full-shape-plan.json` 是「只有九个 `seconds` 不同」）。我逐 leaf 复核了这两个论断，都对：

- `plans-short/full-plan.json` vs `plans/full-plan.json`：56 个 leaf 差 17 个，其中 10 个 `seconds`，7 个是**速率**（`LATLOAD.ratePerSecond` 500→250、`DNS/DNSALT.ratePerSecond` 200→100、`LOSS.ratePerSecond` 500→250、`REL.connectionsPerSecond` 20→10、`THRU.targetBytesPerSecond` 60 000 000→15 000 000、`PERSIST.idleSeconds` 25→6）。
- `plans-windows/full-shape-plan.json` vs `plans/full-plan.json`：只有 9 个 `seconds` 不同（`LOSS` 保持 120 s），其余 47 个 leaf 全同。

但是**没有任何一处说明「发布口径」用哪一套**——README 只说 `plans/` 是「the canonical set」，没说报告的数字来自它；`plans-short/` 与 `plans-windows/` 被描述为「validating a change」，但没人说这两套的数字不能与 canonical 的数字并排。

真正的问题出在 artifact 的 provenance 上。E5-b2 轮 `pass1/wf-aot-opt/run.json`：

```json
"planHash": "ed0a3477b74dfa97",
"planPath": "C:\\wfbench\\e2e-win2\\full-plan.json",
"planSource": "file"
```

我把这个 hash 和三个仓库文件对了一遍：

| 仓库文件 | sha256[:16] | 与记录相符？ |
|---|---|---|
| `scripts/plans/full-plan.json` | `ae766f4882f0a4ba` | ✗ |
| `scripts/plans-short/full-plan.json` | `2679f1cb718844ec` | ✗ |
| `scripts/plans-windows/full-shape-plan.json` | `ed0a3477b74dfa97` | **✓** |

也就是：`planPath` 的 basename 说的是 `full-plan.json`，内容却是 `full-shape-plan.json`。而同一个 pass 里：

| pass1 的行 | `planHash` | 对应文件 | 臂时长合计 |
|---|---|---|---|
| `wf-aot-opt` | `ed0a3477b74dfa97` | `plans-windows/full-shape-plan.json` | 321 s |
| `wf-aot-nativeudp` | `b0292c1887c2f467` | `plans/udp-plan.json`（全长） | 240 s |
| `control-pre` | `6c28a3d0ce91a24c` | `plans/base-plan.json`（全长） | 120 s（BASE 两个相位） |

**一轮 campaign 内部就混着两种时长口径**：5 个 full 行跑压缩形状，1 个 udp 行 + 1 个 dns 行 + 2 个控制块跑全长。这不是错误（deploy 只把 `full-plan.json` 换成压缩形状），但它意味着：表里 `LAT` 的一格，`wf-aot-opt` 是 12 s 的 240 个样本，`wf-aot-nativeudp` 是 60 s 的 1200 个样本，而两个格子在同一种格式、同一个阈值下渲染。

分析器这一侧完全看不到这层区别：`RowProfiles.PlanArms["full"]`（`RowProfiles.cs:55-58`）是一张**臂名**表，`TableRowProfiles` 的 `plan` 列（`:26,118`）打印的是 `"full"` 这个**名字**，`FindingsCollector.Structure.cs:22-36` 只检查「声明的臂在不在」，从不检查时长或速率。

**后果**

- 把一次压缩口径 campaign 的数字与一次 canonical campaign 的数字放进同一张对照表，**行名、臂名、单位、阈值全一样**，唯一的区分线索是 §1 的 "Arm durations (s, from each run's own tick deltas)" 表和 `planHash` 那一行——而后者需要读者自己去 hash 三个仓库文件才能解码。
- `plans-short/` 的速率差让问题更硬：它的 `LATLOAD` 是 250 rps 而不是 500 rps，`LOSS` 是 250 pkt/s 而不是 500 pkt/s。**负载水平是处理变量**，不是可以随时长一起缩放的装饰：队列行为、在途窗口占用、每包 CPU 摊销都跟着变。把 `plans-short` 的 p99 与 `plans/` 的 p99 并列，等于在比较两个不同实验。
- 对延迟/丢包这类**率**指标，`plans-windows` 与 `plans/` 基本可比（速率相同，只是样本数少 5–10 倍）；对 `< 3/n` 这类**零事件上界**和一切计数型指标，两者差 5–10 倍且没有提示。报告会把两种不同分辨率的格子并排印出来。

**被推翻的条件**：如果 `tables.md`/`verdict.json` 里有一条显式声明「本报告使用 X 这套 plan、各臂声明时长如下、与 `run.json` 的 `planHash` 对账一致」，并且跨 plan 的行会被标成不可比。（现在只有 hash 一行 + 时长表，没有任何对账逻辑。）

**归类**：**设计缺陷**。最小修法（不需要重跑 campaign）：在 `deploy-campaign.sh` 部署时把**实际部署的 plan 文件连同原名**一起写进 `environment.json` 旁边（或直接拷贝进 `environment.json` 的一个 `plans: {rowId: {deployedAs, sha256, sourceRepoPath}}` 块），让 `planHash` 从「一个孤立的 16 位十六进制」变成「可解码的映射」。同时把 canonical / 压缩口径在报告标题里显式分开。约 0.3 人日。

---

### 6. 时长成本与「跑几轮才有统计意义」不匹配，而最便宜的可判定轮数恰好等于系统的下限

**证据**

先给实测，再给校准过的模型。

实测：E5-b2 轮（2026-10-08，压缩口径，3 个 pass，7 行 + 2 控制块）从 orchestrator 日志 `=== preflight ===` 08:19:27 到 `=== orchestrator complete ===` 10:50:12 = **2 小时 30 分 45 秒**；单 pass 49 分 26 秒 / 51 分 25 秒 / 49 分 24 秒。日志里逐行可读出：`control-pre` 121 s、`wf-aot-dnsrelay` 190 s、`wf-aot-opt` 461 s（其中 client 331.6 s + dual ~121 s）、`wf-aot-fdd` 462 s、`proxifier` 451 s、`proxybridge` 459 s、`proxifyre` 452 s、`wf-aot-nativeudp` 250 s、`control-post` 120 s。

模型校准：把「臂时长之和 × 1.033（实测的 client 开销）+ dual 相位 + 8 s 生命周期」套上去，压缩口径的一轮 = 2 988 s（49.8 min），与实测 2 966 s（49.4 min）**差 0.8 %**。模型可信。

换成 canonical `plans/full-plan.json`（十个臂合计 **870 s**，压缩形状是 321 s）：每个 full 行 +549 s，5 个 full 行 = +45.75 min：

| 口径 | 单 pass | 3 passes | 4 passes | 5 passes（设计建议的 4+1） |
|---|---|---|---|---|
| `plans-windows/full-shape`（实测轮） | ~49 min | 2 h 28 min | 3 h 18 min | 4 h 07 min |
| `plans/`（canonical） | **~97 min** | **4 h 51 min** | **6 h 28 min** | **8 h 05 min** |

（不含 `publish.sh` 的构建 + 三次 publish、`deploy-campaign.sh` 的约三十次 WinRM 上传、靶机拉起、结果打包下载、分析器构建。按 `AGENTS.local.md` 自己记的踩坑历史，这几项加起来是十分钟到半小时量级。）

而统计口径这一侧：

- 设计自己写的是 **n = 4 passes minimum, n = 5 preferred, plus 1 discarded warmup pass**（`2026-10-06-proxifier-comparison-design.md:487-488`），并明确要求「document the deviation and the resulting CI width」（RFC 8219 要 ≥20 次重复，它够不着）。
- 任务 AC 写的是「**At least three measured passes after a discarded warm-up pass**」（`prd.md:115`），`implement.md:86` 复述为 "Four passes with randomized program order; pass 1 discarded as warm-up"。
- 实际实现里**没有 warm-up 丢弃**：`orchestrator.ps1` 只有 `-Passes`（默认 4，`:23`）与 `-Seed`，没有任何「第一轮丢掉」的逻辑；E5-b2 用 `-Passes 3`，三个 pass 全部进分析。
- 分析的判定下限是 `DefaultMinPasses = 3`（`AnalysisOptions.cs:21`），低于它一律 `inconclusive`（`README.md` 分析器侧 "Fewer than 3 passes is never enough to decide a pair"）。bootstrap 重采样的是 **pass**（`Analysis README:48`：`--resamples 10000`，"Bootstrap resamples passes (never samples)"）。

于是：**最便宜的可判定实验 = 3 个 pass ≈ 4 h 51 min 的独占机器时间；设计想要的 = 5 个 pass ≈ 8 h。而实际用过的是 3 个 pass（连 warm-up 都没有），正压在判定下限上。** 丢掉任意一个 pass（一次钟差、一次产品起不来、一次 VM 重启）就掉到 `inconclusive`，整轮报废——E5-b2 本身就是为了一次钟差问题而重跑的（上一轮 E5-b 因 VM 快约 3 s 判废）。

**后果**

- 「跑了三轮才得出结论」这件事本身就是结论强度的一部分，而报告里只有 `(n=3)` 这种小字。`n=3` 的 95 % bootstrap CI 是从三个点里重采样出来的，宽度主要由这三个点的散布决定；`different` / `same` 这种二元判定建立在这三个点上。
- 成本决定了**没人会为了验证一条结论而重跑**。这直接导致第 7 条（重跑规则缺失）没有实际约束力：规则写不写都一样，因为重跑一轮的代价是 5–8 小时。
- 设计文档承诺的「document the deviation and the resulting CI width」在仓库里没有落地物：没有 CI 宽度的汇总表，也没有一处写「本报告用了 n=3」。

**被推翻的条件**：如果 report 里每个 headline cell 都带 bootstrap CI 宽度、并且结论都写成区间/等价性形式而不是二元断言，或者机器时间成本被大幅压缩（例如 canonical plan 缩到 30 min/pass）。（表格确实印 `n=K` 与 `[p25–p75]`；但 verdict 的 `different`/`same` 是二元断言。）

**归类**：**设计缺陷**（这是设计层的问题：把 experimental design 定在 4–5 轮，又把每轮的单价定在 1.5 小时，两者相乘超出了「能重复」的范围），**整改只能如实披露 + 降单价**：在这两台机器内可做的是把 canonical plan 的**时长**压到可重复的量级、同时**不动速率**（即把 `plans-windows` 从「验证用」提升为正式口径，并明确 `LOSS` 为什么必须保持 120 s——因为 2¹⁸−1 的序列空间约束和 `< 3/n` 的分辨率），然后在报告里同时声明「这是压缩时长口径」和由此得到的零事件上界。这需要重跑 campaign 才有意义。

---

### 7. 失败与重跑没有成文规则；一次「起不来」能让一整行竞品静默消失

**证据**

成文规则的全部内容是 `implement.md:90` 的一行：

> **Rollback:** a pass can be re-run independently; the VM can be reset between programs.

没有说：什么条件下**必须**重跑；重跑的单位是 pass 还是整轮 campaign；重跑后旧的 pass 是保留、替换还是两者都留；重跑要不要披露；「这一轮看起来不对」算不算重跑理由。

实现这一侧：

- **无重试**。`Invoke-Row` 在 `Start-Product` 失败时打一行日志、往 `$script:Failures` 里加一条、然后 `return`（`orchestrator.ps1:353-356`）。没有任何 retry，也不会中止 campaign。
- **失败清单不在产物里**。`$script:Failures` 只在最后 `Write-Log` 到 stdout（`orchestrator.ps1:468-474`）。而 stdout 被操作者重定向到 `C:\wfbench\orch.log`（`AGENTS.local.md` §8），`publish-campaign.sh` 打包的是 `C:\wfbench\results\*`——我在 E5-b2 的 campaign zip 的 519 个条目里确认**没有 orchestrator 日志**。也就是说，campaign 自己那份「哪些行失败过、哪些行没起来」的判定，**不会到达分析器**。
- **行可以静默消失**。产品起不来时 `Invoke-Row` 已经建好了行目录（`:348`），但里面什么都没有。分析器的 `LooksLikeRow` 要求目录里有 `run.json` **或至少一个臂文件**（`CampaignLoader.cs:251-253`），空目录不算一行，直接跳过。我自己数了一遍源码里可 grep 到的 finding code，共 **37 个**，其中与「缺失」有关的只有 `declared-arm-missing`（行存在但少了一个臂）、`control-block-missing`（控制块缺失）和 `no-target-ledger`，**没有一个是「某个声明的行整个不见了」**。

**后果**

- 一次 `Start-Product` 超时（40 次 × 500 ms 的轮询，`orchestrator.ps1:124-128`）会让那一行在这一轮里消失。如果它发生在 3 个 pass 中的 1 个，报告会把剩下 2 个 pass 的中位数印出来，`(n=2 of 3; 1 unavailable)`——看上去像一次「数据不全的诚实披露」，实际上少的那一行可能正是结论要对比的竞品。**而且是哪一行消失，取决于机器当时的运气。**
- 重跑的**选择偏差**当前没有任何防护。既然唯一的成文规则说「一个 pass 可以独立重跑」，而报告把 pass 当等权样本取中位数，那么「跑坏了就重跑那个 pass」在流程上完全合法。没有任何一处要求把被丢弃的那一轮也归档，因此**外部读者无法区分「3 个 pass 是连续跑的」和「跑了 5 次留了 3 次」**。这在延迟类指标上尤其危险：延迟对机器状态（热、后台任务、内存压力）高度敏感，而重跑决策通常正是在「这一轮看起来很慢」时做出的。
- 一旦被人发现「某行的数字看起来不对 → 重跑 → 变好了」，这套基准的**全部对外数字**都会被同一个怀疑笼罩，因为没有任何机制能自证清白。

**被推翻的条件**：如果存在一份成文规则规定重跑单位 = 整轮 campaign、每一轮（含被丢弃的）都归档、并且报告必须披露「本轮 campaign 是第几次尝试」；或者分析器对「声明的行在所有 pass 里都缺席」报 finding。

**归类**：**设计缺陷**。最小可落地的修法（这两台机器内、不需要新工具）：在 `orchestrator.ps1` 里把 `$script:Failures` 写成 `results/failures.json`（进产物树，分析器可见）；把「起不来」的行目录里写一个 `run.json` 骨架（`failed: true` + 原因），让 `LooksLikeRow` 认它，从而触发 `declared-arm-missing`；在 `environment.json` 里加一个 `attempt`（本轮 campaign 是这台机器上第几次跑、前几次为什么作废）字段。约 0.3 人日，不需要重跑 campaign。

---

### 8. 原始数据不在仓库里，唯一副本在 `/tmp`——「结论 → 原始数据」这条链在最关键的一环断掉

**证据**

`design.md:235` 与任务 AC（`prd.md:119`）都写着 campaign 产物树 `benchmarks/results/2026-10-06-e2e-competitors/` 已被删除。我核了一遍 git 历史：**那个路径下出现过的文件一共 10 个，全部在 `analysis/` 之下**（`analyze.py`、`synthetic/make_tree.py`、`verification/*-tables.md`、`*-verdict.json`、一个 `.pyc`）。没有任何 `pass*/`、`run.json`、`*.jsonl`、`ledger*`。也就是说：

- 「删掉的」是**分析器程序**（它当时住在那次 campaign 的结果目录里）；
- **campaign 自己的原始记录从来没有进过仓库**——不是被删了，是从来没有。

现在 `benchmarks/results/` 有 21 个目录，最近的两个是 `2026-10-06-aot-instruction-set` 和 `2026-10-06-uot-per-flow`，与 e2e campaign 无关。全仓库搜索 `tables.md` / `verdict.json` 的结果为空——**仓库里没有任何一份 campaign 报告**。分析器 README 是**工具的契约**，不是报告（我逐行看过，里面没有本次 campaign 的任何测量值）。

仓库里唯一带 e2e 数字的东西是归档任务笔记（`.trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/research/baseline/E5b2-windows-rerun.md` 等）与 `harness-audit.md`；它们是过程记录，不是数据。

而真实数据现在在这里：

```
/tmp/wf-bench/val/pass1/…                     # 2026-10-06 的 validation 轮（8 行 × 8 臂，harness-audit 用的那份）
/tmp/wf-bench/e5b2-campaign/campaign-e5b2.zip # 2026-10-08 E5-b2 轮，519 个条目
/tmp/wf-bench/e5b2-campaign/ledger-main.jsonl # 5 399 852 字节
/tmp/wf-bench/e5b2-campaign/ledger-direct.jsonl # 2 409 409 字节
```

靶机账本同样写在 `/tmp`：`start-targets.sh` 把两个实例的 ledger 写进 `/tmp/wf-bench/ledger-{main,direct}.jsonl`，`publish-campaign.sh` 再从那里拷进 `<results>/raw/`。本机 `/tmp` 在 ext4 根分区上（`df -T /tmp` → `/dev/sda2`），所以重启不会清空；但它不在任何版本控制里、不在任何备份里，清理策略由发行版决定（**未能证实**本机有清理任务，但没有理由把它当归档位置）。

**后果**

- `harness-audit.md` 的 21 条结论，第三方能做到的最好程度是「读懂推理、相信数字」。凡是「本次验证数据未触发」的条目（第 4、5、8 条），**没人能再去数据里确认**。
- 2026-10-07 把分析器从 Python 迁到 C# 时，用了「与冻结的 Python 参考逐字对比」来证明行为不变；那个 Python 参考在 git 里（`git show 688d3b4:…/analyze.py` 可取），但**它当时读的那棵输入树不在**。于是「重构没改数字」这句话可以被验证到「对合成树一致」，不能被验证到「对真实 campaign 树一致」。
- 一次 `/tmp` 事故（磁盘满、误删、换机）会让这套基准**从「有历史数据的仪器」变成「只能向前测的仪器」**。而它恰恰是一个用来做「改进有没有效果」的仪器——没有历史数据，就没有 before/after。

**被推翻的条件**：如果 campaign 树在别处（备份盘、外部存储、CI artifact）留有副本并能给出校验和；或者 git 历史里其实存在过（我查的结果是不存在）。

**归类**：**设计缺陷**。可落地的整改（不写进仓库、也不需要新机器）：在宿主上固定一个归档根（例如 `~/bench-archive/<date>-<campaign>/`），让 `publish-campaign.sh` 把 `raw/`、两份 ledger、`orch.log`、`environment.json` 与一份 `SHA256SUMS` 一起写进去；这棵归档不提交 git，但**必须有校验和与固定位置**。约 0.2 人日，不需要重跑 campaign（对已经跑过的两轮，现在就可以手工归档一次）。

---

### 9. 两台机器的时钟一致性是人工前置，而它出错时不会让任何性能数字看起来异常

**证据**

`AGENTS.local.md` §10 把「两台机器的 UTC 时钟一致」列在「结果可信的前提」里，并明说：

> **两台机器的 UTC 时钟一致**（这条脚本查不了）：账本记录的是靶机（Linux 主机）的 `utc`，臂窗口是用客户端（VM）的 `startedUtc` + tick 偏移算的，两者直接比较。VM 的时钟快几秒，就会让每个臂的窗口整体错位同样的秒数……2026-10-08 的 E5-b 轮次就是这样：VM 快约 3 s……跑之前先让 VM 对时，跑完把 VM 与本机的 `Get-Date`/`date -u` 一起记进证据。

`orchestrator.ps1` 里没有任何时间校验（grep：无 `w32tm`、无与宿主的时间对比、无 NTP 状态），`environment.json` 里也没有任何时钟字段。

而 E5-b2 的归档记录（`.trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/research/baseline/E5b2-windows-rerun.md` §1.2）给出了完整的实测链条：

- 上一轮 VM 快 2.5–3.4 s；
- `w32tm /resync /force` **报成功但不搬时钟**——VM 的时间源是 `VM IC Time Synchronization Provider`（Hyper-V 集成组件，stratum 1 = 宿主），resync 只是把时钟同步到那个偏移上；
- 改走 `Set-Date`，并且踩了时区坑（`Set-Date -Date <Kind=Utc>` 被当本地时间用，一次 −3.01 s 的修正变成 +7 h 平移）；
- 本轮跑前校到 −0.01 s，跑完 +0.14 s（漂移 0.054 s/h）。

我在这台机器上找到的操作痕迹是 `/tmp/wf-bench/clockcheck.sh` + `clock-probe.py` + `clock-probe.ps1` + `clock-offset.py`——**全部在 `/tmp`，全部未跟踪**。最后一次探针结果（`/tmp/wf-bench/clock-probe.out`）读 `offset(vm-host) = +0.139…+0.143 s`。所以钟差在两次观测之间跨了 **20 倍**。

**后果**

- 这不是「数字偏一点」。分析器的账本归属规则是：ledger 记录按 `label` 选靶机实例，再按「客户端 `startedUtc` + 臂 tick 偏移」算出的 UTC 窗口定界（`Analysis README` §14.1）。两端时钟差几秒 → 每个臂的窗口整体平移 → **边界上的记录被记到相邻臂**。症状是 `ledger-connection-mismatch` / `ledger-datagram-mismatch` 这类 `measurement-caveat`。
- 关键在下一环：**这些 caveat 不进入任何延迟/丢包/CPU 数字**。§14 是「独立第二意见」，与 headline 表并列但不参与它的计算。所以一次 3 s 钟差的 campaign，延迟表、UDP 表、CPU 表全都照常渲染，只有第 14 节多几十条 caveat。E5-b2 报告的 42 条白名单外 finding 里，20 条归因于残余钟差——读者完全可能把那一节当成脚注跳过。
- 而由于第 6 条（一轮 5–8 小时），钟差是在**跑完之后**才发现的，代价是一整轮。E5-b2 就是这么来的。

**被推翻的条件**：如果 orchestrator 在 preflight 里做一次主机–VM 时间对比并把结果写进 `environment.json`，或者分析器把「钟差 > 某阈值」升级成会污染 headline 的 finding。

**归类**：**设计缺陷**（可脚本化：orchestrator 在 preflight 里发一个 TCP 请求到靶机上一个回声端口做三段式对时，把偏移写进 `environment.json`，超过 0.2 s 直接 `exit 1`），**其余部分只能如实披露**（Hyper-V IC 这个时间源本身就是那个偏移，脚本改不了它，只能每次校）。**这条与 harness-audit 无重叠**——审计没有谈时钟。

---

### 10. VM 的重置、看门狗、防火墙恢复：全是「文档要求人记得做」，脚本不强制；而看门狗与 orchestrator 的交互有一个未证实的坑

**证据**

**重置**：`AGENTS.local.md` §5 只有一句「如果 VM 被重置了，请自行安装相关需要的软件。特别地，Proxifier 需要让用户在 RDP 中手动安装。」没有快照 id、没有版本清单、没有安装校验。`orchestrator.ps1` 不检查任何产品版本（第 3 条已证）。而 `2026-10-06-proxifier-comparison-design.md:580`（risk 16）要求的是「snapshot after configuration; **assert version hash before each pass**; document any GUI-only setup step as a deviation」——「Proxifier 需要 GUI 安装」这个 deviation 确实被记录了，但「每次跑前核对版本哈希」没有。

**看门狗**：`C:\wfbench\watchdog.ps1` 不在仓库里（`git ls-files` 没有、`.gitignore` 清单里也没有它）。我在宿主上只在 `/tmp/wf-bench/deploy/watchdog.ps1` 找到一份副本。它的内容是：

- 心跳超过 `$StaleSeconds = 1500`（25 min）就紧急拆机：停 `ProxiFyreService` / `ProxifierService`，杀五个进程，把被关掉的防火墙档位恢复为开启；
- 注释写着：「The orchestrator touches the heartbeat between products, so the threshold has to exceed one product block (the longest arm plan is about 12 minutes).」

三处与现状不符：

1. 「the longest arm plan is about 12 minutes」——canonical `plans/full-plan.json` 是 **870 s = 14.5 min**，再加 dual 相位的 120 s 与生命周期开销 ≈ **17 min**。1500 s 的门限只剩约 8 分钟余量。（压缩口径下是 ~7.5 min，所以这条注释是按压缩口径写的。）
2. 心跳只在**产品块之间**touch（`orchestrator.ps1:361` 在 client 之前、`:376/:389` 在 dual 与 stop 之后），**一个 17 分钟的产品块内部没有任何心跳**。也就是说，在 canonical plan 下一个正常的产品块已经用掉了 68 % 的门限。
3. `AGENTS.local.md` §6 明说计划任务 `wfbench-watchdog` **默认禁用**，「跑长 campaign 时手动启用」。默认路径下这个死人开关根本不在。

**防火墙恢复**：`design.md:156` 承诺的 teardown 是「download JSONL, **restore firewall and offload state**」。实现的 teardown（`orchestrator.ps1:464-466`）是：

```powershell
Write-Log '=== teardown ==='
foreach ($product in $products) { Stop-Product -Product $product }
Get-Process -Name 'sing-box' ... | Stop-Process -Force ...
# 然后就是打印 $script:Failures
```

**没有恢复防火墙**。默认（计划任务禁用）的结果是：**每跑完一轮 campaign，VM 三个防火墙档位一直保持关闭状态**，直到有人手动打开，或者有人记得启用看门狗并且让它超时触发一次。

**一个未证实的交互**：看门狗杀的是产品进程，**不杀 orchestrator**（它的进程名单是 `WinForward / ProxiFyre / Proxifier / ProxyBridge_CLI / sing-box`）。所以一次误触发之后，orchestrator 会继续下一行、重新 `Start-Product`，但**防火墙已经回到开启状态**——而 `prd.md:95` 与 `AGENTS.local.md` §5 都写明「TCP 重定向的入站连接会被防火墙静默丢弃，表现是 SYN 注入了但监听端永远不 accept」。于是后半程会在一台「产品看起来在跑、实际什么都连不上」的机器上继续测，产物里没有任何标记。**我没有在真实运行里观察到这件事**（E5-b2 的日志是 `=== no failures ===`），这是我从两份脚本的文本推出的，属于「设计上存在、未被证实」的风险。

**后果**

- 这套基准的**运行前提（时钟、VM 状态、看门狗、防火墙）没有一条是脚本强制的**。它们全都写在被 gitignore 的 `AGENTS.local.md` 里，靠操作者每次记得。
- 「VM 是干净的吗」这个问题在任何一份产物里都回答不了。两次 campaign 之间 VM 可能被重置过、产品可能被升级过、驱动可能被换过，而 `environment.json` 只会说 `os = Microsoft Windows 11 IoT Enterprise LTSC build 26100`。
- 防火墙不复原这件事的后果不止是本机不干净：下一轮 campaign 的 `firewallProfiles` 仍然读 `false`（因为它先关再读），所以**上一轮留下的状态和这一轮制造的状态在产物里长得一模一样**，无法区分。

**被推翻的条件**：如果 VM 上有一个脚本化的、带版本校验的重置流程（例如一份 tracked 的 `provision.ps1` + 一份 `installed.json`），或者 orchestrator 的 teardown 确实恢复了它改动的每一项。

**归类**：**设计缺陷**（重置/防火墙在 orchestrator 里各是十几行代码就能强制的），**看门狗那一部分属于「只能如实披露」直到被证实**（要看门狗真的在超时后停下 orchestrator，或者干脆把 orchestrator 也纳入拆除范围；在那之前只能写明「计划任务默认禁用、超时行为未验证」）。

---

## 与 harness-audit 的关系

`harness-audit.md` 的 21 条 + 披露项基本是**实现级**的：某段代码算错了、某个计数器恒零、某个 gate 从不失败。本文件是**运行层与实验层**的，二者不重复，但有三处交叉，我把边界写清楚：

| 本文件 | harness-audit 的对应条目 | 边界 |
|---|---|---|
| 第 3 条（环境记录不全） | 第 21 条「防火墙被关闭，但从未记录」 | 审计那条针对**当时的数据**（`/tmp/wf-bench/val/environment.json` 确实只有 9 个键）。当前代码（`orchestrator.ps1:399-402`）已经写了 `firewallProfiles`，所以那条的实现前提已经不成立；本文件补的是「其余环境量一条没测」和「测了也没有消费者」。 |
| 第 4 条（环境字段无消费者、`firewallProfiles` 恒 false） | 第 10–12 条「结构性恒零的计数器」 | 审计针对记录里的指标；本文件指出**环境块里也有同一种病**：`Set-…` 之后立刻 `Get-…`，字段记录的是脚本自己制造的状态。 |
| 第 10 条（看门狗 / 防火墙恢复） | 第 21 条 | 审计说「关了但没记录」；本文件说「**关了就再也不会自动开**」——`orchestrator.ps1:464-466` 的 teardown 没有恢复语句，`design.md:156` 承诺过这一条。 |

其余七条（驱动链路无版本控制、部署的是工作区、plan 口径未定义、时长成本 vs 统计轮数、无重跑规则且行会静默消失、原始数据不在仓库、时钟）在 `harness-audit.md` 里**没有对应条目**，可以按新增项处理。

另外两点方法论上的呼应，不构成重复：

- `.trellis/spec/backend/benchmark-methodology.md` 的头两条纪律是「**Before a gate is trusted, prove it can fail**」和「**Prove the measured path is taken, in setup**」。本文件第 4 条与第 7 条说的正是这套 campaign 没有对自己的 gate 做过反证：客户端里那两个注入开关（`--inject-corrupt-every` / `--inject-rewrite-every`，`README.md` 客户端 flag 表）从来没有被 `orchestrator.ps1` 传过（`Invoke-Client` 只传 `--target/--tcp-port/--udp-port/--dns-port/--plan/--out/--label/--sampler-process`），所以这套 campaign 里的门限从未在真实链路上被证明过能失败。
- `implement.md` 的 A4 曾经把 preflight 定义成「Enumerate adapters, firewall profiles, checksum/RSC/LSO advanced properties, loaded filter drivers and running services; **refuse to continue when a foreign interceptor is present**」（`:37-40`），实现里只剩「读一次防火墙 + 关掉它 + 一个只打 WARNING 不拒绝的 `Assert-CleanSlate`（`orchestrator.ps1:153-165`）+ 一次 TCP 可达性探测」。**这个承诺与实现的落差本身就是设计缺陷的证据**：preflight 从「一组会拒绝的断言」缩成了「三条只读检查」，而缩水这件事没有写在任何地方。
