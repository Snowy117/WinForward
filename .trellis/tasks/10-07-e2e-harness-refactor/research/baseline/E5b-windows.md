# E5-b：Windows 轻量验证（AC18 三态）

本文件是 E5 子任务「Windows 轻量验证」的证据载体。每条结论都给出命令与原始输出；
拿不到证据的地方写 `blocked`，不写推测。

- 仓库根：`/home/paff/Projects/WinForward`，基线 HEAD `0a5d035`（E1–E4 与 E5-a 已完成）。
- 被测链路：Windows VM 上的 `WinForward.E2E.exe`（代理车道）→ 本机 `wf-aot` + sing-box
  → NixOS 开发机上的 `WinForward.E2E target`（`192.168.100.4`）。
- 机器与部署状态见 `benchmarks/WinForward.E2E/AGENTS.local.md`（gitignored 资产）。

---

## 0. 结论摘要

**`AC18 = fail`** —— 不是 `blocked`（环境全程可用，两个 pass 跑完，`=== no failures ===`），
不是 `pass`（第 4 条判据不满足）。五条判据：1 ✓、2 ✓、3 ✓、**4 ✗**、5 ✓。

- **跑通的部分是干净的**：18 个 client run 的 `run.json` 全部 `failed=false`、
  两个 pass 各 57 条 `result` + 57 条 `armSummary`、**0 条 `error` 记录**；
  分析器退出 0，产出 `tables.md`（1416 行）与 `verdict.json`（564 KB，14 个顶层键）；
  §10 的各项恒等式、逐车道见证、`foreignConnection`、`clientSendLoss`、`directLeak`
  在被测行（wf-aot / wf-fdd）上全部成立。
- **不满足的第 4 条有明确的、可复现的归因**，且**没有一条指向被测链路**：
  320 条白名单外 findings 里 307 条来自账本归属/容量机制（`start-targets.sh` 不给靶机 `--label`
  + 源端点普查表打满），2 条 `foreign-connection` 打在竞品行 `proxybridge` 上，
  4 条 `path-interference` 是跨行比较，5 条是 2-pass 预算，2 条是分析器已定义的披露。
- **两份 plan 相关交付物都成立**：保形压缩 plan 只改了 `seconds`（负载键逐字保留），
  且已被 12 条 shipped plan 的测试与真实 VM 轮次双重验证（full-plan 行的 `wallSeconds` 322–332 s，
  未压缩是 870 s）。
- **门禁**：六条里 1/3/4/5/6 通过；第 2 条 `dotnet test WinForward.slnx` **一条命令**
  在本机（11 GiB / 无 swap）会因同时起 19 个测试宿主而 `Test Run Aborted`，
  逐项目顺序重跑 **15 个项目、1656 个测试全绿**。

---

## 1. 保形压缩 plan：`scripts/plans-windows/full-shape-plan.json`

### 1.1 为什么不是 `plans-short/`

`plans-short/full-plan.json` 同时改了**负载形状**，不只是时长：

| 臂 | `plans/full-plan.json` | `plans-short/full-plan.json` | 后果 |
| --- | --- | --- | --- |
| LOSS | `120s × 500/s` | `24s × 250/s` | 审计 #4（LOSS 在途窗口对丢失的数据报永不释放）的触发条件就是「500/秒 × 120 秒，窗口 4096」，短 plan 下验不到 |
| LATLOAD | `500 rps`，`window 4096` | `250 rps` | 审计 #8（LAT/LATLOAD 的在途窗口删掉抗协调遗漏样本）的触发条件需要 500 rps 把窗口压满 |
| PERSIST | `idleSeconds 25` | `idleSeconds 6` | idle 存活窗口缩短到 1/4 |
| DNS/DNSALT | `200 rps` | `100 rps` | 每秒查询量腰斩 |

`diff <(python3 -m json.tool scripts/plans/full-plan.json) <(python3 -m json.tool scripts/plans-short/full-plan.json)`
的原文（节选）：

```
20,21c20,21
<             "seconds": 60,
<             "ratePerSecond": 500,
---
>             "seconds": 12,
>             "ratePerSecond": 250,
45,46c45,46
<             "seconds": 120,
<             "ratePerSecond": 500,
---
>             "seconds": 24,
>             "ratePerSecond": 250,
```

### 1.2 形状保留与压缩比

新 plan 相对 `scripts/plans/full-plan.json` **只改 `seconds`**，其余每一个键逐字保留。
下表由脚本生成（命令见 1.3），第四列是「除 `seconds` 外是否有键不同」：

```
arm        full_s    new_s   ratio  load keys changed?
IDLE           60       12    5.00  no
LAT            60       12    5.00  no
LATLOAD        60       24    2.50  no
DNS            90       24    3.75  no
DNSALT         90       24    3.75  no
LOSS          120      120    1.00  no
REL           120       24    5.00  no
THRU           60       12    5.00  no
MIX           120       24    5.00  no
PERSIST        90       45    2.00  no
TOTAL         870      321    2.71
```

四个被点名的形状原样保留：

- **LOSS**：`seconds 120`、`ratePerSecond 500`、`payloadBytes 200`、`lossWindowMs 200` —— 压缩比 **1.00**（完全不压缩）。
- **LATLOAD**：`ratePerSecond 500`、`window 4096` —— 只把 60 s 压到 24 s（2.50×）。
- **PERSIST**：`idleSeconds 25`、`intervalMs 1000`、`payloadBytes 200` —— 只把 90 s 压到 45 s（2.00×）；
  45 s / 1000 ms 让调度器排出 10 个请求 → 25 s idle 窗口 → 10 个请求，窗口两侧都非空。
- **DNS/DNSALT**：`ratePerSecond 200`、`tcpPercent 2` —— 只把 90 s 压到 24 s（3.75×）。

其余臂（IDLE、LAT、REL、THRU、MIX）时长压到 1/5，负载键（`ratePerSecond`、
`connectionsPerSecond`、`streams`、`targetBytesPerSecond`、`desktops`、`lossWindowMs`）一个没动。

### 1.3 生成压缩比表的命令

```bash
cd benchmarks/WinForward.E2E
python3 - <<'PY'
import json
full = json.load(open('scripts/plans/full-plan.json'))['arms']
new  = json.load(open('scripts/plans-windows/full-shape-plan.json'))['arms']
fn = {a['name']: a for a in full}; nn = {a['name']: a for a in new}
assert set(fn) == set(nn)
print('%-8s %8s %8s %7s  %s' % ('arm','full_s','new_s','ratio','load keys changed?'))
tf = tn = 0
for name in fn:
    f, n = fn[name], nn[name]
    tf += f['seconds']; tn += n['seconds']
    fk = {k: v for k, v in f.items() if k != 'seconds'}
    nk = {k: v for k, v in n.items() if k != 'seconds'}
    diff = {k: (fk.get(k), nk.get(k)) for k in set(fk) | set(nk) if fk.get(k) != nk.get(k)}
    print('%-8s %8d %8d %7.2f  %s' % (name, f['seconds'], n['seconds'], f['seconds']/n['seconds'], diff or 'no'))
print('%-8s %8d %8d %7.2f' % ('TOTAL', tf, tn, tf/tn))
PY
```

### 1.4 它能被 `PlanFile` 加载的证据

新增的 `plans-windows/` 被接进「shipped plan」测试集，而不是让新 plan 落在一个没有门禁读的目录里：
`tests/WinForward.E2E.Tests/RepoPaths.cs` 加 `WindowsPlansDirectory`，
`PlanFileTests.ShippedPlanPaths()` 把它拼进枚举，`EveryShippedPlanLoads` 的计数从 11 改成 12。

```
$ dotnet test tests/WinForward.E2E.Tests -c Release --filter "FullyQualifiedName~PlanFileTests" --nologo -v quiet
Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5, Duration: 147 ms - WinForward.E2E.Tests.dll (net10.0)
```

**负向对照**（证明该测试真的读这个目录，而不是碰巧通过）：

```
$ mv benchmarks/WinForward.E2E/scripts/plans-windows/full-shape-plan.json /tmp/full-shape-plan.json.bak
$ dotnet test tests/WinForward.E2E.Tests -c Release --filter "FullyQualifiedName~EveryShippedPlanLoads" --nologo -v quiet
[xUnit.net 00:00:00.75]     WinForward.E2E.Tests.PlanFileTests.EveryShippedPlanLoads [FAIL]
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 70 ms
$ mv /tmp/full-shape-plan.json.bak benchmarks/WinForward.E2E/scripts/plans-windows/full-shape-plan.json
$ dotnet test tests/WinForward.E2E.Tests -c Release --filter "FullyQualifiedName~EveryShippedPlanLoads" --nologo -v quiet
Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1, Duration: 110 ms
```

### 1.5 它真的跑得通：本机 selftest

`selftest.sh` 用的是同一个跨平台二进制的 `target` 动词（该环里没有代理，所以它只证明
plan 能加载并跑完，不证明任何产品）：

```
$ cd benchmarks/WinForward.E2E && bash scripts/selftest.sh scripts/plans-windows/full-shape-plan.json
...
[exit code 0]
```

关键记录（来自 `/tmp/wf-bench/selftest/out/`）：

| 臂 | 证据 | 值 |
| --- | --- | --- |
| LOSS | `metrics.sent` / `supplied` | `60001` / `60001`（120 s × 500/s 的量级，与审计 #4 的仿真条件「500/秒 × 120 秒，窗口 4096」同形） |
| LOSS | 身份 `arrived+late+never+abandonedAtTeardown+corruptDatagrams == sent` | `60001+0+0+0+0 == 60001` ✓ |
| LOSS | `gates.clientSendLoss` | `0` |
| LATLOAD | `gates.windowOverflow` | `0`（500 rps 下窗口未被压满，本地无代理时符合预期） |
| PERSIST | `idleSecondsScheduled` / `idleSecondsObserved` | `25` / `25` |
| PERSIST | `survivedIdle` / `reconnects` | `True` / `0` |
| THRU | `budgetBytes` / `budgetReached` | `720000000`（= 60 MB/s × 12 s） / `True` |
| REL | `connectAttempts` / `scheduledAttempts` | `481` / `481`（24 s × 20/s = 480 个配速槽位，两个计数相等） |

