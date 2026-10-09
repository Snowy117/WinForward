# Phase D —— VM 验收实验（AC1–AC5）

任务：`.trellis/tasks/10-09-tcp-close-drain-primary`（prd.md 的 AC1–AC5、design.md §3/§9、implement.md 的 Phase D）。

一句话结论：**四臂全部达到判据** —— `halfClose=100`、`clean=100`、四模式 REL 三条臂的 `observed.timeout` 都是 **0**，`halfClose` 两臂 `clean == connectAttempts`；A/B 显示 drain-only 与 drain+FIN **等价**（都是 601/601 clean、0 timeout），手工 FIN 是冗余的；四臂 3004 次 clean end 的 drain 事件 **全部 `acknowledged`**，p50 = 0 ms、p99 = 0 ms、max = 2 ms，峰值并发 **1**，对 session slot / 端口预算的开销可以忽略。

| AC | 臂 | 判定 | 关键数字 |
| --- | --- | --- | --- |
| AC1 | `hh-nofin`（drain-only，halfClose=100，30 s @ 20） | **通过** | clean 601 / connectAttempts 601，timeout **0**，drain 601/601 acknowledged |
| AC2 | `cl-nofin`（drain-only，clean=100，30 s @ 20） | **通过** | clean 601 / 601，timeout **0**，drain 601/601 acknowledged |
| AC3 | `rel-nofin`（drain-only，四模式，60 s @ 20） | **通过** | timeout **0**；unexpectedEof **590**（父任务参考臂 586）、reset **0**；4 例 `otherError`（0.33 %，resetAfterN 子模式，机制见 §9.2） |
| AC4 | `hh-nofin` vs `hh-fin` | **等价 → 手工 FIN 冗余** | 两臂都 clean 601/601、timeout 0；fin 臂 `Client-visible close injected` 601 次，drain 退出分布与 nofin 臂一致 |
| AC5 | 四臂 drain 事件 | **通过** | 3004 事件全部 `acknowledged`；ElapsedMs p50 0 / p99 0 / max 2 ms；峰值并发 drain = 1；不新增端口，只延长既有 session slot 的持有时间 |

> **最终 revision 认证臂（2026-10-09 10:57Z，`fin-cert`）**：最终树（手工 FIN 已彻底删除）halfClose=100 臂 clean 601/601、timeout 0、truncated 0，`tcp.redirect.drain` 601 事件全部 `acknowledged`，被删 close 事件与日志符号 **0 次残留**，看门狗跑臂前确已 Disabled —— 详见 §11「Final certification」。

---

## 1. 现场状态与时间线

| 项 | 值 |
| --- | --- |
| Windows VM | `192.168.100.2`（WinLTSC，build 26100，16 逻辑核），经 tmux `wfbench` + `benchmarks/WinForward.E2E/scripts/wf.sh` 驱动 |
| Linux 靶机 | `192.168.100.4:40020` TCP+UDP、`192.168.100.4:40053` DNS，target pid 128962，`--label drain`，账本 `/tmp/wf-drain-run/ledger.jsonl`（3733 行） |
| 产品沙箱 | `C:/wf-ac0`（复用 AC0 沙箱）；`C:/wfbench` 全程只读未写（`heartbeat.txt` 在我方实验期间未变化，见 §9.1/§9.3） |
| 产品配置 | `WinForward.exe run --config C:/wf-ac0/wf-fdd/appsettings-debug.json`（Debug 级，AC5 需要 `tcp.redirect.drain`） |
| 防火墙 | 每臂前 `Domain/Private/Public = False`（10:31 关闭并复验）；10:37:43 恢复 `True/True/True` 并复验 |
| 看门狗 | **实验期间未按纪律禁用（偏差，见 §9.1）**；任务全程 Enabled/Ready，实际触发时刻 10:41:02Z（最后一臂结束 10:37:00Z、防火墙恢复 10:37:43Z **之后**），各臂窗口内未触发；结束后保持 Ready |
| 时钟 | VM 时钟快于主机；in-band 估计 **+0.38 ~ +0.43 s**（见 §5.3），工具调用成对采样为 +0.64 s（含往返不确定度） |
| 残留进程 | 实验结束：无 WinForward / sing-box / orchestrator 进程 |

时间线（UTC）：

| 时刻 | 事件 |
| --- | --- |
| 10:21:51 | 本机开始（快照与构建已在更早完成），主工作树 `git status` 与 provenance 逐行一致 |
| 10:26 | 上传 stage-d.zip、Expand-Archive、`Set-ExecutionPolicy -Scope Process Bypass` |
| 10:30:35 | 现场检查：防火墙 True、看门狗 Ready、无残留进程；VM UTC 记录 |
| 10:31 | 防火墙全关；建 4 个 out 目录；起 sing-box（pid 3944，`127.0.0.1:1080` 已复验）；覆盖 nofin 变体；起产品（pid 3884） |
| 10:33:09.6 – 10:33:39.7 | **AC1** `hh-nofin`（30.112 s） |
| 10:34:22.4 – 10:34:52.5 | **AC4-A** `hh-fin`（30.090 s，fin 变体 pid 5616） |
| 10:35:10.9 – 10:35:40.99 | **AC2** `cl-nofin`（30.084 s，nofin 变体 pid 1308） |
| 10:36:00.2 – 10:37:00.31 | **AC3** `rel-nofin`（60.087 s，nofin 变体 pid 2872） |
| 10:37:0x – 10:37:38 | 停产品、拉产品日志；停 sing-box(3944)、拉 sing-box 日志 |
| 10:37:43 / 10:37:46 | 防火墙恢复 True/True/True；终态检查（无进程） |
| 10:41:02 | 看门狗因心跳陈旧 1558 s 自行触发（**实验窗口外**），停 ProxiFyre/Proxifier 服务并重写心跳；与本次测量无关 |

