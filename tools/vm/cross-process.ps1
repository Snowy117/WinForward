param(
    [string]$Root = "C:\wfbench\wfprobe",
    [string]$Remote = "1.2.3.4:443",
    [int]$ListenerPort = 18080,
    [string]$Bind = "127.0.0.1"
)

$ErrorActionPreference = "Stop"
$exe = Join-Path $Root "tools\wfprobe\bin\Release\net11.0\wfprobe.exe"
if (-not (Test-Path $exe)) { throw "测试程序不存在：$exe" }

$listenerLog = Join-Path $env:TEMP "wfprobe-cross-listener.log"
$listenerErr = Join-Path $env:TEMP "wfprobe-cross-listener.err"
$clientLog = Join-Path $env:TEMP "wfprobe-cross-client.log"
$clientErr = Join-Path $env:TEMP "wfprobe-cross-client.err"
Remove-Item $listenerLog, $listenerErr, $clientLog, $clientErr -ErrorAction SilentlyContinue

$script:failures = 0
function Assert([string]$name, [bool]$condition, [string]$detail) {
    if ($condition) {
        "ASSERT PASS: $name ($detail)"
    } else {
        $script:failures++
        "ASSERT FAIL: $name ($detail)"
    }
}

$listener = $null
$client = $null
$result = 1
try {
    $listener = Start-Process -FilePath $exe -ArgumentList @("listen", "--bind", $Bind, "--port", $ListenerPort, "--count", "1") -PassThru -RedirectStandardOutput $listenerLog -RedirectStandardError $listenerErr -WindowStyle Hidden
    Start-Sleep -Seconds 1
    $client = Start-Process -FilePath $exe -ArgumentList @("client", "--remote", $Remote, "--delay-ms", "5000", "--timeout-ms", "6000") -PassThru -RedirectStandardOutput $clientLog -RedirectStandardError $clientErr -WindowStyle Hidden

    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        if ((Test-Path $clientLog) -and ((Get-Content $clientLog -Raw -ErrorAction SilentlyContinue) -match "delay-ms=")) {
            $ready = $true
            break
        }
        Start-Sleep -Milliseconds 250
    }
    if (-not $ready) { throw "client 没有在预期时间内打印 delay-ms 行，无法保证先配置后连接" }

    "listener pid=$($listener.Id) client pid=$($client.Id)"
    & $exe control --enabled 1 --mode documented --port $ListenerPort --target-pid $client.Id --listener-pid $listener.Id | Out-Null

    $null = $client.WaitForExit(15000)
    if (-not $client.HasExited) {
        $client.Kill()
        $client.WaitForExit()
        "client 未退出，已终止"
    }
    $null = $listener.WaitForExit(10000)

    & $exe control --enabled 0 | Out-Null

    "--- listener 输出 ---"
    Get-Content $listenerLog -ErrorAction SilentlyContinue
    "--- client 输出 ---"
    Get-Content $clientLog -ErrorAction SilentlyContinue

    $listenerText = Get-Content $listenerLog -Raw -ErrorAction SilentlyContinue
    $clientText = Get-Content $clientLog -Raw -ErrorAction SilentlyContinue

    Assert "listener 收到 redirect context" ([bool]($listenerText -match "redirect-context=36B")) "36B"
    Assert "context pid 匹配 client" ([bool]($listenerText -match ('pid=' + [string]$client.Id + ' family='))) "pid=$($client.Id)"
    Assert "context original 匹配" ([bool]($listenerText -match ('original=' + [regex]::Escape($Remote)))) "original=$Remote"
    Assert "client 收到回显" ([bool]($clientText -match "echo=[1-9][0-9]*B")) "echo"

    if ($script:failures -gt 0) {
        "cross-process verdict: FAIL ($script:failures assertion(s))"
        $result = 1
    } else {
        "cross-process verdict: PASS"
        $result = 0
    }
}
finally {
    if ($null -ne $client -and -not $client.HasExited) {
        $client.Kill()
        $client.WaitForExit()
    }
    if ($null -ne $listener -and -not $listener.HasExited) {
        $listener.Kill()
        $listener.WaitForExit()
    }
    & $exe control --enabled 0 2>$null | Out-Null
}
exit $result
