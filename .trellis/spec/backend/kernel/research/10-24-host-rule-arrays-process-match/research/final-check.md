# 最终质量检查（trellis-check）

> 检查对象：`feat/host-rule-arrays-process-match` 工作树（未提交）。
> 检查日期：2026-10-24。检查范围：任务文档、全部代码/测试/工具/文档改动、`tools/check.sh --full`、
> `tools/userland/verify-gates.sh`，以及已保存的 WinLTSC gate 输出。
> 本轮没有重跑 Windows gate（按任务要求），只核对了
> `~/.qoder-cn/tmp/.../session-146077b7-.../b33lhyv9s.output` 的内容与时间戳。

## 0. 结论摘要

**通过**。未发现代码阻断项：blob v2 布局、匹配语义、block 边界/预算、C# 与驱动的顺序校验
算法一致；Linux 全量门禁 8/8 PASS，变异门禁 25 红 / 4 绿；保存的 Windows gate 为
`GATE-RESULT=PASS`，驱动 SHA-256、vm-check 118/0、E2E 48 与 verification.md 一致。

保留意见：

- A11 的“4096 条空 permit 表”在当前 v2 的 64 KiB 上限下不可构造（约 2046 条），仓库也没有这个
  既有用例。verification.md 原先声称“由既有容量用例覆盖”不成立，已改为如实记录；PRD 的数值仍需
  产品/计划侧修订（`plan-review.md` P2 #13）。
- 测试质量有两处“应该修”：C++ 负向夹具中有一条同时违反两条检查；C++ 侧除
  `selector-utf8-guard` 外，新增的 `try_open_blob` 检查没有逐条“删检查后夹具变红”的变异门禁。
  它们不改变当前代码的正确性判断，但会削弱回归保护。
- `src/common/rule_blob.h` 恰好 500 有效行（上限 500），`src/driver/rule_table.cpp` 495 有效行；
  拆分是后续维护项，不是本批阻断。

## 1. 本轮亲自执行的命令与结果

| 命令 | 结果 |
|---|---|
| `tools/check.sh --full` | **8 PASS / 0 FAIL / 0 SKIP，121.617 s**；`wf_check` PASS；ctest **84/84**（含 `Differential.DecisionsMatchExpected`）；C# 288 由独立 `dotnet run -- test` 与保存 gate 的第 1127 行确认 |
| `tools/userland/verify-gates.sh` | **29 条检查全部 PASS**：25 条变异红、4 条基线绿；退出码 0 |
| `dotnet run --project src/userland/WinForward.Userland.Tests -c Release -- abi` | `total=29 passed=29 failed=0` |
| `tools/check.sh --fast`（文档修订后复跑） | **5 PASS / 0 FAIL / 0 SKIP，7.635 s** |
| `python3 src/userland/scripts/check-effective-lines.py src/common/rule_blob.h` | `500`，通过（恰好在上限） |
| 手工变异复验（见 §5） | 5 条新增 C# 变异均以**测试失败**（非编译失败）变红，源码已逐字节恢复 |
| `git diff --stat` | 59 个已跟踪文件，3713 insertions / 2453 deletions；另有本任务新增文件（未跟踪） |
| `windows-vm status` / `sc.exe query wfprobe` | VM 运行中；`wfprobe` 服务**不存在**（驱动已卸载）；`C:\workspace\WinForward.Kernel` 仍保留 build/cmake 产物 |

没有重跑 `pwsh -File tools/windows/gate.ps1`，也没有重跑 `tools/windows/mutation.ps1`。

## 2. 保存的 Windows gate 输出核对

来源：`/home/paff/.qoder-cn/tmp/-home-paff-Projects-WinForward-Kernel/tool-outputs/session-146077b7-22b7-407f-84ab-d7f3de89e6da/b33lhyv9s.output`（mtime 2026-10-24 11:52）。

