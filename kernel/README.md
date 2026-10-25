# WinForward

把 Windows 上的 TCP 连接重定向到本地代理的内核化实现：WFP callout 驱动在
`ALE_CONNECT_REDIRECT` 层按规则表改道，C# 用户态负责编译规则、接住被重定向的连接并转发
（直连或经上游 SOCKS5）。

驱动在 Windows 上用 MSVC + WDK 构建，用户态用 .NET SDK；宿主侧只做可移植的编辑、格式化与纯逻辑测试。

## 质量门禁

宿主侧的单一入口是 `tools/check.sh`。它只依赖仓库 devShell 提供的工具，不依赖任何一台机器：

```bash
nix develop             # 或 direnv allow，让 .envrc 加载 devShell
tools/check.sh --fast   # 默认档：格式、有效行、cppcheck、禁止模式
tools/check.sh --full   # 快速档 + 配置 build/check + clang-tidy + 构建并运行 wf_check
tools/check.sh --staged # 提交前：按索引内容检查暂存区的格式与禁止模式
```

| 档位 | 内容 | 预算 |
|---|---|---|
| `--fast`（默认） | clang-format 漂移、有效行上限 500、cppcheck `unusedFunction`、禁止模式 | < 5 s |
| `--full` | `--fast` + 配置 `build/check` + clang-tidy 逐 TU 检查 + `cmake --build --target wf_check` | 视机器而定（本机约 60 s） |
| `--staged` | 按索引内容检查暂存区 C++ 文件的格式与禁止模式；与 `--fast` 组合时其余快速检查照常 | < 2 s |

`--build-dir <目录>` 可替换 `--full` 的构建目录（默认 `build/check`）。

`--fast` 与 `--staged` 可组合；`--full` 与 `--staged` 不能组合（脚本以退出码 2 拒绝：前者构建工作区，后者检查索引）。

退出码只有三种：

- `0`：全部通过；
- `1`：有检查发现（格式漂移、有效行超限、死符号、禁用写法、构建或测试失败）；
- `2`：环境或参数问题（缺 `git` / `clang-format` / `cppcheck` / `python3` / `cmake` / `ninja` /
  `dotnet` / `clang-tidy`、参数非法、检查器自身报错）。环境问题不会被伪装成检查失败。

每一步输出一行 `[check] <名称> ... PASS|FAIL|SKIP`，末尾给出汇总与总耗时。`SKIP` 表示这一步
没有可检查的对象（例如暂存区没有 C++ 文件、配置失败导致 `wf_check` 未执行），不计为失败。

快速档的四项检查都做过「故意破坏后变红」的反向验证。缺工具时脚本以 2 退出，不会把
环境问题算成代码问题。

### 禁止模式检查

快速档的最后一步是 `tools/lint/forbidden.py`：按 `tools/lint/forbidden-rules.txt` 检查
`src/driver/**` 与 `src/common/**` 的禁用写法（标准库容器与字符串、异常、RTTI、`new`/`delete`）。
规则行是 `规则ID <TAB> glob <TAB> 正则 <TAB> 说明`；匹配前剥离注释与字符串，所以注释里写
`std::vector` 不会误报。规则是 blocklist：`#include <new>`、placement new（不带
`nothrow`/`align_val_t` 的 `new (…)`）、
`operator new`、`operator delete` 与 `= delete` 声明都不算命中；`delete *p`、`delete ::p`、
`delete[]` 仍然会报。规则与 `cpp-conventions.md` §5.1 的逐条对应见该文档 §8.4。

确有必要时，在命中行或它的上一行写豁免，**理由不能为空**：

```cpp
// WF-LINT-ALLOW(stl): 这里要复用共享头里的容器类型，理由写在这里
std::vector<int> values;
```

`// WF-LINT-ALLOW(stl):` 这种空理由会被判为无效豁免，检查仍会失败。

### 提交前钩子

仓库自带 `.githooks/pre-commit`，只做一件事：`tools/check.sh --fast --staged`，其中格式与
禁止模式检查的是**索引内容**（`git show :<path>` 的 blob），不是工作区。

```bash
.githooks/install.sh              # 安装：git config --local core.hooksPath .githooks
.githooks/install.sh --status     # 看当前状态
.githooks/install.sh --uninstall  # 卸载：把 local core.hooksPath 复原成安装前的值
```

安装脚本只改本仓库 local 作用域的 git 配置，不会自动改任何人的全局配置；安装前的值记在
`<git-dir>/wf-hooks-path.state`，卸载时据此复原。钩子没生效时先看 `git config --get core.hooksPath`
是不是 `.githooks`。确实需要绕过时用 `git commit --no-verify`——它只跳过钩子，
`tools/check.sh --fast` 仍是最终判据。

## 本地配置

仓库只保存「在任何同类机器上都成立」的东西；本机事实放在不入库的 `wf.local.json` 与 `local/`：

- `wf.local.json`：本机专属配置，字段契约见 `wf.local.example.json`；
- `local/`：本机专用编排脚本（同步、连接虚拟机等），不入库。

