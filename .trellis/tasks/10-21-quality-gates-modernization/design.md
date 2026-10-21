# 设计：质量门禁与工具链现代化

## 1. 原则

1. **门禁必须能失败**：每个检查都要有一条"故意破坏后必须变红"的验证步骤（`cpp-conventions.md` §9.4）。
2. **分层**：提交前的检查秒级完成，构建与 Windows 侧检查另行分档，否则会被 `--no-verify` 绕过。
3. **只用标准工具**：clang-format / clang-tidy / cppcheck / python3 / pwsh / MSBuild / PREfast / CodeQL。不写自研检查器——自研检查器的误报和盲区要自己维护，标准工具的规则库和社区在维护。
4. **仓库与机器解耦**：仓库只放"在任何一台同类机器上都成立"的东西；机器事实（会话名、远程地址、安装路径）放在 gitignored 的本地配置里。

## 2. 配置与边界

### 2.1 两层结构

| 位置 | 是否入库 | 内容 |
|---|---|---|
| 仓库脚本与配置 | 是 | 检查逻辑、工具发现、默认参数 |
| `wf.local.example.json` | 是 | 本地配置的字段契约与示例 |
| `wf.local.json` | 否（`.gitignore`） | 本机的具体值 |
| `local/` | 否（`.gitignore`） | 本机专用编排脚本（同步、连 VM 等） |

### 2.2 字段契约

```json
{
  "vm": { "session": "windows-vm-xxxx", "root": "C:\\wfbench\\wfprobe" },
  "remote": "1.2.3.4:19003",
  "codeql_home": "C:\\tools\\codeql"
}
```

读取规则：

- 文件不存在 = 没有本机配置，**不是错误**；依赖它的档位输出 `SKIP` 并说明缺哪个键。
- 键存在则优先于环境变量；环境变量优先于内置默认值。
- 脚本不得因为缺配置而 `exit 1`（那是环境问题，退出码 2 或 SKIP）。

### 2.3 硬约束

- 仓库内不得出现 `windows-vm`、`C:\wfbench`、局域网/公网固定 IP、`Visual Studio\18`、`10.0.26100` 这类字样（`git grep` 可验证）。
- Windows 侧脚本的 `-Root` 默认值由 `$PSScriptRoot` 推导到仓库根，不依赖配置。
- 宿主侧不实现"把代码送到 Windows 机器"——那是 `local/` 的事。

## 3. 入口与分层

`tools/check.sh`（宿主）：

| 模式 | 内容 | 预算 |
|---|---|---|
| `--fast`（默认） | 格式、有效行、cppcheck、禁止模式 | < 5 s |
| `--full` | fast + 配置构建 `build/check` + clang-tidy + `wf_check` | < 3 min |
| `--staged` | 格式与禁止模式只看暂存区文件（提交前钩子） | < 2 s |

Windows 侧：`tools/windows/gate.ps1`（构建 + 签名 + 安装 + 测试）与 `tools/windows/mutation.ps1`（变异测试）。两者都由人在 Windows 机器上执行，仓库不负责把代码送过去。

退出码：`0` 通过、`1` 有发现、`2` 环境缺失。

## 4. 宿主检查项

### 4.1 格式

`git ls-files -z '*.cpp' '*.h' '*.hpp' '*.inl' | xargs -0 clang-format --dry-run --Werror`。现状零漂移。

### 4.2 有效行

沿用 `src/userland/scripts/check-effective-lines.py`，默认目标改为 `src/` + `tests/`，按扩展名过滤。现状全绿（最大 453 行）。

### 4.3 clang-tidy

`--full` 中按 `build/check/compile_commands.json` 逐 TU 执行 `clang-tidy -p build/check --warnings-as-errors='*'`，任一诊断即失败。不接 `CMAKE_CXX_CLANG_TIDY`，避免拖慢日常构建。

VS 内置 clang-tidy 不用于驱动：实测 clang-cl 不认识 `-cbstring`，`redirect_context.h` 缺 `std::size_t`，随后 `static_assert` 连锁失败。

### 4.4 cppcheck（死符号，新增）

```bash
cppcheck --enable=unusedFunction --std=c++20 --language=c++ \
  --inline-suppr --error-exitcode=1 --quiet \
  -I src/common -I src/driver \
  $(git ls-files 'src/*.cpp' 'src/*.h')
```