臂顺序按任务要求：相邻两臂（AC1 与 AC4-A）使用**同一个 plan 文件**（planHash 相同），只换变体；同臂内产品进程不动。

---

## 2. 构建、冻结快照与一处必要的编译修正（重要偏差）

### 2.1 两个变体的发布

```bash
cd /tmp/wf-drain-snapshot && dotnet publish src/WinForward.Cli/WinForward.Cli.csproj -c Release -r win-x64 -p:PublishAot=false --self-contained false -o /tmp/wf-drain-pub/fin
cd /tmp/wf-drain-only     && dotnet publish src/WinForward.Cli/WinForward.Cli.csproj -c Release -r win-x64 -p:PublishAot=false --self-contained false -o /tmp/wf-drain-pub/nofin
```

| 产物 | sha256 | 备注 |
| --- | --- | --- |
| `/tmp/wf-drain-pub/fin/WinForward.exe`（drain+FIN） | `75cb5940a3bce572d510545dc25d8a300fde7bcd0f33b469dfb73ff8c65845c2` | `FIN_PUBLISH_EXIT=0` |
| `/tmp/wf-drain-pub/nofin/WinForward.exe`（drain-only） | `8b894ac71305be383d971f48cd5effa5ddde6ffb012147fa76f464c8be18e27a` | 编译修正后重发布，`NOFIN_EXIT=0` |
| stage zip（drive.ps1 + 3 plan + 2 变体） | `9776ae6be278e1cd82af65e1b6448eb03aed64c69cee9c5a92519ec26d6b22e5` | 上传后 VM 侧逐文件 hash 复验一致 |

VM 侧复验（`drive.ps1 hashes`）：`variants/fin/WinForward.exe` = `75cb5940…`、`variants/nofin/WinForward.exe` = `8b894ac7…`；harness 客户端 `e2e/WinForward.E2E.exe` = `307e3196…`、`e2e/WinForward.E2E.dll` = `93200c77…`，与 AC0 报告 §2 的已校验产物一致（直接复用）；`wf-fdd/ndisapi.dll` = `c9ece4a7…`（AC0 遗留 sidecar，未改）。

### 2.2 drain-only 快照原样无法编译

`/tmp/wf-drain-only` 的快照在 `dotnet publish` 时**失败**（`src/**` 的分析器错误即构建错误）：

```
TcpRedirectAcceptor.cs(339,13): error S125: Remove this commented out code.
TcpRedirectAcceptor.cs(331,59): error S1172: Remove this unused method parameter 'relay'.
NOFIN_PUBLISH_EXIT=1
```

原因：按 patch 删掉 clean-end 的 `else` 分支后，`InjectClientVisibleCloseAsync` 的 `relay` 参数不再被使用（S1172），而留下的注释行以 `;` 结尾被 Sonar 读作注释掉的代码（S125）。这是**冻结快照本身的缺陷**，不是本次改动的问题。

我的最小修正（只动 `/tmp/wf-drain-only`，主工作树未碰）：去掉不再使用的 `relay` 参数；把方法改成「clean end 早返回、abnormal end 注入 RST」的形状，语义与 patch 完全一致（clean end 不注入任何包；stalled/faulted 仍注入 RST|ACK）。完整 diff 见 `research/verification/drain/variant-diff-acceptor.diff`（同时包含 patch 本身的删除内容）。

| 文件 | sha256 |
| --- | --- |
| `/tmp/wf-drain-snapshot/.../TcpRedirectAcceptor.cs`（= 主工作树文件，未改） | `ee129010e248bb71c1f963a7ad4d56034d13576aea875f80e3027eaa5196a77a` |
| `/tmp/wf-drain-only/.../TcpRedirectAcceptor.cs`（patch + 本次编译修正后） | `48de2252ec9d98f709b454d1d802b9363ec9dc84586e5b4632ab703a4d414603` |

对照臂 fin 快照与主工作树同 hash（`ee129010…`），未做任何修正。

---

## 3. 沙箱、驱动脚本与命令原文

沙箱沿用 AC0 的 `C:/wf-ac0`（e2e / wf-fdd / singbox / plans / logs），新增：`variants/{fin,nofin}/`、`data/`（pid 文件）、`logs/wf-<label>.{out,err}`、`o-<label>/`。配置与 sing-box config 一律未改。

驱动脚本 `C:/wf-ac0/drive.ps1`（原文入库为 `drive.ps1`，sha256 `d85dcab7…`）提供 `fw off|on`、`wd off|on`、`sb start|stop`、`wf start <label>|stop`、`variant <fin|nofin>`、`client <label> <plan> <out>`、`hashes`、`clock`、`status`；每个动词自带 try/catch，避免 PS 语句终止错误吃掉 wf.sh 的结束标记（AC0 §8 的坑）。因脚本执行策略被禁用，先执行过一次 `Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force`。

逐字命令：

``` powershell
# 靶机（Linux，本机；setsid + nohup，pid 存文件）
/tmp/wf-ac0-pub/linux/WinForward.E2E target --bind 192.168.100.4 --tcp-port 40020 --udp-port 40020 --dns-port 40053 --label drain --ledger /tmp/wf-drain-run/ledger.jsonl

# VM 侧（全部经 wf.sh run "& C:/wf-ac0/drive.ps1 <verb>"）
& C:/wf-ac0/drive.ps1 fw off
& C:/wf-ac0/drive.ps1 sb start
& C:/wf-ac0/drive.ps1 variant nofin|fin          # Copy-Item variants/<v>/WinForward.exe wf-fdd/，回显新 hash
& C:/wf-ac0/drive.ps1 wf start <label>           # Start-Process WinForward.exe run --config appsettings-debug.json
& C:/wf-ac0/drive.ps1 client <label> <plan.json> <out-dir>

# 客户端命令（drive.ps1 client 展开）
C:/wf-ac0/e2e/WinForward.E2E.exe client --target 192.168.100.4 --plan C:/wf-ac0/plans/<plan> --out C:/wf-ac0/<out> --label <label> --tcp-port 40020 --udp-port 40020 --dns-port 40053
```