```
$ python3 -c "import json; d=json.load(open('/tmp/wf-bench/selftest/out/run.json')); print('failed=',d['failed']); print([(a['name'],a['failed']) for a in d['arms']])"
failed= False
[('IDLE', False), ('LAT', False), ('LATLOAD', False), ('DNS', False), ('DNSALT', False), ('LOSS', False), ('REL', False), ('THRU', False), ('MIX', False), ('PERSIST', False)]
```

---

## 2. 一轮的完整命令链

> 本节的每条命令都在下面给出原始输出。VM 侧命令经 `benchmarks/WinForward.E2E/scripts/wf.sh`
> 驱动一个持久 tmux 会话里的 `evil-winrm-py`（原因见 `AGENTS.local.md` §4）。

### 2.0 前置：环境可用性（三态判定的门票）

```bash
$ ping -c 1 -W 2 192.168.100.2
1 packets transmitted, 1 received, 0% packet loss, time 0ms
$ bash -c 'cat < /dev/null > /dev/tcp/192.168.100.2/5985' && echo "winrm port 5985 OPEN"
winrm port 5985 OPEN

$ tmux new-session -d -s wfbench -x 220 -y 50
$ tmux send-keys -t wfbench -l "evil-winrm-py -i 192.168.100.2 -u neko -p \"$NEKO_PASS\" --no-colors"
$ tmux send-keys -t wfbench Enter
evil-winrm-py PS C:\Users\Neko\Documents>

$ scripts/wf.sh run '$PSVersionTable.PSVersion.ToString(); (Get-CimInstance Win32_OperatingSystem).Caption; $env:NUMBER_OF_PROCESSORS'
5.1.26100.9444
Microsoft Windows 11 IoT Enterprise LTSC
16
```

时钟对齐（账本归因靠 UTC 窗口，VM 的时区是 PST/PDT 而本机是 CST，本地时间相差 15 小时，
但 UTC 必须一致）：

```bash
$ date -u +'%Y-%m-%dT%H:%M:%S'          # 本机
2026-10-08T09:49:04
$ scripts/wf.sh run '(Get-Date).ToUniversalTime().ToString("o")'   # VM
2026-10-08T09:49:07.7543964Z
```

差 3 秒，账本归因可用。

### 2.1 `scripts/publish.sh`

```bash
$ cd benchmarks/WinForward.E2E && bash scripts/publish.sh
=== build and test ===
    0 Error(s)
Time Elapsed 00:00:12.64

=== publish ===
linux        WinForward.E2E WinForward.E2E.Contracts.dll WinForward.E2E.Contracts.pdb WinForward.E2E.deps.json WinForward.E2E.dll WinForward.E2E.pdb WinForward.E2E.runtimeconfig.json
win          WinForward.E2E.Contracts.dll WinForward.E2E.Contracts.pdb WinForward.E2E.deps.json WinForward.E2E.dll WinForward.E2E.exe WinForward.E2E.pdb WinForward.E2E.runtimeconfig.json
win-direct   WinForward.E2E.Contracts.dll WinForward.E2E.Contracts.pdb WinForward.E2E.Direct.deps.json WinForward.E2E.Direct.dll WinForward.E2E.Direct.exe WinForward.E2E.Direct.pdb WinForward.E2E.Direct.runtimeconfig.json

=== the two Windows clients must be distinguishable by image name ===
/tmp/wf-bench/pub/win-direct/WinForward.E2E.Direct.exe
/tmp/wf-bench/pub/win/WinForward.E2E.exe
```

### 2.2 `scripts/deploy-campaign.sh`

每个文件的字节数与本地一致才算成功（脚本按远端文件大小校验）：

```
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
=== product configurations ===
  ok   C:\wfbench\wf-aot\config.json              972 bytes
  ok   C:\wfbench\wf-aot\config-nativeudp.json    973 bytes
  ok   C:\wfbench\wf-aot\config-dnsrelay.json     699 bytes
  ok   C:\wfbench\wf-fdd\config.json              972 bytes
  ok   C:\wfbench\proxybridge\bench.pbprofile     909 bytes
  ok   C:\wfbench\stage\proxifyre-app-config.json 408 bytes
  ok   C:\wfbench\proxifier\bench.ppx             1457 bytes
=== plans ===
  ok   C:\wfbench\e2e\full-plan.json              1549 bytes
  ok   C:\wfbench\e2e\udp-plan.json               544 bytes
  ok   C:\wfbench\e2e\dns-plan.json               319 bytes
  ok   C:\wfbench\e2e\dual-plan.json              485 bytes
  ok   C:\wfbench\e2e\base-plan.json              147 bytes
=== orchestrator ===
  ok   C:\wfbench\orchestrator.ps1                20824 bytes
```

**这一步踩到了 §7 的「偏离」**：`deploy-campaign.sh` 从 `/tmp/wf-bench/deploy/`（一个**没有脚本生成**
的暂存目录，Oct 6 的旧副本）取 `orchestrator.ps1`，所以它推上 VM 的是**旧编排脚本**（20824 字节），
而不是仓库里的那份（21051 字节，修掉了「dual 阶段每 pass 复用同一目录、只留最后一行的证据」的缺陷）。
配置与 plan 恰好逐字节相同，只有 orchestrator 漂移：

```bash
$ diff /tmp/wf-bench/deploy/orchestrator.ps1 scripts/orchestrator.ps1
294c294
<     param($Product, [string]$PassDir)
---
>     param($Product, [string]$RowDir)
296c296,299
<     $dir = Join-Path $PassDir 'dual'
---
>     # Per row, never per pass: a pass-level directory is rebuilt by every dual row in turn, so only
>     # the last row's lanes survive and the other rows' directLeak evidence is destroyed rather than
>     # reported as missing.
>     $dir = Join-Path $RowDir 'dual'
365c368
<     if ($Product.Dual) { Invoke-DualPhase -Product $Product -PassDir $PassDir }
---
>     if ($Product.Dual) { Invoke-DualPhase -Product $Product -RowDir $productDir }
```

处理：把**仓库里的**那份直接推上去，并用远端字节数核对。

```bash
$ scripts/wf.sh up /home/paff/Projects/WinForward/benchmarks/WinForward.E2E/scripts/orchestrator.ps1 'C:\wfbench\orchestrator.ps1'
[+] File uploaded successfully as: C:\wfbench\orchestrator.ps1
$ scripts/wf.sh run '(Get-Item C:\wfbench\orchestrator.ps1).Length'
21051
```

### 2.3 压缩 plan 的投放（`-PlanRoot`）

不覆盖 VM 上原有的 `C:\wfbench\e2e\full-plan.json`，而是建一个独立的 plan root，
其中 `full-plan.json` 就是保形压缩版；`udp/dns/dual/base` 四项按原样复制
（它们分别喂 `wf-aot-nativeudp`、`wf-aot-dnsrelay`、dual 相位与两个控制块，形状不属本轮压缩对象）。

```bash
$ scripts/wf.sh up .../scripts/plans-windows/full-shape-plan.json 'C:\wfbench\full-shape-plan.upload'
[+] File uploaded successfully as: C:\wfbench\full-shape-plan.upload
$ scripts/wf.sh run 'New-Item -ItemType Directory -Force -Path C:\wfbench\e2e-win | Out-Null; Copy-Item C:\wfbench\full-shape-plan.upload C:\wfbench\e2e-win\full-plan.json -Force; foreach ($f in @("base-plan","dns-plan","dual-plan","udp-plan")) { Copy-Item "C:\wfbench\e2e\$f.json" "C:\wfbench\e2e-win\$f.json" -Force }; Get-ChildItem C:\wfbench\e2e-win | ForEach-Object { $_.Name + " " + $_.Length }; (Get-FileHash C:\wfbench\e2e-win\full-plan.json).Hash'
--- e2e-win ---
base-plan.json 147
dns-plan.json 319
dual-plan.json 485
full-plan.json 1547
udp-plan.json 544
--- orchestrator size ---
21051
--- sha256 of plan root full-plan vs uploaded ---
ED0A3477B74DFA973227C80E732592076220FDFA54E29AEB69094F805779D92E
ED0A3477B74DFA973227C80E732592076220FDFA54E29AEB69094F805779D92E

$ sha256sum benchmarks/WinForward.E2E/scripts/plans-windows/full-shape-plan.json   # 本机
ed0a3477b74dfa973227c80e732592076220fdfa54e29aeb69094f805779d92e  scripts/plans-windows/full-shape-plan.json
```

哈希一致 ⇒ VM 上跑的就是本文件第 1 节那份 plan。

### 2.4 `scripts/start-targets.sh`

```bash
$ ss -ltnu | rg "192.168.100.4:(40010|40011|53|40053|40054)"
tcp   LISTEN 0      512    192.168.100.4:40010      0.0.0.0:*
tcp   LISTEN 0      512    192.168.100.4:40011      0.0.0.0:*
tcp   LISTEN 0      512    192.168.100.4:40053      0.0.0.0:*
tcp   LISTEN 0      512    192.168.100.4:40054      0.0.0.0:*
tcp   LISTEN 0      512    192.168.100.4:53         0.0.0.0:*
udp   UNCONN 0      0      192.168.100.4:40010      0.0.0.0:*
udp   UNCONN 0      0      192.168.100.4:40011      0.0.0.0:*
udp   UNCONN 0      0      192.168.100.4:40053      0.0.0.0:*
udp   UNCONN 0      0      192.168.100.4:40054      0.0.0.0:*
udp   UNCONN 0      0      192.168.100.4:53         0.0.0.0:*
```

### 2.5 VM 上启动 `orchestrator.ps1`（pwsh7）

实际参数：**`-Passes 2 -OutRoot C:\wfbench\results-e5b -PlanRoot C:\wfbench\e2e-win`**，
其余取默认（`-TargetAddress 192.168.100.4`、端口 40010/40010/53/40053/40011/40054、`-Seed 20261006`）。

