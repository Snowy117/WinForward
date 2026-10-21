# WFP 重定向驱动（规则表版）

WDM 驱动，在 `FWPM_LAYER_ALE_CONNECT_REDIRECT_V4/V6` 上挂 callout，按用户态下发的规则 blob
决定放行 / 重定向；block 规则由 `FWPM_LAYER_ALE_AUTH_CONNECT_V4/V6` 上的 `FWP_ACTION_BLOCK`
filter 执行。Phase 0 的硬编码探针行为已被规则表取代。

## 文件

| 文件 | 内容 |
|---|---|
| `probe_ioctl.h` | 控制面 ABI v2：IOCTL 码、配置、计数、规则表回读、事件 |
| `rule_table.h/.cpp` | 规则 blob 的池内存副本、校验（含 selector UTF-8/511、fallback 冲突、顺序冲突）、引用计数、原子替换与退役链 |
| `probe_driver.h` | 驱动内部共享面（状态、锁、窄接口、callout 原型、include 顺序约束） |
| `probe_driver.cpp` | `DriverEntry`/卸载、设备、WFP provider/callout/filter 的注册与拆除 |
| `block_filters.cpp` | 把 block 规则编译成 ALE_AUTH_CONNECT 的 `FWP_ACTION_BLOCK` filter（增删/滚动替换） |
| `probe_control.cpp` | IOCTL 处理：配置（含 0↔1 切换时 block filter 的挂/摘）、规则下发、计数、事件、调用者校验 |
| `probe_classify.cpp` | classify：facts、四道防循环闸、matcher、三个动作、重定向 |
| `process_path.cpp` | `ALE_APP_ID` 的 UTF-16 → UTF-8 转换（511 字节边界、代理对）与 0x807 诊断 |
| `wfprobe.vcxproj` | WDK 工程，`SignMode=Off`；签名由 `WfBuild.psm1` 调 signtool 完成 |

## 控制面

- `ioctl_set_rule_blob`：下发整块 `rule_blob`（`src/common/rule_blob.h`）。校验失败返回
  `STATUS_INVALID_PARAMETER`，当前规则表保持不变；成功则原子替换并 `generation++`。
- `ioctl_get_rule_info`：规则数、兜底动作、总长、generation。
- `ioctl_get_config` / `ioctl_set_config`：启用开关、监听端口 / PID、self-PID 集合。
- `ioctl_get_stats` / `ioctl_reset_stats`：classify 次数、各动作计数、被拒绝的 blob 数、
  context 分配/失败、四道闸的细分计数、block 决策计数。
- `ioctl_get_events`：64 条环形事件（decision / action / branch / 命中规则序号 / 地址族）。
- `ioctl_measure_process_path`（0x807，诊断，**发布前可移除**）：把 UTF-16LE 缓冲区按
  `collect_process_path` 的规则转成 UTF-8，用于验证 511 字节边界与代理对行为；只读、无状态、
  admin-only。存在的唯一理由是长路径进程无法在测试环境构造，删掉它那段逻辑就不可验证。

全部 IOCTL 走 `caller_is_admin`；设备用 `IoCreateDeviceSecure` + `SDDL_DEVOBJ_SYS_ALL_ADM_ALL`。
`FILE_ANY_ACCESS` 没有放宽访问控制：DACL 与调用者校验才是边界。

## 规则与防循环

规则 blob 的第一条命中决定动作（`permit` / `block` / `redirect`，未命中用 `fallback_action`）。
classify 依次检查：

1. 目标端口等于配置的监听端口 → 放行（闸 1）；
2. `previousVersion->localRedirectHandle` 非空（别的 callout 已改写）→ 放行；
3. `FwpsQueryConnectionRedirectState0` 报告 BY_SELF / PREVIOUSLY_BY_SELF → 放行（闸 2）；
4. 条件标志含 `IS_CONNECTION_REDIRECTED` 或 `IS_PROXY_CONNECTION` → 放行（闸 3）；
5. 发起进程在 self-PID 集合里 → 放行（闸 4）。

`block` 决策在 redirect 层只记账并放行；真正的拒绝由 auth 层的 block filter 完成
（详见 `.trellis/tasks/10-20-wfp-driver-rules/research/findings.md` §2）。

## 系统要求

池内存用 `ExAllocatePool2(POOL_FLAG_NON_PAGED, ...)` 分配（`cpp/drivers/wdk-deprecated-api` 的修复），
最低支持 **Windows 10 2004（10.0.19041）**；更早的系统上 `wfprobe.sys` 会因缺少该导出而加载失败。
要覆盖旧系统时，改为运行时用 `MmGetSystemRoutineAddress` 取 `ExAllocatePool2`、缺失时回退到
`ExAllocatePoolWithTag`。

`DriverEntry` 用 `extern "C" DRIVER_INITIALIZE DriverEntry;` 声明角色类型。CodeQL 的
`cpp/drivers/illegal-field-access-2` 与 PREfast 的 C28175 都按这条声明判定 DriverEntry 上下文，
`probe_driver_entry` / `create_device` 里的 `DriverObject->MajorFunction`、`DriverUnload` 赋值才不会被
判成越权访问——只有 SAL 注解（`_Function_class_(DRIVER_INITIALIZE)`）不够。

## 构建、签名与安装

在任意一台装有 Visual Studio Build Tools（MSVC + WDK）的 Windows 机器上，以管理员 PowerShell 运行仓库门禁：

```powershell
pwsh -File tools/windows/gate.ps1 -Remote <测试目标地址:端口>
```

`-Root` 默认由脚本位置推到仓库根，也可以用 `-Root <仓库根>` 显式指定。工具发现、构建、签名、
安装、卸载与 PREfast 都在 `tools/windows/WfBuild.psm1`；只做构建/安装/启动时用
`tools/windows/load-driver.ps1`。工程用 `..\..\src\common` 指向共享头，仓库目录结构要保持。
产物是 `src\driver\x64\Debug\wfprobe.sys`；签名使用证书库中的 `WinForward Test Driver` 证书。
`NuGet.config` 只配置 nuget.org；`-restore` 会按需下载 WDK / SDK 包，不依赖任何本机包目录。

## 安装与卸载

`tools/windows/WfBuild.psm1` 的 `Install-Driver` / `Uninstall-Driver` 负责签名、复制到
`System32\drivers`、创建/启动/停止/删除服务。不要手工复制 `.sys` 或直接改服务，避免门禁与
手工操作的状态不一致；完整流程用 `gate.ps1`，临时调试用 `load-driver.ps1`。

- block 规则编译成独立的 auth 层 filter；凡是“更早的非 block 规则可能与它重叠”的配置会在
  换表时整块拒绝（`rule_order_valid`），绝不让 filter 抢掉 blob 里本应首个命中的 permit/redirect。
- 进程选择器是 `ALE_APP_ID` 的 UTF-16 device path → UTF-8（ASCII 大小写折叠，代理对按标准
  展开）。上限是 **UTF-8 511 字节（不含 NUL）**，缓冲区 512 字节含 NUL；超限/非法代理项整条
  不匹配、不截断。中文路径与 511/512 边界都已实测。
- 用户态要用 `FwpmGetAppIdFromFileName0` 取 app id；该 API 对 >MAX_PATH 的路径会失败，长路径
  需要用户态自己拼 device path。
- 闸 2 目前只由组合用例覆盖，未单独钉住（需要第二个 redirector，findings §5）。
