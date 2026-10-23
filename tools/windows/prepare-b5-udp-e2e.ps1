param(
    [int]$IfIndex = 5,
    [string]$Address = "192.168.100.99",
    [int]$PrefixLength = 24,
    [switch]$Cleanup
)

$ErrorActionPreference = "Stop"

if ($Cleanup) {
    Get-NetFirewallRule -DisplayName "B5-ENV-*" -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
    Remove-NetIPAddress -IPAddress $Address -Confirm:$false -ErrorAction SilentlyContinue
    netsh interface ip delete address name="Internal" addr=$Address | Out-Null
    Write-Output "B5-ENV-CLEAN address=$Address rules=B5-ENV-*"
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
if ($ip.AddressState -ne "Preferred") {
    throw "alias $Address not Preferred: $($ip.AddressState)"
}

foreach ($rule in @(
    @{ Name = "B5-ENV-40250U"; Protocol = "UDP"; Port = 40250 },
    @{ Name = "B5-ENV-40400U"; Protocol = "UDP"; Port = 40400 },
    @{ Name = "B5-ENV-41400T"; Protocol = "TCP"; Port = 41400 }
)) {
    Remove-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue
    New-NetFirewallRule -DisplayName $rule.Name -Direction Inbound -Protocol $rule.Protocol `
        -LocalPort $rule.Port -Action Allow -Profile Any | Out-Null
}

Write-Output "B5-ENV-READY address=$Address ifIndex=$IfIndex state=$($ip.AddressState) prefix=$($ip.PrefixLength) skipAsSource=$($ip.SkipAsSource)"
Write-Output "下一步：启动 wfprobe suite，等到 READY；再启动 Linux 客户端（client 会等待 verify 端口可连接）"
