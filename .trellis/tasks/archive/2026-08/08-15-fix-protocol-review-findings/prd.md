# 修复网络协议审查发现的问题

## Goal

修复 2026-08-15 网络协议审查发现的 6 个问题:2 个严重问题 + 4 个小问题。

## Requirements

### R1 — TCP 中继不得因 30s socket 超时误杀空闲连接 (严重)

- 现状: `Socks5ControlConnection.ConnectOnceAsync` 在连接建立前设置 `socket.ReceiveTimeout/SendTimeout = 30s`(per-attempt 超时),该 socket 随后通过 `GetUpstreamStream()` 交给 `TcpProxyRelay` 长期读写。.NET 的 async socket 操作遵循 Receive/SendTimeout,因此上游空闲 >30s 时中继被 `SocketException(TimedOut)` 杀死。
- 这与 `TcpProxyRelay` 注释声明的 30 分钟 stall 窗口(M4)矛盾:30s 的 socket 超时总是先触发,stall 窗口永远不起作用。
- 要求:
  - 连接建立与认证阶段仍保留 per-attempt 超时语义(30s 上限,现有测试不得退化)。
  - 进入长期中继(或移交 `GetUpstreamStream()`)后,该 socket 的 Receive/Send 超时必须不再生效;空闲保护仅由中继自身的 stall 超时(30 分钟)负责。
  - 空转 >30s 的连接(如 SSH、长轮询)不得被断开;中继仍能在 30 分钟 stall 窗口内按现状工作。
- 约束: 不得改变 `Socks5ControlConnection` 的公开 API 语义;UDP 传输路径不受影响。

### R2 — 转发流 UDP 响应的目的 MAC 必须可达客户端 (严重)

- 现状: `UdpResponseReinjector.InjectAsync` 用 `target.Mac`(适配器自身 MAC)同时作为重建帧的源和目的 MAC。对 Forwarded 流(ON_SEND 注入 Hyper-V 交换机),目的 MAC 是宿主机自己的 NIC MAC,vSwitch 将其送交宿主协议栈而非 VM,VM 永远收不到响应。
- 要求:
  - 转发流的 UDP 响应重建帧必须使用客户端(VM)的 MAC 作为目的 MAC;源 MAC 保持适配器 MAC。
  - 宿主流的现有行为(toward MSTCP,ON_RECEIVE)不得改变。
  - 客户端 MAC 需要在流建立时记录(与 TCP 路径 `OriginalSynFrameCopy` 同思路);无法取得客户端 MAC 时按 fail-closed 处理(丢弃响应并保留现有限速日志风格)。
  - `UdpFrameBuilder.TryBuild` 的 sourceMac/destinationMac 参数语义不变;修复调用方传入正确 MAC。
- 约束: 不得破坏宿主流路径与现有测试语义;`UdpAdapterTarget` 结构可扩展但需保持向后兼容。

### R3 — SOCKS5 UDP relay 源端点校验放宽 (小)

- 现状: `Socks5UdpTransport.ReceiveAsync` 严格要求 `result.RemoteEndPoint.Equals(RelayEndpoint)`,多宿主/任播中继从不同于 BND.ADDR 的 IP 回包会被丢弃(RFC 1928 未强制该相等)。
- 要求: 放宽为地址与 BND 地址属于同一作用域/地址族且端口一致即可;仍须拒绝明显不相关的源(如不同地址族的源)。现有安全约束(拒绝未知来源)不得完全移除。

### R4 — 允许 SOCKS5 空密码 (小)

- 现状: `Socks5Messages.UsernamePassword` 拒绝 `secret.Length == 0`,RFC 1929 允许 0 长度密码。
- 要求: 密码可为空(0 字节);用户名仍要求 1-255 字节。配置校验层若有意拒绝空密码配置,保持现状即可——仅协议编码层放宽。

### R5 — ATYP=3 域名按 ASCII 解码 (小)

- 现状: `Socks5UdpCodec.TryDecode` 对 ATYP=3 域名使用 UTF-8 解码,RFC 1928 规定域名为 ASCII。
- 要求: 改用 ASCII 解码(与 `Socks5Client.ResolveDomainAsync` 侧的一致性检查:该处用 UTF-8 读回复域名,同样改 ASCII);非法字节按现有 fail-closed 风格拒绝或替换,不得抛异常。

### R6 — 修复测试宿主 finalizer 崩溃 (小)

- 现状: 测试全部 271 通过后,测试宿主因 `Socks5ControlConnectionTests.cs:439` 的 `TrackingSocket.Dispose` 测试替身在 finalizer 中抛 `IOException("synthetic socket disposal failure")` 而崩溃("Test host process crashed")。
- 要求: 测试运行结束后宿主进程干净退出,无 unhandled finalizer 异常;现有测试断言不被削弱(合成故障路径仍需覆盖)。

## Acceptance Criteria

- [ ] R1: 有测试证明中继启动后 30s 内无数据不导致断开(如以可控超时的 fake socket/stream 验证上游 socket 超时已禁用),且 per-attempt 连接超时行为测试仍通过。
- [ ] R2: 有测试断言转发流 UDP 响应重建帧的目的 MAC 为记录的客户端 MAC、源 MAC 为适配器 MAC;宿主流行为不变(现有 `UdpRelayTests` 通过或按新语义更新)。
- [ ] R3: 有测试覆盖:同地址族不同 IP 但端口一致的回包被接受;不同地址族的源仍被拒绝。
- [ ] R4: `UsernamePassword("user", "")` 产出合法 RFC 1929 消息(0 长度密码字段)。
- [ ] R5: ATYP=3 域名解码为 ASCII;含非 ASCII 字节时按定义行为处理并有测试。
- [ ] R6: `dotnet test` 全程退出码 0,无 "Test host process crashed"。
- [ ] 全部现有测试(271+)在修复后通过或按新语义有意更新。
- [ ] `dotnet build` 无警告级别回归(仓库现有告警基线除外)。

## Notes

- 修复范围由用户确认:2 个严重问题 + 全部 4 个小问题。
- 审查报告细节见会话中 2026-08-15 的协议审查结论(R1-R6 对应其发现)。
- 复杂任务:需要 `design.md` + `implement.md` 后再 `task.py start`。
