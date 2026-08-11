# 全项目代码审查设计

## 1. 审查目标与边界

本任务不是一次重构或泛化的静态检查；它以可证伪的行为契约为单位，审查全部生产源码（41 个 `src/**/*.cs` 文件），并对每个模块给出已审查的证据。审查确认的功能或协议缺陷直接修复；每个可在宿主无关环境复现的修复必须配套绿色回归测试。没有发现缺陷的模块同样必须出现在覆盖矩阵中。

父任务只拥有跨模块编排、证据汇总、覆盖缺口总表和最终集成/Windows 验证记录；生产源码的审查和修复由下列子任务独占，以避免多个执行者同时改写同一状态机或测试文件。

```text
08-10-code-audit-review                         # 父：汇总与跨模块验收
├── audit-core-config                            # Core + Configuration
├── audit-protocols                              # Protocols
├── audit-ndisapi                                # NDISAPI ABI / capture
├── audit-windows                                # Windows / IP Helper
├── audit-runtime-capture-flow                   # capture、dispatcher、生命周期
├── audit-runtime-tcp                            # TCP transparent redirect
├── audit-runtime-udp-socks                      # SOCKS control + UDP relay
└── audit-cli-integration                        # CLI 组合与发布/硬件门禁
```

子任务树只是审查所有权和可验证交付物的边界，不表达执行依赖；下面的依赖关系必须在各子任务的计划中显式遵守。

## 2. 源码所有权与审查契约

| 子任务 | 独占生产源码 | 审查的主要契约 |
| --- | --- | --- |
| `audit-core-config` | `src/WinForward.Core/*.cs`; `src/WinForward.Configuration/ConfigurationModels.cs` | 有序规则选择、端点/流键规范化、CIDR/进程匹配、配置拒绝和诊断不泄露凭据 |
| `audit-protocols` | `src/WinForward.Protocols/*.cs` | 全部 Span 读取先界限校验；拒绝不改写；IPv4/IPv6/TCP/UDP 解析与校验和；RFC 1928/1929 与 SOCKS UDP 帧 |
| `audit-ndisapi` | `src/WinForward.NdisApi/*.cs` | x64 ABI/布局、native error 传播、驱动句柄所有权、枚举句柄与捕获缓冲区句柄的区分 |
| `audit-windows` | `src/WinForward.Windows/*.cs` | GUID 优先的适配器关联、IPv4/IPv6 IP Helper 解码、进程归因失败/重用时不猜测 |
| `audit-runtime-capture-flow` | `CaptureAdapterScopeResolver`, `CaptureLifecycle`, `CapturePacketProcessor`, `FlowDispatcher`, `IdleExpirySweeper`, `MultiAdapterCaptureLoop`, `NdisAdapterModeController`, `NdisPacketActionExecutor`, `NdisPacketReinjector`, `PacketFlowClassifier`, `SelfTrafficRegistry` | 每个捕获 lease 恰好一次终结；pass/block/proxy 方向正确；生命周期停机先停止并等待处理再还原；自流量优先于规则 |
| `audit-runtime-tcp` | `TcpProxyCoordinator`, `TcpProxyRelay`, `TcpRedirectInjector`, `TcpRedirectInterfaces`, `TcpRedirectListener`, `TcpRedirectTable` | SYN 重传只建立一个 relay；正反向 alias 不跨流；透明重写不碰 TCP 序列空间；失败关闭并完整回收 |
| `audit-runtime-udp-socks` | `Socks5Client`, `UdpAssociations`, `UdpProxyCoordinator`, `UdpResponseReinjector` | SOCKS 控制连接在 SYN 前登记；动态 relay alias 确定且可过期；UDP 返回按 origin/adapter 回注；共享初始化与取消不误释放 |
| `audit-cli-integration` | `src/WinForward.Cli/Program.cs` | 参数/exit code、启动/停止顺序、日志脱敏、配置验证先于 driver、发布和硬件组合门禁 |

测试文件不是子任务的生产所有权。每个子任务优先新增独立、以模块命名的测试文件；若必须改现有测试，变更必须保持最小并在其报告中列出，避免并行写冲突。

## 3. 审查数据流与证据链

