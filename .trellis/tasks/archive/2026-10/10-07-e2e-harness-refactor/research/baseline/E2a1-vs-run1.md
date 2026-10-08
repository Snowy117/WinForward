# E2-a1 回归比对（2a-1 零散修复：`TcpCommand` 兜底 / `Target/Sockets.cs` / 端口冲突 / `payloadBytes` 上限 / 越带读数计数，对照基线 run1）

本批是 **E2-a 的第一小批（2a-1）**：只做与传输接缝无关的 5 项零散修复，**不碰 `Client/Lanes/`、
不碰 `src/`、不改任何 JSON 字段名与契约类型**。因此本轮的等价性要求比后续批次更硬：

| 项 | 位置 | 判据 |
|---|---|---|
| 1 | `Wire/TcpCommand.cs`：两个 `Name` 去掉兜底，未定义成员**抛异常** | `rg '_ =>'` 该文件零命中；已发布的 `mode`/`verdict` 名字集与 run1 逐个相同 |
| 2 | `Target/Sockets.cs`（新）：三台 server 共用 bind；Unix 显式清 `SO_REUSEPORT` | 残留实例再 bind → 发布二进制**响亮** `EADDRINUSE`（含只撞 UDP 端口的情形） |
| 3 | `TargetRunner.ValidatePorts`：显式拒绝 `dnsPort ∈ {tcpPort, udpPort}` | 单测 + 一条命令（退出码 2，错误指名两个选项） |
| 4 | `FrameCodec.MaxPayloadLength` 提为 `internal` 并用于 plan 校验 | 超限 → 退出码 2，`'payloadBytes' is <v>, which is outside 0..<max>`；`0` 仍合法 |
| 5 | `scripts/compare-records.py`：默认 summary 增**越带读数计数**（D17.4） | 改大一个 `latency/*` 读数 → 计数变化、退出码不变；`--strict` 仍逐条列 |

判定线：**结构差异为空（含改名表登记的键集变化）、契约计数零越带（零宽带宽路径按 D17.2 标注并附同二进制
确认运行）、读数只作信息性输出；`--batch B2` 9/9 satisfied**。

---

## 1. 取得方式

```bash
benchmarks/WinForward.E2E/scripts/publish.sh                        # 退出码 0（门禁 1）
cd benchmarks/WinForward.E2E
scripts/selftest.sh scripts/plans/selftest-plan.json                # 退出码 0（门禁 4；完整输出落 $work/client.out）
cp -r /tmp/wf-bench/selftest/{out,ledger.jsonl,target.out,client.out} /tmp/e2a1/run/

R=.trellis/tasks/10-07-e2e-harness-refactor/research
CR=benchmarks/WinForward.E2E/scripts/compare-records.py
python3 $CR $R/baseline/run1 /tmp/e2a1/run --normalize $R/record-normalize.json \
        --band $R/baseline/jitter-band.json --rename-table $R/contract-rename.json --batch B2 --strict
```

| 侧 | 树 | `linux/WinForward.E2E.dll` sha256 | `linux/WinForward.E2E.Contracts.dll` sha256 |
|---|---|---|---|
| `run1`（基线） | A0：HEAD `d90707e` | `2eb371625f7f055c594d1de3f77069a5be545cdc5ec9d7cdfaeade810fa51a58` | 无此文件 |
| `e2a1`（本轮） | HEAD `0d066c9` + 本轮未提交工作树 | `e47560a50ce1eafd7d9bcfc88bc10f19ae790cde4bba6efb2be2a79b50771d3c` | `544ec36dde995b9894620d8d5ee63271c693234031f1be0792e30ac7dc2a6e52` |

**Contracts dll 的 sha256 与 B2c 报告里的不同，是 E1 自己的收尾提交造成的**：B2c 的证据落在 `42e13f9`，
之后 `925c695`（"finish the typed contract and retire the dictionary carriers"）才把 `ArmKeys.*` 与
`ArmParameters` 提交进 `WinForward.E2E.Contracts`（`git log 42e13f9..HEAD -- benchmarks/WinForward.E2E.Contracts`
只有这一个提交）。本轮**没有**改它：`git status` 里没有它的任何文件。

本轮共跑 **9 次** selftest，分三代构建（宿主负载是本轮最重要的自变量，所以逐次记下来）：

| 代 | 二进制 | 运行 | 产物目录 | 宿主 load average |
|---|---|---|---|---|
| 最终（§1 的 sha256） | `e47560a5…` | **`F1`–`F4`**（主证据） | `/tmp/e2a1/F1` … `/tmp/e2a1/F4` | 1.2–1.7 |
| 上一代（**只差一句 XML 注释**：`ClearReusePort` 的 `<summary>`） | `00062e79…` | `L1`–`L4` | `/tmp/e2a1/L1` … `/tmp/e2a1/L4` | ≈3.0 |
| 更早一代（只差 `Sockets.cs` 里一条异常消息的构造写法，字符串值逐字相同） | `01c13416…` | `run0` | `/tmp/e2a1/L1`（被 `L1` 覆盖；它的比对输出仍在 `/tmp/e2a1/compare-strict.txt`） | ≈1 |

产物留在 `/tmp/e2a1/`（未随任务提交，与 B1b/B1c/B2a/B2b/B2c 相同：四条命令随时可重建）。
每代的比对输出（都带 `--strict`）：`F1`–`F4` → `/tmp/e2a1/compare-F{1,2,3,4}.txt`、`L1`–`L4` →
`/tmp/e2a1/compare-L{1,2,3,4}.txt`、`run0` → `/tmp/e2a1/compare-strict.txt`。
`/tmp/e2a1/old-bin/` 是 `git archive HEAD`（改动前源码）单独 publish 出来的**改动前二进制**，只用于 §2.2 的负控。

---

## 2. 本批 5 项的落地与观测

### 2.1 `TcpCommand.Name`：兜底字面量消失，未定义成员抛异常

