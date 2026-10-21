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


## Session 2: 质量门禁与工具链现代化（B1–B6）
<!-- trellis-session: v=2 fp=dfc28958f6ef5bb4 -->

**Date**: 2026-10-21
**Task**: 质量门禁与工具链现代化（B1–B6）
**Branch**: `main`

### Summary

建立宿主侧单一门禁入口（格式/有效行/cppcheck/禁止模式/clang-tidy），删除死符号 block_classification；驱动显式 /W4 + /WX 并接入 PREfast；Windows 脚本去机器耦合（tools/windows + WfBuild.psm1，删除 sync.sh，NuGet 改用 nuget.org）；变异锚点改为区域标记并修掉 -OnlyIndex 假绿；禁止模式检查与只动 local 的提交前钩子；CodeQL 深度档接入并加抽取自检。宿主机禁 --full 8 PASS；Windows 门禁 248 条断言、三次跑中两次 PASS，偶发失败已定位为既有池标签断言竞态并另立任务。

### Git Commits

| Hash | Message |
|------|---------|
| `0b34495` | feat(quality): 宿主质量门禁入口与 cppcheck 死符号检查 |
| `0943c98` | feat(driver): 警告即错误与 PREfast 基线清零 |
| `89fa2dc` | refactor(tools): Windows 侧脚本去机器耦合 |
| `765363c` | refactor(mutation): 变异锚点改为区域标记 |
| `980472f` | feat(quality): 禁止模式检查与提交前钩子 |
| `e488a8d` | feat(tools): CodeQL 深度档接入 |

### Status

[OK] **Completed**

### Next Steps

- 处理 10-21-quality-followups：R1 池标签断言与驱动清理存在竞态；R2 CodeQL 基线 14 条逐条评审。


## Session 3: 质量收尾：池标签断言与 CodeQL 基线清零
<!-- trellis-session: v=2 fp=6355d0cbc96c52f6 -->

**Date**: 2026-10-21
**Task**: 质量收尾：池标签断言与 CodeQL 基线清零
**Branch**: `main`

### Summary

父任务 10-21-quality-gates-modernization 留下的两件事都收掉了。池标签断言的根因不是禁用时清理的时序竞态：WfPr 条目在第一次分配时才创建、本次启动内归零后仍保留（实测驱动卸载后仍 39/39/0），失败只发生在「本次启动还没分配过该标签」的第一轮。RuleSuite 现在把 null 基线按零参与差值，分配/释放/outstanding 三条断言保留，窗口结束仍读不到才显式报错。CodeQL 基线 14 条全部按「修」处置：弃用 API 换 ExAllocatePool2（最低系统版本抬到 Win10 2004）、DriverEntry 补 DRIVER_INITIALIZE 角色类型声明、measure IOCTL 改为局部结构生成再整体拷回；重建数据库后 14 → 0。重启 VM 后 gate.ps1 连续 5 次 PASS（每轮 249 条断言），宿主 check.sh --full 8 项全过。

### Main Changes

- R1：PoolTags.Zero 零基线 + RuleSuite 差值断言；首次运行不再偶发 FAIL，rules 套件断言 3 条变 4 条
- R2：ExAllocatePoolWithTag → ExAllocatePool2(POOL_FLAG_NON_PAGED) ×2；驱动 README 写明最低 Windows 10 2004
- R2：补 extern "C" DRIVER_INITIALIZE DriverEntry 声明（定义处 SAL 对齐，避免 C28252），illegal-field-access-2 ×4 消失
- R2：ioctl_measure_process_path 改局部 process_path_measurement 再整体拷回，unprobeddereference ×7 + usermodememoryoutsidetry ×1 消失
- 规范：cpp-conventions §5.5（WDK 池分配 / 角色类型 / IOCTL 输出）与 §9.6（池标签语义）

### Git Commits

| Hash | Message |
|------|---------|
| `3e162a7` | fix(wfprobe): 池标签断言按零基线处理本次启动首次运行 |
| `50ea622` | fix(driver): 逐条清零 CodeQL 基线 14 条 |
| `b25c6fe` | docs(spec): 补充 WDK 池分配、DriverEntry 角色类型与池标签语义 |
| `af7fe77` | chore(task): 记录质量收尾遗留项的验证结果 |
| `8e4e70b` | chore(task): 任务标题覆盖 R1 与 R2 |

### Testing

- [OK] 重启 VM（WfPr 条目不存在）后 gate.ps1 -Remote 1.2.3.4:19003 连续 5 次 GATE-RESULT=PASS，249 条断言/轮全过
- [OK] codeql.ps1 重建数据库：FINDINGS results=14 → PASS（0 条），抽取自检 successes=1 / failures=0
- [OK] tools/check.sh --full：8 PASS / 0 FAIL；提交前 tools/check.sh --staged 通过
- [OK] 5 轮门禁后池标签无泄漏：WfPr 65/65/0、WfTr 920/920/0，驱动已卸载

### Status

