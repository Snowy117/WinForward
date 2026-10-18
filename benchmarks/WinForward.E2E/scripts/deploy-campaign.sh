#!/usr/bin/env bash
# Stage the clients, product configurations, release plans and orchestrator on the Windows VM.
#
# Every machine value comes from campaign.env + the operator's environment. The deploy records what
# it staged in <results-root>\deploy.json: the commit, the dirty state and summary, a hash of the
# effective campaign environment, and the SHA-256 of every plan plus the release caliber. A dirty
# worktree is refused unless --allow-dirty is passed, because a campaign deploys a commit, not a
# worktree.
#
# The release plan set is scripts/plans-release; it is staged as C:\wfbench\e2e\*.  The canonical
# scripts/plans set is a diagnostic set and is staged under a different root (e2e-canonical), never
# under the release label.
set -euo pipefail

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
e2e_dir=$(cd "$script_dir/.." && pwd)
repo_root=$(cd "$e2e_dir/../.." && pwd)
# shellcheck source=campaign.env
. "$script_dir/campaign.env"

usage() {
    cat <<'EOF'
usage: deploy-campaign.sh [--dry-run] [--allow-dirty] [--plan-set release|canonical|windows]

  --dry-run        validate and write the local deploy record, but make no VM calls and stage nothing
  --allow-dirty    deploy a dirty worktree; the record keeps dirty=true and the status summary
  --plan-set       override WF_PLAN_SET for this invocation

WF_PLAN_SET selects the plan set; the release runbook sets it to release. release stages
scripts/plans-release/{full,udp,dns,dual,base}-plan.json and caliber.json under <WF_VM_ROOT>\e2e.
canonical stages scripts/plans under \e2e-canonical, and windows stages
plans-windows/full-shape-plan.json plus the canonical siblings under \e2e-win.
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
allow_dirty=0
plan_set=
while [ "$#" -gt 0 ]; do
    case "$1" in
        --dry-run) dry_run=1 ;;
        --allow-dirty) allow_dirty=1 ;;
        --plan-set)
            shift
            [ "$#" -gt 0 ] || die "--plan-set needs a value"
            plan_set=$1
            ;;
        --plan-set=*) plan_set=${1#*=} ;;
        -h|--help) usage; exit 0 ;;
        *) usage >&2; exit 2 ;;
    esac
    shift
done

if [ -z "$plan_set" ]; then
    need WF_PLAN_SET
    plan_set=$WF_PLAN_SET
fi
case "$plan_set" in
    release|canonical|windows) ;;
    *) die "unknown plan set '$plan_set'; expected release, canonical or windows" ;;
esac
# The record hashes the effective campaign environment, so the CLI override must win here too.
export WF_PLAN_SET=$plan_set

need WF_VM_ROOT
need WF_RESULTS_ROOT
need WF_SCRATCH
need WF_PUB
need WF_PROXIFIER_PROFILE
if [ "$dry_run" -eq 0 ]; then
    need WF_SESSION
fi

[ -d "$WF_PUB/win" ] || die "no published Windows client at '$WF_PUB/win' (run scripts/publish.sh first)"
[ -d "$WF_PUB/win-direct" ] || die "no published direct client at '$WF_PUB/win-direct' (run scripts/publish.sh first)"
[ -f "$WF_PROXIFIER_PROFILE" ] || die "no Proxifier profile at '$WF_PROXIFIER_PROFILE' (WF_PROXIFIER_PROFILE)"

command -v python3 >/dev/null 2>&1 || die "python3 is required to write deploy.json"

pub=$(cd "$WF_PUB" && pwd)
vm_root=${WF_VM_ROOT%/}
vm_root=${vm_root%\\}
results_root=${WF_RESULTS_ROOT%/}
results_root=${results_root%\\}
[ -n "$vm_root" ] || die "WF_VM_ROOT is empty after trimming separators"
[ -n "$results_root" ] || die "WF_RESULTS_ROOT is empty after trimming separators"

