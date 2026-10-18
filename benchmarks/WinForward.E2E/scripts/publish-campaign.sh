#!/usr/bin/env bash
# Download a campaign from the Windows VM and run the analysis on the full pass tree.
#
# The release path is the default: the downloaded tree keeps every pass, including the warm-up pass,
# because the release invocation discards pass 1 itself and release-inputs.json records that
# provenance. Trimming the tree before the analysis would leave it unable to say which pass was the
# warm-up. --legacy keeps the old invocation for diagnostics; its documents are not a release
# decision.
#
# Every machine value comes from campaign.env + the operator's environment. The analysis runs from
# the repository root with absolute --raw/--out/--thresholds paths, because the checked-in
# caliber.path is relative to that root.
set -euo pipefail

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
repo_root=$(cd "$script_dir/../../.." && pwd)
# shellcheck source=campaign.env
. "$script_dir/campaign.env"

usage() {
    cat <<'EOF'
usage: publish-campaign.sh [--dry-run] [--legacy] [<campaign-root> [<thresholds.json>]]

  <campaign-root>    local directory to hold raw/ and the analysis documents; defaults to
                     WF_CAMPAIGN_ROOT
  <thresholds.json>  the calibration's pre-registered thresholds file; defaults to WF_THRESHOLDS.
                     The release path analyses all seven passes with --min-passes 6 --discard-pass 1.
  --dry-run          resolve the invocation and print it; no VM call, no download, no analysis
  --legacy           run the legacy analysis (diagnostics only; no release verdict)
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

die() { echo "$(basename "$0"): $*" >&2; exit 2; }

dry_run=0
legacy=0
positional=()
while [ "$#" -gt 0 ]; do
    case "$1" in
        --dry-run) dry_run=1 ;;
        --legacy) legacy=1 ;;
        -h|--help) usage; exit 0 ;;
        -*) usage >&2; exit 2 ;;
        *) positional+=("$1") ;;
    esac
    shift
done

results=${positional[0]:-${WF_CAMPAIGN_ROOT:-}}
if [ -z "$results" ]; then
    usage >&2
    exit 2
fi
thresholds=
if [ "$legacy" -eq 0 ]; then
    thresholds=${positional[1]:-${WF_THRESHOLDS:-}}
fi

need WF_SCRATCH
need WF_RESULTS_ROOT
need WF_LEDGER_DIR
need WF_VM_ROOT
if [ "$dry_run" -eq 0 ]; then
    need WF_SESSION
fi
[ -f "$repo_root/benchmarks/WinForward.E2E.Analysis/scripts/analyze.sh" ] || die "no analyzer at $repo_root/benchmarks/WinForward.E2E.Analysis/scripts/analyze.sh"
command -v unzip >/dev/null 2>&1 || die "unzip is required to unpack the campaign"

if [ "$legacy" -eq 0 ]; then
    if [ -z "$thresholds" ]; then
        echo "$(basename "$0"): the release analysis needs the calibration's thresholds file" >&2
        echo "  pass it as the second argument or export WF_THRESHOLDS" >&2
        exit 2
    fi
    [ -f "$thresholds" ] || die "no thresholds file at '$thresholds'"
    thresholds=$(cd "$(dirname "$thresholds")" && pwd)/$(basename "$thresholds")
fi

vm_root=${WF_VM_ROOT%/}
vm_root=${vm_root%\\}
vm_results=${WF_RESULTS_ROOT%/}
vm_results=${vm_results%\\}
[ -n "$vm_root" ] || die "WF_VM_ROOT is empty after trimming separators"
[ -n "$vm_results" ] || die "WF_RESULTS_ROOT is empty after trimming separators"

if [ "$dry_run" -eq 0 ]; then
    mkdir -p "$results"
    results=$(cd "$results" && pwd)
else
    results=$(realpath -m "$results")
fi
raw=$results/raw
scratch=$WF_SCRATCH/publish
zip_remote=$vm_root\\campaign.zip
wf=$script_dir/wf.sh

if [ "$dry_run" -eq 1 ]; then
    echo "=== dry run: no VM call, no download, no analysis ==="
    echo "vm results root : $vm_results"
    echo "local campaign  : $results"
    echo "raw tree        : $raw"
    echo "ledgers         : $WF_LEDGER_DIR/ledger-main.jsonl, $WF_LEDGER_DIR/ledger-direct.jsonl"
    if [ "$legacy" -eq 1 ]; then
        echo "analysis        : legacy (--raw $raw --out $results)"
    else
        echo "analysis        : release (--raw $raw --out $results --release --thresholds $thresholds --min-passes 6 --discard-pass 1)"
    fi
    exit 0
