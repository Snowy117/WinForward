param(
    [string]$Root = (Join-Path $PSScriptRoot "..\.."),
    [int]$Bytes = 1048576,
    [int]$StartTimeoutMs = 20000,
    [int]$ExitTimeoutMs = 60000,
    [switch]$KeepWork
)

# 端到端实证：真实进程 → 驱动重定向 → 控制器监听 → 读回原始目的地 → 双向中继 → 回显校验。
# 每条断言都失败即红；报告文件由控制器自己写，脚本只读，不参与生成。
# 由 gate.ps1 调用，也可以单独执行；-Root 默认从脚本位置推到仓库根。

$ErrorActionPreference = "Stop"

Import-Module (Join-Path $PSScriptRoot "WfBuild.psm1") -Force
try {
    $Root = Resolve-WfRoot -Path $Root
}
catch {
    "环境错误：$($_.Exception.Message -replace '^\[ENV\] ', '')"
    "E2E-RESULT=ENV-MISSING"
    exit 2
}
$script:failures = 0
$script:checks = 0

function Assert([string]$name, [bool]$condition, [string]$detail) {
    $script:checks++
    if ($condition) {
        "ASSERT PASS: $name ($detail)"
    } else {
        $script:failures++
        "ASSERT FAIL: $name ($detail)"
    }
}

function Read-Text([string]$path) {
    try {
        if (Test-Path -LiteralPath $path) {
            $stream = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
            try {
                return (New-Object System.IO.StreamReader($stream)).ReadToEnd()
            } finally {
                $stream.Dispose()
            }
        }

    } catch {
        # 文件正在被写入时可能短暂不可读，下一次轮询再试。
    }
    return ""
}

function Wait-ForText([string]$path, [string]$pattern, [int]$timeoutMs = $StartTimeoutMs) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $deadline) {
        $text = Read-Text $path
        if ($text -match $pattern) { return $true }
        Start-Sleep -Milliseconds 100
    }
    return $false
}

function Wait-ForExit($process, [int]$timeoutMs = $ExitTimeoutMs) {
    if ($null -eq $process) { return $false }
    return $process.WaitForExit($timeoutMs)
}

function StatValue([string]$text, [string]$name) {
    $match = [regex]::Match($text, "(?m)^stat\s+$name\s+(\d+)\s*$")
    if ($match.Success) { return [long]$match.Groups[1].Value }
    return -1
}

function Test-PortOpen([string]$hostName, [int]$port) {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $task = $client.ConnectAsync($hostName, $port)
        if (-not $task.Wait(1000)) { return $false }
        return $client.Connected
    } catch {
        return $false
    } finally {
        $client.Dispose()
    }
}

