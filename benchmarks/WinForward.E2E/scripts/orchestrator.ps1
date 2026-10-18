#requires -Version 5.1
<#
  End-to-end proxifier benchmark orchestrator.

  Runs N passes. Inside a pass, every product row is started, measured with that row's own plan, and
  stopped again. One product is loaded at a time because WinForward and ProxiFyre both drive
  NDISAPI, which serves a single client per adapter.

  The matrix is arranged so that each WinForward row changes at most one feature from the row it
  is paired with, which is what makes a delta attributable: wf-aot-nativeudp, the default
  configuration, is the reference row and runs the full plan; wf-aot-opt is the UoT variant of
  that reference; wf-fdd-opt changes only the build relative to wf-aot-opt; wf-aot-dnsrelay
  changes only the DNS path relative to wf-aot-opt. The partial row therefore runs only the arms
  the changed feature can move.

  A product row that supports it also runs a dual phase: the proxied lane and a second application
  that every product is configured to send direct run at the same time, against two different
  targets. A proxy that intercepts the supposedly direct application produces a flow to a port
  nothing should reach through a proxy, which is a correctness failure rather than a slowdown.

  Everything is written under -OutRoot; the orchestrator log is the only thing on stdout.

  Before the first pass the orchestrator reads the machine's environment and runs a clock gate against
  the Linux host's probe (scripts/clock-probe.py). The default measure-and-correct policy records a
  stable offset and samples every pass pre/post in clock.jsonl; it aborts on a probe failure, an offset
  beyond 5 s, or more than 100 ms of drift across a pass. -RequireClockSync restores the 200 ms bound.
  The environment block is written before the firewall change or any benchmark process start, and every change is
  recorded beside the state it replaced. -PreflightOnly stops after that block.
#>
[CmdletBinding()]
param(
    [int]$Passes = 4,
    [string]$OutRoot = 'C:\wfbench\results',
    [string]$PlanRoot = 'C:\wfbench\e2e',
    [string]$TargetAddress = '192.168.100.4',
    # The Linux host's probe; the default matches scripts/clock-probe.py.
    [int]$ClockProbePort = 40099,
    # Read the environment and run the clock gate, then exit before the firewall change and the loop.
    [switch]$PreflightOnly,
    # Demand a synchronized VM clock: the preflight and every pass phase must stay within 200 ms.
    [switch]$RequireClockSync,
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
    $text = if ($Value -is [string]) { $Value } else { ($Value | ConvertTo-Json -Depth 10) }
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
            ConfigSource = (Join-Path $aot 'config-nativeudp.json'); Plan = 'full-plan.json'; Dual = $true
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
    # This function publishes no return value: a log line and a value on the same stream would let a
    # caller that captures or discards the value take the exit-code line with it. The verdict is
    # recorded in $script:Failures by Test-ClientRun, so callers need nothing back.
    [void](Test-ClientRun -OutDir $OutDir -Label $Label)
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

    # Test-ClientRun returns a verdict but never logs one, so discarding it with [void] cannot swallow
    # a log line here; the verdict itself is already recorded in $script:Failures.
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

    Invoke-Client -Label ($Product.Id + '-p' + $Pass) -OutDir $productDir `
        -PlanPath (Join-Path $PlanRoot $Product.Plan) -TcpPortValue $TcpPort -DnsPortValue $DnsPort `
        -SamplerProcess @($Product.ProcessName)

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
    Invoke-Client -Label ('control-' + $Tag + '-p' + $Pass) -OutDir $dir `
        -PlanPath (Join-Path $PlanRoot 'base-plan.json') -TcpPortValue $TcpPort -DnsPortValue $DnsPort
    Touch-Heartbeat
}

# ---------------------------------------------------------------------------------------------
# Preflight: clock gate and environment evidence
# ---------------------------------------------------------------------------------------------

function Get-Median {
    param([double[]]$Values)
    if ($null -eq $Values -or $Values.Count -eq 0) { return $null }
    $sorted = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($sorted.Count / 2)
    if (($sorted.Count % 2) -eq 1) { return [double]$sorted[$middle] }
    return (([double]$sorted[$middle - 1] + [double]$sorted[$middle]) / 2.0)
}

function Invoke-ClockProbe {
    # One TCP connection per sample. The offset is the VM send/receive midpoint against the host's
    # receive instant, so the estimate does not need the clocks to agree before the probe. The round
    # trip is measured with a stopwatch: a wall clock that steps during a sample would corrupt the
    # midpoint, while the offset itself is what the probe measures.
    param([int]$Count = 5)

    $samples = New-Object System.Collections.ArrayList
    for ($seq = 1; $seq -le $Count; $seq++) {
        $client = [System.Net.Sockets.TcpClient]::new()
        try {
            $client.ReceiveTimeout = 5000
            $client.SendTimeout = 5000
            $client.Connect($TargetAddress, $ClockProbePort)
            $stream = $client.GetStream()
            $vmT0 = (Get-Date).ToUniversalTime()
            $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
            $payload = [System.Text.Encoding]::ASCII.GetBytes('vmT0=' + $vmT0.ToString('o') + "`n")
            $stream.Write($payload, 0, $payload.Length)
            $stream.Flush()
            $replyBytes = New-Object System.IO.MemoryStream
            $chunk = New-Object byte[] 128
            while ($replyBytes.Length -lt 4096) {
                $read = $stream.Read($chunk, 0, $chunk.Length)
                if ($read -le 0) { break }
                $replyBytes.Write($chunk, 0, $read)
                $replyText = [System.Text.Encoding]::ASCII.GetString($replyBytes.ToArray())
                if ($replyText.IndexOf([char]10) -ge 0) { break }
            }
            $stopwatch.Stop()
            $vmT1 = (Get-Date).ToUniversalTime()
            if ($replyBytes.Length -eq 0) { throw 'the host closed the connection without a reply' }
            $reply = [System.Text.Encoding]::ASCII.GetString($replyBytes.ToArray()).Trim()
            $hostRecv = [DateTimeOffset]::Parse(
                $reply,
                [System.Globalization.CultureInfo]::InvariantCulture,
                [System.Globalization.DateTimeStyles]::AssumeUniversal)
            $rttMs = $stopwatch.Elapsed.TotalMilliseconds
            $offsetMs = ($vmT0.AddMilliseconds($rttMs / 2.0) - $hostRecv.UtcDateTime).TotalMilliseconds
            [void]$samples.Add([ordered]@{
                    seq = $seq
                    vmT0Utc = $vmT0.ToString('o')
                    vmT1Utc = $vmT1.ToString('o')
                    hostRecvUtc = $hostRecv.UtcDateTime.ToString('o')
                    vmRttMs = [Math]::Round($rttMs, 3)
                    offsetMs = [Math]::Round($offsetMs, 3)
                })
        }
        catch {
            throw ('probe {0} could not complete: {1}' -f $seq, $_.Exception.Message)
        }
        finally {
            $client.Close()
        }

        Start-Sleep -Milliseconds 150
    }

    return $samples.ToArray()
}

