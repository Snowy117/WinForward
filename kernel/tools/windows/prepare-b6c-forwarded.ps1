#requires -Version 7.0
<#
.SYNOPSIS
    B6c 独立 Linux 门禁的 Windows 侧准备：测试用原始目的地别名、IPv6 链路本地地址与端口放行。
.DESCRIPTION
    本脚本只管 B6c 门禁自己创建的资源（B6C-ENV-* 规则 + -Address 别名 + -WithIpv6 的链路本地
    地址）。-Remove 会删除这些资源，包括 v6 地址与规则；不要在与其它测试共享同一别名的环境里
    并发执行。别名由门禁在开始前加、结束后删，模拟“外部流量打到原始服务器地址”的入口。

    -WithIpv6 额外准备 IPv6 数据面所需的链路本地地址：
      Internal 第二 L fe80::98、Internal 原始目的 fe80::99、External 原始目的 fe80::199。
    主链路本地 L1（Internal / External）只读发现，不添加也不删除。原始目的地必须与 L 不同，
    否则回程 SNAT 写错也无法从客户端观察。
#>
param(
    [int]$IfIndex = 5,
    [string]$Address = "192.168.100.99",
    [int]$PrefixLength = 24,
    [int]$ExternalIfIndex = 14,
    [string]$InternalL2Address = "fe80::98",
    [string]$InternalTargetAddress = "fe80::99",
    [string]$ExternalTargetAddress = "fe80::199",
    [switch]$WithIpv6,
    [switch]$Remove
)

$ErrorActionPreference = "Stop"

$managedIpv6Addresses = @($InternalL2Address, $InternalTargetAddress, $ExternalTargetAddress)

function Get-InterfaceName([int]$Index) {
    $adapter = Get-NetAdapter -InterfaceIndex $Index -ErrorAction Stop
    return $adapter.Name
}

function Get-PlainAddress([string]$Text) {
    # Get-NetIPAddress 对链路本地地址返回带 %scope 的形式；netsh 与客户端只接受裸地址。
    return ($Text -split '%')[0]
}

function Remove-Ipv6Address([string]$InterfaceName, [string]$Ipv6Address) {
    & netsh interface ipv6 delete address $InterfaceName $Ipv6Address 2>$null | Out-Null
}

function Add-Ipv6Address([string]$InterfaceName, [string]$Ipv6Address) {
    Remove-Ipv6Address $InterfaceName $Ipv6Address
    & netsh interface ipv6 add address $InterfaceName $Ipv6Address | Out-Null
}

function Wait-Ipv6Preferred([int]$Index, [string]$Ipv6Address) {
    for ($i = 0; $i -lt 100; $i++) {
        $entry = Get-NetIPAddress -InterfaceIndex $Index -AddressFamily IPv6 -ErrorAction SilentlyContinue |
            Where-Object { (Get-PlainAddress $_.IPAddress) -eq $Ipv6Address } |
            Select-Object -First 1
        if ($entry -and $entry.AddressState -eq "Preferred") {
            return $entry
        }
        Start-Sleep -Milliseconds 200
    }
    $state = (Get-NetIPAddress -InterfaceIndex $Index -AddressFamily IPv6 -ErrorAction SilentlyContinue |
        Where-Object { (Get-PlainAddress $_.IPAddress) -eq $Ipv6Address } |
        Select-Object -First 1).AddressState
    throw "$Ipv6Address 在 ifIndex=$Index 上未进入 Preferred（state=$state）"
}

function Find-PrimaryLinkLocal([int]$Index, [string]$InterfaceName) {
    $candidates = @(Get-NetIPAddress -InterfaceIndex $Index -AddressFamily IPv6 -AddressState Preferred -ErrorAction Stop |
        Where-Object {
            $plain = Get-PlainAddress $_.IPAddress
            $plain -like 'fe80::*' -and $managedIpv6Addresses -notcontains $plain
        })
    if ($candidates.Count -eq 0) {
        throw "$InterfaceName（ifIndex=$Index）没有可用的链路本地地址，无法准备 IPv6 夹具"
    }
    return Get-PlainAddress $candidates[0].IPAddress
}

if ($Remove) {
    Get-NetFirewallRule -DisplayName "B6C-ENV-*" -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
    Remove-NetIPAddress -IPAddress $Address -Confirm:$false -ErrorAction SilentlyContinue

    # v6 地址清理不依赖 -WithIpv6：即使本次没有准备，也删除已知测试地址，避免残留。
    $internalName = $null
    $externalName = $null
    try { $internalName = Get-InterfaceName $IfIndex } catch { }
    try { $externalName = Get-InterfaceName $ExternalIfIndex } catch { }
    if ($internalName) {
        Remove-Ipv6Address $internalName $InternalL2Address
        Remove-Ipv6Address $internalName $InternalTargetAddress
    }
    if ($externalName) {
        Remove-Ipv6Address $externalName $ExternalTargetAddress
    }

    $detail = "internal=$InternalL2Address,$InternalTargetAddress external=$ExternalTargetAddress"
    if ($internalName) { $detail += " internal_if=$internalName" }
    if ($externalName) { $detail += " external_if=$externalName" }
    "B6C-V6-CLEAN $detail"
    "B6C-ENV-CLEAN address=$Address rules=B6C-ENV-*"
    exit 0
}