plan（原文入库，含 sha256）：

| 臂 | plan 文件 | 内容 |
| --- | --- | --- |
| hh-nofin / hh-fin | `plans/p-hh.json`（planHash `0f215d26f7c0b8b6`） | `{"arms":[{"name":"REL","kind":"reliability","seconds":30,"connectionsPerSecond":20,"modeMix":"halfClose=100"}]}` |
| cl-nofin | `plans/p-cl.json`（planHash `fd58a2d9e3b2fb95`） | 同上，`clean=100` |
| rel-nofin | `plans/p-rel.json`（planHash `91185995e3037e77`） | `seconds:60`，`modeMix: clean=25,resetAfterN=25,partialFin=25,halfClose=25` |

结果下载：`wf.sh down C:/wf-ac0/o-<label>/REL.jsonl`、`.../run.json`、`C:/wf-ac0/logs/wf-<label>.err`（产品日志在进程存活时被锁，先 `wf stop` 再拉，与 AC0 一致）。

---
## 4. 每臂结果

### 4.1 AC1 —— `hh-nofin`（drain-only，halfClose=100，30 s @ 20）

- 变体 nofin `8b894ac7…`；窗口 10:33:09.5806605Z → 10:33:39.6969633Z（30.112 s）
- `connectAttempts = scheduledAttempts = 601`；`attemptRecords 37 / omitted 0`；`gates.clientSendLoss = 0`；`achievedRate 20.021`
- `observed = { clean: 601, reset: 0, unexpectedEof: 0, timeout: 0, connectFail: 0, halfCloseViolation: 0, otherError: 0 }`，`fidelityMismatch 0`，`echoedBytes 4,923,392 / trailerBytes 461,568`
- 抽样的 37 条 attempt 记录形状全是 `(halfClose, clean, clean, 8192, eof=true)`；`attemptRecordsOmitted = 0` 保证没有丢弃
- 产品日志：`tcp.relay.ended` 601（全部 cleanEnded）；`Client-visible close injected` **0**；`Client reset injected` 0
- drain：**601 事件，全部 `acknowledged`**，ElapsedMs min 0 / p50 0 / p90 0 / p99 0 / max 1 ms，总持有 1 ms
- 靶机账本块：601 条 `halfClose/halfClose/bytesEchoed=8192`；sing-box 入站计数 593（窗口内）/601（±2 s）

### 4.2 AC4-A —— `hh-fin`（drain+FIN，同 plan）

- 变体 fin `75cb5940…`；窗口 10:34:22.3767442Z → 10:34:52.4710207Z（30.090 s）；planHash 与 AC1 相同
- `601 / 601`；omitted 0；clientSendLoss 0；achievedRate 20.010
- `observed = { clean: 601, 其余全 0 }`；37 条样本同样全 `(halfClose, clean, clean, 8192, true)`
- 产品日志：relay 601 established / 601 cleanEnded；**`Client-visible close injected` 601（injected）**（手工 FIN 每一次都执行了）；clientReset 0
- drain：601 事件全部 `acknowledged`，p50 0 / p99 0 / max 2 ms，总持有 2 ms
- 靶机账本块：601 条 halfClose；sing-box 589/601

### 4.3 AC2 —— `cl-nofin`（drain-only，clean=100，30 s @ 20）

- 变体 nofin；窗口 10:35:10.9005879Z → 10:35:40.9888213Z（30.084 s）
- `601 / 601`；omitted 0；clientSendLoss 0；achievedRate 20.015
- `observed = { clean: 601, 其余全 0 }`；`trailerBytes 0`（clean 模式无尾随数据）
- 产品日志：601/601 cleanEnded；clientClose/clientReset 各 0；drain 601 全部 `acknowledged`（max 2 ms）
- 靶机账本块：601 条 `clean/clean`；sing-box 窗口内 601（±2 s 601，完全吻合）

### 4.4 AC3 —— `rel-nofin`（drain-only，四模式，60 s @ 20）

- 变体 nofin；窗口 10:36:00.2184300Z → 10:37:00.3088385Z（60.087 s）
- `1201 / 1201`；`attemptRecords 375 / omitted 0`；clientSendLoss 0；achievedRate 20.006
- `observed = { clean: 607, unexpectedEof: 590, reset: 0, timeout: 0, connectFail: 0, halfCloseViolation: 0, otherError: 4 }`
- 分模式（客户端 `byMode`）：`clean 301 → clean 301`；`resetAfterN 300 → unexpectedEof 290 + clean 6 + otherError 4`；`partialFin 300 → unexpectedEof 300`；`halfClose 300 → clean 300`
- 产品日志：relay `1201 established / 1201 cleanEnded`（sing-box 把上游 RST 洗成 FIN，所以 resetAfterN 的 abort 在产品侧也是 clean end）；clientClose 0；clientReset 0；**drain 1201 全部 `acknowledged`（max 1 ms）**；Warning 仅 3 条（配置警告 1、read-shape 1、GC 心跳 1，与 AC0 同类）
- 靶机账本块：1201 条，`halfClose 300 / clean 301 / resetAfterN 300 / partialFin 300`，verdict 与 mode 一一对应（`reset` 300、`clean` 301、`partialFin` 300、`halfClose` 300）——与客户端 `byMode.attempts` 完全一致
- 父任务参考臂（`fix3-rel.jsonl`，7cababb 状态、同 plan）：`clean 592 / unexpectedEof 586 / timeout 23 / otherError 0`

---

## 5. 有效性前置检查（每臂）

### 5.1 客户端指标

