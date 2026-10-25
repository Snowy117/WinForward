param(
    [string]$Root = (Join-Path $PSScriptRoot "..\.."),
    [string]$CodeqlHome,
    [string]$Database,
    [string]$Output,
    [string]$Configuration = 'Debug',
    [string]$Platform = 'x64',
    [switch]$SkipDatabase
)

# CodeQL 深度档：为驱动构建 C++ 数据库，跑 microsoft/windows-drivers 的 recommended 套件，输出 SARIF。
# 这是独立档位，不接进 gate.ps1 的默认流程：一次完整运行是分钟级，还要单独安装 CodeQL CLI。
#
# 仓库不附带 CodeQL CLI 与查询包。CLI 的发现顺序是 -CodeqlHome → $env:CODEQL_HOME → PATH；
# 查询包缺失时脚本会自己 codeql pack download。microsoft/cpp-queries 必须显式安装
# （microsoft/windows-drivers 的 suite 依赖它），否则套件解析失败。
#
# 退出码：0 = 分析完成、0 条结果且抽取自检通过；1 = 有结果，或建库/分析/抽取自检失败；2 = 环境/依赖问题。
# 分析后会用 SARIF 的 cpp/extractor/summary 证明抽取成功：successes 必须 > 0、failures 必须为 0。

$ErrorActionPreference = 'Stop'

$modulePath = Join-Path $PSScriptRoot 'WfBuild.psm1'
if (-not (Test-Path -LiteralPath $modulePath -PathType Leaf)) {
    "环境错误：缺少 WfBuild.psm1：$modulePath"
    "CODEQL-RESULT=ENV-MISSING"
    exit 2
}
try {
    Import-Module $modulePath -Force
} catch {
    "环境错误：无法加载 WfBuild.psm1：$($_.Exception.Message)"
    "CODEQL-RESULT=ENV-MISSING"
    exit 2
}

$script:Suite = 'microsoft/windows-drivers:windows-driver-suites/recommended.qls'
$script:RequiredPacks = @(
    @{ Name = 'microsoft/windows-drivers'; Version = '1.10.0' },
    @{ Name = 'microsoft/cpp-queries'; Version = '0.0.5' }
)

# WDK 的 wdm.h 里有 CodeQL 2.27.3 的 C++ 前端解析不了的 __ptr32（design §6 的已知噪声），
# 会让 extractor 以 exit 3 结束、cpp/extractor/summary 记成 0 成功 1 失败。
# 只对提取器前端把该注解定义成空串：真实编译不受影响，数据库才能被证明是完整抽取。
$script:ExtractorOptions = @'
{
  "extractor": {
    "cpp": {
      "frontend_options": ["-D__ptr32="]
    }
  }
}
'@

function New-CodeqlEnvironmentError {
    param([string]$Message)

    # 与 WfBuild.psm1 的 [ENV] 约定一致：环境/依赖缺失以 2 退出，不伪装成断言失败。
    return [System.InvalidOperationException]::new("[ENV] $Message")
}

function Resolve-CodeqlPath {
    param(
        [string]$Root,
        [string]$Value,
        [string]$DefaultRelative
    )

    # 相对路径按仓库根解释，绝对路径原样使用；两种都归一成绝对路径，后面的命令不依赖当前目录。
    $candidate = if ([string]::IsNullOrWhiteSpace($Value)) { $DefaultRelative } else { $Value.Trim() }
    if (-not [System.IO.Path]::IsPathRooted($candidate)) {
        $candidate = Join-Path $Root $candidate
    }
    return [System.IO.Path]::GetFullPath($candidate)
}

function Find-CodeqlBinary {
    param([string]$Directory)

    if ([string]::IsNullOrWhiteSpace($Directory)) { return @() }
    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) { return @() }

    # bundle 在 Windows 上带的是 codeql.cmd；另外几个名字留给别的解压方式与宿主习惯。
    $found = @()
    foreach ($name in @('codeql.cmd', 'codeql.exe', 'codeql.bat', 'codeql')) {
        $candidate = Join-Path $Directory $name
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            $found += $candidate
        }
    }
    return $found
}