```bash
$ scripts/wf.sh run "$a = @('-NoProfile','-ExecutionPolicy','Bypass','-File','C:\wfbench\orchestrator.ps1',
    '-Passes','2','-OutRoot','C:\wfbench\results-e5b','-PlanRoot','C:\wfbench\e2e-win'); ...
    $p = Start-Process 'C:\Program Files\PowerShell\7\pwsh.exe' -ArgumentList $a -PassThru -WindowStyle Hidden `
         -RedirectStandardOutput C:\wfbench\orch-e5b.log -RedirectStandardError C:\wfbench\orch-e5b.err; 'started pid=' + $p.Id"
pwsh7=True
started pid=5576
```

preflight 与 pass 1 的开头：

```
2026-10-08 02:47:25 === preflight ===
2026-10-08 02:47:28   firewall: Domain=False Private=False Public=False
2026-10-08 02:47:28   target reachable at 192.168.100.4:40010
2026-10-08 02:47:35   env: Microsoft Windows 11 IoT Enterprise LTSC build 26100, 16 cpus, 2151 MB visible
2026-10-08 02:47:35 === pass 1/2 ===
2026-10-08 02:47:35   --- control pre (no product) ---
2026-10-08 02:49:37   --- wf-aot-dnsrelay (plan dns-plan.json) ---
2026-10-08 02:52:43   proxy truth: tcp=2 udp=0 uot=2
2026-10-08 02:52:46   --- wf-aot-opt (plan full-plan.json) ---
2026-10-08 02:58:24   proxy truth: tcp=634 udp=0 uot=8
2026-10-08 03:00:24   dual: proxied exit=0 direct exit=0
2026-10-08 03:00:25   dual proxy truth: tcp=95 udp=0 uot=4 directLeak=0
2026-10-08 03:00:28   --- wf-fdd-opt (plan full-plan.json) ---
2026-10-08 03:06:05   proxy truth: tcp=634 udp=0 uot=8
2026-10-08 03:08:06   dual: proxied exit=0 direct exit=0
2026-10-08 03:08:06   dual proxy truth: tcp=95 udp=0 uot=4 directLeak=0
2026-10-08 03:08:09   --- proxifier (plan full-plan.json) ---
```

`planPath` 核对（证明跑的是 `-PlanRoot` 下那份压缩 plan）：

```bash
$ scripts/wf.sh run '$r = Get-Content C:\wfbench\results-e5b\pass1\control-pre\run.json -Raw | ConvertFrom-Json; "planPath=" + $r.planPath; "failed=" + $r.failed; "wallSeconds=" + $r.wallSeconds'
planPath=C:\wfbench\e2e-win\base-plan.json
failed=False
wallSeconds=120.551
```

一处副产品缺陷（记入第 7 节）：`orch-e5b.log` 里**没有**任何 `client <label> exit=<n>` 行。

```bash
$ scripts/wf.sh run '(Select-String -Path C:\wfbench\orch-e5b.log -Pattern "client" | Measure-Object).Count'
0
```

原因见 7.1：`Write-Log` 走的是 `Write-Output`（成功流），而三处调用点写成 `[void](Invoke-Client ...)`，
把函数整条管线的输出（含日志行）一起丢掉了。它不影响 `$script:Failures`（脚本作用域副作用仍在），
但让「client exit 0」这条判据无法从日志直接读出，所以第 3.3 节另给直接测得的退出码。

### 2.6 拉回结果

编排脚本自己在 VM 侧写下的收尾（这是「一轮跑完」的权威印记）：

```bash
$ scripts/wf.sh run 'Get-Content C:\wfbench\orch-e5b.log | Select-String -Pattern "FAILURES|no failures|complete|pass . done"'
2026-10-08 03:37:02   pass 1 done
2026-10-08 04:26:28   pass 2 done
2026-10-08 04:26:49 === no failures ===
2026-10-08 04:26:49 === orchestrator complete ===
```

**两个 pass 全程 `=== no failures ===`** —— 即 `Test-ClientRun` 一次都没记过
「client 没写 run.json」或「run.json failed」。

按 7.6 的处置拉回（`publish-campaign.sh` 第 1-3 步，路径改指本轮）：

```bash
$ scripts/wf.sh run 'Remove-Item C:\wfbench\campaign-e5b.zip -ErrorAction SilentlyContinue; Compress-Archive -Path C:\wfbench\results-e5b\* -DestinationPath C:\wfbench\campaign-e5b.zip -Force; "zipped " + [math]::Round((Get-Item C:\wfbench\campaign-e5b.zip).Length / 1MB, 1) + " MiB"'
zipped 0.9 MiB
$ scripts/wf.sh idle 180
$ scripts/wf.sh down 'C:\wfbench\campaign-e5b.zip' /tmp/wf-bench/e5b-campaign/campaign-e5b.zip 900
ls: cannot access '/tmp/wf-bench/e5b-campaign/campaign-e5b.zip': No such file or directory   # ← 这一步的竞态，见下
unzip:  cannot find or open /tmp/wf-bench/e5b-campaign/campaign-e5b.zip, ...                  # ← 于是解压也失败
```

**`wf.sh down` 的竞态（本轮踩到的第一个坑）**：`wf.sh` 的 `repl_cmd` 在发出命令后立刻用
「最后一行是不是提示符」判断命令结束，而 `tmux send-keys` 之后提示符**还是**最后一行，
于是循环第一轮就 `break`，函数在传输真正完成之前返回。证据是事后的尺寸核对：

```bash
$ scripts/wf.sh run '(Get-Item C:\wfbench\campaign-e5b.zip).Length'
917537
$ stat -c %s /tmp/wf-bench/e5b-campaign/campaign-e5b.zip
917537
$ unzip -t /tmp/wf-bench/e5b-campaign/campaign-e5b.zip | tail -2
    testing: environment.json         OK
No errors detected in compressed data of /tmp/wf-bench/e5b-campaign/campaign-e5b.zip.
```

远端 917537 == 本地 917537，包内 343 个条目、解压后 8890021 字节，完好。
处理：传输之后**必须**核对本地与远端尺寸（`deploy-campaign.sh` 的 `stage()` 正是这么做的，
注释还写明「这个错误曾毁掉整轮验证」）；同一课在本轮又交了一次学费。

顺带一个不影响正确性的观察：经 WinRM 的 Windows PowerShell **5.1** 的 `Compress-Archive`
写出的条目名用反斜杠，`unzip` 会警告并自行转换：

```bash
$ unzip -o campaign-e5b.zip -d raw
Archive:  campaign-e5b.zip
warning:  campaign-e5b.zip appears to use backslashes as path separators
  inflating: raw/pass1/order.txt
```

它不是本轮失败的原因（失败是上面那个竞态），但会让 `unzip -q` 的调用者看不到警告；
本轮为确定性改用按 `\` → `/` 归一化的 python 解包，343 个条目全部落位：

```
raw/pass1/{control-post,control-pre,proxifier,proxifyre,proxybridge,wf-aot-dnsrelay,wf-aot-nativeudp,wf-aot-opt,wf-fdd-opt}
raw/pass2/{同上 9 行}
raw/environment.json
```

### 2.7 账本的位置（第二个坑：位置决定份数）

第一次分析把两个靶机的账本**拼接**成 `raw/target-ledger.jsonl` 又把原文件留在 `raw/` 旁边，
结果每个 DNS 汇总被数了两遍（`2 dnsSummary record(s) report 148188 ... against the client's own 74086`
—— 148188 = 2 × 74094，而 74094 才是账本里的真值）。原因在
`benchmarks/WinForward.E2E.Analysis/Loading/LedgerLocator.cs`：它把
**pass 目录、`--raw`、`--raw` 的父目录**三处的账本**全部**收进来（按路径去重），不是「先找到先用」。

分析器的冻结基准树（`verification/FROZEN.md` 第 18 行描述的 `synthetic-tree.tar.gz` 根布局：
`raw/` + `ledger-main.jsonl` + `ledger-direct.jsonl`）把两个账本**只放在 `raw/` 旁边**，
`raw/` 里不放账本；本轮照做。实测核对（解包冻结树后逐文件数 `dnsSummary`）：

```bash
$ find /tmp/e5b-synth -name "*ledger*"
/tmp/e5b-synth/ledger-main.jsonl
/tmp/e5b-synth/ledger-direct.jsonl
```

（`make_tree.py:15` 的 docstring 说「a `target-ledger.jsonl` per pass」，但生成器不写这种文件、
冻结树里也没有——见 7.7 的 T6。）

```bash
$ ls -l /tmp/wf-bench/e5b-campaign/ledger-main.jsonl /tmp/wf-bench/e5b-campaign/ledger-direct.jsonl
-rw-r--r-- 1 paff users 1456079 Oct  8 19:27 ledger-direct.jsonl
-rw-r--r-- 1 paff users 3266639 Oct  8 19:27 ledger-main.jsonl
$ ls raw
environment.json  pass1  pass2
$ python3 -c "..."   # 账本自身的汇总记录
ledger-main.jsonl    dnsSummary port 53    udpQueries 74094
ledger-main.jsonl    dnsSummary port 40053 udpQueries 77981
ledger-direct.jsonl  dnsSummary port 40054 udpQueries 160
```

两个账本在靶机被 SIGTERM 收尾时写出了 `tcpSummary`/`dnsSummary`/`targetSummary`
（靶机 PID 由脚本点名 SIGTERM，不是 `pkill`；`start-targets.sh` 的 `wait` 随即自然退出）：

```bash
[11:27:47Z] target pids: 3024832 3024833
[11:27:47Z] SIGTERM -> 3024832
[11:27:47Z] SIGTERM -> 3024833
[11:27:48Z] targets left: 0
[11:27:50Z] 13466 lines  3266639 bytes  <- /tmp/wf-bench/ledger-main.jsonl
[11:27:50Z]  6994 lines  1456079 bytes  <- /tmp/wf-bench/ledger-direct.jsonl
```

（顺带：`start-targets.sh` 给靶机**没有** `--label`，所以账本每一条记录的 `label` 都是空串——
这是第 4.1 节一大类 findings 的根因，也说明了为什么它在本轮之前就存在：
`/tmp/wf-bench/start-targets.sh`（Oct 6 的旧版）的日志里同样写着 `label ''`。）

---

## 3. AC18 判据逐条对照

### 3.1 判据与判定方式

