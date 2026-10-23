#requires -Version 7.0
<#
.SYNOPSIS
    Product-level forwarded-UDP reject harness (WinForward.Controller + real ForwardedUdpRelay).
.DESCRIPTION
    本脚本只做“产品 reject 路径”的可重复编排，不替代 Linux 侧的 SOCKS5 替身与客户端：
      1. Linux 先启动：python3 tools/linux/fake_socks5_udp.py 40500 --reject
      2. 本脚本在 VM 上启动真实 WinForward.Controller，并在 forwarded 的原始目的地址上绑 decoy；
      3. READY 之后在 Linux 上跑：
         python3 tools/linux/forwarded_udp_client.py --target <RemoteAddress>:<RemotePort> --count 3 --no-verify
      4. 脚本等待 -ClientWaitSeconds，写 stop 文件，解析 controller 日志并断言：
         relayed=0、back=0、associations>0、failures>0、decoy 收到 0 字节。
    配置里的 forwarded 规则必须把 <RemoteAddress>:<RemotePort> 指到 -Config 中的 SOCKS5 upstream。
    WinpkFilter/WFP 驱动必须先安装并 RUNNING（本脚本不安装驱动）。
.NOTES
    仅用于验收与回归；输出中的 REJECT-E2E-RESULT=PASS 只在全部断言成立时出现。
#>
param(
    [Parameter(Mandatory = $true)][string]$Config,
    [string]$Controller = "C:\workspace\WinForward.Kernel\src\userland\WinForward.Controller\bin\Release\net10.0\WinForward.Controller.exe",
    [string]$RemoteAddress = "192.168.100.99",
    [int]$RemotePort = 40250,
    [int]$ClientWaitSeconds = 20,
    [string]$LogPath = "$env:TEMP\wf-forwarded-reject-controller.log",
    [string]$StopFile = "$env:TEMP\wf-forwarded-reject.stop",
    [string]$ClientReport = ""
)

$ErrorActionPreference = "Stop"
$failures = 0
function Assert-True([bool]$Condition, [string]$Name, [string]$Detail) {
    if ($Condition) {
        "ASSERT PASS: $Name ($Detail)"
    } else {
        $script:failures++
        "ASSERT FAIL: $Name ($Detail)"
    }
}

if (-not (Test-Path -LiteralPath $Controller)) { "缺少 Controller：$Controller"; exit 2 }
if (-not (Test-Path -LiteralPath $Config)) { "缺少 config：$Config"; exit 2 }
Remove-Item -LiteralPath $LogPath, "$LogPath.err" -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $StopFile -Force -ErrorAction SilentlyContinue

# 上一次运行可能留下 stop 文件；必须在启动前确保不存在，否则 controller 会立即退出。
if (Test-Path -LiteralPath $StopFile) { throw "stop 文件无法删除：$StopFile" }
$process = Start-Process -FilePath $Controller `
    -ArgumentList @("run", "--config", $Config, "--stop-file", $StopFile) `
    -RedirectStandardOutput $LogPath -RedirectStandardError "$LogPath.err" `
    -PassThru -WindowStyle Hidden

$ready = $false
for ($i = 0; $i -lt 20; $i++) {
    Start-Sleep -Seconds 1
    if (Test-Path -LiteralPath $LogPath) {
        if (Select-String -LiteralPath $LogPath -Pattern "controller-ready" -Quiet) { $ready = $true; break }
    }
    if ($process.HasExited) { break }
}
Assert-True $ready "controller 进入 READY" "pid=$($process.Id) log=$LogPath"
Assert-True (-not $process.HasExited) "controller 在 READY 后仍在运行" "exited=$($process.HasExited)"
if (-not $ready -or $process.HasExited) {
    $process.Kill()
    "REJECT-E2E-RESULT=FAIL failures=$failures"
    exit 1
}

$decoy = $null
$decoyCount = 0
$decoyBytes = 0
try {
    $address = [System.Net.IPAddress]::Parse($RemoteAddress)
    $decoy = [System.Net.Sockets.Socket]::new($address.AddressFamily,
        [System.Net.Sockets.SocketType]::Dgram, [System.Net.Sockets.ProtocolType]::Udp)
    $decoy.Bind([System.Net.IPEndPoint]::new($address, $RemotePort))
    "DECOY-BOUND $RemoteAddress`:$RemotePort"
} catch {
    "DECOY-BIND-FAILED $($_.Exception.Message)"
    $failures++
}

