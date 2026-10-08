# T2：源端点普查槽位按区间回收（2026-10-08）

本轮修 **T2**（`Target/SourceCensus.cs` 的槽位终身不回收，长 campaign 累计超过容量后普查永久失明）。
ticket 状态见 [`../tickets.md`](../tickets.md)，定位与修法方向见
[`T1T2-label-and-census.md`](T1T2-label-and-census.md) §7，语义条目见
[`../semantic-fixes/index.jsonl`](../semantic-fixes/index.jsonl)。

所有实验都在本机（Linux）重放，不需要 Windows VM。改动落在 **3 个入库文件 + 1 个新测试文件**：

| 文件 | 改动 |
|---|---|
| `benchmarks/WinForward.E2E/Target/SourceCensus.cs` | 槽位加 `_claim`（认领世代）与 `_lastSeen`（本区间是否被数据报碰过）；`Harvest` 读完后推进世代；认领路径加"先作废、全栅栏、再发布"的握手 |
| `tests/WinForward.E2E.Tests/SourceCensusTests.cs` | **新增** 4 条事实：续存端点按区间增量发布、跨区间回收后新端点入表、满表按区间报 overflow、摘要器与接收循环并发时不丢不重 |
| `.trellis/spec/backend/measurement-harness.md` | 新增 §3.12：普查的容量模型、取舍、边界代价与握手（**显式选择**写进 spec）+ §6 的测试条目 |
| `benchmarks/WinForward.E2E/README.md` | `udpSummary` 的记录表条目补上"每循环 64 个活跃端点、空闲即回收、overflow 是本区间没能入表的包数" |

没有碰 `src/`、没有碰 `verification/` 的冻结物、没有 commit。复现脚本在 `/tmp/wf-t2/`（不入库）。

---

## 0. 结论

| 项 | 结论 | 判据 |
|---|---|---|
| **T2** | **fixed** | 微型复现（`--udp-receivers 1`，80 端口 × 40 轮 × 3 波）：修前第 2、3 波**一个端口都进不去**（`sources` 恒 0、overflow = 全部）；修后第 2 波 64/80 入表（容量与第 1 波同为 64，归属略有换手：2368/3200 + 832 overflow），第 3 波 40/40 入表且 overflow 0 |
| **记录键集与语义** | 不变 | `sources[]` 仍是"本区间的增量"（续存端点跨区间仍按差值发布，单测第一条钉住）、`sourceOverflow` 仍是"本区间没能入表的包数"（单测第三条钉住）；`udpReceivers` 等字段未动；冻结 oracle 51 切片 0 差异 |
| **真实规模** | fixed | 8 接收循环 + 60 个持续端点 + 400 个"历史"端点 + 100 个探测端点（10 波 × 10）：修前**每一波 0/10**、`sources` 恒 60、overflow 7565；修后**每一波 10/10**、每区间 `sources` = 60 持续 + 10 探测 = 70、overflow 0（除历史突发那一秒），整轮 overflow 2771 |
| **边界代价** | 已量化并披露 | 回收会重置被顶掉槽位的计数：真实规模 32040 个数据报里 `received = censused + overflow + 1`（差 1 个）；微型复现差 0。远在分析器的 ±band（1% 或该臂一秒的速率）以内 |
| **AC18 判据 4** | T2 这一半已清 | 分析器对该轮产物 rc=0；`sourceOverflow` 的披露仍可用（注入 3 条非零 overflow ⇒ `ledger-source-overflow` 照常报出） |

---

## 1. 机制与修法

### 1.1 修前（T1 证据 §7 的定位）

每个接收循环一张 64 槽的表，键 `(address, port)`，槽位一次认领**终身不回收**；`Harvest` 每秒把
"自上次以来的增量"发成 `sources[]`。容量按整个进程生命期消耗、产出却按秒清账：一个**持续**发包的
源端点会在**每张**表里各占一格（8 循环 ⇒ 有效容量 = 512/8 = **64 个端点整个 campaign 累计**）。
E5-b 的账本恰好记下 64 个源端点，`10:45:58` 起 42 分钟 `sources` 恒 0、`received` 全进 overflow。

### 1.2 修后：世代（epoch）+ 认领（claim）

世代（epoch）由**摘要器**在每次 `Harvest` 末尾推进；槽位上有两个新字段都记世代：

| 字段 | 谁写 | 含义 |
|---|---|---|
| `_claim` | 接收循环（认领时） | 该槽位**本次认领**发生在哪个世代；0 = 未被认领/正在被重新认领。摘要器用它判断"这是不是同一个 claim"，不是同一个就把计数基线归零 |
| `_lastSeen` | 接收循环（每次命中） | 上一次有数据报碰它的世代。接收循环用 `_lastSeen != 当前世代` 判断槽位空闲 |