```text
完整源码清单 + 当前测试 + 既有修复/硬件证据
  -> 子任务按文件逐项审读、追踪调用者/被调用者和 wire format
  -> 已确认问题：最小复现测试 -> 修复 -> focused test + 全套测试
  -> 子任务报告：severity/type/file:line/证据/复现/修复/测试/未覆盖项
  -> 父任务：模块覆盖矩阵 + 去重/跨边界复核 + 最终 build/test/Windows 记录
```

每个问题报告都采用稳定标识（例如 `PROTO-001`）。报告需引用其回归测试的完全限定测试名；测试以该标识或报告文件名反向链接。一个表面上跨越多个模块的问题由最接近缺陷根因的生产源码所有者修复，调用方子任务只在报告中交叉引用，防止重复修复。

审查必须将“确认的 bug”与“测试覆盖缺口”分开：覆盖缺口不应被表述为已存在的 defect；不能在 Linux 证明的 Windows/NDIS 行为必须列为硬件验证项，而不是凭静态推断提升严重级别。

## 4. 执行顺序与依赖

1. 先固定 Linux 基线，并记录当前生产/测试清单。
2. 可并行完成只读审读的 `audit-core-config`、`audit-protocols`、`audit-ndisapi`、`audit-windows`；任何代码修改按本表中的独占范围执行。
3. `audit-runtime-capture-flow` 在读取上述四项的已知契约和报告后，审查 packet ownership、分类和 capture shutdown。
4. `audit-runtime-tcp`、`audit-runtime-udp-socks` 在协议层及 dispatcher 契约明确后进行；两者不应并行修改共享 dispatcher/公共测试文件。
5. `audit-cli-integration` 最后验证组装顺序、最终配置和 Windows-only 门禁。
6. 父任务整合全部报告，检查每个生产文件恰有一行覆盖记录、每个 bug 都有测试/状态、每个 coverage gap 都有后续验证路径。

## 5. 不变量、兼容性与风险控制

- **Fail closed：** 已选择的 `proxy` 绝不静默降级为 `pass`；未知或不确定的 owner 不能被虚构。
- **包改写：** IP/端口/校验和是唯一可变字段；TCP 不适用 UDP 的零校验和反转规则；每个 reject path 必须不修改输入。
- **流/会话：** 以协议、地址族、原始端点和 origin context 区分。PID、DNS ID 或仅端口均不得作为 UDP 路由键。一次性资源的建立、重复包、失败和过期必须有可观测的所有权结局。
- **NDIS 方向：** `SendToMstcp`/`SendToAdapter` 与 `ON_RECEIVE`/`ON_SEND` 必须一起按 host/forwarded origin 检验；捕获缓冲区中观察到的 adapter pointer 不能作为 request handle。
- **兼容性：** 审查/修复保持既有 JSON 配置和 CLI contract；若发现需要行为或配置契约改变，退回规划并征求用户决定，而不是把它作为 bug fix 合入。
- **Windows 风险：** Linux 单测覆盖纯逻辑；真机只验证 NDISAPI、驱动、适配器、Hyper-V 和 Native AOT。对 Windows 主机的每个命令、输入配置、观测结果和回滚（Ctrl+C 后 adapter mode）都要记录。不得使用 catch-all proxy 配置切断 WinRM 管理连接。

## 6. 交付物与回滚形状

每个子任务在自身 `research/` 生成 `audit-findings-<slug>.md`，至少包含：拥有文件表、审读结果、发现表、测试映射、覆盖缺口、局限/Windows gate、focused 与全套测试结果。父任务生成：

- `research/module-coverage-matrix.md` — 41 个生产文件到子任务/报告/结果的唯一映射；
- `research/audit-summary.md` — 去重后的 finding 清单、基线与结束状态；
- `research/coverage-gaps.md` — 模块 × 未覆盖公开行为或关键分支；
- `research/windows-validation.md` — 已执行或待执行的 host/Hyper-V/NDIS 验证。

每个源码修复保持小而独立：先新增可稳定重现的测试，再做最小修复；若 focused 或全套测试失败，回滚该修复及其测试文件变更，保留审查证据并将问题标为未确认/待进一步验证。父任务不以未验证的静态猜测改变行为。
