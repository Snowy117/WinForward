# 可移植核心：规则 blob 与 matcher

## Goal

把"规则表"做成一个纯数据 + 纯函数的可移植核心：用户态编译出 blob，内核只解码执行。全部逻辑在 Linux 上可用 gtest 覆盖，不依赖 WFP 或 WDK。

## Requirements

- 在 `src/common/` 新增 `rule_blob.h`：格式定义、布局 `static_assert`、解码与校验。
- 在 `src/common/` 新增 `matcher.h`：给定 blob 视图与连接事实，返回首个命中的动作与规则序号。
- 内核方言约束：无异常、无 RTTI、无 STL 容器，只用 `<cstddef>` `<cstdint>` `<limits>` `<type_traits>`。
- 不使用动态分配；解码只产出指向原始缓冲区的偏移与指针。
- 进程选择器与规则字符串在编译期已规整为**小写**；运行时比较大小写不敏感（输入也先小写化）。
- 边界处理：blob 被截断、magic/版本不符、偏移越界、规则数与池不一致 —— 一律返回失败，不读越界。

## Acceptance Criteria

- [x] `rule_blob.h` / `matcher.h` 布局断言齐备（尺寸、`offsetof`、`alignof`）
- [x] gtest 覆盖：正常命中、首条命中优先级、无命中回退、截断 blob、magic 不符、偏移越界、端口区间端点、CIDR 的 /0 与 /32、进程串大小写、空规则表
- [x] Linux 上 `cmake --build` + `ctest` 全绿（58/58，含三项重叠夹具）
- [x] `clang-format --dry-run --Werror` 与 `clang-tidy` 干净
- [x] 单文件有效行 ≤ 500

## Notes

- 本批**不改** `src/common/redirect_context.h`（另一批在用，避免并发冲突）。
- 禁止 `git commit`。
