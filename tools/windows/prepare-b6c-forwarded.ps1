#requires -Version 7.0
<#
.SYNOPSIS
    B6c 独立 Linux 门禁的 Windows 侧准备：测试用原始目的地别名与端口放行。
.DESCRIPTION
    本脚本只管 B6c 门禁自己创建的资源（B6C-ENV-* 规则 + -Address 别名）。-Remove 会删除这些
    资源，包括该别名；不要在与其它测试共享同一别名的环境里并发执行。别名由门禁在开始前加、
    结束后删，模拟“外部流量打到原始服务器地址”的入口。
#>
param(
    [int]$IfIndex = 5,
    [string]$Address = "192.168.100.99",
    [int]$PrefixLength = 24,
    [switch]$Remove
)

$ErrorActionPreference = "Stop"

if ($Remove) {
    Get-NetFirewallRule -DisplayName "B6C-ENV-*" -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
    Remove-NetIPAddress -IPAddress $Address -Confirm:$false -ErrorAction SilentlyContinue
    "B6C-ENV-CLEAN address=$Address rules=B6C-ENV-*"
    exit 0
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
    @{ Name = "B6C-ENV-40250U"; Protocol = "UDP"; Port = 40250 },
    @{ Name = "B6C-ENV-40400U"; Protocol = "UDP"; Port = 40400 },
    @{ Name = "B6C-ENV-40400T"; Protocol = "TCP"; Port = 40400 },
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