| # | 判据 | 判定方式 |
| --- | --- | --- |
| 1 | client 退出码 0 | 逐 run 的 `$LASTEXITCODE`；另由 `run.json` 存在 + `failed == false` + CLI 契约（任一拳失败退 1、用法错误退 2）交叉验证 |
| 2 | 记录里无 `error` 记录 | 遍历每个 run 的 `*.jsonl`，统计 `"type":"error"` |
| 3 | `run.json.failed == false` | 逐 run 读 `run.json`，并检查没有 `arms[].failed == true` |
| 4 | findings 在白名单外为空 | `verdict.json` 的 `findings_by_severity` + `findings` 列表，逐条按 3.2 的规则分类 |
| 5 | C# 分析器成功产出 `tables.md`/`verdict.json` | `analyze.sh` 退出码 + 两个文件存在且非空 |

### 3.2 白名单的机械定义

「白名单」不是随手放宽，而是**能由分析器自己的声明表推导出来的 findings**。本轮允许出现的只有三类：

1. `informational` / `udp53-direct`：该行的 port-53 DNS 臂是直连路径测量。
   由 `WinForward.E2E.Analysis/Model/RowProfiles.cs` 的 `Udp53` 声明直接推导，凡 `Udp53 != relayed`
   的行走一条。对应 harness README 自测白名单里的「DNS 端口声明那条」。
2. `informational` / `udp-not-carried`：该行不承载 UDP，它的 UDP 格子读
   `not carried (UDP bypassed)` 并被排除出 UDP 精度与 DNS 延迟比较。同样由 `RowProfiles.Udp` 推导。
3. `measurement-caveat` / `control-drift-undecided`：两个控制块在少于
   `--min-passes`（默认 3）个 pass 时无法被**证明**一致。这是「轻量验证」这个 pass 预算的固有披露，
   不是产品行为。

另有一条按同一逻辑豁免：延迟臂的 `windowOverflow > 0`。分析器 README 把它定义为披露而非失败
（该臂的延迟格子渲染 `n/a (windowOverflow > 0)`），而审计 #8 的存在意味着它在慢产品上正是**预期**结果。

**不在白名单**（出现即 `fail`）：`correctness-failure` 全族（`directLeak`、`foreignConnection`、
`ledger-endpoint-overlap`、`control-drift`、`control-bracketing`）、`harness-error` 全族、
`path-interference`，以及任何无法用上面三类解释的 `measurement-caveat`。

### 3.3 判定

**`AC18 = fail`**（三态里的 `fail`：一轮完整跑通，但有一条断言不满足）。
环境从头到尾可用，所以**不是** `blocked`；第 4 条判据不满足，所以**不是** `pass`。

| # | 判据 | 结果 | 判据载体 |
| --- | --- | --- | --- |
| 1 | client 退出码 0 | **✓（推得，非直接记录）** | 见 3.3.1 |
| 2 | 记录里无 `error` 记录 | **✓** | pass1 与 pass2 各 57 条 `result` + 57 条 `armSummary`，`error` 记录 0 条 |
| 3 | `run.json.failed == false` | **✓** | 两个 pass、9 行、18 个 run 全部 `failed=False`，无 `arms[].failed` |
| 4 | findings 在白名单外为空 | **✗** | 320 条在白名单外（12 correctness-failure、4 path-interference、304 measurement-caveat） |
| 5 | 分析器产出 `tables.md`/`verdict.json` | **✓** | 退出 0，`2 pass(es), 9 row(s), 2 ledger(s), 18 loaded run(s)` |

#### 3.3.1 判据 1 的推导链（因为 7.1 让退出码进不了日志）

编排脚本日志里的 `client <label> exit=<n>` 行被 `[void]` 吞掉（7.1），所以退出码**没有**被直接记录，
这一点必须说清楚，不能装作量到了。可用的替代证据是一条**代码级等价**加两条实测的通道行为：

1. `Client/ClientRunner.cs` 的收尾是两步相邻的语句：

   ```csharp
   await RunFileWriter.WriteRunFileAsync(options, summaries, planHash, targetAddress, runStartTicks, runEndTicks, startedUtc, failed);
   await Console.Out.WriteLineAsync($"e2e client: {summaries.Count} arm(s) written to ...");
   ...
   return failed ? ExitCodes.RuntimeError : ExitCodes.Success;
   ```

   `run.json` 的 `failed` 与退出码取自**同一个** `failed` 变量，所以「某臂失败 ⇒ `run.json.failed=true`
   ⇒ 退出码 1」是同一行代码的两面；用法错误（退出码 2）走的是更早的分支，那种情况下 `run.json` 根本不会生成。
2. 本轮 18 个 run：`run.json` 全部存在、全部 `failed=false`、无 `arms[].failed`、0 条 `error` 记录，
   `client.out` 都写了成功尾声（`e2e client: N arm(s) written to ...`）。
3. 编排脚本自己的 `Test-ClientRun` 只在「没有 `run.json`」或「`failed` 为真/有臂失败」时记一条失败，
   而它在两轮里一次都没记过：`=== no failures ===`（2.6）。
4. 退出码通道的**实测**两端：本机 `selftest.sh` 用同一份二进制跑同一族 plan，脚本在客户端非零退出的
   分支上会 `exit`，而它返回 0（1.5 与第 6 节门禁 6）；用法错误一端由 `selftest.sh` 缺参数退回 2 佐证
   （本轮实测 `exit=2`）。

结论：判据 1 成立；但它成立的方式是「由被测代码的等价关系推得 + 编排脚本的失败计数器为空」，
**不是** 18 次 `$LASTEXITCODE` 的读数。

#### 3.3.2 判据 4 不满足的归因（逐族，见 4.1）

- **320 条里 307 条是账本机制**（10 条 `ledger-endpoint-overlap` correctness-failure + 297 条
  `ledger-*` caveat），根因有两个，都在 harness 侧：
  (a) `start-targets.sh` 不给靶机 `--label`，dual 相位并行跑时账本无法按车道归属（4.1.1）；
  (b) 源端点普查表打满，pass 2 的账本计数读成 0（4.1.2）。
  **没有一条**指向 wf-aot 链路的健康：所以这 307 条既不是 `blocked` 的理由，也不是「跑通」就能算数的
  ——它们是 harness 的归属与容量缺陷，必须修或必须从判据里排除，本轮只能如实登记。
- **2 条 `correctness-failure foreign-connection` 是产品侧**，但打在竞品行 `proxybridge` 上
  （MIX UDP 类 `foreignConnection=716`（pass1）/`708`（pass2）），
  不是被测链路 wf-aot：`wf-aot-opt` 的 MIX `classes.udp.foreignConnection=0`，
  所有 wf-aot 行的 LOSS `metrics.foreignConnection=0`（4.2）。
- **4 条 `path-interference`** 是跨行比较（4.1.3），直接车道之间的差异，机制未定，登记为遗留。
- **5 条 `control-drift-undecided`** 是 pass 预算的直接后果（2 < `--min-passes 3`）。
- **2 条 `latency-ceiling-reached`** 是分析器 README 明确规定的披露形态（4.1 表）。
- **6 条 `informational`** 是白名单内、由 `RowProfiles` 设计表推导出来的那两条。

#### 3.3.3 与 §10「结果可信的前提」的对照

第 4.2 节逐条核对了 §10。结论：**wf-aot 各行的 §10 条目全部成立**
（恒等式、逐车道见证、`foreignConnection=0`、`clientSendLoss=0`、`directLeak=0`），
只有两条例外，都不在 wf-aot 行上：

- `gates.clientSendLoss == 0`：`proxybridge` 的 LATLOAD 是 `7846`（窗口溢出被真实触发的案例）；
- 两个控制块彼此一致：2 个 pass 下分析器只能给 `inconclusive`（见表），**无法被证明**一致。

所以本轮的 wf-aot 链路数据可用（§10 层面），但整轮 campaign 的 findings 不为空（AC18 第 4 条不满足）。
这两句话不矛盾：前者是被测链路，后者是 campaign 级输出。

---

## 4. findings 列表与 §11 已知缺陷对照

### 4.1 分析器 findings

```bash
$ python3 -c "import json,collections; d=json.load(open('verdict.json',encoding='utf-8-sig')); print(json.dumps(d['findings_by_severity'])); c=collections.Counter((f['severity'],f['kind']) for f in d['findings']); [print('  %-20s %-28s %d'%(s,k,n)) for (s,k),n in sorted(c.items())]"
{"correctness-failure": 12, "path-interference": 4, "harness-error": 0, "measurement-caveat": 304, "informational": 6}
   correctness-failure  foreign-connection           2
   correctness-failure  ledger-endpoint-overlap      10
   informational        udp-not-carried              1
   informational        udp53-direct                 5
   measurement-caveat   control-drift-undecided      5
   measurement-caveat   latency-ceiling-reached      2
   measurement-caveat   ledger-connection-mismatch   71
   measurement-caveat   ledger-datagram-mismatch     102
   measurement-caveat   ledger-source-overflow       64
   measurement-caveat   ledger-window-ambiguous      60
   path-interference    direct-lane-latency          4
```

按 3.2 的机械白名单逐条判：**只有 6 条 `informational` 落在白名单内**
（`udp53-direct` × 5 + `udp-not-carried` × 1，全部由 `RowProfiles` 的设计表推导）。
其余 320 条全部在白名单外。为了把「账本机制造成的」与「本轮数据本身造成的」分开，
再用「不给账本」跑一次（`--ledger` 指向一个不存在的路径，定位器会把它过滤掉）：

```bash
$ bash .../analyze.sh --raw ./raw --out /tmp/wf-bench/e5b-noledger --ledger /tmp/wf-bench/nonexistent-ledger.jsonl
e2e-analysis: 2 pass(es), 9 row(s), 0 ledger(s), 18 loaded run(s)
WITHOUT ledger: {"correctness-failure": 2, "path-interference": 4, "harness-error": 0, "measurement-caveat": 8, "informational": 6}
   correctness-failure  foreign-connection           2
   informational        udp-not-carried              1
   informational        udp53-direct                 5
   measurement-caveat   control-drift-undecided      5
   measurement-caveat   latency-ceiling-reached      2
   measurement-caveat   no-target-ledger             1
   path-interference    direct-lane-latency          4
```

