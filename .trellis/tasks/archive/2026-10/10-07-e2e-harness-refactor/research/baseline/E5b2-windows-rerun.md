# E5-b2：Windows 复跑（AC18 三态复判，2026-10-08）

> 承接 `E5b-windows.md`（E5-b）与 `tickets.md` 的 T1/T2/T7。目标：在 T1（账本按靶机实例归属）
> 与 T2（普查槽位按区间回收）已修、且跑前给 VM 对时（T7）的条件下重跑一轮 Windows campaign，
> 让 AC18 的判据 4（findings 白名单外为空）从上一轮的 `fail` 变成**可判定的**结果。

## 0. 结论摘要

**AC18 = `fail`，但失败的「性质」变了：白名单外的 42 条 findings 全部有归因（0 条未归因），
T1/T2 造成的 findings 全数清零，T7 的钟差从约 3 s 压到 ≤0.14 s。**

| 判据 | E5-b（上一轮） | E5-b2（本轮） |
| --- | --- | --- |
| 1 client 退出码 0 | ✓（全部推得） | ✓（**30/57 个 run 有直接记录的 `exit=0`**，其余 27 个仍靠代码等价 + 无失败记录） |
| 2 无 `error` 记录 | ✓（0 条） | ✓（261 条 `result`，**0 条 `error`**） |
| 3 `run.json.failed == false` | ✓（18 run） | ✓（**57 个 run.json 全部 `failed=false`，无 `arms[].failed`**） |
| 4 findings 白名单外为空 | ✗（320 条） | **✗（42 条，但逐条有归属）** |
| 5 分析器产出 `tables.md`/`verdict.json` | ✓ | ✓（exit 0；209532 / 478752 字节） |

findings 总数 **326 → 52**（−84 %），白名单外 **320 → 42**（−87 %）。这 42 条的归属：

| 族 | 条数 | 归属 |
| --- | --- | --- |
| `correctness-failure` / `foreign-connection`（proxybridge MIX，三 pass 各一） | 3 | **竞品侧**（ProxyBridge 把 UDP 数据报送进别的流） |
| `path-interference` / `direct-lane-latency` | 3 | **已披露口径 + 未定的跨行负载混杂**（参考行 proxifier 不承载 UDP） |
| `measurement-caveat` / `ledger-connection-mismatch` | 35 | **20 条 = T7 残留钟差**（拟合 δ 逐 pass 单调增大）**× 分析器已披露的「按关闭时刻归属」窗口规则**；15 条 = **proxybridge 行**（延迟臂只有 25–40 % 的客户端 connect 在靶机上留下连接记录，而客户端自报 0 失败） |
| `measurement-caveat` / `ledger-datagram-mismatch` | 1 | 同一族：δ=0 时差 62 超出 ±60 的带，**δ=+0.02 起落回带内（差 20）** |

**没有被归因的 findings：0 条。** 也就是说：AC18 的判据 4 按机械白名单仍然不满足，但本轮**不存在
「不知道从哪来的」finding**，也没有一条指向被测链路（wf-aot / wf-fdd）的正确性。

其余要点：

- **T1 复跑验证**：`ledger-endpoint-overlap` 10 → **0**、`ledger-window-ambiguous` 60 → **0**；
  账本 20662 + 10961 条记录的 `label` **全部**是 `target:40010` / `target:40011`（§5.1）。
- **T2 复跑验证**：`ledger-source-overflow` 64 → **0**；整轮 8723×2 个 `udpSummary` 区间里
  `sourceOverflow` **恒为 0**（§5.2）。
- **T7 复跑验证**：跑前把 VM 从 **+3.0 s** 校到 **−0.01 s**，跑完 **+0.14 s**（漂移 0.054 s/h）；
  三段式的三次探针**全部落在主机窗口内**（§1.2）。`w32tm /resync` 在本机拓扑下**无效**——它的时间源
  （Hyper-V IC 提供者）就是那个约 3 s 偏移本身。
- 三个 pass 各 9 行、5 个 dual 相位的两条车道各有独立 `run.json`（30 个车道 run），
  `directLeak` 全 0；`=== no failures ===`；`orch-e5b2.err` 0 字节。

## 1. 前置检查

### 1.1 `start-targets.sh` 确实按实例传 `--label`

T1 的修法分两半：分析器入库（`WinForward.E2E.Analysis/Findings/LedgerViews.cs`），启动器是**本机胶水、
不入库**。先确认本机那份真的带了标签（`git check-ignore` 证实它被排除）：

```bash
$ git check-ignore -v benchmarks/WinForward.E2E/scripts/start-targets.sh
benchmarks/WinForward.E2E/.gitignore:6:scripts/start-targets.sh	benchmarks/WinForward.E2E/scripts/start-targets.sh

$ rg -n -- '--label' benchmarks/WinForward.E2E/scripts/start-targets.sh
28:    --label target:40010 --ledger "$ledgers/ledger-main.jsonl" &
33:    --label target:40011 --ledger "$ledgers/ledger-direct.jsonl" &
```

两个实例的**实际命令行**（`pgrep -af`，起靶机之后）：

```bash
$ pgrep -af 'WinForward.E2E target'
3427001 /tmp/wf-bench/pub/linux/WinForward.E2E target --bind 192.168.100.4 --tcp-port 40010 --udp-port 40010 --dns-port 53 --dns-alt-port 40053 --label target:40010 --ledger /tmp/wf-bench/ledger-main.jsonl
3427002 /tmp/wf-bench/pub/linux/WinForward.E2E target --bind 192.168.100.4 --tcp-port 40011 --udp-port 40011 --dns-port 40054 --label target:40011 --ledger /tmp/wf-bench/ledger-direct.jsonl

$ cat /tmp/wf-bench/e5b2-targets.log
e2e target listening on tcp 192.168.100.4:40010, udp 192.168.100.4:40010, dns 192.168.100.4:53 (label 'target:40010', ledger /tmp/wf-bench/ledger-main.jsonl)
e2e target listening on tcp 192.168.100.4:40011, udp 192.168.100.4:40011, dns 192.168.100.4:53 (label 'target:40011', ledger /tmp/wf-bench/ledger-direct.jsonl)
e2e target additional dns listener on 192.168.100.4:40053
proxied target  192.168.100.4:40010 udp/40010 dns/53 dns-alt/40053  -> /tmp/wf-bench/ledger-main.jsonl
direct target   192.168.100.4:40011 udp/40011 dns/40054             -> /tmp/wf-bench/ledger-direct.jsonl
both up; Ctrl-C to stop
```

### 1.2 VM 对时（T7）

上一轮实测 VM 比主机快 2.5–3.4 s，本轮**跑前先测**。用的是 T1 报告 §5 的三段式
（主机 `date -u` → VM `(Get-Date).ToUniversalTime()` → 主机 `date -u`），连测 3 次：

```bash
$ for i in 1 2 3; do
    b=$(date -u +'%Y-%m-%dT%H:%M:%S.%N'); bt=$(date +%s.%N)
    v=$(scripts/wf.sh run '(Get-Date).ToUniversalTime().ToString("o")' 90 | ... )
    at=$(date +%s.%N); a=$(date -u +'%Y-%m-%dT%H:%M:%S.%N')
    echo "probe $i: hostBefore=$b vmUtc=$v hostAfter=$a wallSeconds=..."
  done
probe 1: hostBefore=2026-10-08T14:59:41.655202781 vmUtc=2026-10-08T14:59:44.9885480Z hostAfter=2026-10-08T14:59:42.296705030 wallSeconds=0.636
probe 2: hostBefore=2026-10-08T14:59:42.304146436 vmUtc=2026-10-08T14:59:45.6672801Z hostAfter=2026-10-08T14:59:42.949576513 wallSeconds=0.640
probe 3: hostBefore=2026-10-08T14:59:42.956234245 vmUtc=2026-10-08T14:59:46.2897590Z hostAfter=2026-10-08T14:59:43.599465778 wallSeconds=0.638
```

VM 自报时刻落在主机窗口**之后** 2.69–3.33 s ⇒ 与上一轮同源、同量级，**不满足 ≤0.2 s**。

**`w32tm` 那条路走不通**（这一段值得记下来，因为它同时解释了 T7 的成因）：

