# Implementation plan: E2E benchmark remediation

Each batch is independently verifiable and committed on its own. The main session dispatches,
reviews and commits; it does not write code. Every subagent prompt names the task's
`implement.jsonl` / `check.jsonl` and the relevant `.trellis/spec` files.

## B0 — Decision ledger and task tree

- [x] Write `decisions.md`, `glossary.md`, `measurement-model.md` into the 10-11 review task.
- [x] Create the remediation parent and the three child tasks; link them.
- [ ] Add the superseded pointer to the 10-06 PRD.
- [ ] Fill every `implement.jsonl` / `check.jsonl`.
- [ ] `python3 ./.trellis/scripts/task.py validate <task>` for all four tasks.
- [ ] Commit `docs(e2e): ...` with only the task/doc files.

## B0a — Repair the repository gate (prerequisite)

- [ ] `dotnet build WinForward.slnx -c Release` currently fails with NU1902/NU1903 on
      `System.Security.Cryptography.Pkcs` 9.0.6 (transitive via
      `Microsoft.CodeAnalysis.Analyzer.Testing` → `NuGet.Packaging` 7.0.3).
- [ ] Add a direct `System.Security.Cryptography.Pkcs` reference at 10.0.13 (central version) in
      `tests/WinForward.Analyzers.Tests`, then run the build, format and inspectcode gates.
- [ ] Commit `chore(deps): ...` before the B0 docs commit.

## B1 — Archive and baseline analysis (evidence)

- [ ] Create `~/bench-archive/2026-10-06-validation/` and `~/bench-archive/2026-10-08-e5b2/`.
- [ ] Copy the campaign zip, both ledgers, `environment.json`, orchestrator logs and clock probes;
      write `SHA256SUMS` in each archive root.
- [ ] Write `benchmarks/results/2026-10-08-e5b2.manifest.json` with campaign id, caliber, plan
      hashes, file hashes and archive path. No raw data in git.
- [ ] Run the committed analyzer on the archived E5-b2 tree; store `analysis/` in the archive; add
      the baseline section of `reanalysis-notes.md`.
- [ ] Verify: `sha256sum -c SHA256SUMS`, analyzer exit 0, manifest hashes match.
- [ ] Commit `chore(e2e): ...`.

## B2 — Release plan and row profiles (harness)

- [ ] Add `plans-release/{full,udp,dns,dual,base}-plan.json` with the B0 arm durations and unchanged
      rates; add `stall` to the REL `modeMix` for B4.
- [ ] Update the orchestrator row table so `wf-aot-nativeudp` runs `full-plan.json` with `Dual = $true`;
      leave the deploy mapping to B7.
- [ ] Add the `wf-aot-nativeudp` full-plan row profile and keep `wf-aot-opt` as the UoT variant;
      update `RowProfiles.cs`, `verification/row-profiles.json`, README tables and the
      repo-file-to-row mapping.
- [ ] Commit the four modified WinForward configs (the current schema) so a campaign can deploy a
      commit; keep `wf-aot-opt` and `wf-fdd-opt` byte-identical.
- [ ] Verify: `dotnet test tests/WinForward.E2E.Tests -c Release`; analyzer tests;
      `check-fairness.py`; `check-fixture-drift.py`; README contract check.
- [ ] Commit `feat(e2e): ...`.

## B3 — Orchestrator P0 (harness)

- [ ] Clock: preflight probe with host timestamps; record `clockOffsetMs` (`vm - host`) and
      per-pass pre/post samples in `clock.jsonl`; default gate aborts only on probe failure,
      |offset| > 5 s, or >100 ms drift; `-RequireClockSync` enforces ±0.2 s for a clean campaign.
- [ ] Environment: read NIC offload, dynamic memory, product versions/hashes, power plan, driver
      versions and snapshot id before any `Set-*`; record every change in `environmentChanges`.
