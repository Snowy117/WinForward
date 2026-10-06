# Wire 与 Target 代码质量审查

审查范围：`benchmarks/WinForward.E2E/Wire/`（7 个文件，708 行）与 `benchmarks/WinForward.E2E/Target/`（6 个文件，1454 行），共 **2125 行**；并核对了它们的实际消费方（`Client/` 各 arm、`TargetRunner` 的调用者 `Program.cs`、`analysis/analyze.py`、`README.md` 的协议规格表）。共 13 个文件逐行读过。

基线：`dotnet build benchmarks/WinForward.E2E/WinForward.E2E.csproj -c Release` → **0 Warning / 0 Error**（根 `Directory.Build.props` 是 `TreatWarningsAsErrors` + 4 个分析器包 + AOT/trim analyzer，所以"能编译"本身是有约束的）。

本文**不重复** `harness-audit.md` 的语义正确性问题（账本缺少归属信息、UDP 无逐包记录、靶机静默丢弃无法解码的包、计分口径、公平性）。本文只谈代码质量：协议实现、CRC/Filler、DnsWire、流式读取、三台 server 的结构与重复、账本写入、编排、体量、命名、注释、魔数、死代码、异常与释放、可测试性。凡涉及"这些语义问题背后的代码结构成因"，本文只补充结构性的那一半。

所有结论都在当前 workspace 上核对过，引用格式为 `文件:行号`。另有三处只能靠实测判断的行为，用独立的 `/tmp` 探针程序验证（不是仓库代码），结论写进正文对应小节。

**范围决定（写作时已知）**：`benchmarks/WinForward.E2E` 将做一次彻底重构，**不保留任何向后兼容**（这个 harness 从未完整运行过）。因此 §11 中"账本字段名/顺序"（原第 7 条）与"无锁发布"（原第 12 条）两处限制的理由不再是"`analyze.py` 消费"，而是"靶机与客户端是两个独立部署的二进制 + 热路径"；线格式相关的第 1–6 条与 AOT/trim 的第 11 条完全不变。详见 §11 末尾的《§11 的适用性说明（范围决定后）》。

---

## 概览

**审查对象**：`Wire/` 7 个文件 + `Target/` 6 个文件，共 2125 行。

**审查方式**：逐行读代码 + 读它们的**实际消费方**（client 各 arm、`analyze.py`、README 的规格表），再对三处"只能靠推理"的行为写探针实测。

**基线**：`dotnet build benchmarks/WinForward.E2E/WinForward.E2E.csproj -c Release` → 0 Warning / 0 Error（1.6 s）。

**一句话结论**：`Wire/` 的"单一 schema"目标**基本达成**（client 与 target 共用同一批编解码类，没有各写一份解析器），真正的重复发生在**校验策略、trailer 规则、错误语义**这三层；`Target/` 三台 server 有 8 处可提取的重复，其中一处（`ReuseAddress`）藏着一个实测可复现的运维级陷阱。

**按严重度排序的发现**：

| 级别 | 发现 | 位置 | 证据强度 |
|---|---|---|---|
| **P0** | `ReuseAddress = true` 在 Linux 上同时打开 SO_REUSEPORT，残留的旧 target 实例与新实例**都能 bind 成功**，流量只喂给其中一个（TCP 实测 40/0，UDP 实测 38/2） | `TcpTargetServer.cs:69`、`UdpEchoServer.cs:42`、`DnsServer.cs:42,46` | 探针实测 |
| **P0** | `TcpCommand.Name` 的兜底分支 `_ => "error"` 会在枚举扩容时写出**重复 JSON key**，两个不同 verdict 的计数静默合并；`TcpTargetServer.cs:50-53` 那段专门防这件事的注释因此被绕过 | `TcpCommand.cs:49-59` | 探针实测 `{"error":1,"error":2}` |
| **P1** | `LedgerWriter.WriteAsync` 的 catch 把 **body 委托**也包住了：记录体里的编程错误 = 记录静默消失，只留下一个计数器 | `LedgerWriter.cs:41-63` | 代码 + 同文件注释自证 |
| **P1** | `FrameStreamReader` 在 EOF 丢弃半帧且不报告，上层把"截断的帧"读成"对端干净 half-close"，target 还会真的写 trailer | `FrameStreamReader.cs:45-77` → `TcpTargetServer.cs:263-265,315-318` | 代码路径可枚举 |
| **P1** | `FrameCodec.MaxPayloadLength` 只在**解码侧**校验且是 `private`，编码侧（plan → `FrameBuffer`）无人查 → 超限 plan 静默变成"产品把每个包都丢了" | `FrameCodec.cs:39,87` vs `PlanFile.cs:270` | 代码 |
| **P1** | 端口校验只查 `dnsAlt`，不查 `dnsPort` 与 `tcpPort`/`udpPort` 冲突；配合上面 SO_REUSEPORT，UDP 端口撞车会**双双 bind 成功** | `TargetRunner.cs:142-161` | 探针实测 |
| **P2** | 停机时在途连接必然记成 `TcpVerdict.Error`，与真错误无法区分；4 种异常压成同一个 verdict 且不记原因 | `TcpTargetServer.cs:104,234-249`、`DnsServer.cs:309-317` | 代码 |
| **P2** | `FrameStreamReader` 出错后不消费字节（BadMagic/BadLength）→ 同一错误无限返回；4 个调用点的 `continue` 安全性依赖这条未写下的规则 | `FrameStreamReader.cs:52,60` vs `LatencyArm.cs:569-571` 等 | 代码 |
| **P2** | 协议层**零单元测试**，`internal` + 无 `InternalsVisibleTo`，`FrameStreamReader(Socket)` 无法脱离网络 | 全仓 | 已确认 |

---

## 1. 帧协议实现

### 1.1 做对了的地方（先说清楚，避免误伤）

- 编解码**只有一份**：`FrameCodec.cs:47-127`，client（`Client/FrameBuffer.cs:13-42`）和 target（`TcpTargetServer.cs:339-340`）都走它。没有"两边各写一份解析逻辑"。
- 表驱动的偏移量：`FrameCodec.cs:42-45` 的 5 个 `Offset*` 常量，写（`WriteHeader`，53-60）读（`TryReadHeader`，86-96）共用，和 `README.md:412-420` 的规格表逐字对应。
- 字节序统一大端（`BinaryPrimitives.WriteUInt32/64BigEndian` 全线一致），只有 `Crc32C.cs:22,30` 用小端——那是 x86 `crc32` 指令的操作数语义，**正确**，但需要一行注释（见 1.5）。
- `TcpCommand.TryParse`（24-37）用 `payload.Length != PayloadLength` 精确匹配，没有"至少 5 字节"这种典型错误；`payload[0] > (byte)TcpMode.Stall` 做了枚举上界检查。

### 1.2 编解码的不对称清单

| # | 位置 | 症状 | 建议 |
|---|---|---|---|
| A | `FrameCodec.cs:44,58`（写） vs `TryReadHeader:86-96`（读） | `clientSendTicks` 写在 offset 16，**从来没有被读过**；`FrameHeader`（7-23）里也没有这个字段。每帧白填 8 字节，读者无法判断它是有意义的还是历史残留 | 加注释声明"为线格式保留、不读"（跨主机 Stopwatch 不可比，所以真的不该读）；**不要删**——删了就动线格式 |
| B | `WriteHeader:53-60` / `FinishFrame:62-67` | 用裸 Span 切片，空间不足抛 `ArgumentOutOfRangeException`；`DnsWire.BuildQuery:52-55` 返回 `-1`；`TcpCommand.Write:18-22` 又直接索引。**三套约定** | 在 `FrameCodec` 上统一为"调用方保证空间"并在 class 注释里写明；或加 `Debug.Assert` |
| C | `FrameCodec.cs:39` | `MaxPayloadLength` 是 `private`，只在 `TryReadHeader:87` 校验 | 提成 `internal` 并在 `PlanFile` 的 arm 校验里用（见 7.3） |
| D | `FrameCodec.cs:109` | `frame.Length < header.FrameLength` → **允许尾部多余字节**。对 UDP，一个"合法帧 + 追加垃圾"的数据报会被两端都接受，`Filler.Matches` 也只校验 payload | 这是"检出损毁"能力的缺口。改 `!=` 会改变报告结果，所以属于**需决策项**，先写一条固化现状的测试 |
| E | `FrameCodec.cs:40`（`CommandSequence`）与 `TrailerProtocol.cs:21`（`SequenceBase`） | 两个"序列号命名空间"常量分居两个文件；没有任何地方校验"数据帧序列 < SequenceBase" | 移到 `TcpCommand`/`TrailerProtocol` 或新建 `SequenceSpace`。零风险 |

### 1.3 真正的重复：回复校验阶梯（client 侧三份）

同一套"UDP 回复合法性阶梯"被抄了三遍：