scratch=$WF_SCRATCH
mkdir -p "$scratch"
deploy_json=$scratch/deploy.json
manifest=$scratch/deploy-manifest.tsv
rm -f "$deploy_json" "$manifest"

echo "=== deploy record ==="
python3 - "$deploy_json" "$manifest" "$repo_root" "$e2e_dir" "$plan_set" "$vm_root" "$results_root" "$allow_dirty" <<'PY'
import datetime
import hashlib
import json
import ntpath
import os
import re
import subprocess
import sys

deploy_path, manifest_path, repo_root, e2e_dir, plan_set, vm_root, results_root, allow_dirty = sys.argv[1:9]
allow_dirty = allow_dirty == "1"


def fail(message, code=3):
    print("deploy-campaign.sh: " + message, file=sys.stderr)
    sys.exit(code)


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def git(*args):
    result = subprocess.run(["git", "-C", repo_root, *args], capture_output=True, text=True)
    if result.returncode != 0:
        fail("git " + " ".join(args) + " failed: " + result.stderr.strip())
    return result.stdout


scripts = os.path.join(e2e_dir, "scripts")
plan_names = ["full-plan.json", "udp-plan.json", "dns-plan.json", "dual-plan.json", "base-plan.json"]
if plan_set == "release":
    target_root = ntpath.join(vm_root, "e2e")
    sources = [(name, os.path.join(scripts, "plans-release", name)) for name in plan_names]
    caliber_path = os.path.join(scripts, "plans-release", "caliber.json")
elif plan_set == "canonical":
    target_root = ntpath.join(vm_root, "e2e-canonical")
    sources = [(name, os.path.join(scripts, "plans", name)) for name in plan_names]
    caliber_path = None
else:
    target_root = ntpath.join(vm_root, "e2e-win")
    sources = []
    for name in plan_names:
        if name == "full-plan.json":
            sources.append((name, os.path.join(scripts, "plans-windows", "full-shape-plan.json")))
        else:
            sources.append((name, os.path.join(scripts, "plans", name)))
    caliber_path = None

for name, source in sources:
    if not os.path.isfile(source):
        fail("missing plan " + source)

plan_hashes = {name: sha256(source) for name, source in sources}
caliber = None
if caliber_path is not None:
    if not os.path.isfile(caliber_path):
        fail("missing release caliber " + caliber_path)
    with open(caliber_path, "r", encoding="utf-8") as handle:
        caliber_document = json.load(handle)
    if caliber_document.get("plan") != "full-plan.json":
        fail("caliber.json names plan '" + str(caliber_document.get("plan")) + "', expected full-plan.json")
    if caliber_document.get("sha256") != plan_hashes["full-plan.json"]:
        fail(
            "caliber.json sha256 " + str(caliber_document.get("sha256")) +
            " does not match full-plan.json " + plan_hashes["full-plan.json"]
        )
    caliber = {
        "source": os.path.relpath(caliber_path, repo_root).replace(os.sep, "/"),
        "sha256": sha256(caliber_path),
        "deployedAs": ntpath.join(target_root, "caliber.json"),
    }

commit = git("rev-parse", "HEAD").strip()
branch = git("rev-parse", "--abbrev-ref", "HEAD").strip()
status = [line for line in git("status", "--porcelain=v1", "--untracked-files=normal").splitlines() if line.strip()]
dirty = bool(status)
if dirty and not allow_dirty:
    print("deploy-campaign.sh: refusing to deploy a dirty worktree (" + str(len(status)) + " entr" +
          ("y" if len(status) == 1 else "ies") + "):", file=sys.stderr)
    for line in status[:20]:
        print("  " + line, file=sys.stderr)
    if len(status) > 20:
        print("  ... " + str(len(status) - 20) + " more", file=sys.stderr)
    print("  commit the worktree, or pass --allow-dirty to record it as dirty", file=sys.stderr)
    sys.exit(1)
if dirty:
    print("deploy-campaign.sh: deploying a dirty worktree (--allow-dirty); the record keeps dirty=true",
          file=sys.stderr)

