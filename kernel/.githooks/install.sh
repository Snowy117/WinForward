#!/usr/bin/env bash
# 安装/卸载仓库自带的提交前钩子。只改本仓库 local 作用域的 git 配置。
#
#   .githooks/install.sh              安装（git config --local core.hooksPath .githooks）
#   .githooks/install.sh --uninstall  卸载并复原 local 的 core.hooksPath
#   .githooks/install.sh --status     只报告当前状态
#
# 安装时把 local 作用域原来的 core.hooksPath 记到 <git-dir>/wf-hooks-path.state，
# 卸载时据此复原：原来没设过就清空，原来有值就写回，原来就是 .githooks 也照原样写回。
# global / system 里的 core.hooksPath 不读也不写，不会因为安装或卸载被改动。
set -uo pipefail

hook_dir=".githooks"
hook_rel="${hook_dir}/pre-commit"
state_name="wf-hooks-path.state"

die() {
    printf '.githooks/install.sh: %s\n' "$1" >&2
    exit 2
}

usage() {
    cat <<'EOF'
用法：.githooks/install.sh [--install | --uninstall | --status]

  （无参数） / --install   安装：git config --local core.hooksPath .githooks
  --uninstall              卸载：把 local 的 core.hooksPath 复原成安装前的值
  --status                 只报告 local / global / 生效的 core.hooksPath 与安装前的记录
  -h, --help               显示本帮助

只改本仓库 local 作用域的 git 配置，不碰 --global / --system。
EOF
}

case "${1:-}" in
    ""|--install) action="install" ;;
    --uninstall) action="uninstall" ;;
    --status) action="status" ;;
    -h|--help) usage; exit 0 ;;
    *) die "未知参数：$1（用 --help 查看用法）" ;;
esac

git_dir="$(git rev-parse --absolute-git-dir 2>/dev/null)" || die "当前目录不在 git 仓库里"
repo_root="$(git rev-parse --show-toplevel 2>/dev/null)" || die "当前目录不在 git 工作树里"
cd "${repo_root}" || die "无法进入仓库根目录：${repo_root}"

state_file="${git_dir}/${state_name}"
local_value="$(git config --local --get core.hooksPath 2>/dev/null || true)"
effective_value="$(git config --get core.hooksPath 2>/dev/null || true)"
global_value="$(git config --global --get core.hooksPath 2>/dev/null || true)"

write_state() { # write_state set <值> | write_state unset
    if [[ "$1" == "set" ]]; then
        printf 'set\n%s\n' "$2" >"${state_file}"
    else
        printf 'unset\n' >"${state_file}"
    fi
}

print_status() {
    printf 'local  core.hooksPath = %s\n' "${local_value:-（未设置）}"
    printf 'global core.hooksPath = %s\n' "${global_value:-（未设置）}"
    printf '生效   core.hooksPath = %s\n' "${effective_value:-（未设置）}"
    if [[ -f "${state_file}" ]]; then
        printf '安装前记录（%s）：\n' "${state_file}"
        sed 's/^/    /' "${state_file}"
    else
        printf '安装前记录：无（从未安装或已卸载）\n'
    fi
}

install() {
    [[ -f "${hook_rel}" ]] || die "找不到钩子文件：${hook_rel}"
    chmod +x "${hook_rel}" || die "无法给 ${hook_rel} 加可执行位"

    if [[ "${local_value}" == "${hook_dir}" ]]; then
        if [[ ! -f "${state_file}" ]]; then
            # local 本来就指向 .githooks：记下来，卸载时写回原值而不是清空。
            write_state set "${hook_dir}" || die "无法写 ${state_file}"
            printf '钩子已经装好：local core.hooksPath 原本就是 %s，已记录原值。\n' "${hook_dir}"
        else
            printf '钩子已经装好：local core.hooksPath = %s\n' "${hook_dir}"
        fi
        return 0
    fi

    if [[ -n "${local_value}" ]]; then
        write_state set "${local_value}" || die "无法写 ${state_file}"
    else
        write_state unset || die "无法写 ${state_file}"
    fi

    git config --local core.hooksPath "${hook_dir}" || die "git config --local core.hooksPath 失败"

    installed="$(git config --local --get core.hooksPath 2>/dev/null || true)"
    [[ "${installed}" == "${hook_dir}" ]] || die "安装后校验失败：local core.hooksPath = ${installed}"

    printf '已安装提交前钩子：local core.hooksPath = %s\n' "${installed}"
    printf '钩子命令：tools/check.sh --fast --staged\n'
    printf '卸载：.githooks/install.sh --uninstall\n'
}

uninstall() {
    if [[ -f "${state_file}" ]]; then
        recorded="$(sed -n '1p' "${state_file}")"
        case "${recorded}" in
            set)
                saved="$(sed -n '2p' "${state_file}")"
                git config --local core.hooksPath "${saved}" || die "恢复 local core.hooksPath 失败"
                printf '已复原 local core.hooksPath = %s\n' "${saved}"
                ;;
            unset)
                if [[ -n "${local_value}" ]]; then
                    git config --local --unset-all core.hooksPath || die "清除 local core.hooksPath 失败"
                fi
                printf '已清除 local core.hooksPath（安装前就没有设置）\n'
                ;;
            *)
                die "状态文件格式不认识：${state_file}"
                ;;
        esac
        rm -f "${state_file}" || die "无法删除 ${state_file}"
        return 0
    fi

    if [[ "${local_value}" == "${hook_dir}" ]]; then
        # 没有 state 说明不是本脚本装的：可能是预置值，替用户清掉就不可逆了。
        printf 'local core.hooksPath 已经是 %s 但没有安装记录，看起来是预置值，不动。\n' "${hook_dir}"
        printf '确实要清掉时手动执行：git config --local --unset core.hooksPath\n'
        return 0
    fi

    printf '没有可卸载的钩子（local core.hooksPath = %s）\n' "${local_value:-未设置}"
}

case "${action}" in
    install) install ;;
    uninstall) uninstall ;;
    status) print_status ;;
esac
