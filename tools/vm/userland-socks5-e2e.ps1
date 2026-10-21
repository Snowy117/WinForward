param(
    [string]$Root = "C:\wfbench\wfprobe-socks5",
    [string]$TargetHost = "192.168.100.4",
    [int]$TargetPort = 40001,
    [int]$ProxyPort = 18080,
    [int]$DeadProxyPort = 18081,
    [int]$Bytes = 1048576,
    [int]$StartTimeoutMs = 20000,
    [int]$ExitTimeoutMs = 60000,
    [switch]$KeepWork
)

# 端到端实证（SOCKS5 版）：客户端 → 驱动重定向 → 控制器 → 替身 SOCKS5（loopback）→ 跨机 target。
# 跨机 target 是宿主上的裸 TCP 回显，不在本机上：本脚本只断言中继侧与替身侧的事实，
# target 侧的计数由跑脚本的人在宿主上核对（echo-done connections=...）。
#
# 两轮：
#   1. 失败轮：socks5 指向没人监听的端口。客户端必须明确失败，控制器必须记 relay error，
#      而且不能出现 relay connected（静默回退直连会在这里露馅）。
#   2. 成功轮：1 MiB 伪随机数据，客户端自己核对 SHA-256，替身核对 connections=/connects=/字节数。
#
# 每条断言失败都会让退出码非 0；末行是 E2E-RESULT=PASS/FAIL（ASCII，便于跨控制台代码页解析）。

$ErrorActionPreference = "Stop"
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
        if (-not $task.Wait(3000)) { return $false }
        return $client.Connected
    } catch {
        return $false
    } finally {
        $client.Dispose()
    }
}

function Write-Config([string]$path, [int]$upstreamPort) {
    @{ fallbackAction = "permit"
       socks5 = @{ host = "127.0.0.1"; port = $upstreamPort }
       rules = @(
           @{ name = "socks5-upstream"; action = "permit"; protocol = "tcp"; process = $toolExe },
           @{ name = "e2e-target"; action = "redirect"; protocol = "tcp"; remotePorts = "$TargetPort" }) } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $path -Encoding utf8
}

