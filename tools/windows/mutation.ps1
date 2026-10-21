param(
    [string]$Root = (Join-Path $PSScriptRoot "..\.."),
    [string]$Remote,
    [int[]]$OnlyIndex = @()
)

# 变异测试：把被守的代码改成恒假/改错，确认对应的验证套件变红。
# 每条变异只改一处（或一组必须同时拆掉的闸），跑对应套件，期望退出码非 0。
# 全部跑完后恢复源码、重新构建，并跑一遍正向基线确认测试本身是全绿的。
# 变异锚点的匹配逻辑本期不动（区域标记是后续批次）；构建/签名/安装/卸载走 WfBuild.psm1。

$ErrorActionPreference = "Stop"

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
function Report([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) { "MUTATION PASS: $name ($detail)" } else { $script:failures++; "MUTATION FAIL: $name ($detail)" }
}

$mutations = @(
    @{
        Name = "block-filter 不创建（block 规则不再阻断）"
        File = "block_filters.cpp"
        Edits = @(@{
            Old = "if (selected.action != rule_action::block)"
            New = "if (view.rule_count != 0xFFFFFFFFU || selected.action != rule_action::block)"
        })
        Target = @("rules", "--iterations", "3")
    },
    @{
        Name = "redirect 分支退化成放行"
        File = "probe_classify.cpp"
        Edits = @(@{
            Old = "const bool redirect_wanted = result.action == rule_action::redirect;"
            New = "const bool redirect_wanted = false;"
        })
        Target = @("selftest", "--remote", $Remote, "--iterations", "1")
    },
    @{
        Name = "block 分支退化成放行"
        File = "probe_classify.cpp"
        Edits = @(@{
            Old = "const bool block_wanted = result.action == rule_action::block;"
            New = "const bool block_wanted = false;"
        })
        Target = @("rules", "--iterations", "3")
    },
    @{
        Name = "permit 分支退化成 block"
        File = "probe_classify.cpp"
        Edits = @(@{
            Old = "    probe_increment(result.matched ? state.stats.matched_permit : state.stats.fallback_permit);`n    permit_classification(state,`n                          facts,`n                          rule_index,`n                          result.matched ? probe_decision::permitted_rule : probe_decision::permitted_fallback,`n                          classify_out);"
            New = "    probe_increment(result.matched ? state.stats.matched_block : state.stats.fallback_block);`n    set_block(classify_out);`n    probe_increment(state.stats.blocked);`n    log_decision(facts,`n                 rule_index,`n                 result.matched ? probe_decision::blocked_rule : probe_decision::blocked_fallback,`n                 probe_action::block,`n                 0,`n                 redirect_branch::none);"
        })
        Target = @("rules", "--iterations", "3")
    },
    @{
        Name = "闸 1（监听端口）恒假"
        File = "probe_classify.cpp"
        Edits = @(@{
            Old = "const bool listener_port_hit = config.listener_port != 0 && facts.remote_port == config.listener_port;"
            New = "const bool listener_port_hit = false;"
        })
        Target = @("gates")
    },
    @{
        Name = "闸 2（redirect state）恒假"
        File = "probe_classify.cpp"
        Edits = @(@{
            Old = "const bool redirect_state_hit = redirect_state == FWPS_CONNECTION_REDIRECTED_BY_SELF ||`n                                    redirect_state == FWPS_CONNECTION_PREVIOUSLY_REDIRECTED_BY_SELF;"
            New = "const bool redirect_state_hit = false;"
        })
        Target = @("gates")
    },
    @{
        Name = "闸 4（self-PID）恒假"
        File = "probe_classify.cpp"
        Edits = @(@{
            Old = "const bool self_process_hit = process_id_in_set(config, facts.process_id);"
            New = "const bool self_process_hit = false;"
        })
        Target = @("gates")
    },
    @{
        Name = "闸 2 + 闸 3 同时拆掉（应收敛成环）"
        File = "probe_classify.cpp"
        Edits = @(
            @{
                Old = "const bool redirect_state_hit = redirect_state == FWPS_CONNECTION_REDIRECTED_BY_SELF ||`n                                    redirect_state == FWPS_CONNECTION_PREVIOUSLY_REDIRECTED_BY_SELF;"
                New = "const bool redirect_state_hit = false;"
            },
            @{
                Old = "const bool condition_flags_hit = (facts.condition_flags & (FWP_CONDITION_FLAG_IS_CONNECTION_REDIRECTED |`n                                                               FWP_CONDITION_FLAG_IS_PROXY_CONNECTION)) != 0;"
                New = "const bool condition_flags_hit = false;"
            }
        )
        Target = @("gates")
    },
    @{
        Name = "V6 层的 filter 不挂（只挂 V4）"
        File = "probe_driver.cpp"
        Edits = @(@{
            Old = "        status = add_layer_filter(`n            state.v6, filter_key_v6, FWPM_LAYER_ALE_CONNECT_REDIRECT_V6, callout_key_v6, filter_name_v6, false);"
            New = "        status = STATUS_SUCCESS;"
        })
        Target = @("selftest", "--dual", "--remote", $Remote, "--iterations", "1")
    },
    @{
        Name = "非法 blob 被当成成功"
        File = "probe_control.cpp"
        Edits = @(@{
            Old = "    if (table == nullptr)`n    {`n        probe_increment(state.stats.rejected_blobs);`n        return STATUS_INVALID_PARAMETER;`n    }"
            New = "    if (table == nullptr)`n    {`n        probe_increment(state.stats.rejected_blobs);`n        return STATUS_SUCCESS;`n    }"
        })
        Target = @("rules", "--iterations", "3")
    },
    @{
        Name = "非 ASCII 进程路径被丢弃（UTF-8 转换退化）"
        File = "process_path.cpp"
        Edits = @(@{
            Old = "        std::uint8_t encoded[4]{};"
            New = "        if (code_point > 0x7F)`n        {`n            length = 0;`n            target[0] = '\0';`n            return;`n        }`n        std::uint8_t encoded[4]{};"
        })
        Target = @("rules", "--iterations", "3")
    },
    @{
        Name = "规则顺序冲突检测被绕过"
        File = "rule_table.cpp"
        Edits = @(@{
            Old = "    if (!rule_order_valid(table->view, earlier_rule, blocking_rule))"
            New = "    if (table->view.rule_count == 0xFFFFFFFFU && !rule_order_valid(table->view, earlier_rule, blocking_rule))"
        })
        Target = @("rules", "--iterations", "3")
    },
    @{
        Name = "fallback=block 冲突检测被绕过"
        File = "rule_table.cpp"
        Edits = @(@{
            Old = "    if (table->view.fallback_action == rule_action::block)"
            New = "    if (table->view.rule_count == 0xFFFFFFFFU && table->view.fallback_action == rule_action::block)"
        })
        Target = @("rules", "--iterations", "3")
    },
    @{
        Name = "enabled=0 不撤 block filter"
        File = "probe_control.cpp"
        Edits = @(@{
            Old = "        if (!next_enabled)`n        {`n            probe_delete_block_filters(state.block_filter_ids, state.block_filter_count);`n        }"
            New = "        if (!next_enabled)`n        {`n        }"
        })
        Target = @("rules", "--iterations", "3")
    },
    @{
        Name = "selector UTF-8/511 校验被绕过"
        File = "rule_table.cpp"
        Edits = @(@{
            Old = "        if (selected.process_index != rule_pool_index_any &&`n            !valid_utf8_selector(table->view.process_pool + selected.process_index))"
            New = "        if (table->view.rule_count == 0xFFFFFFFFU && selected.process_index != rule_pool_index_any &&`n            !valid_utf8_selector(table->view.process_pool + selected.process_index))"
        })
        Target = @("rules", "--iterations", "3")
    },
    @{
        Name = "permit 计数器记到 block 上"
        File = "probe_classify.cpp"
        Edits = @(@{
            Old = "probe_increment(result.matched ? state.stats.matched_permit : state.stats.fallback_permit);"
            New = "probe_increment(result.matched ? state.stats.matched_block : state.stats.fallback_block);"
        })
        Target = @("rules", "--iterations", "3")
    }
)

