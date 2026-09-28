# Design — admission/capacity splits, then the no-retained-cost reductions

Scope rule carried from the 09-22 decision: probes first; reductions limited to allocation trades
that keep the current memory shape (no pools/slabs/CTS-style reuse retaining capacity-sized
memory). No behavior change; async-lifetime D1–D11, the udp-relay setup-memory contracts, and the
admission semantics (TTL, cooldown, drop-oldest) stay locked.

## 1. Basis

- S0 = 488.9 B/session (deterministic); S1 − S0 = 828.8; setup start ≤172; T2 anchor ≤1,500
  (measured 1,432.9). The 09-22-udp-teardown-session-tier-alloc reductions did not touch these
  legs (verified by its re-measure).
- Probe shape facts: the S0/S1 cases run with `capacity = N` (pre-seed clamp `min(capacity, 1024)`
  is vacuous for N ≤ 1000) and the S1 executor allocates a **cold** `SetupWorkItem` per session,
  while production recycles items and (with `capacity = 16_384`) pays dictionary growth beyond
  1,024 live flows. Both divergences must be named and quantified, never mixed.

## 2. Architecture and boundaries

- **Benchmark side (additive).** New cases in
  `benchmarks/WinForward.Benchmarks/Perf/SessionSetupDecompositionBenchmarks.cs`, reusing its
  fixtures, fakes and conventions. Existing cases stay shape-identical.
- **Product side (src).** `src/WinForward.Runtime/UdpProxy/*` and — only if adopted and only
  through an internal embedding path — `src/WinForward.Core/BoundedSetupQueue.cs` (public Core
  type with its own tests; its public surface must keep working).
