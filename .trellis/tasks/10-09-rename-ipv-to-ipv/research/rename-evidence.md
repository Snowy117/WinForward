# 改名阶段验收证据（Ipv4/Ipv6 → IPv4/IPv6）

任务：`.trellis/tasks/10-09-rename-ipv-to-ipv`（父任务 `10-09-code-hygiene-rename-comments` R1）
日期：2026-10-09

## 范围与计数

| 维度 | 数值 |
| --- | ---: |
| 受影响文件 | 58 个 `.cs`（`src/` 12、`tests/` 39、`benchmarks/` 7） |
| 子串替换次数（脚本计数） | 850 |
| token 级普查（独立扫描） | 842 处（标识符 839、注释 3、字符串字面量 0） |
| 未改动 | `IPV4`/`IPV6` 全大写（树中不存在）、全小写 camelCase（`ipv4`/`ipv6`/`s_ipv4Source` 等 15 种、约 88 处） |

两个计数的差异来自同一行内的多次出现：改名脚本按子串统计，普查按 token 去重计数。

## 方法与等价性证明

替换规则只有一条：子串 `Ipv` → `IPv`。工具 `.trellis/tasks/10-09-rename-ipv-to-ipv/research/rename_ipv.py`
（`--plan` 只报告、`--apply` 落盘，残留检查非零即失败）。

**字节恒等证明**（比"diff 只含注释行"更强）：对每个改动文件，

```bash
diff -q <(git show HEAD:<file> | sed 's/Ipv/IPv/g') <file>
```

全部为空——工作区文件与 HEAD 施加同一替换后逐字节相同，证明除该子串外无任何改动。

**编译期强制**：`<see cref="…">`/`<paramref>` 引用在 `TreatWarningsAsErrors` + 文档生成下不一致即构建失败，
因此文档注释里的符号名同步有编译器兜底，而非依赖人工检查。

## 批次与提交

| commit | 批次 | 内容 |
| --- | --- | --- |
| `488e06f` | A | `src/` 12 文件 / 116 处（Protocols 8、Windows 4） |
| `fe97c11` | B | `tests/` + `benchmarks/` 46 文件 / 734 处（含 TestSupport 3 文件） |
| `85b199a` | — | 5 个 backend spec 的 9 处符号引用同步 + PRD 基线修正 |

批次边界说明：TestSupport 被多个测试项目共享，必须与 tests 同批改名，否则中间态无法编译——
因此原定的 A/B/C/D 四批合并为 A（src）与 B（其余）两批。

## 验证结果

| 门禁 | 结果 |
| --- | --- |
| `rg -n 'Ipv[46]' --glob '*.cs' src tests benchmarks` | 零命中 |
| `rg -n '\bIPV[46]' --glob '*.cs'` | 零命中（未引入新的大小写分裂） |
| 小写守卫：`rg -c '[A-Za-z0-9_]ipv[46]'` vs `git grep -c … HEAD` | 逐文件计数完全一致（未过度改写） |
| `dotnet build WinForward.slnx -c Release` | 0 Warning(s) / 0 Error(s) |
| `dotnet test WinForward.slnx -c Release` | 14 程序集、**1,663 通过 / 0 失败 / 0 跳过**，与改动前基线逐程序集一致 |
| `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | exit 0、空输出 |

基线修正说明：最初人工汇总为 1,650，遗漏了 `WinForward.Protocols.Tests`（72）与
`WinForward.Analyzers.Tests`（18）。改名前后两轮独立测量均为 1,663，逐程序集计数相同，
等价性结论不受影响。

## 特殊核查

- **ABI 边界**：`src/WinForward.Windows/IPHelperAbi.cs` 的 4 处（`Ipv4AddressOffset`、`Ipv6AddressOffset`、
  `Ipv6ScopeIdOffset`、`DecodeIpv6Address`）逐个核对为托管侧 C# 标识符；同文件的原生结构字段
  （`LocalAddress`、`ScopeId`、`InterfaceLuid` 等）、P/Invoke 入口与封送布局均未触碰。
- **测试显示名**：测试方法名随改名变化，显示名随之改变。已确认无 `--filter`、无按名反射查找，
  故不影响任何断言或筛选。
- **BDN 参数属性**：`TcpRedirectDataPathBenchmarks.Ipv6` → `IPv6` 由 BenchmarkDotNet 反射注入，
  改名后列名随之变化，测试计数不受影响。

## 遗留（已移交注释阶段）

- `benchmarks/results/**` 与 `.trellis/tasks/archive/**` 中的旧拼写是既成历史记录，按任务范围不改。
- 注释与打印文本中的档案指针（任务名、条目号、日期、`design §`）不属于本阶段，由
  `10-09-trim-comment-metadata` 处理；字符串字面量部分已获用户授权一并清理。
