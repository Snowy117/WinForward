# Design: 数据面吞吐优化（批量读包与 buffer 池化）

关联 PRD: `prd.md`（同目录）。父任务: `08-27-fix-eof-reset-design-flaws`。

## 1. 问题重述（第一性原理）

被拦截适配器的全部流量（含 pass）都必须经过用户态往返；处理循环的每包成本决定了
有效吞吐上限。当到达速率 > 处理速率时，WinpkFilter 驱动队列溢出并静默丢包，TCP
表现为 RTO 重传直至 RST/EOF。因此本设计的唯一目标：**把每包成本压到足够低、把
每内核往返的摊销成本做厚，使常规负载下处理速率始终高于到达速率。**

不改语义：pass/block/proxy 决策路径、fail-closed 原则、trace 日志事件集全部保持。

## 2. 现状测量点（每包成本构成）

| # | 成本 | 位置 | 频次 |
|---|------|------|------|
| C1 | `GetAdapterPacketQueueSize` + `ReadPacket` 两次 native 调用 + `NdisNativeCallGate` 进入 | `NdisApiDriver.TryReadPacket` (NdisApiDriver.cs:100-122) | 每包 |
| C2 | `GetFrame().ToArray()` 托管分配+拷贝 | `CapturePacketProcessor.ProcessAsync:34` | 每包 |
| C3 | `Frame.ToArray()` 第二次拷贝 | `TcpProxyCoordinator.cs:214,346,391` | 每个 proxy 包 |
| C4 | `new NdisPacketBuffer()` → `NativeMemory.AllocZeroed(1566B)` | `TcpRedirectInjector.cs:11`、`NdisPacketActionExecutor.cs:39` | 每次注入 |
| C5 | `SetFrame` 拷贝（native struct 写入） | `NdisPacketBuffer.SetFrame` | 每次注入 |
| C6 | `CapturedFlowPacket with {...}` 分配（record class） | `FlowDispatcher.cs:88,115` 等 | 每包 1-3 次 |
| C7 | 空队列 `Task.Delay(1ms)` 轮询 | `NdisCapture.cs:42` | 每空轮询 |

## 3. 方案总览（两阶段）

**阶段 1（本次必做）**：批量读 + 全链路池化 + 就地改写。目标：消除 C1 摊销、C2/C3/C4 分配。

**阶段 2（可选，独立评估后决定）**：批量写（批末 flush 注入）与 `CapturedFlowPacket`
struct 化。目标：进一步消除 C1 注入侧摊销与 C6。不阻塞阶段 1 验收。

## 4. 阶段 1 设计

### 4.1 ABI 层：新增批量导出（WinForward.NdisApi/NdisApiAbi.cs）

上游 ndisapi.dll v3.6.2（UpstreamCommit 417b8734）标准导出，与现有
`ReadPacket` 同族，复用既有 `EthernetRequest` 结构的数组形式：

```csharp
// ETH_M_REQUEST（x64, Pack=1）: hAdapterHandle(8) + dwPacketsNumber(4,in)
//   + dwPacketsSuccess(4,out) + NDISRD_ETH_Packet[N]（每项 8 字节指针），总 16+8N
// 调用方预填 hAdapterHandle/dwPacketsNumber/EthPacket[i].Buffer；
// 驱动成功时回填 dwPacketsSuccess 与各 buffer 内容（详见 implement.md Step 0 记录）
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct EthernetMultiRequest
{
    public nint AdapterHandle;
    public uint PacketsNumber;
    public uint PacketsSuccess;
    public nint FirstBuffer; // EthPacket[ANY_SIZE] 变长数组的首元素；实际按指针数组布局
}

[LibraryImport(LibraryName, EntryPoint = "ReadPackets", SetLastError = true)]
[UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
internal static unsafe partial int ReadPackets(NdisApiSafeHandle handle,
    EthernetMultiRequest* request);
```

- 同族新增 `SendPacketsToMstcp` / `SendPacketsToAdapter`（阶段 1 一并封装但 pump
  暂不使用批量写路径）。
- ABI 断言（`AssertManagedX64Layout`）新增 `EthernetMultiRequest` 尺寸（24）与
  偏移断言；`NDISRD_ETH_Packet[N]` 通过非托管内存手工布局。
