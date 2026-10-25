#requires -Version 7.0
<#
.SYNOPSIS
    B6b forwarded TCP/UDP E2E 的 Windows 侧准备：只放行测试端口的入站连接。
.DESCRIPTION
    B6b 的多入口/多 L 测试由 Linux 客户端主动连 VM 的 listener 端口；Windows 防火墙默认拦截。
    本脚本可重复执行：-Remove 删除本脚本创建的规则。别名（192.168.100.98 / 192.168.77.99）由
    wfprobe 套件在运行时添加并清理，不在这里持久化。
#>
param([switch]$Remove)

$tcpPorts = @(40400..40430) + @(40999)
$udpPorts = @(40250) + @(40400..40430)

if ($Remove)
{
    Get-NetFirewallRule -DisplayName 'wf-b6b-*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    'B6B-PREPARE=REMOVED'
    exit 0
}

Get-NetFirewallRule -DisplayName 'wf-b6b-*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName 'wf-b6b-tcp-in' -Direction Inbound -Protocol TCP `
    -LocalPort $tcpPorts -Action Allow -Profile Any | Out-Null
New-NetFirewallRule -DisplayName 'wf-b6b-udp-in' -Direction Inbound -Protocol UDP `
    -LocalPort $udpPorts -Action Allow -Profile Any | Out-Null
'B6B-PREPARE=OK'
