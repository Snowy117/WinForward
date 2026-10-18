#!/usr/bin/env bash
# tmux-based remote PowerShell execution on the Windows VM (PS 5.1 console over WinRM).
#   wf.sh connect                          open the configured tmux session and the WinRM REPL
#   wf.sh run '<powershell single-line>' [timeout_s]
#   wf.sh file <local.ps1> [timeout_s] [script args...]   upload, run with -ExecutionPolicy Bypass, remove
#   wf.sh up <local> <remote> [timeout_s]
#   wf.sh down <remote> <local> [timeout_s]
#   wf.sh cap [n] | key '<text>' | raw '<text>' | idle [timeout_s]
#
# Machine values are not in this file: campaign.env declares the variable names and the operator
# exports the values (documented in AGENTS.local.md). The REPL is stateful and the tmux server keeps
# it alive between invocations; `connect` recreates it.
set -u

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
# shellcheck source=campaign.env
. "$script_dir/campaign.env"

SESS=
PANE=

need() {
    local name=$1
    if [ -z "${!name:-}" ]; then
        echo "$(basename "$0"): campaign.env: $name is unset or empty" >&2
        echo "  export it (values are documented in benchmarks/WinForward.E2E/AGENTS.local.md)" >&2
        exit 2
    fi
}

need_session() {
    need WF_SESSION
    need WF_SCRATCH
    SESS=$WF_SESSION
    PANE=$WF_SCRATCH/.pane
    mkdir -p "$WF_SCRATCH"
}

usage() {
    cat <<'EOF'
usage: wf.sh <command> [args]

commands:
  connect                              start the tmux session and connect evil-winrm-py to the VM
  run '<powershell>' [timeout_s]       run one PowerShell line and print its output
  file <local.ps1> [timeout_s] [args]  upload the script, run it with -ExecutionPolicy Bypass, remove it
  up <local> <remote> [timeout_s]      upload a file
  down <remote> <local> [timeout_s]    download a file
  cap [n]                              capture the last n lines of the REPL pane
  key '<text>'                         send text plus Enter to the REPL
  raw '<text>'                         send text without Enter
  idle [timeout_s]                     wait until the REPL prompt is the last pane line

Exit codes: 0 success, 2 a required campaign.env value is unset, 124 a remote wait timed out.
EOF
}

cap() { tmux capture-pane -t "$SESS" -p -S -400000 > "$PANE"; }

wait_idle() {  # wait until the shell prompt is the last non-empty line (previous command done)
    local tmo="${1:-600}" waited=0
    while (( waited < tmo * 4 )); do
        cap
        if sed -e '/^[[:space:]]*$/d' "$PANE" | tail -1 | grep -q '^evil-winrm-py PS .*>$'; then return 0; fi
        sleep 0.25; waited=$((waited+1))
    done
    echo "[wf: shell never went idle within ${tmo}s]"; tail -20 "$PANE"; return 124
}

raw_run() {
    local ps="$1" tmo="${2:-300}" S="__WFS${RANDOM}$$__" D="__WFD${RANDOM}$$__"
    wait_idle "$tmo" || return 124
    tmux send-keys -t "$SESS" -l "Write-Output '$S'; $ps; Write-Output '$D'"
    tmux send-keys -t "$SESS" Enter
    local waited=0
    while (( waited < tmo * 4 )); do
        cap
        grep -qx "$D" "$PANE" && break
        sleep 0.25; waited=$((waited+1))
    done
    sleep 0.3; cap
    if ! grep -qx "$D" "$PANE"; then
        echo "[wf: TIMEOUT after ${tmo}s]"; tail -30 "$PANE"; return 124
    fi
    awk -v s="$S" -v d="$D" '
        $0==s {start=NR} $0==d {done_=NR} {line[NR]=$0}
        END{ for(i=start+1;i<done_;i++) print line[i] }' "$PANE" \
        | sed -e '/^evil-winrm-py PS /d'
}

cmd_run()  { raw_run "$1" "${2:-300}"; }

