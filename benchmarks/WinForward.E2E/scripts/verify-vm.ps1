<#
.SYNOPSIS
Read-only prerequisite check for the Windows VM that runs a WinForward.E2E campaign.

.DESCRIPTION
Checks the C:\wfbench layout, the two WinForward builds and their ndisapi.dll sidecars, the patched
sing-box build, the ProxiFyre and Proxifier services, ProxyBridge, the three filter drivers, the
Proxifier profile, pwsh 7, the release plans and caliber, the firewall state as found, and TCP
reachability of the Linux host's targets and clock probe. It changes nothing: no Set-*, no
Start-Service, no file writes. Every missing item is printed by name and the script exits 1.

.PARAMETER TargetAddress
The Linux host's address. Required for the reachability checks; without it the check reports the
missing target address and still runs the local checks.

.EXAMPLE
& 'C:\wfbench\verify-vm.ps1' -TargetAddress <host>
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$TargetAddress = '',
    [string]$VmRoot = 'C:\wfbench',
    [string]$ResultsRoot = '',
    [string]$PlanRoot = '',
    [string]$PwshPath = 'C:\Program Files\PowerShell\7\pwsh.exe',
    [string]$ProxyBridgeExe = 'C:\Program Files\ProxyBridge\ProxyBridge_CLI.exe',
    [int]$TcpPort = 40010,
    [int]$TcpPortDirect = 40011,
    [int]$DnsPort = 53,
    [int]$DnsPortAlt = 40053,
    [int]$ClockProbePort = 40099,
    [int]$ConnectTimeoutMs = 1500,
    [switch]$SkipReachability
)

$ErrorActionPreference = 'Continue'
if ([string]::IsNullOrWhiteSpace($ResultsRoot)) { $ResultsRoot = Join-Path $VmRoot 'results' }
if ([string]::IsNullOrWhiteSpace($PlanRoot)) { $PlanRoot = Join-Path $VmRoot 'e2e' }

$global:WfVerifyMissing = New-Object System.Collections.ArrayList
$global:WfVerifyChecks = 0

function Add-Ok {
    param([string]$Name, [string]$Detail = '')
    $global:WfVerifyChecks++
    if ([string]::IsNullOrWhiteSpace($Detail)) {
        Write-Host ('[ok]      ' + $Name)
    }
    else {
        Write-Host ('[ok]      ' + $Name + ': ' + $Detail)
    }
}

function Add-Missing {
    param([string]$Name, [string]$Detail = '')
    $global:WfVerifyChecks++
    [void]$global:WfVerifyMissing.Add([pscustomobject]@{ name = $Name; detail = $Detail })
    if ([string]::IsNullOrWhiteSpace($Detail)) {
        Write-Host ('[missing] ' + $Name)
    }
    else {
        Write-Host ('[missing] ' + $Name + ': ' + $Detail)
    }
}

function Test-File {
    param([string]$Path, [string]$Name)
    if (Test-Path -LiteralPath $Path -PathType Leaf -ErrorAction SilentlyContinue) {
        Add-Ok $Name $Path
        return $true
    }
    Add-Missing $Name ('not found at ' + $Path)
    return $false
}

function Test-Directory {
    param([string]$Path, [string]$Name)
    if (Test-Path -LiteralPath $Path -PathType Container -ErrorAction SilentlyContinue) {
        Add-Ok $Name $Path
        return $true
    }
    Add-Missing $Name ('not found at ' + $Path)
    return $false
}

function Test-Tcp {
    param([string]$Address, [int]$Port)
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $task = $client.ConnectAsync($Address, $Port)
        if (-not $task.Wait($ConnectTimeoutMs)) { return $false }
        return $client.Connected
    }
    catch {
        return $false
    }
    finally {
        try { $client.Close() } catch { }
    }
}

function Get-VersionText {
    param([string]$Path, [string[]]$Arguments)
    try {
        $output = (& $Path @Arguments 2>&1 | Out-String).Trim()
        $code = $LASTEXITCODE
        if ($null -eq $code) { $code = 0 }
        return [pscustomobject]@{ ok = ($code -eq 0); text = $output }
    }
    catch {
        return [pscustomobject]@{ ok = $false; text = $_.Exception.Message }
    }
}

Write-Output 'verify-vm: read-only prerequisite check (no Set-*, no Start-Service, no writes)'
try {
    $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
    Write-Output ('vm:       ' + [string]$os.Caption + ' build ' + [string]$os.BuildNumber)
}
catch {
    Write-Output ('vm:       (could not read the OS: ' + $_.Exception.Message + ')')
    Add-Missing 'vm-identity' 'Win32_OperatingSystem could not be read'
}

Write-Output ''
Write-Output '=== C:\wfbench layout ==='
[void](Test-Directory $VmRoot 'wfbench-root')
foreach ($name in @('e2e', 'e2e-direct', 'wf-aot', 'wf-fdd', 'singbox', 'proxifier', 'proxybridge', 'stage')) {
    [void](Test-Directory (Join-Path $VmRoot $name) ('layout:' + $name))
}
[void](Test-Directory $ResultsRoot 'results-root')