```
Record(remote):
    epoch = Volatile.Read(_epoch)                 // 每个数据报一次读
    扫槽位：
      命中 (port, high, low)  -> _datagrams++; _lastSeen = epoch; return
      第一个 _lastSeen != epoch 的槽位记成 free
    free < 0  -> _unplaced++                        // 本区间 64 个活跃端点都占着
    认领 free：_claim = 0; 全栅栏; 写 high/low/port/_datagrams=1/_lastSeen=epoch; _claim = epoch

Harvest(totals):
    每个槽位：claim = Volatile.Read(_claim); 0 就跳过（未认领或正在换手）
              读 high/low/port、Interlocked.Read(_datagrams)，再读一次 _claim，
              两次不同就跳过（下一个区间整份发布新 claim 的计数）
              _publishedClaim[i] != claim -> 基线归零（新 claim 拥有它的全部计数）
              delta = datagrams - _published[i]；delta > 0 就发布并推进基线
    Volatile.Write(_epoch, _epoch + 1)              // 区间在这里结束
```

**语义为什么没变**：一个**持续**发包的端点在区间里先到达就命中它自己的槽位（`_lastSeen` 被刷新），
认领不变、计数基线也不变 ⇒ 它仍然按"自上次以来的差值"发布，跨区间续存。空闲判定是"**本区间**还没有
数据报碰过这个槽"——区间一开，所有槽都是这个状态——所以一个**新**端点若抢在持续端点本区间的第一个数据报
之前到达，可以把它的槽拿走（check 复核补记，2026-10-08）：那时持续端点的认领与计数从头开始，但它
**本区间**的数据报仍然整份发布在它自己的键下（`sources[]` 按端点聚合、不按槽位），代价只是旧计数里
"摘要器读完之后"的那几个包。产出的键集合、`sources[]` 的每区间增量语义、`sourceOverflow` 的
"本区间没能入表的包数"语义都没动，`udpReceivers` 等字段更没动。

**容量算术的变化**：从"整个进程生命期累计 64 个端点/表"变成"每个区间 64 个活跃端点/表"。单个区间内
超过 64 个活跃端点的突发仍然照旧进 overflow（第 3 节第 1 波的数字修前修后逐项相同），但一次性的、
已经沉默的端点不再永久占格。**取舍**写进了 spec §3.12：不加大表（只是推迟失明、且让每包扫描变贵），
也不保留"终身制"开关（记录语义没有变，不需要新的契约面）。

---

## 2. 并发与内存序论证

**一张表只有一个写者。** `UdpEchoServer` 给每个接收循环一张自己的 `SourceCensus`，而
`ReceiveLoopAsync` 在 `Record` 之前 `await` 下一次接收，所以同一个表的 `Record` 永不重叠；接收循环
读到的槽位值永远是自己写的。摘要器（`WriteSummaryAsync`，1 Hz + 收尾一次）**只读**。

**认领的发布/作废握手**（发布-获取语义，与修前"端口最后发布"同族）：

1. 摘要器先读 `_claim`；为 0 表示"未认领，或正在换手"，跳过。
2. 认领时接收循环**先把 `_claim` 写成 0**（release），跨一次 `Interlocked.MemoryBarrier()`，再写
   high/low/port/计数/`_lastSeen`，**最后把 `_claim` 写成新世代**（release）。栅栏保证"新身份"不可能
   在"作废"之前被观察到（弱内存序架构也成立；x86-TSO 本来就不重排存储）。
3. 摘要器读身份与计数**之前和之后各读一次 `_claim`**：两次不同 ⇒ 这次读撞上了换手，整个槽位留到下一个
   区间发布。因此**不可能**把"上一个 claim 的地址"和"这一个 claim 的计数"配成一行发出去。
4. 若 `_claim` 与上次发布的相同，`delta = 计数 - 基线`；不同则基线归零（新 claim 拥有它的全部计数）。
   同一世代值不会在同一槽位上出现两次：认领会把 `_lastSeen` 设成该世代，而接收循环在同一世代内只把
   `_lastSeen != 世代` 的槽位当空闲。

**数据路径代价**：每个数据报多一次 `_epoch` 的 volatile 读（每区间一个槽位再加一次 `_lastSeen`
普通写）；没有分配、没有委托、没有 LINQ、没有加锁，扫描仍是一次线性数组遍历，命中仍是一次比较。

