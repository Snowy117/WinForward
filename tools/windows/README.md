# Windows 侧脚本

本目录的脚本只回答一个问题：**在一台装有 Visual Studio Build Tools + WDK 的 Windows 机器上，跑什么。**
仓库不包含“把源码送到那台机器”的编排（同步、连虚拟机、上传）——那些是机器本地的事，放在
`local/` 与 `wf.local.json` 里，见根目录 README。

Windows 侧脚本对**机器事实**（工具安装位置、仓库根、测试目标地址）一律靠发现或参数：
MSBuild 走 `vswhere`，signtool 走 Windows Kits 的版本目录，accesschk 走 PATH / 环境变量 /
系统盘一层目录；仓库里没有固定盘符、固定 IP 或固定版本号路径。
C# 工具的目标框架输出目录（如 `net10.0` / `net11.0`）仍按工程当前配置写死，属于后续批次，
不在本目录的“工具发现”范围内。

## 前置条件

| 项 | 说明 |
|---|---|
| Visual Studio Build Tools | 含 MSBuild + MSVC + WDK；MSBuild 由 `vswhere -latest -products *` 发现 |
| Windows SDK | `Windows Kits\10\bin\<版本>\x64\signtool.exe`，取最高版本 |
| 测试证书 | `Cert:\LocalMachine\My` 中 Subject 含 `WinForward Test Driver` |
| Sysinternals accesschk | 放进 PATH、设置 `WF_ACCESSCHK`，或解压到系统盘/Program Files 下一层目录 |
| .NET SDK | 构建 `tools/wfprobe` 与 `src/userland` |
| 管理员 PowerShell | 安装/卸载驱动、改防火墙规则需要管理员权限 |
| CodeQL CLI（仅 `codeql.ps1`） | 解压 bundle，用 `-CodeqlHome` / `CODEQL_HOME` / PATH 指向 `codeql.cmd` 所在目录；仓库不包含 |

驱动工程用 `..\..\src\common` 指向共享头，因此仓库根下必须同时有 `src\driver`、`src\common`、
`src\userland`、`tools\wfprobe` 与 `tools\socks5`。

## 参数

- `-Root`：仓库根目录。默认由 `$PSScriptRoot` 上推两级（`tools\windows` → 仓库根），也可以显式指定。
- `-Remote`：测试目标地址，格式 `<主机:端口>`。**没有默认值**：先取 `-Remote`，再取环境变量
  `WF_REMOTE`，都没有就报错并说明这是测试目标地址（通常是另一台跑回显/代理服务的机器），不是本机地址。

```powershell
# 完整门禁
pwsh -File .\gate.ps1 -Remote <测试目标地址:端口>

# 也可以换一个仓库根
pwsh -File .\gate.ps1 -Root <仓库根> -Remote <测试目标地址:端口>
```

## 模块边界

`WfBuild.psm1` 是所有外部工具发现与构建/安装动作的唯一实现：

| 函数 | 职责 |
|---|---|
| `Resolve-MSBuild` | `vswhere -latest -products * -find MSBuild\**\Bin\MSBuild.exe`（`-products *` 不能省） |
| `Resolve-SignTool` | 在 `Windows Kits\10\bin\*\x64\signtool.exe` 里挑最高版本 |
| `Resolve-AccessChk` | 发现 Sysinternals accesschk |
| `Resolve-WfTool` | 找到 `wfprobe.exe` 的 Release 输出（不写死目标框架目录） |
| `Get-WfServiceName` | 驱动服务名，供脚本查询状态时复用 |
| `Build-Driver` | MSBuild 构建 `wfprobe.sys`，返回输出/退出码 |
| `Install-Driver` | 签名 → 复制到 `System32\drivers` → 创建服务并等待 RUNNING（失败重试并保留 sc 输出） |
| `Uninstall-Driver` | 关闭重定向、停服务、删服务、删驱动文件 |
| `Invoke-Prefast` | PREfast 分析构建，**解析输出**判定 |
| `Invoke-WfSuite` | 运行一个 `wfprobe` 套件，只返回退出码 |

