# 执行计划 — 可移植核心

1. `src/common/rule_blob.h`：格式、断言、`try_open_blob`。
2. `src/common/matcher.h`：`connection_view`、`match`。
3. 测试工具：在测试里写一个最小 blob 构造器（不放进产品头）。
4. `tests/host/rule_blob_test.cpp`、`tests/host/matcher_test.cpp`。
5. 接进 `src/common/CMakeLists.txt` 与 `tests/host/CMakeLists.txt`。
6. 跑门禁：build、ctest、clang-format、clang-tidy、有效行检查。
7. 在 `.trellis/spec/backend/wfp-architecture.md` 的 §5 补上 blob 契约的一行索引。

产物全部在 `src/common/` 与 `tests/host/`，不要碰别处。