- `LossArm.cs:223-260`（内联写全）
- `MixArm.cs:722-760`（抽了 `BookUndecodable`，698-707）
- `LatencyArm.cs:790-805`（少了 `WasSent` 一步）

三份都是：`TryDecode` → 失败时按 `BadChecksum && TryReadHeader` 决定 `MarkCorruptWithKnownSequence` 还是 `MarkCorrupt` → 校验 `ConnectionId` → `Filler.Matches` → `WasSent` → 记到达。**"CRC 坏但头能读 → 还能归因到某个序列号"这个精细判断被写了两次、漏了一次**（`LatencyArm` 直接 `_corrupt`）。

> 任务：新增 `Wire/ReplyFrame.cs`，`internal enum ReplyFault { None, Corrupt, CorruptKnownSequence, ForeignConnection, Unmatched }`，`internal static ReplyFault Classify(ReadOnlySpan<byte> datagram, uint expectedConnectionId, UdpReliabilityTracker tracker, out FrameHeader header, out ReadOnlySpan<byte> payload)`。三个 arm 各减 15-20 行，行为可以逐分支对齐（`LatencyArm` 的差异要显式决定：是否补上 `WasSent`）。

### 1.4 trailer 规则只共享了常量，没有共享规则

`TrailerProtocol.cs:3-8` 的注释说：

> "Both ends must read the shape from here: a trailer the target writes and the client does not expect is indistinguishable from a product that failed half-close."

实际共享的只有 4 个数字（`PayloadBytes`/`FrameCount`/`TotalBytes`/`SequenceBase`，12-21 行）。**规则本身写在两个地方**：写侧 `TcpTargetServer.cs:329-344`（3 帧、Filler 填充、`Stopwatch.GetTimestamp()` 当 sendTicks），读侧 `ReliabilityArm.cs:676-684`（`Echoed < expectedBytes ? 回显 : trailer`）+ `725`（`trailerBytes >= TrailerProtocol.TotalBytes`）。

客户端那个"超过 expected 的字节一律算 trailer"的启发式是**按字节数分类**，而 target 是按**序列号**（`SequenceBase + index`）盖戳的——`TrailerProtocol` 里现成的判别依据没被用上（README:452-457 也是这么描述的，所以现状是"文档化的启发式"）。

> 任务：`TrailerProtocol` 增加 `internal static bool IsTrailerSequence(ulong sequence) => sequence >= SequenceBase;`，客户端改用它分类（**行为变化**：可能改变 `halfCloseViolation` 的判定，排在后面做）。

### 1.5 大端里的小端需要注释

`Crc32C.cs:22,30` 的 `ReadUInt64LittleEndian` / `ReadUInt32LittleEndian` 与全仓大端形成视觉冲突。加一行 `// the crc32 instruction consumes the operand low-byte-first; this is not a wire endianness choice` 就够了——这类"看起来像 bug 的正确代码"每被审一次就浪费一次人时。

---

## 2. CRC32C / Filler

### 2.1 CRC32C：正确，可测性为零

- **正确性**：反射多项式 `0x82F63B78`、init/final `0xFFFFFFFF`（`Crc32C.cs:8-10`）——CRC-32/ISCSI 标准参数。三段降级（X64 SSE4.2 → SSE4.2 → 256 项表，18-38）都对。关键点是 SSE 路径保持**未 finalize 的 crc 链**、最后一次才 `^ FinalXor`（40），这是最容易写错的地方，写对了。
- **性能**：`Sse42.X64` 单路串行，`crc32` 指令延迟约 3 周期 → 理论吞吐受限于延迟而非端口。三路并行（crc32 + pclmulqdq 组合）能再快 2-3×。但**这是测量工具**：先确认 target 的 CPU 是不是瓶颈再动（见 §11）。ARM64 上走表（没用 `System.Runtime.Intrinsics.Arm.Crc32`），对"Linux x64 靶机"的现状无影响。
- **可测试性**：`internal static`，本仓没有测试工程、没有 `InternalsVisibleTo` → 现在**完全不可测**。这是唯一能挡住"硬件路径打错、只在有 SSE4.2 的机器上表现"的防线。

> 任务（半天）：新建 `tests/WinForward.E2E.Tests`，在 `WinForward.E2E.csproj` 加 `<InternalsVisibleTo Include="WinForward.E2E.Tests" />`（仓库惯例：`src/WinForward.NdiApi/NdisApiAbi.cs:6-11`、`src/WinForward.Core/WinForward.Core.csproj:8-9`），然后测：
> - 黄金向量：`"123456789"` → `0xE3069283`，空串 → `0x00000000`；
> - 表路径与硬件路径一致（给 `Crc32C` 加一个内部 `ComputeTableOnly`，或加 `[MethodImpl(NoInlining)]` 的测试钩子）——**不允许**用 `DOTNET_EnableHWIntrinsic=0` 之外的全局开关。

### 2.2 Filler：线格式锁死，能改的只有注释

- `Seed`（`Filler.cs:33-37`）把 64 位 sequence **截断**成 32 位：`(connectionId * Knuth) ^ (uint)sequence`。两个只在高 32 位不同的序列产生**完全相同的填充**。今天不可达（客户端序列从 1 递增），但必须写进注释——这是"看着像 bug 的正确代码"第二例。
- `ZeroSeedSubstitute`（第 6 行）让 `(conn,seq)→seed` 不再单射，理论上存在两对 `(conn,seq)` 生成同一 payload（xorshift32 是 2³²-1 长单环，只有位移 0 才能整段匹配，所以实际不可达）。CRC 还覆盖了头部，影响可忽略。
- **性能**：`Fill`（8-16）每个**字节**做一次 xorshift，32 KiB 帧 = 32768 次迭代，`Matches`（18-31）同理。这是 throughput/MIX 臂的热路径，而且是线格式的一部分。
  - **允许的优化**：把 4 次 xorshift 展开成一批（输出逐字节相同，因为取的是 `(byte)state` 即低字节），能省掉循环开销。
  - **禁止的优化**：换 PRNG、改种子公式、改 `ZeroSeedSubstitute`——老 client + 新 target 是现实场景（两个独立二进制）。
- **可测试性**：同样零测试。应是**第一优先级**的测试对象，因为它是跨版本兼容的隐式契约：

> 任务：`Fill(conn,seq,span)` 与 `Matches(conn,seq,span)` 对称；固定 `(connectionId: 7, sequence: 3)` 的前 16 字节黄金向量；`sequence = 0x1_0000_0003` 与 `0x0000_0003` 输出相同（把上面那条截断事实**固化成测试**，防止有人"顺手修"）。

**可测试性小结**：`Crc32C` 与 `Filler` 都是纯静态、无 IO、无时钟依赖——最容易测也最该测，现在是 0 覆盖。

---

## 3. DnsWire

边界检查整体是到位的：`BuildQuery:74,93` 有缓冲余量检查，`TryParseQuery:118,139` 的两处界检查都在**索引之前**（label 循环不会越界读），`MaxLabelLength = 63`（46）有名字。

发现（按优先级）：

1. **缺域名总长校验**（`BuildQuery:50-104`）。只查单个 label ≤63 和目标缓冲余量，不查 RFC 1035 的 255 字节总长，也不查报文是否 ≤512（UDP 无 EDNS 时）。今天生成的是 `q{n}.bench.local` 所以没事，但这是个静默陷阱。
   > 任务：`BuildQuery` 增加 `if (offset - HeaderSize > 255) return -1;` 与总长断言。
2. **尾点 FQDN 被拒**（`BuildQuery:66-77`）：`"a.b."` 在 `index == name.Length` 时算出一个空 label → `labelLength is 0` → `-1`。这个循环结构（`for index <= name.Length`）本身是对的，但"拒绝尾点"是个没写下来的决定。
   > 任务：要么支持尾点，要么加一行注释说明"名字必须是未限定形式"。
3. **QNAME 压缩指针被原样回显**（`TryParseQuery:130-134` + `BuildResponse:179`）。查询里若 QNAME 是 `C0 0C`，响应会把这两个字节**原样拷回去**，而响应里 offset 12 正好是它自己 → **自引用指针**；同时 Answer 的 `C0 0C`（187-188）也指向那个环。对 harness 自产流量不可达，但手写 parser 应当二选一：拒绝（`(labelLength & 0xC0) != 0 → offset += 2` 改成 `return false`），或重写成完整名字。
4. **非 A/AAAA 类型静默降级**（`155-160,194-204` 的 `_ => 0`）。`DnsWire.cs:29-32` 定义了 `TypeCname/TypeTxt/TypeHttps`，`BuildResponse` 一个都不处理 → `rdataLength = 0`、`answerCount = 0`、NOERROR 空答案。README:461-464 写明了，客户端 `DnsArm.cs:191-196` 也专门记了 `_emptyAnswers`，**所以这不是 bug**；但新增类型（比如再加一种）会静默走同一条路，而 `QueryTypeFor`（`DnsArm.cs:175-185`）和 `BuildResponse` 在两个文件里各维护一份类型知识。
   > 任务：把 `_ => 0` 展开成 `TypeCname or TypeTxt or TypeHttps => 0` 并加注释"这些类型故意只回 NOERROR/0 answers，客户端按 emptyAnswers 记账"（纯可读性，线格式不变）。
