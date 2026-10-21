param(
    [string]$Root = (Join-Path $PSScriptRoot "..\.."),
    [string]$Remote,
    [string[]]$OnlyIndex = @()
)

# 变异测试：把被守的代码改成恒假/改错，确认对应的验证套件变红。
# 每条变异只改一处（或一组必须同时拆掉的闸），跑对应套件，期望退出码非 0。
# 锚点是源码里的区域标记：
#     // <mutation id="...">
#     ...被替换的语句...
#     // </mutation>
# 脚本按 id 定位区域并整体替换，命中数不为 1 立即失败；全部跑完后与运行前内容逐字节比较。
# 构建/签名/安装/卸载走 WfBuild.psm1。

$ErrorActionPreference = "Stop"

# -OnlyIndex 必须按字符串接收：pwsh -File 把参数作为字符串传进来，[int[]] 绑定会把
# "4,7" 按千分位解析成 47，结果一条用例都没跑还报 PASS。这里按逗号/空白拆成整数；
# 空格写法只有在整体加引号（"0 3"）时才会作为单个参数传进来。
$onlyIndexProvided = $PSBoundParameters.ContainsKey('OnlyIndex')
$selectedIndexes = @()
foreach ($entry in $OnlyIndex) {
    if ($null -eq $entry) { continue }
    $text = "$entry".Trim()
    foreach ($piece in ($text -split '[,\s]+')) {
        if ($piece -eq '') { continue }
        $parsedIndex = 0
        if (-not [int]::TryParse($piece, [System.Globalization.NumberStyles]::Integer,
                [System.Globalization.CultureInfo]::InvariantCulture, [ref]$parsedIndex)) {
            "参数错误：-OnlyIndex 需要逗号或空格分隔的整数，收到：$entry"
            "mutation verdict: ERROR"
            exit 2
        }
        $selectedIndexes += $parsedIndex
    }
}
if ($onlyIndexProvided -and $selectedIndexes.Count -eq 0) {
    "参数错误：-OnlyIndex 没有给出任何整数索引"
    "mutation verdict: ERROR"
    exit 2
}

if ($args.Count -gt 0) {
    "参数错误：-OnlyIndex 只接受逗号串（例如 0,3）；检测到多余参数：$($args -join ' ')"
    "mutation verdict: ERROR"
    exit 2
}

Import-Module (Join-Path $PSScriptRoot "WfBuild.psm1") -Force

try {
    $Root = Resolve-WfRoot -Path $Root
    $Remote = Resolve-Remote -Value $Remote
}
catch {
    "环境错误：$($_.Exception.Message -replace '^\[ENV\] ', '')"
    "mutation verdict: ENV-MISSING"
    exit 2
}
$exe = $null

$script:failures = 0
$script:executedMutations = 0
function Report([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) { "MUTATION PASS: $name ($detail)" } else { $script:failures++; "MUTATION FAIL: $name ($detail)" }
}

# 源码按字节快照：恢复时原样写回，全部跑完后再逐字节比较。Get-Content/Set-Content 会做
# 编码与换行归一化，只有字节比较才能证明“运行前后一致”。
$script:sourceSnapshots = @{}
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)

function Get-SourceBytes([string]$Path) {
    if (-not $script:sourceSnapshots.ContainsKey($Path)) {
        $script:sourceSnapshots[$Path] = [System.IO.File]::ReadAllBytes($Path)
    }
    return ,$script:sourceSnapshots[$Path]
}

function Restore-SourceSnapshot([string]$Path) {
    if ($script:sourceSnapshots.ContainsKey($Path)) {
        [System.IO.File]::WriteAllBytes($Path, $script:sourceSnapshots[$Path])
    }
}

function Restore-AllSourceSnapshots {
    foreach ($path in $script:sourceSnapshots.Keys) {
        Restore-SourceSnapshot -Path $path
    }
}

