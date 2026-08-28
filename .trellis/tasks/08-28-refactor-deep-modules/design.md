# 技术设计：文件拆分 + 模块深化

术语遵循 codebase-design 词汇表：模块/接口/实现/接缝/适配器/深度。

## 0. 总原则

- 拆分 = 移动代码到新文件 + 调整可见性，**不改逻辑**。
- 每个新文件一个主类型；静态纯函数簇提升为 internal static class。
- 依赖方向不变：Cli → {Configuration, NdisApi, Runtime, Windows}；Runtime → {Configuration, Core, NdisApi, Protocols, Windows}。
- 每批结束跑 `dotnet build` + `dotnet test`，全绿才进下一批。

## 1. Runtime 拆分

### TcpProxyCoordinator.cs (1158 → ~450 + 5 新文件)

| 新文件 | 内容（原行号） | 接口形态 |
|---|---|---|
| `TcpFrameRewriter.cs` | static 纯函数簇：TryRewriteForwardLeg / TryGetWritableFrame / SwapEthernetMacs / ClassifyTcpSyn + TcpSynKind 枚举 (329–367, 623–645) | internal static class，零依赖，OS 无关可测 |
| `TcpSequenceObservation.cs` | static 纯函数簇：RecordClientSyn / RecordServerSynAck / TryReadTcpSequenceAdvance / TrackClient*ServerSequence (374–438) | internal static class |
| `ClientResetInjector.cs` | TryInjectClientResetAsync ×2 + HandleRelaySetupFailureAsync (777–824) | 依赖 ITcpRedirectInjector + ILogger |
| `TcpRedirectAcceptor.cs` | RunAcceptLoopAsync / DrainRedundantConnectionsAsync / BoundedRetryDelayAsync / ObserveRelayCompletionAsync / TryAttachRelay (826–953, 1001–1010) | accept/relay 循环簇 |
| `TcpRedirectSession.cs` | 嵌套类提升 (1137–1157)；`RedirectSetup`/`RetiredSession` 一并评估是否随迁或留原处 | 数据类型 |

删除：`ReinjectExistingSynAsync`（315–316，一行 pass-through 别名，内联到调用点）。

### UdpProxyCoordinator.cs (570 → ~300 + 2 新文件)

| 新文件 | 内容 | 说明 |
|---|---|---|
| `UdpProxySession.cs` | 第二顶层类整体迁出 (379–570) | 12 参构造器保持不变（改 wiring 超出本任务范围） |
| `UdpResponseReinjector.cs`（修改） | `IUdpResponseSink` 接口 (9–12) 移入此文件，与唯一实现同居 | 接缝归位 |

深化：三个近似重复的 ownsSession 移除块（RemoveExpiredAsync 241–270 / RemoveFailedSessionAsync 275–300 / RemoveReceiveFailedSessionAsync 321–354）合并为私有 `TryRemoveSession(flow, expectedTask)`，隐藏 `Dictionary<FlowKey, Task<UdpProxySession>>` 并发语义（内嵌于 coordinator，不单独成文件）。

### Socks5Client.cs (504 → 2 文件，文件名与主类型对齐)

| 新文件 | 内容（原行号） |
|---|---|
| `Socks5ControlConnection.cs` | Socks5ControlConnection + ConnectAttempt (9–350) |
| `Socks5UdpTransport.cs` | IUdpProxyTransport / IUdpProxyTransportFactory / Socks5UdpTransportFactory / Socks5UdpTransport (352–504) |

注：`Socks5ReplyReader` 流式读取合并到 Protocols 的方案仅评估，若牵动过大则不做（协议编解码归并是独立改进点）。

## 2. NdisApi / Cli / Configuration 拆分

### NdisApiDriver.cs (493 → 5 新文件 + 薄驱动)

| 新文件 | 内容（原行号） |
|---|---|
| `NdisAdapter.cs` | record (L7) |
| `NdisNativeCallStatus.cs` | 错误解释 (297–338) |
| `NdisNativeCallGate.cs` | GateLease 串行化门 (340–391) |
| `NdisPacketBuffer.cs` | 原生缓冲包装 (393–493) |
| `NdisApiDriver.cs`（保留） | Open/Version?/Dispose + 委托调用 |

删除死公共面（子代理已确认零调用点，删除前复验）：`Version`、`TryReadPacket`、`SendPacketsToMstcp/Adapter[]` 批量族。
不做：拆 `NdisPacketTransceiver` / `NdisFilterManager`（会把单一 13 成员接口拆成两个浅接口，收益低；死面删除后接口自然收窄）。

### Cli/Program.cs (448 → 4 文件)

| 新文件 | 内容（原行号） |
|---|---|
| `Program.cs`（保留） | Main + 命令分发 + usage (10–114 主体) |
| `AdaptersCommand.cs` | ListAdapters + EnumerateAdapters + GetAdapterMac (89–112, 278–299) |
| `CaptureHost.cs` | RunCaptureAsync / RunInterceptionAsync / RunCaptureLoopAsync / CreateCaptureCompositionAsync / CreateUdpCoordinator (114–342) |
| `CoordinatorShutdownCaptureLoop.cs` | 嵌套类提升 (386–447) |

Validate/FindOption/PrintDiagnostics 留在 Program.cs 或随命令文件走，以 400 行为准微调。

