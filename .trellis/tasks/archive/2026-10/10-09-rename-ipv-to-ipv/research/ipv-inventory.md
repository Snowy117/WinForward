# Ipv 命名清单（token 级普查）

普查时间：2026-10-09（本任务开始前的工作树状态）
方法：Python 逐行扫描 `src/`、`tests/`、`benchmarks/` 下的 `.cs`，按 token 归类
（区分标识符 / 字符串字面量 / 注释；排除行内字符串内容）。脚本 `/tmp/inv_scan2.py`。

## 结论摘要

| 维度 | 数量 |
| --- | --- |
| `Ipv[46]` 总出现 | 842 |
| 其中标识符 | 839 |
| 其中字符串字面量 | 0 |
| 其中注释 | 3 |
| 受影响文件 | 58 |

- `IPV4`/`IPV6` 全大写变体：**0 处**。
- 全小写 `ipv4`/`ipv6`：15 种 token、约 88 处，均为合法 camelCase 前缀，**保留**。

## 关键发现：大头在 token 中间，而不是词首

`Ipv` 绝大多数出现在标识符中部（`s_clientIpv4`、`BuildIpv4TcpFrame`、`EtherTypeIpv4`、
`ValidateIpv6TcpChecksum`），只有少数以 `Ipv` 开头（`Ipv6HeaderLength`、`Ipv4Mask`）。

这决定了替换不能只匹配词首：任何 `Ipv4`/`Ipv6` 子串都要改，且改写后要保持单词边界正确
（`s_clientIpv4` → `s_clientIPv4`，`EtherTypeIpv4` → `EtherTypeIPv4`）。

## 按出现次数排序的完整 token 表

| 次数 | token |
| ---: | --- |
| 191 | `s_destIpv4` |
| 168 | `s_clientIpv4` |
| 36 | `BuildIpv4TcpFrame` |
| 23 | `CreateIpv4TcpFrame` |
| 20 | `isIpv6` |
| 18 | `CreateIpv4UdpFrame` |
| 15 | `isIpv4` |
| 15 | `s_destIpv6` |
| 15 | `BuildIpv6TcpFrame` |
| 14 | `s_clientIpv6` |
| 12 | `BuildIpv4TcpSyn` |
| 11 | `Ipv6HeaderLength` |
| 11 | `RandomIpv4` |
| 11 | `s_newIpv4Source` |
| 11 | `s_newIpv4Dest` |
| 10 | `CreateIpv6UdpFrame` |
| 9 | `Ipv4HeaderLength` |
| 9 | `BuildIpv4UdpFrame` |
| 9 | `RandomIpv6` |
| 9 | `s_newIpv6Source` |
| 9 | `s_newIpv6Dest` |
| 7 | `CreateIpv6TcpFrame` |
| 7 | `Ipv6` |
| 7 | `BuildIpv6TcpFrameWithHopByHop` |
| 7 | `BuildIpv4Fragment` |
| 6 | `Ipv4Mask` |
| 6 | `WithIpv4Flags` |
| 5 | `EtherTypeIpv4` |
| 5 | `EtherTypeIpv6` |
| 5 | `DecodeIpv6Address` |
| 5 | `s_otherIpv4` |
| 5 | `ValidateIpv4TcpChecksum` |
| 4 | `TryParseIpv4` / `TryParseIpv6` |
| 4 | `TryFindIpv6Transport` |
| 4 | `BuildIpv6Fragment` |
| 4 | `ValidateIpv4HeaderChecksum` / `ValidateIpv6TcpChecksum` |
| 4 | `BuildIpv6TcpSyn` |
| 3 | `RewriteIpv6Tcp` / `RewriteIpv4Tcp` |
| 3 | `Ipv4AddressOffset` / `Ipv6AddressOffset` / `Ipv6ScopeIdOffset` |
| 3 | `s_otherIpv6` |
| 3 | `ValidateIpv6TcpChecksumAt` |
| 3 | `SetIpv4HeaderChecksum` / `SetIpv6TcpChecksum` |
| 2 | `Ipv6FragmentHeader` / `IsIpv4Fragment` / `IsIpv6Fragment` |
| 2 | `TryRewriteIpv4` / `TryRewriteIpv6` / `TryRewriteIpv4Tcp` / `TryRewriteIpv6Tcp` |
| 2 | `RewriteIpv4TcpFull` / `RewriteIpv6TcpFull` |
| 2 | `BuildIpv4` / `BuildIpv6` / `WriteIpv4Header` / `WriteIpv6Header` |
| 2 | `Ipv4ExpectedMutableOffsets` / `Ipv6ExpectedMutableOffsets` / `Ipv6ExtensionExpectedMutableOffsets` |
| 2 | `ValidateIpv6TcpChecksumWithHopByHop` |
| 2 | `RecomputeIpv4TcpChecksumField` |
| 2 | `BuildIpv4NonFirstFragment` |
| 2 | `SetIpv4TcpChecksum` |
| 2 | `BuildIpv6UdpFrame` |
| 1 | 约 45 个测试方法名（`RoundTripIpv4RestoresOriginalFrameByteForByte` 等） |

## 特殊项

- `Ipv6`（7 次，独立 token）：出现在 `TcpRedirectDataPathBenchmarks` 的 BDN 参数属性
  `public bool Ipv6 { get; set; }`。BDN 通过反射注入 `[Params]`，改名后属性名（即参数列名）
  随之变化，测试基线计数不受影响。
- `EtherTypeIpv4`/`EtherTypeIpv6`：测试常量名，非协议字段名，可安全改名。
- 3 处注释命中：为散文正文中的拼写，随文改写。

## 需要保留（不动）

全小写 camelCase 前缀，共 15 种：`ipv6`(29)、`s_ipv4Source`(8)、`s_ipv6Source`(7)、
`ipv6Length`(7)、`ipv4`(6)、`s_ipv4Destination`(4)、`s_ipv4Dest`(4)、`s_ipv6Dest`(4)、
`ipv6Written`(4)、`_ipv6Frame`(4)、`s_ipv6Destination`(3)、`ipv4Written`(3)、
`ipv6PayloadLength`(3)、`ipv4Length`(2)。

这些符合 `.editorconfig` 的 camelCase 约定（局部变量、`s_` 静态字段、`_` 实例字段），
改成 `iPv` 会违反约定并降低可读性。

## 附带确认

- 无 `--filter` / 反射按测试名查找：测试显示名可随方法名变化。
- `cref`/`paramref` 引用由编译器校验，`TreatWarningsAsErrors` 下不一致即构建失败，因此
  文档注释同步有强制保障。