env_names = re.findall(
    r'^: "\$\{([A-Z0-9_]+):=\}".*$',
    open(os.path.join(scripts, "campaign.env"), "r", encoding="utf-8").read(),
    re.MULTILINE,
)
if not env_names:
    fail("campaign.env declares no WF_* names")
env_lines = []
for name in sorted(env_names):
    value = os.environ.get(name, "")
    if any(token in name for token in ("PASSWORD", "SECRET", "TOKEN")):
        env_lines.append(name + ("=<redacted>" if value else "=<unset>"))
    else:
        env_lines.append(name + "=" + value)
campaign_env_hash = hashlib.sha256("\n".join(env_lines).encode("utf-8")).hexdigest()

plans = [
    {
        "name": name,
        "source": os.path.relpath(source, repo_root).replace(os.sep, "/"),
        "sha256": plan_hashes[name],
        "deployedAs": ntpath.join(target_root, name),
    }
    for name, source in sources
]

record = {
    "schema": "deploy/1",
    "generatedUtc": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    "commit": commit,
    "branch": branch,
    "dirty": dirty,
    "dirtySummary": {
        "entries": len(status),
        "tracked": sum(1 for line in status if not line.startswith("??")),
        "untracked": sum(1 for line in status if line.startswith("??")),
        "truncated": len(status) > 20,
        "sample": status[:20],
    },
    "campaignEnvHash": campaign_env_hash,
    "campaignEnvKeys": sorted(env_names),
    "planSet": plan_set,
    "vmRoot": vm_root,
    "resultsRoot": results_root,
    "plans": plans,
    "caliber": caliber,
}

with open(deploy_path, "w", encoding="utf-8") as handle:
    json.dump(record, handle, indent=2)
    handle.write("\n")

with open(manifest_path, "w", encoding="utf-8") as handle:
    for name, source in sources:
        handle.write(source + "\t" + ntpath.join(target_root, name) + "\n")
    if caliber is not None:
        handle.write(caliber_path + "\t" + caliber["deployedAs"] + "\n")

print("commit   " + commit[:12] + (" (dirty)" if dirty else " (clean)"))
print("plan set " + plan_set + " -> " + target_root)
print("record   " + deploy_path)
PY

while IFS=$'\t' read -r source target; do
    printf '  %-16s %s -> %s\n' "$(basename "$source")" "$(sha256sum "$source" | cut -c1-12)" "$target"
done < "$manifest"
echo

if [ "$dry_run" -eq 1 ]; then
    echo "=== dry run: no VM calls, nothing staged ==="
    echo "would upload $deploy_json to $results_root\\deploy.json"
    exit 0
fi

wf=$script_dir/wf.sh
"$wf" idle 120 >/dev/null 2>&1 || true

echo "=== create the layout ==="
"$wf" run "\$root='$vm_root'; New-Item -ItemType Directory -Force -Path \$root | Out-Null; foreach (\$d in @('e2e', 'e2e-direct', 'wf-aot', 'wf-fdd', 'proxifier', 'proxybridge', 'stage', 'e2e-canonical', 'e2e-win')) { New-Item -ItemType Directory -Force -Path (Join-Path \$root \$d) | Out-Null }; \$out='$results_root'; New-Item -ItemType Directory -Force -Path \$out | Out-Null; 'layout ready'" 300 >/dev/null

stage() {
    local local_path=$1 remote_path=$2 want have
    if [ ! -f "$local_path" ]; then
        printf '  FAIL %-42s local file missing\n' "$(basename "$local_path")" >&2
        return 1
    fi
    want=$(stat -c %s "$local_path")
    "$wf" idle 120 >/dev/null 2>&1 || true
    "$wf" up "$local_path" "$remote_path" 600 >/dev/null 2>&1 || true
    have=$("$wf" run "(Get-Item '$remote_path' -ErrorAction SilentlyContinue).Length" 120 2>/dev/null | tr -d '\r' | tail -1)
    if [ "$want" = "$have" ]; then
        printf '  ok   %-42s %s bytes\n' "$(basename "$remote_path")" "$have"
    else
        printf '  FAIL %-42s local %s, remote %s\n' "$(basename "$remote_path")" "$want" "${have:-absent}"
        return 1
    fi
    sleep 1
}