- Contracts that constrain every change: charge/credit exactly-once (`udp-relay.md` §"Bounded UDP
  setup memory"); the single-slot fast path and `min(capacity, 1024)` pre-seed (`hot-path.md` §2);
  the warm-path 0-B gates; D1–D11 for anything touching the coordinator's scope/teardown.

## 3. Probe design

Conventions of the class: `[Params(1, 100, 1000)]`, `--job short`, marginal
`(alloc@1000 − alloc@1)/999`, means of ≥3 runs with spread, `/proc/loadavg` per run, one process
at a time; every case asserts its own stop condition; deterministic cases reproduce ≤1 B.

### 3.1 Capacity pre-seed split

| Case | Shape | Measures |
|---|---|---|
| `ComponentA1_AssociationTablePreSeed` | `new UdpAssociationTable(capacity: Sessions, initialCapacity: Sessions)` per case, kept alive | both association dictionaries' pre-seed share |
| `ComponentA2_SessionDictionaryPreSeed` | `new Dictionary<FlowKey, UdpSessionSlot>(Sessions)`, kept alive | the sessions dictionary's pre-seed share |
| `ComponentA3_CooldownTableConstruction` | `new UdpSetupCooldownTable(Sessions)` per case | control: expected ≈0 (no pre-seed) |
| (derivation) | A1 + A2 + A3 vs S0 = 488.9 | the remainder names what S0 carries beyond the pre-seed (table locks/fields, coordinator fields, budget) |

### 3.2 Admission split

| Case | Shape | Measures |
|---|---|---|
| `ComponentA4_SlotAndQueueObjects` | `new UdpSessionSlot()` per session, kept alive | the slot object + its inline `BoundedSetupQueue` object |
| `ComponentA5_CompletionCell` | `new TaskCompletionSource(RunContinuationsAsynchronously)` + `.Task` per session | cross-check of the C2e 88.0 measurement in the admission context |
| `ComponentA6_ColdRent` | one `SetupExecutor` with an empty free list: `RentItem(cachedHandler)` per session, kept alive, never enqueued (no workers start) | the cold item + planes + overflow counter |
| `ComponentA7_EnqueueCycle` | per session: `budget.TryCharge(len)` → `setupQueuePool.Rent()` → payload copy → `slot.SetupQueue.TryEnqueue` → `TryDequeue` → `Credit` → lease dispose (real types) | expected ≈0 managed (validates the native-copy claim end to end) |
| `ComponentA8_WarmRentAdmission` (production-shaped) | the S1 admission cycle (slot + TCS + rent + charge + enqueue) with an executor that **recycles** its items through a free list like production | the production-shaped admission leg; `S1 − A8` quantifies the cold-rent overcharge |
| `ComponentA9a_DictionaryGrowthSpansResize` / `ComponentA9b_DictionaryGrowthNoResize` | both pre-seeded, then exactly 2,048 distinct adds with a shared value instance (no per-add slot allocation); A9a pre-seeds 1,024 (spans the ≈1,025 resize), A9b pre-seeds 4,096 (no resize) | readout: `(A9a − A9b) ÷ 2,048` = amortized per-add growth share (both pay the identical key-construction cost — the H shape — so the contrast cancels it; the plain `(alloc@1000 − alloc@1)/999` marginal does not apply to these cases and their docs must say so) |

Harness discipline: keep-alive via sink fields (never `GC.KeepAlive` over a value type — the
Nullable-boxing trap); new fakes stay private; if a new fake adds a per-session cost beyond F/H,
extend the subtraction explicitly.

## 4. Adoption table (filled after the probes; carried adoption rule)

Measured 2026-09-22 (`research/admission-split.md`; 3-batch campaign, all rows deterministic).
Adopt if ≥ ~64 B/session at low/medium risk and the memory shape is unchanged, or if it removes
product-origin exception-shaped work; otherwise record the measured size and defer.

| ID | Candidate | Measured | Decision |
|---|---|---|---|
| A | Inline the per-slot `BoundedSetupQueue` object (class → mutable struct with the `WorkLease`-style keep-as-a-direct-field rule) | A4b = 96.0 (the queue object alone); **net after inlining = 24.0** (the slot grows 48.0 → 120.0) | **not adopted** — 24.0 < the 64 rule; conversion verified and reverted (patch kept) |
| B | Completion cell | 88.0 standalone / ≈56 in-context | **defer** (no retained-cost alternative is expressible without a design pass) |
| C | Association pre-seed (325.9) / sessions pre-seed (163.0) | growth 79.7 B/add (one dict shape) | **defer** (dropping the pre-seed trades ≈325.9 for ≈160+/session of setup-path growth; entry-size work is structural) |
| D | Probe-shape correction (cold-rent, clamp/growth caveats) | A8/A9 quantify both | done |
| E | Pooling / retained memory (slots, cells, tables) | n/a | deferred by the carried rule |

**Measured effect: zero adopted items.** The steady-state admission machinery (~200 B/session)
has no sub-64, low/medium-risk cut beyond the reverted 24; the capacity leg's reducible share is
structural (entry/`FlowKey` size) or retained-memory (excluded). The deliverable is the re-based
reading plus the removal of the two probe-shape traps; the conversion patch at
`research/raw/phase2-inline-reverted.patch` is ready if a future decision wants the 24.0.

Reconciliation (exact): S0 = A1 + A2 + A3 = 488.9; S1 = 488.9 + H 356.8 + 200.0 (slot + queue +
cell, in-context) + cold 272.0; A8 = 1,045.7 = the same minus the cold rent; S1 − A8 = A6 = 272.0.
**Production steady-state admission ≈ 200.0 B/session**; the capacity leg's post-clamp per-session
cost is dictionary growth (A9 = 79.7 B/add for the sessions dictionary).

## 5. Completion-cell analysis (why it is hard to remove)

`ScheduleSessionSetup` creates one `TaskCompletionSource(RunContinuationsAsynchronously)` per new
flow, exposes `slot.Completion = completion.Task`, and the executor sets it when the queued
pipeline finishes (`TrySetResult`/`TrySetCanceled` on refusal). The coordinator's dispose awaits
every slot's `Completion` (with its cancellation/fault catches) before disposing sessions — the
cell is what lets disposal join a setup that has not started yet. Without pooling (deferred),
plausible alternatives are: (a) a lazy cell only for the queued window — the cell is created
before enqueue and read after, so laziness does not remove it; (b) a shared completed-task
sentinel when the pipeline completes inline — the *await* side still needs a per-slot signal; the
probe (`A5`) records the cost and the design records the verdict rather than forcing a change.

## 6. Compatibility and rollback

- Benchmark additions are additive (new case names only); recorded command lines keep running.
- src changes are behavior-zero, gated by the existing suites (`UdpProxy*`, admission/cooldown/
  capacity, `BoundedSetupQueue` tests, warm 0-B gates) and land as one reversible commit group per
  candidate; a candidate that does not reproduce its expected saving is reverted.
- No public API change expected beyond keeping `BoundedSetupQueue`'s existing surface working.

## 7. Risks

- **Shape divergence**: S1's cold-rent overcharge and the probe-capacity vs production-clamp
  divergence are the two ways this leg's history can be misread; A8/A9 quantify both and every
  number is quoted with its shape.
- **Public Core type**: `BoundedSetupQueue` has tests and a documented purpose; candidate A must
  not break its surface or the single-slot fast-path contract.
- **Probe additions must not shift existing cases** (S0–S5, F, H, C1, C2, T1/T3/T5, C2\*, E\*
  reproduce byte-identically or within their spreads — re-verified in the probe batch).

## 8. Measurement protocol

```text
# decomposition family (probes), from the repo root, one process at a time, ≥3 runs
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*SessionSetupDecomposition*' --job short
# focused runs while iterating
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*ComponentA*' --job short
# end-to-end confirmation after src changes
... --filter '*PopulateSessionsNoop*' --job short
... --stability --scenario udpchurn --burst-flows 48 --dial-delay-ms 0 --churn-waves 1 --socks5-external
```

Raw logs + `/proc/loadavg` under this task's `research/raw/`; parsing via
`research/tools/parse-bdn-alloc.py` (copy from the archived task).
