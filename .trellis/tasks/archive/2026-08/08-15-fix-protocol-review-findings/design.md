# 设计:修复网络协议审查发现的问题

## 范围

按用户确认覆盖 R1-R6(2 严重 + 4 小)。所有改动落在 `src/WinForward.Protocols`、`src/WinForward.Runtime`、`tests/WinForward.Core.Tests`。无配置格式、无驱动 ABI、无 CLI 变更。

---

## R1 — TCP 中继上游 socket 超时清除

### 根因

`Socks5ControlConnection.ConnectOnceAsync`(Socks5Client.cs:120-121)为 per-attempt 超时设置
`socket.ReceiveTimeout = socket.SendTimeout = 30s`,该 socket 随后经 `GetUpstreamStream()`
交给 `TcpProxyRelay` 长期读写。.NET 的 async 收发遵循这两个超时,上游空闲 >30s 即抛
`SocketException(TimedOut)`,而 `TcpProxyRelay` 设计的 30 分钟 stall 窗口(M4)永远轮不到。

### 决策

在 `GetUpstreamStream()`(Socks5Client.cs:205)返回流之前清除两个超时:

```csharp
internal Stream GetUpstreamStream()
{
    _socket.ReceiveTimeout = Timeout.Infinite; // -1
    _socket.SendTimeout = Timeout.Infinite;
    return _stream;
}
```

- 时间线:CONNECT 命令(`ConnectDestinationAsync`)仍处于 per-attempt 30s 窗口内(它发生在
  `GetUpstreamStream()` 之前,TcpProxyRelayFactory.EstablishAsync:35→38),连接/认证阶段行为不变。
- `_attemptCancellation.CancelAfter(timeout)` 保持原样:中继阶段没有任何操作使用
  `AttemptToken`,该 CTS 对中继惰性;认证/命令阶段仍受其约束。
- 空闲保护移交:中继写方向由 `PumpAsync` 的 writeTimeout CTS(30 分钟)兜底,读方向同理。
- UDP 控制连接不经过 `GetUpstreamStream()`,其 30s socket 超时保留;UDP 控制 socket 在
  ASSOCIATE 完成后无后续操作,超时永不触发,行为不变。

### 兼容性

- `GetUpstreamStream` 为 internal,`WinForward.Runtime` 已声明 InternalsVisibleTo 测试程序集。
- 公开 API 不变;`Socks5ControlConnection` 其它语义不变。

---

## R2 — 转发流 UDP 响应目的 MAC

### 根因

`UdpResponseReinjector.InjectAsync`(UdpResponseReinjector.cs:86-95)以 `target.Mac` 同时作为
重建帧的源/目的 MAC。转发流(ON_SEND 注入 vSwitch)目的 MAC 为宿主机自身 NIC MAC,vSwitch 将其
送交宿主协议栈,VM 收不到。TCP 反向腿不受影响(复用捕获帧的正确 MAC)。

### 决策:客户端 MAC 沿会话链路透传

捕获点 → 会话 → 响应注入,与 TCP 的 `OriginalSynFrameCopy` 同思路:

1. **捕获点**:`NdisPacketActionExecutor.HandleUdpProxyAsync` 已持有
   `packet.Lease.Frame.Span`,Ethernet 源 MAC 在 offset 6-11(ON_RECEIVE 帧的源 MAC 即客户端/VM MAC)。
   提取 6 字节传给协调器。
2. **协调器**:`UdpProxyCoordinator.TrySendAsync` 增加参数 `byte[]? clientMac = null`
   (可选参数避免大改既有测试调用点)。`CreateSessionAsync` 将首个非空 MAC 存入
   `UdpProxySession.ClientMac`。
3. **会话**:`UdpProxySession.ReceiveLoopAsync` 调用 sink 时附带 `ClientMac`。
4. **Sink 接口**:`IUdpResponseSink.InjectAsync` 增加 `byte[]? clientMac` 参数。
   测试替身(`NoopResponseSink` 等)同步更新。
5. **注入器**:`UdpResponseReinjector.InjectAsync`:
   - 转发流:目的 MAC = `clientMac`(长度必须为 6,否则 fail-closed 丢弃 + 限速日志,
     复用 `LogMissingOriginAdapter` 模式);源 MAC = `target.Mac`。
   - 宿主流:行为不变(源/目的 MAC 均为 `target.Mac`)。

`UdpFrameBuilder.TryBuild` 参数语义不变(sourceMac/destinationMac 分开),仅调用方改传值。

### 边界

- 会话由首包创建,`ClientMac` 在真实路径上必有值;缺 MAC 的转发响应 fail-closed(有日志、有限速)。
- 宿主流的 MAC 语义不被触碰(要求明确不得改变)。
- IPv6 无关:MAC 是 L2 概念。

---

## R3 — SOCKS5 UDP relay 源校验放宽

`Socks5UdpTransport.ReceiveAsync`(Socks5Client.cs:457)当前要求
`result.RemoteEndPoint.Equals(RelayEndpoint)`。改为:

```csharp
if (result.RemoteEndPoint.Port != RelayEndpoint.Port ||
    result.RemoteEndPoint.AddressFamily != RelayEndpoint.AddressFamily)
    throw new IOException(...);
```

- 端口 + 地址族仍严格;IP 允许不同(多宿主/任播中继)。IPv6 scope 不比较(接收接口的 scope
  与 relay 侧 scope 本就可能不同,比较会导致误杀)。
- 保留"拒绝未知来源"的安全姿态:不同端口/不同地址族仍拒绝。

---

## R4 — 允许 SOCKS5 空密码

`Socks5Messages.UsernamePassword`(Socks5State.cs:34)删除 `secret.Length == 0` 限制,仅保留
`> 255` 与用户名 `0 or > 255` 检查。配置层 `ConfigurationModels.cs:214` 的
"1..255 UTF-8 字节"校验保持不变(配置层面仍拒绝空密码,协议编码层放开,互不冲突)。

---

## R5 — ATYP=3 域名按 ASCII 解码

- `Socks5UdpCodec.TryDecode`(Socks5Udp.cs:56):`Encoding.UTF8.GetString` → `Encoding.ASCII.GetString`。
  非 ASCII 字节被替换为 `?`(不抛异常,维持 fail-closed 无抛出风格)。
- `Socks5ControlConnection.ResolveDomainAsync`(Socks5Client.cs:287)同步改 ASCII,两侧一致。

---

## R6 — 测试替身 finalizer 不再抛异常

`Socks5ControlConnectionTests.TrackingSocket.Dispose(bool)`(Socks5ControlConnectionTests.cs:439)
改为仅在受控释放时抛合成异常:

```csharp
protected override void Dispose(bool disposing)
{
    OnDisposing?.Invoke();
    IsDisposedValue = true;
    base.Dispose(disposing);
    if (disposing && DisposeException is not null) throw DisposeException;
}
```

- finalizer 路径(`disposing == false`)永不抛;受控 `Dispose()` 仍抛,合成故障测试语义不变。
- 对象未被显式释放时不再导致 "Test host process crashed"。

---

## 回滚形态

每个问题都是独立小改动(单一文件或局部链),可独立回滚;无 schema/格式迁移。
改动顺序:先 R1(独立)与 R2(链式),再 R3-R6(独立)。全部完成后跑全量测试。
