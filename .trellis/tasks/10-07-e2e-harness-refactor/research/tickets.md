# 待办 ticket（E5-b 的 Windows 轮次暴露，2026-10-08）

证据：`baseline/E5b-windows.md`（1174 行）。裁定见 DD **D22**。

| # | 标题 | 影响 | 修法方向 |
|---|---|---|---|
| **T1** | `start-targets.sh` 不给靶机 `--label` | 账本记录 `label` 全空、dual 相位两条车道并行 ⇒ 端点分区与窗口归属无法判定；10 条 `ledger-endpoint-overlap` + 60 条 `ledger-window-ambiguous` | 启动器按靶机/实例传 `--label`，分析器用 label 选行（`LedgerViews` 已有 label→row 的路径） |
| **T2** | 源端点普查表打满 | pass 2 账本计数读成 0、`sourceOverflow` 3 万余；297 条 ledger caveat 的主体 | 先定位根因（普查容量 vs 1 Hz `sources` 增量 vs 双车道并发），再决定扩容/按窗口重置/披露口径 |
| T3 | orchestrator 吞退出码日志（`[void](Invoke-Client …)` 丢掉 `Write-Log` 管线） | 判据 1 只能由"`failed` 与退出码同源"推得 | `Invoke-Client` 的日志走 `Write-Host` 或把退出码单独记进结果文件 |
| T4 | `deploy-campaign.sh`/`publish-campaign.sh` 源与目标路径漂移（含 `cd` 进已删除的 `analysis/`） | 复现一轮的硬阻塞；本轮按脚本自己的第 1–3 步改指 | 让两个脚本从 plan/结果根参数推导路径，别再写死 `C:\wfbench\results` 与仓库内旧目录 |
| T5 | `wf.sh` 的 `repl_cmd` 传输竞态（判"最后一行是提示符"太早） | 上传/下载时 `ls`/`unzip` 扑空 | 用文件大小/哈希确认，或等提示符出现再返回 |
| T6 | `make_tree.py` docstring 说"每 pass 一份 `target-ledger.jsonl`"，生成器并不写 | 照它做会重复计数（本轮踩到：74094→148188） | 改 docstring 或让生成器真写 |

**AC18 的判据 4（findings 白名单外为空）在 T1/T2 修好之前不可达**；这是 E5 完成声明里显式写
`AC18=fail(T1,T2)` 的原因。被测链路（wf-aot + sing-box）本身健康：0 条 `harness-error`、
wf-aot 的 LOSS/MIX `foreignConnection=0`、§11 第 1 条缺陷以**指标形态**复现（REL `clean` 7/121 vs proxifier 121/121）。
