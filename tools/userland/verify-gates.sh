#!/usr/bin/env bash
# 门禁的反向验证：逐条破坏被守的东西，确认测试真的会红。
#
# ABI 单一来源（读 C++ 头文本的那组测试）：
#   - 结构尺寸、字段偏移、枚举取值、IOCTL 功能号、产品常量、夹具版本与夹具枚举；
# 编译期语义检查：
#   - 规则顺序冲突的进程谓词、block 表达边界、空数组 / 端口 0 / filter 预算 / blob 上限、
#     配置层未知字段拒绝。
#
# 全部变异只改临时副本或先备份再恢复；脚本退出前一定还原源码。基线（未变异）必须先绿。
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root"

tests_project="src/userland/WinForward.Userland.Tests"
log="$(mktemp)"
tmp="$(mktemp -d)"
restore_files=()

cleanup() {
    for backup in "${restore_files[@]:-}"; do
        [ -n "$backup" ] || continue
        cp "$backup.bak" "$backup"
        rm -f "$backup.bak"
    done
    rm -rf "$tmp" "$log"
}
trap cleanup EXIT

checks=0
failures=0
mutations=0
baselines=0
pass() { checks=$((checks + 1)); echo "PASS $*"; }
fail() { checks=$((checks + 1)); failures=$((failures + 1)); echo "FAIL $*"; }

run_suite() { # run_suite <args...>
    dotnet run --project "$tests_project" -c Release -- "$@" >"$log" 2>&1
}

expect_green() { # expect_green <name> <args...>
    local name="$1"; shift
    baselines=$((baselines + 1))
    if run_suite "$@"; then pass "$name（基线绿）"; else fail "$name（期望绿，实际红）"; tail -20 "$log"; fi
}

expect_mutation_red() { # expect_mutation_red <name> <command...>
    local name="$1"; shift
    mutations=$((mutations + 1))
    if "$@" >"$log" 2>&1; then fail "$name（变异后仍绿）"; else pass "$name（变异后变红）"; fi
}

backup() { # backup <file>（不要放进 $(...)：命令替换是子 shell，restore_files 会丢）
    cp "$1" "$1.bak"
    restore_files+=("$1")
}

run_abi_with_root() { # run_abi_with_root <source-root>
    WF_ABI_SOURCE_ROOT="$1" dotnet run --project "$tests_project" -c Release -- abi
}

# ---------- 0. 基线 ----------
expect_green "基线/ABI 一致性" abi
expect_green "基线/全部单测" test

# ---------- 1. ABI 单一来源（改 C++ 头与夹具的临时副本） ----------
mkdir -p "$tmp/src/driver" "$tmp/src/common" "$tmp/tools/wfprobe"
reset_header() {
    cp src/driver/probe_ioctl.h "$tmp/src/driver/probe_ioctl.h"
    cp src/common/redirect_context.h "$tmp/src/common/redirect_context.h"
    cp tools/wfprobe/Protocol.cs "$tmp/tools/wfprobe/Protocol.cs"
}
reset_header

sed -i 's/static_assert(sizeof(probe_config) == 64/static_assert(sizeof(probe_config) == 60/' \
    "$tmp/src/driver/probe_ioctl.h"
expect_mutation_red "ABI/改 C++ 头的结构尺寸" run_abi_with_root "$tmp"
reset_header

sed -i 's/static_assert(offsetof(probe_config, listener_process_id) == 8/static_assert(offsetof(probe_config, listener_process_id) == 12/' \
    "$tmp/src/driver/probe_ioctl.h"
expect_mutation_red "ABI/改 C++ 头的字段偏移" run_abi_with_root "$tmp"
reset_header

sed -i 's/permitted_disabled = 0,/permitted_disabled = 7,/' "$tmp/src/driver/probe_ioctl.h"
expect_mutation_red "ABI/改 C++ 头的枚举取值" run_abi_with_root "$tmp"
reset_header

sed -i 's/ioctl_set_config = make_ioctl(0x800);/ioctl_set_config = make_ioctl(0x801);/' \
    "$tmp/src/driver/probe_ioctl.h"
expect_mutation_red "ABI/改 C++ 头的 IOCTL 功能号" run_abi_with_root "$tmp"
reset_header