```bash
$ scripts/wf.sh run 'w32tm /query /status'
The following error occurred: The service has not been started. (0x80070426)
$ scripts/wf.sh run '(Get-Service w32time).Status.ToString() + " / " + (Get-Service w32time).StartType.ToString()'
Stopped / Manual

$ scripts/wf.sh run 'Start-Service w32time; w32tm /resync /force'
started
Sending resync command to local computer
The command completed successfully.

$ scripts/wf.sh run 'w32tm /query /source'
VM IC Time Synchronization Provider
$ scripts/wf.sh run 'w32tm /query /status | Select-String "Source|Last Successful|Poll Interval|Stratum"'
Stratum: 1 (primary reference - syncd by radio clock)
ReferenceId: 0x564D5450 (source name:  "VMTP")
Last Successful Sync Time: 10/8/2026 7:59:59 AM
Source: VM IC Time Synchronization Provider
Poll Interval: 10 (1024s)
```

resync 报成功，但**时钟没被搬动**——resync 之后再测三段式，偏移仍是 +3.3 s：

```bash
probe 1: hostBefore=2026-10-08T15:00:00.846133378 vmUtc=2026-10-08T15:00:04.1778959Z hostAfter=2026-10-08T15:00:01.485976985 wallSeconds=0.634
probe 2: hostBefore=2026-10-08T15:00:01.493176476 vmUtc=2026-10-08T15:00:04.8266062Z hostAfter=2026-10-08T15:00:02.134322109 wallSeconds=0.635
probe 3: hostBefore=2026-10-08T15:00:02.141136425 vmUtc=2026-10-08T15:00:05.4804668Z hostAfter=2026-10-08T15:00:02.784181446 wallSeconds=0.637
```

也就是说：**VM 的时间源（Hyper-V IC 提供者，stratum 1 = 宿主）本身就比宿主 `date -u` 快约 3.3 s**，
`w32tm /resync` 只是把时钟"同步"到那个偏移上。这解释了 T7 的稳定性（上一轮 12:23 与本轮 14:59
测到的偏移是同一个值）。

于是改走 `Set-Date`。**第一次踩了时区的坑**：`Set-Date -Date <Kind=Utc 的 DateTime>` 会把那个
DateTime 的**字面数字当本地时间**用，而 VM 的时区是 `Pacific Standard Time`（夏令时 UTC−7），
于是一次 −3.01 s 的修正变成了 +7 h 的整体平移：

```bash
$ scripts/wf.sh run '(Get-TimeZone).Id + " base=" + [System.TimeZoneInfo]::Local.BaseUtcOffset + " utcNow=" + (Get-Date).ToUniversalTime().ToString("o")'
Pacific Standard Time base=-08:00:00 localNow=2026-10-08T08:00:39.9392910-07:00 utcNow=2026-10-08T15:00:39.9392910Z
```

正确的写法是**把主机 UTC 显式转成 Kind=Local 再交给 `Set-Date`**（或干脆用 VM 自己的
`(Get-Date).AddSeconds(-x)` 做本地算术）：

```bash
$ HS=$(date -u -d '+0.35 seconds' +'%Y-%m-%dT%H:%M:%S.%6NZ'); echo "host target = $HS"
host target = 2026-10-08T15:01:05.252392Z
$ scripts/wf.sh run '$t=[datetime]::Parse(<HS>,InvariantCulture,RoundtripKind);
    $l=[datetime]::SpecifyKind($t,[System.DateTimeKind]::Utc).ToLocalTime(); Set-Date -Date $l; ...'
set targetUtc=2026-10-08T15:01:05.2523920Z localKind=2026-10-08T08:01:05.2523920-07:00 utcNow=2026-10-08T15:01:05.2544211Z
```

**精测**：三段式的窗口宽度 = 一次 `wf.sh run` 的往返（约 0.63 s），单次只能给到 ±0.3 s。
为了把门槛（≤0.2 s）判准，另外做了一次**socket 级时间交换**：主机在 `192.168.100.4:40099`
监听，VM 用 `TcpClient` 连上、发自己的 `(Get-Date).ToUniversalTime()`、主机在 `recv()` 返回的
瞬间打时间戳并回送，单次往返 0.2 ms 量级。探针与读数：

```bash
$ python3 /tmp/wf-bench/clock-probe.py 192.168.100.4 40099 5 > /tmp/wf-bench/clock-probe.out &
$ scripts/wf.sh file /tmp/wf-bench/clock-probe.ps1
$ python3 /tmp/wf-bench/clock-offset.py <host.out> <vm.out> <label>   # offset = vmT0 + vmRtt/2 - hostRecv
```

| 轮次（UTC） | vm-host 偏移（3–4 次交换） | 处置 |
| --- | --- | --- |
| 14:59:41–43 | **+2.69 … +3.33 s** | 三段式，判不达标 |
| 15:00:00–02 | +2.69 … +3.34 s | `Start-Service w32time` + `w32tm /resync /force`，**无效** |
| 15:06:39 | **+3.24 s** | w32time 又把时钟搬回了 IC 提供者的值 |
| 15:07:13–14 | +0.194 / +0.207 / +0.207 s | `Stop-Service w32time` + `Set-Date`（+0.35 s 估计偏大） |
| **15:10:38** | **−0.013 / −0.003 / −0.004 / −0.003 s** | `Set-Date` 再扣 0.2 s ⇒ **达标** |
| **15:15:17** | **−0.009 / −0.001 / −0.000 s** | 部署与上传之后复测，**仍在门槛内** |
| 15:19:26（开跑瞬间） | VM `15:19:26.786Z` ∈ 主机窗 `[15:19:26.369, 15:19:27.277]` | 起 campaign |

自 15:10:38 到 15:15:17（4.6 分钟）偏移只动了 **+0.002 s**，折算漂移约 **0.03 s/h** ⇒ 3 小时 campaign
内的自漂移远小于 0.2 s 门槛。**最终残留将在 campaign 跑完后再测一次**（见 §3）。

> 结论：**T7 本轮已处置**——不是靠 `w32tm`（那条路在本机拓扑下无效，因为源就是那个偏移本身），
> 而是「停掉 w32time（恢复它原本的 Stopped/Manual 状态）+ `Set-Date` 到主机 UTC」，并留下
> 0.2 ms 量级的 socket 级复测。代价：campaign 期间 VM 不再有任何时间源，靠自漂移（实测 0.03 s/h）。

### 1.3 上传：`orchestrator.ps1` 与压缩 plan 都按哈希核对

**不信 `deploy-campaign.sh` 的源目录**（T4）：它的 `$deploy=/tmp/wf-bench/deploy` 里那份
`orchestrator.ps1` 是 Oct 6 的旧版（20824 字节，缺 dual 相位"每行一份目录"的修复）。
本轮的处理：先把旧版备份，再用仓库版覆盖暂存目录里那一份（配置与 plan 已核对为逐字节相同，
只有 orchestrator 漂移——所以这是最小改动），然后照常跑部署脚本：

```bash
$ diff -q /tmp/wf-bench/deploy/orchestrator.ps1 scripts/orchestrator.ps1
Files /tmp/wf-bench/deploy/orchestrator.ps1 and scripts/orchestrator.ps1 differ
$ cp -p /tmp/wf-bench/deploy/orchestrator.ps1 /tmp/wf-bench/deploy/orchestrator.ps1.stale-20824
$ cp scripts/orchestrator.ps1 /tmp/wf-bench/deploy/orchestrator.ps1
staged copy == repo copy (21051 bytes)

# 配置与 plan 的逐文件核对（只有 orchestrator 漂移）
$ for f in wf-aot-opt.json wf-aot-nativeudp.json wf-aot-dnsrelay.json wf-fdd-opt.json proxybridge.pbprofile proxifyre-app-config.json; do cmp -s /tmp/wf-bench/deploy/configs/$f scripts/configs/$f && echo "same  $f"; done
same  wf-aot-opt.json / same  wf-aot-nativeudp.json / same  wf-aot-dnsrelay.json / same  wf-fdd-opt.json / same  proxybridge.pbprofile / same  proxifyre-app-config.json
$ for f in full-plan udp-plan dns-plan dual-plan base-plan; do cmp -s /tmp/wf-bench/deploy/e2e/$f.json scripts/plans/$f.json && echo "same  $f"; done
same  full-plan / same  udp-plan / same  dns-plan / same  dual-plan / same  base-plan
```

