param(
    [string]$Root = "C:\wfbench\wfprobe",
    [string]$Remote = "1.2.3.4:443",
    [int]$DeadPort = 18081
)

$ErrorActionPreference = "Stop"
$exe = Join-Path $Root "tools\wfprobe\bin\Release\net11.0\wfprobe.exe"
if (-not (Test-Path $exe)) { throw "测试程序不存在：$exe" }

function Get-PoolTag([string]$tag) {
    $line = & $exe pooltag --tag $tag | Select-String "nonpaged_allocs="
    if (-not $line) { throw "pooltag 输出缺失" }
    $text = $line.Line
    $allocs = [uint64]([regex]::Match($text, 'nonpaged_allocs=(\d+)').Groups[1].Value)
    $frees = [uint64]([regex]::Match($text, 'nonpaged_frees=(\d+)').Groups[1].Value)
    $outstanding = [uint64]([regex]::Match($text, 'nonpaged_outstanding=(\d+)').Groups[1].Value)
    return [pscustomobject]@{ Allocs = $allocs; Frees = $frees; Outstanding = $outstanding }
}

function Get-Stats {
    $line = & $exe stats | Select-String "redirected="
    if (-not $line) { throw "stats 输出缺失" }
    $text = $line.Line
    $redirected = [uint64]([regex]::Match($text, 'redirected=(\d+)').Groups[1].Value)
    $contexts = [uint64]([regex]::Match($text, 'contexts_allocated=(\d+)').Groups[1].Value)
    return [pscustomobject]@{ Redirected = $redirected; Contexts = $contexts }
}

$script:failures = 0
function Assert([string]$name, [bool]$condition, [string]$detail) {
    if ($condition) {
        "ASSERT PASS: $name ($detail)"
    } else {
        $script:failures++
        "ASSERT FAIL: $name ($detail)"
    }
}

$client = $null
$result = 1
try {
    "=== 目标无人监听：context 仍应被 WFP 释放 ==="
    $statsBefore = Get-Stats
    $poolBefore = Get-PoolTag "WfPr"
    "before: allocs=$($poolBefore.Allocs) frees=$($poolBefore.Frees) outstanding=$($poolBefore.Outstanding) redirected=$($statsBefore.Redirected) contexts=$($statsBefore.Contexts)"

    $clientLog = Join-Path $env:TEMP "wfprobe-deadport-client.log"
    $clientErr = Join-Path $env:TEMP "wfprobe-deadport-client.err"
    Remove-Item $clientLog, $clientErr -ErrorAction SilentlyContinue
    $client = Start-Process -FilePath $exe -ArgumentList @("client", "--remote", $Remote, "--delay-ms", "3000", "--timeout-ms", "5000") -PassThru -RedirectStandardOutput $clientLog -RedirectStandardError $clientErr -WindowStyle Hidden
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        if ((Test-Path $clientLog) -and ((Get-Content $clientLog -Raw -ErrorAction SilentlyContinue) -match "delay-ms=")) {
            $ready = $true
            break
        }
        Start-Sleep -Milliseconds 250
    }
    if (-not $ready) { throw "client 没有在预期时间内打印 delay-ms 行" }
    & $exe control --enabled 1 --mode documented --port $DeadPort --target-pid $client.Id --listener-pid $client.Id | Out-Null
    $null = $client.WaitForExit(12000)
    if (-not $client.HasExited) {
        $client.Kill()
        $client.WaitForExit()
        "client 未退出，已终止"
    }
    & $exe control --enabled 0 | Out-Null
    Start-Sleep -Seconds 2

    $statsAfter = Get-Stats
    $poolAfter = Get-PoolTag "WfPr"
    "after: allocs=$($poolAfter.Allocs) frees=$($poolAfter.Frees) outstanding=$($poolAfter.Outstanding) redirected=$($statsAfter.Redirected) contexts=$($statsAfter.Contexts)"
    "client exit=$($client.ExitCode)"
    Get-Content $clientLog -ErrorAction SilentlyContinue

    $allocDelta = [int64]$poolAfter.Allocs - [int64]$poolBefore.Allocs
    $freeDelta = [int64]$poolAfter.Frees - [int64]$poolBefore.Frees
    $contextDelta = [int64]$statsAfter.Contexts - [int64]$statsBefore.Contexts
    $redirectedDelta = [int64]$statsAfter.Redirected - [int64]$statsBefore.Redirected

    Assert "pool alloc +1" ($allocDelta -eq 1) "delta=$allocDelta"
    Assert "pool free +1" ($freeDelta -eq 1) "delta=$freeDelta"
    Assert "pool outstanding == 0" ($poolAfter.Outstanding -eq 0) "outstanding=$($poolAfter.Outstanding)"
    Assert "contexts_allocated +1" ($contextDelta -eq 1) "delta=$contextDelta"
    Assert "redirected >= +1" ($redirectedDelta -ge 1) "delta=$redirectedDelta"
    Assert "client failed" ($client.ExitCode -ne 0) "exit=$($client.ExitCode)"

    if ($script:failures -gt 0) {
        "edge-cases verdict: FAIL ($script:failures assertion(s))"
        $result = 1
    } else {
        "edge-cases verdict: PASS"
        $result = 0
    }
}
finally {
    if ($null -ne $client -and -not $client.HasExited) {
        $client.Kill()
        $client.WaitForExit()
    }
    & $exe control --enabled 0 2>$null | Out-Null
}
exit $result