function Test-BytesEqual([byte[]]$First, [byte[]]$Second) {
    if ($First.Length -ne $Second.Length) { return $false }
    for ($index = 0; $index -lt $First.Length; $index++) {
        if ($First[$index] -ne $Second[$index]) { return $false }
    }
    return $true
}

function Assert-SourceUnchanged {
    $changed = @()
    foreach ($path in $script:sourceSnapshots.Keys) {
        $current = [System.IO.File]::ReadAllBytes($path)
        if (-not (Test-BytesEqual -First $script:sourceSnapshots[$path] -Second $current)) {
            $changed += $path
        }
    }
    if ($changed.Count -gt 0) {
        throw "源码没有恢复到运行前状态：$($changed -join '; ')"
    }
    "源码逐字节校验 PASS（$($script:sourceSnapshots.Count) 个文件与运行前一致）"
}

# 按 id 定位唯一区域。返回区域内容（两条标记行之间）的起止下标：BodyStart 是开始标记行之后
# 的下一行行首，CloseStart 是结束标记行的行首；标记行本身不参与替换。
function Find-MutationRegion([string]$Text, [string]$Id) {
    $openPattern = '(?m)^[ \t]*// <mutation id="' + [regex]::Escape($Id) + '">[ \t]*\r?$'
    $openMatches = [regex]::Matches($Text, $openPattern)
    if ($openMatches.Count -ne 1) {
        throw "找不到区域：id=$Id（标记命中 $($openMatches.Count) 次，应为 1）"
    }

    $bodyStart = $openMatches[0].Index + $openMatches[0].Length
    if ($bodyStart -lt $Text.Length -and $Text[$bodyStart] -eq "`n") { $bodyStart++ }

    $closePattern = '(?m)^[ \t]*// </mutation>[ \t]*\r?$'
    $closeMatch = [regex]::Match($Text.Substring($bodyStart), $closePattern)
    if (-not $closeMatch.Success) {
        throw "找不到区域：id=$Id（缺少 // </mutation> 结束标记）"
    }
    $body = $Text.Substring($bodyStart, $closeMatch.Index)
    # 区域不支持嵌套：非贪婪定位会在内层结束标记处截断，宁可立即失败也不改错位置。
    if ([regex]::IsMatch($body, '(?m)^[ \t]*// <mutation id="')) {
        throw "区域标记嵌套（不支持）：id=$Id"
    }

    return [pscustomobject]@{ BodyStart = $bodyStart; CloseStart = $bodyStart + $closeMatch.Index }
}

function Set-MutationRegion([string]$Text, [string]$Id, [string]$New) {
    $region = Find-MutationRegion -Text $Text -Id $Id
    $prefix = $Text.Substring(0, $region.BodyStart)
    $suffix = $Text.Substring($region.CloseStart)
    return $prefix + $New + "`n" + $suffix
}

