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
