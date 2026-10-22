# 执行计划：B1 Forwarded 规则 blob 与匹配器

> 只新增规则层文件；除了 `tests/host/CMakeLists.txt` 与
> `WinForward.Userland.Tests/Program.cs` 的注册行，不改动既有逻辑。

## 0. 读上下文

- 父任务：`.trellis/tasks/10-23-forwarded-socks5-gateway/prd.md`、`design.md`、`implement.md`
- 本任务：`prd.md`、`design.md`
- 现有 host 规则层：
  - `src/common/rule_blob.h`、`src/common/matcher.h`
  - `src/userland/WinForward.Userland/Policy/RuleBlobCompiler.cs`
  - `src/userland/WinForward.Userland/Policy/RuleBlobLayout.cs`、`RuleBlobHeader.cs`、`RuleBlobRule.cs`
  - `src/userland/WinForward.Userland/Policy/NetworkPoolBuilder.cs`、`RemoteNetwork.cs`、
    `RuleBlobNetworkPrefix.cs`、`PolicyCompileException.cs`、`PortRange.cs`
- 测试：
  - `tests/host/rule_blob_builder.h`、`rule_blob_test.cpp`、`matcher_test.cpp`
  - `tests/host/CMakeLists.txt`
  - `src/userland/WinForward.Userland.Tests/Program.cs`、`TestRunner.cs`
- 项目规范：`.trellis/spec/backend/index.md`、`.trellis/spec/backend/cpp-conventions.md`、
  `.trellis/spec/backend/csharp-conventions.md`（若存在）

## 1. C++ 规则层

- [ ] 新增 `src/common/forwarded_blob.h`：按 `design.md` §1 定义 header/rule/view/常量/
      `try_open_forwarded_blob`，`static_assert` 锁尺寸与偏移。
- [ ] 新增 `src/common/forwarded_matcher.h`：按 §2.2 定义 connection view、match result、
      `match_forwarded`；复用 `redirect_context.h` 的 `address_family`。
- [ ] 新增 `tests/host/forwarded_blob_builder.h`：构造合法 blob 的辅助（header/rules/networks）。
- [ ] 新增 `tests/host/forwarded_blob_test.cpp`：解码正常/边界/全部拒绝路径。
- [ ] 新增 `tests/host/forwarded_matcher_test.cpp`：匹配语义/顺序/fallback/IPv6。
- [ ] 修改 `tests/host/CMakeLists.txt`：把新测试文件加入 `wf_common_tests`。
- [ ] 验证：`cmake -S . -B build`（若未配置）→ `cmake --build build` →
      `ctest --test-dir build --output-on-failure`。

## 2. C# 规则层

- [ ] 新增 `src/userland/WinForward.Userland/Forwarded/` 下 7 个文件（见 `design.md` §3）。
- [ ] `ForwardedBlobCompiler` 按 §3.1 校验、§3.2 写字节；错误消息带规则序号。
- [ ] 新增 `src/userland/WinForward.Userland.Tests/ForwardedCompilerTests.cs`、
      `ForwardedLayoutTests.cs`。
- [ ] 在 `Program.cs` 的 `RegisterAll()` 中注册两个测试类。
- [ ] 验证：`dotnet build src/userland/WinForward.Userland.slnx` →
      `dotnet run --project src/userland/WinForward.Userland.Tests -- test`。

## 3. 差分（条件项）

- [ ] 读 `DifferentialFixtureWriter.cs`、`DifferentialCases.cs`、`tests/host/differential_fixture.h`、
      `differential_test.cpp`。
- [ ] 若现有格式可扩展：加 forwarded 夹具生成与 C++ 读取断言；`emit-fixtures` 输出到同一目录。
- [ ] 若不可扩展：不做，在报告里说明原因与建议落点（B2/B4），不要硬改 host 夹具格式。

## 4. spec

- [ ] 新增 `.trellis/spec/backend/forwarded-rule-blob.md`：ABI 表、匹配顺序、校验规则、
      容量上限、与 host blob 的边界。

## 5. 全量验证

- [ ] `cmake --build build --target wf_check`（生成 C# 夹具 + 全部 ctest）。
- [ ] `dotnet build src/userland/WinForward.Userland.slnx`（零警告）。
- [ ] 确认 host 既有测试全部仍绿；没有修改 host 逻辑。

## 6. 报告格式

1. 改了哪些文件，各自一句话；
2. 关键实现决定（尤其 ABI 字段/校验顺序/复用了哪些 host 类型）；
3. 验证命令与结果（贴关键输出摘要）；
4. 计划没覆盖、需要主会话决策的点（差分是否做、容量上限是否需要调整）。

## 回滚点

- 本批只新增文件 + 两处注册行。若构建/测试失败且短时间修不好，整批回退即可，
  不影响 host 规则层与驱动。
