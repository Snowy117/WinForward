# AC0 残余归因 —— halfClose=100 臂的 attempt 级证据

任务：`.trellis/tasks/10-09-tcp-close-drain-primary`（AC0，见 prd.md 的 AC0 与 design.md §9）。
代码基线：HEAD `0aa6919`（洁净 worktree，未改动主工作树；功能状态等于 `7cababb`）。
结论一句话：**残余是混合的，以读法 (a) 为主** —— 两次 `halfClose=100` 臂合计 105 个 timeout 中，92 个（87.6 %）是「echo 8192 + 尾部 768 全部收到、`eof=false`、close 包从未落地」；13 个（12.4 %）是 gap 类（3 个尾部 gap + 10 个整段响应都没到的停滞）。

---

## 1. 现场状态与时间线

| 项 | 值 |
| --- | --- |
| Linux 靶机 | `192.168.100.4:40020` TCP+UDP、`192.168.100.4:40053` DNS；target pid `45349`（实验结束按 pid 停止） |
| Windows VM | `192.168.100.2`（WinLTSC），经 tmux `wfbench` + `scripts/wf.sh` 驱动 |
| 产品沙箱 | `C:\wf-ac0`（保留，Phase D 复用）；`C:\wfbench` 全程只读未写 |
| WinForward | 自建 FDD `C:\wf-ac0\wf-fdd\WinForward.exe`（`run` 动词），pid `4112`（主臂）/ `4200`（Debug 臂），结束按 pid 停止 |
| sing-box | `C:\wf-ac0\singbox\sing-box.exe`（从 `C:\wfbench\singbox` 复制），pid `4696`，监听 `127.0.0.1:1080` |
| 客户端 | 自建 win-x64 FDD `C:\wf-ac0\e2e\WinForward.E2E.exe`（进程名匹配 `WinForward.E2E.exe` 的 proxy 规则） |
| 防火墙 | 跑臂前 `Domain/Private/Public=False`，结束后已恢复 `True/True/True`（已复验） |
| 时钟 | VM `(Get-Date).ToUniversalTime()` 与主机 `date -u` 成对记录见下；`w32tm /resync /force` **失败**：`The service has not been started. (0x80070426)` |
| 看门狗 | 任务 `wfbench-watchdog` 当时 **Enabled**，在 09:49:04Z 触发过一次 emergency teardown（按名字杀掉 `WinForward`/`sing-box` 等并恢复防火墙），实验期间手动 `Disable-ScheduledTask`，结束后已 `Enable-ScheduledTask` 还原（Enabled=True） |
| 残留进程 | 无 orchestrator/sing-box/WinForward 残留；`C:\wfbench\heartbeat.txt` 在实验前已陈旧（09:23:05Z） |

时钟原始记录（VM / 主机，两次工具调用各记一次，非同一瞬间，但误差在同一秒量级）：

- 09:49:46.6003550Z（VM） / 09:49:46Z（主机，同一批次）
- 09:52:03.6909028Z（VM） / 09:52:03Z（主机，同一批次）
- 09:56:40.0799362Z（VM） / 09:56:40Z（主机，同一批次）

交叉核对：`run.json.startedUtc`（VM 时钟）与靶机账本首条 `tcp.utc`（主机时钟）相差 −193 ms（主臂）与 −254 ms（Debug 臂），即主机当前约比 VM 慢 0.2–0.25 s。这个量级不影响本任务判定（窗口宽 40 s，分类只用 attempt 记录）。

实验时间线（UTC）：

| 时刻 | 事件 |
| --- | --- |
| 09:49:02Z | 看门狗触发（实验前，已记录；它在 09:49:04 重写了 heartbeat） |
| 09:49:46Z | 禁用看门狗、建沙箱目录、复制 sidecar；VM/主机对时记录 |
| 09:50:21Z | sing-box + WinForward 启动，`Interception started`，防火墙已关 |
| 09:50:35Z | 主臂开始（`--label ac0-halfclose`） |
| 09:51:14Z | 主臂结束（38.2 s） |
| 09:54:55Z | WinForward 以 `--config appsettings-debug.json` 重启（Debug 级） |
| 09:54:59Z | Debug 臂开始（`--label ac0-halfclose-debug`） |
| 09:55:39Z | Debug 臂结束（39.8 s） |
| 09:56:19Z | 按 pid 停止 WinForward(4200) / sing-box(4696)（主臂的 4112 在重启前已停止） |
| 09:56:40Z | 防火墙恢复 True、看门狗重新启用；VM/主机对时 |
| 09:57Z | Linux target pid 45349 停止；账本定稿复制回本地 |

