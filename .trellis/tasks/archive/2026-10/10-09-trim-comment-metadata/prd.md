# 注释精简：去掉任务编号、日期与 PRD 引用

> 父任务：`10-09-code-hygiene-rename-comments`（R2）
> 本地研究：`research/comment-metadata-inventory.md`

## Goal

把代码注释收敛为"只描述当前代码"的简明表述：读注释不需要回查任何已归档的任务、PRD 或设计
文档，就能知道这段代码在做什么、为什么这么做、在什么前提下成立。

## Background

- 代码树约有 2 万行注释；其中约 719 行带有任务档案引用（任务名、任务内条目号、日期、
  `design §x.y`），分布在 260 个文件中。
- 这些编号在任务归档后失去指代对象。例：
  `// (its forced-refresh trigger) — task 09-17 R1-B.`、
  `/// <paramref name="healthSignal"/> (task 09-17 R1-B) receives the …`、
  `// One native pool backs retained SYNs and association reset templates (B1/B2); the …`。
- 除元数据外，还存在与实现重复的叙述性注释、与实现脱节的过期注释。

## Requirements

### R2.1 删除任务档案引用

- 删除任务名与任务内条目号：`task 09-17`、`R1-A`、`R2.3`、`B11`、`P3`、`M0`、`C4`、`S3/D3` 等。
- 删除日期戳（`2026-09-30`、`measured 2026-08-27` 之类）。
- 删除 `design §3.5`、`PRD`、`AC2` 之类对规划文档条目的交叉引用。
- 删除引用测量产物路径的"证据脚注"式注释（如
  `/// <c>benchmarks/results/2026-09-30-…/README.md</c>`），但若该注释同时承载了当前代码的
  约束（如分配预算、容量下界），把约束留下、把路径去掉。

### R2.2 精简表述

- 删除与相邻代码重复的叙述性注释（把 `i++;` 读成 "increment i" 的那类）。
- 删除描述已删除代码或已失效设计的注释。
- 保留并改写为简明的动机/原因说明：
  - 不变量与前提（"为什么这里必须持锁"、"为什么这个边界必须成立"）
  - 算法、状态机、协议布局的原理与取舍
  - 边界条件与拒绝路径、失败模式与恢复语义
  - 性能与分配约束（热路径零分配、单次解析等）
  - 外部规范依据（RFC 编号与条款、Windows 行为、NDISAPI 语义）
- 改写后的注释应更短，而不是把元数据换成同长度的新句子。

### R2.3 结构保全

- XML 文档注释的标签结构（`<summary>`、`<param>`、`<returns>`、`<remarks>`、`<see cref>`、
  `<paramref>`、`<c>`）保持完整；`cref` 与 `paramref` 指向的名字必须仍然可解析。
- 被注释代码的语义不变：本任务只改注释，不改任何可执行 token。

## Acceptance Criteria

- [x] AC1 `rg -n '/[/]?.*(task [0-9]|20[0-9]{2}-[0-9]{2}-[0-9]{2}|design §|PRD)' --glob '*.cs' src tests benchmarks`
      零命中。
- [x] AC2 条目号模式 `rg -n '/[/]?.*\b[A-Z][0-9]{1,2}\b' --glob '*.cs' src tests benchmarks`
      人工逐条复核为零真实命中（需排除编译器诊断码、十六进制字面量、类型名等假阳性）。
- [x] AC3 注释行总数下降，且下降量有据可查：记录改动前后的注释行计数与逐文件差异。
- [x] AC4 `dotnet build WinForward.slnx -c Release` 零警告（含 XML 文档警告）。
- [x] AC5 `dotnet test WinForward.slnx -c Release` 与基线计数一致。
- [x] AC6 `dotnet format … --verify-no-changes` 零输出、`jb inspectcode` 零 `<Issue>`。
- [x] AC7 语义保全抽查证据：按文件分层抽样（`src/` 生产代码、`tests/`、`benchmarks/` 各若干），
      逐条给出"原注释 → 新注释 → 该注释承载的不变量/动机是否仍在"的对照记录，写入
      `research/comment-trim-audit.md`。

## Out Of Scope

- 不改任何可执行代码。
- 不删除承载真实约束的注释；"精简"不等于"清空"。
- 不重命名符号（属于兄弟任务 `10-09-rename-ipv-to-ipv`）。
- 不改 `.trellis/` 下的任务档案与 spec 文档。
- 不新增注释来解释显而易见的代码。

## Open Questions

（无阻塞项）
