# Ipv4/Ipv6 标识符统一为 IPv4/IPv6

> 父任务：`10-09-code-hygiene-rename-comments`（R1）
> 本地研究：`research/ipv-inventory.md`

## Goal

把代码树中 `Ipv4`/`Ipv6` 的历史拼写统一为规范拼写 `IPv4`/`IPv6`，使同一棵树里不再有两种
地址族前缀并存。这是纯改名：编译产物与运行期语义完全不变，唯一可观察的差异是符号名本身
（以及随之变化的测试显示名）。

## Background

- 现状：`Ipv` 变体共 842 处，分布在 58 个 `.cs` 文件；**其中 839 处在标识符中部**
  （`s_clientIpv4`、`BuildIpv6TcpFrame`、`EtherTypeIpv4`、`ValidateIpv6TcpChecksum`），
  少数以 `Ipv` 开头（`Ipv6HeaderLength`、`Ipv4Mask`）。因此替换必须匹配子串而非词首。
  完整清单：`research/ipv-inventory.md`。
- 同树中既有的 `IPv4`/`IPv6` 出现约 300 处，两种拼写互相引用（例如 `AddressFamilyKind.IPv6`
  与 `Ipv6HeaderLength` 并存）。
- `IPV4`/`IPV6` 全大写变体在这棵树中不存在，不需要处理。
- 全小写 `ipv4`/`ipv6` 是合法的 camelCase 前缀（局部变量、`s_` 静态字段命名如
  `s_ipv6Source`），符合 `.editorconfig` 的 camelCase 约定，不应改动。

## Requirements

### R1.1 符号改名

- 所有以 `Ipv4`/`Ipv6` 开头的标识符改为 `IPv4`/`IPv6` 开头，保持其余词干大小写不变。
  例：`Ipv6HeaderLength` → `IPv6HeaderLength`，`Ipv4Mask` → `IPv4Mask`，
  `Ipv6UdpTryParse` → `IPv6UdpTryParse`。
- 覆盖声明、调用、`nameof`、`<see cref>`/`<paramref>`、以及散文式提及符号名之处。
- 范围：`src/`、`tests/`、`benchmarks/` 下的 `.cs` 文件。

### R1.2 文档注释同步

- XML 文档注释中的符号名引用必须与新名字一致；`<see cref="…">` 由编译器强制校验
  （`GenerateDocumentationFile` + `TreatWarningsAsErrors` 下不一致即失败）。
- 文档注释正文中指代地址族的词同步为规范形式 `IPv4`/`IPv6`；作为普通修饰词而非地址族标识时
  按句子语义判断，指向地址族即用规范形式。

### R1.3 测试显示名

- 测试方法名中的 `Ipv` 一并改名，测试显示名随之变化。这是允许的：`dotnet test` 的计数不变，
  显示名不在任何断言或过滤器中被引用（已核实无 `--filter` 或反射按名查找）。

## Acceptance Criteria

- [ ] AC1 `rg -n '\bIpv[46]\w*' --glob '*.cs' src tests benchmarks` 零命中。
- [ ] AC2 `rg -n '\bIPV[46]' --glob '*.cs' .` 零命中（不引入新的大小写分裂）。
- [ ] AC3 全小写 camelCase 前缀未被误改：`rg -c '[A-Za-z0-9_]ipv[46]' --glob '*.cs' tests benchmarks`
      的逐文件计数与 `git grep -c '[A-Za-z0-9_]ipv[46]' HEAD -- tests benchmarks` 完全一致。
      注：`src/` 本来就没有小写形式，该守卫在 src 上恒为空——src 的等价性改用下面的字节恒等证明。
- [ ] AC3b 字节恒等证明：对每个改动文件，`git show HEAD:<file> | sed 's/Ipv/IPv/g'` 与工作区
      文件逐字节相同（证明除该子串替换外无任何其他改动）。批次 A 已用此证明，批次 B 沿用。
- [ ] AC4 `dotnet build WinForward.slnx -c Release` 零警告。
- [ ] AC5 `dotnet test WinForward.slnx -c Release` 与改动前基线计数一致。基线（2026-10-09 实测，
      14 个程序集）：总计 **1,663**，逐程序集 Configuration 121 / Windows 58 / E2E 364 /
      Integration 24 / Core 63 / Runtime.Flow 183 / NdisApi 74 / Runtime.TcpRedirect 157 /
      Performance 137 / Runtime.Socks5 108 / Runtime.UdpProxy 164 / Runtime.Capture 120 /
      Protocols 72 / Analyzers 18。
- [ ] AC6 `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` 零输出。
- [ ] AC7 证据落盘：改动前后的 `rg -o '\bIpv[46]\w*'` 全量清单、改名映射表、Release 构建与
      测试输出，写入 `research/rename-evidence.md`。

## Out Of Scope

- 不改逻辑、不改布局常量、不改 P/Invoke 与 ABI 签名（`IPHelperAbi.cs` 中的原生结构字段名
  若非托管侧拼写则保持原样）。
- 不重命名与本主题无关的符号。
- 不改 `benchmarks/results/` 下的历史产物。
- 注释的元数据清理属于兄弟任务 `10-09-trim-comment-metadata`，本任务只在改符号名时顺带同步
  被引用的名字，不主动删注释内容。

## Open Questions

（无阻塞项）
