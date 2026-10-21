# WfBuild.psm1 — Windows 侧脚本共享的构建、签名、安装与静态分析动作。
#
# 这里只回答“在这台装有 Visual Studio Build Tools + WDK 的 Windows 机器上跑什么”：
# MSBuild、signtool、accesschk 的安装位置全部靠发现，不写盘符、IP 或版本号路径。
# 编排与断言留在各脚本里（gate.ps1 / mutation.ps1 / load-driver.ps1 / ...）。
#
# 依赖的机器前置条件（脚本只发现、不安装）：
#   - Visual Studio Build Tools（MSBuild + MSVC + WDK）
#   - Windows SDK（x64 signtool）
#   - LocalMachine\My 中的测试证书（Subject 含 WinForward Test Driver）
#   - Sysinternals accesschk（PATH、WF_ACCESSCHK 或系统盘一层目录内）

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:ServiceName = 'wfprobe'
$script:MSBuildPath = $null
$script:SignToolPath = $null
$script:AccessChkPath = $null

function New-WfEnvironmentError {
    param([string]$Message)

    # 环境/前置条件缺失：调用方据此以 2 退出，而不是把环境问题伪装成断言失败。
    return [System.InvalidOperationException]::new("[ENV] $Message")
}
function Get-WfServiceName {
    <#
    .SYNOPSIS
        驱动服务名；调用方不要各自写死。
    #>
    [CmdletBinding()]
    param()

    return $script:ServiceName
}

function Resolve-WfRoot {
    <#
    .SYNOPSIS
        把 -Root 归一化成绝对路径；脚本默认值是从 $PSScriptRoot 推到仓库根。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw (New-WfEnvironmentError "仓库根目录不存在：$Path")
    }
    return (Resolve-Path -LiteralPath $Path).Path
}

function Resolve-Remote {
    <#
    .SYNOPSIS
        解析测试目标地址：-Remote → $env:WF_REMOTE → 报错。

    .DESCRIPTION
        这是“被测的那台机器/服务”的地址，不是本机地址，也没有默认值。
        返回规范化的 <主机:端口> 字符串。
    #>
    [CmdletBinding()]
    param(
        [string]$Value,
        [string]$EnvironmentVariable = 'WF_REMOTE'
    )

    $candidate = $Value
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        $candidate = [Environment]::GetEnvironmentVariable($EnvironmentVariable)
    }
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        throw (New-WfEnvironmentError ("缺少测试目标地址：请用 -Remote <主机:端口> 指定，或设置环境变量 $EnvironmentVariable。" +
            "这是测试目标（通常是运行回显/代理服务的另一台机器），不是本机地址。"))
    }

    $candidate = $candidate.Trim()
    if ($candidate -notmatch '^(?<host>[^:]+):(?<port>[0-9]{1,5})$') {
        throw (New-WfEnvironmentError "测试目标地址格式应为 <主机:端口>，收到：$candidate")
    }
    $port = [int]$Matches['port']
    if ($port -lt 1 -or $port -gt 65535) {
        throw (New-WfEnvironmentError "测试目标端口越界：$port")
    }
    return "$($Matches['host']):$port"
}

function Resolve-MSBuild {
    <#
    .SYNOPSIS
        用 vswhere 发现 MSBuild.exe。

    .DESCRIPTION
        必须带 -products *：VS 18 的 Build Tools 在默认产品过滤下不会出现在结果里。
    #>
    [CmdletBinding()]
    param(
        [string]$VSWhere
    )

    if ($script:MSBuildPath -and (Test-Path -LiteralPath $script:MSBuildPath -PathType Leaf)) {
        return $script:MSBuildPath
    }

    $candidates = New-Object System.Collections.Generic.List[string]
    if ($VSWhere) { $candidates.Add($VSWhere) }
    if ($env:VSWHERE) { $candidates.Add($env:VSWHERE) }
    $command = Get-Command -Name vswhere.exe -ErrorAction SilentlyContinue
    if ($command -and $command.Source) { $candidates.Add($command.Source) }
    if (${env:ProgramFiles(x86)}) {
        $candidates.Add((Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'))
    }
    if ($env:ProgramFiles) {
        $candidates.Add((Join-Path $env:ProgramFiles 'Microsoft Visual Studio\Installer\vswhere.exe'))
    }

    $vsWherePath = $candidates |
        Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } |
        Select-Object -First 1
    if (-not $vsWherePath) {
        throw (New-WfEnvironmentError "找不到 vswhere.exe。请安装 Visual Studio Build Tools（含 MSBuild），或把 vswhere 放进 PATH / 设置 VSWHERE。")
    }

    $found = @(& $vsWherePath -latest -products '*' -find 'MSBuild\**\Bin\MSBuild.exe' 2>$null)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw (New-WfEnvironmentError "vswhere 执行失败（exit=$exitCode）：$vsWherePath")
    }
    $found = @($found |
        ForEach-Object { "$_".Trim() } |
        Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) })
    if ($found.Count -eq 0) {
        throw (New-WfEnvironmentError "vswhere 没有找到 MSBuild.exe。请确认 Build Tools 安装了 MSBuild。")
    }

    $script:MSBuildPath = $found[0]
    return $script:MSBuildPath
}