$mutations = @(
    @{
        Name = "block-filter 不创建（block 规则不再阻断）"
        File = "block_filters.cpp"
        Edits = @(@{
            Id = "block-filter-skip"
            New = "        if (view.rule_count != 0xFFFFFFFFU || selected.action != rule_action::block)"
        })
        Target = @("rules", "--iterations", "3")
    },
    @{
        Name = "redirect 分支退化成放行"
        File = "probe_classify.cpp"
        Edits = @(@{
            Id = "classify-redirect-branch"
            New = "    const bool redirect_wanted = false;"
        })
        Target = @("selftest", "--remote", $Remote, "--iterations", "1")
    },
    @{
        Name = "block 分支退化成放行"
        File = "probe_classify.cpp"
        Edits = @(@{
            Id = "classify-block-branch"
            New = "    const bool block_wanted = false;"
        })
        Target = @("rules", "--iterations", "3")
    },
    @{
        Name = "permit 分支退化成 block"
        File = "probe_classify.cpp"
        Edits = @(@{
            Id = "permit-branch"
            New = (@(
                "    set_block(classify_out);"
                "    probe_increment(state.stats.blocked);"
                "    log_decision(facts,"
                "                 rule_index,"
                "                 result.matched ? probe_decision::blocked_rule : probe_decision::blocked_fallback,"
                "                 probe_action::block,"
                "                 0,"
                "                 redirect_branch::none);"
            ) -join "`n")
        })
        Target = @("rules", "--iterations", "3")
    },
    @{
        Name = "闸 1（监听端口）恒假"
        File = "probe_classify.cpp"
        Edits = @(@{
            Id = "gate-listener-port"
            New = "    const bool listener_port_hit = false;"
        })
        Target = @("gates")
    },
    @{
        Name = "闸 2（redirect state）恒假"
        File = "probe_classify.cpp"
        Edits = @(@{
            Id = "gate-redirect-state"
            New = "    const bool redirect_state_hit = false;"
        })
        Target = @("gates")
    },
    @{
        Name = "闸 4（self-PID）恒假"
        File = "probe_classify.cpp"
        Edits = @(@{
            Id = "gate-self-process"
            New = "    const bool self_process_hit = false;"
        })
        Target = @("gates")
    },
    @{
        Name = "闸 2 + 闸 3 同时拆掉（应收敛成环）"
        File = "probe_classify.cpp"
        Edits = @(
            @{
                Id = "gate-redirect-state"
                New = "    const bool redirect_state_hit = false;"
            },
            @{
                Id = "gate-condition-flags"
                New = "    const bool condition_flags_hit = false;"
            }
        )
        Target = @("gates")
    },
    @{
        Name = "V6 层的 filter 不挂（只挂 V4）"
        File = "probe_driver.cpp"
        Edits = @(@{
            Id = "v6-filter-not-added"
            New = "        status = STATUS_SUCCESS;"
        })
        Target = @("selftest", "--dual", "--remote", $Remote, "--iterations", "1")
    },
    @{
        Name = "非法 blob 被当成成功"
        File = "probe_control.cpp"
        Edits = @(@{
            Id = "invalid-blob-accepted"
            New = (@(
                "    if (table == nullptr)"
                "    {"
                "        probe_increment(state.stats.rejected_blobs);"
                "        return STATUS_SUCCESS;"
                "    }"
            ) -join "`n")
        })
        Target = @("rules", "--iterations", "3")
    },
    @{
        Name = "非 ASCII 进程路径被丢弃（UTF-8 转换退化）"
        File = "process_path.cpp"
        Edits = @(@{
            Id = "process-path-non-ascii"
            New = (@(
                "        if (code_point > 0x7F)"
                "        {"
                "            length = 0;"
                "            target[0] = '\0';"
                "            return;"
                "        }"
                "        std::uint8_t encoded[4]{};"
            ) -join "`n")
        })
        Target = @("rules", "--iterations", "3")
    },
    @{
        Name = "规则顺序冲突检测被绕过"
        File = "rule_table.cpp"
        Edits = @(@{
            Id = "rule-order-conflict"
            New = "    if (table->view.rule_count == 0xFFFFFFFFU && !rule_order_valid(table->view, earlier_rule, blocking_rule))"
        })
        Target = @("rules", "--iterations", "3")
    },
    @{
        Name = "fallback=block 冲突检测被绕过"
        File = "rule_table.cpp"
        Edits = @(@{
            Id = "fallback-block-conflict"
            New = "    if (table->view.rule_count == 0xFFFFFFFFU && table->view.fallback_action == rule_action::block)"
        })
        Target = @("rules", "--iterations", "3")
    },
    @{
        Name = "enabled=0 不撤 block filter"
        File = "probe_control.cpp"
        Edits = @(@{
            Id = "disable-keeps-block-filters"
            New = (@(
                "        if (!next_enabled)"
                "        {"
                "        }"
            ) -join "`n")
        })
        Target = @("rules", "--iterations", "3")
    },
    @{
        Name = "selector UTF-8/511 校验被绕过"
        File = "rule_table.cpp"
        Edits = @(@{
            Id = "selector-utf8-guard"
            New = (@(
                "        if (table->view.rule_count == 0xFFFFFFFFU && selected.process_index != rule_pool_index_any &&"
                "            !valid_utf8_selector(table->view.process_pool + selected.process_index))"
            ) -join "`n")
        })
        Target = @("rules", "--iterations", "3")
    },
    @{
        Name = "permit 计数器记到 block 上"
        File = "probe_classify.cpp"
        Edits = @(@{
            Id = "permit-counter"
            New = "    probe_increment(result.matched ? state.stats.matched_block : state.stats.fallback_block);"
        })
        Target = @("rules", "--iterations", "3")
    },
    @{
        Name = "UDP 协议分支退化成只处理 TCP"
        File = "probe_classify.cpp"
        Edits = @(@{
            Id = "classify-udp-branch"
            New = "    const bool protocol_is_tcp_or_udp = facts.protocol == IPPROTO_TCP;"
        })
        Target = @("udp-rules", "--only", "U1", "--timeout-ms", "5000")
    },
    @{
        Name = "UDP classify 计数只在 TCP 上自增"
        File = "probe_classify.cpp"
        Edits = @(@{
            Id = "udp-classify-counter"
            New = (@(
                "    if (facts.protocol == IPPROTO_TCP)"
                "    {"
                "        probe_increment(state.stats.udp_classify_calls);"
                "    }"
            ) -join "`n")
        })
        Target = @("udp-rules", "--only", "U1", "--timeout-ms", "5000")
    },
    @{
        Name = "UDP permit 计数只在 TCP 上自增"
        File = "probe_classify.cpp"
        Edits = @(@{
            Id = "udp-permit-counter"
            New = (@(
                "void count_udp_permit(probe_state& state, std::uint8_t protocol) noexcept"
                "{"
                "    if (protocol == IPPROTO_TCP)"
                "    {"
                "        probe_increment(state.stats.udp_permitted);"
                "    }"
                "}"
            ) -join "`n")
        })
        Target = @("udp-rules", "--only", "U5", "--timeout-ms", "5000")
    },
    @{
        Name = "UDP block 计数只在 TCP 上自增"
        File = "probe_classify.cpp"
        Edits = @(@{
            Id = "udp-block-counter"
            New = (@(
                "void count_udp_block(probe_state& state, std::uint8_t protocol) noexcept"
                "{"
                "    if (protocol == IPPROTO_TCP)"
                "    {"
                "        probe_increment(state.stats.udp_blocked);"
                "    }"
                "}"
            ) -join "`n")
        })
        Target = @("udp-rules", "--only", "U6", "--timeout-ms", "5000")
    },
    @{
        Name = "UDP redirect 计数只在 TCP 上自增"
        File = "probe_classify.cpp"
        Edits = @(@{
            Id = "udp-redirect-counter"
            New = (@(
                "void count_udp_redirect(probe_state& state, std::uint8_t protocol) noexcept"
                "{"
                "    if (protocol == IPPROTO_TCP)"
                "    {"
                "        probe_increment(state.stats.udp_redirected);"
                "    }"
                "}"
            ) -join "`n")
        })
        Target = @("udp-rules", "--only", "U1", "--timeout-ms", "5000")
    },
    @{
        Name = "any block 只展开 TCP，漏掉 UDP"
        File = "block_filters.cpp"
        Edits = @(@{
            Id = "block-filter-protocol"
            New = (@(
                "    if (protocol == rule_protocol_any)"
                "    {"
                "        return protocol_expansion{ { rule_protocol_tcp, 0 }, 1 };"
                "    }"
            ) -join "`n")
        })
        Target = @("udp-rules", "--only", "U7", "--timeout-ms", "5000", "--ping-host", ($Remote -split ':')[0])
    }

)

