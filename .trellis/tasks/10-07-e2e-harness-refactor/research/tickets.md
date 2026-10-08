# 待办 ticket（E5-b 的 Windows 轮次暴露，2026-10-08）

证据：`baseline/E5b-windows.md`（1174 行）。裁定见 DD **D22**。T1 的本轮工作见
`baseline/T1T2-label-and-census.md`，T2 的修复见 `baseline/T2-census-reclaim.md`。

| # | 标题 | 状态 | 影响 | 修法方向 |
|---|---|---|---|---|
| **T1** | `start-targets.sh` 不给靶机 `--label` | **fixed + 复跑验证通过**（判据见下，2026-10-08 复跑见「T1/T2/T7 的复跑验证」） | 账本记录 `label` 全空、dual 相位两条车道并行 ⇒ 端点分区与窗口归属无法判定；10 条 `ledger-endpoint-overlap` + 60 条 `ledger-window-ambiguous` | 启动器给每个实例 `--label target:<port>`；分析器把 run 只读进它 `run.json` 声明的那个靶机的账本，再按臂窗口定界（"用 label 选 run"在 shipped 拓扑下无解：一个实例服务 14 个 run） |
| **T2** | 源端点普查表打满 | **fixed + 复跑验证通过**（判据见下，2026-10-08 复跑见「T1/T2/T7 的复跑验证」） | pass 2 账本计数读成 0、`sourceOverflow` 3 万余；297 条 ledger caveat 的主体 | 根因：每接收循环 64 槽、按 (address,port) 记账、**终身不回收**；campaign 累计 64 个源端点即满，此后每个数据报只进 overflow。修法：槽位按**区间**回收——摘要器每秒 `Harvest` 后推进世代，接收侧把"整整一个区间没有数据报"的槽当空槽（认领世代 `_claim` 决定计数基线，`_lastSeen` 决定空闲），沿用并加固原有的无锁握手 |
| T3 | orchestrator 吞退出码日志（`[void](Invoke-Client …)` 丢掉 `Write-Log` 管线） | open | 判据 1 只能由"`failed` 与退出码同源"推得 | `Invoke-Client` 的日志走 `Write-Host` 或把退出码单独记进结果文件 |
| T4 | `deploy-campaign.sh`/`publish-campaign.sh` 源与目标路径漂移（含 `cd` 进已删除的 `analysis/`） | open | 复现一轮的硬阻塞；本轮按脚本自己的第 1–3 步改指 | 让两个脚本从 plan/结果根参数推导路径，别再写死 `C:\wfbench\results` 与仓库内旧目录 |
| T5 | `wf.sh` 的 `repl_cmd` 传输竞态（判"最后一行是提示符"太早） | open | 上传/下载时 `ls`/`unzip` 扑空 | 用文件大小/哈希确认，或等提示符出现再返回 |
| T6 | `make_tree.py` docstring 说"每 pass 一份 `target-ledger.jsonl`"，生成器并不写 | open | 照它做会重复计数（本轮踩到：74094→148188） | 改 docstring 或让生成器真写 |
| **T7** | **两台机器的 UTC 时钟差约 3 s**（本轮新发现） | **已处置 + 复跑验证通过**（2026-10-08）：跑前 `Set-Date` 把 VM 校到 ≤0.013 s，跑完残留 +0.14 s；`w32tm /resync` 在本机拓扑下**无效**（时间源就是那个偏移本身） | 臂窗口按客户端 UTC 算、账本按靶机 UTC 写，两者直接比较 ⇒ 每个臂的窗口整体错位，窗口边缘落进相邻臂的流量；是 T1 修完后残余 ledger caveat 的主因 | 跑前给 VM 对时（见下方「T1/T2/T7 的复跑验证」的步骤），并把两边的 `Get-Date`/`date -u` 记进证据；已写进 `AGENTS.local.md` §10 的可信前提 |

## T2 的修复判据（本轮实测）