部署（`scripts/deploy-campaign.sh`，脚本按远端字节数校验）：

```bash
$ bash scripts/deploy-campaign.sh
=== clients ===
  ok   C:\wfbench\e2e\WinForward.E2E.exe          162304 bytes
  ok   C:\wfbench\e2e\WinForward.E2E.dll          354304 bytes
  ok   C:\wfbench\e2e\WinForward.E2E.Contracts.dll 130048 bytes
  ok   C:\wfbench\e2e\WinForward.E2E.deps.json    886 bytes
  ok   C:\wfbench\e2e\WinForward.E2E.runtimeconfig.json 330 bytes
  ok   C:\wfbench\e2e-direct\WinForward.E2E.Direct.exe 162816 bytes
  ok   C:\wfbench\e2e-direct\WinForward.E2E.Direct.dll 354816 bytes
  ok   C:\wfbench\e2e-direct\WinForward.E2E.Contracts.dll 130048 bytes
  ok   C:\wfbench\e2e-direct\WinForward.E2E.Direct.deps.json 907 bytes
  ok   C:\wfbench\e2e-direct\WinForward.E2E.Direct.runtimeconfig.json 330 bytes
=== product configurations ===   (7 个 ok，含 config.json 972 / config-nativeudp.json 973 / config-dnsrelay.json 699 / wf-fdd config.json 972 / bench.pbprofile 909 / proxifyre-app-config.json 408 / bench.ppx 1457)
=== plans ===
  ok   C:\wfbench\e2e\full-plan.json              1549 bytes
  ok   C:\wfbench\e2e\udp-plan.json               544 bytes
  ok   C:\wfbench\e2e\dns-plan.json               319 bytes
  ok   C:\wfbench\e2e\dual-plan.json              485 bytes
  ok   C:\wfbench\e2e\base-plan.json              147 bytes
=== orchestrator ===
  ok   C:\wfbench\orchestrator.ps1                21051 bytes
```

**远端哈希复核**（不靠上传方的成功行）：

```bash
$ scripts/wf.sh run '(Get-FileHash C:\wfbench\orchestrator.ps1 -Algorithm SHA256).Hash.ToLower() + "  " + (Get-Item C:\wfbench\orchestrator.ps1).Length'
077e2bd4a9b9d4f34f846aa94a1bd2383976115686bc0101fbf1f2d85095da21  21051
$ sha256sum scripts/orchestrator.ps1
077e2bd4a9b9d4f34f846aa94a1bd2383976115686bc0101fbf1f2d85095da21  scripts/orchestrator.ps1
```

压缩 plan 与**计划根 `C:\wfbench\e2e-win2`**（本轮新建，不覆盖上一轮的 `e2e-win`）：

```bash
$ scripts/wf.sh up .../scripts/plans-windows/full-shape-plan.json 'C:\wfbench\full-shape-plan.upload'
$ scripts/wf.sh run 'New-Item -ItemType Directory -Force -Path C:\wfbench\e2e-win2 | Out-Null;
    Copy-Item C:\wfbench\full-shape-plan.upload C:\wfbench\e2e-win2\full-plan.json -Force;
    foreach ($f in @("base-plan","dns-plan","dual-plan","udp-plan")) { Copy-Item "C:\wfbench\e2e\$f.json" "C:\wfbench\e2e-win2\$f.json" -Force };
    Get-ChildItem C:\wfbench\e2e-win2 | ForEach-Object { $_.Name + " " + $_.Length + " " + (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower() }'
base-plan.json 147 6c28a3d0ce91a24c4ea984002c871ae75d86f6424134eb6e45c641c80e26e7f3
dns-plan.json 319 4888274b6bfb139ab6c6847fa77fe01a75a002a5d367765b259622c328604be3
dual-plan.json 485 88806d4e91436b73ea1d74bfcac8041a7a73c0abafd6c2dd8e3595a4d21d6651
full-plan.json 1547 ed0a3477b74dfa973227c80e732592076220FDFA54E29AEB69094F805779D92E
udp-plan.json 544 b0292c1887c2f467c51200e4398e77dc47b2207999a0f2ce6ea210b957266c49

$ sha256sum scripts/plans/base-plan.json scripts/plans/dns-plan.json scripts/plans/dual-plan.json scripts/plans/udp-plan.json scripts/plans-windows/full-shape-plan.json
6c28a3d0ce91a24c4ea984002c871ae75d86f6424134eb6e45c641c80e26e7f3  scripts/plans/base-plan.json
4888274b6bfb139ab6c6847fa77fe01a75a002a5d367765b259622c328604be3  scripts/plans/dns-plan.json
88806d4e91436b73ea1d74bfcac8041a7a73c0abafd6c2dd8e3595a4d21d6651  scripts/plans/dual-plan.json
b0292c1887c2f467c51200e4398e77dc47b2207999a0f2ce6ea210b957266c49  scripts/plans/udp-plan.json
ed0a3477b74dfa973227c80e732592076220fdfa54e29aeb69094f805779d92e  scripts/plans-windows/full-shape-plan.json
```

五个文件**逐字节等于仓库版**。

### 1.4 起靶机之前的清理

`start-targets.sh` 会 `rm -f` 两个账本文件，而 `/tmp/wf-bench/` 跨会话共享，所以先把上一轮的
账本原样备份（不删别人的数据）：

```bash
$ ls -l /tmp/wf-bench/ledger-*.jsonl
-rw-r--r-- 1 paff users 1456079 Oct  8 19:27 /tmp/wf-bench/ledger-direct.jsonl
-rw-r--r-- 1 paff users 3266639 Oct  8 19:27 /tmp/wf-bench/ledger-main.jsonl
$ mkdir -p /tmp/wf-bench/archive-before-e5b2 && cp -p ... /tmp/wf-bench/archive-before-e5b2/
$ ls -l /tmp/wf-bench/archive-before-e5b2/
-rw-r--r-- 1 paff users 1456079 Oct  8 19:27 ledger-direct.jsonl
-rw-r--r-- 1 paff users 3266639 Oct  8 19:27 ledger-main.jsonl
-rw-r--r-- 1 paff users 1294477 Oct  6 21:21 target-ledger.jsonl
```

## 2. 命令链与关键输出

### 2.1 `scripts/publish.sh`

```bash
$ bash scripts/publish.sh
=== build and test ===
    0 Error(s)
Time Elapsed 00:00:03.57
=== publish ===
linux        WinForward.E2E WinForward.E2E.Contracts.dll ... 
win          ... WinForward.E2E.exe ...
win-direct   ... WinForward.E2E.Direct.exe ...
publish exit=0
```

### 2.2 起靶机（两个实例，都带 label）

```bash
$ nohup bash scripts/start-targets.sh > /tmp/wf-bench/e5b2-targets.log 2>&1 &
$ ss -ltnup | rg "192.168.100.4:(40010|40011|53|40053|40054)"
udp   UNCONN 0      0      192.168.100.4:53         0.0.0.0:*    users:(("WinForward.E2E",pid=3427001,fd=57))
udp   UNCONN 0      0      192.168.100.4:40010      0.0.0.0:*    users:(("WinForward.E2E",pid=3427001,fd=56))
udp   UNCONN 0      0      192.168.100.4:40011      0.0.0.0:*    users:(("WinForward.E2E",pid=3427002,fd=56))
udp   UNCONN 0      0      192.168.100.4:40053      0.0.0.0:*    users:(("WinForward.E2E",pid=3427001,fd=59))
udp   UNCONN 0      0      192.168.100.4:40054      0.0.0.0:*    users:(("WinForward.E2E",pid=3427002,fd=57))
tcp   LISTEN 0      512    192.168.100.4:54 ... 40010/40011/40053/40054/53 全部 LISTEN
```

### 2.3 启动 orchestrator（pwsh7，`-Passes 3`）

```bash
$ cat /tmp/wf-bench/launch-e5b2.ps1
$a = @('-NoProfile','-ExecutionPolicy','Bypass','-File','C:\wfbench\orchestrator.ps1',
       '-Passes','3','-OutRoot','C:\wfbench\results-e5b2','-PlanRoot','C:\wfbench\e2e-win2')
Set-Content -Path C:\wfbench\heartbeat.txt -Value ((Get-Date).ToString('o'))
$p = Start-Process 'C:\Program Files\PowerShell\7\pwsh.exe' -ArgumentList $a -PassThru -WindowStyle Hidden `
     -RedirectStandardOutput C:\wfbench\orch-e5b2.log -RedirectStandardError C:\wfbench\orch-e5b2.err

