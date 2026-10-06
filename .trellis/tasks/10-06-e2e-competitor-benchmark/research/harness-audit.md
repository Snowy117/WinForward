# Harness 审查结论

对 `benchmarks/WinForward.E2E/` 与消费它的 `analysis/analyze.py` 做了五路独立审查：配速与统计
完整性、延迟与直方图、UDP/TCP 计量、target 侧与资源采样、跨产品公平性。每条结论性指控都用
`/tmp/wf-bench/val/pass1`（8 行 × 8 臂的真实验证数据）或真实账本复核过；**审查者报错的地方也
记下来了**。

复核用的真实数据：短 plan 单轮，8 行（7 个测量行 + 无产品控制组），全部 `failed=false`。

---

## 一、已修复（3 条）

三条都已用数据证实，代码已改并编译通过（0 警告）。

### 1. REL 臂丢弃已完成的尝试 —— 让「TCP 意外率」跨产品不可比

`ReliabilityArm.cs` 原先把超过 512 个的在途尝试列表里的**已完成项删掉**，而这些永远不会进入
`TallyAttempts`。留下的样本全是慢的、挂起的连接。

**证据（算术上不可能）**：`modeSchedule` 是 `clean,resetAfterN,partialFin,halfClose` 的四元严格
轮转，314 次尝试的 `expected` 必然是 157/79/78，实测 **204/55/55**。

偏差随产品变慢而放大，所以不是恒定偏移，而是让慢产品看起来更容易超时。修法：有界并发
（`SemaphoreSlim(512)`，压力大喊停 pacer 而不是丢样本）+ 完成即收集，并新增
`scheduledAttempts` 让「排了多少 / 统计了多少」恒等式可见。

修后验证（直连，1201 次尝试）：`scheduledAttempts = connectAttempts = 1201`，`expected` 与轮转
预测**逐项吻合**，`observed == expected` 全绿。

### 2. MIX 臂的观察窗口（250 ms）短于它自己公布的丢失门限 W（可达 2000 ms）

W 由臂内观测到的 p99 推导（`W = clamp(5×p99, 200ms, 2000ms)`），却是在臂结束**之后**才算的；
而 socket 在最后一次发送后 250 ms 就关掉了。落在 250 ms 与 W 之间的应答被记成「从未到达」。

**证据（单独打击一个产品）**：

| 行 | 公布的 W | `never` | 报告 lossRate |
| --- | --- | --- | --- |
| **proxybridge** | **399.1 ms** | **40** | **3.33 %** |
| 其余 6 行 | 200 ms（≤250，未受影响） | 0 | 0 |

修法：观察地平线取 W 的上限（2000 ms），确保永远不小于公布的 W；同时不再把「等满整个窗口仍未
到达」的包同时记进 `clientSendLoss` 和 `never`（原先同一批包被记两次，一条算客户端丢、一条算
路径丢）。

### 3. `SequenceBitmap` 对不可信输入没有边界检查 —— 会杀死或挂死客户端

```csharp
var required = (int)((index >> 6) + 1);   // 截断转换
...
_words[word]                            // 却用 long 位移当下标
```

`TrySet` 是拿**从网络读到的 sequence 字段**调用的。改坏一个字节：最高位置 1 → 偏移变负 →
`IndexOutOfRangeException`，而调用链无人捕获 → 进程直接死；中间值 → `capacity *= 2` 溢出成 0 →
**死循环**。一个错包就能让 6 小时的跑批报废。

修法：序列上界 `1 << 18`（最重的臂 500 包/秒 × 120 秒 = 60000，远低于此），越界计入独立的
`OutOfRange` 计数器；数组扩容同样加上界。

---

## 二、跑批前必须修（会让数字失去意义）

### 4. LOSS 臂的在途窗口对丢失的数据报永不释放

`Outstanding = SentOk − _resolved`，而 `_resolved` 只在**到达**时递增。于是丢失的数据报永久占用
一个窗口槽位。一旦累计丢失达到窗口大小（plan 默认 4096），`Outstanding >= window` 永久成立，
**该臂剩下的时间完全停止发送**，只空转配速槽位。

审查者的仿真（500/秒 × 120 秒，窗口 4096）：

| 产品行为 | supplied | **实际 sent** | overflow | 最后一次发送 |
| --- | --- | --- | --- | --- |
| 0 % 丢包 | 60000 | 60000 | 0 | 跑满 |
| 10 % 丢包 | 60000 | **40046** | 19954 | **第 80 秒** |
| 50 % 丢包 | 60000 | **8261** | 51739 | **第 16 秒** |

