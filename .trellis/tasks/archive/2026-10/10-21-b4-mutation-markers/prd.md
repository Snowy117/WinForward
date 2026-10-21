# B4 · 变异锚点改为区域标记

> 父任务：`.trellis/tasks/10-21-quality-gates-modernization`（design §5「变异锚点」）

## Goal

把 16 条变异用例的逐字源码锚点换成 `// <mutation id="…">` 区域标记，使变异测试对 clang-format、重排、注释改动免疫；16 条用例全部仍能变红。

## Scope

- `src/driver/*.cpp`：为每个可变异区域加一对标记，共 16 个区域（`gate-redirect-state` 被两条用例共用，故共 17 次替换）。
- `tools/windows/mutation.ps1`：`$mutations` 从 `Old`/`New` 改为 `Id`/`New`；匹配逻辑改为按 id 定位区域并整体替换。
- 不改测试套件、断言、阈值；不新增/删除用例。

## Acceptance

- **A1** 每个 id 恰好一对标记，且至少被一条用例引用；允许一条区域被多条用例共用（如 `gate-redirect-state`）。
- **A2** 脚本按 id 定位；命中数 ≠ 1 立即失败；全部用例结束后校验文件与原始内容逐字节一致。
- **A3** 全量 16 条用例跑完，全部 `MUTATION PASS`，基线全绿。
- **A4** 反向验证：把某个标记的 id 改错 → 脚本报"找不到区域"并非零退出；恢复。
- **A5** `clang-format --dry-run --Werror` 通过（标记不破坏格式），`tools/check.sh --fast` PASS。
- **A6** 运行结束后 `git status --porcelain -- src/driver` 为空。

## Out of Scope

- 新增或删除变异用例。
- 禁止模式检查与提交前钩子（B5）。
- CodeQL（B6）。

## 已知取舍

- `permit-branch` 与 `permit-counter` 的旧锚点互相包含，无法在不支持嵌套的前提下各自拥有区域，因此拆成两个相邻区域：`permit-branch` 只负责“分支退化成 block”，计数器改动由 `permit-counter` 专职覆盖。两条用例合起来的覆盖与旧版一致，变红原因更聚焦。