### ConfigurationModels.cs (436 → 4 文件)

| 新文件 | 内容（原行号） |
|---|---|
| `ConfigurationJson.cs` | WinForwardConfigDto / Socks5ServerDto / RuleDto / ConfigurationJsonContext (8–56) |
| `ConfigurationContract.cs` | Socks5Server / ConfigDiagnostic / RuntimeLogLevel / ValidatedConfiguration (58–91) |
| `PolicyRuleParser.cs` | ParseRule / ParseAction / ValidateNonEmpty / NormalizeSet / ParseSet / ParseNetworks / ParsePorts / ParseProtocol / ParseFamily (271–433) — internal static |
| `ConfigurationModels.cs`（保留改名语义） | ConfigurationLoader + TryParse/TryValidate + 标量解析 + 容量常量 (93–270) |

## 3. 测试拆分（目标：类-每-文件，全部 ≤400）

### 第一步：TestHelpers/ 提取（约 -1000 行去重）

| 新文件 | 提取内容 | 来源 |
|---|---|---|
| `TestHelpers/ChecksumMath.cs` | Sum/Finish/SetIpv4HeaderChecksum/SetIpv4TcpChecksum/SetIpv6TcpChecksum | TcpProxyCoordinator / TcpEndpointRewrite / UdpRelay（3 处重复） |
| `TestHelpers/FrameBuilders.cs` | BuildIpv4/6TcpSyn、BuildIpv4/6TcpFrame、BuildIpv4UdpFrame、HopByHop 变体、MakeSynPacket 等 | 4 个文件重复 |
| `TestHelpers/UdpTransportFakes.cs` | FakeTransportFactory / FakeTransport / FakeResponseSink / ImmediateFault 变体 | UdpProxyCoordinator / UdpRelay |
| `TestHelpers/PacketReinjectorFakes.cs` | FakeReinjector / Counting 变体 | UdpRelay / CapturePipeline |
| `TestHelpers/AdapterFakes.cs` | FakeLocalAddressProvider / FakeAttributor | TcpProxyCoordinator / CapturePipeline |
| `TestHelpers/TrackingSocket.cs` | TrackingSocket | FlowAndConfiguration / Socks5ControlConnection |
| `TestHelpers/AsyncTestExtensions.cs` | WaitForAsync / IgnoreExpectedCancellationAsync | 2 处 |
| `TestHelpers/RecordingLogger.cs` | RecordingRuntimeLogger / RecordingLogger 合一 | 2 处 |
| `TestHelpers/TcpCoordinatorFakes.cs` | 17 个嵌套 fake + DispatcherHarness | TcpProxyCoordinatorTests |
| `TestHelpers/Socks5TestServer.cs` | 本地 SOCKS5 server helper 簇 | Socks5ControlConnectionTests |

提取时 fake 从 private nested 提升为 internal class，需要的地方加 `using static` 或显式引用；保持 xunit 断言不动。

### 第二步：按主题拆类

| 现文件 | 拆为（估算行数） |
|---|---|
| TcpProxyCoordinatorTests (1812) | Lifecycle(≈350) / Capacity(≈330) / Rewrite(≈340) / Concurrency(≈300) + fakes 已入 TestHelpers |
| FlowAndConfigurationTests (1275) | EndpointAndPolicy(≈250) / CoreFlowStructures(≈160) / Socks5Protocol(≈260) / UdpPacketParsing(≈260) / ConfigurationValidation(≈300) / ConfigurationLimits(≈140) |
| CapturePipelineTests (789) | PacketParsing(≈250) / AdapterScopeAndFlowTable(≈170) / FlowDispatcherExecutor(≈380) + CapturePipelineFakes |
| UdpProxyCoordinatorTests (634) | 主文件(≈250) / Lifecycle(≈200) + fakes 共享 |
| Socks5ControlConnectionTests (564) | Timeout(≈270) / UdpAssociate(≈270) + server helper 共享 |
| UdpRelayTests (508) | UdpFrameBuilder(≈160) / UdpResponseReinjector(≈260) |
| TcpEndpointRewriteTests (467) | 保留主体(≈240)，helper 外移后自然达标 |

## 4. 执行批次（风险从低到高）

1. **Batch T**：测试侧全量（TestHelpers 提取 → 7 文件拆分）。纯移动，建立保护网；跑全量测试对比基线数量。
2. **Batch R**：Runtime 3 文件拆分 + 移除簇合并 + pass-through 删除。
3. **Batch N**：NdisApi / Cli / Configuration 拆分 + 死公共面删除。

每批 = 独立可验证交付物，批间允许提交（commit 边界）。

## 5. 验证

```bash
dotnet build WinForward.slnx -warnaserror
dotnet test
rg --files -g '*.cs' src tests | grep -v obj | xargs wc -l | sort -rn   # top ≤ 400
```

基线：先在重构前记录测试通过数（写入任务 notes），收尾时对比不减。

## 6. 已识别但不做的事（防 scope 蔓延）

- UdpProxySession 12 参构造器重构、Socks5 消息编解码归并、RuntimeLogLevel 迁移、泛型关联表、NdisPacketReinjector 删除（它是测试接缝）、NdisApi 常量跨接缝泄漏治理 —— 全部留待后续任务，理由见 prd.md Non-Goals。