echo "=== clients ==="
for f in WinForward.E2E.exe WinForward.E2E.dll WinForward.E2E.Contracts.dll WinForward.E2E.deps.json WinForward.E2E.runtimeconfig.json; do
    stage "$pub/win/$f" "$vm_root\\e2e\\$f"
done
for f in WinForward.E2E.Direct.exe WinForward.E2E.Direct.dll WinForward.E2E.Contracts.dll WinForward.E2E.Direct.deps.json WinForward.E2E.Direct.runtimeconfig.json; do
    stage "$pub/win-direct/$f" "$vm_root\\e2e-direct\\$f"
done

echo "=== product configurations ==="
stage "$e2e_dir/scripts/configs/wf-aot-opt.json"       "$vm_root\\wf-aot\\config.json"
stage "$e2e_dir/scripts/configs/wf-aot-nativeudp.json" "$vm_root\\wf-aot\\config-nativeudp.json"
stage "$e2e_dir/scripts/configs/wf-aot-dnsrelay.json"  "$vm_root\\wf-aot\\config-dnsrelay.json"
stage "$e2e_dir/scripts/configs/wf-fdd-opt.json"       "$vm_root\\wf-fdd\\config.json"
stage "$e2e_dir/scripts/configs/proxybridge.pbprofile" "$vm_root\\proxybridge\\bench.pbprofile"
stage "$e2e_dir/scripts/configs/proxifyre-app-config.json" "$vm_root\\stage\\proxifyre-app-config.json"
stage "$WF_PROXIFIER_PROFILE"                          "$vm_root\\proxifier\\bench.ppx"

echo "=== plans ($plan_set) ==="
while IFS=$'\t' read -r source target; do
    stage "$source" "$target"
done < "$manifest"

echo "=== orchestrator ==="
stage "$e2e_dir/scripts/orchestrator.ps1" "$vm_root\\orchestrator.ps1"

echo "=== ProxiFyre reads its config from Program Files, so copy it into place ==="
# Copy-Item can fail (Program Files\ProxiFyre missing, a locked target) while the REPL still ends at a
# prompt. The token is the deploy's only proof that the copy happened, and the hash comparison aborts
# on a partial or transformed file.
if ! copy_out=$("$wf" run "\$ErrorActionPreference='Stop'; try { \$source='$vm_root\\stage\\proxifyre-app-config.json'; \$target='C:\\Program Files\\ProxiFyre\\app-config.json'; Copy-Item -LiteralPath \$source -Destination \$target -Force -ErrorAction Stop; if (-not (Test-Path -LiteralPath \$target -PathType Leaf)) { throw 'the copied file is missing' }; \$sourceHash=(Get-FileHash -LiteralPath \$source -Algorithm SHA256).Hash; \$targetHash=(Get-FileHash -LiteralPath \$target -Algorithm SHA256).Hash; if (\$sourceHash -ne \$targetHash) { throw ('sha256 mismatch: ' + \$sourceHash + ' != ' + \$targetHash) }; 'PROXIFYRE_COPY_OK ' + \$targetHash } catch { 'PROXIFYRE_COPY_FAIL ' + \$_.Exception.Message }" 200 2>&1); then
    printf '%s\n' "$copy_out" >&2
    echo "$(basename "$0"): the ProxiFyre app-config copy command failed (REPL timeout)" >&2
    exit 1
fi
printf '%s\n' "$copy_out"
if ! grep -q '^PROXIFYRE_COPY_OK ' <<< "$copy_out"; then
    echo "$(basename "$0"): ProxiFyre app-config was not verified in Program Files; the proxifyre row cannot run" >&2
    exit 1
fi

echo "=== deploy record on the VM ==="
stage "$deploy_json" "$results_root\\deploy.json"

echo "=== done ==="