```bash
rg -n '_ =>' benchmarks/WinForward.E2E/Wire/TcpCommand.cs          # exit 1（零命中）
```

**为什么是查表 + `throw` 而不是 `switch` 表达式**：`rg '_ =>'` 要零命中，就必须没有 discard 分支；
而"每个分支都 `return`"的 `switch` **语句**会被 `IDE0066`（Use switch expression，info 级）判红——
实测两种 `switch` 形状（带 `default: throw` 与不带 `default` 后置 `throw`）都在 `dotnet format` 下报
`info IDE0066`。改成 `Dictionary<TcpMode, string>` / `Dictionary<TcpVerdict, string>` 两张表 +
`TryGetValue` 未命中即 `throw new ArgumentOutOfRangeException`：既没有兜底分支，也不触发任何 fixer，
且"哪个成员叫哪个名字"仍是逐行的显式映射（与 `ArmKind.BuildIndex` 的既有形状一致）。

| 观测 | 命令 / 位置 | 结果 |
|---|---|---|
| 兜底分支零命中 | `rg '_ =>' benchmarks/WinForward.E2E/Wire/TcpCommand.cs` | exit 1 |
| 未定义成员抛异常 | `TcpCommandTests.AnUndefinedModeHasNoPublishedName` / `…VerdictHasNoPublishedName` | 两条新用例，`ArgumentOutOfRangeException` |
| 已发布名字集不变 | `tcpSummary.verdicts` 的 8 个键 | 与 run1 **逐键逐值相同**：`clean 61 / reset 25 / partialFin 25 / halfClose 25 / stall 0 / clientClosedEarly 21 / protocolError 0 / error 0` |
| 已发布 `mode`/`verdict` 字符串集不变 | ledger 的 `type:"tcp"` 记录 | 两者都与 run1 相同：mode `{clean, halfClose, partialFin, resetAfterN, unknown}`、verdict `{clean, clientClosedEarly, halfClose, partialFin, reset}` |
| 结构栏 | `compare-records.py` 第 1 栏 | 0（`verdicts` 是 ledger 的**键集**：名字少一个或多一个都会变成结构差异） |

`unknown` 仍是合法的 `mode` 值：它来自 `TcpTargetServer` 的 `command.ModeKnown ? Name(mode) : "unknown"`
（"命令读不出来"），与枚举名表无关；`EveryDefinedModeHasItsOwnName` 断言的是**枚举成员的名字**不含
`unknown`，这正是该用例今天仍然为真的原因。

### 2.2 `Target/Sockets.cs`：三台 server 共用一个 bind 策略

新增文件 `benchmarks/WinForward.E2E/Target/Sockets.cs`（130 行 / 59 有效行，主类型 `Sockets`）：

```csharp
internal static class Sockets
{
    internal static Socket BindTcpListener(EndPoint endPoint);   // ReuseAddress=true + Unix 清 SO_REUSEPORT + Bind + Listen(512)
    internal static Socket BindUdp(EndPoint endPoint);           // 不设任何 reuse 选项 + Unix 清 SO_REUSEPORT + Bind
    internal static EndPoint SourceTemplate(EndPoint endPoint);  // ReceiveFromAsync 的源模板（UdpEchoServer/DnsServer 逐字重复的那 3 行）
}
```

迁移点（4 处 socket 构造 → 3 个调用点）：

| server | 之前 | 现在 |
|---|---|---|
| `TcpTargetServer`（1 个 TCP listener） | 构造 + `ReuseAddress` + `Bind` + `Listen(512)` | `Sockets.BindTcpListener(endPoint)` |
| `UdpEchoServer`（1 个 UDP socket） | 构造 + `ReuseAddress` + `Bind` + 源模板 | `Sockets.SourceTemplate` + `Sockets.BindUdp` |
| `DnsServer`（UDP + TCP 同一个端口） | 两个 socket 各自构造 + `ReuseAddress` + `Bind`（TCP 再 `Listen(512)`）+ 源模板 | `Sockets.SourceTemplate` + `Sockets.BindUdp` + `Sockets.BindTcpListener` |

**bind 失败会被包一层，把端点名字写进消息**（`Program.cs` 的 `catch (SocketException)` 仍然命中，
所以退出码仍是 1）：

```
e2e target: cannot bind 127.0.0.1:31310: Address already in use
```

包装时携带的是 `(int)exception.SocketErrorCode` 而不是 `NativeErrorCode`——实测（Linux）：真实 bind 失败
的 `SocketErrorCode=AddressAlreadyInUse(10048)` / `NativeErrorCode=98`，用 10048 重建两个属性都与原异常
相同，用 98 重建则 `SocketErrorCode` 变成 `(SocketError)98`（`SocketsTests` 断言的正是 `SocketErrorCode`）。

#### 2.2.1 为什么"只清 SO_REUSEPORT"不够（实测，DD 的原方案在 UDP 上半途而废）

DD/审计的原话是"`ReuseAddress` 在 Linux 上顺带打开 SO_REUSEPORT → 显式清零即可响亮 `EADDRINUSE`"。
本轮用独立探针（`/tmp/sockprobe/probe.cs`，**不是仓库代码**）逐个实测了内核语义：

```
fresh udp: REUSEADDR(raw 2)=0 REUSEPORT(raw 15)=0
after SetSocketOption(ReuseAddress,true): REUSEADDR(raw 2)=1 REUSEPORT(raw 15)=1     <- 审计结论复现
SocketOptionName.ReuseUnicastPort -> SocketException: Operation not supported        <- .NET 在 Unix 没有可移植名字
ReuseAddress=true then SetRawSocketOption(1,15,0): REUSEADDR(raw 2)=1 REUSEPORT(raw 15)=0   <- 清零本身有效

udp 双方 ReuseAddress=true + SO_REUSEPORT 已清零（DD 的原方案）:
  first : bind OK (RA=1 RP=0)
  second: bind OK (RA=1 RP=0)                      <- 仍然静默双绑！
udp 不设 ReuseAddress + SO_REUSEPORT 已清零（本文件的方案）:
  first : bind OK (RA=0 RP=0)
  second: bind FAIL AddressAlreadyInUse            <- 这才是"响亮"
```

