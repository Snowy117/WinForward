# Design: jb inspectcode 引入与报告清零

Task: `.trellis/tasks/09-20-jb-inspectcode-cleanup`
Base commit: `74cbb23`
验收：`jb inspectcode -f=Xml -e=HINT -o=<path> WinForward.slnx` 报告零 issue；且不得回退 `dotnet format --severity info --verify-no-changes` exit 0、Release build 0 警告、测试 ≥725。

## 1. 总体策略

- 修复优先、抑制最后；抑制最小范围（局部注释优先，系统性用 `.editorconfig` `resharper_*_highlighting = none`，按证据路径 glob 限定——2026-09-19 用户裁定原则）。
- **jb inspectcode 无 CLI fixer**：全部修改由子代理逐站点编辑，天然要求逐条审查；批次按规则分组，每批后 build+tests。
- **双工具冲突护栏**：每批后验证 `dotnet format ... --verify-no-changes` 仍 exit 0（jb 修改可能触发 RCS/IDE 规则，如删括号 vs RCS1123、集合表达式 vs IDE0300 等）；冲突站点以 dotnet format 门禁为准并记录。
- 判断记录：逐条/逐规则决定写 `research/disposition-log.md`（FIX / SUPPRESS-LOCAL / SUPPRESS-GLOBAL + 理由）。
- 行为零变更；Release 门禁（测试 725 基线）。

## 2. 清单概览（research/jb-inventory.md，1317 条 / 75 规则 / 194 文件）

WARNING 681 / HINT 449 / SUGGESTION 187。项目分布：Tests 711、Runtime 327、Benchmarks 100、Configuration 45、Core 43、Windows 30、NdisApi 29、Protocols 17、Cli 15。

## 3. 分类处置矩阵

### A — 机械安全修复（采纳，逐批审查）
RedundantUsingDirective 79、RedundantNameQualifier 45、RedundantCast 280（⚠ 逐站点确认不改变重载绑定）、RedundantUnsafeContext 6、RedundantExplicitArrayCreation 1、RedundantExtendsListEntry 1、RedundantJumpStatement 2、InvalidXmlDocComment 9、UseCollectionExpression 7、UseUtf8StringLiteral 6、ConvertToAutoProperty(+PrivateSetter/WhenPossible/PropertyCanBeMadeInitOnly) ~11、ConvertClosureToMethodGroup 2、MergeIntoPattern/LogicalPattern 3、RawStringCanBeSimplified 1、VariableHidesOuterVariable 1、UseDeconstruction 2、ConvertToConstant.Local 6（与上次 RCS1118 同向）、ConvertIfStatementToReturnStatement 18、InlineTemporaryVariable 18（可读性逐点）。
⚠ ArrangeRedundantParentheses 41 已移入 D 类（实测与 RCS1123 直接对立，见下）。

### B — 未使用代码家族（尽量采纳，IVT 防线=构建/测试）
MemberCanBePrivate.Global 70、UnusedAutoPropertyAccessor.Global 15、NotAccessedPositionalProperty(.Local/.Global) 20、UnusedMember(.Global/.Local) 9、UnusedParameter.Global 5、UnusedVariable 5、UnusedType.Global 2、CollectionNeverQueried.Global 1、UnusedMethodReturnValue.Global 1、UnusedMemberInSuper.Global 1。
注意：本仓测试经 `InternalsVisibleTo` 消费 internal 成员；jb 不识别 IVT 时的一切"可私有化"建议先验证（构建失败即证明其可见性必要 → 抑制+理由）。`TcpCoordinatorFakes.BarrierListenerFactory`（上次审计已标"确认无用可删"）一并处置。

### C — 正确性/审查类（逐站点深查：可能是真问题或刻意模式）
- AccessToDisposedClosure 64（主要 tests/bench：断言 dispose 后行为等刻意模式居多，逐点分类）+ AccessToModifiedClosure 9
- DisposeOnUsingVariable 11、RedundantSuppressNullableWarningExpression 31、ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract 10、ConditionalAccessQualifierIsNonNullableAccordingToAPIContract 2、NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract 1（nullable 家族；删 `!`/`??` 后构建 TWAEs 兜底，站点级决定）
- HeuristicUnreachableCode + CSharpWarnings::CS0162 4（bench，疑 `while(true)` 死代码）、InconsistentlySynchronizedField 2（tests）、EqualExpressionComparison 1（疑刻意自比较断言）、SuspiciousTypeConversion.Global 1、RedundantArgumentDefaultValue 6、TryStatementsCanBeMerged 1、SwitchStatementMissingSomeEnumCases 2
- CheckNamespace 19 = TestHelpers 目录（与上次 IDE0130 同一裁定）→ 同路径 glob 抑制
- NotResolvedInText 1（FlowDispatcher member-path paramName，与既有 MA0015/S3928/CA2208 pragma 同一刻意模式）→ 抑制+理由
- **InconsistentNaming 37（规则级抑制）**：实证全部为工具间语义差——jb 的 PascalCase 把 `IP*`/`FFFF` 缩写驼峰化（`IPAddressValue`→`IpAddressValue`，违反 BCL 惯例与本仓命名）、把我们标为 field-only 的 `constants_should_be_pascal_case` 误用于局部 const、以及与 t_ ThreadStatic 约定的冲突；无真实问题（命名已由 IDE1006 门禁覆盖且更严格）→ `resharper_inconsistent_naming_highlighting = none` + 详细理由

### D — 风格/性能红线类（规则级抑制为主）
### D — 风格/性能红线类（规则级抑制为主）

