# Implement: 数据面吞吐优化（批量读包与 buffer 池化）

关联: `prd.md`（需求与验收）、`design.md`（方案取舍）。前置：本文档与 design.md
经用户 review 后执行 `task.py start`。

## 实现顺序（每步一个提交点，独立可回滚）

### Step 0: ABI 导出验证 `[阻塞后续所有步骤]`

- [x] Windows 上解析 `ndisapi.dll`（pinned v3.6.2，见 `NdisApiAbi.UpstreamCommit`）
      导出表，确认 `ReadPackets` / `SendPacketsToMstcp` / `SendPacketsToAdapter`
      导出名与 stdcall 约定。
- [x] 将导出表证据（命令与输出摘要）追加到本文件末尾「验证记录」。

验证: `dumpbin /exports ndisapi.dll | findstr /i packets`（或 PowerShell PE 解析）。

### Step 1: ABI + Driver 批量读封装 `[已完成 2026-08-27]`

- [x] `NdisApiAbi.cs`：新增 `EthernetMultiRequest`（24 字节固定头 + `FirstBuffer`
      别名 `EthPacket[0]`）、layout 断言（Size=24，偏移 0/8/12/16）、
      `ReadPackets`/`SendPacketsToMstcp`/`SendPacketsToAdapter` 三个 stdcall
      `LibraryImport`。
- [x] `NdisApiDriver.cs`：`TryReadPackets(nint, NdisPacketBuffer[]) -> int`（查询+
      批量读合并单次 gate 租约；请求内存 16+8N，≤1KB stackalloc、更大走非托管堆）；
      批量 SendPacketsTo* 重载（pump 未使用）；错误解释
      `InterpretBatchReadResult`（空队列→0 / 非空失败→抛 / 回填数 clamp）。
- [x] 单测：`NdisApiAbiTests` +2（部分批 clamp、非空失败抛）。

验证: `dotnet build -c Release && dotnet test -c Release --filter "FullyQualifiedName~NdisApi"`。

### Step 2: Pump 批处理 + 帧池化 `[已完成 2026-08-27，含一处安全性偏差]`

- [x] `PacketRuntime.cs`：`PacketLease` 增加 `onCompleted` 回调构造（唯一完成路径
      触发一次），旧构造保留（null 语义不变）。
- [x] `CapturePacketProcessor.cs`：`ToArray()` → `ArrayPool.Rent` + 拷贝。
- [x] `NdisCapture.cs`：pump 批处理循环（`NdisPacketBuffer[32]` 可注入，批内严格
      保序 await，空批保留 poll delay，buffers 双路径一次性释放）；新增
      `INdisPacketReader` seam 供单测。
- [x] 审计：**design §4.4 原假设被证伪**——`FlowDispatcher.CompleteAsync` 先
      `TryComplete` 再 `execute()`，executor 侧 6 处帧读取发生在 lease 完成之后
      （但全部在 `ProcessAsync` await 窗口内，无逃逸）。**归还点因此外移到
      `ProcessAsync` 外层 finally** 而非完成回调；design.md §4.4/4.5 已同步修正。
- [x] 单测：`NdisCapturePumpTests`（新文件，7 个：保序/部分批/空批/一次性释放/
      参数校验）+ `CapturePipelineTests` +4（回调一次、finally 归还边界、旧构造、
      池化拷贝字节级正确）。

验证: `dotnet test -c Release`（全量，Linux 可 build/run 的部分）。

### Step 3: 就地改写消除第二次拷贝 `[已完成 2026-08-27]`

- [x] `TcpProxyCoordinator.cs`：`CompleteNewRedirectAsync:214`、
      `ReinjectExistingFlowDataAsync:346`、`HandleReverseAsync:391` 三处
      `ToArray()` 删除，改 `packet.Lease.Frame.Span` 就地改写。
- [x] 复核 design §4.5 读-改顺序（`RecordClientSyn`/`RecordServerSynAck` 先读、
      rewrite 后写）与 `TryRewriteIpv4Tcp/Ipv6Tcp` "解析先行、写入后无失败分支"
      的不变量；若发现反例，停止并回到 design 修订。
      实现修正两点：(a) `CompleteNewRedirectAsync` 的 `RecordClientSyn` 原位于
      rewrite 之后（依赖副本语义读原始帧），就地改写下必须前移到 rewrite 之前；
      (b) `Lease.Frame` 是 `ReadOnlyMemory`，需经 `MemoryMarshal.TryGetArray` 取
      可写视图（`TryGetWritableFrame`），非数组承载 → fail-closed Blocked。
