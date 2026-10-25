# Host 规则 blob v2 质量 follow-up

> TODO / 后续任务。来源：任务 `10-24-host-rule-arrays-process-match` 的
> `research/final-check.md` §8 与 `research/structural-review.md`。这些项不阻断 v2 交付，
> 但会削弱回归保护或增加后续修改成本。

## 背景

Host 规则 blob v2（规则数组、文件名/精确路径/目录前缀、block 边界与预算）已交付并通过
Linux 8/8 + 变异 25 红/4 绿 + WinLTSC `GATE-RESULT=PASS`。最终检查结论通过，但留下以下质量项。

## 待办

1. **C++ 负向矩阵的逐检查变异证明**
   - `rule_blob.h` 新增的 selector kind / is_directory / 端口 / 族 / 对齐 / 段重叠等检查，
     目前只有 `selector-utf8-guard` 一条 Windows 变异；其余只有“被拒绝”断言。
   - 为每条新增检查做“删掉该检查 → 对应夹具变红”的反向证明（可用一个只编译
     `tests/host` 的临时变异循环，或逐条手动记录）。
   - 修 `tests/host/rule_blob_validation_test.cpp` 中仍存在的双违反夹具（最终检查问题 1 已修
     一条，复查其余 `SCOPED_TRACE` 是否只违反一条）。
2. **有效行拆分**
   - `src/common/rule_blob.h` 500/500；拆出 `rule_blob_selector.h`（selector/UTF-8/控制字符/长度）。
   - `src/driver/rule_table.cpp` 495/500；按需拆 order/budget helper。
   - 更新 `src/common/CMakeLists.txt`、`tools/windows/mutation.ps1` 标记位置。
3. **边界夹具**
   - C++ 补 DEL（0x7F）控制字符夹具与“511 字节接受”夹具。
   - `CompilerLimitTests` 的协议/族预算因子已隔离；复查是否还有同类“总量已超、因子无法证明”的断言。
4. **Windows 路径解析边界**
   - UNC / 映射盘 / 卷根的句柄法归一化 E2E；`HostRuleParitySuite.DevicePathForDirectory` 对卷根
     `TrimEnd('\\')`；`VmChecks` 可直接断言 `DevicePath == AppId.ForCurrentProcess()`。
5. **互锁与命名**
   - `rule_pool_index_any` / `Protocol.InvalidRuleIndex` 纳入 ABI 一致性测试；
     `rule_pool_index_any` 注释已说明 host/forwarded 双重语义，后续改常量时保持。
   - `TestRule.Process` 改名 `ProcessPath` 或明确注释其前缀语义；`tools/wfprobe/AppId.cs` 注释
     与产品句柄法区分。
   - `host-rule-blob.md` §9 的测试文件路径（`Abi/` 子目录）修正。
6. **路径 selector 的内部 `/`**
   - 目前只拒绝非 `\device\` 前缀与尾部 `\`；手工 blob 可能含内部 `/`，装上永不命中的 block
     filter。可选：解码期拒绝 `/`，或在 spec 里明确“producer 责任”。
7. **文档未验证边界**
   - UNC / 映射盘、长路径（>MAX_PATH）的句柄法输出；`tools/wfprobe/AppId.cs` 仍用
     `FwpmGetAppIdFromFileName0` 作为夹具参照值（仅夹具用途）。

## 不做

- classify-enforced block / sink / 顺序架构：见任务 `10-24-block-classify-enforced-sink`。
- Forwarded blob 的数组化。

## 参考

- `.trellis/tasks/10-24-host-rule-arrays-process-match/research/final-check.md` §8
- `.trellis/tasks/10-24-host-rule-arrays-process-match/research/structural-review.md`
- `.trellis/spec/backend/host-rule-blob.md`