**边界代价（有界、已披露）**：已沉默的槽位被重新认领时旧计数被覆盖，而"摘要器读完这个槽位之后、
世代推进之前"落在旧世代里的那几个数据报会随覆盖消失。窗口只有一次 64 槽扫描里该槽位的那一次读，
量级是"每个换手事件 ≤ 少量包"；真实规模实测 32040 个数据报里差 1 个（第 3.2 节），远小于分析器
判决用的 ±band。替代方案（让摘要器等接收侧确认）要在数据路径上引入等待或第二遍扫描，收益只是把这个
有界窗口再缩小一点，因此不做。

---

## 3. 本机 before/after

两份二进制由同一棵树、只差 `SourceCensus.cs` 一处：

```bash
$ git stash push -- benchmarks/WinForward.E2E/Target/SourceCensus.cs
$ dotnet publish benchmarks/WinForward.E2E/WinForward.E2E.csproj -c Release -r linux-x64 --self-contained false -o /tmp/wf-t2/before
$ git stash pop
$ dotnet publish benchmarks/WinForward.E2E/WinForward.E2E.csproj -c Release -r linux-x64 --self-contained false -o /tmp/wf-t2/after
$ sha256sum /tmp/wf-t2/before/WinForward.E2E.dll /tmp/wf-t2/after/WinForward.E2E.dll
998baf3904ca7b38cc1e35adba4242581cc2e244f33fb1b972d688b32ca0ed8d  /tmp/wf-t2/before/WinForward.E2E.dll
2d7cd768df175e2d4a40fd61b537e5ef8ddd071ea2ddcdcdeef6c78c0145373e  /tmp/wf-t2/after/WinForward.E2E.dll
```

### 3.1 微型复现（`/tmp/wf-t2/micro_repro.py`）

一个靶机、`--udp-receivers 1`、三波**全新**源端口，每波 40 轮、轮间隔 0.05 s，波间静默 2 s；每波结束后
读账本自己的 `udpSummary`，按端口归属到该波。

```bash
$ python3 /tmp/wf-t2/micro_repro.py /tmp/wf-t2/before/WinForward.E2E /tmp/wf-t2/out/micro-before
```
```text
micro reproduction: --udp-receivers 1, 40 round(s) per wave, 0.05s apart, waves [80, 80, 40]
wave 1: 80 fresh port(s) (60260..46105), 40 round(s) = 3200 datagram(s)
   ports of this wave the census listed: 64 / 80; datagrams attributed to them: 2560 / 3200 => 640 had to overflow
   busiest single interval listed 64 of this wave's ports
wave 2: 80 fresh port(s) (34489..55743), 40 round(s) = 3200 datagram(s)
   ports of this wave the census listed: 0 / 80; datagrams attributed to them: 0 / 3200 => 3200 had to overflow
   busiest single interval listed 0 of this wave's ports
wave 3: 40 fresh port(s) (37709..38206), 40 round(s) = 1600 datagram(s)
   ports of this wave the census listed: 0 / 40; datagrams attributed to them: 0 / 1600 => 1600 had to overflow
   busiest single interval listed 0 of this wave's ports
whole run: 8000 datagram(s) sent from 200 port(s); the target received 8000, the census listed 64 distinct port(s) and attributed 2560 datagram(s), sourceOverflow 5440 (received = censused + overflow: True)
after the final summary: 64 distinct port(s) ever listed, 2560 datagram(s) attributed, sourceOverflow 5440
ledger: /tmp/wf-t2/out/micro-before/ledger.jsonl
```

```bash
$ python3 /tmp/wf-t2/micro_repro.py /tmp/wf-t2/after/WinForward.E2E /tmp/wf-t2/out/micro-after
```
```text
micro reproduction: --udp-receivers 1, 40 round(s) per wave, 0.05s apart, waves [80, 80, 40]
wave 1: 80 fresh port(s) (44804..52436), 40 round(s) = 3200 datagram(s)
   ports of this wave the census listed: 64 / 80; datagrams attributed to them: 2560 / 3200 => 640 had to overflow
   busiest single interval listed 64 of this wave's ports
wave 2: 80 fresh port(s) (44318..44455), 40 round(s) = 3200 datagram(s)
   ports of this wave the census listed: 64 / 80; datagrams attributed to them: 2368 / 3200 => 832 had to overflow
   busiest single interval listed 64 of this wave's ports
wave 3: 40 fresh port(s) (50800..35951), 40 round(s) = 1600 datagram(s)
   ports of this wave the census listed: 40 / 40; datagrams attributed to them: 1600 / 1600 => 0 had to overflow
   busiest single interval listed 40 of this wave's ports
whole run: 8000 datagram(s) sent from 200 port(s); the target received 8000, the census listed 168 distinct port(s) and attributed 6720 datagram(s), sourceOverflow 1280 (received = censused + overflow: True)
after the final summary: 168 distinct port(s) ever listed, 6720 datagram(s) attributed, sourceOverflow 1280
ledger: /tmp/wf-t2/out/micro-after/ledger.jsonl
```

