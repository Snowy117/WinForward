# Design: dotnet format 诊断清零（全 solution）

Task: `.trellis/tasks/09-19-src-analyzer-cleanup`
Base commit: `c8a5453`
验收范围（用户已定）：**整个 solution** —— `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` exit 0（基线 1369 条：src 426 / tests 762 / bench 181，182 文件）。

## 1. 总体策略

- **修复优先、抑制最后**；每条抑制必须最小范围且附书面理由（项目 spec：`#pragma` 仅用于"行为有意且已文档化"的场合；系统性不兼容用 `.editorconfig` 规则级条目）。
- **行为零变更铁律**：每批后 `dotnet build` 零警告；批次边界跑 `dotnet test`（≥ 基线 724 全绿）。不做功能性修改。
- **分批 + 按规则**：不用"整仓一次性 dotnet format"，而是按规则分组、逐批 diff 审查，保留可回滚粒度（`git stash`/分批提交）。
- 判断型决定**逐条记录**到 `research/disposition-log.md`（规则 → 站点 → 决定[FIX/SUPPRESS] → 理由），供最终独立审计取证。

## 2. 分类与处置矩阵

数据：`research/analyzer-inventory.md`（由 `research/format-report.json` 生成，权威）。

### F — 纯格式（直接修复，用户已授权）
`WHITESPACE` `FINALNEWLINE` `IMPORTS`（13 处：src 8，bench 4，tests 1）。
机制：`dotnet format whitespace WinForward.slnx --no-restore`（自动修复）+ diff 审查 + build。

### M — 机械风格（自动修复，逐批审查）
- IDE 系：IDE0300/0301/0305（集合表达式）、IDE0042（解构）、IDE0230（u8 字面量）、IDE0028/0017（集合/对象初始化器）、IDE0007（var）、IDE0057（区间运算符）、IDE0039（局部函数）、IDE0034/0031/0037/0074/0056（简化式）。
- 分析器系：MA0003（命名实参，402）、MA0007（尾逗号，80）、MA0154（langword）、RCS1123（括号）、RCS1118（const 局部）、RCS1205（实参顺序）、MA0020/RCS1212/RCS1001/RCS1139/MA0176/MA0089/CA2250/CA1854/CA1861。
- MA0041（测试属性可 static，11）：**手工**加 `static`（CA1822 因 fixer 崩溃被禁，MA0041 是其旧版；手工改动安全）。
- 机制：对整 solution 一次 `dotnet format style` / `dotnet format analyzers` 传入机械规则清单（每次调用成本固定 ≈6-7 分钟，故按"pass"合并而非逐规则）；diff 审查 + build + test。

### J — 判断题（逐站点分析；采纳则改代码，不采纳则记录并抑制）

决策权（用户指示，2026-09-19）：每条规则由实现子代理**自行决策**修复 / 局部抑制 / 全局抑制；任何抑制必须注明明确理由并登记 `research/disposition-log.md`；2.2 检查阶段须逐条复核每条抑制是否合理，最终审计独立复核。

| 规则 | 数量(src/tests/bench) | 处置要点 |
|---|---|---|
| MA0076 隐式区域性 ToString | 57/23/36 | 改 `CultureInfo.InvariantCulture`（或 char 重载）属**输出行为变更**：日志/指标/基准输出采纳（确定性是本项目想要的性质，hot-path 不加分配）；测试断言受影响处单独验证；不接受处局部抑制+理由 |
| MA0042 await using | 1/30/2 | 逐点：确为 IAsyncDisposable 场景采纳；否则抑制 |
| RCS1085 自动属性 | 18/0/3 | 逐点：字段语义/性能依赖（ref/volatile）不采纳 |
| CA1859 具体返回类型 | 14/0/1 | 内部 API 性能优化，项目性能导向：干净处采纳；破坏 deep-module seam 或暴露可变集合处不采纳 |
| IDE0290 主构造器 | 13/5/2 | 逐点：薄封装采纳；校验/捕获语义敏感处不采纳 |
| CA1512 ThrowIf* | 14/0/0 | 采纳（异常类型/参数名不变量保持；消息文案变化可接受） |
| CA1068 CT 最后 | 11/0/0 | 逐点：重排波及调用方（含 tests）简单处采纳；与"options 最后"惯例冲突处抑制+理由 |
| MA0189 InlineArray | 11/0/0 | **高风险（ABI 布局）**：仅在有布局/尺寸测试背书时采纳，否则局部抑制+理由；只动 Windows/NdisApi ABI 结构 |
| MA0040 CT 转发 | 4/0/8 | src 采纳（或论证刻意）；bench 场景与 tests 同性质，评估后统一处置 |
| IDE0059/IDE0270/IDE0031 等 | 6/0/0 等 | 验证语义后采纳 |
| RCS1229/1261/1236/1021/1206/1163/MA0001/MA0042/MA0159/CA1419/CA2016/CA2208/MA0182/VSTHRD104/MA0166 | 各 1-4 | 逐站点；CA2208（paramName 与参数不符）、MA0182（AdapterTransientRetryLogGate 未被引用）需查证后决定（死代码处置走 09-19-compat-api-cleanup 的 KEEP/REMOVE 精神，记录结论） |
| IDE0060（现为 error 级） | 1/0/0 | 查站点：真未用则删/改为接口实现豁免，否则抑制 |
| CA2012 | 0/2/1 | ValueTask 仅可消费一次——测试/基准站点逐点 |

