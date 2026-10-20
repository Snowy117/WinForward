# 执行计划 — WFP MVP

## 批次

- [x] B1 Phase 0 探针（`wfp-phase0-probe`）—— 三条验证成立，commit `e3d4193`
- [x] B2 可移植核心（`wfp-portable-core`）—— 58 个用例，commit `b4bbae2`
- [x] B3 驱动规则表与 IOCTL（`wfp-driver-rules`）—— 16/16 变异全红，commit `42bc945`
- [x] B4a C# 规则编译器与差分测试（`wfp-csharp-blob`）—— 46 用例 + 480 条决策断言，commit `d4e91b8`
- [x] B4b C# 控制器与中继（`10-21-wfp-userland`）—— E2E 48 条断言，commit `257a6b5`
- [x] B5 端到端与验收 —— 全量门禁 `GATE-RESULT=PASS`；A1–A9 逐条通过；10 轮带流量卸载循环，池计数 `3390→3410` 且 `outstanding=0`
- [x] B6 性能初测 —— 480 Mbps 达成，`WinForward.Controller` 90.4% 单核，见 `research/perf-mvp.md`，commit `8237430`

> B6 只把原仓库当**测试工具**使用，不引入任何生产依赖（与 D8 不冲突）。E2E harness 是 C#，`WinForward.E2E` 的驱动方式是按旧产品的配置与进程形态设计的，能跑哪一部分、怎么接，等 MVP 跑通后再评估。

## 每一批的闭环

规划 →（按需审查）→ 实现（子代理）→ 检查（另一个子代理）→ 修复 → 文档对齐 → 主会话提交。

## 纪律

- 主会话不写代码；实现、检查、修复都派子代理。
- 检查者不得是实现者，必须干净上下文。
- 并发仅在文件集不相交时使用；并发后补一次结构审查。
- 每批结束：门禁通过（构建 / 测试 / format / tidy）、文档与代码一致、已提交、工作区干净。
