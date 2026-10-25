# 质量收尾遗留项

> 来源：质量门禁与工具链现代化（B1–B6，父任务 `10-21-quality-gates-modernization` 已归档）的最终验收。两件事都是那批交付暴露出来的，但不在其范围内。

## R1 · 池标签断言偶发失败

### 现象

`tools/windows/gate.ps1` 完整跑三次：第一次 `ASSERT FAIL: 池标签可读（PoolTags.Find 返回 null）` → `rules 套件 (exit=6)` → `GATE-RESULT=FAIL failures=1`；第二、三次 248 条断言全过、`GATE-RESULT=PASS`。输入相同，结果不同。

### 定位

`tools/wfprobe/RuleSuite.cs:686`。测试先 `config.Enabled = 0`、`await Task.Delay(settleMs)`，再调 `PoolTags.Find(Protocol.PoolTag)` 读系统池标签。驱动在禁用时释放规则表等带标签的池内存；一旦释放完成，该标签会从 `SystemPoolTagInformation` 消失，查询返回 null。

即：断言与驱动清理存在竞态，它验证的是"池标签可读"，但读取时刻可能已无该标签的存活分配。

### 修复方向（需先确认语义，再选）

1. 查询挪到禁用之前（此时规则表必然存活），只验证"标签在系统里可见"；
2. null 时改为跳过并说明，而不是 FAIL；
3. 用禁用前的快照做基准，断言"禁用后 outstanding 为 0"，不要求标签仍然可见。

### 验收

- 连续跑 5 次 `gate.ps1`，`rules` 套件 5/5 通过，且不依赖执行时序。
- 不降低其他断言强度：分配/释放次数、`outstanding = 0` 必须保留。

## R2 · CodeQL 基线 14 条评审

### 现状

`tools/windows/codeql.ps1` 在未修改的驱动上跑 `microsoft/windows-drivers:recommended.qls`，基线 14 条：

| 规则 | 条数 |
|---|---|
| `cpp/drivers/unprobeddereference` | 7 |
| `cpp/drivers/illegal-field-access-2` | 4 |
| `cpp/drivers/wdk-deprecated-api` | 2 |
| `cpp/usermodememoryoutsidetry` | 1 |

### 要求

- 逐条给出结论，三选一：**修**（改代码）、**抑制**（CodeQL 支持的抑制形式，附理由）、**接受**（写进 `src/driver/README.md` 或 `tools/windows/README.md` 的已知结果，说明为什么不是问题）。
- 优先级建议：`unprobeddereference`（用户态指针未探测就解引用，在内核里就是蓝屏）> `usermodememoryoutsidetry` > `wdk-deprecated-api`（`ExAllocatePoolWithTag` 迁移到 `ExAllocatePool2`）> `illegal-field-access-2`。
- 修完之后重跑 `codeql.ps1`，基线应下降；若维持不减，必须逐条写明接受理由。

### 验收

- 14 条全部有归属，记录在任务文档里（规则 / 位置 / 结论 / 理由）。
- 修过代码的规则在重跑后不再出现；`tools/check.sh --full` 与 `tools/windows/gate.ps1` 仍通过。
- 若引入抑制，抑制必须写在被抑制的代码或查询调用处附近，并附理由——不接受集中白名单。

## 约束

- 不降低现有门禁强度（`check.sh` 的 8 项、`gate.ps1` 的 248 条断言）。
- 涉及驱动的改动必须走 `gate.ps1` 全流程验证（构建、签名、安装、套件、卸载）。
- 同一时刻只允许一个代理使用 Windows VM。