$ scripts/wf.sh file /tmp/wf-bench/launch-e5b2.ps1
pwsh7=True
started pid=2760
vmUtc=2026-10-08T15:19:26.7860149Z
```

（`-OutRoot` 与 `-PlanRoot` 的语义见 `orchestrator.ps1` 的注释；`C:\wfbench\e2e-win2` 是本轮新建的
计划根，见 1.3。）

### 2.4 三个 pass 跑完

```bash
$ scripts/wf.sh run 'Get-Content C:\wfbench\orch-e5b2.log | Select-String -Pattern "FAILURES|no failures|pass . done|complete|WARNING"'
2026-10-08 09:09:02   pass 1 done
2026-10-08 10:00:27   pass 2 done
2026-10-08 10:49:51   pass 3 done
2026-10-08 10:50:12 === no failures ===
2026-10-08 10:50:12 === orchestrator complete ===
--- err bytes ---
0
--- leftover procs ---
--- rows pass3 ---
control-post
control-pre
proxifier
proxifyre
proxybridge
wf-aot-dnsrelay
wf-aot-nativeudp
wf-aot-opt
wf-fdd-opt
```

三个 pass 的墙钟：**49m26s / 51m25s / 49m24s**（15:19:36→16:09:02、16:09→17:00:27、17:00→17:49:51 UTC），
每 pass 9 行 + 5 个 dual 相位。跑完时没有任何产品/客户端进程残留（teardown 干净）。

过程中用 `/tmp/wf-bench/e5b2-monitor.log` 每 4 分钟只读地看一眼日志（不打扰 REPL），
`=== no failures ===` 是编排脚本自己的失败计数器为空——即 `Test-ClientRun` 一次都没记过
「没写 run.json」或「run.json failed」。

跑之前的那次看门狗险情记在这里：心跳文件当时已经陈旧 **1504 s**，而 `wfbench-watchdog` 的阈值是
1500 s（上一轮 7.3 已知它其实是启用状态）。启动脚本的第一条命令就是 touch 心跳，随后编排脚本在
每条臂前后各 touch 一次（`orchestrator.ps1:304/356/371/384`），最长间隔是 LOSS 臂的 120 s。

### 2.5 停靶机（按 PID，不用 `pkill`）与账本落账

```bash
$ kill -TERM 3427001 3427002          # 两个靶机实例的 PID，点名
$ ss -ltnup | rg "192.168.100.4:(40010|40011|53|40053|40054)"
none
$ ls -l /tmp/wf-bench/ledger-main.jsonl /tmp/wf-bench/ledger-direct.jsonl
-rw-r--r-- 2409409 /tmp/wf-bench/ledger-direct.jsonl      (10961 行)
-rw-r--r-- 5398952 /tmp/wf-bench/ledger-main.jsonl        (20662 行)
```

账本类型分布（SIGTERM 收尾把汇总记录写全了）：

```text
ledger-main   20662 {'udpSummary': 9532, 'tcp': 11126, 'dnsSummary': 2, 'tcpSummary': 1, 'targetSummary': 1}
ledger-direct 10961 {'udpSummary': 9532, 'tcp': 1426,  'dnsSummary': 1, 'tcpSummary': 1, 'targetSummary': 1}
ledger-main   dnsSummary port 53    udpQueries 115499 / tcpQueries 2538
ledger-main   dnsSummary port 40053 udpQueries 114691 / tcpQueries 2538
ledger-direct dnsSummary port 40054 udpQueries 240    / tcpQueries 0
```

### 2.6 拉回结果

```bash
$ scripts/wf.sh run 'Compress-Archive -Path C:\wfbench\results-e5b2\* -DestinationPath C:\wfbench\campaign-e5b2.zip -Force; ...'
zip bytes: 1379548
orch log bytes: 5467
$ scripts/wf.sh down 'C:\wfbench\campaign-e5b2.zip' /tmp/wf-bench/e5b2-campaign/campaign-e5b2.zip 900
$ scripts/wf.sh down 'C:\wfbench\orch-e5b2.log' /tmp/wf-bench/e5b2-campaign/orch-e5b2.log 900
$ ls -l /tmp/wf-bench/e5b2-campaign/
-rw-r--r-- 1379548 campaign-e5b2.zip      <- 与远端 1379548 一致
-rw-r--r--    5467 orch-e5b2.log          <- 与远端 5467 一致
```

（T5 的 `wf.sh down` 竞态仍在，所以**照上一轮的教训逐字节核对尺寸**：这次两边一次对上。）

解包按 `\` → `/` 归一化（`Compress-Archive` 走 Windows PowerShell 5.1 时条目名用反斜杠）：

```text
entries extracted: 514
raw/pass1..pass3 各 9 行；raw/pass*/**/run.json 共 57 个（27 行 + 30 个 dual 车道）；
raw/**/*.jsonl 共 261 个；environment.json 703 字节
```

`environment.json`：`passes=3`、`seed=20261006`、三个防火墙配置档 `enabled=false`、
`sing-box version 1.14.2-singfix`、`2358 MB visible`。

### 2.7 账本放哪儿（沿用上一轮 2.7 的结论）

两个账本放在 `raw/` **旁边**，`raw/` 里**不放**账本（`find raw -name '*ledger*' | wc -l` = 0）：
`LedgerLocator` 会把 pass 目录、`--raw`、`--raw` 父目录三处的账本**全收**，同一个账本放两份就数两遍。

---

## 3. AC18 逐条判定

### 3.1 判据与判定方式（沿用上一轮 3.1 的口径，不改判据）

| # | 判据 | 判定方式 |
| --- | --- | --- |
| 1 | client 退出码 0 | 逐 run 的退出码；本轮有 30/57 个 run 的**直接读数**，其余由代码等价 + 编排脚本失败计数器为空推得 |
| 2 | 记录里无 `error` 记录 | 遍历 `raw/**/*.jsonl`，统计 `"type":"error"` |
| 3 | `run.json.failed == false` | 逐个 `run.json`（含 dual 车道），并检查 `arms[].failed` |
| 4 | findings 在白名单外为空 | `verdict.json` 的 `findings_by_severity` + `findings` 列表，逐条按 3.2 的机械定义分类 |
| 5 | 分析器产出 `tables.md`/`verdict.json` | `analyze.sh` 退出码 + 两个文件存在且非空 |

### 3.2 白名单的机械定义（与上一轮逐字相同）

1. `informational` / `udp53-direct`、2. `informational` / `udp-not-carried`（均由 `RowProfiles` 的设计表推导）、
3. `measurement-caveat` / `control-drift-undecided`（pass 预算不足时的固有披露）、
外加同一逻辑豁免的 `measurement-caveat` / `latency-ceiling-reached`。
不在白名单（出现即 `fail`）：`correctness-failure` 全族、`harness-error` 全族、`path-interference`，
以及任何无法用上面几类解释的 `measurement-caveat`。

### 3.3 判定：**`AC18 = fail`**（判据 4 不满足）

环境从头到尾可用（不是 `blocked`），判据 1/2/3/5 全部满足；判据 4 的机械判据不满足 ⇒ 三态里的 `fail`。
与上一轮不同的是：**42 条白名单外 findings 每一条都有归因（第 4 节），没有一条是「不知道哪来的」**。

#### 3.3.1 判据 1 的证据（本轮比上一轮强）

`orchestrator.ps1:326` 在 dual 相位**直接把 `ExitCode` 写进日志**（这条路径没有被 `[void]` 吞掉）：

```bash
$ rg -c 'dual: proxied exit=0 direct exit=0' /tmp/wf-bench/e5b2-campaign/orch-e5b2.log
15
$ rg -n 'dual: proxied exit' /tmp/wf-bench/e5b2-campaign/orch-e5b2.log | head -3
11:2026-10-08 08:32:25   dual: proxied exit=0 direct exit=0
15:2026-10-08 08:40:06   dual: proxied exit=0 direct exit=0
19:2026-10-08 08:47:38   dual: proxied exit=0 direct exit=0
```

15 行 × 2 条车道 = **30 个 run 的退出码是直接读到的 0**（5 个 dual 行 × 3 pass = 15 次双车道调用 ✓）。
其余 27 个 run（9 行 × 3 pass）的退出码仍进不了日志——T3 未修，`Write-Log` 走成功流而三处调用点写成
`[void](Invoke-Client ...)`（`orchestrator.ps1:262` 的 `client <label> exit=<n>` 行确实一行都没有：
`rg -c client orch-e5b2.log` = 0）。它们的证据是：

```bash
$ for p in pass1 pass2 pass3; do python3 e5b2-check.py raw/$p; done
pass1: 9 rows / result 57 / armSummary 57 / attempt 1585 / error 0 / run.json failed-or-arms-failed: none
pass2: 9 rows / result 57 / armSummary 57 / attempt 1586 / error 0 / run.json failed-or-arms-failed: none
pass3: 9 rows / result 57 / armSummary 57 / attempt 1574 / error 0 / run.json failed-or-arms-failed: none
$ rg -l 'arm\(s\) written to' raw/ | wc -l
57                      # 每个 run 的 client.out 都写了成功尾声
$ find raw -name 'client.err' -size +0 | wc -l
0
```

外加代码级等价：`Client/ClientRunner.cs` 的 `run.json.failed` 与返回值取自同一个 `failed` 变量
（上一轮 3.3.1 已列原文），用法错误（退出码 2）走更早的分支、根本不会生成 `run.json`。

#### 3.3.2 判据 2/3 的原始输出

```text
run.json count: 57
failed=True: []
arms with failed=true: []
error records: 0
result records: 261
```

（上一轮是 18 个 run；本轮 57 个，因为三个 pass 的 dual 车道也各有自己的 `run.json`。）

#### 3.3.3 判据 5 的原始输出

```bash
$ bash benchmarks/WinForward.E2E.Analysis/scripts/analyze.sh --raw ./raw --out .
e2e-analysis: 3 pass(es), 9 row(s), 2 ledger(s), 27 loaded run(s) -> /tmp/wf-bench/e5b2-campaign/tables.md
e2e-analysis: verdict -> /tmp/wf-bench/e5b2-campaign/verdict.json; warmup 5.0 s, 10000 resamples, seed 20261006, min passes 3
e2e-analysis: plots/ is not rendered; wrote plots/SKIPPED.md
e2e-analysis: pass1 ledger(s): ledger-main.jsonl, ledger-direct.jsonl
e2e-analysis: pass2 ledger(s): ledger-main.jsonl, ledger-direct.jsonl
e2e-analysis: pass3 ledger(s): ledger-main.jsonl, ledger-direct.jsonl
analyze exit=0
-rw-r--r-- 209532 tables.md
-rw-r--r-- 478752 verdict.json
```

### 3.4 顺带核对 `AGENTS.local.md` §10「结果可信的前提」

| §10 条目 | 本轮读数 | 判定 |
| --- | --- | --- |
| `scheduledAttempts == connectAttempts` | 5 个有 REL 臂的行 × 3 pass：全部 `481 == 481` | ✓ |
| UDP 恒等式 | 6 行 × 3 pass 的 LOSS：全部 `60001 == 60001` | ✓ |
| 逐车道见证非零 | 5 个 dual 行 × 3 pass 都有 `dual/proxied` 与 `dual/direct` 两份 `run.json`，`directLeak` 全 0 | ✓ |
| `foreignConnection == 0` | wf-aot / wf-fdd / proxifier / proxifyre 全 0；**proxybridge MIX UDP = 1572 / 1531 / 744**（3 条 `foreign-connection` findings 的来源） | wf-* 行 ✓，proxybridge 行 ✗（产品侧） |
| `gates.clientSendLoss == 0` | 全部 0，**除了 proxybridge LATLOAD：7856（pass1）/ 0（pass2）/ 7857（pass3）** | wf-* 行 ✓；proxybridge 行 ✗（已披露形态） |
| 直连车道 `directLeak == 0` | 15 个 dual 相位全部 0 | ✓ |
| 两个控制块彼此一致 | 3 个 pass ⇒ 分析器**能**判定了：没有 `control-drift` correctness-failure；剩下的 2 条 `control-drift-undecided` 是 CI 跨在 ±5 % 带上的「未能证明」，不是「证明不一致」 | ✓（未证明不一致） |
| 两台机器 UTC 一致 | 见 §1.2：全程 ≤0.14 s | ✓ |

---

## 4. findings 逐条归因

### 4.1 52 条的逐 kind 清单（原始输出）

```bash
$ python3 e5b2-findings.py verdict.json
findings_by_severity: {"correctness-failure": 3, "path-interference": 3, "harness-error": 0, "measurement-caveat": 40, "informational": 6}