于是**测量总体由产品自身的失败程度决定**：丢包从第 16 秒才开始的产品，其之后的行为永远采不到；
报告的 `sent`（`lossRate` 的分母、也是 `< 3/n` 里的 n）在不同行之间差 7 倍。

本次验证数据未触发（所有行 `sent = supplied = 12001`、`windowOverflow = 0`，因为无产品丢包）。
但正式 plan 下任何丢包超过 6.8 % 的产品都会触发。

修法：槽位在 `发送时刻 + W` 到期时释放（这才是「在途」的定义），而不是等一个永不到来的应答。

### 5. `Classify` 按 `1..SentOk` 遍历，而不是按实际发送过的序列集合

溢出槽位消耗了序列号却没有发送。于是一部分**从未发出的**数据报被发布为「发出但从未到达」，同时
等量**确实发出的**高序号数据报被完全跳过、不进入任何桶。`never` 会被抬高，`lossRate` 只增不减。

同样未在验证数据中触发（`windowOverflow = 0`），与第 4 条同源。

修法：维护已发送序列集合（或记录最大序列号 + 发送位图），按已发送集合分类。

### 6. LOSS 臂公布的 W 恒为 200 ms 下限，而注释声称它来自 5×p99

`LossArm` 读 `context.Latency.UdpRtt` 的 p99，但 `LatencySet` 是**每臂新建**的，而 LOSS 是唯一
不往 `UdpRtt` 写入的 UDP 臂 → 空直方图的 p99 返回 0 → `clamp(0×5, 200, 2000)` = 200 ms。

**证据**：全部 7 行的 `metrics.window` 精确等于 `200.0`。

对本次数据无害（所有 RTT 为个位数毫秒，200 ms 极为宽松，且 `lossRate` 用的是 `late + never`，
对 W 不敏感），但**注释与分析脚本的说明都是错的**，而且 BASE 臂因为共享了前一个 latency 子臂的
直方图，用的是另一个 W——于是「路径丢失下限」和它要去界定的指标用了不同的门限。

修法：把 W 变成**显式声明**的 plan 参数（默认 200 ms），同一条 W 同时用于观察地平线、在途槽位
释放和分类，并如实发布；不再假装它是自适应推导的。

### 7. LOSS 臂从**实际发送时刻**起算，而不是预定时刻

```csharp
lastSendTicks = Clock.Now;      // 实际
frame.Build(..., lastSendTicks);
```

MIX 臂用的是 `pacer.IntendedTicks(index)`。客户端自身的发送阻塞（UoT 背压等）因此被从每个包的
年龄里减掉，`late`/`never` 被低估 → `lossRate` 被低估。方向是**偏向会让发送方阻塞的产品**，与
harness 自己声明的抗协调遗漏契约相反。

修法：用 `pacer.IntendedTicks(index - 1)` 建帧，实际发送时刻仍按真实时间。

### 8. LAT/LATLOAD 的在途窗口会**删掉**抗协调遗漏样本

`WaitUntil(IntendedTicks(index)); index++; if (InFlight >= window) { WindowOverflow++; continue; }`

被跳过的槽位**消耗掉了调度序号**，所以被推迟的请求不产生任何样本（而不是产生一个大延迟样本），
下一次发送又按自己的新序号打时间戳（≈ 当前时刻）。因此窗口存在的意义——慢产品——恰恰是它测不到
的区间。窗口 64、负载 500/秒时，可测延迟上限只有 128 ms。

本次验证数据 `tcp.windowOverflow = 0`（RTT 远低于上限），未触发。但正式 plan 的 LATLOAD 是
500 rps（验证时是 200），需要复核。

修法：把 plan 的 `window` 放大到覆盖预期尾部（例如 4096，上限变 8.2 秒），并把
`windowOverflow > 0` 变成会让该臂作废的 gate，而不是一个没人读的字段。

### 9. TCP 延迟通道没有 drain（UDP 通道有）

`LatencyArm.cs` 在发送循环结束后**立刻**取消接收循环，UDP 通道却会 drain 最多 1 秒。臂末尾在途
的那一批请求（≈ R×RTT 个，正是最接近截止时刻的样本）被从 `tcp-rtt` 直方图里丢掉。

修法：取消前先有界 drain 到空。

### 10. `reordered` 是死代码，但输出里带着「三分律」上界

`_nextExpected` 只可能越过已经标记到达的序列，所以 `sequence < _nextExpected` 分支不可达。审查者
对 1..6 的全部 1956 种到达顺序做了穷举验证：**重排计数恒为 0**。真正的重排（1,3,2）计数为 0，而
正确实现应为 1。