function Invoke-ClockGate {
    # Measure-and-correct is the default: the per-pass samples in clock.jsonl let the analysis correct
    # each pass, so only a wrong-clock sanity bound or an explicit sync requirement aborts. The old
    # 200 ms bound is kept behind -RequireClockSync for a campaign that demands a clean VM clock.
    param(
        [int]$Count = 5,
        [double]$SanityLimitMs = 5000.0,
        [double]$SyncLimitMs = 200.0,
        [switch]$RequireSync
    )

    $policy = if ($RequireSync) { 'require-clock-sync' } else { 'measure-and-correct' }
    $gate = [ordered]@{
        policy = $policy
        passed = $false
        sanityLimitMs = $SanityLimitMs
        syncLimitMs = $SyncLimitMs
        requireSync = [bool]$RequireSync
        medianOffsetMs = $null
        samples = @()
        warning = $null
        reason = $null
    }

    try {
        $gate.samples = @(Invoke-ClockProbe -Count $Count)
        if ($gate.samples.Count -eq 0) { throw 'the clock probe returned no samples' }
        $offsets = [double[]]@($gate.samples | ForEach-Object { [double]$_.offsetMs })
        $median = [double](Get-Median -Values $offsets)
        $gate.medianOffsetMs = [Math]::Round($median, 3)
        $magnitude = [Math]::Abs($median)
        if ($magnitude -gt $SanityLimitMs) {
            $gate.reason = ('median offset {0:+0.0;-0.0;0} ms exceeds the {1:F0} ms sanity bound' -f $median, $SanityLimitMs)
        }
        elseif ($RequireSync -and $magnitude -gt $SyncLimitMs) {
            $gate.reason = ('median offset {0:+0.0;-0.0;0} ms exceeds the {1:F0} ms require-clock-sync bound' -f $median, $SyncLimitMs)
        }
        else {
            $gate.passed = $true
            if ($magnitude -gt $SyncLimitMs) {
                $gate.warning = ('median offset {0:+0.0;-0.0;0} ms (VM - host) is stable but outside the {1:F0} ms sync bound; proceeding under measure-and-correct (per-pass samples are in clock.jsonl)' -f $median, $SyncLimitMs)
            }
        }
    }
    catch {
        $gate.reason = ('the clock probe could not run: {0}' -f $_.Exception.Message)
    }

    return $gate
}

function Add-ClockLogEntry {
    # One line per probe sample, with the campaign's fixed sign convention: offsetMs = vm - host.
    param([string]$Path, [int]$Pass, [string]$Phase, $Sample)

    $record = [ordered]@{
        pass = $Pass
        phase = $Phase
        offsetMs = [double]$Sample.offsetMs
        rttMs = [double]$Sample.vmRttMs
        vmUtc = [string]$Sample.vmT0Utc
        hostUtc = [string]$Sample.hostRecvUtc
    }
    $line = ($record | ConvertTo-Json -Compress -Depth 4)
    [System.IO.File]::AppendAllText($Path, $line + "`n", (New-Object System.Text.UTF8Encoding($false)))
}

function Measure-ClockPhase {
    param([string]$Path, [int]$Pass, [string]$Phase, [int]$Count = 5)

    $samples = @(Invoke-ClockProbe -Count $Count)
    if ($samples.Count -eq 0) { throw 'the clock probe returned no samples' }
    foreach ($sample in $samples) {
        Add-ClockLogEntry -Path $Path -Pass $Pass -Phase $Phase -Sample $sample
    }
    $offsets = [double[]]@($samples | ForEach-Object { [double]$_.offsetMs })
    return [ordered]@{
        pass = $Pass
        phase = $Phase
        label = ('pass {0} {1}' -f $Pass, $Phase)
        medianOffsetMs = [Math]::Round([double](Get-Median -Values $offsets), 3)
        samples = $samples
    }
}

