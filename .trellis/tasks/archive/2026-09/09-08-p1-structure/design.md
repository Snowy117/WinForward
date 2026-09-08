# P1 design — behavior-zero structural refactors

Evidence: `../09-08-design-review-remediation/research/04-proxy-machinery.md` §3 (split plan),
`05-cli-tests-benchmarks.md` §5, `01-core-configuration.md` §4. Line numbers re-verified against
the tree on 2026-09-08 AFTER P0+P2 landed (baseline 578 tests).

Hard rule for every batch: `dotnet test` totals stay 578 within the task (no production behavior
change, no test added/removed unless a doc-only batch); zero-warning build; mechanical moves
preferred; lock bodies migrate verbatim.

## D1 — UdpProxyCoordinator split (R1)

Current: 471 effective lines, method map verified (:prefixed = current line):

- A. **`UdpSetupCooldownTable`** (leaf-lock micro-module, ~60 eff): fields/constants for
  `_setupTombstones`, diagnostics `SetupTombstoneCountForDiagnostics` (:109) → rename
  `SetupCooldownCountForDiagnostics`, `PruneExpiredTombstones` (:524), and
  `EvictOldestSetupTombstoneUnderGate` (:542) → `EvictOldestUnderGate`. Own `Lock _gate` (leaf —
  nobody calls into coordinator while holding it), mirroring `TcpResetCooldownTable` /
  `TcpPendingSynSetupIndex._setupCooldowns`. **The word "tombstone" retires from UDP** (TCP keeps
  it: 60 s TIME_WAIT grace ≠ UDP 1 s setup-failure cooldown).
- B. **`UdpSetupQueueBudget`** (~65 eff): constants (:13-14, :22), `EnqueueSetupDatagram`'s
  accounting half (:222 charge/drop path), `TryChargePendingSetupBytes` (:260),
  `CreditPendingSetupBytes` (:271), `NoteSetupQueueDrop` (:273), counters (:53, :55-58),
  diagnostics (:115-124). Pure Interlocked — NO lock interaction, trivially extractable. Owns the
  R4 charge/credit exactly-once contract. `EnqueueSetupDatagram` itself (queue mutation on the
  slot) stays in the coordinator — only the budget accounting moves.
- C. **`UdpSessionSetup`** (~150 eff): `CreateSessionAsync` (:365) + `_setupLimiter` semaphore
  (:15 const `MaximumConcurrentSetups`), `RefreshSetupStampsAtDialStart` (:431),
  `FlushSetupQueueAsync` (:456). Receives coordinator capabilities as ctor-injected delegates
  (exactly the `TcpRedirectAcceptor.cs:15-16` pattern) — no cycle, no gate sharing: the coordinator
  gate is NOT passed; the pipeline runs after the coordinator releases it (dial is async by
  design). Owns the 2026-09-06 dial-start re-stamp fix as one cohesive unit.
- D. **`UdpProxyLogging`** (~40 eff): `LogSetupFailure` (:603) + the private LogDebug/LogTrace
  helpers; static class mirroring `TcpRedirectLogging.cs` (21 eff).

Target: coordinator ≈250 eff (TrySendAsync fast path, SendOnReadySessionAsync, RemoveSlotAsync,
RemoveExpiredAsync, DisposeAsync, UdpSessionSlot stays in place). All parts ≤400, file=main-type.

Gate discipline invariant: A gets its own leaf lock; B lock-free; C touches coordinator state
ONLY via delegates (enqueue/credit/counters) so the coordinator `_gate` remains the single gate
for slot state (quality-guidelines single-gate rule).

Tests: existing UDP suites (coordinator lifecycle/capacity/concurrency, setup queue, connreset)
must pass UNCHANGED — they exercise the coordinator interface, not the privates (verify: if any
test uses `InternalsVisibleTo` into members being moved, update the test's access path without
touching assertions; totals stay 578).

## D2 — Benchmarks reorganization (R2)

