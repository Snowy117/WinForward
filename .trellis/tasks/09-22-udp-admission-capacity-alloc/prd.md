# UDP admission and capacity pre-seed allocation reduction

## Goal

Split the Noop **admission leg** (S1 − S0 = 828.8 B/session) and the **capacity pre-seed** (S0 = 488.9 B/session) into attributable components with new decomposition probes, then implement the measured **no-retained-cost** reductions; same instruments, ≥3-run re-measure, anchors updated (T2 ≤1,500 spans capacity + admission + setup start).

User value: after the 09-22-udp-teardown-session-tier-alloc reductions (Noop probe 5,727.0 → 5,161.8 B/session), the admission + capacity legs are the last un-split attributable bookkeeping chunk; the redo's rank 4 lists them at 1,317.7 B/session (9.6 % of the clean whole cycle) with "admission component probe → table/entry footprint + pooled payload slot" as the next step.

## Background — confirmed facts

Research basis (`archive/2026-09/09-21-session-creation-cost/research/noop-decomposition.md`, `archive/2026-09/09-22-session-creation-cost-redo/research/reconciliation-and-decision.md` §5 rank 4, and this session's `archive/.../09-22-udp-teardown-session-tier-alloc/research/remeasure.md`):

- **S0 = 488.9 B/session**, deterministic (spread 0), unchanged by the 09-22 reductions: the coordinator + borrowed resources with `capacity = N`; the marginal is the per-session share of the pre-seed tables (`UdpAssociationTable(capacity, initialCapacity)` ×2 pre-seeded dictionaries + `_sessions` dictionary; clamp `min(capacity, 1024)`).
- **S1 − S0 = 828.8 B/session** (S1 total 1,317.7): "slot claim, setup-task enqueue, setup-queue charge + lease + copy".
- **Setup start** = S2 − S1 ≤172 (quantized 1 vs ~172).
- **T2 anchor**: capacity + admission + setup start ≤1,500 B/session (measured 1,432.9, spread 171.4; unchanged by the 09-22 reductions — verified by the re-measure).
- **C1 = 834.7** (association claim/release direct probe; an upper bound overlapping S0's association-table share).
- Post-09-22 baselines to re-verify: Noop probe marginal 5,161.8 (spread 14.9), churn N=48/D=0 cell 12,560.3 (spread 182.8).

Code facts (inspected 2026-09-22):

- `UdpProxyCoordinator` ctor (`UdpProxyCoordinator.cs:58-66`): `_associations = new UdpAssociationTable(capacity, preSeed)` (two dictionaries pre-seeded to `initialCapacity` when > 0, `UdpAssociations.cs:33-43`), `_sessions = new Dictionary<FlowKey, UdpSessionSlot>(preSeed)`, `_cooldowns = new UdpSetupCooldownTable(capacity)` (**no pre-seed** — its dictionary grows only on cooldown writes, `UdpSetupCooldownTable.cs:21-29`); `preSeed = Math.Min(capacity, 1024)`.
- Production default `capacity = 16_384` (`UdpProxyOptions.cs:19`) → production pre-seed clamps to 1,024 (a fixed ≈3-dictionary cost); beyond 1,024 live flows the dictionaries grow (amortized resize per session). The S0 probe (`capacity = N ≤ 1000`) measures the pre-seed share, not the growth.
- Admission path (`UdpProxyCoordinator.Send.cs:36-58` + `UdpProxyCoordinator.cs:129-148`, `:161-207`): `new UdpSessionSlot()` (one object; its `BoundedSetupQueue` is a second object), `ScheduleSessionSetup` → `new TaskCompletionSource(RunContinuationsAsynchronously)` + `slot.Completion = completion.Task`, `_setupExecutor.RentItem(_setupHandler)`, `_sessions.Add(flow, slot)`, then `EnqueueSetupDatagram` → `_budget.TryCharge` (Interlocked scalars), `_setupQueuePool.Rent()` (native lease; managed allocation only on pool overflow), `payload.CopyTo(lease.Span)` (native), `slot.SetupQueue.TryEnqueue` (inline single-slot fast path — no allocation for the first datagram).
- `SetupExecutor.RentItem` (`SetupExecutor.cs:170-181`) rents from `_free` and recycles items; `SetupWorkItem` + its TCP/UDP planes are pre-allocated once per item (`SetupExecutor.cs:18-83`). **The S1 probe's `AdmissionOnlySetupExecutor` allocates a fresh item per session** (documented as the cold-free-list shape), so S1 currently charges a per-session item production amortizes at steady state (`OverflowAllocations` is the production cold-rent diagnostic, "zero at steady state").
- `BoundedSetupQueue` (`src/WinForward.Core/BoundedSetupQueue.cs`) is a per-slot class: mutable inline `_pending` entry + optional `Queue<Entry>` (materialized only when a second datagram overlaps a setup).
- `UdpSetupQueueBudget` is Interlocked scalars only.

Contracts: `async-lifetime.md` D1–D11; `udp-relay.md` §"Bounded UDP setup memory" (8 MiB global budget, 5 s TTL, charge/credit exactly-once per datagram); `hot-path.md` §2 (single-slot fast path; dictionary pre-sizing clamped to `min(capacity, 1024)`) and §3 (T2); warm-path 0-B gates (`HotPathAllocationGateTests`, `FlowTableClaimAndExpireCycleAllocatesNoManagedBytes`).

Carried scope rule (from the 09-22 decision): probes first; **no retained-memory reductions** (no pools/slabs) — pooling is a follow-up decision taken with the split numbers.

## Requirements

- **R1 — Capacity pre-seed split probe (before any src change).** Direct component cases per table (association-table pre-seed, session-dictionary pre-seed, cooldown-table construction) + the S0 reconciliation; name the probe-shape caveats (probe `capacity = N` vs production clamp + post-1024 growth); ≥3 runs; deterministic cases ≤1 B.
- **R2 — Admission split probe.** Direct component cases splitting S1 − S0 into {slot + inline-queue objects, completion cell + Task, cold item rent (+ planes), charge/enqueue/lease/copy cycle}; plus a **production-shaped warm-rent variant** that quantifies the cold-rent overcharge the S1 shape carries, and a post-pre-seed dictionary-growth case at a fixed session count.
- **R3 — Implement the measured reductions** under the carried rule (adoption: ≥ ~64 B/session at low/medium risk, or removes product-origin exception-shaped work); sized-but-deferred items recorded.
- **R4 — Re-measure**: same instruments (full decomposition family, Noop probe, one churn cell out of process), ≥3 runs; before/after per component.
- **R5 — Anchors**: `hot-path.md` T2 re-anchored or explicitly unchanged with the new measured values, headroom and falsification condition; the §3 sub-anchor sentence (capacity 489 / admission 829 / …) reconciled with the split.
- **R6 — Research artifact**: `research/admission-split.md` (+ raw logs) closing or narrowing the rank-4 gap.

## Acceptance Criteria

- [ ] Pre-seed and admission splits documented per component (≥3 runs, spreads; deterministic rows reproduce within ≤1 B) with the probe-shape caveats named (cold rent vs production recycle; probe capacity vs production clamp + growth).
- [ ] Adoption table filled from the measurements; adopted items implemented behavior-zero with before/after numbers on the same instruments; deferred items carry measured sizes and reasons.
- [ ] T2 anchor and the §3 sub-anchor sentence reflect the measured values (or an explicit no-change rationale with the evidence).
- [ ] No per-session allocation regression in any leg (S0/S1/S2, Noop probe, churn cell re-verified out of process).
- [ ] Quality gates all green: Release build zero-warning, full tests, `dotnet format --severity info --verify-no-changes` empty, `jb inspectcode` zero issues; src changes carry tests.
- [ ] Carried rule honored: no retained-memory trades (pooling deferred with numbers recorded).

## Out of scope

- Retained-memory reductions (pooling/slab of slots, completion cells, table entries) — follow-up decision with the split numbers.
- The G-class session-tier leftovers (`Lock` → interlocked protocol, linked-CTS removal) and the response/retire residue split — their own follow-up.
- Control-connection reuse (product/protocol ADR); the framework anchors (T3a).
- Admission *semantics* (TTL, cooldown, drop-oldest policy) — only allocation shape may change.

## Open Questions

- None blocking.
