# Implement: TCP 重定向端口预算与连接上限保护

关联: `prd.md`、`design.md`。单原子提交（design §6）。

## 实现顺序

### Step 1: 配置层 `tcpFlowCapacity` `[已完成 2026-08-28]`

- [x] `src/WinForward.Configuration/ConfigurationModels.cs`：顶层 DTO 加可选
      `int? TcpFlowCapacity`（JSON `tcpFlowCapacity`，未知属性拒绝语义不变）。
- [x] `TryValidate`：null → 默认 4096；`< 1 || > 8192` → 拒绝（错误信息含范围）；
      `> 4096` → 收集 warning（不阻断）。
- [x] `ValidatedConfiguration`：归一化 `int TcpFlowCapacity` 只读属性（尾部位置参数，
      benchmarks 既有构造零改动）+ `Warnings` 集合。
- [x] `WinForward validate` 输出包含解析值与警告行。
- [x] `tests/.../FlowAndConfigurationTests`：全矩阵 11 cases
      （null/1/4096/4097 警告/8192/0/-1/8193 拒绝/字符串/浮点 parse 拒绝）。

验证: `dotnet test -c Release --filter "FullyQualifiedName~Configuration"`。

### Step 2: 容量派生与 gate `[已完成 2026-08-28]`

- [x] `TcpProxyCoordinator` 构造加 `int? capacity = null`（null → 16_384 兼容），
      会话上限用它；`TcpRedirectTable` 同样加参（null → 16_384）。
- [x] `Program.cs` 接线：`redirectTable` 与 coordinator 均传
      `validated.TcpFlowCapacity`（单一来源）；`run` 启动期输出配置警告。
- [x] 超限计数：Interlocked long + info 级摘要 `tcp.redirect.capacity`
      （budget/rejectedTotal/rejectedSinceLastSummary），挂 IdleExpirySweeper 既有
      tick，无新定时器。
- [x] 测试：超限 → Blocked + `reason=capacity` + 计数递增 + 摘要字段断言 +
      静默期不输出；null 默认回归（既有测试零改动仍绿）。

验证: `dotnet test -c Release`（全量）。

### Step 3: 文档与 validate 手测 `[已完成 2026-08-28]`

- [x] README `Configuration` 节：`tcpFlowCapacity` 说明（默认 4096、范围 1..8192、
      >4096 警告语义、收紧为有意行为）+ JSON 样例补字段。
- [x] `WinForward validate --config` 三态手测（实现子代理 sanity）：
      省略 → `tcpFlowCapacity: 4096` exit 0；6000 → valid + warning exit 0；
      9000 → 拒绝 exit 1。

### Step 4: Windows smoke（可选，借 wf-batch-test 部署）

- [ ] 交叉构建 + 部署（复用 datapath 任务的流程），短连接风暴（PS 循环 curl
      经代理）观察：info 计数出现、无 `listenerAllocation`、无 warn/error。

## 风险文件与回滚

| 提交 | 触碰 | 回滚 |
|------|------|------|
| Step 1-2 | ConfigurationModels / TcpProxyCoordinator / TcpRedirectTable / Program / 测试 | 整体 revert（单提交） |

## start 前检查

- [x] prd.md 收敛（研究结论已回填）
- [x] design.md 定稿（否决项记录）
- [x] implement.jsonl / check.jsonl 真实条目
- [ ] 用户批准最终规划摘要

### Step 1-3 验证记录（2026-08-28）

- `dotnet build -c Release`：0 警告 0 错误（修复两处 Meziantou 规则冲突：
  MA0015/MA0006）。
- `dotnet test -c Release`：332/332 通过（既有 319 零改动 + 新增 13：配置矩阵 11 +
  coordinator 2）。
- `dotnet format --verify-no-changes`：7 个改动文件干净（存量 7 个报错文件不在
  本次 diff）。
- validate 三态实测：省略 → exit 0 含 `tcpFlowCapacity: 4096`；6000 → exit 0 +
  warning；9000 → exit 1 拒绝。
- trellis-check：PASS，零修复（配置契约/单一来源/gate 行为/跨层一致性/全量验证
  五项全过）。
