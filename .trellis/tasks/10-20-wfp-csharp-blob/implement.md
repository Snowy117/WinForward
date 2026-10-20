# 执行计划 — C# blob 编译器

**状态：本批已完成。** 下面保留原计划顺序，每一步补上落地结果与复现命令。

1. **工程骨架 + 工具链**
   `src/userland/` 下两个工程（`WinForward.Userland` 产品、`WinForward.Userland.Tests` 测试）
   加 `Directory.Build.props`（`net10.0`、nullable、warnings-as-errors）与 `WinForward.Userland.slnx`。
   `flake.nix` 的 devShell 加了 `dotnet-sdk_10`（pinned nixpkgs 上是 10.0.401，约 220 MiB），
   并设置 `DOTNET_CLI_TELEMETRY_OPTOUT=1`、`DOTNET_NOLOGO=1`；C# 侧不再依赖宿主 profile 里的 dotnet。
   约定写进 `.trellis/spec/backend/csharp-conventions.md`。

2. **类型镜像 + 布局断言（A1）**
   `RuleBlobHeader` / `RuleBlobRule` / `RuleBlobNetworkPrefix` 全部
   `[StructLayout(LayoutKind.Sequential, Pack = 4)]`，字段用 `public` 字段以保持 blittable。
   `LayoutTests` 断言 `Marshal.SizeOf` 40 / 16 / 20、逐字段 `Marshal.OffsetOf`、
   `Unsafe.SizeOf` 与 `Marshal.SizeOf` 一致、`Pack == 4`、以及一段 40 字节头部黄金序列。

3. **规则模型与编译器主体**
   `PolicyTable`（有序规则 + 兜底动作）→ `RuleBlobCompiler.Compile` → `byte[]`。
   段序 `header | rules | 进程串池 | 网段池`，段首 4 字节对齐；进程串按 UTF-8 落盘、经
   `IProcessPathNormalizer` 归一化（当前实现只折叠 ASCII 大小写）、去重、NUL 结尾，
   索引是池内字节偏移；网段记录 20 字节、主机位清零、按记录去重、索引是 20 的倍数；
   空表的零长度段落在 `total_size` 端点。

4. **`try_open_blob` 接受性验证（A2）**
   由 C++ 侧 `Differential.BlobsOpen` 完成：32 个 blob 全部被 `try_open_blob` 接受，
   并逐组对照清单里的 `rule_count`、`fallback_action`、视图长度。

5. **差分测试接 C++ 侧（A3）+ 边界用例（A4）**
   方案 1（C# 写夹具、C++ gtest 消费）。夹具是 TAB 分隔的 `manifest.txt` 加每组一个 `.blob`，
   格式定义在 `tests/host/differential_fixture.h` 头注释里。
   32 组用例 × 15 条连接 = 480 条「连接 → 决策」断言，覆盖空表、优先级、CIDR `/0` 与 `/32`、
   端口端点、进程串大小写、中文路径、进程 + 网段组合、IPv4/IPv6、兜底动作等。
   用例数与连接数在两侧都是**精确值**断言，另有一组必需用例名单；变异测试确认过这套门禁会红。

6. **编译期非法输入拒绝（A5）**
   未知动作/协议/族、端口区间颠倒、规则族与网段族不一致、null 规则/表；
   进程串的空串、控制字符、NUL、孤立代理项在 `ProcessSelector.Parse` 拒绝；
   CIDR 前缀越界与格式错误在 `RemoteNetwork.Parse` 拒绝。
   归一化后的进程路径超过 **511 字节**（UTF-8，不含 NUL）由 `RuleBlobCompiler` 拒绝 ——
   内核缓冲区是 512（含 NUL），超限路径会被整条丢弃、规则永久静默不命中，必须挡在编译期。

7. **门禁**
   - `dotnet build src/userland/WinForward.Userland.slnx`：0 warning 0 error；
   - `dotnet run --project src/userland/WinForward.Userland.Tests -- test`：50 条全绿；
   - `cmake --build build`：`-Wall -Wextra -Wpedantic -Werror` 通过；
   - `ctest --test-dir build`：62 条全绿（含既有 58 条）；
   - `cmake --build build --target wf_check`：一条命令串起「生成夹具 → 全套 ctest」，退出码 0；
   - 新增 C++ 文件 `clang-format --dry-run --Werror` 与 `clang-tidy -p build` 均无诊断；
   - 全部新增源文件有效行 ≤ 500（最大是 `tests/host/differential_fixture.h` 的 349 行）；
     检查脚本 `python3 src/userland/scripts/check-effective-lines.py` 已验证会在 501 行时退出 1。

8. **契约文档**
   `.trellis/tasks/10-20-wfp-csharp-blob/research/blob-contract.md`：实测尺寸与偏移、
   两段真实字节、编译期规整规则、夹具格式、差分测试与变异测试记录、留给 B4b 的注意事项。

## 新增 / 修改文件

- 新增：`src/userland/`（两个工程、`.gitignore`、`README.md`、20 个 Policy 源文件、
  17 个测试源文件）
- 新增：`tests/host/differential_fixture.h`、`tests/host/differential_test.cpp`
- 修改：`tests/host/CMakeLists.txt`（注册 `wf_differential_tests` 与 `wf_check` target）、
  `flake.nix`（devShell 加 `dotnet-sdk_10`、两个 DOTNET_* 环境变量，并把 CC/CXX 移到 shellHook）
- 新增文档：`.trellis/spec/backend/csharp-conventions.md`、本任务 `research/blob-contract.md`

## 复现

```bash
# 一条命令：生成夹具 + 构建 + 全部 ctest
cmake -S . -B build -G Ninja
cmake --build build --target wf_check

# 等价的分步执行（顺序不能反）
dotnet run --project src/userland/WinForward.Userland.Tests -- test   # 写夹具到 build/diff-fixtures
cmake --build build
ctest --test-dir build --output-on-failure
```

## 反向验证（门禁真的会红）

| 验证 | 做法 | 结果 |
|---|---|---|
| 差分测试会红 | 把一条期望动作改错后重新生成夹具 | 红，指出 case / 连接序号 / 期望与实际动作 |
| 夹具缺失不 skip | 删掉 `build/diff-fixtures` 直接跑 C++ | 红，并打印生成命令 |
| 旧夹具假绿 | 改 C# 期望但不重跑生成，直接 `ctest` | **绿**（这是洞） |
| `wf_check` 堵洞 | 同样只改 C# 期望，跑 `wf_check` | 红，退出码 8 |
| 覆盖数是精确值 | 删一个用例 / 一条连接 | `CoverageFloor` 报出精确值不符 |
| 用例名单 | 重命名一个必需用例 | `CoverageFloor` 指出缺哪一类 |
| devShell 自带 dotnet | 去掉 `dotnet-sdk_10` 的 flake 副本 + 只留 `/nix/store` 的 PATH | `command -v dotnet` → 找不到；换回本仓库 flake → 指向 devShell 的 store 路径 |