5. **`DnsQueryInfo.QuestionEnd` 是解析游标混进了语义结构**（`DnsWire.cs:9-23,148`），而 `BuildResponse(query, info, destination, …)`（152）同时收 span 和 info 却**无法验证二者匹配**——`query.Slice(HeaderSize, questionLength)`（179）在不匹配时直接抛。`DnsServer.cs:129-138` 的 `BuildAnswer` 就是专门用来把两者粘起来的一层壳。
   > 任务：签名收敛为 `BuildResponse(ReadOnlySpan<byte> query, Span<byte> destination, out int answerCount)`，内部自己 `TryParseQuery`。同时删掉 `DnsQueryInfo.QuestionEnd` 的公开性（`TryParseQuery` 改成 `out int questionEnd` 或返回 `(info, end)`）。**收益**：删一层 + 消一处不变量。
6. **回答字节是裸魔数**（`196-199` 的 `10,0,0,1`；`203` 的 `answer[12 + rdataLength - 1] = 1`）。同一个类里 TTL 有名字（`AnswerTtlSeconds:48`），A/AAAA 的答案却靠索引。至少加注释或命名常量。另外 `answer.Slice(12, rdataLength).Clear()`（193）先清后写在 A 分支里是冗余的（4 个字节全被覆盖）。
7. `TryParseResponse`（209-230）不校验 question 段/opcode，只校验 QR bit —— 对 harness 自产流量足够，记一笔即可。

---

## 4. FrameStreamReader

`ReadAsync` 41 行（37-77），缓冲区管理在 `FillAsync`（79-112）。这是本次审查里**问题密度最高**的文件。

**4.1 环境态：属性在出错后保留上一帧的值。**
`Raw`/`Payload`/`Header`（31-35）是"读一帧之后可读"的伴生状态，但成功路径（64-68）才赋值，失败路径（52,61）不清理。所以调用方**必须先看 status 再读属性**。今天 6 个调用点都记得（`PersistentArm.cs:434-449` 是最规范的），`TcpTargetServer.cs:187` 甚至在第一帧就没成功时把 `default` 的 `ConnectionId = 0` 写进了账本。这是"靠纪律"而不是"靠类型"。

> 任务：`internal readonly struct ReadResult { FrameReadStatus Status; FrameHeader Header; ReadOnlyMemory<byte> Raw; ReadOnlyMemory<byte> Payload; }`，`ReadAsync` 返回它。语义等价（`Raw`/`Payload` 仍是内部缓冲切片），但误读在编译期就没了。

**4.2 缓冲切片会在下一次读时被就地改写。**
`Raw = new ReadOnlyMemory<byte>(_buffer, _start, frameLength)`（65-66），而 `FillAsync:86-96` 用 `Buffer.BlockCopy` **就地压缩** `_buffer`（不是 `Array.Resize` 换数组）。也就是说持有 `Raw`/`Payload` 跨过一次 `ReadAsync` 会读到被搬移后的数据。今天所有调用点都在下一次读之前用完（`TcpTargetServer.cs:284` 的 echo 是 await 的，`PersistentArm.cs:452` 同帧内）。**没有 generation token、没有 debug 断言、没有注释。**

> 任务：class 文档写明"切片只在下次 `ReadAsync` 前有效"，并加 `#if DEBUG` 的 generation 计数断言。

**4.3 错误语义不对称且没写下来。**
`BadChecksum` **消费整帧**（60）以便重新同步；`BadMagic`/`BadLength` **不消费**（52）→ 同一个 reader 会永远返回同一个错误。三处 `continue`（`LatencyArm.cs:569-571`、`ThroughputArm.cs:268-272`、`ReliabilityArm.cs:666-668`）的安全性完全依赖这条规则。一旦有人为了"从帧头重新同步"而让 BadChecksum 也不消费，`ReliabilityArm.cs:658` 的 `while(true)`（不看 token）就会变成 100% CPU 热循环。

> 任务：**两件事**——(a) class 注释写清"BadMagic/BadLength 之后 reader 不可用，调用方必须终止连接；BadChecksum 已跳过该帧，可继续"；(b) 考虑把 `FrameReadStatus` 拆成 `Frame | EndOfStream | Recoverable(BadChecksum) | Fatal(...)`，让类型强制这件事。

**4.4 EOF 处的半帧被静默丢弃（最实质的一个）。**
`available` 只有 1..27 字节时，`FillAsync` 返回 false → `EndOfStream`（72-75），那几字节永久留在 `_buffer` 里，**没有任何计数器或属性暴露**。后果链：`TcpTargetServer.cs:263-265` 把 `EndOfStream` 交给 `CompleteOnEndOfStreamAsync` → `halfClose` 模式（315-318）会**真的写 trailer** 并判 `HalfClose` 干净。也就是说"一个把帧截断的产品"会被记成 half-close 通过。

> 任务：`FillAsync` 返回 false 且 `_end != _start` 时返回新增的 `FrameReadStatus.Truncated`；两个 server 各自记账（TCP 计入 `protocolErrors` / 新 verdict，UDP 不涉及）。这是补一个缺失状态，不是改语义决策。

**4.5 缓冲区只增不减。**
`new byte[Math.Max(capacity, DefaultCapacity)]`（28）→ 每连接**至少** 64 KiB；遇到 4 MiB 帧会一路 `Array.Resize` 翻倍（100）到 8 MiB（>85 KB，进 LOH）并永久保留。MIX/PERSIST 臂开几千条连接时值得知道。

> 任务（低优先，别碰读路径）：注释说明增长上界由 `MaxPayloadLength` 决定；如果真要收，只在"解码完成后且剩余为 0"时收缩一次。

**4.6 最大障碍：`FrameStreamReader(Socket)`。**
构造函数绑死 Socket（25），使这个纯状态机无法脱离网络测试。

> 任务：改成接受最小接收接口
> ```csharp
> internal interface IFrameSource { ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct); }
> ```
> （`Socket` 用适配器或直接传方法组委托 `Func<Memory<byte>, CancellationToken, ValueTask<int>>`，字段缓存一次即可，热路径上就是一次委托调用）。**收益**：reader 可用脚本化喂数据源做全部边界测试，`TcpTargetServer` 的模式状态机也能顺手脱离网络（见 §10）。

---

## 5. Target servers 的结构与重复

### 5.1 重复清单（可直接变成提取任务）

| # | 重复内容 | 位置 A | 位置 B | 位置 C | 能否合并 |
|---|---|---|---|---|---|
| 1 | socket 创建 + `ReuseAddress` + Bind (+Listen) | `TcpTargetServer.cs:68-71` | `UdpEchoServer.cs:41-43` | `DnsServer.cs:41-48`（两个 socket） | ✅ `SocketFactory` |
| 2 | Accept 循环（含 `NoDelay=true`、`_connections.Add`、`>=256 → RemoveAll(IsCompleted)`） | `TcpTargetServer.cs:74-106` | `DnsServer.cs:215-244` | | ✅ 见 5.3 |
| 3 | `SendAllAsync` | `TcpTargetServer.cs:140-152`（`sent<=0` → **抛** `IOException`） | `DnsServer.cs:115-127`（`sent<=0` → **静默 return**） | | ✅ 但**必须统一语义**（建议抛） |
| 4 | `ReadExactAsync` | `DnsServer.cs:98-113`（唯一一份） | | | ✅ 收进同一个 `SocketIo` |
| 5 | `_sourceTemplate` 构造 | `UdpEchoServer.cs:37-39` | `DnsServer.cs:37-39` | | ✅ 逐字相同 |
| 6 | `WriteTotals` + `WriteSummaryAsync` | `TcpTargetServer.cs:115-138` | `UdpEchoServer.cs:65-118` | `DnsServer.cs:71-96` | ✅ `ILedgerSection` |
| 7 | 计数器读纪律 | `TcpTargetServer.cs:117-123`（`Interlocked.Read`/`Volatile.Read`） | `UdpEchoServer.cs:67-70`（裸读） | `DnsServer.cs:74-84`（裸读） | ⚠️ 见 5.4 |
| 8 | 停机取消与真错误共用 verdict | `TcpTargetServer.cs:104,234-249` | `DnsServer.cs:309-317` | | ⚠️ 见 5.5 |

### 5.2 P0：`ReuseAddress` 在 Linux 上顺带打开 SO_REUSEPORT

