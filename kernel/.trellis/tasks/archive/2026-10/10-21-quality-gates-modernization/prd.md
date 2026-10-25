# 仓库质量门禁与工具链现代化

## Goal

把散落在各次任务里的检查收敛成仓库的常设设施：宿主侧一条命令跑完所有可移植检查、失败即非零退出；驱动侧获得真正会失败的静态检查；脚本不再绑定任何一台机器。

起点是用户指出的具体缺陷：`src/driver` 中的 `block_classification` 是一个从未被调用的符号。它只是症状——本任务要修的是让这一类问题能被自动发现。

## 环境边界

仓库里的一切必须只依赖"标准工具 + 仓库自身"，不依赖任何一台具体机器：

1. 不出现固定盘符、固定 IP、固定工具安装路径、固定版本号路径。
2. 不出现"连接某台虚拟机""把文件传到某台机器"的编排逻辑——那是开发者本地环境的事，不是仓库的事。
3. 只描述"在一台装有 VS Build Tools + WDK 的 Windows 机器上该跑什么"，以及"在任意 Linux/macOS 开发机上该跑什么"。

## Background

### B1 触发缺陷

- `block_classification` 定义于 `src/driver/probe_classify.cpp:216`（匿名命名空间内），无任何调用点。
- 全仓库唯一的"引用"是 `tools/vm/mutation-test.ps1:97` 的替换文本——一条死代码的唯一使用者是声称要调用它的变异脚本，文本搜索会被骗过。

### B2 三层机制为何失效

| 机制 | 实测结论 |
|---|---|
| MSVC | 匿名命名空间内的未引用函数不产生警告；显式 `static` 的同款函数报 `C4505`。两者均在 `/W4` 下验证。 |
| clang `-Wunused-function` | 能报出同一案例，但 clang 从不编译 `src/driver/`。 |
| VS 内置 clang-tidy | 见 B5，对本驱动不可用。 |

### B3 现有资产盘点（全部可过，但无人调用）

- `.clang-format`：19 个 C++ 文件 `--dry-run --Werror` 零漂移。
- `.clang-tidy`：host 的 4 个翻译单元零用户代码告警。
- `src/userland/scripts/check-effective-lines.py`：默认目标只含 userland 与两个 test 文件，`src/driver` 不在范围；补扫后 19 文件全绿（最大 453 行）。
- `tests/host/CMakeLists.txt` 的 `wf_check`：只做 C# 夹具生成与 ctest。
- `tools/vm/build-and-test.ps1`：Windows 侧完整门禁，硬编码路径与版本。

### B4 可移植性缺陷

- `tools/vm/sync.sh` 直接调用 `windows-vm`（本机专有的虚拟机编排工具），并写死 `C:\wfbench\wfprobe`。
- 各脚本写死 VS 18 BuildTools 路径、`Windows Kits\10\bin\10.0.26100.0`（而工程依赖 10.0.28000.0）、`C:\wfbench`、`1.2.3.4:19003`。
- build / sign / install / uninstall 在三个脚本里各实现一次。
- 变异用例以逐字源码子串为锚点，任何重排或格式化都会让脚本以"锚点命中 0 次"失败。
- 目录名 `tools/vm` 把"虚拟机"这个环境事实写进了仓库。

### B5 MSVC 内置 clang-tidy（存在但不可用）

VS 18 BuildTools 自带 clang-tidy（`VC\Tools\Llvm\x64\bin\clang-tidy.exe`），MSBuild 侧以 `EnableClangTidyCodeAnalysis` + `ClangTidyChecks` 驱动。对驱动实测失败：clang-cl 不认识 `-cbstring`；`redirect_context.h` 报 `std::size_t` 缺失；随后 `static_assert(sizeof(redirect_context) == 36)` 连锁失败。

### B6 MSVC PREfast（可用）

- 该 WDK 工程 `EnableMicrosoftCodeAnalysis` 默认 true，但 `RunCppAnalysis` 为 false，普通构建不执行分析。
- 直接 `RunCodeAnalysis=true` 报 `C1250`：默认 x86 宿主编译器去加载 `...\10.0.28000.0\x86\WindowsPrefast.dll`，而 WDK NuGet 只提供 x64/ARM64。
- 加 `PreferredToolArchitecture=x64` 后成功：19.8 s、10 条告警（C28175×4、C28169×3、C28101×1、C28167×2），全部是 SAL / IRQL 注解类。

### B7 cppcheck（可用，替代自研脚本）

