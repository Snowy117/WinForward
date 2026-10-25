# B2 执行步骤

## 1. 构建门禁

`src/driver/wfprobe.vcxproj` 的 `ClCompile` 增加：

```xml
<WarningLevel>Level4</WarningLevel>
<TreatWarningAsError>true</TreatWarningAsError>
```

## 2. 在 Windows 机器上建立基线

同步源码后用 MSBuild 构建，确认 0 warning；再跑 PREfast 采集 10 条告警的 `file:line` 与源码上下文。

分析调用（两个参数缺一不可）：

```
msbuild wfprobe.vcxproj -restore -t:Rebuild -p:Configuration=Debug -p:Platform=x64 -v:m ^
  -p:RunCodeAnalysis=true -p:EnableMicrosoftCodeAnalysis=true -p:PreferredToolArchitecture=x64
```

## 3. 消解基线

- C28101：`DriverEntry` 用 `DRIVER_INITIALIZE` 声明。
- C28169 ×3：dispatch 函数补 `_Dispatch_type_`。
- C28167 ×2：`probe_lock` / `probe_unlock` 补 `_IRQL_requires_*`（按实际 IRQL 语义，不要照抄）。
- C28175 ×4：WDM 正常写法，`#pragma warning(suppress: 28175)` + 一行理由。

## 4. 验证

- A1：构建 0 warning。
- A2：注入 `static void wf_probe_dead() {}` → `C2220` + `C4505`。
- A3：分析 0 告警。
- A4：临时摘掉一个 `_Dispatch_type_` → `C28169`，恢复。
- A5：签名 → `sc create/start` → RUNNING → `sc stop/delete`。
- A6：`git diff` 自查只含注解/抑制。

## 纪律

- 只在本地改文件，同步到 Windows 机器构建；不要在 Windows 侧直接改源码。
- 不安装长期服务、不改测试证书、不重启机器。
- 不 `git commit`。
