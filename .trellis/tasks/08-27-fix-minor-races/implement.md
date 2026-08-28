# Implement: 次要竞态修复

关联: `prd.md`、`design.md`。三个独立提交点（D1/D2/D3），D4 记录结论。

## Step 1 (D1): 动态 RST 序号

- [x] `TcpRedirectAssociation` 加 `ClientNextSeq/ServerNextSeq (uint?)`（lock 内写）。
- [x] forward/reverse 两个 rewrite 点之前做轻量 TCP 头 seq 跟踪
      （seq + payloadLen；SYN/FIN 计 1；只推进不回退）。
- [x] `TryInjectClientResetAsync` 改用跟踪值（null 回退 ISN+1）。
- [x] 测试：无数据回退 / 有数据 ack=ClientNextSeq / SYN 边界计 1。

验证: `dotnet test -c Release --filter "FullyQualifiedName~TcpProxy"` ✓

## Step 2 (D2): 注入失败显式化

- [x] 注入路径 catch `Win32Exception` → warn `reason=injectionFailure`
      （native error + handle + flow key）→ best-effort 客户端 RST（复用 D1 字段）
      → `FailAssociationAsync`（经墓碑单写点，不新增 TryRemove）。
- [x] 测试：fake injector 抛 Win32Exception → warn + 墓碑写入 + 拆除。
- [x] check 补强：`HandleSynAsync` 首包 SYN 注入 catch 也走同一结构化出口
      （原为自由文本 warn），`FailAssociationAsync` 对已注册 session 与原
      `TearDownSessionAsync` 等价；附测试
      `SynInjectionWin32FailureFailsExplicitlyWithoutReset`。

验证: 同上 ✓

## Step 3 (D3): 连接预算

- [x] `TcpProxyRelay.cs:27` 调用点传 `perAttemptTimeout: 10s, maxAttempts: 2`
      （internal 常量 `RelayConnectMaxAttempts` / `RelayConnectAttemptTimeout`）。
- [x] 测试：传参断言 + 最坏预算语义 + refused 快速失败。

验证: `dotnet test -c Release`（全量 353）+ `dotnet build -c Release`（0w0e）✓

## Step 4 (D4): 结论记录 + PRD 勾选

- [x] PRD AC-D4 勾选并注明：1ms 空批轮询保留（空闲单核 <0.5%，事件化维持
      datapath 否决结论）。

## Step 5: Windows smoke（tmux + evil-winrm-py）

- [ ] 交叉构建 → `upload`/`runps` 部署（复用 wf-batch-test；config 沿用 trace2）。
- [ ] 常规流量 trace：配对完整、零 failed/warn/error（除预期 reason 外）、
      relay 正常、墓碑/grace 行为不回归。
- [ ] 结果记入验证记录。

## Step 5: Windows smoke（tmux + evil-winrm-py）`[已完成 2026-08-28]`

- [x] 交叉构建 → `upload` 直传（tmux 交互式会话，规避 echo 吞输出）→ 部署
      `C:\Users\Neko\wf-batch-test`。
- [x] 常规流量 trace（8× curl example.com 经 SOCKS5 + 8× nslookup 8.8.8.8 +
      6× ping）：

```
TOTAL=3800  cap=820  comp=820（配对完美）  failed=0
notrel=14（合法存量连接水位，与 table-lifecycle smoke 的 11 同量级）
grace=1（墓碑生效）  injfail=0（无注入失败；可观测性由单测锁定，design §7）
relay 2/1（kill 时最后一条活跃属正常）  warn=0  err=0
```

- [x] 结论：无回归；D2 的 `reason=injectionFailure` 事件零触发（预期）。

## 风险文件与回滚

| 提交 | 触碰 | 回滚 |
|------|------|------|
| D1 | TcpProxyCoordinator | revert（字段纯增量，null 回退） |
| D2 | TcpProxyCoordinator | revert（catch 出口还原裸形态） |
| D3 | TcpProxyRelay | revert（恢复默认参数） |

## start 前检查

- [x] prd.md 收敛（含研究修正回填）
- [x] design.md 定稿（D3 预算 10s×2；D4 关闭结论；交叠清单）
- [x] implement.jsonl / check.jsonl 真实条目
- [ ] 用户批准最终规划摘要
