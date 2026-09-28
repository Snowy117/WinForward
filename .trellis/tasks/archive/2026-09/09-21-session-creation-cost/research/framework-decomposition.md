# Framework decomposition — real-transport per-session cost (task 09-21-session-creation-cost, design §3)

> **ERRATUM (2026-09-22, task `09-22-session-creation-cost-redo`).** Every server-touching
> framework-layer number in this report is inflated by ~75.5 KB/session (the relay-socket 576 B
> and self-traffic 160 B components are server-free and unchanged): the loopback SOCKS5 server ran in the
> same process as the measured client, and it allocates a 64 KiB relay-loop buffer plus a
> per-connection socket/arrays for every accepted control connection
> (`benchmarks/WinForward.Benchmarks/Stability/LoopbackSocks5UdpServer.cs:240`, `:150`,
> `:139-144`). Clean values (server out of process): framework path **7,952.0 B/session**
> (control connect + greeting 3,792.2; UDP ASSOCIATE 959.0; relay socket 576; self-traffic 160;
> transport ctor + wiring 2,464.8; harness share 75,489.6 of the recorded total), real probe
> marginal **17,021.2 B/session** (recorded 92,255 = 75.2 KB harness + 17.0 KB clean). The
> in-process shapes reproduce this task byte-for-byte (83,441.6 / 77,447.3 / 80,319.0), so the
> correction is a measurement change, not behavior drift. Corrected report (repo-root-relative):
> `.trellis/tasks/09-22-session-creation-cost-redo/research/framework-decomposition.md`.

Scope: the framework layer of one UDP session — control TCP connect + SOCKS5 handshake, UDP
ASSOCIATE, relay socket create/bind, self-traffic registration — as asked by design §3 ("use the
existing seams to isolate, per session"). All numbers are managed-allocation bytes from
BenchmarkDotNet's `Allocated` column (`GC.GetTotalAllocatedBytes`, process-wide), measured on
HEAD `feb5955` ("after") with `--job short` (3 warmups, 3 iterations, plus the engine's extra
allocation-only pass). Attribution is allocation-only; ns/µs numbers on this dev box are not
decision-grade (same-binary drift up to 2.8×).

## Method and command lines

New benchmark class `FrameworkSetupBenchmarks` (file
`benchmarks/WinForward.Benchmarks/Perf/FrameworkSetupBenchmarks.cs`), all variants against the same
loopback SOCKS5 UDP server the existing `UdpSessionBenchmarks` probe uses:

```text
# 3 runs, one process at a time, launched from the repo root
dotnet run -c Release --project benchmarks/WinForward.Benchmarks \
  -- --filter '*SessionSetupDecomposition*' '*FrameworkSetup*' --job short
```

Raw logs: `research/raw/decomposition-run{1,2,3}.log` (the framework class and the Noop
decomposition class ran in the same sequential invocations). Exact per-case GC lines were parsed
from each log's `// Execute:` block (example parse is embedded in the run notes below).

Seam inventory and its limits (design §1 preference order):

- `Socks5UdpTransport.CreateAsync(server, selfTraffic, ct, createControl, socketFactory,
  disableUdpConnectionReset, maximumFrameSize, addressCache)` is `internal static`, reachable from
  the benchmark project through `InternalsVisibleTo`.
- A **fake control connection is not expressible**: the `createControl` seam's return type is the
  concrete sealed `Socks5ControlConnection` (private constructor), so "real vs fake control" is
  measured as *control path vs its connect-only prefix* (connect+greeting, then + UDP ASSOCIATE).
  This is the one gap design §3 named; no `src/**` edit was made to widen it (recorded, not fixed).
- The relay socket has no fake either (the seam must return a real `Socket`), so its component is
  measured directly with the product's socket options (512 KiB receive buffer, non-blocking mode,
  wildcard bind) — the same calls `CreateAsync` makes.
- The self-traffic registration is measured directly against `SelfTrafficRegistry`
  (register → ownership query → release).

## Per-session components (N = 1000 sequential create+dispose)

Byte spreads are across the three runs (same machine, same command).

| Variant | what it covers | per session (B) | run spread (B) |
|---|---|---:|---:|
| `RealTransport_CreateDisposeAsync` | full framework path: connect + handshake + ASSOCIATE + relay socket + registration + transport ctor + disposal | **83,442** | 3,496 (0.004 %) |
| `Control_ConnectAssociateDisposeAsync` | connect + greeting + UDP ASSOCIATE + disposal | 80,320 | 8,368 (0.01 %) |
| `Control_ConnectDisposeAsync` | connect + greeting + disposal (no ASSOCIATE) | 77,448 | 5,168 (0.007 %) |
| `RelaySocket_CreateBindClose` | relay socket: create, 512 KiB receive buffer, bind, non-blocking, close | 576 | 0 |
| `SelfTraffic_RegisterRelease` | registry entry + token release (ownership asserted both ways) | 160 | 0 |

Derived components:

| Component | B/session | Notes |
|---|---:|---|
| control connect + greeting handshake (+ socket/NetworkStream/deadline state) | 77,448 | includes the connect socket, `NetworkStream`, `QuiescenceScope`, the per-attempt deadline CTS, the 513 B handshake scratch, and its disposal |
| UDP ASSOCIATE | 2,872 | request write + reply parse + relay-endpoint materialization; `RunWithinAttemptAsync` linked CTS per call |
| relay socket (create/bind/options/close) | 576 | 0.7 % of the framework total |
| self-traffic register/release | 160 | one dictionary entry + one token per session |
| residual: transport construction + wiring | 2,386 | dominated by the 1,536 B send buffer (`6 + 16 + cap`); the rest is `SocketAddress` + receive-sender `IPEndPoint` + `SemaphoreSlim(1,1)` + `Socks5UdpTransport` instance |