function Test-ClockPhaseBounds {
    param(
        $PhaseResult,
        [switch]$RequireSync,
        [double]$SanityLimitMs = 5000.0,
        [double]$SyncLimitMs = 200.0
    )

    $result = [ordered]@{ passed = $true; reason = $null; warning = $null }
    if ($null -eq $PhaseResult) {
        $result.passed = $false
        $result.reason = 'no clock phase result was measured'
        return $result
    }
    $median = [double]$PhaseResult.medianOffsetMs
    $magnitude = [Math]::Abs($median)
    if ($magnitude -gt $SanityLimitMs) {
        $result.passed = $false
        $result.reason = ('{0} median offset {1:+0.0;-0.0;0} ms exceeds the {2:F0} ms sanity bound' -f $PhaseResult.label, $median, $SanityLimitMs)
    }
    elseif ($RequireSync -and $magnitude -gt $SyncLimitMs) {
        $result.passed = $false
        $result.reason = ('{0} median offset {1:+0.0;-0.0;0} ms exceeds the {2:F0} ms require-clock-sync bound' -f $PhaseResult.label, $median, $SyncLimitMs)
    }
    elseif ($magnitude -gt $SyncLimitMs) {
        $result.warning = ('{0} median offset {1:+0.0;-0.0;0} ms is stable but outside the {2:F0} ms sync bound; corrected per pass' -f $PhaseResult.label, $median, $SyncLimitMs)
    }
    return $result
}


function Get-FirewallProfileState {
    # Read-only. The environment block records the state as found; the campaign's own disable goes
    # into environmentChanges, so the post-change state can never pass for the machine's environment.
    try {
        $profiles = @(Get-NetFirewallProfile -ErrorAction Stop | ForEach-Object {
                [ordered]@{ name = [string]$_.Name; enabled = [bool]$_.Enabled }
            })
        if ($profiles.Count -eq 0) { throw 'Get-NetFirewallProfile returned no profiles' }
        return [ordered]@{ value = $profiles; reason = $null }
    }
    catch {
        return [ordered]@{ value = $null; reason = $_.Exception.Message }
    }
}

function Disable-FirewallProfiles {
    param($Before)
    $entry = [ordered]@{
        what = 'Set-NetFirewallProfile -Enabled False for every profile'
        before = $Before
        after = $null
        when = (Get-Date).ToString('o')
    }
    try {
        foreach ($profile in @(Get-NetFirewallProfile -ErrorAction Stop)) {
            Set-NetFirewallProfile -Name $profile.Name -Enabled False -ErrorAction Stop
        }
        $entry.after = @(Get-NetFirewallProfile -ErrorAction Stop | ForEach-Object {
                [ordered]@{ name = [string]$_.Name; enabled = [bool]$_.Enabled }
            })
    }
    catch {
        $entry.error = $_.Exception.Message
    }
    return $entry
}

function Restore-FirewallProfiles {
    # Restoring is a second environment change: the entry records the disabled state as "before" and
    # the as-found state as "after", alongside the disable entry that made the change.
    param($Before, $After)

    if ($null -eq $Before -or @($Before).Count -eq 0) { return $null }
    $unchanged = $true
    if ($null -eq $After) {
        $unchanged = $false
    }
    else {
        foreach ($target in @($Before)) {
            $current = @($After | Where-Object { $_.name -eq $target.name }) | Select-Object -First 1
            if ($null -eq $current -or ([bool]$current.enabled) -ne ([bool]$target.enabled)) {
                $unchanged = $false
                break
            }
        }
    }
    if ($unchanged) { return $null }

    $entry = [ordered]@{
        what = 'restore firewall profiles to the environment state'
        before = $After
        after = $null
        when = (Get-Date).ToString('o')
    }
    try {
        foreach ($target in @($Before)) {
            $enabled = if ([bool]$target.enabled) { 'True' } else { 'False' }
            Set-NetFirewallProfile -Name $target.name -Enabled $enabled -ErrorAction Stop
        }
        $entry.after = @(Get-NetFirewallProfile -ErrorAction Stop | ForEach-Object {
                [ordered]@{ name = [string]$_.Name; enabled = [bool]$_.Enabled }
            })
    }
    catch {
        $entry.error = $_.Exception.Message
    }
    return $entry
}

function Get-OperatingSystemInfo {
    $result = [ordered]@{
        caption = $null
        build = $null
        totalVisibleMb = $null
        freeMb = $null
        reason = $null
    }
    try {
        $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
        $result.caption = [string]$os.Caption
        $result.build = [string]$os.BuildNumber
        $result.totalVisibleMb = [int]($os.TotalVisibleMemorySize / 1KB)
        $result.freeMb = [int]($os.FreePhysicalMemory / 1KB)
    }
    catch {
        $result.reason = $_.Exception.Message
    }
    return $result
}

function Get-SingBoxVersion {
    $result = [ordered]@{ value = $null; reason = $null }
    try {
        if (-not (Test-Path -LiteralPath $SingBoxExe)) { throw ('not found: ' + $SingBoxExe) }
        $output = @(& $SingBoxExe version 2>&1)
        if ($LASTEXITCODE -ne 0) { throw ('the version command exited ' + $LASTEXITCODE) }
        if ($output.Count -eq 0) { throw 'the version command returned no output' }
        $result.value = [string]$output[0]
    }
    catch {
        $result.reason = $_.Exception.Message
    }
    return $result
}

