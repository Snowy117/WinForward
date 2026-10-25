param(
    [string]$Root = (Join-Path $PSScriptRoot "..\.."),
    [string]$Remote,
    [int]$DummyRulePort = 65000
)

$ErrorActionPreference = "Stop"

Import-Module (Join-Path $PSScriptRoot "WfBuild.psm1") -Force

try {
    $Root = Resolve-WfRoot -Path $Root
    $Remote = Resolve-Remote -Value $Remote
}
catch {
    "环境错误：$($_.Exception.Message -replace '^\[ENV\] ', '')"
    "firewall-check verdict: ENV-MISSING"
    exit 2
}

try {
    $exe = Resolve-WfTool -Root $Root -Name "wfprobe"
}
catch {
    $message = $_.Exception.Message
    if ($message.StartsWith('[ENV] ')) {
        "环境错误：$($message.Substring(6))"
        "firewall-check verdict: ENV-MISSING"
        exit 2
    }
    throw
}

$ruleName = "wfprobe-firewall-check"
Remove-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
$script:failures = 0
$script:result = 1
function Assert([string]$name, [bool]$condition, [string]$detail) {
    if ($condition) { "ASSERT PASS: $name ($detail)" } else { $script:failures++; "ASSERT FAIL: $name ($detail)" }
}

function Invoke-Probe([string]$label) {
    $line = (& $exe blockprobe --rule-port $DummyRulePort --remote $Remote | Select-String "blockprobe").Line
    if (-not $line) { throw "blockprobe 没有输出" }
    "$label -> $line"
    return $line
}

try {
    $profiles = Get-NetFirewallProfile | Select-Object Name, Enabled
    foreach ($profile in $profiles) { "firewall profile: $($profile.Name) enabled=$($profile.Enabled)" }
    $enabledProfiles = ($profiles | Where-Object { $_.Enabled }).Count
    Assert "至少一个防火墙配置文件已启用" ($enabledProfiles -gt 0) "enabled=$enabledProfiles"

    # 1) 基线：我们的 auth filter 处于活动状态（blob 里有 block 规则），目标端口不在规则里，
    #    连接应该成功 —— 这证明第 2 步的变化来自防火墙，而不是我们的驱动。
    $baseline = @(Invoke-Probe "baseline")[-1]
    Assert "无防火墙规则时连接成功" ($baseline -match "connected=True") $baseline

    # 2) 关键安全实验：防火墙拒绝必须仍然生效。此刻 ALE_AUTH_CONNECT callout 是活动的，
    #    对这条连接返回 PERMIT；若 terminating PERMIT 截胡了防火墙的拒绝，这条断言会失败。
    $address = $Remote.Split(':')[0]
    $port = [int]$Remote.Split(':')[1]
    New-NetFirewallRule -DisplayName $ruleName -Direction Outbound -Action Block -Protocol TCP `
        -RemoteAddress $address -RemotePort $port -Profile Any | Out-Null
    Start-Sleep -Seconds 2
    $blocked = @(Invoke-Probe "with-firewall-block")[-1]
    Assert "防火墙拒绝仍然生效（未被子层 PERMIT 截胡）" ($blocked -match "connected=False") $blocked

    # 3) 反向对照：撤掉防火墙规则后连接恢复成功。
    Remove-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    $after = @(Invoke-Probe "after-firewall-removed")[-1]
    Assert "撤掉防火墙规则后连接恢复" ($after -match "connected=True") $after

    if ($script:failures -gt 0) {
        "firewall-check verdict: FAIL ($script:failures)"
        $script:result = 1
    } else {
        "firewall-check verdict: PASS"
        $script:result = 0
    }
}
finally {
    Remove-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
    & $exe control --enabled 0 2>$null | Out-Null
}
exit $script:result
