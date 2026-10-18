#!/usr/bin/env bash
# Two instances, because the proxied and direct lanes must be attributable: a product that
# intercepts the supposedly direct application then produces a flow to a target nothing should
# reach through a proxy, and the two lanes get separate ledgers instead of a guess about which
# source port belonged to whom. The alternate DNS port exists because several measured products
# special-case destination port 53, so an arm that can only use 53 measures the special case.
#
# Each instance is labelled `target:<port>`, the port it serves — not a run: an instance outlives every
# run it serves, and the analysis reads a run against the ledger of the target its own run.json names
# (README, "the target ledger").
set -euo pipefail

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
# shellcheck source=campaign.env
. "$script_dir/campaign.env"

usage() {
    cat <<'EOF'
usage: start-targets.sh [--dry-run] [--help]

Starts the two long-lived target instances on the Linux host. Addresses, ports and the ledger
directory come from campaign.env; the binary is <WF_PUB>/linux/WinForward.E2E.
EOF
}

need() {
    local name=$1
    if [ -z "${!name:-}" ]; then
        echo "$(basename "$0"): campaign.env: $name is unset or empty" >&2
        echo "  export it (values are documented in benchmarks/WinForward.E2E/AGENTS.local.md)" >&2
        exit 2
    fi
}

dry_run=0
while [ "$#" -gt 0 ]; do
    case "$1" in
        --dry-run) dry_run=1 ;;
        -h|--help) usage; exit 0 ;;
        *) usage >&2; exit 2 ;;
    esac
    shift
done

need WF_HOST_IP
need WF_PUB
need WF_LEDGER_DIR
need WF_TCP_PORT
need WF_UDP_PORT
need WF_DNS_PORT
need WF_DNS_PORT_ALT
need WF_TCP_PORT_DIRECT
need WF_UDP_PORT_DIRECT
need WF_DNS_PORT_DIRECT

binary=$WF_PUB/linux/WinForward.E2E
ledgers=$WF_LEDGER_DIR
bind=$WF_HOST_IP
[ -x "$binary" ] || { echo "no target binary at $binary — publish first" >&2; exit 1; }

main_args=(
    target --bind "$bind"
    --tcp-port "$WF_TCP_PORT" --udp-port "$WF_UDP_PORT"
    --dns-port "$WF_DNS_PORT" --dns-alt-port "$WF_DNS_PORT_ALT"
    --label "target:$WF_TCP_PORT"
    --ledger "$ledgers/ledger-main.jsonl"
)
direct_args=(
    target --bind "$bind"
    --tcp-port "$WF_TCP_PORT_DIRECT" --udp-port "$WF_UDP_PORT_DIRECT"
    --dns-port "$WF_DNS_PORT_DIRECT"
    --label "target:$WF_TCP_PORT_DIRECT"
    --ledger "$ledgers/ledger-direct.jsonl"
)

if [ "$dry_run" -eq 1 ]; then
    echo "=== dry run: no target started, no ledger removed ==="
    printf '  %q' "$binary"
    printf ' %q' "${main_args[@]}"
    printf '\n'
    printf '  %q' "$binary"
    printf ' %q' "${direct_args[@]}"
    printf '\n'
    exit 0
fi

mkdir -p "$ledgers"
rm -f "$ledgers/ledger-main.jsonl" "$ledgers/ledger-direct.jsonl"

pids=()
cleanup() {
    for pid in "${pids[@]}"; do kill "$pid" 2>/dev/null || true; done
    wait 2>/dev/null || true
}
trap cleanup EXIT INT TERM

"$binary" "${main_args[@]}" &
pids+=($!)

"$binary" "${direct_args[@]}" &
pids+=($!)

sleep 2
for pid in "${pids[@]}"; do
    kill -0 "$pid" 2>/dev/null || { echo "a target instance died on startup" >&2; exit 1; }
done

echo "proxied target  ${bind}:${WF_TCP_PORT} udp/${WF_UDP_PORT} dns/${WF_DNS_PORT} dns-alt/${WF_DNS_PORT_ALT}  -> $ledgers/ledger-main.jsonl"
echo "direct target   ${bind}:${WF_TCP_PORT_DIRECT} udp/${WF_UDP_PORT_DIRECT} dns/${WF_DNS_PORT_DIRECT}             -> $ledgers/ledger-direct.jsonl"
echo "both up; Ctrl-C to stop"
wait