- 导出已由 Step 0 证据确认（implement.md 验证记录：ndisapi.def + ndisapi.h:300-302）。

### 4.2 Driver 层：批量读封装（NdisApiDriver）

```csharp
// 返回实际读取的包数（0 = 队列空）；buffers[i] 为预分配的 NdisPacketBuffer 池元素
public unsafe int TryReadPackets(nint adapterHandle, NdisPacketBuffer[] buffers)
```

- 复用现有错误矩阵语义（`NdisNativeCallStatus`）：队列查询失败抛 `Win32Exception`；
  队列空返回 0；队列非空但 `ReadPackets` 返回 0 且实际读取数为 0 → 抛。
- `GetAdapterPacketQueueSize` 返回的排队数与 `buffers.Length` 取 min 作为请求数。
- gate 进入次数：每批 1 次（查询+读取合并于同一次 gate 租约内）。

### 4.3 Pump 层：批处理循环（NdisCapturePump）

- 构造时预分配 `NdisPacketBuffer[] BatchBuffers`（`BatchCapacity`，默认 32）——
  这些 buffer 为 pump 私有、生命周期随 pump，天然免分配。
- 循环：`TryReadPackets` → `readCount == 0` 时保留 1ms delay 语义 → 否则按下标
  0..readCount-1 顺序逐包构造 `NdisCapturedPacket` 并 `await _handler(...)`。
  **批内顺序 await，保序语义与现状完全一致。**
- 每适配器每批一次轮询查询，C7 的轮询频率按批容量摊薄（32 倍），不再单独改造
  事件通知（`SetPacketEvent` 评估结论记录于 §7 风险，本轮不做）。

### 4.4 帧池化：PacketLease 归还钩子（WinForward.Core）

- `PacketLease` 增加完成回调构造（`onCompleted`），`TryComplete`/`Dispose` 的唯一
  完成路径触发一次；double-return 由既有 `_completed` Interlocked 一次性语义防护。
- `CapturePacketProcessor`：`frame.Span` → `ArrayPool<byte>.Shared.Rent(len)` 拷贝一次 →
  池化 lease。C2 的分配变为池租借（拷贝保留一次，因为 native `IntermediateBuffer`
  将被同批后续包复用，帧数据必须立即搬家）。
- **审计结论（实现时修正）**：原设计假设"现有代码在 TryComplete 后不读 Frame"——
  审计证伪。`FlowDispatcher.CompleteAsync` 先 `TryComplete(disposition)` 再
  `execute()`，因此 executor/coordinator 共 6 处帧读取（pass 注入拷帧、UDP payload
  解析、clientMac 切片、proxy 重写副本源等）发生在 lease 完成之后、但全部仍在
  `ProcessAsync` 的 await 窗口内（无 Frame/Memory 逃逸出该窗口；clientMac、payload、
  SYN 模板均为拷贝存储）。
- **因此归还点不挂在 lease 完成回调上**，而是外移到 `ProcessAsync` 外层 `finally`
  （整个 dispatch 链的可证明边界）：池化数组在 `finally` 归还，完成回调构造保留
  供未来"完成即归还"语义使用（契约：帧读取必须先于完成——`PacketLease` 文档与
  单测已锁定该契约）。

### 4.5 就地改写：消除第二次拷贝（TcpProxyCoordinator）

- `CompleteNewRedirectAsync` / `ReinjectExistingFlowDataAsync` / `HandleReverseAsync`
  中 `Frame.ToArray()` 副本删除，改为对 `Lease.Frame.Span` 就地 `TryRewriteTcpEndpoints`。
- 安全性论证（含 §4.4 修正后的时序）：`TryRewriteIpv4Tcp/Ipv6Tcp` 的全部解析与校验
  （版本、头长、协议、分片、长度边界）先于任何字段写入，解析失败路径不触碰帧数据；
  开始写入后无失败分支。因此就地改写要么不动帧、要么完整改写，无中间态。