- [x] 单测：rewrite 失败路径返回 Blocked 且不注入半改写帧（现有测试回归）。
      新增 `SynRewriteParseFailureLeavesFrameByteIdentical`（ARP ethertype →
      解析失败 → 帧字节零改动 + Blocked + listener/association 释放）。

验证: `dotnet test -c Release --filter "FullyQualifiedName~TcpProxy|FullyQualifiedName~Redirect"`。

### Step 4: 注入侧 NdisPacketBuffer 池 `[已完成 2026-08-27]`

- [x] 新增共享池类型（`ConcurrentQueue` + 上限 256，超限释放；进程关闭统一
      释放，挂接运行时关闭序列）。`NdisPacketBufferPool`（WinForward.NdisApi，
      进程级 `Shared` 单例；`Program.RunCaptureLoopAsync` 外层 finally 在
      capture runtime 与 coordinator teardown 注入全部结束后 Drain）。
- [x] `TcpRedirectInjector.cs` / `NdisPacketActionExecutor.PassAsync` 改为
      `Rent()`/`Return()`；`NdisPacketBuffer.Dispose` 语义兼容（归还而非释放）。
      实现形态：buffer 携带 owner-pool 状态机（私有构造 → 释放，池租借 → 归还，
      双重 Dispose no-op），两处注入点以 `using var buffer = pool.Rent()` 复用
      既有 using 结构；构造函数追加可选 `bufferPool` 参数（默认 `Shared`，
      既有调用点零改动）。
- [x] 单测：租借-归还往返、超上限释放、并发租借（新文件
      `NdisPacketBufferPoolTests`，8 个：往返复用/显式 Return/超上限释放/
      64 并发租借唯一性/双重 Dispose no-op/Dispose drain 后仍可 Rent/外部
      buffer 拒绝/私有 buffer 语义回归）。

验证: `dotnet test -c Release`。

### Step 5: 基准与文档收尾

- [ ] `benchmarks/` 增设 capture-process 端到端基准（合成帧序列、统计 pps 与
      Gen0 GC 频率），Windows 上修复前后各跑一轮，结果记入「验证记录」。
- [ ] Windows smoke：trace 模式跑 `smoke/winforwardconfig.json`，核对
      `packet.captured`/`packet.completed` 配对、零 `packet.failed`。
- [ ] 若实现中发现 ndisapi 批量调用有新契约（如 per-packet 部分成功语义），
      更新 `.trellis/spec/backend/windows-ndisapi.md` 后再继续。

## 风险文件与回滚点

| 提交 | 触碰 | 回滚动作 |
|------|------|----------|
| Step 1 | NdisApiAbi.cs, NdisApiDriver.cs | revert（纯新增，无既有行为变化） |
| Step 2 | PacketRuntime.cs, CapturePacketProcessor.cs, NdisCapture.cs | revert（lease 旧构造保留，调用方自动回退） |
| Step 3 | TcpProxyCoordinator.cs | revert（恢复 ToArray 副本即可） |
| Step 4 | TcpRedirectInjector.cs, NdisPacketActionExecutor.cs, 新池类 | revert（池类独立无耦合） |

## start 前检查

- [x] prd.md 收敛（含 Out of Scope）
- [x] design.md 定稿（两阶段边界、否决方案记录）
- [x] implement.jsonl / check.jsonl 已含真实 spec 条目
- [ ] 用户批准最终规划摘要

## 验证记录（实现时追加）

### Step 0: ndisapi.dll 批量导出验证（2026-08-27，已完成）

本地 `smoke/prefx/ndisapi.dll` 为 0 字节占位文件，无法直接解析。改为克隆 pin 的
上游源码验证（等效证据：`.def` 导出清单 + 头文件签名 + 官方文档）：

```
git init /tmp/ndisapi && git remote add origin https://github.com/wiresock/ndisapi.git
git fetch --depth 1 origin 417b8734e844083a10236387fba705d94a2d6bc9 && git checkout FETCH_HEAD
```

- 导出清单（`ndisapi.vs2012/ndisapi.def`）：`ReadPackets`、`SendPacketsToMstcp`、
  `SendPacketsToAdapter`、`SetPacketEvent` 均在 EXPORTS 中（另有
  `ReadPacketsUnsorted` 等，未采用）。