$controllerExe = Join-Path $Root "src\userland\WinForward.Controller\bin\Release\net10.0\WinForward.Controller.exe"
$toolExe = Join-Path $Root "tools\socks5\bin\Release\net10.0\wf-socks5.exe"
$work = Join-Path $env:TEMP ("wf-socks5-e2e-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $work | Out-Null
$stopFile = Join-Path $work "stop.flag"

$proxy = $null
$controller = $null
$result = 1

try {
    Assert "控制器程序存在" (Test-Path -LiteralPath $controllerExe) $controllerExe
    Assert "SOCKS5 替身存在" (Test-Path -LiteralPath $toolExe) $toolExe
    if (-not (Test-Path -LiteralPath $controllerExe)) { throw "缺少 $controllerExe" }
    if (-not (Test-Path -LiteralPath $toolExe)) { throw "缺少 $toolExe" }

    $service = (sc.exe query wfprobe 2>&1 | Out-String)
    Assert "驱动已加载并 RUNNING" ($service -match "RUNNING") $service.Trim()

    & $controllerExe disable | Out-Null
    Assert "测试开始前关闭重定向" ($LASTEXITCODE -eq 0) "exit=$LASTEXITCODE"

    $stateBeforePath = Join-Path $work "state-before.txt"
    & $controllerExe state --report $stateBeforePath | Out-Null
    Assert "读取驱动基线状态" ($LASTEXITCODE -eq 0) "exit=$LASTEXITCODE"
    $before = Read-Text $stateBeforePath
    Assert "基线 enabled=0" ($before -match "(?m)^config abi=2 enabled=0 ") $before.Trim()

    Assert "跨机 target 可达 ${TargetHost}:${TargetPort}" (Test-PortOpen $TargetHost $TargetPort) "host=$TargetHost port=$TargetPort"

    # ---- 替身 SOCKS5：只绑 VM 的 loopback，对齐「WinForward → 上游走 loopback」的形态 ----
    $proxyReport = Join-Path $work "proxy.txt"
    $proxy = Start-Process -FilePath $toolExe -NoNewWindow -PassThru `
        -ArgumentList @("serve", "--listen", "127.0.0.1", "--port", "$ProxyPort", "--connections", "1",
                        "--timeout-ms", "$ExitTimeoutMs", "--report", $proxyReport) `
        -RedirectStandardOutput (Join-Path $work "proxy.stdout") `
        -RedirectStandardError (Join-Path $work "proxy.stderr")
    Assert "替身 SOCKS5 就绪" (Wait-ForText $proxyReport "socks5-ready listen=127\.0\.0\.1:$ProxyPort") (Read-Text $proxyReport).Trim()

    # ---- 配置：socks5 段 + 放行替身进程（否则替身的出站会被同一条 redirect 规则改道） ----
    $okConfig = Join-Path $work "policy-socks5.json"
    $badConfig = Join-Path $work "policy-socks5-dead.json"
    Write-Config $okConfig $ProxyPort
    Write-Config $badConfig $DeadProxyPort

    $checkOut = (& $controllerExe check --config $okConfig 2>&1 | Out-String)
    Assert "带 socks5 段的配置编译通过" ($LASTEXITCODE -eq 0 -and $checkOut -match "rules=2") $checkOut.Trim()
    $checkBad = (& $controllerExe check --config $badConfig 2>&1 | Out-String)
    Assert "失败轮的配置也合法（只是上游没人听）" ($LASTEXITCODE -eq 0 -and $checkBad -match "rules=2") $checkBad.Trim()

    # ---- 失败轮：上游不可达 ----
    Remove-Item -LiteralPath $stopFile -ErrorAction SilentlyContinue
    $failReport = Join-Path $work "controller-fail.txt"
    $controller = Start-Process -FilePath $controllerExe -NoNewWindow -PassThru `
        -ArgumentList @("run", "--config", $badConfig, "--stop-file", $stopFile, "--report", $failReport) `
        -RedirectStandardOutput (Join-Path $work "controller-fail.stdout") `
        -RedirectStandardError (Join-Path $work "controller-fail.stderr")
    Assert "失败轮控制器就绪" (Wait-ForText $failReport "controller-ready pid=(\d+) port=(\d+)") (Read-Text $failReport).Trim()
    Assert "失败轮启动时报告上游" ((Read-Text $failReport) -match "controller-upstream socks5=127\.0\.0\.1:$DeadProxyPort") (Read-Text $failReport).Trim()

    $clientFail = (& $controllerExe client --target "${TargetHost}:${TargetPort}" --bytes $Bytes --family 4 --timeout-ms 15000 2>&1 | Out-String)
    Assert "A3 上游连不上时客户端明确失败" ($LASTEXITCODE -ne 0 -and $clientFail -match "client FAIL") $clientFail.Trim()
    Assert "A3 控制器把 SOCKS5 失败记成 relay error" (Wait-ForText $failReport "relay error #\d+ to=${TargetHost}:${TargetPort} Socks5Exception:") (Read-Text $failReport).Trim()
    $failText = Read-Text $failReport
    Assert "A3 报错里带上游地址" ($failText -match "SOCKS5 上游 127\.0\.0\.1:$DeadProxyPort") $failText.Trim()
    Assert "A3 失败时没有静默回退直连（没有 relay connected）" (-not ($failText -match "relay connected #")) $failText.Trim()

    New-Item -ItemType File -Path $stopFile -Force | Out-Null
    Assert "失败轮控制器按停止信号退出" (Wait-ForExit $controller) "timeout=${ExitTimeoutMs}ms"
    Assert "失败轮控制器退出码 0" ($controller.ExitCode -eq 0) "exit=$($controller.ExitCode)"

    # ---- 成功轮：1 MiB 经 SOCKS5 到跨机 target ----
    Remove-Item -LiteralPath $stopFile -ErrorAction SilentlyContinue
    $okReport = Join-Path $work "controller-ok.txt"
    $controller = Start-Process -FilePath $controllerExe -NoNewWindow -PassThru `
        -ArgumentList @("run", "--config", $okConfig, "--stop-file", $stopFile, "--report", $okReport) `
        -RedirectStandardOutput (Join-Path $work "controller-ok.stdout") `
        -RedirectStandardError (Join-Path $work "controller-ok.stderr")
    Assert "成功轮控制器就绪" (Wait-ForText $okReport "controller-ready pid=(\d+) port=(\d+)") (Read-Text $okReport).Trim()
    Assert "成功轮启动时报告上游" ((Read-Text $okReport) -match "controller-upstream socks5=127\.0\.0\.1:$ProxyPort") (Read-Text $okReport).Trim()

    $client = (& $controllerExe client --target "${TargetHost}:${TargetPort}" --bytes $Bytes --family 4 --timeout-ms 60000 2>&1 | Out-String)
    Assert "A6 客户端 1 MiB 经 SOCKS5 回显成功" ($LASTEXITCODE -eq 0 -and $client -match "client OK bytes=$Bytes sha256=[0-9A-Fa-f]{64}") $client.Trim()
    $hashMatch = [regex]::Match($client, "sha256 sent=([0-9A-Fa-f]{64}) received=([0-9A-Fa-f]{64})")
    Assert "A6 客户端报出双向校验和" $hashMatch.Success $client.Trim()
    if ($hashMatch.Success) {
        Assert "A6 发送与接收的 SHA-256 一致" ($hashMatch.Groups[1].Value -eq $hashMatch.Groups[2].Value) "sent=$($hashMatch.Groups[1].Value) received=$($hashMatch.Groups[2].Value)"
    }

    New-Item -ItemType File -Path $stopFile -Force | Out-Null
    Assert "成功轮控制器按停止信号退出" (Wait-ForExit $controller) "timeout=${ExitTimeoutMs}ms"
    Assert "成功轮控制器退出码 0" ($controller.ExitCode -eq 0) "exit=$($controller.ExitCode)"

    $okText = Read-Text $okReport
    Assert "A1 中继读回原始目的地" ($okText -match "relay accept #1 original=${TargetHost}:${TargetPort} family=IPv4") $okText.Trim()
    Assert "A1 出站经 SOCKS5（via=socks5:）" ($okText -match "relay connected #1 to=${TargetHost}:${TargetPort} via=socks5:127\.0\.0\.1:$ProxyPort") $okText.Trim()
    Assert "A6 中继双向各搬运 $Bytes 字节" (([regex]::Matches($okText, "relay close #\d+ in=$Bytes out=$Bytes")).Count -eq 1) $okText.Trim()
    Assert "退出摘要 accepted=1 refused=0" ($okText -match "controller-stop accepted=1 refused=0 bytes_in=$Bytes bytes_out=$Bytes") $okText.Trim()
    Assert "只有一次 accept（替身的出站没有被改道成环）" (([regex]::Matches($okText, "relay accept #")).Count -eq 1) $okText.Trim()
    Assert "成功轮没有 relay error" (-not ($okText -match "relay error")) $okText.Trim()

    Assert "替身完成 1 次 CONNECT 且字节吻合" (Wait-ForText $proxyReport "socks5-done connections=1 connects=1 bytes_in=$Bytes bytes_out=$Bytes" 30000) (Read-Text $proxyReport).Trim()
    $proxyText = Read-Text $proxyReport
    Assert "A6 替身收到的目标就是原始目的地" ($proxyText -match "socks5 accept #1 from=127\.0\.0\.1:\d+ atyp=1 target=${TargetHost}:${TargetPort}") $proxyText.Trim()
    Assert "A6 替身 CONNECT 成功" ($proxyText -match "socks5 connect #1 target=${TargetHost}:${TargetPort} ok=true") $proxyText.Trim()
    $proxy.WaitForExit($ExitTimeoutMs) | Out-Null
    Assert "替身退出码 0" ($proxy.ExitCode -eq 0) "exit=$($proxy.ExitCode)"

    $stateAfterPath = Join-Path $work "state-after.txt"
    & $controllerExe state --report $stateAfterPath | Out-Null
    $after = Read-Text $stateAfterPath
    Assert "退出后 enabled=0" ($after -match "(?m)^config abi=2 enabled=0 ") $after.Trim()
    $redirectedDelta = (StatValue $after "redirected") - (StatValue $before "redirected")
    Assert "A6 两轮客户端各被重定向一次（redirected +2）" ($redirectedDelta -eq 2) "delta=$redirectedDelta"
    $contextDelta = (StatValue $after "contexts_allocated") - (StatValue $before "contexts_allocated")
    Assert "A6 contexts_allocated +2" ($contextDelta -eq 2) "delta=$contextDelta"

    # ---- 证据行：报告里的关键几行原样打出来，供人工与本批次报告引用 ----
    $okText -split "`n" | Where-Object { $_ -match "controller-upstream|relay accept #1|relay connected #1|relay close #1|controller-stop" } |
        ForEach-Object { "EVIDENCE relay $($_.Trim())" }
    $proxyText -split "`n" | Where-Object { $_ -match "socks5 accept #1|socks5 connect #1|socks5 close #1|socks5-done" } |
        ForEach-Object { "EVIDENCE socks5 $($_.Trim())" }
    $client -split "`n" | Where-Object { $_ -match "client OK|client sha256" } |
        ForEach-Object { "EVIDENCE client $($_.Trim())" }

    $result = 0
} catch {
    "异常：$($_.Exception.Message)"
    $result = 1
} finally {
    if ($null -ne $controller -and -not $controller.HasExited) {
        $controller.Kill()
        $controller.WaitForExit(5000) | Out-Null
    }
    if ($null -ne $proxy -and -not $proxy.HasExited) {
        $proxy.Kill()
        $proxy.WaitForExit(5000) | Out-Null
    }
    & $controllerExe disable 2>$null | Out-Null
    if ($KeepWork) {
        "E2E-WORK=$work"
    } else {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($result -ne 0 -or $script:failures -gt 0) {
    "===== userland-socks5-e2e 失败：$($script:failures) 条断言（checks=$($script:checks)）====="
    "E2E-RESULT=FAIL"
    exit 1
}

"===== userland-socks5-e2e 全部通过：$($script:checks) 条断言 ====="
"E2E-RESULT=PASS checks=$($script:checks)"
exit 0