三处都写了 `SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, value: true)`（`TcpTargetServer.cs:69`、`UdpEchoServer.cs:42`、`DnsServer.cs:42,46`）。在 Linux 上 .NET 把这个选项映射成 **SO_REUSEADDR + SO_REUSEPORT**。实测确认：

```
fresh:   REUSEADDR=0 REUSEPORT=0
after SetSocketOption(ReuseAddress, true):  REUSEADDR=1 REUSEPORT=1
```

后果（同样实测）：

```
同一端口两个 TCP listener（都是 ReuseAddress）：bind 都成功 → 40 个连接全部进第一个，第二个 0 个
同一端口两个 UDP binder：bind 都成功 → 40 个数据报第一个 38、第二个 2
显式把 SO_REUSEPORT 清 0 再 bind：第二次 bind → AddressAlreadyInUse（这才是期望行为）
```

也就是说：**上一个 target 实例还活着时，新实例不会报错**，而是 bind 成功、几乎收不到流量，然后写出一份"产品把一切都丢了"的账本。`scripts/start-targets.sh:22-23` 只 `rm -f` 账本、不做单实例检查；老实例还持有被 unlink 的 inode 继续追加，新实例建了新文件——两条账本加上被切分的流量，分析侧只能靠 `LEDGER_CONNECTION_*` 容差发现（UDP 那一侧连这个都没有）。

> 任务（半天，最高性价比）：
> 1. 新增 `Target/Sockets.cs`：`internal static Socket BindUdp/Dgram(...)`、`BindTcpListener(...)`，内部统一做 `ReuseAddress` + **Unix 上把 SO_REUSEPORT 显式清 0**（`SetRawSocketOption(1, 15, stackalloc byte[4])`，Windows 上 `SO_REUSEPORT` 不存在 → 用 `OperatingSystem.IsWindows()` 分支或 catch `SocketException`），这样残留实例会**大声报 EADDRINUSE**（`Program.cs:81-85` → exit 1）。
> 2. 顺手把 #1 的 4 处 socket 构造、#5 的 template 逻辑收进去，并在注释里写下这段实测结论——否则下一个人一定会把它"简化"掉。

### 5.3 Accept 循环提取（并统一 shutdown 模型）

两个循环除托管对象外逐行同构。差异是 shutdown 来源：TCP 有私有 `_shutdown` CTS（`TcpTargetServer.cs:58,104`，在 accept 循环退出后 cancel，用来打断在途连接的读），DNS 直接把外层 token 透给连接处理（`DnsServer.cs:238`）。

> 任务：新增 `Target/TcpAcceptLoop.cs`（`AcceptAsync` + 连接表 + 256 修剪 + `_shutdown` CTS + drain），两台 server 各持一个实例。**行为等价性**：两者在停机时都走"外层 token 取消 → 循环跳出 → 连接被取消"这条路，所以统一到 TCP 模型不会改变可观测结果；但这是**需要跑一遍 `scripts/selftest.sh` 验证**的改动。
>
> 顺带修 5.6 的 `catch (SocketException) { continue; }`。

### 5.4 计数纪律不一致（低危但值得写下来）

`UdpEchoServer.WriteSummaryAsync`（73-118）和 `DnsServer.WriteTotals`（71-85）读 `long` 字段用的是普通读，而**它们是真的并发路径**——`UdpEchoServer.cs:176-194` 的 1 Hz 摘要循环与接收循环同时跑着。x64 上不会撕裂，所以不是 bug，但这是"靠平台"而不是靠语义；`TcpTargetServer` 用了 `Interlocked.Read`，同一个仓库里两种写法没有理由。

> 任务：三台 server 统一用 `Volatile.Read`/`Interlocked.Read`（或统一在注释里声明"只在循环停止后调用"，但 UDP 的 1 Hz 路径推翻了这个前提）。

### 5.5 停机取消被记成"错误"

`TcpTargetServer.cs:104` 先 `_shutdown.CancelAsync()` 再 drain；在途连接的读立刻抛 `OperationCanceledException` → 234-237 → `TcpVerdict.Error`。也就是说**正常停机必然产出一批 Error 行**，与真的 socket 故障无法区分。同一个方法里 4 种异常（234-249）压成同一个 verdict，账本里没有任何原因字段。DNS 同样（`_tcpAborted` 把"停机取消"和"对端 RST"合并，309-317）。

> 任务：账本记录里新增 `detail`（异常类型名）与/或 `TcpVerdict.AbortedAtShutdown`。会改变 verdicts 分布 → 需要一次 synthetic 数据集验证。

### 5.6 静默的失败模式（吞异常）

- `TcpTargetServer.cs:91-94` / `DnsServer.cs:232-235`：`catch (SocketException) { continue; }` **无计数、无退避**。EMFILE 一类错误会让 accept 循环 100% CPU 空转并静默停止服务，账本里毫无痕迹（`tcpSummary`/`dnsSummary` 都没有 `acceptErrors`）。
- `UdpEchoServer.cs:142-145` / `DnsServer.cs:164-167`：`catch (SocketException) { return; }` 让**一条**接收循环静默退出，`_receiverCount` 的有效并发度下降，没有任何记录。
- `TcpTargetServer.cs:100` / `DnsServer.cs:241`：`RemoveAll(static task => task.IsCompleted)` 会丢弃**已故障**的 task。目前没有已知抛出路径（`WriteConnectionAsync` 走的是吞异常的账本写入），但这是"一旦抛了就无声无息"的设计（.NET 默认不抛未观察异常）。

> 任务：`RemoveAll` 前先 `if (task.IsFaulted) RecordFailure(task.Exception!)`；`acceptErrors`/`receiverExits` 计数器上账（新增字段）；`continue` 加一个 1 ms 级别的退避或至少计数。

### 5.7 三台 server 的共同抽象（具体形状）

```csharp
internal interface ILedgerSection
{
    string RecordType { get; }              // "tcpSummary" / "udpSummary" / "dnsSummary"
    void WriteTotals(Utf8JsonWriter writer);
}

internal static class LedgerSections
{
    internal static ValueTask WriteSummaryAsync(this ILedgerSection section, LedgerWriter ledger, CancellationToken ct)
        => ledger.WriteAsync(w => { w.WriteString("type", section.RecordType); section.WriteTotals(w); }, ct);
}
```

比抽象的 `ServerBase` 更好——共享的是**记录形状**，不是 socket 生命周期；socket 生命周期交给 5.2/5.3 的两个小 helper。三份 `WriteSummaryAsync`（约 35 行）与 `TargetRunner.WriteSummariesAsync` 的 7 参数签名（`TargetRunner.cs:56-101`）一起消失。

---

## 6. LedgerWriter

**并发**：`SemaphoreSlim(1,1)`（12）串行化记录写入，1 Hz flush 循环走同一把门（151）→ 单进程内安全。`_disposed` 是非原子布尔（77），对"单调用者 + `await using`"够用；`DisposeAsync` 幂等。

**关闭语义**：定期 flush（8,146-165）+ 终结时 `_stream.FlushAsync` + `DisposeAsync`（96-97）。正确，但**正确性依赖 `TargetRunner.cs:19-25` 的 `await using` 声明顺序**（ledger 声明在最前 → 最后释放）。写反了的表现是"摘要丢一截 + `ledgerWriteErrors` 上涨"（因为 `WriteAsync` 会把 `ObjectDisposedException` 吞掉并计数）。这是一条隐形但致命的依赖。

> 任务：在 `LedgerWriter` class 注释和 `TargetRunner` 的 `await using` 上方各写一句"server 必须先于 ledger 释放"；更稳的做法是让 `TargetRunner` 显式 `try/finally` 控制顺序，而不是靠声明顺序。

**格式稳定性**：一行一个 JSON 对象；`utc`、`label` 在最前（50-51），body 自己写 `type`。`Utf8JsonWriter` 默认编码器会把非 ASCII 转义（label 含中文 → `\uXXXX`，`json.loads` 正常）。

> 任务：加一个"格式快照"测试（见 §10），把它从口头约束变成可执行约束。

**问题 1（P1）：catch 把 body 委托也包住了。**
`WriteAsync:41-63` 的 `try` 覆盖了 `body(writer)`（52）。于是记录体里的编程错误（null 引用、越界、将来某个枚举扩容导致的索引问题）与"磁盘写失败"完全同质化：**记录静默消失，只留下一个计数器**。`TcpTargetServer.cs:50-53` 的注释恰好描述了这类事故：

> "a ninth verdict would otherwise index past the end of the tally array inside the ledger writer, which swallows the throw, and every connection verdict in the run would go missing without a word."

作者已经识别了这个失败模式，并在**数组大小**上防住了，却在**同一个类的 catch 范围**上留了门。

> 任务：`body(writer)` 移到 try **之外**（或者只对 `_stream.WriteAsync` / 序列化设置 catch）。"磁盘故障不能弄死 target"这条策略完全保留，同时把 body 的 bug 变成响亮的失败。

