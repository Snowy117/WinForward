# Implement: jb inspectcode 引入与报告清零

依据：`prd.md` + `design.md`（分类矩阵/抑制机制/批次）。清单：`research/jb-inventory.md`。

## B0 — 基线固化

- [ ] jb 确定性重跑：`jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-rerun.xml WinForward.slnx; echo exit=$?` → 对比 1317 条一致；**记录退出码语义**（有 issue 时退出码为何值 → 决定 CI 断言实现），存 `research/baseline-jb.txt`
- [ ] `dotnet build WinForward.slnx -c Release --no-restore`（0 warning）+ `dotnet test WinForward.slnx -c Release --no-restore`（≥725 全绿），存 `research/baseline-build.txt` / `research/baseline-test.txt`
- [ ] `git status --porcelain` 确认工作树干净、baseline `74cbb23`

## B1 — A 类机械修复（按规则小批，每批 Release build + 关键批次 tests）

- [ ] RedundantUsingDirective 79（防误删条件编译/global using；逐站点确认符号被其它 using 覆盖）
- [ ] RedundantNameQualifier 45（`System.Xxx` 全限定在 using 存在时简化）
- [ ] RedundantCast 280（逐站点确认重载绑定不变；tests/bench 为主）
- [ ] RedundantUnsafeContext 6 / RedundantExplicitArrayCreation 1 / RedundantExtendsListEntry 1 / RedundantJumpStatement 2
- [ ] InvalidXmlDocComment 9（修复 XML doc，不改变 doc 语义）
- [ ] UseCollectionExpression 7 / UseUtf8StringLiteral 6（与既有 IDE0300/IDE0230 裁定一致）
- [ ] ConvertToAutoProperty(+PrivateSetter/WhenPossible)/PropertyCanBeMadeInitOnly ~11
- [ ] ConvertClosureToMethodGroup 2 / MergeIntoPattern(+LogicalPattern) 3 / RawStringCanBeSimplified 1 / VariableHidesOuterVariable 1 / UseDeconstruction 2 / ConvertToConstant.Local 6
- [ ] ConvertIfStatementToReturnStatement 18 / InlineTemporaryVariable 18（可读性逐点；红线不达标则抑制）
- [ ] 批次结束：全量 jb 重跑核对本批规则归零

## B2 — B 类未使用代码家族

- [ ] MemberCanBePrivate.Global 70（逐点；IVT：改后构建失败即还原并抑制+理由）
- [ ] UnusedAutoPropertyAccessor.Global 15 / NotAccessedPositionalProperty(.Local/.Global) 20 / UnusedMember(.Global/.Local) 9 / UnusedParameter.Global 5 / UnusedVariable 5
- [ ] UnusedType.Global 2（含 `TcpCoordinatorFakes.BarrierListenerFactory`：上次审计"确认无用可删"，本次删除或理由抑制）/ CollectionNeverQueried.Global 1 / UnusedMethodReturnValue.Global 1 / UnusedMemberInSuper.Global 1
- [ ] Release build + tests 门禁

## B3 — C 类正确性/审查（逐站点；决定入 disposition-log）

- [ ] AccessToDisposedClosure 64 + AccessToModifiedClosure 9：逐点分类（刻意测试模式→局部抑制+个别理由；src 站点从严核查）
- [ ] DisposeOnUsingVariable 11 / RedundantSuppressNullableWarningExpression 31 / ConditionIsAlwaysTrueOrFalse… 10 / ConditionalAccessQualifier… 2 / NullCoalescingCondition… 1（nullable 家族；删 `!`/改判空后由 TWAEs 构建兜底）
- [ ] HeuristicUnreachableCode + CS0162 4（bench 死代码核查）/ InconsistentlySynchronizedField 2 / EqualExpressionComparison 1（疑刻意自比较断言）/ SuspiciousTypeConversion.Global 1 / RedundantArgumentDefaultValue 6 / TryStatementsCanBeMerged 1 / SwitchStatementMissingSomeEnumCases(NoDefault) 2
- [ ] NotResolvedInText 1 → 局部抑制（member-path paramName 刻意模式，理由引用既有 MA0015/S3928/CA2208 叙事）
- [ ] CheckNamespace 19 → `[tests/WinForward.Core.Tests/TestHelpers/**.cs]` 增 `resharper_check_namespace_highlighting = none`（与 IDE0130 同路径同理由）
- [ ] InconsistentNaming 37 → 规则级 `resharper_inconsistent_naming_highlighting = none` + 详细理由（缩写驼峰化/局部 const 误读/t_ 约定；命名已由 IDE1006 更严格覆盖）；键实测生效
- [ ] Release build + tests 门禁

