param(
    [string]$Root = "C:\wfbench\wfprobe",
    [string]$Remote = "1.2.3.4:19003",
    [int]$Iterations = 3,
    [int]$ReplaceIterations = 100
)

$ErrorActionPreference = "Stop"

$msb = "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
$signtool = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"
$accesschk = "C:\SysInternals\accesschk64.exe"
$driverProject = Join-Path $Root "src\driver\wfprobe.vcxproj"
$driverBinary = Join-Path $Root "src\driver\x64\Debug\wfprobe.sys"
$toolProject = Join-Path $Root "tools\wfprobe\wfprobe.csproj"
$toolBinary = Join-Path $Root "tools\wfprobe\bin\Release\net11.0\wfprobe.exe"

$script:failures = 0
function Assert([string]$name, [bool]$condition, [string]$detail) {
    if ($condition) {
        "ASSERT PASS: $name ($detail)"
    } else {
        $script:failures++
        "ASSERT FAIL: $name ($detail)"
    }
}

$script:result = 1
$serviceStarted = $false
$bootBefore = $null
try {
    "===== 构建驱动 ====="
    $buildOutput = & $msb $driverProject -restore -p:Configuration=Debug -p:Platform=x64 -v:m 2>&1 | Out-String
    $buildOutput
    if ($LASTEXITCODE -ne 0) { throw "驱动构建失败" }
    Assert "驱动构建 0 warning" (-not ($buildOutput -match "warning C")) "msbuild"

    "===== 签名 ====="
    $thumbprint = (Get-ChildItem Cert:\LocalMachine\My |
                   Where-Object { $_.Subject -like '*WinForward Test Driver*' }).Thumbprint
    if (-not $thumbprint) { throw "找不到测试证书" }
    & $signtool sign /fd SHA256 /sha1 $thumbprint /v $driverBinary | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "签名失败" }
    $signature = Get-AuthenticodeSignature $driverBinary
    "签名状态：$($signature.Status)"
    Assert "签名状态 Valid" ($signature.Status -eq "Valid") $signature.Status

    "===== 安装并启动驱动 ====="
    # 这台 VM 可能被多个批次共用：服务名与驱动文件路径都是固定的，安装/启动可能撞上并发清理。
    # 失败时重试，并把 sc 的输出留下来，避免只看到一句「方法调用在 null 上」。
    $serviceStarted = $false
    for ($installAttempt = 0; $installAttempt -lt 3 -and -not $serviceStarted; $installAttempt++) {
        sc.exe stop wfprobe 2>$null | Out-Null
        sc.exe delete wfprobe 2>$null | Out-Null
        for ($attempt = 0; $attempt -lt 40; $attempt++) {
            $text = sc.exe query wfprobe 2>$null | Out-String
            if ($text -match "STOPPED" -or $text -match "1060") { break }
            Start-Sleep -Milliseconds 250
        }
        Copy-Item $driverBinary "C:\Windows\System32\drivers\wfprobe.sys" -Force
        if (-not (Test-Path "C:\Windows\System32\drivers\wfprobe.sys")) { throw "驱动文件复制失败" }
        $createOutput = sc.exe create wfprobe type= kernel start= demand binPath= C:\Windows\System32\drivers\wfprobe.sys 2>&1 | Out-String
        $startOutput = sc.exe start wfprobe 2>&1 | Out-String
        Start-Sleep -Seconds 1
        $probe = sc.exe query wfprobe 2>&1 | Out-String
        $stateLine = ($probe | Select-String "STATE")
        if ($stateLine -and $stateLine.Line -match "RUNNING") {
            $serviceStarted = $true
            break
        }
        "安装尝试 $($installAttempt + 1) 失败：$($createOutput.Trim()) / $($startOutput.Trim()) / $($probe.Trim())"
        sc.exe delete wfprobe 2>$null | Out-Null
        Start-Sleep -Milliseconds 500
    }
    if (-not $serviceStarted) { throw "驱动无法进入 RUNNING" }
    $state = (sc.exe query wfprobe | Select-String "STATE").Line.Trim()

    "服务状态：$state"
    Assert "驱动进入 RUNNING" ($state -match "RUNNING") $state
    $bootBefore = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
    "boot time: $bootBefore"

    "===== 设备 DACL ====="
    if (-not (Test-Path $accesschk)) { throw "找不到 accesschk64.exe，无法验证设备 DACL" }
    $dacl = (& $accesschk -accepteula -q -o '\Device\WfProbe' | Out-String)
    $dacl
    Assert "DACL 含 SYSTEM" ($dacl -match 'SYSTEM') "accesschk"
    Assert "DACL 含 Administrators" ($dacl -match 'Administrators') "accesschk"
    Assert "DACL 不含 Everyone" (-not ($dacl -match 'Everyone')) "accesschk"

    "===== 构建测试程序 ====="
    & dotnet build $toolProject -c Release -v:q
    if ($LASTEXITCODE -ne 0) { throw "测试程序构建失败" }

    "===== 进程选择器契约 ====="
    & $toolBinary appid
    Assert "appid 归一化可用" ($LASTEXITCODE -eq 0) "appid"

    "===== 规则表 / IOCTL / 计数器（含 A1/A2/A3/A4/A6/A8）====="
    & $toolBinary rules --iterations $ReplaceIterations
    Assert "rules 套件" ($LASTEXITCODE -eq 0) "exit=$LASTEXITCODE"

    "===== dual-mode 之前的 V4 重定向 ====="
    & $toolBinary selftest --remote $Remote --iterations $Iterations
    Assert "selftest V4" ($LASTEXITCODE -eq 0) "exit=$LASTEXITCODE"

    "===== dual-mode IPv6（.NET TcpClient.Connect(string,int)）====="
    & $toolBinary selftest --dual --remote $Remote --iterations $Iterations
    Assert "selftest dual-mode V6" ($LASTEXITCODE -eq 0) "exit=$LASTEXITCODE"

    "===== 四道防循环闸 ====="
    & $toolBinary gates
    Assert "gates 套件" ($LASTEXITCODE -eq 0) "exit=$LASTEXITCODE"

    "===== 不绕过 Windows 防火墙 ====="
    & (Join-Path $Root "tools\vm\firewall-check.ps1") -Root $Root -Remote $Remote
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
    & (Join-Path $Root "tools\vm\userland-e2e.ps1") -Root $Root
    Assert "userland-e2e" ($LASTEXITCODE -eq 0) "exit=$LASTEXITCODE"


    "===== 运行后状态 ====="
    $state = (sc.exe query wfprobe | Select-String "STATE").Line.Trim()
    Assert "全部套件之后驱动仍 RUNNING（无崩溃）" ($state -match "RUNNING") $state
    $bootAfter = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
    Assert "VM 没有重启（无蓝屏）" ($bootAfter -eq $bootBefore) "boot=$bootAfter"

    if ($script:failures -gt 0) {
        "===== 失败：$script:failures 条断言 ====="
        $script:result = 1
    } else {
        "===== 全部通过 ====="
        $script:result = 0
    }
}
catch {
    "失败：$($_.Exception.Message)"
    $script:result = 1
}
finally {
    if ($serviceStarted) {
        "===== 卸载 ====="
        & $toolBinary control --enabled 0 2>$null | Out-Null
        sc.exe stop wfprobe 2>$null | Out-Null
        for ($attempt = 0; $attempt -lt 40; $attempt++) {
            $text = sc.exe query wfprobe 2>$null | Out-String
            if ($text -match "STOPPED" -or $text -match "1060") { break }
            Start-Sleep -Milliseconds 250
        }
        $state = (sc.exe query wfprobe 2>$null | Select-String "STATE")
        if ($state) { "卸载后状态：$($state.Line.Trim())" }
        sc.exe delete wfprobe 2>$null | Out-Null
        Start-Sleep -Milliseconds 500
        Remove-Item "C:\Windows\System32\drivers\wfprobe.sys" -Force -ErrorAction SilentlyContinue
    }
}

if ($script:result -eq 0) { "GATE-RESULT=PASS" } else { "GATE-RESULT=FAIL failures=$($script:failures)" }
exit $script:result

