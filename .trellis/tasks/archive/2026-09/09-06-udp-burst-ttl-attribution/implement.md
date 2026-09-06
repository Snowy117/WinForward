# Implementation plan — UDP burst first-datagram TTL re-attribution

Validation gate after every step: `dotnet build -c Release` zero-warning
(`TreatWarningsAsErrors` is on) + `dotnet test` green.

## Steps

1. [ ] `BoundedSetupQueue.RefreshEnqueuedStamps(DateTimeOffset)` in
       `src/WinForward.Core/PacketRuntime.cs` per design.md (record-struct `with`
       refresh, in-place queue rebuild, `Count`/`Bytes` invariant).
       - Queue-level tests in `tests/WinForward.Core.Tests/UdpSetupQueueTests.cs`
         (empty no-op / single pending / all entries / invariants).
2. [ ] `UdpProxyCoordinator.CreateSessionAsync`: refresh under `_gate` right after
       `_setupLimiter.WaitAsync` returns, with the flush-style slot-ownership check;
       add `internal long SetupStampsRefreshedCount` (Interlocked); update
       `FlushSetupQueueAsync` XML doc to the dial-start age semantics.
       - Coordinator-level tests: limiter-wait refresh delivers (TtlExpired==0,
         StampsRefreshed>=1); dial-delay > TTL still drops (TtlExpired==1).
       - Validation: `dotnet build -c Release`; `dotnet test` full suite; existing
         budget/credit/cooldown suites unchanged.
3. [x] Acceptance matrix (spec contract): re-run `udp.burstEstablishment` — probe
       `128 × 4000` (expect loss 0, 128 first responses, timeToLast ≈ 64 s) +
       spot points 8/48/128 × 100 ms; artifacts under
       `benchmarks/results/2026-09-06-udp-burst-ttl-fix/` with before/after README.
       - Result: probe firstResponses 8 → **128**, lossRate 0.9375 → **0**,
         timeToLast 64037.5 ms (model 16×4 s = 64 s exact); spot points within
         noise (p50 103.1→102.5, 309.0→325.0, 827.1→828.8; max 1646.7→1651.2);
         background windows zero loss everywhere, probe burst window paced 256 239
         datagrams @ ~4000 pps with sub-0.05 ms send p95.
4. [x] Spec update: rewrite the udp-relay.md measured-contract bullet to post-fix
       semantics (age from dial start; limiter-wait excused; matrix gate unchanged).
5. [x] trellis-check full-scope pass (build 0-warning, 505/505, contract audit,
       doc-vs-JSONL accuracy, mutation re-verification: 4 tests catch a neutered
       refresh); parent PRD row synced; commit (Phase 3.4) pending user approval.

## Review gates

- After step 2: product semantics change is exactly the stamp refresh — budget
  accounting, tombstones, teardown paths untouched (diff review).
- After step 3: numbers compared against `results/2026-09-06-udp-burst/README.md`
  before any landing claim.

## Rollback

Two small diffs (Core queue + coordinator call site + counter) plus tests/docs.
Revert = restore enqueue-stamp age semantics; the burst matrix immediately re-measures
the 93.75 % corner as the regression signal.