- C 导出签名（`include/ndisapi.h:300-302`）：
  `BOOL __stdcall ReadPackets(HANDLE hOpen, PETH_M_REQUEST pPackets)`（SendPackets*
  同形）。
- `ETH_M_REQUEST`（`include/Common.h`，Pack=1，x64）：
  `hAdapterHandle(8) + dwPacketsNumber(4,in) + dwPacketsSuccess(4,out) +
  NDISRD_ETH_Packet[N]`，每项为单个 `INTERMEDIATE_BUFFER*`（8 字节），
  总大小 16 + 8N。
- 调用契约（官方 ReadPackets 文档 + CNdisApi::ReadPackets 实现）：调用方填
  hAdapterHandle / dwPacketsNumber / EthPacket[i].Buffer；驱动成功时回填
  dwPacketsSuccess（实际返回包数）与各 buffer 内容；返回 FALSE 表示调用失败。
  WOW64 转换路径仅 32 位进程需要，WinForward 为 x64 进程，无需处理。
- 结论：Step 1 可按上述 ABI 直接落地；实现时建议同步在 Windows smoke 机上对真实
  DLL 跑一次 `dumpbin /exports` 复核（非阻塞）。

### Step 1 + Step 2 验证记录（2026-08-27）

- `dotnet build -c Release`：成功，0 警告 0 错误。
- `dotnet test -c Release`：310/310 通过（含新增 13 个测试：NdisApiAbi +2、
  NdisCapturePump 新文件 7 个、CapturePipeline +4），0 跳过。
- `dotnet test -c Release --filter "FullyQualifiedName~NdisApi"`：16/16；
  `~NdisCapturePump`：7/7。
- `dotnet format --verify-no-changes`：改动文件全部干净（仓库存量 format 报错
  为 6 个未触碰文件，未顺手修改）。
- 改动统计：7 文件，+351/-16。
- 审计结论（详见 design.md §4.4 修正）：executor 侧 6 处帧读取发生在
  `TryComplete` 之后但在 `ProcessAsync` await 窗口内；归还点外移至
  `ProcessAsync` finally。

### Step 3 + Step 4 验证记录（2026-08-27）

- `dotnet build -c Release`：成功，0 警告 0 错误。
- `dotnet test -c Release`：319/319 通过（310 基线 + 新增 9 个：
  TcpProxyCoordinatorTests +1、NdisPacketBufferPoolTests 新文件 8 个），0 跳过。
- `dotnet test -c Release --filter "FullyQualifiedName~TcpProxy|FullyQualifiedName~Redirect"`：42/42。
- `dotnet format --verify-no-changes`：本次改动文件全部干净（报错的 7 个文件
  —— CaptureAdapterScopeResolver / MultiAdapterCaptureLoop /
  NdisAdapterModeController / NdisPacketReinjector / PacketFlowClassifier /
  TcpRedirectInjectorTests / CaptureLifecycleTests —— 均为存量问题且不在本次
  diff 中，未顺手修改）。
- 安全性复核结论：
  - `TryRewriteIpv4Tcp/Ipv6Tcp` 全部校验先于首个写入、写入后无失败分支，
    `SwapEthernetMacs` 先长度检查——就地改写无中间态（新测试
    `SynRewriteParseFailureLeavesFrameByteIdentical` 锁定）。
  - `RecordClientSyn` 前移到 rewrite 之前；`RecordServerSynAck` 本就在前。
    RST 模板仍取原始帧字节，与改前行为一致。
  - 注入后的帧不再被读取（executor 只取 `Context.Key` 缓存值做 trace 字段；
    FakeInjector 断言的帧为注入时快照拷贝），池化数组归还点仍在
    `ProcessAsync` finally，时序不变。
  - 生产路径每捕获包独占一个池化帧，不存在同帧并发/重复处理；测试 harness
    中复用同一 packet 对象的场景（并发 burst、SYN 重发）断言均不涉及帧字节，
    行为不回退。
- 已知边界（未回退、留作记录）：
  - `UdpResponseReinjector.InjectAsync` 每次 UDP 响应注入仍 `new
    NdisPacketBuffer()`——不在 design §4.6 改造点清单内（PRD C4 仅列
    TcpRedirectInjector/NdisPacketActionExecutor），属 per-response 而非
    per-captured-packet 路径；可作为后续微优化。
  - `Return` 与 `using`/`Dispose` 混用时：显式 `Return` 后再 `Dispose`（或
    反之）中第二次调用为 no-op/拒绝，不会双归还（状态机锁定 + 单测覆盖）。
