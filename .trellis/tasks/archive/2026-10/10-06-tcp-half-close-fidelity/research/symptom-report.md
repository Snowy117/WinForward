# WinForward TCP 症状报告（来自端到端 benchmark）

这份文档只描述**观察到的症状**和可核对的证据，不做根因判断。测量环境：WinForward AOT 构建，
通过 loopback 上的 sing-box 1.14.2 转发，客户端在 Windows VM 上、靶机在 Linux 上，两者之间是
真实的网络路径（不是环回）。

## 一句话

客户端**感知不到上游的 RST**，并且**一旦客户端半关闭（发 FIN），这条连接就用不下去了**。

## 症状一：上游 RST 不传递给客户端

上游按约定发 RST，客户端一次都没识别出来。

| 项 | 数值 |
| --- | --- |
| 要求靶机发 RST 的连接数 | **300** |
| 客户端识别为 `reset` 的 | **0** |
| 靶机账本记录的 `reset` verdict | **900 次，零失败** |

缺失的 300 次去哪里了：它们混在 595 次 `unexpectedEof` 和 550 次 `timeout` 里——也就是说，
**RST 要么被折叠成了「正常 EOF」，要么干脆让连接挂到 10 秒超时**。客户端无法区分「对端优雅关闭」
和「对端异常重置」，这两种语义在应用层是完全不同的处理路径。

## 症状二：客户端半关闭之后连接失效

这条规律非常干净——**凡是客户端发完请求就 `shutdown(SD_SEND)` 的模式全部劣化，不发 FIN 的模式
完好**：

| 上游模式 | 客户端是否发 FIN | 期望 | 实测 |
| --- | --- | --- | --- |
| `partialFin` | **否** | `unexpectedEof` × 300 | **300，全对** |
| `resetAfterN` | 否 | `reset` × 300 | 0（见症状一） |
| `clean` + `halfClose` | **是** | `clean` × 601 | **仅 56** |

601 次「客户端半关闭」的尝试里 **545 次没能正常走完**。

`halfClose` 模式尤其说明问题：客户端发完请求就半关闭，然后**仍然期待收到数据**——靶机随后会再发
3 帧（每帧 256 字节）再关闭。这是 HTTP/1.0 风格的经典用法，也是长轮询/流式响应的基础。实测中
这些尾随数据基本没到达。

## 总体数字（REL 臂，60 秒 @ 20 连接/秒，四种模式各 25%）

```
scheduledAttempts : 1201
connectAttempts   : 1201        ← 排了多少就统计了多少，无丢弃
expected          : clean=601 reset=300 unexpectedEof=300
observed          : clean=56  reset=0   unexpectedEof=595 timeout=550
achievedRate      : 17.15/s     ← 目标 20/s，客户端不是瓶颈
meanConnectMs     : 6.708
meanTransferMs    : 7.284
```

`expected` 是由模式轮转唯一确定的，可以当作一把尺子：**它和实测的差就是产品行为**。

`achievedRate` 略低于目标，是因为那 550 次超时每次要挂满 10 秒才被放弃——**不是客户端发不出去，
而是连接挂在那里**。

## 两端对照的证据

靶机侧的账本（target 对自己行为的记录）与客户端观察**一一对不上**：

```
target-side verdicts: {'clean': 904, 'reset': 900, 'partialFin': 900, 'halfClose': 900, 'clientClosedEarly': 116}
target-side modes   : {'clean': 904, 'resetAfterN': 900, 'partialFin': 900, 'halfClose': 900, 'unknown': 116}

mode x verdict:
   clean          clean              904
   halfClose      halfClose          900     ← 靶机成功完成了「客户端半关闭后再发 3 帧」
   partialFin     partialFin        900
   resetAfterN    reset              900     ← 靶机确实发了 RST
```

**靶机每一次都履约了，零失败**；而客户端收到的是 0 次 reset 和 550 次超时。同样的靶机在直连
（不经任何代理）时，`observed` 与 `expected` **逐项完全相等**——所以这不是靶机的问题，也不是
客户端的问题。

## 与既有工作记录的对应

`.trellis/tasks/08-30-proxy-perf-stability/prd.md` 把

> `port-budget-windows` — … loopback-aware **upstream error-path RST close**（symmetric to #2's
> client RST）…

列在 **"New candidates"** 里，即**已识别但尚未实现**。

而已落地的 `ClientResetInjector`（`08-30-fast-hardening`，2026-08-30）服务的是另一条路径：
`TcpProxyCoordinator` 在**中继自身故障/容量拒绝**时给客户端一个可见的失败面
（`TcpProxyCoordinator.Injections.cs:201`「established client-visible posture through
ClientResetInjector」）。

**两者不重叠**：现有机制覆盖「中继出错」，不覆盖「上游发来 RST」。本报告测到的正是后者的用户可见
影响。症状二（半关闭）在既有记录里没有对应条目。

## 修好之后应该是什么样

用同一个 60 秒的 REL 臂复测，看三个数：

- `observed.reset` 接近 **300**（客户端能把上游 RST 和正常 FIN 区分开）
- `observed.timeout` 接近 **0**
- `observed.clean` 接近 **601**（半关闭之后流仍然可读）

参考基线——**直连**（不经代理）时这三个数是 `reset=300`、`timeout=0`、`clean=601`，`observed`
与 `expected` 逐项相等。这就是「完好」的样子。

## 怎么复现

复现步骤、单模式的最小 plan（`modeMix: "resetAfterN=100"` 之类）和命令都在
`/tmp/rel-repro.md`。那份文档是给「迭代修复」用的，这份是给「看症状」用的。

## 需要留意的测量前提

这份数据用的是**修好之后**的 harness。在此之前 harness 自身有一个统计偏差（把已完成的尝试从统计里
删掉，导致留下的样本偏向慢/挂起的连接），它会把超时率**高估**、把模式分布扭曲。修好后的数字是
上面这一组，比修之前更温和但结论一致：**0 次 RST、大量超时、半关闭后不可用**。