- 覆盖范围是 `src/`：宿主编译器已经用 `-Wall -Werror` 覆盖 `tests/host`，而 `src/driver` 是编译器看不到的部分。
- 不需要 WDK 头即可解析；全树 19 个文件约 3.3 s，`src/` 更快。
- 已知误报只有一处：`redirect_context.h` 的 `try_read_original_destination` 只被测试使用。处理方式是在定义处写 `// cppcheck-suppress unusedFunction` 并注明理由，配合 `--inline-suppr`——抑制出现在被抑制的代码旁边，不是一张集中白名单。
- 检查器自身的验收：在未修改的代码上必须恰好报出 `block_classification`。

### 4.5 禁止模式

`tools/lint/forbidden.py` + `tools/lint/forbidden-rules.txt`（每行 `规则ID <TAB> glob <TAB> 正则 <TAB> 说明`）：

| 规则 | 模式 | 依据 |
|---|---|---|
| `stl` | `std::(vector\|string\|map\|unordered_map\|unordered_set\|deque\|list\|set\|shared_ptr\|function)` | §5.1 |
| `exceptions` | `\b(throw\|try\|catch)\b` | §5.1 |
| `rtti` | `\b(dynamic_cast\|typeid)\b` | §5.1 |
| `heap` | `\bnew\b` | §5.1（`src/common` 的 placement new 用豁免） |

豁免与 cppcheck 保持一致：命中行或上一行写 `// WF-LINT-ALLOW(规则ID): 理由`。

### 4.6 驱动静态分析（Windows 侧）

**警告即错误**：`wfprobe.vcxproj` 增加 `<WarningLevel>Level4</WarningLevel>` 与 `<TreatWarningAsError>true</TreatWarningAsError>`。当前零告警，注入未引用 `static` 函数即以 `C2220` 失败。

**PREfast**：

```
msbuild wfprobe.vcxproj -restore -t:Rebuild -p:Configuration=Debug -p:Platform=x64 \
  -p:RunCodeAnalysis=true -p:EnableMicrosoftCodeAnalysis=true -p:PreferredToolArchitecture=x64
```

两个前置条件缺一不可：`RunCodeAnalysis=true`（默认并不执行），`PreferredToolArchitecture=x64`（否则 x86 宿主找不到 WDK 的 x86 Prefast 插件，`C1250`）。

基线 10 条：C28101×1 加 `DRIVER_INITIALIZE`；C28169×3 补 `_Dispatch_type_`；C28167×2 补 `_IRQL_requires_*`；C28175×4 是 WDM 正常写法，`#pragma warning(suppress: 28175)` 加理由。

**门禁判定不能只看退出码**：`/WX` 不会把 PREfast 告警提升为错误——实测摘掉 `_Dispatch_type_` 后 MSBuild 仍报 `Build succeeded / 0 Error(s)`、退出码 0。Windows 门禁必须解析 `warning C28xxx` 行或 `-clp:Summary` 的 `Warning(s)` 计数，任一非零即失败。

## 5. Windows 侧脚本

目录 `tools/windows/`（原 `tools/vm/`，删掉 `sync.sh`）。所有脚本只回答一个问题：**在这台 Windows 机器上跑什么**。

- `WfBuild.psm1`：`Resolve-MSBuild`（`vswhere -latest -products * -find MSBuild\**\Bin\MSBuild.exe`；不带 `-products *` 找不到 VS 18 BuildTools）、`Resolve-SignTool`（`Windows Kits\10\bin\*\x64\signtool.exe` 取最高版本）、`Build-Driver`、`Install-Driver`、`Uninstall-Driver`、`Invoke-Prefast`、`Invoke-WfSuite`。
- `gate.ps1`（原 `build-and-test.ps1`）：构建 → 签名 → 安装 → 各套件 → 卸载。
- `mutation.ps1`（原 `mutation-test.ps1`）：变异用例。
- 参数：`-Root` 默认 `$PSScriptRoot\..\..`；`-Remote` 无默认值，取 `-Remote` → `$env:WF_REMOTE` → 报错并说明这是测试目标地址。
- 安装物（测试证书、WDK）由机器的前置条件提供，脚本只发现、不安装。