ps_quote() { printf "'%s'" "$(printf '%s' "$1" | sed "s/'/''/g")"; }

cmd_file() {
    local f="$1" tmo=900
    shift
    if [ "$#" -gt 0 ] && [[ "$1" =~ ^[0-9]+$ ]]; then tmo=$1; shift; fi
    f=$(cd "$(dirname "$f")" && pwd)/$(basename "$f")
    local stem remote want have invocation arg output rc=0
    stem=$(basename "$f" .ps1 | sed 's/[^A-Za-z0-9_.-]/_/g')
    remote="C:\\Windows\\Temp\\wf-$stem-$$.ps1"
    want=$(stat -c %s "$f")
    if ! cmd_up "$f" "$remote" 600 >/dev/null 2>&1; then
        echo "[wf: upload failed for $f]" >&2
        return 1
    fi
    have=$(raw_run "(Get-Item '$remote' -ErrorAction SilentlyContinue).Length" 120 2>/dev/null | tr -d '\r' | tail -1)
    if [ "$want" != "$have" ]; then
        echo "[wf: upload size mismatch: local $want, remote ${have:-absent}]" >&2
        return 1
    fi
    invocation="powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File '$remote'"
    for arg in "$@"; do
        if [[ "$arg" == -* ]]; then invocation+=" $arg"; else invocation+=" $(ps_quote "$arg")"; fi
    done
    output=$(raw_run "$invocation; Write-Output ('__WFEXIT__:' + \$LASTEXITCODE)" "$tmo") || rc=$?
    printf '%s\n' "$output" | sed '/^__WFEXIT__:/d'
    if [ "$rc" -eq 0 ]; then
        rc=$(printf '%s\n' "$output" | sed -n 's/^__WFEXIT__://p' | tail -1)
        [ -n "$rc" ] || rc=1
    fi
    raw_run "Remove-Item '$remote' -Force -ErrorAction SilentlyContinue" 60 >/dev/null 2>&1 || true
    return "$rc"
}


repl_cmd() {  # run a raw evil-winrm-py REPL command (upload/download/...)
    local c="$1" tmo="${2:-900}"
    wait_idle "$tmo" || return 124
    tmux send-keys -t "$SESS" -l "$c"
    tmux send-keys -t "$SESS" Enter
    local waited=0
    while (( waited < tmo * 4 )); do
        cap
        if sed -e '/^[[:space:]]*$/d' "$PANE" | tail -1 | grep -q '^evil-winrm-py PS .*>$'; then break; fi
        sleep 0.25; waited=$((waited+1))
    done
    sleep 0.5; cap
    awk -v c="$c" '$0==c {start=NR} {line[NR]=$0} END{for(i=start+1;i<=NR;i++) print line[i]}' "$PANE" \
        | sed -e '/^evil-winrm-py PS /d' -e '/^[[:space:]]*$/d'
}

cmd_up()   { repl_cmd "upload $1 $2" "${3:-900}"; }
cmd_down() { repl_cmd "download $1 $2" "${3:-900}"; }

cmd_connect() {
    need WF_SESSION
    need WF_SCRATCH
    need WF_VM_HOST
    need WF_VM_USER
    need WF_VM_PASSWORD_VAR
    need WF_WINRM_PORT
    local password_var=$WF_VM_PASSWORD_VAR
    local password=${!password_var:-}
    if [ -z "$password" ]; then
        echo "$(basename "$0"): campaign.env: $password_var is unset or empty (it holds the VM password)" >&2
        exit 2
    fi
    SESS=$WF_SESSION
    PANE=$WF_SCRATCH/.pane
    mkdir -p "$WF_SCRATCH"
    tmux kill-session -t "$SESS" 2>/dev/null || true
    # The password is passed through the tmux server's environment so it does not appear in this
    # script's own command line; the remote REPL's command line still carries it.
    tmux set-environment -g WF_VM_PASSWORD "$password"
    if ! tmux new-session -d -s "$SESS" -x 220 -y 50; then
        tmux set-environment -gu WF_VM_PASSWORD 2>/dev/null || true
        echo "[wf: could not create tmux session $SESS]" >&2
        return 1
    fi
    tmux set-option -t "$SESS" history-limit 200000 >/dev/null 2>&1 || true
    tmux send-keys -t "$SESS" -l "evil-winrm-py -i $WF_VM_HOST -u $WF_VM_USER -p \"\$WF_VM_PASSWORD\" --port $WF_WINRM_PORT --no-colors"
    tmux send-keys -t "$SESS" Enter
    local waited=0
    while (( waited < 240 )); do
        cap
        if sed -e '/^[[:space:]]*$/d' "$PANE" | tail -1 | grep -q '^evil-winrm-py PS .*>$'; then
            tmux set-environment -gu WF_VM_PASSWORD 2>/dev/null || true
            echo "[connected $WF_VM_USER@$WF_VM_HOST:$WF_WINRM_PORT]"
            return 0
        fi
        sleep 0.5; waited=$((waited+1))
    done
    tmux set-environment -gu WF_VM_PASSWORD 2>/dev/null || true
    echo "[wf: no WinRM prompt within 120s]"; tail -20 "$PANE" 2>/dev/null; return 1
}

if [ "$#" -eq 0 ] || [ "$1" = "-h" ] || [ "$1" = "--help" ] || [ "$1" = "help" ]; then
    usage
    exit 0
fi

case "$1" in
    connect) shift; cmd_connect ;;
    run) shift; need_session; cmd_run "$@" ;;
    up) shift; need_session; cmd_up "$@" ;;
    down) shift; need_session; cmd_down "$@" ;;
    file) shift; need_session; cmd_file "$@" ;;
    cap) shift; need_session; cap; tmux capture-pane -t "$SESS" -p -S -400000 | tail -"${1:-40}" ;;
    key) shift; need_session; tmux send-keys -t "$SESS" -l "$1"; tmux send-keys -t "$SESS" Enter ;;
    raw) shift; need_session; tmux send-keys -t "$SESS" -l "$1" ;;
    idle) shift; need_session; wait_idle "${1:-600}" && echo "[idle]" ;;
    *) usage >&2; exit 2 ;;
esac