于是白名单外的 findings 分成三族，每一族都能追到一个具体机制：

| 族 | 条数 | 机制 | 是否产品侧 |
| --- | --- | --- | --- |
| `ledger-endpoint-overlap`（correctness-failure） | 10 | 见 4.1.1 | **不是**（launcher 缺 label） |
| `ledger-connection/datagram-mismatch`、`ledger-source-overflow`、`ledger-window-ambiguous` | 297 | 见 4.1.1 与 4.1.2 | **不是** |
| `foreign-connection`（correctness-failure） | 2 | `proxybridge` 的 MIX UDP 类把 716/708 个数据报送进了别的流 | **是**（但打在竞品行上，不是 wf-aot） |
| `direct-lane-latency`（path-interference） | 4 | 见 4.1.3 | 不是（跨行比较的混杂） |
| `control-drift-undecided` | 5 | 2 个 pass < 分析器 `--min-passes 3`，控制块一致性无法被**证明** | 不是（预算） |
| `latency-ceiling-reached` | 2 | `proxybridge` LATLOAD `windowOverflow=7846/7860`——分析器 README 定义的披露（审计 #8 的预期形态） | 不是（披露） |

#### 4.1.1 `ledger-window-ambiguous` × 60 与 `ledger-endpoint-overlap` × 10：launcher 没给账本 label

`start-targets.sh` 启动靶机时**不传 `--label`**，所以账本每一条记录的 `label` 都是空串：

```bash
$ python3 -c "import json,collections; print(collections.Counter(json.loads(l)['label'] for l in open('ledger-main.jsonl') if l.startswith('{')))"
Counter({'': 13466})
```

分析器文档写明归属有两条路：给了每轮 label 就按 label 选，
否则只能按「客户端的 `startedUtc` + 臂的 tick 偏移」推出的 UTC 窗口选
（`WinForward.E2E.Analysis/README.md` §"The target ledger"）。本轮的 dual 相位里，
**代理车道与直连车道是同时跑的**，两条车道的窗口必然重叠，而账本又没有 label，
于是：

- 每个 dual 臂、每条车道各得一条 `ledger-window-ambiguous`：5 行 × 3 臂 × 2 车道 × 2 pass = **60** ✓；
- 端点分区检查（proxied 窗口的端点集合 vs direct 窗口的端点集合必须不相交）在窗口重叠时
  **两边看到的是同一批记录**，交集必然非空 → 每个 dual 行、每个 pass 各一条
  `ledger-endpoint-overlap`：5 × 2 = **10** ✓，报出来的端点全是 VM 自己的临时端口
  （`192.168.100.2:56972`、`…:63504` 之类）。

所以这 10 条 correctness-failure **不是**「产品把直连应用抓进了代理」。同一轮的
sing-box 侧证据是干净的：5 个 dual 行的 `dual proxy truth: ... directLeak=0` 全为 0，
分析器自己的 `dual_phase.rows` 里 `directLeak` 也都是 0。
这是**启动脚本与分析器之间的契约缺口**：`make_tree.py` 的合成树专门造了
「per-run ledger labels in one pass」来让这条检查成立，而 shipped `start-targets.sh` 给不出 label。
它不是本轮引入的：`/tmp/wf-bench/start-targets.sh`（Oct 6 的旧版）的 `targets.log` 里同样写着
`label ''`。

#### 4.1.2 297 条 `ledger-*` 计数 caveat：源端点普查表打满

`ledger-source-overflow` 报的是 `udpSummary.sourceOverflow`——靶机的**源端点普查表**装不下的
端点数。pass 1 的数是「略少」（`58583` vs 客户端 `60001`），pass 2 直接是**零**：

```
pass2/wf-aot-nativeudp LOSS  ledger counted 0 datagram(s) ... against the client's own 60001.0 (metrics.sent)
                             udpSummary.sourceOverflow=58700
pass2/control-post    BASE   ledger counted 0 datagram(s) ... against the client's own 31202.0
                             udpSummary.sourceOverflow=31160
```

也就是说：一个 120 秒、60001 个数据报的 LOSS 窗口里，有 58700 个数据报的源端点没能进表
（≈ 每个数据报一个源端点），而后一个 pass 的这些窗口在普查里读成 0。
这条计数路径在本轮配置下**不可用**，与分析器 README 已披露的口径（「账本没有 arm/row/product 归属」
「UDP 摘要是 1 Hz 区间增量」）方向一致，但量级超出了「区间粒度」能解释的范围。
**它是 harness 侧的计数容量问题，不是产品差异**；具体机制（是产品中继端点的端口翻腾、
还是普查表在 pass 之间不再复位）本轮没有定位，登记为遗留。

#### 4.1.3 4 条 `direct-lane-latency`：跨行比较里的负载混杂

```
proxifyre      the direct lane's LAT tcp-rtt p50 is 3106.8 us against 1391.1 us on proxifier (123.3 % worse)
proxybridge    ... 2871.8 us ... (106.4 % worse)
wf-aot-opt     ... 2870.8 us ... (106.4 % worse)
wf-fdd-opt     ... 2272.8 us ... (63.4 % worse)
```

参考行是 `proxifier`，而它的画像恰恰是 `not-carried`（完全不承载 UDP）：它的代理车道少做了
一整条 UDP 路径的活，VM 上的总负载因此低于其它四行，直连车道也就更快。
**这是假设，不是结论**——本文件只报数值与这个可检验的混杂来源；归属该由对照 campaign 判定。

### 4.2 pass 1 的逐臂原始读数（未经分析器，直接读记录）

这些是 AC18 判据 2/3 的直接证据，也是 AGENTS.local.md §10「结果可信的前提」的逐条核对。

**判据 2/3：无 `error` 记录、`run.json.failed == false`**

```bash
$ scripts/wf.sh run "\$p='C:\wfbench\results-e5b\pass1'; \$e=@(Get-ChildItem \$p -Recurse -Filter *.jsonl | ForEach-Object { Select-String -Path \$_.FullName -Pattern '\"type\":\"error\"' }); 'error records: ' + \$e.Count; Get-ChildItem \$p -Directory | ForEach-Object { \$r=Join-Path \$_.FullName 'run.json'; \$j=Get-Content \$r -Raw | ConvertFrom-Json; \$f=@(\$j.arms | Where-Object { \$_.failed }); \$_.Name + ' failed=' + \$j.failed + ' arms=' + @(\$j.arms).Count + ' armsFailed=' + \$f.Count + ' wall=' + [math]::Round(\$j.wallSeconds) }"
error records: 0
control-post failed=False arms=1 armsFailed=0 wall=120
control-pre failed=False arms=1 armsFailed=0 wall=121
proxifier failed=False arms=10 armsFailed=0 wall=322
proxifyre failed=False arms=10 armsFailed=0 wall=322
proxybridge failed=False arms=10 armsFailed=0 wall=328
wf-aot-dnsrelay failed=False arms=2 armsFailed=0 wall=180
wf-aot-nativeudp failed=False arms=3 armsFailed=0 wall=240
wf-aot-opt failed=False arms=10 armsFailed=0 wall=332
wf-fdd-opt failed=False arms=10 armsFailed=0 wall=332
```

`wall` 一列同时是**压缩 plan 真的生效**的证据：full-plan 行是 322–332 秒，
而未压缩的 `full-plan` 是 870 秒；`wf-aot-dnsrelay` 180 秒（= 2 × 90 s 的 `dns-plan`）、
`wf-aot-nativeudp` 240 秒（= `udp-plan` 的 60+60+120）、控制块 120 秒（= `base-plan` 的
TCP 相 + UDP 相各 60 s）。

**§10 的逐条**

| §10 条目 | 原始读数 | 判定 |
| --- | --- | --- |
| `scheduledAttempts == connectAttempts` | 每行 REL：`481 / 481` | ✓ |
| UDP 恒等式 `arrived+late+never+abandonedAtTeardown+corruptDatagrams == sent` | 每行 LOSS：`60001+0+0+0+0 == 60001` | ✓ |
| 逐车道见证非零 | MIX `metrics.desktops` 有 4 个 desktop，每个 `udp.sent=721`、`pageConnections=26`、`bulkFrames=458`、`dnsSent=8` 全 > 0；`gates.idleLanes=0`；LATLOAD `lanesPlanned==lanesStarted==2`、`laneShortfall=0` | ✓ |
| `foreignConnection == 0` | LOSS `metrics.foreignConnection=0`；MIX `metrics.classes.udp.foreignConnection=0` | ✓ |
| `gates.clientSendLoss == 0`（含 LAT/LATLOAD 的窗口溢出与积压丢弃） | wf-aot-opt / wf-fdd-opt / proxifier / proxifyre 的 LATLOAD：`clientSendLoss=0`。**proxybridge 的 LATLOAD：`windowOverflow=7846`、`clientSendLoss=7846`**（`backlogDrops=0`、`scheduleTruncated=0`、`laneShortfall=0`、`inFlightCeilingMs=8538.083`） | wf-* 行 ✓；**proxybridge 行 ✗** |
| 直连车道 `directLeak == 0` | 5 个 dual 行的日志均为 `dual proxy truth: ... directLeak=0`（proxifier/wf-aot-opt/wf-fdd-opt/proxifyre/proxybridge） | ✓ |
| 两个控制块彼此一致 | 2 个 pass < 分析器 `--min-passes 3`，只能得到 `inconclusive` | 无法证明（见 4.3） |

**支撑读数的命令**（每行一条 `result` 记录，字段路径按 harness README 的契约表）：