[OK] **Completed**

### Next Steps

- 无：父任务的遗留项已清空；后续新批次另立任务


## Session 4: Host UDP 路线 A 实验验证
<!-- trellis-session: v=2 fp=9d8321e092206e8c -->

**Date**: 2026-10-21
**Task**: Host UDP 路线 A 实验验证
**Branch**: `main`

### Summary

用 wfprobe 实验探针在 WinLTSC 上验证 WFP UDP local redirect：ALE_CONNECT_REDIRECT 对 connected/unconnected 均生效，IP_WFP_REDIRECT_CONTEXT cmsg 逐 datagram 带回原始目的地；connected 客户端接受 relay 普通回包，无需伪造源或注入。结论：路线 A 可行，产品化另立任务。

### Git Commits

| Hash | Message |
|------|---------|
| `c3ce6bf` | feat(driver): 验证 UDP local redirect 与 per-datagram context |

### Status

[OK] **Completed**


## Session 5: Host UDP 正式接入驱动
<!-- trellis-session: v=2 fp=host-udp-production-2026-10-22 -->

**Date**: 2026-10-21 ~ 2026-10-22
**Task**: Host UDP 正式接入驱动：规则化 redirect、per-datagram context 与生命周期
**Branch**: `main`

### Summary

把路线 A 的 UDP local redirect 从实验探针转成正式能力：规则层接受 UDP，classify 按“本地端点+远端目的地”关联一次，redirect context 携带 protocol=17 与原始目的地；block 在 ALE_AUTH_CONNECT 用 6/17 两条 filter 实现，UDP 为静默丢包；控制面 ABI 升到 v3，增加 protocol 与 4 个 UDP 计数器；删除全部实验 UDP 脚手架，新增正式 `wfprobe udp-rules` U1–U13 与门禁。

### Key Decisions

- UDP block 接受连接级静默丢包：send 成功、对端收不到、recv 超时；不伪造错误码。
- `protocol=any` 覆盖 TCP+UDP；`any`/fallback block 按 6/17 各一条 filter，避免 ALE_AUTH_CONNECT 误拦 ICMP。
- 同 listener socket 回包复用入站重定向关联，不重新 classify；`self_process_ids` 用于中继新开 socket 的出站。
- connected UDP 的 redirect context 由 WFP 持有，本 boot 不释放（1/10/60/180/300s 实测），U12 只对 unconnected 做严格池断言，connected 记 LIMIT。
- ABI v3 为破坏性变更，驱动/用户态/wfprobe 同批升级。

### Git Commits

| Hash | Message |
|------|---------|
| `d0f4793` | chore(task): 记录 Host UDP 规划与 spike 结论 |
| `e078003` | feat(policy): 规则 blob 与编译链支持 UDP |
| `c2b0c4f` | chore(task): 记录 Host UDP B1 交付与 Q1 裁决 |
| `a3af7b2` | feat(driver): ABI v3 与删除 UDP 实验脚手架 |
| `75e790a` | chore(task): 记录 Host UDP B2a 交付与 B2 拆分 |
| `3ac192a` | feat(driver): UDP 正式进入 classify 与 block filter |
| `9c26acb` | chore(task): 记录 Host UDP B2b 交付与 VM 崩溃证据 |
| `dd15b5d` | feat(verification): 添加 Host UDP UdpSuite 与门禁 |
| `2b4cd83` | docs: 更新 Host UDP 文档与 WFP 架构 |
| `ed0c8ad` | chore(task): 记录 Host UDP B3 交付与最终门禁 |
| `c889d4e` | chore(task): 归档 Host UDP B0-B3 与父任务 |

### Testing

- [OK] 宿主 `tools/check.sh --full`：8 PASS / 0 FAIL；C# 134/134；ctest 66/66
- [OK] VM 干净启动 `gate.ps1`：GATE-RESULT=PASS，507 断言；同 boot 第二次 PASS，505 断言
- [OK] `udp-rules` U1–U13：255–257 断言，0 失败；U7 any/fallback block 下 ICMP 2/2 通
- [OK] `mutation.ps1 -OnlyIndex 16..21`：6 条 UDP 变异全红，源码逐字节还原
- [OK] 同 boot 重复 gate：RuleSuite 改基线差值后稳定 PASS；卸载 WfPr 无新增 outstanding

### Known Limits

- connected UDP context 在本 boot 内不释放，`sc stop` 也不回收；这是 WFP 行为，文档与 U12 均按 LIMIT 记录，未伪绿。
- VM 期间出现过一次 0x11D/0xc000021a；dump 栈在启动早期 ETW，崩溃模块无 wfprobe，证据不支持归因驱动。
- `AGENTS.md` 被仓库 `.gitignore` 忽略，内容已更新但只在本机生效，未 force-add。

### Status

[OK] **Completed**

### Next Steps

- 无：Host UDP 的驱动、ABI、验证、文档与归档均已完成；后续若 WFP 释放 connected context，可收紧 U12。