各脚本是薄壳：解析参数、排列步骤、断言结果。`-Root` / `-Remote` 等参数名对调用方保持不变，
每个脚本也可以单独执行。

## gate.ps1

一次完整的 Windows 侧门禁：

1. 构建驱动，断言输出里没有 `warning C`；
2. 签名并安装，断言签名 `Valid`、服务 `RUNNING`，记录 boot time；
3. **PREfast 静态分析**：`-restore -t:Rebuild -p:RunCodeAnalysis=true
   -p:EnableMicrosoftCodeAnalysis=true -p:PreferredToolArchitecture=x64`，解析输出；
4. `accesschk -q -o \Device\WfProbe`，断言含 SYSTEM / Administrators、不含 Everyone；
5. 构建 `tools/wfprobe`，跑 `appid`、`rules`、`selftest`（V4）、`selftest --dual`、`gates`；
6. 跑 `firewall-check.ps1`；
7. 构建用户态，跑单测与 `vm-check`；
8. 跑 `userland-e2e.ps1`；
9. 断言全部套件之后驱动仍 `RUNNING`、VM boot time 未变；
10. 无论成功失败，最后卸载驱动。

### PREfast 判定

`/WX` **不会**把 PREfast 告警提升为错误：MSBuild 仍可能报 `Build succeeded / 0 Error(s)` 并以 0 退出。
所以 `Invoke-Prefast` 必须看输出：

- 出现 `warning C28xxx` 行 → 失败；
- 输出不含 `Running Code Analysis`（工具集静默跳过分析时不能被当成 0 告警通过）→ 失败；
- `-clp:Summary` 的 `Warning(s)` 计数不为 0（或没有解析到 Summary）→ 失败；
- MSBuild 退出码非 0 → 失败。

分析命令必须带 `-restore`（干净同步后缺 NuGet 资产会报 MSB8036）和
`PreferredToolArchitecture=x64`（x86 宿主找不到 WDK 的 x86 Prefast 插件，报 C1250）。

末行输出 `GATE-RESULT=PASS`、`GATE-RESULT=FAIL` 或 `GATE-RESULT=ENV-MISSING`。

## codeql.ps1

独立的深度档：追踪一次 MSBuild 驱动构建生成 CodeQL C++ 数据库，跑
`microsoft/windows-drivers` 的 recommended 套件，输出 SARIF。**不在 `gate.ps1` 的默认流程里**，
仓库也不附带 CodeQL CLI bundle。

```powershell
pwsh -File .\codeql.ps1 -CodeqlHome <bundle 里的 codeql 目录>
pwsh -File .\codeql.ps1 -CodeqlHome <...> -Database D:\codeql\db -Output D:\codeql\wf.sarif
pwsh -File .\codeql.ps1 -CodeqlHome <...> -SkipDatabase   # 复用数据库，只重跑分析
```

| 参数 | 默认值 | 说明 |
|---|---|---|
| `-Root` | `$PSScriptRoot\..\..` | 仓库根 |
| `-CodeqlHome` | 无 | `codeql.cmd` 所在目录，或它的上一层（bundle 根）；优先于环境变量与 PATH |
| `-Database` | `<Root>\build\codeql\db` | 数据库目录；相对路径按 `-Root` 解释 |
| `-Output` | `<Root>\build\codeql\wf.sarif` | SARIF 路径；不能放在 `-Database` 里面 |
| `-Configuration` / `-Platform` | `Debug` / `x64` | 建库时 MSBuild 的配置 |
| `-SkipDatabase` | 关 | 跳过建库，复用已有数据库（必须有 `codeql-database.yml`） |

工具发现与依赖：

- `codeql` 按 `-CodeqlHome` → `$env:CODEQL_HOME` → PATH 查找。显式给了 `-CodeqlHome` 却找不到
  就直接报错，不会静默回退到 PATH；都没有时以 2 退出，并说明仓库不附带 CLI。