function Get-NicOffload {
    $result = [ordered]@{
        adapters = @()
        source = 'Get-NetAdapter / Get-NetAdapterAdvancedProperty'
        reason = $null
    }
    $keywordPattern = 'Rsc|Lso|Checksum|InterruptModeration'
    $displayPattern = 'receive segment coalescing|large send offload|checksum offload|interrupt moderation|receive side scaling'
    try {
        $adapters = @(Get-NetAdapter -ErrorAction Stop)
        if ($adapters.Count -eq 0) { throw 'Get-NetAdapter returned no adapters' }
        foreach ($adapter in $adapters) {
            $entry = [ordered]@{
                name = [string]$adapter.Name
                interfaceDescription = [string]$adapter.InterfaceDescription
                status = [string]$adapter.Status
                linkSpeed = [string]$adapter.LinkSpeed
                macAddress = [string]$adapter.MacAddress
                advancedProperties = @()
                reason = $null
            }
            try {
                # The full advanced-property table runs to dozens of rows; only the offload and
                # interrupt-moderation controls can move a latency or CPU reading.
                $entry.advancedProperties = @(Get-NetAdapterAdvancedProperty -Name $adapter.Name -ErrorAction Stop |
                        Where-Object { ($_.RegistryKeyword -match $keywordPattern) -or ($_.DisplayName -match $displayPattern) } |
                        ForEach-Object {
                            [ordered]@{
                                displayName = [string]$_.DisplayName
                                registryKeyword = [string]$_.RegistryKeyword
                                displayValue = [string]$_.DisplayValue
                                registryValue = $_.RegistryValue
                            }
                        })
            }
            catch {
                $entry.reason = $_.Exception.Message
            }
            $entry.hasRsc = @($entry.advancedProperties | Where-Object { $_.registryKeyword -match 'Rsc' }).Count -gt 0
            $entry.hasLso = @($entry.advancedProperties | Where-Object { $_.registryKeyword -match 'Lso' }).Count -gt 0
            $entry.hasChecksum = @($entry.advancedProperties | Where-Object { $_.registryKeyword -match 'Checksum' }).Count -gt 0
            $entry.hasInterruptModeration = @($entry.advancedProperties | Where-Object { $_.registryKeyword -match 'InterruptModeration' }).Count -gt 0
            $result.adapters += $entry
        }
    }
    catch {
        $result.adapters = $null
        $result.reason = $_.Exception.Message
    }
    return $result
}

function Get-DynamicMemory {
    $result = [ordered]@{
        totalPhysicalMb = $null
        totalVisibleMb = $null
        freeMb = $null
        computerSystem = $null
        operatingSystem = $null
        setting = $null
        sources = @()
        reason = $null
    }
    $reasons = New-Object System.Collections.ArrayList

    try {
        $cs = Get-CimInstance Win32_ComputerSystem -ErrorAction Stop
        $result.computerSystem = [ordered]@{
            manufacturer = [string]$cs.Manufacturer
            model = [string]$cs.Model
            numberOfLogicalProcessors = [int]$cs.NumberOfLogicalProcessors
            totalPhysicalMemoryBytes = [int64]$cs.TotalPhysicalMemory
        }
        $result.totalPhysicalMb = [int]($cs.TotalPhysicalMemory / 1MB)
        $result.sources += 'Win32_ComputerSystem'
    }
    catch {
        [void]$reasons.Add(('Win32_ComputerSystem: ' + $_.Exception.Message))
    }

    try {
        $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
        $result.operatingSystem = [ordered]@{
            caption = [string]$os.Caption
            version = [string]$os.Version
            buildNumber = [string]$os.BuildNumber
            totalVisibleMemoryKb = [int64]$os.TotalVisibleMemorySize
            freePhysicalMemoryKb = [int64]$os.FreePhysicalMemory
        }
        $result.totalVisibleMb = [int]($os.TotalVisibleMemorySize / 1KB)
        $result.freeMb = [int]($os.FreePhysicalMemory / 1KB)
        $result.sources += 'Win32_OperatingSystem'
    }
    catch {
        [void]$reasons.Add(('Win32_OperatingSystem: ' + $_.Exception.Message))
    }

    # Dynamic memory is configured on the Hyper-V host. The guest sees the current assignment in
    # Win32_ComputerSystem; the setting itself is only readable when the virtualization namespace is
    # present, and otherwise the reason says so instead of guessing from the RAM figures.
    try {
        $vm = @(Get-CimInstance -Namespace 'root\virtualization\v2' -ClassName Msvm_VirtualSystemSettingData -ErrorAction Stop |
                Where-Object { $_.ElementName -eq $env:COMPUTERNAME } |
                Select-Object -First 1)
        if ($vm.Count -eq 0) { throw 'no Msvm_VirtualSystemSettingData matches this computer name' }
        $result.setting = [ordered]@{
            dynamicMemoryEnabled = $vm[0].DynamicMemoryEnabled
            startupMemoryBytes = $vm[0].StartupMemory
            minimumMemoryBytes = $vm[0].MinimumMemory
            maximumMemoryBytes = $vm[0].MaximumMemory
        }
        $result.sources += 'Msvm_VirtualSystemSettingData'
    }
    catch {
        [void]$reasons.Add(('dynamic memory setting: ' + $_.Exception.Message))
    }

    if ($reasons.Count -gt 0) { $result.reason = ($reasons -join '; ') }
    return $result
}