- New `benchmarks/WinForward.Benchmarks/Stability/StabilityShared.cs` (ns
  `WinForward.Benchmarks.Stability`): `LatencyDistribution` + percentile/TicksTo* math cluster +
  `CountingRuntimeLogger` + product-event helpers — extracted from UdpBurstScenario.cs and
  deduplicated against UdpLossScenario.cs (Burst's own comment admits mirroring Loss).
- New `Stability/UdpBurstInstrumentation.cs`: `BackgroundWindow`, `InFlightStamp`,
  `InFlightTracker`, `BackgroundSender`, `BurstCountingSink`, `PhaseOutcome`/`BurstResult`.
- UdpBurstScenario.cs keeps orchestration only (~150 eff; 437 → compliant).
- `BenchmarkShared.cs` moves from `namespace WinForward.Benchmarks.Perf` to file at project root
  `benchmarks/WinForward.Benchmarks/BenchmarkShared.cs` with root ns `WinForward.Benchmarks`
  (Stability already reaches into Perf for it); update Perf files' usings accordingly.
- Benchmarks build via `dotnet build benchmarks/WinForward.Benchmarks` (zero warnings; not
  part of the test project — totals unaffected). Rationale: benchmarks are bound by the ≤400 rule
  since 2026-08-29.

## D3 — Core re-homing (R3, smallest batch first)

- `Domain.cs`: extract `FlowTable` + `TransportTuple` → new `src/WinForward.Core/FlowTable.cs`
  (post-P2 surface: TryResolve/TryClaimResolved/RemoveExpired only — the move is smaller than at
  review time). Remaining vocabulary types stay in Domain.cs (spec-tolerated root layout).
- `PacketRuntime.cs`: `BoundedSetupQueue` → own file `BoundedSetupQueue.cs` (unrelated roommate
  of the lease vocabulary).
- Pure moves, namespaces unchanged (`WinForward.Core`), no usings churn expected in consumers.

## D4 — UdpProxy→Socks5 edge normalization (R4)

Facts to verify first (implementer): what does `src/WinForward.Protocols/Socks5Udp.cs` already
contain vs. the codec types in `src/WinForward.Runtime/Socks5/Socks5UdpTransport.cs`
(`Socks5UdpDatagram` def, `TryDecode` :82, `Socks5UdpReceiveSkipReason` :15,
`Socks5UdpReceiveResult` :43)?

Decision tree:
1. If Protocols already has the datagram wire codec and Runtime duplicated it → deduplicate:
   Runtime keeps only transport types, consumes the Protocols codec (delete the duplicate).
2. If the codec lives only in Runtime → move the pure codec surface (datagram record + decode +
   header-size const; NOT the socket-bound `Socks5UdpReceiveResult`/interface) to
   WinForward.Protocols next to `Socks5Udp.cs`.
3. Either way, the residual UdpProxy→Socks5 usage is the `IUdpProxyTransport` factory seam — the
   exact mirror of sanctioned TcpRedirect→Socks5. Document it: add to directory-structure.md's
   sanctioned-edge list with one-line rationale ("UdpProxy consumes the SOCKS5 UDP transport
   factory seam, mirroring TcpRedirect→Socks5; datagram wire codec lives in Protocols").
   udp-relay.md cross-reference updated to name the codec's home.

## D5 — NdisCapturePump options record (R5)

Ctor today (NdisCapture.cs:67): 9 params — `(driver, adapterHandle, handler)` required +
6 optional knobs (`pollDelay`, `batchCapacity`, `onBatchCompleted`, `onTransientRetry`,
`onDegraded`, `transientRetryBaseDelay`). Introduce
`public sealed record NdisCapturePumpOptions(TimeSpan? PollDelay = null, int? BatchCapacity = null, Action? OnBatchCompleted = null, Action<int,int>? OnTransientRetry = null, Action<int>? OnDegraded = null, TimeSpan? TransientRetryBaseDelay = null)`
in NdisCapture.cs (cohesive, same file); ctor becomes
`(INdisPacketReader, nint, Func<...>, NdisCapturePumpOptions? options = null)`; validation and
defaults move verbatim. Update call sites: MultiAdapterCaptureLoop + NdisCapturePumpTests +
NdisCaptureResilienceTests (named-argument sites become record initializers — mechanical).
File stays ≤400 eff.

## Spec updates landing WITH the code (Phase 3.3)

- directory-structure.md: sanctioned-edge entry (D4); split precedent entry (UDP coordinator
  471→5, mirrors TCP 1158→5); BenchmarkShared/StabilityShared organization note.
- tcp-local-redirect.md / udp-relay.md / quality-guidelines.md: tombstone terminology unified —
  TCP "tombstone" = TIME_WAIT grace (unchanged); UDP becomes "setup cooldown" everywhere.

## Rollback

Five independent commit batches (B1 Core moves, B2 pump options, B3 codec edge, B4 coordinator
split, B5 benchmarks). No forward dependencies; any batch reverts in isolation.

## Risks

- B4 is the only batch with real (structural) risk: delegate wiring in C must preserve the
  exactly-once budget semantics — the five credit sinks (flush/eviction/slot-drain/dispose-drain)
  each keep their single call site; review the diff for charge/credit pairing.
- Benchmark moves are compile-only risk (no tests); gated by zero-warning build of the benchmark
  project.