--- findings by (severity, kind) ---
  correctness-failure    foreign-connection           x3   OUTSIDE
  informational          udp-not-carried              x1   ALLOWED
  informational          udp53-direct                 x5   ALLOWED
  measurement-caveat     control-drift-undecided      x2   ALLOWED
  measurement-caveat     latency-ceiling-reached      x2   ALLOWED
  measurement-caveat     ledger-connection-mismatch   x35  OUTSIDE
  measurement-caveat     ledger-datagram-mismatch     x1   OUTSIDE
  path-interference      direct-lane-latency          x3   OUTSIDE

OUTSIDE the whitelist: 42
```

| kind | 条数 | 归属 |
| --- | --- | --- |
| `udp53-direct` × 5 + `udp-not-carried` × 1 | 6 | 白名单内（`RowProfiles` 设计表） |
| `control-drift-undecided` × 2 | 2 | 白名单内（pass 预算类披露；本轮 3 pass 下只剩 tcp-rtt / udp-rtt 两条 CI 跨带） |
| `latency-ceiling-reached` × 2 | 2 | 白名单内的豁免项（proxybridge LATLOAD 的 `windowOverflow`，分析器 README 定义的披露） |
| `foreign-connection` × 3 | 3 | **竞品侧**：`pass{1,2,3}/proxybridge MIX` 的 `classes.udp.foreignConnection = 1572/1531/744` |
| `direct-lane-latency` × 3 | 3 | **跨行比较的负载混杂**（参考行 proxifier 不承载 UDP），数值 13–22 %（上一轮是 63–123 %） |
| `ledger-connection-mismatch` × 35 + `ledger-datagram-mismatch` × 1 | 36 | 见 4.2（20 + 1 条时钟边界）与 4.3（15 条 proxybridge） |

**没有一条落在「未归因」里。**

### 4.2 36 条账本 caveat 的机制：臂边界 × 残钟差（δ 拟合）

分析器的窗口规则是**已披露的**：`WinForward.E2E.Analysis/README.md` 的 Attribution 一节写着
「a connection that opens inside one arm and closes inside the next is attributed where it closed」，
并且写明「The window compares two machines' clocks, so a campaign whose client and target clocks differ
by seconds mis-attributes every arm's boundary」（这正是 T7）。

把这条规则量化：臂窗口 = 客户端 `startedUtc` + tick 偏移；账本记录用的是靶机时钟。若两台机器差 δ，
则**每个**臂窗口相对账本记录整体平移 δ，于是每条臂「丢掉自己头 δ 秒的连接、收到下一条臂头 δ 秒的连接」。
用逐 pass 扫描 δ 验证（`/tmp/wf-bench/e5b2-shift.py`，δ 取 −0.30…+0.30 步长 0.01）：

```text
scope                                      kind                        client   L(0)  best d   L(d)    err
pass1/proxifier/direct MIX                 connection-mismatch           54.0     48   +0.01     54    0.0
pass2/proxifier MIX                        connection-mismatch          108.0     56   +0.05    108    0.0
pass2/proxifier THRU                       connection-mismatch            4.0     50   +0.04      4    0.0
pass2/proxifier/direct LAT                 connection-mismatch           41.0     59   +0.06     41    0.0
pass2/proxifyre/direct MIX                 connection-mismatch           54.0     28   +0.06     54    0.0
pass3/proxifier MIX                        connection-mismatch          108.0     56   +0.08    108    0.0
pass3/proxifyre THRU                       connection-mismatch            4.0     14   +0.05      4    0.0
pass3/proxifyre/direct MIX                 connection-mismatch           54.0     28   +0.10     54    0.0
（完整 35 行见 §4.2 末尾的表）
finding(s) explained by a single uniform shift (|L(d)-client| <= 2) : 20 of 35
shift that does it, per finding: {+0.05: 5, +0.06: 4, +0.08: 3, +0.09: 3, +0.01: 2, +0.04: 1, +0.10: 1, +0.11: 1}
```

**20/35 条被单一 δ 精确解释（误差 0.0）**，且拟合出的 δ 逐 pass 单调增大：
pass1 `+0.01` → pass2 `+0.04…+0.08` → pass3 `+0.05…+0.11`。这与 §1.2 独立测到的漂移曲线
（15:10 的 −0.005 s → 17:54 的 +0.142 s，0.054 s/h）**同向、同量级**（拟合值是下限，因为只有对齐到
突发边界时才需要最小位移）。其中 `pass2/proxifier THRU` 是最干净的一例，原始记录：

```text
THRU 窗口 16:42:11.300 .. 16:42:23.308（12.0 s，客户端自报只开 4 条 stream）
窗口内 50 条 tcp 记录全部落在最后 100 ms：
  peer :51759/:51760/:51761/:51762  bytes=179896320  持续 11.99 s  起始 16:42:11.217   ← THRU 自己的 4 条
  其余 46 条  bytes≈190000/228000  持续 0.02–0.03 s  起始 16:42:23.25–23.29          ← 下一条臂 MIX 的页连接