---

## 2. 构建与产物（独立 worktree，未碰主工作树）

`@bash
git -C /home/paff/Projects/WinForward worktree add --detach /tmp/wf-ac0 HEAD   # HEAD = 0aa6919
cd /tmp/wf-ac0
dotnet publish benchmarks/WinForward.E2E/WinForward.E2E.csproj -c Release -r linux-x64 --self-contained false -o /tmp/wf-ac0-pub/linux
dotnet publish benchmarks/WinForward.E2E/WinForward.E2E.csproj -c Release -r win-x64  --self-contained false -o /tmp/wf-ac0-pub/win
dotnet publish src/WinForward.Cli/WinForward.Cli.csproj -c Release -r win-x64 -p:PublishAot=false --self-contained false -o /tmp/wf-ac0-pub/wf-fdd
`@

（CLI 项目自带 `PublishAot=true` 与 `RuntimeIdentifier=win-x64`；本机不能跨 OS 编译 NativeAOT，所以按 README 的 FDD 配方显式 `-p:PublishAot=false`。三个 publish 全部成功。）

| 产物 | sha256 |
| --- | --- |
| /tmp/wf-ac0-pub/linux/WinForward.E2E | 35802188398c3a0074f3ae16078a1f8f3473f12e32622bdca765429e38da893d |
| /tmp/wf-ac0-pub/win/WinForward.E2E.dll | 93200c772b09af6f8f1f008f365ddec100a384b1c1e49c2c4604b7726ae33f8e |
| /tmp/wf-ac0-pub/win/WinForward.E2E.exe | 307e3196f64df3bf905af04916d1e989ffa3cd6701970fc12f2dcbf123c4d908 |
| /tmp/wf-ac0-pub/wf-fdd/WinForward.exe | 3e52be682146c5e2e9506fa20598f815d008094a3ce19e25ef32d85e4c037238 |

worktree 用完已 `git worktree remove /tmp/wf-ac0`（干净移除，主工作树未被本任务改动）；发布产物保留在 `/tmp/wf-ac0-pub` 供对照。

---

## 3. 沙箱布置（`C:\wf-ac0`，保留）

部署方式：本地打三个 zip（`e2e-win.zip` / `wf-fdd.zip` / `stage.zip`），`wf.sh up <abs-local> C:/wf-ac0/<name>.zip` 上传，VM 上 `Expand-Archive -Force` 后删除 zip（临时文件已删）。

最终布局（跑完后复验）：

`@
C:\wf-ac0\
  e2e\        WinForward.E2E.exe/.dll/.deps.json/.runtimeconfig.json (+pdb)
  wf-fdd\     WinForward.exe、ndisapi.dll（从 C:\wfbench\wf-fdd 复制）、appsettings.json、appsettings-debug.json
  singbox\    sing-box.exe（从 C:\wfbench\singbox 复制）、config.json（日志改到沙箱内）、singbox.log
  plans\      ac0-halfclose.json
  out\        REL.jsonl、run.json                      # 主臂（Information）
  out-debug\  REL.jsonl、run.json                      # Debug 臂
  logs\       wf-info.err/out、wf-debug.err/out、singbox.err/out
`@

`C:\wf-ac0\wf-fdd\appsettings.json` 最终内容（与任务草案一致，未修改；首跑即正常代理，无需改配置）：

`@json
{
  "Logging": { "LogLevel": { "Default": "Information" } },
  "WinForward": {
    "Socks5Servers": [ { "Name": "main", "Host": "127.0.0.1", "Port": 1080, "UdpOverTcp": true } ],
    "LocalTargets": [ { "Name": "dns", "Host": "192.168.100.4", "Port": 53 } ],
    "Host": {
      "FallbackAction": "pass",
      "Rules": [
        { "Process": ["sing-box.exe"], "Action": "pass" },
        { "Process": ["WinForward.E2E.Direct.exe"], "Protocol": ["tcp", "udp"], "Action": "pass" },
        { "Protocol": ["udp"], "RemotePort": ["53"], "Action": "proxy", "Target": "dns" },
        { "Process": ["WinForward.E2E.exe"], "Protocol": ["tcp", "udp"], "Action": "proxy", "Target": "main" }
      ]
    },
    "Forwarded": { "FallbackAction": "pass", "Rules": [] }
  }
}
`@