sed -i 's/redirect_context_magic = 0x31434657U;/redirect_context_magic = 0x31434658U;/' \
    "$tmp/src/common/redirect_context.h"
expect_mutation_red "ABI/改 redirect_context 魔数" run_abi_with_root "$tmp"
reset_header

sed -i 's/inline constexpr std::uint32_t max_block_filters = 128;/inline constexpr std::uint32_t max_block_filters = 129;/' \
    "$tmp/src/driver/probe_ioctl.h"
expect_mutation_red "ABI/改 C++ 头的 max_block_filters" run_abi_with_root "$tmp"
reset_header

sed -i 's/public const uint AbiVersion = 7;/public const uint AbiVersion = 8;/' "$tmp/tools/wfprobe/Protocol.cs"
expect_mutation_red "ABI/改夹具的 ABI 版本" run_abi_with_root "$tmp"
reset_header

sed -i 's/public const byte DecisionRedirected = 14;/public const byte DecisionRedirected = 15;/' \
    "$tmp/tools/wfprobe/Protocol.cs"
expect_mutation_red "ABI/改夹具的枚举取值" run_abi_with_root "$tmp"
reset_header

# ---------- 2. 产品常量 ----------
backup src/userland/WinForward.Userland/Device/ProbeIoctl.cs
product_ioctl="src/userland/WinForward.Userland/Device/ProbeIoctl.cs"
sed -i 's/public const uint RuleBlobMaxSize = 64 \* 1024;/public const uint RuleBlobMaxSize = 65 * 1024;/' \
    "$product_ioctl"
expect_mutation_red "ABI/改产品常量" dotnet run --project "$tests_project" -c Release -- abi
cp "$product_ioctl.bak" "$product_ioctl"

# ---------- 3. 冲突检查镜像 ----------
backup src/userland/WinForward.Userland/Policy/RuleOrderValidator.cs
validator="src/userland/WinForward.Userland/Policy/RuleOrderValidator.cs"
sed -i 's/ProcessesOverlap(earlier, blocking, processPool, selectors)/true/' "$validator"
expect_mutation_red "冲突检查/拆掉进程谓词" dotnet run --project "$tests_project" -c Release -- test
cp "$validator.bak" "$validator"

# ---------- 3.5 编译期语义检查 ----------
backup src/userland/WinForward.Userland/Policy/RuleBlobCompiler.cs
compiler="src/userland/WinForward.Userland/Policy/RuleBlobCompiler.cs"
sed -i 's/if (validatePolicy && action == RuleAction.Block && !compiled.IsExactFile)/if (false)/' "$compiler"
expect_mutation_red "编译/放行 block 的文件名与目录选择器" dotnet run --project "$tests_project" -c Release -- test
cp "$compiler.bak" "$compiler"

sed -i 's/if (total > PolicyLimits.MaxBlockFilters)/if (false)/' "$compiler"
expect_mutation_red "编译/取消 block filter 预算" dotnet run --project "$tests_project" -c Release -- test
cp "$compiler.bak" "$compiler"

sed -i 's/if (totalSize > PolicyLimits.RuleBlobMaxSize)/if (false)/' "$compiler"
expect_mutation_red "编译/取消 64 KiB blob 上限" dotnet run --project "$tests_project" -c Release -- test
cp "$compiler.bak" "$compiler"

backup src/userland/WinForward.Userland/Configuration/PolicyConfigurationDocument.HostSelectors.cs
host_selectors="src/userland/WinForward.Userland/Configuration/PolicyConfigurationDocument.HostSelectors.cs"
sed -i 's/if (values.Count == 0)/if (false)/' "$host_selectors"
expect_mutation_red "配置/接受空选择器数组" dotnet run --project "$tests_project" -c Release -- test
cp "$host_selectors.bak" "$host_selectors"

sed -i 's/if (port == 0)/if (false)/' "$host_selectors"
expect_mutation_red "配置/接受端口 0" dotnet run --project "$tests_project" -c Release -- test
cp "$host_selectors.bak" "$host_selectors"