MIX 窗口 16:42:23.315 .. 16:42:47.371 → 只数到 56（客户端自报 108 = 页连接 104 + bulk 4）
把窗口整体前移 +0.04 s：THRU 50 → 4，MIX 56 → 108，两条 finding 同时归零。
```

唯一那条 `ledger-datagram-mismatch` 同族：

```text
pass3/proxifyre/direct MIX  client=2402  band=±60
  shift +0.00 -> census 2464  |diff| 62.0  OUT
  shift +0.02 -> census 2382  |diff| 20.0  in band
```

即：**36 条账本 caveat 里 21 条 = T7 残钟差 × 分析器已披露的窗口归属规则**（不是产品差异，
也不是新的机制缺陷）；剩下 15 条见 4.3。

`control-drift-undecided` 从 5 条降到 2 条，也正是 3 个 pass 的结果：tcp-rtt `post/pre = 0.9595
(95 % CI 0.9366–1.0381)`、udp-rtt `= 1.0210 (95 % CI 0.9100–1.0251)`，两条 CI 都跨在 ±5 % 的带上
⇒ 分析器给 `inconclusive`。三个 pass 下**没有**出现 `control-drift` / `control-bracketing`
这类 correctness-failure ⇒ 控制块没有被证明不一致。

### 4.3 15 条 proxybridge 的账本 caveat（竞品行，不是机制缺陷）

这 15 条（3 个 pass × {`LAT`、`LATLOAD`、`MIX`、`THRU`、`/proxied LAT`}）**任何 δ 都解释不了**
（扫描 δ∈[−0.30, +0.30] 后误差仍有 4–30）。原始证据：

```text
pass2/proxybridge LAT  窗口 16:30:44.361..16:30:57.389
  客户端 LAT 结果: tcp.connectAttempts=13  tcp.connectFailures=0  meanConnectMs=3.79  tcp.received=241
  账本在该窗口内只有 4 条 tcp 记录（±1 s 内 5 条：4 条探测 + 1 条车道连接）：
     4 条 0 字节 verdict=clientClosedEarly（探针连接）
     1 条 11.99 s verdict=clean（车道连接，起点在窗口开始之前）
  ⇒ 靶机只看到 4/13 个探针连接，而客户端自报 0 失败、连接时延正常
```

三个 pass、两种车道（单车道行与 `/proxied` 车道）都是这个形态：靶机侧只记到客户端 connect 次数的
**25–40 %**。合理解释是这一行把部分客户端 connect 在本地应答（或复用上游连接），于是
「靶机看到的连接数」与「客户端 connectAttempts」本来就不是同一个量；**这属于竞品行行为，
不是 harness 缺陷，也不是被测链路（wf-aot / wf-fdd）的问题**——同一轮里 wf-aot / wf-fdd / proxifier /
proxifyre 的延迟臂在拟合 δ 下都与客户端自报数**逐条相等**。

另外两条（`MIX` +4、`THRU` 0/4）是同一张图的另一面：proxybridge 的 THRU 流在这台机器上被
**截断**（`verdict=protocolError`、每条只echoed约 8–9 MB，而同轮其它行是 180 MB），
它们的连接直到 THRU 窗口结束后 0.5–0.9 s 才终结，于是被记进 MIX 窗口（+4），自己那一格读成 0。

### 4.4 3 条 `foreign-connection`

```text
pass1/proxybridge MIX  MIX.udp.foreignConnection=1572
pass2/proxybridge MIX  MIX.udp.foreignConnection=1531
pass3/proxybridge MIX  MIX.udp.foreignConnection=744
```

上一轮同样打在 proxybridge 上（716 / 708）。**竞品侧**：产品把属于某条流的数据报投递进了另一条流，
是正确性问题，不是性能问题。同轮所有 wf-aot / wf-fdd 行的 `classes.udp.foreignConnection` 与
LOSS 的 `metrics.foreignConnection` 都是 0。

### 4.5 3 条 `direct-lane-latency`

```text
proxifyre    the direct lane's LAT tcp-rtt p50 is 3167.2 us against 2586.6 us on proxifier (22.4 % worse)
proxybridge  ... 3144.7 us ... (21.6 % worse)
wf-aot-opt   ... 2926.6 us ... (13.1 % worse)
```

分析器把每行的直连车道 LAT p50 与**最好**的那一行比，超过 5 % 就报 `path-interference`（它的措辞是
「产品干扰了本该直连的流量」）。但最好那一行是 `proxifier`，而它的画像是 `not-carried`
（完全不承载 UDP）——它的代理车道少做一整条 UDP 路径的活，VM 上的总负载因此低于其它行。
**这是假设，不是结论**：本文件只报数值与这个可检验的混杂来源。相对上一轮（63–123 %），
本轮的量级缩到 13–22 %，也就是这个混杂变小了，但归属仍应由对照 campaign 判定。

### 4.6 一个必须写明的口径问题（给 check 的判断点）

判据 4 的机械白名单（3.2）里**没有**「竞品行的产品缺陷」这一格：只要竞品行真的错，`foreign-connection`
就必然出现，判据 4 就必然不满足。本轮 42 条里 3 条属于这一类。也就是说：
**按现行判据 4，`pass` 与「竞品行有正确性缺陷」是互斥的**。本轮只如实登记，不擅自放宽白名单。

---

## 5. 与上一轮（`E5b-windows.md`）的逐 kind 对比

### 5.1 总表

```bash
$ python3 e5b2-diff.py /tmp/wf-bench/e5b-campaign/verdict.json verdict.json
severity               kind                             E5-b    E5-b2    delta
correctness-failure    foreign-connection                  2        3       +1
correctness-failure    ledger-endpoint-overlap            10        0      -10
informational          udp-not-carried                     1        1       +0
informational          udp53-direct                        5        5       +0
measurement-caveat     control-drift-undecided             5        2       -3
measurement-caveat     latency-ceiling-reached             2        2       +0
measurement-caveat     ledger-connection-mismatch         71       35      -36
measurement-caveat     ledger-datagram-mismatch          102        1     -101
measurement-caveat     ledger-source-overflow             64        0      -64
measurement-caveat     ledger-window-ambiguous            60        0      -60
path-interference      direct-lane-latency                 4        3       -1