于是 RFC 4737 要求的重排指标**根本没有被测量**，分析脚本却为这个结构上恒零的格子打印
`< 3/n` 的统计上界——把「测不到」呈现成「测到了零」。

修法：按「已发送的最高序号」比较，或直接删掉该字段与它的上界。

### 11. 另外三个结构性恒零的计数器

`unmatchedReplies` 在 LOSS 臂**没有任何调用点**；`abandonedAtTeardown` 在 LOSS 臂从不使用；
`metrics/corruptRate` 只能看到**回程**损坏（target 对无法解码的包静默丢弃，不回显），所以
去程损坏被测成丢包而非错包——这也让 harness 自带的 `--inject-corrupt-every` 自检失去意义（它
不重算 CRC，target 直接拒收，客户端只会看到 `never`；只有 `--inject-rewrite-every` 会被检出）。

修法：要么接通，要么从输出里删除。绝不为无法移动的计数器打印上界。

### 12. 五个臂硬编码 `clientSendLoss = 0`

`ReliabilityArm`、`ThroughputArm`、`DnsArm`、`MixArm`、`IdleArm` 全部写死 0，而 MIX 的
`metrics.clientSendLoss` 是真实计算的。分析脚本读的是 `metrics` 而不是 `gates`，所以今天这些
还是惰性的；但它们是**永远不可能失败的校验位**，读者会以为校验通过了。

修法：由各臂自己的计数器推导，或删掉该字段。

### 13. 分母为 0 时 `JsonValue.Ratio` 返回 0，于是「没测到」渲染成「满分」

`sent == 0` 时 `lossRate` 发布为 `0`——一个完美的 UDP 精度格子。同理 DNS `answerRate = 0`。
一个 UDP 端口没开的产品会得到 `sent ≈ 1`、`lossRate = 0`。MIX 里 `bytesPerPage` 也把发送侧字节
除以完成页数（两个不同的总体）。

修法：分母为 0 时输出 `null`/`n/a`。

### 14. 资源采样没有进程身份，重启会静默污染 CPU

采样记录里没有 PID 或 `StartTime`。`cpuSeconds` 是**按名字求和**的累计计数器，只要有同名进程退出
就会**下降**；CPU 由「首个样本与末个样本之差」算出，于是一次重启就能让差值变成任意小甚至负数，
且分析脚本只检查 `t1 <= t0`，会把 `-12.34 %` 照常渲染出来。**最不稳定的产品反而得到最低的 CPU。**

另外 `process.Refresh()` 在 `TryReadLong` 的保护之外，而采样循环没有 catch——任何抛出都会让该次
运行**剩下的所有臂**失去采样数据，只在最后 dispose 时才浮出水面。

修法：记录 PID + `StartTime`，逐样本校验身份，计数器非单调即拒收该格子；循环加 catch。

### 15. DNS 的 TCP 响应按 FIFO 匹配，id 校验是同义反复

`DnsArm.Classify(counters, transactionId, message)` 里比较 `responseId != transactionId`，但两个
调用点传进去的都是刚从这条报文里解析出来的 `responseId`——永远相等。UDP 侧靠 pending 字典已经
校验过 id 所以无害；**TCP 侧是从队列头部出队、完全没有 id 校验**，于是错配或重复的响应也会被记成
`answered`。约 2 % 的 TCP 份额限制了影响（≈0.02 pp），但它会掩盖产品自身的 id 处理缺陷。

修法：队列里存 `(id, intended)` 对，出队后按 id 比对。

### 16. BASE 控制组跑的是两倍时长、只覆盖两个臂，且参数不来自 plan

`BASE = latency 子臂 + loss 子臂` 背靠背，所以 `parameters.seconds = 60` 而实际跨度 120 秒。它只
为 LAT 与 LOSS 提供无产品下限，另外 6 个臂没有下限，而分析脚本只取 `metrics.loss.lossRate` 一个
字段、只用在 gate 表里。控制组还总是**第一个**跑，于是唯一能发现「上一轮产品留下污染」的那一行，
永远不跟在产品后面。

修法：BASE 在每轮产品块前后各跑一次；把下限值发布在 LOSS 数字旁边；把控制行移出成对比较。

---

## 三、公平性 —— 这三条会让对比结论直接错掉

### 17. Proxifier 的 UDP 是「泄漏到直连」，却按产品成绩发布 ✅ 已用数据证实