| 检查 | hh-nofin | hh-fin | cl-nofin | rel-nofin |
| --- | --- | --- | --- | --- |
| `scheduledAttempts == connectAttempts` | 601 == 601 | 601 == 601 | 601 == 601 | 1201 == 1201 |
| `attemptRecordsOmitted` | 0 | 0 | 0 | 0 |
| `gates.clientSendLoss` | 0 | 0 | 0 | 0 |
| `truncated` | 0 | 0 | 0 | 0 |
| `connectFail / halfCloseViolation` | 0 / 0 | 0 / 0 | 0 / 0 | 0 / 0 |
| `achievedRate`（目标 20/s） | 20.021 | 20.010 | 20.015 | 20.006 |

### 5.2 靶机账本（窗口定界）

把账本按「相邻记录间隔 > 5 s」切块，得到**恰好四块**，与四个臂一一对应：

| 块 | 条数 | 起止（主机 UTC） | 跨度 | mode 分布 |
| --- | --- | --- | --- | --- |
| hh-nofin | 601 | 10:33:09.213322 … 10:33:39.058657 | 29.845 s | halfClose 601 |
| hh-fin | 601 | 10:34:21.967433 … 10:34:51.871581 | 29.904 s | halfClose 601 |
| cl-nofin | 601 | 10:35:10.508158 … 10:35:40.387120 | 29.879 s | clean 601 |
| rel-nofin | 1201 | 10:35:59.809885 … 10:36:59.707503 | 59.898 s | clean 301 / resetAfterN 300 / partialFin 300 / halfClose 300 |

- 每块条数与臂的 `connectAttempts` 精确一致，mode 分布与 plan 完全对应；四块之间最小间隔 42.4 s，不存在跨臂混入。
- 直接用 run 窗口圈（原始）时窗口内有 590/590/590/1190 条：缺口全部在**前缘**，来自 VM 时钟快于主机（§5.3）。按偏移修正后为 601/598/601/1198（残余 1–3 条为尾部：部分连接的账本记录在客户端臂结束时刻之后才落盘）。两种口径都远小于窗口宽度，块的独立性已把归属钉死。
- 账本 `bytesEchoed` 无缺失行；靶机在实验结束时仍在运行，因此账本里只有 tcp 3004 条与 udpSummary 729 条，没有 tcpSummary/targetSummary 汇总记录（汇总在停机时写）。

### 5.3 时钟

- 工具调用成对采样（主机 `date -u` 与 VM `(Get-Date).ToUniversalTime()`，同一批次，含 ~1 s 往返不确定度）：`+0.650 / +0.637 / +0.638 / +0.645 / +0.651 / +0.642 / +0.639 / +0.645 / +0.116` s（最后一个是离群值，前面有 wf.sh 往返排队）。
- in-band 估计（更可靠）：每臂账本块首条记录是**连接结束**时刻，`窗口起点(VM) − 首条记录(主机) = +0.367 / +0.409 / +0.392 / +0.409 s`，首条连接自身时长 12–19 ms（ledger ticks = ns），故 VM **快约 0.38–0.43 s**。
- 结论：主机比 VM 慢 < 0.7 s，窗口边缘最多移动 1 s，而臂间隔 ≥ 42 s；不影响任何判定。

---

## 6. AC1–AC5 逐项判定

- **AC1（drain-only，halfClose=100）—— 通过。** `observed.clean == connectAttempts == 601`、`observed.timeout == 0`。同时回答了 design.md §3 的使能假设：`closesocket` 之后的真实 FIN 能被客户端 ACK，drain 每次都在 **0–1 ms** 内看到该 ACK 并退出，不需要「把 socket 句柄交给 drain」的兜底方案。
- **AC2（drain-only，clean=100）—— 通过。** 同样 601/601 clean、0 timeout，drain 601/601 acknowledged（max 2 ms）。
- **AC3（四模式 REL）—— 通过。** `timeout 0`；形状与父任务参考臂一致：`unexpectedEof 590`（参考 586；= partialFin 300 + resetAfterN 290，后者是 sing-box 把上游 RST 洗成 FIN 的既有行为，任务 Out of Scope 已记录）、`reset 0`（同因）、`clean 607`（参考 592）。唯一残余是 **4 例 `otherError`（4/1201 = 0.33 %），全部落在 `resetAfterN` 子模式**，机制证据见 §9.2；这 4 例不是 timeout，也不是 close 路径失败。
- **AC4（A/B：手工 FIN 冗余）—— 判定「等价，冗余，可以删除」。** 两臂用同一 plan、相邻运行、同一靶机同一 sing-box 实例：`hh-nofin` clean 601/601、timeout 0、drain 601/601 acknowledged；`hh-fin` clean 601/601、timeout 0、drain 601/601 acknowledged，且 `Client-visible close injected` 601 次全部落地。手工 FIN 没有带来任何可观测差异（drain 退出原因分布、p99、max 都在同一天然噪声内：0–1 ms vs 0–2 ms），而它引入了 design.md §1 的 overtake 风险。**结论：removal 放行。**
- **AC5（drain 成本）—— 通过，见 §7。** 3004 次 drain 全部 `acknowledged`，p50 0 ms、p99 0 ms、max 2 ms、峰值并发 1；drain 不持有额外端口，只延长既有 session slot 与表项。

---

## 7. AC5：drain 事件的统计与预算口径

数据来源：四臂的 Debug 产品日志（`tcp.redirect.drain`，`TcpRedirectLog.cs:21`），事件字段 `Outcome` / `ElapsedMs` / `TcpAssociation` / 端点。`ElapsedMs` 量的是 **arm 到退出**的等待（不含前面的 relay dispose）。