### N — IDE1006 命名（230）→ 按用户裁定约定（2026-09-19，两轮澄清）

约定：

| 字段类型 | 命名 | 示例 |
|---|---|---|
| `public`/`protected` 静态/实例字段（可能公开暴露） | PascalCase，无前缀 | 公开字段出于性能允许保留（interop 结构/热路径容器），命名同 PascalCase |
| `internal` 与 `private` static | s_camelCase | `s_defaultInstance` |
| `[ThreadStatic]` static | t_camelCase | `t_currentContext` |
| `internal` 与 `private` 实例字段 | _camelCase | `_disposed` |
| `private const` / `internal const` | PascalCase | `MaxBufferSize` |
| 局部 const | camelCase（局部变量风格） | `ipOffset` |

注：editorconfig 按**声明**可访问性匹配（无法区分 internal 类型上的 `public` 字段）；嵌套容器/测试辅助类的 `public` 字段保持 PascalCase。

现状违例分桶与处置（完整清单：`research/naming-inventory.md`）：

| 桶 | 数量 | 处置 |
|---|---|---|
| A1 私有 static 缺 `s_` | 67（66 + t_ 特例） | 66 处改名 s_camelCase |
| A2 internal static 现 Pascal | 9（InterceptionHealthMonitor 3、TcpProxyRelay 3、LayeredCaptureRunner 2、ClientResetInjector 1） | 改名 s_camelCase |
| A3 internal 实例字段现 Pascal | 12（SetupExecutor：SetupWorkItem 9 + UdpSetupWork 3） | 改名 _camelCase |
| B 非私有实例字段被判缺 `_` | 76 | 全部声明 public（IPHelperAbi/NdisApiAbi ABI 字段、internal 类型上的 public 容器字段、tests 3）→ 保持 PascalCase，**零改名** |
| C `Prefix _ not expected`（NativeLease `_pool`/`_pointer`） | 2 | 配置修正后（internal 实例 → _camelCase）合规，**零改动** |
| D 局部 const 被判 Pascal | 85 | `constants` 规则去掉 `local`，**零改名** |
| E `[ThreadStatic] t_recycleCache` | 1 | 方案1：增加 t_ 规则（实证多规则"任一满足"语义）；方案2：单站点 pragma+理由 |

editorconfig 编辑（5 处）：`static_fields` 限定 {private, private_protected, internal} 保持 s_camelCase；`instance_fields` 同可见性保持 _camelCase；`non_private_static_fields` 与 `non_private_readonly_fields` 去掉 internal/private_protected（留 public/protected/protected_internal → PascalCase）；`constants` 去掉 `local`；(可选) t_ 规则。

执行流（用户指示）：**先改 editorconfig，再跑 dotnet format** ——
1. 改 `.editorconfig`（上述 5 处 + 注释更新）。
2. `dotnet format style WinForward.slnx --no-restore --severity info --diagnostics IDE1006` 应用 87 处改名（对应桶 A1/A2/A3；同名跨文件引用由 rename fixer 处理，若跨项目引用未传播则按构建错误清单补改）。
3. diff 审查 + build + test；残余 IDE1006 逐点处置（E 特例）。

