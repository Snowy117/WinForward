# WFP Phase 0 探针驱动

最小 WDM 驱动，只在 `FWPM_LAYER_ALE_CONNECT_REDIRECT_V4` 上挂一个 callout，用来验证三件事：

1. `localAddressAndPort` 是 ANY 时把连接改到 loopback、否则改到该 local 地址，两条分支是否都能落到本地监听端口；
2. 写进 `localRedirectContext` 的 `winforward::wfp::redirect_context` 能否被用户态用
   `SIO_QUERY_WFP_CONNECTION_REDIRECT_CONTEXT` 原样读回；
3. `localRedirectContext` 的所有权归谁。

## 文件

| 文件 | 内容 |
|---|---|
| `probe_ioctl.h` | 驱动与 C# 测试程序之间的控制面 ABI（IOCTL 码、配置、计数、事件） |
| `probe_driver.h` | 驱动内部共享面（状态、窄接口、callout 原型） |
| `probe_driver.cpp` | `DriverEntry` / 卸载、WFP 对象注册与拆除、设备与 IOCTL |
| `probe_classify.cpp` | classify：各道闸、两条重定向分支、redirect context 分配 |
| `wfprobe.vcxproj` | WDK 工程，`SignMode=Off`，签名在构建后用 signtool 手工完成 |

## 构建

在 `WinLTSC` 上（工程文件里的 `..\..\src\common` 指向 `redirect_context.h`，所以目录结构要保持）：

```powershell
$msb = "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
& $msb C:\wfbench\wfprobe\src\driver\wfprobe.vcxproj -restore -p:Configuration=Debug -p:Platform=x64
```

产物：`src\driver\x64\Debug\wfprobe.sys`。

## 签名

模板里的 `sign.ps1` 走 `/f <pfx>` 会报 `Store::ImportCertObject() failed (0x80090010)`，改用证书库里已有的那张：

```powershell
$thumb = (Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -like '*WinForward Test Driver*' }).Thumbprint
& "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe" sign /fd SHA256 /sha1 $thumb /v <sys>
```

注意：签名必须走 `/sha1 <thumbprint>`，`/f <pfx>` 这条路在本机不可用（导入私钥报 0x80090010）。

## 安装与卸载

```powershell
sc.exe stop wfprobe
sc.exe delete wfprobe
Copy-Item wfprobe.sys C:\Windows\System32\drivers\wfprobe.sys -Force
sc.exe create wfprobe type= kernel start= demand binPath= C:\Windows\System32\drivers\wfprobe.sys
sc.exe start wfprobe
sc.exe query wfprobe
```

卸载顺序：先 `sc.exe stop wfprobe`（触发 `DriverUnload`），再 `sc.exe delete wfprobe`。

## 设备安全

- 设备对象用 `IoCreateDeviceSecure` + `SDDL_DEVOBJ_SYS_ALL_ADM_ALL` 创建，DACL 只允许
  SYSTEM 与 Builtin\Administrators；`accesschk64.exe -q -o \Device\WfProbe` 可验证。
- 每个 IOCTL 入口再用 `SeTokenIsAdmin` 校验调用者。`FILE_DEVICE_SECURE_OPEN` 只影响设备
  命名空间的打开路径，不替代 DACL。
- DACL 收紧后，测试程序必须以管理员身份运行。

## 与 design.md 的差异

- **设备对象先建、callout 后注册**：`FwpsCalloutRegister1` 在 WDM 下需要设备对象，而 design.md
  的列表把设备创建写在 callout 注册之后。拆除顺序仍严格逆序。
- **`<ndis.h>` 需要 `NDIS630`**：WFP callout 不属于 NDIS 驱动模型，必须在包含前定义 `NDIS630`，
  否则 `fwpsk.h` 依赖的 `NET_BUFFER_LIST` 等类型不会被导出。
- 重定向改的是 `remoteAddressAndPort`（官方文档与 WFPSampler 示例的写法）；`localAddressAndPort`
  只是读取，用来判断 ANY 分支。`localAddressAndPort` 本身在 connect-redirect 层不可写。
