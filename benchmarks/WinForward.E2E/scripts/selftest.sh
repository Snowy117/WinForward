#!/usr/bin/env bash
# A green run here says the harness works, never that a product was measured: there is no proxy in
# this loop. Ports are deliberately unusual so this can run while a campaign target is up.
set -euo pipefail

repo=$(cd "$(dirname "$0")/.." && pwd)
pub=${WF_PUB:-${TMPDIR:-/tmp}/wf-bench/pub}/linux
work=/tmp/wf-bench/selftest
tcp=31010
dns=5301
plan=${1:-}

# The usage check runs before anything is started: a missing plan argument must not leave a target
# running behind it, and it has to be a non-zero exit so a caller cannot read it as a green run.
if [ -z "$plan" ]; then
    echo "usage: $0 <plan.json>" >&2
    echo "shipped plans: $repo/scripts/plans/*.json, $repo/scripts/plans-short/*.json and $repo/scripts/plans-windows/*.json" >&2
    exit 2
fi

mkdir -p "$work"

if [ ! -x "$pub/WinForward.E2E" ]; then
    echo "publishing the Linux build"
    ( cd "$repo" && dotnet publish -c Release -r linux-x64 --self-contained false -o "$pub" >/dev/null )
fi

cleanup() {
    [ -n "${target_pid:-}" ] && kill "$target_pid" 2>/dev/null || true
    wait 2>/dev/null || true
}
trap cleanup EXIT

rm -f "$work/ledger.jsonl"
"$pub/WinForward.E2E" target --bind 127.0.0.1 --tcp-port "$tcp" --udp-port "$tcp" --dns-port "$dns" \
    --dns-alt-port "$((dns + 1))" --label selftest --ledger "$work/ledger.jsonl" >"$work/target.out" 2>&1 &
target_pid=$!
sleep 1

rm -rf "$work/out"
client_status=0
"$pub/WinForward.E2E" client --target 127.0.0.1 --tcp-port "$tcp" --udp-port "$tcp" --dns-port "$dns" \
    --plan "$plan" --out "$work/out" --label selftest >"$work/client.out" 2>&1 || client_status=$?
tail -20 "$work/client.out"
if [ "$client_status" -ne 0 ]; then
    echo "selftest: the client exited $client_status; its full output is in $work/client.out" >&2
    exit "$client_status"
fi

echo
echo "=== every result record, arm by arm ==="
python3 - "$work/out" <<'PY'
import json, os, sys
outdir = sys.argv[1]
for name in sorted(os.listdir(outdir)):
    if not name.endswith('.jsonl'):
        continue
    for line in open(os.path.join(outdir, name), encoding='utf-8-sig'):
        if not line.strip().startswith('{'):
            continue
        record = json.loads(line)
        if record.get('type') != 'result':
            continue
        metrics = record.get('metrics', {})
        print('--- %s (%s) ---' % (record['arm'], record['kind']))
        for key in sorted(metrics):
            value = metrics[key]
            if isinstance(value, dict):
                inner = ', '.join('%s=%s' % (k, v) for k, v in sorted(value.items()))
                print('    %-34s %s' % (key, inner[:150]))
            else:
                print('    %-34s %s' % (key, value))
        for cls, hist in sorted(record.get('latency', {}).items()):
            if hist.get('count'):
                print('    [%-14s] n=%-8s p50=%-10s p99=%s' % (cls, hist.get('count'), hist.get('p50Us'), hist.get('p99Us')))
        for note in record.get('notes', []):
            print('    note: %s' % note)
PY