| 臂 | drain 事件 | acknowledged | deadline | retired | ElapsedMs p50 / p99 / max | 总持有 | 峰值并发（事件时间 − ElapsedMs 估算） |
| --- | --- | --- | --- | --- | --- | --- | --- |
| hh-nofin | 601 | 601 | 0 | 0 | 0 / 0 / **1 ms** | 1 ms | 1 |
| hh-fin | 601 | 601 | 0 | 0 | 0 / 0 / **2 ms** | 2 ms | 1 |
| cl-nofin | 601 | 601 | 0 | 0 | 0 / 0 / **2 ms** | 2 ms | 1 |
| rel-nofin | 1201 | 1201 | 0 | 0 | 0 / 0 / **1 ms** | 1 ms | 1 |
| **合计** | **3004** | **3004（100 %）** | **0** | **0** | **0 / 0 / 2 ms** | 6 ms | **1** |

口径说明：

- drain 事件数 = 各臂 clean end 数（601+601+601+1201 = 3004），与 `tcp.relay.ended` 的 `cleanEnded` 计数逐一相符：每个 clean end 进入且只进入一次 drain。
- 并发峰值用每个事件的 `[事件时间 − ElapsedMs, 事件时间]` 区间做扫描线求得，全程为 1——20 conn/s 的到达间隔（50 ms）远大于 drain 的 0–2 ms 退出时间。
- **没有任何 `deadline` 或 `retired` 退出**：本拓扑上 FIN→ACK 往返在同一台 VM 内是亚毫秒级，drain 在 arm 时 ACK 往往已在 tracker 里（R4 的「先检查、已覆盖则立即退出」路径），5 s deadline 从未被用到。
- **端口 / 容量预算**：drain 不新建 socket、不新建连接、不申请新端口。它持有的是**已经属于该 session 的两样东西**：(a) 一个 session slot（`TcpRedirectOptions.Capacity` 默认 **16,384** 个并发代理流）；(b) 该 session 的本地监听端口（`TcpRedirectListenerFactory` 以 `0.0.0.0:0` 绑定，由 OS 取动态端口，`TcpRedirectTable._candidatePorts[port]` 计数）与表项/反向索引 claim。客户端面向那条连接的本地端口属于 MSTCP 的 TCB，两种设计下都一样。drain 只是把这份持有**延长 ElapsedMs**：实测四臂合计 6 ms、峰值 1。
- 最坏情况的界：若每个 clean end 都跑满 deadline，并发 drain ≤ `deadline 5 s × clean-end 速率 20/s = 100`，即 ≤100/16,384 ≈ **0.6 %** 的 session slot 与动态端口，且只在「FIN 及其全部重传都丢失」的极端情形才出现。

---

## 8. A/B 对比明细（AC4）

| 项 | hh-nofin（drain-only） | hh-fin（drain+FIN） |
| --- | --- | --- |
| 变体 sha256 | `8b894ac7…` | `75cb5940…` |
| planHash | `0f215d26f7c0b8b6` | `0f215d26f7c0b8b6`（同一 plan 文件） |
| connectAttempts / clean / timeout | 601 / **601** / **0** | 601 / **601** / **0** |
| `Client-visible close injected` | **0** | **601（injected）** |
| `Client reset injected` | 0 | 0 |
| drain 退出 | 601 acknowledged | 601 acknowledged |
| ElapsedMs p50 / p99 / max | 0 / 0 / 1 ms | 0 / 0 / 2 ms |
| 靶机账本 | 601 halfClose / verdict halfClose | 601 halfClose / verdict halfClose |

结论：**两臂等价，手工 clean-end FIN 是冗余的**（它在 fin 臂确实每次都执行了，但没有改变任何客户端可见结果）。按 prd 的 AC4 决策规则，removal 可以进入 Phase C3。

机制旁证：fin 臂 `Client-visible close injected` 601 次说明注入路径工作正常，而两臂的 drain 退出原因、时长分布与残余（都是 0）完全一致——「drain 释出 relay → MSTCP 发真实 FIN → 等客户端 ACK」这条链路本身就足以让 close 落地，手工 FIN 叠加在它之上不产生可测差异。

---

## 9. 异常、残余与不确定性

### 9.1 现场纪律的两处偏差（如实记录）

1. **看门狗未在实验期间禁用。** 批量脚本里 `fw off` 因 PowerShell 5.1 的 GpoBoolean 转换问题抛出语句终止错误，导致同一批的 `wd off` 未执行；修好 `fw off` 后的后续批次**遗漏了 `wd off`**。事后核对：`wfbench-watchdog` 全程 Enabled（State Ready，每分钟检查一次），dead-man 阈值是心跳陈旧 1500 s，而心跳最后写入为 10:15:04Z，故最早触发时刻是 **10:40:04Z**；最后一臂 10:37:00Z 结束、产品进程在数秒内停止、10:37:43Z 防火墙已恢复——看门狗实际在 **10:41:02Z** 触发（日志：`heartbeat stale by 1,558s -> emergency teardown`，10:41:04Z `teardown complete`，同时停掉 ProxiFyreService/ProxifierService 并重写心跳），**落在所有测量窗口之外**。四臂的产品/靶机/sing-box 记录均完整（601/601/601/1201），无任何一臂被截断，数据不受影响；但这是流程上的实质风险，已记入本报告。
2. **drain-only 冻结快照原样不能编译**（§2.2），我在 `/tmp/wf-drain-only` 内做了最小修正并重新发布；修正语义等价于原 patch，diff 已入库。主工作树未做任何写入（`git status` 与 provenance 逐行一致，`TcpRedirectAcceptor.cs` mtime 仍是 18:15）。

### 9.2 AC3 的 4 例 `otherError`（0.33 %）—— 有证据的残余