Linux 上 **`SO_REUSEADDR` 单独就足以让第二个数据报 socket 绑上同一个地址端口**（内核 6.18 实测；
TCP listener 则不同：两个 `SO_REUSEADDR` listener 的第二个会在 `listen()` 处 `EADDRINUSE`）。
所以 `Sockets.BindUdp` **不设任何 reuse 选项**——UDP 没有 TIME_WAIT，停止的实例端口立刻释放，
不需要 `SO_REUSEADDR`；`BindTcpListener` 保留它（重启要能绑 TIME_WAIT 里的端口）。
清 `SO_REUSEPORT` 仍然照 DD 落地：它管的是 **TCP listener** 那一半（旧实例 + `RP=1` 时第二个 listener
会 bind 成功），并且让"新二进制撞旧二进制"也响亮。

#### 2.2.2 发布二进制的观测（最终二进制）

```
$ NEW target --bind 127.0.0.1 --tcp-port 31410 --udp-port 31410 --dns-port 5330 --ledger …   # 后台，先起
$ NEW target --bind 127.0.0.1 --tcp-port 31410 --udp-port 31410 --dns-port 5330 --ledger …
tcp-collision exit=1
e2e target: cannot bind 127.0.0.1:31410: Address already in use

$ NEW target --bind 127.0.0.1 --tcp-port 31420 --udp-port 31421 --dns-port 5331 --ledger …   # 后台，先起
$ NEW target --bind 127.0.0.1 --tcp-port 31430 --udp-port 31421 --dns-port 5332 --ledger …   # 只撞 UDP 端口
udp-collision exit=1
e2e target: cannot bind 127.0.0.1:31421: Address already in use
```

第二条是本项的真正判据：新实例的 TCP 端口（31430）与 DNS 端口（5332）都绑成功了，只在 **UDP 端口**
上失败——DD 的原方案在这条命令下会**静默双绑**。

#### 2.2.3 负控：改动前的二进制（`git archive HEAD` 单独 publish）

```
$ OLD target --bind 127.0.0.1 --tcp-port 31210 --udp-port 31211 --dns-port 5314 --ledger …   # 后台
$ OLD target --bind 127.0.0.1 --tcp-port 31220 --udp-port 31211 --dns-port 5315 --ledger …   # 同一个 UDP 端口
old-second exit-status: still running (bind succeeded)
  --- old first  --- e2e target listening on tcp 127.0.0.1:31210, udp 127.0.0.1:31211, …
  --- old second --- e2e target listening on tcp 127.0.0.1:31220, udp 127.0.0.1:31211, …   <- 两个都"listening"

$ OLD target … --udp-port 31231 …        # 后台的残留旧实例
$ NEW target … --udp-port 31231 …        # 修好的二进制
new instance exit=1
e2e target: cannot bind 127.0.0.1:31231: Address already in use
```

即：**改动前是两个实例都活着、内核按哈希切分流量**（审计 §5.2 的 P0 在本机复现），改动后同一个
残留实例（哪怕是旧二进制）会让新实例带着端口名退出 1；启动脚本 `start-targets.sh` 的
`kill -0` 检查因此第一次真正有意义。

### 2.3 `dnsPort ∉ {tcpPort, udpPort}`

`TargetRunner.TryCreate` 的端口检查抽成 `ValidatePorts`（`TryCreate` 触到 `MA0051` 的 60 行上限，
抽出来顺带让"范围 → dns 冲突 → dnsAlt 冲突"的次序成为一段可读代码）：

```
$ NEW target --bind 127.0.0.1 --tcp-port 40010 --udp-port 40010 --dns-port 40010 --ledger …
e2e target: the dns port must differ from the tcp and udp ports: --dns-port 40010 collides with --tcp-port 40010
exit=2

$ NEW target --bind 127.0.0.1 --tcp-port 40010 --udp-port 40011 --dns-port 40011 --ledger …
e2e target: the dns port must differ from the tcp and udp ports: --dns-port 40011 collides with --udp-port 40011
exit=2
```

消息按判据**指名两个端口**（两个数字必然相等，所以区分它们的是选项名）。`tcpPort == udpPort`
仍然合法——campaign 的 `start-targets.sh` 就是这么用的，`TargetOptionsTests.TheSameTcpAndUdpPortIsAccepted`
把这条钉成回归护栏；单测另有两条（撞 tcp / 撞 udp）、一条"dnsAlt 的旧校验仍然生效"、一条范围校验。
`dnsAltPort` 的校验**一字未动**。

### 2.4 `payloadBytes ∈ 0..FrameCodec.MaxPayloadLength`

`FrameCodec.MaxPayloadLength` 由 `private const uint` 提为 `internal const uint`，实际值
**4194304**（`4u * 1024u * 1024u`，即 4 MiB）；`PlanFile` 的数值域表把 `payloadBytes` 的上界从
`int.MaxValue` 换成 `(int)FrameCodec.MaxPayloadLength`：

```
$ NEW client --target 127.0.0.1 --plan payload-over.json --out …      # {"payloadBytes":4194305}
e2e client: arm 'LATPAYLOADOVER' (kind 'latency'): 'payloadBytes' is 4194305, which is outside 0..4194304
exit=2

$ NEW client --target 127.0.0.1 --plan payload-zero.json --out …      # {"payloadBytes":0}
… 1 arm(s) written …
exit=0                                                                 # 0 仍是"未声明"，不是"下界"
```