- [ ] `failures.json`; failed rows write a `run.json` skeleton; `declared-row-missing` finding.
- [ ] Clean-slate failures enter `failures.json` and the result tree.
- [ ] Product-row order randomization per pass (the campaign arms are rows; keep the control blocks
      fixed), with the permutation seed recorded.
- [ ] Defer the `connectionId` caption correction to the release-mode/oracle decision; do not
      change the frozen legacy `tables.md` in B3.
- [ ] Verify: `pwsh -NoProfile -Command "$null=[scriptblock]::Create((Get-Content -Raw ...))"`;
      C# tests; a short VM preflight (preflight + one row, no campaign).
- [ ] Commit `feat(e2e): ...`.

## B4 — Positive control and `stall` (harness)

- [ ] Target `--inject-delay-ms <n>` (deterministic, per-request stall) and a sensitivity plan that
      runs the reference row without a product.
- [ ] Record the effective delay in `<ledger-base>.injection.json` beside the ledger, never a new
      published ledger key; the legacy contract and `check-fixture-drift`/`oracle-diff` must stay
      unchanged.
- [ ] `stall` (2 s) in the REL `modeMix` of both `plans-release/full-plan.json` and
      `plans/full-plan.json`, so the release plan stays a duration-only variant; re-hash
      `plans-release/caliber.json` and keep the README's "only `seconds` changed" claim true.
- [ ] Tests: injection changes the observed p99.9; the sensitivity row fails when injection is on.
- [ ] Verify: `dotnet test tests/WinForward.E2E.Tests -c Release`; local self-test; sensitivity run.
- [ ] Commit `feat(e2e): ...`.

## B5 — Verdict and gate semantics (harness)

Read `research/b5-release-design.md` first (oracle strategy D15). The legacy
`tables.md`/`verdict.json` stay byte-identical; the release path is C#-only; **do not change the
frozen golden** for B5.

- [ ] B5a-lite: thresholds schema/parser, `--release`/`--thresholds`/`--min-passes`/`--discard-pass`
      (release-only), reference row/opponents, caliber validation at run time, public seam skeleton;
      no default output change; parser/validation tests.
- [ ] B5d-lite: `clock.jsonl` parser, `-offset` window shift, pre/post interpolation and residual,
      no-op when absent; public `ClockCorrection`; copied-tree test with the sign and a negative control.
- [ ] B5b: release comparison/state machine, Bonferroni-adjusted intervals, TOST family, REL
      capability, exploratory demotion, family/opponent logic; tests and named negative controls.
- [ ] B5c: gate matrix, `verdict.release.json`/`tables.release.md` writer, findings/limits; tests
      incl. the gate matrix and the `unevaluable` path.
- [ ] B5e: campaign invocation (`--release --thresholds ... --min-passes 6 --discard-pass 1`),
      B7 staging, README/report contract, `measurement-tooling.md` updates.
- [ ] Verify: legacy `oracle-diff.py`/boundary/fairness stay green; release tests and negative
      controls; a copied-tree clock test; `git diff` shows no frozen-golden change.
- [ ] Commit each slice separately (`feat(analysis): ...`).

## B6 — `BASE-singbox` (harness)

- [ ] Client `--socks5 <host:port>` (CONNECT only) and a control row that runs the BASE plan through
      sing-box.
- [ ] Orchestrator support; plan/profile entry; analysis row.
- [ ] Verify: E2E tests; a calibration-day record of the hop.
- [ ] Commit `feat(e2e): ...`.

## B7 — Reproducible driver (harness)

- [ ] Track `wf.sh`, `deploy-campaign.sh`, `start-targets.sh`, `publish-campaign.sh`; remove them
      from `.gitignore`.
- [ ] Parameterize addresses, ports, paths and credential variable names; commit a `campaign.env`
      with variable names only; machine values stay in `AGENTS.local.md`.
- [ ] `deploy.json` records `commit`, `dirty`, `dirtySummary`, `campaignEnvHash`, plan hashes and
      the release `caliber.json` hash.