- 形状：全部 `mode=resetAfterN`、`expected=reset`、`observed=otherError`、`reset=false`、`eof=false`、`protocolError=true`，`echoedBytes = 2048 / 5120 / 2048 / 5120`（典型情况读满 8192）。
- 客户端判定链：`FrameReadStatus.Truncated`（「对端在一帧中间关闭」）→ `protocolError=true` → `Classify` 落到 `OtherError`（`ReliabilityExchange.cs:146-152, 193-196`）。不是 timeout（timeout 要求 `eof=false ∧ ¬protocolError`）。
- 靶机侧同一连接的账本：`mode=resetAfterN`、`verdict=reset`、`bytesEchoed=8192`（四条都是）；该臂 300 条 resetAfterN **全部**记录 `bytesEchoed=8192`。
- 机制读法（与任务 Out of Scope 的「sing-box 洗掉上游 RST」一致）：resetAfterN 的语义是 target 写完响应用零 linger abort，**abort 会丢弃仍在发送缓冲里的尾部数据**；sing-box 把 RST 洗成 FIN，客户端于是在「尾部被截断」的位置看到优雅 EOF。大多数同伴（290/300）表现为 `unexpectedEof`（在帧边界前看到 EOF，echoed 0/7168/3072 不等），这 4 例刚好切在一帧中间。产品侧无异常：这 4 条连接的产品日志与其他 1197 条同形（relay cleanEnded、drain acknowledged、无 clientReset 注入、无 deadline），全臂 Warning 仅 3 条且都与 close/drain 路径无关。
- 定性：**harness / 上游 RST 保真度问题**（`resetAfterN` 子模式，任务明确 out of scope），不是 close drain 的回归；不构成 AC3 的 timeout 残余。参考臂对应位置是 23 个 timeout 与 0 个 otherError，本次 0 timeout、4 otherError，差异属于同一子模式的两种表现，未观察到 close 路径失败。

### 9.3 其它不确定性与未决点

- **deadline 路径在本次实验中完全没有被触发**（0 次 `deadline`、0 次 `retired`）。AC0 观察到的 92 例「close 从未落地」与 10 例「整段响应未到」在本轮**一次都没有复现**（两侧合计 3004 次 clean end 全部在 0–2 ms 内 acknowledged）。因此本报告能证明「有 drain 时不再出现残余」，但给不出 drain 在**真实丢包**下等一个 RTO（~3 s）的实测分布；AC5 的时长分布是「本拓扑无丢包」的形状（p50 = 0），不是 deadline 边界的行为证据。
- **AC3 的 `unexpectedEof` 口径**：任务书写「≈300」是 partialFin 单模式的期望，resetAfterN 的 290 例因 RST→FIN 洗白同样计入 unexpectedEof；父任务参考臂 `fix3-rel` 的 unexpectedEof 就是 586，本臂 590 与该形状一致。
- **账本窗口边缘**：原始窗口圈出 590/590/590/1190（前缘时钟差），偏移修正后 601/598/601/1198（尾部若干条在臂结束之后落盘）。块的独立性（§5.2）是归属判据，窗口只作交叉核对。
- **sing-box 日志秒级时间戳**：窗口内计数 593/589/601/1186 与「±2 s」计数 601/601/601/1201 的差全部来自 1 s 粒度；每个臂 ±2 s 计数与 `connectAttempts` 精确相等。
- **产品日志时间戳带 `-07:00` 偏移**（VM 本地时区），报告中所有时间已换算成 UTC。
- **靶机实例**：账本副本复制完成后，按 pid（128962）停止了长期靶机实例，端口 40020/40053 已释放；因此 `ledger.jsonl` 副本里没有停机时才会写的汇总记录（见表头说明）。重跑所需的命令见 §3。
- **看门狗 10:41 触发的副作用**：按设计停掉了 ProxiFyreService / ProxifierService（Manual 启动，不属于本次实验），并把心跳重写到 10:41:04Z；`C:/wfbench` 在我方实验期间（10:21–10:37）没有被写入（心跳 mtime 直到 10:41:04 都还是 10:15:04）。

---

## 10. 原始证据清单

根目录：`.trellis/tasks/10-09-tcp-close-drain-primary/research/verification/drain/`

