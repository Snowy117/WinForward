#!/usr/bin/env bash
# 宿主侧质量门禁入口。
#
# 三个档位：
#   --fast（默认）   格式、有效行、cppcheck
#   --full           fast + CMake 配置 + 构建并运行 wf_check
#   --staged         只检查暂存区里的 C++ 文件格式（提交前钩子）
#
# --fast 与 --staged 可以组合：快速档，但格式只看暂存区文件。
# --full 与 --staged 不能组合：前者构建工作区，后者检查索引。
#
# 退出码：0 通过；1 有检查发现；2 环境或参数问题。
#
# 本脚本不读取 wf.local.json：所有检查项都不依赖本机配置，文件缺失是正常状态。

# 不用 set -e：每个检查各自收集退出码，最后统一汇总，避免第一个失败就中断其余检查。
set -uo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "${script_dir}/.." && pwd)"
cd "${repo_root}" || {
    printf 'tools/check.sh: 无法进入仓库根目录：%s\n' "${repo_root}" >&2
    exit 2
}

build_dir="build/check"
mode=""
staged=0
pass_count=0
fail_count=0
skip_count=0

temp_paths=()

# shellcheck disable=SC2329 # 由 trap EXIT 间接调用
cleanup_temp_paths() {
    local path
    for path in "${temp_paths[@]}"; do
        [[ -n "${path}" ]] || continue
        rm -rf -- "${path}"
    done
}

trap cleanup_temp_paths EXIT

now_ns() {
    date +%s%N
}

usage() {
    cat <<'EOF'
用法：tools/check.sh [--fast | --full] [--staged] [--build-dir <目录>]

  --fast             格式、有效行、cppcheck（默认）
  --full             在 --fast 基础上配置并构建 wf_check（会跑 C# 测试与 ctest）
  --staged           只检查暂存区里的 C++ 文件格式
  --build-dir <目录>  指定 --full 使用的构建目录（默认 build/check）
  -h, --help         显示本帮助

--fast 与 --staged 可以组合：快速档，但格式只检查暂存区文件。
--full 与 --staged 不能组合：前者构建工作区，后者检查索引。

退出码：0 通过；1 有检查发现；2 环境或参数问题。
本地配置 wf.local.json 由使用它的脚本读取；本脚本不依赖它，缺失不是错误。
EOF
}

fail_usage() {
    printf 'tools/check.sh: %s\n' "$1" >&2
    printf '用 tools/check.sh --help 查看用法。\n' >&2
    exit 2
}

pass() {
    pass_count=$((pass_count + 1))
    if [[ -n "${2:-}" ]]; then
        printf '[check] %s ... PASS（%s）\n' "$1" "$2"
    else
        printf '[check] %s ... PASS\n' "$1"
    fi
}

fail() {
    fail_count=$((fail_count + 1))
    if [[ -n "${2:-}" ]]; then
        printf '[check] %s ... FAIL（%s）\n' "$1" "$2"
    else
        printf '[check] %s ... FAIL\n' "$1"
    fi
}

skip() {
    skip_count=$((skip_count + 1))
    if [[ -n "${2:-}" ]]; then
        printf '[check] %s ... SKIP（%s）\n' "$1" "$2"
    else
        printf '[check] %s ... SKIP\n' "$1"
    fi
}

indent_tail() {
    tail -n "${2:-40}" <<<"$1" | sed 's/^/    /'
}

collect_git_paths() { # collect_git_paths <数组名> <git 参数...>
    local -n target="$1"
    shift
    local list_file
    list_file="$(mktemp)" || return 1
    temp_paths+=("${list_file}")
    if ! git "$@" >"${list_file}"; then
        rm -f "${list_file}"
        return 1
    fi
    # shellcheck disable=SC2034 # target 是 nameref，数组由调用方的名字持有
    mapfile -d '' -t target <"${list_file}"
    rm -f "${list_file}"
}

tool_version() {
    case "$1" in
        clang-format) clang-format --version 2>/dev/null | head -n 1 | sed 's/^clang-format version //' ;;
        cppcheck) cppcheck --version 2>/dev/null | head -n 1 | sed 's/^Cppcheck //' ;;
        python3) python3 --version 2>/dev/null | head -n 1 | sed 's/^Python //' ;;
        cmake) cmake --version 2>/dev/null | head -n 1 | awk '{print $3}' ;;
        ninja) ninja --version 2>/dev/null | head -n 1 ;;
        dotnet) dotnet --version 2>/dev/null | head -n 1 ;;
        git) git --version 2>/dev/null | awk '{print $3}' ;;
        *) "$1" --version 2>/dev/null | head -n 1 ;;
    esac
}