**修后每区间的容量行为与 overflow 归属**（同一份账本按区间拆开）：

```text
== micro-before (19 interval(s))                      == micro-after (19 interval(s))
   [ 1] received+  960  sources= 64  overflow=  192      [ 1] received+  960  sources= 64  overflow=  192
   [ 2] received+ 1520  sources= 64  overflow=  304      [ 2] received+ 1520  sources= 64  overflow=  304
   [ 3] received+  720  sources= 64  overflow=  144      [ 3] received+  720  sources= 64  overflow=  144
   [ 7] received+ 1440  sources=  0  overflow= 1440      [ 7] received+ 1440  sources= 64  overflow=  288
   [ 8] received+ 1520  sources=  0  overflow= 1520      [ 8] received+ 1520  sources= 64  overflow=  304
   [ 9] received+  240  sources=  0  overflow=  240      [ 9] received+  240  sources= 64  overflow=   48
   [12] received+  200  sources=  0  overflow=  200      [12] received+  200  sources= 40  overflow=    0
   [13] received+  760  sources=  0  overflow=  760      [13] received+  760  sources= 40  overflow=    0
   [14] received+  640  sources=  0  overflow=  640      [14] received+  640  sources= 40  overflow=    0
```

- **第 1 波（80 端口同区间涌入）修前修后逐项相同**：容量仍然是 64，第 65..80 个端口这一区间进不来，
  它们的 640 个数据报如实进 `sourceOverflow`（192+304+144 = 640 = 3200 − 2560）。修法不改变单区间的容量。
- **第 2 波**：修前 `sources` 恒 0（表格已满，42 分钟式的永久失明从这里开始）；修后同样的 64 格被回收后
  交给新一波，overflow 仍只属于"这一区间没抢到格子的那 16 个端口"（288+304+48 = 640）。
- **第 3 波（40 端口 < 容量）**：修后 40/40 入表、overflow **0**；修前 0/40。
- 整轮 `received = censused + overflow`（8000 = 6720 + 1280）在修后仍然成立，一个数据报都没被静默吞掉。

### 3.2 真实规模复现（`/tmp/wf-t2/scale_repro.py`）

形状照 T1 证据的手法自造（脚本是新写的）：8 个接收循环；60 个**持续**端点每 0.1 s 发一轮贯穿整轮；
400 个"历史轮次"端点各发 8 个数据报后沉默（模拟 campaign 早先那些 run 的 socket）；随后 100 个探测端点
分 10 波、每波 10 个全新端口各发 25 轮。每波按账本 `udpSummary` 记录归属。

```bash
$ python3 /tmp/wf-t2/scale_repro.py /tmp/wf-t2/before/WinForward.E2E /tmp/wf-t2/out/scale-before
```
```text
campaign shape: --udp-receivers 8, 60 continuous endpoint(s) every 0.1s, 400 one-shot endpoint(s), 100 probe endpoint(s) in 10 wave(s) of 10
history: 400 one-shot endpoint(s) sent 8 datagram(s) each; the census has now listed 60 distinct port(s)
history: 400 one-shot endpoint(s) sent 8 datagram(s) each; the census has now listed 60 distinct port(s)
wave 1: 10 fresh probe port(s) (35353..60639), 25 round(s)
   the census listed 0 / 10 of them; sourceOverflow published while the wave ran: 379
wave 2: 10 fresh probe port(s) (35242..55992), 25 round(s)
   the census listed 0 / 10 of them; sourceOverflow published while the wave ran: 430
wave 3: 10 fresh probe port(s) (36020..59269), 25 round(s)
   the census listed 0 / 10 of them; sourceOverflow published while the wave ran: 433
wave 4: 10 fresh probe port(s) (33753..58768), 25 round(s)
   the census listed 0 / 10 of them; sourceOverflow published while the wave ran: 513
wave 5: 10 fresh probe port(s) (40713..59214), 25 round(s)
   the census listed 0 / 10 of them; sourceOverflow published while the wave ran: 420
wave 6: 10 fresh probe port(s) (37263..57617), 25 round(s)
   the census listed 0 / 10 of them; sourceOverflow published while the wave ran: 446
wave 7: 10 fresh probe port(s) (36693..58615), 25 round(s)
   the census listed 0 / 10 of them; sourceOverflow published while the wave ran: 471
wave 8: 10 fresh probe port(s) (35298..60637), 25 round(s)
   the census listed 0 / 10 of them; sourceOverflow published while the wave ran: 428
wave 9: 10 fresh probe port(s) (40061..54611), 25 round(s)
   the census listed 0 / 10 of them; sourceOverflow published while the wave ran: 432
wave 10: 10 fresh probe port(s) (35012..59420), 25 round(s)
   the census listed 0 / 10 of them; sourceOverflow published while the wave ran: 418
whole run: 100 probe endpoint(s) in 10 wave(s), 0 of them ever listed by the census
   60 / 60 continuous endpoint(s) still listed after the history saturated the tables
   distinct port(s) ever listed: 129; sourceOverflow over the whole run: 7519
after the final summary: 129 distinct port(s) ever listed, sourceOverflow 7565
ledger: /tmp/wf-t2/out/scale-before/ledger.jsonl
```