`bench.ppx` 里 `<Udp mode="mode_bypass"/>`。**证据**：Proxifier 的 `proxy-truth.json` 是
`tcp 674, udp 0, utcp 0`——sing-box 一条 UDP 流都没看到——可它的 `LOSS` 是
**12001 发出 / 12001 到达 / 丢包率 0**，`MIX` 1201/1201，`answerRate` 1.000，而且
`dns-rtt.p50 = 894 µs` 是**七行里最快的**（proxyfyre 1041、wf-aot-native 1151、wf-*-utcp
1226/1262、wf-fdd-native 1266、proxybridge 2167）。

它只可能「赢」：UDP 精度 0 % 丢包对比 ProxyBridge 真实的 3.3–4.0 %，远超预设的 0.5 pp 阈值。

根因是**缺少数据报级的转发证据**：`UdpEchoServer` 只把包回显给观察到的源端点，不记录任何东西
（账本里 2337 条 `udpSummary` 只有 `received/bytes/ticks`，没有逐包记录）；TCP 侧反而记了 `peer`。
唯一的泄漏检测器是 sing-box 的流计数，而它 (a) 数的是流不是包，(b) 恰好对唯一全泄漏的产品豁免。

设计文档早就写明了这一点（`2026-10-06-proxifier-comparison-design.md:219,240-241`：「UDP 仍会
**直连**到达 target，回显负载会报告正常的丢包与『成功』」「Proxifier 的 UDP 行必须报告为
**not carried**，而不是 100 % 丢包」），只是没有落实。

### 18. WinForward 的 DNS 走 `localTarget` 直连，绕过了 SOCKS5 ✅ 已证实

四份 WinForward 配置（diff 确认一致）都有 `localTargets[dns] = 192.168.77.4:53` 以及一条位于
客户端规则**之前**的 `udp + remotePort 53 → target dns` 规则。按
`src/WinForward.Configuration/ConfigurationModels.cs:132-136`，`LocalTarget` 是把负载「verbatim」
转发到端点——直连 socket，不经 SOCKS5。所以 DNS 臂 200 q/s 里的 196 走直连，只有 2 % 的 TCP 份额
真的经 sing-box。

这是**你明确要求的**「dns 的 local target 优化」，所以它是有意为之，不是失误。但 ProxiFyre 与
ProxyBridge 的 DNS 是真走 SOCKS5 的，所以 `dns-rtt.p50` 这一栏在混合两条不同的路径，**目前不可比**。
（单轮数据里它没体现成优势：wf-aot-native 1151 µs vs proxifyre 1041 µs——直连 + NDISAPI 重注入
并不比 sing-box 那一跳便宜。）

### 19. CPU 只统计用户态，而五个产品的内核/用户划分差别很大

采样只覆盖进程计数器，没有任何驱动、DPC/ISR 或非分页池。**同一负载下实测**（单 vCPU 百分比）：
proxifier **5.22**、wf-aot-utcp 13.11、wf-aot-native 14.18、wf-fdd-utcp 17.04、wf-fdd-native 19.43、
proxybridge 25.61、proxifyre **25.82**。

约 5 倍的差距，而看不见的那一半正是差异所在：Proxifier 用 WFP 内核回调做重定向，
WinForward/ProxiFyre 把每个包拉进用户态（NDISAPI），ProxyBridge 走 WinDivert 用户态拷贝。

**这是真实的架构差异，必须如实披露，绝不能当成用户态效率排名。** 目前 harness 与报告对内核态
只字未提。

### 20. THRU 臂被自己的目标速率卡住，四行字节级完全相同

全局限速器按 `targetBytesPerSecond` 放行，每流一次只发一个 32 KB 帧（停等）。
**实测**：proxifier、proxifyre 与四个 WinForward 行全部 `bytesSent = 399,996,000` /
`goodputMbps = 159.96`——**是 harness 让它们停下的，不是产品**。只有 proxybridge 真的低于上限
（120,966,400 / 31.26 Mbps）。`budgetReached` 连饱和的行也报 `False`。

它不会偏袒谁，但**上限之上完全没有区分度**：四行之间就算真有 2 倍差距也会被报成「无差异」，
而这一栏的 verdict 家族是 `none`（只有置信区间）。

### 21. 防火墙被关闭，但从未记录

所有行统一关闭三个防火墙配置，只写进 stdout。`environment.json` 里没有该字段，所以 `tables.md`
第一节永远不会说这件事。审查者按拓扑判断**没有任何一个产品需要它**（产品→sing-box 都是环回，
客户端出站由默认放通策略覆盖，泄漏路径也是出站）。影响是对所有行一致、无差别偏差，但它悄悄
移除了一个读者会默认成立的前提。

### 22. 每行只采样一个进程名，而分析脚本只保留「样本最多」的那一个

