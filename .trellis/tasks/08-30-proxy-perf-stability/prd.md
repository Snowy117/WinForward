# Proxy perf/stability improvement program

## Goal

Parent task for the proxy performance/stability improvement program. It owns the ranked
backlog produced by the 2026-08-30 deep-dive research, the child-task map, cross-child
acceptance criteria, and the final integration review. The product's core value is SOCKS5
proxy forwarding (pass/block are byproducts), so backlog ordering follows proxy-path impact.

This parent is not the implementation target unless it gains direct work; implementation
happens in child tasks.

## Source Material

- Research: `.trellis/tasks/archive/2026-08/08-30-proxy-perf-stability-research/research/`
  - `synthesis-and-backlog.md` — deduplicated ranked backlog (authoritative for ordering)
  - `tcp-redirect-path.md`, `udp-proxy-path.md`, `socks5-capture-dispatch.md`,
    `measurement-gaps.md` — per-path findings with file:line evidence
- Baseline at research time: commit `e5667af`, 431/431 tests, zero-warning build.
- Spot-verified top claims (trust without re-verification): dead production hot dispatch
  entry, missing client RST on mid-flow relay end, missing `NoDelay`.

## Child Task Map

| Child | Backlog items | Status |
|-------|---------------|--------|
| `08-30-fast-hardening` | #2 client RST (R1) + #3 NoDelay/64KiB buffers/stall re-arm throttle (X4, X5, X8a) | completed 2026-08-30 |
| `08-30-hot-path-revival` | #1 revive hot dispatch path (X1): (a) TCP-gate reverse diversion, (b) port bitmap prefilter | completed 2026-08-30 |
| `08-30-atomic-retire` | #4 atomic retire+remove+tombstone + bounded queues (R2, R3, R4): global 8 MiB setup budget + 5 s datagram TTL + tombstone bounds; SYN-path grace check | completed 2026-08-30 |
| `08-30-udp-alloc-jumbo` | #5 UDP allocation zero-out + buffer sizing consistency (X6, R5) | completed 2026-08-30 |
| `08-30-windows-reality` | #9 Windows measurement program (executed on Win11 IoT LTSC VM per user decision): stability matrix, BDN subset, 1h soak, port-pool attribution | completed 2026-08-30 |
| `08-30-batched-ioctls` | #7 batched reinjection IOCTLs (X3) — elevated by 08-30 VM data: Windows per-IO cost is the structural gap (batching amortizes it); Phase 2 UDP response micro-batch pending DNS-dense measurement per its PRD trigger | completed 2026-08-30 |
| `08-30-driver-resilience` | #10 transient driver-error retry/backoff with single-pump degradation + TCP SYN setup off the pump thread (R7, R8) | completed 2026-08-30 |
| `09-06-udp-burst-establishment` | burst-shape benchmark for the reported UDP establishment issue (dozens of new flows at one instant, DNS-wave): `udp.burstEstablishment` stability scenario + dial-delay knob + baseline matrix; findings gate follow-up fix children | completed 2026-09-06 (commit pending archive) |
| `09-06-udp-burst-ttl-attribution` | burst follow-up #1 (small, targeted): re-age setup-queue datagrams from dial start so limiter queue-wait stops counting as client staleness; acceptance gate = burst matrix re-run (128×4000 probe 93.75 % loss → 0) | completed 2026-09-06 (commit pending) |
| `09-06-local-mux-transport` | burst follow-up #2 (structural, research first): fixed connection pool + stream multiplexing to the local SOCKS5 server — collapses wave latency, removes the TTL interaction and port churn; go/no-go memo | planning |
| `09-20-transport-lifecycle` | transport lifecycle hardening: explicit quiescence boundary and session state (single-flight teardown, work leases, migrated owners) | completed 2026-09-20 (archived) |
| `09-28-udp-association-reuse` | UDP association reuse + session resource budget: one authenticated SOCKS5 association per N flows with passive capability detection and sticky per-flow fallback, configurable retention / relay receive buffer / capacity, and the `udpSessionBudget` soak. 1 h acceptance recorded (`benchmarks/results/2026-09-28-udp-reuse/step4-session-budget-1h.jsonl`, verdict `passed/retentionBounded/poolingCovered` true, 0 loss, 1.070 descriptors per live session); relay-socket sharing stays a separate follow-up | completed 2026-09-29 (archived) |
| `09-29-tcp-redirect-batched-injection` | structural finding F1 of `09-29-tcp-udp-path-structural-perf`: the client-facing redirect data legs join the batched-injection mechanism (one lane flush per pump iteration) and rewrite in place on the pump's capture slot — no per-frame rental, no copy, ~32× fewer injection IOCTLs on the product's main data path. **Corrects the `08-30-batched-ioctls` injection-scope table**: its "TCP mid-flow relay — userland sockets, no injection — out" row describes only the relay legs (proxy ↔ real server); the client-facing redirect legs (client ↔ local listener) do inject every mid-flow frame and belonged in batching scope | completed 2026-09-29 (archived) |
| `09-30-expiry-sweep-bounded-pause` | structural finding F3 of `09-29-tcp-udp-path-structural-perf`: the expiry legs stop walking their whole population under a table gate. Site 1 (`FlowTable.RemoveExpired`) becomes a chunked round at **minimal hold granularity** — a live-slot registry replaces the full-table scan, every `_gate` hold examines ≤256 entries and removes ≤1, and the holds-flow predicate runs with no table lock held (the store/tombstone lock-nesting edge is gone); sites 2–6 (`TcpRedirectTable`, `TcpRedirectSessionStore` + tombstones, `UdpProxyCoordinator`, `UdpAssociationPool`, `UdpAssociationTable`) reuse their retirement scratch and re-check under one short hold. Measured: resolves completed inside sweep windows 6.5–10.4 k → **13.1–14.1 M** per 15 s at 65,536 flows, all five allocation sites red→0 B on their gated tick (355,672 / 520 / 272 / 328 / 4,184 → 0), production-shaped sweep 0.033 ms, full suite 1,013 green, per-gate proof 80/80. A batched-removal variant was implemented, measured and **rejected** (it buys the sweep's own duration with an order of magnitude of warm-path progress). **Note for F2**: the F3.4 activity bucket is deferred to F2, whose representation contract (seven items) is in the task's `research/implementation-notes.md` §8 | completed 2026-09-30 (archived) |
| `09-30-warm-path-lock-chain` | structural finding F2 of `09-29-tcp-udp-path-structural-perf`: the warm packet path stops paying the lock chain. Activity becomes a lock-free 500 ms bucket (`ActivityBucketClock`, ticked once per pump iteration in the composition, `FlowState.Reset` now injected-clock — F3's D9 defect fixed); `FlowTable` gains a **pre-allocated direct-mapped warm cache** with exact key validation (seqlock snapshot + transport-tuple corroboration) over the still-authoritative gated `Dictionary`, so the 0 B claim/expire gate stays exact; the self-traffic **exact-tuple** check moves to claim time while the **wildcard relay-socket** half stays on the warm path, lock-free; TCP pays one reverse probe (no gate) instead of two gate entries; the UDP ready send is wait-free (`_activityGate` off the send/touch paths, cached session lookup). Measured: warm-arm self-normalised four-thread ratio **0.153–0.161 → 0.933–0.980**, four-thread throughput 3.3 → **22.5–23.4 M/s**, UDP `ReadySend` at four workers 523 → **186–188 ns**, every row 0 B, full suite 1,039 green, per-gate proofs 20/20. **Rejected by measurement**: `ConcurrentDictionary` indexes (they delivered the ratio but allocated 488 B per claim, breaking the exact `FlowTableClaimAndExpireCycleAllocatesNoManagedBytes` gate) — the direct-mapped cache replaced them. Recorded residuals: a one-probe stale-serve window on the three caches, AC-1 measured at 4,096 live flows (shipped capacity permits 16×), and an unguarded backwards-clock step | completed 2026-09-30 (archived) |
| `09-30-flow-key-parse-once` | structural finding F4 of `09-29-tcp-udp-path-structural-perf`: the packet path stops paying for convenience data structures. `FlowKey` goes 128 → **64 B** (the adapter string is interned into a process-lived `AdapterSlotTable` with monotone never-reused slots and refusal on exhaustion; the generation stays an integer key field, so equality keeps its semantics) with the packed endpoints round-tripping and F2's warm-cache hash/equality re-proven against an independent oracle; a frame is parsed **once** (`PacketLayout`, 16 B, carried on the packet) instead of four times; the two `uint?` sequence trackers and their `_sequenceGate` become CAS-max `long`s; and the per-packet context is interned (`FlowContext` 168 → **80 B**, `CapturedFlowPacket` 224 → **152 B**). The defaulted-layout hardening caught a **live** latent bug: a `default(PacketLayout)` was indistinguishable from a valid TCP layout and one in-place rewrite leg had been mis-rewriting at offset 26 while its allocation gate stayed green. Measured: all 16 `tcp-redirect-data-path` rows improve (worst 1.50×), the IPv6 reverse forwarded row 1.68–1.88×, `ResolveWarmHit` 76 → 26 ns, every row 0 B; the optional IPv6 checksum step was **rejected by measurement**; full suite 1,075 green and both commit gates at zero | completed 2026-09-30 (archived) |
| `10-01-pump-io-shape` | structural finding F5 of `09-29-tcp-udp-path-structural-perf`: the capture pump stops paying two IOCTLs per drain and a 1 ms sleep per idle poll. The drain now issues the **batched read first**, with the queue-size query demoted to the non-success **disambiguator** (both ABI hypotheses implemented and proven at the driver↔native seam, whose read/query counters are new); a **self-healing ABI-mismatch guard** makes the shape safe on unverified hardware — a failed read with a non-empty queue arms a sticky per-handle query-first shape, logs a rate-limited `adapter.readShape.mismatch` diagnostic and retries the drain immediately, instead of retrying into a false adapter degradation (red-before: `True\|5\|1\|31`); and the idle wait becomes a bounded **event wait** on the driver's packet-arrival signal behind an injectable seam, with the no-signal path byte-identical. Measured: idle CPU 0.0178 → **0.00055 CPU-s/s** (~31×, same-session A/B), poll cadence 886 → **10 waits/s**, wake p50 0.078 → **0.066 ms** with 5,000/5,000 signal-driven wakes park-confirmed at 1.0002/wake; two new exact 0 B gates; full suite 1,099 green; per-gate proofs 140/140 + 40/40. **The pinned ABI itself stays unverified on this host** — nine Windows experiments with expected observations and fallbacks are recorded in the artifact README, the partial-batch row being acceptance-relevant | completed 2026-10-01 (archived) |
| later: zero-copy-datapath | #6 zero-copy proxy data path (X2) — deprioritized by 08-30 VM data: managed path is platform-equivalent, not the Windows bottleneck | on demand |
| later: hardening-bundle | #8 keepalive + lock cleanup + ServerGC (R6, X7, X8, R11) | on demand |

New candidates from 08-30-windows-reality (see
`benchmarks/results/2026-08-30-windows-vm/README.md`):

- `port-budget-windows` — high-churn port-pool capacity: deployment guidance (widen
  dynamic range / TcpTimedWaitDelay), loopback-aware upstream error-path RST close
  (symmetric to #2's client RST), harness-side port accounting. Attribution: 96.65 % →
  0.40 % failures on widened pool, pure OS capacity, not a product defect.
- `local-mux-transport` → now task `09-06-local-mux-transport` (research first) — fixed
  connection pool + stream multiplexing to the local SOCKS5 server (e.g. VLESS inbound +
  multiplex) eliminating port churn, per-flow handshake cost, and loopback connects;
  sing-box has no UDS inbound. Elevated by the 09-06 burst findings (wave latency +
  TTL loss both root in per-flow dialing).
- `windows-real-nic` — real-NIC/ETW measurement past the ~4.7k pps loopback ceiling.
- small: UdpSession real-transport populate[1000] NA on Windows.

Deferred (from research §5): SOCKS5 handshake allocation diet, tighter 150s setup budget
(X9), TCP socket buffer sizing docs (R10), per-flow DNS cache, IPv6 perf (fold into
windows-reality-program).

## Requirements

- Each child task must carry its own PRD and acceptance criteria testable in isolation.
- Ordering constraints live in the child PRDs (e.g. hot-path-revival runs after
  fast-hardening).
- Every child must keep the repo at: full test suite green, zero-warning build, and
  allocation-gate benchmarks not regressed (existing benchmark discipline).
- Cross-cutting invariants that no child may break: exactly-once pool returns, OCE token
  discipline, batch-slot stability contract, in-place read-then-write mutation order,
  teardown single-writer semantics.

## Acceptance Criteria

- [x] fast-hardening landed: relay fault/stall produces a client-visible RST; NoDelay on
      both relay legs; pooled 64KiB pump buffers; stall re-arm throttled.
- [x] hot-path-revival landed: production composition executes the non-async warm dispatch
      entry (verified by test or benchmark on the production composition shape).
- [x] atomic-retire landed: retire+alias-removal+tombstone atomic under the store gate;
      TCP tombstone queue drains on sweep; UDP setup tombstones bounded; global 8 MiB
      setup budget + 5 s TTL with charge/credit exactly-once.
- [ ] Each landed child has its research finding re-validated against current source
      (file:line references may have drifted since e5667af).
- [ ] Integration review: after each child, full test suite + build clean; benchmarks
      recorded for affected paths.
- [ ] Backlog table above kept in sync (children created/completed update this file).

## Notes

- Research evidence program (windows-reality-program) is explicitly not product code; it
  should be scheduled early enough to inform whether zero-copy/batched-IOCTL effort is
  worth it (research: "determines where the next optimization dollar goes").