totals: E5-b 326 -> E5-b2 52
E5-b  by severity: {"correctness-failure": 12, "path-interference": 4, "harness-error": 0, "measurement-caveat": 304, "informational": 6}
E5-b2 by severity: {"correctness-failure": 3,  "path-interference": 3, "harness-error": 0, "measurement-caveat": 40,  "informational": 6}
```

### 5.2 T1 消掉了多少

| kind | E5-b | E5-b2 | 说明 |
| --- | --- | --- | --- |
| `ledger-endpoint-overlap`（correctness-failure） | 10 | **0** | 双车道两条车道的账本不再互相读到对方（分析器按实例标签选账本） |
| `ledger-window-ambiguous` | 60 | **0** | 同上：窗口重叠不再是「不可归属」的理由 |

两轮的数字对得上：E5-b 是 5 个 dual 行 × 3 臂 × 2 车道 × 2 pass = 60、5 × 2 = 10；本轮这两族直接消失。

**账本标签的原始证据**（本轮新写的两个账本，逐条统计 `label`）：

```text
ledger-main   labels: {'target:40010': 20662}
ledger-direct labels: {'target:40011': 10961}
```

分析器自己的口径（`verdict.json.ledger.passes.pass1.attribution`）：

```text
2 ledger(s); the ledger's own label names the target instance that wrote it (target:40010, target:40011),
so a run is read against the ledger of the target its own run.json declares, then the arm's UTC window bounds it
```

### 5.3 T2 消掉了多少

| kind | E5-b | E5-b2 | 说明 |
| --- | --- | --- | --- |
| `ledger-source-overflow` | 64 | **0** | 普查槽位按区间回收后，整轮没有一次溢满 |

```text
每次读取（跑完 / 拉回后各一次）：
ledger-main  records 20662  udpSummary 9532  maxOverflow 0  overflowIntervals 0  maxReceived 1761983
ledger-direct records 10961  udpSummary 9532  maxOverflow 0  overflowIntervals 0  maxReceived 224056
```

E5-b 的 pass2 里，一个 120 秒 LOSS 窗口有 58700 个数据报的源端点进不了表、后续窗口读成 0；
本轮同样的窗口 `sourceOverflow` 恒 0。T2 条目的「1 秒区间增量」语义没变（`sources[]` 仍是本区间增量）。

### 5.4 T7 消掉了多少

| kind | E5-b | E5-b2 | 说明 |
| --- | --- | --- | --- |
| `ledger-connection-mismatch` | 71 | 35 | 钟差 3 s → ≤0.14 s 后，绝大多数臂边界回到正确的一侧 |
| `ledger-datagram-mismatch` | 102 | 1 | 同上（那条残留见 4.2） |

注意**pass 数不同**（E5-b 2 个、本轮 3 个），而这两族是**逐臂**的 ⇒ 同样条件下 3 个 pass 应比 2 个
多 50 %。按 3 个 pass 折算，E5-b 的 71 / 102 相当于 ~107 / ~153，于是本轮实测的 35 / 1 是
**约 −67 % / −99 %**。T7 的敏感性分析（上一轮 `T1T2-label-and-census.md` §6.3）说 +3.0 s 的整体平移
把 caveat 从 249 压到 96；本轮不再需要任何人工平移。

### 5.5 变多/没变的

- `foreign-connection` 2 → 3：**因为从 2 个 pass 变成 3 个 pass**（每个 pass 一条），不是回归。
- `direct-lane-latency` 4 → 3：`wf-fdd-opt` 那一行不再越线；量级从 63–123 % 缩到 13–22 %。
- `control-drift-undecided` 5 → 2：不是修好的，是 **pass 数够了**——分析器从「无法判定」变成
  「判定了但 CI 跨带」。
- `informational` 6 → 6：设计表推导，稳定。
- `harness-error` 两轮都是 **0**。

---

## 6. 公平性守卫 `check-fairness.py`

```bash
$ python3 benchmarks/WinForward.E2E/scripts/check-fairness.py --tables tables.md
check-fairness.py: rules .../WinForward.E2E.Analysis/verification/row-profiles.json (9 row(s))
  PASS  #17/designed-rows: section 1 holds at least one declared row
  PASS  #17/rule: the rules declare at least one UDP-incapable row
  PASS  #17/profile: section 1 publishes every UDP-incapable row as 'not carried (UDP bypassed)'
  PASS  #17/section 4: every UDP cell of a UDP-incapable row reads 'not carried (UDP bypassed)' (10 cell(s))
  PASS  #17/section 5: every UDP cell of a UDP-incapable row reads 'not carried (UDP bypassed)' (160 cell(s))
  PASS  #17/section 8: every UDP cell of a UDP-incapable row reads 'not carried (UDP bypassed)' (36 cell(s))
  PASS  #17/section 9: the UDP-incapable row's DNS numbers are withheld (4 cell(s))
  PASS  #17/counterweight: a row that does carry UDP still prints numbers
  PASS  #18/section 9: every measured row's UDP/53 path carries the rules' own label
  PASS  #18/coverage: the DNS table holds every designed row whose plan runs a DNS arm (6)
  NOTE  #18: the table carries 2 relayed and 4 non-relayed port-53 row(s)
  PASS  #18/section 8: the UDP-accuracy table repeats the carriage label of every row it holds (5)
  PASS  #19/scope: the CPU table discloses 'user-mode only'
  PASS  #19/gap: the disclosure names the kernel/DPC/ISR time it leaves out
  PASS  #11/arms: the undecodable token stays inside the ledger section
  NOTE  #11: this tree's targets decoded every datagram, so no disclosure is printed
check-fairness.py: tables.md: all 14 guard(s) held
fairness exit=0
```

14 条守卫在**真实 Windows 轮次**的表格上再次全部成立（与上一轮一致）。

---

## 7. 门禁

本轮**只新增/修改文档**（本文件、`tickets.md`、`E5b-windows.md` 顶部指针），没有一行 `*.cs`/`*.json`/脚本改动，
所以六条门禁理论上不受影响——但任务书要求「仍跑一次」，于是**六条一条不落全跑了**（逐项目顺序的测试，
避免上一轮 6.1 那种「一条命令同时起 19 个宿主」在 11 GiB / 无 swap 上的内存颠簸）。
一次性顺序执行，原始日志 `/tmp/wf-bench/e5b2-gates/gates.log`。

| # | 门禁 | 结果 |
| --- | --- | --- |
| 1 | `dotnet build WinForward.slnx -c Release` | **通过**：exit 0，`0 Warning(s) 0 Error(s)` |
| 2 | `dotnet test`（逐项目顺序，`--no-build`） | **通过**：15 个项目 rc 全 0，**1660 passed / 0 failed**（`WinForward.TestSupport` 是辅助项目） |
| 3 | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | **通过**：exit 0，输出 **0 字节** |
| 4 | `jb inspectcode -f=Xml -e=HINT -o=... WinForward.slnx` | **通过**：exit 0，报告里 `<Issue ` 元素 **0 个** |
| 5 | `effective-lines.py`（四条路径） | **通过**：exit 0，输出 **0 字节** |
| 6 | `scripts/selftest.sh scripts/plans/selftest-plan.json` | **通过**：exit 0，11 `result` + 11 `armSummary`、**0 条 `error` 记录**、`run.json.failed` 全 false |

```bash
$ rg -n '=====|^exit=|issue-elements' /tmp/wf-bench/e5b2-gates/gates.log
1:===== 1 build: dotnet build WinForward.slnx -c Release =====
39:exit=0
40:===== 3 format: dotnet format ... =====
41:exit=0  bytes=0
42:===== 4 inspectcode: jb inspectcode -f=Xml -e=HINT =====
1075:exit=0
1076:issue-elements=0
1077:===== 5 effective-lines =====
1078:exit=0  bytes=0
1079:===== extra readme-contract =====
1085:exit=0
1086:===== 6 selftest =====
1459:exit=0
1460:===== 2 tests: per project, sequential =====
```

逐项目（`rc=0` 全部）：

```text
18  WinForward.Analyzers.Tests            121 WinForward.Configuration.Tests    63  WinForward.Core.Tests
361 WinForward.E2E.Tests                   24 WinForward.Integration.Tests      74  WinForward.NdisApi.Tests
137 WinForward.Performance.Tests           72 WinForward.Protocols.Tests       120 WinForward.Runtime.Capture.Tests
183 WinForward.Runtime.Flow.Tests         108 WinForward.Runtime.Socks5.Tests  157 WinForward.Runtime.TcpRedirect.Tests
164 WinForward.Runtime.UdpProxy.Tests      58 WinForward.Windows.Tests
合计 1660 passed / 0 failed（上一轮 1656；多出来的 4 条是 T2 那轮新增的 SourceCensusTests）
```

顺带跑的两条 harness 自有门禁（不在六条里）：

```bash
$ python3 benchmarks/WinForward.E2E/scripts/check-readme-contract.py
note: metrics/clientSendLoss is a declared legacy fallback, not a current writer's key
note: parameters/window is a documented non-key; the record carries a readable spelling instead
note: parameters/loss.lossWindowMs is a documented non-key; the record carries a readable spelling instead
111 key(s) checked against 401 declared constant path(s): ok
exit=0