- 查询包检查走 `codeql resolve packs`：`microsoft/windows-drivers@1.10.0` 与
  `microsoft/cpp-queries@0.0.5` 缺哪个就 `codeql pack download` 哪个，下载后再检查一遍。
  `microsoft/cpp-queries` 必须显式装——suite 依赖它，不装会在解析问题清单时失败。
- 建库前先 `codeql resolve queries <suite>` 解析一次套件，路径或包版本问题会在建库之前报出来。
- 建库时用 `--extractor-options-file` 传入 `build\codeql\extractor-options.json`，里面只有一条给
  C++ 提取器前端的 `-D__ptr32=`：WDK 的 `wdm.h` 用了 CodeQL 2.27.3 前端解析不了的 `__ptr32`，
  不处理会让提取器 exit 3、`cpp/extractor/summary` 记成 0 成功 1 失败。真实编译（MSBuild/cl）不受影响。

流程与产物（数据库、SARIF 与日志在 `build\codeql\`，已 gitignore）：

1. 建库：`codeql database create db --language=cpp --source-root <Root> --command "database-create.bat"
   --extractor-options-file extractor-options.json`。`.bat` 里是 `MSBuild.exe
   src\driver\wfprobe.vcxproj -restore -nologo -t:Rebuild -p:Configuration=Debug -p:Platform=x64 -v:m`，
   退出码原样返回。MSBuild 路径带空格，直接拼进 `--command` 容易在引号上翻车，所以固定包一层。
   `-restore` 不能省（干净同步后缺 NuGet 资产会报 MSB8036）；`-t:Rebuild` 保证编译器真的被调用。
2. 分析：`codeql database analyze db
   "microsoft/windows-drivers:windows-driver-suites/recommended.qls" --format=sarif-latest
   --output wf.sarif`。
3. 摘要：解析 SARIF 的 `runs[].results`，输出条数、按 `ruleId` 分组、前 50 条位置；SARIF
   解析失败会报错，不会当成 0 条。
4. 抽取自检：解析 `runs[0].invocations[*].toolExecutionNotifications` 里 descriptor id 为
   `cpp/extractor/summary` 的那条，读 `properties.attributes` 的 `extractor-successes` 与
   `extractor-failures`。successes 必须 > 0、failures 必须为 0；找不到 summary、0 成功或有失败
   都判 FAIL（`数据库不完整，结果不可信`）。这样 0 条结果也不会在数据库没抽到代码时误报 PASS。

产物：`db\`、`wf.sarif`、`database-create.bat`、`extractor-options.json`、`database-create.log`、
`analyze.log`。已存在的数据库会被删除重建；为防止误删，目标目录里必须有 `codeql-database.yml`。
MSBuild 的中间产物按常规落在 `src\driver\obj\` 与 `src\driver\x64\`（都已 gitignore），不随数据库
一起放在 `build\codeql\`。

实测（2026-10-21，CLI 2.27.3，16 核 / 2.8 GB 虚拟机）：

| 步骤 | 结果 |
|---|---|
| 建库（含追踪构建） | 52–68 s（冷树首次带 `-restore` 约 74 s） |
| 分析（106 条查询） | 114–120 s |
| 数据库 / SARIF | 22 MB / 约 400 KB |
| 当前基线 | 14 条：unprobeddereference ×7、illegal-field-access-2 ×4、wdk-deprecated-api ×2、usermodememoryoutsidetry ×1 |

同一个数据库上重复跑 `-SkipDatabase`：分析步约 26 s，整进程约 38.6 s（命中 CodeQL 查询结果缓存）；
上表是重建数据库后的完整耗时。

分析完成后还会做抽取自检（见上面流程第 4 步）：2026-10-21 的基线在加上 `-D__ptr32=` 处理后，
`cpp/extractor/summary` 记的是 successes=1、failures=0；抽取失败的数据库即使 0 条结果也会 FAIL。
旧版本脚本建的数据库可能没带 `-D__ptr32=`，自检会失败；删掉 `build\codeql\db` 重建即可。

**0 命中不能直接当门禁，当前基线本身也不是 0**：退出码 `1` 表示有结果，或建库/分析/抽取自检失败
（看末行 `CODEQL-RESULT`：`FINDINGS results=N` / `FAIL`）；`2` 是环境/依赖缺失。判断回归要看
`ruleId` 集合与位置有没有变化。变红能力做过注入实验：在 `src/driver/probe_classify.cpp` 里加一个
`if (value + increment < value)`（`UINT16` 参数会提升到 `int`），重跑后 SARIF 出现
`cpp/bad-addition-overflow-check`；恢复源码后结果与基线逐条一致。

### 已知限制

- **非 ASCII 路径**：CodeQL 2.27.3 在含中文的路径下建库会失败（路径被写成 `??`）。仓库请放在纯
  ASCII 短路径下，例如 `C:\src\wfprobe`；`-Database` / `-Output` 也不要放到含中文的目录。
- `microsoft/cpp-queries@0.0.5` 的 `cpp/bad-addition-overflow-check` 只识别“变量 + 变量”的写法；
  `if (x + 1 < x)` 这种字面量写法实测不报（与包内文档不一致），注入实验用等价的
  `if (value + increment < value)`。

## mutation.ps1

变异测试：逐条改驱动源码，重建 + 签名 + 装载，跑对应套件并期望**非 0**；每条结束后恢复源码。
全部结束后重建并跑正向基线，确认测试本身是全绿的。

```powershell
pwsh -File .\mutation.ps1 -Remote <测试目标地址:端口>
pwsh -File .\mutation.ps1 -Remote <测试目标地址:端口> -OnlyIndex 0,3
```

`-OnlyIndex` 选跑部分用例，只写逗号串 `-OnlyIndex 0,3`（索引从 0 起）。不要写成 `-OnlyIndex 0 3`
或 `-OnlyIndex @(0,3)`：`pwsh -File` 会把它们展开/按位置绑定，`3` 可能被当成 `-Root` 的值；
而旧版 `[int[]]` 绑定又会把 `4,7` 当千分位读成 47，导致一条用例都没跑却报 PASS。脚本现在按字符串
自己拆逗号；索引越界或没有选中任何用例会以参数错误（退出码 2）失败，不会静默跑 0 条。
锚点是源码里的 `// <mutation id="…">` 到 `// </mutation>` 区域标记；脚本按 id 定位并整体替换，
命中数不为 1 会立即失败并打印 id。每条结束后恢复源码，全部跑完后与运行前内容逐字节比较。

