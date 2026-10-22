# B4a：驱动 L2 骨架与 Forwarded pass/block

## Goal

在现有 `wfprobe.sys` 上增加 MAC 帧层（L2）callout，下发 B1 的 `wf_forwarded_blob`，按入口
ifIndex、client/remote CIDR、端口、协议匹配并执行 pass/block；提供 Windows 适配器
stable ID/名字 → ifIndex 的解析，以及 `wfprobe forwarded-rules` 验证套件。为 B4b 的 DNAT/回程
打好驱动骨架。

## Background

- B1 已交付 `wf_forwarded_blob` v1 与 `match_forwarded`；B2 已交付配置解析、多上游、
  `ForwardedUpstreams`。
- B3 spike（已归档）证明：netvsc 上 `FWPS_LAYER_INBOUND/OUTBOUND_MAC_FRAME_NATIVE` 可见、
  `FwpsInjectMacReceiveAsync0`/`SendAsync0` 可用；但：
  - MAC 层 `inFixedValues` 没有 MAC/EtherType/IP/端口，必须自己解析 NBL；
  - 入向 layer 58、出向 layer 59；
  - 防环必须显式 `FwpsQueryPacketInjectionState0`；
  - B3 是“加法注入”，原帧消费/改写是 B4 的硬约束；
  - LWF 对注入帧的可见性未测。
- 本批只做“看见 + 匹配 + pass/block”；DNAT、redirect 表、查询 IOCTL、回程注入是 B4b；
  用户态 listener/中继是 B4c。

## In Scope

1. 驱动新增 MAC 帧层 callout（inbound/outbound `MAC_FRAME_NATIVE`），注册/注销与现有
   ALE callout 共存；只处理 forwarded 规则，不动 host 路径。
2. 转发规则 blob 下发：新 IOCTL/ABI 版本（v4），规则表原子替换 + generation；复用
   `src/common/forwarded_blob.h` 与 `forwarded_matcher.h`。
3. 入口匹配：`ingress_adapter_id`（ifIndex）、client/remote CIDR、远端端口、协议、地址族；
   首个命中；未命中走 fallback（permit/block）。
4. pass/block 执行：pass = 原帧 `FWP_ACTION_PERMIT`；block = `FWP_ACTION_BLOCK` 丢帧
   （本批不注入 RST；RST 留 B4b）。
5. 计数/事件：per action、per adapter、per rule；诊断可读。
6. Windows 适配器解析：实现 B2 的 `IAdapterIdResolver`，把配置里的 stable ID/名字解析成
   ifIndex；解析失败在配置期拒绝。
7. `wfprobe forwarded-rules` 套件 + Linux 客户端验证。
8. 驱动 README / spec / ABI 布局断言。

## Out of Scope

- DNAT、redirect 表、原始目的地查询 IOCTL、回程 SNAT、帧注入（B4b）。
- 用户态 listener、SOCKS5 中继、upstream 选择（B4c）。
- UDP（B5）。
- 成本 benchmark（B6）。
- External switch / 物理 NIC / 嵌套 VM 覆盖。

## Requirements

- **R1 共存**：L2 callout 与现有 ALE/host 路径互不影响；host 既有测试与 E2E 仍绿。
- **R2 匹配**：入口 ifIndex（`0xFFFFFFFF` = 任意）、client/remote CIDR、远端端口、协议、
  地址族；顺序首个命中；未命中 fallback。
- **R3 pass/block**：pass 原帧放行；block 丢帧且计数；block 不注入 RST（客户端表现为
  超时/连接失败），明确写进文档。
- **R4 防环**：classify 遇到 `FwpsQueryPacketInjectionState0` 为 self-injected 的帧直接
  permit；B4b 的注入路径在此骨架上接。
- **R5 ABI**：新 IOCTL/结构版本化；C++ `static_assert` + C# 布局断言；host ABI 不变。
- **R6 适配器**：`IAdapterIdResolver` 的 Windows 实现返回 ifIndex；解析失败/0 结果给
  domain-qualified 错误。
- **R7 卸载**：callout 注销按现有重试规则；失败不留死 callout。
- **R8 观测**：至少 per-action 计数；规则 generation 可回读；事件可选但要有计数。

## Acceptance Criteria

- [ ] **A1 共存**：L2 callout 注册/卸载干净；host 既有 `wfprobe rules` / E2E 仍 PASS。
- [ ] **A2 blob 下发**：forwarded blob 下发后回读规则数/fallback/generation 一致；非法 blob
      整块拒绝且旧表不变。
- [ ] **A3 pass**：Linux 客户端 ping/TCP 经过入口，`permitted` 计数增加；连接正常。
- [ ] **A4 block**：命中 block 的 TCP 连接失败/超时，`blocked` 计数增加；不注入 RST。
- [ ] **A5 匹配**：多规则顺序、ifIndex、client/remote CIDR、端口、协议各一条断言。
- [ ] **A6 fallback**：未命中走 fallback permit/block；计数可区分。
- [ ] **A7 适配器解析**：配置 names → ifIndex；解析失败在配置期报错。
- [ ] **A8 工具/门禁**：`wfprobe forwarded-rules` 在 VM 上 PASS；C#/C++ 测试与
      `tools/check.sh --full` 通过。
- [ ] **A9 文档**：驱动 README/spec 记录 L2 层字段、layer 58/59、block 语义、防环与 B4b 边界。

## Notes

- 严格按 B3 research §6.1 的六条 B4 硬约束做：原帧消费/改写留给 B4b；本批 classify 必须
  能区分“命中 block”和“未命中”；不要照抄 spike 的端口触发。
- 先只做 inbound/outbound 的计数与 pass/block，不碰注入；B4b 再接 DNAT/回程。
- 适配器 id 就用 ifIndex；不要引入第二套 id 空间。
