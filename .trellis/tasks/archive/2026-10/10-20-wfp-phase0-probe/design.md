# 设计 — Phase 0 探针

## 驱动骨架（`src/driver/`）

WDM 驱动，不用 KMDF。骨架按以下顺序建立，注销严格逆序：

```
DriverEntry
  FwpmEngineOpen0                      → engine handle
  FwpsRedirectHandleCreate0            → redirect handle（全局一个，缓存）
  FwpsCalloutRegister1                 → callout at FWPS_LAYER_ALE_CONNECT_REDIRECT_V4
  FwpmCalloutAdd0
  FwpmFilterAdd0                       → 把流量引到 callout（只需一个全放行的 filter）
  IoCreateDevice + 符号链接            → IOCTL 通道（设 PID / 监听端口 / 取日志）

分类函数 classify_fn（用 classifyFn1 签名）
  FwpsQueryConnectionRedirectState0                         → 防重定向环
  flags & FWP_CONDITION_FLAG_IS_CONNECTION_REDIRECTED       → PERMIT
  目标端口 == 监听端口                                       → PERMIT（自流量闸）
  PID 不在白名单                                             → PERMIT
  FwpsAcquireClassifyHandle0
  FwpsAcquireWritableLayerDataPointer0 → FWPS_CONNECT_REQUEST0*
  localAddressAndPort 是 ANY ? 目的改为 loopback : 目的改为 local 地址
  端口改为监听端口
  分配 localRedirectContext（非分页池）并填充原始目的地
  localRedirectTargetPID = 用户态 PID
  localRedirectHandle    = redirect handle
  FwpsApplyModifiedLayerData0
  FwpsReleaseClassifyHandle0
  classifyOut->actionType = FWP_ACTION_PERMIT

DriverUnload（逆序）
  FwpmFilterDeleteById0 → FwpmCalloutDeleteById0 → FwpsCalloutUnregisterById0
  FwpsRedirectHandleDestroy0 → FwpmEngineClose0 → 删设备
```

## 测试程序

C# 控制台（VM 上已有 dotnet 11 RC）：

- **监听模式**：`TcpListener` 绑 `0.0.0.0`，accept 后 `WSAIoctl` 取
  `SIO_QUERY_WFP_CONNECTION_REDIRECT_CONTEXT` 与 `SIO_QUERY_WFP_CONNECTION_REDIRECT_RECORDS`，打印原始目的地，回显数据。
- **客户端模式**：连指定 `IP:port`，发一段数据，期望收到回显。

## 构建与部署（在 VM 上）

工程以 `C:\wfbench\driver-dev\template\` 为模板新建，改动点见该目录 `README.md`：
`WindowsTargetPlatformVersion=10.0.28000.0`、`PlatformToolset=WindowsKernelModeDriver10.0`、
`ConfigurationType=Driver`、`DriverType=WDM`、`SignMode=Off`、`Driver_SpectreMitigation=false`、
两个 `PackageReference`。

```powershell
$msb = "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
& $msb <proj>.vcxproj -restore -p:Configuration=Debug -p:Platform=x64
```

**签名注意**：模板里的 `sign.ps1` 走 `/f <pfx>` 这条路当前会报
`Store::ImportCertObject() failed (0x80090010)`。改用证书库里的那份：

```powershell
$thumb = (Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -like '*WinForward Test Driver*' }).Thumbprint
& "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe" sign /fd SHA256 /sha1 $thumb /v <sys>
```

## 风险

| 风险 | 应对 |
|---|---|
| 注销顺序错 → BSOD | 严格逆序；每轮 load/unload 后 `sc query` 确认状态 |
| 上下文所有权判断错 → UAF/泄漏 | 先查官方文档与 WDK sample，再用 200 次连接观察非分页池增长 |
| 无内核调试器 | 结论靠 IOCTL 回灌 + 用户态观测，不依赖 WinDbg |
