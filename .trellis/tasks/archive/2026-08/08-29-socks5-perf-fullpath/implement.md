# Implement: SOCKS5 full-path benchmarks and performance

## 阶段 A：测量基建（不碰产品代码）

- [x] A1 `Stability/LoopbackSocks5TcpServer.cs`：RFC 1928 最小 TCP 服务端（greeting/method/CONNECT/透传），仿 LoopbackSocks5UdpServer 惯例
- [x] A2 `Perf/FrameRewriterBenchmarks.cs`：SYN + mid-flow 两形态 × 128/1400B；TryRewriteForwardLeg（两种 shape）/ClassifyTcpSyn/SwapEthernetMacs
- [x] A3 `Perf/Socks5HandshakeBenchmarks.cs`：对 A1 跑 ConnectAsync + ConnectDestinationAsync 全握手
- [x] A4 `Perf/DispatcherBenchmarks.cs` 增加 `WarmProxyDisabledTraceAsync`（policy 命中 + server 字典）
- [x] A5 `Perf/UdpSessionBenchmarks.cs` 改真实 transport（Socks5UdpTransport + LoopbackSocks5UdpServer）+ await ready 语义
- [x] A6 `Stability/TcpThroughputScenario.cs` + `SoakScenario`/`SoakRunner`/`SoakOptions` 注册 `tcp.throughput`
- [x] A7 基准文件遵守 400 有效行限制与 directory-structure 规则

### A 阶段结果（2026-08-29，ShortRun 冒烟）

- 构建 Release 零警告；测试 386/386；src/ 零改动（A5 ready 观察用假服务器 `RelayForwarded` 计数器，无需 internal hook）。
- 冒烟数字：ClassifyTcpSyn 39.7ns；SwapEthernetMacs 7.0ns/0B（JIT 已逃逸分析掉 6B 分配，C1 降级为源码明确化）；HostShape 重写 73.7ns@128→622.7ns@1400；**ForwardedShape 248.8ns@128→2.94µs@1400（C2 头号热点，0 分配纯 CPU，嫌疑 `IPAddressValue.From(IPAddress)`）**；Socks5Handshake 1.13ms/70.6KB；UdpSession 真实 transport await-ready 1/100/1000 会话=1.44/30.5/313.8ms（10.3x@10x，超线性消失；~90KB/会话，含被捕获异常 5/会话需 B2 定位）；WarmProxy 594.2ns/352B vs WarmPass 221ns/160B；tcp.throughput quick：32,786 传输 0 失败，144.5MB/s 单向，握手均值 1.5ms。

## 阶段 B：基线

- [x] B1 全矩阵跑绿：54 基准 PERF_EXIT=0 + stability quick STAB_EXIT=0（2026-08-29）
- [x] B2 基线落盘 `benchmarks/results/2026-08-29-socks5-perf/baseline/`（README 含超线性调查结论：旧 29x 为 Noop 入队伪影，真实语义线性 10.8x@10x；每会话 90KB 待 C3 拆解产品/框架份额）
- [x] B3 复核 70% 吞吐目标分母：TcpRelay 单连接口径与 soak 16 并发回显口径不可比 → 分母改为 soak 自带 bare 对照模式（C4 实现），PRD 已同步修正

### C 阶段补充（B 复核后的定量热点）

- C4 范围新增：`TcpThroughputScenario` 加 bare 对照模式（改 soak 不改产品），验收比例 = socks5/bare。

## 阶段 C：优化（每项独立 commit，先跑基线确认热点再动手）

**预研结论（A 阶段代码解剖，待 B 基线定量确认）：**
- C2 主热点：`TcpRedirectTable.cs:55` 存 `IPAddress?`，每包 `TcpFrameRewriter.cs:25` 调 `IPAddressValue.From(IPAddress)`（TryWriteBytes 虚调用）→ 表创建时（`TcpRedirectSetup.cs:119` 冷边）转换一次存 `IPAddressValue?`。
- C2b（新发现）：WarmProxy 每包额外 192B = Proxy 分支不走 `DispatchAsync` sync fast path（FlowDispatcher.cs:256 落入 `DispatchSlowAsync` async 状态机）。Proxy 是主路径，应纳入 sync fast path（FlowDispatcher.cs:247-277 区域）。
- C1 降级：SwapEthernetMacs 分配已被 JIT 逃逸分析消除（0B 实测），仅源码明确化 stackalloc。

