# MVP 性能初测（E2E harness，THRU 臂）

日期：2026-10-21
目的：MVP 跑通后，用原仓库的 `benchmarks/WinForward.E2E` 取第一组性能数据，与 2026-10-18 CPU 分析的现状对比。

## 1. 方法

沿用原仓库 harness 的 THRU 臂，参数对齐 CPU 分析那一轮：

| 项 | 值 |
|---|---|
| 计划 | `{"name":"THRU","kind":"throughput","seconds":30,"streams":4,"targetBytesPerSecond":60000000}` |
| 帧 | 32 KiB |
| 目标速率 | 60 MB/s ≈ 480 Mbps |
| 采样 | `--sampler-process WinForward.Controller`，1 Hz 读 `Process.TotalProcessorTime` |
| 客户端 | `WinForward.E2E client --target 192.168.100.2 --tcp-port 19000` |

被测路径：客户端连接 → WFP `ALE_CONNECT_REDIRECT` 改写到本地监听端口 → 控制器 accept、读 redirect context → 直连原始目的地 → 双向中继。

## 2. 结果

| 指标 | 值 |
|---|---|
| goodput | **479.92 Mbps**（目标 480） |
| 帧 | 发出 54878、回显 54876、`corrupt=0`、`protocolErrors=0` |
| 传输字节 | 1,799,932,800 |
| 中继侧 | 4 条连接，`bytes_in=1,799,998,548` / `bytes_out=1,799,998,400` |
| **`WinForward.Controller` CPU** | **90.4%**（单核百分比；26.20 CPU-s / 28.98 s） |
| 负载生成器 CPU | 434.2%（harness 自身成本，不与产品比较） |

与 2026-10-18 CPU 分析的对照：

| 场景 | 单核 CPU |
|---|---:|
| 旧代理路径（NDIS + sing-box SOCKS5） | 154.0% |
| 旧纯透传（NDIS 抓包 + 回注，不过 sing-box） | 58.1% |
| sing-box 同轮 | 47.0% |
| **新架构（WFP 重定向 + 直连中继）** | **90.4%** |

## 3. 可比性限制（必须一起读）

1. **不是同一条路径**。新栈目前只做**直连中继**，没有 SOCKS5 上游。旧「154%」是经过 sing-box 的完整代理路径，旧「58.1%」是包级抓取 + 回注、没有用户态 TCP 中继。90.4% 介于两者之间是预期的——它替换掉的是"每包成本"，代价是新引入的"每连接一次的用户态中继"。
2. **目标端在 VM 内部**。宿主防火墙阻断了 VM → 宿主的所有 TCP（ICMP 通、TCP 全超时，且无免密 sudo 可开端口），所以 target 只能起在 VM 里（`192.168.100.2:19000`）。这意味着数据不过物理/vNIC 链路，网络栈成本低于基线那轮，**90.4% 可能低估了真实链路下的数字**。
3. **单次运行**，未取多轮中位数。基线那轮也是单轮，但样本量同样为 1。
4. 内核侧成本按设计接近 0（每连接一次 classify），实测该轮 `rules_replaced` 等计数与中继无关，CPU 全部落在用户态中继上。

## 4. 与 kill criteria 的关系

Phase 0 的判据是「480 Mbps THRU 代理路径的进程 CPU 不能降到 110% 以下，就停止完整重写」。

本轮 90.4% 已经低于 110%，落在当初对"用户态 relay"的预测区间（85–100%）内。但**这不能当作 kill criteria 的直接结论**——判据说的是代理路径，而本轮跑的是直连中继。要真正判定，需要在「真实链路 + SOCKS5 上游」下复测一次。

结论：**方向成立，数值在预期区间**；正式判定留给带 SOCKS5 的复测。

## 5. 环境事实（供后续复测参考）

- 宿主 `192.168.100.4` 与 VM `192.168.100.2` 同网段，ICMP 通，但**所有 TCP 端口从 VM 侧均超时**（测过 19000 / 30053 / 30006 / 30010）。
- 因此跨机测量需要宿主放行端口（需 root），或把 target 放进 VM。
- 跨机那一版的失败现场保留在 findings 里：`relay error: OperationCanceledException`，`bytes_in=0`——中继出站连不上，客户端只看到协议错误。这本身就是"出站不可达时客户端无诊断信息"的一个实例。