Component sum = 83,442 B/session = the full path exactly (by construction of the deltas).

## Cross-checks against the anchors

1. **~84 KB/session anchor.** The A/B record's framework anchor is ~84 KB/session (real probe
   ≈ 89–90 KB minus Noop probe ≈ 5.3–5.6 KB). Measured on HEAD: real-transport probe marginal
   (1000 vs 1 sweep, `research/raw/baseline-UdpSession-run{1,2,3}.log`) = **92,255 / 92,265 /
   92,244 B/session**; Noop probe marginal = **5,737 / 5,737 / 5,727 B/session** (recomputed
   5,738.8 / 5,737.3 / 5,727.8); difference = **86,516 / 86,528 / 86,517 B/session** (run-matched
   recomputed values). The isolated full framework path (83,442) covers **96.4 %** of that
   difference.
2. **The residual ~3.08 KB/session is harness-shaped, not product-shaped.** Two candidate sources,
   both outside the per-session framework path: (a) the in-process loopback SOCKS5 server in the
   real probe keeps N concurrent `RelayConnection`s alive (a 65,536 B = 64 KiB managed relay
   buffer, plus control handling and `ConcurrentDictionary` growth) whereas the component variants
   exercise one
   connection at a time; (b) the real session's receive loop awaits a real socket (native +
   managed `SocketReceiveFromResult` plumbing) instead of the fake `Task.Delay` await. The
   component table should therefore be read as the *framework path cost*, and the ~3 KB/session
   gap as the real-path session interaction + harness concurrency.
3. **`udp.sessionFootprint` retained numbers** (existing scenario, 3 runs, fake transports;
   sample taken with the session pool live, before disposal) — full rows and interpretation in
   `research/churn-measurements.md`; raw records `research/raw/footprint-run{1,2,3}.jsonl`:

   | sessions | workingSetDeltaBytes (r1/r2/r3) | allocatedBytes (r1/r2/r3) | gen0Collections (r1/r2/r3) |
   |---:|---|---|---|
   | 1 | 0 / 0 / 0 | 51,680 / 51,736 / 51,736 | 0 / 0 / 0 |
   | 100 | 0 / 0 / 0 | 432,072 / 440,544 / 459,048 | 0 / 0 / 0 |
   | 1000 | 0 / 0 / 0 | 4,202,592 / 4,184,448 / 4,019,880 | 0 / 0 / 0 |

   The allocated marginal is 3,961.7 B/session (100 vs 1) and 4,087.8 B/session (1000 vs 1), mean
   of the three runs; `workingSetDeltaBytes` is 0 in every row, so **retained** working-set growth
   is not observable at this scenario's granularity (100 ms settle, no forced GC) — the retained
   share is bounded only by the receive-window/slot structures the Noop stages price, and the
   real transport's extra retained footprint is kernel-side (512 KiB relay receive buffer, moved
   not removed by pooling).

## Interpretation (allocation first)

- The framework layer is **~93 % dominated by the per-session control connection + handshake**
  (77.4 KB of 83.4 KB/session) and ~7 % by everything the relay transport itself needs. There is
  no framework component that a pure WinForward-side optimization can remove: the control
  connection is the SOCKS5 protocol's per-session dial (a *fresh* TCP connect + greeting +
  ASSOCIATE per UDP flow, confirmed at `Socks5UdpTransport.CreateAsync` /
  `TcpRedirectSetup`), and the ~2.9 KB ASSOCIATE share is protocol traffic on that same socket.
- The only structural lever visible in the numbers is **connection reuse** (shared/pooled control
  connections with per-flow ASSOCIATE), which is a product/protocol decision (and a security /
  attribution trade-off), not an allocation micro-fix. Per the user directive this is recorded as
  a follow-up candidate, not acted on here.
- `unsafe`/direct-memory variants have **no place** in this layer: the allocations are BCL
  socket/stream/CTS objects (77 KB control + 2.4 KB transport ctor), not buffer materialization;
  the only buffer (the 1,536 B send buffer) is already a plain array whose lifetime equals the
  session's.
- Retained (post-GC) footprint from `udp.sessionFootprint` is dominated by the fake-transport
  path's bookkeeping + receive windows; the real transport's retained footprint includes the
  relay socket's kernel receive buffer (512 KiB, kernel memory — moved, not removed, if the
  socket is pooled) and the control TCP socket state.

## Gaps / ambiguities

- Fake control connection not expressible (sealed type); connect+handshake and ASSOCIATE are
  protocol-indivisible without a `src/**` seam. Reported as measured sequence minus its
  connect-only prefix, per design §1's gap rule.
- DNS resolution is included in the control path (`Socks5AddressCache` is null in the benchmark,
  matching the existing probe's factory call); production composition may cache the address
  (resolving once per server), which is a cold-edge option, not a per-session framework cost.
- The ~3 KB/session anchor gap is not fully attributed to (a) vs (b) above; the split would need a
  server-side counter or a fake server, i.e. new harness surface. Recorded as a gap.