function Resolve-Codeql {
    param([string]$HomeDirectory)

    # 显式 -CodeqlHome 优先，并且失败就是失败，不静默回退到 PATH——否则很容易以为在用的是参数指定的 bundle。
    $homes = @()
    if (-not [string]::IsNullOrWhiteSpace($HomeDirectory)) { $homes += @{ Label = '-CodeqlHome'; Path = $HomeDirectory } }
    if (-not [string]::IsNullOrWhiteSpace($env:CODEQL_HOME)) { $homes += @{ Label = 'CODEQL_HOME'; Path = $env:CODEQL_HOME } }

    foreach ($entry in $homes) {
        $directory = $entry.Path
        if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
            throw (New-CodeqlEnvironmentError "$($entry.Label) 指向的目录不存在：$directory")
        }
        # 既接受 bundle 解压后的 codeql 目录（里面直接有 codeql.cmd），也接受它的上一层（bundle 根）。
        foreach ($candidateDirectory in @($directory, (Join-Path $directory 'codeql'))) {
            $found = @(Find-CodeqlBinary -Directory $candidateDirectory)
            if ($found.Count -gt 0) {
                return [pscustomobject]@{ Path = $found[0]; Source = $entry.Label }
            }
        }
        throw (New-CodeqlEnvironmentError "$($entry.Label) 里找不到 codeql.cmd / codeql.exe：$directory。请指向 CodeQL bundle 解压后的那个 codeql 目录。")
    }

    foreach ($name in @('codeql', 'codeql.cmd', 'codeql.exe', 'codeql.bat')) {
        $command = Get-Command -Name $name -ErrorAction SilentlyContinue
        if ($command -and $command.Source -and (Test-Path -LiteralPath $command.Source -PathType Leaf)) {
            return [pscustomobject]@{ Path = $command.Source; Source = 'PATH' }
        }
    }

    throw (New-CodeqlEnvironmentError ('没有找到 CodeQL CLI。仓库不附带 CLI，请任选其一：用 -CodeqlHome 指向 bundle 里的 codeql 目录；' +
        '设置 CODEQL_HOME；或把该目录放进 PATH。'))
}

function Get-ResolvedPacks {
    param([string]$Codeql)

    # resolve packs 会把 bundle 自带、--search-path 与本地包缓存（用户目录下的 .codeql\packages）都列出来。
    # 输出是分组文本：包名一行（scope/name:），下面缩进列出 @版本:。
    $output = & $Codeql resolve packs 2>&1 | Out-String
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw (New-CodeqlEnvironmentError "codeql resolve packs 失败（exit=$exitCode）：`n$($output.Trim())")
    }

    $index = @{}
    $current = $null
    foreach ($line in ($output -split "`r?`n")) {
        $trimmed = $line.Trim()
        if ($trimmed -match '^([A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+):$') {
            $current = $Matches[1]
            if (-not $index.ContainsKey($current)) { $index[$current] = @() }
            continue
        }
        if ($current -and $trimmed -match '^@([^:\s]+):') {
            $index[$current] = @($index[$current]) + $Matches[1]
        }
    }
    return $index
}

function Test-PackInstalled {
    param(
        [hashtable]$Index,
        [string]$Name,
        [string]$Version
    )

    if (-not $Index.ContainsKey($Name)) { return $false }
    return (@($Index[$Name]) -contains $Version)
}

