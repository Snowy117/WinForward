param(
    [string]$Root = (Join-Path $PSScriptRoot "..\..")
)

# 只做「构建 → 签名 → 安装 → 启动」，给调试与证据采集用；完整门禁见 gate.ps1。

$ErrorActionPreference = "Stop"

Import-Module (Join-Path $PSScriptRoot "WfBuild.psm1") -Force

try {
    $Root = Resolve-WfRoot -Path $Root

    $build = Build-Driver -Root $Root
    $build.Output
    if ($build.ExitCode -ne 0) { throw "驱动构建失败（exit=$($build.ExitCode)）" }

    $install = Install-Driver -Root $Root
    "签名状态：$($install.SignatureStatus)"
    "服务状态：$($install.State)"
    "DRIVER-STATUS=RUNNING"
}
catch {
    $message = $_.Exception.Message
    if ($message.StartsWith('[ENV] ')) {
        "环境错误：$($message.Substring(6))"
        exit 2
    }
    "失败：$message"
    exit 1
}