**变异锚点**：逐字源码子串改为区域标记。

```cpp
// <mutation id="redirect-branch">
    const bool redirect_wanted = result.action == rule_action::redirect;
// </mutation>
```

16 条用例覆盖 17 个区域。脚本按 `id` 定位并整体替换，命中数不为 1 立即失败；全部用例结束后校验文件与原始内容逐字节一致。

## 6. CodeQL

在 Windows 侧执行（必须追踪 MSVC 构建）：

```
codeql database create build/codeql/db --language=cpp --command="<msbuild 命令>"
codeql database analyze build/codeql/db \
  "microsoft/windows-drivers:windows-driver-suites/recommended.qls" \
  --format=sarif-latest --output=build/codeql/wf.sarif
```

- `codeql` 可执行文件由 `codeql_home` 或 PATH 发现；仓库不下载、不提交二进制。
- 许可：当前仓库非开源，CLI 许可不覆盖；用户裁定先部署，风险记录在 §9。
- 依赖包需显式补齐：`microsoft/windows-drivers@1.10.0` 之外还要 `microsoft/cpp-queries@0.0.5`，否则 suite 解析失败。

实测（2026-10-21，CLI 2.27.3）：

| 步骤 | 结果 |
|---|---|
| 下载 bundle | 667 MB / 40 s |
| 建库（含追踪构建） | 56 s |
| 分析（recommended 106 条查询） | 127 s |
| 库内容 | 11,274 个函数；`probe_classify.cpp` 的 22 个函数全部在库 |
| 当前代码命中 | 0 |

两个已知噪声，均不影响成库：提取器在 `wdm.h:7909` 报 `POINTER_32`（`__ptr32`）解析错误；追踪构建时 `link.exe` 找不到 `Wdmsec.lib` / `fwpkclnt.lib`（不在 `LIB` 路径上），提取器在链接阶段退出码 3。

**0 命中不能直接当门禁**：必须先做注入实验证明它会变红（implement P6 的必做项）。

## 7. 文件布局

```
tools/check.sh                     # 宿主入口
tools/lint/forbidden.py            # 4.5
tools/lint/forbidden-rules.txt
tools/windows/WfBuild.psm1         # 5
tools/windows/gate.ps1
tools/windows/mutation.ps1
tools/windows/codeql.ps1           # 6
.githooks/pre-commit               # check.sh --fast --staged
.githooks/install.sh
wf.local.example.json              # 2.2 契约
```

`.gitignore` 增加 `wf.local.json` 与 `local/`。

## 8. 兼容性与迁移

- `check-effective-lines.py` 的 CLI 不变，默认目标变宽（现状全绿）。
- `build/`、`cmake-build-debug/` 原样保留；检查使用独立 `build/check`。
- Windows 脚本改名后参数名保持不变，文档同步更新；`sync.sh` 删除，其职责移交 `local/`。
- 生产代码改动三处：删除 `block_classification`；17 个变异区域标记；一处 cppcheck 抑制。

## 9. 风险与回滚

| 风险 | 处置 |
|---|---|
| cppcheck 误报/漏报 | 抑制写在定义处并附理由；已知盲区（模板、宏拼接）在脚本文档中注明。 |
| cppcheck 解析不了某些 WDK 语法 | 只覆盖 `src/`；解析失败会以 `syntaxError` 出现，不会被当成通过。 |
| `/WX` 在 WDK 升级后冒新告警 | 预期行为，门禁失败即暴露；回滚只需删两行。 |
| 区域标记污染源码 | 17 处、每处 2 行；不接受可退回子串锚点，但需接受重构即失效。 |
| CodeQL 许可 | 已记录；仓库开源后自然消解。 |
| `local/` 缺失 | 只是相关档位 SKIP，不影响宿主检查。 |

## 10. 验收映射

| 验收 | 设计 |
|---|---|
| AC1 | §3 入口与退出码 |
| AC2 | §4.4 cppcheck |
| AC3 | §5 变异锚点 + 删除死函数 |
| AC4 | §4.2 |
| AC5 | §4.6 警告即错误 |
| AC6 | §2 配置与边界、§5 |
| AC7 | §7 钩子 |
| AC8 | §4.5 |
| AC9 | §4.6 PREfast |
| AC10 | §6 CodeQL |