function Install-MissingPacks {
    param(
        [string]$Codeql,
        [hashtable]$Index
    )

    $missing = @()
    foreach ($pack in $script:RequiredPacks) {
        $spec = "$($pack.Name)@$($pack.Version)"
        if (Test-PackInstalled -Index $Index -Name $pack.Name -Version $pack.Version) {
            Write-Host "查询包已安装：$spec"
        } else {
            Write-Host "缺少查询包：$spec"
            $missing += $spec
        }
    }
    if ($missing.Count -eq 0) { return $Index }

    Write-Host "执行 codeql pack download $($missing -join ' ')（首次需要网络）"
    $specs = @($missing)
    $output = & $Codeql pack download @specs 2>&1 | Out-String
    $exitCode = $LASTEXITCODE
    foreach ($line in ($output.Trim() -split "`r?`n")) {
        if ($line.Trim()) { Write-Host "  $($line.TrimEnd())" }
    }
    if ($exitCode -ne 0) {
        throw (New-CodeqlEnvironmentError "查询包下载失败（exit=$exitCode）。命令：codeql pack download $($missing -join ' ')")
    }

    $after = Get-ResolvedPacks -Codeql $Codeql
    foreach ($pack in $script:RequiredPacks) {
        if (-not (Test-PackInstalled -Index $after -Name $pack.Name -Version $pack.Version)) {
            throw (New-CodeqlEnvironmentError ("下载后仍然找不到查询包 $($pack.Name)@$($pack.Version)。" +
                "请运行 codeql resolve packs 检查包缓存与版本；依赖里不带版本时会解析到别的版本。"))
        }
    }
    return $after
}

function Resolve-CodeqlSuite {
    param(
        [string]$Codeql,
        [string]$Suite
    )

    # 先解析一次套件：suite 路径写错或包版本冲突时，在 1 分钟的建库之前就报出来。
    $output = & $Codeql resolve queries $Suite 2>&1 | Out-String
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        Write-Host "套件解析失败：$Suite"
        foreach ($line in ($output.Trim() -split "`r?`n")) { Write-Host "  $line" }
        throw (New-CodeqlEnvironmentError "查询套件无法解析（exit=$exitCode）。先运行 codeql resolve packs 看真实错误与已安装版本。")
    }
    return @($output -split "`r?`n" | Where-Object { $_ -match '\.ql\s*$' }).Count
}

function Write-DatabaseCreateScript {
    param(
        [string]$Path,
        [string]$MSBuild,
        [string]$Project,
        [string[]]$Arguments
    )

    # codeql database create --command 最终由 shell 执行；MSBuild 路径带空格时直接拼字符串很容易在引号上翻车。
    # 固定写一个 .bat：路径由参数拼好、退出码原样返回，顺便留作这次建库命令的现场记录。
    $lines = @(
        '@echo off',
        'rem Generated by tools/windows/codeql.ps1; used as codeql database create --command.',
        ('"{0}" "{1}" {2}' -f $MSBuild, $Project, ($Arguments -join ' ')),
        'exit /b %ERRORLEVEL%'
    )
    $content = ($lines -join "`r`n") + "`r`n"
    [System.IO.File]::WriteAllText($Path, $content, [System.Text.UTF8Encoding]::new($false))
}

function Remove-ExistingDatabase {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) { return }
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw (New-CodeqlEnvironmentError "-Database 指向的不是目录：$Path")
    }
    # database create 要求目标不存在；为了不误删用户目录，只删除“看起来确实是 CodeQL 数据库”的目标。
    $marker = Join-Path $Path 'codeql-database.yml'
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) {
        throw (New-CodeqlEnvironmentError ("数据库目录已存在，但里面没有 codeql-database.yml，不像是 CodeQL 数据库：$Path。" +
            "为安全起见不递归删除它；请换一个 -Database，或手动清理。"))
    }
    Write-Host "删除旧数据库：$Path"
    Remove-Item -LiteralPath $Path -Recurse -Force
}

function Get-DirectorySizeMb {
    param([string]$Path)

    $bytes = (Get-ChildItem -LiteralPath $Path -File -Recurse -ErrorAction SilentlyContinue |
        Measure-Object -Property Length -Sum).Sum
    if (-not $bytes) { return 0 }
    return [math]::Round($bytes / 1MB, 1)
}

