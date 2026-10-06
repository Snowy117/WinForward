#requires -Version 5.1
<#
  End-to-end proxifier benchmark orchestrator.

  Runs N passes. Inside a pass, every product row is started, measured with that row's own plan, and
  stopped again. One product is loaded at a time because WinForward and ProxiFyre both drive
  NDISAPI, which serves a single client per adapter.

  The matrix is arranged so that each WinForward row differs from the reference row by at most one
  feature, which is what makes a delta attributable: wf-aot-opt is the reference, wf-fdd-opt changes
  only the build, wf-aot-nativeudp changes only the UDP carriage, wf-aot-dnsrelay changes only the
  DNS path. The three partial rows therefore run only the arms the changed feature can move.

  A product row that supports it also runs a dual phase: the proxied lane and a second application
  that every product is configured to send direct run at the same time, against two different
  targets. A proxy that intercepts the supposedly direct application produces a flow to a port
  nothing should reach through a proxy, which is a correctness failure rather than a slowdown.

  Everything is written under -OutRoot; the orchestrator log is the only thing on stdout.
#>
[CmdletBinding()]
param(
    [int]$Passes = 4,
    [string]$OutRoot = 'C:\wfbench\results',
    [string]$PlanRoot = 'C:\wfbench\e2e',
    [string]$TargetAddress = '192.168.100.4',
    [int]$TcpPort = 40010,
    [int]$UdpPort = 40010,
    [int]$DnsPort = 53,
    # The alternate DNS responder port and the direct lane's whole target. Several products
    # special-case destination port 53, so a DNS comparison that can only use 53 measures each
    # product's special case rather than the proxy.
    [int]$DnsPortAlt = 40053,
    [int]$TcpPortDirect = 40011,
    [int]$DnsPortDirect = 40054,
    [string]$ClientExe = 'C:\wfbench\e2e\WinForward.E2E.exe',
    [string]$ClientExeDirect = 'C:\wfbench\e2e-direct\WinForward.E2E.Direct.exe',
    [string]$SingBoxExe = 'C:\wfbench\singbox\sing-box.exe',
    [string]$SingBoxConfig = 'C:\wfbench\singbox\config.json',
    [string]$SingBoxLog = 'C:\wfbench\singbox\singbox.log',
    [string]$HeartbeatPath = 'C:\wfbench\heartbeat.txt',
    [int]$Seed = 20261006
)

$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
$script:Failures = New-Object System.Collections.ArrayList

function Write-JsonNoBom {
    # Windows PowerShell 5.1's `Set-Content -Encoding UTF8` prefixes a byte-order mark, which every
    # strict JSON reader downstream then rejects. Write the file through .NET with the BOM off.
    param([string]$Path, $Value)
    $text = if ($Value -is [string]) { $Value } else { ($Value | ConvertTo-Json -Depth 6) }
    [System.IO.File]::WriteAllText($Path, $text, (New-Object System.Text.UTF8Encoding($false)))
}

function Touch-Heartbeat {
    # The watchdog treats a stale heartbeat as "operator lost" and tears the campaign down.
    Set-Content -Path $HeartbeatPath -Value ((Get-Date).ToString('o'))
}

