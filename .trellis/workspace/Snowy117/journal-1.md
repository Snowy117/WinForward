# Journal - Snowy117 (Part 1)

> AI development session journal
> Started: 2026-10-20

---



## Session 1: WFP MVP 端到端跑通、SOCKS5 上游与性能实测
<!-- trellis-session: v=2 fp=a9dc27379dc71ba2 -->

**Date**: 2026-10-21
**Task**: WFP MVP 端到端跑通、SOCKS5 上游与性能实测
**Branch**: `main`

### Summary

把 WinForward 从 C#/NDISAPI 包级拦截迁到 C++/WFP 连接级重定向，在本仓库内跑通最小闭环。核心难点与结论：redirect 层不执行拦截（block 必须编译成 AUTH_CONNECT 的 BLOCK filter）；auth 层 callout 终止型 PERMIT 会截胡 Windows 防火墙；只挂 V4 会漏 dual-mode socket；进程选择器只能用 ALE_APP_ID 且上限 511 字节；fallback=block 会让整表静默失效。验证：门禁 GATE-RESULT=PASS（242 条断言），A1-A9 全过，10 轮带流量卸载循环池计数守恒；16/16 驱动变异全红，19/19 SOCKS5 变异全红，ABI 单一来源由读头文本的一致性测试锁定。性能：480 Mbps 达成，控制器 81.3% 单核（基线 154.0%，-47.2%），其中 74.3% 是内核态；内存 privateBytes p50 13.31MB；延迟经三段式分解后确认靶场底噪 3.8ms，跨 campaign 的 11.4% 差异没有分辨力，已撤回该结论。遗留：REL 臂与全机 CPU 计数（验证逐连接成本）、NativeAOT 复测、Proxifier 未安装无法同框测量。

### Git Commits

| Hash | Message |
|------|---------|
| `b4bbae2` | feat(core): 初始化仓库并落地规则 blob 与匹配器 |
| `e3d4193` | feat(driver): Phase 0 探针验证 WFP 重定向 |
| `d4e91b8` | feat(userland): C# 规则编译器与跨语言 ABI 差分测试 |
| `42bc945` | feat(driver): 规则表、V6、防循环闸与 block filter |
| `257a6b5` | feat(userland): 控制器、中继与端到端闭环 |
| `8237430` | docs(perf): MVP 首轮 THRU 性能数据 |
| `5289be7` | chore(plan): 批次台账收尾 |
| `e066e3b` | feat(userland): SOCKS5 上游与跨机吞吐复测 |
| `2a983a1` | docs(perf): 内核态 CPU、内存、延迟与 Proxifier 对照 |
| `a9034e1` | docs(perf): 延迟三段式分解，撤回上一节的跨 campaign 延迟结论 |

### Status

[OK] **Completed**