单测 `ThePayloadBoundIsTheFrameCodecs` 做**边界两侧**（4194304 通过 / 4194305 拒绝，错误文本按字面量
`0..4194304` 断言，避免"常量改了、测试跟着改"），`APayloadOfZeroMeansUndeclared` 钉住 0 的语义。

### 2.5 比对工具：默认 summary 增加"越带读数计数"（D17.4）

- 语义：一条读数**在 `--band` 里记了带宽**并且它的任一统计量越过该带宽（`count` 用
  `allowedCountDelta`），就算越带；**没有带宽记录的读数不算越带**、单独计数
  （`bandless`），因为"漏记 = 带宽 0"会把每一个时钟和内存读数都报成越带。
- 输出：summary 行加 `readingsOutOfBand=<n>/<有带宽的读数路径数>`，紧随其后**新增一行**人类可读的
  `readings out of band: …`；`--strict` 的逐条列表里越带项标 `, out of band`；
  `--json-out` 的 `readings[]` 每项多一个 `out_of_band` 布尔。
- **退出码与判定完全不变**：读数永远不是 failure（`failing` 仍只看 structural/identity/contract/rename）。

**敏感性实验（判据那条"改大一个 `latency/*` 读数"）**：取基线 `run1` 的两份拷贝，只把
`LAT.jsonl` 的 `latency/tcp-rtt/meanUs`（965.051 → 1930.102，带宽 39.323）翻倍：

| 对照 | summary | 退出码 |
|---|---|---|
| `run1` vs 未改动的拷贝 | `readings=0/366 readingsOutOfBand=0/366` | 0 |
| `run1` vs 只翻倍一个读数的拷贝 | `readings=1/366 readingsOutOfBand=**1**/366` | 0 |
| `run1` vs 翻倍 `tcp-rtt` 全部统计量（DD 原实验的形状：`meanUs` + `count`） | `readings=2/366 readingsOutOfBand=**2**/366` | 0 |

（这三行的退出码取自**不带 `--rename-table/--batch`** 的比较；按 §1 的命令跑同一对会因改名表 9 条
pending 而 exit 1，与读数无关——五种组合见 §8.2。）

```
readings out of band: 1 of 366 reading paths with a recorded band moved past it; 0 moved with no recorded band (informational either way: a reading never fails the comparison)
  records/LAT.jsonl::latency/tcp-rtt/meanUs: mean 965.051 -> 1930.1, max |delta| 965.051 (band 39.323, out of band), n 1/1
```

**DD 的原实验也被复现**：`run1` vs `run2`（带宽就是这一对冻结的）读数是 `284/366 moved`、越带
`0/366`——即"宿主本身的抖动"恰好落在带宽内，而把读数放大一倍立刻越带。这正是当初
"默认汇总读数计数不变、只有 `--strict` 可见"那个盲区被补上的地方。

---

## 3. 九次运行的四类计数（含零宽带宽的逐值核对）

### 3.1 主证据：最终二进制在安静宿主上的 4 次运行，契约栏全 0

| 运行 | 结构 | 条件键 | 身份 | 契约 | 已声明 | 改名 | 读数（信息性） | 越带读数 | 退出码 |
|---|---|---|---|---|---|---|---|---|---|
| `F1` | **0** | **0** | **0** | **0** | 27 | **0** | 296/366 | 219/366 | **0** |
| `F2` | **0** | **0** | **0** | **0** | 27 | **0** | 294/366 | 218/366 | **0** |
| `F3` | **0** | **0** | **0** | **0** | 27 | **0** | 293/366 | 219/366 | **0** |
| `F4` | **0** | **0** | **0** | **0** | 27 | **0** | 297/366 | 206/366 | **0** |

- **结构 / 条件键 / 身份 / 契约四栏 4 次全 0**，`--batch B2` 4 次都是
  `9 entries required, 9 satisfied, 0 not observed`（改名观测：`landed 9, pending 0, vanished 0`）。
  `declared=27` 与 B2c 逐条相同（26 个 B2 改名残留 + `run.json planSource`）。
- 四次运行的读数栏是 `206–219/366` 越带、退出码恒 0——读数是信息性的，**不参与判定**（§4 解释它的构成）。

### 3.2 次证据：上一代构建在**宿主争用**（load average ≈3）下的 4 次运行

| 运行 | 结构 | 条件键 | 身份 | 契约 | 已声明 | 改名 | 读数 | 越带读数 | 退出码 |
|---|---|---|---|---|---|---|---|---|---|
| `L1` | **0** | **0** | **0** | **6** | 27 | **0** | 292/366 | 221/366 | 1 |
| `L2` | **0** | **0** | **0** | **9** | 27 | **0** | 299/366 | 224/366 | 1 |
| `L3` | **0** | **0** | **0** | **12** | 27 | **0** | 294/366 | 227/366 | 1 |
| `L4` | **0** | **0** | **0** | **6** | 27 | **0** | 295/366 | 230/366 | 1 |

结构 / 条件键 / 身份三栏仍然全 0，改名表仍然 9/9；**契约栏的 6/9/12/6 条全部落在"零宽带宽"路径上**，
按 D17.2 标注为 **`zero-width band (unmeasured spread)`**：带宽是 run1↔run2 两次恰好取到同一个值冻结出来
的（`baseRange=[0,0] afterRange=[0,0] maxAbsDelta=0`），宿主一争用就会有一个尾沿数据报落在统计窗口外。

### 3.3 零宽带宽的逐值核对：每一条越带键都回到过基线值