```bash
$ python3 /tmp/wf-t2/scale_repro.py /tmp/wf-t2/after/WinForward.E2E /tmp/wf-t2/out/scale-after
```
```text
campaign shape: --udp-receivers 8, 60 continuous endpoint(s) every 0.1s, 400 one-shot endpoint(s), 100 probe endpoint(s) in 10 wave(s) of 10
history: 400 one-shot endpoint(s) sent 8 datagram(s) each; the census has now listed 60 distinct port(s)
history: 400 one-shot endpoint(s) sent 8 datagram(s) each; the census has now listed 60 distinct port(s)
wave 1: 10 fresh probe port(s) (33336..55574), 25 round(s)
   the census listed 10 / 10 of them; sourceOverflow published while the wave ran: 0
wave 2: 10 fresh probe port(s) (34377..58836), 25 round(s)
   the census listed 10 / 10 of them; sourceOverflow published while the wave ran: 0
wave 3: 10 fresh probe port(s) (33083..57908), 25 round(s)
   the census listed 10 / 10 of them; sourceOverflow published while the wave ran: 0
wave 4: 10 fresh probe port(s) (37402..58443), 25 round(s)
   the census listed 10 / 10 of them; sourceOverflow published while the wave ran: 0
wave 5: 10 fresh probe port(s) (33375..54025), 25 round(s)
   the census listed 10 / 10 of them; sourceOverflow published while the wave ran: 0
wave 6: 10 fresh probe port(s) (34041..60721), 25 round(s)
   the census listed 10 / 10 of them; sourceOverflow published while the wave ran: 0
wave 7: 10 fresh probe port(s) (34146..57945), 25 round(s)
   the census listed 10 / 10 of them; sourceOverflow published while the wave ran: 0
wave 8: 10 fresh probe port(s) (35981..58954), 25 round(s)
   the census listed 10 / 10 of them; sourceOverflow published while the wave ran: 0
wave 9: 10 fresh probe port(s) (36837..53783), 25 round(s)
   the census listed 10 / 10 of them; sourceOverflow published while the wave ran: 0
wave 10: 10 fresh probe port(s) (39014..59786), 25 round(s)
   the census listed 10 / 10 of them; sourceOverflow published while the wave ran: 0
whole run: 100 probe endpoint(s) in 10 wave(s), 100 of them ever listed by the census
   60 / 60 continuous endpoint(s) still listed after the history saturated the tables
   distinct port(s) ever listed: 559; sourceOverflow over the whole run: 2771
after the final summary: 559 distinct port(s) ever listed, sourceOverflow 2771
ledger: /tmp/wf-t2/out/scale-after/ledger.jsonl
```

账本自己的时间线（每行一个 `udpSummary` 区间；`received+` 是该区间收到数，`censused` 是该区间发布到
`sources[]` 的数据报数，`overflow` 是同一区间的 `sourceOverflow`）：

```text
== scale-before（47 个区间）                            == scale-after（47 个区间）
   [ 1] received+ 420  sources= 60 censused= 360 ovf=   0    [ 1] received+ 420  sources= 60 censused= 387 ovf=   0
   [ 2] received+1247  sources=129 censused= 672 ovf= 624    [ 2] received+1250  sources=213 censused= 771 ovf= 496
   [ 3] received+3093  sources= 86 censused= 579 ovf=2525    [ 3] received+3090  sources=460 censused= 830 ovf=2275
   [ 4] received+ 600  sources= 60 censused= 550 ovf=  50    [ 4] received+ 600  sources= 60 censused= 600 ovf=   0
   [ 5] received+ 610  sources= 60 censused= 557 ovf=  53    [ 5] received+ 610  sources= 70 censused= 610 ovf=   0
   …                                                          …
   [13] received+ 550  sources= 60 censused= 505 ovf=  45    [13] received+ 550  sources= 70 censused= 550 ovf=   0
   …                                                          …
   [45] received+ 600  sources= 60 censused= 554 ovf=  46    [45] received+ 600  sources= 60 censused= 600 ovf=   0
identity: received=32040 censused=24475 overflow=7565         identity: received=32040 censused=29268 overflow=2771
          censused+overflow=32040 exact=True                           censused+overflow=32039 exact=False
```