## load-driver.ps1

只做「构建 → 签名 → 安装 → 启动」，给调试和证据采集用：

```powershell
pwsh -File .\load-driver.ps1
pwsh -File .\load-driver.ps1 -Root <仓库根>
```

末行输出 `DRIVER-STATUS=RUNNING`。卸载用 `Uninstall-Driver` 对应的 `sc stop` / `sc delete`，
或直接跑 `gate.ps1` 让它按 finally 卸载。

## firewall-check.ps1

三段实验：无防火墙规则时连接成功 → 加一条出站 Block 规则后连接被拒 → 撤掉规则连接恢复。
中间那段在驱动的 AUTH_CONNECT block filter 活动时执行，用来证明没有 terminating PERMIT 截胡防火墙。

```powershell
pwsh -File .\firewall-check.ps1 -Remote <测试目标地址:端口> -Root <仓库根>
```

需要驱动已 RUNNING、`wfprobe.exe` 已构建。末行 `firewall-check verdict: PASS|FAIL`。

## userland-e2e.ps1

真实进程 → 驱动重定向 → 控制器监听 → 读回原始目的地 → 双向中继 → 回显校验（V4 + V6，各 1 MiB），
以及「退出后 enabled=0、旧端口释放、不卸载驱动直接重启」。

```powershell
pwsh -File .\userland-e2e.ps1
pwsh -File .\userland-e2e.ps1 -Root <仓库根> -KeepWork   # 保留报告，末行 E2E-WORK=<目录>
```

