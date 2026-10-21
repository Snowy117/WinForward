# MVP 性能初测（E2E harness，THRU 臂）

日期：2026-10-21
目的：MVP 跑通后，用原仓库的 `benchmarks/WinForward.E2E` 取第一组性能数据，与 2026-10-18 CPU 分析的现状对比。

> **先看最后一节**：直连中继的初测（90.4%）与经真实 sing-box 的复测（81.3%）是两次不同的测量，与基线同形的是后者。

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
---

# 附加：经真实 sing-box 的复测（与基线同形）

日期：2026-10-21（同一天，初测之后）

初测是直连中继，与基线的「WinForward → sing-box」不同形。这一轮把上游补齐，路径与 2026-10-18 那轮一致：

```
WinForward.E2E client (VM) → WFP 重定向 → WinForward.Controller (VM) → 127.0.0.1:1080
  sing-box 1.14.2-singfix (direct outbound) → 192.168.100.4:40000 target (宿主)
```

## 上游的来历

按 `WinForward` 仓库 `benchmarks/WinForward.E2E/AGENTS.local.md` §5 的指导，从源码构建：

- sing-box `v1.14.2` + `SagerNet/sing@87c33f17688f`，打补丁
  `.trellis/tasks/10-06-e2e-competitor-benchmark/research/sing-socks-pipelined-payload.patch`
  （SOCKS5 握手后流水线字节被 `bufio.Reader` 吞掉）
- 补丁自带的 `TestPipelinedRequestPayloadReachesHandler`：打前 FAIL、打后 PASS
- 另编了一个**撤销补丁的对照版**：同一流水线测试下，打过补丁的回显 41 字节完整帧（26 ms），
  未打补丁的读超时——证明补丁确实解决了这个问题
- 配置与仓库模板 `scripts/singbox/base-singbox-socks5.json` 逐字节相同

## 结果

| 项 | 值 |
|---|---|
| goodput | **479.88 Mbps**（目标 480） |
| 帧 | 发出 54874、回显 54874、`corrupt=0`、`protocolErrors=0` |
| 中继 | 4 条连接全部 `via=socks5:127.0.0.1:1080`，`bytes_in=1,799,998,548` |
| **`WinForward.Controller` CPU** | **81.3%**（23.55 CPU-s / 28.96 s） |
| **`sing-box` CPU** | **53.0%**（15.36 CPU-s / 28.95 s） |

与 2026-10-18 基线的对照：

| 项 | 旧（NDISAPI 包级 + sing-box） | 新（WFP 重定向 + sing-box） | 变化 |
|---|---:|---:|---:|
| 代理路径进程 | 154.0% | **81.3%** | **−47.2%** |
| sing-box | 47.0% | 53.0% | +12.8% |
| 两者合计 | 201.0% | 134.3% | −33.2% |

`sing-box` 这一行是环境校验：同一个二进制、同样的配置与目标，53.0% 对 47.0% 偏高 12.8%，
说明这台 VM 当前状态与当年并非完全一致（构建工具链是 nixpkgs 的 go1.26.7，官方 release 用的不是它），
因此 81.3% 也应带同样的折扣看，不宜当作精确复现值。

## 关于 kill criteria

Phase 0 的判据是「480 Mbps THRU 代理路径的进程 CPU 不能降到 110% 以下就停止完整重写」。

本轮**代理路径 = 81.3%**，与基线同形、同一 harness、同一计划、同一台 VM。判据满足，且留有约 26% 的余量。

## 可比性限制（这一轮的）

1. **sing-box 的构建链不同**（源码自建 vs 官方 release），其 CPU 偏高 12.8% 提示环境有差异。
2. **单次运行**，与基线同样只有一轮样本。
3. 目标在宿主机、客户端与控制器在 VM，跨 vNIC——这一点与基线一致。
4. 上游在 `127.0.0.1:1080`（loopback），与基线一致。
