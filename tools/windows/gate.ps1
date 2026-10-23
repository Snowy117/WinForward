param(
    [string]$Root = (Join-Path $PSScriptRoot "..\.."),
    [string]$Remote,
    [int]$Iterations = 3,
    [int]$ReplaceIterations = 100
)

# 完整门禁：构建 → 签名/安装 → PREfast → 各套件 → 卸载。
# 工具发现、构建、安装、分析都走 WfBuild.psm1；本脚本只负责顺序与断言。

$ErrorActionPreference = "Stop"

Import-Module (Join-Path $PSScriptRoot "WfBuild.psm1") -Force

try {
    $Root = Resolve-WfRoot -Path $Root
    $Remote = Resolve-Remote -Value $Remote
}
catch {
    "环境错误：$($_.Exception.Message -replace '^\[ENV\] ', '')"
    "GATE-RESULT=ENV-MISSING"
    exit 2
}
$serviceName = Get-WfServiceName
$script:failures = 0
function Assert([string]$name, [bool]$condition, [string]$detail) {
    if ($condition) {
        "ASSERT PASS: $name ($detail)"
    } else {
        $script:failures++
        "ASSERT FAIL: $name ($detail)"
    }
}
function Get-PoolOutstanding([string]$Tag, [string]$ToolExe) {
    if (-not $ToolExe -or -not (Test-Path -LiteralPath $ToolExe -PathType Leaf)) {
        return $null
    }
    $text = (& $ToolExe pooltag --tag $Tag | Out-String)
    if ($text -match 'nonpaged_outstanding=(\d+)') {
        return [long]$Matches[1]
    }
    if ($text -match '未找到 tag=') {
        return [long]0
    }
    return $null
}

$script:result = 1
$serviceStarted = $false
$bootBefore = $null
$toolBinary = $null