| 文件 | 内容 | sha256 |
| --- | --- | --- |
| `hh-nofin/REL.jsonl` | AC1 客户端记录 | `b2623fb2e89f0798d8c0142e39c4ab07da59a1632ddfab5f3f6b73b41c9caf6e` |
| `hh-nofin/run.json` | AC1 窗口/planHash | `b06224c4e6bd49d3e8d73598893b2b3347cf891f9cf67c4a4b23ee9e68966f35` |
| `hh-nofin/wf-debug.err.jsonl` | AC1 产品 Debug 日志（4219 行，含 drain 事件） | `604e441105766c67464d1066e53d7029390d20ae10ba1fed7ca657bbd9b0eb84` |
| `hh-fin/REL.jsonl` | AC4-A 客户端记录 | `ed95e79085bf1131ee50fa9073c16930c7ed68f31feebe6bac3a2c18564ff7a7` |
| `hh-fin/run.json` | AC4-A 窗口 | `1124133f2fff6460535adb361ad0c6d34f3321fccc956d7ba167c73e1874fd6f` |
| `hh-fin/wf-debug.err.jsonl` | AC4-A 产品日志（4821 行） | `738273a67681dd444c5285a00cf37d27eb9ba2472786ec30abb21f1a461b98af` |
| `cl-nofin/REL.jsonl` | AC2 客户端记录 | `94bc8f3493a2ccd94ec0e531bc7202c5b3a710b715fa1ce85ae09a8d0cd6bacf` |
| `cl-nofin/run.json` | AC2 窗口 | `40e00e1458895c8d328589cb16a377ff6db7089f8d7af641373ec85f8c306c20` |
| `cl-nofin/wf-debug.err.jsonl` | AC2 产品日志（4220 行） | `f3fc065471616bd620858615ffd6b535014695dc0d6a3672dc8553d4c693ae5d` |
| `rel-nofin/REL.jsonl` | AC3 客户端记录 | `c2bcd133ac7d4969984b7ed1da62aff3b28a9405a2d32cfc5710e9cd192c11e1` |
| `rel-nofin/run.json` | AC3 窗口 | `ded347348bcd99d5befa6c79f205adfc684d2ab40862e1df2a692bf179e91ee2` |
| `rel-nofin/wf-debug.err.jsonl` | AC3 产品日志（8424 行） | `84d7c5a3e5cca49e8096372e0679c16d5ec43b4dcd2345075b3a11ac10678a0e` |
| `ledger.jsonl` | 靶机全量账本（3733 行：tcp 3004 + udpSummary 729；汇总记录在靶机停止时才写，本文件没有） | `87cd5000aa1dc639c1cca9fd79a8dea82afb04c38df5196b114191b889728a54` |
| `singbox.log` | sing-box 日志（四臂 + AC0 遗留，4206 条 inbound） | `4c3c5ba7c0a57c4bf02ec75aa4711c2caf11aa7b7093fc679edca168cb8c962f` |
| `target.out` | 靶机 stdout | `6a53856fd81a39661501e1ac9c77e5b6ff814a30a48ad1ef938db2d52a76e32e` |
| `analysis.txt` | 四臂完整逐项分析输出（含账本窗口与 sing-box 计数；偏移取 -400 ms 的那一版） | `313043f8e0913ba4d1470e708f87104e6d4598bd943f3c787de93ebcaa81310a` |
| `drive.ps1` | VM 侧驱动脚本原文 | `d85dcab73fab106c60822b45137c6c821a6d69ac4116c69059009f9844271712` |
| `plan-p-hh.json` / `plan-p-cl.json` / `plan-p-rel.json` | 三个 plan 原文 | `0f215d26f7c0b8b68cc8db0608f8aff39d263f3aad8d2c77f06165ae54b0bbd2` / `fd58a2d9e3b2fb95f6bfeca5f2dbe5b2aae9b643f31f77826f78960447936b90` / `91185995e3037e773fab774e94467b0879d27607fdd90a2dc671a4cad6628d48` |
| `variant-diff-acceptor.diff` | fin vs nofin 的 `TcpRedirectAcceptor.cs` 完整 diff（含 §2.2 编译修正） | `6b3c791b2b0d55aeb9c3f1f716834e64143e09d47c69d5b374ff9b33b52e00c0` |

实验对象（`/tmp` 冻结快照与产物，供复算）：

| 文件 | sha256 |
| --- | --- |
| `/tmp/wf-drain-pub/fin/WinForward.exe` | `75cb5940a3bce572d510545dc25d8a300fde7bcd0f33b469dfb73ff8c65845c2` |
| `/tmp/wf-drain-pub/nofin/WinForward.exe` | `8b894ac71305be383d971f48cd5effa5ddde6ffb012147fa76f464c8be18e27a` |
| `/tmp/wf-drain-snapshot/.../TcpRedirectAcceptor.cs` | `ee129010e248bb71c1f963a7ad4d56034d13576aea875f80e3027eaa5196a77a`（= 主工作树，未改） |
| `/tmp/wf-drain-only/.../TcpRedirectAcceptor.cs` | `48de2252ec9d98f709b454d1d802b9363ec9dc84586e5b4632ab703a4d414603` |
| `/tmp/wf-drain-local/stage-d.zip` | `9776ae6be278e1cd82af65e1b6448eb03aed64c69cee9c5a92519ec26d6b22e5` |
| harness `/tmp/wf-ac0-pub/...`（复用 AC0） | `WinForward.E2E(linux) 35802188…` / `WinForward.E2E.exe 307e3196…` / `.dll 93200c77…`，与 AC0 §2 一致 |

主工作树在实验前后 `git status --short` 与 `/tmp/wf-drain-snapshot-provenance.txt` 逐行一致，`HEAD = 0aa6919`，未被本次实验改动。

---

## 11. Final certification（最终 revision 认证臂）

Phase D 认证的是 drain-only **变体快照**（`/tmp/wf-drain-only`，行为等价但不是最终形态）。本节认证**将要提交的最终代码树**：`/tmp/wf-final-snapshot`。

### 11.1 认证对象与部署

- 最终树已删除全部手工 clean-end close 符号：`TcpRedirectAcceptor` 里 clean end 只 drain，abnormal end 才调用 `InjectAbnormalEndResetAsync`；`TryInjectClientCloseAsync` / `TryBuildFin` / `tcp.redirect.clientClose` / `TcpRedirectRelayEndCloseFailed` 在 `src/` 与 `tests/` 中 **grep 无命中**（0 处）。
- 构建（与 Phase D 同配方）：`cd /tmp/wf-final-snapshot && dotnet publish src/WinForward.Cli/WinForward.Cli.csproj -c Release -r win-x64 -p:PublishAot=false --self-contained false -o /tmp/wf-final-pub` → `FINAL_PUBLISH_EXIT=0`，`/tmp/wf-final-pub/WinForward.exe` sha256 = `f16ffaa98a0b04031f01c9310e80ed034998aefa5bac516b1c12ee8890e9705a`（2,010,878 B）。
- 部署：`stage-final.zip`（sha256 `11eee62c411dc6d6190ca00161164c20e57abaf705c21e78431185f8762b7791`）上传 + `Expand-Archive -Force` 到 `C:/wf-ac0`；`drive.ps1 variant final` 覆盖 `wf-fdd/WinForward.exe` 后 VM 侧复验 hash = `f16ffaa9…`（与本地一致）。`ndisapi.dll` 与 `appsettings*.json` 未动。
- harness 复用 Phase D 的 `/tmp/wf-ac0-pub`（`WinForward.E2E.exe 307e3196…` / `.dll 93200c77…`）。

### 11.2 现场处置（本次特别核对）