function Resolve-SignTool {
    <#
    .SYNOPSIS
        从 Windows Kits 的版本目录里挑最高版本的 x64 signtool.exe。
    #>
    [CmdletBinding()]
    param(
        [string]$KitsBinRoot
    )

    if ($script:SignToolPath -and (Test-Path -LiteralPath $script:SignToolPath -PathType Leaf)) {
        return $script:SignToolPath
    }

    $roots = New-Object System.Collections.Generic.List[string]
    if ($KitsBinRoot) { $roots.Add($KitsBinRoot) }
    if (${env:ProgramFiles(x86)}) {
        $roots.Add((Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'))
    }
    if ($env:ProgramFiles) {
        $roots.Add((Join-Path $env:ProgramFiles 'Windows Kits\10\bin'))
    }

    $found = @()
    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
        foreach ($versionDir in @(Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue)) {
            $candidate = Join-Path $versionDir.FullName 'x64\signtool.exe'
            if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
            $version = [version]'0.0.0.0'
            [void][version]::TryParse($versionDir.Name, [ref]$version)
            $found += [pscustomobject]@{
                Path    = $candidate
                Version = $version
                Name    = $versionDir.Name
            }
        }
    }

    if ($found.Count -eq 0) {
        throw (New-WfEnvironmentError "找不到 signtool.exe。需要安装 Windows SDK（Windows Kits\10\bin\<版本>\x64\signtool.exe）。")
    }
    $script:SignToolPath = ($found |
        Sort-Object -Property Version, Name -Descending |
        Select-Object -First 1).Path
    return $script:SignToolPath
}

function Resolve-AccessChk {
    <#
    .SYNOPSIS
        发现 Sysinternals accesschk（设备对象 DACL 的检查工具）。
    #>
    [CmdletBinding()]
    param(
        [string]$Path
    )

    if ($script:AccessChkPath -and (Test-Path -LiteralPath $script:AccessChkPath -PathType Leaf)) {
        return $script:AccessChkPath
    }

    $candidates = New-Object System.Collections.Generic.List[string]
    if ($Path) { $candidates.Add($Path) }
    if ($env:WF_ACCESSCHK) { $candidates.Add($env:WF_ACCESSCHK) }
    foreach ($name in @('accesschk64.exe', 'accesschk.exe')) {
        $command = Get-Command -Name $name -ErrorAction SilentlyContinue
        if ($command -and $command.Source) { $candidates.Add($command.Source) }
    }

    # Sysinternals 经常被解压到系统盘根目录下的独立文件夹。不假设文件夹名字，只向下扫一层。
    $searchRoots = @()
    if ($env:SystemDrive) { $searchRoots += "$($env:SystemDrive)\" }
    if ($env:ProgramFiles) { $searchRoots += $env:ProgramFiles }
    if (${env:ProgramFiles(x86)}) { $searchRoots += ${env:ProgramFiles(x86)} }
    if ($env:ProgramData) { $searchRoots += $env:ProgramData }
    foreach ($root in $searchRoots) {
        if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
        foreach ($dir in @(Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue)) {
            $candidates.Add((Join-Path $dir.FullName 'accesschk64.exe'))
            $candidates.Add((Join-Path $dir.FullName 'accesschk.exe'))
        }
    }

    $found = $candidates |
        Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } |
        Select-Object -First 1
    if (-not $found) {
        throw (New-WfEnvironmentError "找不到 accesschk（Sysinternals）。请把它放进 PATH 或设置 WF_ACCESSCHK。")
    }

    $script:AccessChkPath = (Resolve-Path -LiteralPath $found).Path
    return $script:AccessChkPath
}

function Resolve-WfTool {
    <#
    .SYNOPSIS
        找到 wfprobe 的 Release 可执行文件（不写死目标框架目录名）。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Root,
        [string]$Name = 'wfprobe'
    )

    $Root = Resolve-WfRoot -Path $Root
    $base = Join-Path $Root "tools\$Name\bin\Release"
    if (-not (Test-Path -LiteralPath $base -PathType Container)) {
        throw (New-WfEnvironmentError "没有找到 $Name 的 Release 构建目录：$base。请先 dotnet build ... -c Release。")
    }
    $exe = Get-ChildItem -LiteralPath $base -Filter "$Name.exe" -File -Recurse -ErrorAction SilentlyContinue |
        Sort-Object -Property LastWriteTime -Descending |
        Select-Object -First 1
    if (-not $exe) {
        throw (New-WfEnvironmentError "Release 目录里没有 $Name.exe：$base")
    }
    return $exe.FullName
}