**问题 2（P2）：每条记录的分配。**
`new MemoryStream(512)` + `new Utf8JsonWriter` + 捕获闭包（46-52）每条记录一次。同一个二进制里已经有零分配的写法：`Client/JsonlFile.cs:9,26-42`（复用 `MemoryStream` + `Utf8JsonWriter`，`SetLength(0)` + `Reset` + `FlushAsync` 后再追加 `\n`——顺序正确）。TCP 是每连接一条、UDP 是每秒一条，所以不是热点，但让同一仓库里两份 JSONL sink 用不同分配策略没有理由。

**问题 3（重复）：`Client/JsonlFile.cs` 与 `Target/LedgerWriter.cs` 是同一件事的两份实现。**
两者都是：`FileStream` + `SemaphoreSlim` 门 + `Action<Utf8JsonWriter>` 记录体 + `GetBuffer()` + `\n`。差别只有两点：(a) 是否吞异常（LedgerWriter 吞、JsonlFile 抛）；(b) 是否给每条记录自动包 `utc`/`label`。

> 任务（本次最省事、最不碰格式的合并）：`Wire/JsonlSink.cs`（或 `Cli/`），构造参数 `(path, JsonlPolicy policy, Action<Utf8JsonWriter>? envelope)`：
> - `LedgerWriter` = `policy: SwallowAndCount`, `envelope: 写 utc + label`；
> - `JsonlFile` = `policy: Propagate`, `envelope: null`；
> 内含复用的 writer/buffer（问题 2 一并解决）。**行为要保持**：client 侧的异常传播语义不能变（`ClientRunner` 依赖它把臂标记为失败）。

---

## 7. TargetRunner

**职责**：`RunAsync`(10-41) 编排 + `TryCreate`(104-164)/`Apply`(167-217)/`TryPort`(219-230) CLI 解析 + `WriteSummariesAsync`(56-101) 账本汇总 + `PrintHelp`(232-247) + `AnnounceAsync`(43-54)。文件不大，但三个关注点混在一起，而且 CLI 解析是 `ClientRunner.TryCreate`（`ClientRunner.cs:51-110`）的**逐字孪生**——连 `#pragma warning disable RCS1239` 的那句理由注释都一样（`TargetRunner.cs:103` vs `ClientRunner.cs:51`）。

**7.1 选项名有 3 份清单，必须手工同步**：`TargetOptions.s_knownOptions`（25-34）、`TargetRunner.Apply` 的 switch（170-216）、`PrintHelp`（234-246）。漏一处的失败模式是"用法错误"而不是静默错误（因为 `TryCreate:117` 会先拒绝未知选项），但每次加选项都要改三处。

> 任务：`internal sealed record TargetOption(string Name, string ValueHint, string Help, Action<TargetOptions,string> Apply)` 数组，三者全部派生。同理可推广到 client。

**7.2 CLI 解析提取**：

> 任务：`Cli/CommandLine.cs`：`internal interface IOptionSink { bool Apply(string name, string value, out string? error); }` + `static bool TryParse(string[] args, IReadOnlyList<string> known, IOptionSink sink, out string? error)`。`TargetOptions`/`ClientOptions` 各实现 sink；`#pragma RCS1239` 只需存在一处（`ClientRunner` 已经有 `TryAssignPort`/`TryAssignCount` 的更好写法，`ClientRunner.cs:127-140`，可以顺势统一）。

**7.3 端口校验不完备（P1）**：`TryCreate:142-161` 只校验范围以及 `dnsAltPort ∉ {tcp,udp,dns}`。**没有校验 `dnsPort` 与 `tcpPort`/`udpPort` 冲突**，而 `DnsServer` 同时监听 TCP+UDP 同一个端口（`DnsServer.cs:41-48`）——配合 5.2 的 SO_REUSEPORT，`--dns-port 40010 --udp-port 40010` 会**两个都 bind 成功**，UDP 报文按内核哈希分给二者（实测 38/2），测量静默失真。

> 任务：显式拒绝 `dnsPort ∈ {tcpPort, udpPort}` 与 `dnsAltPort ∈ {tcpPort, udpPort, dnsPort}`（后者已有），并在 `PrintHelp` 注释里说明"tcpPort 与 udpPort 相同是合法的"（默认值就是同一个数）。

**7.4 影响测量的参数既不可配也不记账**：`var workers = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);`（18）。它决定 UDP 接收并发度与 DNS worker 数，公式没有出处。

> 任务：加 `--udp-receivers`（默认保持现公式），并把生效值写进 `targetSummary`（新增字段，安全）。

**7.5 `PrintHelp` 漏了 exit 1**：`TargetRunner.cs:245` 写"Exits 0 on a clean shutdown, 2 on a usage error"，但 `Program.cs:81-85` 会把 bind 失败映射成 `ExitCodes.RuntimeError = 1`。加一行。

**7.6 `WriteSummariesAsync` 的 7 个参数**（56-63）：收集成 `ILedgerSection[]`（见 5.7）后可缩成一次遍历 + 一次 `targetSummary`。**注意保持写入顺序**（udp → dns → dnsAlt → tcp → targetSummary，65-72），因为账本是给人看的。

**7.7 `startedTicks` 的取点**（27）在 socket 构造之后、announce 之前，合理；`AnnounceAsync` 在 bind 之后调用（28），所以 bind 失败不会打印假消息，正确。

---

## 8. 超长方法与类型

| 文件:行号 | 名称 | 行数 | 问题 | 建议接缝 |
|---|---|---|---|---|
| `UdpEchoServer.cs:217-338` | `SourceCensus`（嵌套类） | **122** | server 里塞了第二个完整类型；`Record` 40 行、`Harvest` 23 行、`TryGetIdentity` 27 行 | 提成独立文件 `Target/SourceCensus.cs`（它的注释已经把它描述成独立机制），`Slot` 一起搬 |
| `TargetRunner.cs:104-164` | `TryCreate` | **61** | 手写解析循环，index 在两处推进（125、139），带 pragma | 抽 `Cli/CommandLine.TryParse` |
| `DnsWire.cs:152-207` | `BuildResponse` | **56** | 手工布局 + 类型 switch + 裸魔数 | 与 `TryParseQuery` 合并（§3.5）；A/AAAA 答案抽 `WriteAnswer` |
| `DnsWire.cs:50-104` | `BuildQuery` | **55** | label 循环 + 三处返回 -1 + 缺总长校验 | 抽 `TryWriteName(Span<byte>, ReadOnlySpan<char>, ref int offset)` |
| `UdpEchoServer.cs:120-174` | `ReceiveLoopAsync` | **55** | 接收 + 计数 + 解码判定 + 回显 + 5 个 catch | 抽 `TryEchoAsync`，catch 收进小 helper |
| `DnsServer.cs:270-320` | `HandleTcpConnectionAsync` | **51** | 长度前缀 + 读消息 + 回答 + 3 个 catch | 抽 `ReadLengthPrefixedAsync` |
| `TargetRunner.cs:167-217` | `Apply` | **51** | 6 个 case 每个都重复 try/assign/return | 用 7.1 的 descriptor 表 |
| `TcpTargetServer.cs:252-299` | `RunModeAsync` | **48** | 协议状态机 + socket 发送 + verdict 判定 混在一起 | 见 §10.3：抽 `TcpConnectionProtocol` |
| `UdpEchoServer.cs:73-118` | `WriteSummaryAsync` | **46** | 采集 + 排序 + 写 20 行 JSON | 采集/排序 vs 写 JSON 分开 |
| `TargetRunner.cs:56-101` | `WriteSummariesAsync` | **46** | 7 参数 + 4 次重复写入 | `ILedgerSection[]` |
| `DnsWire.cs:106-150` | `TryParseQuery` | **45** | label 循环 + 静默接受压缩指针 | 见 §3.3 |
| `FrameStreamReader.cs:37-77` | `ReadAsync` | **41** | `_hasFrame` 延迟推进 + 出错不清理 + 两处状态映射 | 返回 `ReadResult`（§4.1） |
| `TcpTargetServer.cs:212-250` | `RunConnectionAsync` | **39** | 首帧分类 + 4 个 catch → 同一个 verdict | 与 `RunModeAsync` 合成协议对象 |
| `DnsServer.cs:176-213` | `AnswerAsync` | **38** | 用 `bool` 返回值通信"是否继续" | 改成 `AnswerResult` 枚举或直接抛 |
| `LedgerWriter.cs:70-105` | `DisposeAsync` | **36** | 3 个 catch + 门 + 双 TryAsync | 可接受；合并进 `JsonlSink` 后重审 |
| `DnsServer.cs:140-174` | `UdpLoopAsync` | **35** | 与 `TcpTargetServer.RunAsync` 的 accept 循环同构 | 见 §5.3 |
| `TcpTargetServer.cs:74-106` | `RunAsync` | **33** | accept + 连接表 + drain | 见 §5.3 |
| `TcpTargetServer.cs:48-345` | `TcpTargetServer`（类型级） | **345**（整文件） | 4 职责：socket 生命周期 / accept 记账 / TCP 协议状态机 / 账本 JSON | 拆 2 个类型（`TcpConnectionProtocol` + server） |
| `UdpEchoServer.cs:11-339` | `UdpEchoServer`（类型级） | **339**（整文件，含 2 个类型） | server + `SourceCensus` 两个类型挤在一个文件 | 拆 2 个文件 |