$ python3 e5b2-check.py /tmp/wf-bench/selftest/out      # 自测产物
rows            : 1 ['.']
result records  : 11
armSummary recs : 11
attempt records : 6
error records   : 0
run.json failed/arms-failed: none
```

---

## 8. 偏离与遗留

### 8.1 对时过程里的两次「搬错方向」（都已复位，不影响数据）

`w32time` 被起过一次（原本 `Stopped / Manual`），后来停回去了；期间为了找正确写法，时钟被搬动过两次
（一次 −3.01 s 变成 +7 h、一次再搬回来），最终校到 **≤0.013 s**。**campaign 是在校时完成之后才启动的**
（15:10:38 达标 → 15:19:26 开跑），所以本轮数据不受这段折腾影响；完整时间线留在 §1.2，
坑（`Set-Date` 的 Kind 语义 + VM 时区 UTC−7）写进了 `tickets.md` 的 T7 步骤。

### 8.2 部署仍然绕开了 `deploy-campaign.sh` 的源目录（T4 未修）

脚本的 `$deploy=/tmp/wf-bench/deploy` 里那份 `orchestrator.ps1` 还是 Oct 6 的旧版（20824 字节）。
本轮的处理是「备份旧版 → 用仓库版覆盖暂存目录里那一份 → 照常跑脚本 → **远端 sha256 复核**」，
而不是像上一轮那样在脚本之外单独补一次上传。这样脚本自己的字节数校验也变成有意义的。
远端结果：`077e2bd4…  21051`，与仓库版逐字节相同。配置与 5 个 plan 先核对为 `same`，所以这是最小改动。

### 8.3 看门狗的险情（上一轮 7.3 的老问题）

启动前心跳已陈旧 **1504 s**，而阈值是 1500 s——再晚几十秒，`wfbench-watchdog` 就会
「emergency teardown」（停产品 + 把防火墙恢复成开启）。启动脚本的第一条命令就是 touch 心跳。
`AGENTS.local.md` §6 说该任务「默认禁用」，实测仍是 **`Ready`**（启用），与上一轮 7.3 一致。

### 8.4 遗留（本轮**没有**解决的）

- **T3**：`Invoke-Client` 的 `client <label> exit=<n>` 仍被 `[void]` 吞掉 ⇒ 判据 1 只有 30/57 个 run
  是直接读数（dual 相位那条路径直接读 `ExitCode`），其余 27 个仍靠代码等价推。
- **T4/T5/T6**：三个脚本/生成器的问题本轮都没动（T5 的传输竞态靠「逐字节核对尺寸」绕过）。
- **判据 4 的白名单缺口**（§4.6）：机械白名单里没有「竞品行的产品缺陷」这一格，于是
  「竞品行真有正确性缺陷」与「AC18 = pass」互斥。这是需要产品负责人拍板的口径问题，本轮只登记。
- **15 条 proxybridge 账本 caveat 的机理**没有定论：靶机侧只看到 25–40 % 的客户端 connect 而客户端
  自报 0 失败，是「本地应答 connect」还是「上游连接复用」需要产品侧行为实验才能分开。
- **3 条 `direct-lane-latency`**：仍只能作为「参考行不承载 UDP ⇒ VM 负载更低」的**假设**；
  量级已从 63–123 % 缩到 13–22 %。
- **残钟差 0.054 s/h**：T7 修的是「3 s 级」的错位；要连臂边界上的突发误配也消掉，得让分析器自己
  估偏移、或让靶机按 run 切分账本，属于比 T7 更大的决定。
- 绘图仍未渲染（`plots/SKIPPED.md`，E4 的既定形态）；账本仍无 arm/row/逐包归属（§14.6 的口径披露）。

---

## 附录 A：35 条 `ledger-connection-mismatch` 的 δ 扫描全表

`/tmp/wf-bench/e5b2-shift.py`：对每条 finding，把该臂的窗口整体平移 δ（δ∈[−0.30, +0.30]，步长 0.01），
数账本里落在平移后窗口内的 `tcp` 记录条数，取与客户端自报数误差最小的那一格。

```text
findings considered: 35
scope                                      kind                        client   L(0)  best d   L(d)    err
pass1/proxifier/direct MIX                 connection-mismatch           54.0     48   +0.01     54    0.0
pass1/proxybridge LAT                      connection-mismatch           13.0      3   -0.30      3   10.0
pass1/proxybridge LATLOAD                  connection-mismatch           25.0     12   +0.02     13   12.0
pass1/proxybridge MIX                      connection-mismatch          108.0    112   -0.30    112    4.0
pass1/proxybridge THRU                     connection-mismatch            4.0      0   -0.30      0    4.0
pass1/proxybridge/proxied LAT              connection-mismatch           41.0     17   -0.01     17   24.0
pass2/proxifier MIX                        connection-mismatch          108.0     56   +0.05    108    0.0
pass2/proxifier THRU                       connection-mismatch            4.0     50   +0.04      4    0.0
pass2/proxifier/direct LAT                 connection-mismatch           41.0     59   +0.06     41    0.0
pass2/proxifier/direct MIX                 connection-mismatch           54.0     28   +0.06     54    0.0
pass2/proxifier/proxied LAT                connection-mismatch           41.0     46   +0.05     41    0.0
pass2/proxifier/proxied MIX                connection-mismatch           54.0     28   +0.05     54    0.0
pass2/proxifyre/direct LAT                 connection-mismatch           41.0     66   +0.01     41    0.0
pass2/proxifyre/direct MIX                 connection-mismatch           54.0     28   +0.06     54    0.0
pass2/proxybridge LAT                      connection-mismatch           13.0      4   -0.30      4    9.0
pass2/proxybridge LATLOAD                  connection-mismatch           25.0     10   +0.08     11   14.0
pass2/proxybridge MIX                      connection-mismatch          108.0    112   -0.30    112    4.0
pass2/proxybridge THRU                     connection-mismatch            4.0      0   -0.30      0    4.0
pass2/proxybridge/proxied LAT              connection-mismatch           41.0     12   +0.05     13   28.0
pass3/proxifier MIX                        connection-mismatch          108.0     56   +0.08    108    0.0
pass3/proxifier THRU                       connection-mismatch            4.0     56   +0.08      4    0.0
pass3/proxifier/direct LAT                 connection-mismatch           41.0     66   +0.09     41    0.0
pass3/proxifier/direct MIX                 connection-mismatch           54.0     28   +0.11     54    0.0
pass3/proxifier/proxied LAT                connection-mismatch           41.0     66   +0.09     41    0.0
pass3/proxifier/proxied MIX                connection-mismatch           54.0     28   +0.08     54    0.0
pass3/proxifyre MIX                        connection-mismatch          108.0     91   +0.05    108    0.0
pass3/proxifyre THRU                       connection-mismatch            4.0     14   +0.05      4    0.0
pass3/proxifyre/direct LAT                 connection-mismatch           41.0     66   +0.09     41    0.0
pass3/proxifyre/direct MIX                 connection-mismatch           54.0     28   +0.10     54    0.0
pass3/proxifyre/proxied MIX                connection-mismatch           54.0     28   +0.06     54    0.0
pass3/proxybridge LAT                      connection-mismatch           13.0      5   +0.07      6    7.0
pass3/proxybridge LATLOAD                  connection-mismatch           25.0     13   +0.10     14   11.0
pass3/proxybridge MIX                      connection-mismatch          108.0    112   -0.30    112    4.0
pass3/proxybridge THRU                     connection-mismatch            4.0      0   -0.30      0    4.0
pass3/proxybridge/proxied LAT              connection-mismatch           41.0     10   +0.06     11   30.0

finding(s) explained by a single uniform shift (|L(d)-client| <= 2) : 20 of 35
shift that does it, per finding: Counter({0.05: 5, 0.06: 4, 0.08: 3, 0.09: 3, 0.01: 2, 0.04: 1, 0.11: 1, 0.1: 1})
```

（`L(0)` 是 δ=0、即分析器现在的读数；`best d` 那一列对 proxybridge 的 15 条顶到了扫描边界 −0.30，
表示「任何单向平移都不解释它」——见 4.3。）

---
