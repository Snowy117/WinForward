# WinForward 提交 9c6aff7 审查建议汇总

- **审查对象**：`9c6aff7` — *fix(attribution): reuse one owner-table slot per kind so a scan allocates nothing*
- **代码基线**：仓库 HEAD `0665290`；经 `git diff --stat 9c6aff7 HEAD -- src tests benchmarks` 确认，这三个目录与提交时完全一致，因此本文的行号与结论对提交本身有效。
- **审查性质**：代码正确性与仓库规范（`.trellis/spec`）符合性审查。提交记录的性能数据（0.53 MB/s、52×、2402 scans、REL A/B 0/1795）按约定采信，未复测。
- **本文范围**：审查中提出的全部建议，按“规范 / 测试 / 文档 / 命名 / 原生内存 / 设计”分组，并附复跑证据与行动清单。

---

## 0. 已复跑的门与结论

| 门 | 命令 | 结果 |
|---|---|---|
| Release 构建 | `dotnet build WinForward.slnx -c Release` | 0 Warning / 0 Error |
| 全量测试 | `dotnet test WinForward.slnx -c Release --no-build` | exit 0，各程序集全绿 |
| 目标项目 | `dotnet test tests/WinForward.Windows.Tests/WinForward.Windows.Tests.csproj -c Release --no-build` | 60/60 通过（含 11 条 cache facts、2 条新 parser facts） |
| 新 gate 单独跑 | `dotnet test WinForward.slnx -c Release --no-build --filter "FullyQualifiedName~WinForward.Windows.Tests.IPHelperOwnerTableParserTests"` | 2/2 通过（Linux 主机，无 iphlpapi） |
| 格式门 | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | exit 0，空输出 |
| 有效行上限 | `python3 tools/effective-lines.py <changed files>` | exit 0（≤400 有效行） |
| JetBrains 检查 | `jb inspectcode` | 未复跑（作者记录为干净；该项 10–20 分钟） |

**总体结论**：未发现会造成归属错误或性能回归的缺陷。缓存语义与旧实现逐分支等价（`T < / = / > requestInstant` 三种情形逐一核对）；IPv4 解码的端序等价性经独立程序实测确认。问题集中在规范漂移、新路径的测试覆盖缺口，以及若干文档与命名一致性问题。

### 0.1 核对无误、无需改动的部分（记录在案）

1. **缓存语义等价**：旧实现的“锁外正命中快路径 + 锁内 `TakenUtc >= requestInstant` 权威分支”合并为同一把 per-kind gate 下的三分支后，正命中、负答案、强制重扫的时机与旧版一致；`ReadCount` 仍只在成功 publish 后自增；`WindowMs = 0` 的“保留 coalescing、去掉 reuse”语义不变。
2. **IPv4 解码等价**：`DecodeIPv4Address` 读驱动写入的网络序字节，`IPAddressValue.FromIPv4` 以 `ReadUInt32BigEndian` 落到 `Bits`，与 `IPAddressValue` 的契约自洽；旧的 `new IPAddress(row.LocalAddress)` 在 little-endian 上得到同一地址（实测 C0 00 02 0A → 192.0.2.10）。新写法额外把端序显式化。
3. **失效顺序**：`Begin*Fill` 先置 `IsAvailable = false`、`CompleteFill` 最后置 `true`，fill + 搜索 + publish 全在同一 gate 内，不存在“两张表混合可搜索”的窗口。
4. **`Endpoint(AddressFamilyKind, IPAddress, ushort)` 构造器删除**：repo-wide `rg`（含 tests/benchmarks）确认最后调用者已消失；family 由 `DecodeIPv4Address` / `DecodeIPv6Value` 结构性决定，原 fail-closed 前置检查不再必要。属于接口变深，方向正确。
5. **依赖方向**：`WinForward.Windows` 不能引用 `WinForward.Runtime`，用回调注入 counter 是正确方向；`static () => RuntimeCounters.Shared.Increment(...)` 不产生闭包。
6. **注释与文档约定**：新增注释无归档指针、无 `;` / `)` 结尾；返回槽位的“有效期到同 kind 下一读”写进了接口契约。