**分界线建议**：`TcpTargetServer` 的协议部分（`RunConnectionAsync` 212-250、`RunModeAsync` 252-299、`CompleteOnEndOfStreamAsync` 305-327、`SendTrailerAsync` 329-344 = 约 120 行）整体搬进 `Target/TcpConnectionProtocol.cs`，把"回显"和"关闭"抽象成两个回调（`Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> send`、`Action shutdownSend`）。这样协议状态机可以脱离 socket 单测（配合 §4.6）。

---

## 9. 命名 / 注释 / 魔数 / 死代码 / 异常 / 释放

### 9.1 P0：`TcpCommand.Name` 的兜底分支会造出重复 JSON key

`TcpCommand.cs:39-59` 两个 `switch` 都有 `_ =>` 兜底：`Name(TcpMode)` → `"unknown"`，`Name(TcpVerdict)` → `"error"`。而 `TcpTargetServer.WriteTotals:121-124` 会**遍历枚举**写对象：

```csharp
foreach (var verdict in Enum.GetValues<TcpVerdict>())
    writer.WriteNumber(TcpCommand.Name(verdict), Volatile.Read(ref _verdicts[(int)verdict]));
```

实测 `Utf8JsonWriter` **不检查重名**：`{"error":1,"error":2}`。所以加第 9 个 verdict 时，`_verdicts` 不会越界（`s_verdictCount` 防住了，`TcpTargetServer.cs:50-53` 的注释也解释了为什么），但 `tcpSummary.verdicts` / `targetSummary.tcp.verdicts` 里会出现**两个 `"error"`**，Python 的 `json.loads` 后者覆盖前者 → 两个不同 verdict 的计数静默合并。同理 `Name(TcpMode)` 的 `"unknown"`（39-47）与 `WriteConnectionAsync:193` 的 `modeKnown ? … : "unknown"` 撞名：第 6 个 mode 会被记成"未知模式"，与"客户端没发命令"混为一谈。

> 任务（1 行 + 1 个测试）：
> 1. 去掉两个 `_ =>` 兜底，改成 `_ => throw new ArgumentOutOfRangeException(nameof(mode))`（或 `verdict.ToString()`，但要与 README:435-437 的拼写表对齐——注意枚举名 `ResetAfterN`/`PartialFin` 与输出 `resetAfterN`/`partialFin` 不同，`ToString()` 会破坏契约，所以**推荐 throw**）。
> 2. 加断言测试：`Enum.GetValues<TcpVerdict>().Select(TcpCommand.Name).Distinct().Count() == Enum.GetValues<TcpVerdict>().Length`，`TcpMode` 同理。这条测试能永久封住这一类 bug。

### 9.2 命名

| 位置 | 问题 | 建议 |
|---|---|---|
| `TcpCommand.cs:16` `PayloadLength` | 与 `FrameCodec` 的 "payload length"（28 字节头里的那个字段）同名不同物 | `CommandPayloadBytes` |
| `UdpEchoServer.cs:221,289,115` `_unplaced` / `HarvestUnplaced` / `"sourceOverflow"` | 一个概念三个名字（"census 表装不下的源端点"） | 统一到 `overflow`（**JSON 字段名不能改**，只改 C# 侧） |
| `UdpEchoServer.cs:196-204` `ToAddress` | 只为 `WriteString("address", ToAddress(key).ToString())`（108）存在，每次分配 `IPAddress` + string | 保留（1 Hz × 端点数，无所谓），但名字应表明"把 key 还原成 IPAddress" |
| `DnsServer.cs:246` `AnswerTcpAsync` / `:176` `AnswerAsync` | 一个带 Tcp 后缀一个不带，读者要回头看参数才知道 | `AnswerUdpAsync` / `AnswerTcpAsync` |
| `TcpTargetServer.cs:31` `CommandOutcome(Mode, ExpectedBytes, ModeKnown, Outcome)` | `ModeKnown=false` 时 `Mode` 仍写着 `TcpMode.Clean`（占位值），是个可误读的双字段表达 | 阶段：`record struct CommandOutcome(TcpMode? Mode, uint? ExpectedBytes, …)` |

### 9.3 注释与文档

- **`TrailerProtocol.cs:3-8`** 声称单点定义"形状"，实际只有常量共享（§1.4）。
- **`TargetRunner.cs:245`** 漏 exit 1（§7.5）。
- **`README.md:434-437`** 的 mode 表是 `TcpCommand.cs:5-12` 的手抄副本；`README.md:412-420` 的偏移表是 `FrameCodec.cs:42-45` 的手抄副本。改一处忘另一处就会产生"文档撒谎"。至少在两处加交叉引用（`see Wire/FrameCodec.cs:42-45`）。
- **`Crc32C.cs:22,30`** 的小端需要一行解释（§1.5）。
- **`Filler.cs:35`** 的 64→32 位截断需要一行解释（§2.2）。
- **`FrameStreamReader`** 整个类**没有一行文档注释**，而它的契约（错误后是否可继续、切片有效期）恰恰是最需要写下来的（§4.2/4.3）。

### 9.4 魔数（重复出现的）

| 值 | 位置 | 建议 |
|---|---|---|
| `512`（listen backlog） | `TcpTargetServer.cs:71`、`DnsServer.cs:48` | 命名常量 `ListenBacklog`，一处定义 |
| `256`（连接表修剪阈值） | `TcpTargetServer.cs:98`、`DnsServer.cs:239` | 命名常量（两处同值但含义相同，可共享） |
| `64`（`SourceCapacity`） | `UdpEchoServer.cs:14` | 有名字，好；但值得在注释里写明"64 × 8 receiver"的内存代价 |
| `2`（stall 秒） | `TcpTargetServer.cs:54` | 有名字且 README:459 有说明，好 |
| `4096`（`MemoryStream(512)` / `MaxMessageLength`） | `LedgerWriter.cs:46`、`DnsWire.cs:34` | `MemoryStream(512)` 的 512 建议命名或直接复用 buffer（§6 问题 2） |

### 9.5 死代码 / 未用

- `FrameCodec.OffsetClientSendTicks`（44）写入但**永不读**（§1.2-A）。
- `DnsWire.TypeCname/TypeTxt/TypeHttps`（29-32）在 target 侧永不命中（`BuildResponse` 不处理），只在 client 侧用。
- `FrameDecodeError.Truncated`（30）在 `TryReadHeader` 里可达（74-78），但 `FrameStreamReader:52` 把它映射进 `BadLength`，语义丢失。
- `FrameReadStatus` 里**没有** `Truncated`（§4.4）。
- `UdpEchoServer.ToAddress` 只有一个调用点（108）。
- 未发现真正无引用的方法——`internal` + 单程序集让"删除未用成员"这件事本身是可见的。

### 9.6 吞异常与资源释放

**吞异常清单**（按后果排序）：

| 位置 | 吞掉什么 | 后果 | 判定 |
|---|---|---|---|
| `LedgerWriter.cs:59-63` | 记录体 + I/O 全部 | 记录消失 + 计数（§6 问题 1） | **应收窄** |
| `TcpTargetServer.cs:234-249` | OCE / SocketException / IOException / ODE | 4 种原因压成 `Error`，无原因入账（§5.5） | 应收窄 |
| `TcpTargetServer.cs:91-94`、`DnsServer.cs:232-235` | accept 的任何 SocketException | 无计数无退避（§5.6） | 应收窄 |
| `UdpEchoServer.cs:142-145`、`DnsServer.cs:164-167` | 接收循环的 SocketException | 静默减少接收并发（§5.6） | 应计数 |
| `TcpTargetServer.cs:100`、`DnsServer.cs:241` | `RemoveAll` 丢弃故障 task | 未观察异常，无日志 | 应记录 |
| `LedgerWriter.cs:118-132,134-144` | stderr 写失败 | 有注释、有理由 | ✅ 保留 |
| `TcpTargetServer.cs:154-170` `RemoteEndPointText` | 取 endpoint 失败 → `"unknown"` | 合理 | ✅ 保留 |

**IDisposable**：