"READY：请在 $ClientWaitSeconds 秒内用 Linux 客户端发 3 条，并把客户端输出放到 -ClientReport=$ClientReport；log=$LogPath"
$clientReceived = -1
$reportDeadline = (Get-Date).AddSeconds($ClientWaitSeconds)
while ((Get-Date) -lt $reportDeadline -and
       (-not $ClientReport -or -not (Test-Path -LiteralPath $ClientReport))) {
    Start-Sleep -Milliseconds 500
}
Start-Sleep -Seconds 1
if (-not $ClientReport -or -not (Test-Path -LiteralPath $ClientReport)) {
    $failures++
    "ASSERT FAIL: 缺少客户端回报文件（-ClientReport）：$ClientReport"
} else {
    $report = Get-Content -LiteralPath $ClientReport -Raw
    if ($report -match 'received=(\d+)/(\d+)') {
        $clientReceived = [int]$Matches[1]
        $expected = [int]$Matches[2]
        Assert-True ($clientReceived -eq 0 -and $expected -eq 3) "客户端收到 0/3（没有回退直连）" "received=$clientReceived/$expected report=$ClientReport"
    } else {
        $failures++
        "ASSERT FAIL: 客户端回报里没有 received=N/M：$report"
    }
}

New-Item -ItemType File -Path $StopFile -Force | Out-Null
if (-not $process.WaitForExit(15000)) {
    "controller 未在 15s 内退出，强制结束"
    $process.Kill()
    $failures++
}

if ($null -ne $decoy) {
    try {
        $decoy.Blocking = $false
        $buffer = [byte[]]::new(65535)
        while ($true) {
            $received = $decoy.Receive($buffer)
            if ($received -le 0) { break }
            $decoyCount++
            $decoyBytes += $received
        }
    } catch [System.Net.Sockets.SocketException] {
        # WouldBlock 表示 decoy 没有收到任何帧。
    } finally {
        $decoy.Dispose()
    }
}
Assert-True ($decoyCount -eq 0) "decoy 没有收到直连回退帧" "count=$decoyCount bytes=$decoyBytes"

$log = Get-Content -LiteralPath $LogPath -Raw -ErrorAction SilentlyContinue
Assert-True ($log -match "udp-relay association-failed") "controller 记录了 association-failed" "log=$LogPath"
$stop = [regex]::Match($log, "udp-relay stop in=(\d+) relayed=(\d+) back=(\d+) associations=(\d+) failures=(\d+)")
Assert-True $stop.Success "controller 打印了 udp-relay stop" "matched=$($stop.Success)"
if ($stop.Success) {
    $relayed = [int]$stop.Groups[2].Value
    $back = [int]$stop.Groups[3].Value
    $associations = [int]$stop.Groups[4].Value
    $failed = [int]$stop.Groups[5].Value
    Assert-True ($relayed -eq 0) "没有成功中继的 datagram" "relayed=$relayed"
    Assert-True ($back -eq 0) "没有回程字节" "back=$back"
    Assert-True ($associations -gt 0) "至少尝试过一次 UDP ASSOCIATE" "associations=$associations"
    Assert-True ($failed -gt 0) "所有关联都失败" "failures=$failed"
}

if ($failures -eq 0) {
    "REJECT-E2E-RESULT=PASS"
    exit 0
}
"REJECT-E2E-RESULT=FAIL failures=$failures"
exit 1
