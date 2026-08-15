# 执行计划:修复网络协议审查发现的问题

## 顺序与依赖

R1、R3、R4、R5、R6 相互独立;R2 为链式改动(执行器 → 协调器 → 会话 → sink → 注入器)。
先做 R1(独立、影响中继)与 R2,再批量做 R3-R6,最后全量验证。

## 步骤

1. **R1** — `src/WinForward.Runtime/Socks5Client.cs`
   - `GetUpstreamStream()` 中设置 `_socket.ReceiveTimeout = Timeout.Infinite; _socket.SendTimeout = Timeout.Infinite;` 后返回 `_stream`。
   - 新增测试(Socks5ControlConnectionTests):经真实 loopback SOCKS5 测试服务器建立连接 →
     `GetUpstreamStream()` → `((NetworkStream)stream).Socket.ReceiveTimeout == -1`(且 SendTimeout == -1)。
   - 现有连接超时/认证测试必须仍通过(连接阶段超时行为未变)。

2. **R2** — MAC 透传链:
   - `src/WinForward.Runtime/NdisPacketActionExecutor.cs`:提取 `packet.Lease.Frame.Span[6..12]` 的源 MAC,传入 `TrySendAsync`。
   - `src/WinForward.Runtime/UdpProxyCoordinator.cs`:
     - `IUdpResponseSink.InjectAsync` 增加 `byte[]? clientMac`。
     - `TrySendAsync` 增加 `byte[]? clientMac = null`;`CreateSessionAsync`/`UdpProxySession` 记录首个 6 字节 MAC。
     - `ReceiveLoopAsync` 调用 sink 时附带 MAC。
   - `src/WinForward.Runtime/UdpResponseReinjector.cs`:`InjectAsync` 按 design.md 选择目的 MAC;
     转发流 MAC 缺失/长度非 6 → 丢弃 + 限速日志(复用 `LogMissingOriginAdapter` 模式)。
   - 更新测试替身与调用点(UdpProxyCoordinatorTests、UdpRelayTests、Socks5ControlConnectionTests 的 `NoopResponseSink`)。
   - 新增/更新测试:
     - 转发流:重建帧目的 MAC == 记录的客户端 MAC,源 MAC == 适配器 MAC(现有
       `ForwardedFlowResponseInjectsTowardOriginAdapter` 增强断言)。
     - 宿主流:行为不变(现有断言保持)。
     - 转发流缺 MAC:fail-closed 丢弃 + 限速日志。

3. **R3** — `src/WinForward.Runtime/Socks5Client.cs` `ReceiveAsync`:端口 + 地址族校验替代完全相等。
   - 新增测试:同地址族不同 IP 同端口 → 接受;不同地址族/不同端口 → 拒绝。

4. **R4** — `src/WinForward.Protocols/Socks5State.cs`:密码允许 0 长度。
   - 新增测试:`UsernamePassword("user", "")` 产出 `[1, 4, 'u','s','e','r', 0]` 合法编码。

5. **R5** — `src/WinForward.Protocols/Socks5Udp.cs` 与 `Socks5Client.cs:287`:域名解码改 ASCII。
   - 新增测试:ATYP=3 ASCII 域名正确解码;非 ASCII 字节按替换语义处理且不抛异常。

6. **R6** — `tests/WinForward.Core.Tests/Socks5ControlConnectionTests.cs:439`:
   `if (disposing && DisposeException is not null) throw DisposeException;`

## 验证

- `dotnet build` 无新增警告。
- `dotnet test tests/WinForward.Core.Tests/WinForward.Core.Tests.csproj`:全绿、退出码 0、
  无 "Test host process crashed"。
- 复查点:per-attempt 超时测试(连接/认证阶段)在 R1 后仍通过;UDP 宿主流测试在 R2 后仍通过。

## 回滚点

- R1 之后:跑 TcpProxyRelayTests + Socks5ControlConnectionTests 全绿再继续。
- R2 之后:跑 UdpProxyCoordinatorTests + UdpRelayTests 全绿再继续。
- 最后:全量测试 + 构建。