- cppcheck 2.22（nixpkgs 提供，跨平台）：`--enable=unusedFunction --std=c++20 --language=c++ --error-exitcode=1`。
- 不需要 WDK 头即可解析；全树 19 个 C++ 文件耗时约 3.3 s。
- 实测只报 `block_classification` 一处。`redirect_context.h` 的 `try_read_original_destination` 只被测试使用（测试 TU 含 gtest 宏，cppcheck 解析不了），用一行 `// cppcheck-suppress unusedFunction` 加理由抑制。
- 结论：死符号检查直接用 cppcheck，不写自研脚本。

### B8 CodeQL

- Microsoft 提供 `microsoft/windows-drivers` 查询包（recommended / mustrun / mustfix），是 WHCP 认证所用的一套。
- C/C++ 必须靠构建追踪建库，因此只能在 Windows 侧（有 MSVC + WDK 的机器）执行。
- 许可：当前仓库非开源，CLI 许可不覆盖；用户裁定先部署、许可后续处理，风险记录在案。

## Requirements

- **R1 单一入口**：宿主侧一条命令串起格式、静态检查、有效行、构建与测试，任一项失败即非零退出并输出汇总；区分快速档（提交前）与完整档。
- **R2 接线已有资产**：`clang-format --dry-run --Werror`、`clang-tidy`、`check-effective-lines.py` 纳入 R1；有效行默认扫描覆盖全部 C++ 源文件。
- **R3 死符号检查**：用 cppcheck 的 `unusedFunction` 覆盖 `src/`，接入 R1；按它的结论删除 `block_classification` 并改写依赖它的变异用例。不引入自研检查脚本。
- **R4 驱动静态分析**：驱动在警告即错误下构建通过、警告级别显式固定；PREfast 接入 Windows 侧门禁，存量告警清零或带理由抑制。
- **R5 Windows 侧脚本与机器解耦**：删除 `sync.sh`（含 `windows-vm` 调用）；`tools/vm` 更名 `tools/windows`；工具经 `vswhere` / Kits 目录发现；根目录由参数或环境变量提供；远程测试地址不再写死；变异锚点改为区域标记。
- **R6 规范可执行化**：`cpp-conventions.md` §5.1 的禁止清单至少有一条可执行检查，并为豁免提供可审计的声明形式。
- **R7 CodeQL**：在 Windows 侧完成安装、建库、分析，跑通 `microsoft/windows-drivers:recommended.qls`，结果落 SARIF 并接入深度档。许可问题按用户裁定暂不处理。

## Acceptance Criteria

- [ ] **AC1** 新克隆仓库、进入 devShell 后直接执行 R1 入口即可跑完；人为制造一处格式漂移时退出码非零。
- [ ] **AC2** 向 `src/driver` 注入一个未被引用的匿名命名空间函数，R1 快速档非零退出并报出文件与符号；删除后恢复。
- [ ] **AC3** `block_classification` 不再存在；原依赖它的变异用例仍能执行，且"变异生效时测试套件确实变红"。
- [ ] **AC4** `check-effective-lines.py` 默认扫描覆盖全部 C++ 源文件；新增一个超限文件会被报出。
- [ ] **AC5** 驱动在警告即错误下构建通过；注入未引用 `static` 函数时构建失败。
- [ ] **AC6** 仓库内不再出现 `windows-vm`、`C:\wfbench`、固定 IP、固定 VS/Kits 版本路径；脚本在任意安装了 VS + WDK 的 Windows 机器上可运行。
- [ ] **AC7** 提交前钩子能拦住格式或静态检查失败；安装可逆（`core.hooksPath`）。
- [ ] **AC8** 在 `src/driver` 写入一处 §5.1 禁止用法（如 `std::vector`），R6 检查非零退出；带豁免声明时通过。
- [ ] **AC9** Windows 门禁开启 PREfast 后通过；摘掉一个 `_Dispatch_type_` 注解时门禁失败。
- [ ] **AC10** CodeQL 建库与分析跑通、SARIF 非空；人为注入一个 mustfix 类缺陷能被报出。

## Out of Scope

- 远程 CI：仓库未配置 remote，本地入口 + 提交前钩子是等价物。
- 让 clang 解析驱动翻译单元（clang-cl + WDK）。
- C# 侧（`src/userland`）的检查体系改造。

## Technical Notes

- 宿主检查依赖 devShell：clang / clang-format / clang-tidy 21.1.8、cppcheck 2.22、python3、cmake、ninja、dotnet-sdk_10。
- 驱动有效警告级别实测已达 level-4（`C4505` 可触发），当前零告警；R4 的增量是显式化警告即错误 + PREfast。
- `build/` 已被 `.gitignore` 覆盖，检查使用独立的 `build/check`。
- PREfast 口径：`-p:RunCodeAnalysis=true -p:EnableMicrosoftCodeAnalysis=true -p:PreferredToolArchitecture=x64`。
- CodeQL 安装物不进入仓库；脚本只负责发现 `codeql` 可执行文件并执行建库/分析。