$controllerExe = Join-Path $Root "src\userland\WinForward.Controller\bin\Release\net10.0\WinForward.Controller.exe"
$work = Join-Path $env:TEMP ("wf-userland-e2e-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $work | Out-Null
$stopFile = Join-Path $work "stop.flag"

$echo = $null
$controller = $null
$result = 1

try {
    Assert "控制器程序存在" (Test-Path -LiteralPath $controllerExe) $controllerExe
    if (-not (Test-Path -LiteralPath $controllerExe)) { throw "缺少 $controllerExe" }

    & $controllerExe disable | Out-Null
    Assert "测试开始前关闭重定向" ($LASTEXITCODE -eq 0) "exit=$LASTEXITCODE"

    $stateBeforePath = Join-Path $work "state-before.txt"
    & $controllerExe state --report $stateBeforePath | Out-Null
    Assert "读取驱动基线状态" ($LASTEXITCODE -eq 0) "exit=$LASTEXITCODE"
    $before = Read-Text $stateBeforePath
    Assert "基线 enabled=0" ($before -match "(?m)^config abi=3 enabled=0 ") $before.Trim()

    # ---- 真实目标：独立进程的回显服务 ----
    $echoReport = Join-Path $work "echo.txt"
    $echo = Start-Process -FilePath $controllerExe -NoNewWindow -PassThru `
        -ArgumentList @("echo", "--port", "0", "--connections", "3", "--report", $echoReport) `
        -RedirectStandardOutput (Join-Path $work "echo.stdout") `
        -RedirectStandardError (Join-Path $work "echo.stderr")
    Assert "echo 服务就绪" (Wait-ForText $echoReport "echo-ready port=(\d+)") (Read-Text $echoReport).Trim()
    $echoMatch = [regex]::Match((Read-Text $echoReport), "echo-ready port=(\d+)")
    if (-not $echoMatch.Success) { throw "echo 服务没有就绪" }
    $echoPort = [int]$echoMatch.Groups[1].Value
    "echo port = $echoPort"

    # ---- 配置 ----
    $configPath = Join-Path $work "policy.json"
    @{ fallbackAction = "permit"; rules = @(
        @{ name = "e2e-echo"; action = "redirect"; protocol = "tcp"; remotePorts = "$echoPort" }) } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $configPath -Encoding utf8

    & $controllerExe check --config $configPath | Out-Null
    Assert "合法配置编译通过" ($LASTEXITCODE -eq 0) "exit=$LASTEXITCODE"

    $conflictPath = Join-Path $work "conflict.json"
    @{ fallbackAction = "permit"; rules = @(
        @{ action = "permit"; remotePorts = "$echoPort" },
        @{ action = "block"; remotePorts = "$echoPort" }) } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $conflictPath -Encoding utf8
    $conflictOut = (& $controllerExe check --config $conflictPath 2>&1 | Out-String)
    Assert "歧义配置在编译期被拒" ($LASTEXITCODE -ne 0) "exit=$LASTEXITCODE"
    Assert "报错带两条规则序号" ($conflictOut -match "#0" -and $conflictOut -match "#1") $conflictOut.Trim()

    $fallbackPath = Join-Path $work "fallback-block.json"
    @{ fallbackAction = "block"; rules = @(
        @{ action = "permit"; remotePorts = "$echoPort" }) } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $fallbackPath -Encoding utf8
    $fallbackOut = (& $controllerExe check --config $fallbackPath 2>&1 | Out-String)
    Assert "兜底 block 与 permit 规则共存被拒" ($LASTEXITCODE -ne 0) "exit=$LASTEXITCODE"
    Assert "报错指向兜底动作（fallbackAction）" ($fallbackOut -match "fallbackAction") $fallbackOut.Trim()

    $reversedPath = Join-Path $work "reversed.json"
    @{ fallbackAction = "permit"; rules = @(
        @{ action = "permit" },
        @{ action = "redirect"; remotePorts = "9000-8000" }) } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reversedPath -Encoding utf8
    $reversedOut = (& $controllerExe check --config $reversedPath 2>&1 | Out-String)
    Assert "端口区间颠倒被拒" ($LASTEXITCODE -ne 0) "exit=$LASTEXITCODE"
    Assert "端口颠倒报错带规则序号" ($reversedOut -match "#1") $reversedOut.Trim()

    # ---- 第一次运行 ----
    Remove-Item -LiteralPath $stopFile -ErrorAction SilentlyContinue
    $report1 = Join-Path $work "controller1.txt"
    $controller = Start-Process -FilePath $controllerExe -NoNewWindow -PassThru `
        -ArgumentList @("run", "--config", $configPath, "--stop-file", $stopFile, "--report", $report1) `
        -RedirectStandardOutput (Join-Path $work "controller1.stdout") `
        -RedirectStandardError (Join-Path $work "controller1.stderr")

    Assert "控制器就绪" (Wait-ForText $report1 "controller-ready pid=(\d+) port=(\d+)") (Read-Text $report1).Trim()
    $readyMatch = [regex]::Match((Read-Text $report1), "controller-ready pid=(\d+) port=(\d+)")
    if (-not $readyMatch.Success) { throw "控制器没有就绪" }
    $listenerPort = [int]$readyMatch.Groups[2].Value
    $r1 = Read-Text $report1
    Assert "A1 规则数与兜底动作回读一致" ($r1 -match "controller-rules installed=1 .*count=1 fallback=0") $r1.Trim()
    Assert "监听端口已告知驱动" ($r1 -match "controller-config enabled=1 listener_port=$listenerPort") $r1.Trim()

    $client4 = (& $controllerExe client --target "127.0.0.1:$echoPort" --bytes $Bytes --family 4 2>&1 | Out-String)
    Assert "V4 客户端经重定向后回显成功" ($LASTEXITCODE -eq 0 -and $client4 -match "client OK bytes=$Bytes") $client4.Trim()
    $client6 = (& $controllerExe client --target "[::1]:$echoPort" --bytes $Bytes --family 6 2>&1 | Out-String)
    Assert "V6 客户端经重定向后回显成功" ($LASTEXITCODE -eq 0 -and $client6 -match "client OK bytes=$Bytes") $client6.Trim()

    New-Item -ItemType File -Path $stopFile -Force | Out-Null
    Assert "控制器按停止信号退出" (Wait-ForExit $controller) "timeout=${ExitTimeoutMs}ms"
    Assert "控制器第一次退出码 0" ($controller.ExitCode -eq 0) "exit=$($controller.ExitCode)"

    $r1 = Read-Text $report1
    Assert "退出时关闭重定向" ($r1 -match "controller-config-disabled enabled=0") $r1.Trim()
    Assert "A3/V4 原始目的地读回正确" ($r1 -match "relay accept #\d+ original=127\.0\.0\.1:$echoPort family=IPv4") $r1.Trim()
    Assert "A3/V6 原始目的地读回正确" ($r1 -match "relay accept #\d+ original=::1:$echoPort family=IPv6") $r1.Trim()
    Assert "A4 两条会话各搬运 $Bytes 字节" (([regex]::Matches($r1, "relay close #\d+ in=$Bytes out=$Bytes")).Count -eq 2) $r1.Trim()
    Assert "退出摘要 2 条连接、双向 $($Bytes * 2) 字节" ($r1 -match "controller-stop accepted=2 refused=0 bytes_in=$($Bytes * 2) bytes_out=$($Bytes * 2)") $r1.Trim()
    Assert "没有第二条 accept（没有成环）" (([regex]::Matches($r1, "relay accept #")).Count -eq 2) $r1.Trim()
    Assert "控制器输出退出码 0 行" ($r1 -match "controller-exit code=0") $r1.Trim()
    Assert "A5 第一轮中继带 records（闸 2 路径）" (([regex]::Matches($r1, "relay accept #\d+ .* records=[1-9]\d*")).Count -eq 2) $r1.Trim()

    $stateAfter1Path = Join-Path $work "state-after1.txt"
    & $controllerExe state --report $stateAfter1Path | Out-Null
    $after1 = Read-Text $stateAfter1Path
    Assert "退出后驱动仍加载且规则表仍在" ($after1 -match "rule abi=3 installed=1 .*count=1 fallback=0") $after1.Trim()
    Assert "退出后 enabled=0" ($after1 -match "(?m)^config abi=3 enabled=0 ") $after1.Trim()
    Assert "A5 redirected 精确 +2" ((StatValue $after1 "redirected") - (StatValue $before "redirected") -eq 2) "delta=$((StatValue $after1 'redirected') - (StatValue $before 'redirected'))"
    Assert "A5 contexts_allocated 精确 +2" ((StatValue $after1 "contexts_allocated") - (StatValue $before "contexts_allocated") -eq 2) "delta=$((StatValue $after1 'contexts_allocated') - (StatValue $before 'contexts_allocated'))"
    # 两条出站连接都带 redirect records，第一轮走驱动闸 2；第二轮用 --no-redirect-records 走闸 4。
    # 不要写成「两者之和」：求和会把两条路径混成一个数，拆掉任意一道闸断言仍然可能绿。
    $redirectStateDelta = (StatValue $after1 "permitted_redirect_state") - (StatValue $before "permitted_redirect_state")
    $selfProcessDelta = (StatValue $after1 "permitted_self_process") - (StatValue $before "permitted_self_process")
    Assert "A5 中继出站带 records，走闸 2（redirect_state +2）" ($redirectStateDelta -eq 2) "delta=$redirectStateDelta"
    Assert "A5 第一轮不走 self-PID 闸（self_process +0）" ($selfProcessDelta -eq 0) "delta=$selfProcessDelta"


    Assert "规则表替换一次" ((StatValue $after1 "rules_replaced") - (StatValue $before "rules_replaced") -eq 1) "delta=$((StatValue $after1 'rules_replaced') - (StatValue $before 'rules_replaced'))"
    Assert "旧监听端口已释放" (-not (Test-PortOpen "127.0.0.1" $listenerPort)) "port=$listenerPort"

    # ---- 第二次运行：不卸载驱动即可重启；这一轮故意不带 records，走驱动闸 4（self-PID）----
    Remove-Item -LiteralPath $stopFile -ErrorAction SilentlyContinue
    $report2 = Join-Path $work "controller2.txt"
    $controller = Start-Process -FilePath $controllerExe -NoNewWindow -PassThru `
        -ArgumentList @("run", "--config", $configPath, "--stop-file", $stopFile, "--report", $report2, "--no-redirect-records") `
        -RedirectStandardOutput (Join-Path $work "controller2.stdout") `
        -RedirectStandardError (Join-Path $work "controller2.stderr")

    Assert "重启后控制器就绪" (Wait-ForText $report2 "controller-ready pid=(\d+) port=(\d+)") (Read-Text $report2).Trim()
    $clientAgain = (& $controllerExe client --target "127.0.0.1:$echoPort" --bytes $Bytes --family 4 2>&1 | Out-String)
    Assert "A6 重启后客户端仍能回显" ($LASTEXITCODE -eq 0 -and $clientAgain -match "client OK bytes=$Bytes") $clientAgain.Trim()

    New-Item -ItemType File -Path $stopFile -Force | Out-Null
    Assert "重启后的控制器退出" (Wait-ForExit $controller) "timeout=${ExitTimeoutMs}ms"
    Assert "控制器第二次退出码 0" ($controller.ExitCode -eq 0) "exit=$($controller.ExitCode)"
    $r2 = Read-Text $report2
    Assert "重启后的退出摘要 1 条连接" ($r2 -match "controller-stop accepted=1 refused=0 bytes_in=$Bytes bytes_out=$Bytes") $r2.Trim()

    $stateAfter2Path = Join-Path $work "state-after2.txt"
    & $controllerExe state --report $stateAfter2Path | Out-Null
    $after2 = Read-Text $stateAfter2Path
    Assert "重启后 enabled 又回到 0" ($after2 -match "(?m)^config abi=3 enabled=0 ") $after2.Trim()
    Assert "规则表替换累计 +2" ((StatValue $after2 "rules_replaced") - (StatValue $before "rules_replaced") -eq 2) "delta=$((StatValue $after2 'rules_replaced') - (StatValue $before 'rules_replaced'))"
    $selfProcessSecond = (StatValue $after2 "permitted_self_process") - (StatValue $after1 "permitted_self_process")
    $redirectStateSecond = (StatValue $after2 "permitted_redirect_state") - (StatValue $after1 "permitted_redirect_state")
    Assert "A5 闸 4：不带 records 时由 self-PID 放行（self_process +1）" ($selfProcessSecond -eq 1) "delta=$selfProcessSecond"
    Assert "A5 闸 4 路径不再命中闸 2（redirect_state +0）" ($redirectStateSecond -eq 0) "delta=$redirectStateSecond"
    Assert "A5 第二轮 accept 行 records=0" ($r2 -match "relay accept #\d+ .* records=0") $r2.Trim()


    # ---- 目标侧对账 ----
    Assert "echo 服务处理了 3 条连接" (Wait-ForText $echoReport "echo-done connections=3" 15000) (Read-Text $echoReport).Trim()
    $echo.WaitForExit(15000) | Out-Null
    Assert "echo 总回显字节 $($Bytes * 3)" ((Read-Text $echoReport) -match "echo-done connections=3 bytes=$($Bytes * 3)") (Read-Text $echoReport).Trim()
    Assert "echo 退出码 0" ($echo.ExitCode -eq 0) "exit=$($echo.ExitCode)"

    $result = 0
} catch {
    "异常：$($_.Exception.Message)"
    $result = 1
} finally {
    if ($null -ne $controller -and -not $controller.HasExited) {
        $controller.Kill()
        $controller.WaitForExit(5000) | Out-Null
    }
    if ($null -ne $echo -and -not $echo.HasExited) {
        $echo.Kill()
        $echo.WaitForExit(5000) | Out-Null
    }
    & $controllerExe disable 2>$null | Out-Null
    if ($KeepWork) {
        "E2E-WORK=$work"
    } else {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }

}

if ($result -ne 0 -or $script:failures -gt 0) {
    "===== userland-e2e 失败：$($script:failures) 条断言（checks=$($script:checks)）====="
    "E2E-RESULT=FAIL"
    exit 1
}

"===== userland-e2e 全部通过：$($script:checks) 条断言 ====="
"E2E-RESULT=PASS checks=$($script:checks)"
exit 0

