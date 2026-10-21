# WinForward

把 Windows 上的 TCP 连接重定向到本地代理的内核化实现：WFP callout 驱动在
`ALE_CONNECT_REDIRECT` 层按规则表改道，C# 用户态负责编译规则、接住被重定向的连接并转发
（直连或经上游 SOCKS5）。

驱动在 Windows 上用 MSVC + WDK 构建，用户态用 .NET SDK；宿主侧只做可移植的编辑、格式化与纯逻辑测试。

## 质量门禁

宿主侧的单一入口是 `tools/check.sh`。它只依赖仓库 devShell 提供的工具，不依赖任何一台机器：

```bash
nix develop             # 或 direnv allow，让 .envrc 加载 devShell
tools/check.sh --fast   # 默认档：格式、有效行、cppcheck
tools/check.sh --full   # 快速档 + 配置 build/check + 构建并运行 wf_check
tools/check.sh --staged # 提交前：按索引内容检查暂存区里的 C++ 文件格式
```

| 档位 | 内容 | 预算 |
|---|---|---|
| `--fast`（默认） | clang-format 漂移、有效行上限 500、cppcheck `unusedFunction` | < 5 s |
| `--full` | `--fast` + `cmake -S . -B build/check -G Ninja` + `cmake --build --target wf_check` | 视机器而定 |
| `--staged` | 按索引内容检查暂存区 C++ 文件的格式；与 `--fast` 组合时其余快速检查照常 | < 2 s |

`--build-dir <目录>` 可替换 `--full` 的构建目录（默认 `build/check`）。

`--fast` 与 `--staged` 可组合；`--full` 与 `--staged` 不能组合（脚本以退出码 2 拒绝：前者构建工作区，后者检查索引）。

退出码只有三种：

- `0`：全部通过；
- `1`：有检查发现（格式漂移、有效行超限、死符号、构建或测试失败）；
- `2`：环境或参数问题（缺 `git` / `clang-format` / `cppcheck` / `python3` / `cmake` / `ninja` / `dotnet`、
  参数非法）。环境问题不会被伪装成检查失败。

每一步输出一行 `[check] <名称> ... PASS|FAIL|SKIP`，末尾给出汇总与总耗时。`SKIP` 表示这一步
没有可检查的对象（例如暂存区没有 C++ 文件、配置失败导致 `wf_check` 未执行），不计为失败。

快速档的三项检查都做过「故意破坏后变红」的反向验证。缺工具时脚本以 2 退出，不会把
环境问题算成代码问题。

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

## 目录

| 路径 | 内容 |
|---|---|
| `src/driver/` | WFP callout 驱动（内核方言，MSVC + WDK） |
| `src/common/` | 内核与用户态共享的 ABI 与可移植核心 |
| `src/userland/` | 规则编译、控制器、重定向中继与测试 |
| `tests/host/` | 宿主上的 C++ 单测与跨语言差分测试 |
| `tools/` | 宿主门禁、Windows 侧脚本与测试替身 |

构建与测试的细节见 `src/driver/README.md`、`src/userland/README.md`。
