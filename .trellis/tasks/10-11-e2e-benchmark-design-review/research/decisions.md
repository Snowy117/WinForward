# E2E benchmark remediation decisions

Status: accepted 2026-10-18. Source: `design-problems.md` plus the four axis documents in this
directory, resolved through a grilling session. This file records the decisions, not the evidence;
the evidence stays in the axis documents.

## Decision record

| ID | Decision | Alternatives considered | Why | Consequence |
| --- | --- | --- | --- | --- |
| D1 | **Claim boundary = dual track.** Internal regression and capability assertions are the primary use. External output is a *solution comparison* with a limits section; no product ranking. Two clean single-factor comparisons exist: build mode (`wf-aot-opt` vs `wf-fdd-opt`) and UDP carriage (`wf-aot-nativeudp` vs `wf-aot-opt`). Product-level claims are limited to the build-mode pair, marked exploratory; the UDP-carriage pair is configuration-level. | A product ranking; capability-only; solution comparison only. | Seven rows are seven factor bundles. The two single-factor pairs hold the other declared factors fixed; neither is a product ranking. A ranking would require `BASE-singbox`, a default-transport row, a declared config policy and blindness, and even then the driver/process-model differences remain. | The report title, row labels and conclusions say "solution", not "product". "Cannot support" is stated where the design cannot support it. |
| D2 | **Quality priority = validity > reproducibility > external credibility > precision.** | Precision first; reproducibility first; credibility first. | Precision bought on an invalid comparison is waste. The review's cheapest wins are validity and evidence-chain fixes; precision is the last and most expensive knob. | The first two batches are evidence and instrument, not more passes. The campaign budget follows after the gates work. |
| D3 | **Release caliber = a new `plans-release/full-plan.json`.** Release caliber means the triple *(plan file, content hash, declared arm durations)*; every published table prints it. `plans-short` is screening only and never shares a table with the release caliber. Canonical `plans/` stays as a reference caliber, not the release session. | Canonical 870 s; compressed 321 s; canonical-vs-compressed split. | Canonical is ~11 h for 7 passes and still cannot resolve the 0.1 pp REL threshold; compressed cannot host warm-up and leaves REL at 0.625 pp. A rate-preserving plan with arm minimums is the only one that fits one night and keeps the declared rates. | New plan set, new row profiles, deploy mapping and README tables. MIX/PERSIST 45 s is the fallback if the night is 8–9 h. |
| D4 | **Primary family = four inferential metrics plus one capability gate.** Inferential: `lat.tcp_rtt.p50`, `cpu.proxy.vcpuPct`, `mem.privateBytes.p50`, `loss.lossRate`. `rel.unexpectedEofRate` is a capability gate (upper bound and threshold), not a difference claim. Everything else is exploratory. | All 21 headline; latency/CPU only; capability only. | The measured run-to-run noise is 13–15 % for latency p50, 10.4 % for CPU and 1.3 % for memory; a 5 % latency threshold is inside the noise. REL's zero-event bound at the release sample is 0.25 pp, so a difference claim is not resolvable. | Thresholds come from the calibration band, not from the 5/10/10/0.5/0.1 constants. Latency/CPU conclusions become "not worse than X" / "difference at least Y". REL is reported as an upper bound. |
| D5 | **Config policy = both WinForward configurations run the full plan.** `wf-aot-nativeudp` (default, native UDP) is the reference row; `wf-aot-opt` (opt-in UoT) is a declared variant. Opponents run their documented configuration. The patched sing-box `1.14.2-singfix` is declared. | Default only; recommended-config only; keep the asymmetry as a limitation. | The head row was non-default while the default row ran only three arms. A solution comparison that silently uses a tuned WinForward row is not a solution comparison. Running both rows keeps the UoT story without hiding the default. | One more full row, ~4 min/pass and ~0.5 person-day of row-profile work. The report labels both rows and states the sing-box patch. |
| D6 | **Instrument = P0 plus a calibration-only `BASE-singbox`.** P0: positive control, clock probe + drift gate (measure-and-correct), environment read-before-change, gate-into-verdict, arm-order randomization, clean-slate failures, `failures.json`. `connectionId` is deferred; its table caption is corrected now. | All three gaps; P0 only; P0 + `connectionId`. | P0 is the minimum for a falsifiable instrument. A stable clock offset is correctable per pass; an unmeasured jump or drift is not, so the gate is on drift and sanity, not on absolute offset. `BASE-singbox` is cheap once it is a calibration control rather than a per-pass row. `connectionId` is a 1–2 day build and is not on the critical path for solution comparison or regression. | The caption says "UTC-window attribution"; the `connectionId` work is a follow-up. `BASE-singbox` is run once during calibration. The analysis shifts the client window by `-clockOffsetMs` before ledger attribution and reports the residual. |
| D7 | **Verdict model = band-aligned, with `unevaluable`.** Reference row = default config. Holm family = per primary metric, reference vs each opponent. `different` uses a bootstrap test whose null is "the ratio is inside the band". `same` only in a separately pre-registered TOST equivalence family, unadjusted and declared. Degenerate/zero-variance intervals return `unevaluable`. `min_passes` is a CLI parameter (release default 6). Percentile bootstrap stays; the n=6 simulated coverage is printed. | Disclosure only; remove `same`; full BCa + Holm + TOST rewrite. | The current Holm never changes a verdict, `same` is produced by zero-width intervals, and the `different` p-value null is "ratio = 1" while the decision uses a band. The reference-vs-opponent family matches the question actually asked. | The legacy output stays byte-identical; the release mode writes separate outputs and is C#-only (D15). The primary family is data in the thresholds file, not inferred from the 21 metrics. BCa is a follow-up, not this round. |
| D8 | **Rerun rule = instrument-only pass rerun.** A pass may be rerun only when a pre-registered instrument gate failed (clock, product did not start, ledger identity). The failed pass is archived and disclosed. A second rerun in one campaign reruns the whole campaign. "It looks slow" is not a reason. | Strict whole-campaign rerun; ad hoc; no rerun. | A 7-pass night is ~9 h; a mechanical failure should not cost the whole night. The guard against selection bias is that the failure is detected by a gate, not by looking at the metric values. | `attempt` and `failures.json` in the product tree; invalidation rules are data, not prose. |
| D9 | **Evidence = archive + checked-in manifest + re-analysis.** Archive root `~/bench-archive/<date>-<campaign>/` with `SHA256SUMS`; a small manifest is checked in under `benchmarks/results/`; raw data stays out of git. E5-b2 is re-analyzed under the new rules and the differences are recorded. | Raw data in git; archive only; retract old numbers. | The only copies are in `/tmp`. There is no external publication, so the task is archive + internal note, not retraction. | Archive is the first operational batch; re-analysis happens after the verdict changes. |
| D10 | **Task structure = review task stays the decision ledger; remediation is a new parent with three children.** 10-11 gets `decisions.md`, `glossary.md`, `measurement-model.md`; the new parent owns implementation; children are evidence, harness and campaign. | 10-11 as parent; one task; no new task. | The review's PRD says "review, not remediation". Mixing the two sets of acceptance criteria makes both unverifiable. | The parent’s `prd.md`/`design.md`/`implement.md` are the source of truth for implementation; 10-11 is archived at B10, not immediately, so its research paths in the task manifests stay resolvable. |
| D11 | **10-06 stays historical.** Add a "superseded by" pointer at the top of its PRD; do not rewrite the original Goal/AC. New requirements live in the remediation PRD. | Rewrite 10-06; archive it; leave both. | The original product-comparison intent is evidence of the drift the review found. Rewriting it would erase that evidence. | A one-paragraph pointer; the original AC stays unchecked as history. |
| D12 | **No external publication exists.** The old numbers are internal notes only. | — | Confirmed by the owner. | The old-data step is archive + re-analysis + internal note; no external correction. |
| D13 | **No hard deadline.** The sequence is evidence/docs → code → calibration → release campaign. | — | Confirmed by the owner. | Calibration and release are one night each; no schedule compression beyond the 10 h assumption. |
| D14 | **Night window = 10 h assumption.** The release campaign is planned at ~9–9.5 h for 7 passes. If the actual window is 8–9 h, the fallback is MIX/PERSIST 45 s, not dropping the default config row. | 8 h plan; split over two nights. | The 10 h plan keeps the full primary family and both WinForward configurations. Splitting passes over two nights would mix session-level factors. | The fallback is pre-declared; it does not change any other decision. |
| D15 | **Oracle strategy = B (legacy frozen, release path C#-only).** The default analyzer invocation keeps `tables.md`/`verdict.json` byte-identical and `oracle-diff` green. `--release --thresholds ... --min-passes ... --discard-pass ...` writes `tables.release.md`/`verdict.release.json`; the release rules are covered by C# unit tests, synthetic-tree negative controls and a copied-tree clock test, not by the Python oracle. | A patched Python reference + one re-freeze; a versioned second golden. | The Python reference is retired; patching the whole verdict semantics into it duplicates the C# work and is hard to review. The legacy oracle remains meaningful for the unchanged path. | The report states that the release rules are C#-only. `FROZEN.md`/`oracle-diff` do not move for B5; the release output has its own contract. The `connectionId` and clock captions are applied only in the release output. |

## Campaign parameters

Release plan per full row (seconds):

| Arm | IDLE | LAT | LATLOAD | DNS | DNSALT | LOSS | REL | THRU | MIX | PERSIST |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Release | 30 | 30 | 30 | 30 | 30 | 120 | 60 | 30 | 60 | 60 |

- Per-run warm-up: 30 s before the plan; per-arm trim: first 5 s of every arm. The deviation from
  the design's 30/60 s per-arm warm-up is declared.
- REL rate 20/s, 60 s = 1200 attempts, zero-event bound 0.25 pp. LOSS stays 120 s because of the
  tracker's 2^18−1 sequence space.
- Calibration: 3 back-to-back release runs on the default-config reference row, same binary, same
  session. Band `B = max(B_business, 1.5 × spread)`; if `B` exceeds the smallest effect worth
  claiming, the metric becomes report-only.
- Release campaign: 7 passes total = 1 discarded warm-up + 6 measured. Every attempt archived;
  `attempt` recorded.

## Acceptance criteria

- 10-11 research docs exist and agree with the campaign plan.
- Parent + three children exist, linked, with non-empty `implement.jsonl` / `check.jsonl`.
- Release plan, row profiles and configs are committed; a campaign deploys a commit.
- Every P0 gate is proven to fail at least once.
- Calibration band is pre-registered; release campaign has 7 archived passes with attempt metadata.
- Report prints release caliber, primary family, MDD/band, coverage, attempts and limits.
- Archive verifies with `sha256sum -c`; manifest is checked in.
- E5-b2 re-analysis notes exist.
- Project quality gates pass: build, test, format, inspectcode.
- 10-06 has the superseded pointer; 10-11 is archived; goal completed.

## Assumptions / open items

- The actual night window is 10 h. If it is 8–9 h, D14's fallback applies without reopening the
  other decisions.
- `connectionId`, BCa, QUIC/HTTP3, true blinding, IPv6 and kernel DPC/ISR are out of scope for this
  remediation and stay in the report's limits section.
- The campaign still uses the two existing machines and the existing `AGENTS.local.md` topology.
