# Directory Structure

> How backend code is organized in this project.

---

## Overview

WinForward 是 .NET 10 解决方案（`WinForward.slnx`），按层分项目，依赖方向固定：

```
Cli → { Core, Configuration, Protocols, NdisApi, Runtime, Windows }
Runtime → { Configuration, Core, NdisApi, Protocols, Windows }
```

依赖方向不得逆转；发现需要跨层移动类型时先确认方向再动手。

---

## Directory Layout

```
src/
├── WinForward.Cli/            # 入口 + composition root（Program.cs）
├── WinForward.Configuration/  # JSON DTO + ConfigurationLoader + ValidatedConfiguration
├── WinForward.Core/           # 零依赖基元（Endpoint、IPAddressValue、FlowKey、IPPrefix…）
├── WinForward.NdisApi/        # NDISAPI interop（Abi 声明 / Driver / Gate / Buffer）
├── WinForward.Protocols/      # 纯协议编解码（Socks5Messages、Socks5UdpCodec）
├── WinForward.Runtime/        # 捕获/调度运行时；内部分 4 个子命名空间（见下节）
└── WinForward.Windows/        # Windows 专属（AdapterIdentity 等）
tests/
└── WinForward.Core.Tests/     # xunit；类-每-文件
    └── TestHelpers/           # 跨测试文件共享的 fakes/helpers
benchmarks/                    # throwaway 基准宿主（不适用文件行数约定）
```

### Runtime 子命名空间（2026-08-29 确立）

`WinForward.Runtime` 根只放调度核心与日志：`FlowDispatcher`（含 `CapturedFlowPacket`、`PacketCaptureMetadata`、`NativeFrameHandle`、`IPacketActionExecutor`、`ISelfTrafficGuard`）、`PacketFlowClassifier`、`IdleExpirySweeper`、`SelfTrafficRegistry`（含嵌套 `SelfTrafficKey`/`SelfTrafficToken`）、`RuntimeLogging`（共 15 个类型）。其余按域分子目录，目录名 = 命名空间后缀：

| 命名空间 | 内容 |
|---|---|
| `WinForward.Runtime.Capture` | NDIS 抓包：生命周期、适配器模式控制、包处理、再注入（`IPacketReinjector` 在此） |
| `WinForward.Runtime.TcpRedirect` | TCP 全链路：redirect 表/会话/监听/注入、relay、frame 改写、`ClientResetInjector`（按类型依赖归 TCP，勿移回根） |
| `WinForward.Runtime.UdpProxy` | UDP 会话与响应再注入 |
| `WinForward.Runtime.Socks5` | TCP/UDP 共用的 SOCKS5 拨号与 UDP 传输（编解码仍在 `WinForward.Protocols`） |

- 新文件按域归组；根命名空间只进"所有组都引用的调度词汇"。
- 跨组引用直接 `using`，允许的既有边：根→TcpRedirect（`TcpRedirectOutcome`）、根/Capture→两个 Coordinator、TcpRedirect/UdpProxy→Capture 的 `IPacketReinjector`、TcpRedirect→Socks5。出现新的组间循环时先考虑挪类型再考虑加 using。

---

## Module Organization

### 文件行数上限（2026-08-28 重构确立）

- 每个 .cs 文件**有效行数 ≤ 400**：有效行 = 非空、非注释行（`wc -l` 总行数仅作参考，不作为超标依据）。
- 行数超标时的拆分顺序：先找自然接缝（static 纯函数簇、嵌套类提升、`// ----` 分区注释、第二顶层类型），再考虑新模块。
- **不为拆而拆**：拆分不得严重损害可读性或性能。先例：`Cli/Program.cs`（397 有效行）与 `ConfigurationModels.cs`（367）达标后保持内聚不拆；`TcpRedirectLogging` 因被 4 个文件 15 处调用而保留独立文件，即使只有 25 有效行。

### 文件与类型的关系

- **文件名 = 主类型名**，一个文件一个主类型。
- 允许的同文件簇（紧密关联才同居）：接口 + 唯一实现（如 `UdpResponseReinjector.cs` 内的 `IUdpResponseSink`）；同一 ABI 目录的互操作声明；record + 直接操作它的静态类。
- 同文件多类型是例外不是常态；出现第二个"不相关"顶层类型时就是拆分信号（先例：`Socks5Client.cs` 曾装 5 个类型 → 拆为 `Socks5ControlConnection.cs` + `Socks5UdpTransport.cs`）。
- 接缝归位：接口放在其**实现**旁边，不放在使用方文件里。

### 拆分纪律（行为零变更重构）

- 拆分 = 物理搬移 + 调整可见性，逻辑不动；lock body 逐字迁移，保持单锁语义（先例：`TcpRedirectSessionStore` 吸收原 coordinator 全部 `_gate` 锁状态）。
- 死公共面删除前必须 rg 全仓（含 tests/、benchmarks/）复验零调用点；ABI P/Invoke 声明保留完整目录，即使托管包装层无人调用（先例：批量 `SendPacketsTo*` 删除、`NdisApiNative` P/Invoke 保留）。
- pass-through 别名（一行转调）不设独立方法，内联到调用点（先例：`ReinjectExistingSynAsync`）。

---

## Naming Conventions

- 产品代码文件 = 主类型名 PascalCase；xunit 测试文件 = 测试类名 + `Tests` 后缀，按主题命名（`TcpProxyCoordinatorLifecycleTests`、`ConfigurationValidationTests`）。
- 测试 fake/helper 组织：
  - 仅单文件使用 → 留在该文件内（private nested 或文件私有均可）。
  - **≥ 2 个文件重复 → 提取到 `tests/WinForward.Core.Tests/TestHelpers/`**，按类别分组文件（`ChecksumMath`、`FrameBuilders`、`UdpTransportFakes`、`PacketReinjectorFakes`、`TcpCoordinatorFakes`、`Socks5TestServer`…）。
  - TestHelpers 使用测试项目根 namespace（`WinForward.Core.Tests`，不加 `.TestHelpers` 后缀）；fake 从 private nested 提升为 internal（`InternalsVisibleTo` 已配置）。
  - 合并重复 fake 时取行为超集（先例：`FakeReinjector` 同时记录 `DeviceFlags` 与 `Flags` 两个 flag 平面；`TrackingSocket` 支持可选 SocketType/ProtocolType）。

---

## Examples

- 深模块拆分范例：`TcpProxyCoordinator.cs`（原 1158 行）→ coordinator（入口路由）+ `TcpRedirectSessionStore`（单锁并发核心）+ `TcpRedirectSetup` + `TcpRedirectAcceptor` + `ClientResetInjector` + `TcpFrameRewriter`/`TcpSequenceObservation`（static 纯簇，OS 无关可测）。
- 接缝归位范例：`IUdpResponseSink` 从 `UdpProxyCoordinator.cs` 移到唯一实现所在的 `UdpResponseReinjector.cs`。
- 重复消除范例：校验和数学（`Sum`/`Finish`/`Set*Checksum`）与帧构造器统一进 `TestHelpers/ChecksumMath.cs` / `FrameBuilders.cs`，调用点留 1 行 wrapper 固定默认参数。