`appsettings-debug.json` 是给 Debug 臂用的第二层（通过 `WinForward.exe run --config C:/wf-ac0/wf-fdd/appsettings-debug.json` 合并）：

`@json
{ "Logging": { "LogLevel": { "Default": "Debug" } } }
`@

`C:\wf-ac0\singbox\config.json`（只把 log.output 从 `C:\wfbench\...` 改到沙箱）：

`@json
{
  "log": { "level": "info", "timestamp": true, "output": "C:\\wf-ac0\\singbox\\singbox.log" },
  "inbounds": [ { "type": "socks", "tag": "in", "listen": "127.0.0.1", "listen_port": 1080 } ],
  "outbounds": [ { "type": "direct", "tag": "direct" } ]
}
`@

起停命令（VM，逐字）：

`@powershell
$s = Start-Process -FilePath C:/wf-ac0/singbox/sing-box.exe -ArgumentList run,-c,C:/wf-ac0/singbox/config.json -WorkingDirectory C:/wf-ac0/singbox -RedirectStandardOutput C:/wf-ac0/logs/singbox.out -RedirectStandardError C:/wf-ac0/logs/singbox.err -PassThru -WindowStyle Hidden
$w = Start-Process -FilePath C:/wf-ac0/wf-fdd/WinForward.exe -ArgumentList run -WorkingDirectory C:/wf-ac0/wf-fdd -RedirectStandardOutput C:/wf-ac0/logs/wf.out -RedirectStandardError C:/wf-ac0/logs/wf.err -PassThru -WindowStyle Hidden
# 停止（按 pid 点名）：
Stop-Process -Id <wf-pid> -Force; Stop-Process -Id <singbox-pid> -Force
`@

注意：产品日志实际都写在 `stderr`（JSON console logger），`wf.out` 是空的。

---

## 4. 跑臂：命令与 plan

Linux 靶机（后台，pid 写文件；账本放 `/tmp/wf-ac0-run/`，与任务里建议的 `/tmp/wf-ac0/ledger.jsonl` 不同——`/tmp/wf-ac0` 这次是 git worktree，写进去会弄脏它）：

`@bash
setsid nohup /tmp/wf-ac0-pub/linux/WinForward.E2E target --bind 192.168.100.4 \
  --tcp-port 40020 --udp-port 40020 --dns-port 40053 --label ac0 \
  --ledger /tmp/wf-ac0-run/ledger.jsonl > /tmp/wf-ac0-run/target.out 2>&1 < /dev/null &
echo $! > /tmp/wf-ac0-run/target.pid
`@

plan `C:\wf-ac0\plans\ac0-halfclose.json`：

`@json
{"arms":[{"name":"REL","kind":"reliability","seconds":30,"connectionsPerSecond":20,"modeMix":"halfClose=100"}]}
`@

客户端（两臂同一条，只有 out/label 不同；主臂原文）：

`@powershell
C:/wf-ac0/e2e/WinForward.E2E.exe client --target 192.168.100.4 --plan C:/wf-ac0/plans/ac0-halfclose.json --out C:/wf-ac0/out --label ac0-halfclose --tcp-port 40020 --udp-port 40020 --dns-port 40053
`@

结果拉回：`wf.sh down C:/wf-ac0/out/REL.jsonl <abs-local>`（run.json 同理）；Debug 臂在 `out-debug`。**日志文件被进程占用时下载会失败**，要先按 pid 停进程再 down。

有效性检查（全部通过）：

