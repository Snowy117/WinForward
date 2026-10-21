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

## mutation.ps1

变异测试：逐条改驱动源码，重建 + 签名 + 装载，跑对应套件并期望**非 0**；每条结束后恢复源码。
全部结束后重建并跑正向基线，确认测试本身是全绿的。

```powershell
pwsh -File .\mutation.ps1 -Remote <测试目标地址:端口>
pwsh -File .\mutation.ps1 -Remote <测试目标地址:端口> -OnlyIndex 0,3
```

本期锚点仍是逐字源码子串（按 id 定位的区域标记是后续批次）；匹配逻辑没有改。锚点命中数不为 1
会立即失败。

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

`userland-e2e.ps1` / `userland-socks5-e2e.ps1`：参数或仓库根解析失败以 `2` 退出；其余失败为 `1`。