- **看门狗：跑臂前执行 `Disable-ScheduledTask -TaskName wfbench-watchdog`，`(Get-ScheduledTask).State` 复核 = `Disabled`**；跑臂全程保持 Disabled（跑臂后的 status 复查仍为 Disabled）；收尾 `Enable-ScheduledTask` 还原，State 复核 = `Ready`。
- 防火墙：跑臂前 `Domain/Private/Public = False`（status 复核三档全 False）；收尾恢复 `True/True/True`（status 复核）。
- 进程：sing-box pid 2356（`127.0.0.1:1080` 已复验）、WinForward pid 3088（`run --config C:/wf-ac0/wf-fdd/appsettings-debug.json`）；收尾均按 pid 停止，status 复核 `wf=0 sb=0`、无残留进程。
- 时钟：成对采样 VM 快 `+0.679 s`（10:57:30.612 / 10:57:31.2907462）、`+0.677 s`（10:58:02.197 / 10:58:02.8734828）。

### 11.3 臂与结果

- 命令：`& C:/wf-ac0/drive.ps1 client fin-cert p-hh.json out-fin-cert`，展开为 `C:/wf-ac0/e2e/WinForward.E2E.exe client --target 192.168.100.4 --plan C:/wf-ac0/plans/p-hh.json --out C:/wf-ac0/out-fin-cert --label fin-cert --tcp-port 40020 --udp-port 40020 --dns-port 40053`；plan = Phase D 的 `p-hh.json`（`halfClose=100`，30 s @ 20，planHash `0f215d26f7c0b8b6`）；靶机沿用 `--label drain`（新账本 `/tmp/wf-drain-run/ledger-fin-cert.jsonl`）。
- 窗口：`2026-10-09T10:57:32.103442Z → 2026-10-09T10:58:02.242830Z`（30.134 s），`CLIENT_EXIT=0`。
- 指标：`connectAttempts = scheduledAttempts = 601`；`attemptRecords 37 / omitted 0`；`gates.clientSendLoss = 0`；`achievedRate 20.019`；`observed = { clean: 601, reset: 0, unexpectedEof: 0, timeout: 0, connectFail: 0, halfCloseViolation: 0, otherError: 0 }`；`truncated 0`；`fidelityMismatch 0`；`echoedBytes 4,923,392 / trailerBytes 461,568`；37 条样本全为 `(halfClose, clean, clean, 8192, eof=true)`。
- drain：**601 事件 = 601 连接 = 601 cleanEnded**，全部 `acknowledged`；ElapsedMs min 0 / p50 0 / p90 0 / p99 0 / **max 2 ms**，总持有 3 ms；峰值并发 **1**。
- **被删事件核对（逐串 grep Debug 日志，全部 0 次）**：`Client-visible close injected for` 0、`clientClose` / `tcp.redirect.clientClose` 0、`close injected` 0、`TryInjectClientClose` 0、`RelayEndCloseFailed` 0。注意：子串 `client-visible close` 在日志里出现 **601** 次，全部来自 drain 事件自己的报文模板（`The client-visible close for … ended as {Outcome} after {ElapsedMs} ms of drain.`），不是被删事件——按整句/事件名核对可以区分。
- 有效性：靶机账本窗口片段 `ledger-window.jsonl` **601 条，全部 `halfClose/halfClose`**；账本总表 601 条 tcp；sing-box 入站窗口内 583 / ±2 s 601；产品日志 4249 行（Debug 4237、Information 9、Warning 3：配置警告 1、进程归属 1、read-shape 1，均与 close/drain 路径无关）。

### 11.4 判定

**通过。** `observed.clean == connectAttempts == 601`、`observed.timeout == 0`、`truncated == 0`；`tcp.redirect.drain` 事件数 601 == 连接数 601 且全部 `acknowledged`；被删除的 close 注入事件与日志符号 **0 次残留**。与 Phase D 的 drain-only 臂（`hh-nofin`）逐项同形：同为 clean 601/601、timeout 0、drain 601 acknowledged、`Client-visible close injected` 0。

### 11.5 原始数据（`research/verification/drain-final/`）

| 文件 | 内容 | sha256 |
| --- | --- | --- |
| `REL.jsonl` | 认证臂客户端记录 | `87f520a182cdfa0aa667206d8f9628065f61e168c696e0f0d1ead96ab9aa0c14` |
| `run.json` | 窗口 / planHash | `8c23b836453e0081e62e95f7a48e2705e008c8d7033b3a156d5ff3e57e1eed19` |
| `wf-debug.err.jsonl` | 产品 Debug 日志（4249 行，含 601 条 drain 事件） | `9865303a29606661fa3bb13f8fec268ec3559d3117388da2beb5a0eac3b72ff7` |
| `ledger.jsonl` | 靶机账本全量（723 行：tcp 601 + udpSummary 122） | `1776c86224178b99cd6945edf8733c0ef503d86e466bc3d57b0296aa8a8410ee` |
| `ledger-window.jsonl` | 靶机账本窗口片段（601 条，全部 halfClose/halfClose） | `f76924d79936dfdad00335b47b10d9205dc9a186d3f9a95e1ea00ccd06252979` |
| `singbox.log` | sing-box 日志（含本臂 601 条 inbound） | `ea5f756cc3b4b81d4f352d480e33e1dc393f0017f6c3bb3ba297130d9a4f6bdc` |
| `target.out` | 靶机 stdout | `8c0414311ebdf9a33da0a4aadd2bbbd111cb3c138588d37df1e471d75b595983` |

实验对象：`/tmp/wf-final-pub/WinForward.exe` = `f16ffaa98a0b04031f01c9310e80ed034998aefa5bac516b1c12ee8890e9705a`；`/tmp/wf-final-local/stage-final.zip` = `11eee62c…`。主工作树未被本认证臂改动。