function Build-Driver {
    <#
    .SYNOPSIS
        构建 wfprobe.sys（Debug|x64），返回输出与退出码，由调用方决定是否断言。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Root,
        [string]$Configuration = 'Debug',
        [string]$Platform = 'x64',
        [switch]$Rebuild
    )

    $Root = Resolve-WfRoot -Path $Root
    $project = Join-Path $Root 'src\driver\wfprobe.vcxproj'
    if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
        throw (New-WfEnvironmentError "驱动工程不存在：$project")
    }

    $msbuild = Resolve-MSBuild
    $arguments = @(
        $project,
        '-restore',
        '-nologo',
        "-p:Configuration=$Configuration",
        "-p:Platform=$Platform",
        '-v:m'
    )
    if ($Rebuild) { $arguments += '-t:Rebuild' }

    $output = & $msbuild @arguments 2>&1 | Out-String
    $exitCode = $LASTEXITCODE
    return [pscustomobject]@{
        Output   = $output
        ExitCode = $exitCode
        Project  = $project
        Binary   = Join-Path $Root "src\driver\$Platform\$Configuration\wfprobe.sys"
    }
}

function Install-Driver {
    <#
    .SYNOPSIS
        签名、复制到 System32\drivers、创建服务并等它进入 RUNNING。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Root,
        [string]$Configuration = 'Debug',
        [string]$Platform = 'x64',
        [string]$ServiceName = $script:ServiceName
    )

    $Root = Resolve-WfRoot -Path $Root
    $binary = Join-Path $Root "src\driver\$Platform\$Configuration\wfprobe.sys"
    if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) {
        throw (New-WfEnvironmentError "驱动没有构建：$binary")
    }

    $certificate = Get-ChildItem Cert:\LocalMachine\My |
        Where-Object { $_.Subject -like '*WinForward Test Driver*' } |
        Sort-Object -Property NotAfter -Descending |
        Select-Object -First 1
    if (-not $certificate) {
        throw (New-WfEnvironmentError "找不到测试证书：LocalMachine\My 中没有 Subject 含 WinForward Test Driver 的证书。")
    }

    $signTool = Resolve-SignTool
    & $signTool sign /fd SHA256 /sha1 $certificate.Thumbprint /v $binary | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "签名失败（signtool exit=$LASTEXITCODE）：$binary"
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $binary

    $target = Join-Path $env:SystemRoot "System32\drivers\$ServiceName.sys"
    $started = $false
    $lastDetail = ''

    # 同一台机器可能被多个批次共用：服务名和驱动文件路径是固定的，失败时重试并留下 sc 输出。
    for ($installAttempt = 0; $installAttempt -lt 3 -and -not $started; $installAttempt++) {
        sc.exe stop $ServiceName 2>$null | Out-Null
        sc.exe delete $ServiceName 2>$null | Out-Null
        for ($attempt = 0; $attempt -lt 40; $attempt++) {
            $text = sc.exe query $ServiceName 2>$null | Out-String
            if ($text -match 'STOPPED' -or $text -match '1060') { break }
            Start-Sleep -Milliseconds 250
        }

        Copy-Item -LiteralPath $binary -Destination $target -Force
        if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
            throw "驱动文件复制失败：$target"
        }
        $createOutput = sc.exe create $ServiceName type= kernel start= demand binPath= $target 2>&1 | Out-String
        $startOutput = sc.exe start $ServiceName 2>&1 | Out-String
        Start-Sleep -Seconds 1
        $query = sc.exe query $ServiceName 2>&1 | Out-String
        if ($query -match 'RUNNING') {
            $started = $true
            break
        }

        $lastDetail = "create=$($createOutput.Trim()) start=$($startOutput.Trim()) query=$($query.Trim())"
        sc.exe delete $ServiceName 2>$null | Out-Null
        Start-Sleep -Milliseconds 500
    }
    if (-not $started) {
        throw "驱动无法进入 RUNNING（$lastDetail）"
    }

    $state = ((sc.exe query $ServiceName 2>&1 | Select-String 'STATE') | Select-Object -First 1).Line
    return [pscustomobject]@{
        Service         = $ServiceName
        Binary          = $target
        State           = $state.Trim()
        SignatureStatus = $signature.Status
    }
}