---

## 1. 规范符合性建议

### S1（中）缓存契约的 owner 文档仍是旧实现

- **文件**：`.trellis/spec/backend/traffic-policy-lifecycle.md`，`Process Attribution: The Owner-Table Cache` 一节（约 L92–L154）
- **问题**：
  - L94 / L97 仍写 “it **allocates**, it awaits”——与新增到 `.trellis/spec/backend/hot-path.md` 的“进程归属有分配预算”规则直接矛盾，且这两句本身重复；
  - L99 的 “One snapshot slot” 未跟上 reusable slot 的词汇；
  - L119–L121 说 `IPHelperOwnerTableReader` 拥有 “the fail-closed row-count validation”，实际校验已移到 `IPHelperOwnerTableParser`；
  - L145–L149 的 “Correct” 代码片段仍是旧实现的锁外 fast path（`snapshot.TakenUtc <= requestInstant`）。
- **影响**：`hot-path.md` 的 “Where things moved” 表把缓存契约指向这一节，它是规则的 owner；后续文档提交 `4fba0a4` 更新了另外三份 spec，唯独漏了它。
- **建议**：补一次 spec 修订——删掉 “it allocates” 与重复句，改用槽位词汇，更新 seam 描述，替换旧代码片段。

### S2（中）新 0 B gate 未登记进 per-gate proof 清单

- **文件**：`.trellis/spec/backend/allocation-gate-host-lumps.md`，per-gate 清单（约 L122–L133）
- **问题**：清单是失败分类的凭据（“the gate is a recorded victim”）。`WinForward.Windows.Tests.IPHelperOwnerTableParserTests` 不在其中；该项目名下登记的却是 `WinForward.Windows.Tests.ProcessOwnerTableCacheTests`，而后者不含任何 exact gate（该文件无 `GC.GetAllocatedBytesForCurrentThread`）。
- **影响**：新 gate 若被 host lump 命中，会落入 `UNEXPLAINED FAILURE` 分支，而不是被归类为已记录事件。
- **建议**：新增 `WinForward.Windows.Tests.IPHelperOwnerTableParserTests:tests/WinForward.Windows.Tests/IPHelperOwnerTableParserTests.cs` 一行；同时复核 `ProcessOwnerTableCacheTests` 那条是否为误登（它出现在 2026-10-01 的 gate-stability 记录里，但那是稳定性证明目标，不是 exact gate）。

### S3（中）新填充协议（状态机）没有测试覆盖

- **文件**：`src/WinForward.Windows/IProcessOwnerTableReader.cs`（`OwnerTable`，L140–L167）；测试缺口位于 `tests/WinForward.Windows.Tests/IPHelperOwnerTableParserTests.cs`
- **问题**：PRD R5 要求“rejected-read 行为保持覆盖”，但本次新增的状态没有任何断言：
  1. `Begin*Fill` 之后到 `CompleteFill` 之前 `IsAvailable == false`（进行中不可搜索）；
  2. `OwnerTable.Unavailable` 的不可填充守卫（`AssertFillable` 抛 `InvalidOperationException`）；
  3. 两种失败时机的不同语义：`ValidateRowCount` 在 `Begin*Fill` 之前抛出（槽位仍 available、旧行仍可 reuse），fill 开始后失败则槽位失效；
  4. 现有 fake `ScriptedOwnerTableReader` 每次 `Script` 换实例、从不失效，因此这层契约在测试里完全没有模型。
- **建议**：补 3–4 条事实，覆盖上述 1–3；可在 `IPHelperOwnerTableParserTests` 内完成（该测试项目已有 internal 访问）。

### S4（中）四个 fill 只有一个有测试，UDP4/UDP6/TCP6 解码零覆盖