function Get-ServiceBinaryPath {
    param([string]$Name)
    try {
        $service = Get-CimInstance Win32_Service -Filter ("Name='{0}'" -f $Name) -ErrorAction Stop
        if ($null -eq $service -or @($service).Count -eq 0) {
            throw ('service {0} does not exist' -f $Name)
        }
        if ([string]::IsNullOrWhiteSpace([string]$service.PathName)) {
            throw ('service {0} has an empty PathName' -f $Name)
        }
        $path = [string]$service.PathName
        if ($path -match '^\s*"([^"]+)"') { $path = $matches[1] }
        elseif ($path -match '^\s*(\S+?\.exe)\b') { $path = $matches[1] }
        return [ordered]@{ path = [System.Environment]::ExpandEnvironmentVariables($path); reason = $null }
    }
    catch {
        return [ordered]@{ path = $null; reason = $_.Exception.Message }
    }
}

function New-FileEvidenceEntry {
    param([string]$Group, [string]$Path)
    $entry = [ordered]@{
        group = $Group
        path = $(if ([string]::IsNullOrWhiteSpace($Path)) { $null } else { $Path })
        sizeBytes = $null
        fileVersion = $null
        sha256 = $null
        reason = $null
    }
    if ([string]::IsNullOrWhiteSpace($Path)) {
        $entry.reason = 'the path is not declared'
        return $entry
    }
    try {
        $item = Get-Item -LiteralPath $Path -ErrorAction Stop
        if ($item.PSIsContainer) { throw 'the path is a directory' }
        $entry.sizeBytes = [int64]$item.Length
        $version = [string]$item.VersionInfo.FileVersion
        if (-not [string]::IsNullOrWhiteSpace($version)) { $entry.fileVersion = $version }
        $entry.sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256 -ErrorAction Stop).Hash
    }
    catch {
        $entry.reason = $_.Exception.Message
    }
    return $entry
}