- [x] C1 `SwapEthernetMacs` 零分配（TcpFrameRewriter.cs:55）——字节索引对换，7.0→4.1ns/0B
- [x] C2 FrameRewriter 数据面其余分配清零——`IPAddressValue?` 存 association（TcpRedirectTable/Setup 冷边转换一次），ForwardedShape 2887.9→607.4ns@1400（4.8x，与 HostShape 持平）；调用链审计：TcpRedirect 域仅剩冷边转换（TcpRedirectSetup provider 处、TcpProxyRelay.cs:46 ToIPAddress 每 relay 连接一次），无其他每包转换
- [x] C2b Proxy 纳入 sync fast path——WarmProxy 596.4ns/352B → 257.9ns/160B（192B 状态机消除，与 Pass 分配一致）；用户审查抓出 C2b 遗留死代码（FlowDispatcher 恒假条件，FlowAction 枚举仅 {Proxy,Pass,Block}），已删除
- [x] C3 UDP 冷路径——枚举簿记 2.1KB→**0.4KB/会话（≤1KB 达标）**：BoundedSetupQueue 单槽快路径、去 registered TCS、catch 内联失败处理、委托 ctor 缓存、字典 clamp 预分；Noop 探针 5.73→3.84KB@100；捕获异常判定为拆卸正常信号（零 SocketException，无产品缺陷）；时间线性保持
- [x] C4 bare 对照模式（--tcp-relay-mode socks5|bare）——socks5 141.38 vs bare 150.60 MB/s，**比值 93.9% ≥ 70% PASS**；握手 1.45ms 重新测定
- [x] C5 每步 386/386 + 零警告 + udp.lossRate 零丢包回归 + tcpthroughput 双模式 EXIT=0

## 阶段 D：复测与固化

- [x] D1 优化后全矩阵重跑（57 基准 PERF_EXIT=0 + stability quick STAB_EXIT=0），落盘 `benchmarks/results/2026-08-29-socks5-perf/optimized/`（含 bare follow-up 对照 + windows/ 冲烟证据 + README 对照表）
- [x] D2 spec 更新：`hot-path.md` 契约 3 扩展（Proxy warm shape）+ 新增 "SOCKS5 Path Contracts" 段（7 节完整格式，英文）
- [x] D3 Windows 实机冲烟（WinLtsc via evil-winrm-py）：udp.lossRate 两轮零丢包；产品真实代理 TCP（3 次完整 redirect→relay 闭环 + sing-box 侧 7 条 ESTABLISHED 铁证 + curl 200/TLSv1.3）UDP（nslookup ✓）；61 事件 0 警告。**方法论发现**：上游 fake-IP 路由器透明代理使"出口 IP"指标失效（WinForward 停止时 curl 也显示 2406:da18 出口），可靠证据 = SOCKS5 服务器侧连接表 + 产品 debug 事件（已记入 optimized/README）
- [x] D4 journal 记录 + 任务归档准备

## 验证命令

```bash
dotnet build -c Release   # 零警告
dotnet test               # 386/386 基线
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --job short
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --quick
```

## 风险文件与回滚点

| 文件 | 风险 | 回滚 |
|---|---|---|
| `UdpProxyCoordinator.cs`（503 行，并发敏感） | C3 改动 | 独立 commit revert |
| `TcpFrameRewriter.cs`（纯函数，单测全覆盖） | 低 | 独立 commit revert |
| `TcpProxyRelay.cs`（若 C4 触及） | 数据面行为 | 独立 commit revert + tcp soak |

## task.py start 前检查

- [ ] prd.md 收敛（无未决 Open Question）
- [ ] design.md / implement.md 就位（本文件）
- [ ] implement.jsonl / check.jsonl 填真实条目
- [ ] 用户批准最终规划摘要