修法分两半：`benchmarks/WinForward.E2E/Target/SourceCensus.cs`（槽位加 `_claim`/`_lastSeen`，`Harvest`
推进世代）+ 新增 `tests/WinForward.E2E.Tests/SourceCensusTests.cs`；契约与取舍写进
`measurement-harness.md` §3.12 与 `benchmarks/WinForward.E2E/README.md`。证据全文见
[`baseline/T2-census-reclaim.md`](baseline/T2-census-reclaim.md)。

1. **微型复现**（`--udp-receivers 1`，三波全新端口：80/80/40 个 × 40 轮，波间静默 2 s）：
   修前第 1 波恰好 64 入表、overflow = 3200 − 2560 = 640，第 2、3 波 **0 入表**（`sources` 恒 0、
   overflow 全收）；修后第 1 波数字**逐项相同**，第 2 波 64/80 入表（overflow 仍只属于没抢到格子的
   16 个端口），第 3 波 **40/40 入表、overflow 0**。修后整轮 `received = censused + overflow`（8000 = 6720 + 1280）。
2. **真实规模**（8 接收循环 + 60 个持续端点 + 400 个历史端点 + 100 个探测端点 × 10 波）：
   修前每一波 **0/10**、每区间 `sources` 恒 60、整轮 overflow 7519；修后每一波 **10/10**、每区间
   `sources` = 60 + 10 = 70、overflow 0（除历史突发那一秒），100 个探测端点全部入表，整轮见到的不同
   端口 129 → 559，整轮 overflow 7519 → 2771（残留全部有归属：历史突发超容 + 换手瞬间）。
3. **语义不变**：`sources[]` 仍是本区间增量（续存端点跨区间按差值发布）、`sourceOverflow` 仍是本区间
   没能入表的包数、`udpReceivers` 等字段未动；冻结 oracle 51 切片 0 差异；`check-readme-contract.py` ok。
4. **单测**：4 条事实绿；把 `SourceCensus.cs` 换回修前版本后
   `ASlotNoDatagramTouchedForAnIntervalIsReclaimedForAFreshEndpoint` 红（Expected 64 / Actual 0）。
5. **回归**：`selftest.sh scripts/plans/selftest-plan.json` rc=0、产物 **0 条 error 记录**；分析器对该轮
   产物 rc=0；把 3 条 `udpSummary` 的 `sourceOverflow` 注入成非零后 `ledger-source-overflow` 照常报出。
6. **边界代价**：换手会重置被顶掉槽位的计数，实测 32040 个数据报里差 **1** 个（微型复现差 0），远在
   分析器的 ±band 以内；有界、已披露，写进 spec §3.12。
7. **门禁**：build 零警告、`dotnet test -m:1` 14 项目 **1660 passed / 0 failed**、format 空输出、
   inspectcode 清缓存全量 0 `<Issue>`、effective-lines 四路径无输出、oracle-diff 51 切片 rc=0。

## T1 的修复判据（本轮实测）

修法分两半：`benchmarks/WinForward.E2E.Analysis/Findings/LedgerViews.cs`（入库）+
本机 `benchmarks/WinForward.E2E/scripts/start-targets.sh` 的两个 `--label target:<port>`（**该文件被
`.gitignore` 排除**，是本机胶水，不入库）。约定与理由写进两个 README 的 "Run the target" /
"Attribution"。

1. **真实 E5-b 数据重跑**（只把账本 `label` 改成修好的启动器会写的值，记录内容不动）：
   `ledger-endpoint-overlap` 10 → **0**、`ledger-window-ambiguous` 60 → **0**；
   `ledger-source-overflow` 64 → 50（真值）。
2. **本机双车道 fixture**（自造，两靶机 + 两车道同时跑）：20 条 findings → 3 条，其中 2 条是 fixture
   自身形状；`ledger-endpoint-overlap` 1 → 0、`ledger-window-ambiguous` 6 → 0、conn/datagram mismatch
   10 → 0。