| 键（`records/<arm>.jsonl::`） | run1 | F1 | F2 | F3 | F4 | L1 | L2 | L3 | L4 |
|---|---|---|---|---|---|---|---|---|---|
| `LATLOAD metrics/udp.outstandingAtTeardown` | 0 | 0 | 0 | 0 | 0 | 0 | **1** | 0 | 0 |
| `LATLOAD metrics/udp.unmatchedReplies` | 0 | 0 | 0 | 0 | 0 | 0 | **1** | 0 | 0 |
| `LATLOAD metrics/udp.lossRate` | 0 | 0 | 0 | 0 | 0 | 0 | **0.000625** | 0 | 0 |
| `BASE metrics/loss/reordered` | 0 | 0 | 0 | 0 | 0 | **1** | 0 | **1** | **5** |
| `BASE metrics/loss/reorderRate` | 0 | 0 | 0 | 0 | 0 | **0.0004** | 0 | **0.0004** | **0.001999** |
| `LOSS metrics/reordered` | 0 | 0 | 0 | 0 | 0 | 0 | 0 | **1** | 0 |
| `LOSS metrics/reorderRate` | 0 | 0 | 0 | 0 | 0 | 0 | 0 | **0.0005** | 0 |
| `LATLOAD latency/tcp-rtt/count` | 1601 | 1601 | 1601 | 1601 | 1601 | 1601 | 1601 | 1601 | 1601 |

`L4` 的 `reordered=5` 是这一族里最大的一次数值（那一次运行里 `LOSS.reordered` 是 0）；九次运行里没有
哪条键**持续**越带，安静宿主上的 4 次（`F1`–`F4`）一条都没有。这正是 D17 已经裁定的现象：带宽是**零宽**，
不是"这个量稳定"。

### 3.4 零宽契约量的逐值核对（E2-a 判据点名）

| 路径 | run1 | F1 | F2 | F3 | F4 |
|---|---|---|---|---|---|
| `LATLOAD metrics/udp.received` | 1601 | 1601 | 1601 | 1601 | 1601 |
| `LAT metrics/udp.received` | 161 | 161 | 161 | 161 | 161 |
| `LAT metrics/tcp.received` | 322 | 322 | 322 | 322 | 322 |
| `BASE metrics/latency/{tcp,udp}.received` | 101 / 101 | 101 / 101 | 101 / 101 | 101 / 101 | 101 / 101 |
| `ledger udp/received` | 6967 | 6967 | 6967 | 6967 | 6967 |
| 全部 `*/unmatchedReplies`、`*/outstandingAtTeardown` | 0 | 0 | 0 | 0 | 0 |

（`L2` 那一次唯一的例外是 LATLOAD 的 `udp.unmatchedReplies`/`udp.outstandingAtTeardown` 各 1、`udp.lossRate`
0.000625，§3.3 已列。）`inFlight` 不是发布键（D18.5 第 12 条），本轮也不碰在途窗口，故只在后续接缝批次里
以单测覆盖。

---

## 4. `--strict` 读数摘要与"越带读数"的构成

`F1` 的摘要（`--strict`，节选；完整列表 296 行）：

```
== 7. readings (informational observed movement; never a failure) ==
  296 of 366 measured reading paths moved between the runs
  219 of the 366 reading paths with a recorded band moved past it; 0 moved with no recorded band
  every movement, largest first:
    ledger/ledger.jsonl::ticks: mean 1.98561e+13 -> 4.97819e+13, max |delta| 2.99258e+13 (band 9.91671e+10, out of band), n 97/97
    records/run.json::endedTicks: … (band 9.91564e+10, out of band)
    records/LAT.jsonl::latency/tcp-rtt/maxUs: …
summary: structural=0 conditional=0 identity=0 declared=27 contract=0 rename=0 readings=296/366 readingsOutOfBand=219/366 compared=1160 measured=878 classes(contract=512 reading=366 identity=12)
readings out of band: 219 of 366 reading paths with a recorded band moved past it; 0 moved with no recorded band (informational either way: a reading never fails the comparison)
```

`219/366` 的构成（按读数族分类，一次运行内的全部越带项，`F1`）：

| 条数 | 族 | 例子 |
|---|---|---|
| 71 | `latency/*` 直方图统计量 | `latency/udp-rtt/maxUs`、`latency/tcp-rtt/p999Us` |
| 49 | 内存读数 | `privateBytes`、`processes/privateBytes`、`workingSetBytes` |
| 41 | **boot 相对的 `Stopwatch` 时间戳** | `ticks`、`startedTicks`、`endedTicks` |
| 33 | CPU 时间 | `cpuSeconds`、`generatorCpuSeconds` |
| 9 | 速率 | `goodputBps`、`achievedRate` |
| 6 | 毫秒测量 | `metrics/meanConnectMs`、`metrics/udp.windowCeilingMs` |
| 4 | 线程数 | `threads` |
| 3 | 传输量 | `ledger bytes`、`received`、`sources/datagrams` |
| 3 | 墙钟 | `wallSeconds`、`metrics/elapsedSeconds` |

逐类解释（D17.4 要求的"对超出带宽的路径逐条解释"，按族给结论）：

1. **`ticks`/`startedTicks`/`endedTicks`（41 条）**：`Stopwatch.GetTimestamp()` 是**自开机**的计数器，带宽
   是在 run1/run2 那一对（同一 boot）上冻结的；跨 boot 比较时它必然越带，与产品无关。
2. **内存 / CPU / 线程 / 墙钟（49+33+4+3 = 89 条）**：进程级采样读数，随宿主负载与调度漂移；run1 与
   run2 是在**同一台机器不同时段**测的，带宽只覆盖那次测到的漂移。
3. **`latency/*` 直方图（71 条）**：本轮的客户端测量路径**一行未改**（改动只在 Target 侧 socket/CLI 与
   plan 加载表），但本轮 selftest 与 run1 之间隔着宿主争用，RTT 尾沿整体抬高；
   `latency/tcp-rtt/count`、`latency/udp-rtt/count` 这类**样本数**在九次运行里与 run1 逐个相同
   （1601/322/161/101…），说明是分布而非样本集变化。
4. **速率与传输量（9+3 条）**：`goodput`/`achievedRate` 的分母是墙钟，跟着第 2 类走；`ledger bytes`/
   `received` 的**契约值**逐值相同（§3.4），只有被归为读数的聚合量在动。
