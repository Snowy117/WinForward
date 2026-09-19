# Implement: dotnet format 诊断清零（全 solution）

Task: `.trellis/tasks/09-19-src-analyzer-cleanup`
依据：`prd.md`（范围/验收）+ `design.md`（分类处置矩阵、命名重配、审计设计）。

## 预检（B0，基线固化）

门禁配置：**build/test 一律 `-c Release`**（零分配门禁测试仅在 Release 下精确；2026-09-19 实测：Debug 下 `EstablishedUdpDatagramPathAllocatesNoManagedBytes` / `SynRetentionWithWarmSynCopyPoolAllocatesNoManagedBytes` 确定性失败，Release 下通过；实测基线 **725 全绿**，spec 文本里的 724 为陈旧值）。

- [x] `dotnet build WinForward.slnx -c Release --no-restore` → 0 warning 0 error（研究目录 baseline-build.txt）
- [ ] `dotnet test WinForward.slnx -c Release --no-restore` → 725/725 全绿（研究目录 baseline-test.txt）
- [ ] `git status --porcelain` 确认工作树干净、基线 `c8a5453`

## B1 — F 纯格式

- [ ] `dotnet format whitespace WinForward.slnx --no-restore`
- [ ] `git diff` 审查（预期仅 13 处格式 hunk + 导入排序，`IMPORTS` 3 处）
- [ ] `dotnet build -c Release WinForward.slnx --no-restore`（0 warning）

## B2 — M 机械组（全量：src + tests + benchmarks，一次 pass 覆盖）

- [ ] 先跑一次 style pass：`dotnet format style WinForward.slnx --no-restore --severity info --diagnostics IDE0300 IDE0301 IDE0305 IDE0042 IDE0230 IDE0028 IDE0017 IDE0007 IDE0057 IDE0039 IDE0034 IDE0031 IDE0037 IDE0074 IDE0056`
- [ ] 再跑一次 analyzers pass：`dotnet format analyzers WinForward.slnx --no-restore --severity info --diagnostics MA0003 MA0007 MA0154 RCS1123 RCS1118 RCS1205 MA0020 RCS1212 RCS1001 RCS1139 MA0176 MA0089 CA2250 CA1854`
- [ ] 手工项：MA0041（tests 11 处，属性加 `static`；CA1822 fixer 禁用，只手工）、CA1861（tests 7 处）
- [ ] `git diff --stat` + 抽样审查（重点：断言参数被改为命名实参后语义不变）
- [ ] `dotnet build -c Release` 0 warning；`dotnet test -c Release` ≥725 全绿

（原 B2/B3 的 src 与 tests/benchmarks 拆分合并：pass 本身是 solution 级，拆两轮只是重复运行。）

## B4 — J 判断题（逐站点；决定写入 `research/disposition-log.md`）

决策权（用户指示）：每条分析器规则由实现子代理**自行决策**——改代码 / 局部抑制 / 全局抑制；任何抑制（pragma 或 editorconfig）都必须**明确注明理由**并登记 disposition-log。

src 优先（site 数量少的规则手工改，MA0076 用定向 pass）：

- [ ] MA0076（src 57）：`dotnet format analyzers WinForward.slnx --no-restore --severity info --diagnostics MA0076` → 逐 hunk 审查；测试断言/不接受处还原为原样并加局部 pragma（记录）
- [ ] CA1512（src 14）：改 ThrowIf*；`dotnet build` 验证
- [ ] RCS1085（src 18）：逐点（ref/volatile/性能语义）；不采纳处 pragma
- [ ] CA1859（src 14）：逐点（seam/可变集合暴露 vs 内部性能）；采纳则改签名+调用方
- [ ] IDE0290（src 13）：逐点；不采纳处 pragma 或留规则（决定后统一）
- [ ] CA1068（src 11）：逐点；重排或 pragma+理由
- [ ] MA0189（src 11，ABI）：查 NdisApi/Windows 布局测试背书；无背书→pragma+理由；采纳→跑 ABI 尺寸测试
- [ ] MA0040（src 4 / bench 8）：采纳转发或论证；bench 与 tests 类似，统一决定
- [ ] IDE0059（src 6）/ IDE0270 / IDE0031 / IDE0074：验证语义后处理
- [ ] 小项：MA0001(3, src+bench)、MA0020、MA0089、MA0159、MA0042(src 1)、RCS1229(4)、RCS1261(src 2)、RCS1236、RCS1206、RCS1021、RCS1163、CA1419、CA2016、CA2208、CA2250(2)、CA1854、VSTHRD104、MA0166、IDE0060(error)
- [ ] MA0182：查 AdapterTransientRetryLogGate 引用（reflection/测试）；确死→按 compat-cleanup 精神处置并记录；未死→复核分析器误报→pragma+理由
- [ ] tests/bench 剩余判断项：MA0076(23/36)、MA0042(30/2)、RCS1261(7/3)、IDE0290(5/2)、CA2012(2/1)、VSTHRD104、MA0166、RCS1163(3)
- [ ] 每小组后 `dotnet build -c Release` 0 warning；B4 结束 `dotnet test -c Release` ≥725 全绿