3. **等价性**：同一棵树上"实例绑定"与"per-run label"两份输出的计数逐项相同（真实数据与本机各一次）。
4. **回归**：修好的分析器跑**没打 label** 的真实树，逐项等于基线（320 条白名单外 findings 一条不差）。
5. **冻结 oracle**：`oracle-diff.py --batch 1a,1b,1c,2,3,4,5` ⇒ 51 切片、0 差异、rc=0。
6. 门禁：build 零警告、`dotnet test -m:1` exit 0（14 项目 1656 passed）、format 空输出、
   inspectcode 0 `<Issue>`、effective-lines 四路径无输出。

## T2 的定位结论（T1 轮实测，已由本轮修复）

- **根因**：`Target/SourceCensus.cs` 每接收循环 64 槽、键 `(address, port)`、槽位一次认领**终身不回收**；
  一个持续发包的端点会在**每张**表里占一格 ⇒ 有效容量 = 64×8/8 = **64 个端点整个 campaign 累计**，
  而产出（`sources[]`）是每区间增量。容量与语义不匹配就是缺陷本身。
- **真实证据**：E5-b 的 `ledger-main.jsonl` 恰好记下 64 个源端点（最后一个 `10:45:18`），
  `10:45:58` 起每一秒 `received` 100 % 进 `sourceOverflow`，此后 42 分钟 `sources` 恒为 0。
- **最小复现**：`--udp-receivers 1` + 80 个新端口 ⇒ 恰好 64 个入表、overflow = 3200−2560 = 640；
  三波新端口一个也进不来，而已在表里的端口继续被正确累计。
- **修复**：按区间回收槽位（世代 + 认领，见上面的判据与
  [`baseline/T2-census-reclaim.md`](baseline/T2-census-reclaim.md)）；代价与替代方案见该证据第 1、2 节。

## T7 的证据（本轮新发现）

- 当场实测：VM 的 `(Get-Date).ToUniversalTime()` 比主机 `date -u` 快 **2.5–3.2 s**（命令墙钟 0.65 s）。
- 从 campaign 记录量：把 LOSS 臂的突发末秒与客户端自己的臂结束时刻相减，10 个突发独立的 run 中位数
  **−3.08 s**（范围 −2.60…−3.42）。
- 影响：把账本 `utc` 整体 +3.0 s 再分析，`ledger-connection-mismatch` 90 → **13**、
  `ledger-datagram-mismatch` 102 → **33**（且这 33 条**全部**落在报 `sourceOverflow > 0` 的 scope 里，
  即全部是 T2），measurement-caveat 总数 249 → **96**。敏感性：2.5–3.5 s 的平移把总数压到 96–161。

**AC18 的判据 4（findings 白名单外为空）在 T1/T2/T7 修好之前仍不可达**；这是 E5 完成声明里显式写
`AC18=fail(T1,T2)` 的原因（T1 轮把 T7 也加了进来）。**T2 本轮已 `fixed`**（判据见上）：修后账本族
剩余的 caveat 只该来自 T7（跨机时钟）与既有的已定义披露（`ledger-source-overflow` 现在只在真正
超过"每区间 64 个活跃端点/循环"时出现）。被测链路（wf-aot + sing-box）本身健康：0 条
`harness-error`、wf-aot 的 LOSS/MIX `foreignConnection=0`、§11 第 1 条缺陷以**指标形态**复现
（REL `clean` 7/121 vs proxifier 121/121）。

---

## T1/T2/T7 的复跑验证（E5-b2，2026-10-08）

证据全文：[`baseline/E5b2-windows-rerun.md`](baseline/E5b2-windows-rerun.md)。一轮 **3 个 pass**、
压缩 plan（`scripts/plans-windows/full-shape-plan.json`）、`-PlanRoot C:\wfbench\e2e-win2`，
起止 15:19:36 → 17:50:12 UTC（`=== no failures ===`、`orch-e5b2.err` 0 字节）。

