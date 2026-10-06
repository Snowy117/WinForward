# 复现：WinForward 中继的 TCP 半关闭与 RST 保真度缺陷

这份文档给出一个 **60 秒一轮**的最小复现，用来迭代修复。它不依赖任何第三方软件，
只用 harness 自己的 `target` 和 `client` 两个动词。

## 结论先给

在 WinForward 后面，客户端观察到的 TCP 结束语义和上游真实发生的语义对不上：

| 上游模式（target 实际做的） | 客户端**应该**看到 | 客户端实际看到（1201 次尝试） |
| --- | --- | --- |
| `resetAfterN`：回显 N 字节后发 RST | `reset` × 300 | **`reset` × 0** |
| `partialFin`：回显 N 字节后 FIN（客户端**不**半关闭） | `unexpectedEof` × 300 | `unexpectedEof` × 300 ✅ |
| `clean`：完整回显后 FIN（客户端先半关闭） | `clean` × 300 | `clean` × 56 |
| `halfClose`：客户端 FIN 后再发 3 帧 | `clean` × 301 | 计入上面 |

合计：`clean 56 / reset 0 / unexpectedEof 595 / timeout 550`。

**两个独立的缺陷，规律很干净：**

1. **客户端半关闭之后，连接就废了。** `clean` 和 `halfClose` 这两个模式的共同点是
   「客户端发完请求就 `shutdown(SD_SEND)`」，它们的 601 次里有 545 次没能走完。
   而 `partialFin` 明确**不**半关闭，300/300 全部正常。所以问题不在 FIN 本身能否
   转发，而在客户端半关闭之后这个流还能不能继续收到数据。

2. **上游的 RST 一次都没传到客户端。** 300 次 `resetAfterN` 里 0 次被客户端识别为
   reset，它们以 EOF 或挂起收场。RST 和「正常 FIN」被折叠成了同一个事件。

顺带一个测量事实：`achievedRate` 是 17.15/s（目标 20/s），所以客户端没有成为瓶颈，
那 550 次超时是真的挂到 10 秒超时，不是发包发不过来。

## 需要用到的文件

target 二进制、client 二进制、以及这个 plan：

```json
{ "arms": [ { "name": "REL", "kind": "reliability", "seconds": 60, "connectionsPerSecond": 20,
              "modeMix": "clean=25,resetAfterN=25,partialFin=25,halfClose=25" } ] }
```

已放在 VM 的 `C:\wfbench\e2e\rel-plan.json`，以及本机 `/tmp/wf-bench/deploy/e2e/rel-plan.json`。

## 步骤

### 1. 在 Linux 主机上起 target（一个终端，让它一直跑）

```bash
/tmp/wf-bench/pub/linux/WinForward.E2E target \
    --bind 192.168.77.4 --tcp-port 30010 --udp-port 30010 --dns-port 53 \
    --ledger /tmp/wf-bench/rel-ledger.jsonl
```

需要 Linux 侧放通 TCP 30010（`sudo iptables -I nixos-fw 1 -p tcp --dport 30010 -j ACCEPT`）。

### 2. 先跑一条直连基线，确认 harness 本身是好的

```bash
rm -rf /tmp/wf-bench/relout && mkdir -p /tmp/wf-bench/relout
/tmp/wf-bench/pub/linux/WinForward.E2E client \
    --target 192.168.77.4 --plan /tmp/wf-bench/deploy/e2e/rel-plan.json \
    --out /tmp/wf-bench/relout --label direct \
    --tcp-port 30010 --udp-port 30010 --dns-port 53
```

直连时 `observed` 应当**逐项等于** `expected`：

```
scheduledAttempts : 1201
connectAttempts   : 1201
expected          : {"clean":601,"reset":300,"unexpectedEof":300,...}
observed          : {"clean":601,"reset":300,"unexpectedEof":300,...}
```

这一步很重要：如果基线不是这样，说明是 harness 的问题而不是产品的问题。基线是全绿
的，所以后面看到的差异都是产品造成的。

### 3. 在 VM 上起 sing-box 和 WinForward，再跑同一个 plan

