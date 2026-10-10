# Owner-table attribution scan: allocation budget for the per-connection path

## Goal

Make one owner-table scan (`GetExtendedTcpTable`/`GetExtendedUdpTable` through
`IPHelperOwnerTableReader`) allocate nothing on the managed heap in steady state, so that the
process-attribution path stops dominating process allocation under connection churn. The REL arm's
measured 28 MB/s allocation rate and the +26 MiB private-memory staircase must disappear without a
change to any attribution answer and without moving the E2E gates.

## Background (measured, not inferred)

The 2026-10-09 E2E campaign first reported the REL arm's private-memory staircase; the 2026-10-10
diagnosis session reproduced it on a restored VM (FDD build of `dc5ede8`, 20 cps REL, 120 s, 2400
connections, `DOTNET` defaults) and attributed it:

- `dotnet-counters` during the run: `dotnet.gc.heap.total_allocated` mean **27.8 MB/s**, max 38.1
  MB/s; private memory 30.8 → 63.2 MiB; 2400 connections ⇒ **~1.4 MB per connection**.
- A `dotnet-trace --profile gc-verbose` capture of the same load (11,939 allocation ticks,
  1878.9 MiB sampled) puts **96 %** of all sampled allocation under one stack:
  `IPHelperOwnerTableReader.ReadTcp4()` ← `OwnerTable` read ← `ProcessOwnerTableCache.Lookup`
  ← `WindowsProcessAttributor.FindOwnerSafely` ← `FlowDispatcher.AttributeProcessAsync`
  ← `FlowAttributionPipeline.RunAttributionAsync` ← `SetupExecutor.WorkerLoop`.
  The allocating types are `WinForward.Windows.TcpOwnerRow[]` (1066.9 MiB) and
  `System.Net.IPAddress` (738.8 MiB) — one fresh row array plus **two `IPAddress` objects per row**
  per scan. Evidence: `/tmp/wf-run/evidence/alloc-profile-20cps.txt`,
  `/tmp/wf-run/evidence/rel20.nettrace`.
- The same load at 1 cps costs **42 KB per connection** and never steps; the amplification is not
  data volume, it is the per-new-flow scan of a system-wide table whose size grows with the number of
  concurrent connections the load itself creates.
- Why a scan happens per new flow: a snapshot can only contain sockets that already existed when it
  was taken, and a new flow's socket is newer than every existing snapshot, so its lookup misses the
  reuse window by construction. `FindAsync` then retries once after 2 ms, which for a negative answer
  is a second scan.
- `.trellis/spec/backend/hot-path.md` exempts process attribution from the zero-allocation contract
  ("cold edges ... process attribution"). That exemption was written for a per-flow *setup* cost; it
  did not account for attribution running once per new flow, i.e. at the connection rate, with a scan
  cost proportional to the whole system's connection table.

## Requirements

- **R1 — bounded managed cost per scan.** A scan must not allocate memory proportional to the number
  of rows: no per-row `IPAddress`, no fresh row array per scan. Steady-state target: 0 B per scan
  after the row storage has grown once to the table's size.
- **R2 — semantics unchanged.** Every fact currently pinned by
  `tests/WinForward.Windows.Tests/ProcessOwnerTableCacheTests.cs` must keep holding: TCP answers may
  be served from a snapshot within the reuse window exactly when the four-tuple matched; negative
  answers require a read taken at or after the request instant; UDP never reuses (its predicate
  matches the local port alone); concurrent missers coalesce onto one scan; an unavailable table is
  never cached and never answers; scan accounting is unchanged (`ProcessOwnerTableCache.ScanCount`
  still counts successful fills only).
- **R3 — no framework address on the scan path.** Rows and predicates stay in `IPAddressValue`
  domain, per the "raw addresses only on the hot path" convention.
- **R4 — observability.** The heartbeat reports owner-table scans as a counter delta, so a campaign
  proves the scan rate instead of inferring it.
- **R5 — tests.** The existing cache facts pass unchanged; a new allocation gate proves the scan path
  allocates 0 B in steady state (and can fail); the row-count validation and rejected-read behaviour
  keep their coverage.
- **R6 — E2E.** The `wf-fdd-opt` full-plan row stays green (no gate regression) with latency and
  throughput inside the pre-fix row's noise, and the 20 cps REL measurement shows allocation below
  1.5 MB/s with no private-memory step.

