# C# 侧 blob 编译器与布局一致性

## Goal

在 `src/userland/` 建 C# 工程，把规则模型编译成与 `src/common/rule_blob.h` 逐字节一致的 blob，并用差分测试把跨语言 ABI 锁死。

## 为什么单独一批

`rule_blob.h` 是内核与用户态之间唯一的规则传输格式（D2）。两侧一旦对不上，症状是**规则静默不生效**，不会报错。所以这个格式必须由**两侧共享的测试**锁定，而不是各写一份然后祈祷。

## Requirements

- C# 侧镜像：`[StructLayout(LayoutKind.Sequential, Pack = 4)]` 定义头部与规则结构，`Marshal.SizeOf` 与逐字段 `Marshal.OffsetOf` 断言。
- 规则模型：有序规则列表，每条含进程选择器（可选）、远端 CIDR（可选）、远端端口区间、协议、地址族、动作；外加兜底动作。
- 编译器：输入规则模型，输出 `byte[]`，满足 design.md 的全部布局不变量。
- 进程串归一小写、去重、NUL 结尾；网段记录定长 20 字节。
- **差分测试**：产出的 blob 交给 C++ 侧同一份 `try_open_blob` + `match` 消费，断言决策与 C# 侧模型一致。

## Acceptance Criteria

- [ ] A1 `Marshal.SizeOf` 头部 == 40、规则 == 16；逐字段 offset 断言与 `rule_blob.h` 一致
- [ ] A2 编译产物能被 `try_open_blob` 接受
- [ ] A3 差分测试：≥20 组规则模型 × ≥10 个连接，C# 期望决策与 C++ `match()` 结果逐条一致
- [ ] A4 边界覆盖：空规则表、多规则先后优先级、CIDR /0 与 /32、端口区间端点、进程串大小写、IPv4 与 IPv6
- [ ] A5 非法输入（进程串含非法字符、端口区间颠倒、CIDR 前缀越界）在编译期被拒绝
- [ ] A6 在 Linux 上用仓库 devShell 能构建并跑通测试
- [ ] A7 单文件有效行 ≤ 500，代码风格与仓库一致

## Notes

- 依赖 B2（`rule_blob.h` / `matcher.h`，commit `b4bbae2`）。**不依赖 B3**，可并行。
- 本批只做"模型 → blob"的编译与差分验证；配置解析、IOCTL 下发、监听中继在后续批次。
- 禁止 `git commit`。
