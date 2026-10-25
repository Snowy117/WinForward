# 计划审查结论（blob v2 批次）

> 独立 Plan 子代理审查，2026-10-24。结论：**有条件通过**。以下为必须处理项与建议。

## P0 必须改

1. **共享常量被删**：`src/common/forwarded_blob.h`、`forwarded_matcher.h`、`tests/host/forwarded_blob_test.cpp`
   仍使用 `rule_family_any` / `rule_protocol_any` / `rule_port_any_begin/end`；v2 的 `rule_blob.h`
   不能删掉它们。保留四个旧常量（forwarded 依赖），掩码常量另起名，并加 static_assert / 测试锁住。
2. **有效行超限**：`src/common/rule_blob.h` 原 480/500，加 UTF-8 校验后预计超限；
   `tests/host/rule_blob_test.cpp` 与 `matcher_test.cpp` 也会超 500。需要拆文件：
   - 新 `src/common/rule_blob_selector.h`（selector/UTF-8 校验）；
   - 新 `tests/host/rule_blob_v2_test.cpp`（v2 负向矩阵），更新
     `src/common/CMakeLists.txt`、`tests/host/CMakeLists.txt` 与 `tests/host/rule_blob_builder.h` 的包含。
3. **控制字符口径两侧不一致**：C++ 拒绝 `0x7F..0x9F`，C# `ProcessSelector` 只拒 `<0x20` 与 `0x7F`。
   统一口径（C1 也拒绝），并加一条 C1 用例。
4. **`MaxBlockFilters` parity 方案不可实现**：`CppAbiSource` 只解析 `probe_ioctl.h` / `redirect_context.h`，
   `probe_driver.h` 含 Windows 类型。把 `max_block_filters` 移到 `probe_ioctl.h` 或新的
   `src/common/rule_limits.h`，`probe_driver.h` 用 static_assert 保持一致。
5. **`RuleBlobMaxSize` 两份**：`PolicyLimits.cs` 与 `ProbeIoctl.cs` 各一份；`verify-gates.sh` 的变异点
   在 `ProbeIoctl.cs`。要么单一权威，要么把两处都纳入 `AbiConsistencyTests`；不要留下无测试互锁的副本。

## P1 缺失工作与顺序

6. 遗漏的 C# 测试/夹具文件：`TestAppId.cs`、`ProcessPathTests.cs`、`CompilerTests.cs`、
   `RuleOrderTests.cs`、`InvalidInputTests.cs`、`ConfigurationTests.cs`、`LayoutTests.cs`、
   `BlobReader.cs`、`DifferentialCase.cs`、`VmChecks.cs`。`VmChecks` 是唯一的产品 vs 驱动顺序差分，
   要补数组 / 前缀用例。
7. 端口颠倒分层冲突：配置层 `PolicyConfigurationDocument.HostSelectors.cs` 已抛 JSON 路径错误，
   而 `ConfigurationTests` / `tools/windows/userland-e2e.ps1` 断言错误里含 `#1`。明确由谁判定并同步。
8. `CheckCommand.cs` 回归：按“有任一 process”选 Windows 解析器；纯文件名规则在 Linux 上会抛
   `PlatformNotSupportedException`。按 `Kind == Path` 判断，并加 Linux 用例。
9. wfprobe 夹具失真：
   - `BlobBuilder.cs` 的 `CorruptVersion` 写 2（v2 已合法），应写 3；
   - `NetworkPoolOutOfRange` 写 offset 32（v2 是 `selector_pool_offset`），应写 40；
   - `RuleSuite.cs` 的 block + `"ABC"/"abc"` 会先被 block 边界拒绝，通过原因不是大小写折叠；
     改用设备路径，另加 block + 文件名负向用例。
10. 差分 C++ 侧常量：`tests/host/differential_test.cpp` 的精确用例数 / 连接数、`required_case_names`
    需要同步更新，否则只改 C# `DifferentialTests` 必红。
11. 变异脚本结构性缺口：`mutation.ps1` 把文件硬拼成 `src\driver\`；`selector-utf8-guard` 的替换文本
    引用 v1 字段；`verify-gates.sh` 的 `ProcessesOverlap` 调用签名已变。需要同步改脚本。
12. 规范化缺口：`rule_blob.h` 接受 `kind=filename` + `is_directory=1`；`protocol_mask` / `family_mask`
    的 0 与 3 都表示“全部”。强制 filename → is_directory==0，并在文档里定死规范值。

## P2

13. 验收不可执行：仓库里没有“4096 条空 permit 表”；v2 下 32B/规则 + 56B 头，64KiB 上限约 2046 条。
    改成可执行值 / 可观测行为；“每条路径都生成对应 filter”没有可观测计数，只测行为。
14. 文档遗漏：`forwarded-rule-blob.md` 仍写 host 版本 1；`wfp-architecture.md` O5 仍“未开始”；
    design §4.2 未写卷根尾斜杠归一（实现已处理）；UNC / 映射盘未验证。
15. 顺序：共享 ABI + 宿主测试单独提交无法让 `check.sh --full` 变绿；Linux 纵切必须作为一个可绿单元。

## 关键文件

- `src/common/rule_blob.h`
- `src/common/matcher.h`
- `src/userland/WinForward.Userland/Policy/RuleBlobCompiler.cs`
- `src/driver/rule_table.cpp`
- `tools/windows/mutation.ps1`
