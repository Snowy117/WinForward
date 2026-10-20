# 执行计划 — C# blob 编译器

1. `src/userland/` 工程骨架；确认 devShell 里 dotnet 可用（不行就改 flake.nix）。
2. 类型镜像 + 布局断言（A1）。
3. 规则模型与编译器主体。
4. `try_open_blob` 接受性验证（A2）。
5. 差分测试接 C++ 侧（A3），边界用例（A4）。
6. 编译期非法输入拒绝（A5）。
7. 门禁：Linux 上构建 + 测试全绿；有效行检查。
8. 把 blob 契约的实际字节布局写进本任务 `research/`，供 B4b 与后续维护者对照。

不要碰 `src/driver/` 与 `tools/`。`src/common/` 与 `tests/host/` 所在的批次已提交（commit `b4bbae2`），允许在其中**新增**文件（差分测试的 C++ 消费端 + CMake 注册），但不要修改既有实现。