try {
    "===== 构建驱动 ====="
    $build = Build-Driver -Root $Root
    $build.Output
    if ($build.ExitCode -ne 0) { throw "驱动构建失败（exit=$($build.ExitCode)）" }
    Assert "驱动构建 0 warning" (-not ($build.Output -match "warning C")) "msbuild"

    "===== 签名并安装 ====="
    $serviceStarted = $true
    $install = Install-Driver -Root $Root
    "签名状态：$($install.SignatureStatus)"
    Assert "签名状态 Valid" ($install.SignatureStatus -eq "Valid") $install.SignatureStatus
    "服务状态：$($install.State)"
    Assert "驱动进入 RUNNING" ($install.State -match "RUNNING") $install.State
    $installedSha = (Get-FileHash (Join-Path $env:SystemRoot "System32\drivers\wfprobe.sys") -Algorithm SHA256).Hash
    "驱动 SHA256：$installedSha"
    $bootBefore = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
    "boot time: $bootBefore"

    "===== PREfast 静态分析 ====="
    $prefast = Invoke-Prefast -Root $Root
    $prefast.Output
    Assert "PREfast 0 告警" ($prefast.Success) `
        "exit=$($prefast.ExitCode) analysis-ran=$($prefast.AnalysisRan) analysis-warnings=$($prefast.AnalysisWarnings) summary-warnings=$($prefast.SummaryWarnings)"
    if (-not $prefast.Success) { throw "PREfast 分析失败（/WX 不会提升 PREfast 告警，判据是输出）" }

    "===== 设备 DACL ====="
    $accessChk = Resolve-AccessChk
    $dacl = (& $accessChk -accepteula -q -o '\Device\WfProbe' | Out-String)
    $dacl
    Assert "DACL 含 SYSTEM" ($dacl -match 'SYSTEM') "accesschk"
    Assert "DACL 含 Administrators" ($dacl -match 'Administrators') "accesschk"
    Assert "DACL 不含 Everyone" (-not ($dacl -match 'Everyone')) "accesschk"

    "===== 构建测试程序 ====="
    $toolProject = Join-Path $Root "tools\wfprobe\wfprobe.csproj"
    & dotnet build $toolProject -c Release -v:q
    if ($LASTEXITCODE -ne 0) { throw "测试程序构建失败" }
    $toolBinary = Resolve-WfTool -Root $Root -Name "wfprobe"

    "===== 进程选择器契约 ====="
    $code = Invoke-WfSuite -Root $Root -ToolExe $toolBinary -Arguments @("appid")
    Assert "appid 归一化可用" ($code -eq 0) "appid"

    "===== 规则表 / IOCTL / 计数器（含 A1/A2/A3/A4/A6/A8）====="
    $code = Invoke-WfSuite -Root $Root -ToolExe $toolBinary -Arguments @("rules", "--iterations", $ReplaceIterations)
    Assert "rules 套件" ($code -eq 0) "exit=$code"

    "===== MAC 帧层 forwarded（F1/F2/F3/F6；F2/F4 用固定源端口收窄，不碰 WinRM）====="
    # 直接调用而不走 Invoke-WfSuite：套件用 Write-Host 输出，gate 的日志/重定向收不到；
    # 这里把 stdout+stderr 收进变量再打印，失败时能直接看到是哪条断言。
    $forwardedOutput = & $toolBinary forwarded-rules --peer $Remote --only F1,F2,F3,F6 --timeout-ms 3000 2>&1 |
        Out-String
    $forwardedOutput
    $code = $LASTEXITCODE
    Assert "forwarded-rules 套件" ($code -eq 0) "exit=$code"

    "===== UDP：redirect/permit/block/any/进程选择器/IPv6/生命周期/热替换（U1–U13）====="
    $pingHost = ($Remote -split ':')[0]
    # 连接窗口用默认 1000ms；完整 1/10/60/180/300s 测量见 udp-rules 的 research 记录，gate 不等 300s。
    $code = Invoke-WfSuite -Root $Root -ToolExe $toolBinary -Arguments @("udp-rules", "--timeout-ms", "10000", "--ping-host", $pingHost, "--connected-window-ms", "1000")
    Assert "udp-rules 套件" ($code -eq 0) "exit=$code"

    "===== dual-mode 之前的 V4 重定向 ====="
    $code = Invoke-WfSuite -Root $Root -ToolExe $toolBinary -Arguments @("selftest", "--remote", $Remote, "--iterations", $Iterations)
    Assert "selftest V4" ($code -eq 0) "exit=$code"

    "===== dual-mode IPv6（.NET TcpClient.Connect(string,int)）====="
    $code = Invoke-WfSuite -Root $Root -ToolExe $toolBinary -Arguments @("selftest", "--dual", "--remote", $Remote, "--iterations", $Iterations)
    Assert "selftest dual-mode V6" ($code -eq 0) "exit=$code"

    "===== 四道防循环闸 ====="
    $code = Invoke-WfSuite -Root $Root -ToolExe $toolBinary -Arguments @("gates")
    Assert "gates 套件" ($code -eq 0) "exit=$code"

    "===== 不绕过 Windows 防火墙 ====="
    & (Join-Path $Root "tools\windows\firewall-check.ps1") -Root $Root -Remote $Remote
    Assert "firewall-check" ($LASTEXITCODE -eq 0) "exit=$LASTEXITCODE"

    "===== 构建用户态（控制器、配置层、IOCTL 客户端、中继）====="
    $userlandSolution = Join-Path $Root "src\userland\WinForward.Userland.slnx"
    $userlandTests = Join-Path $Root "src\userland\WinForward.Userland.Tests"
    & dotnet build $userlandSolution -c Release -v:q
    Assert "用户态构建成功" ($LASTEXITCODE -eq 0) "exit=$LASTEXITCODE"

    "===== 用户态单测（含配置、冲突检查、ABI 一致性、夹具）====="
    & dotnet run --project $userlandTests -c Release -- test
    Assert "用户态单测" ($LASTEXITCODE -eq 0) "exit=$LASTEXITCODE"

    "===== VM 契约：结构尺寸 / 511-512 边界 / 规则顺序差分 ====="
    & dotnet run --project $userlandTests -c Release -- vm-check
    Assert "vm-check 差分" ($LASTEXITCODE -eq 0) "exit=$LASTEXITCODE"

    "===== 端到端：真实进程 → 重定向 → 中继 → 回显（含 1 MiB 校验和与重启）====="
    & (Join-Path $Root "tools\windows\userland-e2e.ps1") -Root $Root
    Assert "userland-e2e" ($LASTEXITCODE -eq 0) "exit=$LASTEXITCODE"

    "===== 运行后状态 ====="
    $state = (sc.exe query $serviceName | Select-String "STATE").Line.Trim()
    Assert "全部套件之后驱动仍 RUNNING（无崩溃）" ($state -match "RUNNING") $state
    $bootAfter = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
    Assert "VM 没有重启（无蓝屏）" ($bootAfter -eq $bootBefore) "boot=$bootAfter"

    if ($script:failures -gt 0) {
        "===== 失败：$($script:failures) 条断言 ====="
        $script:result = 1
    } else {
        "===== 全部通过 ====="
        $script:result = 0
    }
}
catch {
    $message = $_.Exception.Message
    if ($message.StartsWith('[ENV] ')) {
        "环境错误：$($message.Substring(6))"
        $script:result = 2
    } else {
        "失败：$message"
        $script:result = 1
    }
}
finally {
    if ($serviceStarted) {
        "===== 卸载 ====="
        $poolTags = @("WfPr", "WfIj", "WfTr")
        $poolBefore = @{}
        foreach ($tag in $poolTags) {
            $poolBefore[$tag] = Get-PoolOutstanding $tag $toolBinary
            "卸载前 $tag outstanding：$($poolBefore[$tag])"
        }
        Uninstall-Driver -ToolExe $toolBinary
        $state = (sc.exe query $serviceName 2>$null | Select-String "STATE")
        if ($state) { "卸载后状态：$($state.Line.Trim())" }
        foreach ($tag in $poolTags) {
            $before = $poolBefore[$tag]
            $after = Get-PoolOutstanding $tag $toolBinary
            "卸载后 $tag outstanding：$after"
            if ($null -ne $before -and $null -ne $after) {
                if ($after -gt $before) {
                    $script:failures++
                    $script:result = 1
                    "ASSERT FAIL: 卸载没有新增 $tag outstanding ($before -> $after)"
                } else {
                    "ASSERT PASS: 卸载没有新增 $tag outstanding ($before -> $after)"
                }
            } else {
                "卸载前后 $tag outstanding：无法读取（按环境缺口跳过比较，tool=$toolBinary）"
            }
        }
    }
}

if ($script:result -eq 0) {
    "GATE-RESULT=PASS"
} elseif ($script:result -eq 2) {
    "GATE-RESULT=ENV-MISSING"
} else {
    "GATE-RESULT=FAIL failures=$($script:failures)"
}
exit $script:result