- 时序安全性：就地改写发生在 `TryComplete` 之后（executor 内）但**归还发生在
  `ProcessAsync` finally**（§4.4 修正），改写落在池化数组上时数组尚未归还池，无
  撕裂风险；改写后注入直接消费改写结果，`Frame` 此后不再被读取。
- 读-改顺序审计：`RecordServerSynAck`（读原帧）→ rewrite（写）→ 注入，读先于写；
  reverse 路径 `RecordClientSyn` 同理。trace 日志字段取自 `Context.Key` 缓存值，
  非帧数据。

### 4.6 注入侧池化：NdisPacketBuffer 池

- 新增进程级共享池（`ConcurrentQueue<NdisPacketBuffer>` + 容量上限，默认 256；
  超限释放回 native 堆）。`Rent()/Return()` API，`Dispose()` 语义改为归还。
- 改造点：`TcpRedirectInjector.InjectAsync`、`NdisPacketActionExecutor.PassAsync`。
  C4 消除；C5 的单次 native 写入拷贝保留（结构使然）。
- 池元素在进程关闭时由池统一 `Dispose`（`IAsyncDisposable` 挂到运行时关闭序列）。

## 5. 数据流（改造后）

```
pump(per adapter, 1 thread)
 ├─ TryReadPackets(batch=32)                    [gate: 1次/批]
 └─ for i in 0..n-1 (顺序 await，保序):
     ProcessAsync
      ├─ ArrayPool.Rent → PacketLease(带归还)   [1次拷贝：native→pooled]
      ├─ TryParse/Classify（只读）
      └─ Dispatch
          ├─ pass  → buffer池.Rent → Send*      [gate: 1次/包]
          └─ proxy → 就地rewrite(无拷贝) → buffer池.Rent → Send*
```

阶段 1 后每包净成本：1 次池租借拷贝 + 1 次 native 写入拷贝 + 2 次 gate 进入
（读摊销后 <1 次 + 注入 1 次）+ 0 次堆分配（lease/buffer 池化）。对比现状
（2 次拷贝 + 2 次 alloc + 2+ 次 gate）的量化对比由基准给出。

## 6. 兼容性与回滚

- 全部为内部实现替换：`ITcpRedirectInjector`、`IPacketActionExecutor`、
  `PacketLease` 公共形状不变（PacketLease 构造函数新增重载，旧构造保留）。
- 配置、日志事件名、exit code 语义零变化。
- 分四个独立提交（ABI+Driver / Pump+Lease 池化 / 就地改写 / 注入池化），任一提交
  可单独 revert；阶段 2 独立分支评估。

## 7. 权衡与已否决方案

| 方案 | 结论 | 理由 |
|------|------|------|
| 批量写（批末 flush 注入） | 推迟至阶段 2 | 改变逐包注入时序，失败语义（批量 Send 的 per-packet 失败不可知）需要重新设计 fail-closed 路径；收益（gate 进入 -1 次/包）小于风险 |
| `SetPacketEvent` 事件通知 | 本轮不做 | 需要跨 native 事件与 cancellation 的等待集成，复杂度高；批量读已把轮询摊薄 32 倍 |
| `CapturedFlowPacket` struct 化 | 阶段 2 | 接口签名传播面广（executor/coordinator/dispatcher），收益中等，不阻塞主目标 |
| 每-pump thread-local buffer 池 | 否决 | 注入点（executor 单例）跨 pump 共享，thread-local 归属混乱；共享 ConcurrentQueue 上限池更简单 |
| 去掉 `NdisNativeCallGate` | 否决 | 防御 ndisapi wrapper 并发模型未知面；批量化后争用自然下降，无需求证其必要性 |

## 8. 验证策略

- 单元/集成：现有测试全绿；新增 `TryReadPackets` 错误矩阵单测（fake native 层不可
  行时以接口 seam 测试 pump 批处理逻辑：批内顺序、空批 delay、部分批）。
- 基准：`benchmarks/` 增设 capture-process 端到端 pps 基准（合成帧序列喂泵，
  统计包/秒与 Gen0 GC 频率），修复前后各跑一轮记录。
- 硬件验证：Windows smoke 环境（smoke/ 配置）跑 trace，确认
  `packet.captured`/`packet.completed` 逐包配对、无 `packet.failed`。