| 检查 | 主臂 | Debug 臂 |
| --- | --- | --- |
| `scheduledAttempts == connectAttempts` | 601 == 601 | 601 == 601 |
| `gates.clientSendLoss` | 0 | 0 |
| `attemptRecords` / `attemptRecordsOmitted` | 86 / **0** | 86 / **0** |
| 靶机账本行数（窗口内） | 601 | 601 |
| 靶机 mode/verdict/bytesEchoed | 601/601 `halfClose/halfClose/8192` | 601/601 同 |
| sing-box 入站 SOCKS 连接 | 合计 1202 条 `inbound connection to 192.168.100.4:40020`（两臂） | — |
| 账本记录全部落在 run 窗口内 | 是（窗口外 0 条） | 是（窗口外 0 条） |

两臂 `run.json` 窗口：

- 主臂 `ac0-halfclose`：09:50:35.6738461Z → 09:51:14.1463384Z（`wallSeconds=38.468`），planHash `f7f827aa4c7bb9c8`
- Debug 臂 `ac0-halfclose-debug`：09:54:59.7928999Z → 09:55:39.6351541Z

---

## 5. result 记录关键指标

| 指标 | 主臂 `ac0-halfclose` | Debug 臂 `ac0-halfclose-debug` |
| --- | --- | --- |
| scheduledAttempts / connectAttempts | 601 / 601 | 601 / 601 |
| observed.clean | 549 | 548 |
| observed.timeout | **52** | **53** |
| reset / unexpectedEof / halfCloseViolation / connectFail / otherError | 0 / 0 / 0 / 0 / 0 | 0 / 0 / 0 / 0 / 0 |
| truncated | 0 | 0 |
| fidelityMismatch / fidelityRate | 52 / 0.086522 | 53 / 0.088186 |
| expectedBytes | 8192 | 8192 |
| attemptRecords / attemptRecordsOmitted | 86 / 0 | 86 / 0 |
| achievedRate（目标 20/s） | 15.728 | 15.115 |
| meanConnectMs / meanTransferMs | 4.44 / 0.486 | 4.958 / 0.572 |
| 全臂 echoedBytes / trailerBytes | 4,890,624 / 457,984 | 4,874,240 / 455,936 |

合计口径自检（与逐条记录一致）：主臂 549×8192 + 48×8192 = 4,890,624、549×768 + 47×768 + 256 = 457,984；Debug 臂 595×8192 = 4,874,240、548×768 + 45×768 + 2×256 = 455,936。

---

## 6. attempt 分类（AC0 结论）

`halfClose` 语义：客户端先收 8192 字节 echo、再收 3×256=768 字节尾随数据，之后应当是 clean EOF（`eof=true`）；所有 timeout 的 `eof` 必然为 false。设计里的判别式：`echoedBytes==8192 && trailerBytes==768 && eof==false` → 读法 (a)「close 包没落地」；`trailerBytes < 768` → 读法 (b)「close 前面还有 gap」。

逐条统计（每个 timeout 的 attempt 记录；两臂的 timeout 记录 100 % 覆盖）：

| 形状 | 主臂 (52) | Debug 臂 (53) | 合计 (105) | 归入 |
| --- | --- | --- | --- | --- |
| `echoed=8192, trailer=768, eof=false` | **47** | **45** | **92（87.6 %）** | 读法 (a) |
| `echoed=8192, trailer=256`（3 帧尾随只到 1 帧） | 1 | 2 | 3 | 读法 (b)，尾部 gap |
| `echoed=0, trailer=0`（请求发出，整段响应一字未到） | 4 | 6 | 10 | 读法 (b)（整段 gap，见下） |
| 其它形状 | 0 | 0 | 0 | — |

因此：**残余是混合，以 (a) 为主**——约 88 % 的 timeout 是「数据全到、close 从未被客户端看到」，约 12 % 是「close 前面有 gap」。整臂口径：105 / 1202 = 8.7 % 的尝试挂到客户端 10 s 超时。

支撑证据（同一次 Debug 臂的产品日志，`wf-debug.err.jsonl`，4222 行）：

