#requires -Version 7.0
<#
.SYNOPSIS
    Linux 门禁调用的 suite 启动器：同步运行 wfprobe 并把 stdout 写到指定文件。
.DESCRIPTION
    由 tools/linux/forwarded_gate.py 上传后调用。先打印 SUITE-PID，等待进程退出后打印
    SUITE-EXIT=<code>；UTF-8 输出重定向到 -Out，错误写到 <Out>.err。
#>
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Out,
    [Parameter(Mandatory = $true)][string]$ArgumentLine
)
$arguments = $ArgumentLine -split '\s+'
$process = Start-Process -FilePath $Exe -ArgumentList $arguments -RedirectStandardOutput $Out `
    -RedirectStandardError "$Out.err" -PassThru -NoNewWindow
"SUITE-PID=$($process.Id)"
$process.WaitForExit()
"SUITE-EXIT=$($process.ExitCode)"
