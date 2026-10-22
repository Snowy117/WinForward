# B1：Forwarded 规则 blob 与匹配器

## Goal

定义 Forwarded 域的跨语言规则契约，并交付两侧实现：C++ 的 `wf_forwarded_blob` v1 解码与
`match_forwarded` 匹配器，C# 的 `ForwardedPolicyTable` 模型、编译器、布局镜像与测试。为 B2
配置接线和 B4/B5 数据面提供稳定、可差分验证的规则层。

## Background

- 现有 host 规则层是 `src/common/rule_blob.h` + `matcher.h` + `src/userland/.../Policy/RuleBlobCompiler.cs`：
  按进程、远端 CIDR、远端端口、协议、地址族匹配。
- Forwarded 的匹配维度不同：**入口适配器、客户端 CIDR、目的 CIDR、远端端口、协议、地址族**，
  动作需要携带 `upstream_id`（选哪个 SOCKS5）。
- 当前仓库的 JSON 配置还没有 forwarded 域，也没有多上游；B1 只做规则层，不碰 JSON、不碰驱动、
  不碰 IOCTL。配置接线是 B2，驱动 classify/redirect 是 B4/B5。
- 已有可复用的值对象：`RemoteNetwork`（CIDR 解析/归一化）、`PortRange`、`RuleAction`、
  `RuleProtocol`、`RuleAddressFamily`，以及 C++ 侧的 `network_prefix`、`rule_action`、
  协议/族常量。B1 复用它们，不复制一套新语义。

## In Scope

1. `src/common/forwarded_blob.h`：magic `"WFB1"`、version 1、header 40 字节、rule 24 字节、
   `network_prefix` 复用 `rule_blob.h` 的 20 字节布局、常量与 `try_open_forwarded_blob`。
2. `src/common/forwarded_matcher.h`：`forwarded_connection_view`、`forwarded_match_result`、
   `match_forwarded`（纯函数、无分配、无内核依赖）。
3. `tests/host/forwarded_blob_test.cpp`、`tests/host/forwarded_matcher_test.cpp`，以及
   `forwarded_blob_builder.h` 测试构造器；加入 `tests/host/CMakeLists.txt`。
4. C# `src/userland/WinForward.Userland/Forwarded/`：
   `ForwardedPolicyRule`、`ForwardedPolicyTable`、`ForwardedBlobLayout`、`ForwardedBlobHeader`、
   `ForwardedBlobRule`、`ForwardedBlobCompiler`。
5. C# 测试：编译/布局/边界/拒绝非法；若现有差分夹具格式可复用，增加 forwarded 差分用例。
6. spec：新增 `.trellis/spec/backend/forwarded-rule-blob.md`，写明 ABI、匹配语义、校验规则。

## Out of Scope

- JSON `forwarded` 段、多上游表、`clientCidr` 的配置解析（B2）。
- 驱动 classify/redirect、IOCTL、redirect 表（B4/B5）。
- 适配器 stable ID → 接口 id 的解析（B4 前接线；B1 只定义 `ingress_adapter_id` 是 uint32）。
- IPv6 完整矩阵（B1 覆盖 v4/v6 基本匹配与族一致性，不追求所有双栈组合）。
- 进程选择器（forwarded 没有宿主进程，字段不存在）。

## Requirements

- **R1 ABI 固定**：header/rule 的尺寸、字段偏移、字节序、对齐由 `static_assert` 与 C# 布局
  断言锁定；任何改动都提升版本。
- **R2 严格解码**：`try_open_forwarded_blob` 拒绝坏 magic/version/header_size/total_size、
  段越界/未对齐/重叠、网段池不整除、规则字段越界或引用非法；失败时 `out` 保持不变。
- **R3 匹配语义**：按规则数组顺序首个命中；匹配 `ingress_adapter_id`（任意/具体）、
  `clientCidr`、`remoteCidr`、远端端口区间、协议、地址族；未命中返回 fallback 动作。
- **R4 动作约束**：fallback 只允许 permit/block；`redirect` 规则必须带 `upstream_id`；
  permit/block 规则不得带 `upstream_id`。两侧编译/解码都要拒绝违规组合。
- **R5 族一致性**：规则的地址族若具体，则 client/remote 网段的族必须与之一致；两个网段都
  具体且族不同则拒绝；family=any 时由网段族决定匹配。
- **R6 容量上限**：规则数与网段池有明确上限（与 host 层同量级或更大），超限编译期拒绝。
- **R7 两侧一致**：同一份夹具在 C# 编译结果与 C++ 解码/匹配上决策一致；差分为条件项
  （现有夹具格式可复用就做，不可复用则在报告里说明）。
- **R8 不破坏 host**：host 的 `rule_blob.h`/`matcher.h` 行为与测试不变；forwarded 新增文件
  独立编译。

## Acceptance Criteria

- [ ] **A1 C++ 解码/匹配**：`forwarded_blob_test` + `forwarded_matcher_test` 覆盖正常、边界、
      拒绝路径；`ctest` 全绿。
- [ ] **A2 C# 编译/布局**：`ForwardedBlobCompiler` 的正确性、布局镜像、非法输入拒绝；
      `dotnet run --project src/userland/WinForward.Userland.Tests -- test` 全绿。
- [ ] **A3 差分（条件项）**：若现有夹具格式可扩展，C# 生成的 forwarded 夹具在 C++ 上逐条一致；
      否则报告说明并留到 B2/B4。
- [ ] **A4 全量门禁**：`cmake --build build --target wf_check` 通过；host 既有测试不变红。
- [ ] **A5 文档**：`forwarded-rule-blob.md` 写明 ABI、匹配顺序、校验规则与容量上限。
- [ ] **A6 无越界改动**：不修改 JSON 配置、驱动、IOCTL、redirect 表；只新增规则层文件与测试。

## Notes

- 先定 ABI 再写代码；两侧不要各自发明字段。字段顺序以 `design.md` §1 为准。
- `ingress_adapter_id` 的 `0xFFFFFFFF` 是"任意"，不要用 0；upstream 的 `0xFFFFFFFF` 是"无"。
- 复用 host 的 `network_prefix`、`rule_action`、协议/族常量；forwarded 只新增 header/rule
  与 matcher，不复制常量定义。
- 本批不碰 Windows VM；全部在 Linux 上验证。
