# 技术设计：Host UDP B1 规则层

## 1. 协议值与版本

- `rule_protocol_udp = 17`，与 `transport_protocol::udp` 一致。
- `rule_blob_version` 保持 1：规则布局不变，新增协议值是枚举扩展；旧驱动遇到 17 会整块
  拒绝（fail-closed），不会静默放行。
- `is_known_protocol` 从 `{0,6}` 扩为 `{0,6,17}`；其他值继续拒绝整块 blob。

## 2. matcher

- `connection_view.protocol` 已经是原始 IPPROTO 值；`rule_matches` 的
  `selected.protocol != any && != connection.protocol` 不需要改。
- 增加 `static_assert(static_cast<uint8_t>(transport_protocol::udp) == rule_protocol_udp)`。
- `protocol=any` 从本批起在 matcher 层覆盖 UDP；驱动要到 B2 才会把 UDP 送进 matcher。

## 3. C# 编译链

- `RuleProtocol.Udp = 17`。
- `PolicyConfigurationDocument.ParseProtocol` 接受 `"udp"`；错误文案 `any / tcp / udp`。
- `RuleBlobCompiler.ValidateProtocol` 接受 `Udp`。
- `RuleOrderValidator.Describe` 的协议文本增加 `udp`；`ProtocolsOverlap` 已按数值比较，不改。
- 差分用例加一条 `protocol=udp` 的规则：connection 12（ipv4 443 udp svchost）命中，
  TCP connections 不命中。现有 `protocol-any-includes-udp` 保留。

## 4. 驱动临时安全闸

B1 的驱动仍然在 `probe_classify_fn` 里对非 TCP 早退，不会把 UDP 送进 matcher。若此时允许
`protocol=udp` 的 block 规则通过，`block_filters.cpp` 会按 `selected.protocol` 生成 UDP 的
auth filter，报文被静默丢弃，但 classify 不会记录 blocked/事件——这是不可接受的中间态。

因此在 `allocate_rule_table` 的 blob 校验之后、返回表之前加：

```cpp
// 临时（B2 删除）：classify 尚未匹配 UDP，UDP block filter 会在没有 classify 记录的情况下
// 静默丢包。B2 转正后删除这段，并补一条 UDP block 可加载的 VM 用例。
for (rule : view.rules) {
    if (rule.action == block && rule.protocol == rule_protocol_udp) {
        DbgPrint(...);
        return nullptr;
    }
}
```

- 只拒绝 UDP block；UDP permit / redirect 规则可以加载，但 classify 早退，行为不变。
- `protocol=any` block 仍按现有 TCP 规则处理，不受影响。
- 这条闸是 B1 的已知临时代码，B2 必须删除；`probe_classify.cpp`/`rule_table.cpp` 可加
  mutation 标记便于验证。

## 5. 测试

- C++：`rule_blob_test` 17 接受、18 拒绝、UDP 字段解码；`matcher_test` UDP-only / any /
  TCP 不命中 UDP。
- C#：`CompilerTests` 字节 17；`ConfigurationTests` 解析 `"udp"`；`InvalidInputTests` 未知
  协议改 18；`TransportCases` 新增 UDP-only 差分；`RuleOrderTests` 的 `Describe` 断言
  （如有）更新。
- 验证：`tools/check.sh --full`；`dotnet run --project src/userland/WinForward.Userland.Tests -c Release -- test`。