# ---------- 4. 配置层 ----------
backup src/userland/WinForward.Userland/Configuration/PolicyConfigurationDocument.cs
configuration="src/userland/WinForward.Userland/Configuration/PolicyConfigurationDocument.cs"
sed -i 's/"remotePorts", "remoteNetwork", "process"/"remotePortsX", "remoteNetwork", "process"/' "$configuration"
expect_mutation_red "配置/让未知字段被静默忽略" dotnet run --project "$tests_project" -c Release -- test
cp "$configuration.bak" "$configuration"

# ---------- 4.5 SOCKS5 客户端与配置 ----------
backup src/userland/WinForward.Userland/Relay/Socks5Client.cs
socks5_client="src/userland/WinForward.Userland/Relay/Socks5Client.cs"

sed -i 's/private const byte AddressDomain = 0x03;/private const byte AddressDomain = 0x01;/' "$socks5_client"
expect_mutation_red "SOCKS5/把域名地址类型写成 IPv4" dotnet run --project "$tests_project" -c Release -- test
cp "$socks5_client.bak" "$socks5_client"

sed -i 's/private const byte AddressIpv6 = 0x04;/private const byte AddressIpv6 = 0x01;/' "$socks5_client"
expect_mutation_red "SOCKS5/把 IPv6 地址类型写成 IPv4" dotnet run --project "$tests_project" -c Release -- test
cp "$socks5_client.bak" "$socks5_client"

sed -i 's/request\[offset\] = (byte)(port >> 8);/request[offset] = (byte)(port \& 0xFF);/' "$socks5_client"
expect_mutation_red "SOCKS5/端口字节序写反" dotnet run --project "$tests_project" -c Release -- test
cp "$socks5_client.bak" "$socks5_client"

sed -i 's/catch (OperationCanceledException exception) when (!token.IsCancellationRequested)/catch (OperationCanceledException exception) when (false)/' "$socks5_client"
expect_mutation_red "SOCKS5/握手超时不包装成 Socks5Exception" dotnet run --project "$tests_project" -c Release -- test
cp "$socks5_client.bak" "$socks5_client"

sed -i 's/if (rep != 0)/if (false)/' "$socks5_client"
expect_mutation_red "SOCKS5/忽略 rep 非 0" dotnet run --project "$tests_project" -c Release -- test
cp "$socks5_client.bak" "$socks5_client"

sed -i 's/if (reply\[1\] == MethodNoneAcceptable)/if (false)/' "$socks5_client"
sed -i 's/if (reply\[1\] != MethodNoAuthentication)/if (false)/' "$socks5_client"
expect_mutation_red "SOCKS5/接受上游拒绝 no-auth" dotnet run --project "$tests_project" -c Release -- test
cp "$socks5_client.bak" "$socks5_client"

sed -i 's/if (character > 0x7F)/if (false)/' "$socks5_client"
expect_mutation_red "SOCKS5/非 ASCII 域名不校验" dotnet run --project "$tests_project" -c Release -- test
cp "$socks5_client.bak" "$socks5_client"

backup src/userland/WinForward.Userland/Relay/OutboundConnector.cs
connector="src/userland/WinForward.Userland/Relay/OutboundConnector.cs"
sed -i 's/if (socks5 is not null)/if (false)/' "$connector"
expect_mutation_red "SOCKS5/配了上游却走直连（静默回退）" dotnet run --project "$tests_project" -c Release -- test
cp "$connector.bak" "$connector"

sed -i 's/Socks5Endpoint? socks5 = ParseSocks5(root);/Socks5Endpoint? socks5 = null;/' "$configuration"
expect_mutation_red "配置/静默忽略 socks5 段" dotnet run --project "$tests_project" -c Release -- test
cp "$configuration.bak" "$configuration"

# ---------- 5. 还原后必须重新变绿 ----------
expect_green "还原后/ABI 一致性" abi
expect_green "还原后/全部单测" test

echo
if [ "$failures" -ne 0 ]; then
    echo "===== 反向验证失败：$failures / $checks（变异 $mutations 条，基线 $baselines 条）====="
    exit 1
fi

echo "===== 反向验证通过：$mutations 条变异全部让目标测试变红；$baselines 条基线为绿（共 $checks 条检查）====="