- **文件**：`src/WinForward.Windows/IPHelperOwnerTableParser.cs`（`FillUdp4` / `FillUdp6` / `FillTcp4` / `FillTcp6`，L18–L74）
- **问题**：现有测试只走 TCP4。IPv6 路径的两个危险点没有断言——`row.ScopeId`（UDP6）与 `row.LocalScopeId` / `row.RemoteScopeId`（TCP6）的对应关系、16 字节 fixed buffer 的切片；UDP4 的端口/地址解码同理。0 B gate 只跑 TCP4；`WindowsBoundaryAuditTests` 测的是旧的 `IPHelperAbi.DecodeIPv6Address`，不是新解析器。
- **建议**：按现有 `BuildTcp4Image` 的形状补 UDP4 / UDP6 / TCP6 的 image 构造与解码事实（IPv6 用 scope id 非零的样本）。

---

## 2. 文档准确性建议

### S5（低）缓存文档把失败的两种时机混为一谈

- **文件**：`src/WinForward.Windows/ProcessOwnerTableCache.cs`，类型文档 L25–L28
- **问题**：“A read that fails leaves the slot unavailable, so its previous contents stop answering” 只对“fill 已开始”的失败成立；`ValidateRowCount` 在 `Begin*Fill` **之前**抛出时，槽位仍 available 且保留旧行（旧快照可以继续按 reuse 规则回答正命中）。代码行为与旧实现一致（符合 R2），因此是文档问题；提交信息里的 “a failed scan cannot answer stale” 同理。
- **建议**：改为 “A fill that begins invalidates the slot; its previous contents stop answering until a later fill completes.”；如需保留旧行为，另加一句说明“校验失败不发生失效，等同该次读取未发生”。

### S6（低）design.md 与实现不同步

- **文件**：`.trellis/tasks/10-10-owner-table-scan-alloc/design.md`
- **问题**：
  1. “Native buffer retention”（reader 持有一个常驻 `nint`、`Marshal.ReAllocHGlobal` 增长）没有实现，`IPHelperOwnerTableReader` 仍是每次 scan 一次 `AllocHGlobal/FreeHGlobal`；PRD 并未要求，且该草图若照做还有并发漏洞（见第 4 节）。
  2. Observability 里写的 counter 名是 `attributionOwnerTableReads`，落地为 `attributionOwnerTableScans`（spec 已按落地名更新，design 未同步）。
- **影响**：下一位读者会以为常驻 buffer 已经实现。
- **建议**：把这两点标为“未实现 / 已改名”，或按第 4 节的取舍正式记一笔“决定不做”。

### S7（低）提交标题的措辞

- **问题**：标题 “a scan allocates nothing” 严格讲只对 managed heap 成立（生产路径每次 scan 仍有一次 unmanaged alloc/free 与一次系统级枚举）。PRD R1 与 spec 的表述是准的。
- **建议**：后续引用时写成 “allocates nothing on the managed heap”。无需改写历史提交。

---

## 3. 命名与代码一致性问题（低）

### S8 `DecodeIPv6Value` 是一行转发

- **文件**：`src/WinForward.Windows/IPHelperOwnerTableParser.cs` L81–L82
- **依据**：`.trellis/spec/backend/directory-structure.md` 的 Split Discipline：“A pass-through alias — a one-line forwarder — gets no method of its own; inline it at the call site.”
- **建议**：在调用点直接使用 `IPAddressValue.FromIPv6(span, scopeId)`。`DecodeIPv4Address` 不是转发（它包了裸内存读取），保留。

### S9 gate 命名偏离仓库的 `*AllocatesNoManagedBytes` 族

- **文件**：`tests/WinForward.Windows.Tests/IPHelperOwnerTableParserTests.cs` L33–L57，`AFillAndItsLookupsAllocateNothingInSteadyState`
- **问题**：与仓库其余 20 多条 `*AllocatesNoManagedBytes` gate 不同族，`--filter` 检索 exact gate 时会漏掉它。
- **建议**：若要统一，改为 `…FillAndLookupsAllocateNoManagedBytes`，并同步 spec 与 PRD 中的引用（该名字已出现在 `allocation-gates.md`、`native-lease-and-pool-lifetime.md`、PRD AC3）。属于可选项，避免为改名引出一轮文档同步也行。