`--sampler-process` 支持重复，但编排脚本每行只传一个名字。分析脚本随后只分析记录最多的那个名字，
且**平局时按字母序**——而每个 tick 每个名字恰好一条记录，所以平局是常态而非边缘情况。于是：
工作分散在两个进程的产品被少算；同名第二个进程（Proxifier 的服务与 GUI **都是 `Proxifier.exe`**）
会被静默加总，而溯源表只列名字、从不列数量。验证数据里每行都是 `matched=1`、无缺失 tick，
所以没发作，但风险是活的。

---

## 四、只需在报告里如实披露

- **`tcp-connect` 的样本总体排除了连接失败的请求**。挂起或重置连接的产品贡献 0 个样本，慢而成功
  的产品贡献大样本；`connectFailures` 有记录但没有任何 gate 读它。这一栏可能反转连接排序。
- **`tcp-connect` 与 `meanConnectMs` 是两个不同的统计量**，起点不同（前者含 pacer 过冲和 socket
  创建，后者不含），差值是几十到几百微秒——对亚毫秒的连接来说超过 5 %。**绝不能并列引用。**
- **MIX 的 `dns-rtt` 是用 `Socket.Available` + `Task.Delay(1)` 轮询测的**，会加上最多一个定时器
  tick（Windows 上可达 15.6 ms）到一个亚毫秒的 DNS RTT 上。它不是 verdict 指标，但会出现在延迟表里。
- **直方图在 17.18 秒处饱和**，更长的挂起与 17.18 秒无法区分。
- **账本没有归属信息、也没有任何代码读它**：记录里没有 arm/row/product，也没有绝对时间戳；
  UDP/DNS 是 1 Hz 聚合、没有逐包身份；target 的 `undecodable`（去程损坏的唯一目击者）在分析里
  完全没出现。设计文档要的 `tcp.fidelityDiscrepancyRate` 因此算不出来。
- **分析脚本声明的 5 秒预热只作用于内存，没有作用于 CPU**；而 CPU 又是取「首个非 IDLE 样本到末个」
  的首尾差，其分母**包含臂与臂之间的空隙时间**，但分子也包含那段空隙里烧掉的 CPU——所以
  「已排除 IDLE」这句披露在最关键的地方是不成立的。
- **内存泄漏斜率的拟合序列是按固定臂序而非实际运行序拼接的**，臂序被打乱时折线会来回跳，
  斜率与「是否泄漏」的结论随之失去意义。

---

## 五、审查者说错的地方（我复核后否掉的）

- **「LAT lanes 竞态会让 `sentOk > supplied`」**：plan 里没写 `lanes`，实测 `lanes = 1`，
  逐行 `supplied == sentOk`（401/401、4001/4001），没有触发。代码确实是真 bug（多 lane 时
  共享计数器非原子、`Supplied` 被最后一个 lane 覆盖），但本次数据不受影响。
- **「LAT/LATLOAD 窗口截断会删掉样本」**：机制成立，但实测 `tcp.windowOverflow = 0`、
  `udp.windowOverflow = 0`（proxybridge 的 UDP 是 977，需要单独看），本次未触发。
- **「LOSS 的 W 自适应规则会让某个产品得到更宽松的门限」**：不会。空直方图让**所有**产品都是
  200 ms 的同一个下限，是恒定值而非逐产品宽松。问题在注释撒谎，不在偏袒。

---

## 六、需要你定的四件事

1. **DNS local target**：WinForward 保留它（当作特性展示，但需要为公平对比补一个不带该优化的
   配置，或者把 `dns.*` 从并列比较里拿出来单独标注）／删掉它（与其他人同路径）／做一个独立的
   标注行？
2. **Proxifier 的 UDP**：按设计文档标注为 `not carried` 并排除出 UDP 与 DNS 的成对比较
   （最省事、也是设计文档原本的要求）／还是先实现数据报级归属（target 记录每个包的源端点、
   客户端记录自己的本地端点，泄漏的流会显示陌生源端点）再下结论？
3. **THRU 的上限**：抬到最快产品之上（需要先知道它有多快）／改成自适应爬坡直到 goodput 不再上升／
   保持现状但如实声明「上限之上无区分度，四行并列不代表等速」？
4. **修复范围**：第二节 13 条全部修完再跑／只修会让结论错的那几条（第 4、6、7、8、13、14 条 +
   公平性三条）／还是先停下来重新规划？

我的建议是第 4 条选「修会让结论错的那些 + 公平性三条」，其余如实披露。第二节里纯粹是
「输出里带着测不到的零」的那几条（第 10、11、12 条）改动很小，顺手一起修掉也不亏。
