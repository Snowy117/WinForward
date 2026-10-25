# 执行计划：Host UDP B1

## 1. C++ 共享层

- [ ] `src/common/rule_blob.h`：`rule_protocol_udp = 17`；`is_known_protocol` 接受 17；
      注释写明枚举扩展与 fail-closed；`rule_blob_version` 不变。
- [ ] `src/common/matcher.h`：加 UDP `static_assert`。
- [ ] `tests/host/rule_blob_test.cpp`：17 接受；未知协议从 17 改 18；UDP 规则解码/字段断言。
- [ ] `tests/host/matcher_test.cpp`：UDP 规则只命中 UDP；any 命中 UDP；TCP 规则不命中 UDP。
- [ ] 验证：`tools/check.sh --full`。

## 2. C# 规则编译链

- [ ] `src/userland/WinForward.Userland/Policy/RuleProtocol.cs`：`Udp = 17`，注释更新。
- [ ] `Configuration/PolicyConfigurationDocument.cs`：解析 `"udp"`；schema 注释/错误文案。
- [ ] `Policy/RuleBlobCompiler.cs`：`ValidateProtocol` 接受 UDP。
- [ ] `Policy/RuleOrderValidator.cs`：`Describe` 支持 udp。
- [ ] `Tests/CompilerTests.cs`：UDP 字节 17。
- [ ] `Tests/ConfigurationTests.cs`：`"udp"` 解析。
- [ ] `Tests/InvalidInputTests.cs`：未知协议改 18。
- [ ] `Tests/TransportCases.cs`：`protocol=udp` 只命中 connection 12。
- [ ] 如有 `RuleOrderTests` 的协议文案断言，补 udp。
- [ ] 验证：`dotnet run --project src/userland/WinForward.Userland.Tests -c Release -- test`。

## 3. 驱动临时安全闸

- [ ] `src/driver/rule_table.cpp`：在 `allocate_rule_table` 里拒绝 `action=block &&
      protocol=udp` 的规则，`DbgPrint` 说明 B2 删除；不影响 TCP/any。
- [ ] 加 `<mutation id="udp-block-temporary-guard">` 区域（可选，B2 删除时一并清理）。
- [ ] 代码审查确认：UDP permit/redirect 可加载；classify 仍早退；无 ABI 改动。

## 4. 验证与交付

- [ ] `tools/check.sh --fast`；`tools/check.sh --full`。
- [ ] C# 测试全过；现有 TCP 规则/差分测试不退化。
- [ ] `git status` 只包含本批文件；不 commit（主会话提交）。
- [ ] 报告：改了哪些文件、验证命令与结果、临时闸位置、B2 需要删除的清单。

## 回滚点

- 本批只动 shared/C#/host tests + 一处 driver 临安闸；回滚直接 revert 本批提交。

## 交付记录（2026-10-21）

- 实现完成，独立检查结论：**通过**（无阻断、无必须修；4 条建议）。
- 改动 16 个文件：计划内 14 个 + 必需的 `DifferentialTests.cs` / `tests/host/differential_test.cpp`
  用例计数 32→33（不更新会让差分覆盖门禁变红）。
- 验证：`tools/check.sh --fast` 5/5；`ctest --test-dir build/check` 64/64；C# 131 用例中
  129 过，2 个失败是 HEAD 就存在的实验 ABI 镜像缺口（`ioctl_udp_probe_*`），B1 未引入；
  `tools/check.sh --full` 7/8，唯一失败同源，`wf_check` 在 dotnet 处短路。
- 临时安全闸：`src/driver/rule_table.cpp` 的 `udp-block-temporary-guard`，只拒绝
  `action=block && protocol=udp`；B2b 删除并补 VM 用例。它没有登记进 `mutation.ps1`，
  当前是惰性标记，这是已知覆盖缺口。
- 已知中间态：`protocol=any` block 仍按 TCP 下发；`fallback_action=block` 的无条件 filter
  在 B1 仍会静默拦 UDP（B0 之前就存在），B2b 的 classify 转正后才会记录。
- 检查建议的处置：错误文案补负向测试、C# 端针对 B1 临时闸的 fail-fast 都留到 B2a/B3；
  不在 B1 扩范围。
