# 待办 ticket（E5-b 的 Windows 轮次暴露，2026-10-08）

证据：`baseline/E5b-windows.md`（1174 行）。裁定见 DD **D22**。T1/T2 的本轮工作见
`baseline/T1T2-label-and-census.md`。

| # | 标题 | 状态 | 影响 | 修法方向 |
|---|---|---|---|---|
| **T1** | `start-targets.sh` 不给靶机 `--label` | **fixed**（判据见下） | 账本记录 `label` 全空、dual 相位两条车道并行 ⇒ 端点分区与窗口归属无法判定；10 条 `ledger-endpoint-overlap` + 60 条 `ledger-window-ambiguous` | 启动器给每个实例 `--label target:<port>`；分析器把 run 只读进它 `run.json` 声明的那个靶机的账本，再按臂窗口定界（"用 label 选 run"在 shipped 拓扑下无解：一个实例服务 14 个 run） |
| **T2** | 源端点普查表打满 | **located（未修）** | pass 2 账本计数读成 0、`sourceOverflow` 3 万余；297 条 ledger caveat 的主体 | 根因已定位：每接收循环 64 槽、按 (address,port) 记账、**终身不回收**；campaign 累计 64 个源端点即满，此后每个数据报只进 overflow。修法：摘要器 harvest 后按区间回收槽位（约 30–50 行），见证据 §7.4 |
| T3 | orchestrator 吞退出码日志（`[void](Invoke-Client …)` 丢掉 `Write-Log` 管线） | open | 判据 1 只能由"`failed` 与退出码同源"推得 | `Invoke-Client` 的日志走 `Write-Host` 或把退出码单独记进结果文件 |
| T4 | `deploy-campaign.sh`/`publish-campaign.sh` 源与目标路径漂移（含 `cd` 进已删除的 `analysis/`） | open | 复现一轮的硬阻塞；本轮按脚本自己的第 1–3 步改指 | 让两个脚本从 plan/结果根参数推导路径，别再写死 `C:\wfbench\results` 与仓库内旧目录 |
| T5 | `wf.sh` 的 `repl_cmd` 传输竞态（判"最后一行是提示符"太早） | open | 上传/下载时 `ls`/`unzip` 扑空 | 用文件大小/哈希确认，或等提示符出现再返回 |
| T6 | `make_tree.py` docstring 说"每 pass 一份 `target-ledger.jsonl`"，生成器并不写 | open | 照它做会重复计数（本轮踩到：74094→148188） | 改 docstring 或让生成器真写 |
| **T7** | **两台机器的 UTC 时钟差约 3 s**（本轮新发现） | open | 臂窗口按客户端 UTC 算、账本按靶机 UTC 写，两者直接比较 ⇒ 每个臂的窗口整体错位，窗口边缘落进相邻臂的流量；是 T1 修完后残余 ledger caveat 的主因 | 跑前给 VM 对时（`w32tm /resync` / `Set-Date`），并把两边的 `Get-Date`/`date -u` 记进证据；已写进 `AGENTS.local.md` §10 的可信前提 |

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

## T2 的定位结论（本轮实测）

- **根因**：`Target/SourceCensus.cs` 每接收循环 64 槽、键 `(address, port)`、槽位一次认领**终身不回收**；
  一个持续发包的端点会在**每张**表里占一格 ⇒ 有效容量 = 64×8/8 = **64 个端点整个 campaign 累计**，
  而产出（`sources[]`）是每区间增量。容量与语义不匹配就是缺陷本身。
- **真实证据**：E5-b 的 `ledger-main.jsonl` 恰好记下 64 个源端点（最后一个 `10:45:18`），
  `10:45:58` 起每一秒 `received` 100 % 进 `sourceOverflow`，此后 42 分钟 `sources` 恒为 0。
- **最小复现**：`--udp-receivers 1` + 80 个新端口 ⇒ 恰好 64 个入表、overflow = 3200−2560 = 640；
  三波新端口一个也进不来，而已在表里的端口继续被正确累计。
- **后续**：按独立 ticket 修（按区间回收槽位），代价与替代方案见证据 §7.4。

## T7 的证据（本轮新发现）

- 当场实测：VM 的 `(Get-Date).ToUniversalTime()` 比主机 `date -u` 快 **2.5–3.2 s**（命令墙钟 0.65 s）。
- 从 campaign 记录量：把 LOSS 臂的突发末秒与客户端自己的臂结束时刻相减，10 个突发独立的 run 中位数
  **−3.08 s**（范围 −2.60…−3.42）。
- 影响：把账本 `utc` 整体 +3.0 s 再分析，`ledger-connection-mismatch` 90 → **13**、
  `ledger-datagram-mismatch` 102 → **33**（且这 33 条**全部**落在报 `sourceOverflow > 0` 的 scope 里，
  即全部是 T2），measurement-caveat 总数 249 → **96**。敏感性：2.5–3.5 s 的平移把总数压到 96–161。

**AC18 的判据 4（findings 白名单外为空）在 T1/T2/T7 修好之前仍不可达**；这是 E5 完成声明里显式写
`AC18=fail(T1,T2)` 的原因（本轮把 T7 也加了进来）。被测链路（wf-aot + sing-box）本身健康：0 条
`harness-error`、wf-aot 的 LOSS/MIX `foreignConnection=0`、§11 第 1 条缺陷以**指标形态**复现
（REL `clean` 7/121 vs proxifier 121/121）。