5. **毫秒测量（6 条）**：`meanConnectMs`/`tcp.meanConnectMs`、`tcp.windowCeilingMs`/`udp.windowCeilingMs`、
   `gates/inFlightCeilingMs`——连接耗时与窗口上限都由实测 RTT 派生，同样跟着第 3 类走。

**登记（本批的观测，不是本批的缺陷）**：这条计数目前对"**同一 boot 的一对运行**"是灵敏的
（`run1` vs `run1` 是 0/366；放大一个读数立刻 1/366），但对"**跨 boot**"的一对会饱和
（`206–219/366`，主要由 boot 相对时间戳与宿主采样读数构成）。要让它在跨 boot 比较里也可用，需要
①带宽按 N 次运行冻结并对零宽路径给 `observedSpread`（D17.3，已登记给 **E5**），或②把"boot 相对"
的读数单独归一类（本批不擅自动配置/契约）。本轮**没有**改判据：这些读数依旧不参与 exit code。

---

## 5. 六条门禁

| # | 命令 | 观测 |
|---|---|---|
| 1 | `benchmarks/WinForward.E2E/scripts/publish.sh` | 退出 0；三份产物重建（linux / win / win-direct），sha256 见 §1 |
| 2 | `dotnet build WinForward.slnx -c Release` | `0 Warning(s) / 0 Error(s)` |
| 3 | `dotnet test tests/WinForward.E2E.Tests -c Release` | `Failed: 0, Passed: 188`（E1 基线 173 + 本批 15） |
| 4 | `scripts/selftest.sh scripts/plans/selftest-plan.json` | 退出 0（§1 的 9 次运行全部 0） |
| 5 | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | 在**冻结源码**上重跑：退出 0，**输出 0 字节**（`/tmp/e2a1/format-final.out`） |
| 6 | `jb inspectcode -f=Xml -e=HINT -o=/tmp/e2a1/jb-inspectcode-final.xml WinForward.slnx` | 在**冻结源码**上重跑的一次：`Report` 下 `<Issues />` 为空：**0 条 `<Issue>`**（`<IssueTypes />` 亦空，无 `CSharpErrors`） |

本批 15 条新用例：`TcpCommandTests` +2（未定义 mode/verdict 抛异常）、`TargetOptionsTests` **新文件 5 条**
（同 tcp/udp 端口合法、dns 撞 tcp、dns 撞 udp、dnsAlt 旧校验、端口范围）、`SocketsTests` **新文件 5 条**
（UDP 不带任何 reuse 选项、TCP listener 保留 `SO_REUSEADDR` 且清掉 `SO_REUSEPORT`、第二个 UDP bind 失败并
指名端点、第二个 TCP listener 失败、源模板）、`PlanFileValidationTests` +3（边界 4194304 通过 /
4194305 拒绝、`0` 未声明）。

门禁 5 首轮报 **1 条** `info MA0185`（`Sockets.cs` 里 `string.Create(InvariantCulture, …)` 的参数全是
字符串，判"可简化"）→ 改成普通插值；顺带删掉因此不再使用的 `using System.Globalization;`。
门禁 2 首轮报 **1 条** `error MA0051`（`TargetRunner.TryCreate` 涨到 71 行 > 60）→ 把端口检查抽成
`ValidatePorts`（这也是"次序可读"的正当拆分，不是为过门禁的糊弄）。门禁 6 首轮即 0 条。
最后一次源码改动是把 `ClearReusePort` 的一句 `<summary>` 改写成不含外部前提（原稿提到"项目级
`SkipLocalsInit`"，而本仓没有这个设置），改完把 **1–6 六条门禁全部重跑**：§1 的 sha256、§3 的 `F1`–`F4`、
§4 的摘要与构成、门禁 1/2/3/5/6 的观测值都取自这次重跑。

---

## 6. 偏离与登记

| 项 | 说明 | 归属 |
|---|---|---|
| `BindUdp` **不设 `SO_REUSEADDR`**（DD 只说了清 `SO_REUSEPORT`） | 实测 Linux 上 `SO_REUSEADDR` 单独就允许第二个数据报 socket 绑同一端口（§2.2.1），只清 `SO_REUSEPORT` 会让 UDP 那一半继续静默；UDP 无 TIME_WAIT，去掉它不牺牲重启能力。TCP 保留 `SO_REUSEADDR` | 本轮（有意，且是本项判据成立的必要条件） |
| bind 失败消息带了端点名（`cannot bind <ep>: …`） | 判据要的是"响亮"，原样只有 OS 的 `Address already in use`，看不出是四个 listener 里的哪一个；异常类型仍是 `SocketException`，退出码仍是 1 | 本轮（有意） |
| `TcpCommand.Name` 改为查表（而不是 `switch`+`throw`） | `rg '_ =>'` 要零命中 + `IDE0066` 会把纯 `switch` 语句判红，两者夹住的唯一形状就是表查询（§2.1） | 本轮（有意，已在代码注释里写明） |
| "越带读数计数"跨 boot 会饱和（`F1`–`F4` 是 206–219/366） | 带宽是同一 boot 上的一对运行冻结的；boot 相对时间戳与宿主采样读数必然越带。计数不影响退出码，且同 boot 的一对仍灵敏（0 → 1） | 观测；改进（N 次运行带宽 / boot 相对读数归类）登记 **E5**（D17.3） |
| 契约栏的零宽带宽越带（宿主争用时 6–12 条/次） | 全部落在 `jitter-band.json` 里 `baseRange=[0,0]` 的键上，同一二进制换次运行即回 0（§3.3）；安静宿主上的 `F1`–`F4` 一次都没有；按 D17.2 标注 `zero-width band (unmeasured spread)` | 已知现象（D17），不阻塞本批 |
| `SourceTemplate` 一并搬进 `Sockets.cs` | 审计 §5.2 的同一项（"#1 的 4 处 socket 构造、#5 的 template 逻辑收进去"），两台 server 逐字重复的 3 行 | 本轮（有意） |
| **F11 不在本批**（5 处 UDP `ConnectAsync` 在 try 之外） | 属于 `Lanes/`/`LaneEngine.OpenAsync` 的机制统一 → **2a-2/2a-3**；本批一个字都没动 `Client/Lanes`（它还不存在） | E2-a 后续小批 |
| `ReuseAddress` 在 Windows 上是"抢占端口"语义 | 本项"响亮第二次 bind"只在 Linux 成立（`SocketsTests` 的 TCP 用例在非 Linux 上直接返回）；靶机只部署在 Linux（`AGENTS.local.md` §7） | 已登记，不修（Windows 需要 `SO_EXCLUSIVEADDRUSE`，超出本批） |
| `reordered` / `reorderRate` 的语义 | 本批只观察到它们在零宽带宽上漂移（0/1/5）；"这个计数器测不测得到重排"是 F10 的账 | E3 |