- `tcp.relay.ended` 601 次，**outcome 全部 cleanEnded**（`Stalled=0`、`Faulted=0`）；`TCP relay for session` 行共 1202 = 601 started + 601 ended。
- `Client-visible close injected` **601 次，outcome 全部 injected**；`Client reset injected` 0 次。
- `relaySetupFailed=0`、`failed with=0`、`batch-failed=0`、`deferred=0`；Warning/Error 只有 3 条且与注入路径无关（配置 warning 1、read-shape 1、GC 心跳 1）。
- 把 Debug 臂的 53 个 timeout 按「客户端连接创建顺序 ↔ connectionId 递增」映射到日志（映射校验：601/601 匹配，账本 `utc` 与日志时间差稳定在 418–504 ms 的窄带内），**53/53 都能查到自己的 `Client-visible close injected` 事件**；这些会话的 relay 生命周期（`TCP relay for session` established → cleanEnded）**只有 1–5 ms**（全臂 p50 = 2 ms、max = 26 ms），而客户端却等满 10 s。

机制读法：close 路径对每一个挂住的连接都执行了、且日志记为 `injected`，但客户端从未看到；会话在毫秒级 retire，此后没有重传、也没有 alias 可解析——这正是 design.md §1 的「single-shot」性质。10 个 `echoed=0` 的极端例（靶机账本明确记录 `bytesEchoed=8192`，客户端却 0 字节）说明同一性质也会吃掉**数据本身**：响应还在 MSTCP 发送队列里时会话就 retire 了，首传没被注入，之后的重传又落到 tombstone，客户端自然一无所获。按设计的 `trailerBytes < 768` 规则它们归 (b)，但机制上是「整段 gap」而不是「尾部 gap」，报告里单列。

靶机侧对照（同一份账本，两臂窗口内 1202 行）：`mode=halfClose` 1202、`verdict=halfClose` 1202、`bytesEchoed=8192` 1202——target 每次都完整回显、每次都走完了半关闭尾随流程，缺陷只在产品的中继/close 路径上。

---

## 7. 异常、不确定性与原始证据

- `w32tm /resync /force` 失败（Windows Time 服务未启动，`0x80070426`）。实测成对记录偏差 < 1 s（见 §1），足以支撑本任务；交叉核对显示主机比 VM 慢约 0.2–0.25 s，已原样记录。
- 主臂是 Information 级日志：`tcp.redirect.*`/`tcp.relay.*`/`tcp.redirect.clientClose` 全部是 `LogLevel.Debug`（`src/WinForward.Runtime/Logging/TcpRedirectLog.cs`），所以「注入次数 / relay outcome」的计数来自 Debug 臂，而不是主臂；主臂的 AC0 分类只依赖 attempt 记录（这正是设计要求的零代码成本证据）。
- 10 个 `echoed=0` 的停滞被归入 (b) 是遵循设计给的判别式；它们是否与「首传丢失 + retire 后 tombstone」完全同因，本次无法再细分（日志没有逐字节计数），建议 Phase D 的 drain 臂顺带观察它们是否归零。
- 看门狗是本实验最大的现场风险：它当时 Enabled 且每分钟检查一次，心跳一陈旧就会杀 `WinForward`/`sing-box` 并恢复防火墙。因为不能写 `C:\wfbench`（心跳路径），唯一安全做法是实验期间禁用任务，结束后还原——报告如实记录了这一改动。
- 整个实验期间 `C:\wfbench` 未被我方写入：跑完后目录里最新的文件是 `watchdog.log`/`heartbeat.txt`（09:49:04Z，看门狗自己写的），早于本任务第一次写操作（09:49:46Z）。沙箱 `C:\wf-ac0` 按任务要求保留，只删了上传用的三个 zip。
- Debug 臂第二次采样与主臂一致（52 vs 53 个 timeout；形状分布 47/1/4 vs 45/2/6），说明结论不是单次噪声。

原始文件（均在 `.trellis/tasks/10-09-tcp-close-drain-primary/research/verification/ac0/`）：