- 修前：历史突发把每张表填满后，`sources` 永远只剩那 60 个"早到"的持续端点，之后**每一个**探测端点
  都进不来（10 波全部 0/10），整轮 7519 个数据报进 overflow——正是 E5-b 的形状。
- 修后：历史端点沉默一个区间后让位；每一波 10 个探测端点全部入表，每个区间 `sources` = 60 持续 +
  10 探测 = **70**、overflow **0**；100 个探测端点**全部**被普查看到；整轮见到的不同端口从 129 → 559。
- 残留的 2771 个 overflow 全部有归属：历史突发那一秒（496 + 2275，超过每循环 64 个活跃端点的部分）
  加各波换手瞬间的少量（本区间没抢到格子的那几个数据报）。
- 边界代价实测：32040 = 29268 + 2771 + **1**，即 32040 个数据报里有 1 个在换手窗口里丢了，属于第 2 节
  的有界边界代价（微型复现那一轮差 0）。

---

## 4. 单测

`tests/WinForward.E2E.Tests/SourceCensusTests.cs`（新文件，4 条事实）：

1. `AContinuingEndpointKeepsItsSlotAndIsPublishedAsTheIntervalsOwnDelta`：64 个持续端点第一区间各 1 个
   数据报（发布 64×1），第二区间各 3 个（发布 64×**3**，不是累计的 4）——"每区间增量"语义跨区间成立。
2. `ASlotNoDatagramTouchedForAnIntervalIsReclaimedForAFreshEndpoint`：第一波 64 个沉默后，第二波 64 个
   **全新**端口全部入表、overflow 0、键与第一波不相交；再来一波 40 个（< 容量）也全部入表。
3. `AFullTableReportsTheIntervalsUnplacedDatagramsAsOverflow`：65 个同时活跃 ⇒ 64 入表、第 65 个的 5 个
   数据报进 overflow；下一区间 64 个仍在发 ⇒ 第 65 个的 3 个数据报**只**报 3（不是累计 8）。
4. `EveryDatagramIsPublishedExactlyOnceBeneathASummariserRunningWithTheReceiveLoop`：写侧（模拟接收循环）
   在测试线程记录 200000 个数据报、读侧（摘要器）在另一个线程不停 `Harvest`；每个端口累计发布数
   **恰好**等于记录数、总数相等、`HarvestUnplaced() == 0`——并发下不丢不重。

```bash
$ dotnet test tests/WinForward.E2E.Tests/WinForward.E2E.Tests.csproj -c Release --filter "FullyQualifiedName~SourceCensusTests"
Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4, Duration: 119 ms - WinForward.E2E.Tests.dll (net10.0)
```

**变异检查**（把 `SourceCensus.cs` 换回修前版本再跑同一组事实）：

```bash
$ git stash push -- benchmarks/WinForward.E2E/Target/SourceCensus.cs
$ dotnet test tests/WinForward.E2E.Tests/WinForward.E2E.Tests.csproj -c Release --filter "FullyQualifiedName~SourceCensusTests"
  Failed WinForward.E2E.Tests.SourceCensusTests.ASlotNoDatagramTouchedForAnIntervalIsReclaimedForAFreshEndpoint
  Error Message:
   Assert.Equal() Failure: Values differ
Expected: 64
Actual:   0
Failed!  - Failed:     1, Passed:     3, Skipped:     0, Total:     4
```

回收那条事实在修前是**红**的（"回收后新端点仍能入表"正是被钉住的行为）；其余三条是修前也成立的
不变量事实，用来防止修法把"每区间增量/overflow 归属/并发不丢不重"改坏。

---

## 5. 回归

### 5.1 shipped selftest

```bash
$ bash benchmarks/WinForward.E2E/scripts/selftest.sh scripts/plans/selftest-plan.json
selftest rc=0
```
```text
error records: 0 []
ledger records: 258 udpSummary: 97 labels: ['selftest']
total sourceOverflow published: 0
distinct source ports listed: 7
```

（修后的 target 二进制就是被 selftest 用的那份：`/tmp/wf-bench/pub/linux/WinForward.E2E.dll` 的 sha256 =
`2d7cd768…`，与 §3 的 after 一致。）

### 5.2 分析器对该轮产物

```bash
$ bash benchmarks/WinForward.E2E.Analysis/scripts/analyze.sh --raw /tmp/wf-bench/selftest/out --flat \
      --ledger /tmp/wf-bench/selftest/ledger.jsonl --out /tmp/wf-t2/selftest-analysis
analyze rc=0
findings_by_severity: {"correctness-failure": 0, "path-interference": 0, "harness-error": 0,
                       "measurement-caveat": 1, "informational": 1}
  informational      dns-arm-port flat/out DNS
  measurement-caveat control-block-missing flat
```