| 断言 | 输出位置 | 实际值 |
|---|---|---|
| 驱动 SHA256 | 第 33 行 | `4027481512319429C772D940208D30A1CA20297EB98F7CBEA7AED8AD8E6D1C39` |
| `appid` | 第 93 行 | `ASSERT PASS: appid 归一化可用` |
| `rules` | 第 374 行 | `ASSERT PASS: rules 套件 (exit=0)` |
| `forwarded-rules` | 第 471 行 | PASS |
| `udp-rules` | 第 757 行 | PASS |
| `selftest` V4 / dual | 第 775 / 792 行 | PASS |
| `gates` | 第 816 行 | PASS |
| 用户态单测 | 第 1127 行 | `[tests] total=288 passed=288 failed=0` |
| vm-check | 第 1249 行 | `checks=118 failures=0 verdict=PASS` |
| E2E | 第 1872 行 | `E2E-RESULT=PASS checks=48` |
| 卸载 | 第 1883–1887 行 | WfPr/WfIj/WfTr outstanding 断言 PASS |
| 最终结论 | 第 1888 行 | `GATE-RESULT=PASS` |

与 verification.md 的 A10 行一致（SHA、118/0、48、时间 11:52）。gate 里的 `rules` 套件还包含
parity 用例：文件名 255–264 行、精确路径 266–282 行、目录 284–296 行、数组 297–342 行、
block 348–365 行，均 PASS。

## 3. PRD A1–A11 核对

| # | 验收项 | 可复现证据 | 结论 |
|---|---|---|---|
| A1 | 五个数组编译成 1 条 blob 规则；驱动回读规则数相等 | `CompilerTests.OneRuleOneBlobRule`（C# 288/288）；gate 332–336 行（matrix 规则数=1、事件 `MatchedRule=0`） | 通过 |
| A2 | 文件名 `dns.exe` 命中/`dns` 不命中 | `tests/host/matcher_test.cpp` 文件名用例；gate 255–264 行（`wfprobe.exe`/`not-wfprobe.exe`/无扩展名） | 通过 |
| A3 | 目录直接子文件、深层后代、`MyApp2` 不命中 | `matcher_test.cpp` 的 `tools` vs `tools2`；gate 284–296 行（直接子文件、同名前缀目录、深层后代） | 通过 |
| A4 | 精确路径只命中该文件，同名文件在其他目录不命中 | `matcher_test.cpp` 精确路径用例；gate 266–282 行（原文件命中、同名副本 fallback、副本规则反向） | 通过 |
| A5 | 同字段 OR、字段间 AND、顺序与 `rule_index` | 差分 42×15；gate 297–342 行；ctest `Differential.DecisionsMatchExpected` PASS | 通过 |
| A6 | block 精确路径数组生效；文件名/目录前缀编译期失败；驱动整块拒绝 | `CompilerSelectorTests`、gate 348–365 行（两条路径各命中、block+文件名/目录 error=87、generation/规则数不变） | 通过 |
| A7 | 现有顺序歧义拒绝，消息带两条序号 | `RuleOrderTests`（288/288）；gate 97–98 行（冲突规则 `#0`/`#1`、驱动拒绝）；vm-check 顺序矩阵 | 通过 |
| A8 | fallback 矛盾、端口颠倒、族/网段不一致、511 继续生效 | `CompilerTests`/`InvalidInputTests`/`ConfigurationTests`（288/288）；`ProcessPathTests` 511/512；`ConfigurationTests.ReversedPorts` | 通过（端口颠倒/端口 0 现由配置层带 JSON 路径拒绝，仍算生效） |
| A9 | C# blob 与 C++ `try_open_blob`+`match` 逐条一致 | `check.sh --full` 的 ctest 84/84，含 `Differential.BlobsOpen` / `DecisionsMatchExpected` | 通过 |
| A10 | 驱动六套件全绿 | 保存的 gate 输出：appid/rules/udp-rules/forwarded-rules/selftest/gates 全 PASS，`GATE-RESULT=PASS` | 通过 |
| A11 | 4096 条空 permit 表与 E2E 不回归 | E2E 48 checks PASS；**4096 子项没有证据**：v2 上限约 2046 条，仓库无该用例 | **部分通过**（见问题 4） |

## 4. 设计一致性

- **v2 布局**：header 56 / rule 32 / selector 8 / port 4 / network 20；`rule_blob.h` 对每个结构有
  `sizeof`、`alignof`、逐字段 `offsetof` 的 `static_assert`；`LayoutTests` 锁 C# 的 `Pack=4`、尺寸与
  偏移；`BlobAbiConsistencyTests` 从 `rule_blob.h` / `probe_ioctl.h` 源码文本把版本、magic、尺寸、
  动作、selector kind、掩码、511/64 KiB/128 与 `RuleBlobLayout`、`PolicyLimits`、`Protocol.cs`
  三处互锁。**一致。**