| ticket | E5-b | E5-b2 | 复跑结论 |
| --- | --- | --- | --- |
| **T1** | `ledger-endpoint-overlap` 10、`ledger-window-ambiguous` 60 | **0 / 0** | 通过。账本 20662 + 10961 条记录的 `label` 全部是 `target:40010` / `target:40011`；分析器口径是「按 `run.json` 声明的实例选账本，再用臂窗口定界」 |
| **T2** | `ledger-source-overflow` 64（pass2 一个 120 s 窗口 58700 个数据报进不了表、后续窗口读成 0） | **0** | 通过。整轮 9532×2 个 `udpSummary` 区间 `sourceOverflow` **恒为 0**，`maxReceived` 1761983；`sources[]` 仍是本区间增量 |
| **T7** | VM 快 2.5–3.4 s ⇒ `ledger-connection-mismatch` 71、`ledger-datagram-mismatch` 102 | 残留 **35 / 1**，其中 **21 条**可用单一 δ 精确解释（δ 逐 pass 从 +0.01 增到 +0.11） | 通过（残留已量化）。跑前 +3.0 s → −0.013 s，跑完 +0.14 s，漂移 0.054 s/h；三段式三次探针全部落在主机窗口内 |

**T7 的对时步骤（可照抄，2026-10-08 实测有效）**：

1. `w32tm /query /status` 先看一眼：本机 VM 的 `w32time` 是 `Stopped / Manual`，
   `Start-Service w32time` + `w32tm /resync /force` 会报 `The command completed successfully.`
   **但时钟纹丝不动**——因为它的时间源是 `VM IC Time Synchronization Provider`（stratum 1 = 宿主），
   而那个源本身就带着约 3.3 s 偏移。**所以这条路在本机拓扑下是无效的**，别被"成功"骗了。
2. 把 `w32time` 停回 `Stopped/Manual`（停掉之后没有任何东西会再把时钟搬回去，实测 4.6 分钟漂 0.002 s）。
3. `Set-Date`。**坑**：`Set-Date -Date <Kind=Utc 的 DateTime>` 会把那个 DateTime 的**字面数字当本地时间**，
   而 VM 的时区是 `Pacific Standard Time`（夏令时 UTC−7）——照 UTC 传会把时钟整体搬错 7 小时。
   正确写法（把主机 UTC 显式转成 `Kind=Local`）：
   ```powershell
   $t = [datetime]::Parse('<主机 date -u 的结果>', [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind)
   $l = [datetime]::SpecifyKind($t, [System.DateTimeKind]::Utc).ToLocalTime()
   Set-Date -Date $l
   ```
4. 复测。三段式（主机 `date -u` → VM `(Get-Date).ToUniversalTime()` → 主机 `date -u`）只能给到
   「窗口宽度 = 一次 `wf.sh run` 的往返 ≈ 0.63 s」，**判不到 0.2 s 门槛**。要判准就用 socket 级交换：
   主机在 `192.168.100.4:40099` 监听，VM 连上、发 `(Get-Date).ToUniversalTime()`，主机在 `recv()` 返回时
   打时间戳并回送（单次往返 0.2 ms 量级），偏移取 `vmT0 + vmRtt/2 − hostRecv`。
   工具留在 `/tmp/wf-bench/clock-probe.py`、`clock-probe.ps1`、`clockcheck.sh`、`clock-offset.py`。
5. **残留**：本轮实测 0.054 s/h 的自漂移（3 小时约 0.15 s）。它仍然足以在**臂边界**上把下一条臂
   头 50–100 ms 的**突发**连接算进上一条臂（`pass2/proxifier` 的 THRU 因此多算 46 条、MIX 少算 52 条），
   这是本轮 35 条 `ledger-connection-mismatch` 里 20 条的成因。要彻底消掉得让分析器自己估偏移
   （或让靶机按 run 定时重启），属于比 T7 更大的决定，本轮不修。