# ---------- 参数 ----------
while (( $# > 0 )); do
    case "$1" in
        --fast)
            if [[ -n "${mode}" && "${mode}" != "fast" ]]; then
                fail_usage "--fast 不能与 --full 同时使用"
            fi
            mode="fast"
            ;;
        --full)
            if [[ -n "${mode}" && "${mode}" != "full" ]]; then
                fail_usage "--full 不能与 --fast 同时使用"
            fi
            mode="full"
            ;;
        --staged)
            staged=1
            ;;
        --build-dir)
            if (( $# < 2 )) || [[ -z "$2" || "$2" == -* ]]; then
                fail_usage "--build-dir 需要一个目录参数"
            fi
            build_dir="$2"
            shift
            ;;
        --build-dir=*)
            build_dir="${1#--build-dir=}"
            [[ -n "${build_dir}" ]] || fail_usage "--build-dir 需要一个目录参数"
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            fail_usage "未知参数：$1"
            ;;
    esac
    shift
done

if [[ -z "${mode}" ]]; then
    if (( staged )); then
        mode="staged"
    else
        mode="fast"
    fi
fi

if (( staged )) && [[ "${mode}" == "full" ]]; then
    fail_usage "--full 不能与 --staged 组合：--full 构建工作区，--staged 检查索引"
fi

# ---------- 环境 ----------
required_tools=(git clang-format)
case "${mode}" in
    staged)
        ;;
    fast)
        required_tools+=(cppcheck python3)
        ;;
    full)
        required_tools+=(cppcheck python3 cmake ninja dotnet)
        ;;
esac

missing_tools=()
for tool in "${required_tools[@]}"; do
    command -v "${tool}" >/dev/null 2>&1 || missing_tools+=("${tool}")
done

if command -v git >/dev/null 2>&1 && ! git rev-parse --git-dir >/dev/null 2>&1; then
    printf '[check] 环境 ... FAIL（当前目录不是 git 仓库）\n'
    printf '[check] tools/check.sh 需要在仓库根目录运行：%s\n' "${repo_root}" >&2
    printf '[check] 汇总：环境不满足，退出码 2\n' >&2
    exit 2
fi

start_ns="$(now_ns)"

