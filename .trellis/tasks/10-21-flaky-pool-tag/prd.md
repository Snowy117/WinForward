# 池标签断言偶发失败

> 来源：B6 之后的最终验收（2026-10-21）。这是既有测试的缺陷，不属于质量门禁那批交付。

## 现象

`tools/windows/gate.ps1` 完整跑三遍：第一遍 `ASSERT FAIL: 池标签可读（PoolTags.Find 返回 null）` → `rules 套件 (exit=6)` → `GATE-RESULT=FAIL failures=1`；第二遍 248 条断言全过、`GATE-RESULT=PASS`；第三遍待记录。输入完全相同的两次运行结果不同。

## 定位

`tools/wfprobe/RuleSuite.cs:686`：测试先 `config.Enabled = 0`、`await Task.Delay(settleMs)`，再 `PoolTags.Find(Protocol.PoolTag)` 读系统池标签。驱动在禁用时会把规则表等带标签的池内存释放掉；一旦释放完成，该标签会从 `SystemPoolTagInformation` 里消失，查询返回 null，断言失败。

也就是说这条断言与驱动清理存在竞态：它在验证"池标签可读"，但读的时刻可能已经没有该标签的存活分配。

## 修复方向（待定，需先确认语义）

1. 把查询挪到禁用之前（此时规则表必然存活），只验证"标签在系统里可见"；
2. 或在 null 时改为 Skip + 说明，而不是 FAIL；
3. 或断言"禁用后 outstanding 必须为 0"，用禁用前的快照做基准，不要求标签仍然可见。

## 验收

- 连续跑 5 次 `gate.ps1`，`rules` 套件 5/5 通过，且看不出时序依赖。
- 不降低其他断言的强度（分配/释放次数、outstanding=0 这些必须保留）。

## 备注

- 这是门禁可信度问题：偶发红的门禁会让人习惯性重跑，等于没有门禁。