## B4 — D 类风格/性能红线

- [ ] ArrangeRedundantParentheses 41 → 规则级 `resharper_arrange_redundant_parentheses_highlighting = none`（与 RCS1123 实测 32/37 直接对立；理由引用交集证据）；键实测生效
- [ ] InvertIf 50：逐站点（早退自然处采纳；其余抑制）→ 汇总后定局部 vs 规则级
- [ ] ArrangeObjectCreationWhenTypeNotEvident 256：先尝试 style 键改向（实测）；否则规则级抑制+理由（de facto 目标类型 new）
- [ ] LINQ 家族：LoopCanBeConvertedToQuery 9 / ForeachCanBePartlyConvertedToQuery(+AnotherGetEnumerator) 7 / ForCanBeConvertedToForeach 3 → 规则级抑制+性能理由（hot-path 红线）
- [ ] ReplaceWithPrimaryConstructorParameter 48 逐点（薄封装采纳；性能/捕获语义敏感处抑制）
- [ ] MethodSupportsCancellation 19（tests）→ `[tests/**.cs]` 抑制+理由（与上次 MA0040 裁定一致）
- [ ] MoveLocalFunctionAfterJumpStatement 4 / SeparateLocalFunctionsWithJumpStatement 4 / DuplicatedSequentialIfBodies 5 / ConvertIfStatementToSwitchStatement 5 / AsyncMethodWithoutAwait 3 / PreferConcreteValueOverDefault 1 / MergeIntoPattern 1
- [ ] 每个新增 `resharper_*` 键**实测生效**（局部/全量 jb 重跑确认该项消失）
- [ ] Release build + tests 门禁

## B5 — 抑制汇总与交叉验证

- [ ] 全部新增抑制登记 `research/disposition-log.md`（机制/范围/理由）
- [ ] `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` → exit 0
- [ ] 全量 jb 重跑 → 剩余仅经审计抑制项之外为 0

## B6 — 引入交付（AGENTS.md + CI）

- [ ] AGENTS.md 的 Pre-Commit Quality Gate 增补 jb 自检（命令、~5 分钟、XML 输出到临时路径、零 issue 才可提交）
- [ ] `.github/workflows/analyzer-gate.yml` 增补 jb job：固定版本安装 `JetBrains.ReSharper.GlobalTools`（2026.1.3）→ 运行同一命令 → **解析 XML 断言 Issues 为空**（不依赖退出码）→ 上传 report artifact

## B7 — 终局门（提交前）

- [ ] `jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-final.xml WinForward.slnx` → **零 issue**
- [ ] `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` → **exit 0**
- [ ] `dotnet build WinForward.slnx -c Release --no-restore` → 0 warning；`dotnet test WinForward.slnx -c Release --no-restore` → ≥725 全绿
- [ ] `git diff --stat` 总览 + 热点文件复查

## A — 独立审计（B7 后、提交前）

- [ ] 派遣独立代理审计全部 jb 抑制（新增 + 既有 resharper 键 + 本任务新增 pragma）：逐条移除抑制重跑 jb 验证"触发且不可修"；裁定 NECESSARY / RATIONALE-STALE / UNNECESSARY / FIXABLE；产出 `research/suppression-audit.md`；处置后复跑 B7 四门

## 提交与收尾

- [ ] 按批次分组提交（配置 / A 机械 / B 未使用 / C 正确性 / D 风格+抑制 / E 交付），message 遵循仓库惯例
- [ ] spec 更新：jb 门禁与抑制经验并入 `quality-guidelines.md`

## 回滚点

- 每批次最小回滚单元（按文件还原）；jb 修改引发 dotnet format 回退时，回滚该批并转抑制路径。
