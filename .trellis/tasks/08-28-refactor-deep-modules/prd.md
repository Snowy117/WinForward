# Refactor oversized files and deepen module design

## Goal

按 codebase-design 原则（深模块、小接口、接缝归位）重构 WinForward 代码库：
拆分所有超过 400 有效行数的 .cs 文件，同时收紧明显过宽或错位的模块接口。
**行为零变更** —— 纯结构重构，由现有测试保护网验证。

## Scope

13 个超标文件：

- src（6）: `TcpProxyCoordinator.cs`(1158), `UdpProxyCoordinator.cs`(570), `Socks5Client.cs`(504), `NdisApiDriver.cs`(493), `Cli/Program.cs`(448), `ConfigurationModels.cs`(436)
- tests（7）: `TcpProxyCoordinatorTests.cs`(1812), `FlowAndConfigurationTests.cs`(1275), `CapturePipelineTests.cs`(789), `UdpProxyCoordinatorTests.cs`(634), `Socks5ControlConnectionTests.cs`(564), `UdpRelayTests.cs`(508), `TcpEndpointRewriteTests.cs`(467)

## Requirements

1. 每个拆出的新类型放在自己的文件中，文件名 = 主类型名（xunit 测试遵循类-每-文件约定）。
2. 测试重构只允许移动 / 重命名 / 提取 fake，不允许修改断言语义或测试覆盖。
3. 跨文件重复的测试基础设施（校验和数学、帧构造器、UDP transport fakes 等 9 类，详见 design.md §5）提取到 `tests/WinForward.Core.Tests/TestHelpers/`。
4. 删除经确认无调用点的死公共面（deletion test 失败项）：`NdisApiDriver.Version`、`TryReadPacket`、批量 Send*；`TcpProxyCoordinator.ReinjectExistingSynAsync`（pass-through 别名）。
5. 错位类型归位：`IUdpResponseSink` 移到实现旁；`UdpProxySession`、`TcpRedirectSession`、`CoordinatorShutdownCaptureLoop` 等嵌套/同居类型各自成文件。
6. `ConfigurationModels.cs` 按职责拆为 DTO / 验证契约 / loader 解析器三组文件。
7. 拆分优先沿现有自然接缝（分区注释、嵌套类边界、static pure 函数簇），不引入新抽象层，除非研究报告中明确识别为深模块机会（`TcpFrameRewriter`、`TcpSequenceObservation`、`UdpSessionRegistry` 移除簇合并）。

## Non-Goals

- 不改运行时行为、网络协议逻辑、配置格式或公共 API 语义。
- 不做泛型关联表等大范围抽象合并（`TcpRedirectTable`/`UdpAssociationTable` 骨架相似但合并风险大于收益，仅观察）。
- 不移动 `RuntimeLogLevel` 跨项目（涉及依赖方向调整，另立任务）。
- 不处理 benchmarks/Program.cs(818 行)（ throwaway 基准宿主，非产品代码）。

## Acceptance Criteria

- [ ] 仓库内所有 src/ 与 tests/ 下 .cs 文件 `wc -l` ≤ 400 行。
- [ ] `dotnet build WinForward.slnx` 零警告零错误（TreatWarningsAsErrors 已开启）。
- [ ] `dotnet test` 全绿，测试数量不少于重构前基线。
- [ ] 死公共面成员已删除且编译通过。
- [ ] 错位类型已归位（文件名与主类型一致）。