### S10 cache 的 `ReadCount` 与 “scan” 词汇并存

- **文件**：`src/WinForward.Windows/ProcessOwnerTableCache.cs` L50–L51
- **问题**：文档已改为 “Successful owner-table scans”，属性名仍是 `ReadCount`；fake 的 `ReadCount` 又计“尝试次数”，两者语义不同名同。
- **建议**：可改名 `ScanCount`（仅一处测试断言 `Assert.Equal(0, cache.ReadCount)`），或至少在文档里点明与 fake 计数的差别。低优先。

---

## 4. `Marshal.AllocHGlobal`：定位与替代方案

### 4.1 事实澄清

- **不是本次提交引入的**：`git log -S "Marshal.AllocHGlobal" -- src/WinForward.Windows` 指向 `4a0f70e`；`9c6aff7^` 的 `IPHelperOwnerTableReader.cs` 第 108 行即是它，本次 diff 只编辑了类文档一行。
- **但它是 `src/` 里的孤例**：其余原生分配均为 `NativeMemory.AllocZeroed/Free`（`NdisReadPacketCalls.cs`、`NdisPacketBuffer.cs`、`Core/NativeBufferPool.cs`）。
- **IL 证据**：.NET 10 中 `Marshal.AllocHGlobal(IntPtr)` 的实现就是调用 `NativeMemory.Alloc(UIntPtr)`（再调 `Malloc`，null 时抛 OOM）——两者是同一个分配器，只是 API 拼写不同。
- **为什么必须调用方分配**：`GetExtendedTcpTable` / `GetExtendedUdpTable` 是“探测 size → 调用方提供 buffer”的两段式契约，owner-PID 表没有系统分配的变体；同目录的 `UnicastAddressInventory` 之所以不用 `AllocHGlobal`，是因为它用的 `GetUnicastIpAddressTable` 由系统分配、`FreeMibTable` 释放（API 家族不同）。
- **是否存在“为了过 managed gate 而滥用”**：不成立。0 B gate 走脚本化 image + parser，全程不碰 native；生产路径的 unmanaged 分配早于本次改动；被测的 27.8 MB/s 是 `dotnet.gc.heap.total_allocated`，trace 点名 `TcpOwnerRow[]` 与 `IPAddress`；且 `finally` 内当次释放，不驻留、不成阶梯。

### 4.2 可选方案与取舍

| 方案 | 做法 | 代价 | 结论 |
|---|---|---|---|
| A. 现代化拼写 | `Marshal.AllocHGlobal/FreeHGlobal` → `NativeMemory.Alloc/Free`（保持不清零）或 `AllocZeroed`（对齐仓库习惯，多一次 memset） | 两行 + 类文档那句 `<c>Marshal.FreeHGlobal</c>`；零行为差异 | **建议做** |
| B. 常驻 buffer（design.md 原计划） | reader 持有 **per-kind** 常驻 buffer，增长不回收 | 需 `IDisposable` + composition teardown 接线；常驻内存停在见过的最宽表（busy desktop 上 MB 级） | 仅作为独立、可测量的改动；否则不做 |
| C. 池化 pinned 托管 buffer | `ArrayPool<byte>.Shared.Rent(size)` + `fixed` | 新 size class 会分配托管数组（把 managed 分配请回 scan 路径）；`NativeBufferPool` 是固定 size，装不下变长表 | 不优于 A |
| D. 换 API | 系统分配的 owner-PID 接口 / WMI / ETW | 枚举不存在；WMI/ETW 为 PRD 明确 out of scope | 不可行 |

**方案 B 的三个必须解决的问题**（照 design.md 草图直接做会是坏方案）：