它自己起一个回显服务（独立进程、系统分配端口），用 `WinForward.Controller` 起监听与中继，
再用独立客户端进程对照 SHA-256。末行 `E2E-RESULT=PASS|FAIL`。

## userland-socks5-e2e.ps1

形态是「客户端 → 驱动重定向 → 控制器 → SOCKS5 替身（VM loopback）→ 跨机 target」。
宿主侧先起一个裸 TCP 回显目标：

```bash
dotnet build tools/socks5/wf-socks5.csproj -c Release
dotnet tools/socks5/bin/Release/net10.0/wf-socks5.dll echo \
    --listen 0.0.0.0 --port 40001 --connections 1 --timeout-ms 120000 --report /tmp/echo-target.txt
```

Windows 侧：

```powershell
pwsh -File .\userland-socks5-e2e.ps1 -Remote <跨机 target 地址:端口>
# 也可以拆开指定：-TargetHost <地址> -TargetPort 40001
```

`-Remote` 这里是**跨机 target**（bare TCP 回显）的 host:port，也可以由 `WF_REMOTE` 提供；
显式给出的 `-TargetHost` / `-TargetPort` 优先，缺的字段才从 `-Remote` 取（因此可以只给 host
或只给 port，两者都显式给时 `-Remote` 只做格式校验）。脚本自己写配置：`socks5` 段 + 放行替身进程的 permit
规则 + 指向 target 端口的 redirect 规则。permit 那条是必需的——替身的出站连接同样会走
`ALE_CONNECT_REDIRECT`，没有这条规则它会被同一条 redirect 规则改回控制器，形成环。

成功轮断言 `relay connected ... via=socks5:...`、替身的
`socks5-done connections=1 connects=1 bytes_in=<N> bytes_out=<N>`、以及客户端 SHA-256 两边一致；
失败轮断言客户端明确失败、控制器记 `relay error ... Socks5Exception`、且没有 `relay connected`。
target 侧的 `echo-done connections=` 在宿主上核对。末行 `E2E-RESULT=PASS|FAIL`。

## 单跑套件

驱动已 RUNNING 时：

```powershell
wfprobe rules --iterations 3
wfprobe selftest --remote <测试目标地址:端口> --iterations 3
wfprobe selftest --dual --remote <测试目标地址:端口> --iterations 3
wfprobe gates
wfprobe blockprobe --rule-port 65000 --remote <测试目标地址:端口>
wfprobe stats / events --count 32 / pooltag / appid

WinForward.Controller check --config policy.json
WinForward.Controller run   --config policy.json --stop-file stop.flag --report controller.txt
WinForward.Controller state --report state.txt
WinForward.Controller disable
```

## 退出码

`gate.ps1`：

- `0`：通过；
- `1`：有断言失败或检查发现；
- `2`：参数/环境问题（例如缺 `-Remote`、仓库根不存在、找不到 MSBuild / signtool / accesschk）。

`mutation.ps1` / `load-driver.ps1` / `firewall-check.ps1` 同样区分 `0` / `1` / `2`：`[ENV]` 环境或前置条件错误（缺工具、缺工程、缺证书）以 `2` 退出。

`codeql.ps1`：`0` 分析完成、0 条结果且抽取自检通过；`1` 有结果（`CODEQL-RESULT=FINDINGS results=N`），或建库/分析/抽取自检失败（`CODEQL-RESULT=FAIL`）；`2` 环境/依赖问题（找不到 CLI、查询包下载失败、套件解析失败），末行 `CODEQL-RESULT=ENV-MISSING`。

`userland-e2e.ps1` / `userland-socks5-e2e.ps1`：参数或仓库根解析失败以 `2` 退出；其余失败为 `1`。