- [ ] Stage `plans-release/*` as `C:\wfbench\e2e\{full,udp,dns,dual,base}-plan.json` and stage
      `caliber.json`; do not stage canonical `plans/` under the release label.
- [ ] B5e already runs the full 7-pass tree with `--discard-pass 1` from the repo cwd; B7 must
      track `publish-campaign.sh`, remove the `.gitignore` entry, and parameterize the remaining
      paths. Do not redo the invocation or stage only passes 2–7.
- [ ] `verify-vm.ps1` checks the VM prerequisites (ProxiFyre service, Proxifier service,
      ProxyBridge, ndisrd/ProxiFyreDrv/WinDivert, sing-box 1.14.2-singfix, `bench.ppx`,
      `ndisapi.dll`, `C:\wfbench` layout) and refuses with a named missing item.
- [ ] Verify: `bash -n`; `--help`/dry run; `verify-vm.ps1`; `git ls-files` shows the four scripts;
      a dirty-tree run refuses without `--allow-dirty`.
      refuses without `--allow-dirty`.
- [ ] Commit `feat(e2e): ...`.

## B8 — Calibration (campaign)

- [ ] Preflight: clock, firewall state, product versions, `BASE-singbox`, targets.
- [ ] Three back-to-back release runs on `wf-aot-nativeudp`; write `thresholds.json` with
      `B = max(B_business, 1.5 × spread)`; record the sensitivity proof.
- [ ] Archive the sensitivity run's `<ledger-base>.injection.json`, its ledger and the sensitivity
      plan; `publish-campaign.sh` does not collect the sidecar.
- [ ] Verify: all gates green; band file exists before B9; archive the calibration tree.
- [ ] Commit the calibration record/band file (not raw data).
- [ ] Report to the user: the band, the duration, and the release-night go/no-go.

## B9 — Release campaign and report (campaign)

- [x] Run seven passes (one discarded + six measured) under the release caliber; archive every
      attempt; write `attempt`.
- [x] Analyze the full 7-pass tree with `--min-passes 6 --discard-pass 1` and the pre-registered
      band; do not stage only 2–7; n=6 measured passes; generate the release tables/verdict.
- [x] Write the report with release caliber, primary family, band/MDD, coverage, attempts and the
      limits section; review it against the measurement model.
- [x] Verify: gates green; `sha256sum -c`; manifest matches; report review.
- [x] Commit the report and manifest (not raw data).
- [x] Report to the user: what the campaign supports and what it does not.

Result: `2026-10-19-release` (attempt 2), 7 passes / 6 measured, committed `d09ad7b`; report
`benchmarks/results/2026-10-19-release/README.md`, manifest
`benchmarks/results/2026-10-19-release.manifest.json`, archive
`~/bench-archive/2026-10-19-release/`. The memory family is not comparable (dynamic memory below
the pre-registered floor), so no primary inferential claim is published; loss is `unevaluable`,
REL is capability-fail, latency/CPU are report-only.


## B10 — Re-analysis and final acceptance

- [x] Re-run the new analyzer on the archived E5-b2 tree; update `reanalysis-notes.md` with the
      differences; do not change the old conclusions silently.
- [x] Update `.trellis/spec/` with any new contract, gate or lesson.
- [x] Final gates: `dotnet build WinForward.slnx -c Release`; `dotnet test WinForward.slnx -c Release`;
      `dotnet format ... --verify-no-changes --no-restore`; `jb inspectcode ...` with zero `<Issue>`.
- [x] Archive 10-11, the child tasks and the parent; update the goal to `complete`.

Result: re-analysis committed `0ffde74`; `measurement-tooling.md` carries the 111-key/403-path
record and the re-analysis bullets. Final gates ran at `d09ad7b`: build, test, format, inspectcode
(0 issues), oracle-diff, fixture-drift, fairness, boundary and README contract all exit 0.