# 索引越界是参数错误：以 2 退出，不进入构建/测试。
foreach ($index in $selectedIndexes) {
    if ($index -lt 0 -or $index -ge $mutations.Count) {
        "参数错误：-OnlyIndex 中的 $index 超出用例范围 0..$($mutations.Count - 1)"
        "mutation verdict: ERROR"
        exit 2
    }
}

"===== 先构建一次测试程序 ====="
try {
    & dotnet build (Join-Path $Root "tools\wfprobe\wfprobe.csproj") -c Release -v:q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "测试程序构建失败" }
    $exe = Resolve-WfTool -Root $Root -Name "wfprobe"

    for ($mutationIndex = 0; $mutationIndex -lt $mutations.Count; $mutationIndex++) {
        if ($selectedIndexes.Count -gt 0 -and $selectedIndexes -notcontains $mutationIndex) { continue }
        $script:executedMutations++
        $mutation = $mutations[$mutationIndex]
        $file = Join-Path $Root "src\driver\$($mutation.File)"
        $originalBytes = Get-SourceBytes -Path $file
        $original = $utf8NoBom.GetString($originalBytes)

        try {
            # 先把整条变异的替换文本算完再落盘：id 找不到时源码还没被改过，直接中止整轮。
            $patched = $original
            foreach ($edit in $mutation.Edits) {
                $patched = Set-MutationRegion -Text $patched -Id $edit.Id -New $edit.New
            }
            [System.IO.File]::WriteAllText($file, $patched, $utf8NoBom)

            try {
                $build = Build-Driver -Root $Root
                if ($build.ExitCode -ne 0) { throw "变异后驱动构建失败：$($build.Output)" }
                Install-Driver -Root $Root | Out-Null
                $code = Invoke-WfSuite -Root $Root -ToolExe $exe -Arguments $mutation.Target
                Report $mutation.Name ($code -ne 0) "target-exit=$code"
            }
            catch {
                $message = $_.Exception.Message
                if ($message.StartsWith('[ENV] ')) { throw }
                Report $mutation.Name $false $message
            }
        }
        finally {
            Uninstall-Driver -ToolExe $exe
            Restore-SourceSnapshot -Path $file
        }
    }

    if ($script:executedMutations -eq 0) {
        throw "没有执行任何变异用例（-OnlyIndex=$($selectedIndexes -join ',')），拒绝输出 PASS"
    }

    "===== 恢复源码后的逐字节校验 ====="
    Assert-SourceUnchanged

    "===== 恢复源码后的正向基线（必须全绿）====="
    try {
        $build = Build-Driver -Root $Root
        if ($build.ExitCode -ne 0) { throw "恢复源码后的驱动构建失败：$($build.Output)" }
        Install-Driver -Root $Root | Out-Null
        $baseline = @(
            @{ Name = "rules"; Args = @("rules", "--iterations", "3") },
            @{ Name = "selftest-v4"; Args = @("selftest", "--remote", $Remote, "--iterations", "1") },
            @{ Name = "selftest-dual"; Args = @("selftest", "--dual", "--remote", $Remote, "--iterations", "1") },
            @{ Name = "gates"; Args = @("gates") },
            @{ Name = "udp-rules-restored"; Args = @("udp-rules", "--timeout-ms", "10000", "--ping-host", ($Remote -split ':')[0]) }
        )
        foreach ($case in $baseline) {
            $code = Invoke-WfSuite -Root $Root -ToolExe $exe -Arguments $case.Args
            Report "baseline-$($case.Name)" ($code -eq 0) "target-exit=$code"
        }
    }
    finally {
        Uninstall-Driver -ToolExe $exe
    }

    "===== 基线之后的逐字节校验 ====="
    Assert-SourceUnchanged
}
catch {
    $message = $_.Exception.Message
    if ($message.StartsWith('[ENV] ')) {
        "环境错误：$($message.Substring(6))"
        "mutation verdict: ENV-MISSING"
        exit 2
    }
    "失败：$message"
    "mutation verdict: ERROR"
    exit 1
}
finally {
    # 异常路径也必须把源码写回运行前的字节。
    Restore-AllSourceSnapshots
}

if ($script:failures -gt 0) {
    "mutation verdict: FAIL ($($script:failures))"
    exit 1
}
"mutation verdict: PASS"
exit 0
