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
      rates; add `stall` to the MIX `modeMix` for B4.
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

- [ ] Clock gate: preflight round-trip to the target echo endpoint; write `clockOffsetMs`; abort
      above 0.2 s.
- [ ] Environment: read NIC offload, dynamic memory, product versions/hashes, power plan, driver
      versions and snapshot id before any `Set-*`; record every change in `environmentChanges`.
- [ ] `failures.json`; failed rows write a `run.json` skeleton; `declared-row-missing` finding.
- [ ] Clean-slate failures enter `failures.json` and the result tree.
- [ ] Arm-order randomization per pass, with the permutation seed recorded.
- [ ] Correct the `connectionId` table caption to "UTC-window attribution".
- [ ] Verify: `pwsh -NoProfile -Command "$null=[scriptblock]::Create((Get-Content -Raw ...))"`;
      C# tests; a short VM preflight (preflight + one row, no campaign).
- [ ] Commit `feat(e2e): ...`.

## B4 — Positive control and `stall` (harness)

- [ ] Target `--inject-delay-ms <n>` (deterministic, per-request stall) and a sensitivity plan that
      runs the reference row without a product.
- [ ] `stall` (2 s) in the release MIX `modeMix`.
- [ ] Tests: injection changes the observed p99.9; the sensitivity row fails when injection is on.
- [ ] Verify: `dotnet test tests/WinForward.E2E.Tests -c Release`; local self-test; sensitivity run.
- [ ] Commit `feat(e2e): ...`.

## B5 — Verdict and gate semantics (harness)

Split into three commits if the diff is large:

- [ ] B5a: `--thresholds <file>`; primary family and band data; `verdict.json` prints band, MDD,
      coverage and `min_passes`.
- [ ] B5b: band-aligned `different`; `unevaluable` for degenerate/zero-variance; `same` only in a
      declared TOST family; Holm family = reference vs each opponent; `min_passes` CLI.
- [ ] B5c: gate-into-verdict (gate failure invalidates the cell); UDP identity checks all prefixes;
      DNS `clientSendLoss` denominator.
- [ ] Update the golden/boundary trees only with a recorded semantic change in `FROZEN.md`; never
      regenerate a golden to hide a defect.
- [ ] Verify: analyzer tests; `python3 .../oracle-diff.py`; `check-boundary-trees.py`;
      `check-fairness.py`; a synthetic-tree run.
- [ ] Commit `feat(analysis): ...`.

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
- [ ] `deploy.json` records `commit`, `dirty`, `dirtySummary`, `campaignEnvHash`, plan hashes.
- [ ] Verify: `bash -n`; `--help`/dry run; `git ls-files` shows the four scripts; a dirty-tree run
      refuses without `--allow-dirty`.
- [ ] Commit `feat(e2e): ...`.

## B8 — Calibration (campaign)

- [ ] Preflight: clock, firewall state, product versions, `BASE-singbox`, targets.
- [ ] Three back-to-back release runs on `wf-aot-nativeudp`; write `thresholds.json` with
      `B = max(B_business, 1.5 × spread)`; record the sensitivity proof.
- [ ] Verify: all gates green; band file exists before B9; archive the calibration tree.
- [ ] Commit the calibration record/band file (not raw data).
- [ ] Report to the user: the band, the duration, and the release-night go/no-go.

## B9 — Release campaign and report (campaign)

- [ ] Run seven passes (one discarded + six measured) under the release caliber; archive every
      attempt; write `attempt`.
- [ ] Analyze with the pre-registered band; generate `tables.md` and `verdict.json`.
- [ ] Write the report with release caliber, primary family, band/MDD, coverage, attempts and the
      limits section; review it against the measurement model.
- [ ] Verify: gates green; `sha256sum -c`; manifest matches; report review.
- [ ] Commit the report and manifest (not raw data).
- [ ] Report to the user: what the campaign supports and what it does not.

## B10 — Re-analysis and final acceptance

- [ ] Re-run the new analyzer on the archived E5-b2 tree; update `reanalysis-notes.md` with the
      differences; do not change the old conclusions silently.
- [ ] Update `.trellis/spec/` with any new contract, gate or lesson.
- [ ] Final gates: `dotnet build WinForward.slnx -c Release`; `dotnet test WinForward.slnx -c Release`;
      `dotnet format ... --verify-no-changes --no-restore`; `jb inspectcode ...` with zero `<Issue>`.
- [ ] Archive the child tasks and the parent; update the goal to `complete`.