- 三台 server 的 `DisposeAsync` 都**不取消自己的循环**，靠 `Socket.Dispose()` 触发 `ObjectDisposedException`（`TcpTargetServer.cs:87-90,108-113`、`UdpEchoServer.cs:59-63`、`DnsServer.cs:51-56`）。这是有意的，但只体现在 catch 分支里；建议在 `DisposeAsync` 上补一句注释。
- `TcpTargetServer._shutdown` 的 cancel 发生在 `RunAsync:104`（accept 循环之后、drain 之前）——**顺序是语义**（先 cancel 才能打断在途读），值得一行注释。
- `TcpTargetServer.cs:281` 的 `Task.Delay(s_stallDelay, CancellationToken.None)` 是**故意不可取消**，README:465-468 有说明——这是"注释没撒谎"的正面例子，保留。
- `LedgerWriter.DisposeAsync:77` 的 `_disposed` 非原子；`Client/JsonlFile.cs:59-64` 同样。当前都是单调用者，够用；合并成 `JsonlSink` 时统一处理。
- `[StructLayout(LayoutKind.Auto)]` 出现在 6 个 struct 上（`FrameCodec.cs:6`、`DnsWire.cs:6`、`TcpTargetServer.cs:11,28`、`UdpEchoServer.cs:206,330`）。对 struct 而言默认布局是 Sequential，`Auto` 是让 JIT 收紧字段布局的**性能提示**——不是噪音，但一处解释都没有，读者会以为是抄来的。建议统一注释一次（或在 `.editorconfig`/spec 里写清"值类型在热路径按值传递，允许 JIT 重排"）。

---

## 10. 可测试性

### 10.1 现状：协议层零单元测试

全仓只有 `scripts/selftest.sh`（起 client + target 跑完整 plan）。它能证明"整套能用"，**证明不了"用错了会怎样"**：CRC 硬件路径、坏魔数、半帧、压缩指针 QNAME、重复 JSON key、端口撞车——一个都不会被它触发。而这套代码的全部价值就是"在异常路径上仍然数得对"。

### 10.2 结构性障碍（按修复难度）

1. **`Wire/` 全是 `internal` 且无 `InternalsVisibleTo`** → 任何测试工程都得先加这一行（仓库已有惯例：`src/WinForward.Core/WinForward.Core.csproj:8-9`）。
2. **`FrameStreamReader(Socket)`**（`FrameStreamReader.cs:25`）→ 接口化后可完全脱离网络（§4.6）。
3. **`TcpTargetServer.RunConnectionAsync/RunModeAsync` 直接持有 `Socket`**（212-299）→ 配合 2 变成纯签名活。
4. **`UdpEchoServer`/`DnsServer` 的计数器是私有字段**，无法从测试观察；但它们承载的判定逻辑很少，优先度最低。
5. **`LedgerWriter` 已经是"路径进、JSONL 出"的形状**（18-30）→ 只要允许注入 `Stream`（`LedgerWriter(Stream, string label)` 主构造，`LedgerWriter(string path, string label)` 转发），就能断言格式稳定性——**这是在保护账本契约**，价值很高。

### 10.3 应该先写的测试（全部纯函数 / 无网络 / 无 flakiness）

1. `Crc32C`：黄金向量 + 表路径与硬件路径一致。
2. `FrameCodec`：`WriteFrameInPlace` → `TryDecode` 往返；`BadMagic`/`BadLength`（4 MiB + 1）/`Truncated`/`BadChecksum` 四种错误各一条；**固化"D 项：尾部多余字节被接受"**（若将来决定改成 `!=`，这条测试就是变更单）。
3. `Filler`：`Fill`/`Matches` 对称 + 固定 `(conn,seq)` 的黄金字节 + 高 32 位截断事实。
4. `DnsWire`：`BuildQuery` → `TryParseQuery` 往返；压缩指针 QNAME；超长名字；5 种 queryType 的 `BuildResponse` 的 `answerCount`。
5. `TcpCommand`：**所有**枚举成员的 `Name` 互不相同（封住 §9.1）。
6. `LedgerWriter`：写 2 条 → 断言两行、`utc`/`label` 在最前、`\n` 结尾、字段集合快照（封住账本契约）。
7. `FrameStreamReader`（接口化之后）：一帧拆成 3 个 TCP 段喂进去；EOF 落在帧中间 → 断言 `Truncated`；两个坏魔数 → 断言第二次读仍返回 `BadMagic`（固化 §4.3 的现状或新契约）。

**合并后的入口**：`WinForward.E2E.Tests` 加入 `WinForward.slnx`（30-32 行附近有 benchmarks 文件夹），并纳入 AGENTS.md 的 `dotnet test WinForward.slnx -c Release` 门禁。

---

## 11. 不要动的地方

| # | 位置 | 为什么不能动 | 允许做什么 |
|---|---|---|---|
| 1 | 帧布局、魔数 `0x57464531`、5 个偏移、大端、header 28 / trailer 4（`FrameCodec.cs:36-59`） | client 与 target 是**两个独立部署的二进制**，版本可能不一致；README:412-420 是它的规格 | 加注释；`MaxPayloadLength` 从 `private` 提到 `internal`；**增加**校验（不改变已接受集合） |
| 2 | CRC32C 参数（`Crc32C.cs:8-10`） | 同上 | 加硬件/表路径一致性测试；性能优化必须逐字节等价 |
| 3 | `Filler` 的输出字节流（`Filler.cs:8-45`） | 同上，且 client 靠它校验 payload 内容 | 批量展开 xorshift（输出等价）；**禁止**换 PRNG / 改种子公式 / 改 `ZeroSeedSubstitute` |
| 4 | `TrailerProtocol` 的 4 个常量（12-21） | 两端必须同时换 | 只能**新增**辅助方法（如 `IsTrailerSequence`） |
| 5 | `TcpCommand.Write` 的 5 字节布局与 mode 编码（16-37） | 同上；README:434-437 是规格 | 改名 `PayloadLength` → `CommandPayloadBytes`（纯 C# 侧） |
| 6 | `DnsWire` 生成的查询/响应字节 | 产品会按 DNS 特例处理（AGENTS.local.md §3 的 53 与 40053 设计就是为此）；响应形状直接决定 `answerRate`/`emptyAnswers` 等已发布指标 | 内部重构（`BuildResponse` 签名、命名常量）；若要拒绝 QNAME 压缩指针，需先确认没有真实 resolver 这么发 |
| 7 | 账本字段名、`type` 字符串、`utc`/`label` 的位置与顺序 | **靶机与客户端是两个独立部署的二进制**（同一份账本可能被不同版本的写入端与读取端交错消费），且格式一旦漂移，"靶机记了什么"这件事就无法与客户端侧对齐（详见 §11 末尾的适用性说明） | 仍建议**以新增字段为主**；由于已决定不做向后兼容，**改名/改序在重构中可以一次做完，但必须与读取端同一次提交落地** |
| 8 | `udpSummary` 的 **1 Hz 节奏** 与 **`sources`/`sourceOverflow` 是区间增量**（`UdpEchoServer.cs:209-300`） | 增量语义是 `SourceCensus.Harvest` 的设计核心（`Harvest` 发布"自上次发布以来的增量"），改成累计会让同一份数据在同一消费者手里翻好几倍；节奏决定"区间"这件事本身是否成立 | 在 `WriteSummaryAsync` 上方补一句"这是增量不是累计"；字段名随重构一起改可以，但必须同时确认消费端的求和逻辑 |
| 9 | `_sourceTemplate` 共享给 N 个并发 `ReceiveFromAsync`（`UdpEchoServer.cs:37-39,128`；`DnsServer.cs:37-39,150`） | **实测安全**：`ReceiveFromAsync` 每次自己 `Serialize()` 出新的 `SocketAddress`，返回的 `RemoteEndPoint` 是新实例（实测 `sameInstance=False`） | 加一行"已实测安全，勿在热路径加锁/拷贝"的注释 |
| 10 | `#pragma RCS1239`（`TargetRunner.cs:103`、`ClientRunner.cs:51`）、`Task.Delay(2s, CancellationToken.None)`（`TcpTargetServer.cs:281`）、`LingerState = 0`（291） | 分别是分析器与手写循环的真实冲突、不可取消的注入故障语义（README:465-468）、RST 注入的实现手段 | 提取到共享 helper 后 pragma 只剩一处（§7.2）；语义不动 |
| 11 | AOT / trim | 根 `Directory.Build.props` 对**所有**项目开 `EnableAotAnalyzer`/`EnableTrimAnalyzer` + `TreatWarningsAsErrors`（E2E 本身没有 `PublishAot`，但门禁是全局的） | `Enum.GetValues<T>()` 安全；`Utf8JsonWriter` 安全；**禁止**引入 `JsonSerializer.Serialize(pojo)` 反射路径（会直接编译失败，也确实会破坏将来的 AOT 发布） |
| 12 | `Interlocked.Read` / `Volatile.Write` 的无锁发布（`UdpEchoServer.cs:224-287`，尤其是"最后写 `_port`"的发布协议） | 这是**为了让每个数据报零分配零锁**而设计的（注释 209-216 已说明），是热路径；改成锁或 `ConcurrentDictionary` 会把 target 的每包成本抬进被测路径 | 可以重命名/搬文件，但发布顺序与内存序必须逐字保留；三台 server 间统一读纪律（§5.4）不涉及此处 |