### S — 抑制（新增）
- 只有 A/J 类中"不采纳"的站点才有抑制；**优先局部 `#pragma warning disable <RULE> // 理由`**（spec 惯例），规则级 `.editorconfig` 条目仅用于系统性不兼容（沿用现有中文注释格式，写明"为何修复不可取"）。
- 新增抑制全部登记到 `research/disposition-log.md`。

## 3. 既有 36 条抑制的处置（审计阶段，不提前动）

本任务实现阶段不改动既有 `severity = none` 条目；由 §5 的独立审计实证后处置（删除/改注释/保留），处置后复跑终局门。

## 4. 执行批次（行为零变更门禁）

| 批次 | 内容 | 验证 |
|---|---|---|
| B0 | 基线固化：build + test 记录（预期 724 全绿、零警告） | 输出存 `research/baseline-*.txt` |
| B1 | F 纯格式（dotnet format whitespace） | diff 审查 + build |
| B2 | M 机械组：tests+benchmarks 项目（MA0003 320、IDE0300 94、MA0007 54、IDE0042 44、MA0076tests 23 除外…） | diff + build + test |
| B3 | M 机械组：src 项目 | diff + build + test |
| B4 | J 判断题：src 优先（MA0076、CA1859、CA1512、RCS1085、IDE0290、CA1068、MA0189、MA0040、其余小项），tests/bench 的判断项 | 逐点 disposition-log + build + test |
| B5 | N 命名重配 + 改名 | verify 重跑 + build |
| B6 | S 抑制汇总（不采纳站点的 pragma/规则条目） | 每条有理由 + build |
| B7 | 终局门：`dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` exit 0；build 零警告；test ≥724 | 全绿 |

执行方式：主会话设计/编排；代码批由 trellis-implement 子代理执行（每批先读 implement.jsonl 注入的 spec）；B2-B4 之间任一失败即回滚该批（git 回退该批文件）再分析。

## 5. 最终独立审计设计（用户硬性要求）

- **时机**：B7 通过后、提交前。发现问题 → 修正 → 复跑 B7 门。
- **独立性**：新派遣的通用子代理（非实现者、不带实现历史），输入 = 终局 diff、最终 `.editorconfig`、全部 `#pragma` 清单、`research/disposition-log.md`、本 PRD/design。
- **实证方法**（每条抑制/忽略项）：
  1. 对 36 条既有 editorconfig 抑制 + 本任务新增规则级抑制：临时将其改为启用（`severity` 恢复默认/警告），跑一次全量 `dotnet format --severity info --verify-no-changes --report`，得到"启用后各规则命中数 + 样本"，随后还原。pragma 站点同理（临时移除 pragma 或定向验证）。
  2. 逐条裁定：**NECESSARY**（确实触发且理由成立）/ **RATIONALE-STALE**（触发但注释理由与本仓库不符 → 改写）/ **UNNECESSARY**（不触发且文档所述触发模式不存在 → 删除）/ **FIXABLE**（触发但本应修复 → 标记）。
  3. warning-默认规则注意：删除"当前不触发"的抑制前须确认其理由所指模式确实不在仓库中（避免解除未来 build break 防护）；不确定则保留并修正理由。
- **产出**：`research/suppression-audit.md`（逐条结论 + 证据 + 处置）。处置应用后复跑 B7 三门。

## 6. 风险与对策

- fixer 已知 bug（CA1822 族/IDE0130 已禁）→ 只对白名单规则运行 fixer；每批 build。
- MA0076 区域性语义变更 → 逐点审查 + 测试兜底；hot-path 分配纪律（spec hot-path.md）。
- MA0189 ABI 布局 → 默认保守，无测试背书不采纳。
- 集合表达式（IDE0300/0305）热路径 → 语义等价（编译期展开），分配行为不变；分配门禁测试兜底。
- 大 diff 审查 → 按规则分批 + `git diff --stat` 统计 + 抽样 hunk。
- 既有 36 条抑制含失效条目 → 审计阶段实证处置，不在实现期扩大改动面。

## 7. 收尾

- 提交策略：按批次分组提交（F 格式 / M 机械 / J 判断 / N 命名 / S+审计处置），message 遵循仓库惯例。
- spec 更新：命名约定裁决 + 抑制策略增补写入 `.trellis/spec/backend/quality-guidelines.md`。
