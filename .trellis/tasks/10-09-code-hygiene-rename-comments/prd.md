# 代码树命名与注释规范化整理

## Goal

统一代码树中 IPv4/IPv6 相关标识符的拼写，并把注释收敛为"只描述当前代码"的简明表述，使
维护者读到的每一个名字和每一行注释都能自解释，不需要回查任何已归档的任务、PRD 或设计文档。

行为零变化是硬约束：本次整理不得改变任何运行期语义。

## Background

- `Ipv4`/`Ipv6` 是历史拼写，与本仓库既有的规范形式 `IPv4`/`IPv6`（`AddressFamilyKind.IPv4`、
  `IPv6Any` 等）并存，同一棵树里两种拼写互相引用，读起来分裂。真实分布见
  `10-09-rename-ipv-to-ipv/research/ipv-inventory.md`：**842 处**，其中 **839 处在标识符中部**
  （`s_clientIpv4`、`BuildIpv6TcpFrame`、`EtherTypeIpv4`），仅少数以 `Ipv` 开头
  （`Ipv6HeaderLength`、`Ipv4Mask`）。
- 注释里混入了对任务档案的引用（`task 09-17 R2.3`、`(P3)`、`(B11)`、`(R4)`、`design §3.6`、
  `2026-09-30` 等）。这些编号在任务归档后失去指代对象，维护者无法理解。真实分布见
  `10-09-trim-comment-metadata/research/comment-metadata-inventory.md`：注释共 21,154 行，
  命中档案指针的 **609 行**，分布在 **234 个文件**。

## Requirements

### R1 命名规范化（子任务 `10-09-rename-ipv-to-ipv`）

- 代码树中所有以 `Ipv4`/`Ipv6` 开头的符号名统一为 `IPv4`/`IPv6` 开头，覆盖 `src/`、`tests/`、
  `benchmarks/` 三个目录下的 `.cs` 文件。
- 同步更新所有引用点：声明、调用、`nameof`、XML 文档注释的 `<see cref>`/`<paramref>` 文本、
  以及对符号名的散文式提及。
- 已符合 .NET camelCase 约定的全小写形式（`ipv4`、`ipv6`、`s_ipv6Source` 等）保持不变。

### R2 注释精简（子任务 `10-09-trim-comment-metadata`）

- 删除注释中的任务档案引用：任务名与任务内的编号（`R1-A`、`B11`、`P3`、`M0`、`C4` …）、日期、
  PRD 条目号、`design §x.y` 之类的交叉引用。
- 删除与当前实现重复的叙述性注释、已与实现脱节的过期注释、以及描述已删除代码的注释。
- 保留并改写为简明的动机/原因说明：不变量与前提、算法或布局原理、边界与拒绝条件、失败模式、
  性能与分配约束、外部规范（RFC / Windows 行为）依据。
- XML 文档注释保持结构完整（`<summary>`、`<param>`、`<returns>`、`<see cref>` 等）且语义不缩水。

### R3 交付纪律（两个子任务共同）

- 每个子任务独立验证、独立归档。
- 门禁：`dotnet build WinForward.slnx -c Release` 零警告、`dotnet test WinForward.slnx -c Release`
  与基线一致、`dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore`
  零输出、`jb inspectcode -f=Xml -e=HINT` 报告零 `<Issue>`。

## Acceptance Criteria

- [ ] AC1 `rg -n '\bIpv[46]\w*' --glob '*.cs' src tests benchmarks` 零命中（`Ipv` 变体在 `.cs`
      文件中彻底消失）。
- [ ] AC2 注释中不再出现任务编号、任务名、日期或 PRD/design 交叉引用模式：
      `rg -n '/[/]?.*(task [0-9]|\b[A-Z][0-9]{1,2}\b|20[0-9]{2}-[0-9]{2}-[0-9]{2}|design §|PRD)' --glob '*.cs'`
      的人工复核结论为"零真实命中"（`\b[A-Z][0-9]\b` 的假阳性如 `CS1574`、`0x0A1` 需逐条排除）。
- [ ] AC3 `dotnet build WinForward.slnx -c Release` 零警告（`TreatWarningsAsErrors` 下即零错误）。
- [ ] AC4 `dotnet test WinForward.slnx -c Release` 与本次整理开始前记录的基线完全一致
      （总数与逐项目计数皆相同；测试的显示名随方法名变化不影响计数）。
      基线（2026-10-09 实测，14 个程序集全部通过、零跳过）：总计 **1,663**，逐程序集
      Configuration 121 / Windows 58 / E2E 364 / Integration 24 / Core 63 / Runtime.Flow 183 /
      NdisApi 74 / Runtime.TcpRedirect 157 / Performance 137 / Runtime.Socks5 108 /
      Runtime.UdpProxy 164 / Runtime.Capture 120 / Protocols 72 / Analyzers 18。
- [ ] AC5 两个代码门禁命令全部通过：`dotnet format … --verify-no-changes` 零输出、
      `jb inspectcode` 零 `<Issue>`。
- [ ] AC6 语义保全抽查：从本次改写的注释中抽取样本，确认每一条仍能独立说明"这段代码为什么
      这么写"或"这段代码在什么前提下成立"，没有把不变量简化成同义反复。

## Out Of Scope

- 不改动行为：不重构逻辑、不调整算法、不改常量取值、不改 ABI 布局与 P/Invoke 签名。
- 不改动全小写普通词汇（`ipv6` 作为类型名/变量名前缀时若已是 camelCase 约定则保留）。
- 不改动 `.trellis/` 下的任务档案、spec 文档与 workspace 记录本身（spec 中若引用旧符号名，
  仅在子任务实施时按需同步）。
- 不改动 `benchmarks/results/` 下的历史测量产物（其中的旧拼写是既成记录）。

## Children

| 子任务 | 交付物 |
| --- | --- |
| `10-09-rename-ipv-to-ipv` | R1：`.cs` 中 `Ipv`→`IPv` 的机械改名与编译/测试等价证明 |
| `10-09-trim-comment-metadata` | R2：注释去元数据化与精简，附带语义保全抽查证据 |

## Notes

- 父任务只负责源需求集、子任务映射与最终集成验收，本身不直接改代码。
- 子任务的验收证据写入各自的 `implement.md` / `check.jsonl` 复盘。
