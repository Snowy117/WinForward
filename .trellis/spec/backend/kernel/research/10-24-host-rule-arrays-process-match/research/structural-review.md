# 结构审查结论（合并 diff）

> 独立 Plan 子代理审查，2026-10-24 11:09。结论：**需要先修，再提交**。代码分层没有阻断性问题，
> 问题集中在「v2 ABI 单一来源未自动化」「文档/spec 未收口」「新 Windows 解析器与数组顺序没有上机差分」。

## 必须修

1. **v2 ABI 常量没有自动互锁**：`src/common/rule_blob.h`（magic/version/56/32/8/4/掩码/kind）、
   `RuleBlobLayout.cs`、`LayoutTests.cs` 手抄数字、`tools/wfprobe/Protocol.cs` 第三份。
   `CppAbiSource` 只读 `probe_ioctl.h` / `redirect_context.h`；`AbiConsistencyTests` 对夹具只锁
   三个常量。需要新增一个 v2 blob ABI 一致性测试（读 `rule_blob.h` 文本 + `RuleBlobLayout` +
   `Protocol.cs`），至少锁：version、header/rule/selector/port/network 尺寸、magic、动作值、
   kind 值、协议/族掩码、511 上限、`max_block_filters`、`rule_blob_max_size`。
2. **文档/spec 未动**（R10）：`wfp-architecture.md` §5/§7.10、`src/userland/README.md`、
   `src/driver/README.md`、`forwarded-rule-blob.md`、`csharp-conventions.md` 仍引用 v1 /
   `FwpmAppIdNormalizer`；`host-rule-blob.md` 不存在。单独文档批处理。
3. **`WindowsDevicePath` 生产实现没有成功路径测试**：现有测试只走注入解析器；VM 侧只测
   “不存在的路径失败”。需要在 `VmChecks` 用 `ForWindows()` 编译当前进程路径，断言设备路径等于
   `AppId.ForCurrentProcess()`、`is_directory=0`；对进程目录断言 `is_directory=1` 且能命中自身。
4. **`VmChecks` 仍是单值矩阵**：需要把 `RuleOrderProcessTests` 的前缀/数组矩阵搬进 VM 差分
   （产品拒绝 ⇒ 驱动拒绝；接受 ⇒ generation+1）：tools vs tools2、目录遮蔽后代、多端口相交/
   不相交、混合族。
5. **`rule_blob.h` 491/500 且职责混装**：建议拆 `src/common/rule_blob_selector.h`
   （selector/UTF-8/控制字符/长度），forwarded 专用 helper 归位；同步 CMake。若时间不足，
   至少在 spec 里记录为 follow-up，并保证新增校验不破 500。
6. **变异门禁证明不了新检查**：`verify-gates.sh` 新变异使用 `if (false)`，可能被
   warnings-as-errors 当成构建失败；`expect_mutation_red` 只看退出码，会误判。需要区分构建失败
   与测试失败，并使用运行时不可折叠的条件；C++ 负向夹具需要反向证明（删检查后对应夹具变红）。

## 建议同批处理

- **`\device\` 前缀校验**：路径型选择器必须是 `\device\` 开头的设备路径（大小写不敏感）；
  编译器、`try_open_blob`、wfprobe 三处一致，防止 `\foo\bar.exe` 装成永不命中的 block filter。
- **端口 0**：配置层已拒绝；编译器对直接模型也应拒绝（`PortRange.Single(0)`），避免程序化 API
  产出永不命中的规则。
- **预算前移**：`allocate_rule_table` 按同一公式做纯 blob 检查，避免 `enabled=0` 接受、
  `enabled=1` 拒绝的耦合。
- **`HostRuleParitySuite` 恢复**：装上“拦截 wfprobe 自己”的 block filter 后，恢复必须放在
  `try/finally`，否则任一 Check 失败会把驱动留在自阻塞状态。
- **`HostRuleParitySuite` 漏装 blob**：`ExpectChildPermit` 调用点（原文件第 74、106 行后）缺少
  `device.SetRuleBlob(...)`，导致“精确路径命中副本自身”和“目录命中深层后代”两个用例跑的是
  上一条规则。VM 实验已证实补上后 `rules` 套件全绿。
- **重复逻辑**：ASCII 折叠、文件名/前缀判定、端口合并、block 预算、selector 校验在多处各写一份；
  至少加注释互指，时间允许时抽 `rule_text.h` / `PortRangeMath` / `BlockFilterBudget`。
- **`rule_pool_index_any` 语义**：host 只用于“未命中规则序号”，forwarded 仍当 network 池哨兵；
  注释里写清，避免误用。
- **wfprobe `TestRule.Process` 语义**：已映射为 path 选择器（前缀语义），字段名与语义漂移；
  可改名 `ProcessPath` 或补注释。
- **`ControllerCheckTests.PathRuleRequiresWindows`**：在 Windows 上也会通过，原因不同；按平台
  跳过或改名。

## 关键文件

- `src/common/rule_blob.h`
- `src/userland/WinForward.Userland/Policy/RuleBlobCompiler.cs`
- `src/driver/rule_table.cpp`
- `src/userland/WinForward.Userland.Tests/Abi/AbiConsistencyTests.cs`
- `tools/windows/mutation.ps1`
- `tools/wfprobe/HostRuleParitySuite.cs`