Write-Output ''
Write-Output '=== clients and WinForward builds ==='
foreach ($name in @('WinForward.E2E.exe', 'WinForward.E2E.dll', 'WinForward.E2E.Contracts.dll',
        'WinForward.E2E.deps.json', 'WinForward.E2E.runtimeconfig.json')) {
    [void](Test-File (Join-Path (Join-Path $VmRoot 'e2e') $name) ('client:' + $name))
}
foreach ($name in @('WinForward.E2E.Direct.exe', 'WinForward.E2E.Direct.dll', 'WinForward.E2E.Contracts.dll',
        'WinForward.E2E.Direct.deps.json', 'WinForward.E2E.Direct.runtimeconfig.json')) {
    [void](Test-File (Join-Path (Join-Path $VmRoot 'e2e-direct') $name) ('direct-client:' + $name))
}
foreach ($build in @('wf-aot', 'wf-fdd')) {
    $buildDir = Join-Path $VmRoot $build
    [void](Test-File (Join-Path $buildDir 'WinForward.exe') ($build + ':WinForward.exe'))
    [void](Test-File (Join-Path $buildDir 'ndisapi.dll') ($build + ':ndisapi.dll'))
}
[void](Test-File (Join-Path $VmRoot 'orchestrator.ps1') 'orchestrator.ps1')