function Write-Log {
    param([string]$Message)
    Write-Output ('{0} {1}' -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'), $Message)
}

function Get-ProcessSet {
    param([string]$Name)
    @(Get-Process -Name $Name -ErrorAction SilentlyContinue)
}

function Get-Products {
    $e2e = 'C:\wfbench\e2e'
    $aot = 'C:\wfbench\wf-aot'
    $fdd = 'C:\wfbench\wf-fdd'
    $rows = @(
        [pscustomobject]@{
            Id = 'wf-aot-opt'; ProcessName = 'WinForward'; Kind = 'process'
            Exe = (Join-Path $aot 'WinForward.exe'); Args = @('run', '--config', (Join-Path $aot 'config.json'))
            ConfigSource = (Join-Path $aot 'config.json'); Plan = 'full-plan.json'; Dual = $true
        }
        [pscustomobject]@{
            Id = 'wf-fdd-opt'; ProcessName = 'WinForward'; Kind = 'process'
            Exe = (Join-Path $fdd 'WinForward.exe'); Args = @('run', '--config', (Join-Path $fdd 'config.json'))
            ConfigSource = (Join-Path $fdd 'config.json'); Plan = 'full-plan.json'; Dual = $true
        }
        [pscustomobject]@{
            Id = 'wf-aot-nativeudp'; ProcessName = 'WinForward'; Kind = 'process'
            Exe = (Join-Path $aot 'WinForward.exe'); Args = @('run', '--config', (Join-Path $aot 'config-nativeudp.json'))
            ConfigSource = (Join-Path $aot 'config-nativeudp.json'); Plan = 'udp-plan.json'; Dual = $false
        }
        [pscustomobject]@{
            Id = 'wf-aot-dnsrelay'; ProcessName = 'WinForward'; Kind = 'process'
            Exe = (Join-Path $aot 'WinForward.exe'); Args = @('run', '--config', (Join-Path $aot 'config-dnsrelay.json'))
            ConfigSource = (Join-Path $aot 'config-dnsrelay.json'); Plan = 'dns-plan.json'; Dual = $false
        }
        [pscustomobject]@{
            Id = 'proxifyre'; ProcessName = 'ProxiFyre'; Kind = 'service'; Service = 'ProxiFyreService'
            ConfigSource = 'C:\Program Files\ProxiFyre\app-config.json'; Plan = 'full-plan.json'; Dual = $true
        }
        [pscustomobject]@{
            Id = 'proxifier'; ProcessName = 'Proxifier'; Kind = 'service'; Service = 'Proxifier'
            ConfigSource = 'C:\wfbench\proxifier\bench.ppx'; Plan = 'full-plan.json'; Dual = $true
        }
        [pscustomobject]@{
            Id = 'proxybridge'; ProcessName = 'ProxyBridge_CLI'; Kind = 'process'
            Exe = 'C:\Program Files\ProxyBridge\ProxyBridge_CLI.exe'
            Args = @('--profile', 'C:\wfbench\proxybridge\bench.pbprofile', '--verbose', '0')
            ConfigSource = 'C:\wfbench\proxybridge\bench.pbprofile'; Plan = 'full-plan.json'; Dual = $true
        }
    )
    , $rows
}

function Start-Product {
    param($Product)
    if ($Product.Kind -eq 'service') {
        Start-Service -Name $Product.Service -ErrorAction SilentlyContinue
    }
    else {
        Start-Process -FilePath $Product.Exe -ArgumentList $Product.Args -WindowStyle Hidden `
            -WorkingDirectory (Split-Path $Product.Exe) | Out-Null
    }

    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        Start-Sleep -Milliseconds 500
        if ((Get-ProcessSet $Product.ProcessName).Count -gt 0) { return $true }
    }

    return $false
}

function Stop-Product {
    param($Product)
    foreach ($process in (Get-ProcessSet $Product.ProcessName)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }

    if ($Product.Kind -eq 'service') {
        Stop-Service -Name $Product.Service -Force -ErrorAction SilentlyContinue
    }

    # A driver can outlive its user-mode client and keep filtering, which would contaminate the next
    # row. Give the stack time to unwind, then confirm nothing is left.
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        if ((Get-ProcessSet $Product.ProcessName).Count -eq 0) { break }
        Start-Sleep -Milliseconds 250
    }

    Start-Sleep -Seconds 3
}

function Assert-CleanSlate {
    param($Except)
    $busy = @()
    foreach ($product in @(Get-Products)) {
        # Several rows share one image name, so the exclusion is by process name, not by row id.
        if ($Except -and $product.ProcessName -eq $Except.ProcessName) { continue }
        if ((Get-ProcessSet $product.ProcessName).Count -gt 0) { $busy += $product.ProcessName }
    }

    if ($busy.Count -gt 0) {
        Write-Log ('  WARNING foreign interceptor(s) running: ' + ($busy -join ', '))
    }
}

function Start-SingBox {
    Get-Process -Name 'sing-box' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    Start-Process -FilePath $SingBoxExe -ArgumentList @('run', '-c', $SingBoxConfig) `
        -WorkingDirectory (Split-Path $SingBoxExe) -WindowStyle Hidden | Out-Null
    Start-Sleep -Seconds 4
    return (Get-ProcessSet 'sing-box').Count -gt 0
}

function Clear-SingBoxLog {
    if (Test-Path $SingBoxLog) { Clear-Content -Path $SingBoxLog -ErrorAction SilentlyContinue }
}

function Test-TargetReachable {
    param([int]$Port = $TcpPort)
    try {
        $client = [System.Net.Sockets.TcpClient]::new()
        $client.Connect($TargetAddress, $Port)
        $client.Close()
        return $true
    }
    catch {
        return $false
    }
}

function Get-ProxyTruth {
    # sing-box logs one line per flow that reached it, in three shapes. They are counted separately
    # because the UoT control CONNECT is addressed to the magic name and would otherwise inflate the
    # TCP total. Counting these beside the flows the client supplied is the ground truth for "did
    # this product really relay the traffic", and it is why a product that silently leaks cannot
    # masquerade as a fast one.
    param([int[]]$LeakPorts = @())
    if (-not (Test-Path $SingBoxLog)) {
        return [pscustomobject]@{ tcp = 0; udp = 0; utcp = 0; total = 0; directLeak = 0 }
    }

    $lines = @(Get-Content -Path $SingBoxLog -ErrorAction SilentlyContinue)
    $plain = @($lines | Select-String -SimpleMatch 'inbound connection to')
    $tcp = @($plain | Where-Object { $_.Line -notmatch [regex]::Escape('sp.v2.udp-over-tcp.arpa') }).Count
    $udp = @($lines | Select-String -SimpleMatch 'inbound packet connection to').Count
    $utcp = @($lines | Select-String -SimpleMatch 'inbound UoT connect connection to').Count

    # Any flow aimed at the direct lane's target is a rule-matching defect: that application is
    # configured to bypass the proxy on every product.
    $leak = 0
    if ($LeakPorts.Count -gt 0) {
        $pattern = ':' + (($LeakPorts | ForEach-Object { [string]$_ }) -join ':|:') + '\b'
        $leak = @($lines | Where-Object { $_ -match $pattern }).Count
    }

    return [pscustomobject]@{ tcp = $tcp; udp = $udp; utcp = $utcp; total = ($tcp + $udp + $utcp); directLeak = $leak }
}

function Copy-EffectiveConfig {
    param($Product, [string]$Destination)
    if (Test-Path $Product.ConfigSource) {
        Copy-Item -Path $Product.ConfigSource -Destination $Destination -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-Client {
    param(
        [string]$Label,
        [string]$OutDir,
        [string]$PlanPath,
        [int]$TcpPortValue,
        [int]$DnsPortValue,
        [string]$Exe = $ClientExe,
        [string[]]$SamplerProcess = @()
    )

    if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force -ErrorAction SilentlyContinue }
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

    $clientArgs = @(
        'client',
        '--target', $TargetAddress,
        '--tcp-port', $TcpPortValue,
        '--udp-port', $TcpPortValue,
        '--dns-port', $DnsPortValue,
        '--plan', $PlanPath,
        '--out', $OutDir,
        '--label', $Label
    )
    foreach ($name in $SamplerProcess) { $clientArgs += @('--sampler-process', $name) }

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    # -Wait -PassThru is the only combination that reliably populates ExitCode; a bare WaitForExit()
    # on a -PassThru handle leaves it empty, which would read as a failure.
    $process = Start-Process -FilePath $Exe -ArgumentList $clientArgs -PassThru -Wait -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $OutDir 'client.out') -RedirectStandardError (Join-Path $OutDir 'client.err')
    $stopwatch.Stop()

    $exit = if ($null -eq $process.ExitCode) { 'unknown' } else { $process.ExitCode }
    Write-Log ('  client {0} exit={1} in {2:n0}s' -f $Label, $exit, $stopwatch.Elapsed.TotalSeconds)
    return (Test-ClientRun -OutDir $OutDir -Label $Label)
}

function Test-ClientRun {
    # The harness's own run.json is the authority on whether a run was good: it knows which arms
    # completed, and an exit code cannot express a partially failed plan.
    param([string]$OutDir, [string]$Label)

    $runPath = Join-Path $OutDir 'run.json'
    if (-not (Test-Path $runPath)) {
        [void]$script:Failures.Add(('client {0} wrote no run.json' -f $Label))
        return $false
    }

    $run = Get-Content $runPath -Raw | ConvertFrom-Json
    $failedArms = @($run.arms | Where-Object { $_.failed } | ForEach-Object { $_.name })
    if ($run.failed -or $failedArms.Count -gt 0) {
        [void]$script:Failures.Add(('client {0} failed: run={1} arms={2}' -f $Label, $run.failed, ($failedArms -join ',')))
        return $false
    }

    return $true
}

function Invoke-DualPhase {
    # The proxied lane and a second application that every product is configured to send direct run
    # at the same time with the same workload shape against two different targets, so the only
    # difference between the lanes is the path. The direct lane is a different image name, which is
    # how all of these products are told to send an application around the proxy; a product that
    # intercepts it anyway produces a flow to a target nothing should reach through a proxy, and
    # that is a correctness failure rather than a slowdown.
    param($Product, [string]$RowDir)

    # Per row, never per pass: a pass-level directory is rebuilt by every dual row in turn, so only
    # the last row's lanes survive and the other rows' directLeak evidence is destroyed rather than
    # reported as missing.
    $dir = Join-Path $RowDir 'dual'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $plan = Join-Path $PlanRoot 'dual-plan.json'

    Clear-SingBoxLog
    Touch-Heartbeat

    $proxiedDir = Join-Path $dir 'proxied'
    $directDir = Join-Path $dir 'direct'
    foreach ($target in @($proxiedDir, $directDir)) {
        if (Test-Path $target) { Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue }
        New-Item -ItemType Directory -Force -Path $target | Out-Null
    }

    $proxiedArgs = @('client', '--target', $TargetAddress, '--tcp-port', $TcpPort, '--udp-port', $UdpPort,
        '--dns-port', $DnsPort, '--plan', $plan, '--out', $proxiedDir, '--label', ($Product.Id + '-dual-proxied'),
        '--sampler-process', $Product.ProcessName)
    $directArgs = @('client', '--target', $TargetAddress, '--tcp-port', $TcpPortDirect, '--udp-port', $TcpPortDirect,
        '--dns-port', $DnsPortDirect, '--plan', $plan, '--out', $directDir, '--label', ($Product.Id + '-dual-direct'))

    $proxied = Start-Process -FilePath $ClientExe -ArgumentList $proxiedArgs -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $proxiedDir 'client.out') -RedirectStandardError (Join-Path $proxiedDir 'client.err')
    $direct = Start-Process -FilePath $ClientExeDirect -ArgumentList $directArgs -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $directDir 'client.out') -RedirectStandardError (Join-Path $directDir 'client.err')

    $proxied.WaitForExit()
    $direct.WaitForExit()
    Write-Log ('  dual: proxied exit={0} direct exit={1}' -f $proxied.ExitCode, $direct.ExitCode)

    $truth = Get-ProxyTruth -LeakPorts @($TcpPortDirect, $DnsPortDirect)
    Write-JsonNoBom -Path (Join-Path $dir 'proxy-truth.json') -Value $truth
    Write-Log ('  dual proxy truth: tcp={0} udp={1} uot={2} directLeak={3}' -f $truth.tcp, $truth.udp, $truth.utcp, $truth.directLeak)
    if ($truth.directLeak -gt 0) {
        [void]$script:Failures.Add(('{0}: {1} proxied flow(s) to the direct lane''s target' -f $Product.Id, $truth.directLeak))
    }

    [void](Test-ClientRun -OutDir $proxiedDir -Label ($Product.Id + '-dual-proxied'))
    [void](Test-ClientRun -OutDir $directDir -Label ($Product.Id + '-dual-direct'))
}