- **ArrangeRedundantParentheses 41（实测与 RCS1123 直接对立 32/37 站点，86%）**：上个任务 RCS1123 强制补齐的清晰括号（如 `4 + (2 * 8)`、`TryGetValue(...) ? x : 0`），jb 判为冗余——同一 token 上两工具相反。dotnet format 门禁为准 → `resharper_arrange_redundant_parentheses_highlighting = none` 规则级抑制 + 实测交集理由（剩余 ~5 个非冲突站点一并由规则级覆盖，避免同一检查半开半关）。
- ArrangeObjectCreationWhenTypeNotEvident 256（HINT）：jb 偏好"类型不明显时显式写类型"，与代码库 de facto 的目标类型 `new(...)` 风格相悖 → 先探 style 键能否改向；不行则规则级抑制+理由
- **性能红线（规则级抑制+理由）**：LoopCanBeConvertedToQuery 9、ForeachCanBePartlyConvertedToQuery(UsingAnotherGetEnumerator) 7、ForCanBeConvertedToForeach 3 —— 性能优先，显式循环不转 LINQ
- **InvertIf 50（用户红线：逐站点）**：仅当反转后是自然的早退路径且可读性更好时采纳；其余抑制（局部或按多数裁决规则级）；记录每站点归属
- ReplaceWithPrimaryConstructorParameter 48：与上次 IDE0290 同类，逐站点（性能/捕获语义）；薄封装采纳
- MethodSupportsCancellation 19（全在 tests）：与上次 MA0040 对 tests 的裁定一致（测试刻意不串 token）→ `[tests/**.cs]` 抑制+理由
- MoveLocalFunctionAfterJumpStatement 4、SeparateLocalFunctionsWithJumpStatement 4、DuplicatedSequentialIfBodies 5、ConvertIfStatementToSwitchStatement 5、AsyncMethodWithoutAwait 3（review：移除 async 或补 await）、MergeIntoPattern、PreferConcreteValueOverDefault 1（SetupExecutor `default`→`CancellationToken.None`）：逐点小项

### E — 引入交付（用户已确认"门禁+CI 全套"）
- AGENTS.md Pre-Commit Quality Gate 增补 jb 自检命令（与 dotnet format 并列；注明 ~5 分钟与 XML 临时输出）
- CI：`Analyzer Gate` workflow 增补 jb job（PR/push master）：固定版本安装 JetBrains 工具（`JetBrains.ReSharper.GlobalTools`，版本 = 2026.1.3），运行后**解析 XML 断言 Issues 为空**（jb 退出码语义需在 B0 实测确认；CI 一律以解析为准），report 作为 artifact 上传
- 可复现性：工具版本（2026.1.3）记录于 AGENTS.md 注释与 CI；本地经由 nix devshell 提供

## 4. 抑制机制（实证验证）

- 规则级：`.editorconfig` `resharper_<inspection_id_snake>_highlighting = none`（先例：现有 `resharper_arrange_trailing_comma_*` 键）。**每个新键在启用后必须实测生效**（局部重跑 jb 确认该项从报告消失），否则改用局部注释。
- 站点级：`// ReSharper disable once <InspectionId>`（附行内理由）或成对 disable/restore。
- 所有抑制附书面理由；glob 限定作用域；最终独立审计（审计 = 临时移除抑制并重跑 jb，验证每条"触发且不可修"）。

## 5. 执行批次（行为零变更门禁）

| 批次 | 内容 | 验证 |
|---|---|---|
| B0 | 确定性核验（jb 重跑确认 1317 稳定 + 实测退出码语义）+ Release build/test 基线固化 | 记录 research/baseline-jb.txt |
| B1 | A 类机械修复（按规则小批） | 每批 build + tests；结束 jb 定向重跑 |
| B2 | B 类未使用代码家族 | 同上（IVT 失败站点转抑制） |
| B3 | C 类正确性/审查逐站点 | disposition-log 逐条；build+tests |
| B4 | D 类风格/红线逐站点 + 规则级抑制（InconsistentNaming/CheckNamespace/LINQ 家族/ObjectCreation 等） | 每键实测生效 |
| B5 | 抑制汇总 + 四工具交叉验证 | dotnet format verify exit 0 + jb 重跑 |
| B6 | E 交付：AGENTS.md + CI | CI YAML 语法自查 |
| B7 | 终局门：jb 报告零 issue；dotnet format exit 0；Release build 0 警告；tests ≥725 | 全绿 |
| A | 独立代理审计全部 jb 抑制（新增 + 既有 resharper 键） | research/suppression-audit.md；处置后复验 |

## 6. 风险与对策

- **jb 报告波动**：B0 先做确定性重跑；同版本同输入应稳定（若波动 >2%，先调查再动手）。
- **双门禁冲突**（删括号 vs RCS1123、集合表达式 vs IDE0300、命名 vs IDE1006）：每批交叉验证；冲突站点以既有 dotnet format 门禁为准。
- **性能红线**：LINQ 转换家族规则级拒绝；hot-path 文件（Protocols/NDisApi/Windows ABI、Runtime 热路径）内的任何改动逐点过 hot-path spec + 分配门禁。
- **AccessToDisposedClosure 64 条**：分类为"刻意测试模式"者需给出与代码语义一致的个别/批量理由；src 站点（Program.cs 等）从严。
- **jb 误报工具间差异**：以"修复是否提升质量/可维护性"为唯一裁决标准，工具差异处抑制并记录。

## 7. 收尾

- 提交：按批次分组（配置/机械/未使用/正确性/风格+抑制/交付），message 遵循仓库惯例。
- spec 更新：jb 门禁与抑制经验并入 `quality-guidelines.md`；AGENTS.md 同步。