function Join-WindowsPath {
    param([string]$Base, [string]$Leaf)
    if ($Base.EndsWith('\') -or $Base.EndsWith('/')) { return $Base + $Leaf }
    return $Base + '\' + $Leaf
}


function Get-ProductVersions {
    param($Products)
    $descriptors = New-Object System.Collections.ArrayList

    foreach ($root in @('C:\wfbench\wf-aot', 'C:\wfbench\wf-fdd')) {
        $group = ($root -split '[\\/]')[-1]
        foreach ($name in @('WinForward.exe', 'ndisapi.dll')) {
            [void]$descriptors.Add([ordered]@{ group = $group; path = (Join-WindowsPath -Base $root -Leaf $name) })
        }
        foreach ($item in @(Get-ChildItem -LiteralPath $root -Filter 'WinForward*.dll' -ErrorAction SilentlyContinue | Where-Object { -not $_.PSIsContainer })) {
            [void]$descriptors.Add([ordered]@{ group = $group; path = $item.FullName })
        }
    }

    [void]$descriptors.Add([ordered]@{ group = 'sing-box'; path = $SingBoxExe })

    foreach ($product in $Products) {
        if ($product.Kind -eq 'service') {
            $binary = Get-ServiceBinaryPath -Name $product.Service
            [void]$descriptors.Add([ordered]@{ group = $product.Id; path = $binary.path; reason = $binary.reason })
        }
        elseif (-not [string]::IsNullOrWhiteSpace($product.Exe)) {
            [void]$descriptors.Add([ordered]@{ group = $product.Id; path = $product.Exe })
        }
    }

    $entries = @()
    $seen = @{}
    foreach ($descriptor in $descriptors) {
        $key = [string]$descriptor.path
        if ([string]::IsNullOrWhiteSpace($key)) {
            $entry = New-FileEvidenceEntry -Group $descriptor.group -Path $null
            if ($descriptor.Contains('reason') -and $descriptor.reason) { $entry.reason = $descriptor.reason }
            $entries += $entry
            continue
        }
        $lower = $key.ToLowerInvariant()
        if ($seen.ContainsKey($lower)) { continue }
        $seen[$lower] = $true
        $entries += New-FileEvidenceEntry -Group $descriptor.group -Path $key
    }

    return $entries
}

function Get-PowerPlan {
    $result = [ordered]@{ output = $null; source = 'powercfg /getactivescheme'; reason = $null }
    try {
        $command = Get-Command powercfg -ErrorAction SilentlyContinue
        if ($null -eq $command) { throw 'powercfg is not available' }
        $output = @(powercfg /getactivescheme 2>&1)
        if ($LASTEXITCODE -ne 0) { throw ('powercfg exited ' + $LASTEXITCODE) }
        $result.output = (($output | ForEach-Object { [string]$_ }) -join "`n").Trim()
        if ([string]::IsNullOrWhiteSpace($result.output)) { throw 'powercfg returned no output' }
    }
    catch {
        $result.output = $null
        $result.reason = $_.Exception.Message
    }
    return $result
}

function Get-DriverVersions {
    $result = [ordered]@{
        driverquery = @()
        drivers = @()
        serviceRows = @()
        reason = $null
    }
    $reasons = New-Object System.Collections.ArrayList
    $wanted = 'ndisrd|ProxifierDrv|WinDivert'

    try {
        $command = Get-Command driverquery -ErrorAction SilentlyContinue
        if ($null -eq $command) { throw 'driverquery is not available' }
        $lines = @(driverquery /v /fo csv 2>&1)
        if ($LASTEXITCODE -ne 0) { throw ('driverquery exited ' + $LASTEXITCODE) }
        $header = @($lines | Select-Object -First 1)
        $matched = @($lines | Where-Object { $_ -match $wanted })
        $result.driverquery = @($header + $matched)
        if ($matched.Count -eq 0) { [void]$reasons.Add('driverquery returned no row for ' + $wanted) }
    }
    catch {
        [void]$reasons.Add('driverquery: ' + $_.Exception.Message)
    }

    foreach ($name in @('ndisrd', 'ProxifierDrv', 'WinDivert')) {
        try {
            $driver = Get-CimInstance Win32_SystemDriver -Filter ("Name='{0}'" -f $name) -ErrorAction Stop
            if ($null -eq $driver) { throw 'not present' }
            $entry = [ordered]@{
                name = [string]$driver.Name
                displayName = [string]$driver.DisplayName
                state = [string]$driver.State
                startMode = [string]$driver.StartMode
                pathName = [string]$driver.PathName
                fileVersion = $null
                reason = $null
            }
            if (-not [string]::IsNullOrWhiteSpace($entry.pathName)) {
                $path = $entry.pathName -replace '^\\\\\?\?\\', ''
                $path = $path -replace '^\\SystemRoot\\', ($env:SystemRoot + '\')
                $path = [System.Environment]::ExpandEnvironmentVariables($path)
                if (Test-Path -LiteralPath $path) {
                    $version = [string](Get-Item -LiteralPath $path).VersionInfo.FileVersion
                    if (-not [string]::IsNullOrWhiteSpace($version)) { $entry.fileVersion = $version }
                }
            }
            $result.drivers += $entry
        }
        catch {
            $result.drivers += [ordered]@{
                name = $name; displayName = $null; state = $null; startMode = $null
                pathName = $null; fileVersion = $null; reason = $_.Exception.Message
            }
        }
    }

    foreach ($name in @('ProxiFyreService', 'Proxifier', 'ndisrd', 'ProxifierDrv', 'WinDivert')) {
        try {
            $service = Get-Service -Name $name -ErrorAction Stop
            $startType = [string]$service.StartType
            if ([string]::IsNullOrWhiteSpace($startType)) {
                $wmi = Get-CimInstance Win32_Service -Filter ("Name='{0}'" -f $name) -ErrorAction SilentlyContinue
                if ($null -ne $wmi) { $startType = [string]$wmi.StartMode }
            }
            $result.serviceRows += [ordered]@{
                name = [string]$service.Name
                displayName = [string]$service.DisplayName
                status = [string]$service.Status
                startType = $startType
            }
        }
        catch {
            $result.serviceRows += [ordered]@{
                name = $name; displayName = $null; status = $null; startType = $null
                reason = $_.Exception.Message
            }
        }
    }

    if ($reasons.Count -gt 0) { $result.reason = ($reasons -join '; ') }
    return $result
}

function Get-SnapshotEvidence {
    $result = [ordered]@{
        value = $null
        reason = $null
        vmId = $null
        vmName = $null
        sources = @()
    }
    $reasons = New-Object System.Collections.ArrayList

    try {
        $product = Get-CimInstance Win32_ComputerSystemProduct -ErrorAction Stop
        $result.vmId = [string]$product.UUID
        $result.sources += 'Win32_ComputerSystemProduct.UUID'
    }
    catch {
        [void]$reasons.Add(('Win32_ComputerSystemProduct: ' + $_.Exception.Message))
    }

    try {
        $guest = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Virtual Machine\Guest\Parameters' -ErrorAction Stop
        $result.vmName = [string]$guest.VirtualMachineName
        $result.sources += 'HKLM:\SOFTWARE\Microsoft\Virtual Machine\Guest\Parameters'
    }
    catch {
        [void]$reasons.Add(('guest parameters: ' + $_.Exception.Message))
    }

    # A checkpoint identifier lives on the Hyper-V host, and the guest exposes no property that names
    # the current checkpoint. Record the VM identity the management namespace carries when it is
    # present, and leave snapshotId null rather than passing a VM id off as a checkpoint id.
    try {
        $candidates = @(Get-CimInstance -Namespace 'root\virtualization\v2' -ClassName Msvm_VirtualSystemSettingData -ErrorAction Stop)
        $selected = $null
        foreach ($candidate in $candidates) {
            if ((-not [string]::IsNullOrWhiteSpace($result.vmName) -and $candidate.ElementName -eq $result.vmName) -or
                    $candidate.ElementName -eq $env:COMPUTERNAME) {
                $selected = $candidate
                break
            }
        }
        if ($null -eq $selected) { throw 'no virtual-system setting matches this guest' }
        if ([string]::IsNullOrWhiteSpace($result.vmId)) {
            $result.vmId = [string]$selected.VirtualSystemIdentifier
        }
        $result.sources += 'Msvm_VirtualSystemSettingData.VirtualSystemIdentifier'
    }
    catch {
        [void]$reasons.Add(('checkpoint identifier: ' + $_.Exception.Message))
    }

    if ([string]::IsNullOrWhiteSpace($result.value)) {
        $result.value = $null
        if ($reasons.Count -gt 0) { $result.reason = ($reasons -join '; ') }
        else { $result.reason = 'the guest exposes no checkpoint identifier' }
    }
    return $result
}

function Get-EnvironmentBlock {
    # Reads the script's parameter values; every collector is read-only and returns its own reason
    # when the value is not available on this machine.
    param($Products, [string]$Started)

    $osInfo = Get-OperatingSystemInfo
    $firewallInfo = Get-FirewallProfileState
    $singBoxInfo = Get-SingBoxVersion
    $nicOffload = Get-NicOffload
    $dynamicMemory = Get-DynamicMemory
    $productVersions = @(Get-ProductVersions -Products $Products)
    $powerPlan = Get-PowerPlan
    $driverVersions = Get-DriverVersions
    $snapshot = Get-SnapshotEvidence

    $block = [ordered]@{
        started = $Started
        os = $osInfo.caption
        build = $osInfo.build
        logicalCpus = [Environment]::ProcessorCount
        totalRamMb = $osInfo.totalVisibleMb
        freeRamMb = $osInfo.freeMb
        passes = $Passes
        seed = $Seed
        firewallProfiles = $firewallInfo.value
        firewallProfilesReason = $firewallInfo.reason
        singBox = $singBoxInfo.value
        singBoxReason = $singBoxInfo.reason
        target = [ordered]@{
            host = $TargetAddress
            tcpPort = $TcpPort; udpPort = $UdpPort; dnsPort = $DnsPort; dnsPortAlt = $DnsPortAlt
            tcpPortDirect = $TcpPortDirect; dnsPortDirect = $DnsPortDirect
        }
        clockOffsetMs = $null
        clockGate = $null
        nicOffload = $nicOffload
        dynamicMemory = $dynamicMemory
        productVersions = $productVersions
        powerPlan = $powerPlan
        driverVersions = $driverVersions
        snapshotId = $snapshot.value
        snapshotIdReason = $snapshot.reason
        snapshotEvidence = [ordered]@{
            vmId = $snapshot.vmId
            vmName = $snapshot.vmName
            sources = @($snapshot.sources)
        }
        environmentChanges = @()
        unavailable = [ordered]@{}
    }

    if ($null -eq $block.os) {
        $block.unavailable['os'] = $osInfo.reason
        $block.unavailable['build'] = $osInfo.reason
    }
    if ($null -eq $block.totalRamMb) {
        $block.unavailable['totalRamMb'] = $osInfo.reason
        $block.unavailable['freeRamMb'] = $osInfo.reason
    }

    return $block
}

# ---------------------------------------------------------------------------------------------
# Campaign
# ---------------------------------------------------------------------------------------------

$products = Get-Products

Write-Log '=== preflight ==='
New-Item -ItemType Directory -Force -Path $OutRoot | Out-Null

$existingCampaign = @(Get-ChildItem -LiteralPath $OutRoot -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq 'environment.json' -or $_.Name -eq 'clock.jsonl' -or $_.Name -like 'pass*' })
if ($PreflightOnly -and $existingCampaign.Count -gt 0) {
    Write-Log ('  preflight-only refused: {0} already holds a campaign ({1}); use a fresh -OutRoot' -f $OutRoot, (($existingCampaign | ForEach-Object { $_.Name }) -join ', '))
    exit 1
}

$clockLogPath = Join-Path $OutRoot 'clock.jsonl'
if (-not $PreflightOnly) {
    if ($existingCampaign.Count -gt 0) {
        Write-Log ('  WARNING output root already holds campaign files ({0}); overwriting environment.json and clock.jsonl' -f (($existingCampaign | ForEach-Object { $_.Name }) -join ', '))
    }
    # clock.jsonl is the per-pass evidence: one line per probe, pre and post each pass. Truncating it at
    # the start of the campaign keeps a previous run's samples from being read as this run's clock.
    [System.IO.File]::WriteAllText($clockLogPath, '', (New-Object System.Text.UTF8Encoding($false)))
}
$started = (Get-Date).ToString('o')

# The clock probe runs first so the host probe does not wait through the environment sweep; both the
# probe and the sweep are read-only and precede every Set-* and benchmark process start.
$clockGate = Invoke-ClockGate -Count 5 -SanityLimitMs 5000 -SyncLimitMs 200 -RequireSync:$RequireClockSync
$environment = Get-EnvironmentBlock -Products $products -Started $started
$environment.clockGate = $clockGate
$environment.clockOffsetMs = $clockGate.medianOffsetMs
Write-JsonNoBom -Path (Join-Path $OutRoot 'environment.json') -Value $environment
Write-Log ('  environment: ' + (Join-Path $OutRoot 'environment.json'))
Write-Log ('  env: {0} build {1}, {2} cpus, {3} MB visible' -f $environment.os, $environment.build, $environment.logicalCpus, $environment.totalRamMb)
if ($null -ne $environment.singBox) { Write-Log ('  sing-box: ' + $environment.singBox) }

if ($clockGate.samples.Count -gt 0) {
    $sampleText = (($clockGate.samples | ForEach-Object { '{0:+0.0;-0.0;0}' -f [double]$_.offsetMs }) -join ', ')
    Write-Log ('  clock: median {0:+0.0;-0.0;0} ms (VM - host), sanity +/-{1:F0} ms, sync +/-{2:F0} ms, samples [{3}] ms' -f [double]$clockGate.medianOffsetMs, [double]$clockGate.sanityLimitMs, [double]$clockGate.syncLimitMs, $sampleText)
}
else {
    Write-Log '  clock: no samples'
}
Write-Log ('  clock policy: ' + $clockGate.policy)
if (-not $clockGate.passed) {
    Write-Log ('  clock gate FAILED: ' + $clockGate.reason)
    exit 1
}
if ($null -ne $clockGate.warning) {
    Write-Log ('  clock gate passed with warning: ' + $clockGate.warning)
}
else {
    Write-Log '  clock gate passed'
}
if ($PreflightOnly) {
    Write-Log '  clock samples: none (preflight-only)'
}
else {
    Write-Log ('  clock samples: ' + $clockLogPath)
}

if ($PreflightOnly) {
    Write-Log '  preflight-only: environment written; exiting before any change or pass'
    exit 0
}

try {
# Every exit after the firewall change runs the teardown below: the clock aborts inside the pass loop
# must stop the products, stop sing-box and restore the firewall, not leave the VM half-configured.
# The firewall state in environment.json is the state as found above. The disable is a campaign
# change, so it is recorded with its before and after arrays instead of replacing that state.
$firewallChange = Disable-FirewallProfiles -Before $environment.firewallProfiles
$environment.environmentChanges += $firewallChange
Write-JsonNoBom -Path (Join-Path $OutRoot 'environment.json') -Value $environment
if ($firewallChange.Contains('error')) {
    Write-Log ('  WARNING firewall could not be disabled: ' + $firewallChange.error)
}
else {
    Write-Log ('  firewall disabled: ' + (($firewallChange.after | ForEach-Object { $_.name + '=' + $_.enabled }) -join ' '))
}
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

$random = [System.Random]::new($Seed)

for ($pass = 1; $pass -le $Passes; $pass++) {
    $passDir = Join-Path $OutRoot ('pass{0}' -f $pass)
    New-Item -ItemType Directory -Force -Path $passDir | Out-Null
    Write-Log ('=== pass {0}/{1} ===' -f $pass, $Passes)

    # Product order is randomised per pass so a drift in the machine shows up as scatter rather than
    # as a difference between products, which would be indistinguishable from a real effect.
    $order = @($products | Sort-Object { $random.Next() })
    Write-JsonNoBom -Path (Join-Path $passDir 'order.txt') -Value (($order | ForEach-Object { $_.Id }) -join ',')

    try {
        $preClock = Measure-ClockPhase -Path $clockLogPath -Pass $pass -Phase 'pre'
    }
    catch {
        Write-Log ('  clock probe failed before pass {0}: {1}' -f $pass, $_.Exception.Message)
        exit 1
    }
    $preBounds = Test-ClockPhaseBounds -PhaseResult $preClock -RequireSync:$RequireClockSync
    if (-not $preBounds.passed) {
        Write-Log ('  clock gate FAILED: ' + $preBounds.reason)
        exit 1
    }
    if ($null -ne $preBounds.warning) { Write-Log ('  clock WARNING: ' + $preBounds.warning) }

    Invoke-ControlBlock -PassDir $passDir -Pass $pass -Tag 'pre'

    foreach ($product in $order) {
        Invoke-Row -Product $product -PassDir $passDir -Pass $pass
    }

    Invoke-ControlBlock -PassDir $passDir -Pass $pass -Tag 'post'

    try {
        $postClock = Measure-ClockPhase -Path $clockLogPath -Pass $pass -Phase 'post'
    }
    catch {
        Write-Log ('  clock probe failed after pass {0}: {1}' -f $pass, $_.Exception.Message)
        exit 1
    }
    $postBounds = Test-ClockPhaseBounds -PhaseResult $postClock -RequireSync:$RequireClockSync
    if (-not $postBounds.passed) {
        Write-Log ('  clock gate FAILED: ' + $postBounds.reason)
        exit 1
    }
    if ($null -ne $postBounds.warning) { Write-Log ('  clock WARNING: ' + $postBounds.warning) }

    $driftMs = [Math]::Abs([double]$postClock.medianOffsetMs - [double]$preClock.medianOffsetMs)
    Write-Log ('  clock pass {0}: pre {1:+0.0;-0.0;0} ms, post {2:+0.0;-0.0;0} ms, drift {3:F1} ms (VM - host)' -f $pass, [double]$preClock.medianOffsetMs, [double]$postClock.medianOffsetMs, $driftMs)
    if ($driftMs -gt 100.0) {
        Write-Log ('  clock drift gate FAILED: {0:F1} ms across pass {1} exceeds the 100 ms bound' -f $driftMs, $pass)
        exit 1
    }

    Write-Log ('  pass {0} done' -f $pass)
}

}
finally {
    Write-Log '=== teardown ==='
    try {
        foreach ($product in $products) { Stop-Product -Product $product }
        Get-Process -Name 'sing-box' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    }
    catch {
        Write-Log ('  WARNING process teardown failed: ' + $_.Exception.Message)
    }
    if ($null -ne $firewallChange) {
        if ($null -eq $environment.firewallProfiles) {
            Write-Log '  WARNING firewall was changed but the pre-change state is unavailable; not restoring'
        }
        else {
            $firewallRestore = $null
            try {
                $firewallRestore = Restore-FirewallProfiles -Before $environment.firewallProfiles -After $firewallChange.after
                if ($null -ne $firewallRestore) {
                    $environment.environmentChanges += $firewallRestore
                    try {
                        Write-JsonNoBom -Path (Join-Path $OutRoot 'environment.json') -Value $environment
                    }
                    catch {
                        Write-Log ('  WARNING could not write environment.json after firewall restore: ' + $_.Exception.Message)
                    }
                    if ($firewallRestore.Contains('error')) {
                        Write-Log ('  WARNING firewall restore failed: ' + $firewallRestore.error)
                    }
                    else {
                        Write-Log ('  firewall restored: ' + (($firewallRestore.after | ForEach-Object { $_.name + '=' + $_.enabled }) -join ' '))
                    }
                }
            }
            catch {
                Write-Log ('  WARNING firewall restore failed: ' + $_.Exception.Message)
            }
        }
    }
}

if ($script:Failures.Count -gt 0) {
    Write-Log ('=== FAILURES ({0}) ===' -f $script:Failures.Count)
    foreach ($failure in $script:Failures) { Write-Log ('  ' + $failure) }
}
else {
    Write-Log '=== no failures ==='
}

Write-Log '=== orchestrator complete ==='