```bash
$ scripts/wf.sh run "foreach (\$row in @('wf-aot-opt','wf-fdd-opt','proxifier','proxybridge','proxifyre','wf-aot-nativeudp')) { \$p = \"C:\wfbench\results-e5b\pass1\\\$row\LOSS.jsonl\"; \$r = @(Get-Content \$p | Where-Object { \$_ -like '*\"type\":\"result\"*' })[0] | ConvertFrom-Json; \$m=\$r.metrics; \$row + ' LOSS sent=' + \$m.sent + ' supplied=' + \$m.supplied + ' arrived=' + \$m.arrived + ' never=' + \$m.never + ' foreign=' + \$m.foreignConnection + ' lossRate=' + \$m.lossRate }"
wf-aot-opt LOSS sent=60001 supplied=60001 arrived=60001 never=0 foreign=0 lossRate=0
wf-fdd-opt LOSS sent=60001 supplied=60001 arrived=60001 never=0 foreign=0 lossRate=0
proxifier LOSS sent=60001 supplied=60001 arrived=60001 never=0 foreign=0 lossRate=0
proxybridge LOSS sent=60001 supplied=60001 arrived=60001 never=0 foreign=0 lossRate=0
proxifyre LOSS sent=60001 supplied=60001 arrived=60001 never=0 foreign=0 lossRate=0
wf-aot-nativeudp LOSS sent=60001 supplied=60001 arrived=60001 never=0 foreign=0 lossRate=0

$ scripts/wf.sh run "\$p='C:\wfbench\results-e5b\pass1\proxybridge\LATLOAD.jsonl'; \$r=@(Get-Content \$p | Where-Object { \$_ -like '*\"type\":\"result\"*' })[0] | ConvertFrom-Json; 'proxybridge LATLOAD gates: ' + (\$r.gates | ConvertTo-Json -Compress)"
proxybridge LATLOAD gates: {"clientSendLoss":7846,"windowOverflow":7846,"backlogDrops":0,"sendFailures":0,"lanesPlanned":2,"lanesStarted":2,"laneShortfall":0,"scheduleTruncated":0,"inFlightCeilingMs":8538.083,"windowMs":0}
```

注意 `wf-aot-opt`/`wf-fdd-opt` 的 LATLOAD 是 `windowOverflow=0`、
`inFlightCeilingMs≈8201 ms`，即审计 #8 要求的「窗口放大到覆盖预期尾部」在**参考行**上成立；
proxybridge 那一行是窗口溢出被真实触发的案例。

### 4.3 §11 已知缺陷是否命中

**命中第 1 条（已确认的 WinForward 缺陷：客户端半关闭后连接失效）。**
按 REL 臂的 `byMode` 拆开（pass 1，每行 481 次尝试 = 4 个模式 × 约 120 次）：

| 行 | `clean` 模式 (n=121) | `halfClose` 模式 (n=120) | 失败形态 |
| --- | --- | --- | --- |
| **wf-aot-opt** | `clean=7`，**`timeout=114`** | `clean=9`，**`timeout=111`**，`trailer=85248` | 超时（连接停止推进） |
| **wf-fdd-opt** | `clean=10`，**`timeout=111`** | `clean=10`，**`timeout=110`**，`trailer=88320` | 超时 |
| proxifier（同拓扑参考行） | `clean=121` | `clean=120`，`trailer=92160` | 无 |
| proxybridge | `clean=0`，`reset=121` | `halfCloseViolation=120` | 复位而非干净关闭 |
| proxifyre | `clean=0`，`reset=121` | `halfCloseViolation=120` | 同上 |

即：**同一拓扑、同一 plan、同一轮次**里，两个 WinForward 行在客户端半关闭（`clean` 模式在客户端 FIN
时由靶机关闭发送侧；`halfClose` 模式在 FIN 后写 3 帧 trailer）之后分别有 114/111 与 111/110 次**超时**，
而 proxifier 是 `clean=121/121`、`halfClose=120/120` 且 trailer 完整（`92160 = 120 × 768`）。
这与 `/tmp/winforward-tcp-symptoms.md` 的结论（「已确认是 WinForward 的缺陷，同拓扑下 proxifier 是
`clean=46/46`，证明可修」）是同一现象在不同轮次、不同行号下的复现。

**命中第 3 条（上游 RST 被洗成 FIN，已重新定性为 sing-box 的行为）。**
参考行 proxifier 的 `resetAfterN` 模式：靶机 `SetLinger(0)` + abort，本该让对端看到复位，
实测 `reset=0`、`unexpectedEof=115/120`——复位变成了 EOF。sing-box 在所有行的上游，
所以这是路径属性而非产品差异，与
`.trellis/tasks/10-06-e2e-competitor-benchmark/research/singbox-rst-to-fin.md` 一致。
（proxybridge/proxifyre 的 `reset=121` 出现在 `clean` 模式而不是 `resetAfterN` 模式，
两者是不同现象，归属留给对照 campaign，本文件不作结论。）

**对 AC18 的影响：无。** 上面每一项都是 REL 臂的**指标列**
（`metrics.byMode`、`fidelityMismatch`、`unexpectedEof`），分析器把它们渲染成表格数字，
不产生 findings——第 4.1 节的 findings 白名单判据不受这条产品缺陷影响。
反过来说这也正是 AC18 的设计意图：它判的是**链路健康**（跑通、无错误记录、无断言失败），
不是产品好坏；产品侧的差异由表格承载。

**第 2/4/5 条**（`/tmp/rel-repro.md` 的 60 秒复现步骤、`harness-audit.md` 的五路审查、
`singbox-issue-report.md` 的上游 SOCKS5 补丁）不是「可复现的缺陷」：前者是复现步骤文档，
后两者分别是审查结论与已部署的补丁（VM 上的 sing-box 就是 `1.14.2-singfix`），本轮不做重复验证。

---

## 5. C# 分析器的输出

分析器是 `benchmarks/WinForward.E2E.Analysis`，入口是 `scripts/analyze.sh`（它先构建项目，再
`exec` 产物，所以退出码就是分析的退出码）。本轮用的是**默认口径**：`--warmup-seconds 5`、
`--resamples 10000`、`--seed 20261006`、`--min-passes 3`（后两项与分析器默认一致）。

```bash
$ cd /tmp/wf-bench/e5b-campaign
$ bash /home/paff/Projects/WinForward/benchmarks/WinForward.E2E.Analysis/scripts/analyze.sh --raw ./raw --out .
e2e-analysis: 2 pass(es), 9 row(s), 2 ledger(s), 18 loaded run(s) -> /tmp/wf-bench/e5b-campaign/tables.md
e2e-analysis: verdict -> /tmp/wf-bench/e5b-campaign/verdict.json; warmup 5.0 s, 10000 resamples, seed 20261006, min passes 3
e2e-analysis: plots/ is not rendered; wrote plots/SKIPPED.md
e2e-analysis: pass1 ledger(s): ledger-main.jsonl, ledger-direct.jsonl
e2e-analysis: pass2 ledger(s): ledger-main.jsonl, ledger-direct.jsonl
$ echo "exit=$?"
exit=0
```

产出（三个都在，非空）：

| 文件 | 大小 | 形状 |
| --- | --- | --- |
| `tables.md` | 255779 字节 / 1416 行 | 15 节表格 |
| `verdict.json` | 564820 字节 | 14 个顶层键（`bootstrap`/`control_blocks`/`dual_phase`/`findings`/`findings_by_severity`/`flat_mode`/`generated_by`/`ledger`/`metrics`/`passes`/`raw`/`row_profiles`/`rows`/`thresholds`）；21 个 headline 指标；326 条 findings |
| `plots/SKIPPED.md` | 899 字节 | E4 的既定形态（不绘图） |

它读到的树：`2 pass(es)`、`9 row(s)`、`2 ledger(s)`、`18 loaded run(s)`、`ledger.available=true`。

额外跑一次 E4 交付的公平性守卫，把 §四 的口径披露按**本轮真实输出**再验一遍
（这不是六条门禁之一，但它盯的就是分析器文档里那几条披露）：

```bash
$ python3 /home/paff/Projects/WinForward/benchmarks/WinForward.E2E/scripts/check-fairness.py --tables /tmp/wf-bench/e5b-campaign/tables.md
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
check-fairness.py: /tmp/wf-bench/e5b-campaign/tables.md: all 14 guard(s) held
$ echo "exit=$?"
exit=0
```

这一条比它看起来重要：AC17 的 §四 披露此前只在**合成树**上验过，本轮的 14 条守卫是在
**真实 Windows 轮次**的表格上全部成立的（含 `proxifier` 那一行的 `not carried (UDP bypassed)`
在 §4/§5/§8 共 206 个格子里的一致渲染）。

---

## 6. 门禁

本轮改动包含 `*.cs`（`RepoPaths.cs`、`PlanFileTests.cs`）、`*.json`（新 plan）、`*.md`（本文件、
harness README 的 Layout 表）与 `scripts/selftest.sh`（usage 一行），所以六条门禁**全部受影响**，
一条不落都跑了。

| # | 门禁 | 命令 | 结果 |
| --- | --- | --- | --- |
| 1 | 构建 | `dotnet build WinForward.slnx -c Release` | **通过**：exit 0，`0 Warning(s) 0 Error(s)` |
| 2 | 测试 | `dotnet test WinForward.slnx -c Release` | **单条命令不通过**（`Test Run Aborted`，见 6.1）；逐项目顺序重跑见 6.1 |
| 3 | 格式 | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | **通过**：exit 0，输出 **0 字节** |
| 4 | 检查器 | `jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx` | **通过**：exit 0，报告里 `<Issues />` 为空（0 个 `<Issue>`） |
| 5 | 有效行 | `python3 benchmarks/WinForward.E2E/scripts/effective-lines.py benchmarks/WinForward.E2E benchmarks/WinForward.E2E.Contracts benchmarks/WinForward.E2E.Analysis tests/WinForward.E2E.Tests` | **通过**：exit 0，无输出 |
| 6 | 自测 | `cd benchmarks/WinForward.E2E && scripts/selftest.sh scripts/plans/selftest-plan.json` | **通过**：exit 0，`11 result` + `11 armSummary`，**0 条 `error` 记录**，`run.json.failed` 全 false |

原始输出：