（两条都是 flat 单 run 树固有的，与 T1 记录的形状一致。）

### 5.3 `sourceOverflow` 的披露仍可用

把该轮账本里 3 条 `udpSummary` 的 `sourceOverflow` 注入成 21（只动这一个字段）再分析：

```bash
$ bash …/analyze.sh --raw /tmp/wf-bench/selftest/out --flat \
      --ledger /tmp/wf-t2/overflow-disclosure/ledger.jsonl --out /tmp/wf-t2/overflow-disclosure/analysis
analyze rc=0
findings_by_severity: {"correctness-failure": 0, "path-interference": 0, "harness-error": 0,
                       "measurement-caveat": 2, "informational": 1}
  measurement-caveat ledger-source-overflow | udpSummary.sourceOverflow=63: the endpoint census table was
  full, so the endpoint list for this window is incomplete
```

### 5.4 冻结 oracle（分析器行为不得变）

```bash
$ python3 benchmarks/WinForward.E2E/scripts/oracle-diff.py --batch 1a,1b,1c,2,3,4,5
compared 51 slice(s): 0 differ(ent), 0 missing
differences: 0 structure, 0 value, 0 missing
rc=0: every slice of this batch is equal
```

---

## 6. 语义与"显式选择"

`udpSummary` 的键集合、`sources[]` 的每区间增量语义、`sourceOverflow` 的每区间未入表计数都没有变，所以
**没有**加新的 CLI 开关或记录字段；改变的是表背后的容量模型，而它是被当成契约写下来的，不是悄悄改的：

- `.trellis/spec/backend/measurement-harness.md` **§3.12 "The UDP source census is a per-interval table
  (T2, 2026-10-08)"**：记录语义不变、容量模型（每循环 64 个**活跃**端点）、取舍（不加大表、不留终身制
  开关）、有界边界代价（实测 1/32040）、以及握手的内存序论证。
- `.trellis/spec/backend/measurement-harness.md` §6 增加 `SourceCensusTests` 的测试条目。
- `benchmarks/WinForward.E2E/README.md` 的 `udpSummary` 记录表条目：把"每循环 64 个活跃端点、空闲即回收、
  `sourceOverflow` 是本区间没能入表的包数"写给读记录的人。
- `SourceCensus` 的类型注释里保留了同样的契约与内存序论证（这份代码的锁-自由握手一直是这样记的）。

**明确不改的**：`--udp-receivers`、`--label`、`--ledger` 等 CLI 面；记录键集；分析器侧对
`sources`/`sourceOverflow` 的读取与 finding 文本。

---

## 7. 门禁

| # | 门禁 | 结果 |
|---|---|---|
| 1 | `dotnet build WinForward.slnx -c Release` | **通过**：`0 Warning(s), 0 Error(s)` |
| 2 | `dotnet test WinForward.slnx -c Release -m:1` | 见 §7.1 |
| 3 | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | **通过**：exit 0，输出 **0 字节** |
| 4 | `jb inspectcode -f=Xml -e=HINT`（先清 `~/.local/share/JetBrains/` 与 `/tmp/JB`） | 见 §7.2 |
| 5 | `python3 benchmarks/WinForward.E2E/scripts/effective-lines.py benchmarks/WinForward.E2E benchmarks/WinForward.E2E.Contracts benchmarks/WinForward.E2E.Analysis tests/WinForward.E2E.Tests` | **通过**：exit 0，无输出 |
| 6 | `python3 benchmarks/WinForward.E2E/scripts/oracle-diff.py --batch 1a,1b,1c,2,3,4,5` | **通过**：51 切片、0 差异、rc=0 |
| 7 | `python3 benchmarks/WinForward.E2E/scripts/check-readme-contract.py` | **通过**：111 key(s) / 401 constant path(s): ok |

### 7.1 全量测试

最终树（inspectcode 那条修复之后）重跑：