fi

mkdir -p "$scratch" "$raw"

echo "=== 1. zip and download the campaign output ==="
"$wf" run "Remove-Item '$zip_remote' -ErrorAction SilentlyContinue; Compress-Archive -Path '$vm_results\\*' -DestinationPath '$zip_remote' -Force; 'zipped ' + [math]::Round((Get-Item '$zip_remote').Length / 1MB, 1) + ' MiB'" 600
"$wf" idle 180
rm -f "$scratch/campaign.zip"
"$wf" down "$zip_remote" "$scratch/campaign.zip" 900

echo "=== 2. extract into raw/ ==="
rm -rf "$raw"
mkdir -p "$raw"
cd "$raw"
unzip -o -q "$scratch/campaign.zip"
find . -type d -name '__MACOSX' -prune -exec rm -rf {} + 2>/dev/null || true

echo "=== 3. target-side ledgers ==="
# The two shipped target instances write separate ledgers: the proxied rows' traffic goes to
# ledger-main.jsonl and the dual phase's direct lane to ledger-direct.jsonl. The analysis finds
# either name in raw/ (LedgerLocator's search list).
found=0
for ledger in ledger-main.jsonl ledger-direct.jsonl; do
    if [ -f "$WF_LEDGER_DIR/$ledger" ]; then
        cp "$WF_LEDGER_DIR/$ledger" "$raw/$ledger"
        echo "$ledger lines: $(wc -l < "$raw/$ledger")"
        found=1
    fi
done
if [ "$found" -eq 0 ]; then
    echo "WARNING: no ledger at $WF_LEDGER_DIR/{ledger-main,ledger-direct}.jsonl" >&2
fi

echo "=== 4. deploy provenance ==="
if [ -f "$raw/deploy.json" ]; then
    python3 - "$raw/deploy.json" <<'PY'
import json
import sys

try:
    with open(sys.argv[1], "r", encoding="utf-8") as handle:
        deploy = json.load(handle)
except (OSError, ValueError) as error:
    print("WARNING: deploy.json is unreadable: " + str(error), file=sys.stderr)
else:
    summary = deploy.get("dirtySummary") or {}
    print("commit   " + str(deploy.get("commit")))
    print("dirty    " + str(deploy.get("dirty")) + " (" + str(summary.get("entries")) + " entries)")
    print("plan set " + str(deploy.get("planSet")))
    for plan in deploy.get("plans") or []:
        print("plan     " + str(plan.get("name")) + " " + str(plan.get("sha256"))[:12] +
              " -> " + str(plan.get("deployedAs")))
    caliber = deploy.get("caliber")
    if caliber:
        print("caliber  " + str(caliber.get("sha256"))[:12] + " -> " + str(caliber.get("deployedAs")))
PY
else
    echo "WARNING: raw/deploy.json is missing; this campaign cannot be tied to a deployed commit" >&2
fi

if [ "$legacy" -eq 0 ]; then
    for pass in 1 2 3 4 5 6 7; do
        [ -d "$raw/pass$pass" ] || die "'$raw/pass$pass' is missing; the release analysis needs the full seven-pass tree (one discarded + six measured), not a trimmed one"
    done
fi

echo "=== 5. analyse ==="
cd "$repo_root"
if [ "$legacy" -eq 0 ]; then
    bash "$repo_root/benchmarks/WinForward.E2E.Analysis/scripts/analyze.sh" \
        --raw "$raw" --out "$results" \
        --release --thresholds "$thresholds" --min-passes 6 --discard-pass 1
else
    echo "legacy diagnostics only: no release verdict is written"
    bash "$repo_root/benchmarks/WinForward.E2E.Analysis/scripts/analyze.sh" --raw "$raw" --out "$results"
fi

echo "=== done ==="
ls -la "$results"
if [ "$legacy" -eq 0 ]; then
    for artifact in release-inputs.json verdict.release.json tables.release.md findings.release.json; do
        if [ ! -f "$results/$artifact" ]; then
            echo "$(basename "$0"): the release run wrote no '$artifact'" >&2
            exit 1
        fi
    done
    echo "release artifacts: release-inputs.json verdict.release.json tables.release.md findings.release.json"
    echo "archive them together with raw/deploy.json, thresholds.json and its optional coverage.json"
fi