## Acceptance Criteria

Measured outcomes are in `research/after-20cps.md`.

- [x] AC1 (allocation): the 20 cps REL measurement reports `dotnet.gc.heap.total_allocated` mean
  **0.533 MB/s on the committed revision** (0.546 on the revision before the non-fillable-constant
  hardening) against a 1.5 MB/s target and a 27.8 MB/s baseline (52×), and 64.0 MB allocated over
  the whole 120 s load (baseline ~3.3 GB).
- [~] AC1 (private memory): the load-start step shrank from **+30 MB to +11.2 MB** on the committed
  revision and the post-load sawtooth (55–73 MB band) became a flat 44–50 MB plateau, but the
  ≤ 5 MiB target is **not met**.
  The residual is GC segment commitment, not churn: only 64 MB is allocated, yet committed grows
  9.3 → 40 MB because a single LOH segment is committed for 2.83 MiB of >85 KB arrays (per-flow
  tombstone dictionaries 1.2 MiB, the owner-table slot's own growth 1.64 MiB). Two levers measured:
  chunked slot storage would remove our share, and `DOTNET_GCHeapHardLimit=0x2000000` lowers the
  plateau by ~8 MB (38–42 MB) at the same step. Both are follow-ups, not part of this task's scope
  (see Out of Scope).
- [x] AC2: a `gc-verbose` trace of the same load attributes **4.7 %** of sampled allocation to the
  owner-table stack (baseline 96 %), and no `IPAddress` allocation remains on the path; the sampled
  total drops from 1878.9 MiB to 61.7 MiB.
- [x] AC3: `IPHelperOwnerTableParserTests.AFillAndItsLookupsAllocateNoManagedBytes` asserts
  exactly 0 B across a refill and two lookups over a 256-row scripted table, and was proved to fail
  by injecting a per-row `IPAddress.Parse` (see `implement.md`, step 5).
- [x] AC4: `dotnet build WinForward.slnx -c Release` is zero-warning (0 warnings, 0 errors) and the
  full `dotnet test WinForward.slnx -c Release` run is green (every project "Test Run Successful");
  `dotnet format --verify-no-changes` exits 0 with empty output and the `jb inspectcode` report for
  the changed projects carries no issues.
- [x] AC5 (affected path): the REL arm's own A/B on the same plan and upstream improves — 1795/1795
  exchanges against 1635 exchanged + 110 `connectFail`, connect p50 8.75 → 7.20 ms, p99 17.18 →
  15.24 ms. The full-plan campaign row is **not** re-run: the local stand-in upstream speaks TCP
  only, so the UDP/UoT arms would not be comparable, and the affected path is covered by the REL A/B
  plus the trace (`directLeak`/`foreignConnection` cannot move: no direct-lane traffic and no
  foreign-source packets exist in this run).
- [x] AC6: the heartbeat reports `attributionOwnerTableScans` and the run shows 2401 scans for 2400
  connections (one per new flow, as designed).

## Out of Scope

- Reducing the number of scans. Negative answers are only sound from a scan taken at or after the
  request, and a new flow's socket is newer than every existing snapshot, so the scan rate is
  bounded below by the new-flow rate by design. The exemption from "no allocation" is what changes,
  not the scan cadence.
- Replacing `GetExtendedTcpTable`/`GetExtendedUdpTable` with ETW-based attribution, and any
  targeted per-socket query (Windows exposes no tuple-keyed owner query).
- The remaining non-attribution allocation sites on the connection path (they measure < 1 MB/s
  combined; the trace ranks them after the table scan and they stay under the existing gates).
- The E2E harness's config-format migration and any other campaign-harness change.

## Decisions

- **D1** Keep the scan cadence and the cache semantics; remove the per-scan managed cost by making
  the table snapshot a reusable, cache-owned slot that is only ever touched under the per-kind gate.
- **D2** Keep the reader seam (`IProcessOwnerTableReader`) shape, and let the production reader own
  one reusable table per kind. The contract becomes "the returned table is valid until the next read
  of the same kind", which is what makes reuse safe and what the gate enforces.
- **D3** The allocation gate measures the scan path with a scripted reader, not the native call: the
  native enumeration is not available on the test host, and the gate's subject is the managed cost of
  the path above the seam.