function Invoke-Row {
    param($Product, [string]$PassDir, [int]$Pass)

    $productDir = Join-Path $PassDir $Product.Id
    New-Item -ItemType Directory -Force -Path $productDir | Out-Null

    Write-Log ('  --- {0} (plan {1}) ---' -f $Product.Id, $Product.Plan)
    Assert-CleanSlate -Except $Product

    if (-not (Start-Product -Product $Product)) {
        Write-Log ('  {0} did not start' -f $Product.Id)
        [void]$script:Failures.Add(('{0} did not start' -f $Product.Id))
        return
    }

    Start-Sleep -Seconds 5
    Clear-SingBoxLog
    Touch-Heartbeat

    [void](Invoke-Client -Label ($Product.Id + '-p' + $Pass) -OutDir $productDir `
        -PlanPath (Join-Path $PlanRoot $Product.Plan) -TcpPortValue $TcpPort -DnsPortValue $DnsPort `
        -SamplerProcess @($Product.ProcessName))

    $truth = Get-ProxyTruth
    Write-JsonNoBom -Path (Join-Path $productDir 'proxy-truth.json') -Value $truth
    Write-Log ('  proxy truth: tcp={0} udp={1} uot={2}' -f $truth.tcp, $truth.udp, $truth.utcp)
    Copy-EffectiveConfig -Product $Product -Destination (Join-Path $productDir (Split-Path $Product.ConfigSource -Leaf))
    if (Test-Path $SingBoxLog) { Copy-Item $SingBoxLog (Join-Path $productDir 'singbox.log') -Force -ErrorAction SilentlyContinue }

    if ($Product.Dual) { Invoke-DualPhase -Product $Product -RowDir $productDir }

    Stop-Product -Product $Product
    Touch-Heartbeat
}