Write-Output ''
Write-Output '=== release plans and caliber ==='
$planNames = @('full-plan.json', 'udp-plan.json', 'dns-plan.json', 'dual-plan.json', 'base-plan.json')
$plansPresent = $true
foreach ($name in $planNames) {
    if (-not (Test-File (Join-Path $PlanRoot $name) ('plan:' + $name))) { $plansPresent = $false }
}
$caliberPath = Join-Path $PlanRoot 'caliber.json'
if (Test-Path -LiteralPath $caliberPath -PathType Leaf -ErrorAction SilentlyContinue) {
    try {
        $caliber = Get-Content -LiteralPath $caliberPath -Raw -ErrorAction Stop | ConvertFrom-Json
        Add-Ok 'caliber.json' $caliberPath
        if ([string]$caliber.planSet -eq 'plans-release') {
            Add-Ok 'caliber.planSet' ([string]$caliber.planSet)
        }
        else {
            Add-Missing 'caliber.planSet' ('expected plans-release, got ' + [string]$caliber.planSet)
        }
        if ($plansPresent -and -not [string]::IsNullOrWhiteSpace([string]$caliber.sha256)) {
            $actual = (Get-FileHash -LiteralPath (Join-Path $PlanRoot 'full-plan.json') -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($actual -eq ([string]$caliber.sha256).ToLowerInvariant()) {
                Add-Ok 'caliber.sha256' 'matches full-plan.json'
            }
            else {
                Add-Missing 'caliber.sha256' ('does not match full-plan.json (' + $actual.Substring(0, 12) + ' != ' + ([string]$caliber.sha256).Substring(0, 12) + ')')
            }
        }
    }
    catch {
        Add-Missing 'caliber.json' ('unreadable: ' + $_.Exception.Message)
    }
}
else {
    Add-Missing 'caliber.json' ('not found at ' + $caliberPath + ' (was the release plan set deployed?)')
}

Write-Output ''
Write-Output '=== sing-box ==='
$singBoxDir = Join-Path $VmRoot 'singbox'
$singBoxExe = Join-Path $singBoxDir 'sing-box.exe'
[void](Test-File $singBoxExe 'sing-box:sing-box.exe')
[void](Test-File (Join-Path $singBoxDir 'config.json') 'sing-box:config.json')
if (Test-Path -LiteralPath $singBoxExe -PathType Leaf -ErrorAction SilentlyContinue) {
    $version = Get-VersionText -Path $singBoxExe -Arguments @('version')
    $firstLine = (($version.text -split "`r?`n") | Select-Object -First 1)
    if ($version.ok -and $version.text -match '1\.14\.2-singfix') {
        Add-Ok 'sing-box:version' $firstLine
    }
    else {
        Add-Missing 'sing-box:version' ('expected 1.14.2-singfix; ' + $firstLine)
    }
}

Write-Output ''
Write-Output '=== services, ProxyBridge and drivers ==='
$proxiFyre = Get-Service -Name 'ProxiFyreService' -ErrorAction SilentlyContinue
if ($null -ne $proxiFyre) {
    Add-Ok 'service:ProxiFyreService' ([string]$proxiFyre.Status)
}
else {
    Add-Missing 'service:ProxiFyreService' 'not installed (the campaign starts and stops it)'
}
$proxifier = Get-Service -Name 'Proxifier' -ErrorAction SilentlyContinue
if ($null -ne $proxifier) {
    Add-Ok 'service:Proxifier' ([string]$proxifier.Status)
}
else {
    Add-Missing 'service:Proxifier' 'not installed as the service named Proxifier'
}
if (Test-Path -LiteralPath $ProxyBridgeExe -PathType Leaf -ErrorAction SilentlyContinue) {
    Add-Ok 'proxybridge:ProxyBridge_CLI.exe' $ProxyBridgeExe
}
else {
    $fallback = Join-Path (Join-Path $VmRoot 'proxybridge') 'ProxyBridge_CLI.exe'
    if (Test-Path -LiteralPath $fallback -PathType Leaf -ErrorAction SilentlyContinue) {
        Add-Missing 'proxybridge:ProxyBridge_CLI.exe' ('only found at ' + $fallback + '; the orchestrator expects ' + $ProxyBridgeExe)
    }
    else {
        Add-Missing 'proxybridge:ProxyBridge_CLI.exe' ('not found at ' + $ProxyBridgeExe)
    }
}
[void](Test-File (Join-Path (Join-Path $VmRoot 'proxybridge') 'bench.pbprofile') 'proxybridge:bench.pbprofile')
foreach ($name in @('ndisrd', 'ProxifierDrv', 'WinDivert')) {
    $service = Get-Service -Name $name -ErrorAction SilentlyContinue
    $driver = Get-CimInstance Win32_SystemDriver -Filter ("Name='{0}'" -f $name) -ErrorAction SilentlyContinue
    if ($null -ne $service -or $null -ne $driver) {
        $state = if ($null -ne $driver) { [string]$driver.State } elseif ($null -ne $service) { [string]$service.Status } else { 'unknown' }
        Add-Ok ('driver:' + $name) $state
    }
    else {
        Add-Missing ('driver:' + $name) 'not registered as a service or system driver'
    }
}

Write-Output ''
Write-Output '=== Proxifier profile ==='
[void](Test-File (Join-Path (Join-Path $VmRoot 'proxifier') 'bench.ppx') 'proxifier:bench.ppx')

Write-Output ''
Write-Output '=== pwsh 7 ==='
if (Test-Path -LiteralPath $PwshPath -PathType Leaf -ErrorAction SilentlyContinue) {
    $pwsh = Get-VersionText -Path $PwshPath -Arguments @('-NoProfile', '-NonInteractive', '-Command', '$PSVersionTable.PSVersion.ToString()')
    $firstLine = (($pwsh.text -split "`r?`n") | Select-Object -First 1)
    if ($pwsh.ok -and $pwsh.text -match '^7\.') {
        Add-Ok 'pwsh7' $firstLine
    }
    else {
        Add-Missing 'pwsh7' ('present but did not run: ' + $firstLine)
    }
}
else {
    Add-Missing 'pwsh7' ('not found at ' + $PwshPath)
}

Write-Output ''
Write-Output '=== firewall (as found, not judged) ==='
$firewall = @(Get-NetFirewallProfile -ErrorAction SilentlyContinue | ForEach-Object {
        [string]$_.Name + '=' + [string]$_.Enabled
    })
if ($firewall.Count -eq 0) {
    Write-Output 'firewall: (could not read the profiles)'
}
else {
    Write-Output ('firewall: ' + ($firewall -join ', '))
}

Write-Output ''
Write-Output '=== host target reachability ==='
if ($SkipReachability) {
    Write-Output 'reachability: skipped (-SkipReachability)'
}
elseif ([string]::IsNullOrWhiteSpace($TargetAddress)) {
    Add-Missing 'target-address' 'pass -TargetAddress <host> to check the targets and the clock probe'
}
else {
    $probes = @(
        [pscustomobject]@{ name = 'target:proxied-tcp'; port = $TcpPort },
        [pscustomobject]@{ name = 'target:direct-tcp'; port = $TcpPortDirect },
        [pscustomobject]@{ name = 'target:proxied-dns'; port = $DnsPort },
        [pscustomobject]@{ name = 'target:proxied-dns-alt'; port = $DnsPortAlt },
        [pscustomobject]@{ name = 'clock-probe'; port = $ClockProbePort }
    )
    foreach ($probe in $probes) {
        if (Test-Tcp -Address $TargetAddress -Port $probe.port) {
            Add-Ok $probe.name ($TargetAddress + ':' + $probe.port + ' reachable')
        }
        else {
            Add-Missing $probe.name ('no TCP accept from ' + $TargetAddress + ':' + $probe.port)
        }
    }
}

Write-Output ''
if ($global:WfVerifyMissing.Count -eq 0) {
    Write-Output ('verify-vm: OK (' + $global:WfVerifyChecks + ' checks)')
    exit 0
}
Write-Output ('verify-vm: FAILED (' + $global:WfVerifyMissing.Count + ' missing of ' + $global:WfVerifyChecks + ' checks)')
foreach ($item in $global:WfVerifyMissing) {
    Write-Output ('  - ' + $item.name)
}
exit 1