function Get-ExtractorSummary {
    param($Runs)

    # CodeQL 的 cpp/extractor/summary 是抽取器自检。真实字段在 properties.attributes 里（带连字符），
    # 不是 properties 顶层；SARIF 里没有这条就无法证明数据库抽到了代码。
    $found = $false
    $successes = 0
    $failures = 0
    foreach ($run in $Runs) {
        foreach ($invocation in @($run.invocations)) {
            foreach ($notification in @($invocation.toolExecutionNotifications)) {
                if (-not $notification.descriptor -or $notification.descriptor.id -ne 'cpp/extractor/summary') { continue }
                $found = $true
                $attributes = $notification.properties.attributes
                if (-not $attributes) { continue }
                if ($null -ne $attributes.'extractor-successes') {
                    $successes += [int]$attributes.'extractor-successes'
                }
                if ($null -ne $attributes.'extractor-failures') {
                    $failures += [int]$attributes.'extractor-failures'
                }
            }
        }
    }
    return [pscustomobject]@{ Found = $found; Successes = $successes; Failures = $failures }
}

function Get-SarifSummary {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "分析结束但没有生成 SARIF：$Path"
    }
    $raw = Get-Content -LiteralPath $Path -Raw
    if ([string]::IsNullOrWhiteSpace($raw)) {
        throw "SARIF 是空文件：$Path"
    }
    try {
        $sarif = $raw | ConvertFrom-Json -ErrorAction Stop
    } catch {
        # 解析失败必须报错：当成 0 条会把坏结果伪装成绿。
        throw "SARIF 解析失败（不代表 0 条结果）：$Path`n$($_.Exception.Message)"
    }
    if (-not $sarif.version) {
        throw "SARIF 结构不完整（缺 version）：$Path"
    }

    $runs = @($sarif.runs)
    if ($runs.Count -eq 0) {
        throw "SARIF 没有 runs：$Path"
    }
    $results = @()
    foreach ($run in $runs) { $results += @($run.results) }

    $groups = @{}
    foreach ($result in $results) {
        $rule = $result.ruleId
        if (-not $rule -and $result.rule -and $result.rule.id) { $rule = $result.rule.id }
        if (-not $rule) { $rule = '(无 ruleId)' }
        if (-not $groups.ContainsKey($rule)) { $groups[$rule] = 0 }
        $groups[$rule]++
    }

    return [pscustomobject]@{
        Runs      = $runs.Count
        Results   = $results
        Groups    = $groups
        Extractor = Get-ExtractorSummary -Runs $runs
    }
}

function Format-SarifLocation {
    param($Result)

    $locations = @($Result.locations)
    if ($locations.Count -eq 0) { return '(无位置)' }
    $physical = $locations[0].physicalLocation
    if (-not $physical) { return '(无位置)' }
    $uri = $physical.artifactLocation.uri
    $line = $physical.region.startLine
    if ($uri -and $line) { return "${uri}:$line" }
    if ($uri) { return $uri }
    return '(无位置)'
}

function Format-SarifMessage {
    param($Result)

    $text = $Result.message.text
    if (-not $text) { return '' }
    $single = ($text -split "`r?`n")[0].Trim()
    if ($single.Length -gt 160) { $single = $single.Substring(0, 157) + '...' }
    return $single
}

$script:exitCode = 0
$script:resultCount = 0
$verdict = 'PASS'

