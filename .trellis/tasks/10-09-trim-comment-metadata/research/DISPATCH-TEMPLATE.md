# 注释精简子代理指令（55 批共用）

> 本文件是**投递给子代理的 prompt 模板**，不是任务交付物。
> `{BATCH_ID}` 替换为批次号，`{FILE_LIST}` 替换为该批文件清单（见同目录 `batch-NN.txt`）。
> 父任务：`10-09-code-hygiene-rename-comments`；本阶段任务：`10-09-trim-comment-metadata`。

---

Active task: .trellis/tasks/10-09-trim-comment-metadata

你是 WinForward 仓库（`/home/paff/Projects/WinForward`）注释精简任务的执行者，负责批次
`{BATCH_ID}`。不要派遣其他子代理。不要 commit。

## 绝对禁令（违反即任务失败）

1. **禁止用脚本自动编辑文件。** 不得使用 `sed`、`awk`、`perl -i`、`python -c` 写文件、
   批量正则替换等任何自动化手段修改源码。每一处注释的改写都必须经过你的阅读理解，
   通过编辑工具逐条完成。允许用只读命令（`rg`、`wc`、`git diff`）来查看与统计。
2. **禁止改动任何可执行代码。** 只改注释文本。不得改动标识符、常量值、字符串字面量、
   表达式、语句结构、空行与行尾。唯一例外：把注释里提到的旧拼写 `Ipv4`/`Ipv6` 同步为
   `IPv4`/`IPv6`（若该批仍有残留）。
3. **不得把承载真实约束的注释删成空白。** "精简"不是"清空"。

## 你要处理的文件

```
{FILE_LIST}
```

## 判据：删除什么

- **档案指针**：任务名与任务内条目号（`task 09-17`、`R1-A`、`R2.3`、`B11`、`P3`、`M0`、`C4`、
  `S3/D3`、`(B1/B2)` …）、日期（`2026-09-30`、`measured 2026-08-27`）、
  `design §3.5`、`PRD`、`AC2` 等对已归档规划文档的引用。
- **证据脚注**：指向测量产物路径的引用（`<c>benchmarks/results/2026-09-30-…/README.md</c>`）。
  但若同一句还承载当前代码的约束（容量下界、分配预算、超时值、上限值），**保留约束、去掉路径**。
- **冗余叙述**：与紧邻代码重复的注释（把 `i++;` 读成 "increment i" 的那类）、
  对显而易见的赋值/调用/返回做解说的注释。
- **过期内容**：描述已删除代码、已失效设计、已不存在的成员的注释。

## 判据：保留什么，并且改写得更简短

- **不变量与前提**：为什么必须持锁、为什么这个边界必须成立、为什么这里的顺序不能换。
- **动机**：为什么选这个方案而不是更直观的那个（含实测到的取舍）。
- **边界与拒绝路径**：什么输入被拒绝、拒绝后状态如何、为什么必须 fail-closed。
- **性能与分配约束**：热路径零分配、单次解析、池化与租借平衡、缓冲上限。
- **外部规范依据**：RFC 条款、Windows/NDISAPI 行为、协议字段布局。
- **所有权与生命周期**：谁创建、谁释放、谁等待谁。

改写要求：**更短**，不是把元数据换成同长度的新句子。一条注释只讲一件事；
如果一条长注释里既有元数据又有真实约束，删元数据、留约束、必要时拆成两行。

## 结构保全

- XML 文档注释的标签必须完整且配对：`<summary>`、`<param>`、`<returns>`、`<remarks>`、
  `<see cref="…">`、`<paramref name="…">`、`<c>`、`<para>`。不得删除标签只留正文。
- `cref` / `paramref` 指向的名字必须仍然可解析——编译（`TreatWarningsAsErrors`）会因此失败，
  所以改完必须构建通过。
- 若某条注释读完拿不准它承载的是"真实约束"还是"冗余叙述"，**保留原文**并在报告里列为
  `UNSURE`。

## 步骤

1. 读取你的每个文件。
2. 逐条改写注释（编辑工具，一处一次）。
3. 统计：`git diff --stat -- {FILE_LIST}`
4. 证明没有动代码：
   `git diff -U0 -- {FILE_LIST} | rg '^[+-]' | rg -v '^(\+\+\+|---)' | rg -v '^[+-]\s*(///?|//|\*)'`
   **必须为空**（除注释行外没有任何增删）。若不为空，说明你改了可执行代码——立刻改回，
   并在报告里说明。
5. 统计注释行变化（只读）：
   `wc -l {FILE_LIST}` 与 `rg -c '^\s*//' {FILE_LIST}`

## 报告格式（严格照此，纯文本）

```
BATCH: {BATCH_ID}
FILES: <n> processed
TRIMFILES: <n> files with at least one edit>
EDITS: <n> comment rewrites>
DELETED: <n> comments removed entirely>
XML_STRUCTURE: <ok | problems>
CODE_TOKENS: <clean | offending lines>
UNSURE: <none | file:line — why>
RESIDUAL_METADATA: <none | file:line — pattern still present>
NOTES: <one or two lines, only if something genuinely surprising>
```