"===== 先构建一次测试程序 ====="
try {
    & dotnet build (Join-Path $Root "tools\wfprobe\wfprobe.csproj") -c Release -v:q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "测试程序构建失败" }
    $exe = Resolve-WfTool -Root $Root -Name "wfprobe"

    for ($mutationIndex = 0; $mutationIndex -lt $mutations.Count; $mutationIndex++) {
        if ($OnlyIndex.Count -gt 0 -and $OnlyIndex -notcontains $mutationIndex) { continue }
        $mutation = $mutations[$mutationIndex]
        $file = Join-Path $Root "src\driver\$($mutation.File)"
        $original = Get-Content $file -Raw
        $patched = $original
        foreach ($edit in $mutation.Edits) {
            $hits = ([regex]::Matches($patched, [regex]::Escape($edit.Old))).Count
            if ($hits -ne 1) { throw "锚点命中 $hits 次（应为 1）：$($mutation.Name)" }
            $patched = $patched.Replace($edit.Old, $edit.New)
        }

        Set-Content -Path $file -Value $patched -NoNewline -Encoding utf8NoBOM
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
        finally {
            Uninstall-Driver -ToolExe $exe
            Set-Content -Path $file -Value $original -NoNewline -Encoding utf8NoBOM
        }
    }

    "===== 恢复源码后的正向基线（必须全绿）====="
    try {
        $build = Build-Driver -Root $Root
        if ($build.ExitCode -ne 0) { throw "恢复源码后的驱动构建失败：$($build.Output)" }
        Install-Driver -Root $Root | Out-Null
        $baseline = @(
            @{ Name = "rules"; Args = @("rules", "--iterations", "3") },
            @{ Name = "selftest-v4"; Args = @("selftest", "--remote", $Remote, "--iterations", "1") },
            @{ Name = "selftest-dual"; Args = @("selftest", "--dual", "--remote", $Remote, "--iterations", "1") },
            @{ Name = "gates"; Args = @("gates") }
        )
        foreach ($case in $baseline) {
            $code = Invoke-WfSuite -Root $Root -ToolExe $exe -Arguments $case.Args
            Report "baseline-$($case.Name)" ($code -eq 0) "target-exit=$code"
        }
    }
    finally {
        Uninstall-Driver -ToolExe $exe
    }
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

if ($script:failures -gt 0) {
    "mutation verdict: FAIL ($($script:failures))"
    exit 1
}
"mutation verdict: PASS"
exit 0