### §11 的适用性说明（范围决定后）

范围决定为"彻底重构、不保留向后兼容"之后，本节的 12 条按理由分成三组，**没有一条因此失效**：

- **理由已变但结论不变（7、12）**：第 7 条（账本字段名/`type`/顺序）原本的依据是"`analyze.py` 消费"，现在改为"靶机与客户端是两个独立部署的二进制"——同一份账本会被不同版本的写入端与读取端交错消费，字段语义漂移会让"靶机记了什么"无法与客户端侧对齐；第 12 条（无锁发布）原本也提到消费方，现在改为"热路径"——把每包零分配零锁的设计换成锁或 `ConcurrentDictionary`，等于把一个测量工具的开销塞进被测路径。两条的**做法**因此放宽了一档（改名/改序可以在重构里一次做完），但**约束**（一次落地、不破坏语义）没有放宽。
- **完全有效且理由不变（1–6、8、9、10、11）**：线格式六条基于"两个独立部署的二进制"这一硬件事实；第 8 条基于 `Harvest` 的增量设计；第 9 条基于实测；第 10 条是分析器/注入语义/实现手段的约束；第 11 条（AOT/trim）与范围决定完全无关——根 `Directory.Build.props` 的 `EnableAotAnalyzer` + `EnableTrimAnalyzer` + `TreatWarningsAsErrors` 保持不变。

---

## 12. 建议的重构顺序（收益 / 风险）

| 序 | 任务 | 收益 | 风险 | 需要的行为保证 |
|---|---|---|---|---|
| **1** | `TcpCommand.Name` 去掉两个 `_ =>` 兜底 + 加"名字互不相同"测试（§9.1） | 消灭一类静默数据合并 | **极低** | 输出字符串与 README:434-437 完全一致 |
| **2** | 端口校验补齐 `dnsPort ∉ {tcpPort, udpPort}`；`Target/Sockets.cs` 统一 bind，Unix 上显式清 SO_REUSEPORT（§5.2, §7.3） | 残留实例从"静默窃取端口"变成"响亮 EADDRINUSE" | 低（Windows 分支要 try/catch） | 首次启动行为不变；重启仍能绑（SO_REUSEADDR 保留） |
| **3** | `LedgerWriter.WriteAsync` 把 `body(writer)` 移出 catch（§6 问题 1） | 记录体的编程错误不再静默 | **极低** | "磁盘故障不弄死 target"策略保留 |
| **4** | 加 `WinForward.E2E.Tests` + `InternalsVisibleTo` + §10.3 的 1/2/3/5/6 号测试（纯函数，无网络） | 把 CRC/Filler/帧/账本契约变成可执行约束；后续所有重构的安全网 | 低（新工程，不改产品代码） | `dotnet test WinForward.slnx -c Release` 保持绿 |
| **5** | 抽 `JsonlSink`（`LedgerWriter` + `JsonlFile` 合一，复用 writer/buffer）（§6 问题 2/3） | 删约 80 行 + 去掉每条记录的两次分配 | 中（client 侧的异常传播语义不能变） | client 臂失败仍能被 `ClientRunner` 观察到；账本格式一致（用 4 号测试兜） |
| **6** | 抽 `ILedgerSection` + `SocketIo`（`SendAllAsync`/`ReadExactAsync` 统一，`sent<=0` 统一为抛）（§5.1 #3/#4/#6, §5.7） | 删约 60 行；消掉"同名不同语义"的 `SendAllAsync` | 中（DNS 的 `SendAllAsync` 语义变化会改变回显失败路径的行为） | 用 selftest 跑一遍 UDP/DNS 臂，对比计数 |
| **7** | 抽 `TcpAcceptLoop`（accept + 连接表 + 256 修剪 + `_shutdown` + drain），统一到 TCP 模型；`SocketException` 加计数与退避；`RemoveAll` 观察故障（§5.3, §5.6） | 删约 50 行；停机行为一致；不再有静默的 accept 死亡 | 中（DNS 的 shutdown 来源从外层 token 变成私有 CTS，需验证） | `scripts/selftest.sh` 全绿；关机时 DNS/TCP 的中断计数分布不变 |
| **8** | 抽 `Cli/CommandLine`（两处解析合一）+ 选项清单单一来源 + help 补 exit 1（§7.1, §7.2, §7.5） | 删约 60 行；加选项只改一处；`#pragma` 只剩一处 | 低 | 两个 verb 的 `--help` 文本与错误消息不变 |
| **9** | `FrameStreamReader`：`IFrameSource` 接口化 + 返回 `ReadResult` + 新增 `Truncated` + class 文档（§4.1-4.6） | 唯一能同时解决"可测性"和"截断被误判成 half-close"的改动 | **中高**（6 个调用点 + 一条新的 verdict 路径） | 逐分支对比：6 个调用点的 status 处理不变，只有"EOF 落在帧中间"从 `EndOfStream` 变成 `Truncated`（这是有意的行为变化，需在 selftest 里确认没有误报） |
| **10** | 拆文件/拆类型：`SourceCensus.cs`、`TcpConnectionProtocol.cs`、§8 表格里的长方法（`BuildQuery`/`BuildResponse`/`HandleTcpConnectionAsync`/`AnswerAsync`） | 可读性 + 让协议状态机可单测 | 中 | 纯搬移 + 签名变化，行为靠 4 号测试与 selftest 兜 |
| **11** | `DnsWire.BuildResponse` 签名收敛（吸收 `DnsServer.BuildAnswer`）+ 显式化 `_ => 0` + 总长校验（§3.1, §3.4, §3.5） | 删一层 + 消一处不变量 + 关掉两个静默陷阱 | 中（响应字节必须逐字节不变） | 用 4 号测试做往返对比；**任何响应字节变化都要重新跑一遍产品对比** |
| **12** | 账本新增 `detail`（Error 原因）/`acceptErrors`/`udpReceivers`（§5.5, §5.6, §7.4） | 让"停机取消"与"真错误"可区分；让未知参数可见 | 中（先跑 `analysis/synthetic` 验证） | 只加字段不改名；`analyze.py` 输出不变 |
| **13** | 性能（可选，最后）：`Filler` 批量展开、CRC 三路并行、读缓冲收缩（§2） | target 侧 CPU 余量 | 中高（线格式相邻） | 每一项都要有"逐字节一致"的测试（4 号测试兜）；先确认 target 是瓶颈再动 |

**不建议做的**（投入产出比差或风险不成比例）：给 `FrameCodec.WriteHeader/FinishFrame` 加返回值式错误处理（调用方都是自己人，异常足够）；给 `UdpEchoServer.SourceCensus` 换哈希表（零分配设计是刻意的，收益只在极端 pps 下出现）；把账本的 `sources` 改成累计（会破坏"区间增量"的设计核心）；给 `_sourceTemplate` 加锁（实测无必要，且会进热路径）。

---

## 附：本次审查的限定与未做的事

- **未跑** `jb inspectcode`（AGENTS.md 说 10-20 分钟）；`dotnet build -c Release` 已跑，0 警告，所以引用的都是"能通过现有门禁"的代码。
- 三个运行时探针是独立的 `/tmp` 小程序，**不是仓库代码**，结论（SO_REUSEPORT 映射 / `Utf8JsonWriter` 重复键 / `ReceiveFromAsync` 模板安全）已写进正文对应小节：
  1. `SocketOptionName.ReuseAddress = true` 之后读回 `SO_REUSEADDR = 1`、`SO_REUSEPORT = 1`；显式把 `SO_REUSEPORT` 清 0 后，第二个 listener 的 bind 报 `AddressAlreadyInUse`。
  2. 同端口两个 listener/binder 都能 bind 成功；TCP 40 个连接全部进第一个，UDP 40 个数据报第一个 38、第二个 2。
  3. `Utf8JsonWriter` 写出 `{"error":1,"error":2}` 且不抛异常。
  4. `ReceiveFromAsync` 调用后传入的 `IPEndPoint` 模板未被修改（`template = 0.0.0.0:0`），返回的 `RemoteEndPoint` 是新实例（`sameInstance=False`）。
- 我**没有修改任何文件**。
- 账本的**语义**问题（归属、UDP 无逐包记录、静默丢弃不可解码包）按分工没有重复，`.trellis/tasks/10-06-e2e-competitor-benchmark/research/harness-audit.md` 已覆盖；本报告只在"这些语义问题背后的代码结构成因"上做了补充（例如 `TcpCommand.Name` 的兜底、`LedgerWriter` 的 catch 范围、`FrameStreamReader` 缺失的 `Truncated` 状态）。
