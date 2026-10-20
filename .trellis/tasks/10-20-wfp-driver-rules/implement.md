# 执行计划 — 驱动规则表与 IOCTL

1. `rule_table`：池内存持有 + rundown + 原子替换 + 失败不动旧表。
2. IOCTL：下发 / 回读 / 计数器。
3. classify 改造：`connection_view` 构造 + `match()` 调用 + 三个动作分支。
4. V6 callout 注册与双栈 classify。
5. 四道防循环闸，逐道做负向验证。
6. 计数器接线。
7. 测试程序扩展：下发真实 blob、三条规则场景、dual-mode IPv6 场景、原子替换压力。
8. 门禁：驱动构建 0 warning、签名、加载、卸载；`src/driver` clang-format 干净；有效行 ≤ 500。
9. 结论与遗留写进本任务 `research/`。

每道闸、每条规则分支都要有负向验证——破坏它，验证必须变红。