```bash
$ dotnet build WinForward.slnx -c Release
    0 Warning(s)
    0 Error(s)
$ echo exit=$?    → 0

$ dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore
$ echo "exit=$?  bytes=$(stat -c %s format.out)"
exit=0  bytes=0

$ jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx
Inspection report was written to /tmp/wf-bench/e5b-gates/jb-inspectcode.xml
$ echo exit=$?    → 0
$ rg -c "<Issue " jb-inspectcode.xml   → 0
$ tail -3 jb-inspectcode.xml
  <IssueTypes />
  <Issues />
</Report>

$ python3 benchmarks/WinForward.E2E/scripts/effective-lines.py benchmarks/WinForward.E2E \
      benchmarks/WinForward.E2E.Contracts benchmarks/WinForward.E2E.Analysis tests/WinForward.E2E.Tests
$ echo "exit=$?  bytes=$(stat -c %s efflines.out)"
exit=0  bytes=0

$ cd benchmarks/WinForward.E2E && scripts/selftest.sh scripts/plans/selftest-plan.json
$ echo exit=$?    → 0
$ python3 /tmp/e5b-check.py /tmp/wf-bench/selftest/out
rows            : 1 ['.']
result records  : 11
armSummary recs : 11
attempt records : 6
error records   : 0
run.json failed/arms-failed: none
```

> 注意第 4 条的计数方式：`grep -c '<Issue'` 会把容器元素 `<IssueTypes />` 与 `<Issues />` 也算进去
> （本轮因此先得到「2」这个假警报）。判据是 `<Issue ` 元素本身，本轮为 **0**。

### 6.1 第 2 条为什么单条命令跑不过，以及它怎么重跑

`dotnet test WinForward.slnx -c Release` 在 `11:40:59` 之后把解决方案里约 19 个测试项目
**同时**起成测试宿主，而这台机器只有 11 GiB 内存、**没有 swap**，当时可用内存约 1 GiB：

```bash
$ free -g
               total        used        free      shared  buff/cache   available
Mem:              11           9           0           0           2           1
Swap:              0           0           0
```

结果除两个项目外全部 `Test Run Aborted.`（既不是断言失败，也不是编译错误）：

```bash
$ rg -c "Test Run Aborted" test.out      → 11
$ rg -n "Passed!|Failed!" test.out
95:Passed!  - Failed:     0, Passed:    18, Skipped:     0, Total:    18, Duration: 11 m 15 s - WinForward.Analyzers.Tests.dll (net10.0)
97:Passed!  - Failed:     0, Passed:   357, Skipped:     0, Total:   357, Duration: 8 s - WinForward.E2E.Tests.dll (net10.0)
$ echo "exit=$?"    → 1
```

这两个跑通的项目里就有本轮改过测试的那一个：**`WinForward.E2E.Tests` 357/357 全绿**
（含 `PlanFileTests.EveryShippedPlanLoads`，11 → 12 条 shipped plan）。
处理：`dotnet build-server shutdown` 回收常驻 MSBuild 节点（`nodeReuse` 留下 26 个进程、约 2 GiB），
然后**逐项目顺序**重跑同一个测试集（`dotnet test <project> -c Release --no-build`），
每次只起一个测试宿主。

### 6.2 逐项目顺序重跑的结果：15 个项目全绿，1656 个测试 0 失败

```bash
$ dotnet build-server shutdown          → exit 0
$ free -m   # 可用内存从 4252 MB 升到 5828 MB
$ for p in $(dotnet sln WinForward.slnx list | grep -E '^tests/.*csproj$'); do dotnet test "$p" -c Release --no-build; done
[12:03:33Z] WinForward.Analyzers.Tests           rc=0   8s :: Passed! - Failed: 0, Passed:  18, Total:  18
[12:03:37Z] WinForward.Configuration.Tests       rc=0   4s :: Passed! - Failed: 0, Passed: 121, Total: 121
[12:03:40Z] WinForward.Core.Tests                rc=0   3s :: Passed! - Failed: 0, Passed:  63, Total:  63
[12:03:51Z] WinForward.E2E.Tests                 rc=0  11s :: Passed! - Failed: 0, Passed: 357, Total: 357
[12:03:55Z] WinForward.Integration.Tests         rc=0   4s :: Passed! - Failed: 0, Passed:  24, Total:  24
[12:03:58Z] WinForward.NdisApi.Tests             rc=0   3s :: Passed! - Failed: 0, Passed:  74, Total:  74
[12:04:01Z] WinForward.Performance.Tests         rc=0   3s :: Passed! - Failed: 0, Passed: 137, Total: 137
[12:04:04Z] WinForward.Protocols.Tests           rc=0   3s :: Passed! - Failed: 0, Passed:  72, Total:  72
[12:04:14Z] WinForward.Runtime.Capture.Tests     rc=0  10s :: Passed! - Failed: 0, Passed: 120, Total: 120
[12:04:18Z] WinForward.Runtime.Flow.Tests        rc=0   4s :: Passed! - Failed: 0, Passed: 183, Total: 183
[12:04:25Z] WinForward.Runtime.Socks5.Tests      rc=0   7s :: Passed! - Failed: 0, Passed: 108, Total: 108
[12:04:30Z] WinForward.Runtime.TcpRedirect.Tests rc=0   5s :: Passed! - Failed: 0, Passed: 157, Total: 157
[12:04:35Z] WinForward.Runtime.UdpProxy.Tests    rc=0   5s :: Passed! - Failed: 0, Passed: 164, Total: 164
[12:04:36Z] WinForward.TestSupport               rc=0   1s :: (helper project, no tests)
[12:04:39Z] WinForward.Windows.Tests             rc=0   3s :: Passed! - Failed: 0, Passed:  58, Total:  58
[12:04:39Z] projects with non-zero exit: 0 of 15
```

合计 **1656 个测试、0 失败**（`WinForward.TestSupport` 是纯辅助项目）。
注意耗时对比：同一个 `WinForward.Analyzers.Tests` 在并行那次用了 **11 分 15 秒**，
顺序跑只用 **8 秒**——那 11 分钟是内存颠簸，不是测试本身。
所以第 2 条门禁的**测试内容全绿**；不通过的是「一条命令同时起 19 个宿主」这个调用方式在
本机 11 GiB / 无 swap 的约束下不成立。这是环境约束，不是本轮改动的回归，
但它意味着 AGENTS.md 里那条命令在这台机器上不能照抄。

顺带跑的两条 harness 自有门禁（不在六条里，但它们盯的正是本轮动过的两个文件）：

```bash
$ cd benchmarks/WinForward.E2E && python3 scripts/check-readme-contract.py
note: metrics/clientSendLoss is a declared legacy fallback, not a current writer's key
note: parameters/window is a documented non-key; the record carries a readable spelling instead
note: parameters/loss.lossWindowMs is a documented non-key; the record carries a readable spelling instead
111 key(s) checked against 401 declared constant path(s): ok
$ echo "exit=$?"
exit=0
```

（其余命令的原始输出见下一节回填。）

---

## 7. 偏离与遗留

### 7.1 `orchestrator.ps1`：`Invoke-Client` 的日志行被调用点吞掉

`Write-Log` 用 `Write-Output`（成功流）写日志，而三处调用点写成 `[void](Invoke-Client ...)`，
于是函数整条管线的输出——包括每一行 `client <label> exit=<n> in <n>s`——被 `[void]` 丢弃。
证据：

```bash
$ scripts/wf.sh run '(Select-String -Path C:\wfbench\orch-e5b.log -Pattern "client" | Measure-Object).Count'
0
$ scripts/wf.sh run 'Test-Path C:\wfbench\results-e5b\pass1\control-pre\run.json'
True
```

即：client 确实跑了、写了 `run.json`，但日志里没有它的退出码行。
`$script:Failures`（脚本作用域，副作用）不受影响，所以最终的 `=== FAILURES ===` 段仍然可信；
受影响的是「退出码可读性」——AC18 的「client exit 0」因此不能从日志直接读出，只能另行测（见 3.3）。
修法方向：`Write-Log` 改走主机（`Write-Host`），或调用点改成先接住输出再转发。

### 7.2 `deploy-campaign.sh` 从没有生成者的暂存目录取文件

脚本从 `/tmp/wf-bench/deploy/`（Oct 6 的旧副本，仓库里**没有任何脚本**生成它）取
`orchestrator.ps1`/`configs/`/`e2e/`，所以它推上去的是旧编排脚本。见 2.2 的 `diff`：
`C:\wfbench\orchestrator.ps1` 在部署后是 20824 字节（旧），仓库里是 21051 字节。
配置与 plan 恰好逐字节相同（逐文件 `diff -q` 全 `same`），所以**只有 orchestrator 漂移**。
在一台新机器上按 `AGENTS.local.md` §8 走，这个目录根本不存在 ⇒ 部署步骤无法照做。
本轮的处理是绕开它（把仓库里那份直接推上去）；根治办法是让 `deploy-campaign.sh` 直接从
`scripts/` 与 `scripts/plans*/` 取源，或补一个生成 `/tmp/wf-bench/deploy/` 的脚本。

**这次替换不是形式主义**：仓库版把 dual 相位的输出目录从每 pass 一份改成每行一份，
本轮结果里 5 个 dual 行各有自己的 `dual/{proxied,direct}`，且 pass 级 `dual/` 不存在；
用旧版部署的话，这 5 行的 directLeak 证据会互相覆盖，只剩最后一行的：

```bash
$ scripts/wf.sh run 'Get-ChildItem C:\wfbench\results-e5b\pass1 -Directory | ForEach-Object { $d = Join-Path $_.FullName "dual"; if (Test-Path $d) { $_.Name + " -> " + ((Get-ChildItem $d -Directory | ForEach-Object { $_.Name }) -join ",") } }; "pass-level dual exists: " + (Test-Path C:\wfbench\results-e5b\pass1\dual)'
proxifier -> direct,proxied
proxifyre -> direct,proxied
proxybridge -> direct,proxied
wf-aot-opt -> direct,proxied
wf-fdd-opt -> direct,proxied
pass-level dual exists: False
```

### 7.3 `AGENTS.local.md` 的两处状态与文档不符

- §6 说计划任务 `wfbench-watchdog`「默认禁用」，实际是 **`Ready`（启用）**，且它的日志显示它确实开火过：

  ```bash
  $ scripts/wf.sh run '(Get-ScheduledTask -TaskName wfbench-watchdog).State'
  Ready
  $ scripts/wf.sh run 'Get-Content C:\wfbench\watchdog.log -Tail 3'
  2026-10-08 02:39:01 heartbeat stale by 1,558s -> emergency teardown
  2026-10-08 02:39:01 stopped service ProxiFyreService
  2026-10-08 02:39:02 teardown complete
  ```

  本轮不构成阻塞：编排脚本在每条臂前后都 touch 心跳，最长的无心跳间隔是 LOSS 臂的 120 秒，
  远低于 1500 秒阈值。但如果编排脚本在两次 touch 之间卡住超过 25 分钟，看门狗会把
  sing-box 停掉并把防火墙恢复成开启——那会让剩余的行全部作废，且日志里只会留下一行
  `emergency teardown`。