try {
    $Root = Resolve-WfRoot -Path $Root

    $Database = Resolve-CodeqlPath -Root $Root -Value $Database -DefaultRelative 'build\codeql\db'
    $Output = Resolve-CodeqlPath -Root $Root -Value $Output -DefaultRelative 'build\codeql\wf.sarif'
    $outputDirectory = Split-Path -Parent $Output
    if (-not $outputDirectory) {
        throw (New-CodeqlEnvironmentError "-Output 需要是文件路径：$Output")
    }

    # 重建数据库会删掉 -Database 目录；输出文件绝对不能落在里面。
    $databasePrefix = $Database.TrimEnd('\') + '\'
    if ($Output.StartsWith($databasePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw (New-CodeqlEnvironmentError "-Output 不能放在 -Database 目录里（重建数据库时会被删掉）：$Output")
    }
    foreach ($value in @($Configuration, $Platform)) {
        if ($value -match '[\s"]') {
            throw (New-CodeqlEnvironmentError "-Configuration / -Platform 不应包含空白或引号：$value")
        }
    }

    "===== 发现 CodeQL CLI ====="
    $codeqlInfo = Resolve-Codeql -Home $CodeqlHome
    $codeql = $codeqlInfo.Path
    $cliVersion = (& $codeql version --format=terse 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw (New-CodeqlEnvironmentError "CodeQL CLI 无法执行（exit=$LASTEXITCODE）：$codeql")
    }
    "CodeQL CLI：$codeql（$cliVersion，来自 $($codeqlInfo.Source)）"

    "===== 检查查询包 ====="
    $packIndex = Get-ResolvedPacks -Codeql $codeql
    $packIndex = Install-MissingPacks -Codeql $codeql -Index $packIndex

    "===== 解析查询套件 ====="
    $queryCount = Resolve-CodeqlSuite -Codeql $codeql -Suite $script:Suite
    "套件：$($script:Suite)（$queryCount 条查询）"

    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Database) | Out-Null

    if ($SkipDatabase) {
        $marker = Join-Path $Database 'codeql-database.yml'
        if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) {
            throw (New-CodeqlEnvironmentError "-SkipDatabase 需要已有数据库，但 $Database 里没有 codeql-database.yml。")
        }
        "复用数据库：$Database"
    } else {
        "===== 建库（追踪 MSBuild 构建）====="
        $msbuild = Resolve-MSBuild
        $project = Join-Path $Root 'src\driver\wfprobe.vcxproj'
        if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
            throw (New-CodeqlEnvironmentError "驱动工程不存在：$project")
        }
        Remove-ExistingDatabase -Path $Database

        # 干净同步的树缺 NuGet 资产时会报 MSB8036，所以 -restore 不能省；
        # -t:Rebuild 保证编译器真的被调用，否则增量构建可能什么也不编译、提取器抓不到代码。
        $msbuildArguments = @(
            '-restore',
            '-nologo',
            '-t:Rebuild',
            "-p:Configuration=$Configuration",
            "-p:Platform=$Platform",
            '-v:m'
        )
        $buildScript = Join-Path $outputDirectory 'database-create.bat'
        Write-DatabaseCreateScript -Path $buildScript -MSBuild $msbuild -Project $project -Arguments $msbuildArguments
        $extractorOptionsPath = Join-Path $outputDirectory 'extractor-options.json'
        [System.IO.File]::WriteAllText($extractorOptionsPath, $script:ExtractorOptions, [System.Text.UTF8Encoding]::new($false))
        "建库命令：$buildScript"
        "提取器选项：$extractorOptionsPath"
        "  $msbuild $project $($msbuildArguments -join ' ')"

        $createCommand = '"' + $buildScript + '"'
        $createArguments = @(
            'database', 'create', $Database,
            '--language=cpp',
            "--source-root=$Root",
            "--command=$createCommand",
            "--extractor-options-file=$extractorOptionsPath"
        )
        $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        $createOutput = @()
        & $codeql @createArguments 2>&1 | Tee-Object -Variable createOutput | Out-Host
        $createExitCode = $LASTEXITCODE
        $stopwatch.Stop()
        $createText = (@($createOutput) | ForEach-Object { "$_" }) -join "`n"
        Set-Content -LiteralPath (Join-Path $outputDirectory 'database-create.log') -Value $createText -Encoding utf8
        if ($createExitCode -ne 0) {
            throw "建库失败（exit=$createExitCode）。完整输出见 $(Join-Path $outputDirectory 'database-create.log')"
        }
        "建库耗时：$([math]::Round($stopwatch.Elapsed.TotalSeconds, 1)) s；数据库：$Database（$(Get-DirectorySizeMb -Path $Database) MB）"
    }

    "===== 分析 ====="
    $analyzeArguments = @(
        'database', 'analyze', $Database,
        $script:Suite,
        '--format=sarif-latest',
        "--output=$Output"
    )
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $analyzeOutput = @()
    & $codeql @analyzeArguments 2>&1 | Tee-Object -Variable analyzeOutput | Out-Host
    $analyzeExitCode = $LASTEXITCODE
    $stopwatch.Stop()
    $analyzeText = (@($analyzeOutput) | ForEach-Object { "$_" }) -join "`n"
    Set-Content -LiteralPath (Join-Path $outputDirectory 'analyze.log') -Value $analyzeText -Encoding utf8
    if ($analyzeExitCode -ne 0) {
        throw "分析失败（exit=$analyzeExitCode）。完整输出见 $(Join-Path $outputDirectory 'analyze.log')"
    }
    "分析耗时：$([math]::Round($stopwatch.Elapsed.TotalSeconds, 1)) s"

    "===== 结果摘要 ====="
    $summary = Get-SarifSummary -Path $Output
    $script:resultCount = $summary.Results.Count
    $sarifSizeKb = [math]::Round((Get-Item -LiteralPath $Output).Length / 1KB, 1)
    "SARIF：$Output（$sarifSizeKb KB，$($summary.Runs) 个 run）"
    "结果条数：$($summary.Results.Count)"

    $extractor = $summary.Extractor
    "抽取校验：extractor-successes=$($extractor.Successes)，extractor-failures=$($extractor.Failures)"
    if (-not $extractor.Found) {
        throw "SARIF 里没有 cpp/extractor/summary，无法证明抽取成功，结果不可信。"
    }
    if ($extractor.Failures -gt 0) {
        throw "数据库不完整，结果不可信（extractor-failures=$($extractor.Failures)）。"
    }
    if ($extractor.Successes -le 0) {
        throw "extractor-successes 为 0，数据库没有抽到代码，结果不可信。"
    }
    "抽取校验通过。"

    if ($summary.Results.Count -eq 0) {
        "没有结果（基线）。"
    } else {
        "按规则分组："
        foreach ($group in ($summary.Groups.GetEnumerator() | Sort-Object -Property Value -Descending)) {
            "  {0,5}  {1}" -f $group.Value, $group.Key
        }
        "结果位置（最多列出 50 条）："
        $shown = 0
        foreach ($result in $summary.Results) {
            if ($shown -ge 50) {
                "  ...（共 $($summary.Results.Count) 条，只列出前 50 条）"
                break
            }
            $rule = $result.ruleId
            if (-not $rule) { $rule = '(无 ruleId)' }
            "  $rule $(Format-SarifLocation -Result $result) $(Format-SarifMessage -Result $result)"
            $shown++
        }
        $verdict = 'FINDINGS'
        $script:exitCode = 1
    }
} catch {
    $message = $_.Exception.Message
    if ($message.StartsWith('[ENV] ')) {
        "环境错误：$($message.Substring(6))"
        $verdict = 'ENV-MISSING'
        $script:exitCode = 2
    } else {
        "失败：$message"
        $verdict = 'FAIL'
        $script:exitCode = 1
    }
}

if ($verdict -eq 'FINDINGS') {
    "CODEQL-RESULT=FINDINGS results=$script:resultCount"
} else {
    "CODEQL-RESULT=$verdict"
}
exit $script:exitCode