1. **必须 per-kind**：4 个 kind 各有独立 gate、可以并发 fill，共用一个 buffer 是数据竞争；
2. **释放所有权**：`IPHelperOwnerTableReader` 无 `IDisposable`、`WindowsProcessAttributor` 不可释放，buffer 会活到进程结束，需接入 composition 的 teardown，才符合仓库“每次 rent 恰好释放一次”的纪律；
3. **常驻内存**：AC1 的残差本身就在讲 committed memory，增加常驻 buffer 需先有测量证明其收益大于代价。

**建议**：先执行 A；B 若要做，单独立项并先测量；无论做不做，都在 `design.md` 记下决定，避免“设计说要做、代码没做”的长期误读。

---

## 5. 设计层面讨论（codebase-design 视角，均为可选项）

### S11 `OwnerTable` 接口变宽，时序不变式靠文档维系

- **位置**：`src/WinForward.Windows/IProcessOwnerTableReader.cs`（类文档 L50–L88、接口文档 L31–L38）
- **观察**：`OwnerTable` 从“不可变值 + `Lookup`”变成“`Begin*Fill` / 写 span / `CompleteFill` 的时序协议 + `IsAvailable` 状态 + 共享 `Unavailable` 常量 + `fillable` 标志”。接口现在携带类型系统表达不了的时序不变式：begin 与 complete 之间不可搜索；fill 与搜索必须由同一把 gate 串行化——只能靠接口文档文字约定“调用方必须在 `ProcessOwnerTableCache` 的 per-kind gate 下搜索”。
- **可讨论方向**：让槽位与它的 gate 同处一个模块（槽位由 cache 拥有，或 reader 暴露 `ReadInto` 而 gate 自持），代价是 scripted seam 形状要变。当前取舍有据可依（fake 要能替换 native 读取），记录在案即可。
- **风险等级**：不会导致缺陷；若未来出现第二个 reader 实现或第二个 cache 实例，这条“同 kind 一把锁”的隐式耦合会成为 bug 源。

### S12 `fillable` 布尔构造参数

- **位置**：`src/WinForward.Windows/IProcessOwnerTableReader.cs` L82–L88、L104–L118
- **依据**：`.trellis/spec/backend/quality-guidelines.md` 的 Deep modules 一节：“Ownership is expressed in the constructor signature, not in boolean flags or create-if-null branches.”
- **建议**：可考虑让“不可填充”成为类型事实（例如 `OwnerTable.CreateUnavailable()` 返回一个子类型或不同的叶类型），而非运行时标志。当前实现只有一处调用，属可选项。

### S13 `ReadSink` 的形状与调用位置

- **位置**：`src/WinForward.Windows/ProcessOwnerTableCache.cs` L53–L58、L100
- **更正**：该形状是仓库既有模式——`NativeBufferPool.AccountingSink` 用同样的 `get; set;` 属性、同样的 “Composition sets it once at startup… must not throw and never influences … behavior” 文档，同样的 `static () => RuntimeCounters.Shared.Increment(...)` 在 `DurableCaptureBundle.cs:198` 有先例。因此**不作为本次提交的问题**。
- **唯一剩余差异**：`AccountingSink` 在无锁的 rent/return 路径上调用，`ReadSink` 在持有 per-kind gate 时调用。若未来要统一 sink 契约，可两处一起做：把调用移出锁，或在其外包裹 `try/catch`，让“诊断不影响行为”成为强制而非约定。

---

## 6. 行动清单（按性价比排序）

