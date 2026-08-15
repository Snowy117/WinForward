# Implement — 修复抓包路径网络中断与 TCP 代理失效

## 前置检查

- [x] `dotnet --version` 可用（net10.0 SDK）；`dotnet build WinForward.slnx` 基线绿；`dotnet test` 基线绿。
- [x] Phase 2 前经 trellis-before-dev 加载 spec：backend/windows-ndisapi、error-handling、logging-guidelines、quality-guidelines、directory-structure（implement.jsonl 已配）。

## 实施步骤（按序，每步可独立验证）

### S1 — R1：nonFlow 无条件 pass
- 文件：`src/WinForward.Runtime/FlowDispatcher.cs`
  - `DispatchNonFlowAsync`（:163）：self-traffic 检查后一律 PassAsync；trace `packet.action action=pass reason=nonFlow`；删除 `EvaluateNonFlow`（:186-199）。
- 测试：`tests/WinForward.Core.Tests/FlowDispatcherTests.cs`——non-flow 命中 proxy/block 规则的用例改为断言 pass。
- 验证：`dotnet test --filter FlowDispatcher` 绿。

### S2 — R3：NotRelevant → pass
- 文件：`src/WinForward.Runtime/NdisPacketActionExecutor.cs`（:99-105）：`NotRelevant` 分支调用 `PassAsync`；更新注释。
- 测试：执行器级用例（fake reinjector + fake coordinator 返回 NotRelevant → 断言回注一次、方向保持）。
- 验证：`dotnet test` 相关过滤绿。

### S3 — R2a：IAdapterLocalAddressProvider seam
- 新增：`src/WinForward.Windows/AdapterLocalAddressProvider.cs`（接口 `IAdapterLocalAddressProvider` 与 NetworkInterface 实现同层——Runtime 引用 Windows 为既有边界；v4 同子网优先、v6 排 link-local 前缀优先、两族排 loopback；无候选 null）。
- 文件：`src/WinForward.Cli/Program.cs`（:224 区域）接线。
- 验证：`dotnet build` 绿（尚无调用方，接口先行）。

### S4 — R2b：redirect 表按 origin 成形
- 文件：`src/WinForward.Runtime/TcpRedirectTable.cs`
  - `TcpRedirectAssociation` 构造器加 `IPAddress? forwardLocalAddress = null`；新增 `ForwardLocalAddress` 属性；forwarded 形状端点：`ReverseSource=(L,P)`、`ReverseDestination=AcceptedPeer=(C,C.port)`。
  - `TryClaim` 加同参数，去重 reverse 键按形状构造。
- 测试：`TcpProxyCoordinatorTests`/表级用例——两种形状的端点与去重键。
- 验证：`dotnet test --filter TcpRedirect` 绿。

### S5 — R2c：coordinator 转发分支 + MAC 门控
- 文件：`src/WinForward.Runtime/TcpProxyCoordinator.cs`
  - 构造器注入 `IAdapterLocalAddressProvider`。
  - `CompleteNewRedirectAsync`（:172）：origin==Forwarded → 解析 L（null → teardown + Blocked + warn）→ 重写 `(C,C.port)→(L,P)`，不交换 MAC；host 路径不变。
  - `ReinjectExistingFlowDataAsync`（:266）：按 `association.ForwardLocalAddress` 分支（forwarded：重写 `(C,C.port)→(L,P)` 不交换 MAC）。
  - `HandleReverseAsync`（:322）：`SwapEthernetMacs` 门控为 `towardMstcp`。
- 测试：转发 SYN 重写形状（dst=L:P、src 保持、MAC 不变）、host 回归（双交换）、L=null fail-closed、反向 MAC 门控。
- 验证：`dotnet test` 全绿。

### S6 — R4：relay 失败 RST
- 新增：`src/WinForward.Protocols/TcpResetBuilder.cs`（以原始 SYN 帧为模板构造 RST|ACK；截断 14+20+20、offset=5、totlen=40、重算校验和——复用 `PacketChecksums` 工具）。
- 文件：`src/WinForward.Runtime/TcpProxyCoordinator.cs`——SYN 记录 `ClientInitialSeq`+帧副本；`HandleReverseAsync` 记录 SYN+ACK seq 为 `ServerInitialSeq`；`RunAcceptLoopAsync` catch（:594）先注入 RST（host→MSTCP / forwarded→原网卡）再 teardown；序号缺失退化为现状。
- 文件：`src/WinForward.Runtime/TcpRedirectTable.cs`——association 序号字段。
- 测试：RST 帧形状（端点/MAC/flags/序号/校验和）、失败路径注入一次、序号缺失不注入。
- 验证：`dotnet test` 全绿。

### S7 — 全量验证 + 构建发布产物
- `dotnet build WinForward.slnx` 与 `dotnet test` 全绿。
- `dotnet publish src/WinForward.Cli -c Release -r win-x64` 产出网关部署包（dist/ 或artifacts目录，按现有发布习惯）。

### S8 — R6：网关实机 smoke（用户在 Windows 网关执行；客户端侧观测经 WinRM→192.168.100.2 与本机 192.168.77.6）
1. 部署新构建 + 现有 smoke/winforward.config.json，trace 日志启动。
2. **防火墙检查点**：VM `curl https://1.1.1.1`；若 SYN 到 listener 被吞（redirect.created 有、relay.started 无、MSTCP 无 SYN-ACK）→ 配置防火墙放行规则后重测（文档化，不改代码）。
3. AC1：VM/本机 ARP+ping 网关正常；日志无 nonFlow drop；curl 报文出现在捕获日志。
4. AC2：VM+本机 curl 成功；`tcp.relay.started`>0；singbox.log 见 1.1.1.1:443 TCP inbound。
5. AC3：WinForward 启动前建立的长连接（如先起持续 curl/ping TCP 流）在启动后继续。
6. AC4：停 sing-box → 新 connect 立即 RST 失败；恢复后正常。
7. AC6：注入帧无重复捕获回环（报文计数无爆炸）。
8. AC7：网关本机 curl 经代理成功。

## 验证命令

- `dotnet build WinForward.slnx`
- `dotnet test`（全量；关键过滤：FlowDispatcher / TcpProxyCoordinator / TcpRedirect / NdisPacketActionExecutor / TcpResetBuilder）
- smoke 观测：`rg "tcp.relay.started|tcp.redirect.created|packet.dropped|packet.reinjected" smoke/winforward.log`

## 风险文件 / 回滚点

- `FlowDispatcher.cs`（S1）、`NdisPacketActionExecutor.cs`（S2）：单文件改动，单独可回滚。
- `TcpProxyCoordinator.cs` + `TcpRedirectTable.cs`（S4-S6）：核心改动区；`ForwardLocalAddress=null` 时行为退化为现状，是天然回滚开关。
- `Program.cs`：仅接线，最后提交。

## 启动前确认

- prd.md / design.md 已过最终评审并获用户明确批准（task.py start 的前置）。
- implement.jsonl / check.jsonl 已含真实 spec 条目（sub-agent 模式门槛）。