| 文件 | 内容 | sha256 |
| --- | --- | --- |
| REL.jsonl | 主臂全部记录（result/attempt/sample） | 0a77f7349fb8125df2d0dbf24749259106cc542ccce70ee26cb3fdad624b035d |
| run.json | 主臂窗口与环境 | 17e494ea91c556611266c7764807735c9e2806e96763955625c0a79d9a59d3e5 |
| ledger.jsonl | 靶机全量账本（1669 行：tcp 1202 + udpSummary 464 + dns/tcp/targetSummary 各 1） | 383e9e4cb0324f6609551497b43cf62b003dfb76a629a31ea844109b695789df |
| target.out | 靶机 stdout | bee998c3f9f3da565b4ade98be2aa928563cefe4453533a4c6c48a4cbc934adf |
| debug/REL.jsonl | Debug 臂全部记录 | f04354563f295ba268d78ad774597ef0cbc39c398be3dddd0738d0e0d453a664 |
| debug/run.json | Debug 臂窗口 | fa09b028e47fba3ceb392afe2bc267bfc8dd2ebadf9c2eca9f8d7f36ea87a838 |
| debug/wf-info.err.jsonl | 主臂产品日志（Information，25 行） | 4afc4dcdda72f76963f57c6dcddb1ee2f0c21c44a9c83a98d22bbdc5bb1e907e |
| debug/wf-debug.err.jsonl | Debug 臂产品日志（4222 行，含 relay/close 事件） | 4a6b543969c38ee57b5919f21502ae79f338acf7e096896948dbe4a78b696554 |
| debug/singbox.log | sing-box 日志（1202 条 SOCKS 入站） | 8aa09c2d6306e2ff7a1f063ee60151d56786d8b9c2ff39be5a13867cc2851e8f |

---

## 8. Phase D 复用说明

**沙箱**：`C:\wf-ac0` 已保留（e2e/wf-fdd/singbox/plans/out/out-debug/logs）。Phase D 需要把新构建的 `WinForward.exe` 覆盖进 `C:\wf-ac0\wf-fdd\`（`ndisapi.dll` 已在），客户端同理覆盖 `C:\wf-ac0\e2e\`；配置、plan、日志目录都可直接复用。`C:\wfbench` 仍然只读。每个 run 启动前照旧先查 `scheduledAttempts == connectAttempts`、`attemptRecordsOmitted == 0`、账本窗口外 0 条。

**VM 侧固定流程**（全部经 `benchmarks/WinForward.E2E/scripts/wf.sh`）：

1. 关防火墙：`Set-NetFirewallProfile -Profile Domain,Private,Public -Enabled False`；跑完 `... -Enabled True`。
2. **禁用看门狗**：`Disable-ScheduledTask -TaskName wfbench-watchdog`，跑完 `Enable-ScheduledTask -TaskName wfbench-watchdog`。不禁用的话它会在心跳陈旧时按名字杀掉 `WinForward`/`sing-box` 并恢复防火墙（本任务就撞上它 Enabled）。
3. 起 sing-box（命令见 §3），确认 `127.0.0.1:1080` Listening。
4. 起产品：`WinForward.exe run`（**必须带 `run`**，无参数只打印帮助）。想看 relay/close/drain 事件就加 `--config C:/wf-ac0/wf-fdd/appsettings-debug.json`（默认 Information 会把这些 Debug 事件全滤掉）。
5. 跑臂：客户端命令见 §4，`--out C:/wf-ac0/out-<name> --label <name>`。
6. 停止：`Stop-Process -Id <pid> -Force`（sing-box、WinForward；**产品日志在进程存活时被锁住，`wf.sh down` 会报 being used by another process，先停再拉**）。
7. 拉结果：`wf.sh down C:/wf-ac0/out-<name>/REL.jsonl <abs-local>`。

**Linux 靶机**（本机，命令见 §4）：本次用 `--label ac0`、端口 40020/40053、账本 `/tmp/wf-ac0-run/ledger.jsonl`、pid 存 `/tmp/wf-ac0-run/target.pid`。停止一律 `kill <pid>`（按 pid 点名，不用 pkill）。注意账本里 `connectionId` 每臂都从 `0x52450000` 重新开始，跨臂 join 必须先用 `run.json` 的 UTC 窗口定界。

**wf.sh 使用坑（本次踩到）**：PowerShell 单行里不要出现单引号（会截断 bash 引号）；路径统一写正斜杠 `C:/...`（反斜杠在多层命令传递中会丢，`C:\wfbench\x` 会变成 `C:wfbenchx` 并被解析成相对路径）；`;` 串起来的命令里若某条抛 statement-terminating 错误（例如对 `$null` 调方法），evil-winrm-py 的 `Invoke-Expression` 会中止整行后续输出——避免制造错误，或把易错段放最后。

**产出**：本文件与 `research/verification/ac0/` 下的原始 JSONL/日志副本。