---

## 7. 给 check 的独立验证点

1. **`rg '_ =>'` 与 `IDE0066` 的夹缝**：确认 `TcpCommand.cs` 零命中，同时
   `dotnet format style WinForward.slnx --diagnostics IDE0066 --severity info --verify-no-changes --no-restore --include benchmarks/WinForward.E2E/Wire/TcpCommand.cs`
   退出 0（把 `Name` 改回 `switch` 表达式或 `switch` 语句，两条里必有一条红）。
2. **UDP 的"响亮 EADDRINUSE"必须用只撞 UDP 端口的那条命令验证**（§2.2.2 第二条）：只撞 TCP 端口会在
   `Bind` 处失败，掩盖"UDP 那半是否真的修好"。再用 `git archive HEAD` 的旧二进制当后台残留，确认新二进制
   仍然响亮（§2.2.3）。
3. **越带读数计数的语义**：`run1` vs `run1` 的拷贝 = `0/366`；只翻倍 `latency/tcp-rtt/meanUs` =
   `1/366` 且 exit 0；再把 `--band` 去掉（band = 0，不测量）时那条**不应**计入（未记带宽 → 归
   `bandless` 计数，不是越带）。这是本批唯一语义新增，值得独立判断口径。
4. **零宽带宽的结论**：`python3` 直接读 `/tmp/e2a1/L{1,2,3,4}/out/*.jsonl`，确认 §3.3 表里每一条越带键
   在另一次运行里回到 run1 的值；重点看 `LATLOAD metrics/udp.unmatchedReplies`（`L2` = 1，其余 = 0）。
   安静宿主上的 `/tmp/e2a1/F{1,2,3,4}` 应当整栏 0——把宿主重新加载到 load average ≈3 再跑几次，
   零宽路径就会开始出现尾沿越带（D17 的"unmeasured spread"，不是回归）。
5. **`payloadBytes` 的边界是 4194304 而不是"某个 1 MiB 之类的数"**：从 `FrameCodec.MaxPayloadLength`
   读出（`4u*1024u*1024u`），并用 §2.4 的命令确认错误文本里的 `<max>` 就是这个值、退出码 2。

---

## 8. check 轮（独立复核）：修复、复现与登记

复核在**冻结源码**上重跑：`scripts/publish.sh` 重建的 `linux/` 与改动前的发布产物**逐文件逐字节相同**
（`diff -r` 无输出），`WinForward.E2E.dll` = `e47560a5…`、`.Contracts.dll` = `544ec36d…`，与 §1 一致。

### 8.1 修复：§3.3 补一行（check 轮补入）

§3.3 的表漏了 `LOSS metrics/reorderRate`——它是 `L3` 四条越带键里的一条（§3.2 的 `contract=12` 正是
**4 键 × 3 个统计量**），值也同源。check 轮从 `--json-out` 的 findings 与 `/tmp/e2a1/L3/out/LOSS.jsonl`
复算后补入：`run1` 0 / `L3` **0.0005** / 其余 7 次运行 0。

### 8.2 口径说明：§2.5 那两行的退出码

`退出码 0` 对应的是**不带 `--rename-table/--batch`** 的比较（读数不参与判定，所以是 0）。若按 §1 的命令
（带 `--batch B2`）跑同一对，`run1` 与自己的拷贝会因改名表有 **9 条 pending** 而 exit 1——这与读数无关，
是改名表的方向性（`run1` 是旧拼写，拷贝也还是旧拼写）。check 轮五种组合都跑了：

| 对照 | 命令 | summary | 退出码 |
|---|---|---|---|
| `run1` vs 未改动的拷贝 | §1 原命令 | `readings=0/366 readingsOutOfBand=0/366 rename=9` | 1 |
| `run1` vs 未改动的拷贝 | 去掉 `--rename-table/--batch` | `readings=0/366 readingsOutOfBand=0/366 rename=0` | **0** |
| `run1` vs 只翻倍 `latency/tcp-rtt/meanUs` | 去掉改名表 | `readings=1/366 readingsOutOfBand=1/366` | **0** |
| `run1` vs 只翻倍 | §1 原命令 | `readings=1/366 readingsOutOfBand=1/366 rename=9` | 1 |
| 翻倍 + 从带宽文件删掉该键（混合情形） | 去掉改名表 | `readings=1/366 readingsOutOfBand=0/365`，`1 moved with no recorded band` | 0 |

即"读数不改变退出码"成立，§2.5 的判据（`0 → 1` 的越带计数）也逐格复现；混合情形还确认了
**没有带宽记录的读数既不算越带、也不算在带内**（分母 365，单独进 `bandless` 计数），
`--strict` 标 `no band` 而不是 `out of band`，`--json-out` 的 `out_of_band` 为 `false`。

### 8.3 独立复现（check 轮自己的一次全新 selftest）