| # | 优先级 | 动作 | 涉及文件 | 工作量 |
|---|---|---|---|---|
| 1 | 高 | 修订 `traffic-policy-lifecycle.md` 的缓存一节（S1） | `.trellis/spec/backend/traffic-policy-lifecycle.md` | S |
| 2 | 高 | 把 `IPHelperOwnerTableParserTests` 登进 per-gate 清单，复核 `ProcessOwnerTableCacheTests` 条目（S2） | `.trellis/spec/backend/allocation-gate-host-lumps.md` | S |
| 3 | 高 | 补 fill 协议状态机测试：进行中不可搜索、`Unavailable` 守卫、两种失败时机（S3） | `tests/WinForward.Windows.Tests/IPHelperOwnerTableParserTests.cs` | M |
| 4 | 中 | 补 UDP4 / UDP6 / TCP6 解码事实（S4） | 同上 | M |
| 5 | 中 | `AllocHGlobal` → `NativeMemory`，同步类文档（第 4 节方案 A） | `src/WinForward.Windows/IPHelperOwnerTableReader.cs` | S |
| 6 | 中 | 收紧失败读的文档措辞（S5） | `src/WinForward.Windows/ProcessOwnerTableCache.cs` | S |
| 7 | 低 | design.md 标记未实现项与改名（S6） | `.trellis/tasks/10-10-owner-table-scan-alloc/design.md` | S |
| 8 | 低 | 内联 `DecodeIPv6Value`（S8） | `src/WinForward.Windows/IPHelperOwnerTableParser.cs` | XS |
| 9 | 低 | gate 改名对齐 `*AllocatesNoManagedBytes` 族，同步 spec/PRD 引用（S9） | 测试 + 两份 spec + PRD | S |
| 10 | 低 | `cache.ReadCount` → `ScanCount` 或文档点明差异（S10） | `src/WinForward.Windows/ProcessOwnerTableCache.cs` | XS |
| 11 | 可选 | 常驻 buffer 方案 B：先测量再决定，按 per-kind / 释放 / 常驻内存三项设计 | `IPHelperOwnerTableReader` + composition | L |
| 12 | 可选 | 记录 `OwnerTable` 时序不变式与 `fillable` 的设计取舍（S11、S12）；sink 契约统一（S13） | 设计笔记 / 两处代码 | M |

> 说明：1–5 建议在下次触碰该子系统时一并完成；6–10 属清理项；11–12 是需要单独权衡的设计项，不构成对本次提交的返工要求。

---

## 附录 A：复跑命令

```bash
# 提交与工作树一致性
git diff --stat 9c6aff7 HEAD -- src tests benchmarks

# 质量门（Release）
dotnet build WinForward.slnx -c Release
dotnet test WinForward.slnx -c Release --no-build
dotnet test tests/WinForward.Windows.Tests/WinForward.Windows.Tests.csproj -c Release --no-build
dotnet test WinForward.slnx -c Release --no-build --filter "FullyQualifiedName~WinForward.Windows.Tests.IPHelperOwnerTableParserTests"
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore

# 有效行
python3 tools/effective-lines.py src/WinForward.Windows/IPHelperOwnerTableParser.cs \
  src/WinForward.Windows/IProcessOwnerTableReader.cs src/WinForward.Windows/ProcessOwnerTableCache.cs \
  src/WinForward.Windows/IPHelperOwnerTableReader.cs tests/WinForward.Windows.Tests/IPHelperOwnerTableParserTests.cs

# 原生分配点普查
rg -n "AllocHGlobal|FreeHGlobal|NativeMemory\." src tests benchmarks
```

## 附录 B：端序等价性实测

临时控制台程序（`/tmp/ipdecode`，不在仓库内）验证：wire 字节 `C0 00 02 0A` 在小端机上读作 `uint = 0x0A0200C0`；旧写法 `new IPAddress((long)0x0A0200C0)` 输出 `192.0.2.10`，新写法 `IPAddressValue.FromIPv4(bytes)` 得到 `Bits = 0xC000020A`（`ReadUInt32BigEndian`），二者表示同一地址。反射 dump 同时确认 `Marshal.AllocHGlobal(IntPtr)` → `NativeMemory.Alloc(UIntPtr)` → `Malloc` 的调用链。

## 附录 C：未复跑项

- `jb inspectcode`（10–20 分钟；作者记录为对改动项目无 issue）。
- 记录型性能数据（20 cps REL 0.53 MB/s、52×、2402 scans、REL A/B 0/1795 connect failures）按要求采信，未复测。
- 0 B gate 的“注入一次分配即失败”证明由作者记录在 `implement.md` 第 5 步；本次未重复注入（避免改动受版本控制的文件）。