- §8 的示例用 `-Passes 4`；在保形压缩 plan 下，一个 pass 约 50 分钟，4 个 pass 约 3.3 小时。
  文档没有写这个量级，容易被低估。

### 7.4 共享暂存目录里的旧账本先做了备份

`start-targets.sh` 会 `rm -f /tmp/wf-bench/ledger-main.jsonl /tmp/wf-bench/ledger-direct.jsonl`，
而 `/tmp/wf-bench/` 是跨会话共享的。启动靶机前先把三个既有账本原样备份到
`/tmp/wf-bench/archive-before-e5b/`：

```bash
$ ls -l /tmp/wf-bench/archive-before-e5b/
-rw-r--r-- 1 paff users 28520971 Oct  8 17:29 ledger-direct.jsonl
-rw-r--r-- 1 paff users  404602 Oct  6 22:45 ledger-main.jsonl
-rw-r--r-- 1 paff users  1294477 Oct  6 21:21 target-ledger.jsonl
```

（这三个文件都不是本轮的产物；备份是为了不把别的会话的原始数据顺手删掉。）

### 7.5 遗留：本轮**没有**验证到的部分

- 绘图：分析器只写 `plots/SKIPPED.md`（E4 的既定形态，不是本轮缺陷）。
- `benchmarks/WinForward.Benchmarks/` 的三个超限文件（AC1 的已知债务）不在扫描范围内。
- 逐包身份：账本没有 arm/row/product 归属，因此「去程损坏」仍只能以 target 侧总量呈现
  （`harness-audit.md` §四 第 5 条的口径披露，本轮不改变）。

### 7.6 `publish-campaign.sh` 今天跑不完，所以拉回步骤按其自身三条命令改指本轮的树

`scripts/publish-campaign.sh` 有**四处**过期事实，任何一处都足以让它无法完成：

```bash
$ rg -n 'results=|Compress-Archive|^cd "\$results|analyze.sh' scripts/publish-campaign.sh
8:results="$repo/benchmarks/results/2026-10-06-e2e-competitors"
15:"$wf" run '... Compress-Archive -Path C:\wfbench\results\* -DestinationPath C:\wfbench\campaign.zip -Force; ...'
36:cd "$results/analysis"
37:bash "$repo/benchmarks/WinForward.E2E.Analysis/scripts/analyze.sh" --raw "$raw" --out "$results"
```

1. **第 8 行的目标目录已不存在**（随 Python 分析器一起在 `b3b4aa4` 删除，且没有被 git 跟踪）：

   ```bash
   $ ls -d benchmarks/results/2026-10-06-e2e-competitors
   ls: cannot access 'benchmarks/results/2026-10-06-e2e-competitors': No such file or directory
   $ git ls-files benchmarks/results/2026-10-06-e2e-competitors | wc -l
   0
   ```

2. **第 36 行 `cd` 进那个已删除的 `analysis/` 子目录**，在 `set -e` 下直接终止脚本。
   把该行原样执行：

   ```bash
   $ (cd benchmarks/results/2026-10-06-e2e-competitors/analysis)
   bash: line 1: cd: benchmarks/results/2026-10-06-e2e-competitors/analysis: No such file or directory
   exit=1
   ```

   （这一步本身是多余的：`analyze.sh` 的头部注释写明它**从不改变工作目录**。）

3. **第 15 行把 VM 侧源目录写死成 `C:\wfbench\results`**，即编排脚本 `-OutRoot` 的默认值。
   本轮为不动 VM 上上一轮 campaign 的唯一副本，用的是 `-OutRoot C:\wfbench\results-e5b`，
   于是这条命令取不到本轮数据。

4. **第 28-30 行找的账本名 `start-targets.sh` 从来不写**：靶机写的是
   `ledger-main.jsonl` / `ledger-direct.jsonl`，而 `/tmp/wf-bench/target-ledger.jsonl`
   是一个 Oct 6 的**陈旧残留**（1294477 字节，`Oct 6 21:21`）。第 3 步不会报缺失，
   而是把这个旧账本当成本轮的结果静默拷进 `raw/`——比「找不到」更坏的一种失败。

```bash
$ rg -n 'ledger-' scripts/start-targets.sh
22:rm -f "$ledgers/ledger-main.jsonl" "$ledgers/ledger-direct.jsonl"
25:    --ledger "$ledgers/ledger-main.jsonl" &
29:    --ledger "$ledgers/ledger-direct.jsonl" &
$ rg -n 'target-ledger' scripts/publish-campaign.sh
28:if [ -f /tmp/wf-bench/target-ledger.jsonl ]; then
29:    cp /tmp/wf-bench/target-ledger.jsonl "$raw/target-ledger.jsonl"
$ ls -l /tmp/wf-bench/target-ledger.jsonl
-rw-r--r-- 1 paff users 1294477 Oct  6 21:21 /tmp/wf-bench/target-ledger.jsonl
```

**本轮的处置（偏离）**：不去改动 VM 的目录布局去迎合一个坏脚本（`C:\wfbench\results` 很可能是
上一轮 campaign 在本机的唯一副本，动它有风险），而是**按该脚本自己的第 1-3 步，把路径改指本轮的树**
（源 `C:\wfbench\results-e5b`、zip 名 `campaign-e5b.zip`、落地到 `/tmp/wf-bench/e5b-campaign/`），
账本改用 `start-targets.sh` 真正写的两个文件（`ledger-main.jsonl` 代理车道 + `ledger-direct.jsonl`
直连车道），放在**分析器文档化的位置**（`raw/` 旁边，`raw/` 里不放账本——放两份会被
`LedgerLocator` 全部收进来、每个汇总数两遍，见 2.7），然后按指令的后半句用 `analyze.sh` 分析。
第 2.6 节给出逐步原始输出。

**为什么 2 个 pass 而不是按时间预算砍成 1 个**：`-Passes 2` 是任务书里列在第一位的参数，
且 `environment.json` 会把启动参数记成 `passes=2`；如果只跑 1 个 pass 却保留 `passes=2` 的记录，
原始数据里就会留下一个「声明 2 个、实际 1 个」的自相矛盾——而这类声明与实际的错位正是本任务
（也是 7.2/7.6）要消灭的东西。代价是一个 pass 约 50 分钟，本轮可接受。

### 7.7 建议登记的 ticket（本轮发现，未修）

按「是不是本轮该改的」排序。前三条是 harness 资产，第四条是脚本漂移，
最后两条是分析器/靶机的语义问题，都需要产品负责人决定归属。

| # | 缺陷 | 证据位置 | 建议 |
| --- | --- | --- | --- |
| T1 | `start-targets.sh` 不给靶机 `--label`，于是账本无 per-run 归属；dual 相位两条车道并行时，`ledger-window-ambiguous` 必然对每个 dual 臂各报一次、端点分区检查必然误报 `ledger-endpoint-overlap`（本轮 60 + 10 条） | 2.7、4.1.1 | 让靶机能按 run 归属（每 run 重启靶机 / 在账本记录里带客户端 label / 或者干脆声明 dual 相位不参与端点分区检查），并把这三种选择写进分析器 README |
| T2 | 靶机源端点普查表打满后，账本的数据报计数在 pass 2 读成 **0**，`ledger-datagram-mismatch` 变成噪声（本轮 297 条 `ledger-*` caveat 的主体） | 4.1.2 | 先定位「一个数据报一个源端点」是不是产品中继端口的翻腾所致，再决定是放大普查表、按区间复位，还是在溢满时让账本显式作废该窗口 |
| T3 | `orchestrator.ps1` 的 `Write-Log` 走 `Write-Output`，而三处 `[void](Invoke-Client ...)` 把它的日志行一起丢掉，于是 `client <label> exit=<n>` 从不落盘 | 2.5、7.1 | `Write-Log` 改 `Write-Host`，或调用点接住管线输出再转发；在此之前 AC18 的「退出码 0」只能靠代码等价推导 |
| T4 | `deploy-campaign.sh` 从没有生成者的 `/tmp/wf-bench/deploy/` 取源（本轮因此推上去一份**旧编排脚本**），`publish-campaign.sh` 的目标目录已被删除、`cd` 进已删除的 `analysis/`、VM 源写死 `C:\wfbench\results`、找的账本名 `start-targets.sh` 从不写 | 2.2、7.2、7.6 | 两个脚本都改成从 `scripts/` 与 `scripts/plans*/` 直接取源、目标目录可用环境变量覆盖；这是「按 AGENTS.local.md §8 复现一轮」目前唯一的硬阻塞 |
| T5 | `wf.sh` 的 `repl_cmd` 在 `send-keys` 之后立刻判提示符，传输未完成就返回；`up`/`down` 的调用者若不核对尺寸会静默拿到半截文件 | 2.6 | 传输后强制「本地尺寸 == 远端尺寸」并重试；`deploy-campaign.sh` 的 `stage()` 已经是正确写法，把它抽成 `wf.sh` 的一部分 |
| T6 | `LedgerLocator` 把 pass 目录、`--raw`、`--raw` 父目录三处的账本**全收**（按路径去重），所以同一个账本换个位置再放一份就会被数两遍（本轮实测 `148188 = 2 × 74094`）。而 `verification/synthetic/make_tree.py:15` 的 docstring 声称「a `target-ledger.jsonl` per pass」，**生成器并不写这种文件**（`rg -n "target-ledger" make_tree.py` 只命中 docstring 这一行），冻结树 `synthetic-tree.tar.gz` 里也只有 `raw/` + 两个 beside-raw 账本。照 docstring 把账本同时放进 `raw/passN/` 的人会踩到重复计数 | 2.7 | 修 `make_tree.py` 的 docstring（它现在是唯一说「每 pass 一份账本」的地方），或在 `LedgerLocator` 里明确「第一个有匹配的目录胜出」并写进分析器 README |