function Uninstall-Driver {
    <#
    .SYNOPSIS
        停服务、删服务、删 Sys32 下的驱动文件；尽力而为，不掩盖调用方的失败。
    #>
    [CmdletBinding()]
    param(
        [string]$ServiceName = $script:ServiceName,
        [string]$ToolExe
    )

    if ($ToolExe -and (Test-Path -LiteralPath $ToolExe -PathType Leaf)) {
        & $ToolExe control --enabled 0 2>$null | Out-Null
    }

    sc.exe stop $ServiceName 2>$null | Out-Null
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        $text = sc.exe query $ServiceName 2>$null | Out-String
        if ($text -match 'STOPPED' -or $text -match '1060') { break }
        Start-Sleep -Milliseconds 250
    }
    sc.exe delete $ServiceName 2>$null | Out-Null
    Start-Sleep -Milliseconds 500
    Remove-Item -LiteralPath (Join-Path $env:SystemRoot "System32\drivers\$ServiceName.sys") `
        -Force -ErrorAction SilentlyContinue
}

function Invoke-Prefast {
    <#
    .SYNOPSIS
        跑 MSVC PREfast 分析构建，并解析输出判断是否通过。

    .DESCRIPTION
        /WX 不会把 PREfast 告警提升为错误，所以不能只看退出码：
        出现 warning C28xxx，或 -clp:Summary 的 Warning(s) 非零，都算失败。
        输出里没有 'Running Code Analysis'（分析被静默跳过）也算失败。
        -restore 不能省：干净同步后缺少 NuGet 资产会报 MSB8036；
        PreferredToolArchitecture=x64 不能省：x86 宿主找不到 WDK 的 x86 Prefast 插件（C1250）。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Root,
        [string]$Configuration = 'Debug',
        [string]$Platform = 'x64'
    )

    $Root = Resolve-WfRoot -Path $Root
    $project = Join-Path $Root 'src\driver\wfprobe.vcxproj'
    if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
        throw (New-WfEnvironmentError "驱动工程不存在：$project")
    }

    $msbuild = Resolve-MSBuild
    $arguments = @(
        $project,
        '-restore',
        '-nologo',
        '-t:Rebuild',
        "-p:Configuration=$Configuration",
        "-p:Platform=$Platform",
        '-p:RunCodeAnalysis=true',
        '-p:EnableMicrosoftCodeAnalysis=true',
        '-p:PreferredToolArchitecture=x64',
        '-v:m',
        '-clp:Summary'
    )

    $output = & $msbuild @arguments 2>&1 | Out-String
    $exitCode = $LASTEXITCODE

    $analysisWarnings = [regex]::Matches($output, '(?i)warning\s+C28[0-9]{3}\s*:').Count
    $summaryWarnings = -1
    $summaryMatch = [regex]::Match($output, '(?im)^\s*([0-9]+)\s+Warning\(s\)\s*$')
    if ($summaryMatch.Success) {
        $summaryWarnings = [int]$summaryMatch.Groups[1].Value
    }

    $analysisRan = $output -match 'Running Code Analysis'
    $success = ($exitCode -eq 0) -and $analysisRan -and ($analysisWarnings -eq 0) -and ($summaryWarnings -eq 0)
    return [pscustomobject]@{
        Output           = $output
        ExitCode         = $exitCode
        AnalysisRan      = $analysisRan
        AnalysisWarnings = $analysisWarnings
        SummaryWarnings  = $summaryWarnings
        Success          = $success
    }
}

function Invoke-WfSuite {
    <#
    .SYNOPSIS
        运行一个 wfprobe 套件，输出走主机，只返回退出码。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Root,
        [Parameter(Mandatory)]
        [string[]]$Arguments,
        [string]$ToolExe
    )

    if (-not $ToolExe) {
        $ToolExe = Resolve-WfTool -Root $Root -Name 'wfprobe'
    }
    if (-not (Test-Path -LiteralPath $ToolExe -PathType Leaf)) {
        throw (New-WfEnvironmentError "测试程序不存在：$ToolExe")
    }

    # 用 Write-Host 而不是管道输出：函数只把退出码返回给调用方，套件自己的输出直接进主机。
    & $ToolExe @Arguments 2>&1 | ForEach-Object { Write-Host $_ }
    $exitCode = $LASTEXITCODE
    return $exitCode
}

Export-ModuleMember -Function @(
    'Resolve-WfRoot',
    'Resolve-Remote',
    'Resolve-MSBuild',
    'Resolve-SignTool',
    'Resolve-AccessChk',
    'Resolve-WfTool',
    'Build-Driver',
    'Install-Driver',
    'Uninstall-Driver',
    'Invoke-Prefast',
    'Invoke-WfSuite',
    'Get-WfServiceName'
)