`scripts/selftest.sh scripts/plans/selftest-plan.json` exit 0，产物对照 `run1`：

```
summary: structural=0 conditional=0 identity=0 declared=27 contract=0 rename=0 readings=296/366 readingsOutOfBand=213/366 compared=1160 measured=878
executed batch: B2; 9 entries required, 9 satisfied, 0 not observed
```

四类计数与 §3.1 的 `F1`–`F4` 同形（exit 0）；零宽契约量逐值相同：`ledger udp/received=6967`、
`LATLOAD udp.received=1601`、`LAT udp.received=161`、`LAT tcp.received=322`、
`BASE latency/{tcp,udp}.received=101/101`。`F1`–`F4`/`L1`–`L4` 也**全部重算**：四类计数、
`readingsOutOfBand`、`B2 9/9` 与 §3.1/§3.2 逐格相同；`L1`–`L4` 的 7 条越带键**全部**落在
`jitter-band.json` 的零宽键上（`baseRange=[0,0]`、`afterRange=[0,0]`、`maxAbsDelta=0`），且每条都在
另一次运行里回到 `run1` 的值（§3.3 的 9 列逐格复算）。

端口与边界的命令行复核（发布二进制）：`--dns-port` 撞 `--tcp-port`/`--udp-port` → exit 2 且两名并指；
`--tcp-port == --udp-port` → 正常 listening；`dns-alt-port` 的三条旧校验与范围校验一字未动；
`payloadBytes` 4194305 → exit 2 + `outside 0..4194304`，4194304 通过（并在活靶机上真跑完一条
`payloadBytes=4194304` 的 LAT 臂，exit 0），`0` 仍为"未声明"。

**UDP 响亮失败的矩阵**（发布二进制，只撞 UDP 端口）：

| 后台残留 | 前台 | 结果 |
|---|---|---|
| 新（tcp 32410 / udp 32411） | 新（tcp 32412 / **udp 32411**） | exit 1，`cannot bind 127.0.0.1:32411: Address already in use` |
| 新（tcp 32420 / udp 32421） | 新（**tcp 32420** / udp 32422） | exit 1，`cannot bind 127.0.0.1:32420: …` |
| **旧**（tcp 32430 / udp 32431） | **旧**（tcp 32432 / **udp 32431**） | 两个都 "listening"（负控：改动前静默双绑） |
| **旧**（tcp 32440 / udp 32441） | 新（tcp 32442 / **udp 32441**） | exit 1，`cannot bind 127.0.0.1:32441: …` |

旧二进制出处已独立核对：`/tmp/e2a1/old` 的源码与 `git archive HEAD` 逐文件相同（`diff -r` 排除
`bin`/`obj` 无输出），其 `obj` 产物与 `/tmp/e2a1/old-bin/*.dll` **逐字节相同**。

### 8.4 突变与还原哈希

| 突变 | 观测 | 还原 |
|---|---|---|
| `Sockets.ClearReusePort` 写 1 而不是 0 | `SocketsTests` **4/5 红**（含两条 RAW 读回断言） | `sha256 3e9217cd…` 复原 |
| `BindUdp` 加回 `ReuseAddress=true` | **2/5 红**（UDP 不带 reuse、第二个 UDP bind 失败并指名端点） | 同上 |
| `BindTcpListener` 去掉显式 `ReuseAddress=true` | 5/5 **仍绿**：Linux 上 .NET 的 `Socket.Bind` 对 TCP 已经设了 `SO_REUSEADDR`（探针：fresh `RA=0/RP=0` → Bind 后 `RA=1/RP=0`；Python 的裸 socket 在 bind/listen 后仍是 `0/0`），显式那行在本平台是意图而非机制；用例钉的是**结果**（`RA=1`、`RP=0`、第二个 listener 失败），重启可绑 TIME_WAIT 端口另有探针为证（TIME_WAIT 在 `ss` 可见：无 `SO_REUSEADDR` 的 bind `EADDRINUSE`，有则 OK） | 同上 |
| `TcpCommand.Name` 改回"无 `default` 的 `switch` + 后置 `throw`" | `rg '_ =>'` 仍零命中，但 `IDE0066` → `info`：`dotnet format style … --diagnostics IDE0066 --include benchmarks/WinForward.E2E/Wire/TcpCommand.cs` exit 2、输出 `info IDE0066: Use 'switch' expression`——**门禁 5** 的判红路径（`dotnet build` 自身不跑 IDE 分析器，除非 `EnforceCodeStyleInBuild`，本仓未设）。故表查询是"零兜底 + IDE0066"两个约束夹住的唯一形状 | `sha256 77448b64…` 复原 |

### 8.5 登记（不属于本批，未改）

| 项 | 说明 | 归属 |
|---|---|---|
| README 的 `payloadBytes` 行仍写 `0 or more` | 本批把域收成 `0..4194304`；`--dns-port` 行也没写新的冲突规则（`--dns-alt-port` 行写了自己的） | **E5**（README 契约表由它统一改） |
| README 的 `Non-obvious properties` 没有 target 的 bind 策略（第二个实例响亮失败、Linux 清 `SO_REUSEPORT`、UDP 不设 reuse） | `Sockets.cs` 的 `<remarks>` 是代码侧依据，E5 正是逐条复核那一节 | **E5** |
| `measurement-harness.md` §3.5 的读数行仍写"listed under `--strict`" | D17.4 之后默认 summary 也会**计数**越带读数与非带宽读数 | Phase 3.3 `trellis-update-spec`（E5 的"不在范围内"把 spec 留给该步） |
| `SocketsTests` 的三处非 Linux 早退是静默 `return` | xunit 2.9.3 没有动态 skip；守卫是确定性的（不是 flake 风险），但在 Windows 上 3 条用例会"绿着什么都不断言"。靶机只在 Linux 部署、CI 也不跑这个测试工程，故不阻塞 | 已登记，不修 |
