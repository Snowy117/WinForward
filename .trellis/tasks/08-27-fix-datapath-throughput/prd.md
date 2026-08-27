# 数据面吞吐优化：批量读包与 buffer 池化

## Goal

消除数据面的单包串行读取与每包分配拷贝，使处理循环的吞吐能力显著高于常规负载的
包速率，防止 WinpkFilter 驱动队列溢出引发的静默丢包 → MSTCP RTO → 重传耗尽 → RST /
应用读超时 EOF。这是 reset 频率异常增高的第一主因。

## 确认事实（代码审计）

- `NdisApiAbi.cs` 仅有单包 `ReadPacket` P/Invoke；上游 ndisapi.dll v3.6.2
  （UpstreamCommit 417b8734）标准导出含 `ReadPackets`/`SendPacketsToMstcp`/
  `SendPacketsToAdapter` 批量形式，实现前需在 Windows 上验证导出表。
- `NdisNativeCallGate`（NdisApiDriver.cs:193-244）以全局 Monitor 串行化全部
  native 调用，现状每包至少进入 2 次（读）+ 1 次（注入）。
- `IntermediateBuffer` ABI 尺寸 1566 字节（含 1514 帧上限）。
- `PacketLease`（WinForward.Core/PacketRuntime.cs）持有 `ReadOnlyMemory<byte>`，
  `_completed` 一次性完成语义可安全承载池化归还钩子。
- `benchmarks/` 为自写 console 基准（NativeBuffer/FlowTable/SelfTraffic/UdpSession/
  Dispatcher/Relay），无 capture 泵级端到端 pps 基准。
- 开发环境为 Linux：构建与单测可跨平台执行，涉及驱动/native 的验证须在
  Windows smoke 环境完成。

## 成因分析

现状数据路径（每个包的完整开销链）：

1. **单包读取**：`NdisCapturePump`（`src/WinForward.NdisApi/NdisCapture.cs:35-52`）
   调用 `NdisApiDriver.TryReadPacket` → `NdisApiNative.ReadPacket`
   （`src/WinForward.NdisApi/NdisApiDriver.cs:100-121`），一次内核往返只取一个包。
   WinpkFilter 官方推荐批量 `ReadPackets`（一次最多数十上百个包），单包模式的内核
   转换开销使吞吐上限远低于线速。
2. **逐包串行 await**：取包后 `_handler(packet, ct)` 逐包 await 完整的分类 → 分发 →
   改写 → 注入链，没有批次处理或流水线。
3. **每包多次分配与拷贝**：
   - `CapturePacketProcessor.ProcessAsync`（`CapturePacketProcessor.cs:34`）
     `GetFrame().ToArray()` 第一次托管拷贝；
   - `TcpProxyCoordinator.CompleteNewRedirectAsync` / `ReinjectExistingFlowDataAsync`
     （`TcpProxyCoordinator.cs:214,346`）`Frame.ToArray()` 第二次拷贝；
   - `TcpRedirectInjector.InjectAsync`（`TcpRedirectInjector.cs:11`）与
     `NdisPacketActionExecutor.PassAsync`（`NdisPacketActionExecutor.cs:39`）每次注入
     `new NdisPacketBuffer()` → `NativeMemory.AllocZeroed(sizeof(IntermediateBuffer))`
     （约 1.6KB 非托管内存，非池化，`NdisApiDriver.cs:246-254`）+ `SetFrame` 第三次
     拷贝；
   - `CapturedFlowPacket` 为 record class，分发路径多次 `packet with {...}` 分配。
4. **全量流量过用户态**：适配器处于 `SentTunnel|ReceiveTunnel`
   （`NdisAdapterModeController.cs:41`），pass 流量同样走上述路径，开销与代理流量
   相当。
5. **轮询延迟**：`TryReadPacket` 失败后 `Task.Delay(1ms)`
   （`NdisCapture.cs:42`），未使用 WinpkFilter 的事件通知机制（如
   `SetPacketEvent`），空-非空切换时给第一个包引入 0~1ms+ 的额外延迟与 CPU 消耗。

高 pps 下上述每包成本叠加 GC 暂停，处理速率低于到达速率，WinpkFilter 内部队列
（容量有限）溢出并静默丢包。TCP 流表现为反复重传、拥塞窗口塌缩，最终重传计数
耗尽触发 RST；接收侧表现为应用读超时或 EOF。

## 解决方案方向（Requirements）

- 将 `NdisCapturePump` 改为批量读取：按 WinpkFilter ABI 使用包数组形式的
  `ReadPackets`（或经 `NdisApiDriver` 封装的等价批量接口），一次内核往返取回一批
  包，逐包处理但分摊读取开销。批量接口缺失时先在 `NdisApiAbi`/`NdisApiDriver`
  补齐 P/Invoke 封装。
- 引入非托管 buffer 池：`NdisPacketBuffer` 或其底层 `IntermediateBuffer` 采用池化
  分配（`ObjectPool`/自定义 free-list），注入路径（`TcpRedirectInjector`、
  `NdisPacketActionExecutor.PassAsync`）不再每包 `AllocZeroed/Free`。
- 削减托管拷贝：读取后的帧数据尽量以池化 `byte[]`/`Memory<byte>` 传递，rewrite 路径
  复用同一缓冲就地改写（`TryRewriteTcpEndpoints` 已是就地语义），消除两次 `ToArray`
  中至少一次。
- 降低轮询开销：评估改用事件通知（`SetPacketEvent`）或保留轮询但延迟自适应退避，
  确保空转 CPU 与首包延迟二者都可接受。此项如与 spec 冲突需先更新
  `.trellis/spec/backend/windows-ndisapi.md`。
- 保持包处理顺序语义：同适配器内 reinject 顺序与到达顺序一致（现串行循环天然
  保序，改造后不得引入乱序）。
- 不改变对外配置语义与 fail-closed 原则。

## Out of Scope

- 批量写（批末 flush 注入）与 `SetPacketEvent` 事件通知：design.md §7 已记录
  权衡，推迟到阶段 2 独立评估。
- `CapturedFlowPacket` record class 的 struct 化：接口传播面广，阶段 2 决定。
- 端口预算、表生命周期、次要竞态：分别由兄弟任务 `fix-port-budget`、
  `fix-table-lifecycle`、`fix-minor-races` 承载。

## Acceptance Criteria

- [ ] `NdisCapturePump` 使用批量读取接口，单次循环至少可取回多个包。
- [ ] 注入与 reinject 路径不再出现每包 `NativeMemory.AllocZeroed`（池化或复用）。
- [ ] 每包托管分配次数较修复前显著下降（以基准或计数器证明，目标：热路径零新增
      `ToArray`，具体数值在 design.md 定稿）。
- [ ] `benchmarks/` 下吞吐基准（现有或补充）显示单适配器处理 pps 上限提升，且高
      负载下不出现持续重传。
- [ ] `dotnet test -c Release` 全量通过；trace 日志语义不回退（packet.captured/
      completed 配对完整）。

## Notes

- 涉及 `WinForward.NdisApi` ABI 层新增封装时，须遵循
  `.trellis/spec/backend/windows-ndisapi.md` 既有约定（如枚举句柄传递规则）。
- 复杂任务：`task.py start` 前需补 `design.md`（池化与批量读取的边界、线程模型）
  与 `implement.md`（分步清单与验证命令）。