- **`count == 0 -> first == 0`**：`is_valid_rule_range` 强制；C# `CompileRule` 与 wfprobe `Write`
  都写 0；负向夹具覆盖 process/network/port 三类。**一致。**
- **`\device\` 前缀**：C# 编译器、`try_open_blob`、wfprobe `ValidateSelector` 三处都要求大小写
  不敏感前缀、不以 `\` 结尾；C++ 负向夹具 `path-without-device-prefix` / `path-with-trailing-backslash`
  覆盖。**一致。**
- **控制字符 C0/DEL/C1**：`rule_blob.h` 的 `is_control_code_point` 拒绝 `<0x20`、`0x7F..0x9F`；
  C# `ProcessSelector.EnsureRepresentable` 同口径；C# 测试有 C0/DEL/C1 三条，C++ 有 C0/C1，
  **缺 DEL 0x7F 夹具**（见问题 7）。**代码一致，测试有缺口。**
- **511 / 64 KiB**：C# 在归一化后按 UTF-8 字节拒绝 >511；`try_open_blob` 解码时再检；驱动
  `rule_blob_max_size` 在 `allocate_rule_table` 限制；`ProcessPathTests` 511/512、`CompilerLimitTests`
  64 KiB 边界。C++ 只有 512 拒绝，**没有 511 接受夹具**（见问题 7）。**代码一致，测试有缺口。**
- **block 边界与预算**：C# `ValidateBlockBudget`、驱动 `block_filter_budget_valid`、
  `probe_install_block_filters`、wfprobe `RuleBlobOrder` 使用同一公式（layer × protocol ×
  process' × port' × network'，fallback=block 固定 4）；驱动对同形 blob 先 `block_expression_valid`
  再预算，失败时旧表/旧 filter 不变。**一致。**
- **顺序校验**：C# `RuleOrderValidator`、驱动 `rule_order_valid`、wfprobe `RuleBlobOrder` 的协议掩码、
  有效族集、端口区间、CIDR 前缀、文件名/路径/目录重叠语义逐条对应；vm-check 118 条差分含
  `tools` vs `tools2`、目录遮蔽后代、多端口、混合族、非 ASCII 路径。**一致。**
- **一条配置规则 = 一条 blob 规则**：C# 只按 `table.Rules` 生成 entries；数组是池区间；测试与
  gate 均断言规则数=1。**一致。**

## 5. 测试质量

- **C#**：数组 OR/AND（`ConfigurationArrayTests`、`ProcessCases`、`CompilerTests.OneRuleOneBlobRule`）、
  文件名/精确/目录（`CompilerSelectorTests`、`ProcessPathTests`）、`tools` vs `tools2`
  （`RuleOrderProcessTests`）、block 拒绝（`CompilerSelectorTests`）、预算 128/129
  （`CompilerLimitTests`）都有覆盖。
- **C++**：`matcher_test.cpp` 覆盖三种进程语义、大小写、非 ASCII、缺失路径、首个命中；
  `matcher_array_test.cpp` 覆盖五个字段的 OR/AND 与掩码；`rule_blob_validation_test.cpp` 有
  30 条表驱动 + 16 条特殊负向夹具；`differential_test.cpp` 精确锁定 42×15 和必需用例名。
- **差分**：42 组 × 15 连接，覆盖文件名、目录、精确路径、数组 OR/AND、混合族、多端口、协议数组、
  block 精确路径数组；ctest 84/84。
- **变异门禁**：`verify-gates.sh` 25 条变异全部红、4 条基线绿；本轮手动确认 5 条新增变异是
  **测试失败**而不是构建失败（详见 §5.1）。`if (false)` 在当前 `Directory.Build.props`
  （`TreatWarningsAsErrors=true`）下不会产生 CS0162 编译错误，5 条都命中对应 `[fail]`。
- **缺口**：C++ 侧只有 `selector-utf8-guard` 一条 `rule_blob.h` 变异（Windows `mutation.ps1`），
  其余新增的 selector/端口/族/对齐检查没有逐条反向证明；`CompilerLimitTests` 的协议/族预算
  用例不能单独证明协议因子（见问题 1–3）。

### 5.1 手动变异复验

对以下 5 条新增变异分别备份源文件、执行 `sed`、跑
`dotnet run --project src/userland/WinForward.Userland.Tests -c Release -- test`，再逐字节恢复
（`cmp` 全部 `RESTORED_OK`，`git diff --stat` 未变化）：

| 变异 | 退出码 | 失败测试 |
|---|---:|---|
| `RuleBlobCompiler.cs`：`if (validatePolicy && action == Block && !compiled.IsExactFile)` → `if (false)` | 1 | `compiler-selector/block 规则拒绝文件名选择器`、`...目录前缀选择器` |
| `RuleBlobCompiler.cs`：`if (total > MaxBlockFilters)` → `if (false)` | 1 | `compiler-limit/block filter 预算 128 接受 129 拒绝`、`...把协议与族展开计入` |
| `RuleBlobCompiler.cs`：`if (totalSize > RuleBlobMaxSize)` → `if (false)` | 1 | `compiler-limit/blob 总大小 64 KiB 边界` |
| `PolicyConfigurationDocument.HostSelectors.cs`：`if (values.Count == 0)` → `if (false)` | 1 | `config-array/五个字段空数组都拒绝` |
| `PolicyConfigurationDocument.HostSelectors.cs`：`if (port == 0)` → `if (false)` | 1 | `config-array/端口 0 拒绝并带 JSON 路径` |

## 6. 文档 / 代码一致

- `src/userland/README.md`、`src/driver/README.md`、`wfp-architecture.md`、`host-rule-blob.md`、
  `forwarded-rule-blob.md`、`index.md`、`csharp-conventions.md` 都已更新到 v2；没有仍在指引使用
  `FwpmAppIdNormalizer` / `WindowsAppId` 的现行文档（`FwpmGetAppIdFromFileName0` 只作为历史对比或
  wfprobe 夹具说明出现）。
- **已修**：`src/userland/README.md` 原先把“端口区间颠倒、端口 0”归到 `RuleBlobCompiler`，与实现
  （`PolicyConfigurationDocument.HostSelectors` 在配置层带 JSON 路径拒绝）不符；已改为“解析层做
  结构 + 取值校验，跨字段语义留给编译器”。
- **已修**：`verification.md` 的 A7/A8 测试数 278 → 288，A9 ctest 83 → 84，A11 的 4096 假覆盖
  改为如实记录，§5 的未勾选 mutation 项补上本轮证据。
- **未决**：`tools/wfprobe/AppId.cs:7` 仍写“用户态用同一套 API 拿到同一串”；产品的路径型选择器
  已经改走 `WindowsDevicePath` 句柄法。该文件是夹具，注释不影响行为，但会误导读者。
- **未决**：`src/common/rule_blob.h:38-39` 的 `rule_pool_index_any` 注释只提“未命中规则序号”，
  而 `forwarded_blob.h`/`forwarded_matcher.h` 仍把同一个 `0xFFFFFFFF` 当作网段池“不限”哨兵。
  结构审查的“注释里写清”一项没有落实。
- `host-rule-blob.md` §9 的测试表把 `BlobAbiConsistencyTests` 写成根目录文件（实际在
  `WinForward.Userland.Tests/Abi/`），只是路径简写，属排版问题。

## 7. 结构

- `src/common/rule_blob.h` / `matcher.h` 只用内核方言：`<cstddef>`/`<cstdint>`、无 STL 容器、
  无异常/RTTI、无分配；`matcher` 是纯函数，DISPATCH_LEVEL 可用。
- 驱动的可移植字节校验已收敛到 `try_open_blob`：`rule_table.cpp` 不再做 UTF-8/长度检查，只保留
  block 表达边界、filter 预算、fallback/顺序策略；`block_filters.cpp` 的 `RtlUTF8ToUnicodeN`
  是转换而不是第二套校验。
- 三份常量由 `BlobAbiConsistencyTests` 互锁；`rule_pool_index_any` / `Protocol.InvalidRuleIndex`
  没有纳入这组测试（影响很小，见问题 10）。
- **有效行**：`src/common/rule_blob.h` **500/500**（恰好在上限），`src/driver/rule_table.cpp`
  **495/500**。`check.sh` 通过，但下一次加校验就会超限；按结构审查建议拆
  `rule_blob_selector.h` / 迁移 forwarded 专用 helper 是后续项，不影响本批。

## 8. 问题清单

严重程度定义：**阻断** = 必须修才能合入；**应该修** = 不影响当前正确性，但削弱证据/可维护性；
**建议** = 后续优化。

| # | 严重程度 | 位置 | 问题与理由 |
|---|---|---|---|
| 1 | **应该修** | `tests/host/rule_blob_validation_test.cpp:216` | `selector-is-directory-invalid` 在**文件名** selector 上写 `is_directory=2`，同时违反“`is_directory <= 1`”和“`kind=file_name` 时 `is_directory == 0`”两条检查。删掉任意一条，夹具仍会被另一条拒绝，无法定位是哪条在生效。建议改为 path selector + `is_directory=2`（只违反范围检查），文件名 + `is_directory=1` 已有独立夹具。 |
| 2 | **应该修** | `tests/host/rule_blob_validation_test.cpp:420-443`；`tools/windows/mutation.ps1:309-321`；`tools/userland/verify-gates.sh:129-152` | C++ 负向矩阵只断言“被拒绝”；C++ 侧只有 `selector-utf8-guard` 一条 `rule_blob.h` 变异（`mutation.ps1`），其余新增的 selector 形态 / 端口 / 族检查没有“删掉该检查后对应夹具变红”的反向证明；`verify-gates.sh` 只覆盖 C# 编译期检查。结构审查 item 6 的 C++ 反向证明未完全落实。建议增加一个只编译 `tests/host` 的临时变异循环（或至少为新增 selector/端口/族检查各留一条可手动复验的记录）。 |
| 3 | **应该修** | `src/userland/WinForward.Userland.Tests/CompilerLimitTests.cs:48-51` | `BlockBudgetCountsLayers` 的第二段用 129 条路径 + 2 协议；129 条路径即使按 1 协议也已超 128，因此删掉协议因子测试仍会红，不能证明“协议/族展开计入”。建议用 64 路径 × 2 协议 = 128 接受、65 × 2 = 130 拒绝（族同理）来隔离因子。 |
| 4 | **应该修** | `.trellis/tasks/10-24-host-rule-arrays-process-match/prd.md:98` | A11 的“4096 条空 permit 表”在 v2 下不可构造（56 + 32×N ≤ 65536，N ≤ 2046），仓库也没有该用例；`verification.md` 原先声称既有容量用例覆盖，已被本轮改为如实记录。需要 PRD/验收侧改成可执行值（如 2046 条空池）或行为判据；按原文严格验收时 A11 只剩 E2E 不回归部分成立。 |
| 5 | **应该修** | `src/common/rule_blob.h:38-39` | `rule_pool_index_any` 注释写“只用于未命中规则序号”，但 `src/common/forwarded_blob.h:143`、`forwarded_matcher.h:100/113` 仍用它作为网段池“不限”哨兵。建议在注释中明确“host 只用于未命中序号；forwarded 仍作为 network/port 哨兵”，避免后续误删或误改。 |
| 6 | **建议** | `src/common/rule_blob.h`（有效行 500/500）；`src/driver/rule_table.cpp`（495/500） | 两个文件都贴着火线。按结构审查建议拆 `rule_blob_selector.h`（selector/UTF-8/控制字符/长度）并迁移 forwarded 专用 helper；本批不阻断，但下一次改 ABI 很容易超限。 |
| 7 | **建议** | `tests/host/rule_blob_validation_test.cpp:225-229`、`:377-381` | C++ 负向矩阵没有 DEL（0x7F）夹具，也没有“511 字节接受”夹具；代码本身覆盖（`is_control_code_point`、`length > 511`），但边界证据不完整。建议补 `selector-del` 与 511 正例。 |
| 8 | **建议** | `src/userland/WinForward.Userland.Tests/VmChecks.cs:175-197` | VM 上验证了句柄法输出形状、目录标记与“选择器命中原进程”，但没有像结构审查建议那样断言 `file.DevicePath == AppId.ForCurrentProcess()`。行为等价证据已有，直接相等断言可再收紧。 |
| 9 | **建议** | `tools/wfprobe/HostRuleParitySuite.cs:497-518` | `DevicePathForDirectory` 直接返回 `GetFinalPathNameByHandleW` 的字符串，没有 `TrimEnd('\\')`；如果 `exeDirectory` 是卷根，`TestProcessSelector.Directory` 会因“路径以 `\` 结尾”抛异常。当前从 `net11.0` 目录运行不会触发，属夹具边缘。 |
| 10 | **建议** | `tools/wfprobe/BlobBuilder.cs:34`、`:251-254` | `TestRule.Process` 仍作为单值属性存在，并映射成 path selector（前缀语义），字段名与语义漂移；结构审查建议改名 `ProcessPath` 或补注释。 |
| 11 | **建议** | `src/common/rule_blob.h:397-408` | 路径型 selector 只检查 `\device\` 前缀与“不以 `\` 结尾”，不拒绝内部 `/`。产品编译器不会产出，但一个手工 blob 的 block 规则可能装成永不命中的 app-id filter（classify 记 block、auth 拦不住）。可考虑在解码期一并拒绝 `/`，或在 spec 里写清这是 producer 责任。 |
| 12 | **建议** | `src/userland/WinForward.Userland.Tests/Abi/BlobAbiConsistencyTests.cs` | 三份常量互锁已覆盖 v2 的主要常量，但没有锁 `rule_pool_index_any` 与 `tools/wfprobe/Protocol.InvalidRuleIndex`；小风险，可顺手补上。 |
| 13 | **建议** | `tools/wfprobe/AppId.cs:7` | 注释“用户态用同一套 API 拿到同一串”已过时：产品路径型选择器走 `WindowsDevicePath` 句柄法，只有夹具还使用 `FwpmGetAppIdFromFileName0`。 |
| 14 | **建议** | `.trellis/spec/backend/host-rule-blob.md` §9 | 测试表把 `BlobAbiConsistencyTests` 写成根目录文件（实际在 `Abi/` 子目录）；另外 UNC / 映射盘的句柄法输出未在文档里列为未验证边界（plan-review P2 #14）。 |

**阻断项：0。**

## 9. 已修 / 未决

**本轮已修（仅文档）**

- `verification.md`：A7/A8 的 278 → **288**；A9 的 83 ctest → **84/84**；A11 的“4096 由既有用例
  覆盖”改为如实记录“4096 未执行、v2 上限约 2046”；§5 三条 mutation 复选框补上 25 红/4 绿与
  手动复验证据。
- `src/userland/README.md`：把“端口区间颠倒、端口 0”从 `RuleBlobCompiler` 改归到配置层
  （结构 + 取值校验）；语义校验列表只保留跨字段项。

**未决**

- PRD A11 的 4096 数值（问题 4）。
- C++ 负向夹具双违反（问题 1）与 C++ 变异门禁（问题 2）。
- 预算测试的协议因子隔离（问题 3）。
- `rule_pool_index_any` 注释（问题 5）。
- `rule_blob.h` / `rule_table.cpp` 有效行拆分（问题 6）。
- DEL/511 夹具、VM 相等断言、目录夹具 TrimEnd、`TestRule.Process` 命名、路径内部 `/`、
  `rule_pool_index_any` 互锁、AppId 注释（问题 7–13）。

## 10. 已知边界与风险（不是新缺陷）

- block 不支持文件名 / 目录前缀进程选择器；WFP auth filter 只能精确匹配 `ALE_APP_ID`。后续任务
  `10-24-block-classify-enforced-sink` 处理 sink/顺序语义。
- 路径型选择器要求编译期路径存在（文件或目录）；解析失败带规则序号拒绝。
- 网段池不做跨规则去重；blob 略大，但规则区间连续。
- v1 blob 不再读取；控制器 / 驱动 / wfprobe 必须同批升级；控制面 IOCTL 仍是 v7。
- UNC / 映射盘的句柄法输出未做 E2E；`VmChecks` 只覆盖本机卷上的文件与目录。
- WinLTSC 上驱动已卸载（`sc query wfprobe` → 1060），但 `C:\workspace\WinForward.Kernel` 仍保留
  build/cmake 产物；VM 上没有 git，无法核对工作树与本地是否逐字节一致（gate 用的是 11:52 的
  工作树快照）。

## 11. 最终结论

**通过**。

判定依据：Linux `tools/check.sh --full` 8/8 PASS（C# 288/288、ctest 84/84），
`tools/userland/verify-gates.sh` 25 红/4 绿，保存的 Windows gate `GATE-RESULT=PASS`（驱动 SHA、
vm-check 118/0、E2E 48 均与记录一致）；A1–A10 均有可复现证据。A11 的 4096 子项是 PRD 的可执行性
缺陷（已在本报告与 verification.md 中如实记录），不构成代码阻断；问题 1–3 是测试质量改进项，
建议在提交前一并处理或明确留作后续任务。
