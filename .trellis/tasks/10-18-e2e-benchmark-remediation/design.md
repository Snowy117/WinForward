# Design: E2E benchmark remediation

## Context

The 10-11 review produced a claim-boundary decision, a release caliber, a primary family, an
instrument P0 list, a verdict-model direction and an evidence plan. This task turns those into
committed code, a calibrated campaign and a report.

## Workstreams

| Child | Scope | Batches |
| --- | --- | --- |
| `10-18-e2e-benchmark-evidence` | Archive, manifest, baseline analysis, old-data re-analysis, report template | B1, B10 |
| `10-18-e2e-benchmark-harness` | Release plan, row profiles, orchestrator gates, positive control, verdict model, `BASE-singbox`, reproducible driver | B2–B7 |
| `10-18-e2e-benchmark-campaign` | Calibration night, release night, report, archive | B8–B9 |

The parent owns B0 (decision ledger and task tree), cross-batch review and final acceptance.

## Batch plan

| Batch | Scope | Main outputs | Verification | Depends |
| --- | --- | --- | --- | --- |
| B0 | Decision ledger and task tree | 10-11 research docs; parent/child task docs; 10-06 pointer | `task.py validate`; doc consistency; `git diff` review | — |
| B0a | Repair the repository gate | direct package override for the vulnerable transitive `System.Security.Cryptography.Pkcs` | `dotnet build`; `dotnet format`; `jb inspectcode` zero issues | — |
| B1 | Archive and baseline | `~/bench-archive/2026-10-06-validation/`, `~/bench-archive/2026-10-08-e5b2/`, `SHA256SUMS`, checked-in manifest, baseline analysis | `sha256sum -c`; `analyze.sh` exit 0; manifest fields | B0 |
| B2 | Release plan and row profiles | `plans-release/`, orchestrator row plan, `RowProfiles`, `row-profiles.json`, committed configs, README | E2E tests; analyzer tests; fairness/fixture/boundary checks | B0 |
| B3 | Orchestrator P0 | clock probe + drift gate, environment read-before-change, `failures.json`/failed rows, clean-slate, arm-order randomization; caption deferred to the release mode | `pwsh` parse; C# tests; short VM preflight | B2 |
| B4 | Positive control and `stall` | target delay injection, sensitivity plan, `stall` in the REL `modeMix`, tests | E2E tests; local self-test; sensitivity run | B2 |
| B5 | Release path (oracle strategy B) | thresholds schema, primary family, Bonferroni-adjusted band, `unevaluable`, TOST, REL capability, clock correction, gate matrix, release outputs; legacy frozen | legacy `oracle-diff`/boundary/fairness green; release tests + negative controls; copied-tree clock | B2, B4 |
| B6 | `BASE-singbox` | client CONNECT-only SOCKS5, control row, plan, analysis support | E2E tests; VM calibration record | B3–B5 |
| B7 | Reproducible driver | tracked/parameterized scripts, `campaign.env`, `deploy.json` | `bash -n`; dry run; `git ls-files` | B2 |
| B8 | Calibration | three reference runs, pre-registered band, `BASE-singbox` record, sensitivity proof | gates green; band file written before B9 | B3–B7 |
| B9 | Release campaign | seven passes (one discarded + six measured), report, archive, manifest | gates green; `sha256sum -c`; report review | B8 |
| B10 | Re-analysis and acceptance | old-data re-analysis, notes, spec updates, final gates | new analyzer on old tree; build/test/format/inspectcode; goal complete | B9 |

## Contracts

### Release plan

`benchmarks/WinForward.E2E/scripts/plans-release/full-plan.json` (and the `udp`, `dns`, `dual`,
`base` siblings) keeps every `ratePerSecond` from `plans/` and changes only durations:

| Arm | IDLE | LAT | LATLOAD | DNS | DNSALT | LOSS | REL | THRU | MIX | PERSIST |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Seconds | 30 | 30 | 30 | 30 | 30 | 120 | 60 | 30 | 60 | 60 |

`REL` stays 20/s. `LOSS` stays 120 s because of the tracker's 2^18−1 sequence space. `MIX`
`modeMix` gains `stall`. The release caliber is identified by the plan file, its content hash and
these declared durations.

### Thresholds and family file

The calibration produces a campaign-local `thresholds.json`:

```json
{
  "caliber": {"plan": "full-plan.json", "sha256": "..."},
  "warmupSeconds": 5,
  "primary": ["lat.tcp_rtt.p50", "cpu.proxy.vcpuPct", "mem.privateBytes.p50", "loss.lossRate"],
  "equivalence": [],
  "metrics": {
    "lat.tcp_rtt.p50": {"band": 0.18, "business": 0.05, "spread": 0.12, "source": "calibration"},
    "cpu.proxy.vcpuPct": {"band": 0.18, "business": 0.10, "spread": 0.12, "source": "calibration"}
  }
}
```

The analyzer takes it with `--thresholds <file>`; without the flag it keeps the historical constants
and says so in `verdict.json`. The primary family is data, not a hard-coded list.

### Orchestrator artifacts

- `environment.json` adds `clockOffsetMs`, `nicOffload`, `dynamicMemory`, `productVersions`,
  `powerPlan`, `driverVersions`, `snapshotId`, `environmentChanges`. Fields are read before any
  `Set-*`; every change is recorded as an `environmentChange` entry. Per-pass pre/post clock
  samples go to `clock.jsonl` (pass, phase, offsetMs, rttMs, vmUtc, hostUtc), with
  `clockOffsetMs = vm - host`.
- `failures.json` lists every failed row/arm with a reason. A row that did not start writes a
  `run.json` skeleton with `failed: true` and a reason, so `LooksLikeRow` sees it and the
  `declared-row-missing` finding can fire.
- `attempt` is the campaign ordinal; the attempt record names previous voided attempts and why.
- `deploy.json` records `commit`, `dirty`, `dirtySummary`, `campaignEnvHash` and the deployed plan
  hashes.

### Verdict

- `different`: the bootstrap interval excludes the band; the test null is "the ratio is inside the
  band".
- `same`/`equivalent`: only in a pre-declared TOST family, unadjusted and declared.
- `unevaluable`: degenerate interval, zero variance, or fewer than `min_passes` measured passes.
- Holm family: per primary metric, reference row vs each opponent.
- Coverage: the simulated coverage for the measured n is printed in `verdict.json`.

## Concurrency

Default is serial. Two pairs have disjoint file sets and may run concurrently:

- B1 (archive/manifest) with B2 (plan/row profiles) after B0.
- B4 (target/plans) with B5 (analysis) after B2, if B5 does not touch the client or the plans.

Any concurrent pair gets a structure review before commit. No code changes run during B8/B9.

## Rollback

Each batch is one commit; the previous commit is the rollback point. Campaign data is archived
before analysis. A failed campaign keeps its archive and attempt record; it is never silently
deleted. If B5 changes the golden fixture, the semantic change is recorded in `FROZEN.md` and a new
boundary tree is added; regenerating the golden to make a test pass is not an acceptable rollback.

## Risks

| Risk | Mitigation |
| --- | --- |
| Golden/oracle drift in B5 | Plan review before implementation; explicit semantic diff; new boundary tree; `FROZEN.md` entry |
| Orchestrator cannot be exercised on Linux | `pwsh` parse check plus a short VM preflight before the calibration night |
| VM clock or availability | Clock gate; preflight; attempt archive; calibration uses the same session |
| Tracking the driver exposes machine-local values | `campaign.env` holds variable names, `AGENTS.local.md` holds values; `.gitignore` keeps secrets out |
| Release night exceeds the window | Pre-declared MIX/PERSIST 45 s fallback; duration measured in calibration |
| `BASE-singbox` adds scope | Calibration-only control; not a per-pass product row |

## Subagent context rule

Every implementation or check prompt must tell the subagent to read, in order:

1. its task's `implement.jsonl` / `check.jsonl`;
2. `prd.md`, `design.md`, `implement.md`;
3. `.trellis/spec/backend/index.md` and the spec files the manifest lists, plus any adjacent spec
   its change touches (`benchmark-methodology.md`, `measurement-record-contract.md`,
   `measurement-judgement.md`, `measurement-harness.md`, `measurement-tooling.md`,
   `measurement-run-lifecycle.md`, `quality-guidelines.md`, `directory-structure.md`,
   `error-handling.md`);
4. `AGENTS.md` and, for VM work, `benchmarks/WinForward.E2E/AGENTS.local.md`.

Subagents never run `git commit` or `git push`; the main session commits.
