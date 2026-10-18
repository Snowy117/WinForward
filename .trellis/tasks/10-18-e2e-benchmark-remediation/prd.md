# E2E benchmark remediation: release caliber, instrument gates, campaign

## Goal

Turn the 10-11 design-review decisions into a release-grade E2E benchmark: a declared release
caliber and measurement model, a falsifiable instrument, a calibrated 7-pass campaign, and a report
whose claims stay inside the measured evidence.

## Why

The 10-11 review found that the current benchmark cannot support a product ranking: seven rows are
seven factor bundles, the default inference degenerates at n=4, the thresholds sit inside the
run-to-run noise, the gates do not invalidate verdict cells, the environment and driver chain are
not reproducible, and the raw data lives only in `/tmp`. The agreed remediation is not "more runs";
it is to make the claim boundary, the instrument and the evidence chain match.

## Scope

- Release caliber: `plans-release/full-plan.json`, rate-preserving, declared arm durations.
- Row profiles: both WinForward configurations run the full plan. `wf-aot-nativeudp` (default) is
  the reference row; `wf-aot-opt` (opt-in UoT) is a declared variant.
- Instrument: positive control, clock gate, environment read-before-change, gate-into-verdict,
  arm-order randomization, clean-slate failures, `failures.json`, `BASE-singbox` calibration control.
- Verdict model: four inferential primary metrics plus a REL capability gate, a noise-derived band,
  `unevaluable` instead of degenerate `same`, a band-aligned test, and a Holm family of
  reference-vs-opponent comparisons.
- Campaign: one calibration night (three reference runs), one 7-pass release night (one discarded +
  six measured), a report, an archive and a checked-in manifest.
- Reproducibility: archived raw data with checksums; a tracked and parameterized campaign driver
  with a `deploy.json` commit/dirty record.

## Non-goals

- No product-ranking claim. No absolute-latency claim. No external-network extrapolation.
- QUIC/HTTP3, true blinding, IPv6, kernel DPC/ISR and the `connectionId` implementation are out of
  scope. The `connectionId` table caption is corrected to match the implemented UTC-window
  attribution.
- No product-side performance work under `src/`.

## Requirements

1. `decisions.md`, `glossary.md` and `measurement-model.md` in the 10-11 review task capture the
   agreed decisions and the claim boundary.
2. The release plan and row profiles are committed; a campaign deploys a commit, not a worktree.
3. Every P0 instrument gate is proven to fail at least once (positive control, clock gate,
   gate-into-verdict, clean-slate).
4. The verdict model reports `unevaluable` for degenerate intervals, uses a noise-derived band per
   primary metric, and applies Holm only to the pre-declared reference-vs-opponent family.
5. The calibration night produces a pre-registered band and a recorded `BASE-singbox` control; the
   release campaign runs seven passes with attempt numbers and archives every attempt.
6. The report prints the release caliber, primary family, band/MDD, coverage, attempts and the
   limits section.
7. Raw data is archived with `SHA256SUMS`; a checked-in manifest records the campaign id, plan
   hashes, file hashes and archive path.
8. The old E5-b2 tree is re-analyzed under the new rules and the differences are recorded.

## Acceptance Criteria

- [ ] 10-11 research docs exist and agree with the campaign plan.
- [ ] Parent and three child tasks exist, are linked, and have non-empty `implement.jsonl` /
      `check.jsonl`.
- [ ] Release plan and row profiles are committed; `dotnet build WinForward.slnx -c Release` has
      zero warnings and `dotnet test WinForward.slnx -c Release` is green.
- [ ] `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` is empty;
      `jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx` has zero `<Issue>`.
- [ ] `check-fairness.py`, `check-boundary-trees.py`, `check-fixture-drift.py` and `oracle-diff.py`
      pass on the committed analyzer.
- [ ] Positive control moves p99.9 to about 1 s; the clock gate aborts on probe failure, an
      absolute offset above 5 s, or more than 100 ms drift (with `-RequireClockSync` enforcing
      ±0.2 s); the environment block records the required fields before any change; a gate failure
      invalidates the verdict cell; arm order is randomized.
- [ ] The calibration band is written before the release campaign; the release campaign has seven
      archived passes (one discarded + six measured) with attempt metadata.
- [ ] The report contains the release caliber, primary family, band/MDD, coverage, attempts and the
      limits section.
- [ ] The archive verifies with `sha256sum -c`; the manifest is checked in.
- [ ] E5-b2 re-analysis notes exist and the new rules are reproducible.
- [ ] 10-06 has a superseded pointer; 10-11 is archived; the goal is completed.

## Constraints

- Two machines only: the Linux host and the Windows VM. No new hardware and no network emulation.
- One calibration night and one release night. If the window is 8–9 hours, use the MIX/PERSIST 45 s
  fallback rather than dropping the default configuration row.
- Concurrent owner work on `src/` and `tests/` (NDISAPI optimization): stage only this task's
  paths; use project-scoped build/test/format while that work is in progress; run the full-solution
  gate only at a stable point; never stage, revert or fix the owner's files.
- Every subagent reads its task's `implement.jsonl` / `check.jsonl` and the relevant
  `.trellis/spec` files before editing. The main session does not write code.

## Notes

- The decision rationale and alternatives are in
  `.trellis/tasks/10-11-e2e-benchmark-design-review/research/decisions.md`.
- The batch sequence, contracts and verification commands are in `design.md` and `implement.md`.