function Invoke-ControlBlock {
    param([string]$PassDir, [int]$Pass, [string]$Tag)
    # The control block is the floor every row is compared against, and running it again after the
    # products is the only thing in the campaign that can detect a product which left a driver
    # filtering after it exited.
    Write-Log ('  --- control {0} (no product) ---' -f $Tag)
    Assert-CleanSlate -Except $null
    $dir = Join-Path $PassDir ('control-' + $Tag)
    [void](Invoke-Client -Label ('control-' + $Tag + '-p' + $Pass) -OutDir $dir `
        -PlanPath (Join-Path $PlanRoot 'base-plan.json') -TcpPortValue $TcpPort -DnsPortValue $DnsPort)
    Touch-Heartbeat
}

# ---------------------------------------------------------------------------------------------
# Campaign
# ---------------------------------------------------------------------------------------------

$products = Get-Products

Write-Log '=== preflight ==='
Get-NetFirewallProfile | ForEach-Object { Set-NetFirewallProfile -Name $_.Name -Enabled False }
# The firewall is off for every row. That is a uniform environment change rather than a per-product
# requirement, and a reader of the results has to be able to see it.
$firewall = @(Get-NetFirewallProfile | ForEach-Object { @{ name = $_.Name; enabled = [bool]$_.Enabled } })
Write-Log ('  firewall: ' + (($firewall | ForEach-Object { $_.name + '=' + $_.enabled }) -join ' '))

if (-not (Test-TargetReachable)) {
    Write-Log ('  target unreachable at {0}:{1} - aborting' -f $TargetAddress, $TcpPort)
    exit 1
}
Write-Log ('  target reachable at {0}:{1}' -f $TargetAddress, $TcpPort)
if (-not (Test-TargetReachable -Port $TcpPortDirect)) {
    Write-Log ('  direct-lane target unreachable at {0}:{1} - the dual phase will fail' -f $TargetAddress, $TcpPortDirect)
}

if (-not (Start-SingBox)) {
    Write-Log '  sing-box did not start - aborting'
    exit 1
}

$os = Get-CimInstance Win32_OperatingSystem
$environment = [ordered]@{
    started = (Get-Date).ToString('o')
    os = $os.Caption
    build = $os.BuildNumber
    logicalCpus = [int]$env:NUMBER_OF_PROCESSORS
    totalRamMb = [int]($os.TotalVisibleMemorySize / 1KB)
    freeRamMb = [int]($os.FreePhysicalMemory / 1KB)
    passes = $Passes
    seed = $Seed
    firewallProfiles = $firewall
    singBox = (& $SingBoxExe version 2>&1 | Select-Object -First 1)
    target = [ordered]@{
        host = $TargetAddress
        tcpPort = $TcpPort; udpPort = $UdpPort; dnsPort = $DnsPort; dnsPortAlt = $DnsPortAlt
        tcpPortDirect = $TcpPortDirect; dnsPortDirect = $DnsPortDirect
    }
}
New-Item -ItemType Directory -Force -Path $OutRoot | Out-Null
Write-JsonNoBom -Path (Join-Path $OutRoot 'environment.json') -Value $environment
Write-Log ('  env: {0} build {1}, {2} cpus, {3} MB visible' -f $os.Caption, $os.BuildNumber, $environment.logicalCpus, $environment.totalRamMb)

$random = [System.Random]::new($Seed)

for ($pass = 1; $pass -le $Passes; $pass++) {
    $passDir = Join-Path $OutRoot ('pass{0}' -f $pass)
    New-Item -ItemType Directory -Force -Path $passDir | Out-Null
    Write-Log ('=== pass {0}/{1} ===' -f $pass, $Passes)

    # Product order is randomised per pass so a drift in the machine shows up as scatter rather than
    # as a difference between products, which would be indistinguishable from a real effect.
    $order = @($products | Sort-Object { $random.Next() })
    Write-JsonNoBom -Path (Join-Path $passDir 'order.txt') -Value (($order | ForEach-Object { $_.Id }) -join ',')

    Invoke-ControlBlock -PassDir $passDir -Pass $pass -Tag 'pre'

    foreach ($product in $order) {
        Invoke-Row -Product $product -PassDir $passDir -Pass $pass
    }

    Invoke-ControlBlock -PassDir $passDir -Pass $pass -Tag 'post'

    Write-Log ('  pass {0} done' -f $pass)
}

Write-Log '=== teardown ==='
foreach ($product in $products) { Stop-Product -Product $product }
Get-Process -Name 'sing-box' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

if ($script:Failures.Count -gt 0) {
    Write-Log ('=== FAILURES ({0}) ===' -f $script:Failures.Count)
    foreach ($failure in $script:Failures) { Write-Log ('  ' + $failure) }
}
else {
    Write-Log '=== no failures ==='
}

Write-Log '=== orchestrator complete ==='
