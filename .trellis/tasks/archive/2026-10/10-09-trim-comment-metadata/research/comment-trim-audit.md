# 注释精简验收证据（C 档全量微缩重审）

任务：`.trellis/tasks/10-09-trim-comment-metadata`（父任务 `10-09-code-hygiene-rename-comments` R2）
日期：2026-10-09

## 范围

| 维度 | 数值 |
| --- | ---: |
| 纳入整理的文件 | 596 个 git 跟踪的 `.cs`（`src/` 138、`tests/` 244、`benchmarks/` 243、`analyzers/` 7，去重后为含注释者） |
| 零注释、正确跳过 | 36 |
| 执行单元 | 56 个主批次 + 2 个补漏批次（每批 3–20 个文件、按字节预算装箱，文件集互不重合） |
| 注释站点改写 | 约 1,001 处（逐条人工阅读后改写） |
| 整条注释删除 | 约 100 处 |
| 字符串字面量修订 | 13 处（获用户授权后） |

批次划分依据：按源码字节数装箱（目标 ~85 KB/批、上限 100 KB、单批 ≤20 文件），
装箱结果写入 `research/batches/batch-NN.txt`；覆盖完整性经脚本核对为「596 文件、无重复、无遗漏」。

## 方法与纪律

- **禁止脚本编辑**（用户硬性要求）：所有改写由子代理逐条阅读、逐条编辑完成；`rg`/`wc`/`git diff`
  仅用于只读验证。
- **禁止子代理跑 dotnet**（4.6 GiB 动态内存机器）：并行 dotnet 会 OOM。构建与测试由父代理串行执行。
- **每批自带等价性证明**：`git diff -U0 | rg '^[+-]' | rg -v '注释行'` 必须为空——
  即除注释行外没有任何可执行 token 变化。字符串字面量例外需逐行列出（`LITERAL_EDITS`）。
- **每轮父代理串行验证**：`dotnet build -c Release` 零警告 → `dotnet test -c Release --no-build`
  对 1,663 基线。
- **XML 结构保全**：子代理逐文件比对标签多重集与 `cref`/`paramref` 目标。
- **失败自纠记录**：多个批次报告并自行回滚了过程中的失误（误删可执行行、误合并行、误删
  `// ReSharper disable once` 指令、误改字符串字面量），均由批次内置的代码 token 检查捕获。

## 删除判据与保留判据

**删除**：任务名与任务内条目号（`task 09-17`、`R1-A`、`B11`、`P3`、`M0`、`S3/D3`、`(B1/B2)` …）、
日期戳（`2026-09-30`、`measured 2026-08-27`）、`design §x.y` 与 PRD/AC 编号、测量产物路径
（`benchmarks/results/…`、研究目录）、“pre-change / 09-28 series / the former …” 这类描述过去的措辞、
与紧邻代码重复的叙述、描述已删除代码的过期注释。

**保留并改写更短**：不变量与前提、动机与取舍、边界与拒绝路径、性能与分配约束（含池租借平衡）、
外部规范依据（RFC/Windows/NDISAPI）、所有权与生命周期。

**刻意保留的"非指针"引用**（子代理逐条判断并列入 UNSURE，父代理复核）：
- 活文档引用：`.trellis/spec/backend/*.md`（质量门禁要求 suppression 必须带可核验理由）；
- 兄弟代码交叉引用：同一文件树内的类型/方法/测试名；
- 报告自身章节号：`benchmarks/WinForward.E2E.Analysis` 的 `§N` 指向生成的报告章节，并由
  golden 文件 pin 住；
- 协议/格式常量：`0x{n:X8}`、`{x:F1}`、`{rate:P0}` 等插值格式说明符；
- benchmark 自身阶段标签：`SessionSetupDecompositionBenchmarks` 的 `S1/A4/C2` 对应其方法名。

## 验证结果

| 门禁 | 结果 |
| --- | --- |
| 注释中的档案指针（全树） | **0 命中** |
| 字符串字面量中的档案指针（全树） | **0 真实命中**（59 处候选经逐条甄别均为格式说明符/阶段标签/代码行为描述） |
| pragma 尾注释中的档案指针 | **0 命中** |
| `dotnet build WinForward.slnx -c Release` | 0 Warning(s) / 0 Error(s) |
| `dotnet test WinForward.slnx -c Release` | 14 程序集、**1,663 通过 / 0 失败 / 0 跳过**（每轮与最终各测一次） |
| `python3 …/oracle-diff.py` | exit 0，51 个 slice 全部 equal，0 差异 |
| `python3 …/check-fairness.py` | exit 0，14 个 guard 全部 PASS |
| `dotnet format … --verify-no-changes` | 见最终门禁记录 |
| `jb inspectcode -e=HINT` | 见最终门禁记录（清缓存后串行重跑） |

## 交付形态

- 56 个主批次 + 2 个补漏批，**逐批一个 commit**（便于事后核查与单批回滚），
  message 均为 `refactor(comments): trim archive metadata and narration in batch NN`。
- 批次清单、派发指令与残留扫描脚本存档于 `research/`（`batches/`、`prompts/`、`DISPATCH-TEMPLATE.md`）。

## 已知非目标与遗留

- 字符串字面量中的**行为性**字符串（路径查找键 `RepoPaths.HarnessTask = "10-07-e2e-harness-refactor"`、
  golden 文件 pin 住的标题如 `### 14.7 Truncated frames …`）刻意保留：改动会破坏查找或 golden 校验。
- `benchmarks/results/**` 与 `.trellis/tasks/archive/**` 中的历史指针保留：它们是既成记录，
  不属于代码注释。
- `benchmarks/README.md` 仍引用旧 note 文本：文档正文，非代码。