```bash
$ dotnet test WinForward.slnx -c Release -m:1
… 14 个项目，逐项目串行 …
Passed!  - Failed:     0, Passed:    18, Skipped:     0, Total:    18 - WinForward.Analyzers.Tests.dll
Passed!  - Failed:     0, Passed:   121, Skipped:     0, Total:   121 - WinForward.Configuration.Tests.dll
Passed!  - Failed:     0, Passed:    63, Skipped:     0, Total:    63 - WinForward.Core.Tests.dll
Passed!  - Failed:     0, Passed:   361, Skipped:     0, Total:   361 - WinForward.E2E.Tests.dll
Passed!  - Failed:     0, Passed:    24, Skipped:     0, Total:    24 - WinForward.Integration.Tests.dll
Passed!  - Failed:     0, Passed:    74, Skipped:     0, Total:    74 - WinForward.NdisApi.Tests.dll
Passed!  - Failed:     0, Passed:   137, Skipped:     0, Total:   137 - WinForward.Performance.Tests.dll
Passed!  - Failed:     0, Passed:    72, Skipped:     0, Total:    72 - WinForward.Protocols.Tests.dll
Passed!  - Failed:     0, Passed:   120, Skipped:     0, Total:   120 - WinForward.Runtime.Capture.Tests.dll
Passed!  - Failed:     0, Passed:   183, Skipped:     0, Total:   183 - WinForward.Runtime.Flow.Tests.dll
Passed!  - Failed:     0, Passed:   108, Skipped:     0, Total:   108 - WinForward.Runtime.Socks5.Tests.dll
Passed!  - Failed:     0, Passed:   157, Skipped:     0, Total:   157 - WinForward.Runtime.TcpRedirect.Tests.dll
Passed!  - Failed:     0, Passed:   164, Skipped:     0, Total:   164 - WinForward.Runtime.UdpProxy.Tests.dll
Passed!  - Failed:     0, Passed:    58, Skipped:     0, Total:    58 - WinForward.Windows.Tests.dll
$ echo $?
0
```

14 个项目 / **1660 passed / 0 failed / 0 skipped**（T1 轮的 1656 + 本轮 4 条新事实），一次跑绿，没有
`WinForward.E2E.Tests` 的分配门噪声失败。`WinForward.E2E.Tests` 从 357 → 361。

### 7.2 inspectcode

第一次清缓存全量跑抓出 **1 条** `<Issue>`，是本轮新测试自己招来的：

```text
<Issue TypeId="AccessToDisposedClosure" File="tests\WinForward.E2E.Tests\SourceCensusTests.cs"
       Offset="4005-4009" Line="104" Message="Captured variable is disposed in the outer scope" />
```

并发事实里 `Task.Run` 的闭包捕获了 `using var stop`（`CancellationTokenSource`）。按仓库的抑制政策优先改代码：
闭包改捕获 `var token = stop.Token;`（`CancellationToken` 是结构体、且在源被 dispose 之后读
`IsCancellationRequested` 仍然安全），dispose 与捕获就此分开，不再需要 `#pragma`。随后**清缓存重跑全量**：

```bash
$ rm -rf ~/.local/share/JetBrains/ /tmp/JB
$ jb inspectcode -f=Xml -e=HINT -o=/tmp/wf-t2/inspectcode2.xml WinForward.slnx
Inspection report was written to /tmp/wf-t2/inspectcode2.xml
exit=0
$ python3 - <<'PY'   # 解析 XML，不信 exit code
issues: 0 issue types: 0
IssueTypes self-closed empty: True
Issues self-closed empty: True
PY
```

`<IssueTypes />` 与 `<Issues />` 都是空的 ⇒ **0 条 `<Issue>`**，也没有 `CSharpErrors`。改完后
format（空输出）与 oracle-diff（51 切片 0 差异）都重跑过，计数不变。

---

## 8. 偏离、未做与给 check 的点

1. **脚本不入库**：两个复现脚本（`/tmp/wf-t2/micro_repro.py`、`/tmp/wf-t2/scale_repro.py`）只在 `/tmp`，
   与本轮证据里的原始输出一一对应；证据正文给了完整命令与输出。
2. **before 二进制靠 `git stash` 取**（§3）：同一棵树、只差这一个文件，两个 dll 的 sha256 不同；
   "修前"行为（第 2/3 波 0 入表、探测端点 0/10）本身就是它确实是旧代码的旁证。
3. **边界代价是"有界"而不是"零"**（第 2 节）：真实规模实测 1/32040。如果 check 要求零丢失，需要改设计
   （摘要器等接收侧确认或双缓冲交接），会引入等待或第二遍扫描；本轮按"披露 + 量级远小于分析器 band"处理。
4. **没有跑 Windows campaign**：T2 是靶机（Linux）侧的问题，本机复现即闭环；下一轮 campaign 会用修好的
   target 二进制，届时 `ledger-source-overflow` 应当只在真正超过"每区间 64 个活跃端点/循环"时出现。
5. **`sourceOverflow` 的高频出现不再是"普查已死"的信号**：分析器的 finding 文本（"the endpoint census
   table was full, so the endpoint list for this window is incomplete"）仍然准确，但它现在描述的是**本区间**
   的容量事件，而不是"此后永远失明"。这条已经写进 spec §3.12；check 若要复核，看微型复现第 1 波与第 3 波
   的对照即可（同为修后，一波 640 overflow、一波 0）。