```bash
cp wf.local.example.json wf.local.json   # 然后按本机实际情况填写
```

字段：

| 键 | 内容 |
|---|---|
| `vm.session` | 本机虚拟机会话名 |
| `vm.root` | Windows 上的仓库根目录 |
| `remote` | 测试目标地址与端口 |
| `codeql_home` | CodeQL CLI 安装目录（未配置时从 PATH 查找） |

`wf.local.json` 不存在不是错误：依赖它的脚本按约定输出 `SKIP` 并说明缺哪个键；
`tools/check.sh` 的所有检查项都不依赖它。宿主侧检查脚本（`tools/check.sh`、
`src/userland/scripts/check-effective-lines.py`）不写死盘符、IP、工具安装路径或固定版本号路径；
Windows 侧脚本在 `tools/windows/` 里：机器事实靠发现/参数；C# 工具的目标框架输出路径仍按工程写死，属于后续批次。

## Windows 侧门禁

在装有 Visual Studio Build Tools + WDK 的 Windows 机器上，以管理员 PowerShell 运行：

```powershell
pwsh -File tools/windows/gate.ps1 -Remote <测试目标地址:端口>
pwsh -File tools/windows/mutation.ps1 -Remote <测试目标地址:端口> -OnlyIndex 0,3
```

`-Root` 默认由脚本位置推到仓库根；`-Remote` 没有默认值，先取参数再取 `WF_REMOTE`。
MSBuild、signtool、accesschk 都按机器上的安装情况发现（C# 工具的 `net10.0` 类输出路径仍按工程写死，不在本批范围内）。PREfast 分析接在 gate 里，
判定看输出里的 `warning C28xxx` 与 Summary 的 `Warning(s)`，不只看 MSBuild 退出码。
仓库不负责把代码送到那台机器——同步与连接虚拟机属于 `local/` 或本机配置，
细节见 `tools/windows/README.md`。

### CodeQL 深度档

`tools/windows/codeql.ps1` 是独立的深度档，不在 `gate.ps1` 的默认流程里：它追踪一次 MSBuild
驱动构建生成 CodeQL C++ 数据库，跑 `microsoft/windows-drivers` 的 recommended 套件，结果写到
`build/codeql/wf.sarif`。

仓库不包含 CodeQL CLI（bundle 约 667 MB，不进仓库）。机器上解压好之后，用 `-CodeqlHome`、
`CODEQL_HOME` 或 PATH 指向 `codeql.cmd` 所在目录。首次运行会按需下载
`microsoft/windows-drivers@1.10.0` 与 `microsoft/cpp-queries@0.0.5`（后者必须显式装，
否则套件解析失败）。

```powershell
pwsh -File tools/windows/codeql.ps1 -CodeqlHome <bundle 里的 codeql 目录>
pwsh -File tools/windows/codeql.ps1 -CodeqlHome <...> -SkipDatabase   # 复用数据库，只重跑分析
```

一次完整流程实测（16 核 / 2.8 GB 虚拟机）：建库约 52–68 s（冷树首次带 `-restore` 约 74 s），
分析约 114–120 s，数据库约 22 MB，SARIF 约 400 KB。数据库、SARIF 与日志在 `build/codeql/`
（已 gitignore）；MSBuild 的中间产物照常落在 `src/driver/obj/` 与 `src/driver/x64/`（同样已 gitignore）。

WDK 头里的 `__ptr32` 会让 CodeQL 2.27.3 的 C++ 前端报错退出；脚本通过 `--extractor-options-file`
只给提取器前端加 `-D__ptr32=`，真实编译不受影响。分析后还会校验 SARIF 里的
`cpp/extractor/summary`（`extractor-successes > 0` 且 `extractor-failures = 0`），抽取失败的数据库
不会因为 0 条结果就 PASS。

退出码：`0` 分析完成、0 条结果且抽取自检通过；`1` 有结果（`CODEQL-RESULT=FINDINGS results=N`），
或建库/分析/抽取自检失败（`CODEQL-RESULT=FAIL`）；`2` 环境/依赖缺失（`CODEQL-RESULT=ENV-MISSING`）。
**不要只看退出码或 0 命中**：2026-10-21 的基线上有 14 条既有结果，判断回归要看 SARIF 里的
`ruleId` 与位置。细节见 `tools/windows/README.md`。

## 目录

| 路径 | 内容 |
|---|---|
| `src/driver/` | WFP callout 驱动（内核方言，MSVC + WDK） |
| `src/common/` | 内核与用户态共享的 ABI 与可移植核心 |
| `src/userland/` | 规则编译、控制器、重定向中继与测试 |
| `tests/host/` | 宿主上的 C++ 单测与跨语言差分测试 |
| `tools/` | 宿主门禁、Windows 侧脚本与测试替身 |
| `.githooks/` | 提交前钩子与安装/卸载脚本 |

构建与测试的细节见 `src/driver/README.md`、`src/userland/README.md`。