if (( ${#missing_tools[@]} > 0 )); then
    printf '[check] 环境 ... FAIL（缺少：%s）\n' "${missing_tools[*]}"
    printf '[check] 这些工具由仓库 devShell 提供：先运行 nix develop（或 direnv allow）后重试。\n' >&2
    printf '[check] 汇总：环境不满足，退出码 2\n' >&2
    exit 2
fi

env_detail=""
for tool in "${required_tools[@]}"; do
    version="$(tool_version "${tool}")"
    [[ -n "${version}" ]] || version="?"
    if [[ -n "${env_detail}" ]]; then
        env_detail+="，"
    fi
    env_detail+="${tool} ${version}"
done
pass "环境" "${env_detail}"

# ---------- 格式 ----------
run_format_check_worktree() {
    local -a files=()
    if ! collect_git_paths files ls-files -z -- '*.cpp' '*.h' '*.hpp' '*.inl'; then
        fail "格式" "git ls-files 失败"
        return
    fi

    if (( ${#files[@]} == 0 )); then
        skip "格式" "没有 C++ 文件"
        return
    fi

    local output
    if output="$(clang-format --dry-run --Werror "${files[@]}" 2>&1)"; then
        pass "格式" "工作区 ${#files[@]} 个文件"
    else
        fail "格式" "工作区 ${#files[@]} 个文件有漂移"
        indent_tail "${output}" 80
    fi
}

run_format_check_staged() {
    local -a files=()
    if ! collect_git_paths files diff --cached --name-only --diff-filter=ACMR -z -- \
        '*.cpp' '*.h' '*.hpp' '*.inl'; then
        fail "格式" "git diff --cached 失败"
        return
    fi

    if (( ${#files[@]} == 0 )); then
        skip "格式" "暂存区里没有 C++ 文件"
        return
    fi

    # 按索引内容检查：git show 读的是将要提交的 blob，不是工作区文件。
    local tmp_root
    tmp_root="$(mktemp -d)" || {
        fail "格式" "无法创建临时目录"
        return
    }
    temp_paths+=("${tmp_root}")

    local -a staged_files=()
    local file
    for file in "${files[@]}"; do
        mkdir -p "${tmp_root}/$(dirname -- "${file}")" || {
            fail "格式" "无法创建临时目录：${file}"
            return
        }
        if ! git show ":${file}" >"${tmp_root}/${file}"; then
            fail "格式" "读取暂存区内容失败：${file}"
            return
        fi
        staged_files+=("${tmp_root}/${file}")
    done

    local style="file:${repo_root}/.clang-format"
    local output
    if output="$(clang-format --dry-run --Werror --style="${style}" "${staged_files[@]}" 2>&1)"; then
        pass "格式" "暂存区 ${#files[@]} 个文件"
    else
        fail "格式" "暂存区 ${#files[@]} 个文件有漂移"
        # clang-format 看到的是临时镜像路径，这里映射回仓库相对路径。
        output="${output//"${tmp_root}/"/}"
        indent_tail "${output}" 80
    fi
}

run_format_check() {
    if (( staged )); then
        run_format_check_staged
    else
        run_format_check_worktree
    fi
}

# ---------- 有效行 ----------
run_effective_lines_check() {
    local output status
    output="$(python3 src/userland/scripts/check-effective-lines.py 2>&1)"
    status=$?

    if (( status == 0 )); then
        local -a lines=()
        mapfile -t lines <<<"${output}"
        local detail="${lines[-1]#通过：}"
        pass "有效行" "${detail}"
        return
    fi

    local offenders
    offenders="$(printf '%s\n' "${output}" | grep '^超限：' || true)"
    if [[ -n "${offenders}" ]]; then
        local offender_count
        offender_count="$(printf '%s\n' "${offenders}" | wc -l | tr -d ' ')"
        fail "有效行" "${offender_count} 个文件超过 500 行"
        printf '%s\n' "${offenders}" | sed 's/^/    /'
    else
        fail "有效行" "检查脚本本身失败"
        indent_tail "${output}" 40
    fi
}

# ---------- cppcheck ----------
run_cppcheck_check() {
    local -a files=()
    if ! collect_git_paths files ls-files -z -- 'src/*.cpp' 'src/*.h'; then
        fail "cppcheck" "git ls-files 失败"
        return
    fi

    if (( ${#files[@]} == 0 )); then
        skip "cppcheck" "src/ 下没有 C++ 文件"
        return
    fi

    # --inline-suppr 让定义处的 // cppcheck-suppress 生效。
    #
    # SAL 注解宏由 WDK 头在 MSVC 下定义，cppcheck 不加载 WDK 头，看到的是未知宏；逐条给空定义，
    # 让它能把驱动翻译单元解析完——否则 unknownMacro 会中断解析，把真正被引用的符号误报成
    # unusedFunction。这里只声明「宏存在且展开为空」，不关闭 unknownMacro 检查：以后再遇到新的
    # SAL 注解宏，往这个数组里追加一行。
    local -a cppcheck_defines=(
        -D'_IRQL_raises_(irql)='
        -D'_IRQL_requires_(irql)='
        -D'_IRQL_requires_max_(irql)='
        -D'_IRQL_saves_='
        -D'_IRQL_saves_global_(kind,param)='
        -D'_IRQL_restores_='
        -D'_IRQL_restores_global_(kind,param)='
        -D'_Dispatch_type_(t)='
        -D'_Function_class_(x)='
    )
    local output status
    output="$(cppcheck --enable=unusedFunction --std=c++20 --language=c++ --inline-suppr --error-exitcode=1 --quiet \
        -I src/common -I src/driver "${cppcheck_defines[@]}" "${files[@]}" 2>&1)"
    status=$?

    if (( status == 0 )); then
        pass "cppcheck" "${#files[@]} 个文件"
    else
        fail "cppcheck" "发现未使用符号或解析失败"
        indent_tail "${output}" 80
    fi
}

run_fast_checks() {
    run_format_check
    run_effective_lines_check
    run_cppcheck_check
}

# ---------- --full：配置与 wf_check ----------
run_configure_check() {
    local output
    if output="$(cmake -S . -B "${build_dir}" -G Ninja 2>&1)"; then
        pass "配置" "${build_dir}"
        return 0
    fi

    fail "配置" "${build_dir}"
    indent_tail "${output}" 60
    return 1
}

run_wf_check() {
    local output
    if output="$(cmake --build "${build_dir}" --target wf_check 2>&1)"; then
        pass "wf_check" "C# 测试与 ctest 全绿"
        return 0
    fi

    fail "wf_check" "构建或测试失败"
    indent_tail "${output}" 80
    return 1
}

# ---------- 执行 ----------
case "${mode}" in
    staged)
        run_format_check
        ;;
    fast)
        run_fast_checks
        ;;
    full)
        run_fast_checks
        if run_configure_check; then
            run_wf_check
        else
            skip "wf_check" "配置失败，未构建"
        fi
        ;;
esac

# ---------- 汇总 ----------
end_ns="$(now_ns)"
total_ms=$(((end_ns - start_ns) / 1000000))
printf '\n[check] 汇总：%d PASS，%d FAIL，%d SKIP（共 %d 项，耗时 %d.%03d s）\n' \
    "${pass_count}" "${fail_count}" "${skip_count}" \
    "$((pass_count + fail_count + skip_count))" \
    "$((total_ms / 1000))" "$((total_ms % 1000))"

if (( fail_count > 0 )); then
    printf '[check] 结论：FAIL\n'
    exit 1
fi

printf '[check] 结论：PASS\n'
exit 0