VM 上已有一个脚本把这几步串起来了：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File C:\wfbench\rel-through.ps1
```

它做四件事：关防火墙；起 `C:\wfbench\singbox\sing-box.exe`（sing-box 1.14.2，带
UoT 流水线修复）和 `C:\wfbench\wf-aot\WinForward.exe`（`udpOverTcp: true` 的配置）；
跑 client；收尾。跑完结果在 `C:\wfbench\relout\REL.jsonl`。

读结果：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File C:\wfbench\rel2.ps1 C:\wfbench\relout\REL.jsonl
```

### 4. 看这两个字段就够了

```
connectAttempts : 1201          <- 必须等于 scheduledAttempts
expected        : clean=601 reset=300 unexpectedEof=300
observed        : clean=56  reset=0   unexpectedEof=595 timeout=550
```

`expected` 由 `modeSchedule`（`clean,resetAfterN,partialFin,halfClose`）这个严格轮转
唯一确定，所以它是一把尺子：**如果 `expected` 不是轮转的精确分布，那说明统计本身有
偏差，先别信 `observed`。**

## 修复到什么程度算好了

- `observed.reset` 应当接近 300（客户端能把上游 RST 和正常 FIN 区分开）；
- `observed.timeout` 应当接近 0；
- `observed.clean` 应当接近 601（或者，如果某个模式确实无法支持，`fidelityMismatch`
  要能解释清楚是哪一种，而不是笼统地变成超时）。

## 两个缺陷分别怎么看

想单独盯一个，把 plan 的 `modeMix` 换成单一模式，跑起来更快：

```json
{ "arms": [ { "name": "REL", "kind": "reliability", "seconds": 30, "connectionsPerSecond": 20,
              "modeMix": "resetAfterN=100" } ] }
```

- `modeMix: "resetAfterN=100"` → 只看 RST 能否传达。修好后 `observed.reset` 应当
  等于 `connectAttempts`。
- `modeMix: "clean=100"` → 只看客户端半关闭之后流还能不能用。修好后 `observed.clean`
  应当等于 `connectAttempts`。
- `modeMix: "halfClose=100"` → 客户端半关闭后上游还要再发 3 帧（每帧 256 字节，
  共 768 字节的尾随数据）。修好后应当记为 `clean`，并且 `truncated` 为 0。

## 参考：相关代码位置

- 客户端的模式语义与期望映射：`benchmarks/WinForward.E2E/Client/Arms/ReliabilityArm.cs`
  （`ExpectedOutcome`、`ExchangeAsync` 里 `mode is TcpMode.Clean or TcpMode.HalfClose` 才
  做 `ShutdownQuietly(socket, SocketShutdown.Send)`）。
- target 侧的故障注入：`benchmarks/WinForward.E2E/Target/TcpTargetServer.cs`。
- 判定与账本：`Target/TargetRunner.cs`、`Target/LedgerWriter.cs`。
  target 的账本在 `/tmp/wf-bench/rel-ledger.jsonl`，它会记录自己实际执行了哪个模式、
  以及 `verdict`。**如果账本说 `reset` 而客户端说 `unexpectedEof`，那就是中继把 RST
  吞掉了**——这正是当前的情况。

## 已经拿到的账本证据

跑完之后 target 侧账本对自己行为的记录（两次运行累计）：

```
target-side verdicts: {'clean': 904, 'reset': 900, 'partialFin': 900, 'halfClose': 900, 'clientClosedEarly': 116}
target-side modes   : {'clean': 904, 'resetAfterN': 900, 'partialFin': 900, 'halfClose': 900, 'unknown': 116}

mode x verdict:
   clean          clean              904
   halfClose      halfClose          900     <- target 成功完成了「客户端半关闭后再发 3 帧」
   partialFin     partialFin        900
   resetAfterN    reset              900     <- target 确实发了 RST
```

也就是说 **target 每一次都把 RST 发出去了、每一次都把 half-close 的尾随数据发出去了**，
一次都没有失败。而客户端那边收到的是 0 次 reset、550 次超时。两端一一对照，缺陷只可能
在中继路径上，不在 target 也不在客户端。