## B5 — N 命名（用户约定：先改 editorconfig，再跑 dotnet format；清单 research/naming-inventory.md）

清点权威性（用户指示）：以 **editorconfig + dotnet format 的自动检查**（诊断输出与 fixer 结果）为准；research/naming-inventory.md 的手写正则清点仅用于规模预估，**可能有遗漏，不作为清单依据**。

- [ ] `.editorconfig` 5 处编辑：`static_fields` 限定 {private, private_protected, internal} 保持 s_camelCase；`instance_fields` 同可见性保持 _camelCase；`non_private_static_fields` / `non_private_readonly_fields` 去掉 internal/private_protected；`constants` 去掉 `local`；同步更新注释
- [ ] `dotnet format style WinForward.slnx --no-restore --severity info --diagnostics IDE1006` 应用 87 处改名（A1 66 + A2 9 + A3 12）；diff 审查改名正确性；跨项目引用（tests 经 IVT）若未传播按构建错误补改
- [ ] `[ThreadStatic] t_recycleCache`：先试 editorconfig 增加 t_ 规则（实证"任一满足"语义）；不成立则单站点 pragma + 理由
- [ ] 重跑 `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore`，IDE1006 归零
- [ ] `dotnet build -c Release` 0 warning；`dotnet test -c Release` ≥725 全绿

## B6 — S 抑制汇总

- [ ] 汇总 B4 中"不采纳"的站点：局部 `#pragma warning disable <RULE> // 理由`（spec 格式）或规则级 editorconfig 条目（系统性时才用）
- [ ] 每条抑制登记 `research/disposition-log.md`
- [ ] `dotnet build` 0 warning

## B7 — 终局门（提交前）

- [ ] `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` → **exit 0**（不加 tail 管道以保真实退出码）
- [ ] `dotnet build WinForward.slnx -c Release --no-restore` → 0 warning
- [ ] `dotnet test WinForward.slnx -c Release --no-restore` → ≥725 全绿
- [ ] `git diff --stat` 总览 + 敏感文件重点复查

## A — 独立审计（B7 后、提交前）

- [ ] 派遣独立子代理（输入：终局 diff、.editorconfig、pragma 清单、disposition-log、prd/design）按 design §5 方法实证审查**全部**抑制（本任务新增 + 既有 36 条 + 全部 pragma）
- [ ] 产出 `research/suppression-audit.md`（逐条：NECESSARY / RATIONALE-STALE / UNNECESSARY / FIXABLE + 证据）
- [ ] 应用处置（删除/改注释/修复）→ 复跑 B7 三门

## 提交

- [ ] 按批次分组提交（F/M/J/N/S+审计），message 遵循仓库惯例（`chore:`/`refactor:`/`style:` 等）
- [ ] spec 更新：命名约定裁决 + 抑制策略写入 `.trellis/spec/backend/quality-guidelines.md`

## 回滚点

- 每批次为最小回滚单元（`git stash` / 按文件还原）；B2/B3/B4 失败时先回滚该批再分析，不叠加修复。
- MA0189/命名改名若引发 ABI/调用方连锁问题：优先回滚该批，改走"抑制 + 记录"路径。
