# Admission and capacity split — step-2 attribution

Task `09-22-udp-admission-capacity-alloc`. Evidence: `research/raw/probe-campaign-run{1,2,3}.log`
(full family, 3 runs, exit 0; all campaign A-series rows deterministic with spread 0.0), parsed
with `research/tools/parse-bdn-alloc.py`; A4b was sized after the campaign and its pre/post reads
(96.0 class / 0 B inlined) are `research/raw/probe-a4b-run1.log` and
`research/raw/probe-a-inline-run1.log`. Conventions: per-session marginal
`(alloc@1000 − alloc@1)/999`; A9a/A9b are fixed-workload cases (their readout is the contrast,
never the marginal — see §5).

## 1. Measured components (B/session; campaign rows spread 0.0 across 3 runs, A4b sized by its own smoke)

| Case | Marginal | Measures |
|---|---:|---|
| `ComponentA1_AssociationTablePreSeed` | 325.9 | the two association dictionaries' pre-seed |
| `ComponentA2_SessionDictionaryPreSeed` | 163.0 | the sessions-dictionary pre-seed |
| `ComponentA3_CooldownTableConstruction` | 0.0 | control — no pre-seed |
| `ComponentA4_SlotAndQueueObjects` | 144.0 | slot object + its inline queue object |
| `ComponentA5_CompletionCell` | 88.0 | TCS + Task |
| `ComponentA6_ColdRent` | 272.0 | cold `SetupWorkItem` (incl. its inline FlowKey) + two planes |
| `ComponentA7_EnqueueCycle` | 0.0 | charge → pool rent → native copy → enqueue → dequeue → credit |
| `ComponentA8_WarmRentAdmissionAsync` | 1,045.7 | the S1 shape with a recycling (production-shaped) executor |
| `ComponentA4b_BoundedSetupQueueObject` | 96.0 | the queue object alone (measured pre- and post-conversion; the inline experiment reverted — see §4 A) |
| `ComponentA9a` / `ComponentA9b` totals | 950,584 / 787,288 B | fixed 2,048 adds, pre-seed 1,024 (spans one resize) vs 2,048 (none) |

## 2. Reconciliation (exact)

- **Pre-seed**: A1 + A2 + A3 = 325.9 + 163.0 + 0.0 = **488.9 = S0** — the capacity pre-seed is
  fully accounted for; the cooldown table and the coordinator's own fields are per-invocation
  constants, not per-session.
- **Admission**: S1 (1,317.7) = **488.9 (pre-seed) + 356.8 (H: flow-key construction harness) +
  200.0 (slot + queue + completion cell, in-context) + 272.0 (cold rent)**.
- **Warm-rent variant**: A8 (1,045.7) = 488.9 + 356.8 + 200.0 exactly; **S1 − A8 = 272.0 = A6** —
  the cold-rent overcharge is exactly the cold item + planes.
- A7 = 0.0 managed: the native payload copy path (charge → rent → copy → enqueue → dequeue →
  credit) allocates nothing end to end.

## 3. Production-shaped reading (the reframe this split earns)

- The recorded admission leg (S1 − S0 = 828.8) decomposes into **356.8 harness** (H — always
  subtracted in product-shape math), **272.0 cold rent** (production amortizes it: the executor
  recycles items and `OverflowAllocations` is its zero-at-steady-state diagnostic), and
  **≈200.0 of steady-state machinery** (slot + queue + completion cell).
- **Capacity**: the S0 shape (`capacity = N`) shows the pre-seed share; in production
  (`capacity = 16_384`, clamp 1,024) the pre-seed is a one-time ≈0.5 MB (the A1 + A2 share at
  the clamp's scale) and live flows **beyond the clamp** pay amortized dictionary growth —
  measured as the A9 contrast: **79.7 B per add** for the sessions dictionary (one resize
  spanning 2,048 adds); the association pair would add a comparable share when it grows.
- Net: the steady-state admission target is ~200 B/session, and the capacity leg's per-session
  cost after the clamp is dictionary growth (entry size ≈ 144 B, `FlowKey` ≈ 128 B of it).

## 4. Adoption table (measured)

| ID | Candidate | Measured | Decision |
|---|---|---:|---|
| A | Inline the per-slot `BoundedSetupQueue` object into its slot | A4b (queue object alone) = **96.0**; **net after inlining = 24.0** (the slot object grows 48.0 → 120.0 with the inline payload, swallowing 72 of the 96) | **not adopted** — 24.0 is below the 64 rule; the conversion was implemented, verified (100 focused tests, build/format green, A8/S1 exactly −24) and reverted; the patch is kept at `research/raw/phase2-inline-reverted.patch` for any future decision |
| B | Completion cell | 88.0 standalone; ≈56 in-context | **defer** — no retained-cost alternative is expressible without a design pass (the cell is what disposal joins a queued setup through) |
| C | Association pre-seed (325.9) / sessions pre-seed (163.0) | growth 79.7/add (sessions dict shape) | **defer** — dropping the association pre-seed trades ≈325.9 for ≈160+/session of setup-path growth; entry-size work (`FlowKey` ≈ 128 B) is a structural/product-wide change |
| D | Probe-shape correction (cold-rent + clamp/growth caveats) | done (A8, A9) | record in the docs and the §3 sub-anchor sentence |
| E | Pooling / retained memory (slots, cells, tables) | n/a | deferred by the carried rule (follow-up decision with these numbers) |

**Adopted set is empty.** Every candidate landed as a sized, reasoned verdict: the steady-state
admission machinery (~200) has no sub-64-risk-free cut beyond the reverted 24, and the capacity
leg's reducible share is structural (entry size) or retained-memory (excluded by the carried
rule). The task's product outcome is the re-based reading and the removal of two probe-shape
traps — the numbers above are the input for the follow-up pooling/entry decisions.

## 5. Notes and caveats

- A9a/A9b docs state their convention: the standard marginal does not apply; the readout is
  `(A9a − A9b) ÷ 2,048` = 79.7 B/add, deterministic across the three runs.
- The in-context 200.0 (slot + queue + cell) is derived from the exact reconciliation; the
  standalone A4 + A5 sum is 232.0. The 32 B difference was not chased (the derived figure is used
  only inside the reconciliation, never as an anchor; the standalone cases attribute components).
- The recorded S0/S1 values are unchanged and reproduced byte-identically in this campaign — this
  split re-bases the *reading* of the leg, not the recorded numbers.
- Existing cases (S0–S5, F, H, C1, C2, T1/T3/T5, C2\*, E\*) reproduced byte-identically or within
  their recorded spreads after the additions.

## 6. Final-tree confirmation (no product change adopted)

The phase-2 conversion was reverted, so the product code is byte-identical to the campaign's tree;
the benchmark file keeps all A-series cases including A4b. Gates on the final tree: `build` exit 0
zero-warning; full suite 807 green; `format --verify-no-changes` exit 0 with empty output; `jb
inspectcode` 0 issues. Confirmation runs (single, archived as `research/raw/confirm-*`): Noop probe
**5,162.4** (established 5,161.8, spread 14.9) and churn N=48/D=0 **12,591.2** (established
12,560.3, spread 182.8) — both inside the established spreads; the churn cell is lossless
(48/0/48, gen 0/0/0).