if ($WithIpv6) {
    $binding = Get-NetAdapterBinding -Name (Get-InterfaceName $IfIndex) -ComponentID ms_tcpip6 -ErrorAction SilentlyContinue
    if (-not $binding -or -not $binding.Enabled) {
        throw "Internal 的 ms_tcpip6 未启用；IPv6 forwards 夹具需要它，请手工启用后重跑（脚本不修改宿主绑定）"
    }
}

Remove-NetIPAddress -IPAddress $Address -Confirm:$false -ErrorAction SilentlyContinue
New-NetIPAddress -InterfaceIndex $IfIndex -IPAddress $Address -PrefixLength $PrefixLength -ErrorAction Stop | Out-Null
for ($i = 0; $i -lt 75; $i++) {
    $state = (Get-NetIPAddress -IPAddress $Address -ErrorAction SilentlyContinue).AddressState
    if ($state -eq "Preferred") { break }
    Start-Sleep -Milliseconds 200
}
$ip = Get-NetIPAddress -IPAddress $Address -ErrorAction Stop
if ($ip.AddressState -ne "Preferred") { throw "alias $Address not Preferred: $($ip.AddressState)" }

$rules = @(
    @{ Name = "B6C-ENV-40250T"; Protocol = "TCP"; Port = 40250 },
    @{ Name = "B6C-ENV-40250U"; Protocol = "UDP"; Port = 40250 },
    @{ Name = "B6C-ENV-40251T"; Protocol = "TCP"; Port = 40251 },
    @{ Name = "B6C-ENV-40251U"; Protocol = "UDP"; Port = 40251 },
    @{ Name = "B6C-ENV-40400U"; Protocol = "UDP"; Port = 40400 },
    @{ Name = "B6C-ENV-40400T"; Protocol = "TCP"; Port = 40400 },
    @{ Name = "B6C-ENV-40420T"; Protocol = "TCP"; Port = 40420 },
    @{ Name = "B6C-ENV-40600U"; Protocol = "UDP"; Port = 40600 },
    @{ Name = "B6C-ENV-40500"; Protocol = "TCP"; Port = 40500 },
    @{ Name = "B6C-ENV-40500U"; Protocol = "UDP"; Port = 40500 },
    @{ Name = "B6C-ENV-41400T"; Protocol = "TCP"; Port = 41400 }
)
foreach ($rule in $rules) {
    Remove-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue
    New-NetFirewallRule -DisplayName $rule.Name -Direction Inbound -Protocol $rule.Protocol `
        -LocalPort $rule.Port -Action Allow -Profile Any | Out-Null
}
"B6C-ENV-READY address=$Address ifIndex=$IfIndex state=$($ip.AddressState) prefix=$($ip.PrefixLength)"

if ($WithIpv6) {
    $internalL1 = Find-PrimaryLinkLocal $IfIndex (Get-InterfaceName $IfIndex)
    $externalL1 = Find-PrimaryLinkLocal $ExternalIfIndex (Get-InterfaceName $ExternalIfIndex)

    Add-Ipv6Address (Get-InterfaceName $IfIndex) $InternalL2Address
    Add-Ipv6Address (Get-InterfaceName $IfIndex) $InternalTargetAddress
    Add-Ipv6Address (Get-InterfaceName $ExternalIfIndex) $ExternalTargetAddress

    $null = Wait-Ipv6Preferred $IfIndex $InternalL2Address
    $null = Wait-Ipv6Preferred $IfIndex $InternalTargetAddress
    $null = Wait-Ipv6Preferred $ExternalIfIndex $ExternalTargetAddress

    $v6Rules = @(
        @{ Name = "B6C-ENV-V6-40250T"; Protocol = "TCP"; Port = 40250 },
        @{ Name = "B6C-ENV-V6-40250U"; Protocol = "UDP"; Port = 40250 },
        @{ Name = "B6C-ENV-V6-40251T"; Protocol = "TCP"; Port = 40251 },
        @{ Name = "B6C-ENV-V6-40251U"; Protocol = "UDP"; Port = 40251 },
        @{ Name = "B6C-ENV-V6-40400T"; Protocol = "TCP"; Port = 40400 },
        @{ Name = "B6C-ENV-V6-40400U"; Protocol = "UDP"; Port = 40400 },
        @{ Name = "B6C-ENV-V6-40420T"; Protocol = "TCP"; Port = 40420 },
        @{ Name = "B6C-ENV-V6-40600U"; Protocol = "UDP"; Port = 40600 },
        @{ Name = "B6C-ENV-V6-41400T"; Protocol = "TCP"; Port = 41400 }
    )
    foreach ($rule in $v6Rules) {
        Remove-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue
        New-NetFirewallRule -DisplayName $rule.Name -Direction Inbound -Protocol $rule.Protocol `
            -LocalPort $rule.Port -Action Allow -Profile Any | Out-Null
    }

    "B6C-V6-READY internal_l1=$internalL1 internal_l2=$InternalL2Address internal_target=$InternalTargetAddress " +
        "external_l1=$externalL1 external_target=$ExternalTargetAddress internal_ifindex=$IfIndex " +
        "external_ifindex=$ExternalIfIndex"
}
