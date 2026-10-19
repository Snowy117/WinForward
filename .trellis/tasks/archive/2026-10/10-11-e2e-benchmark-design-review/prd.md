# E2E benchmark 设计审查

## Goal

判断 `benchmarks/WinForward.E2E` 这套端到端对比基准**作为一件测量仪器和一次实验**能支持什么决定、无法支持什么结论，找出其中的结构性设计缺陷，并给出**在现有两台机器（Linux 靶机宿主 + 一台 Windows VM）内可落实**的整改方向与优先级。

这是一次审查，不是一次改造：本任务不改 harness、不改分析器、不重跑 campaign。

## Why

这套基准已经在被用来支撑对外结论（"WinForward 对比 ProxiFyre / Proxifier / ProxyBridge"）和对内结论（性能与稳定性改进是否有效）。一轮 campaign 的成本是数小时的独占机器时间，而**它的问题清单与它的结论清单并不对齐**：如果设计上无法支持某个结论，那么花掉的时间不只是浪费，还会把"测不出来"渲染成"测出来是零"、"分辨率不足"渲染成"无差异"。

项目自己的规范已经写明这套纪律（`.trellis/spec/backend/benchmark-methodology.md`：gate 必须被证明能够失败；行必须测到它命名的那条路径）。这次审查要回答的是：**这套基准自己守住了这条纪律吗？**

## Scope

覆盖整条链路，而不只是 harness 代码：

| 对象 | 路径 |
|---|---|
| harness（client / target / wire / lanes / arms） | `benchmarks/WinForward.E2E/` |
| plan 三套与六份产品配置 | `benchmarks/WinForward.E2E/scripts/{plans,plans-short,plans-windows,configs}/` |
| campaign 驱动与本地胶水 | `benchmarks/WinForward.E2E/scripts/orchestrator.ps1`、`deploy-campaign.sh`、`start-targets.sh`、`publish-campaign.sh`、`wf.sh` |
| 分析器与报告契约 | `benchmarks/WinForward.E2E.Analysis/` |
| 设计文档 | `benchmarks/WinForward.E2E/README.md`、`benchmarks/WinForward.E2E.Analysis/README.md`、`benchmarks/research/2026-10-06-proxifier-comparison-design.md`、`.trellis/tasks/10-06-e2e-competitor-benchmark/design.md` |
| 本机运行手册 | `benchmarks/WinForward.E2E/AGENTS.local.md` |

## Non-goals

- **不产出实现级 bug 清单**：`.trellis/tasks/10-06-e2e-competitor-benchmark/research/harness-audit.md` 已有 21 条 + 披露项。重复的只做交叉引用；但"这类 bug 为什么能长期存在"属于设计层，要写。
- 不修改任何代码、不重跑 campaign、不动 `src/`。
- 不做产品侧性能优化建议。
- 不推翻整套基准重做。

## Constraints

1. **只有两台机器**：当前 Linux 宿主（靶机所在）与远端一台 Windows VM。
   **禁止**任何"增加机器 / 更换环境 / 搭建更复杂的网络拓扑来模拟真实网络"的建议——提了也落实不了。
   所有整改建议必须在这两台机器、现有 `AGENTS.local.md` 拓扑内可执行。
2. 每条发现必须有证据锚点：`file:line`、plan JSON 字段、脚本片段或文档原文引用。没查实的写"未能证实"，不许推测性断言。
3. 每条发现必须能被反驳：给出"什么证据会推翻它"。
4. 与 `harness-audit.md` 不重复叙述。

## Requirements

- **R1 四条轴都要覆盖**：统计与决策模型 / 仪器有效性与归因 / 实验设计与公平性 / 可复现性与运行层。
- **R2 每条发现回答三件事**：证据是什么；它让**哪个结论**不可信；不管它读者会得到什么错误印象。
- **R3 分类**：`设计缺陷`（改设计才能解决）／`只能如实披露`／`已被 harness-audit 覆盖`。
- **R4 两张清单**：这套基准**现在能支持的决定**与**不能支持的决定**，各自写明前提。
- **R5 建议按性价比排序**：估算人时与机器时间，标注"是否需要重跑 campaign"，并说明它消除了哪条发现。
- **R6 文档与实现漂移要单独成节**：设计文档承诺过、但没落实或实现得不一样的条目（例如 `BASE-singbox`），本身就是设计缺陷的证据。

## Acceptance Criteria

- [x] `research/` 下四条轴各一份文档（`design-stats-and-decisions.md`、`design-instrument-validity.md`、`design-experiment-fairness.md`、`design-reproducibility.md`），另有一份汇总的 `design-problems.md`（问题清单 + 排序后的建议）
- [x] 每条发现都带证据锚点；三份轴文档带"被推翻的条件"，汇总文档在"能/不能支持"两张表里给出前提
- [x] "能支持 / 不能支持的决定"两张清单成文（`design-problems.md` 第 6 节）
- [x] 建议清单中没有任何一条需要新机器或新环境（A/B/C/D 四组共 24 条，全部限定在现有两台机器内）
- [x] 与 `harness-audit.md` 的 21 条无重复叙述，交叉引用到位（第 8 节：四处需要对齐，含三条对 audit 的修正）
- [x] qmazon review 后，确认哪些发现转成独立的整改任务——2026-10-18 全部确认，决策见 `research/decisions.md`，整改任务为 `.trellis/tasks/10-18-e2e-benchmark-remediation/`

## Out of scope for this turn

把发现变成代码改动。审查完成后由用户决定分派哪些整改任务。
